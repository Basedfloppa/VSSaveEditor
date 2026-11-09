using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using VSCore;
using Xunit;

namespace VSCore.Tests;

/// <summary>
/// The histogram decoder now walks 32-cell words and skips the ones whose bit planes are all zero.
/// That is exact only because a zero word is 32 copies of palette[0] — and palette[0] is not always
/// air. These tests pin the fast path against a per-cell decode of the same chunk, including the
/// palette[0] != 0 case the shortcut silently gets wrong if it assumes "zero means air".
/// </summary>
public class ChunkHistogramTests
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
        return ms.ToArray();
    }

    private static byte[] Blob(int[] solidPalette, ushort[] solidCells, int[]? liquidPalette = null, ushort[]? liquidCells = null)
        => Chunk(BuildField(solidPalette, solidCells),
            liquidPalette is null ? null : BuildField(liquidPalette, liquidCells!));

    /// <summary>Ground truth: the per-cell decode the word shortcut has to reproduce exactly.</summary>
    private static void Expected(byte[] blob, out long solid, out long fluid, out long air, long[] hist)
    {
        var solids = ChunkBlockDecoder.DecodeChunkBlocks(blob);
        var fluids = ChunkBlockDecoder.DecodeChunkLiquids(blob) ?? new ushort[ChunkBlockDecoder.CellsPerChunk];
        solid = fluid = air = 0;
        for (int i = 0; i < ChunkBlockDecoder.CellsPerChunk; i++)
        {
            if (solids[i] != 0) { solid++; hist[solids[i]]++; }
            else if (fluids[i] != 0) { fluid++; hist[fluids[i]]++; }
            else air++;
        }
    }

    private static void AssertSameAsPerCell(byte[] blob)
    {
        var hist = new long[65536];
        var touched = new List<int>();
        ChunkBlockDecoder.DecodeChunkHistogram(blob, hist, touched, out var solid, out var fluid, out var air);

        var expected = new long[65536];
        Expected(blob, out var es, out var ef, out var ea, expected);

        Assert.Equal(es, solid);
        Assert.Equal(ef, fluid);
        Assert.Equal(ea, air);
        for (int id = 0; id < hist.Length; id++)
            if (hist[id] != expected[id])
                Assert.Fail($"id {id}: histogram {hist[id]} != per-cell {expected[id]}");

        // The caller resets exactly these slots after each chunk; a missing id would leak counts into
        // the next chunk, an extra one would clear a slot another chunk still needs.
        var touchedSet = new HashSet<int>(touched);
        Assert.Equal(touched.Count, touchedSet.Count);
        for (int id = 0; id < hist.Length; id++)
            Assert.Equal(expected[id] > 0, touchedSet.Contains(id));

        // and the caller-visible invariant: after clearing the touched slots the array is clean again
        foreach (var id in touched) hist[id] = 0;
        Assert.DoesNotContain(hist, v => v != 0);
    }

    [Fact]
    public void Mostly_air_chunk_with_whole_zero_words_matches_per_cell_decode()
    {
        // palette[0] is air: the common case, where whole words are 32 air cells
        var cells = new ushort[ChunkBlockDecoder.CellsPerChunk];
        for (int i = 0; i < 200; i++) cells[i * 97] = 7;
        for (int i = 0; i < 50; i++) cells[15000 + i * 31] = 9;
        AssertSameAsPerCell(Blob(new[] { 0, 7, 9 }, cells));
    }

    [Fact]
    public void Zero_words_are_not_air_when_palette0_is_a_block()
    {
        // palette[0] = 3: every all-zero word is 32 solid cells, not air. A shortcut that assumed
        // "zero word means air" would report ~all cells air here.
        var cells = new ushort[ChunkBlockDecoder.CellsPerChunk];
        Array.Fill(cells, (ushort)3);
        for (int i = 0; i < 64; i++) cells[i * 501] = 5;
        AssertSameAsPerCell(Blob(new[] { 3, 5 }, cells));
    }

    [Fact]
    public void Liquid_field_fills_the_solid_gaps()
    {
        var solids = new ushort[ChunkBlockDecoder.CellsPerChunk];
        var liquids = new ushort[ChunkBlockDecoder.CellsPerChunk];
        for (int i = 0; i < ChunkBlockDecoder.CellsPerChunk; i += 3) solids[i] = 11;
        for (int i = 1; i < ChunkBlockDecoder.CellsPerChunk; i += 7) liquids[i] = 12;
        AssertSameAsPerCell(Blob(new[] { 0, 11 }, solids, new[] { 0, 12 }, liquids));
    }

    [Fact]
    public void Single_entry_palette_and_empty_fields_are_all_air()
    {
        AssertSameAsPerCell(Chunk(null, null));
        var cells = new ushort[ChunkBlockDecoder.CellsPerChunk];
        AssertSameAsPerCell(Blob(new[] { 0 }, cells));
    }

    [Fact]
    public void Random_chunks_match_per_cell_decode()
    {
        var rng = new Random(20261006);
        for (int round = 0; round < 12; round++)
        {
            // palette[0] alternates between air and a real block, and never collides with the others
            int first = round % 3 == 0 ? 100 + round : 0;
            var palette = new[] { 0, 41, 42, 43 };
            if (first != 0) palette[0] = first;
            var cells = new ushort[ChunkBlockDecoder.CellsPerChunk];
            for (int i = 0; i < cells.Length; i++)
                cells[i] = (ushort)(rng.Next(4) == 0 ? palette[rng.Next(palette.Length)] : palette[0]);
            AssertSameAsPerCell(Blob(palette, cells));
        }
    }
}
