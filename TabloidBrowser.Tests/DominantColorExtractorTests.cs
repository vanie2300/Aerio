using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace TabloidBrowser.Tests;

public class DominantColorExtractorTests
{
    [Fact]
    public void Extract_PicksSaturatedColor_OverLargerNeutralBackground()
    {
        // 75% white background, 25% red mark: white must not win.
        var image = CreateImage(16, 16, (x, _) =>
            x < 12
                ? ((byte)255, (byte)255, (byte)255, (byte)255)
                : ((byte)255, (byte)0, (byte)0, (byte)255));

        var color = DominantColorExtractor.Extract(image);

        Assert.NotNull(color);
        Assert.True(color!.Value.R > 200);
        Assert.True(color.Value.G < 60);
        Assert.True(color.Value.B < 60);
    }

    [Fact]
    public void Extract_ReturnsNull_ForGrayscaleImage()
    {
        var image = CreateImage(8, 8, (_, _) => (128, 128, 128, 255));

        Assert.Null(DominantColorExtractor.Extract(image));
    }

    [Fact]
    public void Extract_IgnoresFullyTransparentPixels()
    {
        var image = CreateImage(8, 8, (_, _) => (255, 0, 0, 0));

        Assert.Null(DominantColorExtractor.Extract(image));
    }

    [Fact]
    public void Extract_ReturnsNull_ForNullImage()
    {
        Assert.Null(DominantColorExtractor.Extract(null));
    }

    [Fact]
    public void Extract_ConvertsNonBgra32Images()
    {
        var image = CreateImage(4, 4, (_, _) => (0, 0, 255, 255));
        var rgb24 = new FormatConvertedBitmap(image, PixelFormats.Rgb24, null, 0);
        rgb24.Freeze();

        var color = DominantColorExtractor.Extract(rgb24);

        Assert.NotNull(color);
        Assert.True(color!.Value.B > 200);
    }

    private static BitmapSource CreateImage(
        int width,
        int height,
        Func<int, int, (byte R, byte G, byte B, byte A)> pixel)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b, a) = pixel(x, y);
                var i = (y * stride) + (x * 4);
                pixels[i] = b;
                pixels[i + 1] = g;
                pixels[i + 2] = r;
                pixels[i + 3] = a;
            }
        }

        var bitmap = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }
}
