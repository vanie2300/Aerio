using Xunit;
using TabloidBrowser;

namespace TabloidBrowser.Tests;

public class AiToolDefinitionsTests
{
    private static readonly string[] AllToolNames =
    [
        "web_search", "search_and_read", "search_followup",
        "get_page_content", "get_page_metadata", "extract_page_links",
        "navigate_to", "open_tab"
    ];

    private static int TotalToolCount => AiToolDefinitions.GetAllTools().Count;

    [Fact]
    public void GetAllTools_NoFilter_ReturnsAllTools()
    {
        var tools = AiToolDefinitions.GetAllTools();
        Assert.Equal(AllToolNames.Length, tools.Count);
    }

    [Fact]
    public void GetAllTools_NullFilter_ReturnsAllTools()
    {
        var tools = AiToolDefinitions.GetAllTools(null);
        Assert.Equal(AllToolNames.Length, tools.Count);
    }

    [Fact]
    public void GetAllTools_AllDisabled_ReturnsNone()
    {
        var enabled = AllToolNames.ToDictionary(n => n, _ => false);
        var tools = AiToolDefinitions.GetAllTools(enabled);
        Assert.Empty(tools);
    }

    [Fact]
    public void GetAllTools_SomeDisabled_FiltersCorrectly()
    {
        var enabled = new Dictionary<string, bool>
        {
            ["web_search"] = true,
            ["navigate_to"] = false,
            ["open_tab"] = false,
        };
        var tools = AiToolDefinitions.GetAllTools(enabled);
        Assert.Equal(TotalToolCount - 2, tools.Count);
        Assert.Contains(tools, t => t.Name == "web_search");
        Assert.DoesNotContain(tools, t => t.Name == "navigate_to");
        Assert.DoesNotContain(tools, t => t.Name == "open_tab");
    }

    [Fact]
    public void GetAllTools_EmptyDict_ReturnsAll()
    {
        var tools = AiToolDefinitions.GetAllTools(new Dictionary<string, bool>());
        Assert.Equal(TotalToolCount, tools.Count);
    }

    [Fact]
    public void GetAllTools_UnknownTools_DefaultToEnabled()
    {
        var enabled = new Dictionary<string, bool>
        {
            ["nonexistent_tool"] = false,
        };
        var tools = AiToolDefinitions.GetAllTools(enabled);
        Assert.Equal(TotalToolCount, tools.Count);
    }

    [Theory]
    [InlineData("web_search")]
    [InlineData("search_and_read")]
    [InlineData("search_followup")]
    [InlineData("get_page_content")]
    [InlineData("get_page_metadata")]
    [InlineData("extract_page_links")]
    [InlineData("navigate_to")]
    [InlineData("open_tab")]
    public void GetAllTools_EachToolCanBeDisabled(string toolName)
    {
        var enabled = new Dictionary<string, bool> { [toolName] = false };
        var tools = AiToolDefinitions.GetAllTools(enabled);
        Assert.DoesNotContain(tools, t => t.Name == toolName);
    }

    [Fact]
    public void GetAllTools_ToolNamesAreUnique()
    {
        var tools = AiToolDefinitions.GetAllTools();
        var names = tools.Select(t => t.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }
}
