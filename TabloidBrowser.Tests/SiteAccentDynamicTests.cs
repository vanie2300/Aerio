using System.Text.Json;
using System.Windows.Media;
using Xunit;

namespace TabloidBrowser.Tests;

/// <summary>
/// The accent resolver is dynamic-first: page-declared colour candidates are
/// consumed in priority order, skipping anything that would not read as an
/// accent, before falling back to favicon/brand-map inputs.
/// </summary>
public class SiteAccentDynamicTests
{
    [Fact]
    public void ParsePageColors_FirstUsableCandidate_Wins()
    {
        var color = SiteAccentService.ParsePageColors("""{"candidates":["#ff0000","#1db954"]}""");

        Assert.Equal(Color.FromRgb(0xFF, 0x00, 0x00), color);
    }

    [Fact]
    public void ParsePageColors_SkipsTransparentGreyAndWhiteCandidates()
    {
        // Transparent, near-black, pure grey, and near-white are all rejected;
        // the first saturated colour is used.
        var color = SiteAccentService.ParsePageColors(
            """{"candidates":["rgba(0, 0, 0, 0)","#000000","#808080","#fefefe","#9146ff"]}""");

        Assert.Equal(Color.FromRgb(0x91, 0x46, 0xFF), color);
    }

    [Theory]
    [InlineData("#f00", 0xFF, 0x00, 0x00)]
    [InlineData("#1DB954FF", 0x1D, 0xB9, 0x54)]
    [InlineData("rgb(29, 185, 84)", 0x1D, 0xB9, 0x54)]
    [InlineData("rgba(255, 0, 0, 0.9)", 0xFF, 0x00, 0x00)]
    [InlineData("red", 0xFF, 0x00, 0x00)]
    public void ParsePageColors_ParsesCommonCssColorFormats(string candidate, byte r, byte g, byte b)
    {
        var color = SiteAccentService.ParsePageColors(
            JsonSerializer.Serialize(new { candidates = new[] { candidate } }));

        Assert.Equal(Color.FromRgb(r, g, b), color);
    }

    [Fact]
    public void ParsePageColors_FallsBackToLegacyMetaField()
    {
        var color = SiteAccentService.ParsePageColors("""{"meta":"#ff0000"}""");

        Assert.Equal(Color.FromRgb(0xFF, 0x00, 0x00), color);
    }

    [Fact]
    public void ParsePageColors_FallsBackToManifestThemeField()
    {
        var color = SiteAccentService.ParsePageColors("""{"manifestTheme":"#1db954"}""");

        Assert.Equal(Color.FromRgb(0x1D, 0xB9, 0x54), color);
    }

    [Fact]
    public void ParsePageColors_HandlesDoubleEncodedScriptResult()
    {
        // ExecuteScriptAsync serializes the script's string result once more.
        var payload = JsonSerializer.Serialize("""{"candidates":["#1db954"]}""");

        var color = SiteAccentService.ParsePageColors(payload);

        Assert.Equal(Color.FromRgb(0x1D, 0xB9, 0x54), color);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"candidates":[]}""")]
    [InlineData("""{"candidates":[123,"#000000"]}""")]
    public void ParsePageColors_NoUsableCandidate_ReturnsNull(string? json)
    {
        Assert.Null(SiteAccentService.ParsePageColors(json));
    }

    [Fact]
    public void ParsePageColors_IgnoresCandidatesArrayOnNonObjectRoot()
    {
        Assert.Null(SiteAccentService.ParsePageColors("""[1,2,3]"""));
    }
}
