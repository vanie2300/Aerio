using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Wpf;

namespace TabloidBrowser;

public record ReaderArticle(
    string Title,
    string? Byline,
    string? SiteName,
    string? PublishedTime,
    string Content
);

public class BrowserTab : INotifyPropertyChanged
{
    private Color ActiveTabColor = Color.FromRgb(24, 24, 24);
    private Color InactiveTabColor = Color.FromRgb(17, 17, 17);
    private Color AudioTabColor = Color.FromRgb(40, 28, 40);
    private Color ActiveAccentColor = Color.FromRgb(114, 77, 223);
    private Color InactiveAccentColor = Color.FromRgb(58, 58, 58);
    private Color SleepingAccentColor = Color.FromRgb(94, 94, 94);
    private Color AudioAccentColor = Color.FromRgb(0, 200, 83);
    private Color AiAccentColor = Color.FromRgb(0, 180, 220);

    private static readonly Brush AudioMuteBrush = FreezeBrush(new SolidColorBrush(Color.FromRgb(0, 200, 83)));
    private static readonly Brush InactiveMuteBrush = FreezeBrush(new SolidColorBrush(Color.FromRgb(128, 128, 128)));
    private static readonly Brush TransparentBrush = FreezeBrush(Brushes.Transparent);

    private static Brush FreezeBrush(Brush brush) { brush.Freeze(); return brush; }

    private string _title = "New tab";
    private string _address = string.Empty;
    private bool _isActive;
    private bool _isSleeping;
    private bool _isPlayingAudio;
    private bool _isMuted;
    private bool _hasAudio;
    private Color? _siteAccentColor;
    private ImageSource? _favicon;
    private string? _faviconUri;
    private ImageSource? _mediaArtwork;
    private TabOwner _owner = TabOwner.User;

    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public TabOwner Owner
    {
        get => _owner;
        set
        {
            if (_owner == value) return;
            _owner = value;
            _cachedAccentBrush = null;
            DispatchOnPropertyChanged();
            DispatchOnPropertyChanged(nameof(AccentBrush));
            DispatchOnPropertyChanged(nameof(IsAiOwned));
        }
    }

    public string? AiTaskId { get; set; }

    public bool IsAiOwned => Owner == TabOwner.AI;

    public AiTaskState AiState { get; set; } = AiTaskState.Idle;

    public long PageVersion { get; set; } = 1;

    public BrowserTabKind Kind { get; set; } = BrowserTabKind.Start;

    public WebView2? WebView { get; set; }

    public List<Action> CleanupActions { get; } = [];

    public List<ChatMessage>? AiChatHistory { get; set; }

    public void RunCleanup()
    {
        foreach (var action in CleanupActions.ToArray())
        {
            try { action(); }
            catch (Exception ex) { Log.Error(ex, "Cleanup action failed"); }
        }
        CleanupActions.Clear();
    }

    public DateTimeOffset LastActiveUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Throttles accent re-resolution triggered by favicon changes so animated
    /// favicons cannot cause repeated work.
    /// </summary>
    public DateTimeOffset LastFaviconAccentAttemptUtc { get; set; }

    public bool IsLoading { get; set; }

    public double ZoomLevel { get; set; } = 1.0;

    public string? RestoredUrl { get; set; }

    public bool IsRestoredPlaceholder { get; set; }

    public bool IsWebViewInitialized { get; set; }

    private bool _isReaderMode;

    public bool IsReaderMode
    {
        get => _isReaderMode;
        set
        {
            if (_isReaderMode == value) return;
            _isReaderMode = value;
            if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
                dispatcher.Invoke(() => OnPropertyChanged());
            else
                OnPropertyChanged();
        }
    }

    public ReaderArticle? ReaderContent { get; set; }

    public double ScrollX { get; set; }

    public double ScrollY { get; set; }

    public bool PendingScrollRestore { get; set; }

    public bool AllowNextNavigationScrollRestore { get; set; }

    public bool IsPlayingAudio
    {
        get => _isPlayingAudio;
        set
        {
            if (_isPlayingAudio == value) return;
            _isPlayingAudio = value;
            if (value) _hasAudio = true;
            _cachedTabBrush = null;
            _cachedAccentBrush = null;
            DispatchOnPropertyChanged();
            DispatchOnPropertyChanged(nameof(AccentBrush));
            DispatchOnPropertyChanged(nameof(Title));
            DispatchOnPropertyChanged(nameof(MuteIcon));
            DispatchOnPropertyChanged(nameof(MuteForeground));
            DispatchOnPropertyChanged(nameof(MuteToolTip));
            DispatchOnPropertyChanged(nameof(ShowMuteIcon));
            DispatchOnPropertyChanged(nameof(TabBrush));
        }
    }

    /// <summary>
    /// Primary color of the site currently playing media. When set, it replaces
    /// the default green audio accent on the tab. Cleared on navigation.
    /// </summary>
    public Color? SiteAccentColor
    {
        get => _siteAccentColor;
        set
        {
            if (_siteAccentColor == value) return;
            _siteAccentColor = value;
            _cachedAccentBrush = null;

            // The playing-tab background is tinted with this accent, so the tab
            // brush must be rebuilt too.
            _cachedTabBrush = null;
            DispatchOnPropertyChanged();
            DispatchOnPropertyChanged(nameof(AccentBrush));
            DispatchOnPropertyChanged(nameof(TabBrush));
        }
    }

    /// <summary>
    /// The site's favicon, shown in the tab strip. Null means the tab should
    /// fall back to the accent dot.
    /// </summary>
    public ImageSource? Favicon
    {
        get => _favicon;
        set
        {
            if (ReferenceEquals(_favicon, value)) return;
            _favicon = value;
            DispatchOnPropertyChanged();
            DispatchOnPropertyChanged(nameof(HasFavicon));
        }
    }

    public bool HasFavicon => _favicon is not null;

    /// <summary>
    /// Album art / video poster advertised by the playing tab's Media Session,
    /// used as the media chip's background.
    /// </summary>
    public ImageSource? MediaArtwork
    {
        get => _mediaArtwork;
        set
        {
            if (ReferenceEquals(_mediaArtwork, value)) return;
            _mediaArtwork = value;
            DispatchOnPropertyChanged();
        }
    }

    /// <summary>URL the current <see cref="MediaArtwork"/> came from (change detection).</summary>
    public string? MediaArtworkUrl { get; set; }

    /// <summary>
    /// URI of the currently displayed favicon, used to avoid reloading the
    /// same image when WebView2 raises repeated FaviconChanged events.
    /// </summary>
    public string? FaviconUri
    {
        get => _faviconUri;
        set => _faviconUri = value;
    }

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (_isMuted == value) return;
            _isMuted = value;
            DispatchOnPropertyChanged();
            DispatchOnPropertyChanged(nameof(MuteIcon));
            DispatchOnPropertyChanged(nameof(Title));
            DispatchOnPropertyChanged(nameof(MuteForeground));
            DispatchOnPropertyChanged(nameof(MuteToolTip));
            DispatchOnPropertyChanged(nameof(ShowMuteIcon));
        }
    }

    public bool HasAudio
    {
        get => _hasAudio;
        set
        {
            if (_hasAudio == value) return;
            _hasAudio = value;
            DispatchOnPropertyChanged();
            DispatchOnPropertyChanged(nameof(ShowMuteIcon));
            DispatchOnPropertyChanged(nameof(MuteIcon));
            DispatchOnPropertyChanged(nameof(MuteForeground));
            DispatchOnPropertyChanged(nameof(MuteToolTip));
            DispatchOnPropertyChanged(nameof(Title));
        }
    }

    public bool ShowMuteIcon => _isPlayingAudio || _hasAudio;

    public string MuteIcon => ShowMuteIcon ? (_isMuted ? "\U0001F507" : "\U0001F50A") : "";

    public Brush MuteForeground
    {
        get
        {
            if (!ShowMuteIcon) return TransparentBrush;
            return _isMuted ? InactiveMuteBrush : AudioMuteBrush;
        }
    }

    public string MuteToolTip => !ShowMuteIcon ? "" : _isMuted ? "Unmute tab" : "Mute tab";

    public double NativeScrollY { get; set; }

    private UIElement? _cachedContent;
    public UIElement? CachedContent
    {
        get => _cachedContent;
        set
        {
            if (_cachedContent is UIElement oldElement && oldElement != value && VisualTreeHelper.GetParent(oldElement) is Panel parent)
                parent.Children.Remove(oldElement);
            _cachedContent = value;
        }
    }

    public string Title
    {
        get
        {
            if (_isSleeping) return $"{_title} (sleep)";
            return _title;
        }
        set
        {
            var newTitle = string.IsNullOrWhiteSpace(value) ? "New tab" : value;
            if (string.Equals(_title, newTitle, StringComparison.Ordinal)) return;
            _title = newTitle;
            DispatchOnPropertyChanged();
        }
    }

    public string Address
    {
        get => _address;
        set
        {
            if (string.Equals(_address, value, StringComparison.Ordinal)) return;
            _address = value;
            SiteAccentColor = null;
            Favicon = null;
            FaviconUri = null;
            DispatchOnPropertyChanged();
        }
    }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            _cachedTabBrush = null;
            _cachedAccentBrush = null;
            DispatchOnPropertyChanged();
            DispatchOnPropertyChanged(nameof(TabBrush));
            DispatchOnPropertyChanged(nameof(AccentBrush));
            DispatchOnPropertyChanged(nameof(TabBorderThickness));
        }
    }

    public bool IsSleeping
    {
        get => _isSleeping;
        set
        {
            if (_isSleeping == value) return;
            _isSleeping = value;
            _cachedAccentBrush = null;
            DispatchOnPropertyChanged();
            DispatchOnPropertyChanged(nameof(Title));
            DispatchOnPropertyChanged(nameof(AccentBrush));
        }
    }

    public bool SuspendedForSleep { get; set; }

    public DateTimeOffset? SuspendedAtUtc { get; set; }

    private Brush? _cachedTabBrush;

    public Brush TabBrush
    {
        get
        {
            if (_cachedTabBrush is not null) return _cachedTabBrush;
            var brush = _isActive ? new SolidColorBrush(ActiveTabColor)
                : IsPlayingAudio ? new SolidColorBrush(ResolvePlayingBackground())
                : new SolidColorBrush(InactiveTabColor);
            brush.Freeze();
            _cachedTabBrush = brush;
            return brush;
        }
    }

    public Thickness TabBorderThickness => _isActive ? new Thickness(0, 0, 0, 2) : new Thickness(0);

    private Brush? _cachedAccentBrush;

    public Brush AccentBrush
    {
        get
        {
            if (_cachedAccentBrush is not null) return _cachedAccentBrush;
            var brush = Owner == TabOwner.AI
                ? new SolidColorBrush(AiAccentColor)
                : IsPlayingAudio
                    ? new SolidColorBrush(ResolvePlayingAccent())
                    : IsSleeping
                        ? new SolidColorBrush(SleepingAccentColor)
                        : _isActive
                            ? new SolidColorBrush(ActiveAccentColor)
                            : new SolidColorBrush(InactiveAccentColor);
            brush.Freeze();
            _cachedAccentBrush = brush;
            return brush;
        }
    }

    /// <summary>How much of the site accent bleeds into a playing tab's background.</summary>
    private const double PlayingBackgroundTint = 0.15;

    /// <summary>Appearance setting (from the theme): tint playing tabs by site accent.</summary>
    private bool _tintPlayingTabs = true;

    /// <summary>
    /// Background for a playing (non-active) tab: the neutral audio tint blended
    /// with the site's accent so the tab carries the site's hue, subtly.
    /// </summary>
    private Color ResolvePlayingBackground()
    {
        if (!_tintPlayingTabs)
        {
            return AudioTabColor;
        }

        var accent = SiteAccentColor ?? AudioAccentColor;
        return AccentColorAdjuster.Blend(AudioTabColor, accent, PlayingBackgroundTint);
    }

    private Color ResolvePlayingAccent()
    {
        var background = _isActive ? ActiveTabColor : ResolvePlayingBackground();
        var accent = SiteAccentColor ?? AudioAccentColor;
        return AccentColorAdjuster.AdjustForContrast(accent, background);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetVisualColors(Theme theme)
    {
        ActiveTabColor = theme.TabActive;
        InactiveTabColor = theme.TabInactive;
        AudioTabColor = theme.TabAudio;
        ActiveAccentColor = theme.TabActiveAccent;
        InactiveAccentColor = theme.TabInactiveAccent;
        SleepingAccentColor = theme.TabSleepingAccent;
        AudioAccentColor = theme.TabAudioAccent;
        _tintPlayingTabs = theme.TintPlayingTabs;
    }

    public void RefreshVisuals()
    {
        _cachedTabBrush = null;
        _cachedAccentBrush = null;
        OnPropertyChanged(nameof(TabBrush));
        OnPropertyChanged(nameof(AccentBrush));
        OnPropertyChanged(nameof(TabBorderThickness));
    }

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void DispatchOnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(() => OnPropertyChanged(propertyName));
        else
            OnPropertyChanged(propertyName);
    }
}
