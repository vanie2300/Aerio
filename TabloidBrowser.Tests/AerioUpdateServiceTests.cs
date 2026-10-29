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
            """{"tag_name":"v1.5.0","html_url":"https://github.com/vanie2300/Aerio/releases/tag/v1.5.0","assets":[{"name":"Aerio-Setup-win-x64.exe","browser_download_url":"https://github.com/vanie2300/Aerio/releases/download/v1.5.0/Aerio-Setup-win-x64.exe"}]}""");

        var release = await service.GetLatestReleaseAsync();

        Assert.NotNull(release);
        Assert.Equal(new Version(1, 5, 0), release.Version);
        Assert.Equal("https://github.com/vanie2300/Aerio/releases/tag/v1.5.0", release.ReleasePageUri.AbsoluteUri);
        Assert.Equal(
            "https://github.com/vanie2300/Aerio/releases/download/v1.5.0/Aerio-Setup-win-x64.exe",
            release.InstallerDownloadUri.AbsoluteUri);
    }

    [Fact]
    public async Task DownloadInstallerAsync_SavesInstallerAndReportsProgress()
    {
        var installerBytes = Encoding.UTF8.GetBytes("installer");
        var handler = new StubHttpMessageHandler(request =>
            request.RequestUri?.AbsolutePath == "/repos/vanie2300/Aerio/releases/latest"
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"tag_name":"v1.5.0","html_url":"https://github.com/vanie2300/Aerio/releases/tag/v1.5.0","assets":[{"name":"Aerio-Setup-win-x64.exe","browser_download_url":"https://github.com/vanie2300/Aerio/releases/download/v1.5.0/Aerio-Setup-win-x64.exe"}]}""",
                        Encoding.UTF8,
                        "application/json")
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(installerBytes)
                });
        var service = new AerioUpdateService(new HttpClient(handler));
        var release = await service.GetLatestReleaseAsync();
        Assert.NotNull(release);

        var directory = Path.Combine(Path.GetTempPath(), $"AerioUpdateTests-{Guid.NewGuid():N}");
        var destinationPath = Path.Combine(directory, "Aerio-Setup-win-x64.exe");
        var progress = new List<AerioDownloadProgress>();
        try
        {
            await service.DownloadInstallerAsync(
                release,
                destinationPath,
                new InlineProgress<AerioDownloadProgress>(progress.Add));

            Assert.Equal(installerBytes, await File.ReadAllBytesAsync(destinationPath));
            Assert.NotEmpty(progress);
            Assert.Equal(installerBytes.Length, progress[^1].BytesReceived);
            Assert.Equal(installerBytes.Length, progress[^1].TotalBytes);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
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
    [InlineData("""{"tag_name":"v1.5.0","html_url":"https://github.com/vanie2300/Aerio/releases/tag/v1.5.0","assets":[{"name":"Aerio-Setup-win-x64.exe","browser_download_url":"https://example.com/Aerio-Setup-win-x64.exe"}]}""")]
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

    [Fact]
    public async Task GetLatestReleaseAsync_ReleaseWithoutInstaller_Throws()
    {
        var service = CreateService(HttpStatusCode.OK,
            """{"tag_name":"v1.5.0","html_url":"https://github.com/vanie2300/Aerio/releases/tag/v1.5.0","assets":[]}""");

        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetLatestReleaseAsync());
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

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
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
