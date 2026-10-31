using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using VSCore;
using Xunit;

namespace VSCore.Tests;

/// <summary>
/// The fused decoder must produce exactly the same column summaries and histogram as the full
/// DecodeChunkBlocksMergedOnePass path. A divergence here is silent: the map would show a wrong
/// surface/bottom/water line while every other test stays green.
/// </summary>
public class ChunkBlockSummaryTests
{
    private static void WriteVarint(Stream s, long v)
    {
        ulong u = (ulong)v;
        while (u >= 0x80) { s.WriteByte((byte)((u & 0x7F) | 0x80)); u >>= 7; }
        s.WriteByte((byte)u);
    }

    private static void WriteI32(Stream s, int v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, v);
        s.Write(b);
    }

    /// <summary>Block field: [int32 compressed-palette length][zstd palette][zstd bit planes].</summary>
    private static byte[] BuildField(int[] palette, ushort[] cells)
    {
        int bits = ChunkBlockDecoder.BitsNeeded(palette.Length);
        var planes = new byte[bits * 4096];
        var index = new Dictionary<int, int>();
        for (int i = 0; i < palette.Length; i++) index[palette[i]] = i;
        for (int idx = 0; idx < ChunkBlockDecoder.CellsPerChunk; idx++)
        {
            int val = index[cells[idx]];
            int word = idx >> 5, bit = idx & 31;
            for (int p = 0; p < bits; p++)
                if (((val >> p) & 1) != 0)
                    planes[p * 4096 + word * 4 + (bit >> 3)] |= (byte)(1 << (bit & 7));
        }
        var palBytes = new byte[palette.Length * 4];
        for (int i = 0; i < palette.Length; i++) BinaryPrimitives.WriteInt32LittleEndian(palBytes.AsSpan(i * 4), palette[i]);
        var packed = ChunkBlockDecoder.ZstdCompress(palBytes);
        var ms = new MemoryStream();
        WriteI32(ms, packed.Length);
        ms.Write(packed);
        ms.Write(ChunkBlockDecoder.ZstdCompress(planes));
        return ms.ToArray();
    }

    private static byte[] Chunk(byte[]? solids, byte[]? liquids)
    {
        var ms = new MemoryStream();
        void Field(int field, byte[] payload)
        {
            WriteVarint(ms, ((long)field << 3) | 2);
            WriteVarint(ms, payload.Length);
            ms.Write(payload);
        }
        if (solids is not null) Field(1, solids);
        if (liquids is not null) Field(16, liquids);
        Field(12, System.Text.Encoding.ASCII.GetBytes("1.22.7"));
        return ms.ToArray();
    }

    private static (byte[] blob, ushort[] merged) Build(int[] solidPalette, ushort[] solidCells,
        int[]? liquidPalette, ushort[]? liquidCells)
    {
        var solids = BuildField(solidPalette, solidCells);
        var liquids = liquidPalette is null ? null : BuildField(liquidPalette, liquidCells!);
        var merged = (ushort[])solidCells.Clone();
        if (liquidCells is not null)
            for (int i = 0; i < merged.Length; i++) if (merged[i] == 0) merged[i] = liquidCells[i];
        return (Chunk(solids, liquids), merged);
    }

    private static ChunkColumnSummary ExpectedColumn(ushort[] cells, int col, bool[] water, bool[] rain)
    {
        int top = 0, bottom = 0, wd = 0, rainY = 0;
        ushort topId = 0, rainId = 0;
        bool run = false;
        for (int y = 31; y >= 0; y--)
        {
            ushort id = cells[col + y * 1024];
            if (id == 0) { run = false; continue; }
            if (top == 0) { top = y + 1; topId = id; if (id < water.Length && water[id]) { wd = 1; run = true; } }
            else if (run) { if (id < water.Length && water[id]) wd++; else run = false; }
            bottom = y + 1;
            if (rainY == 0 && id < rain.Length && !rain[id]) { rainY = y + 1; rainId = id; }
        }
        return new ChunkColumnSummary
        {
            TopY = (byte)top, BottomY = (byte)bottom, TopId = topId,
            WaterDepth = (byte)wd, RainY = (byte)rainY, RainId = rainId
        };
    }

    private static void AssertSummary(byte[] blob, ushort[] merged, bool[] water, bool[] rain)
    {
        var got = new ChunkColumnSummary[1024];
        ChunkBlockDecoder.DecodeChunkSummary(blob, got, water, rain);
        for (int col = 0; col < 1024; col++)
        {
            var want = ExpectedColumn(merged, col, water, rain);
            Assert.Equal(want.TopY, got[col].TopY);
            Assert.Equal(want.BottomY, got[col].BottomY);
            Assert.Equal(want.TopId, got[col].TopId);
            Assert.Equal(want.WaterDepth, got[col].WaterDepth);
            Assert.Equal(want.RainY, got[col].RainY);
            Assert.Equal(want.RainId, got[col].RainId);
        }
    }

    private static bool[] WaterTable(params int[] ids)
    {
        var t = new bool[ushort.MaxValue + 1];
        foreach (var id in ids) t[id] = true;
        return t;
    }

    [Fact]
    public void Summary_MatchesFullDecode_RandomChunks()
    {
        var rnd = new Random(20261031);
        var water = WaterTable(2906);
        var rain = new bool[20_000];
        for (int i = 0; i < rain.Length; i++) rain[i] = i % 7 == 0; // a mix of permeable/impermeable ids

        int[] solidPalette = { 0, 5, 9, 11, 2906 };
        int[] liquidPalette = { 0, 2906 };
        for (int iter = 0; iter < 30; iter++)
        {
            var solidCells = new ushort[ChunkBlockDecoder.CellsPerChunk];
            var liquidCells = new ushort[ChunkBlockDecoder.CellsPerChunk];
            for (int i = 0; i < solidCells.Length; i++)
            {
                solidCells[i] = (ushort)solidPalette[rnd.Next(solidPalette.Length)];
                liquidCells[i] = (ushort)liquidPalette[rnd.Next(liquidPalette.Length)];
                // make surface columns realistic: a solid top with an air gap in some columns
                if ((i & 1023) == 0) solidCells[i] = (ushort)solidPalette[rnd.Next(solidPalette.Length)];
            }
            var (blob, merged) = Build(solidPalette, solidCells, liquidPalette, liquidCells);
            AssertSummary(blob, merged, water, rain);
        }
    }

    [Fact]
    public void Summary_WaterRunStopsAtAir()
    {
        var water = WaterTable(2906);
        var rain = new bool[10_000];
        // one column: water at y=10, air at y=9, water at y=8 -> depth must be 1, not 2
        var cells = new ushort[ChunkBlockDecoder.CellsPerChunk];
        int col = 5 + 7 * 32;
        cells[col + 10 * 1024] = 2906;
        cells[col + 8 * 1024] = 2906;
        var (blob, merged) = Build(new[] { 0, 2906 }, cells, null, null);
        AssertSummary(blob, merged, water, rain);
        var got = new ChunkColumnSummary[1024];
        ChunkBlockDecoder.DecodeChunkSummary(blob, got, water, rain);
        Assert.Equal(1, got[col].WaterDepth);
        Assert.Equal(11, got[col].TopY);
        Assert.Equal(9, got[col].BottomY); // bottommost non-air is y=8 -> y+1
    }

    [Fact]
    public void Summary_CanSkipWaterAndRain_ForBlocksMode()
    {
        // The blocks map reads neither the water column nor the rain surface. Skipping them must not
        // change the top/bottom or the top block id (the decoder's expensive per-column runs are optional).
        var water = WaterTable(2906);
        var rain = new bool[10_000];
        var cells = new ushort[ChunkBlockDecoder.CellsPerChunk];
        int col = 5 + 7 * 32;
        cells[col + 10 * 1024] = 2906; // water on the surface
        cells[col + 8 * 1024] = 2906;  // and one cell below, with air in between
        var (blob, _) = Build(new[] { 0, 2906 }, cells, null, null);

        var full = new ChunkColumnSummary[1024];
        ChunkBlockDecoder.DecodeChunkSummary(blob, full, water, rain);

        var lean = new ChunkColumnSummary[1024];
        ChunkBlockDecoder.DecodeChunkSummary(blob, lean, water, rain,
            needWater: false, needBottom: true, needRain: false);

        Assert.Equal(full[col].TopY, lean[col].TopY);
        Assert.Equal(full[col].TopId, lean[col].TopId);
        Assert.Equal(full[col].BottomY, lean[col].BottomY);
        Assert.Equal(1, full[col].WaterDepth);
        Assert.Equal(0, lean[col].WaterDepth);
        Assert.True(full[col].RainY > 0);
        Assert.Equal(0, lean[col].RainY);
    }

    [Fact]
    public void Summary_LiquidFieldIsMerged()
    {
        var water = WaterTable(2906);
        var rain = new bool[10_000];
        var solids = new ushort[ChunkBlockDecoder.CellsPerChunk];
        var liquids = new ushort[ChunkBlockDecoder.CellsPerChunk];
        for (int i = 0; i < liquids.Length; i++) liquids[i] = 2906;
        var (blob, merged) = Build(new[] { 0 }, solids, new[] { 0, 2906 }, liquids);
        AssertSummary(blob, merged, water, rain);
    }

    [Fact]
    public void Summary_NonSimplePalette_UsesExactFallback()
    {
        // index 0 maps to a real block (5), index 1 maps to air: "any bit set" is not "non-air"
        var water = WaterTable(2906);
        var rain = new bool[10_000];
        var cells = new ushort[ChunkBlockDecoder.CellsPerChunk];
        for (int i = 0; i < cells.Length; i++) cells[i] = (ushort)(i % 3 == 0 ? 5 : i % 3 == 1 ? 0 : 9);
        var (blob, merged) = Build(new[] { 5, 0, 9 }, cells, null, null);
        AssertSummary(blob, merged, water, rain);
    }

    [Fact]
    public void Histogram_MatchesFullDecode()
    {
        var rnd = new Random(7);
        var hist = new long[ushort.MaxValue + 1];
        var touched = new List<int>();
        int[] solidPalette = { 0, 5, 9, 11 };
        for (int iter = 0; iter < 10; iter++)
        {
            var cells = new ushort[ChunkBlockDecoder.CellsPerChunk];
            for (int i = 0; i < cells.Length; i++) cells[i] = (ushort)solidPalette[rnd.Next(solidPalette.Length)];
            var (blob, merged) = Build(solidPalette, cells, null, null);
            ChunkBlockDecoder.DecodeChunkHistogram(blob, hist, touched, out long solid, out long fluid, out long air);
            long expectedSolid = merged.Count(x => x != 0);
            Assert.Equal(expectedSolid, solid);
            Assert.Equal(0, fluid);
            Assert.Equal(ChunkBlockDecoder.CellsPerChunk - expectedSolid, air);
            foreach (var id in touched)
            {
                long want = merged.Count(x => x == id);
                Assert.Equal(want, hist[id]);
                hist[id] = 0;
            }
            touched.Clear();
        }
    }

    [Fact]
    public void TerrainMap_BottomY_IsExactAboveY255()
    {
        // The old staging word packed cy into 3 bits at 50..52 and bottom into 53..57: for cy=8 the
        // high bit of cy landed in the low bit of bottom, so every column with a bottom block at
        // y<32 in a chunk above y=255 got BottomY+1. The merge now keeps the values as ints.
        var db = Path.Combine(Path.GetTempPath(), "vs-bottom-" + Guid.NewGuid().ToString("N") + ".vcdbs");
        try
        {
            var cells = new ushort[ChunkBlockDecoder.CellsPerChunk];
            cells[0] = 5; // x=0, z=0, y=0
            var blob = Build(new[] { 0, 5 }, cells, null, null).blob;
            using (var con = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db }.ToString()))
            {
                con.Open();
                foreach (var sql in new[]
                         {
                             "CREATE TABLE chunk(position INTEGER PRIMARY KEY, data BLOB);",
                             "CREATE TABLE mapchunk(position INTEGER PRIMARY KEY, data BLOB);",
                             "CREATE TABLE mapregion(position INTEGER PRIMARY KEY, data BLOB);",
                             "CREATE TABLE gamedata(savegameid INTEGER PRIMARY KEY, data BLOB);",
                             "CREATE TABLE playerdata(playerid INTEGER PRIMARY KEY AUTOINCREMENT, playeruid TEXT, data BLOB);"
                         })
                using (var c = con.CreateCommand()) { c.CommandText = sql; c.ExecuteNonQuery(); }
                using (var ins = con.CreateCommand())
                {
                    ins.CommandText = "INSERT INTO chunk(position,data) VALUES($p,$d);";
                    ins.Parameters.AddWithValue("$p", (long)ChunkPosHelper.ToIndex(0, 8, 0));
                    ins.Parameters.AddWithValue("$d", blob);
                    ins.ExecuteNonQuery();
                }
            }

            using var save = VSSaveFile.OpenReadOnly(db);
            var tm = TerrainMap.BuildInArea(save, null);
            int i = tm.ColumnIndex(0, 0);
            Assert.True(i >= 0);
            Assert.Equal(8 * 32, tm.SurfaceY[i]);
            Assert.Equal(8 * 32, tm.BottomY[i]);   // was 257 before the packing fix

            // Blocks mode must not allocate the water column; the heightmap must not allocate the rain
            // surface/shadow it never reads. Both still carry surface/bottom/top.
            var blocks = TerrainMap.BuildInArea(save, null, null, TerrainNeeds.Blocks, TerrainColorizer.ModeBlocks);
            Assert.Null(blocks.WaterDepth);
            Assert.NotNull(blocks.MapY);
            Assert.NotNull(blocks.Shadow);
            var height = TerrainMap.BuildForMode(save, null, TerrainColorizer.ModeHeight);
            Assert.NotNull(height.WaterDepth);
            Assert.Null(height.MapY);
            Assert.Null(height.Shadow);
        }
        finally
        {
            if (File.Exists(db)) File.Delete(db);
        }
    }
}
