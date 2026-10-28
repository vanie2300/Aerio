using System.Text.Json;

namespace TabloidBrowser;

public static class AiToolDefinitions
{
    public static List<AiTool> GetAllTools(Dictionary<string, bool>? enabledTools = null)
    {
        var allTools = new List<AiTool>
        {
            SearchTool(),
            SearchAndReadTool(),
            ReadPageTool(),
            NavigateTool(),
            OpenTabTool(),
            ClickTool(),
            FillTool(),
            ListElementsTool(),
            ScrollTool(),
            CloseTabTool()
        };

        if (enabledTools is null)
            return allTools;

        return allTools.Where(t => !enabledTools.TryGetValue(t.Name, out var enabled) || enabled).ToList();
    }

    public static AiTool SearchTool() => new(
        "search",
        "Search the web via the browser's search engine. Returns result list. Never a search engine site.",
        new
        {
            type = "object",
            properties = new
            {
                query = new { type = "string", description = "Search query" }
            },
            required = new[] { "query" }
        }
    );

    public static AiTool SearchAndReadTool() => new(
        "search_and_read",
        "Search via the configured backend and read a result page.",
        new
        {
            type = "object",
            properties = new
            {
                query = new { type = "string", description = "Search query" },
                result_index = new { type = "integer", description = "Which result to read (0-based, default 0)" }
            },
            required = new[] { "query" }
        },
        RequiresWrite: true
    );

    public static AiTool ReadPageTool() => new(
        "read_page",
        "Read the current page. Returns text content, headings, and links.",
        new
        {
            type = "object",
            properties = new
            {
                offset = new { type = "integer", description = "Start reading from this character position (default 0)" },
                limit = new { type = "integer", description = "Max characters to return (default 32000)" }
            }
        }
    );

    public static AiTool NavigateTool() => new(
        "navigate",
        "Go to a URL in the current tab and read it. Never navigate to a search engine — use search instead.",
        new
        {
            type = "object",
            properties = new
            {
                url = new { type = "string", description = "URL to open (a concrete page, not a search engine)" }
            },
            required = new[] { "url" }
        },
        RequiresWrite: true
    );

    public static AiTool OpenTabTool() => new(
        "open_tab",
        "Open a URL in a new tab. Never open a search engine homepage — use search instead.",
        new
        {
            type = "object",
            properties = new
            {
                url = new { type = "string", description = "URL to open (a concrete page, not a search engine)" },
                title = new { type = "string", description = "Tab label (default: URL)" }
            },
            required = new[] { "url" }
        }
    );

    public static AiTool ClickTool() => new(
        "click",
        "Click an element on the page by CSS selector.",
        new
        {
            type = "object",
            properties = new
            {
                selector = new { type = "string", description = "CSS selector, e.g. '#btn', '.link', 'button[type=submit]'" }
            },
            required = new[] { "selector" }
        },
        RequiresWrite: true
    );

    public static AiTool FillTool() => new(
        "fill",
        "Type text into an input field on the page.",
        new
        {
            type = "object",
            properties = new
            {
                selector = new { type = "string", description = "CSS selector for the input" },
                value = new { type = "string", description = "Text to type" }
            },
            required = new[] { "selector", "value" }
        },
        RequiresWrite: true
    );

    public static AiTool ListElementsTool() => new(
        "list_elements",
        "List buttons, inputs, and links on the current page.",
        new
        {
            type = "object",
            properties = new { }
        }
    );

    public static AiTool ScrollTool() => new(
        "scroll",
        "Scroll the page up, down, to top, or to bottom.",
        new
        {
            type = "object",
            properties = new
            {
                direction = new { type = "string", description = "up, down, top, or bottom" },
                amount = new { type = "integer", description = "Pixels to scroll (default 500)" }
            },
            required = new[] { "direction" }
        }
    );

    public static AiTool CloseTabTool() => new(
        "close_tab",
        "Close an AI-owned tab by title or URL. Cleans up tabs the agent opened.",
        new
        {
            type = "object",
            properties = new
            {
                title = new { type = "string", description = "Exact tab title to close" },
                url = new { type = "string", description = "Exact URL of the tab to close" }
            }
        },
        RequiresWrite: true
    );
}

public class AiToolExecutor
{
    private readonly WebSearchService _searchService;
    private readonly AiAuthorizer _authorizer;
    private readonly PageInteractionService? _pageInteraction;
    private readonly Func<string, int, int, CancellationToken, Task<string?>> _getPageContent;
    private readonly Func<string, CancellationToken, Task<string?>> _getPageMetadata;
    private readonly Func<string, CancellationToken, Task<string?>> _extractPageLinks;
    private readonly Func<string, string, CancellationToken, Task> _navigateTo;
    private readonly Func<string, string, CancellationToken, Task> _openTab;
    private readonly Func<BrowserTab?> _getActiveTab;
    private readonly Func<BrowserTab?> _getSelectedTab;
    private readonly Func<string, string, CancellationToken, Task<string>> _closeTab;

    private static readonly Dictionary<string, string> AliasMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["web_search"] = "search",
        ["get_page_content"] = "read_page",
        ["get_page_metadata"] = "read_page",
        ["extract_page_links"] = "read_page",
        ["navigate_to"] = "navigate",
        ["click_element"] = "click",
        ["fill_field"] = "fill",
        ["get_interactive_elements"] = "list_elements",
        ["scroll_page"] = "scroll",
        ["get_selected_text"] = "list_elements",
        ["search_followup"] = "search"
    };

    private static readonly Dictionary<string, bool> _requiresWriteCache = AiToolDefinitions.GetAllTools()
        .ToDictionary(t => t.Name, t => t.RequiresWrite);

    private static string ResolveToolName(string name)
    {
        return AliasMap.TryGetValue(name, out var canonical) ? canonical : name;
    }

    public AiToolExecutor(
        WebSearchService searchService,
        AiAuthorizer authorizer,
        PageInteractionService? pageInteraction,
        Func<string, int, int, CancellationToken, Task<string?>> getPageContent,
        Func<string, CancellationToken, Task<string?>> getPageMetadata,
        Func<string, CancellationToken, Task<string?>> extractPageLinks,
        Func<string, string, CancellationToken, Task> navigateTo,
        Func<string, string, CancellationToken, Task> openTab,
        Func<BrowserTab?> getActiveTab,
        Func<BrowserTab?> getSelectedTab,
        Func<string, string, CancellationToken, Task<string>>? closeTab = null)
    {
        _searchService = searchService;
        _authorizer = authorizer;
        _pageInteraction = pageInteraction;
        _getPageContent = getPageContent;
        _getPageMetadata = getPageMetadata;
        _extractPageLinks = extractPageLinks;
        _navigateTo = navigateTo;
        _openTab = openTab;
        _getActiveTab = getActiveTab;
        _getSelectedTab = getSelectedTab;
        _closeTab = closeTab ?? ((_, _, _) => Task.FromResult("Error: close_tab not available"));
    }

    public async Task<string> ExecuteToolAsync(AiToolCall toolCall, CancellationToken ct = default)
    {
        try
        {
            var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(toolCall.Arguments);
            var canonicalName = ResolveToolName(toolCall.Name);
            var requiresWrite = _requiresWriteCache.TryGetValue(canonicalName, out var rw) && rw;

            var activeTab = _getActiveTab();
            var auth = _authorizer.Authorize(canonicalName, activeTab, requiresWrite);
            if (!auth.IsAllowed)
                return $"Authorization denied: {auth.DenialReason}";

            return toolCall.Name switch
            {
                "web_search" or "search" => await ExecuteSearch(args, ct),
                "search_and_read" => await ExecuteSearchAndRead(args, ct),
                "get_page_content" or "read_page" => await ExecuteReadPage(args, ct),
                "get_page_metadata" => await ExecuteGetPageMetadata(args, ct),
                "extract_page_links" => await ExecuteExtractLinks(args, ct),
                "navigate_to" or "navigate" => await ExecuteNavigate(args, ct),
                "open_tab" => await ExecuteOpenTab(args, ct),
                "click_element" or "click" => await ExecuteClick(args),
                "fill_field" or "fill" => await ExecuteFill(args),
                "get_interactive_elements" or "list_elements" => await ExecuteListElements(),
                "get_selected_text" => await ExecuteGetSelectedText(),
                "scroll_page" or "scroll" => await ExecuteScroll(args),
                "close_tab" => await ExecuteCloseTab(args, ct),
                "search_followup" => await ExecuteSearchFollowup(args, ct),
                _ => $"Error: Unknown tool '{toolCall.Name}'"
            };
        }
        catch (Exception ex)
        {
            return $"Error executing {toolCall.Name}: {ex.Message}";
        }
    }

    private async Task<string> ExecuteSearch(Dictionary<string, JsonElement>? args, CancellationToken ct)
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

        if (resultIndex < 0 || resultIndex >= results.Count)
            resultIndex = 0;

        var selected = results[resultIndex];
        var content = await OpenAndReadArticleAsync(selected, ct);

        var output = $"Opened and read: {selected.Title} ({selected.Url})\n\n";
        output += content ?? "Failed to read page content.";

        return output;
    }

    private async Task<string?> OpenAndReadArticleAsync(SearchResult selected, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(selected.Url))
                return "Failed to open search result: missing URL.";

            // Reuse the AI's current tab if it already owns one; only open a new tab
            // when none exists yet. Prevents searches from piling up tabs.
            var focus = _getActiveTab();
            if (focus is null || focus.Owner != TabOwner.AI)
            {
                await _openTab(selected.Url, selected.Title, ct);
            }

            return await _getPageContent(selected.Url, 0, 32000, ct);
        }
        catch
        {
            return null;
        }
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

    private async Task<string> ExecuteReadPage(Dictionary<string, JsonElement>? args, CancellationToken ct)
    {
        var offset = GetOptionalInt(args, "offset", 0);
        var limit = GetOptionalInt(args, "limit", 32000);
        var content = await _getPageContent("", offset, limit, ct);

        var metadata = await _getPageMetadata("", ct);
        var links = await _extractPageLinks("", ct);

        var output = content ?? "Failed to read page content.";
        if (!string.IsNullOrWhiteSpace(metadata))
            output += "\n\n--- Page Metadata ---\n" + metadata;
        if (!string.IsNullOrWhiteSpace(links))
            output += "\n\n--- Links ---\n" + links;

        return output;
    }

    private async Task<string> ExecuteGetPageMetadata(Dictionary<string, JsonElement>? args, CancellationToken ct)
    {
        var url = GetRequiredString(args, "url");
        if (string.IsNullOrWhiteSpace(url))
            return "Error: url parameter is required";
        var content = await _getPageMetadata(url, ct);
        return content ?? "Failed to read page metadata.";
    }

    private async Task<string> ExecuteExtractLinks(Dictionary<string, JsonElement>? args, CancellationToken ct)
    {
        var url = GetRequiredString(args, "url");
        if (string.IsNullOrWhiteSpace(url))
            return "Error: url parameter is required";
        var content = await _extractPageLinks(url, ct);
        return content ?? "Failed to extract links.";
    }

private async Task<string> ExecuteNavigate(Dictionary<string, JsonElement>? args, CancellationToken ct)
    {
        var url = GetRequiredString(args, "url");
        if (string.IsNullOrWhiteSpace(url))
            return "Error: url parameter is required";

        await _navigateTo(url, "", ct);
        var content = await _getPageContent("", 0, 32000, ct);
        return $"Navigated to {url}\n\n{content ?? "Failed to read page content."}";
    }

    private async Task<string> ExecuteOpenTab(Dictionary<string, JsonElement>? args, CancellationToken ct)
    {
        var url = GetRequiredString(args, "url");
        var title = GetRequiredString(args, "title");

        if (string.IsNullOrWhiteSpace(url))
            return "Error: url parameter is required";
        if (string.IsNullOrWhiteSpace(title))
            title = url;

        await _openTab(url, title, ct);
        return $"Opened new tab: {title} ({url})";
    }

    private async Task<string> ExecuteCloseTab(Dictionary<string, JsonElement>? args, CancellationToken ct)
    {
        var title = GetRequiredString(args, "title");
        var url = GetRequiredString(args, "url");

        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(url))
        {
            var focusTab = _getActiveTab();
            if (focusTab == null)
                return "Error: No focused tab to close.";
            title = focusTab.Title;
        }

        return await _closeTab(title, url ?? "", ct);
    }

    private async Task<string> ExecuteClick(Dictionary<string, JsonElement>? args)
    {
        if (_pageInteraction == null)
            return "Error: Page interaction not available";

        var selector = GetRequiredString(args, "selector");
        if (string.IsNullOrWhiteSpace(selector))
            return "Error: selector parameter is required";

        var tab = _getSelectedTab();
        if (tab == null)
            return "Error: No active tab to interact with";

        var versionBefore = tab.PageVersion;

        var clicked = await _pageInteraction.ClickElementAsync(selector);

        if (!clicked)
            return $"Element not found: {selector}";

        await Task.Delay(300);
        var versionAfter = tab.PageVersion;
        var url = await _pageInteraction.GetCurrentUrlAsync();
        var navigated = versionAfter > versionBefore;

        return $"Clicked: {selector}" +
               (navigated ? $"\nPage navigated (v{versionBefore} -> v{versionAfter}). URL: {url}" : "");
    }

    private async Task<string> ExecuteFill(Dictionary<string, JsonElement>? args)
    {
        if (_pageInteraction == null)
            return "Error: Page interaction not available";

        var selector = GetRequiredString(args, "selector");
        var value = GetRequiredString(args, "value");

        if (string.IsNullOrWhiteSpace(selector))
            return "Error: selector parameter is required";
        if (string.IsNullOrWhiteSpace(value))
            return "Error: value parameter is required";

        var tab = _getSelectedTab();
        if (tab == null)
            return "Error: No active tab to interact with";

        var filled = await _pageInteraction.FillFieldAsync(selector, value);
        if (!filled) return $"Field not found: {selector}";

        var url = await _pageInteraction.GetCurrentUrlAsync();
        return $"Filled {selector} with: {value}\nCurrent URL: {url}";
    }

    private async Task<string> ExecuteListElements()
    {
        if (_pageInteraction == null)
            return "Error: Page interaction not available";

        var elements = await _pageInteraction.GetInteractiveElementsAsync();
        if (elements.Count == 0)
            return "No interactive elements found.";

        return string.Join("\n", elements.Select(e =>
            $"[{e.Tag}] {(string.IsNullOrEmpty(e.Selector) ? e.Id : e.Selector)} — {(string.IsNullOrEmpty(e.Text) ? e.Placeholder : e.Text)}"));
    }

    private async Task<string> ExecuteGetSelectedText()
    {
        if (_pageInteraction == null)
            return "Error: Page interaction not available";

        var text = await _pageInteraction.GetSelectedTextAsync();
        return string.IsNullOrEmpty(text) ? "No text selected." : $"Selected text: {text}";
    }

    private async Task<string> ExecuteScroll(Dictionary<string, JsonElement>? args)
    {
        if (_pageInteraction == null)
            return "Error: Page interaction not available";

        var direction = GetRequiredString(args, "direction");
        if (string.IsNullOrWhiteSpace(direction))
            return "Error: direction parameter is required (up/down/top/bottom)";

        var amount = GetOptionalInt(args, "amount", 500);
        var scrolled = await _pageInteraction.ScrollPageAsync(direction, amount);
        return scrolled ? $"Scrolled {direction}" : $"Invalid direction: {direction}";
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

    private static int GetOptionalInt(Dictionary<string, JsonElement>? args, string key, int defaultValue)
    {
        if (args is null || !args.TryGetValue(key, out var element))
            return defaultValue;

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value))
            return value;

        return defaultValue;
    }
}
