using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace TabloidBrowser.Tests;

public class FaviconImageDecoderTests
{
    [Fact]
    public void Decode_NullStream_ReturnsNull()
    {
        Assert.Null(FaviconImageDecoder.Decode(null!));
    }

    [Fact]
    public void Decode_EmptyStream_ReturnsNull()
    {
        using var stream = new MemoryStream();

        Assert.Null(FaviconImageDecoder.Decode(stream));
    }

    [Fact]
    public void Decode_PngStream_ReturnsFrozenImageWithSourceSize()
    {
        using var stream = CreatePngStream(width: 20, height: 12);

        var image = Assert.IsAssignableFrom<BitmapSource>(FaviconImageDecoder.Decode(stream));

        Assert.True(image.IsFrozen);
        Assert.Equal(20, image.PixelWidth);
        Assert.Equal(12, image.PixelHeight);
    }

    [Fact]
    public void Decode_StreamAtEnd_ReturnsImage()
    {
        // WebView2 may hand back a stream whose position is not at zero.
        using var stream = CreatePngStream(width: 8, height: 8);
        stream.Seek(0, SeekOrigin.End);

        var image = Assert.IsAssignableFrom<BitmapSource>(FaviconImageDecoder.Decode(stream));

        Assert.True(image.IsFrozen);
        Assert.Equal(8, image.PixelWidth);
    }

    private static MemoryStream CreatePngStream(int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0x11;
            pixels[i + 1] = 0x22;
            pixels[i + 2] = 0x33;
            pixels[i + 3] = 0xFF;
        }

        var frame = BitmapFrame.Create(BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(frame);

        var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;
        return stream;
    }
}
