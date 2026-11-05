using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace TabloidBrowser;

public sealed record AerioRelease(
    Version Version,
    Uri ReleasePageUri,
    Uri InstallerDownloadUri,
    string? InstallerSha256 = null,
    Uri? ChecksumManifestUri = null);

public readonly record struct AerioDownloadProgress(long BytesReceived, long? TotalBytes);

public sealed class AerioUpdateService
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/vanie2300/Aerio/releases/latest";
    private const string InstallerAssetName = "Aerio-Setup-win-x64.exe";
    private const string ChecksumManifestAssetName = "SHA256SUMS.txt";
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
        string? installerSha256 = null;
        Uri? checksumManifestUri = null;
        foreach (var asset in assetsElement.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("GitHub returned an invalid release asset.");
            }

            if (!asset.TryGetProperty("name", out var nameElement) ||
                nameElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var assetName = nameElement.GetString();
            if (string.Equals(assetName, InstallerAssetName, StringComparison.Ordinal))
            {
                if (!asset.TryGetProperty("browser_download_url", out var downloadUrlElement) ||
                    downloadUrlElement.ValueKind != JsonValueKind.String ||
                    !Uri.TryCreate(downloadUrlElement.GetString(), UriKind.Absolute, out installerDownloadUri) ||
                    !IsTrustedInstallerUri(installerDownloadUri))
                {
                    throw new InvalidDataException("GitHub returned an invalid installer download URL.");
                }

                // GitHub exposes a sha256 digest for release assets. When
                // present it is carried through so the download can be
                // integrity-checked before the installer is ever executed.
                installerSha256 = null;
                if (asset.TryGetProperty("digest", out var digestElement) &&
                    digestElement.ValueKind == JsonValueKind.String)
                {
                    installerSha256 = NormalizeSha256Digest(digestElement.GetString());
                }

                continue;
            }

            if (string.Equals(assetName, ChecksumManifestAssetName, StringComparison.Ordinal) &&
                checksumManifestUri is null)
            {
                // Older releases without per-asset digests can still be
                // verified against the published SHA256SUMS.txt manifest.
                if (asset.TryGetProperty("browser_download_url", out var checksumUrlElement) &&
                    checksumUrlElement.ValueKind == JsonValueKind.String &&
                    Uri.TryCreate(checksumUrlElement.GetString(), UriKind.Absolute, out var checksumUri) &&
                    IsTrustedInstallerUri(checksumUri))
                {
                    checksumManifestUri = checksumUri;
                }
            }
        }

        if (installerDownloadUri is null)
        {
            throw new InvalidDataException("GitHub release does not include the Aerio installer.");
        }

        return new AerioRelease(version, releasePageUri, installerDownloadUri, installerSha256, checksumManifestUri);
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

            var expectedSha256 = release.InstallerSha256;
            if (expectedSha256 is null && release.ChecksumManifestUri is not null)
            {
                expectedSha256 = await FetchChecksumFromManifestAsync(
                    release.ChecksumManifestUri,
                    Path.GetFileName(destinationPath),
                    cancellationToken);
            }

            if (!VerifyInstallerDigest(temporaryPath, expectedSha256))
            {
                throw new InvalidDataException(
                    "The downloaded installer failed its SHA-256 integrity check and was discarded.");
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

    /// <summary>
    /// Accepts GitHub asset digests of the form "sha256:<64 hex chars>"; anything
    /// else is treated as absent rather than guessable.
    /// </summary>
    private static string? NormalizeSha256Digest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return null;

        var separator = digest.IndexOf(':');
        if (separator <= 0) return null;

        var algorithm = digest[..separator].Trim();
        if (!string.Equals(algorithm, "sha256", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return NormalizeSha256Hex(digest[(separator + 1)..]);
    }

    /// <summary>
    /// Accepts a bare 64-character SHA-256 hex string (as found in a
    /// sha256sum-style manifest); anything else is treated as absent rather
    /// than guessable.
    /// </summary>
    private static string? NormalizeSha256Hex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var trimmed = value.Trim();
        return trimmed.Length == 64 && trimmed.All(char.IsAsciiHexDigit)
            ? trimmed.ToLowerInvariant()
            : null;
    }

    /// <summary>
    /// Verifies a downloaded installer against its expected SHA-256 digest.
    /// A missing expectation is allowed (older releases); a present expectation
    /// must match exactly.
    /// </summary>
    public static bool VerifyInstallerDigest(string filePath, string? expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256)) return true;
        if (!File.Exists(filePath)) return false;

        using var stream = File.OpenRead(filePath);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        return string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads the installer's digest from a sha256sum-style SHA256SUMS.txt
    /// manifest ("<64 hex>  <file name>" per line).
    /// </summary>
    private async Task<string?> FetchChecksumFromManifestAsync(
        Uri manifestUri,
        string fileName,
        CancellationToken cancellationToken)
    {
        if (!IsTrustedInstallerUri(manifestUri))
        {
            throw new InvalidDataException("The checksum manifest URL is not a trusted Aerio release asset.");
        }

        using var response = await _httpClient.GetAsync(manifestUri, cancellationToken);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(cancellationToken);

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r', ' ');
            // sha256sum format: hash, two spaces, file name.
            if (line.Length <= 66 || line[64] != ' ' || line[65] != ' ')
            {
                continue;
            }

            var hash = line[..64];
            var name = line[66..].Trim();
            if (!string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return NormalizeSha256Hex(hash);
        }

        return null;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Aerio", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}
