using System.Windows.Media;
using Xunit;

namespace TabloidBrowser.Tests;

/// <summary>
/// A playing (non-active) tab's background is the neutral audio tint blended
/// with the site's accent, so the tab carries the site's hue subtly.
/// </summary>
public class PlayingTabTintTests
{
    private static readonly Color DefaultAudioTabColor = Color.FromRgb(40, 28, 40);
    private static readonly Color DefaultInactiveTabColor = Color.FromRgb(17, 17, 17);
    private static readonly Color DefaultActiveTabColor = Color.FromRgb(24, 24, 24);

    // ---------- Blend ----------

    [Fact]
    public void Blend_HalfwayBetweenBlackAndWhite_IsMidGrey()
    {
        var blended = AccentColorAdjuster.Blend(Colors.Black, Colors.White, 0.5);

        Assert.Equal(128, blended.R);
        Assert.Equal(128, blended.G);
        Assert.Equal(128, blended.B);
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(0.0)]
    public void Blend_AmountAtOrBelowZero_ReturnsBase(double amount)
    {
        Assert.Equal(Colors.Red, AccentColorAdjuster.Blend(Colors.Red, Colors.Blue, amount));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(5.0)]
    public void Blend_AmountAtOrAboveOne_ReturnsOverlay(double amount)
    {
        Assert.Equal(Colors.Blue, AccentColorAdjuster.Blend(Colors.Red, Colors.Blue, amount));
    }

    // ---------- Playing-tab background ----------

    [Fact]
    public void PlayingTab_WithSiteAccent_TintsBackgroundTowardAccent()
    {
        var tab = new BrowserTab { IsPlayingAudio = true, SiteAccentColor = Colors.Red };

        var color = Assert.IsType<SolidColorBrush>(tab.TabBrush).Color;

        // Red channel dominates and the plain audio tint no longer applies.
        Assert.True(color.R > color.G && color.R > color.B);
        Assert.NotEqual(DefaultAudioTabColor, color);
    }

    [Fact]
    public void PlayingTab_WithoutSiteAccent_UsesGreenAudioTint()
    {
        var tab = new BrowserTab { IsPlayingAudio = true };

        var color = Assert.IsType<SolidColorBrush>(tab.TabBrush).Color;

        Assert.True(color.G > color.R);
        Assert.True(color.G > color.B);
    }

    [Fact]
    public void PlayingTab_BackgroundStaysDark()
    {
        var tab = new BrowserTab { IsPlayingAudio = true, SiteAccentColor = Colors.Red };

        var color = Assert.IsType<SolidColorBrush>(tab.TabBrush).Color;

        // Readability first: even a bright accent only nudges the dark background.
        Assert.True(AccentColorAdjuster.RelativeLuminance(color) < 0.1);
    }

    [Fact]
    public void NonPlayingInactiveTab_KeepsInactiveBackground()
    {
        var tab = new BrowserTab { IsPlayingAudio = false, SiteAccentColor = Colors.Red };

        var color = Assert.IsType<SolidColorBrush>(tab.TabBrush).Color;

        Assert.Equal(DefaultInactiveTabColor, color);
    }

    [Fact]
    public void ActivePlayingTab_KeepsActiveBackground()
    {
        // The tint is for background playing tabs; the active tab stays neutral
        // and signals playback through its accent dot/border.
        var tab = new BrowserTab { IsActive = true, IsPlayingAudio = true, SiteAccentColor = Colors.Red };

        var color = Assert.IsType<SolidColorBrush>(tab.TabBrush).Color;

        Assert.Equal(DefaultActiveTabColor, color);
    }

    [Fact]
    public void ChangingSiteAccent_RebuildsTabBrush()
    {
        var tab = new BrowserTab { IsPlayingAudio = true };
        var first = tab.TabBrush;
        var raised = new List<string?>();
        tab.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        tab.SiteAccentColor = Colors.Red;
        var second = tab.TabBrush;

        Assert.NotSame(first, second);
        Assert.Contains(nameof(BrowserTab.TabBrush), raised);
    }

    [Fact]
    public void PlayingTab_AccentMeetsContrastAgainstTheTintedBackground()
    {
        var accent = Color.FromRgb(0x00, 0x2B, 0xE7);
        var tab = new BrowserTab { IsPlayingAudio = true, SiteAccentColor = accent };

        var tintedBackground = AccentColorAdjuster.Blend(
            DefaultAudioTabColor,
            accent,
            0.15);
        var dotColor = Assert.IsType<SolidColorBrush>(tab.AccentBrush).Color;

        Assert.True(
            AccentColorAdjuster.ContrastRatio(dotColor, tintedBackground) >=
            AccentColorAdjuster.MinimumContrast);
    }
}
