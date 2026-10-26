using System.Text.Json;
using Xunit;
using TabloidBrowser;

namespace TabloidBrowser.Tests;

public class AiToolDefinitionsTests
{
    private static int TotalToolCount => AiToolDefinitions.GetAllTools().Count;

    [Fact]
    public void GetAllTools_NoFilter_ReturnsAllTools()
    {
        var tools = AiToolDefinitions.GetAllTools();
        Assert.True(tools.Count > 0, "Should have at least one tool");
        Assert.Equal(TotalToolCount, tools.Count);
    }

    [Fact]
    public void GetAllTools_NullFilter_ReturnsAllTools()
    {
        var tools = AiToolDefinitions.GetAllTools(null);
        Assert.Equal(TotalToolCount, tools.Count);
    }

    [Fact]
    public void GetAllTools_AllDisabled_ReturnsNone()
    {
        var allNames = AiToolDefinitions.GetAllTools().Select(t => t.Name).ToList();
        var enabled = allNames.ToDictionary(n => n, _ => false);
        var tools = AiToolDefinitions.GetAllTools(enabled);
        Assert.Empty(tools);
    }

    [Fact]
    public void GetAllTools_SomeDisabled_FiltersCorrectly()
    {
        var enabled = new Dictionary<string, bool>
        {
            ["search"] = true,
            ["navigate"] = false,
            ["open_tab"] = false,
        };
        var tools = AiToolDefinitions.GetAllTools(enabled);
        Assert.Equal(TotalToolCount - 2, tools.Count);
        Assert.Contains(tools, t => t.Name == "search");
        Assert.DoesNotContain(tools, t => t.Name == "navigate");
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

    [Fact]
    public void ReadPageTool_HasOffsetAndLimitParameters()
    {
        var tool = AiToolDefinitions.ReadPageTool();
        Assert.Equal("read_page", tool.Name);
        var json = JsonSerializer.Serialize(tool.Parameters);
        Assert.Contains("offset", json);
        Assert.Contains("limit", json);
    }

    [Fact]
    public void ReadPageTool_DescriptionMentionsHeadingsAndLinks()
    {
        var tool = AiToolDefinitions.ReadPageTool();
        Assert.Contains("headings", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("links", tool.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SearchAndReadTool_DescriptionMentionsReading()
    {
        var tool = AiToolDefinitions.SearchAndReadTool();
        Assert.Contains("read", tool.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScrollTool_DescriptionIsShort()
    {
        var tool = AiToolDefinitions.ScrollTool();
        Assert.True(tool.Description.Length < 100, $"Scroll description too long: {tool.Description.Length} chars");
    }

    [Fact]
    public void AllToolDescriptions_AreUnder100Chars()
    {
        var tools = AiToolDefinitions.GetAllTools();
        foreach (var tool in tools)
        {
            Assert.True(tool.Description.Length < 100,
                $"Tool '{tool.Name}' description too long: {tool.Description.Length} chars. Max: 100");
        }
    }

    [Fact]
    public void TenTools_Total()
    {
        Assert.Equal(10, TotalToolCount);
    }

    [Fact]
    public void SearchTool_Description_SteersAwayFromSearchEngineSites()
    {
        var tool = AiToolDefinitions.GetAllTools().First(t => t.Name == "search");
        Assert.Contains("search engine", tool.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NavigateAndOpenTab_Descriptions_DiscourageSearchEngineNavigation()
    {
        var navigate = AiToolDefinitions.GetAllTools().First(t => t.Name == "navigate");
        var openTab = AiToolDefinitions.GetAllTools().First(t => t.Name == "open_tab");
        Assert.Contains("search engine", navigate.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("search engine", openTab.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DefaultAiSystemPrompt_MentionsSearchTool()
    {
        var p = BrowserSettings.Default.AiSystemPrompt;
        Assert.Contains("search", p, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never navigate to a search engine site", p, StringComparison.OrdinalIgnoreCase);
    }
}
