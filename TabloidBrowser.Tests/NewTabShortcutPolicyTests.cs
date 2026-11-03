using TabloidBrowser;
using Xunit;

namespace TabloidBrowser.Tests;

public class NewTabShortcutPolicyTests
{
    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("http://example.com", true)]
    [InlineData("ftp://example.com", false)]
    [InlineData("example.com", false)]
    [InlineData("https://", false)]
    [InlineData("", false)]
    public void IsValidShortcutUrl_AcceptsOnlyHttpAndHttps(string value, bool expected)
    {
        Assert.Equal(expected, NewTabShortcutPolicy.IsValidShortcutUrl(value));
    }

    [Theory]
    [InlineData("example.com", "https://example.com/")]
    [InlineData("  example.com  ", "https://example.com/")]
    [InlineData("https://example.com", "https://example.com/")]
    public void TryNormalizeShortcutUrl_FillsSchemeAndTrims(string input, string expected)
    {
        Assert.True(NewTabShortcutPolicy.TryNormalizeShortcutUrl(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void TryNormalizeShortcutUrl_RejectsInvalidInput()
    {
        Assert.False(NewTabShortcutPolicy.TryNormalizeShortcutUrl("   ", out _));
        Assert.False(NewTabShortcutPolicy.TryNormalizeShortcutUrl("not a url??::", out _));
    }

    [Fact]
    public void TryNormalizeIconUrl_AllowsEmptyAndNormalizesHttpUrls()
    {
        Assert.True(NewTabShortcutPolicy.TryNormalizeIconUrl(" ", out var empty));
        Assert.Null(empty);
        Assert.True(NewTabShortcutPolicy.TryNormalizeIconUrl(" icons.example.com/a.png ", out var normalized));
        Assert.Equal("https://icons.example.com/a.png", normalized);
        Assert.False(NewTabShortcutPolicy.TryNormalizeIconUrl("file:///C:/icon.png", out _));
    }

    [Fact]
    public void Normalize_TrimsAndDropsInvalidEntries()
    {
        var shortcuts = new[]
        {
            new NewTabShortcut("  Wiki  ", "  https://wikipedia.org  ", " https://icons.example.com/wiki.png "),
            new NewTabShortcut("Bad", "ftp://files.example.com"),
            new NewTabShortcut("   ", "https://example.com"),
            new NewTabShortcut("Blank url", "   ")
        };

        var normalized = NewTabShortcutPolicy.Normalize(shortcuts);

        var entry = Assert.Single(normalized);
        Assert.Equal("Wiki", entry.Name);
        Assert.Equal("https://wikipedia.org", entry.Url);
        Assert.Equal("https://icons.example.com/wiki.png", entry.IconUrl);
    }

    [Fact]
    public void Normalize_FallsBackToDefaultsWhenInputIsNull()
    {
        var normalized = NewTabShortcutPolicy.Normalize(null);

        Assert.NotEmpty(normalized);
        Assert.All(normalized, shortcut => Assert.False(string.IsNullOrWhiteSpace(shortcut.Name)));
        Assert.All(normalized, shortcut => Assert.True(NewTabShortcutPolicy.IsValidShortcutUrl(shortcut.Url)));
    }
}
