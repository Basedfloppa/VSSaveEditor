using System.Collections;
using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VSCore;
using VSSaveEditor.Components;

namespace VSSaveEditor;

public partial class MainWindow : Window, IChunkViewHost, IPlayersTabHost, IEntityEditHost, IEntitiesTabHost, IItemsTabHost
{
    private static readonly TimeSpan SettingsSaveDelay = TimeSpan.FromMilliseconds(350);
    private readonly ChunkViewService _chunkView; // chunk search (Services/ChunkViewService.cs)

    private readonly EditSession _edits = new(); // accumulated data edits: applied to the working copy
    private readonly EntitiesTabService _entities; // the "Entities" tab (Services/EntitiesTabService.cs)
    private readonly ItemsTabService _items; // the "Items" tab: inventory search (Services/ItemsTabService.cs)

    // ---- block overlay filter (the "Find blocks" tab) ----
    private readonly HashSet<string> _filterCodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, Color> _filterColors = new(); // id -> highlight colour

    private readonly Flyout _historyFlyout;
    private readonly PlayersTabService _players; // the "Players" tab (Services/PlayersTabService.cs)

    private readonly List<VSSaveFile>
        _retiredSaves = new(); // saves displaced by a new load but still read by a background scan

    private readonly ScanService _scan = new(); // scan model + epoch (Services/ScanService.cs)

    private readonly AppSettings _settings = AppSettings.Load();

    // One status line for the whole application (P1-7): sources write to their own channels, the senior message wins.
    private bool _busy;
    private ChunkAreas? _areas; // areas (connected chunk groups) of the save - the map is built for one of them
    private ChunkArea? _areaLoading; // which area is being built now (guard against a duplicate request)
    private ChunkArea? _areaQueued; // a request that arrived during a build: run it after the current one
    private bool _applying; // an apply of edits to the working copy is running
    private bool _closeConfirmed; // the user already confirmed closing (a second Closing does not ask again)
    private string? _savedCopyPath; // the last file the working copy was saved to
    private bool _deletingChunks; // guard against deleting chunks twice
    private List<object> _dump = new(); // the last assembled text dump - the "Copy raw dump" export
    private int _dumpSeq; // dump epoch: a background build does not publish a stale result
    private bool _exportingImage; // guard against exporting the map to PNG twice
    private CancellationTokenSource? _filterCts;
    private BlockFilterMap? _filterMap;
    private HashSet<int>? _filterMapKey; // which id set the current map was built for
    private int _lastCx, _lastCz; // the last shown chunk (fallback when the X/Z fields do not parse)
    private int? _layerYLast; // the last applied Y layer (for persistence)
    private int _loadEpoch; // load epoch: background tasks of older loads publish no data
    private int _lodIndex = TerrainView.LodAuto; // current LOD mode (the radio group in Settings)
    private bool _removingBlocks; // guard against starting block removal twice
    private bool _replacingBlocks; // guard against starting block replacement twice
    private int _renderMode = TerrainView.ModeBlocks; // current render mode (the radio group in Settings)
    private VSSaveFile? _save;
    private string? _selectedSave;
    private bool _settingsLoading = true; // true while saved settings are applied - do not write them back
    private DispatcherTimer? _settingsSaveTimer; // settings write debounce - do not write the file on every slider tick
    private bool _sliderReady;
    private readonly DispatcherTimer? _statusTimer;
    private bool _surfaceMode = true;

    public MainWindow()
    {
        // clean up working copies left by crashed previous runs (older than 3 days)
        VSSaveFile.CleanupStaleWorkingCopies();
        _chunkView = new ChunkViewService(this);
        _players = new PlayersTabService(this);
        Status.Changed += OnStatusChanged;
        // The timer is needed only while there are messages with a lifetime (operation results).
        _statusTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            Status.Expire();
            if (!Status.NeedsTimer) _statusTimer?.Stop();
        });
        InitializeComponent();
        // The "Entities" tab presenter takes the controls from the window lazily - create it after the layout.
        _entities = new EntitiesTabService(this);
        _items = new ItemsTabService(this);
        // The bottom drawer tabs live in Components/Tabs: the window subscribes to their events in the same place
        // where it applies the initial map state (radio/checkbox defaults no longer arrive on their own).
        WireTabComponents();
        // RenderModeCombo/LodCombo were removed - radio groups replace them (see Settings in Components/Tabs/SettingsTab.axaml).
        // The defaults (Blocks, Auto) are set with IsChecked="True" on the corresponding radios in the tab layout.
        Map.SelectedChunksChanged += UpdateSelInfo;
        // Map event subscriptions happen exactly once (they used to be registered on every save load and piled up)
        Map.HoverChanged += txt =>
        {
            Status.Set(StatusChannel.Hover, txt);
            UpdateMapTooltip(txt);
        };
        Map.LodStatusChanged += txt => LodStatusText.Text = txt;
        // The user scrolled to another area of the save - build its map in the background (P1-21).
        // Until then the map is drawn only for the loaded area, distant parts are not shown.
        Map.AreaRequested += area => _ = LoadAreaAsync(area);
        // "rendering…" in the layer label: a Y slice decodes the chunks of the visible area - when zoomed out
        // this takes noticeable time, and the user should see that the map is still rebuilding.
        Map.RebuildStateChanged += _ => UpdateLayerLabel();
        Closing += OnWindowClosing;
        Closed += (_, _) =>
        {
            SaveSettings();
            FlushSettingsSave();
            AppSettings.Flush(); // the last settings are written in the background - wait for them on exit
            // closing the window is the only moment when open connections must be released
            try
            {
                _save?.Dispose();
            }
            catch
            {
            }

            foreach (var s in _retiredSaves)
                try
                {
                    s.Dispose();
                }
                catch
                {
                }

            _retiredSaves.Clear();
            _save = null;
        };
        ApplySavedSettings();
        _settingsLoading = false;
        ApplySavedLayout();
        // The history flyout belongs to the button (it is not created per click): that way its content
        // is also reachable for headless probes, not only through the popup layer.
        _historyFlyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        _historyFlyout.Opening += (_, _) => RebuildHistoryFlyout();
        HistoryBtn.Flyout = _historyFlyout;
        RebuildRecentMenu();
        UpdatePendingStrip();
        SizeChanged += (_, _) => SaveWindowSize(); // with the settings debounce: do not spam the disk
    }

    /// <summary>Cached "all data" model of the save (available after the background scan).</summary>
    private SaveScanResult? ScanData => _scan.Data;

    /// <summary>The single status line model - read by diagnostics and headless probes.</summary>
    public AppStatus Status { get; } = new();

    // The last valid chunk is kept separately: with an empty/broken X/Z field we must not silently return 0,0
    private int CurrentCx => int.TryParse(ChunkXBox.Text, out var v) ? v : _lastCx;
    private int CurrentCz => int.TryParse(ChunkZBox.Text, out var v) ? v : _lastCz;

    // ===================== RECENT SAVES (P1-5) =====================

    private IReadOnlyList<string> Recent => _settings.RecentFiles;

    // ---- IChunkViewHost: the window gives the "Chunks" tab presenter the list, the panel, status and confirmations ----
    Control IChunkViewHost.Owner => this;
    VSSaveFile? IChunkViewHost.Save => _save;
    ScanService IChunkViewHost.Scan => _scan;
    EditSession IChunkViewHost.Edits => _edits;
    StackPanel IChunkViewHost.Panel => ChunkDataPanel;
    TextBox IChunkViewHost.HexBox => HexEditBox;
    ComboBox IChunkViewHost.ChunkPick => ChunkPick;

    string IChunkViewHost.ChunkQueryText
    {
        get => ChunkSearchBox.Text ?? "";
        set => ChunkSearchBox.Text = value;
    }

    string IChunkViewHost.ChunkListSummary
    {
        set => ChunkSummaryLabel.Text = value;
    }

    string IChunkViewHost.ChunkStatus
    {
        set => Status.Set(StatusChannel.Chunk, value);
    }

    void IChunkViewHost.SetSaveEnabled(bool enabled)
    {
        SaveCopyBtn.IsEnabled = enabled;
    }

    void IChunkViewHost.PendingChanged()
    {
        UpdatePendingStrip();
    }

    Task<bool> IChunkViewHost.Confirm(string title, string message)
    {
        return ConfirmDialog(title, message);
    }

    void IChunkViewHost.ShowItems(string query) => ShowItemsQuery(query);

    // ---- IEntitiesTabHost: the "Entities" tab draws the selected record card, the window provides navigation ----
    Control IEntitiesTabHost.Owner => this;
    ListBox IEntitiesTabHost.List => EntityList;
    StackPanel IEntitiesTabHost.DetailPanel => EntityDetailPanel;
    TextBlock IEntitiesTabHost.EmptyHint => EntitiesView.EntityEmptyHint;
    WrapPanel IEntitiesTabHost.ChipsPanel => EntityChipsPanel;

    string IEntitiesTabHost.Summary
    {
        set => EntitySummaryLabel.Text = value;
    }

    string IEntitiesTabHost.FilterText
    {
        get => EntityFilterBox.Text ?? "";
        set => EntityFilterBox.Text = value;
    }

    EntityCategory IEntitiesTabHost.Category => EntitiesView.Category;

    void IEntitiesTabHost.ShowChunk(int cx, int cz)
    {
        Map.FocusChunk(cx, cz);
        ShowChunkData(cx, cz);
    }

    void IEntitiesTabHost.ShowItems(string query) => ShowItemsQuery(query);

    void IEntitiesTabHost.SetCategory(EntityCategory category) => EntitiesView.SetCategory(category);

    // ---- IPlayersTabHost: the "Players" tab draws the details itself, the window provides controls and map navigation ----
    Control IPlayersTabHost.Owner => this;
    VSSaveFile? IPlayersTabHost.Save => _save;
    ScanService IPlayersTabHost.Scan => _scan;
    EditSession IPlayersTabHost.Edits => _edits;
    ListBox IPlayersTabHost.List => PlayerList;
    StackPanel IPlayersTabHost.Detail => PlayerDetailPanel;
    TextBlock IPlayersTabHost.EmptyHint => PlayerEmptyHint;
    TextBox IPlayersTabHost.Filter => PlayerFilterBox;
    CheckBox IPlayersTabHost.PendingOnly => PlayerPendingOnlyCheck;
    TextBlock IPlayersTabHost.Count => PlayerCountLabel;

    string IPlayersTabHost.Status
    {
        set => Status.Set(StatusChannel.Chunk, value);
    }

    void IPlayersTabHost.PendingChanged()
    {
        UpdatePendingStrip();
    }

    void IPlayersTabHost.FocusChunk(int cx, int cz)
    {
        if (cx == int.MinValue) return;
        Map.FocusChunk(cx, cz);
        ShowChunkData(cx, cz);
    }

    void IPlayersTabHost.ShowItems(string query) => ShowItemsQuery(query);

    // ---- IItemsTabHost: the "Items" tab searches inventories and leads back to the owner ----
    Control IItemsTabHost.Owner => this;
    ListBox IItemsTabHost.List => ItemList;
    StackPanel IItemsTabHost.DetailPanel => ItemDetailPanel;
    TextBlock IItemsTabHost.EmptyHint => ItemsView.ItemEmptyHint;
    string IItemsTabHost.FilterText => ItemFilterBox.Text ?? "";
    InventorySourceFilter IItemsTabHost.SourceFilter => ItemsView.SourceFilter;

    string IItemsTabHost.Summary
    {
        set => ItemSummaryLabel.Text = value;
    }

    string IItemsTabHost.Status
    {
        set => Status.Set(StatusChannel.Search, value);
    }

    void IItemsTabHost.ShowChunk(int cx, int cz)
    {
        Map.FocusChunk(cx, cz);
        SelectDataTab(ChunkDataView);
        ShowChunkData(cx, cz);
    }

    void IItemsTabHost.OpenOwner(ItemStackRow row)
    {
        if (row.IsPlayer)
        {
            SelectDataTab(PlayersView);
            PlayerPendingOnlyCheck.IsChecked = false;
            PlayerFilterBox.Text = row.Inventory.PlayerUid ?? row.OwnerLabel;
            _players.Refresh();
            return;
        }

        SelectDataTab(EntitiesView);
        _entities.SelectAddress(row.Inventory.Position, row.Inventory.Field, row.Inventory.Occurrence);
    }

    void IItemsTabHost.CopyText(string text, string note) => CopyToClipboard(text, note);

    /// <summary>Opens Items with a ready filter: from a chunk, entity or player card.</summary>
    private void ShowItemsQuery(string query)
    {
        ItemsView.ItemSourcePick.SelectedIndex = 0;
        ItemFilterBox.Text = query;
        SelectDataTab(ItemsView);
        _items.Refresh();
    }

    /// <summary>Selects a tab by its content rather than its index: the tab order may change.</summary>
    private void SelectDataTab(Control content)
    {
        for (var i = 0; i < DataTabs.ItemCount; i++)
            if (DataTabs.Items[i] is TabItem { Content: { } c } && ReferenceEquals(c, content))
            {
                DataTabs.SelectedIndex = i;
                return;
            }
    }

    // ---- IEntityEditHost: the window gives the dump entity card the save, the edit session and confirmations ----
    VSSaveFile? IEntityEditHost.Save => _save;
    EditSession IEntityEditHost.Edits => _edits;

    string IEntityEditHost.Status
    {
        set => Status.Set(StatusChannel.Chunk, value);
    }

    void IEntityEditHost.PendingChanged()
    {
        UpdatePendingStrip();
    }

    Task<bool> IEntityEditHost.Confirm(string title, string message)
    {
        return ConfirmDialog(title, message);
    }

    /// <summary>Copies the current model message into the single line and keeps the expiry timer running.</summary>
    private void OnStatusChanged()
    {
        var text = Status.Text;
        // There is exactly one line and no wrapping: newlines in result messages (a file path)
        // would otherwise take several visual lines. The full text is in the tooltip.
        var oneLine = text.Replace("\r\n", "\n").Replace("\n", " · ");
        if (!string.Equals(AppStatusText.Text, oneLine, StringComparison.Ordinal)) AppStatusText.Text = oneLine;
        ToolTip.SetTip(AppStatusText, text.Length > 0 ? text : null);
        if (Status.NeedsTimer)
        {
            if (_statusTimer is { IsEnabled: false }) _statusTimer.Start();
        }
        else
        {
            _statusTimer?.Stop();
        }
    }

    /// <summary>Progress with a label (P1-7): a bar without a label does not say what is being counted.</summary>
    private void ShowProgress(string label)
    {
        ProgressLabel.Text = label;
        ProgressLabel.IsVisible = true;
        LoadProgress.IsVisible = true;
    }

    private void HideProgress()
    {
        LoadProgress.IsVisible = false;
        LoadProgress.IsIndeterminate = false;
        ProgressLabel.IsVisible = false;
    }

    /// <summary>
    ///     Reports an unhandled exception to the user (called from the App global handler)
    ///     and clears any stuck busy flags so the window stays usable.
    /// </summary>
    public void ReportUnhandled(Exception ex)
    {
        try
        {
            Status.Set(StatusChannel.App, $"Unexpected error: {ex.Message}", priority: AppStatus.PriorityError);
            _busy = false;
            _applying = false;
            HideProgress();
        }
        catch
        {
            /* the error message itself must not throw */
        }
    }

    /// <summary>
    ///     Updates the tooltip overlay above the map (the text comes from TerrainView.HoverChanged).
    ///     The overlay is a separate layer, so a hover does not redraw the map.
    /// </summary>
    private void UpdateMapTooltip(string? text)
    {
        if (HoverTooltipCheck.IsChecked != true || string.IsNullOrEmpty(text))
        {
            MapTooltip.IsVisible = false;
            return;
        }

        MapTooltipText.Text = text;
        MapTooltip.IsVisible = true;
    }

    /// <summary>
    ///     Applies the saved settings to the controls (radio, checkbox, layer mode).
    ///     The Y layer is applied later in LoadSave, since it depends on the height range of the loaded world.
    /// </summary>
    private void ApplySavedSettings()
    {
        _settingsLoading = true;
        _renderMode = Math.Clamp(_settings.RenderMode, 0, 2);
        _lodIndex = Math.Clamp(_settings.LodIndex, 0, TerrainView.LodLabels.Length - 1);
        SettingsView.SelectRenderMode(_renderMode);
        SettingsView.SelectLod(_lodIndex);
        _surfaceMode = _settings.SurfaceMode;
        ChunkGridCheck.IsChecked = _settings.ShowChunkGrid;
        HoverTooltipCheck.IsChecked = _settings.ShowHoverTooltip;
        ExportScaleBox.Value = Math.Clamp(_settings.ExportScale, 1, 8);
        ExportFormatBox.SelectedIndex = Math.Clamp(_settings.ExportFormat, 0, 2);
        JpegQualityBox.Value = Math.Clamp(_settings.JpegQuality, 30, 100);
        ExportFilterBox.SelectedIndex = Math.Clamp(_settings.ExportFilterMode, 0, 3);
        ExportTransparentBgCheck.IsChecked = _settings.ExportTransparentBg;
        ExportRegionBox.SelectedIndex = Math.Clamp(_settings.ExportRegionMode, 0, 1);
        UpdateExportRegionLabel();
        _filterCodes.Clear();
        foreach (var c in _settings.OverlayCodes) _filterCodes.Add(c);
        FilterDimCheck.IsChecked = _settings.OverlayDim;
        // the bottom data panel is expanded after the scan in RunScanAsync (ApplyDataPanelVisibility)
    }

    private void SaveSettings()
    {
        if (_settingsLoading) return;
        _settings.RenderMode = _renderMode;
        _settings.LodIndex = _lodIndex;
        _settings.ShowChunkGrid = ChunkGridCheck.IsChecked == true;
        _settings.ShowHoverTooltip = HoverTooltipCheck.IsChecked == true;
        _settings.SurfaceMode = _surfaceMode;
        _settings.LayerY = _surfaceMode ? null : _layerYLast;
        _settings.ShowDataTabs = DataPanel.IsExpanded;
        _settings.OverlayCodes = _filterCodes.ToArray();
        _settings.OverlayDim = FilterDimCheck.IsChecked == true;
        _settings.OverlayEnabled = _filterMap is not null;
        _settings.ExportScale = (int)Math.Clamp(ExportScaleBox.Value ?? 1, 1, 8);
        _settings.ExportFormat = Math.Clamp(ExportFormatBox.SelectedIndex, 0, 2);
        _settings.JpegQuality = (int)Math.Clamp(JpegQualityBox.Value ?? 90, 30, 100);
        _settings.ExportFilterMode = Math.Clamp(ExportFilterBox.SelectedIndex, 0, 3);
        _settings.ExportTransparentBg = ExportTransparentBgCheck.IsChecked == true;
        _settings.ExportRegionMode = Math.Clamp(ExportRegionBox.SelectedIndex, 0, 1);
        ScheduleSettingsSave();
    }

    /// <summary>
    ///     Writing settings to disk with a debounce: a slider/radio/checkbox can raise dozens of events in a row,
    ///     and writing the file on each one is synchronous I/O on the UI thread. We write at most every ~350 ms, plus once on close.
    /// </summary>
    private void ScheduleSettingsSave()
    {
        if (_settingsLoading) return;
        _settingsSaveTimer ??= new DispatcherTimer(SettingsSaveDelay, DispatcherPriority.Background,
            (_, _) => FlushSettingsSave());
        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }

    private void FlushSettingsSave()
    {
        _settingsSaveTimer?.Stop();
        _settings.Save();
    }


    /// <summary>Updates the line about the chunk/region selection (a chunk click, Shift+drag selects a region).</summary>
    private void UpdateSelInfo()
    {
        var b = Map.SelectionBounds;
        DeleteChunksBtn.IsEnabled = b is not null && _save is not null && !_deletingChunks;
        UpdateExportRegionLabel();
        if (b is null)
        {
            Status.Set(StatusChannel.Selection, "No chunks selected");
            RefreshDumpForSelection(null);
            _chunkView.ShowEmptyState();
            return;
        }

        var (miX, miZ, maX, maZ) = b.Value;
        Status.Set(StatusChannel.Selection,
            $"Selected: {Map.SelectionCount} chunk(s) — region cx {miX}..{maX}, cz {miZ}..{maZ}");
        RefreshDumpForSelection(b);
        // a single chunk: show its structured data in the "Chunks" tab right away
        if (Map.SelectionCount == 1 && Map.SelectedChunks.FirstOrDefault() is var (sx, sz))
            ShowChunkData(sx, sz);
    }

    /// <summary>Explains what will be exported: the whole built area or the selected chunk region.</summary>
    private void UpdateExportRegionLabel()
    {
        if (ExportRegionLabel is null) return;
        var areaNote = _areas is { Areas.Count: > 1 } many
            ? $" · area {(Map.Terrain?.Area?.Index ?? 0) + 1} of {many.Areas.Count}"
            : "";
        if (ExportRegionBox.SelectedIndex != 1)
        {
            var t = Map.Terrain;
            // "the whole world" now means the built area: the map does not draw distant chunk groups (P1-21)
            ExportRegionLabel.Text = t is null ? "whole area" : $"whole area ({t.Width}×{t.Height} blocks){areaNote}";
            return;
        }

        if (Map.SelectionBounds is not { } b)
        {
            ExportRegionLabel.Text = "no chunks selected — the whole area will be exported";
            return;
        }

        var (miX, miZ, maX, maZ) = b;
        ExportRegionLabel.Text =
            $"cx {miX}..{maX}, cz {miZ}..{maZ} — {(maX - miX + 1) * 32}×{(maZ - miZ + 1) * 32} blocks";
    }

    /// <summary>
    ///     Builds the text dump in the background and stores it for "Copy raw dump". Without a selection, the dump
    ///     covers the whole save; with a selection, only the selected chunks. The model may not be ready yet - then
    ///     nothing changes. The result used to go straight into the "Dump" tab list; that page is gone
    ///     (a searchable record list replaced it), and the dump remains as an export for troubleshooting.
    /// </summary>
    private async void RefreshDumpForSelection((int minCx, int minCz, int maxCx, int maxCz)? bounds)
    {
        var data = ScanData;
        if (data is null) return;
        var byColumn = _scan.ByColumn;
        var seq = ++_dumpSeq;
        List<object> items;
        try
        {
            items = bounds is { } b
                ? await Task.Run(() => ScanDumpBuilder.BuildForSelection(data, byColumn, b))
                : await Task.Run(() => ScanDumpBuilder.Build(data));
        }
        catch (Exception ex)
        {
            if (seq == _dumpSeq)
                Status.Set(StatusChannel.Scan, $"Dump build failed: {ex.Message}", priority: AppStatus.PriorityError);
            return;
        }

        if (seq != _dumpSeq) return; // the selection changed while we computed - do not publish a stale dump
        _dump = items;
    }

    private void OnClearSelection(object? sender, RoutedEventArgs e)
    {
        Map.ClearSelection();
    }

    // ===================== CHUNK SEARCH AND CHUNK DATA VIEW =====================

    /// <summary>
    ///     Builds the chunk (cx, cz) index with counters and the initial list. The index is computed once
    ///     per scan: previously a ComboBox with ALL world coordinates was built on every scan -
    ///     on a big save that is tens of thousands of rows in which nothing could be found.
    /// </summary>
    private void PopulateChunkPick()
    {
        if (ScanData is null)
        {
            _chunkView.SetChunkIndex(Array.Empty<ChunkColumnInfo>());
            return;
        }

        _chunkView.SetChunkIndex(ChunkBrowser.Build(ScanData));
    }

    private void OnGoChunk(object? sender, RoutedEventArgs e)
    {
        if (!int.TryParse(ChunkXBox.Text, out var cx) || !int.TryParse(ChunkZBox.Text, out var cz))
        {
            Status.Set(StatusChannel.Chunk, "Chunk X/Z must be integers");
            return;
        }

        ShowChunkData(cx, cz);
    }

    // ===================== CHUNK SEARCH =====================

    /// <summary>Rebuilds the chunk list for the query (parsing and error reporting live in ChunkQuery).</summary>
    private void OnChunkSearch(object? sender, RoutedEventArgs e)
    {
        if (ScanData is null)
        {
            Status.Set(StatusChannel.Search, "Save not scanned yet.");
            return;
        }

        _chunkView.RefreshChunkList();
        Status.Set(StatusChannel.Search, ChunkSummaryLabel.Text ?? "");
    }

    private void OnChunkSearchClear(object? sender, RoutedEventArgs e)
    {
        ChunkSearchBox.Text = "";
        _chunkView.RefreshChunkList();
    }

    // ===================== CHUNK SELECTION =====================

    private void OnChunkPicked(object? sender, SelectionChangedEventArgs e)
    {
        if (ChunkPick.Tag is not List<(int X, int Z, int Ent, int BE)> cols || ChunkPick.SelectedIndex < 0) return;
        var c = cols[ChunkPick.SelectedIndex];
        ShowChunkData(c.X, c.Z);
    }

    /// <summary>
    ///     Shows a chunk: the X/Z fields and the "last valid chunk" are window state,
    ///     while blob parsing and the cards belong to ChunkViewService.
    /// </summary>
    private void ShowChunkData(int cx, int cz)
    {
        ChunkXBox.Text = cx.ToString();
        ChunkZBox.Text = cz.ToString();
        _lastCx = cx;
        _lastCz = cz; // remember the valid chunk: with broken input CurrentCx/Cz must not return 0,0
        _chunkView.Show(cx, cz);
    }

    /// <summary>
    ///     Hex fallback: parsing and applying live in ChunkViewService (the panel, the hex box and the
    ///     confirmation come in through IChunkViewHost).
    /// </summary>
    private async void OnApplyHex(object? sender, RoutedEventArgs e)
    {
        await _chunkView.ApplyHexAsync();
    }

    /// <summary>
    ///     The "Review &amp; apply…" button in the drawer and the "Players" tab: first show what
    ///     exactly will go into the working copy, and only then apply (P1-2).
    /// </summary>
    private async void OnReviewAndCommit(object? sender, RoutedEventArgs e)
    {
        await ReviewAndCommitAsync();
    }

    /// <summary>
    ///     The former "Save edits to copy…" entry - the same review/apply path, so that there are not two
    ///     different confirmations for one operation.
    /// </summary>
    private async void OnSaveEditsToCopy(object? sender, RoutedEventArgs e)
    {
        await ReviewAndCommitAsync();
    }

    private async Task ReviewAndCommitAsync()
    {
        if (_save is null || _selectedSave is null || !_edits.HasPending || _applying) return;
        try
        {
            // the report reads blobs from the working copy - with many edits this is not instant
            Status.Set(StatusChannel.Chunk, "Building the change preview…");
            var save = _save;
            var scan = ScanData;
            var edits = _edits;
            var report = await Task.Run(() => PendingDiff.Build(edits, save, scan));
            Status.Set(StatusChannel.Chunk, "");
            var result = await PendingChangesDialog.ShowAsync(this, report, ApplyEditsAsync);
            if (result is null) Status.Set(StatusChannel.Chunk, "Review closed — nothing was applied.");
        }
        catch (Exception ex)
        {
            Status.Set(StatusChannel.Chunk, $"Preview failed: {ex.Message}", priority: AppStatus.PriorityError);
        }
    }

    /// <summary>
    ///     Applies the accumulated edits to the WORKING COPY instead of creating a new copy from the original.
    ///     After that the working copy itself shows the changed data, and History remembers what was applied.
    ///     Writing the file to disk is a separate "Save a copy…" command (next to the open save).
    /// </summary>
    private async Task<ApplyResult> ApplyEditsAsync(IProgress<string> stage)
    {
        if (_save is null || _selectedSave is null) throw new InvalidOperationException("no save is open");
        if (_applying) throw new InvalidOperationException("edits are already being applied");
        _applying = true;
        try
        {
            // The edit snapshot (blob references) is taken instantly on the UI thread, while reading the blobs
            // and splicing the payload edits go to the background: otherwise the window freezes on every chunk (P1-12).
            var save = _save;
            var target = _selectedSave!; // capture the target before the background work
            var changeSet = _edits.Snapshot();
            stage.Report($"Materialising {changeSet.Count} change(s)…");
            var changes = await Task.Run(() => EditSession.Materialize(save, changeSet));
            stage.Report($"Applying {changes.Count} change(s) to the working copy…");
            var rows = await Task.Run(() => save.ApplyChanges(changes));
            var recorded = _edits.CommitPending(); // pending -> History, not into the void
            _chunkView.EndEditing();
            _players.EndEditing();
            _chunkView.UpdateDirtyStatus();
            UpdatePendingStrip();
            stage.Report("Refreshing the working copy…");
            await ScanAsync(save, _loadEpoch); // the cards read the already changed blobs
            UpdateWindowTitle();
            // above Scan (85): otherwise "Scan complete" after the rescan would hide the main point -
            // the edits are applied but the file is not saved yet
            Status.Set(StatusChannel.Chunk,
                $"Applied {recorded} edit(s) into the working copy ({rows} row(s)). Original unchanged: {target}. "
                + "Use File → Save a copy… to write the .vcdbs next to the original.",
                ttlSeconds: 30, priority: 90);
            return new ApplyResult(save.Path, target);
        }
        finally
        {
            _applying = false;
        }
    }

    // ===================== EDIT HISTORY: UNDO/REDO (P1-1) =====================

    private void OnUndo(object? sender, RoutedEventArgs e)
    {
        ApplyHistory(_edits.Undo(), true);
    }

    private void OnRedo(object? sender, RoutedEventArgs e)
    {
        ApplyHistory(_edits.Redo(), false);
    }

    /// <summary>
    ///     Carries the undo/redo result through the UI: redraws the affected area,
    ///     refreshes the strip and the save buttons. The strip lives in the drawer header, not in the status line.
    /// </summary>
    private void ApplyHistory(EditRecord? record, bool undone)
    {
        if (record is null) return;
        if (record.IsPlayer)
        {
            _players.Refresh();
        }
        else
        {
            var visible = _scan.ColumnsFor(_chunkView.CurrentCx, _chunkView.CurrentCz)
                .Any(c => c.Position == record.Key);
            if (visible) _chunkView.RefreshPosition(record.Key);
        }

        _chunkView.UpdateDirtyStatus();
        UpdatePendingStrip();

        // The "Entities" tab card holds a parsed copy of the record: after an undo/redo it must be
        // re-read, otherwise the card would show a value the session no longer has.
        _entities.ReloadDetail();

        var what = record.IsPlayer ? $"player #{record.Key}" : record.Scope;
        Status.Set(StatusChannel.Chunk, $"{(undone ? "Undone" : "Redone")}: {what} · {record.Label}. "
                                        + $"{_edits.UndoCount} undo / {_edits.RedoCount} redo step(s) left.");
    }

    /// <summary>Syncs the Undo/Redo/Review/Save buttons and the drawer header counter with the EditSession state.</summary>
    public void UpdatePendingStrip()
    {
        if (UndoBtn is null) return; // the window is still being built (InitializeComponent has not finished)
        var journal = _edits.Journal;
        UndoBtn.IsEnabled = journal.CanUndo;
        RedoBtn.IsEnabled = journal.CanRedo;
        ReviewBtn.IsEnabled = _edits.HasPending && !_applying;
        if (SaveCopyNowBtn is not null) SaveCopyNowBtn.IsEnabled = _save is not null && !_applying;
        // The History badge shows the whole session history: both applied and not yet applied edits.
        // Otherwise it would drop to zero after "Apply", even though the history should have just become non-empty.
        PendingBadgeText.Text = (_edits.AppliedCount + journal.UndoCount).ToString();
        ToolTip.SetTip(UndoBtn, journal.NextUndo is { } u ? $"Undo: {u.Scope} · {u.Label}" : "Nothing to undo");
        ToolTip.SetTip(RedoBtn, journal.NextRedo is { } r ? $"Redo: {r.Scope} · {r.Label}" : "Nothing to redo");
        if (HistoryBtn is not null)
            ToolTip.SetTip(HistoryBtn,
                $"History: {_edits.AppliedCount} applied, {journal.UndoCount} pending"
                + (_edits.UnsavedAppliedCount > 0
                    ? $" · {_edits.UnsavedAppliedCount} not saved as a copy yet"
                    : ""));
        if (WorkingCopyLabel is not null)
        {
            var editable = _save?.SourcePath is { Length: > 0 };
            WorkingCopyLabel.IsVisible = editable;
            if (editable)
            {
                WorkingCopyLabel.Text = _edits.UnsavedAppliedCount > 0 ? "working copy · unsaved" : "working copy";
                ToolTip.SetTip(WorkingCopyLabel,
                    $"Edits go to a temporary working copy. Original (unchanged): {_save!.SourcePath}");
            }
        }
    }

    /// <summary>The window title shows that the working copy is what is open, and whether anything is unsaved.</summary>
    private void UpdateWindowTitle()
    {
        var name = _selectedSave is null ? "" : " — " + Path.GetFileName(_selectedSave);
        var dirty = _edits.UnsavedAppliedCount > 0 || _edits.HasPending ? " *" : "";
        Title = "VS Save Editor" + name + dirty;
    }

    /// <summary>
    ///     The history flyout: newest on top, reverted entries dimmed, the journal cursor marker.
    ///     The content is rebuilt on every open: the history changes after every edit.
    /// </summary>
    private void OnShowHistory(object? sender, RoutedEventArgs e)
    {
        RebuildHistoryFlyout();
    }

    private void RebuildHistoryFlyout()
    {
        _historyFlyout.Content = BuildHistoryContent();
    }

    private Control BuildHistoryContent()
    {
        var entries = _edits.Journal.BuildView();
        var applied = _edits.Applied;
        var panel = new StackPanel { Spacing = 2, Margin = new Thickness(10), MinWidth = 420 };

        if (applied.Count == 0 && entries.Count == 0)
        {
            CardUi.AddLine(this, panel,
                "No edits yet. Field edits and raw hex land here; applied edits stay in this list.");
        }
        else
        {
            if (applied.Count > 0)
            {
                CardUi.AddHeader(panel,
                    $"Applied to the working copy ({applied.Count})"
                    + (_edits.UnsavedAppliedCount > 0 ? $" · {_edits.UnsavedAppliedCount} not saved as a copy" : ""));
                // the flyout must not build thousands of rows: the newest 300 suffice, the rest is a counter
                var shownApplied = applied.Skip(Math.Max(0, applied.Count - 300)).ToList();
                if (shownApplied.Count < applied.Count)
                    CardUi.AddLine(this, panel,
                        $"{applied.Count - shownApplied.Count} earlier applied edit(s) not shown", gray: true);
                DateTime? at = null;
                foreach (var a in shownApplied.AsEnumerable().Reverse())
                {
                    if (a.At != at)
                    {
                        at = a.At;
                        CardUi.AddHeader(panel, $"Applied {a.At:HH:mm:ss}");
                    }

                    var where = a.SavedTo is { Length: > 0 } saved
                        ? $"  — saved to {Path.GetFileName(saved)}"
                        : "  — not in a saved copy yet";
                    CardUi.AddLine(this, panel, $"{a.Scope} · {a.Label}{where}", isMono: true,
                        gray: a.SavedTo is null);
                }
            }

            if (entries.Count > 0)
            {
                CardUi.AddHeader(panel, "Pending (not applied yet)");
                string? scope = null;
                foreach (var entry in entries)
                {
                    if (entry.Record.Scope != scope)
                    {
                        scope = entry.Record.Scope;
                        CardUi.AddHeader(panel, scope);
                    }

                    var marker = entry.IsNext ? entry.IsFuture ? "next redo → " : "next undo → " : "   ";
                    var line = new TextBlock
                    {
                        Text = marker + entry.Record.Label,
                        FontFamily = UiStyle.Mono,
                        FontSize = UiStyle.MonoFontSize,
                        TextWrapping = TextWrapping.Wrap,
                        // reverted edits are dimmed: they have not returned into the data yet
                        Opacity = entry.IsFuture ? 0.45 : 1.0,
                        FontWeight = entry.IsNext ? FontWeight.Bold : FontWeight.Normal
                    };
                    CardUi.BindTheme(this, line, TextBlock.ForegroundProperty,
                        entry.IsFuture ? "TextSecondaryBrush" : "TextPrimaryBrush");
                    panel.Children.Add(line);
                }

                CardUi.AddLine(this, panel, "Ctrl+Z undo · Ctrl+Y (or Ctrl+Shift+Z) redo", gray: true);
            }

            CardUi.AddLine(this, panel,
                "Applied edits are already part of the loaded working copy; Ctrl+Z only touches pending edits.",
                gray: true);
        }

        return new ScrollViewer
            { Content = panel, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    /// <summary>Puts the save at the top of the recent list and refreshes the menu and the start panel.</summary>
    private void PushRecent(string path)
    {
        _settings.RecentFiles = RecentFileList.Push(Recent, path).ToArray();
        RebuildRecentMenu();
        ScheduleSettingsSave();
    }

    private void OnClearRecent(object? sender, RoutedEventArgs e)
    {
        _settings.RecentFiles = Array.Empty<string>();
        RebuildRecentMenu();
        ScheduleSettingsSave();
        Status.Set(StatusChannel.App, "Recent saves list cleared.");
    }

    /// <summary>Rebuilds the File → Recent submenu; "Clear list" stays the last item.</summary>
    private void RebuildRecentMenu()
    {
        if (RecentMenu is null) return;
        while (RecentMenu.Items.Count > 1) RecentMenu.Items.RemoveAt(0);
        var index = 1;
        foreach (var path in Recent)
        {
            var item = new MenuItem
            {
                Header = RecentMenuLabel(path, index),
                IsEnabled = RecentFileList.Exists(path)
            };
            ToolTip.SetTip(item, path);
            var captured = path;
            item.Click += (_, _) => LoadSave(captured);
            RecentMenu.Items.Insert(RecentMenu.Items.Count - 1, item);
            index++;
        }

        if (Recent.Count == 0)
        {
            var empty = new MenuItem { Header = "(no recent saves yet)", IsEnabled = false };
            RecentMenu.Items.Insert(0, empty);
        }
    }

    private static string RecentMenuLabel(string path, int index)
    {
        var name = Path.GetFileName(path);
        if (!RecentFileList.Exists(path)) return $"{name}  (missing)";
        return index <= 9 ? $"_{index}  {name}" : name;
    }

    private static string DescribeRecent(string path)
    {
        var name = Path.GetFileName(path);
        var dir = Path.GetDirectoryName(path) ?? "";
        if (!RecentFileList.Exists(path)) return $"{name}  (missing)     {dir}";
        try
        {
            var fi = new FileInfo(path);
            return $"{name}  —  {fi.Length / (1024.0 * 1024.0):0.#} MB     {dir}";
        }
        catch
        {
            return $"{name}     {dir}";
        }
    }

    /// <summary>Opens the Nth recent save (Ctrl+1…Ctrl+9).</summary>
    private void OpenRecentAt(int index)
    {
        if (index < 0 || index >= Recent.Count) return;
        var path = Recent[index];
        if (!RecentFileList.Exists(path))
        {
            Status.Set(StatusChannel.App, $"Recent save #{index + 1} is missing: {path}",
                priority: AppStatus.PriorityError);
            return;
        }

        LoadSave(path);
    }

    // ===================== BLOCK OVERLAY FILTER (the "Find blocks" tab) =====================

    /// <summary>Fills the block list with the REAL block set of the open save (the scan histogram).</summary>
    private void PopulateFilterList()
    {
        if (ScanData is null || _save is null) return;
        var query = (FilterSearchBox.Text ?? "").Trim();
        var matched = ScanData.WorldBlockCodeCounts
            .OrderByDescending(kv => kv.Value)
            .Where(kv => query.Length == 0 || kv.Key.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        const int maxRows = 250;
        var shown = matched.Take(maxRows).ToList();

        FilterListPanel.Children.Clear();
        AddHeader(FilterListPanel, query.Length == 0
            ? $"Blocks in this save: {ScanData.WorldBlockCodeCounts.Count:N0} (top {shown.Count} by count)"
            : $"Matches for '{query}': {matched.Count:N0} (showing {shown.Count})");
        if (matched.Count > shown.Count)
            AddLine(FilterListPanel, $"   … {matched.Count - shown.Count:N0} more - refine your search", gray: true);

        var reg = _save.BlockRegistry;
        // colours for already selected codes (including ones restored from settings) - so the map and legend match
        foreach (var code in _filterCodes)
        foreach (var id in reg.IdsOf(code))
            _filterColors[id] = TerrainView.HighlightColor(code, id);
        foreach (var kv in shown)
        {
            var code = kv.Key;
            var ids = reg.IdsOf(code);
            var color = ids.Count > 0 && _filterColors.TryGetValue(ids[0], out var c)
                ? c
                : TerrainView.HighlightColor(code, ids.Count > 0 ? ids[0] : 0);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new Border
            {
                Width = 12,
                Height = 12,
                Background = new SolidColorBrush(color),
                // A fixed gray rather than a theme brush: the swatch must outline an arbitrary block colour
                // and stay legible on any theme, so it deliberately does not depend on the theme.
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center
            });
            var cb = new CheckBox
            {
                Content = $"{code}  -  {kv.Value:N0} blocks",
                IsChecked = _filterCodes.Contains(code),
                Tag = code,
                VerticalAlignment = VerticalAlignment.Center
            };
            cb.IsCheckedChanged += OnFilterCodeToggled;
            row.Children.Add(cb);
            FilterListPanel.Children.Add(row);
        }

        FilterBuildBtn.IsEnabled = matched.Count > 0;
        UpdateRemoveBlocksEnabled();
        UpdateFilterStatus();
    }

    /// <summary>
    ///     The delete button is enabled exactly when there is a selection and no write is running. Checkboxes can
    ///     be ticked and unticked at any time, so the state is recomputed on every selection change,
    ///     not only on a list rebuild (otherwise the button would stay gray after the very first tick).
    /// </summary>
    private void UpdateRemoveBlocksEnabled()
    {
        var enabled = _filterCodes.Count > 0 && !_removingBlocks && !_replacingBlocks
                      && _save is not null && ScanData is not null;
        RemoveBlocksBtn.IsEnabled = enabled;
        ReplaceBlocksBtn.IsEnabled = enabled;
    }

    private void OnFilterSearchChanged(object? sender, TextChangedEventArgs e)
    {
        PopulateFilterList();
    }

    private void OnFilterCodeToggled(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || cb.Tag is not string code) return;
        if (cb.IsChecked == true) _filterCodes.Add(code);
        else _filterCodes.Remove(code);
        // pin the colour to ALL ids of this code so the map and the legend match
        if (_save is not null)
            foreach (var id in _save.BlockRegistry.IdsOf(code))
                _filterColors[id] = TerrainView.HighlightColor(code, id);
        UpdateRemoveBlocksEnabled();
        SaveSettings();
        UpdateFilterStatus();
    }

    /// <summary>
    ///     Removes the block codes ticked in the list (their cells become air) in the WORKING COPY.
    ///     This is cleaning a world of the blocks of an uninstalled mod: the mod code stays in BlockIDs, but not a single cell
    ///     with it remains in the chunks. There are deliberately no ready-made categories in the UI - exactly what is ticked
    ///     is removed, and the search only narrows the list. The original is not modified; block entities on the removed
    ///     blocks are left alone.
    /// </summary>
    private async void OnRemoveBlocks(object? sender, RoutedEventArgs e)
    {
        if (_removingBlocks) return;
        if (_save is null || _selectedSave is null || ScanData is null)
        {
            Status.Set(StatusChannel.Filter, "Open a save and wait for the scan to finish first.");
            return;
        }

        if (_filterCodes.Count == 0)
        {
            Status.Set(StatusChannel.Filter, "Select the block codes to remove first.");
            return;
        }

        // code -> id: usually through the registry; for "id:NNN" (the code was not in BlockIDs) take the number itself
        var reg = _save.BlockRegistry;
        var ids = new SortedSet<int>();
        var unresolved = new List<string>();
        foreach (var code in _filterCodes)
        {
            var codeIds = reg.IdsOf(code);
            if (codeIds.Count > 0)
            {
                foreach (var id in codeIds) ids.Add(id);
                continue;
            }

            if (code.StartsWith("id:", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(code.AsSpan(3), out var raw) && raw is > 0 and <= ushort.MaxValue)
            {
                ids.Add(raw);
                continue;
            }

            unresolved.Add(code);
        }

        if (ids.Count == 0)
        {
            Status.Set(StatusChannel.Filter, "The selected codes have no block ids in this save.",
                priority: AppStatus.PriorityError);
            return;
        }

        long cells = 0;
        var vanilla = 0;
        var rows = new List<(string Code, long Count)>();
        foreach (var code in _filterCodes)
        {
            var count = ScanData.WorldBlockCodeCounts.TryGetValue(code, out var c) ? c : 0;
            cells += count;
            if (!code.Contains(':')) vanilla++;
            rows.Add((code, count));
        }

        rows.Sort((a, b) => b.Count.CompareTo(a.Count));

        var msg = new StringBuilder();
        msg.AppendLine(
            $"{_filterCodes.Count} block code(s) → {ids.Count} block id(s), {cells:N0} block cell(s) in this world.");
        msg.AppendLine(
            "Their cells become AIR in the working copy; the original save is not touched. "
            + "Use File → Save a copy… to write the result next to it.");
        msg.AppendLine();
        foreach (var (code, count) in rows.Take(8)) msg.AppendLine($"  {code} — {count:N0}");
        if (rows.Count > 8) msg.AppendLine($"  … and {rows.Count - 8} more code(s)");
        if (vanilla > 0)
        {
            msg.AppendLine();
            msg.AppendLine(
                $"WARNING: {vanilla} selected code(s) are VANILLA (no domain). Removing those destroys world content, not just leftover mod blocks.");
        }

        if (unresolved.Count > 0)
            msg.AppendLine($"\n{unresolved.Count} code(s) are unknown to this save's registry and will be skipped.");
        msg.AppendLine("\nBlock entities / entities that referenced these blocks are left in place.");
        msg.Append("\nContinue?");

        _removingBlocks = true;
        UpdateRemoveBlocksEnabled();
        try
        {
            if (!await ConfirmDialog("Remove blocks", msg.ToString())) return;

            var save = _save;
            var wanted = ids.ToArray();
            var codeCount = _filterCodes.Count;
            ShowProgress("Removing blocks in the working copy…");
            LoadProgress.IsVisible = true;
            LoadProgress.IsIndeterminate = false;
            LoadProgress.Value = 0;
            var progress = new UiProgress(pct =>
            {
                LoadProgress.Value = pct;
                Status.Set(StatusChannel.Filter, $"Removing blocks in the working copy… {pct}%");
            });
            var res = await Task.Run(() => save.RemoveBlocks(wanted, progress));

            var mapOk = true;
            if (res.ChunksChanged > 0)
            {
                _edits.RecordApplied($"find blocks · {codeCount} code(s)",
                    $"erased {res.PaletteEntriesCleared:N0} palette entr(ies) in {res.ChunksChanged:N0} chunk(s)");
                UpdatePendingStrip();
                // The map was drawn from the previous blobs: without a rebuild it would show the removed blocks.
                Map.ClearSelection();
                mapOk = await RebuildMapAfterMutationAsync(save, Map.Terrain?.Area);
                if (!ReferenceEquals(save, _save)) return;
                await ScanAsync(save, _loadEpoch);
            }

            Status.Set(StatusChannel.App, res.ChunksChanged == 0
                ? $"No chunk in this save used the selected block ids — nothing changed (checked {res.ChunksScanned:N0} chunk(s))."
                : $"Erased {res.PaletteEntriesCleared:N0} palette entr(ies) across {res.ChunksChanged:N0} of {res.ChunksScanned:N0} chunk(s) in the working copy. "
                  + $"Original unchanged: {_selectedSave}. Use File → Save a copy… to keep the cleaned world."
                  + (mapOk ? "" : " The map could not be rebuilt — reopen the save to refresh it."),
                ttlSeconds: 30, priority: 90);
        }
        catch (Exception ex)
        {
            Status.Set(StatusChannel.App, $"Remove blocks failed: {ex.Message}", priority: AppStatus.PriorityError);
        }
        finally
        {
            _removingBlocks = false;
            UpdateRemoveBlocksEnabled();
            HideProgress();
        }
    }


    /// <summary>
    ///     Replaces the ticked block codes with another block in the WORKING COPY. Only the int32
    ///     in the chunk palette changes, so the bit planes, light and other fields stay untouched.
    ///     The target block is chosen from the BlockIDs of this save: writing an id the game does not know
    ///     would mean an invisible/broken block.
    /// </summary>
    private async void OnReplaceBlocks(object? sender, RoutedEventArgs e)
    {
        if (_replacingBlocks || _removingBlocks) return;
        if (_save is null || _selectedSave is null || ScanData is null)
        {
            Status.Set(StatusChannel.Filter, "Open a save and wait for the scan to finish first.");
            return;
        }

        if (_filterCodes.Count == 0)
        {
            Status.Set(StatusChannel.Filter, "Select the block codes to replace first.");
            return;
        }

        var reg = _save.BlockRegistry;
        var ids = new SortedSet<int>();
        var unresolved = new List<string>();
        foreach (var code in _filterCodes)
        {
            var codeIds = reg.IdsOf(code);
            if (codeIds.Count > 0)
            {
                foreach (var id in codeIds) ids.Add(id);
                continue;
            }

            if (code.StartsWith("id:", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(code.AsSpan(3), out var raw) && raw is > 0 and <= ushort.MaxValue)
            {
                ids.Add(raw);
                continue;
            }

            unresolved.Add(code);
        }

        if (ids.Count == 0)
        {
            Status.Set(StatusChannel.Filter, "The selected codes have no block ids in this save.",
                priority: AppStatus.PriorityError);
            return;
        }

        long cells = 0;
        foreach (var code in _filterCodes)
            cells += ScanData.WorldBlockCodeCounts.TryGetValue(code, out var c) ? c : 0;

        var choices = new List<BlockReplaceDialog.Choice> { new("air", 0) };
        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "air" };
        foreach (var code in reg.Codes.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
        {
            if (!seenCodes.Add(code)) continue;
            var codeIds = reg.IdsOf(code);
            if (codeIds.Count == 0) continue;
            choices.Add(new BlockReplaceDialog.Choice(code, codeIds.Min()));
        }

        var pick = await BlockReplaceDialog.ShowAsync(this, choices,
            $"{_filterCodes.Count} selected code(s), {cells:N0} block cell(s) in this world.");
        if (pick is null) return;

        if (ids.All(id => id == pick.Id))
        {
            Status.Set(StatusChannel.Filter, $"All selected blocks are already '{pick.Code}' (id {pick.Id}) — nothing to do.");
            return;
        }

        var rows = new List<(string Code, long Count)>();
        foreach (var code in _filterCodes)
            rows.Add((code, ScanData.WorldBlockCodeCounts.TryGetValue(code, out var c) ? c : 0));
        rows.Sort((a, b) => b.Count.CompareTo(a.Count));

        var msg = new StringBuilder();
        msg.AppendLine(
            $"{_filterCodes.Count} code(s) → {ids.Count} block id(s) will become '{pick.Code}' (id {pick.Id}).");
        msg.AppendLine(
            $"{cells:N0} cell(s) in this world. Only palette entries change; every other byte of the chunk is kept.");
        msg.AppendLine("The original save is not touched — use File → Save a copy… to write the result.");
        msg.AppendLine();
        foreach (var (code, count) in rows.Take(8)) msg.AppendLine($"  {code} — {count:N0}");
        if (rows.Count > 8) msg.AppendLine($"  … and {rows.Count - 8} more code(s)");
        if (unresolved.Count > 0)
            msg.AppendLine($"\n{unresolved.Count} code(s) are unknown to this save's registry and will be skipped.");
        msg.Append("\nContinue?");

        _replacingBlocks = true;
        UpdateRemoveBlocksEnabled();
        try
        {
            if (!await ConfirmDialog("Replace blocks", msg.ToString())) return;

            var save = _save;
            var wanted = ids.ToArray();
            var codeCount = _filterCodes.Count;
            ShowProgress($"Replacing blocks with '{pick.Code}' in the working copy…");
            LoadProgress.IsVisible = true;
            LoadProgress.IsIndeterminate = false;
            LoadProgress.Value = 0;
            var progress = new UiProgress(pct =>
            {
                LoadProgress.Value = pct;
                Status.Set(StatusChannel.Filter, $"Replacing blocks in the working copy… {pct}%");
            });
            var res = await Task.Run(() => save.ReplaceBlocks(wanted, pick.Id, progress));

            var mapOk = true;
            if (res.ChunksChanged > 0)
            {
                _edits.RecordApplied($"find blocks · {codeCount} code(s) → {pick.Code}",
                    $"replaced {res.PaletteEntriesReplaced:N0} palette entr(ies) in {res.ChunksChanged:N0} chunk(s)");
                UpdatePendingStrip();
                Map.ClearSelection();
                mapOk = await RebuildMapAfterMutationAsync(save, Map.Terrain?.Area);
                if (!ReferenceEquals(save, _save)) return;
                await ScanAsync(save, _loadEpoch);
            }

            Status.Set(StatusChannel.App, res.ChunksChanged == 0
                ? $"No chunk in this save used the selected block ids — nothing changed (checked {res.ChunksScanned:N0} chunk(s))."
                : $"Replaced {res.PaletteEntriesReplaced:N0} palette entr(ies) with '{pick.Code}' (id {pick.Id}) "
                  + $"across {res.ChunksChanged:N0} of {res.ChunksScanned:N0} chunk(s) in the working copy. "
                  + $"Original unchanged: {_selectedSave}. Use File → Save a copy… to keep the edited world."
                  + (mapOk ? "" : " The map could not be rebuilt — reopen the save to refresh it."),
                ttlSeconds: 30, priority: 90);
        }
        catch (Exception ex)
        {
            Status.Set(StatusChannel.App, $"Replace blocks failed: {ex.Message}", priority: AppStatus.PriorityError);
        }
        finally
        {
            _replacingBlocks = false;
            UpdateRemoveBlocksEnabled();
            HideProgress();
        }
    }

    private void OnFilterClear(object? sender, RoutedEventArgs e)
    {
        _filterCodes.Clear();
        _filterCts?.Cancel();
        _filterMap = null;
        _filterMapKey = null;
        Map.SetFilterOverlay(null, null, FilterDimCheck.IsChecked == true);
        PopulateFilterList();
        SaveSettings();
    }

    private void OnFilterDimChanged(object? sender, RoutedEventArgs e)
    {
        Map.SetFilterDim(FilterDimCheck.IsChecked == true);
        SaveSettings();
    }

    private async void OnFilterBuild(object? sender, RoutedEventArgs e)
    {
        await BuildFilterOverlayAsync();
    }

    /// <summary>Restores the overlay after a restart (no await - the result is not needed).</summary>
    private void BuildFilterOverlay()
    {
        _ = BuildFilterOverlayAsync();
    }

    /// <summary>Builds the match map in the background (the chunk palette filters out irrelevant chunks) and enables the overlay.</summary>
    private async Task BuildFilterOverlayAsync()
    {
        try
        {
            if (ScanData is null || _save is null || Map.Terrain is null)
            {
                Status.Set(StatusChannel.Filter, "Open a save and wait for the scan to finish first.");
                return;
            }

            if (_filterCodes.Count == 0)
            {
                Status.Set(StatusChannel.Filter, "Select at least one block.");
                return;
            }

            var reg = _save.BlockRegistry;
            var ids = _filterCodes.SelectMany(c => reg.IdsOf(c)).Distinct().ToList();
            if (ids.Count == 0)
            {
                Status.Set(StatusChannel.Filter, "The selected codes have no ids in this save's registry.");
                return;
            }

            var save = _save;
            var grid = Map.Terrain!;
            // cache: the same id set and the same grid - just enable the ready map
            if (_filterMap is not null && _filterMapKey is not null && _filterMapKey.SetEquals(ids) &&
                _filterMap.MinX == grid.MinX && _filterMap.MinZ == grid.MinZ && _filterMap.Width == grid.Width)
            {
                Map.SetFilterOverlay(_filterMap, _filterColors, FilterDimCheck.IsChecked == true);
                UpdateFilterStatus();
                return;
            }

            _filterCts?.Cancel();
            var cts = new CancellationTokenSource();
            _filterCts = cts;
            FilterBuildBtn.IsEnabled = false;
            ShowProgress("Building block overlay…");
            LoadProgress.IsIndeterminate = true;
            var sw = Stopwatch.StartNew();
            var progress = new UiProgress(pct => Status.Set(StatusChannel.Filter, $"Scanning chunks... {pct}%"));
            try
            {
                var built = await Task.Run(() => BlockFilterScanner.Build(save, grid, ids, progress, cts.Token));
                if (cts.IsCancellationRequested) return;
                sw.Stop();
                _filterMap = built;
                _filterMapKey = new HashSet<int>(ids);
                Map.SetFilterOverlay(built, _filterColors, FilterDimCheck.IsChecked == true);
                UpdateFilterStatus(sw.ElapsedMilliseconds);
                SaveSettings();
            }
            catch (OperationCanceledException)
            {
                Status.Set(StatusChannel.Filter, "Overlay build cancelled.");
            }
            finally
            {
                FilterBuildBtn.IsEnabled = true;
                HideProgress();
            }
        }
        catch (Exception ex)
        {
            Status.Set(StatusChannel.Filter, $"Overlay error: {ex.Message}", priority: AppStatus.PriorityError);
        }
    }

    private void UpdateFilterStatus(long elapsedMs = 0)
    {
        var map = _filterMap;
        if (map is null)
        {
            Status.Set(StatusChannel.Filter, _filterCodes.Count == 0
                ? "Nothing selected - search for a code and tick it in the list below."
                : $"Selected codes: {_filterCodes.Count}. Press Show on map.");
            return;
        }

        var reg = _save?.BlockRegistry;
        var top = map.IdCells.OrderByDescending(kv => kv.Value).Take(4)
            .Select(kv => $"{reg?.CodeOf(kv.Key) ?? "id:" + kv.Key}: {kv.Value:N0}");
        Status.Set(StatusChannel.Filter,
            $"codes: {_filterCodes.Count} | columns: {map.HitColumns:N0} | blocks: {map.MatchCells:N0}"
            + (elapsedMs > 0 ? $" | {elapsedMs} ms" : "")
            + (map.IdCells.Count > 0 ? " | " + string.Join(", ", top) : " | no matches"));
    }

    // ===================== LAYOUT, MENU, SHORTCUTS =====================

    /// <summary>Applies the saved window size and the "map/panel" ratio.</summary>
    private void ApplySavedLayout()
    {
        Width = Math.Clamp(_settings.WindowWidth, 900, 3840);
        Height = Math.Clamp(_settings.WindowHeight, 600, 2400);
        ApplyPanelRowLayout();
        ShowPanelItem.Header = DataPanel.IsExpanded ? "Hide data panel" : "Show data panel";
    }

    /// <summary>The bottom panel's share of the free height (0.15…0.8).</summary>
    private void SetPanelRatio(double ratio)
    {
        ratio = Math.Clamp(ratio, 0.15, 0.8);
        LayoutRoot.RowDefinitions[2].Height = new GridLength(1 - ratio, GridUnitType.Star); // map
        LayoutRoot.RowDefinitions[4].Height = new GridLength(ratio, GridUnitType.Star); // panel
    }

    private double CurrentPanelRatio()
    {
        var map = LayoutRoot.RowDefinitions[2].ActualHeight;
        var panel = LayoutRoot.RowDefinitions[4].ActualHeight;
        return map + panel <= 1 ? 0.4 : panel / (map + panel);
    }

    private void OnPanelSplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        _settings.PanelRatio = CurrentPanelRatio();
        ScheduleSettingsSave();
    }

    private void SaveWindowSize()
    {
        if (WindowState != WindowState.Normal) return;
        _settings.WindowWidth = Width;
        _settings.WindowHeight = Height;
        ScheduleSettingsSave();
    }

    /// <summary>Selects a bottom panel tab by its header (and expands the panel when it is collapsed).</summary>
    private void SelectTab(string header)
    {
        if (!DataPanel.IsExpanded) DataPanel.IsExpanded = true;
        for (var i = 0; i < DataTabs.ItemCount; i++)
            if ((DataTabs.Items[i] as TabItem)?.Header as string == header)
            {
                DataTabs.SelectedIndex = i;
                break;
            }
    }

    private void OnViewSettings(object? sender, RoutedEventArgs e)
    {
        SelectTab("Settings");
    }

    private void OnViewFindBlocks(object? sender, RoutedEventArgs e)
    {
        SelectTab("Find blocks");
    }

    private void OnToggleDataPanel(object? sender, RoutedEventArgs e)
    {
        DataPanel.IsExpanded = !DataPanel.IsExpanded;
    }

    private void OnFocusSearch(object? sender, RoutedEventArgs e)
    {
        SelectTab("Chunks");
        ChunkSearchBox.Focus();
    }

    private void OnZoomIn(object? sender, RoutedEventArgs e)
    {
        Map.ZoomBy(1.25);
    }

    private void OnZoomOut(object? sender, RoutedEventArgs e)
    {
        Map.ZoomBy(1 / 1.25);
    }

    private void OnZoomFit(object? sender, RoutedEventArgs e)
    {
        Map.FitToArea();
    }

    /// <summary>Window shortcuts (they work when the focus is not in an input field).</summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (FocusManager?.GetFocusedElement() is TextBox) return; // do not interfere with typing (including Ctrl+Z in a field)
        var ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
        var shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        if (ctrl && e.Key == Key.O)
        {
            OnOpen(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.F)
        {
            OnFocusSearch(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.B)
        {
            OnViewFindBlocks(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (ctrl && shift && e.Key == Key.S)
        {
            OnSaveCopy(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        // Undo/redo: Ctrl+Z and Ctrl+Shift+Z (as in editors) plus Ctrl+Y
        if (ctrl && e.Key == Key.Z)
        {
            if (shift) OnRedo(this, new RoutedEventArgs());
            else OnUndo(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.Y)
        {
            OnRedo(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        // Ctrl+1…Ctrl+9 - recent saves in list order
        if (ctrl && e.Key >= Key.D1 && e.Key <= Key.D9)
        {
            OpenRecentAt(e.Key - Key.D1);
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.OemPlus:
            case Key.Add:
                Map.ZoomBy(1.25);
                e.Handled = true;
                break;
            case Key.OemMinus:
            case Key.Subtract:
                Map.ZoomBy(1 / 1.25);
                e.Handled = true;
                break;
            case Key.F:
                Map.FitToArea();
                e.Handled = true;
                break;
            case Key.Left:
                Map.PanByPixels(60, 0);
                e.Handled = true;
                break;
            case Key.Right:
                Map.PanByPixels(-60, 0);
                e.Handled = true;
                break;
            case Key.Up:
                Map.PanByPixels(0, 60);
                e.Handled = true;
                break;
            case Key.Down:
                Map.PanByPixels(0, -60);
                e.Handled = true;
                break;
            case Key.Escape:
                Map.ClearSelection();
                e.Handled = true;
                break; // drop the chunk selection
        }
    }

    private void OnFilterNextMatch(object? sender, RoutedEventArgs e)
    {
        GotoFilterMatch(true);
    }

    private void OnFilterPrevMatch(object? sender, RoutedEventArgs e)
    {
        GotoFilterMatch(false);
    }

    /// <summary>Jump to the next/previous matching block (without materializing the match list).</summary>
    private void GotoFilterMatch(bool forward)
    {
        if (_filterMap is null)
        {
            Status.Set(StatusChannel.Filter, "Build the overlay first (Show on map).");
            return;
        }

        var (cx, cz) = Map.CentreWorld();
        if (!_filterMap.TryFindNext(cx, cz, forward, out var wx, out var wz))
        {
            Status.Set(StatusChannel.Filter, "No matches found.");
            return;
        }

        var idx = _filterMap.Index(wx, wz);
        var code = _save?.BlockRegistry.CodeOf(_filterMap.TopBlock[idx]) ?? "id:" + _filterMap.TopBlock[idx];
        Map.CentreOn(wx + 0.5, wz + 0.5, 2.0);
        Status.Set(StatusChannel.Filter,
            $"{(forward ? "▶" : "◀")} {code} · X={wx} Z={wz} · Y={_filterMap.TopY[idx]} · blocks in column: {_filterMap.Count[idx]}");
    }

    // The card primitives live in CardUi: both the chunk cards (ChunkViewService) and the
    // "Find blocks" block list in this file use them.
    private void AddHeader(StackPanel sp, string text)
    {
        CardUi.AddHeader(sp, text);
    }

    private void AddLine(StackPanel sp, string text, bool isMono = false, bool bold = false, bool gray = false)
    {
        CardUi.AddLine(this, sp, text, isMono, bold, gray);
    }

    /// <summary>The folder to start the dialog in: the last used one (if it still exists).</summary>
    private async Task<IStorageFolder?> StartFolderAsync(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return null;
        try
        {
            var full = Path.GetFullPath(dir);
            if (!Directory.Exists(full)) return null;
            var uri = new UriBuilder(Uri.UriSchemeFile, string.Empty) { Path = full }.Uri;
            return await StorageProvider.TryGetFolderFromPathAsync(uri);
        }
        catch
        {
            return null;
        } // non-standard path - just open the dialog with the default location
    }

    /// <summary>Remembers the folder of the chosen file so the next dialog opens there.</summary>
    private void RememberDir(string? filePath, Action<AppSettings, string> assign)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(dir)) return;
            assign(_settings, dir);
            ScheduleSettingsSave();
        }
        catch
        {
            /* not critical: we simply will not remember the folder */
        }
    }

    private async void OnSaveCopy(object? sender, RoutedEventArgs e)
    {
        await SaveCopyAsync();
    }

    /// <summary>
    ///     Saves the WORKING COPY to the chosen file. By default the dialog opens in the folder of
    ///     the source save and suggests the name "&lt;name&gt; (edited).vcdbs", so the result sits next
    ///     to the open file rather than in /tmp. The original cannot be overwritten.
    /// </summary>
    private async Task<bool> SaveCopyAsync()
    {
        if (_save is null || _selectedSave is null) return false;
        if (_applying || _busy)
        {
            Status.Set(StatusChannel.App, "Still applying edits or loading a save — try again in a moment.");
            return false;
        }
        var save = _save;
        if (save.SourcePath is null)
        {
            Status.Set(StatusChannel.App, "This save was not opened for editing — nothing to save.",
                priority: AppStatus.PriorityError);
            return false;
        }

        if (_edits.HasPending)
        {
            var pending = _edits.ChunkCount + _edits.PlayerCount;
            var applyFirst = await ConfirmDialog("Pending edits are not applied",
                $"{pending} location(s) still have pending edits. Only applied edits are part of the working copy.\n\n" +
                "Apply them first (Review & apply…) and then save?\n" +
                "Yes — apply now, No — save the working copy without them.");
            if (applyFirst)
            {
                await ReviewAndCommitAsync();
                if (_edits.HasPending) return false; // the review was closed, nothing was applied
            }
        }

        try
        {
            var originalDir = Path.GetDirectoryName(Path.GetFullPath(_selectedSave));
            var suggested = Path.GetFileNameWithoutExtension(_selectedSave) + " (edited)" +
                            Path.GetExtension(_selectedSave);
            var dest = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save the working copy next to the original",
                SuggestedFileName = suggested,
                SuggestedStartLocation = await StartFolderAsync(originalDir),
                DefaultExtension = "vcdbs",
                FileTypeChoices = new[] { new FilePickerFileType("VS Save") { Patterns = new[] { "*.vcdbs" } } }
            });
            var path = dest?.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return false;
            if (!path.EndsWith(".vcdbs", StringComparison.OrdinalIgnoreCase)) path += ".vcdbs";
            var same = string.Equals(Path.GetFullPath(path), Path.GetFullPath(_selectedSave),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            if (same)
            {
                Status.Set(StatusChannel.App,
                    "Refusing to overwrite the original save — choose another file name next to it.",
                    priority: AppStatus.PriorityError);
                return false;
            }

            Status.Set(StatusChannel.App, "Saving the working copy…");
            var saved = await Task.Run(() => save.SaveCopyTo(path));
            _savedCopyPath = saved;
            _edits.MarkSaved(saved);
            RememberDir(path, (s, d) => s.LastSaveDir = d);
            UpdatePendingStrip();
            UpdateWindowTitle();
            Status.Set(StatusChannel.App,
                $"Saved the working copy: {saved} · original unchanged: {_selectedSave}",
                ttlSeconds: 30, priority: 90);
            return true;
        }
        catch (Exception ex)
        {
            Status.Set(StatusChannel.App, $"Failed to save the working copy: {ex.Message}",
                priority: AppStatus.PriorityError);
            return false;
        }
    }

    /// <summary>
    ///     Exit is the only moment when the working copy disappears. If it holds applied
    ///     or not yet applied edits, we ask: save next to the original or discard.
    /// </summary>
    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed) return;
        if (_save is null) return;
        var lost = _edits.UnsavedAppliedCount + (_edits.HasPending ? _edits.Journal.UndoCount : 0);
        if (lost == 0) return;

        e.Cancel = true;
        var save = await ConfirmDialog("Unsaved working copy",
            $"{lost} edit(s) are only in the temporary working copy.\n\n" +
            "Save them as a .vcdbs next to the original before closing?\n" +
            "No — the working copy will be discarded.");
        if (save)
        {
            if (!await SaveCopyAsync()) return; // the file choice was cancelled - the window stays open
        }
        else if (!await ConfirmDialog("Discard edits?",
                     "The temporary working copy and its edits will be deleted. Continue?"))
        {
            return;
        }

        _closeConfirmed = true;
        Close();
    }

    private void OnExit(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>Opens the save passed as a command line argument / file association.</summary>
    public void OpenStartupFile(string path)
    {
        if (!File.Exists(path)) return;
        if (_busy)
        {
            // the file argument/menu used to be ignored with no feedback
            var name = Path.GetFileName(_selectedSave ?? string.Empty);
            Status.Set(StatusChannel.App, name.Length > 0
                ? $"Still loading {name} — please wait a moment, then open again."
                : "Still loading the previous save — please wait a moment, then open again.");
            return;
        }

        LoadSave(path);
    }

    private async void OnOpen(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select Vintage Story save (.vcdbs)",
                AllowMultiple = false,
                SuggestedStartLocation = await StartFolderAsync(_settings.LastOpenDir),
                FileTypeFilter = new[] { new FilePickerFileType("VS Save") { Patterns = new[] { "*.vcdbs" } } }
            });
            if (files.Count == 0) return;
            var path = files[0].TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;
            LoadSave(path);
        }
        catch (Exception ex)
        {
            Status.Set(StatusChannel.App, $"Failed to open file picker: {ex.Message}",
                priority: AppStatus.PriorityError);
        }
    }

    private async void LoadSave(string path)
    {
        if (_busy || _applying)
        {
            Status.Set(StatusChannel.App, _applying
                ? "Still applying edits to the working copy — please wait a moment."
                : "Still loading the previous save — please wait a moment.");
            return;
        }

        // The working copy is one-shot: opening another save destroys both pending edits and the history.
        var lost = _edits.UnsavedAppliedCount + (_edits.HasPending ? _edits.Journal.UndoCount : 0);
        if (_save is not null && lost > 0)
        {
            var saveFirst = await ConfirmDialog("Unsaved working copy",
                $"{lost} edit(s) are only in the working copy of {Path.GetFileName(_selectedSave ?? "")}.\n\n" +
                $"Opening {Path.GetFileName(path)} will discard them. Save them as a copy first?\n" +
                "No — discard and open the other save.");
            if (saveFirst)
            {
                if (!await SaveCopyAsync()) return; // the file choice or the apply was cancelled
            }
            else if (!await ConfirmDialog("Discard edits?",
                         "The working copy and its edits will be discarded. Continue?"))
            {
                return;
            }
        }

        _busy = true;
        var epoch = ++_loadEpoch; // everything started earlier becomes stale

        VSSaveFile newSave;
        try
        {
            // Open the WORKING COPY: all edits go there and the original stays untouched.
            // It used to open the original read-only, and every write created a new copy in temp.
            newSave = VSSaveFile.OpenForEditing(path); // a broken/unavailable file throws right here
        }
        catch (Exception ex)
        {
            // The exception used to escape from async void while _busy stayed true - the window stuck until a restart
            Status.Set(StatusChannel.App, $"Failed to open save: {ex.Message}", priority: AppStatus.PriorityError);
            _busy = false;
            return;
        }

        var previous = _save;
        _save = newSave;
        _selectedSave = path;
        RememberDir(path, (s, d) => s.LastOpenDir = d); // the next dialog opens in this folder
        PushRecent(path); // MRU: move the file to the top of the recent list (P1-5)
        // The old save must not be closed while the background scan thread reads it (otherwise ObjectDisposedException)
        if (previous is not null)
        {
            if (_scan.IsRunning || _applying) _retiredSaves.Add(previous);
            else previous.Dispose();
        }

        ResetPendingStateFor(path); // edits of another file are never applied to the new save
        _savedCopyPath = null;
        UpdateWindowTitle();

        try
        {
            var regions = newSave.MapRegions;

            // --- chunk decoding progress (heavy work off the UI thread) ---
            ShowProgress("Decoding chunks and building the map…");
            LoadProgress.Value = 0;
            Map.SetTerrain(null); // clear the old map
            Map.SetAreas(null); // and its areas: until the new save is parsed, the old ones do not apply
            _areas = null;
            MapTooltipText.Text = "";
            MapTooltip.IsVisible = false; // the tooltip belongs to the previous save
            var chunkCount = newSave.Chunks.Count; // count upfront (do not touch the DB from the callback)
            var progress = new UiProgress(v =>
            {
                if (epoch != _loadEpoch) return; // a stale load does not touch the UI
                LoadProgress.Value = v;
                Status.Set(StatusChannel.App, $"Decoding chunks: {v}% ({chunkCount} chunks)");
            });

            TerrainMap tm;
            ChunkAreas areas;
            try
            {
                // a local reference: the background thread must not read the reassignable _save field.
                // Areas first (chunk positions only - cheap), then the map of the MAIN area:
                // a save with chunks at both ends of the world cannot be built as one map (P1-21).
                areas = await Task.Run(() => ChunkAreas.Build(newSave));
                tm = await Task.Run(() => TerrainMap.BuildInArea(newSave, areas.Main, progress));
            }
            catch (Exception ex)
            {
                Status.Set(StatusChannel.App, $"Failed to load world: {ex.Message}", priority: AppStatus.PriorityError);
                HideProgress();
                _busy = false;
                return;
            }

            if (epoch != _loadEpoch) return; // another save was opened in the meantime - do not publish stale data

            _areas = areas;
            Map.SetAreas(areas);
            Map.SetTerrain(tm);
            Map.SetLodMode(_lodIndex);
            ApplyRenderMode();
            UpdateExportRegionLabel();
            // Slider bounds cover the whole Y range for which the save has data (a slice is interesting in
            // caves below the surface too), not just the relief span. We take the range across ALL areas:
            // the slider must not jump when the area switches.
            int minY = areas.MinWorldY, maxY = areas.MaxWorldY;
            if (maxY <= minY)
            {
                minY = Math.Min(tm.MinSurface, tm.MinWorldY);
                maxY = tm.MaxSurface;
            }

            if (maxY <= minY)
            {
                minY = 0;
                maxY = Math.Max(1, tm.MaxSurface);
            }

            LayerSlider.Minimum = minY;
            LayerSlider.Maximum = maxY;
            _sliderReady = false;
            _surfaceMode = _settings.SurfaceMode;
            if (_surfaceMode)
            {
                LayerSlider.Value = maxY;
                Map.SetLayer(null);
                _layerYLast = null;
            }
            else
            {
                var y = Math.Clamp(_settings.LayerY ?? tm.MaxSurface, minY, maxY);
                LayerSlider.Value = y;
                _layerYLast = y;
                Map.SetLayer(y);
            }

            _sliderReady = true;
            UpdateLayerLabel();

            var version = newSave.DetectVersion();
            var seed = newSave.DetectSeed();
            var g = newSave.GamedataLength;
            Status.Set(StatusChannel.App,
                $"opened: {Path.GetFileName(path)} | regions: {regions.Count}, chunks: {chunkCount} | editing a working copy — the original is not modified");
            MetaText.Text = $"VS version: {version ?? "?"} | seed: {seed?.ToString() ?? "?"}\n" +
                            $"World: {tm.Width}×{tm.Height} blocks (X {tm.MinX}..{tm.MinX + tm.Width - 1}, Z {tm.MinZ}..{tm.MinZ + tm.Height - 1})\n" +
                            AreasMetaLine(areas, tm) +
                            $"Relief: Y {tm.MinSurface}..{tm.MaxSurface} | gamedata: {g} B\n" +
                            $"Tables: {string.Join(", ", newSave.TableNames)}";
            SaveCopyItem.IsEnabled = true;

            // full scan of the save data in the background (does not block the UI)
            RunScanAsync(newSave, epoch);
        }
        catch (Exception ex)
        {
            Status.Set(StatusChannel.App, $"Failed to load save: {ex.Message}", priority: AppStatus.PriorityError);
        }
        finally
        {
            if (epoch == _loadEpoch)
            {
                HideProgress();
                _busy = false;
            }
        }
    }

    /// <summary>The metadata line about areas: how many there are and which one is shown (empty when there is one).</summary>
    private static string AreasMetaLine(ChunkAreas areas, TerrainMap tm)
    {
        if (areas.Areas.Count <= 1) return "";
        var cur = tm.Area;
        return $"Areas: {areas.Areas.Count} chunk group(s) — showing {cur?.Label(areas.Areas.Count) ?? "?"}; " +
               "the rest are built when you scroll to them\n";
    }

    /// <summary>
    ///     Builds the map of the area the user scrolled to and publishes it WITHOUT resetting
    ///     zoom/position (keepView): otherwise switching areas would throw you out of the current view.
    ///     Requests collapse into one: while an area builds, the next one is only remembered -
    ///     fast movement over the map would otherwise queue dozens of heavy builds.
    /// </summary>
    private async Task LoadAreaAsync(ChunkArea area)
    {
        var save = _save;
        if (save is null) return;
        if (ReferenceEquals(Map.Terrain?.Area, area)) return; // already shown
        if (_areaLoading is not null)
        {
            _areaQueued = area;
            return;
        }

        _areaLoading = area;
        var epoch = _loadEpoch;
        var count = _areas?.Areas.Count ?? 1;
        try
        {
            var progress = new UiProgress(pct => Status.Set(StatusChannel.App,
                $"Building map of area {area.Index + 1} of {count}… {pct}%"));
            var tm = await Task.Run(() => TerrainMap.BuildInArea(save, area, progress));
            if (epoch != _loadEpoch || !ReferenceEquals(save, _save)) return; // the save changed while we were building
            // While we were building, the user may have moved back: do not swap in an area they
            // have already left (a request for the current area will come with the next map movement).
            if (!Map.ViewportIntersects(area)) return;
            Map.SetTerrain(tm, keepView: true);
            UpdateExportRegionLabel();
            Status.Set(StatusChannel.App, $"Showing {area.Label(count)}");
            // The overlay filter is tied to the grid of the previous area (SetTerrain resets it):
            // rebuild it for the new one, otherwise it would silently vanish on a switch.
            if (_filterMap is not null)
            {
                _filterMap = null;
                _filterMapKey = null;
                await BuildFilterOverlayAsync();
            }
        }
        catch (Exception ex)
        {
            Status.Set(StatusChannel.App, $"Failed to build area: {ex.Message}", priority: AppStatus.PriorityError);
        }
        finally
        {
            _areaLoading = null;
            if (_areaQueued is { } next)
            {
                _areaQueued = null;
                _ = LoadAreaAsync(next);
            }
        }
    }

    /// <summary>
    ///     Drops the accumulated edits when a save is opened: chunk positions and player ids
    ///     belong to a specific file and must not be applied to the working copy of another save.
    /// </summary>
    private void ResetPendingStateFor(string path)
    {
        var (chunks, players) = _edits.Reset();
        _chunkView.EndEditing();
        _players.EndEditing();
        _scan.Reset();
        _chunkView.ShowEmptyState();
        _players.Refresh();
        UpdatePendingStrip();
        _chunkView.SetChunkIndex(Array.Empty<ChunkColumnInfo>()); // the found chunk list is empty
        _entities.SetIndex(null); // the record list of the previous save must not survive its opening
        _items.SetIndex(null);    // and neither must the found item list
        _dump = new List<object>();
        // the overlay is tied to the grid of the previous save - reset it
        _filterCts?.Cancel();
        _filterMap = null;
        _filterMapKey = null;
        FilterListPanel.Children.Clear();
        Map.SetFilterOverlay(null, null, true);
        Status.Set(StatusChannel.Filter, "");
        _chunkView.UpdateDirtyStatus();
        if (chunks + players > 0)
            Status.Set(StatusChannel.Chunk,
                $"Opened {Path.GetFileName(path)} — {chunks} chunk(s) / {players} player(s) of unsaved edits were dropped (they belonged to the previous save).");
    }

    // ===================== SAVE DATA SCAN (background) =====================

    private void RunScanAsync(VSSaveFile save, int loadEpoch)
    {
        _ = ScanAsync(save, loadEpoch);
    }

    /// <summary>Full scan of the current working copy. Returns a Task: after applying edits
    /// the new model must be awaited, otherwise the cards would show the old blobs.</summary>
    private async Task ScanAsync(VSSaveFile save, int loadEpoch)
    {
        var scan = _scan.Begin(); // this scan is the latest; results of earlier ones are not published
        if (loadEpoch == _loadEpoch)
        {
            Status.Set(StatusChannel.Scan, "Scanning save data...");
            _entities.SetIndex(null); // while the scan runs the record list is empty - old rows are stale
            _items.SetIndex(null);
        }

        var sw = Stopwatch.StartNew();
        var progress = new UiProgress(pct =>
        {
            if (_scan.IsCurrent(scan) && loadEpoch == _loadEpoch)
                Status.Set(StatusChannel.Scan, $"Scanning save data... {pct}%");
        });
        try
        {
            // save is passed explicitly: opening another save will not close this object under our feet
            var data = await Task.Run(() => SaveScanner.Scan(save, progress));
            if (loadEpoch != _loadEpoch) return; // a stale load
            if (!_scan.TryPublish(scan, data)) return; // a stale scan
            sw.Stop();
            var activeRows = data.Chunks.SelectMany(c => c.Entities).Count(e => e.HasInfo);
            Status.Set(StatusChannel.Scan,
                $"Scan complete in {sw.ElapsedMilliseconds} ms — {activeRows} active of {data.EntityCount} entities, {data.BlockEntityCount} block entities, {data.Chunks.Count} chunks.");
            RefreshDumpForSelection(Map.SelectionBounds);
            ApplyDataPanelVisibility(); // show the panel after the scan, respecting the saved state
            PopulateChunkPick(); // chunk index + found list
            _entities.SetIndex(data); // the "Entities" tab: all creatures and block entities of the save
            _items.SetIndex(data);    // the "Items" tab: inventories of players, creatures and containers
            PopulateFilterList();
            _players.Refresh(); // the "Players" tab fills together with the rest of the scan data
            _chunkView.RefreshAll(); // the open chunk card re-reads its blob from the working copy
            if (_settings.OverlayEnabled && _filterCodes.Count > 0)
                BuildFilterOverlay(); // restore the overlay after a restart
        }
        catch (Exception ex)
        {
            if (_scan.IsCurrent(scan))
                Status.Set(StatusChannel.Scan, $"Scan failed: {ex.Message}", priority: AppStatus.PriorityError);
        }
        finally
        {
            _scan.Finish(scan);
            // release the displaced save only after its scan has finished
            if (_retiredSaves.Remove(save))
                try
                {
                    save.Dispose();
                }
                catch
                {
                }
        }
    }

    /// <summary>
    ///     Single entry point for showing/hiding the bottom data panel. It expands only when the bar is open
    ///     (ShowDataTabs) and scan data already exists.
    /// </summary>
    private void ApplyDataPanelVisibility()
    {
        var want = _settings.ShowDataTabs && ScanData != null;
        _settingsLoading = true;
        DataPanel.IsExpanded = want;
        _settingsLoading = false;
    }

    private void OnDataPanelToggled(object? sender, RoutedEventArgs e)
    {
        ShowPanelItem.Header = DataPanel.IsExpanded ? "Hide data panel" : "Show data panel";
        ApplyPanelRowLayout();
        if (_settingsLoading) return;
        _settings.ShowDataTabs = DataPanel.IsExpanded;
        ScheduleSettingsSave();
    }

    /// <summary>A collapsed panel gives its height to the map (Auto row + hidden splitter); an expanded one restores the ratio.</summary>
    private void ApplyPanelRowLayout()
    {
        if (DataPanel.IsExpanded)
        {
            SetPanelRatio(_settings.PanelRatio);
            PanelSplitter.IsVisible = true;
        }
        else
        {
            // the drawer collapses to its header and the map takes the freed space (its size does not change:
            // the map spans rows 2-4 and row 2 is the star-sized one)
            LayoutRoot.RowDefinitions[2].Height = new GridLength(1, GridUnitType.Star);
            LayoutRoot.RowDefinitions[4].Height = GridLength.Auto;
            PanelSplitter.IsVisible = false;
        }
    }

    /// <summary>The "Copy raw dump" export: the same text that the Dump tab used to show
    /// (META, histograms, regions, raw blobs), but without the page now - only on a button.</summary>
    private void OnCopyScanDump(object? sender, RoutedEventArgs e)
    {
        if (_dump.Count == 0)
        {
            Status.Set(StatusChannel.App, "Raw dump is not ready yet — the scan may still be running.");
            return;
        }

        var text = string.Join("\n", _dump.Select(x => x switch
        {
            EntityInfo ei =>
                $"{ei.Species} kind={ei.Kind} sexage={ei.SexAge} chunk({ei.ChunkX},{ei.ChunkY},{ei.ChunkZ})\n" +
                string.Join("\n", ei.Fields) +
                (ei.Note is { Length: > 0 } ? $"\n| note={ei.Note}" : ""),
            _ => x?.ToString() ?? ""
        }));
        CopyToClipboard(text, $"Raw dump copied to clipboard ({text.Length:N0} chars).");
    }

    private void OnCopyEntityList(object? sender, RoutedEventArgs e)
    {
        if (_entities.TotalCount == 0)
        {
            Status.Set(StatusChannel.App, "Nothing to copy — open a save and let the scan finish.");
            return;
        }

        CopyToClipboard(_entities.ListText(), $"{_entities.ShownCount} record(s) copied to clipboard.");
    }

    private void CopyToClipboard(string text, string note)
    {
        try
        {
            Clipboard?.SetTextAsync(text);
            Status.Set(StatusChannel.App, note);
        }
        catch (Exception ex)
        {
            Status.Set(StatusChannel.App, $"Clipboard failed: {ex.Message}", priority: AppStatus.PriorityError);
        }
    }

    // ===================== "ENTITIES" TAB =====================

    private void OnEntityFilterChanged(object? sender, EventArgs e)
    {
        _entities.Refresh();
    }

    private void OnEntityPicked(object? sender, SelectionChangedEventArgs e)
    {
        _entities.Select(EntityList.SelectedItem as EntityRow);
    }

    private void OnEntityClearFilter(object? sender, RoutedEventArgs e)
    {
        EntityFilterBox.Text = "";
        EntityCategoryPick.SelectedIndex = 0;
        _entities.Refresh();
    }

    // ===================== "ITEMS" TAB =====================

    private void OnItemFilterChanged(object? sender, EventArgs e) => _items.Refresh();

    private void OnItemPicked(object? sender, SelectionChangedEventArgs e)
        => _items.Select(ItemList.SelectedItem as ItemStackRow);

    private void OnItemClearFilter(object? sender, RoutedEventArgs e)
    {
        ItemFilterBox.Text = "";
        ItemSourcePick.SelectedIndex = 0;
        _items.Refresh();
    }

    private void OnCopyItemList(object? sender, RoutedEventArgs e)
    {
        if (_items.TotalCount == 0)
        {
            Status.Set(StatusChannel.App, "Nothing to copy — open a save and let the scan finish.");
            return;
        }

        CopyToClipboard(_items.ListText(), $"{_items.ShownCount} item stack(s) copied to clipboard.");
    }

    private void OnItemsInCurrentChunk(object? sender, RoutedEventArgs e)
    {
        ItemSourcePick.SelectedIndex = 0;
        ItemFilterBox.Text = $"chunk:{_lastCx},{_lastCz}";
        _items.Refresh();
    }

    /// <summary>
    ///     Applies the mode to the map. The Y slice is switched too: otherwise a mode choice during a slice
    ///     stayed only in the window field, and after the "Surface" button the map drew the previous mode
    ///     while the label next to it already showed the new one.
    /// </summary>
    private void ApplyRenderMode()
    {
        Map.SetRenderMode(_renderMode);
    }

    private void OnRenderModeSelected(int mode)
    {
        _renderMode = mode;
        if (Map is null) return;
        ApplyRenderMode();
        UpdateLayerLabel();
        SaveSettings();
    }

    private void OnHoverTooltipChanged(object? sender, RoutedEventArgs e)
    {
        // the tooltip is now an overlay above the map: toggle it without redrawing anything
        MapTooltip.IsVisible = HoverTooltipCheck.IsChecked == true && (MapTooltipText.Text ?? "").Length > 0;
        SaveSettings();
    }

    private void OnChunkGridChanged(object? sender, RoutedEventArgs e)
    {
        if (Map is null) return;
        Map.SetShowChunkGrid(ChunkGridCheck.IsChecked == true);
        SaveSettings();
    }

    private void OnExportRegionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateExportRegionLabel();
        SaveSettings();
    }

    private void OnLodSelected(int index)
    {
        _lodIndex = index;
        if (Map is null) return;
        Map.SetLodMode(index);
        SaveSettings();
    }

    private void OnLayer(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_sliderReady) return;
        var y = (int)Math.Round(e.NewValue);
        _surfaceMode = false;
        _layerYLast = y;
        Map.SetLayer(y);
        UpdateLayerLabel();
        SaveSettings();
    }

    private void OnResetLayer(object? sender, RoutedEventArgs e)
    {
        _surfaceMode = true;
        _layerYLast = null;
        Map.SetLayer(null);
        UpdateLayerLabel();
        SaveSettings();
    }

    /// <summary>The current layer label: surface or Y slice (+ "rendering…" while a rebuild runs).</summary>
    private void UpdateLayerLabel()
    {
        if (Map is null || LayerLabel is null) return;
        var suffix = Map.IsRebuilding ? "  (rendering…)" : "";
        LayerLabel.Text = _surfaceMode
            ? $"Surface — {TerrainView.ModeName(_renderMode)}.{suffix}"
            : $"Y layer = {_layerYLast ?? (int)LayerSlider.Value} — slice (real blocks at this level).{suffix}";
    }

    /// <summary>
    ///     Deletes the selected chunk columns (all Y) in the WORKING COPY together with the map data and
    ///     rebuilds the map at once: otherwise the deleted chunks would stay on screen until the save is reopened.
    ///     The original is not modified and the operation goes into History.
    /// </summary>
    private async void OnDeleteChunks(object? sender, RoutedEventArgs e)
    {
        if (_deletingChunks) return;
        if (_save is null || _selectedSave is null) return;
        var save = _save;
        var columns = Map.SelectedChunks.ToList();
        if (columns.Count == 0)
        {
            Status.Set(StatusChannel.App, "Select chunk(s) on the map first.");
            return;
        }

        _deletingChunks = true;
        DeleteChunksBtn.IsEnabled = false;
        try
        {
            var confirm = await ConfirmDialog("Delete chunks",
                $"{columns.Count} chunk column(s) will be deleted from the working copy.\n" +
                "All Y levels of these chunks and their map data are removed.\n" +
                "The original save is untouched; the game regenerates them on the next load.\n" +
                "Use File → Save a copy… to write the result next to the original. Continue?");
            if (!confirm) return;

            ShowProgress("Deleting chunks in the working copy…");
            LoadProgress.IsIndeterminate = true;
            var (chunkRows, mapRows) = await Task.Run(() => save.DeleteChunks(columns));
            if (chunkRows == 0 && mapRows == 0)
            {
                Status.Set(StatusChannel.App,
                    $"No chunk rows matched the selection ({columns.Count} column(s)) — nothing was deleted.");
                return;
            }

            _edits.RecordApplied($"selection · {columns.Count} chunk(s)",
                $"deleted {chunkRows} chunk row(s) + {mapRows} map row(s)");
            UpdatePendingStrip();

            // The map stops being correct right after the deletion: the selection points at chunks that
            // no longer exist, while the area tiles are still drawn. Clear the selection and rebuild the area.
            Map.ClearSelection();
            var mapOk = await RebuildMapAfterMutationAsync(save, Map.Terrain?.Area);
            if (!ReferenceEquals(save, _save)) return;

            await ScanAsync(save, _loadEpoch); // cards/inventories read the already deleted chunks
            Status.Set(StatusChannel.App,
                $"Deleted {chunkRows} chunk row(s) and {mapRows} map chunk row(s) from the working copy. "
                + $"Original unchanged: {_selectedSave}. Use File → Save a copy… to keep the result."
                + (mapOk ? "" : " The map could not be rebuilt — reopen the save to refresh it."),
                ttlSeconds: 30, priority: 90);
        }
        catch (Exception ex)
        {
            Status.Set(StatusChannel.App, $"Delete chunks failed: {ex.Message}", priority: AppStatus.PriorityError);
        }
        finally
        {
            _deletingChunks = false;
            DeleteChunksBtn.IsEnabled = Map.SelectionCount > 0 && _save is not null;
            HideProgress();
        }
    }

    /// <summary>
    ///     Rebuilds the areas and the map after chunks changed (deleting columns or blocks).
    ///     It shows the same area if it survived; if it was deleted entirely, the nearest
    ///     remaining one. The heavy build runs in the background.
    /// </summary>
    private async Task<bool> RebuildMapAfterMutationAsync(VSSaveFile save, ChunkArea? oldArea)
    {
        var epoch = _loadEpoch;
        try
        {
            // The overlay cache is tied to the previous blobs: after deleting chunks/blocks it cannot
            // be reused, otherwise "Show on map" would show matches that no longer exist.
            _filterMap = null;
            _filterMapKey = null;

            var areas = await Task.Run(() => ChunkAreas.Build(save));
            if (epoch != _loadEpoch || !ReferenceEquals(save, _save)) return false;
            _areas = areas;
            Map.SetAreas(areas);

            ChunkArea? target = null;
            if (oldArea is not null && areas.Areas.Count > 0)
                target = areas.Areas
                    .OrderBy(a => Math.Abs(a.MinCx - oldArea.MinCx) + Math.Abs(a.MinCz - oldArea.MinCz)
                                  + Math.Abs(a.MaxCx - oldArea.MaxCx) + Math.Abs(a.MaxCz - oldArea.MaxCz))
                    .First();
            target ??= areas.Main;

            TerrainMap? tm = null;
            if (target is not null)
            {
                LoadProgress.IsIndeterminate = false;
                LoadProgress.Value = 0;
                var progress = new UiProgress(pct =>
                {
                    if (epoch != _loadEpoch) return;
                    LoadProgress.Value = pct;
                    Status.Set(StatusChannel.App, $"Rebuilding the map of the working copy… {pct}%");
                });
                tm = await Task.Run(() => TerrainMap.BuildInArea(save, target, progress));
                if (epoch != _loadEpoch || !ReferenceEquals(save, _save)) return false;
            }

            var sameArea = oldArea is not null && target is not null
                           && oldArea.MinCx == target.MinCx && oldArea.MinCz == target.MinCz
                           && oldArea.MaxCx == target.MaxCx && oldArea.MaxCz == target.MaxCz;
            Map.SetTerrain(tm, keepView: sameArea);
            UpdateExportRegionLabel();
            return true;
        }
        catch (Exception ex)
        {
            Status.Set(StatusChannel.App, $"Changes are in the working copy, but the map could not be rebuilt: {ex.Message}",
                priority: AppStatus.PriorityError);
            return false;
        }
    }

    /// <summary>
    ///     Exports the map of the built area to PNG honouring the render mode, the Y layer and the active
    ///     block overlay filter (the map neither draws nor exports distant areas of the save). With "matches only" the background is transparent - handy
    ///     for overlaying a roads/ores map onto another base layer.
    /// </summary>
    private async void OnExportMapImage(object? sender, RoutedEventArgs e)
    {
        if (_exportingImage) return;
        if (_save is null)
        {
            Status.Set(StatusChannel.App, "Open a save first.");
            return;
        }

        if (Map.Terrain is not { } tm)
        {
            Status.Set(StatusChannel.App, "World map is not ready yet.");
            return;
        }

        // Export area: the whole world or the chunk region selected on the map (in blocks)
        MapRegion? region = null;
        if (_settings.ExportRegionMode == 1)
        {
            if (Map.SelectionBounds is { } b)
                region = new MapRegion(b.minCx * 32, b.minCz * 32, b.maxCx * 32 + 31, b.maxCz * 32 + 31);
            else
                Status.Set(StatusChannel.App, "No chunk selection — exporting the whole area.");
        }

        var format = (MapImageFormat)Math.Clamp(_settings.ExportFormat, 0, 2);
        var ext = ImageWriter.Extension(format);
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export map image",
            SuggestedFileName = Path.GetFileNameWithoutExtension(_selectedSave ?? "world") + "_map." + ext,
            DefaultExtension = ext,
            SuggestedStartLocation = await StartFolderAsync(_settings.LastExportDir ?? _settings.LastOpenDir),
            FileTypeChoices = new[]
            {
                new FilePickerFileType("PNG image") { Patterns = new[] { "*.png" } },
                new FilePickerFileType("JPEG image") { Patterns = new[] { "*.jpg", "*.jpeg" } },
                new FilePickerFileType("WebP (lossless)") { Patterns = new[] { "*.webp" } }
            }
        });
        if (file is null) return;
        var path = file.Path.LocalPath;

        _exportingImage = true;
        ShowProgress("Rendering the map image…");
        // a percentage instead of a running stripe: exporting a Y slice decodes chunks and is noticeable on a big world
        LoadProgress.IsIndeterminate = false;
        LoadProgress.Value = 0;
        try
        {
            var opts = new MapImageOptions
            {
                Scale = Math.Clamp(_settings.ExportScale, 1, 8),
                Mode = _renderMode,
                LayerY = _surfaceMode ? null : _layerYLast,
                Filter = _filterMap,
                // The overlay mode is set explicitly in the settings (it does not depend on the current "Dim the rest" checkbox),
                // so a dimmed map with the selected blocks highlighted can be saved in one click.
                FilterRender = (MapFilterRender)Math.Clamp(_settings.ExportFilterMode, 0, 3),
                TransparentBackground = _settings.ExportTransparentBg,
                Region = region,
                Format = format,
                JpegQuality = Math.Clamp(_settings.JpegQuality, 30, 100),
                MatteColor = (255, 255, 255)
            };
            var progress = new UiProgress(pct => Status.Set(StatusChannel.App, $"Rendering map image… {pct}%"));
            await Task.Run(() => MapImageRenderer.Export(path, tm, opts, progress));
            RememberDir(path, (s, d) => s.LastExportDir = d); // the next export goes to the same folder
            var actual = ImageWriter.FormatFromPath(path) ?? format;
            var (outW, outH) = MapImageRenderer.PixelSize(tm, opts);
            Status.Set(StatusChannel.App, $"Map image saved ({ImageWriter.DisplayName(actual)}): {path} " +
                                          $"({outW}×{outH} px{(region is not null ? ", selected region" : "")})" +
                                          (opts.Filter is not null
                                              ? $" — {opts.Filter.HitColumns:N0} filtered column(s)"
                                              : ""));
        }
        catch (Exception ex)
        {
            Status.Set(StatusChannel.App, $"Export failed: {ex.Message}", priority: AppStatus.PriorityError);
        }
        finally
        {
            _exportingImage = false;
            HideProgress();
        }
    }

    private void OnPlayerFilterChanged(object? sender, RoutedEventArgs e)
    {
        _players.OnFilterChanged();
    }

    private void OnPlayerPicked(object? sender, SelectionChangedEventArgs e)
    {
        _players.OnSelectionChanged();
    }

    public async Task<bool> ConfirmDialog(string title, string message)
    {
        var win = new Window
        {
            Title = title,
            Width = 480,
            MinWidth = 360,
            MaxWidth = 900,
            SizeToContent = SizeToContent.Height,
            MinHeight = 150,
            CanResize = true, // dialogs must be resizable too
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var yes = new Button { Content = "Yes", MinWidth = 90 };
        var no = new Button { Content = "No", MinWidth = 90 };
        var t = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
        var sp = new StackPanel { Margin = new Thickness(16) };
        var row = new StackPanel
            { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        row.Children.Add(no);
        row.Children.Add(yes);
        // long text scrolls instead of being clipped
        sp.Children.Add(new ScrollViewer
            { Content = t, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        sp.Children.Add(row);
        win.Content = sp;

        var tcs = new TaskCompletionSource<bool>();
        yes.Click += (_, _) =>
        {
            tcs.TrySetResult(true);
            win.Close();
        };
        no.Click += (_, _) =>
        {
            tcs.TrySetResult(false);
            win.Close();
        };
        win.Closed += (_, _) => tcs.TrySetResult(false);
        await win.ShowDialog(this);
        return await tcs.Task;
    }

    /// <summary>
    ///     A progress reporter for background tasks: it posts to the UI thread itself (independent of
    ///     SynchronizationContext - important when starting with a save from the CLI, where there is none)
    ///     and throttles updates (~10/s) so the Dispatcher is not flooded. It replaces the
    ///     Progress&lt;T&gt; + manual Dispatcher.UIThread.Post pair (double delivery of the same tick).
    /// </summary>
    private sealed class UiProgress : IProgress<double>
    {
        private readonly Action<int> _apply;
        private readonly object _gate = new();
        private int _last = -1;
        private long _lastTick;

        public UiProgress(Action<int> apply)
        {
            _apply = apply;
        }

        public void Report(double value)
        {
            var pct = (int)Math.Clamp(value * 100, 0, 100);
            lock (_gate)
            {
                if (pct == _last) return; // the value did not change
                var now = Environment.TickCount64;
                if (pct != 100 && now - _lastTick < 100) return; // throttling: at most 10 times per second
                _last = pct;
                _lastTick = now;
            }

            Dispatcher.UIThread.Post(() => _apply(pct));
        }
    }
}