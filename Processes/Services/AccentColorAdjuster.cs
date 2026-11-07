using System.Windows.Media;

namespace TabloidBrowser;

/// <summary>
/// Adjusts accent colors so they stay legible on the tab strip. Brand colors
/// are preserved when they already meet the target contrast; otherwise their
/// HSL lightness is nudged until they do.
/// </summary>
public static class AccentColorAdjuster
{
    /// <summary>WCAG 1.4.11 non-text contrast minimum.</summary>
    public const double MinimumContrast = 3.0;

    private const double DarkBackgroundThreshold = 0.18;
    private const double LightnessStep = 0.02;
    private const int MaxLightnessSteps = 50;

    public static Color AdjustForContrast(
        Color color,
        Color background,
        double minimumContrast = MinimumContrast)
    {
        if (ContrastRatio(color, background) >= minimumContrast)
        {
            return color;
        }

        var (hue, saturation, lightness) = RgbToHsl(color);
        var lighten = RelativeLuminance(background) < DarkBackgroundThreshold;

        var best = color;
        var bestContrast = ContrastRatio(color, background);

        for (var step = 1; step <= MaxLightnessSteps; step++)
        {
            var nextLightness = lighten
                ? lightness + (step * LightnessStep)
                : lightness - (step * LightnessStep);

            if (nextLightness is < 0.0 or > 1.0)
            {
                break;
            }

            var candidate = HslToRgb(hue, saturation, nextLightness);
            var contrast = ContrastRatio(candidate, background);

            if (contrast >= minimumContrast)
            {
                return candidate;
            }

            if (contrast > bestContrast)
            {
                best = candidate;
                bestContrast = contrast;
            }
        }

        return best;
    }

    public static double ContrastRatio(Color first, Color second)
    {
        var a = RelativeLuminance(first);
        var b = RelativeLuminance(second);
        var lighter = Math.Max(a, b);
        var darker = Math.Min(a, b);
        return (lighter + 0.05) / (darker + 0.05);
    }

    public static double RelativeLuminance(Color color) =>
        (0.2126 * Linearize(color.R)) +
        (0.7152 * Linearize(color.G)) +
        (0.0722 * Linearize(color.B));

    /// <summary>
    /// Linear blend between two colours. <paramref name="amount"/> is how much of
    /// <paramref name="overlay"/> to mix in (0 = base colour, 1 = overlay).
    /// </summary>
    public static Color Blend(Color baseColor, Color overlay, double amount)
    {
        var t = Math.Clamp(amount, 0.0, 1.0);
        return Color.FromRgb(
            BlendChannel(baseColor.R, overlay.R, t),
            BlendChannel(baseColor.G, overlay.G, t),
            BlendChannel(baseColor.B, overlay.B, t));
    }

    private static byte BlendChannel(byte from, byte to, double t) =>
        (byte)Math.Clamp((int)Math.Round(from + ((to - from) * t)), 0, 255);

    private static double Linearize(byte channel)
    {
        var value = channel / 255.0;
        return value <= 0.04045
            ? value / 12.92
            : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    private static (double Hue, double Saturation, double Lightness) RgbToHsl(Color color)
    {
        var r = color.R / 255.0;
        var g = color.G / 255.0;
        var b = color.B / 255.0;

        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var lightness = (max + min) / 2.0;

        if (Math.Abs(max - min) < double.Epsilon)
        {
            return (0, 0, lightness);
        }

        var delta = max - min;
        var saturation = lightness > 0.5
            ? delta / (2.0 - max - min)
            : delta / (max + min);

        double hue;
        if (Math.Abs(max - r) < double.Epsilon)
        {
            hue = ((g - b) / delta) + (g < b ? 6.0 : 0.0);
        }
        else if (Math.Abs(max - g) < double.Epsilon)
        {
            hue = ((b - r) / delta) + 2.0;
        }
        else
        {
            hue = ((r - g) / delta) + 4.0;
        }

        return (hue / 6.0, saturation, lightness);
    }

    private static Color HslToRgb(double hue, double saturation, double lightness)
    {
        if (saturation <= 0)
        {
            var gray = ToByte(lightness);
            return Color.FromRgb(gray, gray, gray);
        }

        var q = lightness < 0.5
            ? lightness * (1.0 + saturation)
            : lightness + saturation - (lightness * saturation);
        var p = (2.0 * lightness) - q;

        return Color.FromRgb(
            ToByte(HueToChannel(p, q, hue + (1.0 / 3.0))),
            ToByte(HueToChannel(p, q, hue)),
            ToByte(HueToChannel(p, q, hue - (1.0 / 3.0))));
    }

    private static double HueToChannel(double p, double q, double t)
    {
        if (t < 0) t += 1.0;
        if (t > 1) t -= 1.0;
        if (t < 1.0 / 6.0) return p + ((q - p) * 6.0 * t);
        if (t < 1.0 / 2.0) return q;
        if (t < 2.0 / 3.0) return p + ((q - p) * ((2.0 / 3.0) - t) * 6.0);
        return p;
    }

    private static byte ToByte(double value) =>
        (byte)Math.Clamp((int)Math.Round(value * 255.0), 0, 255);
}
