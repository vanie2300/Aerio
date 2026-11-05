using System.Windows.Media;
using Xunit;

namespace TabloidBrowser.Tests;

public class AccentColorAdjusterTests
{
    private static readonly Color DarkTab = Color.FromRgb(0x18, 0x18, 0x18);
    private static readonly Color DarkAudioTab = Color.FromRgb(0x28, 0x1C, 0x28);
    private static readonly Color LightAudioTab = Color.FromRgb(0xF2, 0xE3, 0xF2);

    [Fact]
    public void ContrastRatio_BlackAndWhite_IsAbout21()
    {
        var ratio = AccentColorAdjuster.ContrastRatio(Colors.Black, Colors.White);
        Assert.InRange(ratio, 20.9, 21.1);
    }

    [Fact]
    public void AdjustForContrast_KeepsReadableColorUnchanged()
    {
        var red = Color.FromRgb(0xFF, 0x00, 0x00);

        var adjusted = AccentColorAdjuster.AdjustForContrast(red, DarkAudioTab);

        Assert.Equal(red, adjusted);
    }

    [Fact]
    public void AdjustForContrast_LightensLowContrastColor_OnDarkBackground()
    {
        var accent = Color.FromRgb(0x00, 0x2B, 0xE7);

        var adjusted = AccentColorAdjuster.AdjustForContrast(accent, DarkAudioTab);

        Assert.NotEqual(accent, adjusted);
        Assert.True(
            AccentColorAdjuster.ContrastRatio(adjusted, DarkAudioTab) >=
            AccentColorAdjuster.MinimumContrast);
    }

    [Fact]
    public void AdjustForContrast_DarkensLowContrastColor_OnLightBackground()
    {
        var accent = Color.FromRgb(0xFF, 0xFC, 0x00);

        var adjusted = AccentColorAdjuster.AdjustForContrast(accent, LightAudioTab);

        Assert.NotEqual(accent, adjusted);
        Assert.True(
            AccentColorAdjuster.ContrastRatio(adjusted, LightAudioTab) >=
            AccentColorAdjuster.MinimumContrast);
    }

    [Fact]
    public void AdjustForContrast_KeepsDarkColorOnLightBackground()
    {
        var accent = Color.FromRgb(0x00, 0x2B, 0xE7);

        var adjusted = AccentColorAdjuster.AdjustForContrast(accent, LightAudioTab);

        Assert.Equal(accent, adjusted);
    }

    [Fact]
    public void AdjustForContrast_HandlesGrayAccent()
    {
        var gray = Color.FromRgb(0x40, 0x40, 0x40);

        var adjusted = AccentColorAdjuster.AdjustForContrast(gray, DarkTab);

        Assert.NotEqual(gray, adjusted);
        Assert.True(
            AccentColorAdjuster.ContrastRatio(adjusted, DarkTab) >=
            AccentColorAdjuster.MinimumContrast);
    }
}
