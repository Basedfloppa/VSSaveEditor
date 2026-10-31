namespace VSCore;

/// <summary>
/// A pre-coloured coarse overview of a <see cref="TerrainMap"/>. It exists so a zoomed-out map does not
/// have to average every block of every area on every pan/zoom: one pyramid pixel is a whole
/// <see cref="CellSize"/>×<see cref="CellSize"/> block cell. The full-resolution arrays remain the source
/// for hover, selection, filtering, Y slices and the detailed view; the pyramid is display-only.
///
/// Small maps do not get a pyramid (the exact map is already cheap), and it is rebuilt only when the map
/// is built/rebuilt or the render mode changes.
/// </summary>
public sealed class TerrainPyramid
{
    public const int MaxDimension = 2048;

    public int MinX { get; }
    public int MinZ { get; }
    public int Width { get; }
    public int Height { get; }
    public int CellSize { get; }
    public int Mode { get; }

    /// <summary>BGRA rows, <see cref="Width"/>×<see cref="Height"/>.</summary>
    public byte[] Pixels { get; }

    private TerrainPyramid(int minX, int minZ, int width, int height, int cellSize, int mode, byte[] pixels)
    {
        MinX = minX; MinZ = minZ; Width = width; Height = height; CellSize = cellSize; Mode = mode; Pixels = pixels;
    }

    /// <summary>Blocks per pyramid pixel on the X/Z axes (both axes use the same cell to keep the aspect).</summary>
    public int CellBlocks => CellSize;

    /// <summary>True when the pyramid can stand in for the exact map at this output density.</summary>
    public bool SuitableFor(double pixelsPerBlock, int outputWidth, int outputHeight)
    {
        // At high zoom the exact map is both sharper and cheap; the pyramid is for the overview.
        if (pixelsPerBlock <= 0) return true;
        return outputWidth <= Width * 1.25 && outputHeight <= Height * 1.25;
    }

    /// <summary>Builds the overview for one render mode. Returns null when the map is already small enough
    /// (the exact LOD pass is not the bottleneck and a second full bitmap would only add memory).</summary>
    public static TerrainPyramid? Build(TerrainMap tm, int mode, int maxDimension = MaxDimension)
    {
        if (tm.Width <= 0 || tm.Height <= 0) return null;
        int maxDim = Math.Clamp(maxDimension, 256, 4096);
        int cell = Math.Max(1, (int)Math.Ceiling(Math.Max(tm.Width, tm.Height) / (double)maxDim));
        if (cell < 2) return null;

        int w = Math.Max(1, (tm.Width + cell - 1) / cell);
        int h = Math.Max(1, (tm.Height + cell - 1) / cell);
        var px = new byte[w * h * 4];
        double range = tm.MaxSurface - tm.MinSurface;
        if (range <= 0) range = 1;

        // One representative column per cell: the centre when it has data, otherwise the first valid
        // cell corner. Sampling (rather than averaging the whole cell) is what makes the overview cheap;
        // the exact map remains one zoom step away for detail.
        Parallel.For(0, h, z =>
        {
            int wz = tm.MinZ + z * cell + cell / 2;
            if (wz >= tm.MinZ + tm.Height) wz = tm.MinZ + tm.Height - 1;
            for (int x = 0; x < w; x++)
            {
                int wx = tm.MinX + x * cell + cell / 2;
                if (wx >= tm.MinX + tm.Width) wx = tm.MinX + tm.Width - 1;
                int i = (wz - tm.MinZ) * tm.Width + (wx - tm.MinX);
                if (tm.SurfaceY[i] <= 0)
                {
                    // A cell whose centre is a hole: try the four corners of the coarse cell.
                    int x0 = tm.MinX + x * cell, z0 = tm.MinZ + z * cell;
                    int x1 = Math.Min(tm.MinX + tm.Width - 1, x0 + cell - 1);
                    int z1 = Math.Min(tm.MinZ + tm.Height - 1, z0 + cell - 1);
                    i = FindData(tm, x0, z0, x1, z1);
                }
                if (i < 0) continue;

                var c = TerrainColorizer.ColumnColor(tm, i, tm.SurfaceY[i], null, mode, range);
                double sh = TerrainColorizer.ExtraShade(tm, i, mode);
                var add = TerrainColorizer.ShadedContribution(c, sh);
                int q = (z * w + x) * 4;
                px[q] = (byte)add.B;
                px[q + 1] = (byte)add.G;
                px[q + 2] = (byte)add.R;
                px[q + 3] = 255;
            }
        });
        return new TerrainPyramid(tm.MinX, tm.MinZ, w, h, cell, mode, px);
    }

    private static int FindData(TerrainMap tm, int x0, int z0, int x1, int z1)
    {
        // Corners, then the centre of each edge: at most 8 probes per empty coarse cell.
        ReadOnlySpan<int> xs = stackalloc int[] { x0, x1, (x0 + x1) / 2 };
        ReadOnlySpan<int> zs = stackalloc int[] { z0, z1, (z0 + z1) / 2 };
        foreach (int z in zs)
        foreach (int x in xs)
        {
            int i = (z - tm.MinZ) * tm.Width + (x - tm.MinX);
            if (tm.SurfaceY[i] > 0) return i;
        }
        return -1;
    }
}
