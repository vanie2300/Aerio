using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Xunit;

namespace TabloidBrowser.Tests;

public class AiServiceTests
{
    [Fact]
    public void IsOpenAICompatible_NimProvider_IsTrue()
    {
        using var svc = new AiService { Provider = "NVIDIA NIM", Endpoint = "https://integrate.api.nvidia.com/v1" };
        Assert.True(svc.IsOpenAICompatible);
    }

    [Fact]
    public void IsOpenAICompatible_OllamaCloud_IsFalse()
    {
        using var svc = new AiService { Provider = "Ollama Cloud", Endpoint = "https://ollama.com" };
        Assert.False(svc.IsOpenAICompatible);
    }

    [Fact]
    public void IsOpenAICompatible_CustomProvider_IsTrueWithoutV1Suffix()
    {
        using var svc = new AiService { Provider = "OpenAI-compatible (custom)", Endpoint = "http://localhost:8000" };
        Assert.True(svc.IsOpenAICompatible);
    }

    [Fact]
    public void IsOpenAICompatible_LocalOllama_IsFalse()
    {
        using var svc = new AiService { Provider = "Ollama (local)", Endpoint = "http://localhost:11434" };
        Assert.False(svc.IsOpenAICompatible);
    }

    [Fact]
    public async Task CompleteAsync_OpenAiCompatibleApi_UsesV1ChatEndpointWithoutRequiringKey()
    {
        var handler = new StubHandler((request, _) =>
        {
            Assert.Equal("http://localhost:1234/v1/chat/completions", request.RequestUri?.ToString());
            Assert.Null(request.Headers.Authorization);
            return JsonResponse("""{"choices":[{"message":{"content":"response"}}]}""");
        });
        using var client = new HttpClient(handler);
        using var svc = new AiService(client)
        {
            Provider = "OpenAI-compatible (custom)",
            Endpoint = "http://localhost:1234",
            Model = "local-model"
        };

        Assert.Equal("response", await svc.CompleteAsync([new ChatMessage("user", "hello")]));
    }

    [Fact]
    public async Task CompleteAsync_LocalOllama_UsesNativeEndpointWithoutApiKey()
    {
        var handler = new StubHandler(async (request, ct) =>
        {
            Assert.Equal("http://localhost:11434/api/chat", request.RequestUri?.ToString());
            Assert.Null(request.Headers.Authorization);
            var body = await request.Content!.ReadAsStringAsync(ct);
            Assert.Contains("\"stream\":false", body);
            return JsonResponse("""{"message":{"content":"local response"},"done":true}""");
        });
        using var client = new HttpClient(handler);
        using var svc = new AiService(client)
        {
            Provider = "Ollama (local)",
            Endpoint = "http://localhost:11434",
            Model = "llama3.2"
        };

        Assert.Equal("local response", await svc.CompleteAsync([new ChatMessage("user", "hello")]));
    }

    [Fact]
    public async Task FetchModelsAsync_LocalOllama_UsesNativeModelListFormat()
    {
        var handler = new StubHandler((request, _) =>
        {
            Assert.Equal("http://localhost:11434/api/tags", request.RequestUri?.ToString());
            return JsonResponse("""{"models":[{"name":"llama3.2:latest"}]}""");
        });
        using var client = new HttpClient(handler);

        var models = await AiService.FetchModelsAsync(
            "http://localhost:11434", "", "Ollama (local)", httpClient: client);

        Assert.Equal(["llama3.2:latest"], models);
    }

    [Fact]
    public async Task FetchModelsAsync_OpenAiCompatible_UsesVersionedModelListAndApiKey()
    {
        var handler = new StubHandler((request, _) =>
        {
            Assert.Equal("https://api.example.test/v1/models", request.RequestUri?.ToString());
            Assert.Equal("secret", request.Headers.Authorization?.Parameter);
            return JsonResponse("""{"data":[{"id":"model-a"}]}""");
        });
        using var client = new HttpClient(handler);

        var models = await AiService.FetchModelsAsync(
            "https://api.example.test/v1", "secret", "OpenAI-compatible (custom)", httpClient: client);

        Assert.Equal(["model-a"], models);
    }

    [Fact]
    public async Task FetchModelsAsync_ReportsServerErrorsInsteadOfReturningAnEmptyList()
    {
        var handler = new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"error":"invalid API key"}""", Encoding.UTF8, "application/json")
            });
        using var client = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            AiService.FetchModelsAsync(
                "https://api.example.test/v1", "invalid", "OpenAI-compatible (custom)", httpClient: client));

        Assert.Contains("401", error.Message);
        Assert.Contains("invalid API key", error.Message);
    }

    [Fact]
    public async Task CompleteWithToolsAsync_UsesOpenAiToolChoiceAndStringArguments()
    {
        var handler = new StubHandler(async (request, ct) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("none", body.RootElement.GetProperty("tool_choice").GetString());
            var args = body.RootElement.GetProperty("messages")
                .EnumerateArray()
                .Last()
                .GetProperty("tool_calls")[0]
                .GetProperty("function")
                .GetProperty("arguments");
            Assert.Equal(JsonValueKind.String, args.ValueKind);
            Assert.Equal("""{"query":"test"}""", args.GetString());
            return JsonResponse("""{"choices":[{"message":{"content":"done"}}]}""");
        });
        using var client = new HttpClient(handler);
        using var svc = new AiService(client)
        {
            Provider = "OpenAI-compatible (custom)",
            Endpoint = "http://localhost:1234/v1",
            Model = "local-model"
        };
        var message = new ChatMessage(
            "assistant",
            "",
            [new AiToolCall("call-1", "search", """{"query":"test"}""")]);

        var response = await svc.CompleteWithToolsAsync(
            [message],
            [new AiTool("search", "Search", new { type = "object" })],
            "none");

        Assert.Equal("done", response.Content);
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> send)
            : this((request, ct) => Task.FromResult(send(request, ct)))
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}