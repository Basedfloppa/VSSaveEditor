using VSCore;
using Xunit;

namespace VSCore.Tests;

/// <summary>
/// The color cache must be transparent: the cached byte must equal ColumnColor + ExtraShade
/// (clamped exactly as ShadedContribution does). A cache that changes the picture would be worse
/// than the repeated computation it replaces.
/// </summary>
public class TerrainColorCacheTests
{
    private static TerrainMap Map()
    {
        const int w = 4, h = 3;
        var surface = new int[w * h];
        var bottom = new int[w * h];
        var top = new ushort[w * h];
        var water = new int[w * h];
        var mapY = new ushort[w * h];
        var mapBlock = new ushort[w * h];
        for (int i = 0; i < surface.Length; i++)
        {
            surface[i] = 100 + i;
            bottom[i] = 40 + i;
            top[i] = 0;           // no registry: the fallback color path, still deterministic
            mapY[i] = (ushort)(100 + i);
        }
        return new TerrainMap
        {
            MinX = 0, MinZ = 0, Width = w, Height = h,
            SurfaceY = surface, BottomY = bottom, TopBlock = top, WaterDepth = water,
            MapY = mapY, MapBlock = mapBlock,
            Shadow = new byte[w * h],
            MinSurface = 100, MaxSurface = 100 + w * h - 1,
            YearRel = 0.25, SeaLevel = 100
        };
    }

    [Fact]
    public void CachedSurfaceColor_EqualsDirectComputation()
    {
        var tm = Map();
        var cache = new TerrainColorCache(tm, TerrainColorizer.ModeBlocks, null);
        double range = tm.MaxSurface - tm.MinSurface;
        for (int i = 0; i < tm.SurfaceY.Length; i++)
        {
            var c = TerrainColorizer.ColumnColor(tm, i, tm.SurfaceY[i], null, TerrainColorizer.ModeBlocks, range);
            var add = TerrainColorizer.ShadedContribution(c, TerrainColorizer.ExtraShade(tm, i, TerrainColorizer.ModeBlocks));
            var packed = TerrainColorizer.ColumnColorCached(tm, i, tm.SurfaceY[i], null, TerrainColorizer.ModeBlocks, range, cache);
            Assert.True(TerrainColorCache.IsValid(packed));
            Assert.True(TerrainColorCache.HasBlock(packed));
            Assert.Equal((byte)add.R, TerrainColorCache.R(packed));
            Assert.Equal((byte)add.G, TerrainColorCache.G(packed));
            Assert.Equal((byte)add.B, TerrainColorCache.B(packed));

            // warm hit returns the identical packed value
            Assert.Equal(packed, TerrainColorizer.ColumnColorCached(tm, i, tm.SurfaceY[i], null, TerrainColorizer.ModeBlocks, range, cache));
        }
    }

    [Fact]
    public void CachedSliceColor_EqualsDirectComputation_IncludingAir()
    {
        var tm = Map();
        var cache = new TerrainColorCache(tm, TerrainColorizer.ModeHeight, 120);
        double range = tm.MaxSurface - tm.MinSurface;
        for (int z = 0; z < tm.Height; z++)
            for (int x = 0; x < tm.Width; x++)
            {
                int i = z * tm.Width + x;
                int wx = tm.MinX + x, wz = tm.MinZ + z;
                var (r, g, b, has) = TerrainColorizer.SliceColor(tm, wx, 120, wz, TerrainColorizer.ModeHeight, range);
                var packed = TerrainColorizer.SliceColorCached(tm, i, wx, 120, wz, TerrainColorizer.ModeHeight, range, cache);
                Assert.Equal(has, TerrainColorCache.HasBlock(packed));
                Assert.Equal(r, TerrainColorCache.R(packed));
                Assert.Equal(g, TerrainColorCache.G(packed));
                Assert.Equal(b, TerrainColorCache.B(packed));
            }
    }
}
