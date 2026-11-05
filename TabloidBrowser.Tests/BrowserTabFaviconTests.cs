using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace TabloidBrowser.Tests;

public class BrowserTabFaviconTests
{
    [Fact]
    public void HasFaviconIsFalseInitially()
    {
        var tab = new BrowserTab();

        Assert.False(tab.HasFavicon);
        Assert.Null(tab.Favicon);
        Assert.Null(tab.FaviconUri);
    }

    [Fact]
    public void SettingFavicon_RaisesFaviconAndHasFaviconNotifications()
    {
        var tab = new BrowserTab();
        var raised = new List<string?>();
        tab.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        tab.Favicon = CreateImage();

        Assert.True(tab.HasFavicon);
        Assert.Contains(nameof(BrowserTab.Favicon), raised);
        Assert.Contains(nameof(BrowserTab.HasFavicon), raised);
    }

    [Fact]
    public void ClearingFavicon_RaisesFaviconAndHasFaviconNotifications()
    {
        var tab = new BrowserTab { Favicon = CreateImage() };
        var raised = new List<string?>();
        tab.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        tab.Favicon = null;

        Assert.False(tab.HasFavicon);
        Assert.Contains(nameof(BrowserTab.Favicon), raised);
        Assert.Contains(nameof(BrowserTab.HasFavicon), raised);
    }

    [Fact]
    public void ChangingAddress_ClearsFaviconAndFaviconUri()
    {
        var tab = new BrowserTab { Address = "https://example.com" };
        tab.Favicon = CreateImage();
        tab.FaviconUri = "https://example.com/favicon.ico";

        tab.Address = "https://other.example";

        Assert.Null(tab.Favicon);
        Assert.False(tab.HasFavicon);
        Assert.Null(tab.FaviconUri);
    }

    [Fact]
    public void SettingSameFaviconInstance_DoesNotRaiseNotifications()
    {
        var image = CreateImage();
        var tab = new BrowserTab { Favicon = image };
        var raised = new List<string?>();
        tab.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        tab.Favicon = image;

        Assert.Empty(raised);
    }

    private static ImageSource CreateImage()
    {
        var pixels = new byte[] { 0, 0, 255, 255 };
        var bitmap = BitmapSource.Create(
            1, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 4);
        bitmap.Freeze();
        return bitmap;
    }
}
