using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;

namespace TabloidBrowser;

public partial class MainWindow
{
    private readonly ObservableCollection<BrowserTab> _tabs = [];
    private BrowserTab? _activeTab;
    private readonly SemaphoreSlim _tabOperationLock = new(1, 1);
    private Point _dragStartPoint;
    private bool _dragReady;
    private int _lastInactiveCount = -1;

    private Point _tabDragStartPoint;
    private int _tabDragFromIndex = -1;
    private int _tabDropInsertIndex = -1;
    private bool _tabIsDragging;
    private bool _tabDragDetaching;
    private BrowserTab? _dragTab;
    private Window? _ghostWindow;
    private AnimatedTabPanel? _tabPanel;
    private const double TabDragThreshold = 6.0;

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Win32Point lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Point
    {
        public int X;
        public int Y;
    }

    private BrowserTab? _splitPrimaryTab;
    private BrowserTab? _splitSecondaryTab;

    private async Task RestoreSessionOrOpenStartAsync()
    {
        if (_isPrivate || _skipSessionPersistence)
        {
            await AddStartTabAsync();
            return;
        }

        var session = _dataStore.LoadSession();
        var restored = session.Tabs
            .Where(tab => BrowserDataStore.IsHttpUrl(tab.Url))
            .ToList();

        if (restored.Count == 0)
        {
            await AddStartTabAsync();
            return;
        }

        var activeIndex = Math.Clamp(session.ActiveIndex, 0, restored.Count - 1);
        BrowserTab? activeTab = null;

        await _tabOperationLock.WaitAsync();
        try
        {
            for (var i = 0; i < restored.Count; i++)
            {
                var sessionTab = restored[i];
                var tab = new BrowserTab
                {
                    Kind = BrowserTabKind.Web,
                    Title = string.IsNullOrWhiteSpace(sessionTab.Title) ? TrimUrl(sessionTab.Url) : sessionTab.Title,
                    Address = sessionTab.Url,
                    RestoredUrl = sessionTab.Url,
                    ScrollX = Math.Max(0, sessionTab.ScrollX),
                    ScrollY = Math.Max(0, sessionTab.ScrollY),
                    IsRestoredPlaceholder = true,
                    AiChatHistory = sessionTab.AiChatHistory
                };

                _tabs.Add(tab);

                if (i == activeIndex)
                {
                    activeTab = tab;
                }
            }

            UpdateMemoryStatus();
        }
        finally { _tabOperationLock.Release(); }

        if (activeTab is not null)
        {
            await ActivateTabAsync(activeTab);
        }
    }

    private async Task AddStartTabAsync()
    {
        BrowserTab tab;
        await _tabOperationLock.WaitAsync();
        try
        {
            tab = new BrowserTab
            {
                Kind = BrowserTabKind.Start,
                Title = "New tab",
                Address = string.Empty
            };

            _tabs.Add(tab);
        }
        finally { _tabOperationLock.Release(); }

        await ActivateTabAsync(tab);
    }

    private async Task AddWebTabAsync(string url)
    {
        BrowserTab tab;
        await _tabOperationLock.WaitAsync();
        try
        {
            tab = new BrowserTab
            {
                Kind = BrowserTabKind.Web,
                Title = "New tab",
                Address = string.Empty
            };

            _tabs.Add(tab);
        }
        finally { _tabOperationLock.Release(); }

        await ActivateTabAsync(tab);
        await NavigateWebAsync(tab, url);
    }

    private async Task AddNativePageTabAsync(BrowserTabKind kind)
    {
        BrowserTab tab;
        await _tabOperationLock.WaitAsync();
        try
        {
            tab = new BrowserTab
            {
                Kind = kind,
                Title = GetNativePageTitle(kind),
                Address = GetNativePageAddress(kind)
            };

            _tabs.Add(tab);
        }
        finally { _tabOperationLock.Release(); }

        await ActivateTabAsync(tab);
    }

    private bool IsSplitActive =>
        _splitPrimaryTab is not null &&
        _splitSecondaryTab is not null &&
        _tabs.Contains(_splitPrimaryTab) &&
        _tabs.Contains(_splitSecondaryTab) &&
        _splitPrimaryTab.Kind == BrowserTabKind.Web &&
        _splitSecondaryTab.Kind == BrowserTabKind.Web;

    private bool IsSplitTab(BrowserTab? tab) =>
        tab is not null && (ReferenceEquals(tab, _splitPrimaryTab) || ReferenceEquals(tab, _splitSecondaryTab));

    private static void RemoveElementFromParent(UIElement element)
    {
        var parent = VisualTreeHelper.GetParent(element);
        if (parent is Panel panel)
        {
            panel.Children.Remove(element);
        }
        else if (parent is Decorator decorator)
        {
            decorator.Child = null;
        }
        else if (parent is ContentControl contentControl)
        {
            contentControl.Content = null;
        }
    }

    private void ClearSplitView(bool clearState = true)
    {
        BrowserPane.SplitWebViewHost.Children.Clear();
        BrowserPane.SplitLoadingOverlay.Visibility = Visibility.Collapsed;
        BrowserPane.SplitPane.Visibility = Visibility.Collapsed;
        BrowserPane.SplitPaneSplitter.Visibility = Visibility.Collapsed;
        BrowserPane.SplitPaneDividerColumn.Width = new GridLength(0);
        BrowserPane.SplitPaneColumn.Width = new GridLength(0);
        BrowserPane.PrimaryPaneColumn.Width = new GridLength(1, GridUnitType.Star);

        if (clearState)
        {
            _splitPrimaryTab = null;
            _splitSecondaryTab = null;
        }
    }

    private async Task<bool> AttachSplitViewAsync(BrowserTab focusedTab)
    {
        if (!IsSplitActive || _splitPrimaryTab is null || _splitSecondaryTab is null)
        {
            ClearSplitView();
            return false;
        }

        var primary = _splitPrimaryTab;
        var secondary = _splitSecondaryTab;

        await EnsureWebViewAsync(primary);
        await EnsureWebViewAsync(secondary);

        if (primary.WebView is null || secondary.WebView is null)
        {
            ClearSplitView();
            return false;
        }

        BrowserPane.WebViewHost.Children.Clear();
        _attachedWebView = null;
        BrowserPane.SplitWebViewHost.Children.Clear();
        BrowserPane.PlaySwapScrim();

        RemoveElementFromParent(primary.WebView);
        RemoveElementFromParent(secondary.WebView);
        BrowserPane.WebViewHost.Children.Add(primary.WebView);
        BrowserPane.SplitWebViewHost.Children.Add(secondary.WebView);

        primary.WebView.Visibility = Visibility.Visible;
        secondary.WebView.Visibility = Visibility.Visible;

        try { primary.WebView.CoreWebView2?.Resume(); }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { Log.Error(ex, "Error occurred"); }
        try { secondary.WebView.CoreWebView2?.Resume(); }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { Log.Error(ex, "Error occurred"); }

        BrowserPane.PrimaryPaneColumn.Width = new GridLength(1, GridUnitType.Star);
        BrowserPane.SplitPaneDividerColumn.Width = new GridLength(5);
        BrowserPane.SplitPaneColumn.Width = new GridLength(1, GridUnitType.Star);
        BrowserPane.SplitPaneSplitter.Visibility = Visibility.Visible;
        BrowserPane.SplitPane.Visibility = Visibility.Visible;

        BrowserPane.WebViewHost.Visibility = Visibility.Visible;
        BrowserPane.StartOverlay.Visibility = Visibility.Collapsed;
        BrowserPane.InternalPageHost.Visibility = Visibility.Collapsed;
        BrowserPane.LoadingOverlay.Visibility = primary.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        BrowserPane.SplitLoadingOverlay.Visibility = secondary.IsLoading ? Visibility.Visible : Visibility.Collapsed;

        var focusedWebView = ReferenceEquals(focusedTab, secondary) ? secondary.WebView : primary.WebView;
        _ = Dispatcher.BeginInvoke(() => focusedWebView.Focus(), DispatcherPriority.ApplicationIdle);
        return true;
    }

    private async Task ActivateTabAsync(BrowserTab tab)
    {
        await _tabOperationLock.WaitAsync();
        try
        {
            if (!_tabs.Contains(tab))
            {
                return;
            }

            var prevTab = _activeTab;
            if (prevTab is not null && prevTab != tab)
            {
                FireAndForget(CaptureScrollPositionAsync(prevTab), "CaptureScrollPositionAsync");
                prevTab.IsActive = false;
            }

            HideFindBar();
            _activeTab = tab;

            tab.IsActive = true;
            tab.IsSleeping = false;
            tab.LastActiveUtc = DateTimeOffset.UtcNow;
            RememberSitePermissionsUrl(tab);

            if (tab.Kind == BrowserTabKind.Web)
            {
                if (IsSplitActive && IsSplitTab(tab))
                {
                    await AttachSplitViewAsync(tab);
                }
                else
                {
                    ClearSplitView();
                    await EnsureWebViewAsync(tab, attachBeforeInitialize: true);
                    AttachActiveWebView(tab);
                }

                if (tab.IsRestoredPlaceholder && !string.IsNullOrWhiteSpace(tab.RestoredUrl))
                {
                    var restoredUrl = tab.RestoredUrl;
                    tab.RestoredUrl = null;
                    tab.IsRestoredPlaceholder = false;
                    await NavigateWebAsync(tab, restoredUrl, restoreScroll: true);
                }

            }
            else
            {
                if (IsSplitTab(tab))
                {
                    ClearSplitView();
                }
                RenderNativeSurface(tab);
            }

            UpdateAddressBar();
            UpdateNavigationButtons();
            UpdateBookmarkButton();
            UpdateMemoryStatus();
            UpdateReaderUi(tab.IsReaderMode);
            UpdateReloadStopButton();
            UpdateZoomDisplay();
        }
        finally { _tabOperationLock.Release(); }
    }

    private WebView2? _attachedWebView;

    private void AttachActiveWebView(BrowserTab tab)
    {
        var webView = tab.WebView;
        if (webView is null)
        {
            return;
        }

        var wasSuspended = tab.SuspendedForSleep;
        tab.SuspendedForSleep = false;
        tab.SuspendedAtUtc = null;

        if (!ReferenceEquals(_attachedWebView, webView))
        {
            // Detach the previously active WebView so stacked WebViews don't
            // accumulate in the host while switching between web tabs.
            if (_attachedWebView is { } previous &&
                ReferenceEquals(previous.Parent, BrowserPane.WebViewHost))
            {
                BrowserPane.WebViewHost.Children.Remove(previous);
            }
            _attachedWebView = webView;
            BrowserPane.PlaySwapScrim();
        }

        if (BrowserPane.WebViewHost.Children.Count != 1 ||
            !ReferenceEquals(BrowserPane.WebViewHost.Children[0], webView))
        {
            RemoveElementFromParent(webView);
            BrowserPane.WebViewHost.Children.Add(webView);
        }
        webView.Visibility = Visibility.Visible;

        try
        {
            webView.CoreWebView2?.Resume();
        }
        catch (ObjectDisposedException ex)
        {
            Log.Error(ex, "Error occurred");
            RemoveElementFromParent(webView);
            tab.WebView = null;
            tab.IsWebViewInitialized = false;
            return;
        }
        catch (InvalidOperationException ex)
        {
            Log.Error(ex, "Error occurred");
        }

        BrowserPane.WebViewHost.Visibility = Visibility.Visible;
        BrowserPane.StartOverlay.Visibility = Visibility.Collapsed;
        BrowserPane.InternalPageHost.Visibility = Visibility.Collapsed;
        BrowserPane.LoadingOverlay.Visibility = tab.IsLoading ? Visibility.Visible : Visibility.Collapsed;

        Dispatcher.BeginInvoke(() => webView.Focus(), DispatcherPriority.ApplicationIdle);

        if (!wasSuspended && !tab.IsRestoredPlaceholder && !tab.IsLoading && HasScrollPosition(tab))
        {
            FireAndForget(RestoreScrollPositionAsync(tab, force: true), "RestoreScrollPositionAsync force");
        }
    }

    private async Task CaptureScrollPositionAsync(BrowserTab? tab)
    {
        if (tab?.Kind != BrowserTabKind.Web || tab.WebView is null)
        {
            return;
        }

        try
        {
            var coreWebView = tab.WebView.CoreWebView2;
            if (coreWebView is null)
            {
                return;
            }

            var json = await coreWebView.ExecuteScriptAsync(ScrollSnapshotScript);
            if (json is null) return;
            using var document = JsonDocument.Parse(json);
            if (TryGetJsonDouble(document.RootElement, "X", out var x) &&
                TryGetJsonDouble(document.RootElement, "Y", out var y))
            {
                tab.ScrollX = Math.Max(0, x);
                tab.ScrollY = Math.Max(0, y);
            }
        }
        catch (ObjectDisposedException ex)
        {
            Log.Error(ex, "Error occurred");
        }
        catch (InvalidOperationException ex)
        {
            Log.Error(ex, "Error occurred");
        }
        catch (COMException ex)
        {
            Log.Error(ex, "Error occurred");
        }
        catch (JsonException ex)
        {
            Log.Error(ex, "Error occurred");
        }
    }

    private async Task RestoreScrollPositionAsync(BrowserTab tab, bool force = false)
    {
        if ((!force && !tab.PendingScrollRestore) || !HasScrollPosition(tab) || tab.WebView is null)
        {
            return;
        }

        var webView = tab.WebView;
        var x = tab.ScrollX;
        var y = tab.ScrollY;
        var script = $"window.scrollTo({FormatScriptNumber(x)}, {FormatScriptNumber(y)});";

        for (var attempt = 0; attempt < 6; attempt++)
        {
            await Task.Delay(attempt == 0 ? 80 : 180);

            try
            {
                if (tab.WebView != webView)
                {
                    return;
                }

                var coreWebView = webView.CoreWebView2;
                if (coreWebView is null)
                {
                    return;
                }

                await coreWebView.ExecuteScriptAsync(script);
            }
            catch (ObjectDisposedException ex)
            {
                Log.Error(ex, "Error occurred");
                return;
            }
            catch (InvalidOperationException ex)
            {
                Log.Error(ex, "Error occurred");
                return;
            }
            catch (COMException ex)
            {
                Log.Error(ex, "Error occurred");
                return;
            }
        }

        tab.PendingScrollRestore = false;
    }

    private static bool HasScrollPosition(BrowserTab tab)
    {
        return tab.ScrollX >= 1.0 || tab.ScrollY >= 1.0;
    }

    private async void SleepTimer_Tick(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted) { _sleepTimer.Stop(); return; }
        try
        {
            var active = _activeTab;
            var sleepMinutes = _dataStore.Settings.TabSleepMinutes;
            List<BrowserTab> snapshot;

            await _tabOperationLock.WaitAsync();
            try
            {
                snapshot = _tabs.Where(tab =>
                    tab != active &&
                    tab.Kind == BrowserTabKind.Web &&
                    !tab.IsSleeping &&
                    !(tab.IsAiOwned && tab.AiState != AiTaskState.Idle) &&
                    !(IsSplitActive && IsSplitTab(tab))).ToList();
            }
            finally { _tabOperationLock.Release(); }

            foreach (var tab in snapshot)
            {
                try
                {
                    if (DateTimeOffset.UtcNow - tab.LastActiveUtc < TimeSpan.FromMinutes(sleepMinutes))
                        continue;

                    await HibernateTabAsync(tab);
                }
                catch (Exception ex) { Log.Error(ex, "Error occurred"); }
            }

            List<BrowserTab> suspendedSnapshot;
            await _tabOperationLock.WaitAsync();
            try
            {
                suspendedSnapshot = _tabs.Where(tab => tab.IsSleeping && tab.SuspendedForSleep).ToList();
            }
            finally { _tabOperationLock.Release(); }

            foreach (var tab in suspendedSnapshot)
            {
                try
                {
                    var suspendedAt = tab.SuspendedAtUtc;
                    if (suspendedAt is null || DateTimeOffset.UtcNow - suspendedAt < DiscardSuspendedAfter)
                        continue;

                    await DiscardSuspendedTabAsync(tab);
                }
                catch (Exception ex) { Log.Error(ex, "Error occurred"); }
            }
        }
        catch (Exception ex) { Log.Error(ex, "SleepTimer_Tick fatal"); }
    }

    private async Task HibernateTabAsync(BrowserTab tab)
    {
        await _tabOperationLock.WaitAsync();
        try
        {
            if (tab == _activeTab || tab.Kind != BrowserTabKind.Web || !_tabs.Contains(tab))
            {
                return;
            }

            if (tab.IsPlayingAudio)
            {
                return;
            }

            // Don't hibernate a tab an AI task is actively using, or either
            // half of an active split view.
            if (tab.IsAiOwned && tab.AiState != AiTaskState.Idle)
            {
                return;
            }

            if (IsSplitActive && IsSplitTab(tab))
            {
                return;
            }

            var hibernateUrl = tab.RestoredUrl ?? tab.Address;
            if (!BrowserDataStore.IsHttpUrl(hibernateUrl))
            {
                return;
            }

            if (tab.WebView is null)
            {
                tab.RestoredUrl = hibernateUrl;
                tab.IsRestoredPlaceholder = true;
                tab.IsSleeping = true;
                UpdateMemoryStatus();
                return;
            }

            var webView = tab.WebView;

            try
            {
                await CaptureScrollPositionAsync(tab);

            }
            catch (InvalidOperationException ex)
            {
                Log.Error(ex, "Error occurred");
            }
            catch (COMException ex)
            {
                Log.Error(ex, "Error occurred");
            }

            if (tab == _activeTab || tab.Kind != BrowserTabKind.Web || tab.IsPlayingAudio)
            {
                return;
            }

            if (await TrySuspendTabAsync(tab, webView))
            {
                return;
            }

            DisposeTabWebView(tab, webView, hibernateUrl);
        }
        finally { _tabOperationLock.Release(); }
    }

    private void DisposeTabWebView(BrowserTab tab, WebView2 webView, string hibernateUrl)
    {
        RemoveElementFromParent(webView);
        if (ReferenceEquals(_attachedWebView, webView)) _attachedWebView = null;
        tab.WebView = null;
        tab.IsWebViewInitialized = false;
        tab.RestoredUrl = hibernateUrl;
        tab.IsRestoredPlaceholder = true;
        tab.IsSleeping = true;
        tab.SuspendedForSleep = false;
        tab.SuspendedAtUtc = null;
        tab.RunCleanup();
        webView.Dispose();

        if (!_tabs.Any(t => t.WebView?.CoreWebView2 is not null))
        {
            lock (_initializationLock) _profile = null;
            _extensionsLoaded = false;
        }

        UpdateMemoryStatus();
    }

    private static readonly TimeSpan DiscardSuspendedAfter = TimeSpan.FromMinutes(30);

    private async Task DiscardSuspendedTabAsync(BrowserTab tab)
    {
        await _tabOperationLock.WaitAsync();
        try
        {
            if (tab == _activeTab || tab.Kind != BrowserTabKind.Web || !_tabs.Contains(tab) ||
                !tab.IsSleeping || !tab.SuspendedForSleep)
            {
                return;
            }

            var webView = tab.WebView;
            if (webView is null || webView.CoreWebView2 is null)
            {
                return;
            }

            var hibernateUrl = tab.RestoredUrl ?? tab.Address;
            if (!BrowserDataStore.IsHttpUrl(hibernateUrl))
            {
                return;
            }

            DisposeTabWebView(tab, webView, hibernateUrl);
        }
        finally { _tabOperationLock.Release(); }
    }

    private async Task<bool> TrySuspendTabAsync(BrowserTab tab, WebView2 webView)
    {
        try
        {
            var coreWebView = webView.CoreWebView2;
            if (coreWebView is null || coreWebView.IsSuspended)
            {
                return false;
            }

            RemoveElementFromParent(webView);
            if (ReferenceEquals(_attachedWebView, webView)) _attachedWebView = null;

            if (await coreWebView.TrySuspendAsync())
            {
                tab.IsSleeping = true;
                tab.SuspendedForSleep = true;
                tab.SuspendedAtUtc = DateTimeOffset.UtcNow;
                tab.RestoredUrl = null;
                tab.IsRestoredPlaceholder = false;
                UpdateMemoryStatus();
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Log.Debug("Tab suspend failed: {Message}", ex.Message);
            return false;
        }
    }

    private bool _memoryUpdatePending;

    private async Task CloseTabAsync(BrowserTab tab)
    {
        int index;
        bool wasActive;
        BrowserTab? nextTab = null;
        await _tabOperationLock.WaitAsync();
        try
        {
            index = _tabs.IndexOf(tab);
            if (index < 0)
            {
                return;
            }

            wasActive = _activeTab == tab;
            if (wasActive)
            {
                _activeTab = null;
                tab.IsActive = false;
            }

            if (_isPermissionsPopupOpen)
            {
                if (_sitePermissionsPopup is not null) _sitePermissionsPopup.IsOpen = false;
            }

            _tabs.Remove(tab);

            if (tab.IsAiOwned)
            {
                // Keep the AI workspace panel and the model's workspace context
                // in sync when the user closes an AI-owned tab mid-task.
                var aiContext = _currentAiContext;
                if (aiContext is not null && aiContext.Workspace.TabIds.Contains(tab.Id))
                {
                    aiContext.Workspace.RemoveTab(tab.Id);
                    _ = Dispatcher.BeginInvoke(RefreshWorkspaceUi);
                }
            }

            if (tab.WebView is not null)
            {
                tab.RunCleanup();
                RemoveElementFromParent(tab.WebView);
                if (ReferenceEquals(_attachedWebView, tab.WebView)) _attachedWebView = null;
                tab.WebView.Dispose();
                tab.WebView = null;
                tab.IsWebViewInitialized = false;

                if (!_tabs.Any(t => t.WebView?.CoreWebView2 is not null))
                {
                    lock (_initializationLock) _profile = null;
                    _extensionsLoaded = false;
                }
            }

            if (IsSplitActive && (ReferenceEquals(tab, _splitPrimaryTab) || ReferenceEquals(tab, _splitSecondaryTab)))
            {
                ClearSplitView();
            }

            if (_tabs.Count == 0)
            {
                FireAndForget(AddStartTabAsync(), "AddStartTabAsync");
                return;
            }

            if (wasActive)
            {
                nextTab = _tabs[Math.Clamp(index - 1, 0, _tabs.Count - 1)];
            }

            UpdateMemoryStatus();
        }
        finally { _tabOperationLock.Release(); }

        if (wasActive && nextTab is not null)
        {
            await ActivateTabAsync(nextTab);
        }
    }

    private async Task SaveCurrentSessionAsync()
    {
        if (_skipSessionPersistence) return;

        await _tabOperationLock.WaitAsync();
        try
        {
            var activeUrl = _activeTab?.RestoredUrl ?? _activeTab?.Address;
            var sessionTabs = new List<SessionTab>();

            foreach (var tab in _tabs.Where(tab => tab.Kind == BrowserTabKind.Web))
            {
                var url = tab.RestoredUrl ?? tab.Address;
                if (!BrowserDataStore.IsHttpUrl(url))
                {
                    continue;
                }

                sessionTabs.Add(new SessionTab(
                    tab.Title.Replace(" (sleep)", string.Empty, StringComparison.Ordinal),
                    url,
                    tab.ScrollX,
                    tab.ScrollY,
                    tab.AiChatHistory is { Count: > 0 } ? tab.AiChatHistory : null));
            }

            var activeIndex = 0;
            if (!string.IsNullOrWhiteSpace(activeUrl) && BrowserDataStore.IsHttpUrl(activeUrl))
            {
                var idx = sessionTabs.FindIndex(t => t.Url == activeUrl);
                if (idx >= 0) activeIndex = idx;
            }

            if (!_dataStore.SaveSession(new BrowserSession(sessionTabs, activeIndex)))
                Log.Debug("Failed to save session");
        }
        finally { _tabOperationLock.Release(); }
    }

    private async Task ClearPrivateProfileAsync()
    {
        _sleepTimer.Stop();

        await _tabOperationLock.WaitAsync();
        try
        {
            foreach (var tab in _tabs)
            {
                if (tab.WebView is not null)
                {
                    tab.RunCleanup();
                    RemoveElementFromParent(tab.WebView);
                    tab.WebView.Dispose();
                    tab.WebView = null;
                    tab.IsWebViewInitialized = false;
                }
            }
        }
        finally { _tabOperationLock.Release(); }

        if (!string.IsNullOrWhiteSpace(_privateUserDataFolder) && Directory.Exists(_privateUserDataFolder))
        {
            for (int retry = 0; retry < 5; retry++)
            {
                if (retry > 0) await Task.Delay(200);
                try
                {
                    Directory.Delete(_privateUserDataFolder, recursive: true);
                    break;
                }
                catch (Exception ex)
                {
                    if (retry == 4) Log.Error(ex, "Failed to delete private profile after 5 retries");
                }
            }
        }
    }

    private static BrowserTab? FindTabFromVisual(object originalSource)
    {
        for (var current = originalSource as DependencyObject; current is not null;
             current = GetVisualOrContentParent(current))
        {
            if (current is FrameworkElement { Tag: BrowserTab tab })
                return tab;
        }
        return null;
    }

    private AnimatedTabPanel GetTabPanel()
    {
        if (_tabPanel is null && VisualTreeHelper.GetChildrenCount(TitleBar.TabStrip) > 0)
        {
            var border = VisualTreeHelper.GetChild(TitleBar.TabStrip, 0);
            if (border is not null && VisualTreeHelper.GetChildrenCount(border) > 0)
            {
                var presenter = VisualTreeHelper.GetChild(border, 0);
                if (presenter is not null && VisualTreeHelper.GetChildrenCount(presenter) > 0)
                    _tabPanel = VisualTreeHelper.GetChild(presenter, 0) as AnimatedTabPanel;
            }
        }
        return _tabPanel!;
    }

    private void TabStrip_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsFromControl<Button>(e.OriginalSource))
            return;

        var tab = FindTabFromVisual(e.OriginalSource);
        if (tab is null) return;

        var index = _tabs.IndexOf(tab);
        if (index < 0) return;

        _dragTab = tab;
        _tabDragFromIndex = index;
        _tabDragStartPoint = e.GetPosition(TitleBar.TabStrip);
        _tabIsDragging = false;
    }

    private void TabStrip_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragTab is null || _tabDragFromIndex < 0) return;
        if (e.LeftButton != MouseButtonState.Pressed) { AbortDrag(); return; }

        var pos = e.GetPosition(TitleBar.TabStrip);

        if (!_tabIsDragging)
        {
            var dx = pos.X - _tabDragStartPoint.X;
            var dy = pos.Y - _tabDragStartPoint.Y;
            if (Math.Sqrt(dx * dx + dy * dy) < TabDragThreshold)
                return;
            var beginPanelPos = GetTabPanel() is { } bp ? e.GetPosition(bp) : default;
            BeginDrag(_tabDragFromIndex, beginPanelPos);
        }

        var currentIndex = _tabs.IndexOf(_dragTab);
        if (currentIndex < 0) { AbortDrag(); return; }

        var panel = GetTabPanel();
        if (panel is null) return;

        var belowTabStrip = pos.Y > TitleBar.TabStrip.ActualHeight + 10;
        _tabDragDetaching = belowTabStrip && !IsSplitActive;

        if (belowTabStrip)
        {
            panel.SetDetaching(true);
            panel.UpdateInsertion(currentIndex, currentIndex, default);

            if (!IsSplitActive)
            {
                ShowGhostPreview();
            }
            else
            {
                HideGhostPreview();
            }
        }
        else
        {
            panel.SetDetaching(false);
            HideGhostPreview();
            var panelPos = e.GetPosition(panel);
            var insertIndex = panel.GetInsertIndex(panelPos);
            _tabDropInsertIndex = insertIndex;
            panel.UpdateInsertion(currentIndex, insertIndex, panelPos);
        }
    }

    private void TabStrip_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_tabDragFromIndex < 0 && _dragTab is null) return;

        if (_tabIsDragging && _tabDragDetaching)
        {
            HideGhostPreview();
            if (_dragTab is not null && _tabs.Contains(_dragTab))
            {
                var url = _dragTab.RestoredUrl ?? _dragTab.Address;
                _ = CloseTabAsync(_dragTab);
                new MainWindow(isPrivate: _isPrivate, startupUrl: url, startupNativeKind: null, skipSessionPersistence: true).Show();
            }

            AbortDrag();
            TitleBar.TabStrip.ReleaseMouseCapture();
            return;
        }

        if (_tabIsDragging)
            EndDrag(e);
        else
            _tabDragFromIndex = -1;

        TitleBar.TabStrip.ReleaseMouseCapture();
    }

    private void TabStrip_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_tabDragFromIndex >= 0 || _dragTab is not null)
            AbortDrag();
    }

    private void BeginDrag(int tabIndex, Point panelPos)
    {
        if (_dragTab is null) return;
        var currentIndex = _tabs.IndexOf(_dragTab);
        if (currentIndex < 0) { AbortDrag(); return; }
        _tabDragFromIndex = currentIndex;
        _tabIsDragging = true;
        _tabDropInsertIndex = currentIndex;
        GetTabPanel()?.BeginDrag(currentIndex, panelPos);
        TitleBar.TabStrip.CaptureMouse();
    }

    private void EndDrag(MouseEventArgs e)
    {
        GetTabPanel()?.EndDrag();
        HideGhostPreview();

        var to = _tabDropInsertIndex;
        if (_dragTab is not null && to >= 0 && to < _tabs.Count)
        {
            var from = _tabs.IndexOf(_dragTab);
            if (from >= 0 && from != to)
            {
                _tabs.Move(from, to);
            }
        }

        _dragTab = null;
        _tabDragFromIndex = -1;
        _tabDropInsertIndex = -1;
        _tabIsDragging = false;
        _tabDragDetaching = false;
    }

    private void AbortDrag()
    {
        GetTabPanel()?.AbortDrag();
        HideGhostPreview();
        _dragTab = null;
        _tabDragFromIndex = -1;
        _tabDropInsertIndex = -1;
        _tabIsDragging = false;
        _tabDragDetaching = false;
    }

    private void ShowGhostPreview()
    {
        var tab = _dragTab;
        if (tab is null && _tabDragFromIndex >= 0 && _tabDragFromIndex < _tabs.Count)
            tab = _tabs[_tabDragFromIndex];
        if (tab is null) return;
        GetCursorPos(out var pt);
        var source = PresentationSource.FromVisual(this);
        var toWpf = source?.CompositionTarget?.TransformFromDevice;
        var wpfPos = toWpf?.Transform(new Point(pt.X, pt.Y)) ?? new Point(pt.X, pt.Y);

        double gw = GetTabPanel()?.CurrentTabWidth ?? 200;

        if (_ghostWindow is null)
        {
            var grid = new Grid { Margin = new Thickness(9, 0, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var dot = new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = tab.AccentBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            Grid.SetColumn(dot, 0);
            grid.Children.Add(dot);

            var title = new TextBlock
            {
                Text = tab.Title,
                Foreground = TryFindResource("Text") as Brush ?? Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontSize = 12
            };
            Grid.SetColumn(title, 1);
            grid.Children.Add(title);

            var border = new Border
            {
                Width = gw,
                Height = 30,
                CornerRadius = new CornerRadius(4),
                Background = tab.TabBrush,
                BorderBrush = tab.AccentBrush,
                BorderThickness = tab.TabBorderThickness,
                Effect = new DropShadowEffect
                {
                    BlurRadius = 16,
                    Opacity = 0.45,
                    ShadowDepth = 4,
                    Color = Colors.Black
                },
                Child = grid
            };
            _ghostWindow = new Window
            {
                Content = border,
                Width = gw,
                Height = 30,
                AllowsTransparency = true,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Topmost = true,
                IsHitTestVisible = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Background = Brushes.Transparent,
                Opacity = 0.92
            };
            _ghostWindow.Left = wpfPos.X - gw / 2;
            _ghostWindow.Top = wpfPos.Y - 15;
            _ghostWindow.Show();
        }
        else
        {
            var border = (Border)_ghostWindow.Content;
            border.Background = tab.TabBrush;
            border.BorderBrush = tab.AccentBrush;
            border.BorderThickness = tab.TabBorderThickness;
            var g = (Grid)border.Child;
            ((Ellipse)g.Children[0]).Fill = tab.AccentBrush;
            ((TextBlock)g.Children[1]).Text = tab.Title;
            _ghostWindow.Left = wpfPos.X - gw / 2;
            _ghostWindow.Top = wpfPos.Y - 15;
        }
    }

    private void HideGhostPreview()
    {
        if (_ghostWindow is not null)
        {
            _ghostWindow.Close();
            _ghostWindow = null;
        }
    }
}



