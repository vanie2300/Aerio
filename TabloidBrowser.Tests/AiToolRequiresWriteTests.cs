using Xunit;

namespace TabloidBrowser.Tests;

public class AiToolRequiresWriteTests
{
    [Fact]
    public void ReadTools_DoNotRequireWrite()
    {
        var tools = AiToolDefinitions.GetAllTools();
        var readTools = new[] { "search", "read_page", "open_tab", "list_elements", "click", "fill" };
        foreach (var name in readTools)
        {
            var tool = tools.First(t => t.Name == name);
            Assert.False(tool.RequiresWrite, $"Tool '{name}' should not require write");
        }
    }

    [Fact]
    public void WriteTools_RequireWrite()
    {
        var tools = AiToolDefinitions.GetAllTools();
        var writeTools = new[] { "search_and_read", "navigate" };
        foreach (var name in writeTools)
        {
            var tool = tools.First(t => t.Name == name);
            Assert.True(tool.RequiresWrite, $"Tool '{name}' should require write");
        }
    }

    [Fact]
    public void ReadTools_And_WriteTools_CoverAllTools()
    {
        var tools = AiToolDefinitions.GetAllTools();
        var readTools = tools.Where(t => !t.RequiresWrite).Select(t => t.Name).ToList();
        var writeTools = tools.Where(t => t.RequiresWrite).Select(t => t.Name).ToList();
        Assert.Equal(tools.Count, readTools.Count + writeTools.Count);
    }
}
