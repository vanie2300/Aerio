using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace TabloidBrowser;

public partial class AerioAirLoadingOverlay : UserControl
{
    private bool _loadingSpinnerActive;
    private bool _airAnimationsRunning;
    private int _transitionVersion;

    public AerioAirLoadingOverlay()
    {
        InitializeComponent();

        // Restart looping effects if WPF temporarily removes/re-adds this control.
        Loaded += (_, _) =>
        {
            if (_loadingSpinnerActive)
                StartAirAnimations();
        };
        Unloaded += (_, _) => StopAirAnimations();
    }

    // Preserved public method: existing browser calls do not need to change.
    public void SetLoadingSpinnerActive(bool active)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetLoadingSpinnerActive(active));
            return;
        }

        if (_loadingSpinnerActive == active)
            return;

        _loadingSpinnerActive = active;
        int thisTransition = ++_transitionVersion;

        // Read the currently displayed opacity BEFORE cancelling any in-flight fade.
        // This avoids a flash if loading toggles quickly (start > stop > start).
        double currentOpacity = Visibility == Visibility.Visible ? Opacity : 0;
        BeginAnimation(OpacityProperty, null);
        Opacity = 1; // The base value after FillBehavior.Stop completes.

        if (active)
        {
            Visibility = Visibility.Visible;
            StartAirAnimations();

            BeginAnimation(OpacityProperty, new DoubleAnimation
            {
                From = currentOpacity,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(280),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            });
            return;
        }

        var fadeOut = new DoubleAnimation
        {
            From = currentOpacity,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(250),
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            FillBehavior = FillBehavior.Stop
        };
        fadeOut.Completed += (_, _) =>
        {
            // Old fade callbacks must never hide a newly reopened overlay.
            if (_loadingSpinnerActive || thisTransition != _transitionVersion)
                return;

            Visibility = Visibility.Collapsed;
            StopAirAnimations();
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
        };
        BeginAnimation(OpacityProperty, fadeOut);
    }

    // Preserved public method: supports your existing dynamic title / URL display.
    public void SetLoadingMessage(string title)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetLoadingMessage(title));
            return;
        }

        LoadingTitleText.Text = string.IsNullOrWhiteSpace(title)
            ? "Opening your space"
            : title;
    }

    private void StartAirAnimations()
    {
        if (_airAnimationsRunning)
            return;

        _airAnimationsRunning = true;

        // Each stream travels left-to-right, fades in, then fades out before looping.
        // Unlike AutoReverse, this reads as an actual breeze instead of a bounce.
        FlowWind(WindPath1, 4.2, 0.0, -12, 12, 0.90);
        FlowWind(WindPath2, 4.2, 0.55, -12, 12, 0.70);
        FlowWind(WindPath3, 4.2, 1.10, -12, 12, 0.50);

        DriftCloud(CloudLeftMove, -7, 8, 12);
        DriftCloud(CloudRightMove, 7, -8, 14);

        Breathe(GlowScale, ScaleTransform.ScaleXProperty);
        Breathe(GlowScale, ScaleTransform.ScaleYProperty);

        PulseDot(LoadingDot1, 0);
        PulseDot(LoadingDot2, 0.22);
        PulseDot(LoadingDot3, 0.44);
    }

    private void StopAirAnimations()
    {
        if (!_airAnimationsRunning)
            return;

        _airAnimationsRunning = false;

        StopWind(WindPath1);
        StopWind(WindPath2);
        StopWind(WindPath3);
        StopDrift(CloudLeftMove);
        StopDrift(CloudRightMove);

        GlowScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        GlowScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        GlowScale.ScaleX = 1;
        GlowScale.ScaleY = 1;

        StopDot(LoadingDot1);
        StopDot(LoadingDot2);
        StopDot(LoadingDot3);
    }

    private static void FlowWind(Path path,
                                 double durationSeconds,
                                 double delaySeconds,
                                 double fromX,
                                 double toX,
                                 double peakOpacity)
    {
        if (path.RenderTransform is not TranslateTransform move)
            throw new InvalidOperationException("Wind paths require a TranslateTransform.");

        path.Opacity = 0;
        move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation
        {
            From = fromX,
            To = toX,
            Duration = TimeSpan.FromSeconds(durationSeconds),
            BeginTime = TimeSpan.FromSeconds(delaySeconds),
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });

        // Fully transparent at each loop boundary so the position reset is invisible.
        var opacity = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(durationSeconds),
            BeginTime = TimeSpan.FromSeconds(delaySeconds),
            RepeatBehavior = RepeatBehavior.Forever
        };
        opacity.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        opacity.KeyFrames.Add(new LinearDoubleKeyFrame(peakOpacity, KeyTime.FromPercent(0.20)));
        opacity.KeyFrames.Add(new LinearDoubleKeyFrame(peakOpacity, KeyTime.FromPercent(0.76)));
        opacity.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        path.BeginAnimation(UIElement.OpacityProperty, opacity);
    }

    private static void DriftCloud(TranslateTransform transform,
                                   double fromX,
                                   double toX,
                                   double durationSeconds)
    {
        transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation
        {
            From = fromX,
            To = toX,
            Duration = TimeSpan.FromSeconds(durationSeconds),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
    }

    private static void Breathe(ScaleTransform transform, DependencyProperty property)
    {
        transform.BeginAnimation(property, new DoubleAnimation
        {
            From = 0.95,
            To = 1.07,
            Duration = TimeSpan.FromSeconds(3.1),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
    }

    private static void PulseDot(Ellipse dot, double delaySeconds)
    {
        var pulse = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(1.55),
            BeginTime = TimeSpan.FromSeconds(delaySeconds),
            RepeatBehavior = RepeatBehavior.Forever
        };
        pulse.KeyFrames.Add(new LinearDoubleKeyFrame(0.28, KeyTime.FromPercent(0)));
        pulse.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.38)));
        pulse.KeyFrames.Add(new LinearDoubleKeyFrame(0.28, KeyTime.FromPercent(1)));
        dot.BeginAnimation(UIElement.OpacityProperty, pulse);
    }

    private static void StopWind(Path path)
    {
        path.BeginAnimation(UIElement.OpacityProperty, null);
        path.Opacity = 0;

        if (path.RenderTransform is not TranslateTransform move)
            return;

        move.BeginAnimation(TranslateTransform.XProperty, null);
        move.X = 0;
    }

    private static void StopDrift(TranslateTransform transform)
    {
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        transform.X = 0;
    }

    private static void StopDot(Ellipse dot)
    {
        dot.BeginAnimation(UIElement.OpacityProperty, null);
        dot.Opacity = 0.3;
    }
}
