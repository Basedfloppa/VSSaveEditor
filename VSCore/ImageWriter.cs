namespace VSCore;

/// <summary>Image format used for the map export.</summary>
public enum MapImageFormat
{
    Png = 0,
    Jpeg = 1,
    Webp = 2
}

/// <summary>Encodes an RGBA buffer into the selected format. All encoders are managed, with no native dependencies.</summary>
public static class ImageWriter
{
    public static byte[] Encode(int width, int height, byte[] rgba, MapImageFormat format,
        int jpegQuality = 90, (byte R, byte G, byte B) matte = default)
        => format switch
        {
            MapImageFormat.Jpeg => JpegEncoder.Encode(width, height, rgba, jpegQuality, matte),
            MapImageFormat.Webp => WebpEncoder.EncodeLossless(width, height, rgba),
            _ => PngEncoder.Encode(width, height, rgba)
        };

    /// <summary>Format for a file extension (null — unknown extension).</summary>
    public static MapImageFormat? FormatFromPath(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".png" => MapImageFormat.Png,
            ".jpg" or ".jpeg" or ".jpe" => MapImageFormat.Jpeg,
            ".webp" => MapImageFormat.Webp,
            _ => null
        };
    }

    public static string Extension(MapImageFormat format) => format switch
    {
        MapImageFormat.Jpeg => "jpg",
        MapImageFormat.Webp => "webp",
        _ => "png"
    };

    public static string DisplayName(MapImageFormat format) => format switch
    {
        MapImageFormat.Jpeg => "JPEG image",
        MapImageFormat.Webp => "WebP (lossless)",
        _ => "PNG image"
    };
}
