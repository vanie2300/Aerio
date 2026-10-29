using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace TabloidBrowser;

public sealed record AerioRelease(Version Version, Uri ReleasePageUri, Uri InstallerDownloadUri);

public readonly record struct AerioDownloadProgress(long BytesReceived, long? TotalBytes);

public sealed class AerioUpdateService
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/vanie2300/Aerio/releases/latest";
    private const string InstallerAssetName = "Aerio-Setup-win-x64.exe";
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
    private readonly HttpClient _httpClient;

    public AerioUpdateService(HttpClient? httpClient = null) =>
        _httpClient = httpClient ?? SharedHttpClient;

    public async Task<AerioRelease?> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(LatestReleaseApiUrl, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var root = document.RootElement;
        if (!root.TryGetProperty("tag_name", out var tagElement) ||
            tagElement.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("html_url", out var releaseUrlElement) ||
            releaseUrlElement.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("assets", out var assetsElement) ||
            assetsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("GitHub returned incomplete release information.");
        }

        var tag = tagElement.GetString()?.Trim();
        var releaseUrl = releaseUrlElement.GetString();
        if (string.IsNullOrWhiteSpace(tag) ||
            !Version.TryParse(tag.TrimStart('v', 'V'), out var version))
        {
            throw new InvalidDataException("GitHub returned a release with an invalid version tag.");
        }

        if (!Uri.TryCreate(releaseUrl, UriKind.Absolute, out var releasePageUri) ||
            releasePageUri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(releasePageUri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("GitHub returned an invalid release page URL.");
        }

        Uri? installerDownloadUri = null;
        foreach (var asset in assetsElement.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("GitHub returned an invalid release asset.");
            }

            if (!asset.TryGetProperty("name", out var nameElement) ||
                nameElement.ValueKind != JsonValueKind.String ||
                !string.Equals(nameElement.GetString(), InstallerAssetName, StringComparison.Ordinal))
            {
                continue;
            }

            if (!asset.TryGetProperty("browser_download_url", out var downloadUrlElement) ||
                downloadUrlElement.ValueKind != JsonValueKind.String ||
                !Uri.TryCreate(downloadUrlElement.GetString(), UriKind.Absolute, out installerDownloadUri) ||
                !IsTrustedInstallerUri(installerDownloadUri))
            {
                throw new InvalidDataException("GitHub returned an invalid installer download URL.");
            }

            break;
        }

        if (installerDownloadUri is null)
        {
            throw new InvalidDataException("GitHub release does not include the Aerio installer.");
        }

        return new AerioRelease(version, releasePageUri, installerDownloadUri);
    }

    public async Task DownloadInstallerAsync(
        AerioRelease release,
        string destinationPath,
        IProgress<AerioDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsTrustedInstallerUri(release.InstallerDownloadUri))
        {
            throw new InvalidDataException("The installer download URL is not a trusted Aerio release asset.");
        }

        using var response = await _httpClient.GetAsync(
            release.InstallerDownloadUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            throw new ArgumentException("The installer destination must include a directory.", nameof(destinationPath));
        }

        Directory.CreateDirectory(destinationDirectory);
        var temporaryPath = Path.Combine(destinationDirectory, $"{Guid.NewGuid():N}.download");
        try
        {
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            {
                await using var destination = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buffer = new byte[81920];
                long bytesReceived = 0;
                var totalBytes = response.Content.Headers.ContentLength;
                int bytesRead;
                while ((bytesRead = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    bytesReceived += bytesRead;
                    progress?.Report(new AerioDownloadProgress(bytesReceived, totalBytes));
                }

                await destination.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static bool IsTrustedInstallerUri(Uri? uri) =>
        uri is not null &&
        uri.Scheme == Uri.UriSchemeHttps &&
        string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.StartsWith(
            "/vanie2300/Aerio/releases/download/",
            StringComparison.OrdinalIgnoreCase);

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Aerio", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}
