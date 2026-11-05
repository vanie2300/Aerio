using System.Windows.Media;
using Xunit;

namespace TabloidBrowser.Tests;

public class BrowserTabSiteAccentTests
{
    [Fact]
    public void PlayingTab_UsesSiteAccentColor_WhenAvailable()
    {
        var tab = new BrowserTab { IsPlayingAudio = true, SiteAccentColor = Colors.Red };

        var brush = Assert.IsType<SolidColorBrush>(tab.AccentBrush);
        Assert.Equal(Colors.Red, brush.Color);
    }

    [Fact]
    public void PlayingTab_FallsBackToAudioAccent_WhenSiteAccentMissing()
    {
        var tab = new BrowserTab { IsPlayingAudio = true };

        var brush = Assert.IsType<SolidColorBrush>(tab.AccentBrush);
        Assert.Equal(Color.FromRgb(0, 200, 83), brush.Color);
    }

    [Fact]
    public void NonPlayingTab_DoesNotUseSiteAccentColor()
    {
        var tab = new BrowserTab
        {
            IsActive = true,
            IsPlayingAudio = false,
            SiteAccentColor = Colors.Red
        };

        var brush = Assert.IsType<SolidColorBrush>(tab.AccentBrush);
        Assert.NotEqual(Colors.Red, brush.Color);
    }

    [Fact]
    public void ChangingAddress_ClearsSiteAccentColor()
    {
        var tab = new BrowserTab { SiteAccentColor = Colors.Red };

        tab.Address = "https://example.com";

        Assert.Null(tab.SiteAccentColor);
    }

    [Fact]
    public void SettingSiteAccentColor_RaisesAccentBrushNotification()
    {
        var tab = new BrowserTab { IsPlayingAudio = true };
        var raised = new List<string?>();
        tab.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        tab.SiteAccentColor = Colors.Red;

        Assert.Contains(nameof(BrowserTab.SiteAccentColor), raised);
        Assert.Contains(nameof(BrowserTab.AccentBrush), raised);
    }

    [Fact]
    public void SettingSiteAccentColor_InvalidatesCachedBrush()
    {
        var tab = new BrowserTab { IsPlayingAudio = true };
        var first = tab.AccentBrush;

        tab.SiteAccentColor = Colors.Red;
        var second = tab.AccentBrush;

        Assert.NotSame(first, second);
        Assert.Equal(Colors.Red, ((SolidColorBrush)second).Color);
    }

    [Fact]
    public void PlayingTab_AdjustsLowContrastSiteAccent_OnInactiveBackground()
    {
        var accent = Color.FromRgb(0x00, 0x2B, 0xE7);
        var tab = new BrowserTab { IsPlayingAudio = true, SiteAccentColor = accent };

        var brush = Assert.IsType<SolidColorBrush>(tab.AccentBrush);

        Assert.NotEqual(accent, brush.Color);
        Assert.True(
            AccentColorAdjuster.ContrastRatio(brush.Color, Color.FromRgb(0x28, 0x1C, 0x28)) >=
            AccentColorAdjuster.MinimumContrast);
    }

    [Fact]
    public void PlayingTab_AdjustsLowContrastSiteAccent_OnActiveBackground()
    {
        var accent = Color.FromRgb(0x00, 0x2B, 0xE7);
        var tab = new BrowserTab
        {
            IsActive = true,
            IsPlayingAudio = true,
            SiteAccentColor = accent
        };

        var brush = Assert.IsType<SolidColorBrush>(tab.AccentBrush);

        Assert.NotEqual(accent, brush.Color);
        Assert.True(
            AccentColorAdjuster.ContrastRatio(brush.Color, Color.FromRgb(0x18, 0x18, 0x18)) >=
            AccentColorAdjuster.MinimumContrast);
    }

    [Fact]
    public void AiOwnedTab_KeepsAiAccent_EvenWithSiteAccentColor()
    {
        var tab = new BrowserTab
        {
            Owner = TabOwner.AI,
            IsPlayingAudio = true,
            SiteAccentColor = Colors.Red
        };

        var brush = Assert.IsType<SolidColorBrush>(tab.AccentBrush);
        Assert.Equal(Color.FromRgb(0, 180, 220), brush.Color);
    }
}
