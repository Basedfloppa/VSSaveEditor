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
    /// <summary>Export area in blocks; null — the whole world. Clipped to the map bounds.</summary>
    public MapRegion? Region { get; set; }
    /// <summary>Mode: <see cref="TerrainColorizer.ModeHeight"/>/<see cref="TerrainColorizer.ModeBlocks"/>/<see cref="TerrainColorizer.ModeDepth"/>.</summary>
    public int Mode { get; set; } = TerrainColorizer.ModeHeight;
    /// <summary>Y layer (null = the surface).</summary>
    public int? LayerY { get; set; }
    /// <summary>Block match map (the overlay filter), null = no filter.</summary>
    public BlockFilterMap? Filter { get; set; }
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
        var filter = o.Filter;

        var opts = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct };
        int done = 0;
        Parallel.For(0, cellsH, opts, z =>
        {
            ct.ThrowIfCancellationRequested();
            for (int x = 0; x < cellsW; x++)
            {
                int wx = rx0 + x, wz = rz0 + z;
                int i = (wz - tm.MinZ) * tm.Width + (wx - tm.MinX);   // index in the full world grid
                int v = tm.SurfaceY[i];

                bool isMatch = false; int matchId = 0, matchCount = 0;
                if (filter is not null)
                {
                    int fi = filter.Contains(wx, wz) ? filter.Index(wx, wz) : -1;
                    if (fi >= 0 && filter.Count[fi] > 0) { isMatch = true; matchId = filter.TopBlock[fi]; matchCount = filter.Count[fi]; }
                }

                byte R, G, B;
                bool draw = false;

                if (isMatch)
                {
                    var (fr, fg, fb) = FilterColor(o, tm, matchId);
                    int boost = Math.Min(70, matchCount * 14);
                    R = (byte)Math.Min(255, fr + boost); G = (byte)Math.Min(255, fg + boost); B = (byte)Math.Min(255, fb + boost);
                    draw = true;
                }
                else if (filter is not null)
                {
                    if (o.FilterRender == MapFilterRender.MatchesOnlyTransparent)
                    {
                        ClearCell(px, w, x, z, s);
                        continue;
                    }
                    if (o.FilterRender == MapFilterRender.MatchesOnlyDark)
                    {
                        R = o.Background.R; G = o.Background.G; B = o.Background.B;
                        draw = true;
                    }
                    else { R = 0; G = 0; B = 0; }   // OverDimmedMap / OverFullMap — the base map decides below
                }
                else { R = 0; G = 0; B = 0; }

                if (!draw && o.LayerY is int layerY && tm.Voxels is not null)
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
                        draw = true;
                    }
                }
                else if (!draw && v > 0)
                {
                    var (r0, g0, b0) = TerrainColorizer.ColumnColor(tm, i, v, o.LayerY, o.Mode, range);
                    double sh = o.Hillshade ? TerrainColorizer.ExtraShade(tm, i, o.Mode) : 1.0;
                    if (filter is not null && o.FilterRender == MapFilterRender.OverDimmedMap) sh *= o.DimFactor;
                    R = (byte)Math.Clamp(r0 * sh, 0, 255); G = (byte)Math.Clamp(g0 * sh, 0, 255); B = (byte)Math.Clamp(b0 * sh, 0, 255);
                    draw = true;
                }

                if (!draw)
                {
                    if (o.TransparentBackground) { ClearCell(px, w, x, z, s); continue; }
                    // no data: like the map background in the editor — the export stays opaque and matches the screen
                    R = o.EmptyColor.R; G = o.EmptyColor.G; B = o.EmptyColor.B;
                }

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
            int d = Interlocked.Increment(ref done);
            if (progress is not null && (d % 64 == 0 || d == cellsH)) progress.Report((double)d / cellsH);
        });
        return (w, h, px);
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
