using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace TabloidBrowser;

public partial class MainWindow
{
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint dwAttribute, ref int pvAttribute, uint cbAttribute);

    [DllImport("shell32.dll")]
    private static extern IntPtr SHAppBarMessage(int dwMessage, ref APPBARDATA pData);

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uCallbackMessage;
        public int uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    private const int ABM_GETAUTOHIDEBAREX = 11;
    private const int ABE_LEFT = 0;
    private const int ABE_TOP = 1;
    private const int ABE_RIGHT = 2;
    private const int ABE_BOTTOM = 3;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public Win32Point ptReserved;
        public Win32Point ptMaxSize;
        public Win32Point ptMaxPosition;
        public Win32Point ptMinTrackSize;
        public Win32Point ptMaxTrackSize;
    }

    private const uint WM_SYSCOMMAND = 0x0112;
    private const uint SC_MINIMIZE = 0xF020;
    private const uint SC_MAXIMIZE = 0xF030;
    private const uint SC_RESTORE = 0xF120;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint DWMWA_SNAP_LAYOUT = 0x100;
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_GETMINMAXINFO = 0x0024;
    private const uint WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;
    private const int HTTOP = 12;
    private const int HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16;
    private const int HTBOTTOMRIGHT = 17;
    private const uint WM_DPICHANGED = 0x02E0;

    private readonly HttpClient _crxHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30),

        // CRX payloads are bounded; refuse to buffer absurd responses.
        MaxResponseContentBufferSize = 128 * 1024 * 1024
    };

    private CoreWebView2Environment? _environment;
    private volatile CoreWebView2Profile? _profile;
    private bool _extensionsLoaded;
    private readonly Dictionary<string, CoreWebView2BrowserExtension> _loadedExtensions = [];
    private readonly Dictionary<string, CoreWebView2DownloadOperation> _downloadOperations = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<CoreWebView2, BrowserTab> _webViewToTabMap = [];
    private Microsoft.Web.WebView2.Wpf.WebView2? _carouselWebView;
    private readonly HashSet<BrowserTab> _initializingTabs = [];
    private readonly object _initializationLock = new();

    private async Task InitializeEnvironmentAsync()
    {
        var availableVersion = CoreWebView2Environment.GetAvailableBrowserVersionString();
        if (string.IsNullOrEmpty(availableVersion))
        {
            throw new InvalidOperationException(
                "Microsoft Edge WebView2 Runtime is not installed.");
        }

        var userDataFolder = _privateUserDataFolder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TabloidBrowser",
            "WebView2");

        Directory.CreateDirectory(userDataFolder);

        var options = new CoreWebView2EnvironmentOptions(
            "--disable-background-networking " +
            "--disable-features=MediaRouter,AutofillServerCommunication,OptimizationHints")
        {
            AreBrowserExtensionsEnabled = true
        };

        _environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
        LogStartupPhase("CoreWebView2Environment created");
    }

    private static void FireAndForget(Task? task, string context = "")
    {
        if (task is null) return;
        task.ContinueWith(t =>
        {
            if (t.Exception is not null)
                Log.Error(t.Exception, $"Fire-and-forget failed{(string.IsNullOrEmpty(context) ? "" : $" ({context})")}");
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    private static readonly Regex ChromeExtensionIdPattern = new("^[a-p]{32}$", RegexOptions.Compiled);

    /// <summary>
    /// Chrome Web Store extension ids are exactly 32 lowercase letters a-p.
    /// Validating here keeps attacker-supplied ids out of the download URL's
    /// query string and out of file paths.
    /// </summary>
    private static bool IsValidChromeExtensionId(string? extensionId) =>
        !string.IsNullOrWhiteSpace(extensionId) && ChromeExtensionIdPattern.IsMatch(extensionId);

    /// <summary>
    /// Only http(s) (and the inert about:blank initial document) may become a
    /// top-level document. Everything else â€” file:, javascript:, data:, custom
    /// external protocols â€” is cancelled before it can load.
    /// </summary>
    private static bool IsAllowedTopLevelNavigationUri(string? uriString)
    {
        if (string.IsNullOrWhiteSpace(uriString)) return true;
        if (!Uri.TryCreate(uriString, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme == Uri.UriSchemeHttp ||
               uri.Scheme == Uri.UriSchemeHttps ||
               string.Equals(uri.Scheme, "about", StringComparison.OrdinalIgnoreCase);
    }

    private void SetStatusTextSafe(string text)
    {
        if (Dispatcher.CheckAccess())
            StatusBar.StatusText.Text = text;
        else
            Dispatcher.BeginInvoke(() => StatusBar.StatusText.Text = text);
    }

    private void ShowStartupError(Exception exception)
    {
        StatusBar.StatusText.Text = "Startup failed";
        BrowserPane.StartOverlay.Visibility = Visibility.Visible;
        BrowserPane.InternalPageHost.Visibility = Visibility.Collapsed;
        BrowserPane.WebViewHost.Visibility = Visibility.Hidden;
        BrowserPane.LoadingOverlay.SetLoadingSpinnerActive(false);

        var result = MessageBox.Show(
            $"Aerio could not start.\n\n{exception.Message}\n\nOpen the WebView2 download page?",
            "Aerio - WebView2 Required",
            MessageBoxButton.YesNo,
            MessageBoxImage.Error);

        if (result == MessageBoxResult.Yes)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://developer.microsoft.com/en-us/microsoft-edge/webview2/",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to open download page");
            }
        }
    }

    private readonly ConcurrentDictionary<BrowserTab, Task> _pendingInitTasks = [];

    private Task EnsureWebViewAsync(BrowserTab tab, bool attachBeforeInitialize = false)
    {
        if (tab.WebView?.CoreWebView2 is not null && tab.IsWebViewInitialized)
        {
            return Task.CompletedTask;
        }

        if (_environment is null)
        {
            throw new InvalidOperationException("WebView2 environment was not initialized.");
        }

        // Prevent duplicate initialization BEFORE creating a WebView2
        Task initTask;
        lock (_initializationLock)
        {
            if (tab.IsWebViewInitialized)
            {
                return Task.CompletedTask;
            }
            if (_pendingInitTasks.TryGetValue(tab, out var pending))
            {
                // Another call is already initializing this tab. Await it rather
                // than returning early â€” an early return silently drops the
                // caller's pending navigation (blank restored tabs, ignored URLs).
                initTask = pending;
            }
            else
            {
                initTask = InitializeTabWebViewCoreAsync(tab, attachBeforeInitialize);
                _pendingInitTasks[tab] = initTask;
            }
        }

        return initTask;
    }

    private async Task InitializeTabWebViewCoreAsync(BrowserTab tab, bool attachBeforeInitialize)
    {
        var webView = tab.WebView;
        if (webView is null)
        {
            webView = new WebView2
            {
                DefaultBackgroundColor = GetWebViewBackgroundColor()
            };
            tab.WebView = webView;
        }

        // A WPF WebView2 must be in the visual tree to complete initialization.
        // Background tabs (e.g. AI tabs) have no visible surface, so temporarily
        // host them (hidden, behind the active WebView) until CoreWebView2 exists.
        var tempHosted = false;
        var hostWasVisible = BrowserPane.WebViewHost.Visibility == Visibility.Visible;
        try
        {
            if (attachBeforeInitialize && _activeTab == tab)
            {
                AttachActiveWebView(tab);
            }
            else if (webView.Parent is null)
            {
                tempHosted = true;
                webView.Visibility = Visibility.Hidden;
                if (_activeTab is not null)
                {
                    Panel.SetZIndex(webView, -1);
                }
                BrowserPane.WebViewHost.Children.Add(webView);
            }

            await webView.EnsureCoreWebView2Async(_environment);

            if (tab.IsWebViewInitialized)
            {
                return;
            }

            var cwv = webView.CoreWebView2;
            _webViewToTabMap[cwv] = tab;

            var cleanupCountBefore = tab.CleanupActions.Count;
            try
            {
            cwv.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            cwv.WebResourceRequested += OnWebResourceRequested;
            cwv.WebMessageReceived += OnWebMessageReceived;
            cwv.Find.ActiveMatchIndexChanged += Find_MatchChanged;
            cwv.Find.MatchCountChanged += Find_MatchChanged;
            cwv.DownloadStarting += WebView_DownloadStarting;
            cwv.ContextMenuRequested += OnContextMenuRequested;

            await cwv.AddScriptToExecuteOnDocumentCreatedAsync(ScrollTrackingScript);
            await cwv.AddScriptToExecuteOnDocumentCreatedAsync(LinkClickScript);
            await cwv.AddScriptToExecuteOnDocumentCreatedAsync(
                "window.__tblBridge=window.chrome.webview;");
            if (_dataStore.Settings.ForceDarkPage)
            {
                await cwv.AddScriptToExecuteOnDocumentCreatedAsync(ForceDarkScript);
            }

            if (_profile is null)
            {
                lock (_initializationLock)
                {
                    if (_profile is null)
                    {
                        _profile = cwv.Profile;
                    }
                }
                await LoadExtensionsAsync();
            }

            cwv.NavigationStarting += OnNavigationStarting;
            cwv.FrameNavigationStarting += OnFrameNavigationStarting;
            webView.NavigationCompleted += OnNavigationCompleted;
            cwv.DocumentTitleChanged += OnDocumentTitleChanged;
            cwv.NewWindowRequested += OnNewWindowRequested;
            cwv.PermissionRequested += OnPermissionRequested;
            cwv.IsDocumentPlayingAudioChanged += OnIsDocumentPlayingAudioChanged;
            cwv.IsMutedChanged += OnIsMutedChanged;
            cwv.FaviconChanged += OnFaviconChanged;

            tab.CleanupActions.Add(() =>
            {
                cwv.NavigationStarting -= OnNavigationStarting;
                cwv.FrameNavigationStarting -= OnFrameNavigationStarting;
                webView.NavigationCompleted -= OnNavigationCompleted;
                cwv.WebResourceRequested -= OnWebResourceRequested;
                cwv.WebMessageReceived -= OnWebMessageReceived;
                cwv.Find.ActiveMatchIndexChanged -= Find_MatchChanged;
                cwv.Find.MatchCountChanged -= Find_MatchChanged;
                cwv.DownloadStarting -= WebView_DownloadStarting;
                cwv.ContextMenuRequested -= OnContextMenuRequested;
                cwv.DocumentTitleChanged -= OnDocumentTitleChanged;
                cwv.NewWindowRequested -= OnNewWindowRequested;
                cwv.PermissionRequested -= OnPermissionRequested;
                cwv.IsDocumentPlayingAudioChanged -= OnIsDocumentPlayingAudioChanged;
                cwv.IsMutedChanged -= OnIsMutedChanged;
                cwv.FaviconChanged -= OnFaviconChanged;
                _webViewToTabMap.TryRemove(cwv, out _);
            });

            var settings = cwv.Settings;
            settings.AreDefaultScriptDialogsEnabled = true;
            settings.AreDevToolsEnabled = _dataStore.Settings.EnableDevTools;
            settings.IsGeneralAutofillEnabled = _dataStore.Settings.EnableAutofill;
            settings.IsPasswordAutosaveEnabled = _dataStore.Settings.EnablePasswordSave;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            settings.IsStatusBarEnabled = false;

            // No host objects are ever registered; deny them outright so a
            // future regression cannot silently expose one to web content.
            settings.AreHostObjectsAllowed = false;

            tab.IsWebViewInitialized = true;
            }
            catch
            {
                // Initialization failed midway â€” unsubscribe anything registered
                // so far so a retry doesn't double-subscribe event handlers.
                var registered = tab.CleanupActions.Skip(cleanupCountBefore).ToArray();
                tab.CleanupActions.RemoveRange(cleanupCountBefore, tab.CleanupActions.Count - cleanupCountBefore);
                foreach (var action in registered)
                {
                    try { action(); } catch { }
                }
                _webViewToTabMap.TryRemove(cwv, out _);
                throw;
            }
        }
        finally
        {
            _pendingInitTasks.TryRemove(tab, out _);
            lock (_initializationLock) _initializingTabs.Remove(tab);

            if (tempHosted)
            {
                Panel.SetZIndex(webView, 0);

                if (_activeTab == tab)
                {
                    webView.Visibility = Visibility.Visible;
                    AttachActiveWebView(tab);
                }
                else
                {
                    RemoveElementFromParent(webView);
                    webView.Visibility = Visibility.Visible;

                    if (_activeTab is { Kind: BrowserTabKind.Web } active &&
                        active.WebView is { } activeWebView)
                    {
                        AttachActiveWebView(active);
                    }
                    else if (!hostWasVisible)
                    {
                        BrowserPane.WebViewHost.Visibility = Visibility.Hidden;
                    }
                }
            }
        }
        }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (sender is not CoreWebView2 cwv || !_webViewToTabMap.TryGetValue(cwv, out var tab)) return;
        if (e.ResourceContext == CoreWebView2WebResourceContext.Document)
        {
            return;
        }
        
        var settings = _dataStore.Settings;
        var host = TrackerPolicy.GetHost(tab.Address);
        var reqHost = TrackerPolicy.GetHost(e.Request.Uri);
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(reqHost))
        {
            return;
        }

        if (settings.TrackerAllowlist.Count > 0 && settings.TrackerAllowlist.Any(domain => TrackerPolicy.DomainMatches(host, domain)))
        {
            return;
        }

        // Local-network guard: a page from the public internet must not be able
        // to reach the user's LAN or intranet (router CSRF, device
        // fingerprinting). Requests the user initiates themselves (top-level
        // documents) are exempt, and allowlisted sites count as trusted.
        if (settings.BlockLocalNetworkAccessEffective &&
            !IsLocalAddress(host) &&
            IsLocalAddress(reqHost))
        {
            BlockWebRequest(cwv, e);
            return;
        }

        if (settings.EnableAdBlock && AdBlockEngine.Active.Current.ShouldBlock(e.Request.Uri))
        {
            BlockWebRequest(cwv, e);
            return;
        }

        if (!settings.BlockTrackers)
        {
            return;
        }

        var blocklist = settings.TrackerBlocklist;
        if (blocklist.Count == 0 || !blocklist.Any(domain => TrackerPolicy.DomainMatches(reqHost, domain)))
        {
            return;
        }

        BlockWebRequest(cwv, e);
    }

    private void BlockWebRequest(CoreWebView2 cwv, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var count = Interlocked.Increment(ref _blockedTrackerCount);

        // Forensic breadcrumb, throttled so ad-heavy pages cannot flood the log:
        // at most one line every 5 seconds shows what was last blocked and the
        // running total, proving the blocker is active in the field.
        var nowTicks = DateTime.UtcNow.Ticks;
        var lastTicks = Interlocked.Read(ref _lastBlockLogTicks);
        if (nowTicks - lastTicks >= TimeSpan.FromSeconds(5).Ticks &&
            Interlocked.Exchange(ref _lastBlockLogTicks, nowTicks) == lastTicks)
        {
            Log.Debug("Blocked {Url} (total {Count})", TrimUrl(e.Request.Uri), count);
        }

        if (Interlocked.Exchange(ref _privacyIndicatorPending, 1) == 0)
        {
            Dispatcher.BeginInvoke(() =>
            {
                _privacyIndicatorPending = 0;
                UpdatePrivacyIndicator();
            });
        }

        e.Response = cwv.Environment.CreateWebResourceResponse(
            new MemoryStream(Array.Empty<byte>()),
            204,
            "No Content",
            "Content-Type: text/plain");
    }

    private long _lastBlockLogTicks;

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (sender is not CoreWebView2 cwv || !_webViewToTabMap.TryGetValue(cwv, out var tab)) return;
        WebMessageReceived(tab, e);
    }

    private void OnContextMenuRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        if (sender is not CoreWebView2 cwv || !_webViewToTabMap.TryGetValue(cwv, out var tab)) return;
        if (_environment is null) return;

        var darkItem = _environment.CreateContextMenuItem(
            "Force dark page",
            null,
            CoreWebView2ContextMenuItemKind.CheckBox);
        darkItem.IsChecked = _dataStore.Settings.ForceDarkPage;
        darkItem.CustomItemSelected += (_, _) =>
        {
            void handler()
            {
                var newState = !_dataStore.Settings.ForceDarkPage;
                darkItem.IsChecked = newState;
                var s = _dataStore.Settings with { ForceDarkPage = newState };
                _dataStore.SaveSettings(s);
                ApplyForceDarkToExistingTabs();
            }
            if (Dispatcher.CheckAccess()) handler();
            else Dispatcher.Invoke(handler);
        };
        e.MenuItems.Add(darkItem);
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (sender is not CoreWebView2 cwv) return;
        if (!_webViewToTabMap.TryGetValue(cwv, out var tab)) return;

        if (e.Uri.StartsWith("tabloid://install-crx/", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            if (tab.Kind != BrowserTabKind.Settings)
            {
                SetStatusTextSafe("Extension install is only allowed from the Settings page.");
                return;
            }
            var extId = e.Uri["tabloid://install-crx/".Length..];
            if (!IsValidChromeExtensionId(extId))
            {
                SetStatusTextSafe("Invalid Chrome Web Store extension id.");
                return;
            }
            var extConfirm = MessageBox.Show(
                $"Install extension {extId} from the Chrome Web Store?",
                "Confirm Extension Install",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (extConfirm != MessageBoxResult.Yes)
                return;
            FireAndForget(DownloadAndInstallCrxAsync(extId), "DownloadAndInstallCrxAsync");
            return;
        }

        if (TryHandleTabloidUri(tab, e.Uri))
        {
            e.Cancel = true;
            return;
        }

        // Top-level navigations are restricted to web content. file:/javascript:/
        // data: and unknown schemes must never load as a top-level document â€”
        // pages (or AI-driven navigation) could otherwise read local files and
        // exfiltrate them to remote endpoints.
        if (!IsAllowedTopLevelNavigationUri(e.Uri))
        {
            e.Cancel = true;
            SetStatusTextSafe($"Blocked navigation to {TrimUrl(e.Uri)}");
            return;
        }

        void handler()
        {
            tab.Kind = BrowserTabKind.Web;
            tab.Address = e.Uri;
            ApplySavedSiteZoom(tab);
            tab.Title = "Loading...";
            tab.IsLoading = true;
            tab.HasAudio = false;
            tab.IsPlayingAudio = false;
            if (cwv is CoreWebView2 webCwv)
            {
                try { tab.IsMuted = webCwv.IsMuted; } catch { tab.IsMuted = false; }
            }
            else
            {
                tab.IsMuted = false;
            }

            if (tab.AllowNextNavigationScrollRestore)
            {
                tab.PendingScrollRestore = HasScrollPosition(tab);
                tab.AllowNextNavigationScrollRestore = false;
            }
            else
            {
                tab.ScrollX = 0;
                tab.ScrollY = 0;
                tab.PendingScrollRestore = false;
            }

            if (_activeTab == tab)
            {
                StatusBar.StatusText.Text = $"Loading {TrimUrl(e.Uri)}";
                UpdateAddressBar();
                UpdateLoadingOverlay();
                UpdateReloadStopButton();
            }
        }
        if (Dispatcher.CheckAccess()) handler();
        else Dispatcher.Invoke(handler);
    }
    private void OnFrameNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // Handle tabloid:// URIs that may come from iframe navigations
        if (e.Uri.StartsWith("tabloid://install-crx/", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            if (sender is CoreWebView2 cwv && _webViewToTabMap.TryGetValue(cwv, out var tab))
            {
                if (tab.Kind != BrowserTabKind.Settings)
                {
                    SetStatusTextSafe("Extension install is only allowed from the Settings page.");
                    return;
                }
            }
            else
            {
                return;
            }
            var extId = e.Uri["tabloid://install-crx/".Length..];
            if (!IsValidChromeExtensionId(extId))
            {
                SetStatusTextSafe("Invalid Chrome Web Store extension id.");
                return;
            }
            var extConfirm = MessageBox.Show(
                $"Install extension {extId} from the Chrome Web Store?",
                "Confirm Extension Install",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (extConfirm != MessageBoxResult.Yes)
                return;
            FireAndForget(DownloadAndInstallCrxAsync(extId), "DownloadAndInstallCrxAsync");
            return;
        }

        // Don't update tab.Address for iframe navigations
    }


    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (sender is not WebView2 webView) return;
        var cwv = webView.CoreWebView2;
        if (cwv is null) return;
        if (!_webViewToTabMap.TryGetValue(cwv, out var tab)) return;

        try
        {
            tab.IsLoading = false;
            tab.LastActiveUtc = DateTimeOffset.UtcNow;
            var historySaveFailed = false;

            if (e.IsSuccess)
            {
                // Per-tab counter: a global counter is bumped by any tab's
                // navigation and produces cross-tab false positives when the AI
                // verifies whether its click caused a navigation.
                tab.PageVersion++;
                var title = cwv.DocumentTitle;
                tab.Title = string.IsNullOrWhiteSpace(title) ? TrimUrl(tab.Address) : title;

                FireAndForget(LoadFaviconAsync(tab), "LoadFaviconAsync navigation");

                if (tab.IsPlayingAudio)
                {
                    FireAndForget(ResolveSiteAccentAsync(tab), "ResolveSiteAccentAsync navigation");
                }

                if (!_isPrivate && BrowserDataStore.IsHttpUrl(tab.Address))
                {
                    if (!_dataStore.AddHistory(tab.Title, tab.Address) && tab == _activeTab)
                    {
                        historySaveFailed = true;
                    }
                }

                FireAndForget(RestoreScrollPositionAsync(tab), "RestoreScrollPositionAsync");

                tab.ReaderContent = null;
                if (tab.IsReaderMode)
                {
                    FireAndForget(cwv.ExecuteScriptAsync(ReaderViewCleanupScript), "ExecuteScriptAsync.ReaderViewCleanupScript");
                    Interlocked.Exchange(ref _pendingReaderTab, tab);
                    var reExtract = await BuildReaderExtractionScriptAsync();
                    await cwv.ExecuteScriptAsync(reExtract);
                }
                else if (_activeTab == tab)
                {
                    FireAndForget(TryInjectReaderDetectionAsync(cwv), "TryInjectReaderDetectionAsync");
                }
            }
            else
            {
                tab.Title = "Navigation failed";
                StatusBar.StatusText.Text = $"Navigation failed: {e.WebErrorStatus}";
            }

            if (_activeTab == tab)
            {
                StatusBar.StatusText.Text = e.IsSuccess
                    ? historySaveFailed ? "Couldn't save this page to history" : "Ready"
                    : StatusBar.StatusText.Text;
                UpdateNavigationButtons();
                UpdateLoadingOverlay();
                UpdateReaderUi(tab.IsReaderMode);
                UpdateReloadStopButton();
                UpdateZoomDisplay();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "NavigationCompleted handler failed for {Url}", tab.Address);
            tab.IsLoading = false;
            if (_activeTab == tab)
            {
                UpdateLoadingOverlay();
                UpdateReloadStopButton();
            }
        }
    }

    private void OnDocumentTitleChanged(object? sender, object? e)
    {
        if (sender is not CoreWebView2 cwv || !_webViewToTabMap.TryGetValue(cwv, out var tab)) return;
        void handler()
        {
            try
            {
                var title = cwv.DocumentTitle;
                if (!string.IsNullOrWhiteSpace(title)) tab.Title = title;
                if (_activeTab == tab) UpdateWindowTitle();
            }
            catch (Exception ex) { Log.Error(ex, "Error occurred"); }
        }
        if (Dispatcher.CheckAccess()) handler();
        else Dispatcher.Invoke(handler);
    }
    private async void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (!IsAllowedTopLevelNavigationUri(e.Uri))
        {
            SetStatusTextSafe($"Blocked popup to {TrimUrl(e.Uri)}");
            return;
        }
        await Dispatcher.InvokeAsync(async () =>
        {
            try { await AddWebTabAsync(e.Uri); }
            catch (Exception ex) { Log.Error(ex, "Failed to open new window for {Url}", e.Uri ?? "unknown"); StatusBar.StatusText.Text = $"Failed to open: {ex.Message}"; }
        }).Task.Unwrap();
    }

    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        if (sender is not CoreWebView2 cwv || !_webViewToTabMap.TryGetValue(cwv, out var tab)) return;
        PermissionRequested(tab, e);
    }

    private void OnIsDocumentPlayingAudioChanged(object? sender, object? e)
    {
        if (sender is not CoreWebView2 cwv || !_webViewToTabMap.TryGetValue(cwv, out var tab)) return;
        void handler()
        {
            try
            {
                var playing = cwv.IsDocumentPlayingAudio;
                if (playing) tab.HasAudio = true;
                tab.IsPlayingAudio = playing;
                RefreshMediaChip();
                if (playing) FireAndForget(ResolveSiteAccentAsync(tab), "ResolveSiteAccentAsync");
            }
            catch (Exception ex) { Log.Error(ex, "Error occurred"); }
        }
        if (Dispatcher.CheckAccess()) handler();
        else Dispatcher.Invoke(handler);
    }

    private static async Task ResolveSiteAccentAsync(BrowserTab tab, bool forceRefresh = false)
    {
        var address = tab.Address;
        var core = tab.WebView?.CoreWebView2;
        if (core is null) return;

        var color = await SiteAccentService.ResolveAsync(core, address, forceRefresh);

        // A favicon or theme-color tag can appear shortly after playback
        // starts. Give it one more chance before settling on the fallback.
        if (color is null && !forceRefresh)
        {
            await Task.Delay(TimeSpan.FromSeconds(1.5));
            if (!tab.IsPlayingAudio) return;
            if (!string.Equals(tab.Address, address, StringComparison.Ordinal)) return;

            core = tab.WebView?.CoreWebView2;
            if (core is null) return;

            color = await SiteAccentService.ResolveAsync(core, address, forceRefresh: true);
        }

        if (color is not { } resolved) return;

        // Navigation may have happened while the color was being resolved, or
        // playback may have stopped; only apply the result to the same page.
        if (!tab.IsPlayingAudio) return;
        if (!string.Equals(tab.Address, address, StringComparison.Ordinal)) return;

        tab.SiteAccentColor = resolved;
    }

    private async Task LoadFaviconAsync(BrowserTab tab)
    {
        if (tab.Kind != BrowserTabKind.Web) return;

        var core = tab.WebView?.CoreWebView2;
        if (core is null) return;

        var address = tab.Address;
        string faviconUri;
        try
        {
            faviconUri = core.FaviconUri;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Reading favicon URI failed for {Url}", address);
            return;
        }

        if (string.IsNullOrEmpty(faviconUri))
        {
            tab.Favicon = null;
            tab.FaviconUri = null;
            return;
        }

        if (tab.Favicon is not null &&
            string.Equals(tab.FaviconUri, faviconUri, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            using var stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            if (stream is null) return;

            var image = FaviconImageDecoder.Decode(stream);
            if (image is null) return;

            // A newer navigation or favicon may have superseded this request.
            if (!string.Equals(tab.Address, address, StringComparison.Ordinal)) return;
            if (!string.Equals(tab.WebView?.CoreWebView2?.FaviconUri, faviconUri, StringComparison.Ordinal)) return;

            tab.Favicon = image;
            tab.FaviconUri = faviconUri;
            RefreshAddressBarChromeIfActive(tab);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Loading favicon failed for {Url}", address);
        }
    }

    /// <summary>
    /// The address bar shows the active tab's favicon; it often arrives after
    /// the address was rendered, so refresh the chrome when it lands.
    /// </summary>
    private void RefreshAddressBarChromeIfActive(BrowserTab tab)
    {
        if (Dispatcher.CheckAccess())
        {
            if (_activeTab == tab) UpdateAddressBarChrome();
        }
        else
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_activeTab == tab) UpdateAddressBarChrome();
            });
        }
    }

    private void OnFaviconChanged(object? sender, object? e)
    {
        if (sender is not CoreWebView2 cwv || !_webViewToTabMap.TryGetValue(cwv, out var tab)) return;
        void handler()
        {
            try
            {
                FireAndForget(LoadFaviconAsync(tab), "LoadFaviconAsync favicon");

                if (!tab.IsPlayingAudio) return;

                // Already resolved (brand/theme/favicon); no need to chase
                // later favicon changes.
                if (tab.SiteAccentColor is not null) return;

                // Animated favicons can raise this repeatedly; debounce.
                var now = DateTimeOffset.UtcNow;
                if (now - tab.LastFaviconAccentAttemptUtc < TimeSpan.FromSeconds(5)) return;
                tab.LastFaviconAccentAttemptUtc = now;

                // The icon that just arrived may be the only color source for
                // this site, so drop any cached miss and resolve again.
                SiteAccentService.Invalidate(tab.Address);
                FireAndForget(ResolveSiteAccentAsync(tab, forceRefresh: true), "ResolveSiteAccentAsync favicon");
            }
            catch (Exception ex) { Log.Error(ex, "FaviconChanged handler failed"); }
        }
        if (Dispatcher.CheckAccess()) handler();
        else Dispatcher.Invoke(handler);
    }

    private void OnIsMutedChanged(object? sender, object? e)
    {
        if (sender is not CoreWebView2 cwv || !_webViewToTabMap.TryGetValue(cwv, out var tab)) return;
        void handler()
        {
            try { tab.IsMuted = cwv.IsMuted; }
            catch (Exception ex) { Log.Error(ex, "Error occurred"); }
        }
        if (Dispatcher.CheckAccess()) handler();
        else Dispatcher.Invoke(handler);
    }

    private void WebView_DownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs args)
    {
        try
        {
            var url = args.DownloadOperation.Uri;
            var ext = Path.GetExtension(new Uri(url).LocalPath);

        if (TrackerPolicy.IsDangerousDownloadExtension(ext))
        {
            var fileName = Path.GetFileName(new Uri(url).LocalPath);
            var insecureTransport = Uri.TryCreate(url, UriKind.Absolute, out var downloadUri) &&
                                    downloadUri.Scheme == Uri.UriSchemeHttp;
            var warning = insecureTransport
                ? $"\n\nThis file type ({ext}) can harm your computer, and it is downloaded over an " +
                  "UNENCRYPTED connection (HTTP) — it could be modified in transit."
                : $"\n\nThis file type ({ext}) can harm your computer.";
            var result = MessageBox.Show(
                $"Download \"{fileName}\"?{warning}",
                "Confirm Download",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
            {
                args.Cancel = true;
                return;
            }
        }

        // Only auto-install CRX payloads that come from the pinned Chrome Web
        // Store update service. A ".crx" download from any other host is just
        // tracked as a regular download â€” never offered for silent install.
        var isTrustedCrxUrl =
            Uri.TryCreate(url, UriKind.Absolute, out var crxUri) &&
            crxUri.Scheme == Uri.UriSchemeHttps &&
            string.Equals(crxUri.Host, "clients2.google.com", StringComparison.OrdinalIgnoreCase) &&
            crxUri.AbsolutePath.StartsWith("/service/update2/crx", StringComparison.OrdinalIgnoreCase);
        if (!isTrustedCrxUrl)
        {
            TrackDownload(args);
            return;
        }

        var crxFileName = Path.GetFileName(new Uri(url).LocalPath);
        if (string.IsNullOrWhiteSpace(crxFileName)) crxFileName = "extension.crx";
        var crxConfirm = MessageBox.Show(
            $"Install extension from \"{crxFileName}\"?\n\nOnly install extensions from sources you trust.",
            "Confirm Extension Install",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (crxConfirm != MessageBoxResult.Yes)
        {
            args.Cancel = true;
            return;
        }

        args.Handled = true;

        var tempDir = Path.Combine(Path.GetTempPath(), "TabloidBrowser", "CrxDownloads");
        Directory.CreateDirectory(tempDir);
        var crxPath = Path.Combine(tempDir, Guid.NewGuid() + ".crx");
        args.ResultFilePath = crxPath;

        var operation = args.DownloadOperation;
        EventHandler<object>? handler = null;
        handler = (_, _) =>
        {
            if (operation.State == CoreWebView2DownloadState.Completed)
            {
                operation.StateChanged -= handler;
                Dispatcher.InvokeAsync(async () =>
                {
                    await InstallCrxAsync(crxPath);
                    try { File.Delete(crxPath); } catch (Exception ex) { Log.Debug(ex, "Failed to delete CRX temp file"); }
                });
            }
            else if (operation.State == CoreWebView2DownloadState.Interrupted)
            {
                operation.StateChanged -= handler;
            }
        };
        operation.StateChanged += handler;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error occurred");
        }
    }

    private async Task DownloadAndInstallCrxAsync(string extensionId)
    {
        try
        {
            if (!IsValidChromeExtensionId(extensionId))
            {
                SetStatusTextSafe("Invalid Chrome Web Store extension id.");
                return;
            }

            var url = $"https://clients2.google.com/service/update2/crx?response=redirect&prodversion=106.0.0.0&acceptformat=crx3&x=id%3D{extensionId}%26installsource%3Dondemand%26uc";

            var tempDir = Path.Combine(Path.GetTempPath(), "TabloidBrowser", "CrxDownloads");
            Directory.CreateDirectory(tempDir);
            var crxPath = Path.Combine(tempDir, $"{extensionId}.crx");

            // Defense in depth: the id is regex-validated above, but never let a
            // composed path escape the download directory.
            var fullCrxPath = Path.GetFullPath(crxPath);
            var fullTempDir = Path.GetFullPath(tempDir);
            if (!fullCrxPath.StartsWith(fullTempDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                Log.Error("CRX download path escaped the temp directory; refusing to write");
                return;
            }

            using var response = await _crxHttpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            await using (var fs = new FileStream(fullCrxPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await response.Content.CopyToAsync(fs);
            }

            await InstallCrxAsync(fullCrxPath, extensionId);
            try { File.Delete(fullCrxPath); } catch (Exception ex) { Log.Debug(ex, "Failed to delete CRX temp file"); }
        }
        catch (Exception ex)
        {
            SetStatusTextSafe($"Failed to download extension: {ex.Message}");
        }
    }

    private void UpdatePrivacyIndicator()
    {
        // The shield doubles as the connection indicator: red while viewing a
        // plain-HTTP site, otherwise the usual tracker-blocking color.
        var tab = _activeTab;
        var insecureHttp = tab?.Kind == BrowserTabKind.Web &&
                           BrowserDataStore.IsHttpUrl(tab.Address) &&
                           Uri.TryCreate(tab.Address, UriKind.Absolute, out var uri) &&
                           uri.Scheme != Uri.UriSchemeHttps;

        var tracking = _dataStore.Settings.BlockTrackers;
        var privacyText = tracking || _dataStore.Settings.EnableAdBlock
            ? $"Ad & tracker blocking on. Blocked {_blockedTrackerCount} requests."
            : "Ad & tracker blocking off";

        NavToolbar.PrivacyButton.Foreground = insecureHttp
            ? GetBrush("Danger")
            : tracking ? GetBrush("Lime") : GetBrush("Muted");
        NavToolbar.PrivacyButton.ToolTip = insecureHttp
            ? $"Not secure â€” unencrypted HTTP connection. {privacyText}"
            : privacyText;
    }

    private void TrackDownload(CoreWebView2DownloadStartingEventArgs args)
    {
        var fileName = args.ResultFilePath is not null ? Path.GetFileName(args.ResultFilePath) : "unknown";
        var resultFilePath = args.ResultFilePath ?? "";
        var operation = args.DownloadOperation;
        var sourceUrl = operation.Uri;
        Dispatcher.BeginInvoke(() => StatusBar.StatusText.Text = $"Downloading {fileName}");

        // Live state for the downloads shelf (feature parity with the page list).
        var shelfItem = new DownloadShelfItem(
            fileName,
            sourceUrl,
            ToByteCount(operation.TotalBytesToReceive),
            resultFilePath);
        _downloadOperations[shelfItem.Id] = operation;
        DownloadShelf.Add(shelfItem);

        void OnDownloadProgress(object? _, object? __)
        {
            var received = ToByteCount(operation.BytesReceived);
            var total = ToByteCount(operation.TotalBytesToReceive);
            Dispatcher.BeginInvoke(() => shelfItem.UpdateProgress(received, total));
        }

        operation.BytesReceivedChanged += OnDownloadProgress;
        operation.StateChanged += OnDownloadCompleted;

        void OnDownloadCompleted(object? _, object? __)
        {
            if (operation.State == CoreWebView2DownloadState.Interrupted)
            {
                // Cancelled/failed downloads: detach the handlers so they don't
                // leak for the process lifetime, and tell the user what happened
                // instead of failing silently.
                operation.StateChanged -= OnDownloadCompleted;
                operation.BytesReceivedChanged -= OnDownloadProgress;
                _downloadOperations.Remove(shelfItem.Id);
                Dispatcher.BeginInvoke(() =>
                {
                    shelfItem.Interrupt();
                    DownloadShelf.NotifyStateChanged(shelfItem);
                    StatusBar.StatusText.Text = $"Download interrupted: {fileName}";
                });
                return;
            }

            if (operation.State != CoreWebView2DownloadState.Completed)
            {
                return;
            }

            operation.StateChanged -= OnDownloadCompleted;
            operation.BytesReceivedChanged -= OnDownloadProgress;
            _downloadOperations.Remove(shelfItem.Id);
            Dispatcher.BeginInvoke(() =>
            {
                shelfItem.Complete(resultFilePath);
                DownloadShelf.NotifyStateChanged(shelfItem);
                if (_dataStore.AddDownload(fileName, resultFilePath, sourceUrl))
                {
                    StatusBar.StatusText.Text = $"Downloaded {fileName}";
                }
                else
                {
                    StatusBar.StatusText.Text = $"Downloaded {fileName}, but couldn't save it to the downloads list";
                }
            });
        }
    }

    /// <summary>
    /// WebView2 reports download counters with mixed signedness; the shelf model
    /// uses non-negative signed counts (0 = unknown).
    /// </summary>
    private static long ToByteCount(long value) => value < 0 ? 0 : value;

    private static long ToByteCount(ulong? value) =>
        value is { } count && count <= long.MaxValue ? (long)count : 0;

    /// <summary>Targets the first video on the page for PIP.</summary>
    private const string PictureInPictureScript =
        """
        (async () => {
            try {
                if (!document.pictureInPictureEnabled) {
                    return JSON.stringify({ ok: false, reason: 'not-supported' });
                }
                const videos = Array.from(document.querySelectorAll('video'));
                const video = videos.find(v => !v.paused && !v.ended && v.readyState > 0)
                    || videos.find(v => v.readyState > 0)
                    || videos[0];
                if (!video) {
                    return JSON.stringify({ ok: false, reason: 'no-video' });
                }
                if (document.pictureInPictureElement === video) {
                    await document.exitPictureInPicture();
                    return JSON.stringify({ ok: true, action: 'exited' });
                }
                await video.requestPictureInPicture();
                return JSON.stringify({ ok: true, action: 'entered' });
            } catch (e) {
                return JSON.stringify({ ok: false, reason: (e && e.name) ? e.name : 'failed' });
            }
        })();
        """;

    /// <summary>Pauses or resumes every media element on the page.</summary>
    private const string MediaPlayPauseScript =
        """
        (() => {
            const media = Array.from(document.querySelectorAll('video,audio'));
            if (media.length === 0) {
                return 'none';
            }
            const anyPlaying = media.some(m => !m.paused && !m.ended);
            const tasks = media.map(m => {
                if (anyPlaying) {
                    m.pause();
                    return null;
                }
                return m.play().catch(() => null);
            });
            return Promise.all(tasks).then(() => anyPlaying ? 'paused' : 'played');
        })();
        """;

    private async Task TogglePictureInPictureAsync(BrowserTab? tab)
    {
        var cwv = tab?.WebView?.CoreWebView2;
        if (cwv is null)
        {
            SetStatusTextSafe("Picture-in-Picture isn't available on this tab.");
            return;
        }

        try
        {
            var raw = await cwv.ExecuteScriptAsync(PictureInPictureScript);
            var (ok, reason, action) = ParseScriptResult(raw);
            if (ok)
            {
                SetStatusTextSafe(action == "exited" ? "Picture-in-Picture closed" : "Picture-in-Picture on");
                return;
            }

            SetStatusTextSafe(reason switch
            {
                "no-video" => "No video found on this page",
                "not-supported" => "This page doesn't support Picture-in-Picture",
                "NotAllowedError" => "Click the video once, then try Picture-in-Picture",
                _ => "Picture-in-Picture isn't available here"
            });
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Picture-in-Picture toggle failed");
            SetStatusTextSafe("Picture-in-Picture isn't available here");
        }
    }

    private async Task ToggleMediaPlayPauseAsync(BrowserTab? tab)
    {
        var cwv = tab?.WebView?.CoreWebView2;
        if (cwv is null) return;

        try
        {
            var raw = await cwv.ExecuteScriptAsync(MediaPlayPauseScript);
            var result = string.IsNullOrWhiteSpace(raw) ? null : JsonSerializer.Deserialize<string>(raw);
            SetStatusTextSafe(result switch
            {
                "paused" => "Media paused",
                "played" => "Media playing",
                _ => "No media on this page"
            });
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Media play/pause failed");
        }
    }

    /// <summary>Clicks the playing page's previous/next transport control.</summary>
    private async Task SkipMediaAsync(BrowserTab? tab, bool forward)
    {
        var cwv = tab?.WebView?.CoreWebView2;
        if (cwv is null) return;

        try
        {
            var script = MediaSkipScript.Replace("'__SKIP__'", forward ? "'next'" : "'previous'");
            var raw = await cwv.ExecuteScriptAsync(script);
            var (ok, _, action) = ParseScriptResult(raw);
            if (!ok)
            {
                SetStatusTextSafe("This player has no next/previous control");
                return;
            }

            SetStatusTextSafe(action == "next" ? "Next track" : "Previous track");

            // A track change usually swaps the cover art; give the page a moment
            // to publish new Media Session metadata, then refresh the chip.
            FireAndForget(RefreshMediaArtworkAfterSkipAsync(tab), "RefreshMediaArtworkAfterSkipAsync");
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Media skip failed");
        }
    }

    private async Task RefreshMediaArtworkAfterSkipAsync(BrowserTab tab)
    {
        await Task.Delay(1500);
        await ResolveMediaArtworkAsync(tab);
    }

    /// <summary>Shows the media chip for whichever tab is currently playing.</summary>
    private void RefreshMediaChip()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(RefreshMediaChip);
            return;
        }

        var playing = _tabs.FirstOrDefault(t => t.IsPlayingAudio && t.Kind == BrowserTabKind.Web);
        NavToolbar.MediaChip.Update(playing);

        if (playing is null)
        {
            StopMediaArtworkTimer();
            return;
        }

        // Keep polling while media plays so cover art follows track changes.
        EnsureMediaArtworkTimer();
        if (playing.MediaArtwork is null)
        {
            FireAndForget(ResolveMediaArtworkAsync(playing), "ResolveMediaArtworkAsync");
        }
    }

    private void ToggleMediaMute()
    {
        if (NavToolbar.MediaChip.Tab is not { } tab || tab.WebView?.CoreWebView2 is null)
        {
            return;
        }

        tab.IsMuted = !tab.IsMuted;
        tab.WebView.CoreWebView2.IsMuted = tab.IsMuted;
    }

    /// <summary>
    /// Clicks the page's own next/previous control. The Media Session API lets
    /// pages *receive* transport actions but has no way to invoke them, so known
    /// players are targeted by their stable hooks plus accessible labels.
    /// </summary>
    private const string MediaSkipScript =
        """
        (() => {
            const direction = '__SKIP__';
            const visible = (el) => el && !el.disabled && (el.offsetParent !== null || el.getClientRects().length > 0);
            const byLabel = (words) => {
                for (const w of words) {
                    const el = document.querySelector(
                        'button[aria-label*="' + w + '" i], [role="button"][aria-label*="' + w + '" i], [title*="' + w + '" i]');
                    if (visible(el)) return el;
                }
                return null;
            };
            try {
                let el;
                if (direction === 'next') {
                    el = document.querySelector('.ytp-next-button')
                      || document.querySelector('[data-testid="control-button-skip-forward"]')
                      || byLabel(['next track', 'next song', 'skip forward', 'next video', 'next']);
                } else {
                    el = document.querySelector('.ytp-prev-button')
                      || document.querySelector('[data-testid="control-button-skip-back"]')
                      || byLabel(['previous track', 'previous song', 'skip back', 'previous video', 'previous']);
                }
                if (visible(el)) { el.click(); return JSON.stringify({ ok: true, action: direction }); }
            } catch (e) { }
            return JSON.stringify({ ok: false, reason: 'no-control' });
        })();
        """;

    /// <summary>Reads the current media artwork URL from the page's Media Session.</summary>
    private const string MediaArtworkScript =
        """
        (() => {
            try {
                const meta = navigator.mediaSession && navigator.mediaSession.metadata;
                if (meta && meta.artwork && meta.artwork.length) {
                    let best = null, bestSize = -1;
                    for (const art of meta.artwork) {
                        if (!art || !art.src) continue;
                        const size = parseInt(art.sizes ? art.sizes : '0', 10) || 0;
                        if (size >= bestSize) { best = art; bestSize = size; }
                    }
                    if (best) return JSON.stringify({ art: best.src });
                }
                const video = document.querySelector('video');
                if (video && video.poster) return JSON.stringify({ art: video.poster });
            } catch (e) { }
            return JSON.stringify({ art: null });
        })();
        """;

    private static readonly HttpClient ArtworkHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
        MaxResponseContentBufferSize = MediaArtworkResolver.MaxArtworkBytes
    };

    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/131.0.0.0 Safari/537.36";

    private readonly Dictionary<string, ImageSource> _artworkCache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failedArtworkUrls = new(StringComparer.Ordinal);
    private string? _lastArtworkMissAddress;
    private DispatcherTimer? _mediaArtworkTimer;

    private void EnsureMediaArtworkTimer()
    {
        if (_mediaArtworkTimer is not null) return;

        _mediaArtworkTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _mediaArtworkTimer.Tick += (_, _) =>
        {
            if (NavToolbar.MediaChip.Tab is { } tab)
            {
                FireAndForget(ResolveMediaArtworkAsync(tab), "ResolveMediaArtworkAsync");
            }
        };
        _mediaArtworkTimer.Start();
    }

    private void StopMediaArtworkTimer()
    {
        _mediaArtworkTimer?.Stop();
        _mediaArtworkTimer = null;
    }

    /// <summary>
    /// Resolves the playing tab's cover art: evaluate the page metadata, fetch
    /// the (untrusted, validated) image once, cache it, and hand it to the chip.
    /// </summary>
    private async Task ResolveMediaArtworkAsync(BrowserTab tab)
    {
        var cwv = tab.WebView?.CoreWebView2;
        if (cwv is null) return;

        var address = tab.Address;
        try
        {
            var raw = await cwv.ExecuteScriptAsync(MediaArtworkScript);
            var url = ParseArtworkUrl(raw);
            if (url is null || !MediaArtworkResolver.IsAllowedArtworkUrl(url))
            {
                if (url is not null)
                {
                    Log.Debug("Media artwork URL rejected: {Url}", url);
                }
                else if (!string.Equals(_lastArtworkMissAddress, address, StringComparison.Ordinal))
                {
                    _lastArtworkMissAddress = address;
                    Log.Debug("No media artwork advertised by {Address}", address);
                }

                return;
            }

            ImageSource? image;
            lock (_artworkCache)
            {
                _artworkCache.TryGetValue(url, out image);
            }

            if (image is null)
            {
                lock (_failedArtworkUrls)
                {
                    if (_failedArtworkUrls.Contains(url)) return;
                }

                // Many artwork CDNs require a browser-like user agent and a
                // referrer (the page itself loads the same image).
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd(BrowserUserAgent);
                if (Uri.TryCreate(address, UriKind.Absolute, out var pageUri))
                {
                    request.Headers.Referrer = pageUri;
                }

                using var response = await ArtworkHttpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode)
                {
                    Log.Debug("Media artwork request failed ({Status}) for {Url}", (int)response.StatusCode, url);
                    MarkArtworkFailed(url);
                    return;
                }

                if (response.Content.Headers.ContentLength is > MediaArtworkResolver.MaxArtworkBytes)
                {
                    MarkArtworkFailed(url);
                    return;
                }

                await using var stream = await response.Content.ReadAsStreamAsync();
                image = FaviconImageDecoder.Decode(stream);
                if (image is null)
                {
                    Log.Debug("Media artwork could not be decoded: {Url}", url);
                    MarkArtworkFailed(url);
                    return;
                }

                Log.Debug("Media artwork loaded: {Url}", url);

                lock (_artworkCache)
                {
                    if (_artworkCache.Count >= 32)
                    {
                        _artworkCache.Clear();
                    }

                    _artworkCache[url] = image;
                }
            }

            // The tab may have navigated while the image was downloading.
            if (!string.Equals(tab.Address, address, StringComparison.Ordinal))
            {
                return;
            }

            tab.MediaArtworkUrl = url;
            tab.MediaArtwork = image;

            if (ReferenceEquals(NavToolbar.MediaChip.Tab, tab))
            {
                NavToolbar.MediaChip.SetArtwork(image);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Media artwork resolution failed");
        }
    }

    private void MarkArtworkFailed(string url)
    {
        lock (_failedArtworkUrls)
        {
            if (_failedArtworkUrls.Count >= 64)
            {
                _failedArtworkUrls.Clear();
            }

            _failedArtworkUrls.Add(url);
        }
    }

    private static string? ParseArtworkUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // ExecuteScriptAsync returns the JSON encoding of the script's value,
        // and the script returns JSON.stringify(...) - so decode twice.
        var inner = raw;
        try { inner = JsonSerializer.Deserialize<string>(raw) ?? raw; }
        catch (JsonException) { /* not a JSON string; try the raw text */ }

        try
        {
            using var document = JsonDocument.Parse(inner);
            if (document.RootElement.TryGetProperty("art", out var art) && art.ValueKind == JsonValueKind.String)
            {
                var value = art.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }
        catch (JsonException) { }

        return null;
    }

    /// <summary>Captures the visible page area to a PNG in the Downloads folder.</summary>
    private async Task CaptureScreenshotAsync()
    {
        var tab = _activeTab;
        var cwv = tab?.WebView?.CoreWebView2;
        if (tab is null || cwv is null)
        {
            SetStatusTextSafe("Open a web page before capturing.");
            return;
        }

        try
        {
            var directory = GetCaptureDirectory();
            Directory.CreateDirectory(directory);
            var fileName = $"Aerio capture {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png";
            var path = Path.Combine(directory, fileName);

            await using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await cwv.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            }

            _dataStore.AddDownload(fileName, path, tab.Address);

            var shelfItem = new DownloadShelfItem(fileName, tab.Address, null, path);
            shelfItem.Complete(path);
            DownloadShelf.Add(shelfItem);

            SetStatusTextSafe($"Screenshot saved: {fileName}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Screenshot capture failed");
            SetStatusTextSafe("Couldn't capture the page.");
        }
    }

    private static string GetCaptureDirectory()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(profile, "Downloads");

        // Only keep it in Downloads when the user actually has one; otherwise
        // fall back to the profile directory rather than creating a stray path.
        return Directory.Exists(downloads) ? downloads : profile;
    }

    private void OpenShelfItem(DownloadShelfItem item)
    {
        if (item.FilePath is not { Length: > 0 } path || !File.Exists(path))
        {
            SetStatusTextSafe("That file is no longer on disk.");
            return;
        }

        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to open downloaded file");
            SetStatusTextSafe("Couldn't open the file.");
        }
    }

    private void RevealShelfItem(DownloadShelfItem item)
    {
        var folder = item.FilePath is { Length: > 0 } path ? Path.GetDirectoryName(path) : null;
        if (!string.IsNullOrEmpty(folder))
        {
            OpenDownloadFolder(folder);
        }
    }

    private void CancelShelfItem(DownloadShelfItem item)
    {
        if (_downloadOperations.TryGetValue(item.Id, out var operation))
        {
            try { operation.Cancel(); }
            catch (Exception ex) { Log.Debug(ex, "Failed to cancel download"); }
        }
    }

    private static (bool Ok, string? Reason, string? Action) ParseScriptResult(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (false, null, null);
        }

        // ExecuteScriptAsync returns the JSON encoding of the script's value,
        // and these scripts return JSON.stringify(...) - so decode twice.
        var inner = raw;
        try { inner = JsonSerializer.Deserialize<string>(raw) ?? raw; }
        catch (JsonException) { /* not a JSON string; try the raw text */ }

        try
        {
            using var document = JsonDocument.Parse(inner);
            var root = document.RootElement;
            var ok = root.TryGetProperty("ok", out var okProp) && okProp.ValueKind == JsonValueKind.True;
            var reason = root.TryGetProperty("reason", out var reasonProp) && reasonProp.ValueKind == JsonValueKind.String
                ? reasonProp.GetString()
                : null;
            var action = root.TryGetProperty("action", out var actionProp) && actionProp.ValueKind == JsonValueKind.String
                ? actionProp.GetString()
                : null;
            return (ok, reason, action);
        }
        catch (JsonException)
        {
            return (false, null, null);
        }
    }
}
