using TabloidBrowser;
using Xunit;

namespace TabloidBrowser.Tests;

public class SearchKeywordServiceTests
{
    [Theory]
    [InlineData("yt jazz piano", "https://www.youtube.com/results?search_query=jazz%20piano")]
    [InlineData("WIKI octopus", "https://en.wikipedia.org/w/index.php?search=octopus")]
    public void TryBuildSearchUrl_BuiltInKeyword_BuildsEncodedQuery(string input, string expected)
    {
        var found = SearchKeywordService.TryBuildSearchUrl(input, [], out var url);

        Assert.True(found);
        Assert.Equal(expected, url);
    }

    [Fact]
    public void TryBuildSearchUrl_CustomKeyword_UsesConfiguredEngine()
    {
        var engines = new[]
        {
            new SearchEngineDef("Videos", "https://videos.example/search?q={q}", "vid")
        };

        var found = SearchKeywordService.TryBuildSearchUrl("VID cats & dogs", engines, out var url);

        Assert.True(found);
        Assert.Equal("https://videos.example/search?q=cats%20%26%20dogs", url);
    }

    [Fact]
    public void TryBuildSearchUrl_UsesConfiguredBuiltInKeyword()
    {
        var keywords = new Dictionary<string, string>
        {
            ["YouTube"] = "video",
            ["Wikipedia"] = ""
        };

        var found = SearchKeywordService.TryBuildSearchUrl("video cats", [], keywords, out var url);

        Assert.True(found);
        Assert.Equal("https://www.youtube.com/results?search_query=cats", url);
        Assert.False(SearchKeywordService.TryBuildSearchUrl("wiki cats", [], keywords, out _));
    }

    [Fact]
    public void TryBuildSearchUrl_EmptyBuiltInKeyword_CanBeReassignedToCustomEngine()
    {
        var engines = new[]
        {
            new SearchEngineDef("Videos", "https://videos.example/search?q={q}", "yt")
        };
        var keywords = new Dictionary<string, string>
        {
            ["YouTube"] = "",
            ["Wikipedia"] = "wiki"
        };

        var found = SearchKeywordService.TryBuildSearchUrl("yt cats", engines, keywords, out var url);

        Assert.True(found);
        Assert.Equal("https://videos.example/search?q=cats", url);
    }

    [Theory]
    [InlineData("yt")]
    [InlineData("yt ")]
    [InlineData("unknown search")]
    [InlineData("ordinary search")]
    public void TryBuildSearchUrl_WithoutMatchingKeyword_ReturnsFalse(string input)
    {
        var found = SearchKeywordService.TryBuildSearchUrl(input, [], out var url);

        Assert.False(found);
        Assert.Equal(string.Empty, url);
    }
}
