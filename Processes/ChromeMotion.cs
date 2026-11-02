namespace TabloidBrowser;

internal static class ChromeMotion
{
    public static double EaseOutCubic(double from, double to, double progress)
    {
        var clampedProgress = Math.Clamp(progress, 0, 1);
        var easedProgress = 1 - Math.Pow(1 - clampedProgress, 3);
        return from + ((to - from) * easedProgress);
    }
}
