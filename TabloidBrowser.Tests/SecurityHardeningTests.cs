using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace TabloidBrowser.Tests;

/// <summary>
/// Regression tests for the security hardening pass: update-integrity
/// verification, SearXNG endpoint policy, and bookmark scheme gating.
/// </summary>
public class SecurityHardeningTests
{
    // ---------- Aerio update installer integrity ----------

    private static string BuildReleaseJson(bool includeDigest, string digestSuffix = "") =>
        "{\"tag_name\":\"v1.5.0\"," +
        "\"html_url\":\"https://github.com/vanie2300/Aerio/releases/tag/v1.5.0\"," +
        "\"assets\":[{\"name\":\"Aerio-Setup-win-x64.exe\"," +
        "\"browser_download_url\":\"https://github.com/vanie2300/Aerio/releases/download/v1.5.0/Aerio-Setup-win-x64.exe\"" +
        (includeDigest ? ",\"digest\":\"" + digestSuffix + "\"" : "") + "}],\"body\":\"\"}";

    private static AerioUpdateService CreateService(string installerBytes, string releaseJson) =>
        new(new HttpClient(new StubHttpMessageHandler(request =>
            request.RequestUri?.AbsolutePath == "/repos/vanie2300/Aerio/releases/latest"
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(releaseJson, Encoding.UTF8, "application/json")
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(installerBytes)
                })));

    private static string TempReleaseDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"AerioSecurityTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string BuildReleaseJsonWithManifest(string? digest = null) =>
        "{\"tag_name\":\"v1.5.0\"," +
        "\"html_url\":\"https://github.com/vanie2300/Aerio/releases/tag/v1.5.0\"," +
        "\"assets\":[" +
        "{\"name\":\"Aerio-Setup-win-x64.exe\"," +
        "\"browser_download_url\":\"https://github.com/vanie2300/Aerio/releases/download/v1.5.0/Aerio-Setup-win-x64.exe\"" +
        (digest is null ? "" : ",\"digest\":\"" + digest + "\"") + "}," +
        "{\"name\":\"SHA256SUMS.txt\"," +
        "\"browser_download_url\":\"https://github.com/vanie2300/Aerio/releases/download/v1.5.0/SHA256SUMS.txt\"}" +
        "],\"body\":\"\"}";

    private static AerioUpdateService CreateServiceWithManifest(
        string installerBytes,
        string releaseJson,
        string manifestText) =>
        new(new HttpClient(new StubHttpMessageHandler(request =>
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path == "/repos/vanie2300/Aerio/releases/latest")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(releaseJson, Encoding.UTF8, "application/json")
                };
            }

            if (path.EndsWith("/SHA256SUMS.txt", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(manifestText)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(installerBytes)
            };
        })));

    [Fact]
    public async Task GetLatestReleaseAsync_WithSha256Digest_ParsesDigest()
    {
        var expected = new string('a', 64);
        var service = CreateService("installer", BuildReleaseJson(true, "sha256:" + expected));

        var release = await service.GetLatestReleaseAsync();

        Assert.NotNull(release);
        Assert.Equal(expected, release.InstallerSha256);
    }

    [Fact]
    public async Task GetLatestReleaseAsync_WithoutDigestButWithManifest_ParsesManifestUri()
    {
        var service = CreateServiceWithManifest("installer", BuildReleaseJsonWithManifest(), "");

        var release = await service.GetLatestReleaseAsync();

        Assert.NotNull(release);
        Assert.Null(release.InstallerSha256);
        Assert.NotNull(release.ChecksumManifestUri);
        Assert.EndsWith("/SHA256SUMS.txt", release.ChecksumManifestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task DownloadInstallerAsync_ManifestHashMatch_VerifiesAndSaves()
    {
        var installerBytes = "installer-via-manifest";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(installerBytes))).ToLowerInvariant();
        var manifest = $"{hash}  Aerio-Setup-win-x64.exe\n";
        var service = CreateServiceWithManifest(installerBytes, BuildReleaseJsonWithManifest(), manifest);
        var release = await service.GetLatestReleaseAsync();
        var directory = TempReleaseDirectory();
        try
        {
            var destination = Path.Combine(directory, "Aerio-Setup-win-x64.exe");

            await service.DownloadInstallerAsync(release!, destination);

            Assert.Equal(installerBytes, await File.ReadAllTextAsync(destination));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadInstallerAsync_ManifestHashMismatch_ThrowsAndLeavesNoInstaller()
    {
        var manifest = $"{new string('b', 64)}  Aerio-Setup-win-x64.exe\n";
        var service = CreateServiceWithManifest("tampered", BuildReleaseJsonWithManifest(), manifest);
        var release = await service.GetLatestReleaseAsync();
        var directory = TempReleaseDirectory();
        try
        {
            var destination = Path.Combine(directory, "Aerio-Setup-win-x64.exe");

            await Assert.ThrowsAsync<InvalidDataException>(
                () => service.DownloadInstallerAsync(release!, destination));

            Assert.False(File.Exists(destination));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("md5:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("sha256:not-hex")]
    [InlineData("just-a-hash-value-without-algorithm")]
    public async Task GetLatestReleaseAsync_WithNonSha256DigestAndNoManifest_Throws(string digest)
    {
        // A non-sha256 digest is never trusted as integrity data. With no
        // manifest either, the release has no usable integrity information and
        // must be rejected (fail closed) rather than silently accepted.
        var service = CreateService("installer", BuildReleaseJson(true, digest));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => service.GetLatestReleaseAsync());
    }

    [Fact]
    public async Task DownloadInstallerAsync_DigestMatch_SavesInstaller()
    {
        var installerBytes = "installer-payload";
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(installerBytes)));
        var service = CreateService(installerBytes, BuildReleaseJson(true, "sha256:" + digest));
        var release = await service.GetLatestReleaseAsync();
        var directory = TempReleaseDirectory();
        try
        {
            var destination = Path.Combine(directory, "Aerio-Setup-win-x64.exe");

            await service.DownloadInstallerAsync(release!, destination);

            Assert.Equal(installerBytes, await File.ReadAllTextAsync(destination));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadInstallerAsync_DigestMismatch_ThrowsAndLeavesNoInstaller()
    {
        var expected = new string('a', 64);
        var service = CreateService("tampered-payload", BuildReleaseJson(true, "sha256:" + expected));
        var release = await service.GetLatestReleaseAsync();
        var directory = TempReleaseDirectory();
        try
        {
            var destination = Path.Combine(directory, "Aerio-Setup-win-x64.exe");

            await Assert.ThrowsAsync<InvalidDataException>(
                () => service.DownloadInstallerAsync(release!, destination));

            Assert.False(File.Exists(destination));
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static AerioRelease BuildManifestRelease() =>
        new(
            new Version(1, 5, 0),
            new Uri("https://github.com/vanie2300/Aerio/releases/tag/v1.5.0"),
            new Uri("https://github.com/vanie2300/Aerio/releases/download/v1.5.0/Aerio-Setup-win-x64.exe"),
            InstallerSha256: null,
            ChecksumManifestUri: new Uri("https://github.com/vanie2300/Aerio/releases/download/v1.5.0/SHA256SUMS.txt"));

    private static AerioUpdateService CreateManifestService(string installerBytes, string manifest) =>
        new(new HttpClient(new StubHttpMessageHandler(request =>
            request.RequestUri?.AbsolutePath.EndsWith("/SHA256SUMS.txt", StringComparison.Ordinal) == true
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(manifest, Encoding.UTF8, "text/plain")
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(installerBytes)
                })));

    [Fact]
    public async Task DownloadInstallerAsync_Sha256SumManifestMismatch_ThrowsAndLeavesNoInstaller()
    {
        // The release has no per-asset digest, so integrity must come from the
        // published SHA256SUMS.txt. A hash that does not match the payload must
        // be rejected — not silently skipped.
        var installerBytes = "tampered-payload";
        var manifest = $"{new string('b', 64)}  Aerio-Setup-win-x64.exe\n";
        var service = CreateManifestService(installerBytes, manifest);
        var directory = TempReleaseDirectory();
        try
        {
            var destination = Path.Combine(directory, "Aerio-Setup-win-x64.exe");

            await Assert.ThrowsAsync<InvalidDataException>(
                () => service.DownloadInstallerAsync(BuildManifestRelease(), destination));

            Assert.False(File.Exists(destination));
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadInstallerAsync_Sha256SumManifestMatch_SavesInstaller()
    {
        var installerBytes = "installer-payload";
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(installerBytes)));
        var manifest = $"{digest}  Aerio-Setup-win-x64.exe\n";
        var service = CreateManifestService(installerBytes, manifest);
        var directory = TempReleaseDirectory();
        try
        {
            var destination = Path.Combine(directory, "Aerio-Setup-win-x64.exe");

            await service.DownloadInstallerAsync(BuildManifestRelease(), destination);

            Assert.Equal(installerBytes, await File.ReadAllTextAsync(destination));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void VerifyInstallerDigest_NoExpectedDigest_ReturnsFalse()
    {
        // Fail closed: when no digest is available the installer must be
        // rejected, not silently accepted.
        var path = Path.Combine(TempReleaseDirectory(), "installer.exe");
        try
        {
            File.WriteAllText(path, "anything");

            Assert.False(AerioUpdateService.VerifyInstallerDigest(path, expectedSha256: null));
            Assert.False(AerioUpdateService.VerifyInstallerDigest(path, expectedSha256: "  "));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void VerifyInstallerDigest_MatchingDigest_ReturnsTrue()
    {
        var directory = TempReleaseDirectory();
        var path = Path.Combine(directory, "installer.exe");
        try
        {
            File.WriteAllText(path, "installer");
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("installer")));

            Assert.True(AerioUpdateService.VerifyInstallerDigest(path, digest));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void VerifyInstallerDigest_TamperedFile_ReturnsFalse()
    {
        var directory = TempReleaseDirectory();
        var path = Path.Combine(directory, "installer.exe");
        try
        {
            File.WriteAllText(path, "installer");
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("other content")));

            Assert.False(AerioUpdateService.VerifyInstallerDigest(path, digest));
            Assert.False(AerioUpdateService.VerifyInstallerDigest(path, new string('0', 64)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task GetLatestReleaseAsync_WithoutDigestOrManifest_Throws()
    {
        // A release that publishes neither an asset digest nor SHA256SUMS.txt
        // must be rejected outright — the installer can never be verified.
        var service = CreateService("installer", BuildReleaseJson(includeDigest: false));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => service.GetLatestReleaseAsync());
    }

    [Fact]
    public async Task DownloadInstallerAsync_NoDigestAvailable_ThrowsAndLeavesNoInstaller()
    {
        // Defense in depth: DownloadInstallerAsync is public and may be called
        // with a hand-built release, so it must enforce the digest gate itself.
        var release = new AerioRelease(
            new Version(1, 5, 0),
            new Uri("https://github.com/vanie2300/Aerio/releases/tag/v1.5.0"),
            new Uri("https://github.com/vanie2300/Aerio/releases/download/v1.5.0/Aerio-Setup-win-x64.exe"),
            InstallerSha256: null,
            ChecksumManifestUri: null);
        var service = CreateService("installer", BuildReleaseJson(includeDigest: false));
        var directory = TempReleaseDirectory();
        try
        {
            var destination = Path.Combine(directory, "Aerio-Setup-win-x64.exe");

            await Assert.ThrowsAsync<InvalidDataException>(
                () => service.DownloadInstallerAsync(release, destination));

            Assert.False(File.Exists(destination));
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---------- SearXNG endpoint policy ----------
    [Fact]
    public void ResolveBackend_CustomHttpsEngine_MapsToSearxng()
    {
        var (backend, endpoint) = WebSearchService.ResolveBackend(
            "My Searx",
            [new SearchEngineDef("My Searx", "https://searx.internal.example/search?q={q}")]);

        Assert.Equal("searxng", backend);
        Assert.Equal("https://searx.internal.example", endpoint);
    }

    [Fact]
    public void ResolveBackend_CustomCleartextRemoteEngine_FallsBackToDuckDuckGo()
    {
        var (backend, endpoint) = WebSearchService.ResolveBackend(
            "My Searx",
            [new SearchEngineDef("My Searx", "http://searx.internal.example/search?q={q}")]);

        Assert.Equal("duckduckgo", backend);
        Assert.Null(endpoint);
    }

    [Fact]
    public void ResolveBackend_CustomLoopbackHttpEngine_MapsToSearxng()
    {
        var (backend, endpoint) = WebSearchService.ResolveBackend(
            "Local Searx",
            [new SearchEngineDef("Local Searx", "http://localhost:8888/search?q={q}")]);

        Assert.Equal("searxng", backend);
        Assert.Equal("http://localhost", endpoint);
    }

    // ---------- Bookmark scheme gate ----------

    [Fact]
    public void AddBookmark_NonHttpUrl_IsRejected()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"AerioSecurityTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using var store = new BrowserDataStore(directory);

            store.AddBookmark("Local file", "file:///C:/Windows/System32/drivers/etc/hosts");
            store.AddBookmark("Javascript", "javascript:alert(1)");

            Assert.Empty(store.Bookmarks);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AddBookmark_HttpUrl_IsAccepted()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"AerioSecurityTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using var store = new BrowserDataStore(directory);

            store.AddBookmark("Example", "https://example.com");

            var bookmark = Assert.Single(store.Bookmarks);
            Assert.Equal("https://example.com", bookmark.Url);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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
