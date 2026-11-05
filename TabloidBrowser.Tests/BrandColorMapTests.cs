using System.Windows.Media;
using Xunit;

namespace TabloidBrowser.Tests;

public class BrandColorMapTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abc", 0xFF, 0x00, 0x00)]
    [InlineData("https://music.youtube.com", 0xFF, 0x00, 0x00)]
    [InlineData("youtube.com", 0xFF, 0x00, 0x00)]
    [InlineData("https://www.netflix.com/browse", 0xE5, 0x09, 0x14)]
    [InlineData("https://twitch.tv/foo", 0x91, 0x46, 0xFF)]
    [InlineData("https://open.spotify.com/playlist/1", 0x1D, 0xB9, 0x54)]
    public void TryGetColor_ResolvesKnownHosts(string input, byte r, byte g, byte b)
    {
        Assert.True(BrandColorMap.TryGetColor(input, out var color));
        Assert.Equal(Color.FromRgb(r, g, b), color);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("https://notyoutube.com")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TryGetColor_ReturnsFalse_ForUnknownHosts(string? input)
    {
        Assert.False(BrandColorMap.TryGetColor(input, out _));
    }

    [Fact]
    public void TryGetColor_MatchesSuffixOnLabelBoundaryOnly()
    {
        Assert.True(BrandColorMap.TryGetColor("https://www.youtube.com", out _));
        Assert.False(BrandColorMap.TryGetColor("https://fakeyoutube.com", out _));
    }
}
