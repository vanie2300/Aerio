using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TabloidBrowser;

public partial class BrowserPaneControl : UserControl
{
    public BrowserPaneControl() => InitializeComponent();

    public event MouseButtonEventHandler? StartSearchSurfaceMouseDown;
    public event KeyEventHandler? StartSearchKeyDown;
    public event RoutedEventHandler? StartSearchGo;
    public event RoutedEventHandler? QuickDuckDuckGo;
    public event RoutedEventHandler? QuickStartpage;
    public event RoutedEventHandler? QuickGitHub;
    public event RoutedEventHandler? QuickHackerNews;
    public event RoutedEventHandler? FocusSplitTab;
    public event RoutedEventHandler? CloseSplit;

    private void StartSearchSurface_PreviewMouseDown(object s, MouseButtonEventArgs e) => StartSearchSurfaceMouseDown?.Invoke(s, e);
    private void StartSearchBox_PreviewKeyDown(object s, KeyEventArgs e) => StartSearchKeyDown?.Invoke(s, e);
    private void StartSearchGo_Click(object s, RoutedEventArgs e) => StartSearchGo?.Invoke(s, e);
    private void QuickDuckDuckGo_Click(object s, RoutedEventArgs e) => QuickDuckDuckGo?.Invoke(s, e);
    private void QuickStartpage_Click(object s, RoutedEventArgs e) => QuickStartpage?.Invoke(s, e);
    private void QuickGitHub_Click(object s, RoutedEventArgs e) => QuickGitHub?.Invoke(s, e);
    private void QuickHackerNews_Click(object s, RoutedEventArgs e) => QuickHackerNews?.Invoke(s, e);
    private void FocusSplitTab_Click(object s, RoutedEventArgs e) => FocusSplitTab?.Invoke(s, e);
    private void CloseSplit_Click(object s, RoutedEventArgs e) => CloseSplit?.Invoke(s, e);

    /// <summary>
    /// Cross-fade for WebView/tab swaps. WebView2 is an HwndHost, so its WPF
    /// opacity can't animate — a full-size scrim fades away instead, masking
    /// the hard content cut.
    /// </summary>
    public void PlaySwapScrim()
    {
        SwapScrim.BeginAnimation(OpacityProperty, null);
        SwapScrim.Opacity = 1;
        SwapScrim.BeginAnimation(OpacityProperty, Motion.Fade(1, 0, Motion.Base));
    }

    /// <summary>Replays the start-page hero entrance (returning Home).</summary>
    public void PlayStartIntro()
    {
        var transform = StartHero.RenderTransform as TranslateTransform;
        if (transform is null)
        {
            transform = new TranslateTransform(20, 0);
            StartHero.RenderTransform = transform;
        }

        StartHero.BeginAnimation(OpacityProperty, null);
        StartHero.Opacity = 0;
        transform.BeginAnimation(TranslateTransform.YProperty, null);
        transform.Y = 20;

        StartHero.BeginAnimation(OpacityProperty, Motion.Fade(0, 1, Motion.Hero));
        var rise = new DoubleAnimation(20, 0, Motion.Hero) { EasingFunction = Motion.OutCubic };
        transform.BeginAnimation(TranslateTransform.YProperty, rise);
    }

    /// <summary>Accent focus ring on the start-page search field.</summary>
    public void FocusStartSearch()
    {
        StartSearchBox.Focus();
    }

    private void StartSearchBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        SearchBorder.BorderBrush = (Brush)FindResource("Accent");
    }

    private void StartSearchBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        SearchBorder.BorderBrush = (Brush)FindResource("Stroke");
    }
}
