using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
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
                try {
                    chrome.webview.postMessage(readScroll());
                } catch {
                }
            };

            const queue = () => {
                if (scheduled) {
                    return;
                }

                scheduled = true;
                requestAnimationFrame(send);
            };

            window.addEventListener('scroll', queue, { passive: true });
            window.addEventListener('pagehide', send);
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
        if (hr != 0) Debug.WriteLine($"DwmSetWindowAttribute failed: 0x{hr:X8}");
        TabStrip.ItemsSource = _tabs;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        _sleepTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _sleepTimer.Tick += SleepTimer_Tick;
        _sleepTimer.Start();

        _startBgTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _startBgTimer.Tick += StartBgTimer_Tick;
        StartBgCanvas.SizeChanged += StartBg_SizeChanged;

        ApplySettings();
        ApplyWindowMode();
        StateChanged += MainWindow_StateChanged;

        Loaded += OnWindowLoaded;
        ContentRendered += OnWindowContentRendered;
        Closing += MainWindow_Closing;
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
            Debug.WriteLine($"Startup failed: {ex}");
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
                    Debug.WriteLine("Reader-content response from wrong tab, discarding");
                    return;
                }
                if (root.TryGetProperty("error", out var errProp))
                {
                    Debug.WriteLine($"Reader extraction failed: {errProp.GetString()}");
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
                                Debug.WriteLine($"RenderReaderViewAsync failed: {t.Exception}");
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
            Debug.WriteLine($"WebMessageReceived failed: {ex}");
        }
    }

    private void StartBgTimer_Tick(object? sender, EventArgs e) => AnimateBgParticles();

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
        QuickLinksPanel.Visibility = _dataStore.Settings.ShowQuickLinks ? Visibility.Visible : Visibility.Collapsed;
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
        _tabOperationLock.Wait();
        BrowserTab[] snapshot;
        try { snapshot = [.. _tabs]; }
        finally { _tabOperationLock.Release(); }
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
            Debug.WriteLine(ex);
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
        BrowserTab[] snapshot;
        _tabOperationLock.Wait();
        try { snapshot = [.. _tabs]; }
        finally { _tabOperationLock.Release(); }
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
            StartBgCanvas.Visibility = Visibility.Visible;
            StartOverlay.ClearValue(BackgroundProperty);
            _startBgTimer?.Start();
            return;
        }

        _startBgTimer?.Stop();
        StartBgCanvas.Visibility = Visibility.Collapsed;

        if (File.Exists(bg))
        {
            StartOverlay.Background = new ImageBrush(new BitmapImage(new Uri(bg)))
            {
                Stretch = Stretch.UniformToFill,
                Opacity = 0.4
            };
            return;
        }

        StartOverlay.ClearValue(BackgroundProperty);
    }

    private void SplitTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item && item.Tag is BrowserTab tab && !IsSplitActive && _activeTab is not null)
        {
            _splitPrimaryTab = _activeTab;
            _splitSecondaryTab = tab;
            _ = AttachSplitViewAsync(_activeTab);
        }
    }

    private async void PopOutTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item && item.Tag is BrowserTab tab && tab.WebView?.CoreWebView2 is not null)
        {
            var url = tab.RestoredUrl ?? tab.Address;
            await CloseTabAsync(tab);
            var exePath = Environment.ProcessPath ?? "TabloidBrowser.exe";
            var args = url is not null ? $"\"{url}\"" : "";
            try { Process.Start(new ProcessStartInfo(exePath, args) { UseShellExecute = true }); }
            catch { }
        }
    }

    private async void FocusSplitTab_Click(object sender, RoutedEventArgs e)
    {
        if (_splitSecondaryTab is not null)
            await ActivateTabAsync(_splitSecondaryTab);
    }

    private void CloseSplit_Click(object sender, RoutedEventArgs e)
    {
        ClearSplitView();
    }
}
