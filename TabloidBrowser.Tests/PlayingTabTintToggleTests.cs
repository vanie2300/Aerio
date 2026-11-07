using System.Windows.Media;
using Xunit;

namespace TabloidBrowser.Tests;

/// <summary>
/// "Tint playing tabs with the site color" is an appearance setting: default on,
/// carried to tabs through the theme, and persisted in settings.json.
/// </summary>
public class PlayingTabTintToggleTests : IDisposable
{
    private readonly string _tempDir;

    public PlayingTabTintToggleTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"AerioTintTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void BrowserSettings_Default_HasTintEnabled()
    {
        Assert.True(BrowserSettings.Default.TintPlayingTabsEffective);
    }

    [Fact]
    public void BrowserSettings_ExplicitFalse_DisablesTint()
    {
        var settings = BrowserSettings.Default with { TintPlayingTabs = false };

        Assert.False(settings.TintPlayingTabsEffective);
    }

    [Fact]
    public void BrowserSettings_ExplicitTrue_KeepsTintEnabled()
    {
        var settings = BrowserSettings.Default with { TintPlayingTabs = true };

        Assert.True(settings.TintPlayingTabsEffective);
    }

    [Fact]
    public void Theme_DefaultsToTintEnabled()
    {
        Assert.True(Theme.Dark.TintPlayingTabs);
        Assert.True(Theme.Light.TintPlayingTabs);
    }

    [Fact]
    public void PlayingTab_TintDisabled_KeepsPlainAudioBackground()
    {
        var tab = new BrowserTab { IsPlayingAudio = true, SiteAccentColor = Colors.Red };
        tab.SetVisualColors(Theme.Dark with { TintPlayingTabs = false });

        var color = Assert.IsType<SolidColorBrush>(tab.TabBrush).Color;

        Assert.Equal(Theme.Dark.TabAudio, color);
    }

    [Fact]
    public void PlayingTab_TintEnabled_BlendsAccentIntoBackground()
    {
        var tab = new BrowserTab { IsPlayingAudio = true, SiteAccentColor = Colors.Red };
        tab.SetVisualColors(Theme.Dark with { TintPlayingTabs = true });

        var color = Assert.IsType<SolidColorBrush>(tab.TabBrush).Color;

        Assert.NotEqual(Theme.Dark.TabAudio, color);
        Assert.True(color.R > color.G && color.R > color.B);
    }

    [Fact]
    public void BrowserDataStore_TintSetting_SurvivesReload()
    {
        using (var store = new BrowserDataStore(_tempDir))
        {
            Assert.True(store.SaveSettings(BrowserSettings.Default with { TintPlayingTabs = false }));
        }

        using var reloaded = new BrowserDataStore(_tempDir);
        Assert.False(reloaded.Settings.TintPlayingTabsEffective);
    }

    [Fact]
    public void BrowserDataStore_TintSetting_AbsentFromFile_DefaultsOn()
    {
        File.WriteAllText(Path.Combine(_tempDir, "settings.json"), """{"SearchEngine":"DuckDuckGo"}""");

        using var store = new BrowserDataStore(_tempDir);

        Assert.True(store.Settings.TintPlayingTabsEffective);
    }
}
