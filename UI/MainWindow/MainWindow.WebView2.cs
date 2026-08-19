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
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace TabloidBrowser;

public partial class MainWindow
{
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

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
        public int lParam;
    }

    private const int ABM_GETAUTOHIDEBAR = 7;
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

    private const uint WM_SYSCOMMAND = 0x0112;
    private const uint SC_MINIMIZE = 0xF020;
    private const uint SC_MAXIMIZE = 0xF030;
    private const uint SC_RESTORE = 0xF120;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint DWMWA_SNAP_LAYOUT = 0x100;
    private const uint WM_DPICHANGED = 0x02E0;

    private readonly HttpClient _crxHttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    private CoreWebView2Environment? _environment;
    private volatile CoreWebView2Profile? _profile;
    private bool _extensionsLoaded;
    private readonly Dictionary<string, CoreWebView2BrowserExtension> _loadedExtensions = [];
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
        BrowserPane.LoadingOverlay.Visibility = Visibility.Collapsed;

        var result = MessageBox.Show(
            $"Tabloid Browser could not start.\n\n{exception.Message}\n\nOpen the WebView2 download page?",
            "Tabloid Browser - WebView2 Required",
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

    private async Task EnsureWebViewAsync(BrowserTab tab, bool attachBeforeInitialize = false)
    {
        if (tab.WebView?.CoreWebView2 is not null && tab.IsWebViewInitialized)
        {
            return;
        }

        if (_environment is null)
        {
            throw new InvalidOperationException("WebView2 environment was not initialized.");
        }

        // Prevent duplicate initialization BEFORE creating a WebView2
        lock (_initializationLock)
        {
            if (tab.IsWebViewInitialized)
            {
                return;
            }
            if (!_initializingTabs.Add(tab))
            {
                return; // Another call is already initializing this tab
            }
        }

        var webView = tab.WebView;
        if (webView is null)
        {
            webView = new WebView2
            {
                DefaultBackgroundColor = GetWebViewBackgroundColor()
            };
            tab.WebView = webView;
        }

        try
        {
            if (attachBeforeInitialize && _activeTab == tab)
            {
                AttachActiveWebView(tab);
            }

            await webView.EnsureCoreWebView2Async(_environment);

            if (tab.IsWebViewInitialized)
            {
                return;
            }

            var cwv = webView.CoreWebView2;
            _webViewToTabMap[cwv] = tab;

            cwv.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            cwv.WebResourceRequested += OnWebResourceRequested;
            cwv.WebMessageReceived += OnWebMessageReceived;
            cwv.Find.ActiveMatchIndexChanged += Find_MatchChanged;
            cwv.Find.MatchCountChanged += Find_MatchChanged;
            cwv.DownloadStarting += WebView_DownloadStarting;
            cwv.ContextMenuRequested += OnContextMenuRequested;

            await cwv.AddScriptToExecuteOnDocumentCreatedAsync(ScrollTrackingScript);
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
                _webViewToTabMap.TryRemove(cwv, out _);
            });

            var settings = cwv.Settings;
            settings.AreDefaultScriptDialogsEnabled = true;
            settings.AreDevToolsEnabled = _dataStore.Settings.EnableDevTools;
            settings.IsGeneralAutofillEnabled = _dataStore.Settings.EnableAutofill;
            settings.IsPasswordAutosaveEnabled = _dataStore.Settings.EnablePasswordSave;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            settings.IsStatusBarEnabled = false;

            tab.IsWebViewInitialized = true;
        }
        finally
        {
            lock (_initializationLock) _initializingTabs.Remove(tab);
        }
        }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (sender is not CoreWebView2 cwv || !_webViewToTabMap.TryGetValue(cwv, out var tab)) return;
        
        var settings = _dataStore.Settings;
        var host = GetHost(tab.Address);
        var reqHost = GetHost(e.Request.Uri);
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(reqHost))
        {
            return;
        }

        if (settings.TrackerAllowlist.Count > 0 && settings.TrackerAllowlist.Any(domain => DomainMatches(host, domain)))
        {
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
        if (blocklist.Count == 0 || !blocklist.Any(domain => DomainMatches(reqHost, domain)))
        {
            return;
        }

        BlockWebRequest(cwv, e);
    }

    private void BlockWebRequest(CoreWebView2 cwv, CoreWebView2WebResourceRequestedEventArgs e)
    {
        Interlocked.Increment(ref _blockedTrackerCount);
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
            var extId = e.Uri["tabloid://install-crx/".Length..];
            if (!string.IsNullOrWhiteSpace(extId))
            {
                FireAndForget(DownloadAndInstallCrxAsync(extId), "DownloadAndInstallCrxAsync");
            }
            return;
        }

        if (TryHandleTabloidUri(tab, e.Uri))
        {
            e.Cancel = true;
            return;
        }

        void handler()
        {
            tab.Kind = BrowserTabKind.Web;
            tab.Address = e.Uri;
            tab.Title = "Loading...";
            tab.IsLoading = true;
            tab.HasAudio = false;
            tab.IsPlayingAudio = false;
            tab.IsMuted = false;

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
            var extId = e.Uri["tabloid://install-crx/".Length..];
            if (!string.IsNullOrWhiteSpace(extId))
            {
                FireAndForget(DownloadAndInstallCrxAsync(extId), "DownloadAndInstallCrxAsync");
            }
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

            if (e.IsSuccess)
            {
                var title = cwv.DocumentTitle;
                tab.Title = string.IsNullOrWhiteSpace(title) ? TrimUrl(tab.Address) : title;

                if (!_isPrivate && BrowserDataStore.IsHttpUrl(tab.Address))
                {
                    _dataStore.AddHistory(tab.Title, tab.Address);
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
                StatusBar.StatusText.Text = e.IsSuccess ? "Ready" : StatusBar.StatusText.Text;
                UpdateNavigationButtons();
                UpdateBookmarkButton();
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
            }
            catch (Exception ex) { Log.Error(ex, "Error occurred"); }
        }
        if (Dispatcher.CheckAccess()) handler();
        else Dispatcher.Invoke(handler);
    }
    private async void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
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
            }
            catch (Exception ex) { Log.Error(ex, "Error occurred"); }
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
    private static bool IsTrackerAllowedForSite(BrowserTab tab, List<string> allowlist)
    {
        var host = GetHost(tab.Address);
        return !string.IsNullOrWhiteSpace(host) &&
            allowlist.Any(domain => DomainMatches(host, domain));
    }

    private static bool IsTrackerRequest(string uri, List<string> blocklist)
    {
        var host = GetHost(uri);
        return !string.IsNullOrWhiteSpace(host) &&
            blocklist.Any(domain => DomainMatches(host, domain));
    }

    private static string? GetHost(string? uri)
    {
        return Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ? parsed.Host : null;
    }

    private static bool DomainMatches(string host, string domain)
    {
        domain = domain.TrimStart('.');
        if (string.Equals(host, domain, StringComparison.OrdinalIgnoreCase))
            return true;
        if (host.EndsWith($".{domain}", StringComparison.OrdinalIgnoreCase))
            return true;
        if (domain.Contains('*'))
        {
            var pattern = "^" + Regex.Escape(domain).Replace("\\*", ".*") + "$";
            return Regex.IsMatch(host, pattern, RegexOptions.IgnoreCase);
        }
        return false;
    }

    private static readonly HashSet<string> DangerousExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".bat", ".cmd", ".ps1", ".vbs", ".scr", ".jar", ".msix", ".appx"
    };

    private void WebView_DownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs args)
    {
        try
        {
            var url = args.DownloadOperation.Uri;
            var ext = Path.GetExtension(new Uri(url).LocalPath);

            if (DangerousExtensions.Contains(ext))
            {
                var fileName = Path.GetFileName(new Uri(url).LocalPath);
                var result = MessageBox.Show(
                    $"Download \"{fileName}\"?\n\nThis file type ({ext}) can harm your computer.",
                    "Confirm Download",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes)
                {
                    args.Cancel = true;
                    return;
                }
            }

            if (!url.Contains(".crx", StringComparison.OrdinalIgnoreCase) &&
                !url.Contains("clients2.google.com/service/update2/crx", StringComparison.OrdinalIgnoreCase))
            {
                TrackDownload(args);
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
            var url = $"https://clients2.google.com/service/update2/crx?response=redirect&prodversion=106.0.0.0&acceptformat=crx3&x=id%3D{extensionId}%26installsource%3Dondemand%26uc";

            var tempDir = Path.Combine(Path.GetTempPath(), "TabloidBrowser", "CrxDownloads");
            Directory.CreateDirectory(tempDir);
            var crxPath = Path.Combine(tempDir, $"{extensionId}.crx");

            using var response = await _crxHttpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            await using (var fs = new FileStream(crxPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await response.Content.CopyToAsync(fs);
            }

            await InstallCrxAsync(crxPath);
            try { File.Delete(crxPath); } catch (Exception ex) { Log.Debug(ex, "Failed to delete CRX temp file"); }
        }
        catch (Exception ex)
        {
            SetStatusTextSafe($"Failed to download extension: {ex.Message}");
        }
    }

    private void UpdatePrivacyIndicator()
    {
        NavToolbar.PrivacyButton.Foreground = _dataStore.Settings.BlockTrackers ? GetBrush("Lime") : GetBrush("Muted");
        NavToolbar.PrivacyButton.ToolTip = _dataStore.Settings.BlockTrackers || _dataStore.Settings.EnableAdBlock
            ? $"Ad & tracker blocking on. Blocked {_blockedTrackerCount} requests."
            : "Ad & tracker blocking off";
    }

    private void TrackDownload(CoreWebView2DownloadStartingEventArgs args)
    {
        var fileName = args.ResultFilePath is not null ? Path.GetFileName(args.ResultFilePath) : "unknown";
        var resultFilePath = args.ResultFilePath ?? "";
        var operation = args.DownloadOperation;
        var sourceUrl = operation.Uri;
        Dispatcher.BeginInvoke(() => StatusBar.StatusText.Text = $"Downloading {fileName}");

        operation.StateChanged += OnDownloadCompleted;
        void OnDownloadCompleted(object? _, object? __)
        {
            if (operation.State != CoreWebView2DownloadState.Completed)
            {
                return;
            }

            operation.StateChanged -= OnDownloadCompleted;
            Dispatcher.BeginInvoke(() =>
            {
                _dataStore.AddDownload(fileName, resultFilePath, sourceUrl);
                StatusBar.StatusText.Text = $"Downloaded {fileName}";
            });
        }
    }
}





