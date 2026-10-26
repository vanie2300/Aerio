using System;
using System.Windows.Media.Animation;

namespace TabloidBrowser;

/// <summary>
/// Central motion tokens for UI transitions. Use these instead of ad-hoc
/// durations/easings so the whole app moves with one rhythm.
/// </summary>
public static class Motion
{
    /// <summary>Micro feedback: press, hover-tint, small state flips.</summary>
    public static readonly TimeSpan Fast = TimeSpan.FromSeconds(0.10);

    /// <summary>Standard transitions: fades, slides, popup entrances.</summary>
    public static readonly TimeSpan Base = TimeSpan.FromSeconds(0.15);

    /// <summary>Larger movements: sidebar, panes, staged content.</summary>
    public static readonly TimeSpan Slow = TimeSpan.FromSeconds(0.25);

    /// <summary>Hero moments: start page entrance.</summary>
    public static readonly TimeSpan Hero = TimeSpan.FromSeconds(0.60);

    /// <summary>Shared EaseOut circle — the app's default easing.</summary>
    public static CircleEase Out { get; } = new() { EasingMode = EasingMode.EaseOut };

    /// <summary>Shared EaseOut cubic for longer travel.</summary>
    public static CubicEase OutCubic { get; } = new() { EasingMode = EasingMode.EaseOut };

    /// <summary>Shared EaseInOut quadratic for two-way moves.</summary>
    public static QuadraticEase InOut { get; } = new() { EasingMode = EasingMode.EaseInOut };

    /// <summary>Convenience factory: opacity fade with the default easing.</summary>
    public static DoubleAnimation Fade(double from, double to, TimeSpan? duration = null) =>
        new(from, to, duration ?? Base) { EasingFunction = Out };

    /// <summary>Convenience factory: slide on a TranslateTransform axis.</summary>
    public static DoubleAnimation Slide(double from, double to, TimeSpan? duration = null) =>
        new(from, to, duration ?? Base) { EasingFunction = Out };
}
