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

    public bool IsSplitPaneTab { get; set; }

    private bool IsActiveOrSplit => _isActive || IsSplitPaneTab;

    private Brush? _cachedTabBrush;

    public Brush TabBrush
    {
        get
        {
            if (_cachedTabBrush is not null) return _cachedTabBrush;
            var brush = IsActiveOrSplit ? new SolidColorBrush(ActiveTabColor)
                : IsPlayingAudio ? new SolidColorBrush(AudioTabColor)
                : new SolidColorBrush(InactiveTabColor);
            brush.Freeze();
            _cachedTabBrush = brush;
            return brush;
        }
    }

    public Thickness TabBorderThickness => IsActiveOrSplit ? new Thickness(1.5) : new Thickness(1);

    private Brush? _cachedAccentBrush;

    public Brush AccentBrush
    {
        get
        {
            if (_cachedAccentBrush is not null) return _cachedAccentBrush;
            var brush = IsPlayingAudio
                ? new SolidColorBrush(AudioAccentColor)
                : IsSleeping
                    ? new SolidColorBrush(SleepingAccentColor)
                    : IsActiveOrSplit
                        ? new SolidColorBrush(ActiveAccentColor)
                        : new SolidColorBrush(InactiveAccentColor);
            brush.Freeze();
            _cachedAccentBrush = brush;
            return brush;
        }
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
