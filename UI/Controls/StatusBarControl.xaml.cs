using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace TabloidBrowser;

public partial class StatusBarControl : UserControl
{
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private int _animationVersion;

    public StatusBarControl()
    {
        InitializeComponent();
        StatusText.TextChanged += StatusText_TextChanged;
        _hideTimer.Tick += HideTimer_Tick;
        ToastPopup.Opened += (_, _) => UpdateToastPlacement();
        Loaded += StatusBarControl_Loaded;
    }

    private void StatusBarControl_Loaded(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } window)
        {
            ToastPopup.PlacementTarget = window;
            window.SizeChanged += Window_SizeChanged;
        }

        UpdateToastPlacement();
        if (!string.IsNullOrWhiteSpace(StatusText.Text))
        {
            PresentToast();
        }
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateToastPlacement();

    private void UpdateToastPlacement()
    {
        if (ToastPopup.PlacementTarget is not Window window || !ToastPopup.IsOpen)
        {
            return;
        }

        ToastPopup.HorizontalOffset = Math.Max(0, (window.ActualWidth - ToastSurface.ActualWidth) / 2);
        ToastPopup.VerticalOffset = Math.Max(0, window.ActualHeight - ToastSurface.ActualHeight - 24);
    }

    private void StatusText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(StatusText.Text))
        {
            return;
        }

        _hideTimer.Stop();
        _animationVersion++;
        if (IsLoaded && ToastPopup.PlacementTarget is Window)
        {
            PresentToast();
        }
    }

    private void PresentToast()
    {
        ToastSurface.BeginAnimation(OpacityProperty, null);
        ToastSurface.Opacity = 0;
        ToastPopup.IsOpen = true;
        UpdateToastPlacement();
        ToastSurface.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        _hideTimer.Start();
    }

    private void HideTimer_Tick(object? sender, EventArgs e)
    {
        _hideTimer.Stop();
        var animationVersion = ++_animationVersion;
        var fadeOut = new DoubleAnimation(ToastSurface.Opacity, 0, TimeSpan.FromMilliseconds(180));
        fadeOut.Completed += (_, _) =>
        {
            if (animationVersion == _animationVersion)
            {
                ToastPopup.IsOpen = false;
            }
        };
        ToastSurface.BeginAnimation(OpacityProperty, fadeOut);
    }
}
