using System.Buffers.Binary;
using System.Collections.Generic;

namespace VSCore;

/// <summary>Result of removing blocks in a working copy of the save.</summary>
public sealed record BlockRemovalOutcome(
    string CopyPath,
    string BackupPath,
    int ChunksScanned,
    int ChunksChanged,
    int FieldsRewritten,
    long PaletteEntriesCleared,
    long BytesBefore,
    long BytesAfter);

/// <summary>Result of replacing blocks with another block.</summary>
public sealed record BlockReplaceOutcome(
    string CopyPath,
    string BackupPath,
    int ChunksScanned,
    int ChunksChanged,
    int FieldsRewritten,
    long PaletteEntriesReplaced,
    long BytesBefore,
    long BytesAfter);

/// <summary>
/// Removal of blocks (liquids included) from chunk data: the selected block ids become air.
///
/// The trick: the block field (field1) and the liquid field (field16) are [int32 marker][palette][zstd bit planes],
/// and a cell value is an INDEX into the palette (see <see cref="ChunkBlockDecoder"/>). Removal therefore needs
/// neither unpacking the planes nor rewriting them: replacing the int32 of the block itself with 0 (air) in the palette is enough —
/// every cell referencing that index becomes air. The planes and all other chunk fields
/// stay untouched byte for byte, and the palette length does not change (so the indices cannot shift).
///
/// Limitations of the trick (deliberate):
///  - the light (field2) and metadata (field3) of cells that became air are not cleared — the game does not read them for air;
///  - the block entity of a removed block stays in field8 (removing block entities themselves is a separate task);
///  - the palette is rebuilt in the same form: raw stays raw, zstd is recompressed as zstd.
/// </summary>
public static class ChunkBlockRewriter
{
    /// <summary>
    /// Returns a new chunk blob with the selected block ids replaced by air, or null
    /// if there is nothing to change (no matches / a damaged blob). <paramref name="entriesCleared"/> — how many
    /// palette entries were zeroed, <paramref name="fieldsRewritten"/> — how many fields (1/16) were rewritten.
    /// </summary>
    public static byte[]? ClearBlockIds(byte[] chunkBlob, IReadOnlySet<int> blockIds,
        out int entriesCleared, out int fieldsRewritten)
        => RewriteBlockIds(chunkBlob, blockIds, 0, out entriesCleared, out fieldsRewritten);

    /// <summary>
    /// Returns a new chunk blob with the selected block ids replaced by <paramref name="targetId" />
    /// (0 — air). The trick is the same as for removal: only the int32 in the palette changes, the bit planes
    /// and all other fields stay untouched. null — nothing to change.
    /// </summary>
    public static byte[]? ReplaceBlockIds(byte[] chunkBlob, IReadOnlySet<int> blockIds, int targetId,
        out int entriesReplaced, out int fieldsRewritten)
    {
        if (targetId is < 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(targetId), "block id must be 0..65535");
        return RewriteBlockIds(chunkBlob, blockIds, targetId, out entriesReplaced, out fieldsRewritten);
    }

    private static byte[]? RewriteBlockIds(byte[] chunkBlob, IReadOnlySet<int> blockIds, int targetId,
        out int entriesReplaced, out int fieldsRewritten)
    {
        entriesReplaced = 0;
        fieldsRewritten = 0;
        if (chunkBlob.Length == 0 || blockIds.Count == 0) return null;

        // On a damaged blob WalkTop returns whatever it managed to parse; if the needed fields are not there,
        // we simply leave the chunk alone. Nothing may throw: the blob is untrusted.
        List<ChunkDataEditor.ChunkTopField> top;
        try { top = ChunkDataEditor.WalkTop(chunkBlob); }
        catch { return null; }
        if (top.Count == 0) return null;

        List<(int Field, int Occurrence, byte[] Payload)>? edits = null;
        foreach (var t in top)
        {
            if (t.Field != 1 && t.Field != 16) continue;
            int len = t.PayloadEnd - t.PayloadStart;
            if (len < 4) continue;
            var payload = new byte[len];
            Buffer.BlockCopy(chunkBlob, t.PayloadStart, payload, 0, len);
            var rewritten = RewritePalette(payload, blockIds, targetId, out int changed);
            if (rewritten is null || changed == 0) continue;
            entriesReplaced += changed;
            fieldsRewritten++;
            (edits ??= new List<(int, int, byte[])>()).Add((t.Field, t.Occurrence, rewritten));
        }
        return edits is null ? null : ChunkDataEditor.SplicePayloads(chunkBlob, edits);
    }

    /// <summary>
    /// Rewrites the palette of a compressed block field: the int32 of the selected ids -> <paramref name="targetId" />.
    /// The bit planes are left alone (they sit after the palette and are copied as-is).
    /// null — the palette form was not recognised or there are no matches.
    /// </summary>
    internal static byte[]? RewritePalette(byte[] payload, IReadOnlySet<int> blockIds, int targetId, out int changed)
    {
        changed = 0;
        if (payload.Length < 4) return null;
        int num = BinaryPrimitives.ReadInt32LittleEndian(payload);
        if (num == 0 || num == int.MinValue) return null;   // 0 — the whole chunk is air; int.MinValue — -num would overflow

        if (num < 0)
        {
            // raw palette: -num = the number of palette bytes (n entries of int32)
            int palBytes = -num;
            if (palBytes % 4 != 0 || 4 + palBytes > payload.Length) return null;
            int n = palBytes / 4;
            if (n <= 1) return null;
            byte[]? outBuf = null;
            for (int i = 0; i < n; i++)
            {
                int id = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4 + i * 4));
                if (id <= 0 || !blockIds.Contains(id) || id == targetId) continue;
                outBuf ??= (byte[])payload.Clone();
                BinaryPrimitives.WriteInt32LittleEndian(outBuf.AsSpan(4 + i * 4), targetId);
                changed++;
            }
            return outBuf;
        }

        // zstd palette: num = the length of the compressed palette block; then come the planes (left alone)
        if (4 + num > payload.Length) return null;
        var pal = ChunkBlockDecoder.ZstdDecompress(payload, 4, num);
        if (pal.Length < 8 || pal.Length % 4 != 0) return null;   // fewer than two entries — not a palette
        int count = pal.Length / 4;
        bool any = false;
        for (int i = 0; i < count; i++)
        {
            int id = BinaryPrimitives.ReadInt32LittleEndian(pal.AsSpan(i * 4));
            if (id <= 0 || !blockIds.Contains(id) || id == targetId) continue;
            BinaryPrimitives.WriteInt32LittleEndian(pal.AsSpan(i * 4), targetId);
            any = true;
            changed++;
        }
        if (!any) return null;

        var packed = ChunkBlockDecoder.ZstdCompress(pal);
        int planesLen = payload.Length - 4 - num;
        var result = new byte[4 + packed.Length + planesLen];
        BinaryPrimitives.WriteInt32LittleEndian(result, packed.Length);
        Buffer.BlockCopy(packed, 0, result, 4, packed.Length);
        Buffer.BlockCopy(payload, 4 + num, result, 4 + packed.Length, planesLen);
        return result;
    }
}
