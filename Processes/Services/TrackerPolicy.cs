using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace TabloidBrowser;

/// <summary>
/// Pure tracker/allowlist/blocklist matching rules used by the per-request
/// WebResource filter and download checks. Kept free of UI and WebView2 types
/// so the rules can be unit-tested in isolation.
/// </summary>
public static class TrackerPolicy
{
    private static readonly HashSet<string> DangerousDownloadExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".bat", ".cmd", ".ps1", ".vbs", ".scr", ".jar", ".msix", ".appx"
    };

    private static readonly ConcurrentDictionary<string, Regex> WildcardDomainRegexCache = new(StringComparer.Ordinal);

    /// <summary>Host of an absolute URI, or null when parsing fails.</summary>
    public static string? GetHost(string? uri)
    {
        return Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ? parsed.Host : null;
    }

    /// <summary>True when the host equals the domain or is a subdomain of it.</summary>
    public static bool DomainMatches(string host, string domain)
    {
        domain = domain.TrimStart('.');
        if (string.Equals(host, domain, StringComparison.OrdinalIgnoreCase))
            return true;
        if (host.EndsWith($".{domain}", StringComparison.OrdinalIgnoreCase))
            return true;
        if (domain.Contains('*'))
        {
            // Wildcard allow/blocklist entries are re-checked for every
            // subresource request — compile each pattern once and reuse it.
            var regex = WildcardDomainRegexCache.GetOrAdd(domain, static pattern =>
                new Regex("^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase | RegexOptions.Compiled));
            return regex.IsMatch(host);
        }
        return false;
    }

    /// <summary>True when the page host is exempt from tracker blocking.</summary>
    public static bool IsSiteAllowed(string? address, List<string> allowlist)
    {
        var host = GetHost(address);
        return !string.IsNullOrWhiteSpace(host) &&
            allowlist.Any(domain => DomainMatches(host, domain));
    }

    /// <summary>True when the request URI's host is on the tracker blocklist.</summary>
    public static bool IsBlockedTracker(string uri, List<string> blocklist)
    {
        var host = GetHost(uri);
        return !string.IsNullOrWhiteSpace(host) &&
            blocklist.Any(domain => DomainMatches(host, domain));
    }

    /// <summary>True for executable downloads that require user confirmation.</summary>
    public static bool IsDangerousDownloadExtension(string extension)
    {
        return DangerousDownloadExtensions.Contains(extension);
    }
}
