namespace TabloidBrowser;

public sealed record ChatMessage(
    string Role,
    string? Content,
    List<AiToolCall>? ToolCalls = null,
    string? ToolCallId = null
);
