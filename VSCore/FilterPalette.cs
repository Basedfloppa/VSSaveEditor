namespace VSCore;

/// <summary>Stable block highlight colour keyed by code/id: identical on the map, in the legend and in the PNG export.</summary>
public static class FilterPalette
{
    public static (byte R, byte G, byte B) ColorFor(string? code, int id)
    {
        int h = id;
        if (!string.IsNullOrEmpty(code)) foreach (var ch in code) h = unchecked(h * 31 + ch);
        double hue = ((h & 0x7fffffff) % 997) / 997.0;
        return FromHsv(hue, 0.85, 1.0);
    }

    public static (byte R, byte G, byte B) FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs((h * 6) % 2 - 1)), m = v - c;
        double r, g, b;
        switch ((int)(h * 6))
        {
            case 0: r = c; g = x; b = 0; break;
            case 1: r = x; g = c; b = 0; break;
            case 2: r = 0; g = c; b = x; break;
            case 3: r = 0; g = x; b = c; break;
            case 4: r = x; g = 0; b = c; break;
            default: r = c; g = 0; b = x; break;
        }
        return ((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
