using Xunit;

namespace TabloidBrowser.Tests;

/// <summary>
/// Artwork URLs come from untrusted page metadata, so only http(s) to
/// non-local hosts may be fetched.
/// </summary>
public class MediaArtworkResolverTests
{
    [Theory]
    [InlineData("https://i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg")]
    [InlineData("http://example.com/cover.png")]
    [InlineData("https://cdn.example.com:8443/art.png?sig=abc")]
    public void IsAllowedArtworkUrl_PublicHttpHosts_AreAllowed(string url)
    {
        Assert.True(MediaArtworkResolver.IsAllowedArtworkUrl(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("/relative/art.png")]
    [InlineData("file:///C:/art.png")]
    [InlineData("data:image/png;base64,iVBORw0KGgo=")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.com/art.png")]
    public void IsAllowedArtworkUrl_BadUrls_AreRejected(string? url)
    {
        Assert.False(MediaArtworkResolver.IsAllowedArtworkUrl(url));
    }

    [Theory]
    [InlineData("http://localhost/art.png")]
    [InlineData("http://127.0.0.1/art.png")]
    [InlineData("http://10.0.0.1/art.png")]
    [InlineData("http://192.168.1.50/art.png")]
    [InlineData("http://172.16.4.4/art.png")]
    [InlineData("http://printer.local/art.png")]
    [InlineData("http://nas/art.png")]
    public void IsAllowedArtworkUrl_LocalNetworkTargets_AreRejected(string url)
    {
        Assert.False(MediaArtworkResolver.IsAllowedArtworkUrl(url));
    }
}
