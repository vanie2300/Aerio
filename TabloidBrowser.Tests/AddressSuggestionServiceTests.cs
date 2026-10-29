using TabloidBrowser;
using Xunit;

namespace TabloidBrowser.Tests;

public class AddressSuggestionServiceTests
{
    [Fact]
    public void FindMatches_PrioritizesTitlePrefixThenUrlPrefixThenContains()
    {
        var bookmarks = new[]
        {
            new BookmarkItem("A story about cats", "https://example.com/cats", DateTimeOffset.UtcNow),
            new BookmarkItem("Cat videos", "https://videos.example/", DateTimeOffset.UtcNow)
        };
        var history = new[]
        {
            new HistoryItem("Example", "https://cats.example/", DateTimeOffset.UtcNow)
        };

        var matches = AddressSuggestionService.FindMatches("cat", bookmarks, history);

        Assert.Equal(
            ["Cat videos", "A story about cats", "Example"],
            matches.Select(match => match.Title));
    }

    [Fact]
    public void FindMatches_DeduplicatesUrlsAndIdentifiesBookmarks()
    {
        var bookmarks = new[]
        {
            new BookmarkItem("Saved page", "https://example.com/", DateTimeOffset.UtcNow)
        };
        var history = new[]
        {
            new HistoryItem("Visited page", "https://example.com/", DateTimeOffset.UtcNow),
            new HistoryItem("Other page", "https://other.example/", DateTimeOffset.UtcNow)
        };

        var matches = AddressSuggestionService.FindMatches("page", bookmarks, history);

        Assert.Equal(2, matches.Count);
        Assert.Equal("https://example.com/", matches[0].Url);
        Assert.Equal("Bookmark", matches[0].Source);
    }

    [Fact]
    public void FindMatches_ReturnsNoResultsForBlankQueryAndHonorsLimit()
    {
        var bookmarks = Enumerable.Range(0, 5)
            .Select(index => new BookmarkItem($"Page {index}", $"https://example{index}.com/", DateTimeOffset.UtcNow))
            .ToArray();

        Assert.Empty(AddressSuggestionService.FindMatches(" ", bookmarks, []));
        Assert.Equal(2, AddressSuggestionService.FindMatches("page", bookmarks, [], maxResults: 2).Count);
    }
}
