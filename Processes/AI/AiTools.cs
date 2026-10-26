using System.Text.Json;

namespace TabloidBrowser;

public sealed record AiTool(
    string Name,
    string Description,
    object Parameters,
    bool RequiresWrite = false
);

public sealed record AiToolCall(
    string Id,
    string Name,
    string Arguments
);

public sealed record AiToolResult(
    string ToolCallId,
    string Content
);

public sealed record AiResponse(
    string? Content,
    List<AiToolCall>? ToolCalls
);

public sealed record SearchResult(
    string Title,
    string Url,
    string Snippet
);

public static class AiToolSchemaBuilder
{
    public static object BuildToolSchema(AiTool tool)
    {
        return new
        {
            type = "function",
            function = new
            {
                name = tool.Name,
                description = tool.Description,
                parameters = tool.Parameters
            }
        };
    }

    public static object BuildToolsArray(List<AiTool> tools)
    {
        return tools.Select(BuildToolSchema).ToList();
    }
}
