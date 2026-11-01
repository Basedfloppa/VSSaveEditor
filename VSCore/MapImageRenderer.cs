using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VSCore;

/// <summary>How to draw the block overlay filter when exporting the map to an image.</summary>
public enum MapFilterRender
{
    /// <summary>Highlights matches over a DIMMED base map (like "Dim the rest" in the editor).</summary>
    OverDimmedMap = 0,
    /// <summary>Highlights matches over the full map (without dimming).</summary>
    OverFullMap = 1,
    /// <summary>Matches only, transparent background — for overlaying onto another base image.</summary>
    MatchesOnlyTransparent = 2,
    /// <summary>Matches only, dark background (as when "Dim the rest" is off in the editor).</summary>
    MatchesOnlyDark = 3
}

/// <summary>A rectangular world area in blocks (bounds inclusive) — for partial exports.</summary>
public readonly record struct MapRegion(int MinX, int MinZ, int MaxX, int MaxZ)
{
    public int Width => Math.Abs(MaxX - MinX) + 1;
    public int Height => Math.Abs(MaxZ - MinZ) + 1;
}

/// <summary>Options for exporting the world map to an image.</summary>
public sealed class MapImageOptions
{
    /// <summary>Pixels per block (1 = native quality, 2/4 = upscale).</summary>
    public int Scale { get; set; } = 1;
    /// <summary>Export area in blocks; null — the entire map or the union of all supplied tiles. Clipped to the map bounds.</summary>
    public MapRegion? Region { get; set; }
    /// <summary>Mode: <see cref="TerrainColorizer.ModeHeight"/>/<see cref="TerrainColorizer.ModeBlocks"/>/<see cref="TerrainColorizer.ModeDepth"/>.</summary>
    public int Mode { get; set; } = TerrainColorizer.ModeHeight;
    /// <summary>Y layer (null = the surface).</summary>
    public int? LayerY { get; set; }
    /// <summary>Block match map (the overlay filter), null = no filter.</summary>
    public IBlockFilter? Filter { get; set; }
    /// <summary>How to draw the filter (see <see cref="MapFilterRender"/>).</summary>
    public MapFilterRender FilterRender { get; set; } = MapFilterRender.OverDimmedMap;
    /// <summary>Colour override for ids -> RGB; null = a deterministic colour by code/id (<see cref="FilterPalette"/>).</summary>
    public IReadOnlyDictionary<int, (byte R, byte G, byte B)>? FilterColors { get; set; }
    /// <summary>Background colour when only the filter is visible (mode <see cref="MapFilterRender.MatchesOnlyDark"/>).</summary>
    public (byte R, byte G, byte B) Background { get; set; } = (16, 18, 24);
    /// <summary>Colour of areas without data: the map background in the editor. Keeps the export opaque and matching the screen.</summary>
    public (byte R, byte G, byte B) EmptyColor { get; set; } = (22, 24, 26);
    /// <summary>Transparent background instead of <see cref="EmptyColor"/> (for overlaying the image).</summary>
    public bool TransparentBackground { get; set; }
    /// <summary>Shade the relief (hillshade).</summary>
    public bool Hillshade { get; set; } = true;
    /// <summary>Dimming factor for the base map under the overlay.</summary>
    public double DimFactor { get; set; } = 0.35;
    /// <summary>Output format (also detected automatically from the file extension).</summary>
    public MapImageFormat Format { get; set; } = MapImageFormat.Png;
    /// <summary>JPEG quality (1..100).</summary>
    public int JpegQuality { get; set; } = 90;
    /// <summary>Matte colour the alpha is composited onto for formats without transparency (JPEG).</summary>
    public (byte R, byte G, byte B) MatteColor { get; set; } = (255, 255, 255);
    /// <summary>Longest output side for a multi-area export; larger unions are sampled at a coarser
    /// block step. 0 disables the limit. Only the multi-map render/export uses it: a single-area export
    /// keeps the requested scale.</summary>
    public int MaxDimension { get; set; } = 16384;
    /// <summary>Pixel budget for a multi-area export. The image is sampled at an integer block step
    /// when the union would exceed it. 0 disables the limit. See <see cref="MaxDimension"/>.</summary>
    public long MaxPixels { get; set; } = 128L * 1024 * 1024;
}

/// <summary>
/// Full render of the world map into RGBA — honouring the render mode, the Y layer and the block overlay filter.
/// The colours come from the same logic as the on-screen map (<see cref="TerrainColorizer"/> /
/// <see cref="FilterPalette"/>), so the export matches what the application shows.
/// </summary>
public static class MapImageRenderer
{
    /// <summary>Renders the map and saves it to a file. The format comes from the path extension,
    /// otherwise from <see cref="MapImageOptions.Format"/>.</summary>
    public static void Export(string path, TerrainMap tm, MapImageOptions o, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var (w, h, px) = Render(tm, o, progress, ct);
        var format = ImageWriter.FormatFromPath(path) ?? o.Format;
        File.WriteAllBytes(path, ImageWriter.Encode(w, h, px, format, o.JpegQuality, o.MatteColor));
    }

    /// <summary>Renders the map and encodes it into the selected format (see <see cref="MapImageOptions.Format"/>).</summary>
    public static byte[] Encode(TerrainMap tm, MapImageOptions o, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var (w, h, px) = Render(tm, o, progress, ct);
        return ImageWriter.Encode(w, h, px, o.Format, o.JpegQuality, o.MatteColor);
    }

    /// <summary>Size of the resulting image, taking the export area and the scale into account.</summary>
    public static (int Width, int Height) PixelSize(TerrainMap tm, MapImageOptions o)
    {
        var (x0, z0, x1, z1) = ResolveRegion(tm, o.Region);
        int s = Math.Clamp(o.Scale, 1, 16);
        return ((x1 - x0 + 1) * s, (z1 - z0 + 1) * s);
    }

    /// <summary>Renders several build tiles into one image. The canvas spans their union (clipped to
    /// <see cref="MapImageOptions.Region"/>); gaps between distant tiles stay background. A union that
    /// would exceed <see cref="MapImageOptions.MaxDimension"/> or <see cref="MapImageOptions.MaxPixels"/>
    /// is sampled at an integer block step rather than failing: a whole save of 18 tiles can otherwise
    /// need more than 4 GiB of RGBA.</summary>
    public static void Export(string path, IReadOnlyList<TerrainMap> maps, MapImageOptions o,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var (w, h, px) = Render(maps, o, progress, ct);
        var format = ImageWriter.FormatFromPath(path) ?? o.Format;
        File.WriteAllBytes(path, ImageWriter.Encode(w, h, px, format, o.JpegQuality, o.MatteColor));
    }

    /// <summary>Encodes several build tiles into one image (see the multi-map <see cref="Export"/>).</summary>
    public static byte[] Encode(IReadOnlyList<TerrainMap> maps, MapImageOptions o,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var (w, h, px) = Render(maps, o, progress, ct);
        return ImageWriter.Encode(w, h, px, o.Format, o.JpegQuality, o.MatteColor);
    }

    /// <summary>Pixel size of a multi-tile export, including the automatic block step.</summary>
    public static (int Width, int Height) PixelSize(IReadOnlyList<TerrainMap> maps, MapImageOptions o)
    {
        var (x0, z0, x1, z1) = ResolveRegion(maps, o.Region);
        var (_, w, h, _) = ResolveSampling(x0, z0, x1, z1, o);
        return (w, h);
    }

    /// <summary>World blocks represented by one output pixel in a multi-tile export (1 = no downscaling).</summary>
    public static int BlocksPerPixel(IReadOnlyList<TerrainMap> maps, MapImageOptions o)
    {
        var (x0, z0, x1, z1) = ResolveRegion(maps, o.Region);
        var (step, _, _, _) = ResolveSampling(x0, z0, x1, z1, o);
        return step;
    }

    /// <summary>Export area in blocks, clipped to the map bounds.</summary>
    private static (int X0, int Z0, int X1, int Z1) ResolveRegion(TerrainMap tm, MapRegion? region)
    {
        int x0 = tm.MinX, z0 = tm.MinZ;
        int x1 = tm.MinX + tm.Width - 1, z1 = tm.MinZ + tm.Height - 1;
        if (region is { } r)
        {
            x0 = Math.Max(x0, Math.Min(r.MinX, r.MaxX));
            x1 = Math.Min(x1, Math.Max(r.MinX, r.MaxX));
            z0 = Math.Max(z0, Math.Min(r.MinZ, r.MaxZ));
            z1 = Math.Min(z1, Math.Max(r.MinZ, r.MaxZ));
        }
        if (x1 < x0 || z1 < z0)
            throw new InvalidOperationException("export region is empty (does not intersect the world)");
        return (x0, z0, x1, z1);
    }

    /// <summary>Union of the built tiles, clipped to the selected region.</summary>
    private static (int X0, int Z0, int X1, int Z1) ResolveRegion(IReadOnlyList<TerrainMap> maps, MapRegion? region)
    {
        if (maps.Count == 0)
            throw new InvalidOperationException("no map tiles to export");
        int x0 = int.MaxValue, z0 = int.MaxValue, x1 = int.MinValue, z1 = int.MinValue;
        foreach (var tm in maps)
        {
            if (tm.Width <= 0 || tm.Height <= 0) continue;
            x0 = Math.Min(x0, tm.MinX);
            z0 = Math.Min(z0, tm.MinZ);
            x1 = Math.Max(x1, tm.MinX + tm.Width - 1);
            z1 = Math.Max(z1, tm.MinZ + tm.Height - 1);
        }
        if (x1 < x0 || z1 < z0)
            throw new InvalidOperationException("no map tiles to export");
        if (region is { } r)
        {
            x0 = Math.Max(x0, Math.Min(r.MinX, r.MaxX));
            x1 = Math.Min(x1, Math.Max(r.MinX, r.MaxX));
            z0 = Math.Max(z0, Math.Min(r.MinZ, r.MaxZ));
            z1 = Math.Min(z1, Math.Max(r.MinZ, r.MaxZ));
        }
        if (x1 < x0 || z1 < z0)
            throw new InvalidOperationException("export region is empty (does not intersect the world)");
        return (x0, z0, x1, z1);
    }

    /// <summary>Output scale and integer block step for the union; the step keeps a sprawling save
    /// inside the image budget, which is what makes a whole-save export possible at all.</summary>
    private static (int Step, int Width, int Height, int Scale) ResolveSampling(int x0, int z0, int x1, int z1, MapImageOptions o)
    {
        int scale = Math.Clamp(o.Scale, 1, 16);
        long worldW = x1 - x0 + 1L, worldH = z1 - z0 + 1L;
        int step = 1;
        int maxDim = o.MaxDimension > 0 ? o.MaxDimension : 0;
        long maxPixels = o.MaxPixels > 0 ? o.MaxPixels : 0;
        if (maxDim > 0 || maxPixels > 0)
        {
            long stepDimW = maxDim > 0 ? (worldW * scale + maxDim - 1) / maxDim : 1;
            long stepDimH = maxDim > 0 ? (worldH * scale + maxDim - 1) / maxDim : 1;
            long area = worldW * worldH;
            long stepPixels = maxPixels > 0
                ? (long)Math.Ceiling(Math.Sqrt((double)area * scale * scale / maxPixels))
                : 1;
            step = (int)Math.Max(1, Math.Max(stepDimW, Math.Max(stepDimH, stepPixels)));
        }
        long maxUsefulStep = Math.Max(worldW, worldH);
        while (true)
        {
            long w = ((worldW + step - 1) / step) * scale;
            long h = ((worldH + step - 1) / step) * scale;
            if ((maxDim == 0 || (w <= maxDim && h <= maxDim)) &&
                (maxPixels == 0 || w * h <= maxPixels))
                return (step, checked((int)w), checked((int)h), scale);
            if (step > maxUsefulStep)
                throw new InvalidOperationException(
                    $"map image budget is too small: even 1 pixel per {step} blocks exceeds the limit");
            step++;
        }
    }

    /// <summary>Renders the map's RGBA buffer (without encoding it into a file).
    /// The channel order is exactly R,G,B,A: that is what PngEncoder/JpegEncoder/WebpEncoder expect.</summary>
    public static (int Width, int Height, byte[] Rgba) Render(TerrainMap tm, MapImageOptions o, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        int s = Math.Clamp(o.Scale, 1, 16);
        // Export area: the whole world or the selected rectangle (in blocks), clipped to the map bounds.
        var (rx0, rz0, rx1, rz1) = ResolveRegion(tm, o.Region);
        int cellsW = rx1 - rx0 + 1, cellsH = rz1 - rz0 + 1;
        int w = cellsW * s, h = cellsH * s;
        long bytes = (long)w * h * 4;
        if (bytes > int.MaxValue)
            throw new InvalidOperationException($"map image too large: {w}x{h} ({bytes / (1024 * 1024)} MiB) — reduce the scale");
        var px = new byte[(int)bytes];
        double range = tm.MaxSurface - tm.MinSurface; if (range <= 0) range = 1;

        var opts = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct };
        int done = 0;
        Parallel.For(0, cellsH, opts, z =>
        {
            ct.ThrowIfCancellationRequested();
            for (int x = 0; x < cellsW; x++)
            {
                int wx = rx0 + x, wz = rz0 + z;
                int i = (wz - tm.MinZ) * tm.Width + (wx - tm.MinX);   // index in the full world grid
                if (!TryRenderCell(tm, i, wx, wz, o, range, fillUnmatchedEmpty: true, out byte R, out byte G, out byte B))
                {
                    if (o.TransparentBackground) { ClearCell(px, w, x, z, s); continue; }
                    // no data: like the map background in the editor — the export stays opaque and matches the screen
                    R = o.EmptyColor.R; G = o.EmptyColor.G; B = o.EmptyColor.B;
                }
                WriteCell(px, w, x, z, s, R, G, B);
            }
            int d = Interlocked.Increment(ref done);
            if (progress is not null && (d % 64 == 0 || d == cellsH)) progress.Report((double)d / cellsH);
        });
        return (w, h, px);
    }

    /// <summary>Renders several tiles into one RGBA canvas. The tiles' empty padding is not painted,
    /// so a gap between distant areas stays background instead of turning into a rectangle of a tile.</summary>
    public static (int Width, int Height, byte[] Rgba) Render(IReadOnlyList<TerrainMap> maps, MapImageOptions o,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var (rx0, rz0, rx1, rz1) = ResolveRegion(maps, o.Region);
        var (step, w, h, s) = ResolveSampling(rx0, rz0, rx1, rz1, o);
        long bytes = (long)w * h * 4;
        if (bytes > int.MaxValue)
            throw new InvalidOperationException($"map image too large: {w}x{h} ({bytes / (1024 * 1024)} MiB) — reduce the scale");
        var px = new byte[(int)bytes];
        FillBackground(px, o);

        int done = 0;
        foreach (var tm in maps)
        {
            ct.ThrowIfCancellationRequested();
            // EmptyMap() uses 1×1 with no data; drawing it adds nothing and its bounds are not the area's.
            if (tm.Width <= 0 || tm.Height <= 0) continue;
            DrawMapIntoCanvas(px, w, h, step, s, rx0, rz0, rx1, rz1, tm, o);
            int d = Interlocked.Increment(ref done);
            if (progress is not null) progress.Report((double)d / Math.Max(1, maps.Count));
        }
        return (w, h, px);
    }

    private static void DrawMapIntoCanvas(byte[] px, int w, int h, int step, int s,
        int rx0, int rz0, int rx1, int rz1, TerrainMap tm, MapImageOptions o)
    {
        int mx0 = Math.Max(rx0, tm.MinX), mx1 = Math.Min(rx1, tm.MinX + tm.Width - 1);
        int mz0 = Math.Max(rz0, tm.MinZ), mz1 = Math.Min(rz1, tm.MinZ + tm.Height - 1);
        if (mx1 < mx0 || mz1 < mz0) return;
        double range = tm.MaxSurface - tm.MinSurface; if (range <= 0) range = 1;

        for (int wz = mz0; wz <= mz1; wz++)
        {
            int row = (wz - tm.MinZ) * tm.Width;
            int oz = (wz - rz0) / step;
            if ((uint)oz >= (uint)h) continue;
            for (int wx = mx0; wx <= mx1; wx++)
            {
                int i = row + (wx - tm.MinX);
                if (!TryRenderCell(tm, i, wx, wz, o, range, fillUnmatchedEmpty: false, out byte R, out byte G, out byte B))
                    continue;
                int ox = (wx - rx0) / step;
                if ((uint)ox < (uint)w) WriteCell(px, w, ox, oz, s, R, G, B);
            }
        }
    }

    /// <summary>One world column into RGBA. The same code feeds the single-tile and the composite
    /// renderer, so an area cannot look different depending on whether it is exported alone or with
    /// its neighbours. <paramref name="fillUnmatchedEmpty"/> keeps the historical single-area
    /// "only highlights on dark" behaviour for columns without data.</summary>
    private static bool TryRenderCell(TerrainMap tm, int i, int wx, int wz, MapImageOptions o, double range,
        bool fillUnmatchedEmpty, out byte R, out byte G, out byte B)
    {
        R = 0; G = 0; B = 0;
        int v = tm.SurfaceY[i];
        var filter = o.Filter;

        bool isMatch = false; int matchId = 0, matchCount = 0;
        if (filter is not null && filter.TryGet(wx, wz, out var foundId, out _, out var foundCount))
        {
            isMatch = true; matchId = foundId; matchCount = foundCount;
        }

        if (isMatch)
        {
            var (fr, fg, fb) = FilterColor(o, tm, matchId);
            int boost = Math.Min(70, matchCount * 14);
            R = (byte)Math.Min(255, fr + boost); G = (byte)Math.Min(255, fg + boost); B = (byte)Math.Min(255, fb + boost);
            return true;
        }

        if (filter is not null)
        {
            if (o.FilterRender == MapFilterRender.MatchesOnlyTransparent) return false;
            if (o.FilterRender == MapFilterRender.MatchesOnlyDark)
            {
                if (v <= 0 && !fillUnmatchedEmpty) return false;
                R = o.Background.R; G = o.Background.G; B = o.Background.B;
                return true;
            }
            // OverDimmedMap / OverFullMap — the base map decides below.
        }

        if (o.LayerY is int layerY && tm.Voxels is not null)
        {
            // Horizontal slice: the colour of the real block at level Y (as on the editor screen).
            // Air at this level = background, so caves and voids are visible in the image.
            var (sr, sg, sb, has) = TerrainColorizer.SliceColor(tm, wx, layerY, wz, o.Mode, range);
            if (has)
            {
                // We do not apply relief shading: it is about the surface, and slice colours are flat.
                double dim = filter is not null && o.FilterRender == MapFilterRender.OverDimmedMap ? o.DimFactor : 1.0;
                R = (byte)Math.Clamp(sr * dim, 0, 255);
                G = (byte)Math.Clamp(sg * dim, 0, 255);
                B = (byte)Math.Clamp(sb * dim, 0, 255);
                return true;
            }
        }
        else if (v > 0)
        {
            var (r0, g0, b0) = TerrainColorizer.ColumnColor(tm, i, v, o.LayerY, o.Mode, range);
            double sh = o.Hillshade ? TerrainColorizer.ExtraShade(tm, i, o.Mode) : 1.0;
            if (filter is not null && o.FilterRender == MapFilterRender.OverDimmedMap) sh *= o.DimFactor;
            R = (byte)Math.Clamp(r0 * sh, 0, 255); G = (byte)Math.Clamp(g0 * sh, 0, 255); B = (byte)Math.Clamp(b0 * sh, 0, 255);
            return true;
        }
        return false;
    }

    private static void FillBackground(byte[] px, MapImageOptions o)
    {
        if (o.TransparentBackground) return;
        var c = o.EmptyColor;
        for (int q = 0; q + 3 < px.Length; q += 4)
        {
            px[q] = c.R; px[q + 1] = c.G; px[q + 2] = c.B; px[q + 3] = 255;
        }
    }

    private static void WriteCell(byte[] px, int w, int x, int z, int s, byte R, byte G, byte B)
    {
        for (int dy = 0; dy < s; dy++)
        {
            int row = ((z * s + dy) * w + x * s) * 4;
            for (int dx = 0; dx < s; dx++)
            {
                int q = row + dx * 4;
                px[q] = R; px[q + 1] = G; px[q + 2] = B; px[q + 3] = 255;   // RGBA (not BGRA!)
            }
        }
    }

    private static void ClearCell(byte[] px, int w, int x, int z, int s)
    {
        for (int dy = 0; dy < s; dy++)
        {
            int row = ((z * s + dy) * w + x * s) * 4;
            for (int dx = 0; dx < s; dx++)
            {
                int q = row + dx * 4;
                px[q] = 0; px[q + 1] = 0; px[q + 2] = 0; px[q + 3] = 0;
            }
        }
    }

    private static (byte R, byte G, byte B) FilterColor(MapImageOptions o, TerrainMap tm, int id)
        => o.FilterColors is not null && o.FilterColors.TryGetValue(id, out var c)
            ? c
            : FilterPalette.ColorFor(tm.Registry?.CodeOf(id), id);
}
