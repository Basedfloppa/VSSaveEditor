using System.Text;
using Xunit;

namespace VSCore.Tests;

/// <summary>Map encoding to PNG/JPEG/WebP: signatures, sizes read from the headers, format limits.
/// The bytes themselves are checked separately by an external decoder (libjpeg/libwebp); the invariants checked here are structural.</summary>
public class ImageWriterTests
{
    private static byte[] Gradient(int w, int h, bool opaque = false)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = (y * w + x) * 4;
                px[p] = (byte)(x * 255 / Math.Max(1, w - 1));
                px[p + 1] = (byte)(y * 255 / Math.Max(1, h - 1));
                px[p + 2] = (byte)((x + y) * 255 / Math.Max(1, w + h - 2));
                px[p + 3] = opaque ? (byte)255 : (byte)(x < w / 2 ? 255 : 40);
            }
        return px;
    }

    private static (int W, int H) PngSize(byte[] png)
    {
        int be(int o) => (png[o] << 24) | (png[o + 1] << 16) | (png[o + 2] << 8) | png[o + 3];
        return (be(16), be(20));
    }

    private static (int W, int H) JpegSize(byte[] jpg)
    {
        int i = 2;
        while (i < jpg.Length - 8)
        {
            if (jpg[i] != 0xFF) { i++; continue; }
            byte m = jpg[i + 1];
            if (m == 0xC0 || m == 0xC1 || m == 0xC2)
                return ((jpg[i + 7] << 8) | jpg[i + 8], (jpg[i + 5] << 8) | jpg[i + 6]);
            if (m == 0xD8 || m == 0xD9 || (m >= 0xD0 && m <= 0xD7)) { i += 2; continue; }
            i += 2 + ((jpg[i + 2] << 8) | jpg[i + 3]);
        }
        throw new InvalidOperationException("SOF not found");
    }

    private static (int W, int H) WebpSize(byte[] webp)
    {
        Assert.Equal("RIFF", Encoding.ASCII.GetString(webp, 0, 4));
        Assert.Equal("WEBP", Encoding.ASCII.GetString(webp, 8, 4));
        Assert.Equal("VP8L", Encoding.ASCII.GetString(webp, 12, 4));
        int bytePos = 20, bit = 0;
        int Read(int count)
        {
            int v = 0;
            for (int k = 0; k < count; k++)
            {
                v |= ((webp[bytePos] >> bit) & 1) << k;
                if (++bit == 8) { bit = 0; bytePos++; }
            }
            return v;
        }
        Assert.Equal(0x2F, Read(8));
        int w = Read(14) + 1;
        int h = Read(14) + 1;
        return (w, h);
    }

    [Fact(DisplayName = "formats: PNG/JPEG/WebP have correct signatures and dimensions")]
    public void Encode_AllFormats()
    {
        int w = 64, h = 40;
        var px = Gradient(w, h);
        var png = ImageWriter.Encode(w, h, px, MapImageFormat.Png);
        var jpg = ImageWriter.Encode(w, h, px, MapImageFormat.Jpeg, 90, (255, 255, 255));
        var webp = ImageWriter.Encode(w, h, px, MapImageFormat.Webp);

        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png.Take(4).ToArray());
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, jpg.Take(2).ToArray());
        Assert.Equal(new byte[] { 0xFF, 0xD9 }, jpg.Skip(jpg.Length - 2).ToArray());
        Assert.Equal("RIFF", Encoding.ASCII.GetString(webp, 0, 4));

        Assert.Equal((w, h), PngSize(png));
        Assert.Equal((w, h), JpegSize(jpg));
        Assert.Equal((w, h), WebpSize(webp));
    }

    [Fact(DisplayName = "WebP lossless: a single-tone image compresses several times over (Huffman coding works)")]
    public void Webp_FlatImage_Compresses()
    {
        int w = 128, h = 128;
        var px = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++) { px[i * 4] = 10; px[i * 4 + 1] = 20; px[i * 4 + 2] = 30; px[i * 4 + 3] = 255; }
        var webp = ImageWriter.Encode(w, h, px, MapImageFormat.Webp);
        Assert.Equal((w, h), WebpSize(webp));
        Assert.True(webp.Length < px.Length / 8, $"WebP is too large: {webp.Length} against {px.Length} raw");
    }

    [Fact(DisplayName = "JPEG: quality affects the size and the file stays valid")]
    public void Jpeg_QualityAffectsSize()
    {
        int w = 96, h = 64;
        var px = Gradient(w, h, opaque: true);
        var low = ImageWriter.Encode(w, h, px, MapImageFormat.Jpeg, 30, (255, 255, 255));
        var high = ImageWriter.Encode(w, h, px, MapImageFormat.Jpeg, 95, (255, 255, 255));
        Assert.Equal((w, h), JpegSize(low));
        Assert.Equal((w, h), JpegSize(high));
        Assert.True(high.Length > low.Length, "JPEG q95 must be larger than q30");
    }

    [Fact(DisplayName = "format from the path extension")]
    public void FormatFromPath()
    {
        Assert.Equal(MapImageFormat.Png, ImageWriter.FormatFromPath("a.PNG"));
        Assert.Equal(MapImageFormat.Jpeg, ImageWriter.FormatFromPath("a.jpg"));
        Assert.Equal(MapImageFormat.Jpeg, ImageWriter.FormatFromPath("a.jpeg"));
        Assert.Equal(MapImageFormat.Webp, ImageWriter.FormatFromPath("a.webp"));
        Assert.Null(ImageWriter.FormatFromPath("a.bmp"));
    }

    [Fact(DisplayName = "WebP: an oversized image fails with a clear error (limit 16384)")]
    public void Webp_TooLarge_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            WebpEncoder.EncodeLossless(20000, 2, new byte[20000 * 2 * 4]));
        Assert.Contains("too large", ex.Message);
    }

    [Fact(DisplayName = "renderer: Export writes the format selected by the extension")]
    public void MapImageRenderer_Export_UsesExtension()
    {
        var tm = new TerrainMap
        {
            MinX = 0, MinZ = 0, Width = 12, Height = 9,
            SurfaceY = new int[108], BottomY = new int[108], TopBlock = new ushort[108], WaterDepth = new int[108],
            MinSurface = 1, MaxSurface = 5
        };
        for (int i = 0; i < 108; i++) { tm.SurfaceY[i] = 1 + i % 4; tm.TopBlock[i] = (ushort)(1 + i % 3); }
        var dir = Path.Combine(Path.GetTempPath(), "vsimg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var (name, sig) in new (string, byte[])[]
            {
                ("m.png", new byte[] { 0x89, 0x50 }), ("m.jpg", new byte[] { 0xFF, 0xD8 }), ("m.webp", Encoding.ASCII.GetBytes("RI"))
            })
            {
                var path = Path.Combine(dir, name);
                MapImageRenderer.Export(path, tm, new MapImageOptions { Scale = 1 });
                var bytes = File.ReadAllBytes(path);
                Assert.True(bytes.Length > 20, $"{name}: empty");
                Assert.Equal(sig, bytes.Take(sig.Length).ToArray());
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
