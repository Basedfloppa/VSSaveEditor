namespace VSCore;

/// <summary>Exports a 2D map of occupied world regions to PNG (managed code, no native dependencies).</summary>
public static class RegionMapPng
{
    public static void Export(string path, IReadOnlyList<MapRegionInfo> regions, int scale = 16)
    {
        var minX = regions.Min(r => X(r.Position));
        var minZ = regions.Min(r => Z(r.Position));
        var maxX = regions.Max(r => X(r.Position));
        var maxZ = regions.Max(r => Z(r.Position));
        int w = (maxX - minX + 1) * scale, h = (maxZ - minZ + 1) * scale;
        w = Math.Max(4, w); h = Math.Max(4, h);

        var bg = new byte[] { 255, 255, 255, 255 };
        var fill = new byte[] { 60, 179, 113, 255 };  // MediumSeaGreen
        var stroke = new byte[] { 0, 100, 0, 255 };   // DarkGreen

        var px = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++) Array.Copy(bg, 0, px, i * 4, 4);

        foreach (var r in regions)
        {
            int sx = (X(r.Position) - minX) * scale;
            int sz = (Z(r.Position) - minZ) * scale;
            for (int y = sz; y < sz + scale && y < h; y++)
                for (int x = sx; x < sx + scale && x < w; x++)
                {
                    bool edge = x == sx || x == sx + scale - 1 || y == sz || y == sz + scale - 1;
                    var c = edge ? stroke : fill;
                    int o = (y * w + x) * 4;
                    Array.Copy(c, 0, px, o, 4);
                }
        }

        var png = PngEncoder.Encode(w, h, px);
        File.WriteAllBytes(path, png);
    }

    // Region position is a packed 64-bit index; the map lays it out by 16-bit halves.
    private static int X(long p) => (int)(uint)(p & 0xFFFF);
    private static int Z(long p) => (int)(uint)((p >> 16) & 0xFFFF);
}
