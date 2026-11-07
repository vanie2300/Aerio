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
/// from. Resolution is dynamic-first: page-declared colors (theme-color,
/// mask-icon, tile color, CSS custom properties, header/nav background,
/// manifest theme_color)  favicon dominant color  curated brand map (last
/// resort)  none. Results are cached per origin.
/// </summary>
public static partial class SiteAccentService
{
    private static readonly TimeSpan PositiveCacheTtl = TimeSpan.FromHours(1);
    private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromSeconds(10);
    private static readonly ConcurrentDictionary<string, CacheEntry> Cache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Lazy<Task<Color?>>> InFlight =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Upper bound on cached origins. Entries are tiny, but the cache had no
    /// eviction at all — over a long session every visited origin accumulated
    /// forever. Insertion-order FIFO keeps the set bounded.
    /// </summary>
    internal const int MaxCachedOrigins = 256;

    /// <summary>
    /// Insertion order for FIFO eviction. Re-resolving an origin enqueues a
    /// duplicate marker; dequeuing an already-removed key is a harmless no-op.
    /// </summary>
    private static readonly ConcurrentQueue<string> CacheInsertionOrder = new();

    internal static int CacheCountForTests => Cache.Count;

    private static void StoreCacheEntry(string origin, CacheEntry entry)
    {
        Cache[origin] = entry;
        CacheInsertionOrder.Enqueue(origin);
        while (CacheInsertionOrder.Count > MaxCachedOrigins &&
               CacheInsertionOrder.TryDequeue(out var oldest))
        {
            Cache.TryRemove(oldest, out _);
        }
    }

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
            StoreCacheEntry(origin, new CacheEntry(refreshed, DateTimeOffset.UtcNow));
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
        CacheInsertionOrder.Clear();
        InFlight.Clear();
    }

    private static async Task<Color?> ResolveAndCacheAsync(
        CoreWebView2? core,
        string? url,
        string origin)
    {
        var color = await ResolveCoreAsync(core, url);
        StoreCacheEntry(origin, new CacheEntry(color, DateTimeOffset.UtcNow));
        return color;
    }

    private static async Task<Color?> ResolveCoreAsync(CoreWebView2? core, string? url)
    {
        Color? color = null;

        // Dynamic signals first: whatever the page itself declares (theme-color,
        // mask-icon, tile color, CSS variables, header colour, manifest) or, if
        // it declares nothing, its favicon's dominant colour.
        if (core is not null)
        {
            color = await ResolveFromPageAsync(core);

            if (color is null)
            {
                color = await ResolveFromFaviconAsync(core);
            }
        }

        // The curated map is now only a last-resort fallback for sites that
        // expose no colour signal at all (and is the sole source when no
        // WebView is available, e.g. during tests or early startup).
        if (color is null && BrandColorMap.TryGetColor(url, out var brand) && IsUsableAccent(brand))
        {
            color = brand;
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

    internal static Color? ParsePageColors(string? json)
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

        // The page script returns every dynamic candidate in priority order;
        // the first one that parses as a usable accent wins.
        if (root.TryGetProperty("candidates", out var candidates) &&
            candidates.ValueKind == JsonValueKind.Array)
        {
            foreach (var candidate in candidates.EnumerateArray())
            {
                if (candidate.ValueKind == JsonValueKind.String &&
                    TryParseCssColor(candidate.GetString(), out var candidateColor) &&
                    IsUsableAccent(candidateColor))
                {
                    return candidateColor;
                }
            }
        }

        // Legacy fields (older payloads / defensive fallback).
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

    // Runs in the page and collects every dynamic colour signal it can find,
    // in priority order: theme-color tags (media-matched first), Safari's
    // mask-icon colour, the legacy Windows tile colour, brand-ish CSS custom
    // properties, coloured header/nav bars, then the web-app manifest's
    // theme_color. ExecuteScriptAsync awaits the returned promise before
    // serializing the result.
    private const string PageColorScript = """
        (async () => {
          const candidates = [];
          const push = (value) => {
            if (value && typeof value === 'string') {
              const v = value.trim();
              if (!v || v === 'transparent' || v === 'rgba(0, 0, 0, 0)') return;
              candidates.push(v);
            }
          };

          try {
            // 1) theme-color tags, preferring ones whose media query matches now.
            const metas = Array.from(document.querySelectorAll('meta[name="theme-color"]'));
            if (metas.length) {
              const matches = (m) => {
                const media = m.getAttribute('media');
                if (!media) return true;
                try { return window.matchMedia(media).matches; } catch (_) { return false; }
              };
              metas.filter(matches).forEach(m => push(m.content));
              metas.forEach(m => push(m.content));
            }

            // 2) Safari pinned-tab colour.
            const mask = document.querySelector('link[rel="mask-icon"]');
            if (mask) push(mask.getAttribute('color'));

            // 3) Legacy Windows tile colour.
            const tile = document.querySelector('meta[name="msapplication-TileColor"]');
            if (tile) push(tile.content);

            // 4) CSS custom properties that commonly hold the brand colour.
            const names = ['--accent-color','--accent','--primary-color','--primary',
                           '--brand-color','--brand','--theme-color','--color-primary',
                           '--main-color','--clr-primary'];
            const roots = [document.documentElement, document.body].filter(Boolean);
            for (const name of names) {
              for (const el of roots) {
                try { push(getComputedStyle(el).getPropertyValue(name)); } catch (_) { }
              }
            }

            // 5) Coloured header/nav bars are the brand on many sites.
            const headerSelectors = ['header','nav','[role="banner"]','.navbar',
                                     '.site-header','.topbar','.app-header','#header'];
            for (const selector of headerSelectors) {
              const el = document.querySelector(selector);
              if (!el) continue;
              try {
                const style = getComputedStyle(el);
                push(style.backgroundColor);
                if (style.backgroundImage && style.backgroundImage.indexOf('gradient') >= 0) {
                  const match = style.backgroundImage.match(/(#[0-9a-f]{3,8}|rgba?\([^)]+\))/i);
                  if (match) push(match[1]);
                }
              } catch (_) { }
            }

            // 6) manifest theme_color (async).
            let manifestTheme = null;
            const link = document.querySelector('link[rel="manifest"]');
            if (link && link.href && typeof fetch === 'function') {
              try {
                const response = await fetch(link.href, { credentials: 'same-origin' });
                if (response.ok) {
                  const manifest = await response.json();
                  if (manifest && typeof manifest.theme_color === 'string') {
                    manifestTheme = manifest.theme_color.trim();
                    push(manifestTheme);
                  }
                }
              } catch (_) { }
            }

            const meta = metas.length ? metas[0].content : null;
            return JSON.stringify({ candidates, meta, manifestTheme });
          } catch (_) {
            return JSON.stringify({ candidates: [], meta: null, manifestTheme: null });
          }
        })()
        """;
}
