using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using VSCore.Format;

namespace VSCore;

/// <summary>Per-column result of a fused chunk decode. Y values are stored as y+1, so 0 means
/// "no block in this column" and a cleared array is already a valid empty result.</summary>
public struct ChunkColumnSummary
{
    public byte TopY;        // y+1 of the topmost non-air cell (0 = none)
    public byte BottomY;     // y+1 of the bottommost non-air cell
    public ushort TopId;     // block at TopY (solid or liquid)
    public byte WaterDepth;  // contiguous water run starting at TopY (0 = top is not water)
    public byte RainY;       // y+1 of the first rain-impermeable block (0 = none)
    public ushort RainId;    // block at RainY
}

/// <summary>
/// Decoder of the VS chunk block array following the inverse of Vintagestory.Common/Compression.DecompressCombined
/// (decoded from the VintagestoryLib client library — the source of truth, Q2).
/// A chunk = 32×32×32 = 32768 cells; the block id is a ushort.
/// </summary>
public static class ChunkBlockDecoder
{
    public const int CellsPerChunk = 32 * 32 * 32; // 32768
    private const int ColumnsPerChunk = 1024;
    private const int PlaneBytes = 4096;           // 1024 int32 words
    private const int MaxPaletteEntries = ushort.MaxValue + 1; // block ids are ushort; 65,536 int32 = 256 KiB

    /// <summary>Returns the chunk's ushort blocks (32768) from the blob of the `chunk` table.</summary>
    public static ushort[] DecodeChunkBlocks(byte[] chunkBlob)
    {
        var blocksCompressed = PbReader.FindField(chunkBlob, 1);
        if (blocksCompressed.Length == 0)
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
        var liq = PbReader.FindField(chunkBlob, 16);
        if (liq.Length == 0) return null;
        try { return DecodeBlocksCompressed(liq); }
        catch { return null; }
    }

    /// <summary>FULL contents of a chunk (32768) in ONE top-level pass: solids (field 1)
    /// + liquids (field 16), where there is no solid block (0) the liquid is put in. In VS liquids are stored
    /// separately; without merging, water is lost. The liquid field is decoded straight into the gaps of the
    /// solid result instead of a second 64 KiB array.</summary>
    public static ushort[] DecodeChunkBlocksMergedOnePass(byte[] chunkBlob)
    {
        ReadOnlySpan<byte> solids = default, fluids = default;
        var r = new PbSpanReader(chunkBlob, maxFields: 5000);
        while (r.MoveNext())
        {
            if (r.WireType != 2) continue;
            if (r.Field == 1) solids = r.Bytes;
            else if (r.Field == 16) fluids = r.Bytes;
        }
        var result = new ushort[CellsPerChunk];
        if (solids.Length > 0) DecodeBlocksCompressed(solids, result);
        if (fluids.Length > 0)
        {
            try { DecodeBlocksCompressed(fluids, result, onlyEmpty: true); }
            catch { /* a damaged liquid field means "no liquids", not a failed chunk */ }
        }
        return result;
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
        var comp = PbReader.FindField(chunkBlob, field);
        if (comp.Length < 4) return Array.Empty<int>();
        return ReadPalette(comp, out _);
    }

    /// <summary>Number of block palette entries of the chunk blob (following DecodeBlocksCompressed).</summary>
    public static int DecodePaletteSize(byte[] chunkBlob)
    {
        var blocksCompressed = PbReader.FindField(chunkBlob, 1);
        if (blocksCompressed.Length < 4) return 0;
        return ReadPalette(blocksCompressed, out _).Length;
    }

    /// <summary>Bits needed for a palette of n entries: ceil(log2(n)) (for n>0).</summary>
    public static int BitsNeeded(int n) => n <= 1 ? 1 : (int)Math.Ceiling(Math.Log(n, 2));

    /// <summary>How many bit planes actually sit in the chunk data (after decompression).</summary>
    public static int CountPlanesInData(byte[] chunkBlob)
    {
        var bc = PbReader.FindField(chunkBlob, 1);
        if (bc.Length < 4) return 0;
        int num = BinaryPrimitives.ReadInt32LittleEndian(bc);
        if (num == 0) return 0;
        int dataOffset = num < 0 ? 4 + (-num) : 4 + num;
        if (dataOffset < 0 || dataOffset > bc.Length) return 0;
        var planes = ZstdDecompressSpan(bc, dataOffset, bc.Length - dataOffset);
        return planes.Length / PlaneBytes;
    }

    /// <summary>Direct decoding of the chunk's blocks field (field1).</summary>
    public static ushort[] DecodeBlocksCompressed(byte[] b) => DecodeBlocksCompressed((ReadOnlySpan<byte>)b);

    public static ushort[] DecodeBlocksCompressed(ReadOnlySpan<byte> b)
    {
        var result = new ushort[CellsPerChunk];
        DecodeBlocksCompressed(b, result);
        return result;
    }

    /// <summary>
    /// Span version of the block-field decoder. <paramref name="onlyEmpty"/> writes only cells that are
    /// still zero — that is how the liquid field is merged into the solid result without an extra array.
    /// </summary>
    public static void DecodeBlocksCompressed(ReadOnlySpan<byte> b, Span<ushort> output, bool onlyEmpty = false)
    {
        // Truncated blob: the int32 marker does not fit — this is air, not an OOB.
        if (b.Length < 4)
        {
            if (!onlyEmpty) output.Clear();
            return;
        }
        // the first int32 (le) = the palette size marker
        if (BinaryPrimitives.ReadInt32LittleEndian(b) == 0)
        {
            if (!onlyEmpty) output.Clear();
            return;
        }
        var palette = _decodePalette ??= new int[MaxPaletteEntries];
        int palCount = ReadPaletteInto(b, Decoder1, palette, out int dataOffset);
        if (palCount <= 1)
        {
            if (!onlyEmpty) output.Clear();
            return;
        }

        // IMPORTANT: the number of bits = ceil(log2(palette.Length)), NOT floor. The data holds ceil bit planes;
        // with floor the highest plane is lost and blocks with high palette indices decode incorrectly.
        int bits = BitsNeeded(palCount);
        var planes = ZstdDecompressSpan(b, dataOffset, b.Length - dataOffset);
        var pal = palette.AsSpan(0, palCount);
        for (int idx = 0; idx < CellsPerChunk; idx++)
        {
            if (onlyEmpty && output[idx] != 0) continue;
            output[idx] = CellId(planes, bits, pal, idx);
        }
    }

    /// <summary>
    /// Fused decode for the terrain map: computes top/bottom/top block/water/rain per column
    /// without materialising the 32 768-cell chunk array or copying the bit planes into int[bits][1024].
    /// <paramref name="isWater"/> and <paramref name="rainPerm"/> are indexed by block id; an id outside
    /// the table is treated as not-water and rain-permeable (the same rule as the old per-code path).
    /// The optional switches skip the per-column water run / bottom / rain-surface scan when the selected
    /// render mode does not read that field (a blocks map never reads the water column).
    /// </summary>
    public static void DecodeChunkSummary(byte[] chunkBlob, Span<ChunkColumnSummary> dst,
        ReadOnlySpan<bool> isWater, ReadOnlySpan<bool> rainPerm,
        bool needWater = true, bool needBottom = true, bool needRain = true)
    {
        if (dst.Length < ColumnsPerChunk) throw new ArgumentException("summary buffer must hold 1024 columns", nameof(dst));
        var target = dst[..ColumnsPerChunk];
        target.Clear();

        ReadOnlySpan<byte> solids = default, fluids = default;
        var r = new PbSpanReader(chunkBlob, maxFields: 5000);
        while (r.MoveNext())
        {
            if (r.WireType != 2) continue;
            if (r.Field == 1) solids = r.Bytes;
            else if (r.Field == 16) fluids = r.Bytes;
        }
        if (solids.Length == 0 && fluids.Length == 0) return;

        var palS = _paletteSolids ??= new int[MaxPaletteEntries];
        var palF = _paletteFluids ??= new int[MaxPaletteEntries];
        int ns = 0, nf = 0;
        ReadOnlySpan<byte> planesS = default, planesF = default;
        if (solids.Length > 0)
        {
            var dec = Decoder1;
            ns = ReadPaletteInto(solids, dec, palS, out int off);
            if (ns > 1) planesS = ZstdDecompressSpan(solids, off, solids.Length - off, dec);
        }
        if (fluids.Length > 0)
        {
            var dec = Decoder2;
            nf = ReadPaletteInto(fluids, dec, palF, out int off);
            if (nf > 1) planesF = ZstdDecompressSpan(fluids, off, fluids.Length - off, dec);
        }
        if (ns <= 1 && nf <= 1) return;

        // The fast path needs a palette where only index 0 maps to air; then "any bit set" is exactly
        // "non-air" and top/bottom can be found with one OR per 32-cell word instead of decoding every cell.
        // A palette with a zero at another index (or at index 0 itself) falls back to the full scan.
        bool simple = SimplePalette(palS, ns) && SimplePalette(palF, nf);
        if (!simple)
        {
            DecodeChunkSummaryFull(target, palS, ns, planesS, palF, nf, planesF, isWater, rainPerm,
                needWater, needBottom, needRain);
            return;
        }

        var top = _summaryTop ??= new byte[ColumnsPerChunk];
        var bottom = needBottom ? (_summaryBottom ??= new byte[ColumnsPerChunk]) : null;
        top.AsSpan().Clear();
        bottom?.AsSpan().Clear();
        int bs = ns > 1 ? BitsNeeded(ns) : 0, bf = nf > 1 ? BitsNeeded(nf) : 0;

        // top/bottom from word masks: word = z + y*32, bit = x. Iterating only set bits keeps the
        // common all-solid case at 32 cheap iterations per word instead of 32 × bits cell decodes.
        for (int y = 31; y >= 0; y--)
        {
            for (int z = 0; z < 32; z++)
            {
                int word = z + y * 32;
                uint mask = 0;
                if (ns > 1) for (int p = 0; p < bs; p++) mask |= PlaneWord(planesS, p, word);
                if (nf > 1) for (int p = 0; p < bf; p++) mask |= PlaneWord(planesF, p, word);
                if (mask == 0) continue;
                int zBase = z * 32;
                byte yy = (byte)(y + 1);
                while (mask != 0)
                {
                    int x = BitOperations.TrailingZeroCount(mask);
                    int col = x + zBase;
                    if (top[col] == 0) top[col] = yy;
                    if (bottom is not null) bottom[col] = yy;
                    mask &= mask - 1;
                }
            }
        }

        var ps = palS.AsSpan(0, Math.Max(0, ns));
        var pf = palF.AsSpan(0, Math.Max(0, nf));
        for (int col = 0; col < ColumnsPerChunk; col++)
        {
            int t = top[col];
            if (t == 0) continue;
            int topY = t - 1;
            int idxTop = col + topY * 1024;
            ushort topId = CellIdMerged(planesS, bs, ps, planesF, bf, pf, idxTop);
            if (topId == 0)
            {
                // A palette duplicate or a malformed plane made the mask optimistic; the full scan is exact.
                DecodeChunkSummaryFull(target, palS, ns, planesS, palF, nf, planesF, isWater, rainPerm,
                    needWater, needBottom, needRain);
                return;
            }

            int water = 0;
            if (needWater && Flag(isWater, topId))
            {
                water = 1;
                for (int y = topY - 1; y >= 0; y--)
                {
                    ushort id = CellIdMerged(planesS, bs, ps, planesF, bf, pf, col + y * 1024);
                    if (id == 0 || !Flag(isWater, id)) break;
                    water++;
                }
            }

            int rainY = 0;
            ushort rainId = 0;
            if (needRain) for (int y = topY; y >= 0; y--)
            {
                ushort id = CellIdMerged(planesS, bs, ps, planesF, bf, pf, col + y * 1024);
                if (id != 0 && id < rainPerm.Length && !rainPerm[id])
                {
                    rainY = y + 1;
                    rainId = id;
                    break;
                }
            }

            target[col] = new ChunkColumnSummary
            {
                TopY = (byte)t,
                BottomY = bottom is null ? (byte)0 : bottom[col],
                TopId = topId,
                WaterDepth = (byte)water,
                RainY = (byte)rainY,
                RainId = rainId
            };
        }
    }

    private static bool SimplePalette(int[] pal, int count)
    {
        if (count <= 1) return true;          // no cells: the whole field decodes to air
        if (pal[0] != 0) return false;
        for (int i = 1; i < count; i++) if (pal[i] == 0) return false;
        return true;
    }

    private static uint PlaneWord(ReadOnlySpan<byte> planes, int plane, int word)
    {
        int off = plane * PlaneBytes + word * 4;
        return off <= planes.Length - 4 ? (uint)BinaryPrimitives.ReadInt32LittleEndian(planes.Slice(off, 4)) : 0u;
    }

    private static ushort CellIdMerged(ReadOnlySpan<byte> planesS, int bs, ReadOnlySpan<int> ps,
        ReadOnlySpan<byte> planesF, int bf, ReadOnlySpan<int> pf, int idx)
    {
        ushort id = bs > 0 ? CellId(planesS, bs, ps, idx) : (ushort)0;
        if (id == 0 && bf > 0) id = CellId(planesF, bf, pf, idx);
        return id;
    }

    /// <summary>Exact per-cell scan — the fallback for palettes the word-mask shortcut cannot represent.</summary>
    private static void DecodeChunkSummaryFull(Span<ChunkColumnSummary> target, int[] palS, int ns,
        ReadOnlySpan<byte> planesS, int[] palF, int nf, ReadOnlySpan<byte> planesF,
        ReadOnlySpan<bool> isWater, ReadOnlySpan<bool> rainPerm,
        bool needWater = true, bool needBottom = true, bool needRain = true)
    {
        target.Clear();   // the fallback can follow a partially filled fast path
        int bs = ns > 1 ? BitsNeeded(ns) : 0, bf = nf > 1 ? BitsNeeded(nf) : 0;
        var ps = palS.AsSpan(0, Math.Max(0, ns));
        var pf = palF.AsSpan(0, Math.Max(0, nf));
        for (int z = 0; z < 32; z++)
        {
            int zBase = z * 32;
            for (int x = 0; x < 32; x++)
            {
                int col = x + zBase;
                int top = 0, bottom = 0, water = 0, rainY = 0;
                ushort topId = 0, rainId = 0;
                bool waterRun = false;
                for (int y = 31; y >= 0; y--)
                {
                    ushort id = CellIdMerged(planesS, bs, ps, planesF, bf, pf, col + y * 1024);
                    if (id == 0)
                    {
                        waterRun = false;
                        continue;
                    }
                    if (top == 0)
                    {
                        top = y + 1;
                        topId = id;
                        if (needWater && Flag(isWater, id)) { water = 1; waterRun = true; }
                    }
                    else if (needWater && waterRun)
                    {
                        if (Flag(isWater, id)) water++;
                        else waterRun = false;
                    }
                    if (needBottom) bottom = y + 1;
                    if (needRain && rainY == 0 && id < rainPerm.Length && !rainPerm[id])
                    {
                        rainY = y + 1;
                        rainId = id;
                    }
                }
                if (top == 0) continue;
                target[col] = new ChunkColumnSummary
                {
                    TopY = (byte)top,
                    BottomY = (byte)bottom,
                    TopId = topId,
                    WaterDepth = (byte)water,
                    RainY = (byte)rainY,
                    RainId = rainId
                };
            }
        }
    }

    /// <summary>
    /// Fused histogram decode for the save scan: counts solid/fluid/air cells and block ids in one pass,
    /// without materialising the chunk array. <paramref name="touched"/> lists the ids touched in this chunk;
    /// the caller must reset those histogram slots afterwards (Clear on a 65 536-long array per chunk is
    /// more expensive than the decode itself).
    /// </summary>
    public static void DecodeChunkHistogram(byte[] chunkBlob, long[] hist, List<int> touched,
        out long solid, out long fluid, out long air)
    {
        if (hist.Length < ushort.MaxValue + 1) throw new ArgumentException("histogram must cover 65536 ids", nameof(hist));
        solid = fluid = air = 0;

        ReadOnlySpan<byte> solids = default, fluids = default;
        var r = new PbSpanReader(chunkBlob, maxFields: 5000);
        while (r.MoveNext())
        {
            if (r.WireType != 2) continue;
            if (r.Field == 1) solids = r.Bytes;
            else if (r.Field == 16) fluids = r.Bytes;
        }
        if (solids.Length == 0 && fluids.Length == 0)
        {
            air = CellsPerChunk;
            return;
        }

        var palS = _paletteSolids ??= new int[MaxPaletteEntries];
        var palF = _paletteFluids ??= new int[MaxPaletteEntries];
        int ns = 0, nf = 0;
        ReadOnlySpan<byte> planesS = default, planesF = default;
        if (solids.Length > 0)
        {
            var dec = Decoder1;
            ns = ReadPaletteInto(solids, dec, palS, out int off);
            if (ns > 1) planesS = ZstdDecompressSpan(solids, off, solids.Length - off, dec);
        }
        if (fluids.Length > 0)
        {
            var dec = Decoder2;
            nf = ReadPaletteInto(fluids, dec, palF, out int off);
            if (nf > 1) planesF = ZstdDecompressSpan(fluids, off, fluids.Length - off, dec);
        }
        int bs = ns > 1 ? BitsNeeded(ns) : 0, bf = nf > 1 ? BitsNeeded(nf) : 0;
        var ps = palS.AsSpan(0, Math.Max(0, ns));
        var pf = palF.AsSpan(0, Math.Max(0, nf));

        for (int idx = 0; idx < CellsPerChunk; idx++)
        {
            ushort id = ns > 1 ? CellId(planesS, bs, ps, idx) : (ushort)0;
            if (id != 0)
            {
                solid++;
                if (hist[id]++ == 0) touched.Add(id);
                continue;
            }
            id = nf > 1 ? CellId(planesF, bf, pf, idx) : (ushort)0;
            if (id != 0)
            {
                fluid++;
                if (hist[id]++ == 0) touched.Add(id);
            }
            else air++;
        }
    }

    /// <summary>Palette of the blocks field: the num marker (int32 le): &lt;0 — raw int32, &gt;0 — zstd-compressed int32.</summary>
    private static int[] ReadPalette(ReadOnlySpan<byte> b, out int dataOffset)
    {
        var buf = _paletteRead ??= new int[MaxPaletteEntries];
        int n = ReadPaletteInto(b, Decoder1, buf, out dataOffset);
        if (n <= 0) return Array.Empty<int>();
        var res = new int[n];
        Array.Copy(buf, res, n);
        return res;
    }

    /// <summary>Reads the palette into a caller-provided buffer; returns the entry count (0 = empty/invalid).</summary>
    private static int ReadPaletteInto(ReadOnlySpan<byte> b, ZstdSharp.Decompressor dec, int[] dst, out int dataOffset)
    {
        dataOffset = 0;
        if (b.Length < 4) return 0;
        int num = BinaryPrimitives.ReadInt32LittleEndian(b);
        if (num == 0) { dataOffset = 4; return 0; }
        if (num < 0)
        {
            if (num == int.MinValue) return 0;
            int bytes = -num;
            if (bytes > b.Length - 4) return 0;
            dataOffset = 4 + bytes;
            int n = bytes / 4;
            if (n <= 1 || n > dst.Length) return 0;
            for (int i = 0; i < n; i++)
                dst[i] = BinaryPrimitives.ReadInt32LittleEndian(b.Slice(4 + i * 4, 4));
            return n;
        }
        if (num > b.Length - 4) return 0;
        dataOffset = 4 + num;
        var palBytes = ZstdDecompressSpan(b, 4, num, dec);
        int count = palBytes.Length / 4;
        if (count <= 1 || count > dst.Length) return 0;
        for (int i = 0; i < count; i++)
            dst[i] = BinaryPrimitives.ReadInt32LittleEndian(palBytes.Slice(i * 4, 4));
        return count;
    }

    /// <summary>Decodes one cell from the bit planes. The planes are read in place: no int[bits][1024] copy.
    /// A missing tail word reads as 0, exactly like the previous Buffer.BlockCopy-based decoder.</summary>
    private static ushort CellId(ReadOnlySpan<byte> planes, int bits, ReadOnlySpan<int> palette, int idx)
    {
        int word = idx >> 5, bit = idx & 31;
        int val = 0;
        for (int p = 0; p < bits; p++)
        {
            int off = p * PlaneBytes + word * 4;
            int w = off <= planes.Length - 4 ? BinaryPrimitives.ReadInt32LittleEndian(planes.Slice(off, 4)) : 0;
            if (((w >> bit) & 1) != 0) val |= 1 << p;
        }
        return val < palette.Length ? (ushort)palette[val] : (ushort)0;
    }

    private static bool Flag(ReadOnlySpan<bool> table, ushort id) => id < table.Length && table[id];

    // A reusable decoder context per thread: DecompressionStream created a new context
    // on every call (and there are 2–3 per chunk). Decompressor is not thread-safe, so the cache is tied
    // to the thread — decoding runs in Parallel.For and every worker has its own context.
    // Decoder2 is only for the liquid field of DecodeChunkSummary/Histogram: both fields' planes must stay
    // alive at the same time, and Unwrap returns a span into the context's internal buffer.
    [ThreadStatic] private static ZstdSharp.Decompressor? _zstdDec;
    [ThreadStatic] private static ZstdSharp.Decompressor? _zstdDec2;
    [ThreadStatic] private static byte[]? _zstdBuf1;
    [ThreadStatic] private static byte[]? _zstdBuf2;
    private static ZstdSharp.Decompressor Decoder1 => _zstdDec ??= new ZstdSharp.Decompressor();
    private static ZstdSharp.Decompressor Decoder2 => _zstdDec2 ??= new ZstdSharp.Decompressor();

    [ThreadStatic] private static int[]? _decodePalette;
    [ThreadStatic] private static int[]? _paletteRead;
    [ThreadStatic] private static int[]? _paletteSolids;
    [ThreadStatic] private static int[]? _paletteFluids;
    [ThreadStatic] private static byte[]? _summaryTop;
    [ThreadStatic] private static byte[]? _summaryBottom;

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
        => ZstdDecompressSpan(b, off, len, Decoder1).ToArray();

    /// <summary>Zero-copy ZSTD decompression into the context's internal buffer. The span is valid only
    /// until the next Unwrap on the SAME context — callers that need two fields alive use Decoder1/Decoder2.</summary>
    private static ReadOnlySpan<byte> ZstdDecompressSpan(ReadOnlySpan<byte> b, int off, int len)
        => ZstdDecompressSpan(b, off, len, Decoder1);

    private static ReadOnlySpan<byte> ZstdDecompressSpan(ReadOnlySpan<byte> b, int off, int len,
        ZstdSharp.Decompressor dec)
    {
        if (len <= 0 || off < 0 || off > b.Length - len) return default;
        // Unwrap(src, max) allocates a new output array on every call (~10 KiB per chunk on the large
        // save, 1.2 GiB for the whole map). Unwrap(src, span) writes into a per-thread buffer instead.
        // Two buffers: the solids and liquids planes must stay alive at the same time in the fused decoders.
        byte[]? buf = null;
        if (ReferenceEquals(dec, _zstdDec)) buf = _zstdBuf1 ??= new byte[ZstdMaxOutput];
        else if (ReferenceEquals(dec, _zstdDec2)) buf = _zstdBuf2 ??= new byte[ZstdMaxOutput];
        if (buf is null)
        {
            try { return dec.Unwrap(b.Slice(off, len), ZstdMaxOutput).ToArray(); }
            catch (ZstdSharp.ZstdException) { return default; }
        }
        try
        {
            int n = dec.Unwrap(b.Slice(off, len), buf);
            return new ReadOnlySpan<byte>(buf, 0, n);
        }
        catch (ZstdSharp.ZstdException)
        {
            // A "bomb" (declared output above ZstdMaxOutput) or a corrupted frame. The exception is thrown
            // BEFORE the output memory is allocated, so it costs no memory; the field is simply treated
            // as absent. We reset the context: the failed frame may have left it in a garbage
            // state, while the next chunk on the same thread has to decode normally.
            if (ReferenceEquals(dec, _zstdDec)) _zstdDec = null;
            else if (ReferenceEquals(dec, _zstdDec2)) _zstdDec2 = null;
            return default;
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
