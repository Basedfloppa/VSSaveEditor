namespace VSCore;

/// <summary>
/// Per-column cache of the final map colour (RGB after the in-game shadow/hillshade, already clamped).
/// Rebuilding a region used to call ColumnColor + ExtraShade for every visible column: string dictionary
/// lookups, ToLowerInvariant/Variants on mod codes and a climate/season LUT sample per pixel. The colour of
/// a column does not depend on the viewport, so it is computed once per (map, mode, Y layer) and reused
/// for panning, zooming and LOD passes.
///
/// Encoding: 0 = not computed yet; bit 31 = computed; bit 30 = has a block. A valid black colour is
/// therefore non-zero and distinguishable from "no data".
/// </summary>
public sealed class TerrainColorCache
{
    public const uint Valid = 1u << 31;
    public const uint Has = 1u << 30;

    public TerrainMap Source { get; }
    public int Mode { get; }
    public int? Layer { get; }
    public int Length => _px.Length;

    private readonly uint[] _px;

    public TerrainColorCache(TerrainMap source, int mode, int? layer)
    {
        Source = source;
        Mode = mode;
        Layer = layer;
        _px = new uint[source.SurfaceY.Length];
    }

    /// <summary>The raw packed value, 0 when the column has not been computed yet.</summary>
    public uint Get(int i) => (uint)i < (uint)_px.Length ? _px[i] : 0;

    public void Set(int i, uint packed) => _px[i] = packed;

    public static uint Pack(bool has, byte r, byte g, byte b)
        => Valid | (has ? Has : 0) | ((uint)r << 16) | ((uint)g << 8) | b;

    public static bool IsValid(uint packed) => (packed & Valid) != 0;
    public static bool HasBlock(uint packed) => (packed & Has) != 0;
    public static byte R(uint packed) => (byte)(packed >> 16);
    public static byte G(uint packed) => (byte)(packed >> 8);
    public static byte B(uint packed) => (byte)packed;
}
