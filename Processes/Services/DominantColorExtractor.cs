using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TabloidBrowser;

/// <summary>
/// Extracts a representative, saturated accent color from an image (typically a
/// favicon or logo). Neutral pixels (white, black, gray) are ignored so the
/// result reflects the brand color rather than the image background.
/// </summary>
public static class DominantColorExtractor
{
    private const int BucketBits = 4; // 16 levels per channel
    private const int MinAlpha = 128;
    private const int NeutralChannelSpread = 24;
    private const int NearBlackThreshold = 24;

    public static Color? Extract(BitmapSource? image)
    {
        if (image is null)
        {
            return null;
        }

        BitmapSource source = image;
        if (source.Format != PixelFormats.Bgra32)
        {
            source = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        }

        var width = source.PixelWidth;
        var height = source.PixelHeight;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var stride = width * 4;
        var pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);

        var buckets = new Dictionary<int, Bucket>();

        for (var i = 0; i < pixels.Length; i += 4)
        {
            var b = pixels[i];
            var g = pixels[i + 1];
            var r = pixels[i + 2];
            var a = pixels[i + 3];
            if (a < MinAlpha)
            {
                continue;
            }

            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));
            var spread = max - min;
            if (spread < NeutralChannelSpread || max < NearBlackThreshold)
            {
                // Gray, white, black, or transparent-ish — not a usable accent.
                continue;
            }

            var saturation = spread / (double)max;
            var key = (r >> (8 - BucketBits)) << (BucketBits * 2)
                | (g >> (8 - BucketBits)) << BucketBits
                | (b >> (8 - BucketBits));

            if (!buckets.TryGetValue(key, out var bucket))
            {
                bucket = new Bucket();
            }

            bucket.Score += 0.25 + saturation;
            bucket.R += r;
            bucket.G += g;
            bucket.B += b;
            bucket.Count++;
            buckets[key] = bucket;
        }

        if (buckets.Count == 0)
        {
            return null;
        }

        var best = buckets.Values.OrderByDescending(bucket => bucket.Score).First();
        return Color.FromRgb(
            (byte)(best.R / best.Count),
            (byte)(best.G / best.Count),
            (byte)(best.B / best.Count));
    }

    private struct Bucket
    {
        public double Score;
        public long R;
        public long G;
        public long B;
        public int Count;
    }
}
