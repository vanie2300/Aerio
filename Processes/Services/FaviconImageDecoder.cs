using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TabloidBrowser;

/// <summary>
/// Decodes favicon streams into frozen <see cref="ImageSource"/> instances that
/// are safe to bind in the UI and share across threads.
/// </summary>
public static class FaviconImageDecoder
{
    /// <summary>
    /// WebView2 can return a stream that leaves WPF's <see cref="BitmapImage"/>
    /// in a "downloading" state, which makes the bitmap impossible to freeze.
    /// The bytes are therefore materialized into a seekable buffer before
    /// decoding.
    /// </summary>
    public static ImageSource? Decode(Stream stream)
    {
        if (stream is null) return null;

        if (stream.CanSeek)
        {
            try { stream.Seek(0, SeekOrigin.Begin); }
            catch (Exception ex) when (ex is IOException or NotSupportedException)
            {
                Log.Debug(ex, "Favicon stream seek failed");
            }
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        if (buffer.Length == 0) return null;
        buffer.Position = 0;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = buffer;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
