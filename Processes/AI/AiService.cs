using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TabloidBrowser;

public sealed class AiService : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    private const int MaxRetries = 3;
    private static readonly int[] RetryDelaysMs = [1000, 3000, 8000];

    public string Endpoint { get; set; } = "http://localhost:11434/v1";
    public string Model { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string SystemPrompt { get; set; } = "";
    public int MaxTokens { get; set; } = 2048;
    public double Temperature { get; set; } = 0.7;

    public async Task<string> CompleteAsync(List<ChatMessage> messages, CancellationToken ct = default)
    {
        var response = await CompleteWithToolsAsync(messages, null, null, ct);
        return response.Content ?? "";
    }

    public async Task<AiResponse> CompleteWithToolsAsync(
        List<ChatMessage> messages,
        List<AiTool>? tools = null,
        string? toolChoice = "auto",
        CancellationToken ct = default)
    {
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                return await CompleteWithToolsCoreAsync(messages, tools, toolChoice, ct);
            }
            catch (Exception ex) when (attempt < MaxRetries && !ct.IsCancellationRequested && IsRetryable(ex))
            {
                var delay = RetryDelaysMs[Math.Min(attempt, RetryDelaysMs.Length - 1)];
                Debug.WriteLine($"AiService.CompleteWithToolsAsync attempt {attempt + 1} failed, retrying in {delay}ms: {ex.Message}");
                await Task.Delay(delay, ct);
            }
        }

        return await CompleteWithToolsCoreAsync(messages, tools, toolChoice, ct);
    }

    private async Task<AiResponse> CompleteWithToolsCoreAsync(
        List<ChatMessage> messages,
        List<AiTool>? tools,
        string? toolChoice,
        CancellationToken ct)
    {
        var endpoint = string.IsNullOrWhiteSpace(Endpoint) ? "http://localhost:11434/v1" : Endpoint;
        var model = string.IsNullOrWhiteSpace(Model) ? "llama3.2" : Model;

        var body = BuildRequestBody(model, messages, tools, toolChoice);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            endpoint.TrimEnd('/') + "/chat/completions")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json")
        };

        if (!string.IsNullOrWhiteSpace(ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);

        try
        {
            using var response = await _http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            return AiResponseParser.Parse(doc.RootElement);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            Debug.WriteLine($"AiService.CompleteAsync timed out: {ex}");
            throw new TimeoutException("AI request timed out.", ex);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"AiService.CompleteAsync failed: {ex}");
            throw;
        }
    }

    public async Task<AiResponse> CompleteStreamingAsync(
        List<ChatMessage> messages,
        Action<string>? onContentDelta = null,
        List<AiTool>? tools = null,
        string? toolChoice = "auto",
        CancellationToken ct = default)
    {
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                return await CompleteStreamingCoreAsync(messages, onContentDelta, tools, toolChoice, ct);
            }
            catch (Exception ex) when (attempt < MaxRetries && !ct.IsCancellationRequested && IsRetryable(ex))
            {
                onContentDelta?.Invoke("\n\n[Retrying...]\n\n");
                var delay = RetryDelaysMs[Math.Min(attempt, RetryDelaysMs.Length - 1)];
                Debug.WriteLine($"AiService.CompleteStreamingAsync attempt {attempt + 1} failed, retrying in {delay}ms: {ex.Message}");
                await Task.Delay(delay, ct);
            }
        }

        return await CompleteStreamingCoreAsync(messages, onContentDelta, tools, toolChoice, ct);
    }

    private async Task<AiResponse> CompleteStreamingCoreAsync(
        List<ChatMessage> messages,
        Action<string>? onContentDelta,
        List<AiTool>? tools,
        string? toolChoice,
        CancellationToken ct)
    {
        var endpoint = string.IsNullOrWhiteSpace(Endpoint) ? "http://localhost:11434/v1" : Endpoint;
        var model = string.IsNullOrWhiteSpace(Model) ? "llama3.2" : Model;

        var body = BuildStreamingRequestBody(model, messages, tools, toolChoice);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            endpoint.TrimEnd('/') + "/chat/completions")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json")
        };

        if (!string.IsNullOrWhiteSpace(ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);

        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            var contentBuilder = new StringBuilder();
            var toolCalls = new Dictionary<int, (string Id, string Name, StringBuilder Arguments)>();

            while (!reader.EndOfStream && !ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (!line.StartsWith("data: ")) continue;

                var data = line[6..];
                if (data == "[DONE]") break;

                try
                {
                    using var doc = JsonDocument.Parse(data);
                    var root = doc.RootElement;

                    if (!root.TryGetProperty("choices", out var choices) ||
                        choices.GetArrayLength() == 0)
                        continue;

                    var delta = choices[0].GetProperty("delta");

                    if (delta.TryGetProperty("content", out var contentEl) &&
                        contentEl.ValueKind == JsonValueKind.String)
                    {
                        var text = contentEl.GetString() ?? "";
                        contentBuilder.Append(text);
                        onContentDelta?.Invoke(text);
                    }

                    if (delta.TryGetProperty("tool_calls", out var toolCallsArr) &&
                        toolCallsArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var tc in toolCallsArr.EnumerateArray())
                        {
                            var index = tc.TryGetProperty("index", out var idx) ? idx.GetInt32() : 0;

                            if (!toolCalls.TryGetValue(index, out var existing))
                            {
                                var id = tc.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                                var name = "";
                                if (tc.TryGetProperty("function", out var fn) &&
                                    fn.TryGetProperty("name", out var nameEl))
                                    name = nameEl.GetString() ?? "";
                                toolCalls[index] = (id, name, new StringBuilder());
                            }
                            else
                            {
                                if (tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                                {
                                    var id = idEl.GetString() ?? "";
                                    if (!string.IsNullOrEmpty(id))
                                        toolCalls[index] = (id, existing.Name, existing.Arguments);
                                }
                            }

                            if (tc.TryGetProperty("function", out var fn2) &&
                                fn2.TryGetProperty("arguments", out var argsEl) &&
                                argsEl.ValueKind == JsonValueKind.String)
                            {
                                toolCalls[index].Arguments.Append(argsEl.GetString());
                            }
                        }
                    }
                }
                catch (JsonException)
                {
                    Debug.WriteLine($"Failed to parse SSE chunk: {data}");
                }
            }

            var toolCallList = toolCalls.Count > 0
                ? toolCalls.OrderBy(kv => kv.Key)
                    .Select(kv => new AiToolCall(kv.Value.Id, kv.Value.Name, kv.Value.Arguments.ToString()))
                    .ToList()
                : null;

            var content = contentBuilder.Length > 0 ? contentBuilder.ToString() : null;
            return new AiResponse(content, toolCallList);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            Debug.WriteLine($"AiService.CompleteStreamingAsync timed out: {ex}");
            throw new TimeoutException("AI streaming request timed out.", ex);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"AiService.CompleteStreamingAsync failed: {ex}");
            throw;
        }
    }

    private object BuildStreamingRequestBody(
        string model,
        List<ChatMessage> messages,
        List<AiTool>? tools,
        string? toolChoice)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = BuildMessages(messages),
            ["max_tokens"] = MaxTokens,
            ["temperature"] = Temperature,
            ["stream"] = true
        };

        if (tools is { Count: > 0 })
        {
            body["tools"] = AiToolSchemaBuilder.BuildToolsArray(tools);
            if (toolChoice is not null)
                body["tool_choice"] = toolChoice;
        }

        return body;
    }

    private object BuildRequestBody(
        string model,
        List<ChatMessage> messages,
        List<AiTool>? tools,
        string? toolChoice)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = BuildMessages(messages),
            ["max_tokens"] = MaxTokens,
            ["temperature"] = Temperature,
            ["stream"] = false
        };

        if (tools is { Count: > 0 })
        {
            body["tools"] = AiToolSchemaBuilder.BuildToolsArray(tools);
            if (toolChoice is not null)
                body["tool_choice"] = toolChoice;
        }

        return body;
    }

    private static readonly HttpClient _fetchHttp = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static async Task<List<string>> FetchModelsAsync(string endpoint, string apiKey, string provider, CancellationToken ct = default)
    {
        try
        {

            if (string.Equals(provider, "Ollama", StringComparison.OrdinalIgnoreCase))
            {
                var baseUrl = endpoint.TrimEnd('/');
                if (baseUrl.EndsWith("/v1", StringComparison.Ordinal))
                    baseUrl = baseUrl[..^3].TrimEnd('/');
                var url = baseUrl + "/api/tags";
                using var resp = await _fetchHttp.GetAsync(url, ct);
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var models = new List<string>();
                foreach (var m in doc.RootElement.GetProperty("models").EnumerateArray())
                {
                    var name = m.GetProperty("name").GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                        models.Add(name);
                }
                return models;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint.TrimEnd('/') + "/models");
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await _fetchHttp.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var text = await response.Content.ReadAsStringAsync(ct);
            using var doc2 = JsonDocument.Parse(text);
            var result = new List<string>();
            foreach (var m in doc2.RootElement.GetProperty("data").EnumerateArray())
            {
                var id = m.GetProperty("id").GetString();
                if (!string.IsNullOrWhiteSpace(id))
                    result.Add(id);
            }
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return [];
        }
    }

    private List<object> BuildMessages(List<ChatMessage> messages)
    {
        var list = new List<object>(messages.Count + 1);
        if (!string.IsNullOrWhiteSpace(SystemPrompt))
            list.Add(new { role = "system", content = SystemPrompt });
        foreach (var m in messages)
        {
            if (m.ToolCalls is { Count: > 0 })
            {
                var toolCallsArray = m.ToolCalls.Select(tc => new
                {
                    id = tc.Id,
                    type = "function",
                    function = new
                    {
                        name = tc.Name,
                        arguments = tc.Arguments
                    }
                }).ToList();
                list.Add(new { role = m.Role, content = m.Content ?? "", tool_calls = toolCallsArray });
            }
            else if (m.ToolCallId is not null)
            {
                list.Add(new { role = "tool", tool_call_id = m.ToolCallId, content = m.Content ?? "" });
            }
            else
            {
                list.Add(new { role = m.Role, content = m.Content ?? "" });
            }
        }
        return list;
    }

    private static bool IsRetryable(Exception ex)
    {
        return ex is HttpRequestException
            or TimeoutException
            or IOException
            or TaskCanceledException { InnerException: IOException };
    }

    public void Dispose() => _http.Dispose();
}
