namespace TabloidBrowser;

public static class SearchKeywordService
{
    private static readonly IReadOnlyDictionary<string, (string Id, string Template)> BuiltInSearches =
        new Dictionary<string, (string Id, string Template)>(StringComparer.OrdinalIgnoreCase)
        {
            ["youtube"] = ("YouTube", "https://www.youtube.com/results?search_query={q}"),
            ["wikipedia"] = ("Wikipedia", "https://en.wikipedia.org/w/index.php?search={q}")
        };

    public static bool TryBuildSearchUrl(
        string input,
        IReadOnlyList<SearchEngineDef>? customEngines,
        out string searchUrl) =>
        TryBuildSearchUrl(input, customEngines, BrowserSettings.DefaultSearchKeywords, out searchUrl);

    public static bool TryBuildSearchUrl(
        string input,
        IReadOnlyList<SearchEngineDef>? customEngines,
        IReadOnlyDictionary<string, string>? builtInKeywords,
        out string searchUrl)
    {
        searchUrl = string.Empty;
        var separator = input.IndexOfAny([' ', '\t', '\r', '\n']);
        if (separator <= 0 || separator == input.Length - 1)
        {
            return false;
        }

        var keyword = input[..separator];
        var query = input[(separator + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        builtInKeywords ??= BrowserSettings.DefaultSearchKeywords;
        var builtIn = BuiltInSearches.Values.FirstOrDefault(search =>
            builtInKeywords.TryGetValue(search.Id, out var configuredKeyword) &&
            !string.IsNullOrWhiteSpace(configuredKeyword) &&
            string.Equals(configuredKeyword.Trim(), keyword, StringComparison.OrdinalIgnoreCase));
        string? template = builtIn.Template;
        if (template is null && customEngines is not null)
        {
            template = customEngines
                .FirstOrDefault(engine =>
                    !string.IsNullOrWhiteSpace(engine.Keyword) &&
                    string.Equals(engine.Keyword.Trim(), keyword, StringComparison.OrdinalIgnoreCase))
                ?.UrlTemplate;
        }

        if (string.IsNullOrWhiteSpace(template))
        {
            return false;
        }

        var encodedQuery = Uri.EscapeDataString(query);
        searchUrl = template.Contains("{q}", StringComparison.Ordinal)
            ? template.Replace("{q}", encodedQuery, StringComparison.Ordinal)
            : template + encodedQuery;
        return true;
    }
}
