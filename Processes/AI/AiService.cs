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

    public string Endpoint { get; set; } = "https://ollama.com";
    public string Provider { get; set; } = "Ollama Cloud";
    public string Model { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string SystemPrompt { get; set; } = "";
    public int MaxTokens { get; set; } = 2048;
    public double Temperature { get; set; } = 0.7;

    /// <summary>
    /// True when the configured provider/endpoint speaks the OpenAI-compatible
    /// chat API (e.g. NVIDIA NIM at /v1/chat/completions). Native Ollama uses
    /// a different request/response shape.
    /// </summary>
    public bool IsOpenAICompatible =>
        Provider.Equals("NVIDIA NIM", StringComparison.OrdinalIgnoreCase) ||
        Provider.Contains("NIM", StringComparison.OrdinalIgnoreCase) ||
        (Endpoint?.Contains("/v1", StringComparison.OrdinalIgnoreCase) ?? false);

    private string GetBaseUrl()
    {
        var raw = string.IsNullOrWhiteSpace(Endpoint) ? "https://ollama.com" : Endpoint;
        return raw.TrimEnd('/');
    }

    private string GetChatUrl()
    {
        var baseUrl = GetBaseUrl();
        // For NVIDIA NIM the preset already ends with /v1, so the chat endpoint
        // is .../v1/chat/completions. Native Ollama uses /api/chat.
        if (IsOpenAICompatible)
            return baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                ? baseUrl + "/chat/completions"
                : baseUrl + "/v1/chat/completions";
        return baseUrl + "/api/chat";
    }

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
                return await CompleteNativeOllamaCoreAsync(messages, tools, ct);
            }
            catch (Exception ex) when (attempt < MaxRetries && !ct.IsCancellationRequested && IsRetryable(ex))
            {
                var delay = RetryDelaysMs[Math.Min(attempt, RetryDelaysMs.Length - 1)];
                Debug.WriteLine($"AiService.CompleteWithToolsAsync attempt {attempt + 1} failed, retrying in {delay}ms: {ex.Message}");
                await Task.Delay(delay, ct);
            }
        }

        return await CompleteNativeOllamaCoreAsync(messages, tools, ct);
    }

    public async Task<AiResponse> CompleteStreamingAsync(
        List<ChatMessage> messages,
        Action<string>? onContentDelta = null,
        Action? onRetry = null,
        List<AiTool>? tools = null,
        string? toolChoice = "auto",
        CancellationToken ct = default)
    {
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                return await CompleteNativeOllamaStreamingCoreAsync(messages, onContentDelta, tools, ct);
            }
            catch (Exception ex) when (attempt < MaxRetries && !ct.IsCancellationRequested && IsRetryable(ex))
            {
                onRetry?.Invoke();
                var delay = RetryDelaysMs[Math.Min(attempt, RetryDelaysMs.Length - 1)];
                Debug.WriteLine($"AiService.CompleteStreamingAsync attempt {attempt + 1} failed, retrying in {delay}ms: {ex.Message}");
                await Task.Delay(delay, ct);
            }
        }

        return await CompleteNativeOllamaStreamingCoreAsync(messages, onContentDelta, tools, ct);
    }

    private static readonly HttpClient _fetchHttp = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static async Task<List<string>> FetchModelsAsync(string endpoint, string apiKey, string provider, CancellationToken ct = default)
    {
        try
        {
            var baseUrl = endpoint.TrimEnd('/');
            // NVIDIA NIM presets already include /v1, so the models route is just
            // .../v1/models; bare hosts use .../v1/models (OpenAI-compatible) too.
            var modelsEndpoint = baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                ? baseUrl + "/models"
                : baseUrl + "/v1/models";
            using var request = new HttpRequestMessage(HttpMethod.Get, modelsEndpoint);
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await _fetchHttp.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var text = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(text);
            var result = new List<string>();
            foreach (var m in doc.RootElement.GetProperty("data").EnumerateArray())
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

    private object BuildNativeOllamaRequestBody(
        string model,
        List<ChatMessage> messages,
        List<AiTool>? tools)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = BuildMessages(messages, openAIFormat: false),
            ["stream"] = false,
            ["options"] = new Dictionary<string, object>
            {
                ["num_predict"] = MaxTokens,
                ["temperature"] = Temperature
            }
        };

        if (tools is { Count: > 0 })
            body["tools"] = AiToolSchemaBuilder.BuildToolsArray(tools);

        return body;
    }

    private object BuildOpenAIRequestBody(
        string model,
        List<ChatMessage> messages,
        List<AiTool>? tools)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = BuildMessages(messages, openAIFormat: true),
            ["stream"] = false,
            ["temperature"] = Temperature,
            ["max_tokens"] = MaxTokens,
            ["top_p"] = 0.95
        };

        if (tools is { Count: > 0 })
        {
            body["tools"] = AiToolSchemaBuilder.BuildToolsArray(tools);
            body["tool_choice"] = "auto";
        }

        AddNimSpecificFields(body);

        return body;
    }

    private void AddNimSpecificFields(Dictionary<string, object> body)
    {
        if (!Provider.Equals("NVIDIA NIM", StringComparison.OrdinalIgnoreCase))
            return;
        // NVIDIA NIM: extra_body entries are merged into the JSON root. For
        // DeepSeek reasoner models, disable chain-of-thought so responses (and
        // tool calls) stream in the normal content/tool_calls fields.
        body["chat_template_kwargs"] = new Dictionary<string, object>
        {
            ["thinking"] = false
        };
    }

    private object BuildNativeOllamaStreamingRequestBody(
        string model,
        List<ChatMessage> messages,
        List<AiTool>? tools)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = BuildMessages(messages, openAIFormat: false),
            ["stream"] = true,
            ["options"] = new Dictionary<string, object>
            {
                ["num_predict"] = MaxTokens,
                ["temperature"] = Temperature
            }
        };

        if (tools is { Count: > 0 })
            body["tools"] = AiToolSchemaBuilder.BuildToolsArray(tools);

        return body;
    }

    private object BuildOpenAIStreamingRequestBody(
        string model,
        List<ChatMessage> messages,
        List<AiTool>? tools)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = BuildMessages(messages, openAIFormat: true),
            ["stream"] = true,
            ["temperature"] = Temperature,
            ["max_tokens"] = MaxTokens,
            ["top_p"] = 0.95
        };

        if (tools is { Count: > 0 })
        {
            body["tools"] = AiToolSchemaBuilder.BuildToolsArray(tools);
            body["tool_choice"] = "auto";
        }

        AddNimSpecificFields(body);

        return body;
    }

    private async Task<AiResponse> CompleteNativeOllamaCoreAsync(
        List<ChatMessage> messages,
        List<AiTool>? tools,
        CancellationToken ct)
    {
        if (IsOpenAICompatible)
            return await CompleteOpenAICoreAsync(messages, tools, ct);

        var baseUrl = GetBaseUrl();
        var model = string.IsNullOrWhiteSpace(Model) ? "llama3.2" : Model;

        if (string.IsNullOrWhiteSpace(ApiKey))
            throw new InvalidOperationException("API key is required for Ollama Cloud. Please enter your API key in Settings > AI.");

        var body = BuildNativeOllamaRequestBody(model, messages, tools);

        var url = GetChatUrl();
        Debug.WriteLine($"[AiService] POST {url} model={model} tools={tools?.Count ?? 0} (non-streaming)");

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
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
            Debug.WriteLine($"[AiService] Response: {(int)response.StatusCode} {response.ReasonPhrase}");
            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync(ct);
                Debug.WriteLine($"[AiService] Error body: {errBody}");
                response.EnsureSuccessStatusCode();
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var errorEl))
            {
                var errorMsg = errorEl.GetString() ?? "Unknown Ollama error";
                Debug.WriteLine($"[AiService] Ollama error: {errorMsg}");
                throw new InvalidOperationException($"Ollama error: {errorMsg}");
            }

            var msg = root.GetProperty("message");
            var content = msg.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String
                ? contentEl.GetString() : null;

            List<AiToolCall>? toolCallList = null;
            if (msg.TryGetProperty("tool_calls", out var toolCallsArr) && toolCallsArr.ValueKind == JsonValueKind.Array)
            {
                toolCallList = new List<AiToolCall>();
                foreach (var tc in toolCallsArr.EnumerateArray())
                {
                    var id = tc.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                    var name = "";
                    var args = "";
                    if (tc.TryGetProperty("function", out var fn))
                    {
                        name = fn.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                        if (fn.TryGetProperty("arguments", out var argsEl))
                        {
                            args = argsEl.ValueKind == JsonValueKind.String
                                ? argsEl.GetString() ?? ""
                                : argsEl.GetRawText();
                        }
                    }
                    toolCallList.Add(new AiToolCall(id, name, args));
                }
            }

            return new AiResponse(content, toolCallList);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            Debug.WriteLine($"AiService.CompleteNativeOllamaCoreAsync timed out: {ex}");
            throw new TimeoutException("AI request timed out.", ex);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"AiService.CompleteNativeOllamaCoreAsync failed: {ex}");
            throw;
        }
    }

    private async Task<AiResponse> CompleteNativeOllamaStreamingCoreAsync(
        List<ChatMessage> messages,
        Action<string>? onContentDelta,
        List<AiTool>? tools,
        CancellationToken ct)
    {
        if (IsOpenAICompatible)
            return await CompleteOpenAIStreamingCoreAsync(messages, onContentDelta, tools, ct);

        var baseUrl = GetBaseUrl();
        var model = string.IsNullOrWhiteSpace(Model) ? "llama3.2" : Model;

        if (string.IsNullOrWhiteSpace(ApiKey))
            throw new InvalidOperationException("API key is required for Ollama Cloud. Please enter your API key in Settings > AI.");

        var body = BuildNativeOllamaStreamingRequestBody(model, messages, tools);

        var url = GetChatUrl();
        Debug.WriteLine($"[AiService] POST {url} model={model} tools={tools?.Count ?? 0}");

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
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
            Debug.WriteLine($"[AiService] Response: {(int)response.StatusCode} {response.ReasonPhrase}");
            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync(ct);
                Debug.WriteLine($"[AiService] Error body: {errBody}");
                response.EnsureSuccessStatusCode();
            }

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            var contentBuilder = new StringBuilder();
            var toolCalls = new Dictionary<int, (string Id, string Name, StringBuilder Arguments)>();
            bool gotMessage = false;
            const int MaxResponseChars = 1_000_000;

            while (!reader.EndOfStream && !ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("error", out var errorEl))
                    {
                        var errorMsg = errorEl.GetString() ?? "Unknown Ollama error";
                        Debug.WriteLine($"[AiService] Ollama error: {errorMsg}");
                        throw new InvalidOperationException($"Ollama error: {errorMsg}");
                    }

                    if (!root.TryGetProperty("message", out var msg))
                        continue;

                    gotMessage = true;

                    if (msg.TryGetProperty("content", out var contentEl) &&
                        contentEl.ValueKind == JsonValueKind.String)
                    {
                        var text = contentEl.GetString() ?? "";
                        contentBuilder.Append(text);
                        if (contentBuilder.Length > MaxResponseChars)
                        {
                            Debug.WriteLine($"[AiService] Response exceeded {MaxResponseChars} chars, stopping");
                            break;
                        }
                        onContentDelta?.Invoke(text);
                        await Task.Yield();
                    }

                    if (msg.TryGetProperty("tool_calls", out var toolCallsArr) &&
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

                            if (tc.TryGetProperty("function", out var fn2) &&
                                fn2.TryGetProperty("arguments", out var argsEl))
                            {
                                var argsStr = argsEl.ValueKind == JsonValueKind.String
                                    ? argsEl.GetString() ?? ""
                                    : argsEl.GetRawText();
                                toolCalls[index] = (toolCalls[index].Id, toolCalls[index].Name,
                                    toolCalls[index].Arguments.Append(argsStr));
                            }
                        }
                    }

                    if (root.TryGetProperty("done", out var doneEl) && doneEl.GetBoolean())
                        break;
                }
                catch (JsonException)
                {
                    Debug.WriteLine($"[AiService] Failed to parse Ollama NDJSON chunk: {line}");
                }
            }

            if (!gotMessage && contentBuilder.Length == 0 && toolCalls.Count == 0)
            {
                Debug.WriteLine("[AiService] No messages received from Ollama — response may have been empty or malformed");
                throw new InvalidOperationException("Ollama returned an empty response. Check your API key and model name.");
            }

            var toolCallList = toolCalls.Count > 0
                ? toolCalls.OrderBy(kv => kv.Key)
                    .Select(kv => new AiToolCall(kv.Value.Id, kv.Value.Name, kv.Value.Arguments.ToString()))
                    .ToList()
                : null;

            var content = contentBuilder.Length > 0 ? contentBuilder.ToString() : null;
            Debug.WriteLine($"[AiService] Response complete: content={content?.Length ?? 0} chars, tools={toolCallList?.Count ?? 0}");
            return new AiResponse(content, toolCallList);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            Debug.WriteLine($"[AiService] Streaming timed out: {ex}");
            throw new TimeoutException("AI streaming request timed out.", ex);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AiService] Streaming failed: {ex}");
            throw;
        }
    }

    private async Task<AiResponse> CompleteOpenAICoreAsync(
        List<ChatMessage> messages,
        List<AiTool>? tools,
        CancellationToken ct)
    {
        var model = string.IsNullOrWhiteSpace(Model) ? "" : Model;
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException("A model is required. Please choose a model in Settings > AI.");

        if (string.IsNullOrWhiteSpace(ApiKey))
            throw new InvalidOperationException("An API key is required for NVIDIA NIM. Please enter your NVIDIA NIM API key in Settings > AI.");

        var body = BuildOpenAIRequestBody(model, messages, tools);

        var url = GetChatUrl();
        Debug.WriteLine($"[AiService] POST {url} model={model} tools={tools?.Count ?? 0} (non-streaming, OpenAI format)");

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
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
            Debug.WriteLine($"[AiService] Response: {(int)response.StatusCode} {response.ReasonPhrase}");
            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync(ct);
                Debug.WriteLine($"[AiService] Error body: {errBody}");
                response.EnsureSuccessStatusCode();
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var errorEl))
            {
                var errorMsg = errorEl.TryGetProperty("message", out var msgEl)
                    ? msgEl.GetString() ?? "Unknown API error"
                    : errorEl.GetString() ?? "Unknown API error";
                Debug.WriteLine($"[AiService] API error: {errorMsg}");
                throw new InvalidOperationException($"API error: {errorMsg}");
            }

            if (!root.TryGetProperty("choices", out var choices) || !choices.EnumerateArray().Any())
                throw new InvalidOperationException("The API returned no choices.");

            var message = choices[0].GetProperty("message");
            var content = message.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String
                ? contentEl.GetString() : null;

            // NVIDIA NIM reasoner models may deliver the answer via
            // "reasoning_content" (thinking is disabled, but some responses still
            // nest it). Surface it so the assistant isn't blank.
            if (string.IsNullOrEmpty(content) &&
                message.TryGetProperty("reasoning_content", out var reasonEl) &&
                reasonEl.ValueKind == JsonValueKind.String)
            {
                content = reasonEl.GetString();
            }

            List<AiToolCall>? toolCallList = null;
            if (message.TryGetProperty("tool_calls", out var toolCallsArr) && toolCallsArr.ValueKind == JsonValueKind.Array)
            {
                toolCallList = new List<AiToolCall>();
                foreach (var tc in toolCallsArr.EnumerateArray())
                {
                    var id = tc.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                    var name = "";
                    var args = "";
                    if (tc.TryGetProperty("function", out var fn))
                    {
                        name = fn.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                        if (fn.TryGetProperty("arguments", out var argsEl))
                        {
                            args = argsEl.ValueKind == JsonValueKind.String
                                ? argsEl.GetString() ?? ""
                                : argsEl.GetRawText();
                        }
                    }
                    toolCallList.Add(new AiToolCall(id, name, args));
                }
            }

            return new AiResponse(content, toolCallList);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            Debug.WriteLine($"AiService.CompleteOpenAICoreAsync timed out: {ex}");
            throw new TimeoutException("AI request timed out.", ex);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"AiService.CompleteOpenAICoreAsync failed: {ex}");
            throw;
        }
    }

    private async Task<AiResponse> CompleteOpenAIStreamingCoreAsync(
        List<ChatMessage> messages,
        Action<string>? onContentDelta,
        List<AiTool>? tools,
        CancellationToken ct)
    {
        var model = string.IsNullOrWhiteSpace(Model) ? "" : Model;
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException("A model is required. Please choose a model in Settings > AI.");

        if (string.IsNullOrWhiteSpace(ApiKey))
            throw new InvalidOperationException("An API key is required for NVIDIA NIM. Please enter your NVIDIA NIM API key in Settings > AI.");

        var body = BuildOpenAIStreamingRequestBody(model, messages, tools);

        var url = GetChatUrl();
        Debug.WriteLine($"[AiService] POST {url} model={model} tools={tools?.Count ?? 0} (OpenAI format)");

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
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
            Debug.WriteLine($"[AiService] Response: {(int)response.StatusCode} {response.ReasonPhrase}");
            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync(ct);
                Debug.WriteLine($"[AiService] Error body: {errBody}");
                response.EnsureSuccessStatusCode();
            }

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            var contentBuilder = new StringBuilder();
            var toolCalls = new Dictionary<int, (string Id, string Name, StringBuilder Arguments)>();
            bool gotChoices = false;
            const int MaxResponseChars = 1_000_000;

            while (!reader.EndOfStream && !ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (string.IsNullOrWhiteSpace(line)) continue;

                // OpenAI streams SSE events: "data: {json}". Ignore keep-alive
                // comments and terminate at "data: [DONE]".
                if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    continue;
                var payload = line.Substring("data:".Length).Trim();
                if (payload == "[DONE]") break;
                if (payload.Length == 0) continue;

                try
                {
                    using var doc = JsonDocument.Parse(payload);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("error", out var errorEl))
                    {
                        var errorMsg = errorEl.TryGetProperty("message", out var msgEl)
                            ? msgEl.GetString() ?? "Unknown API error"
                            : "Unknown API error";
                        Debug.WriteLine($"[AiService] API error: {errorMsg}");
                        throw new InvalidOperationException($"API error: {errorMsg}");
                    }

                    if (!root.TryGetProperty("choices", out var choices) || !choices.EnumerateArray().Any())
                        continue;

                    gotChoices = true;
                    var delta = choices[0].TryGetProperty("delta", out var deltaEl) ? deltaEl : default;

                    if (delta.ValueKind == JsonValueKind.Object)
                    {
                        // content (and reasoning_content for NIM reasoner models)
                        string? text = null;
                        if (delta.TryGetProperty("content", out var contentEl) &&
                            contentEl.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(contentEl.GetString()))
                            text = contentEl.GetString();
                        else if (delta.TryGetProperty("reasoning_content", out var reasonEl) &&
                                 reasonEl.ValueKind == JsonValueKind.String)
                            text = reasonEl.GetString();

                        if (!string.IsNullOrEmpty(text))
                        {
                            contentBuilder.Append(text);
                            if (contentBuilder.Length > MaxResponseChars)
                            {
                                Debug.WriteLine($"[AiService] Response exceeded {MaxResponseChars} chars, stopping");
                                break;
                            }
                            onContentDelta?.Invoke(text);
                            await Task.Yield();
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

                                else if (string.IsNullOrWhiteSpace(toolCalls[index].Id))
                                {
                                    var id = tc.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                                    toolCalls[index] = (id, toolCalls[index].Name, toolCalls[index].Arguments);
                                }

                                if (tc.TryGetProperty("function", out var fn2) &&
                                    fn2.TryGetProperty("arguments", out var argsEl))
                                {
                                    var argsStr = argsEl.ValueKind == JsonValueKind.String
                                        ? argsEl.GetString() ?? ""
                                        : argsEl.GetRawText();
                                    toolCalls[index] = (toolCalls[index].Id, toolCalls[index].Name,
                                        toolCalls[index].Arguments.Append(argsStr));
                                }
                            }
                        }
                    }
                }
                catch (JsonException)
                {
                    Debug.WriteLine($"[AiService] Failed to parse SSE chunk: {line}");
                }
            }

            if (!gotChoices && contentBuilder.Length == 0 && toolCalls.Count == 0)
            {
                Debug.WriteLine("[AiService] No messages received from API — response may have been empty or malformed");
                throw new InvalidOperationException("The API returned an empty response. Check your API key and model name.");
            }

            var toolCallList = toolCalls.Count > 0
                ? toolCalls.OrderBy(kv => kv.Key)
                    .Select(kv => new AiToolCall(kv.Value.Id, kv.Value.Name, kv.Value.Arguments.ToString()))
                    .ToList()
                : null;

            var content = contentBuilder.Length > 0 ? contentBuilder.ToString() : null;
            Debug.WriteLine($"[AiService] Response complete: content={content?.Length ?? 0} chars, tools={toolCallList?.Count ?? 0}");
            return new AiResponse(content, toolCallList);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            Debug.WriteLine($"[AiService] OpenAI streaming timed out: {ex}");
            throw new TimeoutException("AI streaming request timed out.", ex);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AiService] OpenAI streaming failed: {ex}");
            throw;
        }
    }

    private List<object> BuildMessages(List<ChatMessage> messages, bool openAIFormat)
    {
        var list = new List<object>(messages.Count + 1);
        if (!string.IsNullOrWhiteSpace(SystemPrompt))
        {
            if (openAIFormat)
                list.Add(new { role = "system", content = SystemPrompt });
            else
                list.Add(new { role = "system", content = SystemPrompt });
        }
        foreach (var m in messages)
        {
            if (m.ToolCalls is { Count: > 0 })
            {
                if (openAIFormat)
                {
                    var toolCallsArray = m.ToolCalls.Select(tc =>
                    {
                        object? argsObj = null;
                        if (!string.IsNullOrWhiteSpace(tc.Arguments))
                        {
                            try
                            {
                                using var argDoc = JsonDocument.Parse(tc.Arguments);
                                argsObj = argDoc.RootElement.Clone();
                            }
                            catch { argsObj = tc.Arguments; }
                        }
                        return new
                        {
                            id = tc.Id,
                            type = "function",
                            function = new
                            {
                                name = tc.Name,
                                arguments = argsObj
                            }
                        };
                    }).ToList();
                    list.Add(new { role = "assistant", content = m.Content ?? "", tool_calls = toolCallsArray });
                }
                else
                {
                    var toolCallsArray = m.ToolCalls.Select(tc =>
                    {
                        object? argsObj = null;
                        if (!string.IsNullOrWhiteSpace(tc.Arguments))
                        {
                            try
                            {
                                using var argDoc = JsonDocument.Parse(tc.Arguments);
                                argsObj = argDoc.RootElement.Clone();
                            }
                            catch { argsObj = tc.Arguments; }
                        }
                        return new
                        {
                            function = new
                            {
                                name = tc.Name,
                                arguments = argsObj
                            }
                        };
                    }).ToList();
                    list.Add(new { role = m.Role, content = m.Content ?? "", tool_calls = toolCallsArray });
                }
            }
            else if (m.ToolCallId is not null)
            {
                if (openAIFormat)
                    list.Add(new { role = "tool", tool_call_id = m.ToolCallId, content = m.Content ?? "" });
                else
                    list.Add(new { role = "tool", tool_name = m.ToolCallId, content = m.Content ?? "" });
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
        if (ex is TimeoutException { InnerException: OperationCanceledException })
            return false;

        if (ex is HttpRequestException httpEx)
        {
            var code = (int?)httpEx.StatusCode;
            if (code is 401 or 403 or 405 or 400)
                return false;
        }

        return ex is HttpRequestException
            or TimeoutException
            or IOException
            or TaskCanceledException { InnerException: IOException };
    }

    public void Dispose() => _http.Dispose();
}
