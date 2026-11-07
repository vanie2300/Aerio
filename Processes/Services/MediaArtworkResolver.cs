namespace TabloidBrowser;

/// <summary>
/// Validates and fetches media artwork (album art / video poster) advertised by
/// a page's Media Session metadata. Artwork URLs come from web content, so they
/// are treated as untrusted: only http(s) to non-local hosts, with a hard size
/// cap, and the bytes are decoded defensively.
/// </summary>
public static class MediaArtworkResolver
{
    /// <summary>Upper bound for an artwork download (defensive against abuse).</summary>
    public const long MaxArtworkBytes = 4 * 1024 * 1024;

    /// <summary>
    /// True when the URL is safe to fetch for artwork: absolute http(s), a real
    /// host that is not on the local network, and nothing exotic.
    /// </summary>
    public static bool IsAllowedArtworkUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (string.IsNullOrEmpty(uri.Host))
        {
            return false;
        }

        // Never let page metadata point the app at the user's LAN or loopback.
        return !MainWindow.IsLocalAddress(uri.Host);
    }
}
