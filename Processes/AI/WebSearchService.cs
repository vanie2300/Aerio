using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TabloidBrowser;

public sealed class WebSearchService : IDisposable
{
    private readonly HttpClient _http = CreateHttpClient();
    private DateTime _lastRequestTime = DateTime.MinValue;
    private readonly SemaphoreSlim _rateSemaphore = new(1, 1);

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        return client;
    }

    public string Backend { get; set; } = "duckduckgo";
    public string? Endpoint { get; set; }

    /// <summary>
    /// Maps the browser's selected search engine to a supported AI backend.
    /// Only DuckDuckGo and SearXNG have scrapers/JSON support; other engines fall back to DuckDuckGo.
    /// </summary>
    public static (string Backend, string? Endpoint) ResolveBackend(string searchEngine, List<SearchEngineDef>? customEngines = null)
    {
        switch (searchEngine)
        {
            case "DuckDuckGo":
                return ("duckduckgo", null);
            case "SearXNG":
                return ("searxng", "https://searx.be");
            default:
                var custom = customEngines?.FirstOrDefault(e => e.Name == searchEngine);
                if (custom is not null &&
                    Uri.TryCreate(custom.UrlTemplate, UriKind.Absolute, out var tempUri))
                {
                    var host = tempUri.Host;
                    if (host.Contains("searx", StringComparison.OrdinalIgnoreCase) ||
                        tempUri.AbsolutePath.StartsWith("/search", StringComparison.OrdinalIgnoreCase))
                        return ("searxng", $"{tempUri.Scheme}://{host}");
                }
                return ("duckduckgo", null);
        }
    }

    public async Task<List<SearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        await EnsureRateLimitAsync(ct);

        var results = Backend.ToLowerInvariant() switch
        {
            "duckduckgo" => await SearchDuckDuckGoLite(query, ct),
            "searxng" or "custom" => await SearchSearXNG(query, ct),
            _ => await SearchDuckDuckGoLite(query, ct)
        };

        // DuckDuckGo's lite/html endpoints now return an anti-bot CAPTCHA (no results)
        // to automated clients. Fall back to Bing's plain-HTML results so the AI's
        // search tool still returns real results.
        if (results.Count == 0)
        {
            results = await SearchBing(query, ct);
        }

        // Demote results that don't actually relate to the query so the top hit
        // (which the AI opens as an article) is on-topic instead of a random
        // news-aggregator homepage.
        results = RankByRelevance(results, query);

        return results;
    }

    /// <summary>
    /// Scores each result by how many significant query terms appear in its
    /// title, URL and snippet (title matches weighted most), then reorders so
    /// the most on-topic results come first.
    /// </summary>
    public static List<SearchResult> RankByRelevance(List<SearchResult> results, string query)
    {
        var tokens = Regex.Matches(query.ToLowerInvariant(), @"[a-z0-9]{3,}")
            .Select(m => m.Value)
            .Where(t => !StopWords.Contains(t))
            .Distinct()
            .ToList();
        if (tokens.Count == 0)
            return results;

        var scored = results
            .Select(r => (Result: r, Score: RelevanceScore(r, tokens)))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => results.IndexOf(x.Result))
            .Select(x => x.Result)
            .ToList();

        return scored;
    }

    private static int RelevanceScore(SearchResult result, List<string> tokens)
    {
        var title = result.Title.ToLowerInvariant();
        var snippet = result.Snippet.ToLowerInvariant();
        var url = result.Url.ToLowerInvariant();

        var titleMatches = tokens.Count(t => title.Contains(t));
        var snippetMatches = tokens.Count(t => snippet.Contains(t));
        var urlMatches = tokens.Count(t => url.Contains(t));

        return titleMatches * 3 + snippetMatches * 2 + urlMatches;
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "are", "with", "that", "this", "what", "when",
        "from", "you", "your", "how", "why", "who", "has", "have", "was",
        "about", "into", "them", "they", "there", "will", "would", "were",
        "where", "which", "while", "most", "more", "can", "get", "their",
        "latest", "today", "news", "year", "known", "whats"
    };

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

    private async Task<List<SearchResult>> SearchBing(string query, CancellationToken ct)
    {
        try
        {
            var url = $"https://www.bing.com/search?q={Uri.EscapeDataString(query)}&count=10";
            using var response = await _http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync(ct);
            return ParseBingHtml(html);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Bing search failed: {ex}");
            return [];
        }
    }

    public static List<SearchResult> ParseBingHtml(string html)
    {
        var results = new List<SearchResult>();
        var blockPattern = @"<li\s+class=""b_algo""(?<block>.*?)</li>";

        foreach (Match match in Regex.Matches(html, blockPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var block = match.Groups["block"].Value;

            var titleMatch = Regex.Match(block,
                @"<h2[^>]*>\s*<a[^>]+href=""(?<url>[^""]+)""[^>]*>(?<title>.*?)</a>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!titleMatch.Success)
                continue;

            var url = DecodeBingRedirect(System.Net.WebUtility.HtmlDecode(titleMatch.Groups["url"].Value));
            var title = CleanText(titleMatch.Groups["title"].Value);

            var snippetMatch = Regex.Match(block, @"<p[^>]*>(?<snippet>.*?)</p>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var snippet = snippetMatch.Success ? CleanText(snippetMatch.Groups["snippet"].Value) : "";

            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(title))
                continue;

            if (url.StartsWith("//", StringComparison.Ordinal))
                url = "https:" + url;
            else if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                     !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                url = "https://" + url;

            results.Add(new SearchResult(title, url, snippet));

            if (results.Count >= 10)
                break;
        }

        return results;
    }

    public static string DecodeBingRedirect(string url)
    {
        if (url.Contains("bing.com/ck/a", StringComparison.OrdinalIgnoreCase))
        {
            var redirectMatch = Regex.Match(url, @"[?&]u=(?<encoded>a1[^&]+)");
            if (redirectMatch.Success)
            {
                try
                {
                    var encoded = Uri.UnescapeDataString(redirectMatch.Groups["encoded"].Value);
                    var b64 = encoded.StartsWith("a1", StringComparison.Ordinal) ? encoded[2..] : encoded;
                    b64 = b64.PadRight(b64.Length + ((4 - b64.Length % 4) % 4), '=');
                    var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));

                    if (Uri.TryCreate(decoded, UriKind.Absolute, out var uri) &&
                        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                        return decoded;
                }
                catch (FormatException)
                {
                    // Not base64; fall through and return the original URL.
                }
            }
        }

        return url;
    }

    private static string CleanText(string text)
    {
        text = Regex.Replace(text, @"<[^>]+>", " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private async Task<List<SearchResult>> SearchSearXNG(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Endpoint))
            return [];

        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpointUri) ||
            endpointUri.Scheme != "http" && endpointUri.Scheme != "https")
            return [];

        try
        {
            var url = $"{endpointUri.GetLeftPart(UriPartial.Path).TrimEnd('/')}/search?q={Uri.EscapeDataString(query)}&format=json";
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
