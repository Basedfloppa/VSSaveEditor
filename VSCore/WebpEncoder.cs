namespace VSCore;

/// <summary>
/// Minimal managed WebP lossless encoder (VP8L format) with no native dependencies.
/// Only literal pixels are used (no LZ77, transformations or colour cache), but with
/// real canonical Huffman codes built from the frequencies — thanks to that a map with a solid
/// background compresses well. Compatible with libwebp (verified by decoding).
/// </summary>
public static class WebpEncoder
{
    private const int NumLiteralCodes = 256;
    private const int NumLengthCodes = 24;
    private const int GreenAlphabet = NumLiteralCodes + NumLengthCodes;   // 280
    private const int DistanceAlphabet = 40;
    private const int MaxCodeLength = 15;

    private static readonly int[] CodeLengthCodeOrder =
    {
        17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15
    };

    /// <summary>Encodes RGBA (width*height*4) into lossless WebP (VP8L). Alpha is preserved.</summary>
    public static byte[] EncodeLossless(int width, int height, byte[] rgba)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("empty image");
        if (width > 16384 || height > 16384)
            throw new InvalidOperationException($"WebP too large: {width}x{height} (max 16384 per side)");
        if (rgba.Length != (long)width * height * 4) throw new ArgumentException("rgba length mismatch");

        int n = width * height;
        var freqR = new long[NumLiteralCodes];
        var freqG = new long[GreenAlphabet];
        var freqB = new long[NumLiteralCodes];
        var freqA = new long[NumLiteralCodes];
        bool hasAlpha = false;
        for (int i = 0; i < n; i++)
        {
            int p = i * 4;
            byte r = rgba[p], g = rgba[p + 1], b = rgba[p + 2], a = rgba[p + 3];
            freqR[r]++; freqG[g]++; freqB[b]++; freqA[a]++;
            if (a != 255) hasAlpha = true;
        }
        var freqD = new long[DistanceAlphabet];   // LZ77 is not used — the distance code stays a dummy

        var bw = new Vp8lBitWriter();
        bw.WriteBits(0x2F, 8);                    // VP8L signature
        bw.WriteBits(width - 1, 14);
        bw.WriteBits(height - 1, 14);
        bw.WriteBits(hasAlpha ? 1 : 0, 1);
        bw.WriteBits(0, 3);                       // version
        bw.WriteBits(0, 1);                       // no transformations
        bw.WriteBits(0, 1);                       // no colour cache
        bw.WriteBits(0, 1);                       // no meta-Huffman (a single code group)

        // 5 codes: green(+length), red, blue, alpha, distance
        var hg = WriteCodeDefinition(bw, freqG, GreenAlphabet);
        var hr = WriteCodeDefinition(bw, freqR, NumLiteralCodes);
        var hb = WriteCodeDefinition(bw, freqB, NumLiteralCodes);
        var ha = WriteCodeDefinition(bw, freqA, NumLiteralCodes);
        WriteCodeDefinition(bw, freqD, DistanceAlphabet);   // distance is unused with literal pixels

        for (int i = 0; i < n; i++)
        {
            int p = i * 4;
            bw.WriteBits(hg.Codes[rgba[p + 1]], hg.Bits[rgba[p + 1]]);
            bw.WriteBits(hr.Codes[rgba[p]], hr.Bits[rgba[p]]);
            bw.WriteBits(hb.Codes[rgba[p + 2]], hb.Bits[rgba[p + 2]]);
            bw.WriteBits(ha.Codes[rgba[p + 3]], ha.Bits[rgba[p + 3]]);
        }

        return WrapRiff(bw.Flush());
    }

    private readonly struct HuffCode
    {
        public readonly int[] Bits;    // bits per symbol (0 = a single-symbol code, the decoder reads 0 bits)
        public readonly int[] Codes;
        public HuffCode(int[] bits, int[] codes) { Bits = bits; Codes = codes; }
    }

    /// <summary>Writes the Huffman code definition into the bitstream and returns the table for encoding pixels.</summary>
    private static HuffCode WriteCodeDefinition(Vp8lBitWriter bw, long[] freq, int size)
    {
        int used = 0, single = -1;
        for (int i = 0; i < size; i++) if (freq[i] > 0) { used++; single = i; }

        if (used <= 1)
        {
            // a simple code with one symbol: the decoder treats it as 0-bit
            int sym = used == 1 ? single : 0;
            bw.WriteBits(1, 1);                   // simple code
            bw.WriteBits(0, 1);                   // num_symbols = 1
            bool wide = sym > 1;
            bw.WriteBits(wide ? 1 : 0, 1);        // bit width of the first symbol
            bw.WriteBits(sym, wide ? 8 : 1);
            var b0 = new int[size];
            var c0 = new int[size];
            b0[sym] = 0;
            return new HuffCode(b0, c0);
        }

        var lengths = HuffmanLengths(freq, size, MaxCodeLength);

        // meta-code over the length values (19-symbol alphabet, lengths ≤ 7 since they are written in 3 bits)
        var metaFreq = new long[19];
        for (int i = 0; i < size; i++) metaFreq[lengths[i]]++;
        var metaLengths = HuffmanLengths(metaFreq, 19, 7);
        var metaCodes = AssignCodes(metaLengths, 19);
        int metaUsed = 0, metaSingle = -1;
        for (int i = 0; i < 19; i++) if (metaFreq[i] > 0) { metaUsed++; metaSingle = i; }
        var metaBits = (int[])metaLengths.Clone();
        if (metaUsed <= 1) metaBits[metaSingle] = 0;   // a single symbol -> the decoder reads 0 bits

        bw.WriteBits(0, 1);                       // not a simple code
        bw.WriteBits(19 - 4, 4);                  // num_codes = 19
        for (int i = 0; i < 19; i++) bw.WriteBits(metaLengths[CodeLengthCodeOrder[i]], 3);
        bw.WriteBits(0, 1);                       // use_length = 0
        for (int s = 0; s < size; s++) bw.WriteBits(metaCodes[lengths[s]], metaBits[lengths[s]]);

        return new HuffCode(lengths, AssignCodes(lengths, size));
    }

    /// <summary>Lengths of a COMPLETE Huffman code (Kraft == 1) fitted into maxLen.
    /// libwebp accepts only complete codes, so when the depth is too large the frequencies are
    /// "squeezed" (halved) until the tree fits the limit — the code stays complete.</summary>
    private static int[] HuffmanLengths(long[] freq, int size, int maxLen)
    {
        var len = new int[size];
        int used = 0;
        for (int i = 0; i < size; i++) if (freq[i] > 0) used++;
        if (used == 0) { len[0] = 1; return len; }
        if (used == 1) { for (int i = 0; i < size; i++) if (freq[i] > 0) len[i] = 1; return len; }

        var f = (long[])freq.Clone();
        for (int attempt = 0; attempt < 64; attempt++)
        {
            len = HuffmanLengthsUnbounded(f, size);
            int max = 0;
            for (int i = 0; i < size; i++) if (len[i] > max) max = len[i];
            if (max <= maxLen) return len;
            for (int i = 0; i < size; i++) if (f[i] > 1) f[i] = (f[i] + 1) / 2;
        }
        return len;   // unreachable: with equal frequencies the depth is ≤ ceil(log2(used)) ≤ 15
    }

    /// <summary>Plain Huffman without a depth limit (always yields a complete code).</summary>
    private static int[] HuffmanLengthsUnbounded(long[] freq, int size)
    {
        var used = new List<int>();
        for (int i = 0; i < size; i++) if (freq[i] > 0) used.Add(i);
        var len = new int[size];
        int m = used.Count;
        var nodeFreq = new long[2 * m];
        var parent = new int[2 * m];
        var pq = new PriorityQueue<int, long>();
        for (int i = 0; i < m; i++)
        {
            nodeFreq[i] = freq[used[i]];
            parent[i] = -1;
            pq.Enqueue(i, nodeFreq[i]);
        }
        int next = m;
        while (pq.Count > 1)
        {
            int a = pq.Dequeue();
            int b = pq.Dequeue();
            nodeFreq[next] = nodeFreq[a] + nodeFreq[b];
            parent[a] = next; parent[b] = next; parent[next] = -1;
            pq.Enqueue(next, nodeFreq[next]);
            next++;
        }
        int root = pq.Dequeue();
        for (int i = 0; i < m; i++)
        {
            int d = 0, cur = i;
            while (cur != root) { cur = parent[cur]; d++; }
            len[used[i]] = Math.Max(1, d);
        }
        return len;
    }

    /// <summary>Canonical Huffman codes from the lengths. The bits are reversed because VP8L
    /// writes the stream LSB-first, while the code must start from the highest bit (as in libwebp: ReverseBits).</summary>
    private static int[] AssignCodes(int[] lengths, int size)
    {
        var count = new int[MaxCodeLength + 1];
        for (int i = 0; i < size; i++) if (lengths[i] > 0) count[lengths[i]]++;
        var next = new int[MaxCodeLength + 1];
        int code = 0;
        for (int l = 1; l <= MaxCodeLength; l++) { code = (code + count[l - 1]) << 1; next[l] = code; }
        var codes = new int[size];
        for (int i = 0; i < size; i++)
            if (lengths[i] > 0) codes[i] = ReverseBits(next[lengths[i]]++, lengths[i]);
        return codes;
    }

    private static int ReverseBits(int value, int bits)
    {
        int r = 0;
        for (int i = 0; i < bits; i++) { r = (r << 1) | (value & 1); value >>= 1; }
        return r;
    }

    private static byte[] WrapRiff(byte[] payload)
    {
        int pad = payload.Length & 1;
        using var ms = new MemoryStream();
        void Ascii(string s) => ms.Write(System.Text.Encoding.ASCII.GetBytes(s));
        void U32(int v) { ms.WriteByte((byte)v); ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)(v >> 16)); ms.WriteByte((byte)(v >> 24)); }
        Ascii("RIFF");
        U32(4 + 8 + payload.Length + pad);
        Ascii("WEBP");
        Ascii("VP8L");
        U32(payload.Length);
        ms.Write(payload);
        if (pad == 1) ms.WriteByte(0);
        return ms.ToArray();
    }

    /// <summary>VP8L bit stream: bits are written from the lowest to the highest.</summary>
    private sealed class Vp8lBitWriter
    {
        private readonly MemoryStream _ms = new();
        private int _acc, _n;
        public void WriteBits(int value, int bits)
        {
            for (int i = 0; i < bits; i++)
            {
                _acc |= ((value >> i) & 1) << _n;
                if (++_n == 8) { _ms.WriteByte((byte)_acc); _acc = 0; _n = 0; }
            }
        }
        public byte[] Flush()
        {
            if (_n > 0) { _ms.WriteByte((byte)_acc); _acc = 0; _n = 0; }
            return _ms.ToArray();
        }
    }
}
