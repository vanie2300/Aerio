using System.Reflection;
using System.Text.Json;
using Xunit;
using TabloidBrowser;

namespace TabloidBrowser.Tests;

public class AiToolExecutorSimulationTests
{
    private readonly AiToolExecutor _executor;
    private readonly BrowserTab _activeTab;
    private readonly List<string> _navigatedUrls = [];
    private readonly List<(string Url, string Title)> _openedTabs = [];
    private readonly List<string> _closedTabs = [];

    private static readonly Func<string, Task<string?>> DummyJs = _ => Task.FromResult<string?>(null);
    private static readonly Func<Task> DummyNav = () => Task.CompletedTask;

    public AiToolExecutorSimulationTests()
    {
        _activeTab = new BrowserTab { Address = "https://example.com", Title = "Example", Owner = TabOwner.AI };

        var policy = AiBrowserPolicy.Default;
        var authorizer = new AiAuthorizer(policy, () => _activeTab);
        var pageInteraction = new PageInteractionService(DummyJs, DummyNav);

        _executor = new AiToolExecutor(
            new WebSearchService(),
            authorizer,
            pageInteraction,
            (url, offset, limit, ct) => Task.FromResult<string?>($"Page content for {url}"),
            (url, ct) => Task.FromResult<string?>($"Title: Test | URL: {url}"),
            (url, ct) => Task.FromResult<string?>($"- link1: https://example.com/link1\n- link2: https://example.com/link2"),
            async (url, title, ct) => { _navigatedUrls.Add(url); await Task.CompletedTask; },
            async (url, title, ct) => { _openedTabs.Add((url, title)); await Task.CompletedTask; },
            () => _activeTab,
            () => _activeTab,
            (title, url, ct) =>
            {
                _closedTabs.Add(title);
                return Task.FromResult($"Closed: {title}");
            }
        );
    }

    private AiToolCall MakeCall(string name, Dictionary<string, JsonElement>? args = null)
    {
        var argsStr = args is not null
            ? JsonSerializer.Serialize(args)
            : "{}";
        return new AiToolCall("call-1", name, argsStr);
    }

    private static Dictionary<string, JsonElement> MakeArgs(params (string Key, object Value)[] pairs)
    {
        var dict = new Dictionary<string, JsonElement>();
        foreach (var (key, value) in pairs)
        {
            dict[key] = JsonSerializer.SerializeToElement(value);
        }
        return dict;
    }

    [Theory]
    [InlineData("search")]
    [InlineData("search_and_read")]
    [InlineData("read_page")]
    [InlineData("navigate")]
    [InlineData("open_tab")]
    [InlineData("close_tab")]
    [InlineData("click")]
    [InlineData("fill")]
    [InlineData("list_elements")]
    [InlineData("scroll")]
    public void ToolNames_AllExist(string toolName)
    {
        var tools = AiToolDefinitions.GetAllTools();
        Assert.Contains(tools, t => t.Name == toolName);
    }

    [Fact]
    public async Task SearchAndRead_OpensSelectedResultUrl_WhenNoAiTabIsFocused()
    {
        var opened = new List<(string Url, string Title)>();
        var authorizer = new AiAuthorizer(AiBrowserPolicy.Default, () => new BrowserTab { Owner = TabOwner.User });
        var executor = new AiToolExecutor(
            new WebSearchService(),
            authorizer,
            new PageInteractionService(DummyJs, DummyNav),
            (url, offset, limit, ct) => Task.FromResult<string?>($"Page content for {url}"),
            (url, ct) => Task.FromResult<string?>($"Title: Test | URL: {url}"),
            (url, ct) => Task.FromResult<string?>($"- link1: {url}/link1"),
            async (url, title, ct) => { await Task.CompletedTask; },
            async (url, title, ct) => { opened.Add((url, title)); await Task.CompletedTask; },
            () => null,
            () => null,
            (title, url, ct) => Task.FromResult($"Closed: {title}")
        );

        var method = typeof(AiToolExecutor).GetMethod("OpenAndReadArticleAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = await (Task<string?>)method.Invoke(executor, [new SearchResult("Example article", "https://example.com/article", "snippet"), CancellationToken.None])!;

        Assert.Equal("Page content for https://example.com/article", result);
        Assert.Single(opened);
        Assert.Equal("https://example.com/article", opened[0].Url);
        Assert.Equal("Example article", opened[0].Title);
    }

    [Fact]
    public async Task OpenAndReadArticle_HandlesEmptyUrl_WhenAiTabIsFocused()
    {
        var opened = new List<(string Url, string Title)>();
        var authorizer = new AiAuthorizer(AiBrowserPolicy.Default, () => new BrowserTab { Owner = TabOwner.AI });
        var executor = new AiToolExecutor(
            new WebSearchService(),
            authorizer,
            new PageInteractionService(DummyJs, DummyNav),
            (url, offset, limit, ct) => Task.FromResult<string?>($"Page content for {url}"),
            (url, ct) => Task.FromResult<string?>($"Title: Test | URL: {url}"),
            (url, ct) => Task.FromResult<string?>($"- link1: {url}/link1"),
            async (url, title, ct) => { opened.Add((url, title)); await Task.CompletedTask; },
            async (url, title, ct) => { opened.Add((url, title)); await Task.CompletedTask; },
            () => new BrowserTab { Owner = TabOwner.AI },
            () => new BrowserTab { Owner = TabOwner.AI },
            (title, url, ct) => Task.FromResult($"Closed: {title}")
        );

        var method = typeof(AiToolExecutor).GetMethod("OpenAndReadArticleAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = await (Task<string?>)method.Invoke(executor, [new SearchResult("Example article", "", "snippet"), CancellationToken.None])!;

        Assert.Equal("Failed to open search result: missing URL.", result);
        Assert.Empty(opened);
    }

    [Fact]
    public async Task ReadPage_ReturnsContent()
    {
        var call = MakeCall("read_page");
        var result = await _executor.ExecuteToolAsync(call);
        Assert.Contains("Page content", result);
    }

    [Fact]
    public async Task ReadPage_WithOffsetLimit()
    {
        var call = MakeCall("read_page", MakeArgs(("offset", 100), ("limit", 500)));
        var result = await _executor.ExecuteToolAsync(call);
        Assert.Contains("Page content", result);
    }

    [Fact]
    public async Task OpenTab_OpensNewTab()
    {
        var call = MakeCall("open_tab", MakeArgs(("url", "https://github.com"), ("title", "GitHub")));
        var result = await _executor.ExecuteToolAsync(call);
        Assert.Contains("Opened new tab", result);
        Assert.Single(_openedTabs);
        Assert.Equal("https://github.com", _openedTabs[0].Url);
    }

    [Fact]
    public async Task OpenTab_EmptyUrl_ReturnsError()
    {
        var call = MakeCall("open_tab", MakeArgs(("url", ""), ("title", "T")));
        var result = await _executor.ExecuteToolAsync(call);
        Assert.Contains("Error", result);
    }

    [Fact]
    public async Task CloseTab_ByTitle_ClosesTab()
    {
        var call = MakeCall("close_tab", MakeArgs(("title", "Wikipedia")));
        var result = await _executor.ExecuteToolAsync(call);
        Assert.Contains("Closed: Wikipedia", result);
        Assert.Equal("Wikipedia", _closedTabs[0]);
    }

    [Fact]
    public async Task CloseTab_NoArgs_UsesFocusedTabTitle()
    {
        var call = MakeCall("close_tab");
        var result = await _executor.ExecuteToolAsync(call);
        Assert.Contains("Closed: Example", result);
        Assert.Equal("Example", _closedTabs[0]);
    }

    [Fact]
    public async Task CloseTab_ByUrl_ClosesTab()
    {
        var call = MakeCall("close_tab", MakeArgs(("url", "https://example.com")));
        var result = await _executor.ExecuteToolAsync(call);
        Assert.Contains("Closed", result);
    }

    [Fact]
    public async Task Click_InvalidSelector_ReturnsNotFound()
    {
        var call = MakeCall("click", MakeArgs(("selector", "#nonexistent")));
        var result = await _executor.ExecuteToolAsync(call);
        Assert.Contains("not found", result);
    }

    [Fact]
    public async Task Fill_InvalidSelector_ReturnsNotFound()
    {
        var call = MakeCall("fill", MakeArgs(("selector", "#nonexistent"), ("value", "hello")));
        var result = await _executor.ExecuteToolAsync(call);
        Assert.Contains("not found", result);
    }

    [Fact]
    public async Task ListElements_ReturnsResult()
    {
        var call = MakeCall("list_elements");
        var result = await _executor.ExecuteToolAsync(call);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task Scroll_ValidDirection()
    {
        var call = MakeCall("scroll", MakeArgs(("direction", "down")));
        var result = await _executor.ExecuteToolAsync(call);
        Assert.Contains("Scrolled", result);
    }

    [Fact]
    public async Task Scroll_InvalidDirection()
    {
        var call = MakeCall("scroll", MakeArgs(("direction", "diagonal")));
        var result = await _executor.ExecuteToolAsync(call);
        Assert.Contains("Invalid direction", result);
    }

    [Fact]
    public async Task UnknownTool_ReturnsError()
    {
        var call = MakeCall("nonexistent_tool");
        var result = await _executor.ExecuteToolAsync(call);
        Assert.Contains("Unknown tool", result);
    }

    [Fact]
    public async Task Click_EmptySelector_ReturnsError()
    {
        var call = MakeCall("click", MakeArgs(("selector", "")));
        var result = await _executor.ExecuteToolAsync(call);
        Assert.Contains("Error", result);
    }

    [Fact]
    public async Task Fill_EmptyValue_ReturnsError()
    {
        var call = MakeCall("fill", MakeArgs(("selector", "#input"), ("value", "")));
        var result = await _executor.ExecuteToolAsync(call);
        Assert.Contains("Error", result);
    }

    [Fact]
    public async Task Scroll_EmptyDirection_ReturnsError()
    {
        var call = MakeCall("scroll", MakeArgs(("direction", "")));
        var result = await _executor.ExecuteToolAsync(call);
        Assert.Contains("Error", result);
    }

    [Theory]
    [InlineData("read_page")]
    [InlineData("open_tab")]
    [InlineData("click")]
    [InlineData("fill")]
    [InlineData("list_elements")]
    [InlineData("scroll")]
    public async Task AllTools_ExecutionDoesNotThrow(string toolName)
    {
        AiToolCall call = toolName switch
        {
            "open_tab" => MakeCall(toolName, MakeArgs(("url", "https://example.com"), ("title", "Test"))),
            "click" => MakeCall(toolName, MakeArgs(("selector", "#test"))),
            "fill" => MakeCall(toolName, MakeArgs(("selector", "#input"), ("value", "hello"))),
            "scroll" => MakeCall(toolName, MakeArgs(("direction", "down"))),
            _ => MakeCall(toolName)
        };

        var ex = await Record.ExceptionAsync(() => _executor.ExecuteToolAsync(call));
        Assert.Null(ex);
    }

    [Fact]
    public void GetAllTools_ReturnsExactly10()
    {
        var tools = AiToolDefinitions.GetAllTools();
        Assert.Equal(10, tools.Count);
    }

    [Fact]
    public void GetAllTools_AllNamesAreUnique()
    {
        var tools = AiToolDefinitions.GetAllTools();
        var names = tools.Select(t => t.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void ToolSchemaBuilder_ProducesValidSchema()
    {
        var tools = AiToolDefinitions.GetAllTools();
        var schema = AiToolSchemaBuilder.BuildToolsArray(tools);
        var json = JsonSerializer.Serialize(schema);
        Assert.NotNull(json);
        Assert.Contains("\"search\"", json);
        Assert.Contains("\"click\"", json);
        Assert.Contains("\"fill\"", json);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(10, doc.RootElement.GetArrayLength());

        foreach (var toolEl in doc.RootElement.EnumerateArray())
        {
            var type = toolEl.GetProperty("type").GetString();
            Assert.Equal("function", type);
            var fn = toolEl.GetProperty("function");
            var name = fn.GetProperty("name").GetString();
            Assert.NotNull(name);
            Assert.False(string.IsNullOrWhiteSpace(name));
            var desc = fn.GetProperty("description").GetString();
            Assert.NotNull(desc);
            Assert.False(string.IsNullOrWhiteSpace(desc));
            Assert.True(fn.TryGetProperty("parameters", out _));
        }
    }

    [Theory]
    [InlineData("read_page")]
    [InlineData("open_tab")]
    [InlineData("click")]
    [InlineData("fill")]
    [InlineData("list_elements")]
    [InlineData("scroll")]
    public async Task AllTools_AuthorizeOnActiveTab(string toolName)
    {
        AiToolCall call = toolName switch
        {
            "open_tab" => MakeCall(toolName, MakeArgs(("url", "https://example.com"), ("title", "T"))),
            "click" => MakeCall(toolName, MakeArgs(("selector", "#btn"))),
            "fill" => MakeCall(toolName, MakeArgs(("selector", "#input"), ("value", "v"))),
            "scroll" => MakeCall(toolName, MakeArgs(("direction", "down"))),
            _ => MakeCall(toolName)
        };

        var result = await _executor.ExecuteToolAsync(call);
        Assert.False(result.StartsWith("Authorization denied"),
            $"Tool '{toolName}' was denied. Result: {result}");
    }
}
