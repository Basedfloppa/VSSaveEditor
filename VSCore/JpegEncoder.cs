namespace VSCore;

/// <summary>
/// Minimal managed JPEG encoder (baseline, sequential, 4:4:4) with the standard quantization
/// and Huffman tables (JPEG Annex K). No native dependencies.
/// JPEG stores no alpha — the RGBA is composited onto a matte colour.
/// </summary>
public static class JpegEncoder
{
    /// <summary>Encodes RGBA (width*height*4) into baseline JPEG. quality: 1..100.</summary>
    public static byte[] Encode(int width, int height, byte[] rgba, int quality = 90, (byte R, byte G, byte B) matte = default)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("empty image");
        if (width > 65535 || height > 65535) throw new InvalidOperationException($"JPEG too large: {width}x{height} (max 65535)");
        if (rgba.Length != (long)width * height * 4) throw new ArgumentException("rgba length mismatch");
        quality = Math.Clamp(quality, 1, 100);

        var qY = ScaleQuant(LumaQuant, quality);
        var qC = ScaleQuant(ChromaQuant, quality);

        var ms = new MemoryStream();
        WriteMarker(ms, 0xD8);                       // SOI
        WriteApp0(ms);
        WriteDqt(ms, 0, qY);
        WriteDqt(ms, 1, qC);
        WriteSof0(ms, width, height);
        WriteDht(ms, 0x00, DcLumaBits, DcLumaValues);
        WriteDht(ms, 0x10, AcLumaBits, AcLumaValues);
        WriteDht(ms, 0x01, DcChromaBits, DcChromaValues);
        WriteDht(ms, 0x11, AcChromaBits, AcChromaValues);
        WriteSos(ms);

        var bw = new JpegBitWriter();
        int mcusX = (width + 7) / 8, mcusY = (height + 7) / 8;
        int[] block = new int[64];
        int[] tmp = new int[64];
        int[] coef = new int[64];
        int predY = 0, predCb = 0, predCr = 0;

        for (int my = 0; my < mcusY; my++)
            for (int mx = 0; mx < mcusX; mx++)
            {
                EncodePlane(rgba, width, height, mx, my, 0, matte, qY, block, tmp, coef, bw, ref predY, DcLumaCodes, DcLumaSizes, AcLumaCodes, AcLumaSizes);
                EncodePlane(rgba, width, height, mx, my, 1, matte, qC, block, tmp, coef, bw, ref predCb, DcChromaCodes, DcChromaSizes, AcChromaCodes, AcChromaSizes);
                EncodePlane(rgba, width, height, mx, my, 2, matte, qC, block, tmp, coef, bw, ref predCr, DcChromaCodes, DcChromaSizes, AcChromaCodes, AcChromaSizes);
            }

        bw.Flush();
        ms.Write(bw.ToArray());
        WriteMarker(ms, 0xD9);                       // EOI
        return ms.ToArray();
    }

    // component: 0 = Y, 1 = Cb, 2 = Cr
    private static void EncodePlane(byte[] rgba, int width, int height, int mx, int my, int component,
        (byte R, byte G, byte B) matte, int[] quant, int[] block, int[] tmp, int[] coef, JpegBitWriter bw,
        ref int pred, int[] dcCodes, int[] dcSizes, int[] acCodes, int[] acSizes)
    {
        for (int y = 0; y < 8; y++)
        {
            int py = my * 8 + y;
            for (int x = 0; x < 8; x++)
            {
                int px = mx * 8 + x;
                int r = 0, g = 0, b = 0;
                if (px < width && py < height)
                {
                    int p = (py * width + px) * 4;
                    byte a = rgba[p + 3];
                    if (a == 255) { r = rgba[p]; g = rgba[p + 1]; b = rgba[p + 2]; }
                    else
                    {
                        r = (rgba[p] * a + matte.R * (255 - a) + 127) / 255;
                        g = (rgba[p + 1] * a + matte.G * (255 - a) + 127) / 255;
                        b = (rgba[p + 2] * a + matte.B * (255 - a) + 127) / 255;
                    }
                }
                double v;
                if (component == 0) v = 0.299 * r + 0.587 * g + 0.114 * b - 128.0;
                else if (component == 1) v = -0.168736 * r - 0.331264 * g + 0.5 * b;
                else v = 0.5 * r - 0.418688 * g - 0.081312 * b;
                block[y * 8 + x] = (int)Math.Round(v);
            }
        }

        Fdct(block, tmp, coef);

        // quantization (natural order)
        for (int i = 0; i < 64; i++) coef[i] = (int)Math.Round((double)coef[i] / quant[i]);

        // DC (differential coding)
        int diff = coef[0] - pred;
        pred = coef[0];
        int size = Category(diff);
        bw.Write(dcCodes[size], dcSizes[size]);
        if (size > 0) bw.Write(diff < 0 ? diff + (1 << size) - 1 : diff, size);

        // AC (zigzag, run-length)
        int run = 0;
        for (int k = 1; k < 64; k++)
        {
            int c = coef[ZigZag[k]];
            if (c == 0) { run++; continue; }
            while (run > 15) { bw.Write(acCodes[0xF0], acSizes[0xF0]); run -= 16; }
            int s = Category(c);
            int sym = (run << 4) | s;
            bw.Write(acCodes[sym], acSizes[sym]);
            bw.Write(c < 0 ? c + (1 << s) - 1 : c, s);
            run = 0;
        }
        if (run > 0) bw.Write(acCodes[0x00], acSizes[0x00]);   // EOB
    }

    private static int Category(int v)
    {
        int a = Math.Abs(v), n = 0;
        while (a > 0) { a >>= 1; n++; }
        return n;
    }

    // ---- fast separable DCT (cosines precomputed) ----
    private static readonly double[,] Cos = BuildCos();
    private static double[,] BuildCos()
    {
        var c = new double[8, 8];
        for (int u = 0; u < 8; u++)
            for (int x = 0; x < 8; x++)
                c[u, x] = Math.Cos((2 * x + 1) * u * Math.PI / 16.0);
        return c;
    }
    private static readonly double[] C = { 1 / Math.Sqrt(2), 1, 1, 1, 1, 1, 1, 1 };

    private static void Fdct(int[] block, int[] tmp, int[] outv)
    {
        // rows
        for (int y = 0; y < 8; y++)
            for (int u = 0; u < 8; u++)
            {
                double s = 0;
                for (int x = 0; x < 8; x++) s += block[y * 8 + x] * Cos[u, x];
                tmp[y * 8 + u] = (int)Math.Round(s * C[u] * 0.5);
            }
        // columns
        for (int u = 0; u < 8; u++)
            for (int v = 0; v < 8; v++)
            {
                double s = 0;
                for (int y = 0; y < 8; y++) s += tmp[y * 8 + u] * Cos[v, y];
                outv[v * 8 + u] = (int)Math.Round(s * C[v] * 0.5);
            }
    }

    // ---- writing segments ----
    private static void WriteMarker(Stream s, byte m) { s.WriteByte(0xFF); s.WriteByte(m); }
    private static void WriteU16(Stream s, int v) { s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
    private static byte[] Utf8(string str) => System.Text.Encoding.ASCII.GetBytes(str);

    private static void WriteApp0(Stream s)
    {
        WriteMarker(s, 0xE0);
        WriteU16(s, 16);
        s.Write(Utf8("JFIF\0"));
        s.WriteByte(1); s.WriteByte(1);       // version 1.1
        s.WriteByte(0);                       // units
        WriteU16(s, 1); WriteU16(s, 1);       // density
        s.WriteByte(0); s.WriteByte(0);       // no thumbnail
    }

    private static void WriteDqt(Stream s, int id, int[] q)
    {
        WriteMarker(s, 0xDB);
        WriteU16(s, 2 + 1 + 64);
        s.WriteByte((byte)id);                // precision 0 (8-bit) | id
        for (int k = 0; k < 64; k++) s.WriteByte((byte)q[ZigZag[k]]);
    }

    private static void WriteSof0(Stream s, int w, int h)
    {
        WriteMarker(s, 0xC0);
        WriteU16(s, 8 + 3 * 3);
        s.WriteByte(8);                       // precision
        WriteU16(s, h); WriteU16(s, w);
        s.WriteByte(3);                       // components
        s.WriteByte(1); s.WriteByte(0x11); s.WriteByte(0);   // Y: 1x1, q0
        s.WriteByte(2); s.WriteByte(0x11); s.WriteByte(1);   // Cb: 1x1, q1
        s.WriteByte(3); s.WriteByte(0x11); s.WriteByte(1);   // Cr: 1x1, q1
    }

    private static void WriteDht(Stream s, int id, byte[] bits, byte[] values)
    {
        WriteMarker(s, 0xC4);
        WriteU16(s, 2 + 1 + 16 + values.Length);
        s.WriteByte((byte)id);
        for (int i = 0; i < 16; i++) s.WriteByte(bits[i]);
        s.Write(values);
    }

    private static void WriteSos(Stream s)
    {
        WriteMarker(s, 0xDA);
        WriteU16(s, 6 + 2 * 3);
        s.WriteByte(3);
        s.WriteByte(1); s.WriteByte(0x00);    // Y: dc0/ac0
        s.WriteByte(2); s.WriteByte(0x11);    // Cb: dc1/ac1
        s.WriteByte(3); s.WriteByte(0x11);    // Cr: dc1/ac1
        s.WriteByte(0); s.WriteByte(63); s.WriteByte(0);
    }

    /// <summary>Writes bits MSB-first with 0xFF -> 0xFF 0x00 byte stuffing (a JPEG requirement).</summary>
    private sealed class JpegBitWriter
    {
        private readonly MemoryStream _ms = new();
        private int _acc, _n;
        public void Write(int value, int bits)
        {
            for (int i = bits - 1; i >= 0; i--)
            {
                _acc = (_acc << 1) | ((value >> i) & 1);
                if (++_n == 8)
                {
                    _ms.WriteByte((byte)_acc);
                    if (_acc == 0xFF) _ms.WriteByte(0x00);
                    _acc = 0; _n = 0;
                }
            }
        }
        public void Flush()
        {
            if (_n > 0)
            {
                _acc = (_acc << (8 - _n)) | ((1 << (8 - _n)) - 1);   // pad with ones
                _ms.WriteByte((byte)_acc);
                if (_acc == 0xFF) _ms.WriteByte(0x00);
                _acc = 0; _n = 0;
            }
        }
        public byte[] ToArray() => _ms.ToArray();
    }

    // ---- quantization tables (JPEG Annex K) ----
    private static readonly int[] LumaQuant =
    {
        16,11,10,16,24,40,51,61, 12,12,14,19,26,58,60,55,
        14,13,16,24,40,57,69,56, 14,17,22,29,51,87,80,62,
        18,22,37,56,68,109,103,77, 24,35,55,64,81,104,113,92,
        49,64,78,87,103,121,120,101, 72,92,95,98,112,100,103,99
    };
    private static readonly int[] ChromaQuant =
    {
        17,18,24,47,99,99,99,99, 18,21,26,66,99,99,99,99,
        24,26,56,99,99,99,99,99, 47,66,99,99,99,99,99,99,
        99,99,99,99,99,99,99,99, 99,99,99,99,99,99,99,99,
        99,99,99,99,99,99,99,99, 99,99,99,99,99,99,99,99
    };
    private static int[] ScaleQuant(int[] baseQ, int quality)
    {
        int scale = quality < 50 ? 5000 / quality : 200 - quality * 2;
        var q = new int[64];
        for (int i = 0; i < 64; i++) q[i] = Math.Clamp((baseQ[i] * scale + 50) / 100, 1, 255);
        return q;
    }

    private static readonly int[] ZigZag =
    {
         0, 1, 8,16, 9, 2, 3,10, 17,24,32,25,18,11, 4, 5,
        12,19,26,33,40,48,41,34, 27,20,13, 6, 7,14,21,28,
        35,42,49,56,57,50,43,36, 29,22,15,23,30,37,44,51,
        58,59,52,45,38,31,39,46, 53,60,61,54,47,55,62,63
    };

    // ---- standard Huffman tables (JPEG Annex K.3) ----
    private static readonly byte[] DcLumaBits = { 0,1,5,1,1,1,1,1,1,0,0,0,0,0,0,0 };
    private static readonly byte[] DcLumaValues = { 0,1,2,3,4,5,6,7,8,9,10,11 };
    private static readonly byte[] DcChromaBits = { 0,3,1,1,1,1,1,1,1,1,1,0,0,0,0,0 };
    private static readonly byte[] DcChromaValues = { 0,1,2,3,4,5,6,7,8,9,10,11 };

    private static readonly byte[] AcLumaBits = { 0,2,1,3,3,2,4,3,5,5,4,4,0,0,1,0x7d };
    private static readonly byte[] AcLumaValues =
    {
        0x01,0x02,0x03,0x00,0x04,0x11,0x05,0x12, 0x21,0x31,0x41,0x06,0x13,0x51,0x61,0x07,
        0x22,0x71,0x14,0x32,0x81,0x91,0xa1,0x08, 0x23,0x42,0xb1,0xc1,0x15,0x52,0xd1,0xf0,
        0x24,0x33,0x62,0x72,0x82,0x09,0x0a,0x16, 0x17,0x18,0x19,0x1a,0x25,0x26,0x27,0x28,
        0x29,0x2a,0x34,0x35,0x36,0x37,0x38,0x39, 0x3a,0x43,0x44,0x45,0x46,0x47,0x48,0x49,
        0x4a,0x53,0x54,0x55,0x56,0x57,0x58,0x59, 0x5a,0x63,0x64,0x65,0x66,0x67,0x68,0x69,
        0x6a,0x73,0x74,0x75,0x76,0x77,0x78,0x79, 0x7a,0x83,0x84,0x85,0x86,0x87,0x88,0x89,
        0x8a,0x92,0x93,0x94,0x95,0x96,0x97,0x98, 0x99,0x9a,0xa2,0xa3,0xa4,0xa5,0xa6,0xa7,
        0xa8,0xa9,0xaa,0xb2,0xb3,0xb4,0xb5,0xb6, 0xb7,0xb8,0xb9,0xba,0xc2,0xc3,0xc4,0xc5,
        0xc6,0xc7,0xc8,0xc9,0xca,0xd2,0xd3,0xd4, 0xd5,0xd6,0xd7,0xd8,0xd9,0xda,0xe1,0xe2,
        0xe3,0xe4,0xe5,0xe6,0xe7,0xe8,0xe9,0xea, 0xf1,0xf2,0xf3,0xf4,0xf5,0xf6,0xf7,0xf8,
        0xf9,0xfa
    };

    private static readonly byte[] AcChromaBits = { 0,2,1,2,4,4,3,4,7,5,4,4,0,1,2,0x77 };
    private static readonly byte[] AcChromaValues =
    {
        0x00,0x01,0x02,0x03,0x11,0x04,0x05,0x21, 0x31,0x06,0x12,0x41,0x51,0x07,0x61,0x71,
        0x13,0x22,0x32,0x81,0x08,0x14,0x42,0x91, 0xa1,0xb1,0xc1,0x09,0x23,0x33,0x52,0xf0,
        0x15,0x62,0x72,0xd1,0x0a,0x16,0x24,0x34, 0xe1,0x25,0xf1,0x17,0x18,0x19,0x1a,0x26,
        0x27,0x28,0x29,0x2a,0x35,0x36,0x37,0x38, 0x39,0x3a,0x43,0x44,0x45,0x46,0x47,0x48,
        0x49,0x4a,0x53,0x54,0x55,0x56,0x57,0x58, 0x59,0x5a,0x63,0x64,0x65,0x66,0x67,0x68,
        0x69,0x6a,0x73,0x74,0x75,0x76,0x77,0x78, 0x79,0x7a,0x82,0x83,0x84,0x85,0x86,0x87,
        0x88,0x89,0x8a,0x92,0x93,0x94,0x95,0x96, 0x97,0x98,0x99,0x9a,0xa2,0xa3,0xa4,0xa5,
        0xa6,0xa7,0xa8,0xa9,0xaa,0xb2,0xb3,0xb4, 0xb5,0xb6,0xb7,0xb8,0xb9,0xba,0xc2,0xc3,
        0xc4,0xc5,0xc6,0xc7,0xc8,0xc9,0xca,0xd2, 0xd3,0xd4,0xd5,0xd6,0xd7,0xd8,0xd9,0xda,
        0xe2,0xe3,0xe4,0xe5,0xe6,0xe7,0xe8,0xe9, 0xea,0xf2,0xf3,0xf4,0xf5,0xf6,0xf7,0xf8,
        0xf9,0xfa
    };

    // ---- Huffman codes from (bits, values) ----
    private static readonly int[] DcLumaCodes = BuildCodes(DcLumaBits, DcLumaValues);
    private static readonly int[] DcLumaSizes = BuildSizes(DcLumaBits, DcLumaValues);
    private static readonly int[] AcLumaCodes = BuildCodes(AcLumaBits, AcLumaValues);
    private static readonly int[] AcLumaSizes = BuildSizes(AcLumaBits, AcLumaValues);
    private static readonly int[] DcChromaCodes = BuildCodes(DcChromaBits, DcChromaValues);
    private static readonly int[] DcChromaSizes = BuildSizes(DcChromaBits, DcChromaValues);
    private static readonly int[] AcChromaCodes = BuildCodes(AcChromaBits, AcChromaValues);
    private static readonly int[] AcChromaSizes = BuildSizes(AcChromaBits, AcChromaValues);

    private static int[] BuildCodes(byte[] bits, byte[] values)
    {
        var sizes = BuildSizes(bits, values);
        var codes = new int[256];
        int code = 0, k = 0;
        for (int len = 1; len <= 16; len++)
        {
            for (int i = 0; i < bits[len - 1]; i++)
            {
                int sym = values[k++];
                codes[sym] = code++;
            }
            code <<= 1;
        }
        // (sizes already hold the length; codes are the canonical codes)
        for (int i = 0; i < 256; i++) if (sizes[i] == 0) codes[i] = 0;
        return codes;
    }

    private static int[] BuildSizes(byte[] bits, byte[] values)
    {
        var sizes = new int[256];
        int k = 0;
        for (int len = 1; len <= 16; len++)
            for (int i = 0; i < bits[len - 1]; i++) sizes[values[k++]] = len;
        return sizes;
    }
}
