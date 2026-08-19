using System.Text.Json;

namespace TabloidBrowser;

public sealed record AiTool(
    string Name,
    string Description,
    object Parameters
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

public static class AiResponseParser
{
    public static AiResponse Parse(JsonElement root)
    {
        var choices = root.GetProperty("choices");
        if (choices.GetArrayLength() == 0)
            return new AiResponse(null, null);

        var message = choices[0].GetProperty("message");
        string? content = null;
        List<AiToolCall>? toolCalls = null;

        if (message.TryGetProperty("content", out var contentElement) &&
            contentElement.ValueKind != JsonValueKind.Null)
        {
            content = contentElement.GetString();
        }

        if (message.TryGetProperty("tool_calls", out var toolCallsElement) &&
            toolCallsElement.ValueKind == JsonValueKind.Array &&
            toolCallsElement.GetArrayLength() > 0)
        {
            toolCalls = new List<AiToolCall>();
            foreach (var tc in toolCallsElement.EnumerateArray())
            {
                var id = tc.GetProperty("id").GetString() ?? "";
                var function = tc.GetProperty("function");
                var name = function.GetProperty("name").GetString() ?? "";
                var arguments = function.GetProperty("arguments").GetString() ?? "{}";
                toolCalls.Add(new AiToolCall(id, name, arguments));
            }
        }

        return new AiResponse(content, toolCalls);
    }
}
