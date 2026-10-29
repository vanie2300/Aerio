using System.Net;
using System.Text;
using TabloidBrowser;
using Xunit;

namespace TabloidBrowser.Tests;

public class AerioUpdateServiceTests
{
    [Fact]
    public async Task GetLatestReleaseAsync_ValidRelease_ReturnsVersionAndReleasePage()
    {
        var service = CreateService(HttpStatusCode.OK,
            """{"tag_name":"v1.5.0","html_url":"https://github.com/vanie2300/Aerio/releases/tag/v1.5.0"}""");

        var release = await service.GetLatestReleaseAsync();

        Assert.NotNull(release);
        Assert.Equal(new Version(1, 5, 0), release.Version);
        Assert.Equal("https://github.com/vanie2300/Aerio/releases/tag/v1.5.0", release.ReleasePageUri.AbsoluteUri);
    }

    [Fact]
    public async Task GetLatestReleaseAsync_NoPublishedRelease_ReturnsNull()
    {
        var service = CreateService(HttpStatusCode.NotFound, "");

        var release = await service.GetLatestReleaseAsync();

        Assert.Null(release);
    }

    [Theory]
    [InlineData("""{"tag_name":"preview","html_url":"https://github.com/vanie2300/Aerio/releases/tag/preview"}""")]
    [InlineData("""{"tag_name":"v1.5.0","html_url":"https://example.com/release"}""")]
    public async Task GetLatestReleaseAsync_InvalidReleaseData_Throws(string json)
    {
        var service = CreateService(HttpStatusCode.OK, json);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetLatestReleaseAsync());
    }

    [Fact]
    public async Task GetLatestReleaseAsync_ServerError_Throws()
    {
        var service = CreateService(HttpStatusCode.ServiceUnavailable, "");

        await Assert.ThrowsAsync<HttpRequestException>(() => service.GetLatestReleaseAsync());
    }

    private static AerioUpdateService CreateService(HttpStatusCode statusCode, string json)
    {
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        return new AerioUpdateService(new HttpClient(handler));
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
