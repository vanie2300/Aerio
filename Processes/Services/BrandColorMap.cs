using System.Windows.Media;

namespace TabloidBrowser;

/// <summary>
/// Curated brand colors for well-known audio/video sites. Used as a fast,
/// deterministic fallback before inspecting a page's favicon or theme color.
/// Values are approximate brand accents, not official trademarks.
/// </summary>
public static class BrandColorMap
{
    private static readonly IReadOnlyDictionary<string, Color> Colors =
        new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
        {
            // Video streaming
            ["youtube.com"] = Hex(0xFF0000),
            ["youtu.be"] = Hex(0xFF0000),
            ["netflix.com"] = Hex(0xE50914),
            ["twitch.tv"] = Hex(0x9146FF),
            ["vimeo.com"] = Hex(0x1AB7EA),
            ["hulu.com"] = Hex(0x1CE783),
            ["disneyplus.com"] = Hex(0x113CCF),
            ["primevideo.com"] = Hex(0x00A8E1),
            ["max.com"] = Hex(0x002BE7),
            ["hbomax.com"] = Hex(0x5C2D91),
            ["paramountplus.com"] = Hex(0x0064FF),
            ["crunchyroll.com"] = Hex(0xF47521),
            ["plex.tv"] = Hex(0xE5A00D),
            ["rumble.com"] = Hex(0x85C742),
            ["kick.com"] = Hex(0x53FC18),
            ["bilibili.com"] = Hex(0x00A1D6),
            ["dailymotion.com"] = Hex(0x00AAFF),
            ["niconico.com"] = Hex(0xE60012),

            // Social / video calling
            ["tiktok.com"] = Hex(0xFE2C55),
            ["facebook.com"] = Hex(0x1877F2),
            ["instagram.com"] = Hex(0xE1306C),
            ["reddit.com"] = Hex(0xFF4500),
            ["discord.com"] = Hex(0x5865F2),
            ["zoom.us"] = Hex(0x2D8CFF),
            ["teams.microsoft.com"] = Hex(0x6264A7),
            ["snapchat.com"] = Hex(0xFFFC00),
            ["skype.com"] = Hex(0x00AFF0),

            // Audio streaming
            ["spotify.com"] = Hex(0x1DB954),
            ["soundcloud.com"] = Hex(0xFF5500),
            ["music.apple.com"] = Hex(0xFA243C),
            ["deezer.com"] = Hex(0xA238FF),
            ["pandora.com"] = Hex(0x3668FF),
            ["iheart.com"] = Hex(0xC6002B),
            ["audible.com"] = Hex(0xF8991C)
        };

    private static readonly string[] KeysByLength =
        Colors.Keys.OrderByDescending(key => key.Length).ToArray();

    /// <summary>
    /// Finds the brand color for a URL or bare host. Subdomains match their
    /// registrable domain (e.g. music.youtube.com → youtube.com).
    /// </summary>
    public static bool TryGetColor(string? urlOrHost, out Color color)
    {
        color = default;
        var host = SitePermissionPolicy.NormalizeHost(urlOrHost);
        if (host is null)
        {
            return false;
        }

        foreach (var key in KeysByLength)
        {
            if (host.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + key, StringComparison.OrdinalIgnoreCase))
            {
                color = Colors[key];
                return true;
            }
        }

        return false;
    }

    private static Color Hex(uint rgb) =>
        Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
