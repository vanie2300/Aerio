using System;
using System.Collections.Generic;
using System.Linq;

namespace TabloidBrowser;

/// <summary>
/// Validation and normalization rules for new-tab shortcuts. Kept free of WPF
/// types so the rules can be unit-tested in isolation.
/// </summary>
public static class NewTabShortcutPolicy
{
    public static bool IsValidShortcutUrl(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            uri.Scheme is ("http" or "https") &&
            !string.IsNullOrWhiteSpace(uri.Host);
    }

    public static bool TryNormalizeShortcutUrl(string value, out string normalized)
    {
        normalized = value.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (!normalized.Contains("://", StringComparison.Ordinal))
        {
            normalized = "https://" + normalized;
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        normalized = uri.ToString();
        return true;
    }

    /// <summary>Trims names/urls, fills missing schemes, and drops invalid entries.</summary>
    public static List<NewTabShortcut> Normalize(IEnumerable<NewTabShortcut>? shortcuts)
    {
        return (shortcuts ?? BrowserSettings.DefaultNewTabShortcuts)
            .Select(shortcut => shortcut with
            {
                Name = shortcut.Name?.Trim() ?? "",
                Url = shortcut.Url?.Trim() ?? ""
            })
            .Where(shortcut => !string.IsNullOrWhiteSpace(shortcut.Name) &&
                               IsValidShortcutUrl(shortcut.Url))
            .ToList();
    }
}
