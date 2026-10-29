using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace TabloidBrowser;

public partial class MainWindow : Window
{
    private const string ScrollTrackingScript =
        """
        (() => {
            if (window.__tabloidScrollTracker) {
                return;
            }

            window.__tabloidScrollTracker = true;
            let scheduled = false;

            const readScroll = () => {
                const root = document.scrollingElement || document.documentElement || document.body;
                return {
                    type: 'tabloid-scroll',
                    x: Math.max(0, window.scrollX || root?.scrollLeft || 0),
                    y: Math.max(0, window.scrollY || root?.scrollTop || 0)
                };
            };

            const send = () => {
                scheduled = false;
                const now = performance.now();
                if (now - lastSent < 80) {
                    return;
                }
                lastSent = now;
                try {
                    chrome.webview.postMessage(readScroll());
                } catch {
                }
            };

            let lastSent = -1000;

            const queue = () => {
                if (scheduled) {
                    return;
                }

                scheduled = true;
                requestAnimationFrame(send);
            };

            window.addEventListener('scroll', queue, { passive: true });
            window.addEventListener('pagehide', () => {
                lastSent = -1000;
                try { chrome.webview.postMessage(readScroll()); } catch { }
            });
            setTimeout(send, 0);
        })();
        """;
    private const string ScrollSnapshotScript =
        """
        (() => {
            const root = document.scrollingElement || document.documentElement || document.body;
            return {
                X: Math.max(0, window.scrollX || root?.scrollLeft || 0),
                Y: Math.max(0, window.scrollY || root?.scrollTop || 0)
            };
        })();
        """;
    private const string ForceDarkScript =
        """
        (() => {
            if (document.getElementById('tbl-dark')) return;
            var s = document.createElement('style');
            s.id = 'tbl-dark';
            s.textContent = 'html{filter:invert(1)hue-rotate(180deg)}img,video,canvas,svg,iframe,[style*="background-image"]{filter:invert(1)hue-rotate(180deg)}';
            document.documentElement.appendChild(s);
        })();
        """;


    private const string ReaderDetectionScript =
        """
        (() => {
        function isProbablyReaderable(doc,options){
        if(typeof options=="function")options={visibilityChecker:options};
        var defaultOptions={minScore:20,minContentLength:140,visibilityChecker:function(e){return(!e.style||e.style.display!="none")&&!e.hasAttribute("hidden")&&(!e.hasAttribute("aria-hidden")||e.getAttribute("aria-hidden")!="true"||(e.className&&e.className.includes&&e.className.includes("fallback-image")))}};
        options=Object.assign(defaultOptions,options);
        var nodes=doc.querySelectorAll("p, pre, article");
        var brNodes=doc.querySelectorAll("div > br");
        if(brNodes.length){var set=new Set(nodes);[].forEach.call(brNodes,function(e){set.add(e.parentNode)});nodes=Array.from(set)}
        var score=0;
        return [].some.call(nodes,function(e){
        if(!options.visibilityChecker(e))return false;
        var matchString=e.className+" "+e.id;
        if(/ad|ai2html|banner|breadcrumbs|combx|comment|community|cover-wrap|disqus|extra|footer|gdpr|header|legends|menu|related|remark|replies|rss|shoutbox|sidebar|skyscraper|social|sponsor|supplemental|ad-break|agegate|pagination|pager|popup|yom-remote/i.test(matchString)&&!/and|article|body|column|content|main|shadow/i.test(matchString))return false;
        if(e.matches("li p"))return false;
        var len=e.textContent.trim().length;
        if(len<options.minContentLength)return false;
        score+=Math.sqrt(len-options.minContentLength);
        if(score>options.minScore)return true;
        return false})}
        var r=isProbablyReaderable(document);
        chrome.webview.postMessage({type:'reader-detection',readerable:r});
        })();
        """;
    
    private const string ReaderViewCleanupScript =
        """
        (() => { var el = document.getElementById('tbl-reader-view'); if (el) el.remove(); })();
        """;

    private readonly BrowserDataStore _dataStore = new();
    private readonly DispatcherTimer _sleepTimer;
    private readonly string? _privateUserDataFolder;
    private readonly bool _isPrivate;
    private readonly string? _startupUrl;
    private readonly BrowserTabKind? _startupNativeKind;
    private readonly bool _skipSessionPersistence;
    private int _blockedTrackerCount;
    private int _privacyIndicatorPending;

    public MainWindow()
        : this(isPrivate: false)
    {
    }

    public MainWindow(bool isPrivate)
        : this(isPrivate, startupUrl: null, startupNativeKind: null, skipSessionPersistence: false)
    {
    }

    private MainWindow(
        bool isPrivate,
        string? startupUrl,
        BrowserTabKind? startupNativeKind,
        bool skipSessionPersistence)
    {
        _isPrivate = isPrivate;
        _startupUrl = startupUrl;
        _startupNativeKind = startupNativeKind;
        _skipSessionPersistence = skipSessionPersistence;
        _privateUserDataFolder = _isPrivate
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TabloidBrowser",
                "PrivateProfiles",
                Guid.NewGuid().ToString("N"))
            : null;

        InitializeComponent();
        _windowHandle = new WindowInteropHelper(this).EnsureHandle();
        var hwnd = _windowHandle;
        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(WndProc);
        var snapLayoutEnabled = 1;
        var hr = DwmSetWindowAttribute(hwnd, DWMWA_SNAP_LAYOUT, ref snapLayoutEnabled, 4u);
        if (hr != 0) Log.Debug($"DwmSetWindowAttribute failed: 0x{hr:X8}");
        TitleBar.TabStrip.ItemsSource = _tabs;
        BrowserPane.SetNewTabShortcuts(_dataStore.Settings.NewTabShortcuts);
        TitleBar.NewTab += NewTab_Click;
        TitleBar.TabPopOut += PopOutTab_Click;
        TitleBar.MuteToggle += MuteButton_Click;
        TitleBar.TabClose += CloseTab_Click;
        TitleBar.KeepAiTab += KeepAiTab_Click;
        TitleBar.Minimize += Minimize_Click;
        TitleBar.MaximizeRestore += MaximizeRestore_Click;
        TitleBar.CloseWindow += CloseWindow_Click;
        TitleBar.TabMouseDown += Tab_MouseLeftButtonDown;
        TitleBar.TabStripMouseDown += TabStrip_PreviewMouseLeftButtonDown;
        TitleBar.TabStripMouseMove += TabStrip_PreviewMouseMove;
        TitleBar.TabStripMouseUp += TabStrip_PreviewMouseLeftButtonUp;
        TitleBar.TabStripLostCapture += TabStrip_LostMouseCapture;
        TitleBar.TitleBarMouseDown += TitleBar_PreviewMouseLeftButtonDown;
        TitleBar.TitleBarMouseMove += TitleBar_PreviewMouseMove;
        TitleBar.TitleBarMouseUp += TitleBar_PreviewMouseLeftButtonUp;
        FindBar.FindPrevBtn.Click += FindPrev_Click;
        FindBar.FindNextBtn.Click += FindNext_Click;
        FindBar.FindCloseBtn.Click += FindClose_Click;
        FindBar.FindTextBox.TextChanged += FindTextBox_TextChanged;
        FindBar.FindTextBox.PreviewKeyDown += FindTextBox_PreviewKeyDown;
        FindBar.FindDragGrip.PreviewMouseLeftButtonDown += FindDragGrip_MouseLeftButtonDown;
        FindBar.FindDragGrip.PreviewMouseMove += FindDragGrip_MouseMove;
        FindBar.FindDragGrip.PreviewMouseLeftButtonUp += FindDragGrip_MouseLeftButtonUp;
        NavToolbar.Back += Back_Click;
        NavToolbar.Forward += Forward_Click;
        NavToolbar.Reload += Reload_Click;
        NavToolbar.Home += Home_Click;
        NavToolbar.Privacy += Privacy_Click;
        NavToolbar.AddressKeyDown += AddressBox_PreviewKeyDown;
        NavToolbar.AddressTextChanged += AddressBox_TextChanged;
        NavToolbar.AddressSuggestionSelected += AddressSuggestionSelected;
        NavToolbar.Go += Go_Click;
        NavToolbar.Bookmark += Bookmark_Click;
        NavToolbar.ReaderMode += ReaderMode_Click;
        NavToolbar.Private += Private_Click;
        NavToolbar.SleepTabs += SleepTabs_Click;
        NavToolbar.AiToggle += AiToggle_Click;
        NavToolbar.Menu += Menu_Click;
        NavToolbar.NewTab += NewTab_Click;
        NavToolbar.FullScreen += FullScreen_Click;
        NavToolbar.ForceDarkChecked += ForceDarkMenu_Checked;
        NavToolbar.ForceDarkUnchecked += ForceDarkMenu_Unchecked;
        NavToolbar.FindInPage += FindInPage_Click;
        NavToolbar.Print += Print_Click;
        NavToolbar.ShowBookmarks += ShowBookmarks_Click;
        NavToolbar.ShowHistory += ShowHistory_Click;
        NavToolbar.ShowDownloads += ShowDownloads_Click;
        NavToolbar.ShowExtensions += ShowExtensions_Click;
        NavToolbar.ShowHymns += ShowHymns_Click;
        NavToolbar.ZoomIn += ZoomIn_Click;
        NavToolbar.ZoomOut += ZoomOut_Click;
        NavToolbar.ResetZoom += ResetZoom_Click;
        NavToolbar.ClearData += ClearData_Click;
        NavToolbar.SiteData += SiteData_Click;
        NavToolbar.UblockToggle += UblockToggle_Click;
        NavToolbar.ShowSettings += ShowSettings_Click;
        BrowserPane.StartSearchSurfaceMouseDown += StartSearchSurface_PreviewMouseDown;
        BrowserPane.StartSearchKeyDown += StartSearchBox_PreviewKeyDown;
        BrowserPane.StartSearchGo += StartSearchGo_Click;
        BrowserPane.NewTabDialogSubmit += NewTabDialogSubmit_Click;
        BrowserPane.NewTabShortcutSelected += NewTabShortcutSelected;
        BrowserPane.NewTabShortcutsChanged += SaveNewTabShortcuts;
        AiSidebar.AiClearChat += AiClearChat_Click;
        AiSidebar.AiSidebarClose += AiSidebarClose_Click;
        AiSidebar.AiSend += AiSend_Click;
        AiSidebar.AiInputKeyDown += AiInput_PreviewKeyDown;
        AiSidebar.WorkspaceKeepTab += WorkspaceKeepTab_Click;
        AiSidebar.WorkspaceCloseTab += WorkspaceCloseTab_Click;
        AiSidebar.WorkspaceKeepAll += WorkspaceKeepAll_Click;
        AiSidebar.WorkspaceCloseAll += WorkspaceCloseAll_Click;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        _chromeAutoHideTimer.Tick += ChromeAutoHideTimer_Tick;
        _chromeAutoHideTimer.Start();
        Closed += (_, _) => _chromeAutoHideTimer.Stop();
        _sleepTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _sleepTimer.Tick += SleepTimer_Tick;
        _sleepTimer.Start();

        _startBgTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _startBgTimer.Tick += StartBgTimer_Tick;
        _startBgTimer.Start();
        BrowserPane.StartBgCanvas.SizeChanged += StartBg_SizeChanged;

        ApplySettings();
        ApplyWindowMode();
        StateChanged += MainWindow_StateChanged;

        Loaded += OnWindowLoaded;
        ContentRendered += OnWindowContentRendered;
        Closing += MainWindow_Closing;
        SourceInitialized += OnWindowSourceInitialized;
    }

    private void OnWindowSourceInitialized(object? sender, EventArgs e)
    {
        // Win11 rounded window corners (no-op on Win10 / older DWM).
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
                int preference = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
            }
        }
        catch (Exception ex) { Log.Debug(ex, "Rounded corner preference not applied"); }
    }

    private async void OnWindowLoaded(object? sender, RoutedEventArgs e)
    {
        try
        {
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.3))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            RootGrid.BeginAnimation(OpacityProperty, fadeIn);

            NotifySettingsChanged();
            await InitializeEnvironmentAsync();

            var currentVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";
            var lastSeen = _dataStore.Settings.LastSeenVersion;
            if (!string.IsNullOrEmpty(currentVersion) && currentVersion != lastSeen)
            {
                ShowChangelogPopup(currentVersion);
                _dataStore.SaveSettings(_dataStore.Settings with { LastSeenVersion = currentVersion });
            }

            FireAndForget(AdBlockEngine.RefreshOnceAsync(), "AdBlockEngine.RefreshOnceAsync");
            FireAndForget(EnsureUblockReadyAsync(), "EnsureUblockReadyAsync");
            if (!string.IsNullOrWhiteSpace(_startupUrl))
            {
                await AddWebTabAsync(_startupUrl);
            }
            else if (_startupNativeKind is { } nativeKind)
            {
                await AddNativePageTabAsync(nativeKind);
            }
            else
            {
                await RestoreSessionOrOpenStartAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Startup failed");
            ShowStartupError(ex);
        }
    }

    private void OnWindowContentRendered(object? sender, EventArgs e)
    {
        FocusStartSearchIfActive();
    }

    private void WebMessageReceived(BrowserTab tab, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            using var document = JsonDocument.Parse(args.WebMessageAsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String)
            {
                return;
            }

            var msgType = type.GetString();
            if (string.Equals(msgType, "tabloid-scroll", StringComparison.Ordinal))
            {
                if (TryGetJsonDouble(root, "x", out var x) &&
                    TryGetJsonDouble(root, "y", out var y))
                {
                    var sx = Math.Max(0, x);
                    var sy = Math.Max(0, y);
                    if (!Dispatcher.HasShutdownStarted)
                        Dispatcher.BeginInvoke(() =>
                        {
                            tab.ScrollX = sx;
                            tab.ScrollY = sy;
                        });
                }
            }
            else if (string.Equals(msgType, "reader-detection", StringComparison.Ordinal))
            {
                var readerable = root.TryGetProperty("readerable", out var rProp) && rProp.ValueKind == JsonValueKind.True;
                if (!Dispatcher.HasShutdownStarted)
                    Dispatcher.BeginInvoke(() => SetReaderButtonVisibility(readerable));
            }
            else if (string.Equals(msgType, "reader-content", StringComparison.Ordinal))
            {
                var pending = Interlocked.Exchange(ref _pendingReaderTab, null);
                if (pending?.WebView?.CoreWebView2 is null) return;
                if (pending != tab)
                {
                    Log.Debug("Reader-content response from wrong tab, discarding");
                    return;
                }
                if (root.TryGetProperty("error", out var errProp))
                {
                    Log.Debug("Reader extraction failed: {Error}", errProp.GetString() ?? "unknown");
                    return;
                }
                var title = root.TryGetProperty("title", out var tProp) ? tProp.GetString() ?? "" : "";
                var byline = root.TryGetProperty("byline", out var bProp) ? bProp.GetString() : null;
                var siteName = root.TryGetProperty("siteName", out var sProp) ? sProp.GetString() : null;
                var publishedTime = root.TryGetProperty("publishedTime", out var pProp) ? pProp.GetString() : null;
                var content = root.TryGetProperty("content", out var cProp) ? cProp.GetString() ?? "" : "";
                var article = new ReaderArticle(title, byline, siteName, publishedTime, content);
                if (!Dispatcher.HasShutdownStarted)
                    Dispatcher.InvokeAsync(() =>
                    {
                        pending.ReaderContent = article;
                        pending.IsReaderMode = true;
                        _ = RenderReaderViewAsync(pending, article).ContinueWith(t =>
                        {
                            if (t.Exception is not null)
                                Log.Error(t.Exception, "RenderReaderViewAsync failed");
                        }, TaskContinuationOptions.OnlyOnFaulted);
                        UpdateReaderUi(true);
                    });
            }
            else if (string.Equals(msgType, "reader-action", StringComparison.Ordinal))
            {
                if (root.TryGetProperty("action", out var actProp) && actProp.GetString() == "exit")
                {
                    if (!Dispatcher.HasShutdownStarted)
                        Dispatcher.BeginInvoke(() =>
                        {
                            var tab = _activeTab;
                            if (tab is null) return;
                            var cwv = tab.WebView?.CoreWebView2;
                        if (cwv is not null)
                            FireAndForget(cwv.ExecuteScriptAsync(ReaderViewCleanupScript), "ExecuteScriptAsync.ReaderViewCleanupScript");
                        tab.IsReaderMode = false;
                        UpdateReaderUi(false);
                    });
                }
            }
            else if (string.Equals(msgType, "install-crx", StringComparison.Ordinal))
            {
                if (root.TryGetProperty("extensionId", out var extIdProp) &&
                    extIdProp.ValueKind == JsonValueKind.String)
                {
                    var extId = extIdProp.GetString();
                    if (!string.IsNullOrWhiteSpace(extId))
                    {
                        FireAndForget(DownloadAndInstallCrxAsync(extId), "DownloadAndInstallCrxAsync");
                    }
                }
            }
        }

        catch (Exception ex)
        {
            Log.Error(ex, "WebMessageReceived failed");
        }
    }

    private void StartBgTimer_Tick(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized || !IsVisible) return;
        if (_activeTab is null ||
            _activeTab.Kind != BrowserTabKind.Start ||
            BrowserPane.StartOverlay.Visibility != Visibility.Visible)
        {
            _startBgTimer?.Stop();
            return;
        }
        AnimateBgParticles();
    }

    private Color GetAccentColor()
    {
        return _dataStore.Settings.Accent switch
        {
            "Blue" => Color.FromRgb(0, 122, 204),
            "Purple" => Color.FromRgb(114, 77, 223),
            "Gray" => Color.FromRgb(128, 128, 128),
            "Green" => Color.FromRgb(106, 153, 85),
            "White" => Color.FromRgb(255, 255, 255),
            _ => Color.FromRgb(114, 77, 223)
        };
    }

    private void ApplySettings()
    {
        ApplyBrowserChromeVisibilitySetting();
        ApplyTheme();
        ApplyBackground();
        UpdatePrivacyIndicator();
        UpdateBookmarkButton();
        ApplyForceDarkToExistingTabs();
        if (IsLoaded) NotifySettingsChanged();
        PopulateBookmarkBar();
    }



    private void ApplyForceDarkToExistingTabs()
    {
        var enable = _dataStore.Settings.ForceDarkPage;
        BrowserTab[] snapshot = _tabs.ToArray();
        foreach (var tab in snapshot)
        {
            if (tab.WebView?.CoreWebView2 is not null && tab.Kind == BrowserTabKind.Web)
            {
                FireAndForget(ApplyForceDarkTabAsync(tab.WebView.CoreWebView2, enable), "ApplyForceDarkTabAsync");
            }
        }
    }

    private static async Task ApplyForceDarkTabAsync(CoreWebView2 coreWebView, bool enable)
    {
        try
        {
            if (enable)
            {
                await coreWebView.ExecuteScriptAsync(ForceDarkScript);
            }
            else
            {
                await coreWebView.ExecuteScriptAsync(
                    "var e=document.getElementById('tbl-dark');if(e)e.remove()");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error occurred");
        }
    }

    private void ApplyTheme()
    {
        var baseTheme = string.Equals(_dataStore.Settings.Theme, "Light", StringComparison.OrdinalIgnoreCase)
            ? Theme.Light
            : Theme.Dark;

        var accentColor = GetAccentColor();
        var theme = baseTheme with { Accent = accentColor, TabActiveAccent = accentColor };
        theme.ApplyTo(Resources);
        Background = GetBrush("AppBg");

        var webViewBackground = GetWebViewBackgroundColor();
        BrowserTab[] snapshot = _tabs.ToArray();
        foreach (var tab in snapshot)
        {
            tab.SetVisualColors(theme);
            if (tab.WebView is not null)
            {
                tab.WebView.DefaultBackgroundColor = webViewBackground;
            }

            tab.RefreshVisuals();
        }
    }

    private void ApplyBackground()
    {
        var bg = _dataStore.Settings.Background;

        if (string.Equals(bg, "Default", StringComparison.OrdinalIgnoreCase))
        {
            BrowserPane.StartBgCanvas.Visibility = Visibility.Visible;
            BrowserPane.StartOverlay.ClearValue(BackgroundProperty);
            _startBgTimer?.Start();
            return;
        }

        _startBgTimer?.Stop();
        BrowserPane.StartBgCanvas.Visibility = Visibility.Collapsed;

        if (File.Exists(bg))
        {
            BrowserPane.StartOverlay.Background = new ImageBrush(new BitmapImage(new Uri(bg)))
            {
                Stretch = Stretch.UniformToFill,
                Opacity = _dataStore.Settings.BackgroundOpacity
            };
            return;
        }

        BrowserPane.StartOverlay.ClearValue(BackgroundProperty);
    }

    private async void PopOutTab_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is MenuItem item && item.Tag is BrowserTab tab && tab.WebView?.CoreWebView2 is not null)
            {
                var url = tab.RestoredUrl ?? tab.Address;
                await CloseTabAsync(tab);
                var window = new MainWindow(isPrivate: _isPrivate, startupUrl: url, startupNativeKind: null, skipSessionPersistence: true);
                window.Show();
            }
        }
        catch (Exception ex) { Log.Error(ex, "Error occurred"); }
    }

    private static readonly List<(string Version, string[] Features)> Changelog =
    [
        ("1.4.0",
        [
            "AI streaming responses — tokens appear in real-time",
            "Structured logging with daily file rotation",
            "AI prompt injection protection",
            "AI management settings page",
            "Unit test suite (54 tests)",
            "CI/CD pipeline"
        ]),
        ("1.3.0",
        [
            "uBlock Origin integration with EasyList",
            "Custom search engine support",
            "System media detection and playback controls",
            "Tab hibernation with hover preview"
        ]),
        ("1.2.0",
        [
            "Settings JSON persistence",
            "Session restore on startup",
            "Bookmark drag-and-drop reorder",
            "UI refinements and bug fixes"
        ]),
        ("1.1.0",
        [
            "AI assistant sidebar (Ctrl+Shift+A)",
            "OpenAI-compatible API support (Ollama, OpenAI, Groq, OpenRouter)",
            "Agent tool calling with 8 built-in tools",
            "Web search integration",
            "Page context for AI",
            "Markdown rendering in chat",
            "Per-tab chat history",
            "DPAPI API key encryption",
            "Start page particle animation"
        ]),
        ("1.0.0",
        [
            "Initial release"
        ])
    ];

    private void ShowChangelogPopup(string currentVersion)
    {
        if (_isChangelogOpen) return;
        RemovePopupBlur();

        _isChangelogOpen = true;

        if (Content is Grid rootGrid)
        {
            UiFx.PlayBackdropBlur(rootGrid);

            _popupOverlay = new Grid
            {
                Background = new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)),
                IsHitTestVisible = false,
                Opacity = 0
            };
            Grid.SetRowSpan(_popupOverlay, rootGrid.RowDefinitions.Count);
            rootGrid.Children.Add(_popupOverlay);

            var overlayFade = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.15));
            _popupOverlay.BeginAnimation(Grid.OpacityProperty, overlayFade);
        }

        var popup = new Popup
        {
            PlacementTarget = this,
            Placement = PlacementMode.Center,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade
        };

        popup.Closed += (_, _) =>
        {
            RemovePopupBlur();
            _changelogPopup = null;
            _isChangelogOpen = false;
        };

        _changelogPopup = popup;

        var outerBorder = new Border
        {
            Width = 500,
            MaxHeight = 520,
            Background = GetBrush("ChromeBg"),
            BorderBrush = GetBrush("Stroke"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(24, 20, 24, 20),
            Effect = new DropShadowEffect
            {
                BlurRadius = 24,
                Opacity = 0.3
            }
        };

        var mainStack = new StackPanel();

        var header = new TextBlock
        {
            Text = "What's New in Aerio",
            Foreground = GetBrush("Text"),
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        };
        mainStack.Children.Add(header);

        var sub = new TextBlock
        {
            Text = $"You're now on version {currentVersion}",
            Foreground = GetBrush("Muted"),
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 16)
        };
        mainStack.Children.Add(sub);

        var scrollContent = new StackPanel();

        var isFirst = string.IsNullOrEmpty(/* lastSeen was empty on first launch */
            _dataStore.Settings.LastSeenVersion);

        foreach (var entry in Changelog)
        {
            if (!isFirst && string.Compare(entry.Version, _dataStore.Settings.LastSeenVersion, StringComparison.Ordinal) <= 0)
                break;

            var versionHeader = new TextBlock
            {
                Text = $"v{entry.Version}",
                Foreground = GetBrush("Cyan"),
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6)
            };
            scrollContent.Children.Add(versionHeader);

            foreach (var feature in entry.Features)
            {
                var featurePanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(8, 0, 0, 3)
                };
                var bullet = new TextBlock
                {
                    Text = "\u2022",
                    Foreground = GetBrush("Muted"),
                    FontSize = 13,
                    Margin = new Thickness(0, 0, 6, 0)
                };
                featurePanel.Children.Add(bullet);
                var featureText = new TextBlock
                {
                    Text = feature,
                    Foreground = GetBrush("Text"),
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap
                };
                featurePanel.Children.Add(featureText);
                scrollContent.Children.Add(featurePanel);
            }

            var spacer = new System.Windows.Shapes.Rectangle
            {
                Height = 12,
                Fill = Brushes.Transparent
            };
            scrollContent.Children.Add(spacer);
        }

        var scrollViewer = new ScrollViewer
        {
            Content = scrollContent,
            MaxHeight = 360,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        mainStack.Children.Add(scrollViewer);

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };

        var gotItBtn = new Button
        {
            Content = "Got it",
            MinWidth = 92,
            Height = 30,
            Padding = new Thickness(12, 0, 12, 0),
            Style = FindResource("MicroButton") as Style,
            IsDefault = true
        };
        gotItBtn.Click += (_, _) => popup.IsOpen = false;
        buttonRow.Children.Add(gotItBtn);

        mainStack.Children.Add(buttonRow);
        outerBorder.Child = mainStack;
        popup.Child = outerBorder;
        UiFx.PlayPopupEntrance(outerBorder);
        popup.IsOpen = true;
    }
}
