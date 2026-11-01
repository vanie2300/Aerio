using System;
using System.Collections.Generic;
using System.Linq;

namespace TabloidBrowser;

/// <summary>
/// Origin-boundary rules for per-site permissions.
///
/// Rules are keyed by <see cref="NormalizeOrigin"/>: scheme + canonical host +
/// effective port (path, query, fragment, and credentials excluded).
/// https://example.com, http://example.com, and https://example.com:8443 are
/// different origins and never share a grant. Matching is exact-origin: a rule
/// for https://example.com must never apply to evilexample.com or
/// example.com.evil.com.
/// </summary>
public static class SitePermissionPolicy
{
    /// <summary>
    /// Canonicalizes a URL or origin string to scheme + host + effective port.
    /// Returns null when the input is not a plausible HTTP(S) origin.
    /// </summary>
    public static string? NormalizeOrigin(string? input)
    {
        if (!Uri.TryCreate(input?.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || uri.HostNameType == UriHostNameType.Unknown)
        {
            return null;
        }

        // GetLeftPart(Authority) is scheme://host[:port] — path, query, and
        // fragment are excluded, and default ports are canonicalized away.
        return uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
    }

    /// <summary>
    /// Normalizes a URL or bare host to a bare lowercase hostname (no scheme
    /// or port). Used for the legacy hostname-only rule format and for
    /// interpreting free-text site input; prefer <see cref="NormalizeOrigin"/>
    /// for permission decisions.
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
        // "https://" would otherwise become a host named "https").
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

    /// <summary>
    /// Resolves free-text site input (combo box, prompt) to a canonical
    /// origin. A bare host like "example.com" becomes its HTTPS origin —
    /// never both schemes.
    /// </summary>
    public static string? OriginFromUserInput(string? value)
    {
        var origin = NormalizeOrigin(value);
        if (origin is not null)
        {
            return origin;
        }

        return NormalizeHost(value) is { } host ? $"https://{host}" : null;
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
    /// Validates and normalizes stored rules: the key must resolve to an
    /// origin, the permission must be non-empty, and the state must be Allow
    /// or Deny (Block is normalized to Deny; Default rules carry no decision
    /// and are dropped).
    /// </summary>
    public static List<SitePermissionRule> NormalizeRules(IEnumerable<SitePermissionRule>? rules)
    {
        if (rules is null)
        {
            return [];
        }

        var resolved = rules
            .Select((rule, index) =>
            {
                var explicitOrigin = NormalizeOrigin(rule.Host);
                var isExplicit = explicitOrigin is not null;
                var key = explicitOrigin ??
                    (NormalizeHost(rule.Host) is { } host ? $"https://{host}" : string.Empty);
                return new
                {
                    Index = index,
                    IsExplicit = isExplicit,
                    Rule = new SitePermissionRule(key, rule.Permission?.Trim() ?? "", NormalizeState(rule.State))
                };
            })
            .Where(x => x.Rule.Host.Length > 0 &&
                x.Rule.Permission.Length > 0 &&
                x.Rule.State is "Allow" or "Deny")
            .ToList();

        // When a migrated legacy rule conflicts with an explicit origin rule
        // for the same origin+permission, the explicit rule wins.
        return resolved
            .GroupBy(
                x => x.Rule.Host + "|" + x.Rule.Permission,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(x => x.IsExplicit)
                .ThenBy(x => x.Index)
                .First().Rule)
            .ToList();
    }

    /// <summary>
    /// Effective state for a permission on the given page URL or origin.
    /// Exact-origin match only; anything unrecognized resolves to "Default".
    /// </summary>
    public static string StateFor(IEnumerable<SitePermissionRule> rules, string? originOrUrl, string permission)
    {
        var origin = NormalizeOrigin(originOrUrl);
        if (origin is null)
        {
            return "Default";
        }

        return rules.FirstOrDefault(rule =>
            string.Equals(rule.Host, origin, StringComparison.OrdinalIgnoreCase) &&
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
