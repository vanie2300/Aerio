using System.Text.Json;

namespace TabloidBrowser;

public static class AiToolDefinitions
{
    public static List<AiTool> GetAllTools(Dictionary<string, bool>? enabledTools = null)
    {
        var allTools = new List<AiTool>
        {
            WebSearchTool(),
            SearchAndReadTool(),
            SearchFollowupTool(),
            GetPageContentTool(),
            GetPageMetadataTool(),
            ExtractPageLinksTool(),
            NavigateToTool(),
            OpenTabTool()
        };

        if (enabledTools is null)
            return allTools;

        return allTools.Where(t => !enabledTools.TryGetValue(t.Name, out var enabled) || enabled).ToList();
    }

    public static AiTool WebSearchTool() => new(
        "web_search",
        "Search the web for current information. Returns a list of results with title, URL, and snippet.",
        new
        {
            type = "object",
            properties = new
            {
                query = new { type = "string", description = "The search query" }
            },
            required = new[] { "query" }
        }
    );

    public static AiTool SearchAndReadTool() => new(
        "search_and_read",
        "Search the web, navigate to the Nth result, and read the page content.",
        new
        {
            type = "object",
            properties = new
            {
                query = new { type = "string", description = "The search query" },
                result_index = new { type = "integer", description = "Which result to open (0-based). Default: 0" }
            },
            required = new[] { "query" }
        }
    );

    public static AiTool SearchFollowupTool() => new(
        "search_followup",
        "Get related search queries and suggestions. Use when the initial search didn't find good results.",
        new
        {
            type = "object",
            properties = new
            {
                query = new { type = "string", description = "The search query to get follow-ups for" }
            },
            required = new[] { "query" }
        }
    );

    public static AiTool GetPageContentTool() => new(
        "get_page_content",
        "Get the text content of the currently open page. Returns up to ~8000 characters.",
        new
        {
            type = "object",
            properties = new { }
        }
    );

    public static AiTool GetPageMetadataTool() => new(
        "get_page_metadata",
        "Get lightweight metadata from a page — title, meta description, OG tags, headings.",
        new
        {
            type = "object",
            properties = new
            {
                url = new { type = "string", description = "URL to check. If empty, uses current page." }
            }
        }
    );

    public static AiTool ExtractPageLinksTool() => new(
        "extract_page_links",
        "Extract all hyperlinks with their visible text from a page.",
        new
        {
            type = "object",
            properties = new
            {
                source = new { type = "string", description = "Optional URL. If empty, extracts from current page." }
            }
        }
    );

    public static AiTool NavigateToTool() => new(
        "navigate_to",
        "Navigate the current tab to a URL and read its content.",
        new
        {
            type = "object",
            properties = new
            {
                url = new { type = "string", description = "Full URL to navigate to" }
            },
            required = new[] { "url" }
        }
    );

    public static AiTool OpenTabTool() => new(
        "open_tab",
        "Open a URL in a new background tab.",
        new
        {
            type = "object",
            properties = new
            {
                url = new { type = "string", description = "URL to open" },
                title = new { type = "string", description = "Display title for the tab" }
            },
            required = new[] { "url", "title" }
        }
    );
}

public class AiToolExecutor
{
    private readonly WebSearchService _searchService;
    private readonly Func<string, Task<string?>> _getPageContent;
    private readonly Func<string, Task<string?>> _getPageMetadata;
    private readonly Func<string, Task<string?>> _extractPageLinks;
    private readonly Func<string, string, Task> _navigateTo;
    private readonly Func<string, string, Task> _openTab;

    public AiToolExecutor(
        WebSearchService searchService,
        Func<string, Task<string?>> getPageContent,
        Func<string, Task<string?>> getPageMetadata,
        Func<string, Task<string?>> extractPageLinks,
        Func<string, string, Task> navigateTo,
        Func<string, string, Task> openTab)
    {
        _searchService = searchService;
        _getPageContent = getPageContent;
        _getPageMetadata = getPageMetadata;
        _extractPageLinks = extractPageLinks;
        _navigateTo = navigateTo;
        _openTab = openTab;
    }

    public async Task<string> ExecuteToolAsync(AiToolCall toolCall, CancellationToken ct = default)
    {
        try
        {
            var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(toolCall.Arguments);

            return toolCall.Name switch
            {
                "web_search" => await ExecuteWebSearch(args, ct),
                "search_and_read" => await ExecuteSearchAndRead(args, ct),
                "search_followup" => await ExecuteSearchFollowup(args, ct),
                "get_page_content" => await ExecuteGetPageContent(args),
                "get_page_metadata" => await ExecuteGetPageMetadata(args),
                "extract_page_links" => await ExecuteExtractLinks(args),
                "navigate_to" => await ExecuteNavigateTo(args),
                "open_tab" => await ExecuteOpenTab(args),
                _ => $"Error: Unknown tool '{toolCall.Name}'"
            };
        }
        catch (Exception ex)
        {
            return $"Error executing {toolCall.Name}: {ex.Message}";
        }
    }

    private async Task<string> ExecuteWebSearch(Dictionary<string, JsonElement>? args, CancellationToken ct)
    {
        var query = GetRequiredString(args, "query");
        if (string.IsNullOrWhiteSpace(query))
            return "Error: query parameter is required";

        var results = await _searchService.SearchAsync(query, ct);
        if (results.Count == 0)
            return "No results found.";

        return FormatSearchResults(results);
    }

    private async Task<string> ExecuteSearchAndRead(Dictionary<string, JsonElement>? args, CancellationToken ct)
    {
        var query = GetRequiredString(args, "query");
        if (string.IsNullOrWhiteSpace(query))
            return "Error: query parameter is required";

        var resultIndex = GetOptionalInt(args, "result_index", 0);

        var results = await _searchService.SearchAsync(query, ct);
        if (results.Count == 0)
            return "No results found.";

        if (resultIndex >= results.Count)
            resultIndex = 0;

        var selected = results[resultIndex];
        var content = await _getPageContent(selected.Url);

        var output = $"Search results:\n{FormatSearchResults(results)}\n\n";
        output += $"--- Reading: {selected.Title} ({selected.Url}) ---\n";
        output += content ?? "Failed to read page content.";

        return output;
    }

    private async Task<string> ExecuteSearchFollowup(Dictionary<string, JsonElement>? args, CancellationToken ct)
    {
        var query = GetRequiredString(args, "query");
        if (string.IsNullOrWhiteSpace(query))
            return "Error: query parameter is required";

        var suggestions = await _searchService.GetSuggestionsAsync(query, ct);
        if (suggestions.Count == 0)
            return "No suggestions found.";

        return "Related searches:\n" + string.Join("\n", suggestions.Select((s, i) => $"{i + 1}. {s}"));
    }

    private async Task<string> ExecuteGetPageContent(Dictionary<string, JsonElement>? args)
    {
        var content = await _getPageContent("");
        return content ?? "Failed to read page content.";
    }

    private async Task<string> ExecuteGetPageMetadata(Dictionary<string, JsonElement>? args)
    {
        var url = GetOptionalString(args, "url");
        var content = await _getPageMetadata(url ?? "");
        return content ?? "Failed to read page metadata.";
    }

    private async Task<string> ExecuteExtractLinks(Dictionary<string, JsonElement>? args)
    {
        var url = GetOptionalString(args, "url");
        var content = await _extractPageLinks(url ?? "");
        return content ?? "Failed to extract links.";
    }

    private async Task<string> ExecuteNavigateTo(Dictionary<string, JsonElement>? args)
    {
        var url = GetRequiredString(args, "url");
        if (string.IsNullOrWhiteSpace(url))
            return "Error: url parameter is required";

        await _navigateTo(url, "");
        var content = await _getPageContent(url);
        return $"Navigated to {url}\n\n{content ?? "Failed to read page content."}";
    }

    private async Task<string> ExecuteOpenTab(Dictionary<string, JsonElement>? args)
    {
        var url = GetRequiredString(args, "url");
        var title = GetRequiredString(args, "title");

        if (string.IsNullOrWhiteSpace(url))
            return "Error: url parameter is required";
        if (string.IsNullOrWhiteSpace(title))
            title = url;

        await _openTab(url, title);
        return $"Opened new tab: {title} ({url})";
    }

    private static string FormatSearchResults(List<SearchResult> results)
    {
        return string.Join("\n\n", results.Select((r, i) =>
            $"{i + 1}. {r.Title}\n   {r.Url}\n   {r.Snippet}"));
    }

    private static string? GetRequiredString(Dictionary<string, JsonElement>? args, string key)
    {
        if (args is null || !args.TryGetValue(key, out var element))
            return null;

        return element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
    }

    private static string? GetOptionalString(Dictionary<string, JsonElement>? args, string key)
    {
        return GetRequiredString(args, key);
    }

    private static int GetOptionalInt(Dictionary<string, JsonElement>? args, string key, int defaultValue)
    {
        if (args is null || !args.TryGetValue(key, out var element))
            return defaultValue;

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value))
            return value;

        return defaultValue;
    }
}
