using System.IO.Compression;

namespace VSCore;

/// <summary>
/// Minimal PNG encoder (RGBA, 8 bit) with no external native dependencies (works headless).
/// Replaces SkiaSharp, which has no linux-x64 native library in this environment.
/// </summary>
public static class PngEncoder
{
    public static byte[] Encode(int width, int height, byte[] rgba)
    {
        if (rgba.Length != width * height * 4)
            throw new ArgumentException("rgba length mismatch");
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);

        WriteChunk(ms, "IHDR", BuildIHDR(width, height));
        WriteChunk(ms, "IDAT", BuildIDAT(width, height, rgba));
        WriteChunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static byte[] BuildIHDR(int w, int h)
    {
        var b = new byte[13];
        WriteInt(b, 0, w);      // width
        WriteInt(b, 4, h);      // height
        b[8] = 8;               // bit depth
        b[9] = 6;               // color type RGBA
        b[10] = 0; b[11] = 0; b[12] = 0; // compression/filter/interlace
        return b;
    }

    private static byte[] BuildIDAT(int w, int h, byte[] rgba)
    {
        const int bpp = 4;
        int stride = w * bpp;
        var raw = new byte[(stride + 1) * h];
        int o = 0;
        for (int y = 0; y < h; y++)
        {
            raw[o++] = 0; // filter: none
            Array.Copy(rgba, y * stride, raw, o, stride);
            o += stride;
        }
        using var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal))
            zs.Write(raw, 0, raw.Length);
        return z.ToArray();
    }

    private static void WriteChunk(MemoryStream ms, string type, byte[] data)
    {
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        // length
        var len = new byte[4];
        WriteInt(len, 0, data.Length);
        ms.Write(len, 0, 4);
        ms.Write(t, 0, 4);
        ms.Write(data, 0, data.Length);
        var crc = Crc32(t, data);
        ms.Write(crc, 0, 4);
    }

    private static byte[] Crc32(byte[] type, byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in type) crc = Update(crc, b);
        foreach (byte b in data) crc = Update(crc, b);
        crc ^= 0xFFFFFFFF;
        return new[] { (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc };
    }

    private static uint Update(uint crc, byte b)
    {
        crc ^= b;
        for (int i = 0; i < 8; i++)
            crc = (crc >> 1) ^ (0xEDB88320 & (uint)-(int)(crc & 1));
        return crc;
    }

    private static void WriteInt(byte[] b, int off, int v)
    {
        b[off] = (byte)(v >> 24);
        b[off + 1] = (byte)(v >> 16);
        b[off + 2] = (byte)(v >> 8);
        b[off + 3] = (byte)v;
    }
}
