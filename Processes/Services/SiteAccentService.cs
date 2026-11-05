using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;

namespace TabloidBrowser;

/// <summary>
/// Resolves the primary brand/accent color of the site a tab is playing media
/// from. Resolution order: curated brand map → page theme-color (meta/manifest)
/// → favicon dominant color. Results are cached per origin.
/// </summary>
public static partial class SiteAccentService
{
    private static readonly TimeSpan PositiveCacheTtl = TimeSpan.FromHours(1);
    private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromSeconds(10);
    private static readonly ConcurrentDictionary<string, CacheEntry> Cache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Lazy<Task<Color?>>> InFlight =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed record CacheEntry(Color? Color, DateTimeOffset CachedAt);

    public static async Task<Color?> ResolveAsync(
        CoreWebView2? core,
        string? url,
        bool forceRefresh = false)
    {
        var origin = SitePermissionPolicy.NormalizeOrigin(url);
        if (origin is null)
        {
            return null;
        }

        if (!forceRefresh && Cache.TryGetValue(origin, out var cached))
        {
            var ttl = cached.Color is null ? NegativeCacheTtl : PositiveCacheTtl;
            if (DateTimeOffset.UtcNow - cached.CachedAt < ttl)
            {
                return cached.Color;
            }
        }

        if (forceRefresh)
        {
            var refreshed = await ResolveCoreAsync(core, url);
            Cache[origin] = new CacheEntry(refreshed, DateTimeOffset.UtcNow);
            return refreshed;
        }

        // Collapse concurrent requests for the same origin (a play event and a
        // navigation-completed event can overlap) into one resolution.
        var lazy = InFlight.GetOrAdd(origin, _ =>
            new Lazy<Task<Color?>>(() => ResolveAndCacheAsync(core, url, origin)));

        try
        {
            return await lazy.Value;
        }
        finally
        {
            InFlight.TryRemove(origin, out _);
        }
    }

    /// <summary>
    /// Drops cached and in-flight results for a URL's origin. Called when a
    /// favicon changes so a late-loading icon can supply the accent.
    /// </summary>
    public static void Invalidate(string? url)
    {
        var origin = SitePermissionPolicy.NormalizeOrigin(url);
        if (origin is null)
        {
            return;
        }

        Cache.TryRemove(origin, out _);
        InFlight.TryRemove(origin, out _);
    }

    /// <summary>Clears all caches. Intended for tests and settings resets.</summary>
    public static void ClearCache()
    {
        Cache.Clear();
        InFlight.Clear();
    }

    private static async Task<Color?> ResolveAndCacheAsync(
        CoreWebView2? core,
        string? url,
        string origin)
    {
        var color = await ResolveCoreAsync(core, url);
        Cache[origin] = new CacheEntry(color, DateTimeOffset.UtcNow);
        return color;
    }

    private static async Task<Color?> ResolveCoreAsync(CoreWebView2? core, string? url)
    {
        Color? color = null;

        if (BrandColorMap.TryGetColor(url, out var brand) && IsUsableAccent(brand))
        {
            color = brand;
        }

        if (color is null && core is not null)
        {
            color = await ResolveFromPageAsync(core);
        }

        if (color is null && core is not null)
        {
            color = await ResolveFromFaviconAsync(core);
        }

        return color;
    }

    private static async Task<Color?> ResolveFromPageAsync(CoreWebView2 core)
    {
        try
        {
            var json = await core.ExecuteScriptAsync(PageColorScript);
            return ParsePageColors(json);
        }
        catch (Exception ex)
        {
            Log.Debug($"Site accent: page color lookup failed: {ex.Message}");
            return null;
        }
    }

    private static async Task<Color?> ResolveFromFaviconAsync(CoreWebView2 core)
    {
        try
        {
            using var stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            if (stream is null || stream.Length == 0)
            {
                return null;
            }

            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
            {
                return null;
            }

            var color = DominantColorExtractor.Extract(decoder.Frames[0]);
            return color is { } value && IsUsableAccent(value) ? value : null;
        }
        catch (Exception ex)
        {
            Log.Debug($"Site accent: favicon lookup failed: {ex.Message}");
            return null;
        }
    }

    private static Color? ParsePageColors(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            // ExecuteScriptAsync serializes the result; a string result comes
            // back as a JSON string containing our JSON payload.
            if (root.ValueKind == JsonValueKind.String)
            {
                var inner = root.GetString();
                if (string.IsNullOrWhiteSpace(inner))
                {
                    return null;
                }

                using var innerDocument = JsonDocument.Parse(inner);
                return ParseColorProperties(innerDocument.RootElement);
            }

            return ParseColorProperties(root);
        }
        catch (JsonException ex)
        {
            Log.Debug($"Site accent: could not parse page colors: {ex.Message}");
            return null;
        }
    }

    private static Color? ParseColorProperties(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in new[] { "meta", "manifestTheme" })
        {
            if (root.TryGetProperty(name, out var property) &&
                property.ValueKind == JsonValueKind.String &&
                TryParseCssColor(property.GetString(), out var color) &&
                IsUsableAccent(color))
            {
                return color;
            }
        }

        return null;
    }

    /// <summary>
    /// Parses common CSS color formats: hex (#rgb/#rrggbb/#rrggbbaa), rgb()/rgba()
    /// with integer channels, and named colors.
    /// </summary>
    public static bool TryParseCssColor(string? value, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (text.StartsWith('#'))
        {
            return TryParseHex(text, out color);
        }

        var rgb = RgbRegex().Match(text);
        if (rgb.Success &&
            byte.TryParse(rgb.Groups[1].Value, out var r) &&
            byte.TryParse(rgb.Groups[2].Value, out var g) &&
            byte.TryParse(rgb.Groups[3].Value, out var b))
        {
            color = Color.FromRgb(r, g, b);
            return true;
        }

        try
        {
            if (ColorConverter.ConvertFromString(text) is Color parsed)
            {
                color = parsed;
                return true;
            }
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException)
        {
            // Not a named WPF color.
        }

        return false;
    }

    /// <summary>
    /// Rejects colors that are too light, too dark, or effectively gray; those
    /// do not read as a meaningful accent on the tab strip.
    /// </summary>
    public static bool IsUsableAccent(Color color)
    {
        var max = Math.Max(color.R, Math.Max(color.G, color.B));
        var min = Math.Min(color.R, Math.Min(color.G, color.B));
        return max >= 40 && min <= 240 && max - min >= 24;
    }

    private static bool TryParseHex(string text, out Color color)
    {
        color = default;
        var hex = text[1..];

        if (hex.Length == 3)
        {
            hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
        }
        else if (hex.Length == 8)
        {
            // CSS uses #RRGGBBAA; drop the alpha channel for the accent.
            hex = hex[..6];
        }

        if (hex.Length != 6 ||
            !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return false;
        }

        color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    [GeneratedRegex(@"^rgba?\(\s*([0-9]{1,3})\s*,\s*([0-9]{1,3})\s*,\s*([0-9]{1,3})",
        RegexOptions.IgnoreCase)]
    private static partial Regex RgbRegex();

    // Runs in the page: reads <meta name="theme-color"> and the web app
    // manifest's theme_color, returning a small JSON object. ExecuteScriptAsync
    // awaits the returned promise before serializing the result.
    private const string PageColorScript = """
        (async () => {
          try {
            const meta = document.querySelector('meta[name="theme-color"]');
            const metaColor = meta && meta.content ? meta.content.trim() : null;

            let manifestTheme = null;
            const link = document.querySelector('link[rel="manifest"]');
            if (link && link.href && typeof fetch === 'function') {
              try {
                const response = await fetch(link.href, { credentials: 'same-origin' });
                if (response.ok) {
                  const manifest = await response.json();
                  if (manifest && typeof manifest.theme_color === 'string') {
                    manifestTheme = manifest.theme_color.trim();
                  }
                }
              } catch (_) { }
            }

            return JSON.stringify({ meta: metaColor, manifestTheme });
          } catch (_) {
            return JSON.stringify({ meta: null, manifestTheme: null });
          }
        })()
        """;
}
