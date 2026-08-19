using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TabloidBrowser;

public sealed class WebSearchService : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private DateTime _lastRequestTime = DateTime.MinValue;
    private readonly SemaphoreSlim _rateSemaphore = new(1, 1);

    public string Backend { get; set; } = "duckduckgo";
    public string? Endpoint { get; set; }

    public async Task<List<SearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        await EnsureRateLimitAsync(ct);

        return Backend.ToLowerInvariant() switch
        {
            "duckduckgo" => await SearchDuckDuckGoLite(query, ct),
            "searxng" or "custom" => await SearchSearXNG(query, ct),
            _ => await SearchDuckDuckGoLite(query, ct)
        };
    }

    private async Task EnsureRateLimitAsync(CancellationToken ct)
    {
        await _rateSemaphore.WaitAsync(ct);
        try
        {
            var elapsed = (DateTime.UtcNow - _lastRequestTime).TotalMilliseconds;
            if (elapsed < 1000)
            {
                var delay = 1000 - (int)elapsed;
                await Task.Delay(delay, ct);
            }
            _lastRequestTime = DateTime.UtcNow;
        }
        finally
        {
            _rateSemaphore.Release();
        }
    }

    private async Task<List<SearchResult>> SearchDuckDuckGoLite(string query, CancellationToken ct)
    {
        try
        {
            var url = $"https://lite.duckduckgo.com/lite/?q={Uri.EscapeDataString(query)}";
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["q"] = query,
                ["kl"] = "us-en"
            });

            using var response = await _http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync(ct);
            return ParseDuckDuckGoLiteHtml(html);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"DuckDuckGo search failed: {ex}");
            return [];
        }
    }

    private static List<SearchResult> ParseDuckDuckGoLiteHtml(string html)
    {
        var results = new List<SearchResult>();

        var linkPattern = @"<a[^>]+class=""result-link""[^>]*href=""([^""]+)""[^>]*>([^<]+)</a>";
        var snippetPattern = @"<td[^>]+class=""result-snippet""[^>]*>([^<]+)</td>";

        var links = Regex.Matches(html, linkPattern, RegexOptions.IgnoreCase);
        var snippets = Regex.Matches(html, snippetPattern, RegexOptions.IgnoreCase);

        for (int i = 0; i < links.Count && i < 10; i++)
        {
            var url = links[i].Groups[1].Value.Trim();
            var title = System.Net.WebUtility.HtmlDecode(links[i].Groups[2].Value.Trim());
            var snippet = i < snippets.Count
                ? System.Net.WebUtility.HtmlDecode(snippets[i].Groups[1].Value.Trim())
                : "";

            if (!string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(title))
            {
                if (!url.StartsWith("http"))
                    url = "https://" + url;
                results.Add(new SearchResult(title, url, snippet));
            }
        }

        return results;
    }

    private async Task<List<SearchResult>> SearchSearXNG(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Endpoint))
            return [];

        try
        {
            var url = $"{Endpoint.TrimEnd('/')}/search?q={Uri.EscapeDataString(query)}&format=json";
            using var response = await _http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            var results = new List<SearchResult>();
            if (doc.RootElement.TryGetProperty("results", out var resultsArray))
            {
                foreach (var r in resultsArray.EnumerateArray().Take(10))
                {
                    var title = r.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    var resultUrl = r.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                    var snippet = r.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";

                    if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(resultUrl))
                        results.Add(new SearchResult(title, resultUrl, snippet));
                }
            }

            return results;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SearXNG search failed: {ex}");
            return [];
        }
    }

    public async Task<List<string>> GetSuggestionsAsync(string query, CancellationToken ct = default)
    {
        // DuckDuckGo autocomplete is used regardless of Backend since it's a free, no-auth API.
        // SearXNG/custom backends may not expose a suggestions endpoint.
        try
        {
            var url = $"https://duckduckgo.com/ac/?q={Uri.EscapeDataString(query)}&type=list";
            using var response = await _http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            var suggestions = new List<string>();
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in doc.RootElement.EnumerateArray().Take(5))
                {
                    if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() > 0)
                    {
                        var suggestion = item[0].GetString();
                        if (!string.IsNullOrWhiteSpace(suggestion))
                            suggestions.Add(suggestion);
                    }
                }
            }

            return suggestions;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Suggestions fetch failed: {ex}");
            return [];
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _rateSemaphore.Dispose();
    }
}
