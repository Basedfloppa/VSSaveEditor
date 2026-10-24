using Xunit;

namespace VSCore.Tests;

/// <summary>World map export to PNG: modes, scale, overlay filter and transparent background (task 11).</summary>
public class MapImageRendererTests
{
    private static TerrainMap MakeTerrain(int w, int h)
    {
        int n = w * h;
        var tm = new TerrainMap
        {
            MinX = 100, MinZ = 200, Width = w, Height = h,
            SurfaceY = new int[n], BottomY = new int[n], TopBlock = new ushort[n], WaterDepth = new int[n],
            MinSurface = 1, MaxSurface = 6
        };
        for (int i = 0; i < n; i++)
        {
            tm.SurfaceY[i] = 1 + (i % 5);
            tm.TopBlock[i] = (ushort)(1 + (i % 3));
            tm.WaterDepth[i] = i % 4;
        }
        return tm;
    }

    private static BlockFilterMap MakeFilter(int w, int h)
    {
        int n = w * h;
        var fm = new BlockFilterMap
        {
            MinX = 100, MinZ = 200, Width = w, Height = h,
            TopBlock = new ushort[n], TopY = new short[n], Count = new byte[n]
        };
        fm.Count[0] = 3; fm.TopBlock[0] = 1; fm.TopY[0] = 5;
        fm.Count[5] = 1; fm.TopBlock[5] = 2; fm.TopY[5] = 3;
        fm.HitColumns = 2; fm.MatchCells = 4;
        return fm;
    }

    private static (int w, int h) PngSize(byte[] png)
    {
        int be(int o) => (png[o] << 24) | (png[o + 1] << 16) | (png[o + 2] << 8) | png[o + 3];
        return (be(16), be(20));
    }

    [Fact(DisplayName = "map render: the PNG has the correct size and the scale is honored")]
    public void Encode_ProducesPng_WithRequestedScale()
    {
        var tm = MakeTerrain(16, 12);
        var png = MapImageRenderer.Encode(tm, new MapImageOptions { Scale = 3, Mode = TerrainColorizer.ModeHeight });
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png.Take(4).ToArray());
        Assert.Equal((48, 36), PngSize(png));
    }

    [Fact(DisplayName = "map render: the overlay filter and the transparent background do not throw, and the map is not empty")]
    public void Encode_WithFilter_OnlyMatches()
    {
        var tm = MakeTerrain(20, 20);
        var fm = MakeFilter(20, 20);
        var png = MapImageRenderer.Encode(tm, new MapImageOptions
        {
            Scale = 1,
            Filter = fm,
            FilterRender = MapFilterRender.MatchesOnlyTransparent
        });
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png.Take(4).ToArray());
        Assert.Equal((20, 20), PngSize(png));
    }

    [Fact(DisplayName = "map render: the blocks/depth modes and the Y layer do not throw")]
    public void Encode_AllModes()
    {
        var tm = MakeTerrain(10, 10);
        foreach (var mode in new[] { TerrainColorizer.ModeHeight, TerrainColorizer.ModeBlocks, TerrainColorizer.ModeDepth })
        {
            var png = MapImageRenderer.Encode(tm, new MapImageOptions { Mode = mode });
            Assert.True(png.Length > 50);
        }
        var layered = MapImageRenderer.Encode(tm, new MapImageOptions { Mode = TerrainColorizer.ModeHeight, LayerY = 3 });
        Assert.True(layered.Length > 50);
    }

    [Fact(DisplayName = "area export: the crop matches the corresponding part of the full map and the size equals the area size")]
    public void Render_Region_CropsCorrectly()
    {
        var tm = MakeTerrain(24, 18);
        var full = MapImageRenderer.Render(tm, new MapImageOptions());
        int ox = 4, oz = 3, rw = 8, rh = 7;
        var region = new MapRegion(tm.MinX + ox, tm.MinZ + oz, tm.MinX + ox + rw - 1, tm.MinZ + oz + rh - 1);
        var crop = MapImageRenderer.Render(tm, new MapImageOptions { Region = region });

        Assert.Equal((rw, rh), (crop.Width, crop.Height));
        for (int z = 0; z < rh; z++)
            for (int x = 0; x < rw; x++)
                for (int c = 0; c < 4; c++)
                {
                    int fi = ((oz + z) * 24 + (ox + x)) * 4 + c;
                    int ci = (z * rw + x) * 4 + c;
                    Assert.Equal(full.Rgba[fi], crop.Rgba[ci]);
                }
    }

    [Fact(DisplayName = "area export: an area outside the world and the scale are both handled")]
    public void Render_Region_ClampsAndScales()
    {
        var tm = MakeTerrain(10, 10);
        // the area is larger than the world — it is clipped to the bounds
        var big = MapImageRenderer.Render(tm, new MapImageOptions { Region = new MapRegion(tm.MinX - 50, tm.MinZ - 50, tm.MinX + 500, tm.MinZ + 500) });
        Assert.Equal((tm.Width, tm.Height), (big.Width, big.Height));
        // the scale is applied to the area
        var scaled = MapImageRenderer.Render(tm, new MapImageOptions { Scale = 2, Region = new MapRegion(tm.MinX, tm.MinZ, tm.MinX + 4, tm.MinZ + 2) });
        Assert.Equal((10, 6), (scaled.Width, scaled.Height));
        // an area outside the world fails with a clear error
        var ex = Assert.Throws<InvalidOperationException>(() =>
            MapImageRenderer.Render(tm, new MapImageOptions { Region = new MapRegion(tm.MinX + 1000, tm.MinZ + 1000, tm.MinX + 1010, tm.MinZ + 1010) }));
        Assert.Contains("empty", ex.Message);
    }

    [Fact(DisplayName = "map render: an oversized image fails with a clear error instead of an OOM")]
    public void Encode_TooLarge_ThrowsClear()
    {
        var tm = MakeTerrain(2000, 2000);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            MapImageRenderer.Encode(tm, new MapImageOptions { Scale = 16 }));
        Assert.Contains("too large", ex.Message);
    }

    [Fact(DisplayName = "channel order: Render writes RGBA (R and B are not swapped as in BGRA)")]
    public void Render_WritesRgbaNotBgra()
    {
        // flat terrain: the hillshade is identical for every column, so the color is predictable
        int w = 4, h = 4, n = w * h;
        var tm = new TerrainMap
        {
            MinX = 0, MinZ = 0, Width = w, Height = h,
            SurfaceY = new int[n], BottomY = new int[n], TopBlock = new ushort[n], WaterDepth = new int[n],
            MinSurface = 1, MaxSurface = 1
        };
        for (int i = 0; i < n; i++) { tm.SurfaceY[i] = 1; tm.TopBlock[i] = 1; }

        var (rw, rh, px) = MapImageRenderer.Render(tm, new MapImageOptions { Mode = TerrainColorizer.ModeDepth });
        Assert.Equal((w, h), (rw, rh));

        var (cr, cg, cb) = TerrainColorizer.ColumnColor(tm, 0, 1, null, TerrainColorizer.ModeDepth, 1);
        double sh = TerrainColorizer.Hillshade(tm, 0, w);
        byte er = (byte)Math.Clamp(cr * sh, 0, 255), eg = (byte)Math.Clamp(cg * sh, 0, 255), eb = (byte)Math.Clamp(cb * sh, 0, 255);
        Assert.True(er != eb, "the test needs distinguishable R and B");
        Assert.Equal(er, px[0]);
        Assert.Equal(eg, px[1]);
        Assert.Equal(eb, px[2]);
        Assert.Equal(255, px[3]);
    }

    [Fact(DisplayName = "filter modes: a dimmed map, a transparent background and a dark backing")]
    public void FilterRender_Modes()
    {
        var tm = MakeTerrain(16, 16);
        var fm = MakeFilter(16, 16);
        var dimmed = MapImageRenderer.Render(tm, new MapImageOptions { Filter = fm, FilterRender = MapFilterRender.OverDimmedMap });
        var transparent = MapImageRenderer.Render(tm, new MapImageOptions { Filter = fm, FilterRender = MapFilterRender.MatchesOnlyTransparent, TransparentBackground = true });
        var full = MapImageRenderer.Render(tm, new MapImageOptions { Filter = fm, FilterRender = MapFilterRender.OverFullMap });

        // the dimmed base is darker than the full one (we compare an unmatched column that has data)
        int i = 1;   // a column without matches, but with data
        var dimPx = (R: dimmed.Rgba[i * 4], G: dimmed.Rgba[i * 4 + 1], B: dimmed.Rgba[i * 4 + 2]);
        var fullPx = (R: full.Rgba[i * 4], G: full.Rgba[i * 4 + 1], B: full.Rgba[i * 4 + 2]);
        Assert.True(dimPx.R + dimPx.G + dimPx.B < fullPx.R + fullPx.G + fullPx.B, "the dimmed map must be darker than the full one");

        // transparent mode: unmatched columns are transparent
        Assert.Equal(0, transparent.Rgba[i * 4 + 3]);
    }
}
