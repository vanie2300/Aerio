using System;
using System.Collections.Generic;
using System.Linq;

namespace TabloidBrowser;

/// <summary>
/// Origin-boundary rules for per-site permissions. Hosts are validated real
/// hostnames (scheme, port, path, and credentials stripped) and matching is
/// exact-host only: a rule for example.com must never apply to
/// evilexample.com or example.com.evil.com.
/// </summary>
public static class SitePermissionPolicy
{
    /// <summary>
    /// Normalizes a URL or bare host to a bare lowercase hostname.
    /// Returns null when the input is not a plausible host.
    /// </summary>
    public static string? NormalizeHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var input = value.Trim();

        // Inputs that already look scheme-prefixed must parse as absolute
        // URLs on their own — never re-prefix them (a broken entry like
        // "https://" would otherwise become a rule for a host named "https").
        if (input.Contains("://", StringComparison.Ordinal))
        {
            return Uri.TryCreate(input, UriKind.Absolute, out var parsed) &&
                (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)
                ? ValidatedHost(parsed)
                : null;
        }

        if (Uri.TryCreate(input, UriKind.Absolute, out var absolute) &&
            (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            return ValidatedHost(absolute);
        }

        if (Uri.TryCreate($"https://{input}", UriKind.Absolute, out var withScheme) &&
            !string.IsNullOrWhiteSpace(withScheme.Host))
        {
            return ValidatedHost(withScheme);
        }

        return null;
    }

    private static string? ValidatedHost(Uri uri)
    {
        var host = uri.Host;
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        // Reject anything Uri accepted but that is not a plausible hostname
        // (whitespace, control characters, etc.) — CheckHostName returns
        // Unknown for those.
        var type = Uri.CheckHostName(host);
        if (type != UriHostNameType.Dns &&
            type != UriHostNameType.IPv4 &&
            type != UriHostNameType.IPv6)
        {
            return null;
        }

        return host.ToLowerInvariant();
    }

    /// <summary>
    /// Validates and normalizes stored rules: host must parse as a hostname,
    /// permission must be non-empty, and state must be Allow or Deny
    /// (Block is normalized to Deny; Default rules carry no decision and are dropped).
    /// </summary>
    public static List<SitePermissionRule> NormalizeRules(IEnumerable<SitePermissionRule>? rules)
    {
        if (rules is null)
        {
            return [];
        }

        return rules
            .Select(rule => new SitePermissionRule(
                NormalizeHost(rule.Host) ?? "",
                rule.Permission?.Trim() ?? "",
                NormalizeState(rule.State)))
            .Where(rule => rule.Host.Length > 0 &&
                rule.Permission.Length > 0 &&
                rule.State is "Allow" or "Deny")
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Effective state for a permission on the given page URL or host.
    /// Exact-host match only; anything unrecognized resolves to "Default".
    /// </summary>
    public static string StateFor(IEnumerable<SitePermissionRule> rules, string? hostOrUrl, string permission)
    {
        var host = NormalizeHost(hostOrUrl);
        if (string.IsNullOrWhiteSpace(host))
        {
            return "Default";
        }

        return rules.FirstOrDefault(rule =>
            string.Equals(rule.Host, host, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(rule.Permission, permission, StringComparison.OrdinalIgnoreCase))?.State ?? "Default";
    }

    public static string NormalizeState(string? state)
    {
        return state switch
        {
            "Allow" => "Allow",
            "Deny" or "Block" => "Deny",
            _ => "Default"
        };
    }
}
