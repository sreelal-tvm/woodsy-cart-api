using System.Text.RegularExpressions;
using SkiaSharp;

namespace WoodsyCart.Infrastructure.Services;

/// <summary>Shrinks large photos to web-friendly WebP so product pages load quickly on mobile data.</summary>
public static partial class ImageOptimizer
{
    public const int MaxMainSize = 1200;
    public const int MaxThumbSize = 480;
    private const int Quality = 80;

    /// <summary>Returns WebP bytes when the image is bigger than needed, otherwise null (keep original).</summary>
    public static byte[]? Optimize(byte[] data, int maxSize, int skipBelowBytes = 150_000)
    {
        using var bitmap = SKBitmap.Decode(data);
        if (bitmap is null) return null;
        var longest = Math.Max(bitmap.Width, bitmap.Height);
        if (longest <= maxSize && data.Length <= skipBelowBytes) return null;

        var scale = Math.Min(1f, (float)maxSize / longest);
        var width = Math.Max(1, (int)(bitmap.Width * scale));
        var height = Math.Max(1, (int)(bitmap.Height * scale));
        using var resized = scale < 1f ? bitmap.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKCubicResampler.Mitchell)) : bitmap.Copy();
        using var image = SKImage.FromBitmap(resized);
        using var encoded = image.Encode(SKEncodedImageFormat.Webp, Quality);
        var bytes = encoded.ToArray();
        return bytes.Length < data.Length ? bytes : null;
    }

    public static bool IsImage(byte[] data) => SKCodec.Create(new SKMemoryStream(data)) is { } codec && Dispose(codec);

    private static bool Dispose(SKCodec c) { c.Dispose(); return true; }

    /// <summary>Extracts a YouTube video id from watch/shorts/youtu.be links.</summary>
    public static string? YouTubeId(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var m = YouTubePattern().Match(url);
        return m.Success ? m.Groups[1].Value : null;
    }

    [GeneratedRegex(@"(?:youtube\.com/(?:watch\?v=|shorts/|embed/)|youtu\.be/)([A-Za-z0-9_-]{11})")]
    private static partial Regex YouTubePattern();
}
