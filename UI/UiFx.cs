using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace TabloidBrowser;

/// <summary>
/// Shared UI effects: modal popup entrances and animated backdrop blurs.
/// </summary>
public static class UiFx
{
    /// <summary>
    /// Assigns a gaussian blur to the root grid behind a modal and ramps it in
    /// (0 → radius) instead of snapping it on.
    /// </summary>
    public static void PlayBackdropBlur(Grid rootGrid, double radius = 14)
    {
        var blur = new BlurEffect { Radius = 0, KernelType = KernelType.Gaussian };
        rootGrid.Effect = blur;
        blur.BeginAnimation(BlurEffect.RadiusProperty,
            new DoubleAnimation(0, radius, Motion.Base) { EasingFunction = Motion.Out });
    }

    /// <summary>
    /// Scale-and-fade entrance for modal popup content (0.96 → 1).
    /// </summary>
    public static void PlayPopupEntrance(FrameworkElement content)
    {
        if (content is null) return;

        content.RenderTransformOrigin = new Point(0.5, 0);
        content.RenderTransform = new ScaleTransform(0.96, 0.96);
        content.BeginAnimation(UIElement.OpacityProperty, null);
        content.Opacity = 0;
        content.BeginAnimation(UIElement.OpacityProperty, Motion.Fade(0, 1, Motion.Slow));

        if (content.RenderTransform is ScaleTransform scale)
        {
            var ease = Motion.Out;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0.96, 1, Motion.Slow) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(0.96, 1, Motion.Slow) { EasingFunction = ease });
        }
    }

    /// <summary>Ramps an existing backdrop blur back to zero (popup closing).</summary>
    public static void FadeOutBackdropBlur(Grid? rootGrid)
    {
        if (rootGrid?.Effect is BlurEffect blur)
        {
            blur.BeginAnimation(BlurEffect.RadiusProperty,
                new DoubleAnimation(blur.Radius, 0, Motion.Fast));
        }
    }

    /// <summary>Fade-out for popup content before it closes.</summary>
    public static void PlayPopupExit(FrameworkElement? content)
    {
        content?.BeginAnimation(UIElement.OpacityProperty, Motion.Fade(1, 0, Motion.Fast));
    }

    /// <summary>Animated hover overlay for code-built entry cards.</summary>
    public static void AttachHoverOverlay(Border card, Brush hoverBrush, CornerRadius cornerRadius)
    {
        var overlay = new Border
        {
            Background = hoverBrush,
            CornerRadius = cornerRadius,
            Opacity = 0,
            IsHitTestVisible = false
        };

        if (card.Child is Grid grid)
        {
            Grid.SetRowSpan(overlay, grid.RowDefinitions.Count);
            grid.Children.Insert(0, overlay);
        }
        else
        {
            var host = new Grid();
            var oldChild = card.Child;
            card.Child = host;
            host.Children.Add(overlay);
            if (oldChild is not null) host.Children.Add(oldChild);
        }

        card.MouseEnter += (_, _) =>
            overlay.BeginAnimation(UIElement.OpacityProperty, Motion.Fade(overlay.Opacity, 1, Motion.Fast));
        card.MouseLeave += (_, _) =>
            overlay.BeginAnimation(UIElement.OpacityProperty, Motion.Fade(overlay.Opacity, 0, TimeSpan.FromSeconds(0.12)));
    }
}
