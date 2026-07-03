using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TabloidBrowser;

public sealed class AiService : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public string Endpoint { get; set; } = "http://localhost:11434/v1";
    public string Model { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string SystemPrompt { get; set; } = "";
    public int MaxTokens { get; set; } = 2048;
    public double Temperature { get; set; } = 0.7;

    public async Task<string> CompleteAsync(List<ChatMessage> messages, CancellationToken ct = default)
    {
        var endpoint = string.IsNullOrWhiteSpace(Endpoint) ? "http://localhost:11434/v1" : Endpoint;
        var model = string.IsNullOrWhiteSpace(Model) ? "llama3.2" : Model;

        var body = new
        {
            model,
            messages = BuildMessages(messages),
            max_tokens = MaxTokens,
            temperature = Temperature,
            stream = false
        };

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
            return doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";
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
            list.Add(new { role = m.Role, content = m.Content });
        return list;
    }

    public void Dispose() => _http.Dispose();
}
