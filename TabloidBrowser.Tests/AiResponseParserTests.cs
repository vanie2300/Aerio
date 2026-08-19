using Xunit;
using System.Text.Json;
using TabloidBrowser;

namespace TabloidBrowser.Tests;

public class AiResponseParserTests
{
    private static JsonElement ParseJson(string json)
    {
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    [Fact]
    public void Parse_ContentOnly_ReturnsContent()
    {
        var json = """
        {
          "choices": [{
            "message": {
              "content": "Hello world",
              "role": "assistant"
            }
          }]
        }
        """;
        var response = AiResponseParser.Parse(ParseJson(json));
        Assert.Equal("Hello world", response.Content);
        Assert.Null(response.ToolCalls);
    }

    [Fact]
    public void Parse_ToolCallsOnly_ReturnsToolCalls()
    {
        var json = """
        {
          "choices": [{
            "message": {
              "role": "assistant",
              "tool_calls": [{
                "id": "call_123",
                "function": {
                  "name": "web_search",
                  "arguments": "{\"query\": \"test\"}"
                }
              }]
            }
          }]
        }
        """;
        var response = AiResponseParser.Parse(ParseJson(json));
        Assert.Null(response.Content);
        Assert.NotNull(response.ToolCalls);
        Assert.Single(response.ToolCalls);
        Assert.Equal("web_search", response.ToolCalls[0].Name);
        Assert.Equal("call_123", response.ToolCalls[0].Id);
    }

    [Fact]
    public void Parse_ContentAndToolCalls_ReturnsBoth()
    {
        var json = """
        {
          "choices": [{
            "message": {
              "content": "Let me search for that",
              "role": "assistant",
              "tool_calls": [{
                "id": "call_456",
                "function": {
                  "name": "web_search",
                  "arguments": "{\"query\": \"weather\"}"
                }
              }]
            }
          }]
        }
        """;
        var response = AiResponseParser.Parse(ParseJson(json));
        Assert.Equal("Let me search for that", response.Content);
        Assert.NotNull(response.ToolCalls);
        Assert.Single(response.ToolCalls);
    }

    [Fact]
    public void Parse_EmptyChoices_ReturnsEmpty()
    {
        var json = """{ "choices": [] }""";
        var response = AiResponseParser.Parse(ParseJson(json));
        Assert.Null(response.Content);
        Assert.Null(response.ToolCalls);
    }

    [Fact]
    public void Parse_MultipleToolCalls_ReturnsAll()
    {
        var json = """
        {
          "choices": [{
            "message": {
              "role": "assistant",
              "tool_calls": [
                {
                  "id": "call_1",
                  "function": { "name": "web_search", "arguments": "{\"query\": \"a\"}" }
                },
                {
                  "id": "call_2",
                  "function": { "name": "open_tab", "arguments": "{\"url\": \"https://example.com\", \"title\": \"Example\"}" }
                }
              ]
            }
          }]
        }
        """;
        var response = AiResponseParser.Parse(ParseJson(json));
        Assert.Equal(2, response.ToolCalls!.Count);
        Assert.Equal("web_search", response.ToolCalls[0].Name);
        Assert.Equal("open_tab", response.ToolCalls[1].Name);
    }

    [Fact]
    public void Parse_NullContent_HandledCorrectly()
    {
        var json = """
        {
          "choices": [{
            "message": {
              "content": null,
              "role": "assistant"
            }
          }]
        }
        """;
        var response = AiResponseParser.Parse(ParseJson(json));
        Assert.Null(response.Content);
    }
}
