using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace TabloidBrowser;

public sealed record AerioRelease(Version Version, Uri ReleasePageUri);

public sealed class AerioUpdateService
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/vanie2300/Aerio/releases/latest";
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
            releaseUrlElement.ValueKind != JsonValueKind.String)
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

        return new AerioRelease(version, releasePageUri);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Aerio", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}
