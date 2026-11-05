using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace TabloidBrowser;

/// <summary>
/// WPF has no built-in GridLength animation. This interpolates pixel-sized
/// GridLength values so row/column collapses can move with the same duration
/// and easing as the rest of the UI instead of snapping in a single frame.
/// </summary>
public class GridLengthAnimation : AnimationTimeline
{
    public static readonly DependencyProperty FromProperty = DependencyProperty.Register(
        nameof(From), typeof(double), typeof(GridLengthAnimation), new PropertyMetadata(0.0));

    public static readonly DependencyProperty ToProperty = DependencyProperty.Register(
        nameof(To), typeof(double), typeof(GridLengthAnimation), new PropertyMetadata(0.0));

    public double From
    {
        get => (double)GetValue(FromProperty);
        set => SetValue(FromProperty, value);
    }

    public double To
    {
        get => (double)GetValue(ToProperty);
        set => SetValue(ToProperty, value);
    }

    public static readonly DependencyProperty EasingFunctionProperty = DependencyProperty.Register(
        nameof(EasingFunction), typeof(IEasingFunction), typeof(GridLengthAnimation), new PropertyMetadata(null));

    public IEasingFunction? EasingFunction
    {
        get => (IEasingFunction?)GetValue(EasingFunctionProperty);
        set => SetValue(EasingFunctionProperty, value);
    }

    public override Type TargetPropertyType => typeof(GridLength);

    protected override Freezable CreateInstanceCore() => new GridLengthAnimation();

    public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue, AnimationClock animationClock)
    {
        var progress = animationClock.CurrentProgress ?? 0;
        var eased = EasingFunction?.Ease(progress) ?? progress;
        return new GridLength(From + ((To - From) * eased), GridUnitType.Pixel);
    }
}
