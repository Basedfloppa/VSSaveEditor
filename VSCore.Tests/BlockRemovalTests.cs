using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Xunit;

namespace VSCore.Tests;

/// <summary>
/// Removing blocks from chunk data: the field1/field16 palette is zeroed while the bit planes and the
/// remaining fields stay untouched. Synthetic blobs are built right here (a full palette + planes
/// encoder), so the tests do not depend on real saves; a separate check runs on a real save when present.
/// </summary>
public class BlockRemovalTests
{
    // ---------- synthetic block field encoder ----------

    private static void WriteVarint(Stream s, long v)
    {
        ulong u = (ulong)v;
        while (u >= 0x80) { s.WriteByte((byte)((u & 0x7F) | 0x80)); u >>= 7; }
        s.WriteByte((byte)u);
    }

    private static void WriteLen(Stream s, int n) => WriteVarint(s, n);

    /// <summary>Block field in the save format: [int32 marker][palette][zstd bit planes].
    /// compressPalette=false means the raw form (negative marker), true the zstd form (positive marker).</summary>
    private static byte[] BuildField(int[] palette, ushort[] cellIds, bool compressPalette)
    {
        int bits = ChunkBlockDecoder.BitsNeeded(palette.Length);
        var planes = new byte[bits * 4096];
        var index = new Dictionary<int, int>();
        for (int i = 0; i < palette.Length; i++) index.TryAdd(palette[i], i);
        for (int idx = 0; idx < ChunkBlockDecoder.CellsPerChunk; idx++)
        {
            int val = index[cellIds[idx]];
            int word = idx >> 5, bit = idx & 31;
            for (int p = 0; p < bits; p++)
                if (((val >> p) & 1) != 0)
                    planes[p * 4096 + word * 4 + (bit >> 3)] |= (byte)(1 << (bit & 7));
        }

        var palBytes = new byte[palette.Length * 4];
        for (int i = 0; i < palette.Length; i++) BinaryPrimitives.WriteInt32LittleEndian(palBytes.AsSpan(i * 4), palette[i]);

        var ms = new MemoryStream();
        if (compressPalette)
        {
            var packed = ChunkBlockDecoder.ZstdCompress(palBytes);
            WriteI32(ms, packed.Length);
            ms.Write(packed);
        }
        else
        {
            WriteI32(ms, -palBytes.Length);
            ms.Write(palBytes);
        }
        ms.Write(ChunkBlockDecoder.ZstdCompress(planes));
        return ms.ToArray();
    }

    private static void WriteI32(Stream s, int v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, v);
        s.Write(b);
    }

    private static byte[] ChunkBlob(byte[]? field1, byte[]? field16 = null, byte[]? field12 = null)
    {
        var ms = new MemoryStream();
        void Field(int field, byte[] payload)
        {
            WriteVarint(ms, ((long)field << 3) | 2);
            WriteLen(ms, payload.Length);
            ms.Write(payload);
        }
        if (field1 is not null) Field(1, field1);
        if (field16 is not null) Field(16, field16);
        Field(12, field12 ?? new byte[] { (byte)'1', (byte)'.', (byte)'2', (byte)'2', (byte)'.', (byte)'7' });
        return ms.ToArray();
    }

    /// <summary>Cells cycling through the palette — guarantees that every palette entry occurs in the chunk.</summary>
    private static ushort[] Pattern(int[] palette)
    {
        var cells = new ushort[ChunkBlockDecoder.CellsPerChunk];
        for (int i = 0; i < cells.Length; i++) cells[i] = (ushort)palette[i % palette.Length];
        return cells;
    }

    private static byte[] PayloadOf(byte[] blob, int field) =>
        ChunkDataEditor.WalkTop(blob).Where(t => t.Field == field).Select(t =>
        {
            var b = new byte[t.PayloadEnd - t.PayloadStart];
            Buffer.BlockCopy(blob, t.PayloadStart, b, 0, b.Length);
            return b;
        }).First();

    private static void AssertOnlyWantedChanged(ushort[] before, ushort[] after, HashSet<int> wanted)
    {
        int removed = 0;
        for (int i = 0; i < before.Length; i++)
        {
            if (wanted.Contains(before[i])) { Assert.Equal(0, after[i]); removed++; }
            else Assert.Equal(before[i], after[i]);
        }
        Assert.True(removed > 0, "no cell matched the id being removed");
    }

    // ---------- tests ----------

    [Fact(DisplayName = "block removal (raw palette): the selected id becomes air, the other cells and planes stay intact")]
    public void ClearBlockIds_RawPalette_ZeroesOnlySelectedCells()
    {
        int[] palette = { 0, 5, 9 };
        var cells = Pattern(palette);
        var blob = ChunkBlob(BuildField(palette, cells, compressPalette: false));

        var rewritten = ChunkBlockRewriter.ClearBlockIds(blob, new HashSet<int> { 9 }, out int entries, out int fields);

        Assert.NotNull(rewritten);
        Assert.Equal(1, entries);
        Assert.Equal(1, fields);
        AssertOnlyWantedChanged(cells, ChunkBlockDecoder.DecodeChunkBlocks(rewritten!), new HashSet<int> { 9 });

        // raw palette: the field length does not change and the bit planes stay byte-for-byte identical
        Assert.Equal(blob.Length, rewritten!.Length);
        var p1Before = PayloadOf(blob, 1);
        var p1After = PayloadOf(rewritten, 1);
        Assert.Equal(p1Before.Length, p1After.Length);
        Assert.Equal(p1Before.Skip(4 + 3 * 4), p1After.Skip(4 + 3 * 4));

        // a second pass over the result has nothing left to change
        Assert.Null(ChunkBlockRewriter.ClearBlockIds(rewritten, new HashSet<int> { 9 }, out _, out _));
        // the source blob is left untouched
        Assert.Equal(cells, ChunkBlockDecoder.DecodeChunkBlocks(blob));
    }

    [Fact(DisplayName = "block removal (zstd palette): the palette is recompressed, the planes are not touched")]
    public void ClearBlockIds_CompressedPalette_RecompressesPaletteOnly()
    {
        int[] palette = { 0, 5, 9, 11, 27 };
        var cells = Pattern(palette);
        var blob = ChunkBlob(BuildField(palette, cells, compressPalette: true));

        var rewritten = ChunkBlockRewriter.ClearBlockIds(blob, new HashSet<int> { 9, 11 }, out int entries, out int fields);

        Assert.NotNull(rewritten);
        Assert.Equal(2, entries);
        Assert.Equal(1, fields);
        var wanted = new HashSet<int> { 9, 11 };
        AssertOnlyWantedChanged(cells, ChunkBlockDecoder.DecodeChunkBlocks(rewritten!), wanted);

        // the palette stays in compressed form, but 9 and 11 are air in it now
        var p1After = PayloadOf(rewritten!, 1);
        int num = BinaryPrimitives.ReadInt32LittleEndian(p1After);
        Assert.True(num > 0, $"the palette marker should stay positive (zstd), not {num}");
        var pal = ChunkBlockDecoder.ZstdDecompress(p1After, 4, num);
        var palIds = new int[pal.Length / 4];
        Buffer.BlockCopy(pal, 0, palIds, 0, pal.Length);
        Assert.Equal(new[] { 0, 5, 0, 0, 27 }, palIds);

        // the bit planes follow the palette immediately and match byte for byte
        var p1Before = PayloadOf(blob, 1);
        int planesBefore = 4 + BinaryPrimitives.ReadInt32LittleEndian(p1Before);
        int planesAfter = 4 + num;
        Assert.Equal(p1Before.Skip(planesBefore), p1After.Skip(planesAfter));
    }

    [Fact(DisplayName = "liquid removal (field16) zeroes only its palette")]
    public void ClearBlockIds_Liquids_AreClearedToo()
    {
        int[] liqPalette = { 0, 2897 };
        var liqCells = Pattern(liqPalette);
        var blob = ChunkBlob(field1: null, field16: BuildField(liqPalette, liqCells, compressPalette: false));

        var rewritten = ChunkBlockRewriter.ClearBlockIds(blob, new HashSet<int> { 2897 }, out int entries, out int fields);

        Assert.NotNull(rewritten);
        Assert.Equal(1, entries);
        Assert.Equal(1, fields);
        var after = ChunkBlockDecoder.DecodeChunkLiquids(rewritten!)!;
        Assert.All(after, id => Assert.Equal(0, id));
    }

    [Fact(DisplayName = "no matches / malformed blob: null, with no exceptions")]
    public void ClearBlockIds_NoMatchAndMalformed_ReturnNull()
    {
        var blob = ChunkBlob(BuildField(new[] { 0, 5, 9 }, Pattern(new[] { 0, 5, 9 }), false));
        Assert.Null(ChunkBlockRewriter.ClearBlockIds(blob, new HashSet<int> { 777 }, out _, out _));
        Assert.Null(ChunkBlockRewriter.ClearBlockIds(Array.Empty<byte>(), new HashSet<int> { 5 }, out _, out _));

        // int.MinValue marker: -num would overflow
        var garbage = new byte[] { 0x00, 0x00, 0x00, 0x80, 1, 2, 3 };
        var bad = ChunkBlob(garbage);
        var ex = Record.Exception(() =>
        {
            Assert.Null(ChunkBlockRewriter.ClearBlockIds(bad, new HashSet<int> { 5 }, out _, out _));
            Assert.Null(ChunkBlockRewriter.ClearBlockIds(new byte[] { 0x0A }, new HashSet<int> { 5 }, out _, out _));
        });
        Assert.Null(ex);
    }


    private static void AssertReplaced(ushort[] before, ushort[] after, HashSet<int> wanted, int target)
    {
        int replaced = 0;
        for (int i = 0; i < before.Length; i++)
        {
            if (wanted.Contains(before[i]) && before[i] != target) { Assert.Equal(target, after[i]); replaced++; }
            else Assert.Equal(before[i], after[i]);
        }
        Assert.True(replaced > 0, "no cell matched the id being replaced");
    }

    [Fact(DisplayName = "block replacement (raw palette): the selected id becomes target, the other cells and planes stay intact")]
    public void ReplaceBlockIds_RawPalette_ReplacesOnlySelectedCells()
    {
        int[] palette = { 0, 5, 9 };
        var cells = Pattern(palette);
        var blob = ChunkBlob(BuildField(palette, cells, compressPalette: false));

        var rewritten = ChunkBlockRewriter.ReplaceBlockIds(blob, new HashSet<int> { 9 }, 11, out int entries, out int fields);

        Assert.NotNull(rewritten);
        Assert.Equal(1, entries);
        Assert.Equal(1, fields);
        AssertReplaced(cells, ChunkBlockDecoder.DecodeChunkBlocks(rewritten!), new HashSet<int> { 9 }, 11);
        Assert.Equal(blob.Length, rewritten!.Length);

        // a second pass over the result has nothing left to change
        Assert.Null(ChunkBlockRewriter.ReplaceBlockIds(rewritten, new HashSet<int> { 9 }, 11, out _, out _));
        // the source blob is left untouched
        Assert.Equal(cells, ChunkBlockDecoder.DecodeChunkBlocks(blob));
    }

    [Fact(DisplayName = "block replacement (zstd palette): the palette is recompressed, the planes are not touched")]
    public void ReplaceBlockIds_CompressedPalette_ReplacesOnlySelectedCells()
    {
        int[] palette = { 0, 5, 9, 11, 27 };
        var cells = Pattern(palette);
        var blob = ChunkBlob(BuildField(palette, cells, compressPalette: true));

        var rewritten = ChunkBlockRewriter.ReplaceBlockIds(blob, new HashSet<int> { 9, 11 }, 27, out int entries, out int fields);

        Assert.NotNull(rewritten);
        Assert.Equal(2, entries);   // 9 and 11; 27 is already the target and does not count
        Assert.Equal(1, fields);
        AssertReplaced(cells, ChunkBlockDecoder.DecodeChunkBlocks(rewritten!), new HashSet<int> { 9, 11 }, 27);

        var p1After = PayloadOf(rewritten!, 1);
        int num = BinaryPrimitives.ReadInt32LittleEndian(p1After);
        Assert.True(num > 0, $"the palette marker should stay positive (zstd), not {num}");
        var pal = ChunkBlockDecoder.ZstdDecompress(p1After, 4, num);
        var palIds = new int[pal.Length / 4];
        Buffer.BlockCopy(pal, 0, palIds, 0, pal.Length);
        Assert.Equal(new[] { 0, 5, 27, 27, 27 }, palIds);
    }

    [Fact(DisplayName = "CopyAndReplaceBlocks: edits land only in the copy, the original stays intact, a .bak exists")]
    public void CopyAndReplaceBlocks_WritesOnlyToCopy()
    {
        using var t = new TempSave();
        int[] palette = { 0, 42, 43, 77 };
        var cells = Pattern(palette);
        var blobA = ChunkBlob(BuildField(palette, cells, compressPalette: false));
        var blobB = ChunkBlob(BuildField(palette, cells, compressPalette: true));
        CreateSave(t.Src, (10, 0, 20, blobA), (10, 1, 20, blobB));

        var wanted = new HashSet<int> { 42, 77 };
        var outcome = VSSaveFile.CopyAndReplaceBlocks(t.Src, wanted, 5);

        Assert.Equal(2, outcome.ChunksScanned);
        Assert.Equal(2, outcome.ChunksChanged);
        Assert.Equal(2, outcome.FieldsRewritten);
        Assert.Equal(4, outcome.PaletteEntriesReplaced);
        Assert.True(File.Exists(outcome.BackupPath), "a .bak file must exist");
        Assert.False(File.Exists(outcome.CopyPath + "-wal"), "the copy must be self-contained");

        using (var copy = VSSaveFile.OpenReadOnly(outcome.CopyPath))
        {
            foreach (var row in copy.ChunkRowsWithData)
                AssertReplaced(cells, ChunkBlockDecoder.DecodeChunkBlocks(row.Data), wanted, 5);
        }
        // the original is unchanged
        using (var orig = VSSaveFile.OpenReadOnly(t.Src))
        {
            foreach (var row in orig.ChunkRowsWithData)
                Assert.Equal(cells, ChunkBlockDecoder.DecodeChunkBlocks(row.Data));
        }
        Assert.Throws<ArgumentException>(() => VSSaveFile.CopyAndReplaceBlocks(t.Src, Array.Empty<int>(), 5));
    }

    // ---------- save copy ----------

    private sealed class TempSave : IDisposable
    {
        public string Dir { get; }
        public string Src { get; }
        public TempSave()
        {
            Dir = Path.Combine(Path.GetTempPath(), "vsremove_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            Src = Path.Combine(Dir, "world.vcdbs");
        }
        public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
    }

    private static void CreateSave(string path, params (int cx, int cy, int cz, byte[] data)[] chunks)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        using var c = new SqliteConnection(cs);
        c.Open();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText =
                "CREATE TABLE chunk(position INTEGER PRIMARY KEY, data BLOB);" +
                "CREATE TABLE mapchunk(position INTEGER PRIMARY KEY, data BLOB);" +
                "CREATE TABLE mapregion(position INTEGER PRIMARY KEY, data BLOB);" +
                "CREATE TABLE gamedata(savegameid INTEGER PRIMARY KEY, data BLOB);" +
                "CREATE TABLE playerdata(playerid INTEGER PRIMARY KEY, playeruid TEXT, data BLOB);";
            cmd.ExecuteNonQuery();
        }
        foreach (var (cx, cy, cz, data) in chunks)
        {
            using var ins = c.CreateCommand();
            ins.CommandText = "INSERT INTO chunk(position,data) VALUES(@p,@d);";
            ins.Parameters.AddWithValue("@p", (long)ChunkPosHelper.ToIndex(cx, cy, cz));
            ins.Parameters.AddWithValue("@d", data);
            ins.ExecuteNonQuery();
        }
    }

    [Fact(DisplayName = "CopyAndRemoveBlocks: edits land only in the copy, the original stays intact, a .bak exists, idempotent")]
    public void CopyAndRemoveBlocks_WritesOnlyToCopy()
    {
        using var t = new TempSave();
        int[] palette = { 0, 42, 43, 77 };
        var cells = Pattern(palette);
        var solid = BuildField(palette, cells, compressPalette: false);
        var solidZstd = BuildField(palette, cells, compressPalette: true);
        var blobA = ChunkBlob(solid);
        var blobB = ChunkBlob(solidZstd);
        CreateSave(t.Src, (10, 0, 20, blobA), (10, 1, 20, blobB));

        var wanted = new HashSet<int> { 42, 77 };
        var outcome = VSSaveFile.CopyAndRemoveBlocks(t.Src, wanted);

        Assert.Equal(2, outcome.ChunksScanned);
        Assert.Equal(2, outcome.ChunksChanged);
        Assert.Equal(2, outcome.FieldsRewritten);
        Assert.Equal(4, outcome.PaletteEntriesCleared);   // 2 entries in each chunk
        Assert.True(File.Exists(outcome.BackupPath), "a .bak file must exist");
        Assert.False(File.Exists(outcome.CopyPath + "-wal"), "the copy must be self-contained");
        Assert.Equal(new FileInfo(t.Src).Length, new FileInfo(outcome.BackupPath).Length);

        using (var copy = VSSaveFile.OpenReadOnly(outcome.CopyPath))
        {
            foreach (var row in copy.ChunkRowsWithData)
                AssertOnlyWantedChanged(cells, ChunkBlockDecoder.DecodeChunkBlocks(row.Data), wanted);
        }
        // the original is unchanged
        using (var orig = VSSaveFile.OpenReadOnly(t.Src))
        {
            foreach (var row in orig.ChunkRowsWithData)
                Assert.Equal(cells, ChunkBlockDecoder.DecodeChunkBlocks(row.Data));
        }
        // a second pass, now over the COPY, changes nothing (idempotent)
        var again = VSSaveFile.CopyAndRemoveBlocks(outcome.CopyPath, wanted);
        Assert.Equal(0, again.ChunksChanged);
        Assert.Throws<ArgumentException>(() => VSSaveFile.CopyAndRemoveBlocks(t.Src, Array.Empty<int>()));
    }

    // ---------- real save ----------

    /// <summary>On a real save: removing modded ids touches no other cell and no other chunk
    /// field (solids/liquids are compared before and after, plus a byte comparison of the other fields).</summary>
    [Fact(DisplayName = "real save: modded blocks are cleared, the other cells and fields stay unchanged")]
    public void RealSave_ModBlocksRemoved_WithoutCollateralChanges()
    {
        const string realSave = "/home/user/.config/VintagestoryData/Saves/serene cave story.vcdbs";
        if (!File.Exists(realSave)) return;

        using var save = VSSaveFile.OpenReadOnly(realSave);
        var reg = save.BlockRegistry;
        var modIds = reg.Codes.Where(c => c.Contains(':')).SelectMany(reg.IdsOf).ToHashSet();
        if (modIds.Count == 0) return;

        var rows = save.ChunkRowsWithData;
        int checkedChunks = 0, removedCells = 0;
        foreach (var row in rows)
        {
            if (checkedChunks >= 25) break;
            if (!ChunkBlockDecoder.DecodePalette(row.Data).Any(modIds.Contains)) continue;

            var solidsBefore = ChunkBlockDecoder.DecodeChunkBlocks(row.Data);
            var liquidsBefore = ChunkBlockDecoder.DecodeChunkLiquids(row.Data);
            var rewritten = ChunkBlockRewriter.ClearBlockIds(row.Data, modIds, out int entries, out _);
            Assert.NotNull(rewritten);
            Assert.True(entries > 0);
            checkedChunks++;

            var solidsAfter = ChunkBlockDecoder.DecodeChunkBlocks(rewritten!);
            var liquidsAfter = ChunkBlockDecoder.DecodeChunkLiquids(rewritten!);
            for (int i = 0; i < solidsBefore.Length; i++)
            {
                if (modIds.Contains(solidsBefore[i])) { Assert.Equal(0, solidsAfter[i]); removedCells++; }
                else Assert.Equal(solidsBefore[i], solidsAfter[i]);
            }
            if (liquidsBefore is not null)
            {
                Assert.NotNull(liquidsAfter);
                for (int i = 0; i < liquidsBefore.Length; i++)
                {
                    if (modIds.Contains(liquidsBefore[i])) Assert.Equal(0, liquidsAfter![i]);
                    else Assert.Equal(liquidsBefore[i], liquidsAfter![i]);
                }
            }

            // every top-level field except blocks/liquids is byte-for-byte identical
            var topBefore = ChunkDataEditor.WalkTop(row.Data);
            var topAfter = ChunkDataEditor.WalkTop(rewritten!);
            for (int i = 0; i < topBefore.Count; i++)
            {
                var a = topBefore[i];
                var b = topAfter[i];
                Assert.Equal(a.Field, b.Field);
                Assert.Equal(a.Occurrence, b.Occurrence);
                if (a.Field is 1 or 16) continue;
                Assert.Equal(row.Data.Skip(a.PayloadStart).Take(a.PayloadEnd - a.PayloadStart),
                             rewritten!.Skip(b.PayloadStart).Take(b.PayloadEnd - b.PayloadStart));
            }
        }
        Assert.True(checkedChunks > 0, "no chunk with a modded block was found in the save");
        Assert.True(removedCells > 0, "no modded cell was removed");
    }
}
