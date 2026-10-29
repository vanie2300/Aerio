using System.Net;
using System.Net.Http;
using Xunit;

namespace TabloidBrowser.Tests;

public class WebSearchServiceTests
{
    [Fact]
    public async Task SearchAsync_PropagatesCancellationDuringRequest()
    {
        var handler = new BlockingHttpMessageHandler();
        using var client = new HttpClient(handler);
        using var service = new WebSearchService(client);
        using var cancellation = new CancellationTokenSource();

        var search = service.SearchAsync("test query", cancellation.Token);
        await handler.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search);
    }

    [Fact]
    public async Task GetSuggestionsAsync_PropagatesCancellationDuringRequest()
    {
        var handler = new BlockingHttpMessageHandler();
        using var client = new HttpClient(handler);
        using var service = new WebSearchService(client);
        using var cancellation = new CancellationTokenSource();

        var suggestions = service.GetSuggestionsAsync("test query", cancellation.Token);
        await handler.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => suggestions);
    }

    [Fact]
    public void ResolveBackend_DuckDuckGo_MapsCorrectly()
    {
        var (backend, endpoint) = WebSearchService.ResolveBackend("DuckDuckGo");
        Assert.Equal("duckduckgo", backend);
        Assert.Null(endpoint);
    }

    [Fact]
    public void ResolveBackend_SearXNG_MapsCorrectly()
    {
        var (backend, endpoint) = WebSearchService.ResolveBackend("SearXNG");
        Assert.Equal("searxng", backend);
        Assert.Equal("https://searx.be", endpoint);
    }

    [Fact]
    public void ResolveBackend_Google_FallsBackToDuckDuckGo()
    {
        var (backend, endpoint) = WebSearchService.ResolveBackend("Google");
        Assert.Equal("duckduckgo", backend);
        Assert.Null(endpoint);
    }

    [Fact]
    public void ResolveBackend_UnknownEngine_FallsBackToDuckDuckGo()
    {
        var (backend, endpoint) = WebSearchService.ResolveBackend("Startpage");
        Assert.Equal("duckduckgo", backend);
        Assert.Null(endpoint);
    }

    [Fact]
    public void ResolveBackend_CustomSearXngInstance_MapsToSearXng()
    {
        var tuple = WebSearchService.ResolveBackend("MySearX", new()
        {
            new SearchEngineDef("MySearX", "https://mysearx.example/search?q={q}")
        });
        Assert.Equal("searxng", tuple.Backend);
        Assert.Equal("https://mysearx.example", tuple.Endpoint);
    }

    [Fact]
    public void ResolveBackend_CustomNonSearXng_FallsBackToDuckDuckGo()
    {
        var tuple = WebSearchService.ResolveBackend("MyEngine", new()
        {
            new SearchEngineDef("MyEngine", "https://myengine.example/results?q={q}")
        });
        Assert.Equal("duckduckgo", tuple.Backend);
        Assert.Null(tuple.Endpoint);
    }

    [Fact]
    public void ParseBingHtml_ExtractsTitleUrlAndSnippet()
    {
        const string html = """
            <ol id="b_results">
              <li class="b_algo">
                <h2><a href="https://www.bing.com/ck/a?!&&p=abc&u=a1aHR0cHM6Ly93d3cuZXhhbXBsZS5jb20vcmVzb3VyY2U&ntb=1" h="ID=SERP,5154.1">Example <strong>Resource</strong></a></h2>
                <div class="b_caption"><p>This is a snippet about the example resource.</p></div>
              </li>
            </ol>
            """;

        var results = WebSearchService.ParseBingHtml(html);

        Assert.Single(results);
        Assert.Equal("Example Resource", results[0].Title);
        Assert.Equal("https://www.example.com/resource", results[0].Url);
        Assert.Equal("This is a snippet about the example resource.", results[0].Snippet);
    }

    [Fact]
    public void ParseBingHtml_IgnoresNonResultItems()
    {
        const string html = """
            <ol id="b_results">
              <li class="b_no">No results found.</li>
            </ol>
            """;

        var results = WebSearchService.ParseBingHtml(html);

        Assert.Empty(results);
    }

    [Fact]
    public void ParseBingHtml_DirectUrls_AreKeptAsIs()
    {
        const string html = """
            <li class="b_algo"><h2><a href="https://example.org/direct">Direct Link</a></h2><p>Direct snippet.</p></li>
            """;

        var results = WebSearchService.ParseBingHtml(html);

        Assert.Single(results);
        Assert.Equal("https://example.org/direct", results[0].Url);
    }

    [Fact]
    public void DecodeBingRedirect_NonRedirectUrl_ReturnedUnchanged()
    {
        Assert.Equal("https://plain.example/page", WebSearchService.DecodeBingRedirect("https://plain.example/page"));
    }

    [Fact]
    public void DecodeBingRedirect_RedirectUrl_DecodesTarget()
    {
        const string encoded = "https://www.bing.com/ck/a?!&&p=abc&u=a1aHR0cHM6Ly93d3cuZXhhbXBsZS5jb20vcmVzb3VyY2U&ntb=1";
        Assert.Equal("https://www.example.com/resource", WebSearchService.DecodeBingRedirect(encoded));
    }

    [Fact]
    public void RankByRelevance_MatchingResult_ComesFirst()
    {
        var results = new List<SearchResult>
        {
            new("ABS-CBN News - Shows and Movies", "https://news.example", "Latest shows and movie trailers."),
            new("Reduced Emissions: Carbon Dioxide For a Fuller Flame", "https://red.example/co2", "CO2 emissions and the combustion process."),
            new("Best Coffee Beans | Espresso Guide", "https://coffee.example/guide", "How to pick espresso beans.")
        };

        var ranked = WebSearchService.RankByRelevance(results, "co2 emissions");

        Assert.Equal("Reduced Emissions: Carbon Dioxide For a Fuller Flame", ranked[0].Title);
    }

    [Fact]
    public void RankByRelevance_QueryWithoutTokens_KeepsOriginalOrder()
    {
        var results = new List<SearchResult>
        {
            new("First Result", "https://a.example", ""),
            new("Second Result", "https://b.example", "")
        };

        var ranked = WebSearchService.RankByRelevance(results, "the how");

        Assert.Equal("First Result", ranked[0].Title);
        Assert.Equal("Second Result", ranked[1].Title);
    }

    private sealed class BlockingHttpMessageHandler : HttpMessageHandler
    {
        public TaskCompletionSource RequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}