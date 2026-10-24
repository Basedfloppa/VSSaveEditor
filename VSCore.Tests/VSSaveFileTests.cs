using VSCore.Format;
using Xunit;

namespace VSCore.Tests;

/// <summary>Tests against a real save (a copy) — they do not modify the source file.</summary>
public class VSSaveFileTests
{
    // Path to a real, valid save on this machine (non-trivial, not empty).
    private const string RealSave = "/home/user/.config/VintagestoryData/Saves/epic freestyle lands.vcdbs";

    private static bool RealSaveAvailable => File.Exists(RealSave) && new FileInfo(RealSave).Length > 1_000_000;

    [Fact(DisplayName = "read-only: opens the save and sees the tables and regions")]
    public void OpenReadOnly_ListsTablesAndRegions()
    {
        if (!RealSaveAvailable) return; // env-specific
        using var save = VSSaveFile.OpenReadOnly(RealSave);
        var tables = save.TableNames;
        Assert.Contains("mapregion", tables);
        Assert.Contains("chunk", tables);
        Assert.Contains("gamedata", tables);
        Assert.NotEmpty(save.MapRegions);
        Assert.NotEmpty(save.Chunks);
    }

    [Fact(DisplayName = "VS version detection from a chunk (field 12)")]
    public void DetectVersion_Returns122x()
    {
        if (!RealSaveAvailable) return;
        using var save = VSSaveFile.OpenReadOnly(RealSave);
        var v = save.DetectVersion();
        Assert.NotNull(v);
        Assert.Contains("1.22", v);
    }

    [Fact(DisplayName = "region parse: the structure with layer fields")]
    public void RegionBlob_Structural()
    {
        if (!RealSaveAvailable) return;
        using var save = VSSaveFile.OpenReadOnly(RealSave);
        var region = save.MapRegions.OrderByDescending(r => r.DataLength).First();
        var blob = save.ReadRegion(region.Position)!;
        var fields = PbReader.ReadMessage(blob);
        Assert.NotEmpty(fields);
        // expect many len-delim fields (layers)
        Assert.Contains(fields, f => f.WireType == 2);
    }

    [Fact(DisplayName = "artifact: a sample region-map PNG is saved to artifacts/")]
    public void ExportSampleArtifact_KeepsFile()
    {
        if (!RealSaveAvailable) return;
        List<MapRegionInfo> regions;
        using (var save = VSSaveFile.OpenReadOnly(RealSave)) regions = save.MapRegions;
        var dir = Path.Combine(AppContext.BaseDirectory, "../../../../artifacts");
        Directory.CreateDirectory(dir);
        var out_ = System.IO.Path.GetFullPath(Path.Combine(dir, "vs_map_sample.png"));
        RegionMapPng.Export(out_, regions);
        Assert.True(File.Exists(out_) && new FileInfo(out_).Length > 100);
    }

    [Fact(DisplayName = "metadata: best-effort seed from gamedata")]
    public void DetectSeed_ReturnsSeedOrNull()
    {
        if (!RealSaveAvailable) return;
        using var save = VSSaveFile.OpenReadOnly(RealSave);
        var seed = save.DetectSeed();
        Assert.True(seed is null or > 0, "the seed must be null or positive");
    }

    [Fact(DisplayName = "region map export to PNG (offscreen, works headless)")]
    public void ExportRegionMap_ProducesPng()
    {
        if (!RealSaveAvailable) return;
        long count;
        List<MapRegionInfo> regions;
        using (var save = VSSaveFile.OpenReadOnly(RealSave))
        {
            count = save.MapRegions.Count;
            regions = save.MapRegions;
        }
        var tmp = Path.Combine(Path.GetTempPath(), "vsmap_" + Guid.NewGuid().ToString("N") + ".png");
        RegionMapPng.Export(tmp, regions);
        Assert.True(File.Exists(tmp));
        Assert.True(new FileInfo(tmp).Length > 100);
        File.Delete(tmp);
        Assert.True(count > 0);
    }

    [Fact(DisplayName = "terrain map: built from a real save, has relief, exports PNG")]
    public void TerrainMap_BuildAndExport()
    {
        if (!RealSaveAvailable) return;
        TerrainMap tm;
        using (var save = VSSaveFile.OpenReadOnly(RealSave))
            tm = TerrainMap.Build(save);
        Assert.True(tm.Width > 0 && tm.Height > 0);
        Assert.True(tm.MaxSurface > 0, "there must be relief (maxSurface>0)");
        Assert.True(tm.SurfaceY.Distinct().Count() > 1, "the heights must be non-uniform");

        var dir = Path.Combine(AppContext.BaseDirectory, "../../../../artifacts");
        Directory.CreateDirectory(dir);
        var out_ = Path.GetFullPath(Path.Combine(dir, "vs_terrain_sample.png"));
        TerrainMapPng.Export(out_, tm, scale: Math.Clamp(2048 / Math.Max(1, tm.Width), 1, 4));
        Assert.True(File.Exists(out_) && new FileInfo(out_).Length > 100);
    }

    [Fact(DisplayName = "block decoder: real chunks give variation")]
    public void Decoder_RealChunks_Variety()
    {
        if (!RealSaveAvailable) return;
        int nontrivial = 0, decoded = 0;
        using (var save = VSSaveFile.OpenReadOnly(RealSave))
        {
            foreach (var ch in save.ChunkRowsWithData.Take(2000))
                try { decoded++; if (ChunkBlockDecoder.DecodeChunkBlocks(ch.Data).Distinct().Count() > 1) nontrivial++; } catch { }
        }
        Assert.True(decoded > 0);
        Assert.True(nontrivial > 0, "no chunks with block variation");
    }

    [Fact(DisplayName = "TerrainMap.Build with progress: reports up to 1.0 and builds the map")]
    public void TerrainMap_BuildWithProgress_Reaches100()
    {
        if (!RealSaveAvailable) return;
        double last = 0;
        int reports = 0;
        TerrainMap tm;
        using (var save = VSSaveFile.OpenReadOnly(RealSave))
            tm = TerrainMap.Build(save, new Progress<double>(p => { last = Math.Max(last, p); reports++; }));
        Assert.True(reports > 0, "progress must be reported");
        Assert.True(last >= 0.999, $"progress must reach ~1.0 (got {last})");
        Assert.True(tm.Width > 0 && tm.Height > 0);
        Assert.True(tm.Width * tm.Height > 1);
    }

}

public class BlockRegistryTests
{
    private const string RealSave = "/home/user/.config/VintagestoryData/Saves/dark cave valley.vcdbs";
    private static bool RealSaveAvailable => File.Exists(RealSave) && new FileInfo(RealSave).Length > 1_000_000;

    [Fact(DisplayName = "BlockRegistry: parses from gamedata, id to code, known codes")]
    public void Registry_Parses_FromGamedata()
    {
        if (!RealSaveAvailable) return;
        using var save = VSSaveFile.OpenReadOnly(RealSave);
        var reg = save.BlockRegistry;
        Assert.True(reg.Count > 1000, $"the registry is empty ({reg.Count})");
        Assert.Equal("air", reg.CodeOf(0));
        // key blocks by the actual ids from the map
        Assert.NotNull(reg.CodeOf(7435));
        Assert.Contains("soil", reg.CodeOf(7435));
        // water by code
        Assert.True(BlockColorPalette.IsWater(reg.CodeOf(FindIdStartingWith(reg, "water-"))),
            "a block with a 'water-*' code must be recognized as water");
        Assert.True(BlockColorPalette.IsWater("water-still-1"));
        Assert.False(BlockColorPalette.IsWater("soil-medium-normal"));
        Assert.False(BlockColorPalette.IsWater("wateringcan-blue-fired"));
    }

    [Fact(DisplayName = "BlockRegistry: map ids match the codes")]
    public void Registry_MapIds_HaveCodes()
    {
        if (!RealSaveAvailable) return;
        using var save = VSSaveFile.OpenReadOnly(RealSave);
        var reg = save.BlockRegistry;
        var tm = TerrainMap.Build(save);
        int known = 0, total = 0;
        for (int i = 0; i < tm.TopBlock.Length; i++)
        {
            var id = tm.TopBlock[i];
            if (id == 0) continue;
            total++;
            if (reg.HasId(id)) known++;
        }
        Assert.True(known > 0, "some ids from the map must have codes");
        Assert.True((double)known / Math.Max(1, total) > 0.5, $"too few known codes: {known}/{total}");
    }

    private static int FindIdStartingWith(BlockRegistry reg, string prefix)
    {
        foreach (var id in Enumerable.Range(0, 20000))
            if (reg.CodeOf(id)?.StartsWith(prefix) == true) return id;
        return -1;
    }
}

public class BlockColorTests
{
    [Fact(DisplayName = "ColorFor: semantic colors by block code")]
    public void Color_SemanticByCode()
    {
        var soil = BlockColorPalette.ColorFor("soil-medium-normal", 7435);
        Assert.True(soil.r > soil.b, "soil should be brown-reddish (r>b)");
        Assert.True(soil.g > soil.b, "soil is greenish-brown (g>b)");

        var grass = BlockColorPalette.ColorFor("tallgrass-short-free", 4845);
        Assert.True(grass.g > grass.r, "grass should be green (g>r)");

        var rock = BlockColorPalette.ColorFor("rock-claystone", 11164);
        Assert.True(Math.Abs(rock.r - rock.g) < 40 && Math.Abs(rock.g - rock.b) < 50, "rock is gray (the channels are close)");

        var water = BlockColorPalette.ColorFor("water-still-1", 2906);
        Assert.True(water.b > water.r, "water is blue (b>r)");

        // overrides (this used to be treated as a brownish earth-like color) and the fallback
        var gravel = BlockColorPalette.ColorFor("gravel-basalt", 10074);
        Assert.True(Math.Abs(gravel.r - gravel.g) < 50, "gravel is gray");
    }

    [Fact(DisplayName = "IsWater: by code only, without empirical ids")]
    public void IsWater_ByCodeOnly()
    {
        Assert.True(BlockColorPalette.IsWater("water"));
        Assert.True(BlockColorPalette.IsWater("water-still-1"));
        Assert.True(BlockColorPalette.IsWater("water-ne-1"));
        // salt water (oceans/puddles) is water too
        Assert.True(BlockColorPalette.IsWater("saltwater"));
        Assert.True(BlockColorPalette.IsWater("saltwater-still-7"));
        Assert.True(BlockColorPalette.IsWater("SALTWATER-flowing-2"));
        Assert.False(BlockColorPalette.IsWater("soil-medium-normal"));
        Assert.False(BlockColorPalette.IsWater("gravel-basalt"));
        Assert.False(BlockColorPalette.IsWater("wateringcan-blue-fired"));
        Assert.False(BlockColorPalette.IsWater("watershed-1"));
        Assert.False(BlockColorPalette.IsWater("sedimentary-saltrock"));
        Assert.False(BlockColorPalette.IsWater(null));
        Assert.False(BlockColorPalette.IsWater(""));
    }
}

public class ChunkDecoderTests
{
    private const string RealSave = "/home/user/.config/VintagestoryData/Saves/dark cave valley.vcdbs";
    private static bool RealSaveAvailable => File.Exists(RealSave) && new FileInfo(RealSave).Length > 1_000_000;

    static bool IsPow2(int n) => n > 0 && (n & (n - 1)) == 0;

    [Fact(DisplayName = "ChunkDecoder: bit planes = ceil(log2(palette)) for non-powers-of-two")]
    public void Decoder_NonPow2Palette_UsesCeilBits()
    {
        if (!RealSaveAvailable) return;
        using var save = VSSaveFile.OpenReadOnly(RealSave);
        int checked_ = 0, bad = 0;
        foreach (var ch in save.ChunkRowsWithData)
        {
            int ps = ChunkBlockDecoder.DecodePaletteSize(ch.Data);
            if (ps <= 1 || IsPow2(ps)) continue;
            int planes = ChunkBlockDecoder.CountPlanesInData(ch.Data);
            if (planes != ChunkBlockDecoder.BitsNeeded(ps)) bad++;
            if (++checked_ >= 3000) break;
        }
        Assert.True(checked_ > 0, "no non-power-of-two chunks to check");
        Assert.True(bad == 0, $"the data uses ceil bits but the decoder read a different plane count: {bad}/{checked_}");
    }

    [Fact(DisplayName = "ChunkDecoder: the save surface decodes into valid registry codes")]
    public void Decoder_Surface_IdsResolveToCodes()
    {
        if (!RealSaveAvailable) return;
        using var save = VSSaveFile.OpenReadOnly(RealSave);
        var tm = TerrainMap.Build(save);
        int total = 0, known = 0;
        foreach (var id in tm.TopBlock)
        {
            if (id == 0) continue;
            total++;
            if (tm.Registry?.HasId(id) == true) known++;
        }
        Assert.True(total > 0);
        Assert.True((double)known / total > 0.99, $"not every id resolves to a code: {known}/{total}");
    }
}

public class SaltwaterTests
{
    private const string RealSave = "/home/user/.config/VintagestoryData/Saves/dark cave valley.vcdbs";
    [Fact(DisplayName = "Saltwater: salt water in the save is recognized as water")]
    public void Detect()
    {
        if (!System.IO.File.Exists(RealSave)) return;
        using var save = VSSaveFile.OpenReadOnly(RealSave);
        var reg = save.BlockRegistry;
        bool found = false; long waterTotal = 0;
        foreach (var ch in save.ChunkRowsWithData)
        {
            var b = ChunkBlockDecoder.DecodeChunkBlocksMerged(ch.Data); // solids + liquids
            foreach (var id in b)
            {
                var c = reg.CodeOf(id);
                if (c != null && BlockColorPalette.IsWater(c))
                {
                    waterTotal++;
                    if (c.StartsWith("saltwater-") || c == "saltwater")
                    {
                        Assert.True(BlockColorPalette.IsWater(c), $"IsWater does not catch saltwater: {c}");
                        found = true;
                    }
                }
            }
        }
        Assert.True(found, "no salt water was found in the save");
        Assert.True(waterTotal > 1_000_000, $"there should be a huge amount of water, but only {waterTotal} was found");
    }
}
