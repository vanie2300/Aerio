using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TabloidBrowser;

/// <summary>
/// Media control chip: shows what is playing (cover art background + title)
/// and, on hover, transitions to the transport controls (pause,
/// Picture-in-Picture, mute). Hidden when nothing is playing.
/// </summary>
public partial class MediaControlChip : UserControl
{
    public MediaControlChip()
    {
        InitializeComponent();
        Visibility = Visibility.Collapsed;

        // Idle state: controls hidden and non-interactive.
        ControlsLayer.IsHitTestVisible = false;

        MouseEnter += (_, _) => Transition(showControls: true);
        MouseLeave += (_, _) => Transition(showControls: false);
    }

    public event Action? ActivateRequested;
    public event Action? TogglePlayPauseRequested;
    public event Action? SkipPreviousRequested;
    public event Action? SkipNextRequested;
    public event Action? ToggleMuteRequested;
    public event Action? PictureInPictureRequested;

    /// <summary>Tab currently bound to the chip (null when hidden).</summary>
    public BrowserTab? Tab => DataContext as BrowserTab;

    public void Update(BrowserTab? tab)
    {
        DataContext = tab;
        Visibility = tab is null ? Visibility.Collapsed : Visibility.Visible;
        SetArtwork(tab?.MediaArtwork);
    }

    /// <summary>Sets the cover art shown as the chip background.</summary>
    public void SetArtwork(ImageSource? artwork)
    {
        ArtworkImage.Source = artwork;
        ArtworkImage.Visibility = artwork is null ? Visibility.Collapsed : Visibility.Visible;
        ArtworkScrim.Visibility = artwork is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Cross-fades between the info and control layers. The chip keeps a fixed
    /// width, so only opacity changes. Driven from code so the animation
    /// targets resolve reliably.
    /// </summary>
    private void Transition(bool showControls)
    {
        UIElement fadeOut = showControls ? InfoLayer : ControlsLayer;
        UIElement fadeIn = showControls ? ControlsLayer : InfoLayer;

        fadeOut.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(110)));

        fadeIn.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(180))
            {
                BeginTime = TimeSpan.FromMilliseconds(30)
            });

        ControlsLayer.IsHitTestVisible = showControls;
        InfoLayer.IsHitTestVisible = !showControls;
    }

    private void Activate_Click(object sender, RoutedEventArgs e) => ActivateRequested?.Invoke();

    private void TogglePlayPause_Click(object sender, RoutedEventArgs e) => TogglePlayPauseRequested?.Invoke();

    private void SkipPrevious_Click(object sender, RoutedEventArgs e) => SkipPreviousRequested?.Invoke();

    private void SkipNext_Click(object sender, RoutedEventArgs e) => SkipNextRequested?.Invoke();

    private void ToggleMute_Click(object sender, RoutedEventArgs e) => ToggleMuteRequested?.Invoke();

    private void PictureInPicture_Click(object sender, RoutedEventArgs e) => PictureInPictureRequested?.Invoke();
}
