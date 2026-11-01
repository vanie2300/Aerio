using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace TabloidBrowser;

/// <summary>
/// AI endpoint trust policy: API keys and conversation content must only be
/// sent over HTTPS, with one exception for loopback HTTP so local servers
/// (Ollama on localhost) keep working.
/// </summary>
public static class AiEndpoints
{
    public static bool IsSecure(string? endpoint)
    {
        if (!Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return true;
        }

        return uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
    }

    /// <summary>
    /// Validates an endpoint for outbound AI traffic and returns its trimmed
    /// form. Throws when the endpoint is unusable or would send credentials
    /// over an insecure non-loopback connection.
    /// </summary>
    public static string EnsureSecure(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new InvalidOperationException("Enter an AI API endpoint in Settings > AI.");
        }

        if (!IsSecure(endpoint))
        {
            throw new InvalidOperationException(
                "The AI API endpoint must use HTTPS (or http://localhost for a local server). " +
                "API keys are never sent over plain HTTP.");
        }

        return endpoint.Trim();
    }

    /// <summary>
    /// Creates an HttpClient for authenticated AI traffic. Auto-redirects are
    /// disabled so an HTTPS endpoint can never bounce the Authorization header
    /// to a downgraded scheme or a different origin — a 3xx response is
    /// surfaced to the caller and treated as an error instead.
    /// </summary>
    public static HttpClient CreateSecureHttpClient(TimeSpan? timeout = null)
    {
        return new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(30)
        };
    }
}
