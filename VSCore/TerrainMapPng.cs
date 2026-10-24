namespace VSCore;

/// <summary>Exports the terrain height map to PNG (managed code).</summary>
public static class TerrainMapPng
{
    public static void Export(string path, TerrainMap tm, int scale = 1)
    {
        int w = Math.Max(1, tm.Width * scale), h = Math.Max(1, tm.Height * scale);
        var px = new byte[w * h * 4];
        double range = tm.MaxSurface - tm.MinSurface;
        if (range <= 0) range = 1;
        for (int i = 0; i < tm.Width * tm.Height; i++)
        {
            int v = tm.SurfaceY[i];
            byte r, g, b;
            if (v <= 0) { r = 40; g = 100; b = 130; } // water/no data -> blue
            else
            {
                double t = (v - tm.MinSurface) / range;
                // gradient: low = green (valley), high = brown/white (mountains)
                r = (byte)Math.Clamp(80 + t * 160, 0, 255);
                g = (byte)Math.Clamp(170 - t * 80, 0, 255);
                b = (byte)Math.Clamp(40 + t * 120, 0, 255);
            }
            int ox = i % tm.Width, oy = i / tm.Width;
            for (int dy = 0; dy < scale; dy++)
                for (int dx = 0; dx < scale; dx++)
                {
                    int o = ((oy * scale + dy) * w + (ox * scale + dx)) * 4;
                    px[o] = r; px[o + 1] = g; px[o + 2] = b; px[o + 3] = 255;
                }
        }
        File.WriteAllBytes(path, PngEncoder.Encode(w, h, px));
    }
}
