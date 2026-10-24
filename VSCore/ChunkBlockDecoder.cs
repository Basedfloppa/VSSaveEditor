using VSCore.Format;

namespace VSCore;

/// <summary>
/// Decoder of the VS chunk block array following the inverse of Vintagestory.Common/Compression.DecompressCombined
/// (decoded from the VintagestoryLib client library — the source of truth, Q2).
/// A chunk = 32×32×32 = 32768 cells; the block id is a ushort.
/// </summary>
public static class ChunkBlockDecoder
{
    public const int CellsPerChunk = 32 * 32 * 32; // 32768

    /// <summary>Returns the chunk's ushort blocks (32768) from the blob of the `chunk` table.</summary>
    public static ushort[] DecodeChunkBlocks(byte[] chunkBlob)
    {
        var blocksCompressed = PbReader.ReadMessage(chunkBlob, maxDepth: 1)
            .FirstOrDefault(f => f.Field == 1 && f.WireType == 2)?.Bytes;
        if (blocksCompressed is null || blocksCompressed.Length == 0)
            return new ushort[CellsPerChunk]; // empty/air
        return DecodeBlocksCompressed(blocksCompressed);
    }

    /// <summary>Returns the FULL contents of a chunk (32768): solid blocks (field 1) + liquids (field 16).
    /// In VS, water/lava are stored SEPARATELY from solid blocks — in the liquids field (field 16). Where
    /// there is no solid block (0), the liquid is used. Without this all water on the map is lost.</summary>
    public static ushort[] DecodeChunkBlocksMerged(byte[] chunkBlob)
    {
        var solids = DecodeChunkBlocks(chunkBlob);
        var fluids = DecodeChunkLiquids(chunkBlob);
        if (fluids is null) return solids;
        for (int i = 0; i < solids.Length; i++)
            if (solids[i] == 0) solids[i] = fluids[i];
        return solids;
    }

    /// <summary>Chunk liquids (field 16, liquidsCompressed) — the same palette + planes compression as the blocks.
    /// null if there are no liquids.</summary>
    public static ushort[]? DecodeChunkLiquids(byte[] chunkBlob)
    {
        var liq = PbReader.ReadMessage(chunkBlob, maxDepth: 1)
            .FirstOrDefault(f => f.Field == 16 && f.WireType == 2)?.Bytes;
        if (liq is null || liq.Length == 0) return null;
        try { return DecodeBlocksCompressed(liq); }
        catch { return null; }
    }

    /// <summary>FULL contents of a chunk (32768) in ONE top-level pass: solids (field 1)
    /// + liquids (field 16), where there is no solid block (0) the liquid is put in. In VS liquids are stored
    /// separately; without merging, water is lost. Cheaper than two separate ReadMessage calls (faster on streaming loads).</summary>
    public static ushort[] DecodeChunkBlocksMergedOnePass(byte[] chunkBlob)
    {
        byte[]? solids = null, fluids = null;
        foreach (var f in PbReader.ReadMessage(chunkBlob, maxDepth: 1))
        {
            if (f.WireType != 2 || f.Bytes is null) continue;
            if (f.Field == 1) solids = f.Bytes;
            else if (f.Field == 16) fluids = f.Bytes;
        }
        var r = solids is { Length: > 0 } ? DecodeBlocksCompressed(solids) : new ushort[CellsPerChunk];
        if (fluids is { Length: > 0 })
        {
            ushort[] liq;
            try { liq = DecodeBlocksCompressed(fluids); }
            catch { return r; }
            for (int i = 0; i < r.Length; i++) if (r[i] == 0) r[i] = liq[i];
        }
        return r;
    }

    /// <summary>Chunk block palette (field1): ALL ids that appear in the chunk at all.</summary>
    public static int[] DecodePalette(byte[] chunkBlob) => DecodePaletteOfField(chunkBlob, 1);

    /// <summary>Chunk liquid palette (field16).</summary>
    public static int[] DecodeLiquidPalette(byte[] chunkBlob) => DecodePaletteOfField(chunkBlob, 16);

    /// <summary>
    /// Palette of the given chunk field WITHOUT unpacking the bit planes (the planes are the heaviest part).
    /// This is what the fast overlay filter is built on: if the palette has none of the wanted ids, the chunk is not decoded.
    /// </summary>
    public static int[] DecodePaletteOfField(byte[] chunkBlob, int field)
    {
        var comp = PbReader.ReadMessage(chunkBlob, maxDepth: 1)
            .FirstOrDefault(f => f.Field == field && f.WireType == 2)?.Bytes;
        if (comp is null || comp.Length < 4) return Array.Empty<int>();
        return ReadPalette(comp, out _);
    }

    /// <summary>Number of block palette entries of the chunk blob (following DecodeBlocksCompressed).</summary>
    public static int DecodePaletteSize(byte[] chunkBlob)
    {
        var blocksCompressed = PbReader.ReadMessage(chunkBlob, maxDepth: 1)
            .FirstOrDefault(f => f.Field == 1 && f.WireType == 2)?.Bytes;
        if (blocksCompressed is null || blocksCompressed.Length < 4) return 0;
        return ReadPalette(blocksCompressed, out _).Length;
    }

    /// <summary>Palette of the blocks field: the num marker (int32 le): &lt;0 — raw int32, &gt;0 — zstd-compressed int32.</summary>
    private static int[] ReadPalette(byte[] b, out int dataOffset)
    {
        int num = ReadInt32(b, 0);
        if (num == 0) { dataOffset = 4; return Array.Empty<int>(); }
        if (num < 0)
        {
            dataOffset = 4 + (-num);
            int palInts = -num / 4;
            if (palInts <= 1 || dataOffset > b.Length) return Array.Empty<int>();
            var pal = new int[palInts];
            for (int i = 0; i < palInts; i++) pal[i] = ReadInt32(b, 4 + i * 4);
            return pal;
        }
        dataOffset = 4 + num;
        if (dataOffset > b.Length) return Array.Empty<int>();
        var palBytes = ZstdDecompress(b, 4, num);
        int n = palBytes.Length / 4;
        if (n <= 1) return Array.Empty<int>();
        var res = new int[n];
        Buffer.BlockCopy(palBytes, 0, res, 0, n * 4);
        return res;
    }

    /// <summary>Bits needed for a palette of n entries: ceil(log2(n)) (for n>0).</summary>
    public static int BitsNeeded(int n) => n <= 1 ? 1 : (int)Math.Ceiling(Math.Log(n, 2));

    /// <summary>How many bit planes actually sit in the chunk data (after decompression).</summary>
    public static int CountPlanesInData(byte[] chunkBlob)
    {
        var bc = PbReader.ReadMessage(chunkBlob, maxDepth: 1)
            .FirstOrDefault(f => f.Field == 1 && f.WireType == 2)?.Bytes;
        if (bc is null || bc.Length < 4) return 0;
        int num = ReadInt32(bc, 0);
        if (num == 0) return 0;
        int dataOffset = num < 0 ? 4 + (-num) : 4 + num;
        var planes = ZstdDecompress(bc, dataOffset, bc.Length - dataOffset);
        return planes.Length / 4096;
    }

    /// <summary>Direct decoding of the chunk's blocks field (field1).</summary>
    public static ushort[] DecodeBlocksCompressed(byte[] b)
    {
        // Truncated blob: the int32 marker does not fit — this is air, not an OOB in ReadInt32.
        if (b.Length < 4) return new ushort[CellsPerChunk];
        // the first int32 (le) = the palette size marker
        if (ReadInt32(b, 0) == 0) return new ushort[CellsPerChunk];
        var palette = ReadPalette(b, out int dataOffset);
        if (palette.Length <= 1) return new ushort[CellsPerChunk];

        // IMPORTANT: the number of bits = ceil(log2(palette.Length)), NOT floor. The data holds ceil bit planes;
        // with floor the highest plane is lost and blocks with high palette indices decode incorrectly.
        int bits = BitsNeeded(palette.Length);   // == HighestBit for powers of two, ceil for the rest
        var planes = ZstdDecompress(b, dataOffset, b.Length - dataOffset);
        // bit planes: 1024 int each (32768 bits)
        var blocks = new int[bits][];
        int offset = 0;
        for (int p = 0; p < bits; p++)
        {
            var plane = new int[1024];
            if (offset + plane.Length * 4 <= planes.Length)
                Buffer.BlockCopy(planes, offset, plane, 0, plane.Length * 4);
            blocks[p] = plane;
            offset += plane.Length * 4;
        }

        var result = new ushort[CellsPerChunk];
        for (int idx = 0; idx < CellsPerChunk; idx++)
        {
            int word = idx >> 5, bit = idx & 31;
            int val = 0;
            for (int p = 0; p < bits; p++)
                if (((blocks[p][word] >> bit) & 1) != 0) val |= (1 << p);
            result[idx] = val < palette.Length ? (ushort)palette[val] : (ushort)0;
        }
        return result;
    }

    // A backstop for future callers: the blob is untrusted, an out-of-range access yields 0 rather than IndexOutOfRange.
    private static int ReadInt32(byte[] b, int i) =>
        i < 0 || i > b.Length - 4 ? 0
        : b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24);

    // A reusable decoder context per thread: DecompressionStream created a new context
    // on every call (and there are 2–3 per chunk). Decompressor is not thread-safe, so the cache is tied
    // to the thread — decoding runs in Parallel.For and every worker has its own context.
    [ThreadStatic] private static ZstdSharp.Decompressor? _zstdDec;

    // The compressor is the inverse operation of the decoder, needed only when rewriting the block palette
    // (ChunkBlockRewriter). Its context is tied to the thread as well: Compressor is not thread-safe.
    [ThreadStatic] private static ZstdSharp.Compressor? _zstdComp;

    /// <summary>Upper bound of the ZSTD output for one chunk field (palette/bit planes).
    /// Covers valid data with room to spare (16 planes × 4 KB = 64 KB) and caps a "bomb".</summary>
    private const int ZstdMaxOutput = 1 << 20;    // 1 MiB

    /// <summary>ZSTD decompression of the subarray b[off..off+len] (ZstdSharp.Port, managed).
    /// The decoder context is reused per thread (Unwrap writes into its own buffer), the result
    /// is copied into an exact array; the output size is bounded.</summary>
    public static byte[] ZstdDecompress(byte[] b, int off, int len)
    {
        if (len <= 0 || off < 0 || off > b.Length - len) return Array.Empty<byte>();
        var dec = _zstdDec ??= new ZstdSharp.Decompressor();
        try
        {
            return dec.Unwrap(new ReadOnlySpan<byte>(b, off, len), ZstdMaxOutput).ToArray();
        }
        catch (ZstdSharp.ZstdException)
        {
            // A "bomb" (declared output above ZstdMaxOutput) or a corrupted frame. The exception is thrown
            // BEFORE the output memory is allocated, so it costs no memory; the field is simply treated
            // as absent. We reset the context: the failed frame may have left it in a garbage
            // state, while the next chunk on the same thread has to decode normally.
            _zstdDec = null;
            return Array.Empty<byte>();
        }
    }

    /// <summary>ZSTD compression (the inverse of <see cref="ZstdDecompress"/>): needed when rewriting
    /// the block palette, which is stored compressed in the save. Level 3 — the same as ordinary chunk fields.</summary>
    public static byte[] ZstdCompress(byte[] data)
    {
        var comp = _zstdComp ??= new ZstdSharp.Compressor(3);
        return comp.Wrap(data).ToArray();
    }
}
