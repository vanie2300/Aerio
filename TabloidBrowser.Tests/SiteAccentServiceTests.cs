using System.Windows.Media;
using Xunit;

namespace TabloidBrowser.Tests;

public class SiteAccentServiceTests
{
    [Theory]
    [InlineData("#ff0000", 255, 0, 0)]
    [InlineData("#f00", 255, 0, 0)]
    [InlineData("#e50914", 229, 9, 20)]
    [InlineData("#e50914ff", 229, 9, 20)]
    [InlineData("rgb(229, 9, 20)", 229, 9, 20)]
    [InlineData("rgba(229, 9, 20, 0.5)", 229, 9, 20)]
    [InlineData("red", 255, 0, 0)]
    [InlineData("  #00A1D6  ", 0, 0xA1, 0xD6)]
    public void TryParseCssColor_ParsesCommonFormats(string input, byte r, byte g, byte b)
    {
        Assert.True(SiteAccentService.TryParseCssColor(input, out var color));
        Assert.Equal(Color.FromRgb(r, g, b), color);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("not-a-color")]
    [InlineData("#12")]
    [InlineData("rgb(300, 0, 0)")]
    public void TryParseCssColor_RejectsInvalidInput(string? input)
    {
        Assert.False(SiteAccentService.TryParseCssColor(input, out _));
    }

    [Theory]
    [InlineData("#ff0000", true)]
    [InlineData("#1db954", true)]
    [InlineData("#ffffff", false)]
    [InlineData("#000000", false)]
    [InlineData("#808080", false)]
    [InlineData("#0a0a0a", false)]
    public void IsUsableAccent_RejectsNeutralColors(string input, bool expected)
    {
        Assert.True(SiteAccentService.TryParseCssColor(input, out var color));
        Assert.Equal(expected, SiteAccentService.IsUsableAccent(color));
    }

    [Fact]
    public async Task ResolveAsync_UsesBrandMap_WhenWebViewUnavailable()
    {
        SiteAccentService.ClearCache();

        var color = await SiteAccentService.ResolveAsync(
            null,
            "https://www.youtube.com/watch?v=abc");

        Assert.Equal(Color.FromRgb(0xFF, 0x00, 0x00), color);
    }

    [Fact]
    public async Task ResolveAsync_ReturnsNull_ForNonHttpUrl()
    {
        var color = await SiteAccentService.ResolveAsync(null, "about:blank");

        Assert.Null(color);
    }

    [Fact]
    public async Task ResolveAsync_CacheStaysBounded_AfterManyOrigins()
    {
        // Long-session hygiene: the accent cache previously grew without
        // bound — one entry per visited origin, never evicted. Inserting more
        // origins than the cap must evict the oldest instead.
        SiteAccentService.ClearCache();
        try
        {
            for (var i = 0; i < SiteAccentService.MaxCachedOrigins + 50; i++)
            {
                await SiteAccentService.ResolveAsync(null, $"https://site{i}.example.com/");
            }

            Assert.True(
                SiteAccentService.CacheCountForTests <= SiteAccentService.MaxCachedOrigins,
                $"Cache grew to {SiteAccentService.CacheCountForTests} entries (cap {SiteAccentService.MaxCachedOrigins})");

            // The oldest origin was evicted; resolving it again must still
            // work (re-cached on demand, cache stays within the cap).
            await SiteAccentService.ResolveAsync(null, "https://site0.example.com/");
            Assert.True(
                SiteAccentService.CacheCountForTests <= SiteAccentService.MaxCachedOrigins,
                "Re-resolving an evicted origin exceeded the cap");
        }
        finally
        {
            SiteAccentService.ClearCache();
        }
    }
}
