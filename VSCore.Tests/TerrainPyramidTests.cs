using VSCore;
using Xunit;

namespace VSCore.Tests;

/// <summary>The coarse overview is display-only: it must cover the map bounds, stay bounded in size,
/// and not be built for maps that are already small.</summary>
public class TerrainPyramidTests
{
    private static TerrainMap Flat(int w, int h, ushort block = 5)
    {
        int n = w * h;
        var tm = new TerrainMap
        {
            MinX = 1000, MinZ = 2000, Width = w, Height = h,
            SurfaceY = new int[n], BottomY = new int[n], TopBlock = new ushort[n],
            WaterDepth = new int[n], MapY = new ushort[n], MapBlock = new ushort[n],
            MinSurface = 1, MaxSurface = 5
        };
        for (int i = 0; i < n; i++)
        {
            tm.SurfaceY[i] = 1 + i % 5;
            tm.TopBlock[i] = block;
            tm.MapY[i] = (ushort)tm.SurfaceY[i];
            tm.MapBlock[i] = block;
        }
        return tm;
    }

    [Fact]
    public void SmallMap_DoesNotNeedAPyramid()
    {
        Assert.Null(TerrainPyramid.Build(Flat(128, 128), TerrainColorizer.ModeBlocks));
    }

    [Fact]
    public void LongMap_IsReducedToAtMostMaxDimension()
    {
        // 4096×1: one pixel per 2×2 block cell, and still the full X span.
        var pyramid = TerrainPyramid.Build(Flat(4096, 1), TerrainColorizer.ModeBlocks);
        Assert.NotNull(pyramid);
        Assert.Equal(2048, pyramid!.Width);
        Assert.Equal(1, pyramid.Height);
        Assert.Equal(2, pyramid.CellSize);
        Assert.Equal(pyramid.Width * pyramid.Height * 4, pyramid.Pixels.Length);
        Assert.Contains(pyramid.Pixels, b => b != 0); // not an empty bitmap
        Assert.Equal(255, pyramid.Pixels[3]);          // first cell is opaque
    }

    [Fact]
    public void Pyramid_IsOnlyUsedForTheOverviewDensity()
    {
        var pyramid = TerrainPyramid.Build(Flat(4096, 1), TerrainColorizer.ModeBlocks)!;
        Assert.True(pyramid.SuitableFor(0.25, 2048, 1));
        Assert.False(pyramid.SuitableFor(1.0, 4096, 1));
    }

    [Fact]
    public void HeightPyramid_DoesNotRequireTheBlocksRainArrays()
    {
        var tm = Flat(4096, 1);
        tm.MapY = null;
        tm.MapBlock = null;
        tm.Shadow = null;
        tm.WaterDepth = new int[tm.Width * tm.Height];
        var pyramid = TerrainPyramid.Build(tm, TerrainColorizer.ModeHeight);
        Assert.NotNull(pyramid);
        Assert.Contains(pyramid!.Pixels, b => b != 0);
    }

    [Fact]
    public void Needs_SelectTheRightArrays()
    {
        Assert.Equal(TerrainNeeds.Blocks, TerrainMap.NeedsForMode(TerrainColorizer.ModeBlocks));
        Assert.Equal(TerrainNeeds.WaterModes, TerrainMap.NeedsForMode(TerrainColorizer.ModeHeight));
        Assert.Equal(TerrainNeeds.WaterModes, TerrainMap.NeedsForMode(TerrainColorizer.ModeDepth));
        Assert.False(TerrainMap.NeedsForMode(TerrainColorizer.ModeBlocks).HasFlag(TerrainNeeds.Water));
        Assert.True(TerrainMap.NeedsForMode(TerrainColorizer.ModeDepth).HasFlag(TerrainNeeds.Water));
    }
}
