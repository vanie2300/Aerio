namespace TabloidBrowser;

public static class AddressSuggestionService
{
    public static IReadOnlyList<AddressSuggestion> FindMatches(
        string query,
        IReadOnlyList<BookmarkItem> bookmarks,
        IReadOnlyList<HistoryItem> history,
        int maxResults = 8)
    {
        if (string.IsNullOrWhiteSpace(query) || maxResults <= 0)
        {
            return [];
        }

        var normalizedQuery = query.Trim();
        var candidates = bookmarks
            .Select((item, index) => new Candidate(item.Title, item.Url, "Bookmark", index))
            .Concat(history.Select((item, index) => new Candidate(item.Title, item.Url, "History", bookmarks.Count + index)))
            .Select(candidate => candidate with { MatchRank = GetMatchRank(candidate, normalizedQuery) })
            .Where(candidate => candidate.MatchRank >= 0)
            .GroupBy(candidate => candidate.Url, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var match = group.OrderBy(candidate => candidate.MatchRank)
                    .ThenBy(candidate => candidate.Index)
                    .First();
                return match with
                {
                    Source = group.Any(candidate => candidate.Source == "Bookmark") ? "Bookmark" : "History"
                };
            })
            .OrderBy(candidate => candidate.MatchRank)
            .ThenBy(candidate => candidate.Index)
            .Take(maxResults);

        return candidates
            .Select(candidate => new AddressSuggestion(
                string.IsNullOrWhiteSpace(candidate.Title) ? candidate.Url : candidate.Title,
                candidate.Url,
                candidate.Source))
            .ToList();
    }

    private static int GetMatchRank(Candidate candidate, string query)
    {
        if (candidate.Title.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 0;
        if (candidate.Url.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 1;
        if (candidate.Title.Contains(query, StringComparison.OrdinalIgnoreCase)) return 2;
        if (candidate.Url.Contains(query, StringComparison.OrdinalIgnoreCase)) return 3;
        return -1;
    }

    private sealed record Candidate(string Title, string Url, string Source, int Index, int MatchRank = -1);
}
