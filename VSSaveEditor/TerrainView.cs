using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using VSCore;

namespace VSSaveEditor;

/// <summary>
///     Terrain map rendering with pan/zoom, LOD chunk aggregation and an info tooltip (block id/code, coordinates, Y).
///     Performance: the bitmap is built NOT for the whole world but only for the visible region (about a viewport in size).
///     Zoom/pan change the density (_wanted, pixels per block) and rebuild only a small bitmap - therefore
///     zooming stays smooth even on huge worlds (5760×5280). When zoomed out the bitmap is small (K×K chunk merge).
/// </summary>
public class TerrainView : Control
{
    public const int ModeHeight = TerrainColorizer.ModeHeight;
    public const int ModeBlocks = TerrainColorizer.ModeBlocks;

    public const int
        ModeDepth = TerrainColorizer.ModeDepth; // bathymetry: ocean depth (shallow is light, deep is dark)

    // ---- LOD: several discrete levels plus auto-selection (dynamic) ----
    public const double MinWanted = 1.0 / 64; // maximum zoom-out: 64 blocks per pixel (aggregation)
    public const double MaxWanted = 40; // maximum zoom-in: 40 screen pixels per block
    private const double MaxRegionPpb = 8; // bitmap density limit (above it only scaling happens, no rebuild)
    public const int LodAuto = 0;

    public static readonly string[] LodLabels =
        { "Auto (dynamic)", "Full detail", "Fine (2×)", "Normal (4×)", "Coarse (8×)", "Overview (16×)" };

    private static readonly int[] LodBlocks = { 0, 1, 2, 4, 8, 16 }; // index-aligned; 0 = auto

    // Column colours, bathymetry and shading live in VSCore.TerrainColorizer -
    // the same logic is used by the PNG map export (MapImageRenderer) so the image matches the map.

    // Brush/pen cache: no allocations per frame (drawing happens on every pan/zoom).
    private static readonly IBrush BgBrush = new SolidColorBrush(Color.FromRgb(22, 24, 26));
    private static readonly IBrush EmptyBrush = new SolidColorBrush(Color.FromRgb(170, 176, 184));
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)));
    private static readonly IBrush SelFill = new SolidColorBrush(Color.FromArgb(80, 130, 220, 130));
    private static readonly IBrush SelRubber = new SolidColorBrush(Color.FromArgb(60, 140, 220, 140));

    private static readonly IPen SelPen = new Pen(new SolidColorBrush(Color.FromArgb(215, 140, 255, 140)), 1.5);

    // the inverse action (Shift+right click deselection) is drawn red so it is not confused
    private static readonly IBrush DeselRubber = new SolidColorBrush(Color.FromArgb(60, 220, 130, 130));
    private static readonly IPen DeselPen = new Pen(new SolidColorBrush(Color.FromArgb(215, 255, 140, 140)), 1.5);
    private static readonly Color FilterBackground = Color.FromRgb(16, 18, 24);

    // ---- chunk selection (for future delete/modify features) ----
    // Left click on a chunk toggles it (add/remove); Shift+left-drag is a rectangular
    // region selection (several chunks at once). The selection itself is stored and exposed
    // (SelectedChunks / SelectionBounds) so future operations can use it.
    private readonly HashSet<(int cx, int cz)> _selectedChunks = new();

    private WriteableBitmap? _bmp;
    private bool _busyShown;

    // ===================== BLOCK OVERLAY FILTER =====================

    private Dictionary<int, Color>? _filterColors; // id -> highlight colour
    private bool _filterDimOthers = true; // true = the base map is dimmed, false = matches only
    private double _focusX, _focusZ; // world coordinate of the view centre (blocks)
    private MapFrame? _frame; // geometry of the DISPLAYED bitmap (do not change before a new frame is published!)
    private Point _lastDrag = new(double.NaN, double.NaN);
    private int _lastHoverX = int.MinValue, _lastHoverZ = int.MinValue; // the last block under the cursor (avoids a redraw)
    private DispatcherTimer? _layerTimer; // deferred Y-slice rebuild (slider smoothing)
    private int _mode = ModeBlocks;
    private TerrainColorCache? _colorCache;
    private Vector _pan = new(-0, -0); // bitmap placement inside the control
    private double _ppb = 1.0; // bitmap density (== _wanted, but capped by MaxRegionPpb)
    private Point _pressPos; // where the press started on screen (click vs drag)
    private int _rebuildGen; // bitmap rebuild epoch (stale results are not published)
    private bool _rebuildPending;
    private int _regionLeft, _regionTop; // world left/top edge of the bitmap region (blocks)
    private int _regionW, _regionH; // region size in blocks
    private bool _selDeselect; // Shift+right click = inverse action: removes the selection from chunks
    private Point _selRectStart, _selRectCur; // screen corners of the selection rubber band
    private bool _shiftSelect; // Shift+left/right click = rectangle instead of panning
    private bool _showChunkGrid = true;
    private bool _userZoomed; // the user zoomed manually - do not re-fit on window resize
    private double _wanted = 1.0; // screen pixels per block (the user zoom)
    private double _zoom = 1.0; // _wanted/_ppb - the scale used once the density limit is exceeded

    public TerrainView()
    {
        // Until the user zooms manually, keep the whole map fitted to the window:
        // re-fit on every size change (Bounds changes as the right panel is laid out,
        // so we do not lock onto the first one - otherwise the world ends up larger than the viewport and is cropped at the corners).
        SizeChanged += (_, _) =>
        {
            if (Terrain is null) return;
            if (_userZoomed)
            {
                RebuildBitmap();
            }
            else
            {
                FitToViewport();
                RebuildBitmap();
            }
        };
    }

    public IReadOnlyCollection<(int cx, int cz)> SelectedChunks => _selectedChunks;
    public int SelectionCount => _selectedChunks.Count;

    /// <summary>
    ///     The selection bounding box in chunk coordinates, or null.
    ///     "Several chunks = a region": that is the size of the selected region.
    /// </summary>
    public (int minCx, int minCz, int maxCx, int maxCz)? SelectionBounds
    {
        get
        {
            if (_selectedChunks.Count == 0) return null;
            int miX = int.MaxValue, miZ = int.MaxValue, maX = int.MinValue, maZ = int.MinValue;
            foreach (var (cx, cz) in _selectedChunks)
            {
                if (cx < miX) miX = cx;
                if (cx > maX) maX = cx;
                if (cz < miZ) miZ = cz;
                if (cz > maZ) maZ = cz;
            }

            return (miX, miZ, maX, maZ);
        }
    }

    public string HoverText { get; private set; } = "";
    public string LodStatus { get; private set; } = "";

    /// <summary>The current scale: screen pixels per block. Read by diagnostics/probes (for example,
    /// to confirm that an area switch did not reset the user zoom).</summary>
    public double PixelsPerBlock => _wanted;

    public int LodIndex { get; private set; } = LodAuto;

    public int? LayerY { get; private set; }

    /// <summary>Whether a background region rebuild is running (for the "rendering…" indicator).</summary>
    public bool IsRebuilding { get; private set; }

    private double ViewportW => Math.Max(100, Bounds.Width);
    private double ViewportH => Math.Max(100, Bounds.Height);

    /// <summary>The current terrain map (the overlay builder needs it so the grids match 1:1).</summary>
    public TerrainMap? Terrain { get; private set; }

    /// <summary>
    ///     Areas (connected chunk groups) of the open save; null means a save without areas. The map is built
    ///     for one area, while panning goes across the union of all of them - otherwise a distant chunk
    ///     group could not be reached, and it "cannot be shown until you scroll to it".
    /// </summary>
    public ChunkAreas? Areas { get; private set; }

    /// <summary>Maps of the other areas, built ahead of time (or on first visit). The view draws them
    /// as separate overview layers while zoomed out, so two nearby areas no longer replace each other.</summary>
    private readonly Dictionary<ChunkArea, TerrainMap> _areaMaps = new();
    private readonly Dictionary<ChunkArea, TerrainPyramid> _areaOverviews = new();
    private readonly List<ExtraLayer> _extraLayers = new();
    private readonly Dictionary<(ChunkArea Area, double Ppb, int Mode, int Layer), ExtraLayer> _extraCache = new();

    private sealed class ExtraLayer
    {
        public ChunkArea Area = null!;
        public TerrainMap? Map;
        public MapFrame Frame;
        public byte[] Pixels = Array.Empty<byte>();
        public WriteableBitmap? Bitmap;
        public (ChunkArea Area, double Ppb, int Mode, int Layer) Key;
    }

    /// <summary>Whether an area map is already available (the window can publish it without rebuilding).</summary>
    public bool HasAreaMap(ChunkArea area) => _areaMaps.ContainsKey(area);

    /// <summary>Diagnostics: how many extra area overview layers are currently in the drawing list.</summary>
    public int ExtraAreaLayers => _extraLayers.Count;

    /// <summary>Replaces the set of cached area maps. The current Terrain stays the interactive one;
    /// all other maps whose bbox is visible become extra overview layers.</summary>
    public void SetAreaMaps(IReadOnlyDictionary<ChunkArea, TerrainMap> maps)
    {
        _areaMaps.Clear();
        foreach (var kv in maps) _areaMaps[kv.Key] = kv.Value;
        _extraLayers.Clear();
        _extraCache.Clear();
        if (Terrain is not null) RebuildBitmap();
        InvalidateVisual();
    }

    /// <summary>Low-resolution overviews of areas whose full maps are deliberately not cached (the
    /// preload budget): a save with several 60–80M-column areas would otherwise show only the current
    /// one at a whole-world zoom.</summary>
    public void SetAreaOverviews(IReadOnlyDictionary<ChunkArea, TerrainPyramid> overviews)
    {
        _areaOverviews.Clear();
        foreach (var kv in overviews) _areaOverviews[kv.Key] = kv.Value;
        _extraLayers.Clear();
        _extraCache.Clear();
        if (Terrain is not null) RebuildBitmap();
        InvalidateVisual();
    }

    /// <summary>
    ///     The viewport reached an area whose map is not built: the window must build it and
    ///     hand it back through <see cref="SetTerrain(TerrainMap?, bool)"/> with keepView = true (otherwise
    ///     the user would be thrown out of the current zoom/position).
    /// </summary>
    public event Action<ChunkArea>? AreaRequested;

    public void SetAreas(ChunkAreas? areas)
    {
        Areas = areas;
        _areaMaps.Clear();
        _areaOverviews.Clear();
        _extraLayers.Clear();
        _extraCache.Clear();
        InvalidateVisual();
    }

    /// <summary>The current filter match map (null = overlay off).</summary>
    public BlockFilterMap? Filter { get; private set; }

    /// <summary>Render mode name by index (matches the order in the ComboBox).</summary>
    public static string ModeName(int mode)
    {
        return mode switch
        {
            ModeBlocks => "blocks (in-game map)",
            ModeDepth => "ocean depth",
            _ => "heightmap"
        };
    }

    public event Action? SelectedChunksChanged;

    public void ClearSelection()
    {
        if (_selectedChunks.Count == 0 && !_shiftSelect) return;
        _selectedChunks.Clear();
        _shiftSelect = false;
        _selDeselect = false;
        SelectedChunksChanged?.Invoke();
        InvalidateVisual();
    }

    private void ToggleChunk(int cx, int cz)
    {
        var c = (cx, cz);
        if (!_selectedChunks.Remove(c)) _selectedChunks.Add(c);
        SelectedChunksChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>Adds a chunk to the selection (Shift+left click on a chunk).</summary>
    private void SelectChunk(int cx, int cz)
    {
        if (_selectedChunks.Add((cx, cz)))
        {
            SelectedChunksChanged?.Invoke();
            InvalidateVisual();
        }
    }

    /// <summary>Removes one chunk from the selection (Shift+right click on a chunk).</summary>
    private void DeselectChunk(int cx, int cz)
    {
        if (_selectedChunks.Remove((cx, cz)))
        {
            SelectedChunksChanged?.Invoke();
            InvalidateVisual();
        }
    }

    /// <summary>Replaces the selection with every chunk that actually exists inside the rectangle (a region).</summary>
    private void SelectRect(int cx0, int cz0, int cx1, int cz1)
    {
        if (Terrain is null) return;
        _selectedChunks.Clear();
        int loX = Math.Min(cx0, cx1), hiX = Math.Max(cx0, cx1);
        int loZ = Math.Min(cz0, cz1), hiZ = Math.Max(cz0, cz1);
        foreach (var (px, pz) in Terrain.PresentChunks)
            if (px >= loX && px <= hiX && pz >= loZ && pz <= hiZ)
                _selectedChunks.Add((px, pz));
        SelectedChunksChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>Removes the chunks inside the rectangle from the selection - the inverse of <see cref="SelectRect" /> (Shift+right click).</summary>
    private void DeselectRect(int cx0, int cz0, int cx1, int cz1)
    {
        if (Terrain is null || _selectedChunks.Count == 0) return;
        int loX = Math.Min(cx0, cx1), hiX = Math.Max(cx0, cx1);
        int loZ = Math.Min(cz0, cz1), hiZ = Math.Max(cz0, cz1);
        var removed = _selectedChunks.RemoveWhere(c => c.cx >= loX && c.cx <= hiX && c.cz >= loZ && c.cz <= hiZ);
        if (removed > 0)
        {
            SelectedChunksChanged?.Invoke();
            InvalidateVisual();
        }
    }

    public event Action<string>? HoverChanged;
    public event Action<string>? LodStatusChanged;
    public event Action<bool>? RebuildStateChanged;

    private void SetBusy(bool busy)
    {
        IsRebuilding = busy;
        if (_busyShown == busy) return;
        _busyShown = busy;
        RebuildStateChanged?.Invoke(busy);
    }

    // No-op guards: when the effective value/state did not change, do not rebuild the bitmap
    // and do not redraw (otherwise every no-op triggers a costly rebuild of the visible region).
    public void SetLayer(int? y)
    {
        if (LayerY == y) return;
        LayerY = y;
        _colorCache = null; // colours depend on the Y layer (slice colours and the mask colour)
        // A Y slice requires decoding the chunks of the visible area. Zoomed in that is dozens of chunks
        // (instant), zoomed out it is thousands, so a "wide" view is not rebuilt on every
        // slider value but after a short pause: otherwise dragging the slider queues
        // heavy rebuilds and the map sticks.
        if (y is not null && VisibleChunkCount() > 256)
        {
            _layerTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(140), DispatcherPriority.Background,
                (_, _) =>
                {
                    _layerTimer!.Stop();
                    RebuildBitmap();
                });
            _layerTimer.Stop();
            _layerTimer.Start();
        }
        else
        {
            _layerTimer?.Stop();
            RebuildBitmap();
        }

        InvalidateVisual();
    }

    /// <summary>How many chunks fall into the current view (an upper estimate from the scale and the viewport).</summary>
    private int VisibleChunkCount()
    {
        double vw = ViewportW / Math.Max(1e-9, _wanted), vh = ViewportH / Math.Max(1e-9, _wanted);
        return (int)(Math.Ceiling(vw / 32.0) * Math.Ceiling(vh / 32.0));
    }

    public void SetRenderMode(int mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        _colorCache = null; // the shade/semantics are mode-specific
        RebuildBitmap();
        InvalidateVisual();
    }

    public void SetShowChunkGrid(bool show)
    {
        if (_showChunkGrid == show) return;
        _showChunkGrid = show;
        InvalidateVisual();
    }

    public void SetLodMode(int index)
    {
        index = Math.Clamp(index, 0, LodBlocks.Length - 1);
        if (LodIndex == index) return;
        LodIndex = index;
        RebuildBitmap();
        InvalidateVisual();
    }

    private void FitToViewport()
    {
        var tm = Terrain;
        if (tm is null) return;
        double vw = Math.Max(100, Bounds.Width), vh = Math.Max(100, Bounds.Height);
        var fit = Math.Min(vw / tm.Width, vh / tm.Height); // the whole world fits the window
        if (double.IsNaN(fit) || double.IsInfinity(fit)) fit = MaxWanted; // a map without data (Width=1)
        _wanted = Math.Clamp(fit, MinWanted, MaxWanted);
        _focusX = tm.MinX + tm.Width / 2.0;
        _focusZ = tm.MinZ + tm.Height / 2.0;
    }

    public void SetTerrain(TerrainMap? tm) => SetTerrain(tm, keepView: false);

    /// <summary>
    ///     Publishes the map. <paramref name="keepView" /> = true means an area switch under the same
    ///     zoom/position: the user stays where they were looking, and the chunk selection is kept
    ///     (an area switch must not "reset" the region selection).
    /// </summary>
    public void SetTerrain(TerrainMap? tm, bool keepView)
    {
        Terrain = tm;
        _colorCache = null; // belongs to the previous map (or to no map)
        _layerTimer?.Stop(); // the deferred slice belongs to the previous save
        Filter = null; // the match map is tied to the grid of the previous map (area)
        if (!keepView)
        {
            _frame = null; // the frame geometry belongs to the previous save
            _filterColors = null;
        }

        var selWas = _selectedChunks.Count > 0;
        if (!keepView)
        {
            _selectedChunks.Clear();
            _shiftSelect = false;
            _selDeselect = false;
            if (selWas) SelectedChunksChanged?.Invoke();
        }

        if (tm is null)
        {
            _bmp = null;
            _extraLayers.Clear();
            _userZoomed = false;
            _lastHoverX = _lastHoverZ = int.MinValue;
            InvalidateVisual();
            return;
        }

        _lastHoverX = _lastHoverZ = int.MinValue;
        if (!keepView)
        {
            _userZoomed = false;
            // safe default: the whole world is visible (small), SizeChanged will fit it more precisely later
            _wanted = Math.Clamp(Math.Min(ViewportW / tm.Width, ViewportH / tm.Height), MinWanted, MaxWanted);
            _focusX = tm.MinX + tm.Width / 2.0;
            _focusZ = tm.MinZ + tm.Height / 2.0;
        }

        RebuildBitmap();
        InvalidateVisual();
    }

    /// <summary>The current view centre in world coordinates (blocks) - needed for match navigation.</summary>
    public (int wx, int wz) CentreWorld()
    {
        return ((int)Math.Round(_focusX), (int)Math.Round(_focusZ));
    }

    /// <summary>Zoom around the map centre (menu buttons / shortcuts).</summary>
    public void ZoomBy(double factor)
    {
        if (Terrain is null) return;
        HandleZoom(factor, new Point(ViewportW / 2, ViewportH / 2));
        InvalidateVisual();
    }

    /// <summary>Fit the whole world into the window. "World" here is the built area: the other areas
    /// are not drawn on the map until the user scrolls to them.</summary>
    public void FitToArea()
    {
        if (Terrain is null) return;
        _userZoomed = false;
        FitToViewport();
        RebuildBitmap();
        InvalidateVisual();
    }

    /// <summary>Pan by screen pixels (arrow keys).</summary>
    public void PanByPixels(double dx, double dy)
    {
        if (Terrain is null) return;
        PanBy(new Vector(dx, dy));
        InvalidateVisual();
    }

    /// <summary>Centres the map on world coordinates (blocks) and zooms in to a detailed level.</summary>
    public void CentreOn(double worldX, double worldZ, double zoom = 1.0)
    {
        var tm = Terrain;
        if (tm is null) return;
        var (minX, minZ, maxX, maxZ) = WorldRect;
        _focusX = Math.Clamp(worldX, minX, maxX);
        _focusZ = Math.Clamp(worldZ, minZ, maxZ);
        _wanted = Math.Clamp(zoom, MinWanted, MaxWanted);
        _userZoomed = true;
        RebuildBitmap();
        InvalidateVisual();
    }

    /// <summary>Selects a single chunk and centres the map on it (for search results).</summary>
    public void FocusChunk(int cx, int cz)
    {
        if (Terrain is null) return;
        _selectedChunks.Clear();
        _selectedChunks.Add((cx, cz));
        SelectedChunksChanged?.Invoke();
        CentreOn(cx * 32 + 16, cz * 32 + 16, 1.5);
    }

    /// <summary>
    ///     The bitmap density for the LOD mode and the current zoom, QUANTIZED so that the grid step
    ///     matches the map step: a whole number of pixels per block in detailed mode, otherwise 1/K (blocks per pixel).
    /// </summary>
    private double EffectivePpb()
    {
        double ppb;
        if (LodIndex == LodAuto) ppb = Math.Clamp(_wanted, MinWanted, MaxRegionPpb);
        else if (LodBlocks[LodIndex] == 1) ppb = Math.Clamp(_wanted, 1.0, MaxRegionPpb);
        else ppb = 1.0 / LodBlocks[LodIndex];
        if (ppb >= 1) ppb = Math.Max(1, (int)Math.Round(ppb)); // whole pixels per block
        else ppb = 1.0 / Math.Max(1, (int)Math.Ceiling(1.0 / ppb)); // 1/K
        return ppb;
    }

    /// <summary>
    ///     The panning domain: the union of all areas of the save (the current one fits into it with
    ///     room to spare). The bounds of a single map cannot be used - a distant area would be unreachable.
    /// </summary>
    private (int MinX, int MinZ, int MaxX, int MaxZ) WorldRect
    {
        get
        {
            if (Areas is { Areas.Count: > 0 } areas) return areas.WorldBounds;
            var tm = Terrain;
            return tm is null ? (0, 0, 0, 0) : (tm.MinX, tm.MinZ, tm.MinX + tm.Width - 1, tm.MinZ + tm.Height - 1);
        }
    }

    /// <summary>The visible world rectangle in blocks (from the current zoom and focus).</summary>
    private (double MinX, double MinZ, double MaxX, double MaxZ) VisibleWorld()
    {
        double hw = ViewportW / (2 * Math.Max(1e-9, _wanted)), hh = ViewportH / (2 * Math.Max(1e-9, _wanted));
        return (_focusX - hw, _focusZ - hh, _focusX + hw, _focusZ + hh);
    }

    /// <summary>
    ///     When the viewport reaches another area, ask the window to build its map (AreaRequested).
    ///     The check is cheap (there are a handful of areas) and runs from every place that changes focus/zoom.
    /// </summary>
    private void CheckAreaRequest()
    {
        if (Areas is not { Areas.Count: > 1 } areas) return;
        var (vx0, vz0, vx1, vz1) = VisibleWorld();
        var want = areas.AreaForViewport(vx0, vz0, vx1, vz1, Terrain?.Area);
        if (want is null || ReferenceEquals(want, Terrain?.Area)) return;
        AreaRequested?.Invoke(want);
    }

    /// <summary>
    ///     Whether the viewport intersects the area right now. The window checks this before publishing
    ///     a built map: while it was building, the user may have moved back, and there is no need to swap
    ///     in an "area they have already left".
    /// </summary>
    public bool ViewportIntersects(ChunkArea area)
    {
        var (vx0, vz0, vx1, vz1) = VisibleWorld();
        return area.MaxX >= vx0 && area.MinX <= vx1 && area.MaxZ >= vz0 && area.MinZ <= vz1;
    }

    /// <summary>The area the viewport would choose right now (the same rule as CheckAreaRequest).</summary>
    public ChunkArea? PreferredArea()
    {
        if (Areas is not { Areas.Count: > 1 } areas) return Terrain?.Area;
        var (vx0, vz0, vx1, vz1) = VisibleWorld();
        return areas.AreaForViewport(vx0, vz0, vx1, vz1, Terrain?.Area);
    }

    /// <summary>Whether a map built for <paramref name="area"/> should still be published. A build can take
    /// seconds; publishing it after the user has moved to another area (or into the void) swaps in a map they
    /// have already left and hides the piece that was on screen. Intersection alone is not enough — a wide
    /// viewport may intersect several areas at once.</summary>
    public bool IsAreaStillWanted(ChunkArea area) => ReferenceEquals(PreferredArea(), area);

    /// <summary>Does the visible part of the world intersect the built map? If not, only the background is on screen.</summary>
    private bool ViewportShowsData()
    {
        var tm = Terrain;
        if (tm is null) return true;
        var (vx0, vz0, vx1, vz1) = VisibleWorld();
        return vx1 >= tm.MinX && vx0 <= tm.MinX + tm.Width - 1 &&
               vz1 >= tm.MinZ && vz0 <= tm.MinZ + tm.Height - 1;
    }

    /// <summary>The hint shown when the screen sits in the void between areas: where to look for the nearest one.</summary>
    private string VoidHint()
    {
        var tm = Terrain;
        if (tm?.Area is not { } cur) return "No map data here";
        var areas = Areas;
        if (areas is null || areas.Areas.Count < 2) return "No map data here — pan back to the map";
        var near = areas.AreaNearest(_focusX, _focusZ);
        double dist = near.DistanceTo(_focusX, _focusZ);
        string where = ReferenceEquals(near, cur) ? "behind you" : $"X {near.MinX}..{near.MaxX}, Z {near.MinZ}..{near.MaxZ}";
        return $"No map data here\n" +
               $"Chunk areas in this save: {areas.Areas.Count} · nearest is {near.Label(areas.Areas.Count)} " +
               $"(~{dist:N0} blocks away: {where})";
    }

    private void ClampFocus()
    {
        var (minX, minZ, maxX, maxZ) = WorldRect;
        // If the world is smaller than the viewport along an axis, keep it centred (there is nothing to pan);
        // otherwise cursor-anchored zoom jittered at the edges because of the hard focus clamp.
        double w = maxX - minX + 1, h = maxZ - minZ + 1;
        double vw = ViewportW / Math.Max(1e-9, _wanted), vh = ViewportH / Math.Max(1e-9, _wanted);
        _focusX = vw >= w ? minX + w / 2.0 : Math.Clamp(_focusX, minX, maxX);
        _focusZ = vh >= h ? minZ + h / 2.0 : Math.Clamp(_focusZ, minZ, maxZ);
    }

    /// <summary>
    /// Renders the other cached areas as independent overview layers. Only LOD is used: at detail
    /// level the viewport is small and the primary area covers it; the extra layers exist for the
    /// zoomed-out view where several distant patches are visible at once.
    /// </summary>
    private List<ExtraLayer> BuildExtraLayers(TerrainMap primary, double ppb, int? layer, int mode,
        double vx0, double vz0, double vx1, double vz1,
        IReadOnlyDictionary<ChunkArea, TerrainMap> areaMaps,
        IReadOnlyDictionary<ChunkArea, TerrainPyramid> areaOverviews,
        IReadOnlyDictionary<(ChunkArea Area, double Ppb, int Mode, int Layer), ExtraLayer> cacheSnapshot)
    {
        var result = new List<ExtraLayer>();
        // At detail level the primary area fills the viewport; extras are a zoomed-out feature.
        // Do NOT require areaMaps: a large neighbour may have only a pyramid cached (its full map
        // was released by the preload budget), and that pyramid is exactly what should be drawn.
        if (ppb > 1) return result;
        foreach (var (area, map) in areaMaps)
        {
            if (ReferenceEquals(map, primary)) continue;
            var key = (area, ppb, mode, layer ?? int.MinValue);
            if (cacheSnapshot.TryGetValue(key, out var cached) && cached.Bitmap is not null)
            {
                result.Add(cached);
                continue;
            }
            if (map.MinX + map.Width - 1 < vx0 || map.MinX > vx1 || map.MinZ + map.Height - 1 < vz0 || map.MinZ > vz1) continue;
            // A large area at ppb close to 1 would produce a second full-size bitmap; skip it.
            // A cached coarse pyramid turns another area into a small blit instead of a full-area
            // per-column LOD pass (which was repeated on every pan/zoom).
            if (layer is null && map.Pyramid is { } py && py.Mode == mode
                && py.SuitableFor(ppb, (int)Math.Ceiling(map.Width * ppb), (int)Math.Ceiling(map.Height * ppb)))
            {
                var overviewFrame = new MapFrame(map.MinX, map.MinZ, map.Width, map.Height, py.Width, py.Height,
                    py.Width / (double)map.Width, py.Height / (double)map.Height,
                    _zoom, _focusX, _focusZ, 1.0, new Vector());
                result.Add(new ExtraLayer { Area = area, Map = map, Frame = overviewFrame, Pixels = py.Pixels, Key = key });
                continue;
            }
            if ((double)map.Width * map.Height * ppb * ppb > 4_000_000) continue;
            int w = Math.Max(1, (int)Math.Ceiling(map.Width * ppb));
            int h = Math.Max(1, (int)Math.Ceiling(map.Height * ppb));
            byte[] px;
            try { px = ComputeRegion(map, ppb, map.MinX, map.MinZ, map.Width, map.Height, layer, mode, null, applyFilter: false); }
            catch { continue; }
            if (px.Length == 0) continue;
            var frame = new MapFrame(map.MinX, map.MinZ, map.Width, map.Height, w, h,
                w / (double)map.Width, h / (double)map.Height,
                _zoom, _focusX, _focusZ, 1.0, new Vector());
            result.Add(new ExtraLayer { Area = area, Map = map, Frame = frame, Pixels = px, Key = key });
        }
        // Areas without a cached full map still have a pre-coloured pyramid: show it as a coarse layer.
        foreach (var (area, py) in areaOverviews)
        {
            if (ReferenceEquals(area, primary.Area) || areaMaps.ContainsKey(area) || py.Mode != mode) continue;
            var key = (area, ppb, mode, int.MinValue);
            if (cacheSnapshot.TryGetValue(key, out var cached) && cached.Bitmap is not null)
            {
                result.Add(cached);
                continue;
            }
            if (area.MaxX < vx0 || area.MinX > vx1 || area.MaxZ < vz0 || area.MinZ > vz1) continue;
            int pw = area.WidthBlocks, ph = area.HeightBlocks;
            if (!py.SuitableFor(ppb, (int)Math.Ceiling(pw * ppb), (int)Math.Ceiling(ph * ppb))) continue;
            var overviewFrame = new MapFrame(area.MinX, area.MinZ, pw, ph, py.Width, py.Height,
                py.Width / (double)pw, py.Height / (double)ph,
                _zoom, _focusX, _focusZ, 1.0, new Vector());
            result.Add(new ExtraLayer { Area = area, Map = null, Frame = overviewFrame, Pixels = py.Pixels, Key = key });
        }
        return result;
    }

    private void PublishExtraLayers(List<ExtraLayer> layers)
    {
        _extraLayers.Clear();
        foreach (var layer in layers)
        {
            if (layer.Bitmap is not null)
            {
                _extraLayers.Add(layer);
                continue;
            }
            var bmp = new WriteableBitmap(new PixelSize(layer.Frame.BmpW, layer.Frame.BmpH),
                new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using var fb = bmp.Lock();
            unsafe
            {
                var dst = (byte*)fb.Address;
                int rowBytes = layer.Frame.BmpW * 4;
                fixed (byte* p = layer.Pixels)
                {
                    var src = p;
                    for (var y = 0; y < layer.Frame.BmpH; y++)
                    {
                        Buffer.MemoryCopy(src, dst + (long)y * fb.RowBytes, rowBytes, rowBytes);
                        src += rowBytes;
                    }
                }
            }
            layer.Bitmap = bmp;
            _extraLayers.Add(layer);
            _extraCache[layer.Key] = layer;
        }
    }

    private void RebuildBitmap()
    {
        var tm = Terrain;
        if (tm is null) return;
        ClampFocus();
        CheckAreaRequest(); // we reached another area - the window will build its map (AreaRequested)

        // The viewport is entirely in the void between areas: there is nothing to build. Keep the previous frame
        // (it moves off screen with the focus) instead of a one-pixel bitmap at the map edge.
        if (!ViewportShowsData())
        {
            LodStatus = FormatLod();
            LodStatusChanged?.Invoke(LodStatus);
            InvalidateVisual();
            return;
        }

        var ppb = EffectivePpb();
        // Coarse overview: one pre-coloured pyramid pixel per CellSize×CellSize block cell. At this
        // density the exact per-column LOD pass would visit the same blocks only to average them into
        // the same few pixels; the pyramid makes the whole-map view a bitmap blit.
        if (LayerY is null && Filter is null && tm.Pyramid is { } pyramid && pyramid.Mode == _mode
            && pyramid.SuitableFor(ppb, (int)Math.Ceiling(tm.Width * ppb), (int)Math.Ceiling(tm.Height * ppb)))
        {
            _ppb = ppb;
            _zoom = _wanted / Math.Max(1e-9, ppb);
            _regionLeft = tm.MinX;
            _regionTop = tm.MinZ;
            _regionW = tm.Width;
            _regionH = tm.Height;
            double srcPpbX = pyramid.Width / (double)tm.Width;
            double srcPpbY = pyramid.Height / (double)tm.Height;
            var overviewFrame = new MapFrame(tm.MinX, tm.MinZ, tm.Width, tm.Height, pyramid.Width, pyramid.Height,
                srcPpbX, srcPpbY, _zoom, _focusX, _focusZ,
                TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0, new Vector());
            _rebuildGen++; // cancel a pending exact rebuild so it cannot replace the overview with a stale frame
            _rebuildPending = false;
            // Other cached areas must be shown in the same overview frame; their pyramids are blits too.
            var (evx0, evz0, evx1, evz1) = VisibleWorld();
            var extras = BuildExtraLayers(tm, ppb, null, _mode, evx0, evz0, evx1, evz1,
                new Dictionary<ChunkArea, TerrainMap>(_areaMaps),
                new Dictionary<ChunkArea, TerrainPyramid>(_areaOverviews),
                new Dictionary<(ChunkArea Area, double Ppb, int Mode, int Layer), ExtraLayer>(_extraCache));
            SetBusy(false);
            PublishBitmap(pyramid.Pixels, overviewFrame);
            PublishExtraLayers(extras);
            LodStatus = $"LOD: overview {pyramid.CellSize}×{pyramid.CellSize} (coarse)";
            LodStatusChanged?.Invoke(LodStatus);
            InvalidateVisual();
            return;
        }

        _ppb = ppb;
        _zoom = _wanted / _ppb; // screen pixels per block = _ppb*_zoom = _wanted

        // visible world region (blocks) + margin so that panning does not rebuild the bitmap on every step
        double vw = ViewportW / _wanted, vh = ViewportH / _wanted;
        var m = Math.Max(MaxRegionPpb, Math.Max(vw, vh) * 0.2); // ~20% of the viewport as margin on each side
        int minX = tm.MinX, minZ = tm.MinZ, maxX = tm.MinX + tm.Width - 1, maxZ = tm.MinZ + tm.Height - 1;
        var left = (int)Math.Floor(Math.Max(minX, _focusX - vw / 2 - m));
        var right = (int)Math.Ceiling(Math.Min(maxX, _focusX + vw / 2 + m));
        var top = (int)Math.Floor(Math.Max(minZ, _focusZ - vh / 2 - m));
        var bottom = (int)Math.Ceiling(Math.Min(maxZ, _focusZ + vh / 2 + m));
        int rw = Math.Max(1, right - left + 1), rh = Math.Max(1, bottom - top + 1);
        _regionLeft = left;
        _regionTop = top;
        _regionW = rw;
        _regionH = rh;

        // The minimum bitmap size must match the minimum in ComputeRegion,
        // otherwise part of the bitmap stays uninitialized (B7).
        var w = Math.Max(1, (int)Math.Ceiling(rw * _ppb));
        var h = Math.Max(1, (int)Math.Ceiling(rh * _ppb));

        // DPI matters: Bounds reports the control size in physical pixels, and Avalonia draws a WriteableBitmap
        // 1:1 only when its DPI matches the control render scale. We set the bitmap DPI to 96*RenderScaling,
        // otherwise on a 2x screen the map is drawn twice as large and clipped at the right/bottom edge.
        var rs = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;

        // bitmap placement: view centre -> control centre.
        double fx = (_focusX - left) * _ppb, fz = (_focusZ - top) * _ppb;
        _pan = new Vector(ViewportW / 2 / _zoom - fx, ViewportH / 2 / _zoom - fz);

        LodStatus = FormatLod();
        LodStatusChanged?.Invoke(LodStatus);

        // Redraw the "soft" frame at the new scale right away: do not wait for the background rebuild,
        // otherwise zoom steps that change the LOD density stick and jump.
        InvalidateVisual();

        // Pixels are computed IN THE BACKGROUND: the UI does not freeze during a rebuild while the previous frame is shown.
        // Requests collapse: if a computation is already running, rebuild once more when it finishes (the current state by then).
        if (IsRebuilding)
        {
            _rebuildPending = true;
            return;
        }

        SetBusy(true);
        var gen = ++_rebuildGen;
        var layer = LayerY;
        var mode = _mode;
        // The colour cache is keyed by (map, mode, layer). It is created on the UI thread (a zeroed uint[]
        // commits pages lazily) and captured by the background task, so a newer rebuild cannot swap it
        // under a running one. Only the detailed view (ppb >= 1) uses it: the whole-map LOD pass visits
        // every column of the world, and caching that would allocate/fill the full 99 MB array during
        // the first open for a picture the user usually zooms into immediately.
        TerrainColorCache? colorCache = null;
        if (ppb < 1) _colorCache = null; // the LOD pass does not use it; do not hold 99 MB for a whole-map view
        if (ppb >= 1)
        {
            colorCache = _colorCache;
            if (colorCache is null || !ReferenceEquals(colorCache.Source, tm)
                || colorCache.Mode != mode || colorCache.Layer != layer)
            {
                colorCache = new TerrainColorCache(tm, mode, layer);
                _colorCache = colorCache;
            }
        }
        var ppbLocal = _ppb;
        var regLeft = left;
        var regTop = top;
        var regW = rw;
        var regH = rh;
        var bmpW = w;
        var bmpH = h;
        // A frame captures both the content and the transform - it is published atomically
        var frame = new MapFrame(left, top, rw, rh, w, h, ppbLocal, _zoom, _focusX, _focusZ, rs, _pan);
        var (vy0x, vy0z, vy1x, vy1z) = VisibleWorld();
        var areaMapsSnapshot = new Dictionary<ChunkArea, TerrainMap>(_areaMaps);
        var overviewSnapshot = new Dictionary<ChunkArea, TerrainPyramid>(_areaOverviews);
        var extraCacheSnapshot = new Dictionary<(ChunkArea Area, double Ppb, int Mode, int Layer), ExtraLayer>(_extraCache);
        Task.Run(() =>
        {
            byte[] px;
            try
            {
                px = ComputeRegion(tm, ppbLocal, regLeft, regTop, regW, regH, layer, mode, colorCache);
            }
            catch
            {
                px = Array.Empty<byte>();
            }
            var extras = BuildExtraLayers(tm, ppbLocal, layer, mode, vy0x, vy0z, vy1x, vy1z,
                areaMapsSnapshot, overviewSnapshot, extraCacheSnapshot);

            Dispatcher.UIThread.Post(() =>
            {
                SetBusy(false);
                if (gen != _rebuildGen || !ReferenceEquals(Terrain, tm))
                {
                    InvalidateVisual();
                    return;
                } // stale

                if (px.Length > 0) PublishBitmap(px, frame);
                PublishExtraLayers(extras);
                if (_rebuildPending)
                {
                    _rebuildPending = false;
                    RebuildBitmap();
                } // and rebuild the current state right away

                InvalidateVisual();
            }, DispatcherPriority.Render);
        });
    }

    /// <summary>Copies the ready pixels into a WriteableBitmap (the buffer is reused when the size matches).</summary>
    private void PublishBitmap(byte[] px, MapFrame frame)
    {
        int w = frame.BmpW, h = frame.BmpH;
        if (_bmp is null || _bmp.PixelSize.Width != w || _bmp.PixelSize.Height != h)
            _bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96 * frame.Rs, 96 * frame.Rs),
                PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var fb = _bmp.Lock();
        unsafe
        {
            var dst = (byte*)fb.Address;
            int stride = fb.RowBytes, rowBytes = w * 4;
            fixed (byte* p = px)
            {
                var src = p;
                if (stride == rowBytes) Buffer.MemoryCopy(src, dst, px.Length, px.Length);
                else
                    for (var y = 0; y < h; y++)
                    {
                        Buffer.MemoryCopy(src, dst + (long)y * stride, rowBytes, rowBytes);
                        src += rowBytes;
                    }
            }
        }

        _frame = frame; // from now on we draw exactly this frame with exactly this geometry
    }

    private string FormatLod()
    {
        var K = Math.Max(1, (int)Math.Ceiling(1.0 / _ppb));
        var baseName = _ppb >= 1 ? "full detail" : $"merge {K}×{K}";
        if (LodIndex == LodAuto) return "LOD: " + baseName + " (auto)";
        return $"LOD: {LodLabels[LodIndex]} — " + (_ppb >= 1 ? "full detail" : $"{K} blocks/px");
    }

    /// <summary>Places the bitmap for the current focus/region (without rebuilding the content).</summary>
    private void Recentre()
    {
        InvalidateVisual();
        CheckAreaRequest(); // panning without a rebuild can also bring the viewport to another area
        // the transform is computed in DisplayTransform from the frame
    }

    /// <summary>
    ///     Does the frame region cover the whole VISIBLE part of the world (in blocks)?
    ///     The part of the viewport outside the world does not need covering - there is no data there (background).
    ///     Exactly this case broke zooming when the world/chunks are smaller than the screen: the strict check
    ///     "the frame covers the whole viewport" never held there, and every zoom step
    ///     started a heavy rebuild instead of smoothly scaling the ready bitmap.
    /// </summary>
    private bool RegionCoversWorldVisible(MapFrame f)
    {
        var tm = Terrain;
        if (tm is null) return false;
        double vhx = ViewportW / (_wanted * 2), vhz = ViewportH / (_wanted * 2);
        double ix0 = Math.Max(_focusX - vhx, tm.MinX), ix1 = Math.Min(_focusX + vhx, tm.MinX + tm.Width);
        double iz0 = Math.Max(_focusZ - vhz, tm.MinZ), iz1 = Math.Min(_focusZ + vhz, tm.MinZ + tm.Height);
        if (ix0 >= ix1 || iz0 >= iz1) return true; // the world is not visible at all - there can be no "holes"
        return ix0 >= f.Left && ix1 <= f.Left + f.W &&
               iz0 >= f.Top && iz1 <= f.Top + f.H;
    }

    /// <summary>
    ///     The transform of the displayed frame. "Soft" scaling of the shown bitmap to the current
    ///     zoom is geometrically correct at ANY frame density (the same world at another scale) - that is exactly
    ///     what makes zooming smooth. We drop it only when the visible part of the world has areas
    ///     that the frame lacks (otherwise there would be "holes"); while a background rebuild runs we allow
    ///     it so zoom/drag do not stick.
    /// </summary>
    private (double zoomX, double zoomY, double panX, double panY, double focusX, double focusZ,
        double ppbX, double ppbY) DisplayTransform(MapFrame f)
    {
        var canSoft = _bmp is not null && (IsRebuilding || RegionCoversWorldVisible(f));
        if (!canSoft)
            return (f.Zoom, f.Zoom, f.Pan.X, f.Pan.Y, f.FocusX, f.FocusZ,
                f.Ppb * f.Zoom, f.PpbY * f.Zoom);
        var zoomX = _wanted / f.Ppb;
        var zoomY = _wanted / f.PpbY;
        return (zoomX, zoomY,
            ViewportW / 2 / zoomX - (_focusX - f.Left) * f.Ppb,
            ViewportH / 2 / zoomY - (_focusZ - f.Top) * f.PpbY,
            _focusX, _focusZ, _wanted, _wanted);
    }

    /// <summary>The world point at the viewport centre for the DISPLAYED frame (used for consistency checks and hints).</summary>
    public (double wx, double wz) DisplayCenterWorld()
    {
        if (_bmp is null || _frame is not { } f) return (_focusX, _focusZ);
        var (zoomX, zoomY, panX, panY, _, _, _, _) = DisplayTransform(f);
        double cx = ViewportW / 2, cy = ViewportH / 2;
        return (f.Left + (cx / zoomX - panX) / f.Ppb,
            f.Top + (cy / zoomY - panY) / f.PpbY);
    }

    /// <summary>
    ///     Zoom anchored at the point under the cursor: change the density and the focus. If the LOD density did not change,
    ///     only redraw (no heavy bitmap rebuild) - that is what makes zooming smooth.
    /// </summary>
    private void HandleZoom(double f, Point cursor)
    {
        var tm = Terrain;
        if (tm is null) return;
        var old = _wanted;
        var nw = Math.Clamp(_wanted * f, MinWanted, MaxWanted);
        if (Math.Abs(nw - old) < 1e-9) return;
        _userZoomed = true;
        // anchor the point under the cursor; _wanted changes BEFORE ClampFocus so that centring
        // of "world smaller than screen" is already computed for the new scale
        double wcx = (cursor.X - ViewportW / 2) / old, wcz = (cursor.Y - ViewportH / 2) / old;
        _wanted = nw;
        _focusX += wcx - (cursor.X - ViewportW / 2) / nw;
        _focusZ += wcz - (cursor.Y - ViewportH / 2) / nw;
        ClampFocus();
        var oldPpb = _ppb;
        var newPpb = EffectivePpb();
        if (Math.Abs(newPpb - oldPpb) < 1e-9)
        {
            _ppb = newPpb;
            _zoom = nw / newPpb;
            // the density did not change and the shown frame still covers the viewport - scale it smoothly
            if (ViewportCovered())
            {
                CheckAreaRequest(); // zooming without a rebuild also changes the visible world rectangle
                InvalidateVisual();
                return;
            }
        }

        RebuildBitmap();
    }

    private void PanBy(Vector dControl)
    {
        var tm = Terrain;
        if (tm is null) return;
        _focusX -= dControl.X / _wanted;
        _focusZ -= dControl.Y / _wanted;
        ClampFocus();
        // if the viewport is still inside the already drawn region, simply reposition the bitmap
        // (a cheap redraw instead of a heavy region rebuild).
        if (ViewportCovered())
        {
            Recentre();
            return;
        }

        RebuildBitmap();
    }

    /// <summary>Does the frame region cover the whole visible part of the world (can we zoom/pan without a rebuild)?</summary>
    private bool ViewportCovered()
    {
        return _bmp is not null && _frame is { } f && RegionCoversWorldVisible(f);
    }

    // ===================== REGION FILL =====================

    /// <summary>
    ///     Parallel computation of the visible region pixels into a plain BGRA buffer (stride = w*4).
    ///     Rows are independent -> Parallel.For loads every core (it used to be one thread on the UI thread)
    ///     - that is where the zoom speedup comes from. Returns bytes for direct insertion into a WriteableBitmap.
    ///     The Y layer (<paramref name="layerY" />) is a HORIZONTAL SLICE: the real block at that level is drawn
    ///     (see <see cref="TerrainColorizer.SliceColor" />), and air stays transparent (= the map background).
    ///     Voxels are read on demand, only for the visible area (<see cref="TerrainMap.Voxels" />).
    /// </summary>
    private byte[] ComputeRegion(TerrainMap tm, double ppb, int left, int top, int rw, int rh, int? layerY, int mode,
        TerrainColorCache? colorCache, bool applyFilter = true)
    {
        // Extra area layers are drawn without the active-area filter: the filter grid belongs to Terrain.
        var filter = applyFilter ? Filter : null;
        var W = tm.Width;
        double range = tm.MaxSurface - tm.MinSurface;
        if (range <= 0) range = 1;
        var w = Math.Max(1, (int)Math.Ceiling(rw * ppb));
        var h = Math.Max(1, (int)Math.Ceiling(rh * ppb));
        var px = new byte[w * h * 4];
        var slice = layerY is int && tm.Voxels is not null;

        if (ppb >= 1)
        {
            var s = Math.Max(1, (int)Math.Round(ppb));
            Parallel.For(0, rh, z =>
            {
                var i = (top + z - tm.MinZ) * W + (left - tm.MinX);
                var py = z * s;
                for (var x = 0; x < rw; x++)
                {
                    int wx = left + x, wz = top + z;
                    byte R = 0, G = 0, B = 0;
                    var draw = false;
                    if (filter is not null)
                    {
                        var fi = FilterGridIndex(filter, wx, wz);
                        if (fi >= 0 && filter.Count[fi] > 0)
                        {
                            var c = FilterColor(filter.TopBlock[fi]);
                            var boost = Math.Min(70, filter.Count[fi] * 14); // saturation by the number of blocks in the column
                            R = (byte)Math.Min(255, c.R + boost);
                            G = (byte)Math.Min(255, c.G + boost);
                            B = (byte)Math.Min(255, c.B + boost);
                            draw = true;
                        }
                        else if (!_filterDimOthers)
                        {
                            R = FilterBackground.R;
                            G = FilterBackground.G;
                            B = FilterBackground.B;
                            draw = true;
                        }
                    }

                    if (!draw)
                    {
                        if (slice)
                        {
                            bool has;
                            if (colorCache is not null)
                            {
                                var packed = TerrainColorizer.SliceColorCached(tm, i, wx, layerY!.Value, wz, mode, range, colorCache);
                                has = TerrainColorCache.HasBlock(packed);
                                R = TerrainColorCache.R(packed);
                                G = TerrainColorCache.G(packed);
                                B = TerrainColorCache.B(packed);
                            }
                            else
                            {
                                var (sr, sg, sb, h) = TerrainColorizer.SliceColor(tm, wx, layerY!.Value, wz, mode, range);
                                has = h;
                                R = sr; G = sg; B = sb;
                            }
                            if (has) draw = true; // air at the level is background (alpha 0)
                        }
                        else if (tm.SurfaceY[i] > 0)
                        {
                            if (colorCache is null || (filter is not null && _filterDimOthers))
                            {
                                // The cache stores the already-clamped ShadedContribution; dimming it after
                                // the clamp is not the same as the old clamp(c * shade * 0.35). Keep the
                                // exact path while the "dim others" overlay is on (and when there is no cache).
                                var c = TerrainColorizer.ColumnColor(tm, i, tm.SurfaceY[i], layerY, mode, range);
                                var sh = TerrainColorizer.ExtraShade(tm, i, mode);
                                if (filter is not null && _filterDimOthers) sh *= 0.35;
                                R = (byte)Math.Clamp(c.R * sh, 0, 255);
                                G = (byte)Math.Clamp(c.G * sh, 0, 255);
                                B = (byte)Math.Clamp(c.B * sh, 0, 255);
                            }
                            else
                            {
                                var packed = TerrainColorizer.ColumnColorCached(tm, i, tm.SurfaceY[i], layerY, mode, range, colorCache);
                                R = TerrainColorCache.R(packed);
                                G = TerrainColorCache.G(packed);
                                B = TerrainColorCache.B(packed);
                            }
                            draw = true;
                        }
                    }

                    if (draw)
                    {
                        var px0 = x * s;
                        for (var dy = 0; dy < s; dy++)
                        {
                            var row = (py + dy) * w + px0;
                            for (var dx = 0; dx < s; dx++)
                            {
                                var q = (row + dx) * 4;
                                px[q] = B;
                                px[q + 1] = G;
                                px[q + 2] = R;
                                px[q + 3] = 255;
                            }
                        }
                    }

                    i++;
                }
            });
        }
        else
        {
            var K = Math.Max(1, (int)Math.Ceiling(1.0 / ppb));
            var ow = Math.Max(1, (int)Math.Ceiling(rw / (double)K));
            var oh = Math.Max(1, (int)Math.Ceiling(rh / (double)K));
            Parallel.For(0, oh, oy =>
            {
                int z0 = top + oy * K, z1 = Math.Min(top + rh, z0 + K);
                for (var ox = 0; ox < ow; ox++)
                {
                    int x0 = left + ox * K, x1 = Math.Min(left + rw, x0 + K);
                    long rS = 0, gS = 0, bS = 0;
                    int seen = 0, inBand = 0;
                    var hitAny = false;
                    var hitCells = 0;
                    Color hitColor = default;
                    for (var z = z0; z < z1; z++)
                    {
                        var i = (z - tm.MinZ) * W + (x0 - tm.MinX);
                        for (var x = x0; x < x1; x++)
                        {
                            if (filter is not null)
                            {
                                var fi = FilterGridIndex(filter, x, z);
                                if (fi >= 0 && filter.Count[fi] > 0)
                                {
                                    if (!hitAny) hitColor = FilterColor(filter.TopBlock[fi]);
                                    hitAny = true;
                                    hitCells += filter.Count[fi];
                                }
                            }

                            if (slice)
                            {
                                // at the Y level only count columns that have a block: air is background
                                if (colorCache is not null)
                                {
                                    var packed = TerrainColorizer.SliceColorCached(tm, i, x, layerY!.Value, z, mode, range, colorCache);
                                    if (TerrainColorCache.HasBlock(packed))
                                    {
                                        rS += TerrainColorCache.R(packed);
                                        gS += TerrainColorCache.G(packed);
                                        bS += TerrainColorCache.B(packed);
                                        seen++;
                                    }
                                }
                                else
                                {
                                    var (sr, sg, sb, has) = TerrainColorizer.SliceColor(tm, x, layerY!.Value, z, mode, range);
                                    if (has) { rS += sr; gS += sg; bS += sb; seen++; }
                                }

                                i++;
                                continue;
                            }

                            var v = tm.SurfaceY[i];
                            if (v <= 0)
                            {
                                i++;
                                continue;
                            }

                            seen++;
                            if (layerY is int l)
                            {
                                if (tm.BottomY[i] > 0 && tm.BottomY[i] <= l && l <= tm.SurfaceY[i]) inBand++;
                            }
                            else
                            {
                                // The cache stores ShadedContribution: already clamped before summing, which is
                                // mandatory (shade up to 1.48 × snow 251 > 255 would wrap the average byte).
                                if (colorCache is not null)
                                {
                                    var packed = TerrainColorizer.ColumnColorCached(tm, i, v, layerY, mode, range, colorCache);
                                    rS += TerrainColorCache.R(packed);
                                    gS += TerrainColorCache.G(packed);
                                    bS += TerrainColorCache.B(packed);
                                }
                                else
                                {
                                    var c = TerrainColorizer.ColumnColor(tm, i, v, layerY, mode, range);
                                    var add = TerrainColorizer.ShadedContribution(c, TerrainColorizer.ExtraShade(tm, i, mode));
                                    rS += add.R; gS += add.G; bS += add.B;
                                }
                            }

                            i++;
                        }
                    }

                    var q = (oy * w + ox) * 4;
                    byte R, G, B;
                    if (hitAny)
                    {
                        var boost = Math.Min(70, hitCells * 6);
                        R = (byte)Math.Min(255, hitColor.R + boost);
                        G = (byte)Math.Min(255, hitColor.G + boost);
                        B = (byte)Math.Min(255, hitColor.B + boost);
                    }
                    else if (filter is not null && !_filterDimOthers)
                    {
                        R = FilterBackground.R;
                        G = FilterBackground.G;
                        B = FilterBackground.B;
                    }
                    else
                    {
                        if (seen == 0)
                        {
                            px[q + 3] = 0;
                            continue;
                        }

                        if (layerY is int && !slice)
                        {
                            var on = inBand * 2 >= seen;
                            R = on ? (byte)150 : (byte)210;
                            G = on ? (byte)150 : (byte)210;
                            B = on ? (byte)30 : (byte)235;
                        }
                        else
                        {
                            R = (byte)(rS / seen);
                            G = (byte)(gS / seen);
                            B = (byte)(bS / seen);
                        }

                        if (filter is not null && _filterDimOthers) // dim the base map under the overlay
                        {
                            R = (byte)(R * 0.35);
                            G = (byte)(G * 0.35);
                            B = (byte)(B * 0.35);
                        }
                    }

                    px[q] = B;
                    px[q + 1] = G;
                    px[q + 2] = R;
                    px[q + 3] = 255;
                }
            });
        }

        return px;
    }

    /// <summary>Enables/disables the block overlay filter. map = null turns it off.</summary>
    public void SetFilterOverlay(BlockFilterMap? map, IReadOnlyDictionary<int, Color>? colors, bool dimOthers)
    {
        Filter = map;
        _filterColors = colors is null ? null : new Dictionary<int, Color>(colors);
        _filterDimOthers = dimOthers;
        RebuildBitmap();
        InvalidateVisual();
    }

    /// <summary>Switches between "base map dimmed" and "matches only" without recomputing the filter.</summary>
    public void SetFilterDim(bool dimOthers)
    {
        if (_filterDimOthers == dimOthers) return;
        _filterDimOthers = dimOthers;
        if (Filter is not null)
        {
            RebuildBitmap();
            InvalidateVisual();
        }
    }

    /// <summary>A stable highlight colour for a block (the same in the legend, the map and the PNG export).</summary>
    public static Color HighlightColor(string? code, int id)
    {
        var (r, g, b) = FilterPalette.ColorFor(code, id);
        return Color.FromRgb(r, g, b);
    }

    private Color FilterColor(int id)
    {
        return _filterColors is not null && _filterColors.TryGetValue(id, out var c) ? c : HighlightColor(null, id);
    }

    /// <summary>Column index in the filter grid (-1 = out of bounds / no filter).</summary>
    private int FilterGridIndex(int wx, int wz) => FilterGridIndex(Filter, wx, wz);

    private int FilterGridIndex(BlockFilterMap? f, int wx, int wz)
    {
        if (f is null) return -1;
        if (wx < f.MinX || wz < f.MinZ || wx >= f.MinX + f.Width || wz >= f.MinZ + f.Height) return -1;
        return (wz - f.MinZ) * f.Width + (wx - f.MinX);
    }

    // ===================== RENDERING =====================

    /// <summary>Draws cached non-primary areas. Their frames always cover the whole area at the current
    /// LOD density, so a pan/zoom only scales the ready bitmap; the primary area is drawn afterwards.
    /// The transform is derived from the PRIMARY frame, not from the live focus: while an exact frame is
    /// frozen during a rebuild, using the live focus shifted every extra area relative to the primary map.</summary>
    private void DrawExtraLayers(DrawingContext dc, MapFrame primaryFrame)
    {
        var (primaryZoomX, primaryZoomY, primaryPanX, primaryPanY, _, _, _, _) = DisplayTransform(primaryFrame);
        double screenPpbX = primaryFrame.Ppb * primaryZoomX;
        double screenPpbY = primaryFrame.PpbY * primaryZoomY;
        if (!(screenPpbX > 0) || !(screenPpbY > 0)
            || double.IsInfinity(screenPpbX) || double.IsInfinity(screenPpbY)) return;
        foreach (var layer in _extraLayers)
        {
            if (layer.Bitmap is null) continue;
            var f = layer.Frame;
            double zoomX = screenPpbX / f.Ppb;
            double zoomY = screenPpbY / f.PpbY;
            if (!(zoomX > 0) || !(zoomY > 0) || double.IsInfinity(zoomX) || double.IsInfinity(zoomY)) continue;
            // Place the extra bitmap through the PRIMARY frame's screen mapping. This keeps every area
            // in the same coordinate system even when the primary frame is frozen during a rebuild.
            // Translation is in pre-scale units (the matrix scales it), hence the division by zoom.
            double screenX0 = primaryPanX * primaryZoomX + (f.Left - primaryFrame.Left) * screenPpbX;
            double screenY0 = primaryPanY * primaryZoomY + (f.Top - primaryFrame.Top) * screenPpbY;
            double panX = screenX0 / zoomX;
            double panY = screenY0 / zoomY;
            using (dc.PushTransform(Matrix.CreateTranslation(panX, panY) * Matrix.CreateScale(zoomX, zoomY)))
                dc.DrawImage(layer.Bitmap, new Rect(0, 0, layer.Bitmap.PixelSize.Width, layer.Bitmap.PixelSize.Height));
        }
    }

    public override void Render(DrawingContext dc)
    {
        dc.FillRectangle(BgBrush, new Rect(Bounds.Size));
        if (Terrain is null)
        {
            // empty state: a hint instead of an empty dark rectangle before a save is opened
            var ft = new FormattedText("Open a save (.vcdbs)…   scroll to zoom · drag to pan · Shift+drag to select",
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                Typeface.Default, 13, EmptyBrush);
            dc.DrawText(ft, new Point((Bounds.Width - ft.Width) / 2, (Bounds.Height - ft.Height) / 2));
            return;
        }

        if (_bmp is null || _frame is not { } frame)
        {
            // there is no map yet: the first bitmap build is running (this is not "no data" - we are waiting for data)
            DrawCenteredText(dc, "Rendering the map…");
            return;
        }
        DrawExtraLayers(dc, frame);
        var (dispZoomX, dispZoomY, dispPanX, dispPanY, _, _, _, _) = DisplayTransform(frame);
        var m = Matrix.CreateTranslation(dispPanX, dispPanY) * Matrix.CreateScale(dispZoomX, dispZoomY);
        using (dc.PushTransform(m))
        {
            dc.DrawImage(_bmp, new Rect(0, 0, _bmp.PixelSize.Width, _bmp.PixelSize.Height));

            var cellPx = 32.0 * Math.Max(frame.Ppb, frame.PpbY); // chunk size in bitmap pixels
            // draw the grid only when chunks are legible; zoomed far out the cells merge, so skip it
            if (_showChunkGrid && Terrain.PresentChunks.Count > 0 && cellPx >= 3.0)
            {
                double x0b = frame.Left, x1b = frame.Left + frame.W;
                double y0b = frame.Top, y1b = frame.Top + frame.H;
                double bw = _bmp.PixelSize.Width, bh = _bmp.PixelSize.Height;
                // Walk the VISIBLE chunk range instead of all PresentChunks (there can be tens of thousands):
                // previously every frame built 4 lines for every chunk in the world.
                int cxFrom = (int)Math.Floor(x0b / 32.0), cxTo = (int)Math.Floor(x1b / 32.0);
                int czFrom = (int)Math.Floor(y0b / 32.0), czTo = (int)Math.Floor(y1b / 32.0);
                for (var cx = cxFrom; cx <= cxTo; cx++)
                for (var cz = czFrom; cz <= czTo; cz++)
                {
                    if (!Terrain.PresentChunks.Contains((cx, cz))) continue;
                    double gx0 = cx * 32.0, gy0 = cz * 32.0;
                    if (gx0 + 32 < x0b || gx0 > x1b || gy0 + 32 < y0b || gy0 > y1b) continue; // outside the region
                    var x0 = Math.Clamp((gx0 - frame.Left) * frame.Ppb, 0, bw);
                    var y0 = Math.Clamp((gy0 - frame.Top) * frame.PpbY, 0, bh);
                    var x1 = Math.Clamp((gx0 + 32.0 - frame.Left) * frame.Ppb, 0, bw);
                    var y1 = Math.Clamp((gy0 + 32.0 - frame.Top) * frame.PpbY, 0, bh);
                    dc.DrawLine(GridPen, new Point(x0, y0), new Point(x1, y0));
                    dc.DrawLine(GridPen, new Point(x1, y0), new Point(x1, y1));
                    dc.DrawLine(GridPen, new Point(x1, y1), new Point(x0, y1));
                    dc.DrawLine(GridPen, new Point(x0, y1), new Point(x0, y0));
                }
            }

            // selected chunks: highlight over the map (they keep world geometry at any zoom)
            if (_selectedChunks.Count > 0)
            {
                double x0b = frame.Left, x1b = frame.Left + frame.W;
                double y0b = frame.Top, y1b = frame.Top + frame.H;
                foreach (var (cx, cz) in _selectedChunks)
                {
                    double gx0 = cx * 32.0, gy0 = cz * 32.0;
                    if (gx0 + 32 < x0b || gx0 > x1b || gy0 + 32 < y0b || gy0 > y1b) continue; // outside the region
                    var x0 = (gx0 - frame.Left) * frame.Ppb;
                    var y0 = (gy0 - frame.Top) * frame.PpbY;
                    var x1 = (gx0 + 32.0 - frame.Left) * frame.Ppb;
                    var y1 = (gy0 + 32.0 - frame.Top) * frame.PpbY;
                    dc.FillRectangle(SelFill, new Rect(x0, y0, x1 - x0, y1 - y0));
                    dc.DrawRectangle(SelPen, new Rect(x0, y0, x1 - x0, y1 - y0));
                }
            }
        }

        // The screen moved outside the built map (to another area or into the void between them):
        // without a label this would look like "the map disappeared". The other areas are drawn only
        // when the user scrolls to them - the window builds their map on AreaRequested.
        if (!ViewportShowsData()) DrawVoidHint(dc);

        // the rectangular selection rubber band (in the control's screen coordinates)
        if (_shiftSelect && Terrain is not null)
        {
            double l = Math.Min(_selRectStart.X, _selRectCur.X), t = Math.Min(_selRectStart.Y, _selRectCur.Y);
            double r = Math.Max(_selRectStart.X, _selRectCur.X), b = Math.Max(_selRectStart.Y, _selRectCur.Y);
            if (r - l >= 1 && b - t >= 1)
            {
                var rect = new Rect(l, t, r - l, b - t);
                dc.FillRectangle(_selDeselect ? DeselRubber : SelRubber, rect);
                dc.DrawRectangle(_selDeselect ? DeselPen : SelPen, rect);
            }
        }
    }

    // ===================== INPUT =====================

    /// <summary>
    ///     The label centred on an empty screen: it explains that one area of the save is built
    ///     while the user moved to another one (or into the void between them), and where to look for the nearest.
    /// </summary>
    private void DrawVoidHint(DrawingContext dc)
    {
        if (Terrain is not null) DrawCenteredText(dc, VoidHint());
    }

    private void DrawCenteredText(DrawingContext dc, string text)
    {
        var lines = text.Split('\n');
        double y = Math.Max(8, Bounds.Height / 2 - lines.Length * 10);
        foreach (var line in lines)
        {
            var ft = new FormattedText(line, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                Typeface.Default, 13, EmptyBrush);
            dc.DrawText(ft, new Point(Math.Max(8, (Bounds.Width - ft.Width) / 2), y));
            y += ft.Height + 2;
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var f = e.Delta.Y > 0 ? 1.2 : 1 / 1.2;
        HandleZoom(f, e.GetPosition(this));
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        _pressPos = p;
        _lastDrag = p;
        var props = e.GetCurrentPoint(this).Properties;
        var shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        // Shift+left click selects a rectangle (and adds a single chunk),
        // Shift+right click is the inverse action: it removes the selection (and from a single chunk).
        _shiftSelect = shift && (props.IsLeftButtonPressed || props.IsRightButtonPressed);
        _selDeselect = _shiftSelect && props.IsRightButtonPressed;
        if (_shiftSelect)
        {
            _selRectStart = p;
            _selRectCur = p;
            InvalidateVisual();
        }

        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
        // drag the band with the same button that started: left selects, right deselects
        var dragButton = _selDeselect ? props.IsRightButtonPressed : props.IsLeftButtonPressed;
        if (dragButton && !double.IsNaN(_lastDrag.X))
        {
            if (_shiftSelect)
            {
                // Shift+left/right drag stretches the selection band (instead of panning)
                _selRectCur = p;
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            var d = p - _lastDrag;
            _lastDrag = p;
            PanBy(d);
            e.Handled = true;
            return;
        }

        if (Terrain is null) return;
        // the world coordinate under the cursor comes from the DISPLAYED frame (not from the "intent")
        var (px2, py2) = DisplayWorld(p.X, p.Y);
        var wx = (int)Math.Floor(px2);
        var wz = (int)Math.Floor(py2);
        // the same block under the cursor: do not rebuild the text or redraw (saves a full render on every mouse step)
        if (wx == _lastHoverX && wz == _lastHoverZ) return;
        _lastHoverX = wx;
        _lastHoverZ = wz;
        if (wx < Terrain.MinX || wx >= Terrain.MinX + Terrain.Width || wz < Terrain.MinZ ||
            wz >= Terrain.MinZ + Terrain.Height)
        {
            // Outside the built map the coordinates are still needed: they show where
            // the user moved away from the area (there is no block under the cursor because there is no data).
            HoverText = $"X {wx} Z {wz} | no data (outside the loaded area)";
            HoverChanged?.Invoke(HoverText);
            return;
        }
        var i = (wz - Terrain.MinZ) * Terrain.Width + (wx - Terrain.MinX);
        var bid = Terrain.TopBlock[i];
        var code = Terrain.Registry?.CodeOf(bid);
        var filterInfo = "";
        var fiHover = FilterGridIndex(wx, wz);
        if (fiHover >= 0 && Filter is not null && Filter.Count[fiHover] > 0)
        {
            var fid = Filter.TopBlock[fiHover];
            var fcode = Terrain.Registry?.CodeOf(fid) ?? "id:" + fid;
            filterInfo = $"  |  * {fcode} at Y={Filter.TopY[fiHover]} ({Filter.Count[fiHover]} blocks)";
        }

        // with an active Y layer show the real block AT THAT level (the slice), not just the surface
        var sliceInfo = "";
        if (LayerY is int ly && Terrain.Voxels is not null)
            sliceInfo = Terrain.TryBlockAt(wx, ly, wz, out var sid) && sid != 0
                ? $"  |  at Y{ly}: {Terrain.Registry?.CodeOf(sid) ?? sid.ToString()} (id {sid})"
                : $"  |  at Y{ly}: air";
        HoverText =
            $"X {wx} Z {wz} | Y (surface) {Terrain.SurfaceY[i]} | {code ?? bid.ToString()} (id {bid}){sliceInfo}{filterInfo}";
        HoverChanged?.Invoke(HoverText);
        // The map is NOT redrawn on hover: the tooltip (when enabled) is a separate layer in MainWindow.
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var p = e.GetPosition(this);
        var shift = _shiftSelect;
        var deselect = _selDeselect;
        _shiftSelect = false;
        _selDeselect = false;
        if (Terrain is not null)
        {
            if (shift)
            {
                // commit the region: screen rectangle -> chunk range (only chunks that actually exist)
                double l = Math.Min(_selRectStart.X, p.X), t = Math.Min(_selRectStart.Y, p.Y);
                double r = Math.Max(_selRectStart.X, p.X), b = Math.Max(_selRectStart.Y, p.Y);
                int c0x = (int)Math.Floor(WorldX(l) / 32.0), c1x = (int)Math.Floor(WorldX(r) / 32.0);
                int c0z = (int)Math.Floor(WorldZ(t) / 32.0), c1z = (int)Math.Floor(WorldZ(b) / 32.0);
                if (r - l >= 8 && b - t >= 8)
                {
                    // a drag: left selects the region, right deselects the region
                    if (deselect) DeselectRect(c0x, c0z, c1x, c1z);
                    else SelectRect(c0x, c0z, c1x, c1z);
                }
                else
                {
                    // Shift+click without a drag: left adds the chunk, right removes it from the selection
                    if (deselect) DeselectChunk(c0x, c0z);
                    else SelectChunk(c0x, c0z);
                }
            }
            else if (Math.Abs(p.X - _pressPos.X) < 6 && Math.Abs(p.Y - _pressPos.Y) < 6)
            {
                // a click (without a drag) toggles the chunk under the cursor
                ToggleChunk((int)Math.Floor(WorldX(p.X) / 32.0), (int)Math.Floor(WorldZ(p.Y) / 32.0));
            }
        }

        _lastDrag = new Point(double.NaN, double.NaN);
        e.Pointer.Capture(null);
    }

    // control screen coordinate -> world block coordinate (control centre = focus)
    /// <summary>Screen point → world point from the displayed frame.</summary>
    private (double wx, double wz) DisplayWorld(double sx, double sy)
    {
        if (_bmp is null || _frame is not { } f)
            return (_focusX + (sx - ViewportW / 2) / _wanted, _focusZ + (sy - ViewportH / 2) / _wanted);
        var (_, _, _, _, dfx, dfz, dppbX, dppbY) = DisplayTransform(f);
        return (dfx + (sx - ViewportW / 2) / dppbX, dfz + (sy - ViewportH / 2) / dppbY);
    }

    private double WorldX(double px)
    {
        return DisplayWorld(px, 0).wx;
    }

    private double WorldZ(double py)
    {
        return DisplayWorld(0, py).wz;
    }

    /// <summary>
    ///     The consistent state of the displayed frame: the bitmap plus the geometry it was BUILT with.
    ///     Important: while a new region is computed in the background, Render must draw the old bitmap with ITS geometry,
    ///     otherwise the map jumps (old content with the new pan) - that is exactly what broke zooming.
    /// </summary>
    private readonly struct MapFrame
    {
        public readonly int Left, Top, RegionW, RegionH, BmpW, BmpH;
        /// <summary>Bitmap pixels per block on X and Y. A pyramid's output size is rounded up on
        /// each axis independently (e.g. 2336×25600 with CellSize 13 gives 180×1970), so using one
        /// density for both axes stretched the layer and put distant areas at the wrong Z.</summary>
        public readonly double Ppb, PpbY, Zoom, FocusX, FocusZ, Rs;
        public readonly Vector Pan;

        public MapFrame(int left, int top, int rw, int rh, int bw, int bh,
            double ppb, double zoom, double focusX, double focusZ, double rs, Vector pan)
            : this(left, top, rw, rh, bw, bh, ppb, ppb, zoom, focusX, focusZ, rs, pan)
        {
        }

        public MapFrame(int left, int top, int rw, int rh, int bw, int bh,
            double ppb, double ppbY, double zoom, double focusX, double focusZ, double rs, Vector pan)
        {
            Left = left;
            Top = top;
            RegionW = rw;
            RegionH = rh;
            BmpW = bw;
            BmpH = bh;
            Ppb = ppb;
            PpbY = ppbY;
            Zoom = zoom;
            FocusX = focusX;
            FocusZ = focusZ;
            Rs = rs;
            Pan = pan;
        }

        public int W => RegionW;
        public int H => RegionH;
    }
}