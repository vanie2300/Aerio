using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.ComponentModel;
using System.IO.Compression;
using System.Net.Http;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;

namespace TabloidBrowser;

public partial class MainWindow
{
    private IntPtr _windowHandle;
    private bool _isFindBarDragging;
    private bool _isFullScreen;
    private WindowState _previousWindowState;
    private readonly DispatcherTimer _chromeAutoHideTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private DateTime _chromeHideAt = DateTime.UtcNow.AddSeconds(2);
    private bool _browserChromeHidden;
    private bool _autoHideBrowserChromeActive;
    private bool _browserChromeSettingInitialized;
    private bool _chromeManuallyPinnedVisible;
    private bool _chromeHiddenBeforeFullScreen;
    private double _chromeProgress;
    private double _chromeTransitionFrom;
    private double _chromeTransitionTo;
    private long _chromeTransitionStarted;
    private TimeSpan _chromeTransitionDuration;
    private Point _findBarDragStart;
    private bool _suppressFindTextChanged;
    private volatile BrowserTab? _pendingReaderTab;
    private string? _readabilityCache;
    private readonly SemaphoreSlim _readabilityCacheLock = new(1, 1);

    private bool _chromeProbeFailureLogged;

    private void ChromeAutoHideTimer_Tick(object? sender, EventArgs e)
    {
        if (!IsVisible || WindowState == WindowState.Minimized ||
            _isFullScreen || !IsActive)
        {
            return;
        }

        if (!GetCursorPos(out var cursor))
        {
            // Fails on the secure desktop (UAC prompt, lock screen). Log once
            // per failure streak — this fires at 10 Hz and would flood the log.
            if (!_chromeProbeFailureLogged)
            {
                _chromeProbeFailureLogged = true;
                Log.Debug("Unable to read cursor position for browser chrome auto-hide");
            }
            return;
        }

        _chromeProbeFailureLogged = false;

        if (!GetWindowRect(_windowHandle, out var windowRect))
        {
            return;
        }

        var insideWindow = cursor.X >= windowRect.Left &&
                           cursor.X <= windowRect.Right &&
                           cursor.Y >= windowRect.Top &&
                           cursor.Y <= windowRect.Bottom;

        if (_browserChromeHidden)
        {
            if (insideWindow && cursor.Y <= windowRect.Top + 12)
            {
                SetBrowserChromeVisible(true);
                _chromeManuallyPinnedVisible = false;
                if (_isFullScreen)
                    _chromeHiddenBeforeFullScreen = false;
                _chromeHideAt = DateTime.MaxValue;
            }
            return;
        }

        if (!_autoHideBrowserChromeActive || _chromeManuallyPinnedVisible)
            return;

        if (TitleBar.IsKeyboardFocusWithin || NavToolbar.IsKeyboardFocusWithin ||
            NavToolbar.MenuButton.ContextMenu?.IsOpen == true ||
            NavToolbar.AddressSuggestionsPopup.IsOpen || FindBar.FindPopup.IsOpen ||
            BrowserPane.NewTabDialog.IsOpen || BrowserPane.ShortcutManagerDialog.IsOpen ||
            _aiSetupPopup?.IsOpen == true || _sitePermissionsPopup?.IsOpen == true ||
            _changelogPopup?.IsOpen == true || _isPermissionsPopupOpen ||
            (insideWindow && cursor.Y <= windowRect.Top + 84))
        {
            _chromeHideAt = DateTime.MaxValue;
            return;
        }

        if (_chromeHideAt == DateTime.MaxValue)
        {
            _chromeHideAt = DateTime.UtcNow.AddMilliseconds(700);
            return;
        }

        if (DateTime.UtcNow >= _chromeHideAt)
        {
            SetBrowserChromeVisible(false);
        }
    }

    private void SetBrowserChromeVisible(bool visible)
    {
        var targetProgress = visible ? 0.0 : 1.0;
        if (_browserChromeHidden == !visible && Math.Abs(_chromeProgress - targetProgress) < 0.001)
            return;

        _browserChromeHidden = !visible;

        _chromeTransitionFrom = _chromeProgress;
        _chromeTransitionTo = targetProgress;
        var distance = Math.Abs(_chromeTransitionTo - _chromeTransitionFrom);
        _chromeTransitionDuration = TimeSpan.FromMilliseconds(230 * distance);

        if (!SystemParameters.ClientAreaAnimation || distance < 0.001)
        {
            StopChromeAnimation();
            ApplyChromeProgress(targetProgress);
            FinishChromeTransition();
            return;
        }

        _chromeTransitionStarted = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering -= ChromeAnimation_Rendering;
        CompositionTarget.Rendering += ChromeAnimation_Rendering;
    }

    private void ChromeAnimation_Rendering(object? sender, EventArgs e)
    {
        var elapsed = Stopwatch.GetElapsedTime(_chromeTransitionStarted);
        var fraction = _chromeTransitionDuration <= TimeSpan.Zero
            ? 1
            : Math.Clamp(elapsed.TotalMilliseconds / _chromeTransitionDuration.TotalMilliseconds, 0, 1);
        _chromeProgress = ChromeMotion.EaseOutCubic(_chromeTransitionFrom, _chromeTransitionTo, fraction);
        ApplyChromeProgress(_chromeProgress);

        if (fraction >= 1)
            FinishChromeTransition();
    }

    private void ApplyChromeProgress(double progress)
    {
        _chromeProgress = Math.Clamp(progress, 0, 1);
        var opacity = 1 - _chromeProgress;
        var visibleChromeHeight = opacity;
        TitleBarRow.Height = new GridLength(40 * visibleChromeHeight);
        NavToolbarRow.Height = new GridLength(40 * visibleChromeHeight);
        StatusBarRow.Height = new GridLength(22 * visibleChromeHeight);
        TitleBar.Opacity = opacity;
        NavToolbar.Opacity = opacity;
        StatusBar.Opacity = opacity;
        var hitTestVisible = opacity > 0.01;
        TitleBar.IsHitTestVisible = hitTestVisible;
        NavToolbar.IsHitTestVisible = hitTestVisible;
        StatusBar.IsHitTestVisible = hitTestVisible;
    }

    private void FinishChromeTransition()
    {
        StopChromeAnimation();
        _chromeProgress = _chromeTransitionTo;
        ApplyChromeProgress(_chromeProgress);
    }

    private void StopChromeAnimation() =>
        CompositionTarget.Rendering -= ChromeAnimation_Rendering;

    private void ToggleBrowserChrome()
    {
        var show = _browserChromeHidden;
        _chromeManuallyPinnedVisible = show;
        if (_isFullScreen)
            _chromeHiddenBeforeFullScreen = !show;
        SetBrowserChromeVisible(show);
        _chromeHideAt = DateTime.MaxValue;
    }

    private void ToggleBrowserChrome_Click(object sender, RoutedEventArgs e) => ToggleBrowserChrome();

    private void RevealBrowserChrome()
    {
        if (!_browserChromeHidden)
            return;

        SetBrowserChromeVisible(true);
        if (_isFullScreen)
            _chromeHiddenBeforeFullScreen = false;
        _chromeHideAt = DateTime.MaxValue;
    }

    private void ApplyBrowserChromeVisibilitySetting()
    {
        if (!_browserChromeSettingInitialized)
        {
            _autoHideBrowserChromeActive = _dataStore.Settings.AutoHideBrowserChrome;
            _browserChromeSettingInitialized = true;
        }

        if (_isFullScreen)
        {
            return;
        }

        SetBrowserChromeVisible(true);
        _chromeHideAt = _autoHideBrowserChromeActive
            ? DateTime.UtcNow.AddSeconds(2)
            : DateTime.MaxValue;
    }

    private void ApplyWindowMode()
    {
        if (!_isPrivate)
        {
            StatusBar.LocalDataText.Text = "Local";
            return;
        }

        Title = "Aerio - Private";
        StatusBar.LocalDataText.Text = "Private";
        StatusBar.LocalDataText.Foreground = GetBrush("Pink");
        NavToolbar.PrivateButton.Content = "PVT";
        NavToolbar.PrivateButton.ToolTip = "Private window";
        NavToolbar.PrivateButton.IsEnabled = false;
        StatusBar.StatusText.Text = "Private browsing";
    }

    private Brush GetBrush(string key)
    {
        try { return (Brush)FindResource(key); }
        catch (ResourceReferenceKeyNotFoundException)
        {
            Log.Debug("Missing resource key: {Key}", key);
            return Brushes.Transparent;
        }
    }

    private System.Drawing.Color GetWebViewBackgroundColor()
    {
        var color = GetColor("AppBg");
        return System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B);
    }

    private Color GetColor(string key)
    {
        return GetBrush(key) is SolidColorBrush brush ? brush.Color : Colors.Black;
    }

    private void UpdateAddressBar()
    {
        NavToolbar.AddressBox.Text = _activeTab?.Address ?? string.Empty;
    }

    private void UpdateNavigationButtons()
    {
        var tab = _activeTab;
        var webView = tab?.Kind == BrowserTabKind.Web ? tab.WebView : null;
        NavToolbar.BackButton.IsEnabled = webView?.CanGoBack == true;
        NavToolbar.ForwardButton.IsEnabled = webView?.CanGoForward == true;
    }

    private void UpdateBookmarkButton()
    {
        var tab = _activeTab;
        var canBookmark = tab is { Kind: BrowserTabKind.Web } && BrowserDataStore.IsHttpUrl(tab.Address);
        NavToolbar.BookmarkButton.IsEnabled = canBookmark;

        if (!canBookmark || tab is null)
        {
            if (NavToolbar.BookmarkButton.Content?.ToString() != "\uE734")
                NavToolbar.BookmarkButton.Content = "\uE734";
            NavToolbar.BookmarkButton.BorderBrush = GetBrush("Stroke");
            NavToolbar.BookmarkButton.ToolTip = "Open a web page to bookmark";
            return;
        }

        var isBookmarked = _dataStore.IsBookmarked(tab.Address);
        NavToolbar.BookmarkButton.Content = isBookmarked ? "\uE735" : "\uE734";
        NavToolbar.BookmarkButton.BorderBrush = isBookmarked ? GetBrush("Pink") : GetBrush("Stroke");
        NavToolbar.BookmarkButton.ToolTip = isBookmarked ? "Remove bookmark" : "Add bookmark";
    }

    private void UpdateLoadingOverlay()
    {
        var tab = _activeTab;
        var show = tab?.IsLoading == true;
        if (show)
        {
            BrowserPane.LoadingOverlay.Visibility = Visibility.Visible;
            BrowserPane.SetLoadingSpinnerActive(true);
            BrowserPane.LoadingOverlay.Opacity = 0;
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.15))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            BrowserPane.LoadingOverlay.BeginAnimation(OpacityProperty, fadeIn);
            BrowserPane.LoadingTitleText.Text = $"Loading {TrimUrl(tab?.Address ?? string.Empty)}";
        }
        else if (BrowserPane.LoadingOverlay.Visibility == Visibility.Visible)
        {
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.1))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            fadeOut.Completed += (_, _) =>
            {
                BrowserPane.LoadingOverlay.Visibility = Visibility.Collapsed;
                BrowserPane.SetLoadingSpinnerActive(false);
                BrowserPane.LoadingOverlay.Opacity = 1;
            };
            BrowserPane.LoadingOverlay.BeginAnimation(OpacityProperty, fadeOut);
        }
    }

    private void UpdateMemoryStatus()
    {
        List<BrowserTab> snapshot;
        if (!_tabOperationLock.Wait(0))
        {
            if (!_memoryUpdatePending)
            {
                _memoryUpdatePending = true;
                Dispatcher.BeginInvoke(() =>
                {
                    _memoryUpdatePending = false;
                    UpdateMemoryStatus();
                }, DispatcherPriority.Background);
            }
            return;
        }
        try
        {
            snapshot = _tabs.ToList();
        }
        finally { _tabOperationLock.Release(); }
        var suspended = snapshot.Count(tab => tab.Kind == BrowserTabKind.Web && tab.WebView is not null && tab.IsSleeping);
        var hibernated = snapshot.Count(tab => tab.Kind == BrowserTabKind.Web && tab.WebView is null);
        var inactive = suspended + hibernated;

        if (_lastInactiveCount >= 0 && inactive != _lastInactiveCount && inactive > 0)
        {
            var pulse = new DoubleAnimation(1.0, 1.12, TimeSpan.FromSeconds(0.08))
            {
                AutoReverse = true,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            };
            if (NavToolbar.MemoryButton.RenderTransform is ScaleTransform scale)
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
            }
        }
        _lastInactiveCount = inactive;

        NavToolbar.MemoryButton.Content = inactive == 0 ? "RAM" : inactive.ToString();
        NavToolbar.MemoryButton.ToolTip = inactive == 0
            ? "Click to hibernate all inactive tabs"
            : $"{hibernated} hibernated, {suspended} suspended â€” click to hibernate all";
        StatusBar.MemoryStatusText.Text = inactive == 0
            ? "0 hibernated â€” click RAM to hibernate"
            : suspended == 0
                ? $"{hibernated} hibernated â€” click RAM to hibernate"
                : $"{hibernated} hibernated, {suspended} suspended â€” click RAM";
    }

    private void FocusStartSearchIfActive()
    {
        var tab = _activeTab;
        if (tab?.Kind != BrowserTabKind.Start || BrowserPane.IsNewTabDialogOpen)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (tab?.Kind != BrowserTabKind.Start || BrowserPane.IsNewTabDialogOpen)
            {
                return;
            }

            BrowserPane.StartSearchBox.Focus();
            FocusManager.SetFocusedElement(this, BrowserPane.StartSearchBox);
            Keyboard.Focus(BrowserPane.StartSearchBox);
            BrowserPane.StartSearchBox.SelectAll();
        }, DispatcherPriority.ApplicationIdle);
    }

    private async Task ClearBrowsingDataAsync()
    {
        var profile = _profile ?? _tabs
            .Select(tab => tab.WebView?.CoreWebView2?.Profile)
            .FirstOrDefault(candidate => candidate is not null);
        var profileCleared = false;
        try
        {
            if (profile is not null)
            {
                await profile.ClearBrowsingDataAsync();
                profileCleared = true;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to clear WebView browsing data");
        }

        var localDataCleared = _dataStore.ClearBrowsingState();
        if (localDataCleared && profileCleared)
        {
            StatusBar.StatusText.Text = "Browsing data cleared";
        }
        else if (!localDataCleared && !profileCleared)
        {
            StatusBar.StatusText.Text = "Couldn't clear browsing data";
        }
        else if (!localDataCleared)
        {
            StatusBar.StatusText.Text = "Site data cleared, but local history and downloads couldn't be cleared";
        }
        else
        {
            StatusBar.StatusText.Text = "Local history and downloads cleared, but site data couldn't be cleared";
        }
    }

    private void ImportBookmarks()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var count = _dataStore.ImportBookmarks(dialog.FileName);
        StatusBar.StatusText.Text = $"Imported {count} bookmarks";
        var importTab = _activeTab;
        if (importTab is not null)
        {
            RenderNativeSurface(importTab, forceRebuild: true);
        }
    }

    private void ExportBookmarks()
    {
        var dialog = new SaveFileDialog
        {
            FileName = "tabloid-bookmarks.json",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _dataStore.ExportBookmarks(dialog.FileName);
        StatusBar.StatusText.Text = "Bookmarks exported";
    }

    private static void OpenDownloadFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        using (Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        })) { }
    }

    private static int ClampInt(string? value, int min, int max, int fallback)
    {
        return int.TryParse(value, out var parsed) ? Math.Clamp(parsed, min, max) : fallback;
    }

    private static List<string> ParseDomains(string? value, List<string> fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [.. fallback];
        }

        var domains = value
            .Split(['\r', '\n', ',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(domain => domain.ToLowerInvariant())
            .Where(domain => domain.Contains('.'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return domains.Count == 0 ? [.. fallback] : domains;
    }

    private void TitleBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsFromControl<Button>(e.OriginalSource))
            return;

        if (e.ClickCount == 2)
        {
            _dragReady = false;
            ToggleMaximizeRestore();
            return;
        }

        var pos = e.GetPosition(this);
        if (pos.Y < 6) return;

        if (FindTabFromVisual(e.OriginalSource) is not null)
            return;

        _dragStartPoint = pos;
        _dragReady = true;
    }

    private void TitleBar_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragReady || e.LeftButton != MouseButtonState.Pressed)
            return;

        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _dragStartPoint.X) <= 2 &&
            Math.Abs(pos.Y - _dragStartPoint.Y) <= 2)
            return;

        _dragReady = false;
        try
        {
            DragMove();
        }
        catch (Exception ex)
        {
            StatusBar.StatusText.Text = $"Drag error: {ex.Message}";
        }
    }

    private void TitleBar_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragReady = false;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        SendMessage(_windowHandle, WM_SYSCOMMAND, new IntPtr(SC_MINIMIZE), IntPtr.Zero);
    }

    private void MaximizeRestore_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            SendMessage(_windowHandle, WM_SYSCOMMAND, new IntPtr(SC_RESTORE), IntPtr.Zero);
        else
            SendMessage(_windowHandle, WM_SYSCOMMAND, new IntPtr(SC_MAXIMIZE), IntPtr.Zero);
    }

    private void CloseWindow_Click(object? sender, RoutedEventArgs e) => Close();

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            // Pause idle work while minimized: chrome auto-hide probing and the
            // start-page particle loop have nothing to do off-screen.
            _chromeAutoHideTimer.Stop();
            _startBgTimer?.Stop();
            return;
        }

        _chromeAutoHideTimer.Start();
        if (IsStartBgAnimationApplicable())
        {
            _startBgTimer?.Start();
        }

        if (WindowState != WindowState.Maximized)
        {
            RootGrid.Margin = new Thickness(0);
            TitleBar.MaximizeGlyph.Visibility = Visibility.Visible;
            TitleBar.RestoreGlyph.Visibility = Visibility.Collapsed;
            TitleBar.MaximizeRestoreBtn.ToolTip = "Maximize";
            return;
        }

        {
            UpdateMaximizedMargins();

            TitleBar.MaximizeGlyph.Visibility = Visibility.Collapsed;
            TitleBar.RestoreGlyph.Visibility = Visibility.Visible;
            TitleBar.MaximizeRestoreBtn.ToolTip = "Restore";
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DPICHANGED)
        {
            if (WindowState == WindowState.Maximized)
            {
                UpdateMaximizedMargins();
            }
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void UpdateMaximizedMargins()
    {
        var monitor = MonitorFromWindow(_windowHandle, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfoW(monitor, ref info);

        var dpi = VisualTreeHelper.GetDpi(this);
        var sx = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        var sy = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;
        var border = 6.0;

        var left = (info.rcWork.Left - info.rcMonitor.Left) / sx + border;
        var top = (info.rcWork.Top - info.rcMonitor.Top) / sy + border;
        var right = (info.rcMonitor.Right - info.rcWork.Right) / sx + border;
        var bottom = (info.rcMonitor.Bottom - info.rcWork.Bottom) / sy + border;

        var abd = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
        int[] edges = [ABE_LEFT, ABE_TOP, ABE_RIGHT, ABE_BOTTOM];
        int[] autoHideEdges = [0, 0, 0, 0];
        for (int i = 0; i < edges.Length; i++)
        {
            abd.uEdge = edges[i];
            autoHideEdges[i] = SHAppBarMessage(ABM_GETAUTOHIDEBAR, ref abd) != IntPtr.Zero ? 1 : 0;
        }

        if (autoHideEdges[0] != 0 && left <= border) left = 3 / sx;
        if (autoHideEdges[1] != 0 && top <= border) top = 3 / sy;
        if (autoHideEdges[2] != 0 && right <= border) right = 3 / sx;
        if (autoHideEdges[3] != 0 && bottom <= border) bottom = 3 / sy;

        RootGrid.Margin = new Thickness(left, top, right, bottom);
    }

    private void ToggleMaximizeRestore()
    {
        if (WindowState == WindowState.Maximized)
            SendMessage(_windowHandle, WM_SYSCOMMAND, new IntPtr(SC_RESTORE), IntPtr.Zero);
        else
            SendMessage(_windowHandle, WM_SYSCOMMAND, new IntPtr(SC_MAXIMIZE), IntPtr.Zero);
    }

    private void Home_Click(object sender, RoutedEventArgs e)
    {
        var tab = _activeTab;
        if (tab is not null)
        {
            OpenNativePageInTab(tab, BrowserTabKind.Start);
        }
    }

    private async void SleepTabs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            List<BrowserTab> snapshot;
            await _tabOperationLock.WaitAsync();
            try
            {
                snapshot = _tabs.Where(tab =>
                    tab != _activeTab &&
                    tab.Kind == BrowserTabKind.Web &&
                    !tab.IsSleeping &&
                    !(tab.IsAiOwned && tab.AiState != AiTaskState.Idle)).ToList();
            }
            finally { _tabOperationLock.Release(); }
            await Task.WhenAll(snapshot.Select(HibernateTabAsync));
        }
        catch (Exception ex) { Log.Error(ex, "Error occurred"); }
    }

    private async void ClearData_Click(object sender, RoutedEventArgs e)
    {
        try { await ClearBrowsingDataAsync(); }
        catch (Exception ex) { Log.Error(ex, "Error occurred"); }
    }

    private void Bookmark_Click(object sender, RoutedEventArgs e)
    {
        var tab = _activeTab;
        if (tab is not { Kind: BrowserTabKind.Web } || !BrowserDataStore.IsHttpUrl(tab.Address))
        {
            StatusBar.StatusText.Text = "Open a web page before bookmarking";
            return;
        }

        if (_dataStore.IsBookmarked(tab.Address))
        {
            _dataStore.RemoveBookmark(tab.Address);
            StatusBar.StatusText.Text = "Bookmark removed";
        }
        else
        {
            _dataStore.AddBookmark(tab.Title, tab.Address);
            StatusBar.StatusText.Text = "Bookmarked";
        }

        PopulateBookmarkBar();
        UpdateBookmarkButton();
    }

    private async void ShowBookmarks_Click(object sender, RoutedEventArgs e)
    {
        try { await AddNativePageTabAsync(BrowserTabKind.Bookmarks); }
        catch (Exception ex) { Log.Error(ex, "Error occurred"); }
    }

    private async void ShowHistory_Click(object sender, RoutedEventArgs e)
    {
        try { await AddNativePageTabAsync(BrowserTabKind.History); }
        catch (Exception ex) { Log.Error(ex, "Error occurred"); }
    }

    private async void ShowDownloads_Click(object sender, RoutedEventArgs e)
    {
        try { await AddNativePageTabAsync(BrowserTabKind.Downloads); }
        catch (Exception ex) { Log.Error(ex, "Error occurred"); }
    }

    private async void ShowHymns_Click(object sender, RoutedEventArgs e)
    {
        try { await AddNativePageTabAsync(BrowserTabKind.Hymns); }
        catch (Exception ex) { Log.Error(ex, "Error occurred"); }
    }

    private void Privacy_Click(object sender, RoutedEventArgs e)
    {
        StatusBar.StatusText.Text = _isPrivate
            ? "Private mode uses an isolated temporary profile"
            : $"Ad & tracker blocking {(_dataStore.Settings.BlockTrackers || _dataStore.Settings.EnableAdBlock ? "on" : "off")}; blocked {_blockedTrackerCount} requests";

        if (_isPermissionsPopupOpen)
        {
            if (_sitePermissionsPopup is not null) _sitePermissionsPopup.IsOpen = false;
            return;
        }

        ShowSitePermissionsPopup();
    }

    private void Private_Click(object sender, RoutedEventArgs e)
    {
        var window = new MainWindow(isPrivate: true);
        window.Show();
    }

    private async Task SaveStateBeforeCloseAsync()
    {
        _sleepTimer.Stop();
        _sleepTimer.Tick -= SleepTimer_Tick;
        if (_startBgTimer is not null)
        {
            _startBgTimer.Stop();
            _startBgTimer.Tick -= StartBgTimer_Tick;
            _startBgTimer = null;
        }

        if (!_isPrivate)
        {
            if (_dataStore.Settings.AiSidebarOpen)
                _dataStore.SaveSettings(_dataStore.Settings with { AiSidebarOpen = false });

            await CaptureScrollPositionAsync(_activeTab);

            await SaveCurrentSessionAsync();

            if (_dataStore.Settings.ClearDataOnExit)
            {
                var profile = _profile ?? _tabs
                    .Select(tab => tab.WebView?.CoreWebView2?.Profile)
                    .FirstOrDefault(candidate => candidate is not null);
                if (profile is not null)
                {
                    try
                    {
                        var clearTask = profile.ClearBrowsingDataAsync();
                        var timedOut = await Task.WhenAny(clearTask, Task.Delay(3000)) != clearTask;
                        if (timedOut)
                        {
                            Log.Debug("ClearBrowsingDataAsync timed out after 3s");
                        }
                        else
                        {
                            await clearTask;
                        }
                    }
                    catch (Exception ex) { Log.Error(ex, "Failed to clear WebView browsing data on exit"); }
                }

                if (!_dataStore.ClearBrowsingState())
                    Log.Error(new IOException("Local browsing data could not be cleared on exit."), "ClearDataOnExit failed");
            }
        }
    }



    private void UpdateReloadStopButton()
    {
        var isLoading = _activeTab?.IsLoading == true;
        NavToolbar.ReloadButton.Content = isLoading ? "\uE71A" : "\uE72C";
        NavToolbar.ReloadButton.ToolTip = isLoading ? "Stop" : "Reload";
    }

    private void ApplyZoom(BrowserTab tab, double delta)
    {
        tab.ZoomLevel = Math.Clamp(tab.ZoomLevel + delta, 0.25, 5.0);
        if (tab.WebView is not null)
            tab.WebView.ZoomFactor = tab.ZoomLevel;
        UpdateZoomDisplay();
    }

    private void ResetZoom()
    {
        var tab = _activeTab;
        if (tab is null) return;
        tab.ZoomLevel = 1.0;
        if (tab.WebView is not null)
            tab.WebView.ZoomFactor = 1.0;
        UpdateZoomDisplay();
    }

    private void UpdateZoomDisplay()
    {
        var z = _activeTab?.ZoomLevel ?? 1.0;
        if (StatusBar.ZoomText is null) return;
        if (Math.Abs(z - 1.0) < 0.01)
        {
            StatusBar.ZoomText.Visibility = Visibility.Collapsed;
        }
        else
        {
            StatusBar.ZoomText.Text = $"{(int)(z * 100)}%";
            StatusBar.ZoomText.Visibility = Visibility.Visible;
        }
    }

    private void ToggleFullScreen()
    {
        _isFullScreen = !_isFullScreen;
        if (_isFullScreen)
        {
            _chromeHiddenBeforeFullScreen = _browserChromeHidden;
            _chromeManuallyPinnedVisible = false;
            _previousWindowState = WindowState;
            WindowState = WindowState.Maximized;
            WindowStyle = WindowStyle.None;
            SetBrowserChromeVisible(false);
        }
        else
        {
            WindowStyle = WindowStyle.None;
            WindowState = _previousWindowState;
            _chromeManuallyPinnedVisible = false;
            SetBrowserChromeVisible(!_chromeHiddenBeforeFullScreen);
            _chromeHideAt = _autoHideBrowserChromeActive
                ? DateTime.UtcNow.AddSeconds(2)
                : DateTime.MaxValue;
        }
    }

    private void PopulateBookmarkBar()
    {
        NavToolbar.BookmarkBarItems.Children.Clear();
        var bookmarks = _dataStore.Bookmarks;
        if (bookmarks.Count == 0)
        {
            if (NavToolbar.BookmarkBar.Visibility == Visibility.Visible)
            {
                var slideOut = new DoubleAnimation(26, 0, TimeSpan.FromSeconds(0.15))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
                };
                slideOut.Completed += (_, _) => NavToolbar.BookmarkBar.Visibility = Visibility.Collapsed;
                NavToolbar.BookmarkBar.BeginAnimation(HeightProperty, slideOut);
            }
            return;
        }
        if (NavToolbar.BookmarkBar.Visibility != Visibility.Visible)
        {
            NavToolbar.BookmarkBar.Height = 0;
            NavToolbar.BookmarkBar.Visibility = Visibility.Visible;
            var slideIn = new DoubleAnimation(0, 26, TimeSpan.FromSeconds(0.2))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            };
            NavToolbar.BookmarkBar.BeginAnimation(HeightProperty, slideIn);
        }
        else
        {
            NavToolbar.BookmarkBar.ClearValue(HeightProperty);
        }
        foreach (var bm in bookmarks)
        {
            var btn = new Button
            {
                Content = bm.Title,
                MaxWidth = 160,
                Height = 22,
                Margin = new Thickness(0, 0, 3, 0),
                Padding = new Thickness(6, 0, 6, 0),
                FontSize = 11,
                Tag = bm.Url,
                ToolTip = bm.Url,
                Style = TryFindResource("MicroButton") as Style
            };
            btn.Click += async (_, _) =>
            {
                try
                {
                    var tab = _activeTab;
                    if (tab is null) return;
                    await NavigateWebAsync(tab, bm.Url);
                }
                catch (Exception ex) { Log.Error(ex, "Error occurred"); }
            };
            NavToolbar.BookmarkBarItems.Children.Add(btn);
        }
    }

    private async void ShowSiteDataPopup()
    {
        try
        {
            var activeTab = _activeTab;
            var cwv = activeTab?.WebView?.CoreWebView2 ??
                _tabs.Select(tab => tab.WebView?.CoreWebView2).FirstOrDefault(webView => webView is not null);
            if (cwv is null)
            {
                StatusBar.StatusText.Text = "Open a website first to inspect saved site cookies.";
                return;
            }

            var popup = new Popup
            {
                PlacementTarget = this,
                Placement = PlacementMode.Center,
                StaysOpen = false,
                AllowsTransparency = true,
                PopupAnimation = PopupAnimation.Fade
            };
            var stack = new StackPanel { Margin = new Thickness(16) };
            stack.Children.Add(new TextBlock
            {
                Text = "Cookies & Site Data",
                Foreground = GetBrush("Text"),
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6)
            });
            stack.Children.Add(new TextBlock
            {
                Text = "Sites with saved cookies (may include sign-ins, preferences, or other site data):",
                Foreground = GetBrush("Muted"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            });

            var siteCount = new TextBlock
            {
                Text = "Loading saved sitesâ€¦",
                Foreground = GetBrush("Text"),
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 6)
            };
            stack.Children.Add(siteCount);

            var siteList = new StackPanel
            {
                Margin = new Thickness(0, 0, 4, 0)
            };
            var siteListScroll = new ScrollViewer
            {
                MaxHeight = 270,
                MinHeight = 54,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = siteList
            };
            siteListScroll.Resources.Add(typeof(ScrollBar), FindResource("DarkScrollBar"));
            siteList.Children.Add(new TextBlock
            {
                Text = "Loading sitesâ€¦",
                Foreground = GetBrush("Muted"),
                FontSize = 12,
                Margin = new Thickness(10, 14, 10, 14)
            });
            stack.Children.Add(siteListScroll);

            async Task RefreshSitesAsync()
            {
                siteList.Children.Clear();
                siteList.Children.Add(new TextBlock
                {
                    Text = "Loading sitesâ€¦",
                    Foreground = GetBrush("Muted"),
                    FontSize = 12,
                    Margin = new Thickness(10, 14, 10, 14)
                });
                siteCount.Text = "Loading saved sitesâ€¦";
                try
                {
                    var cookies = await cwv.CookieManager.GetCookiesAsync(string.Empty);
                    var domains = cookies
                        .Select(cookie => cookie.Domain.Trim().TrimStart('.'))
                        .Where(domain => !string.IsNullOrWhiteSpace(domain))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(domain => domain, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    siteList.Children.Clear();
                    siteCount.Text = $"{domains.Count} site{(domains.Count == 1 ? "" : "s")} with saved cookies";
                    if (domains.Count == 0)
                    {
                        siteList.Children.Add(new TextBlock
                        {
                            Text = "No saved site cookies found.",
                            Foreground = GetBrush("Muted"),
                            FontSize = 12,
                            Margin = new Thickness(10, 14, 10, 14)
                        });
                    }
                    else
                    {
                        foreach (var domain in domains)
                        {
                            var siteButton = new Button
                            {
                                MinHeight = 46,
                                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                                Padding = new Thickness(10, 6, 10, 6),
                                Margin = new Thickness(0, 0, 0, 6),
                                Background = GetBrush("SurfaceBg"),
                                BorderBrush = GetBrush("Stroke"),
                                BorderThickness = new Thickness(1),
                                Style = TryFindResource("MicroButton") as Style,
                                ToolTip = $"Open https://{domain}"
                            };
                            var row = new Grid();
                            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                            var siteIcon = new Border
                            {
                                Width = 30,
                                Height = 30,
                                CornerRadius = new CornerRadius(8),
                                Background = GetBrush("ChromeAlt"),
                                VerticalAlignment = VerticalAlignment.Center,
                                Child = new TextBlock
                                {
                                    Text = "\uE774",
                                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                                    FontSize = 13,
                                    Foreground = GetBrush("Accent"),
                                    HorizontalAlignment = HorizontalAlignment.Center,
                                    VerticalAlignment = VerticalAlignment.Center
                                }
                            };
                            row.Children.Add(siteIcon);

                            var domainLabel = new TextBlock
                            {
                                Text = domain,
                                Foreground = GetBrush("Text"),
                                FontSize = 12,
                                FontWeight = FontWeights.SemiBold,
                                VerticalAlignment = VerticalAlignment.Center,
                                TextTrimming = TextTrimming.CharacterEllipsis,
                                Margin = new Thickness(10, 0, 8, 0)
                            };
                            Grid.SetColumn(domainLabel, 1);
                            row.Children.Add(domainLabel);

                            var openLabel = new TextBlock
                            {
                                Text = "OPEN  \uE8A7",
                                FontFamily = new FontFamily("Segoe UI, Segoe MDL2 Assets"),
                                Foreground = GetBrush("Muted"),
                                FontSize = 9,
                                FontWeight = FontWeights.SemiBold,
                                VerticalAlignment = VerticalAlignment.Center,
                                Margin = new Thickness(8, 0, 0, 0)
                            };
                            Grid.SetColumn(openLabel, 2);
                            row.Children.Add(openLabel);
                            siteButton.Content = row;
                            siteButton.Click += async (_, _) =>
                            {
                                try
                                {
                                    popup.IsOpen = false;
                                    var siteUrl = new UriBuilder(Uri.UriSchemeHttps, domain).Uri.ToString();
                                    await AddWebTabAsync(siteUrl);
                                }
                                catch (Exception ex)
                                {
                                    Log.Error(ex, "Failed to open saved-cookie site {Domain}", domain);
                                    StatusBar.StatusText.Text = $"Couldn't open {domain}";
                                }
                            };
                            System.Windows.Automation.AutomationProperties.SetName(siteButton, $"Open {domain}");
                            siteList.Children.Add(siteButton);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to get saved site cookies");
                    siteCount.Text = "Saved sites are unavailable";
                    siteList.Children.Clear();
                    siteList.Children.Add(new TextBlock
                    {
                        Text = "Couldn't read cookies from the browser profile.",
                        Foreground = GetBrush("Muted"),
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(10, 14, 10, 14)
                    });
                }
            }

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            var refreshBtn = new Button
            {
                Content = "Refresh",
                Height = 30,
                Style = TryFindResource("MicroButton") as Style
            };
            refreshBtn.Click += async (_, _) => await RefreshSitesAsync();
            actions.Children.Add(refreshBtn);

            var clearBtn = new Button
            {
                Content = "Clear all site data",
                Height = 30,
                Margin = new Thickness(8, 0, 0, 0),
                Style = TryFindResource("MicroButton") as Style
            };
            clearBtn.Click += async (_, _) =>
            {
                try
                {
                    var profile = cwv.Profile;
                    await profile.ClearBrowsingDataAsync();
                    await RefreshSitesAsync();
                    StatusBar.StatusText.Text = "Site data cleared";
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to clear site data");
                    StatusBar.StatusText.Text = "Couldn't clear site data";
                }
            };
            actions.Children.Add(clearBtn);

            var closeBtn = new Button
            {
                Content = "Close",
                Height = 30,
                Margin = new Thickness(8, 0, 0, 0),
                Style = TryFindResource("MicroButton") as Style
            };
            closeBtn.Click += (_, _) => popup.IsOpen = false;
            actions.Children.Add(closeBtn);
            stack.Children.Add(actions);

            var border = new Border
            {
                Background = GetBrush("ChromeBg"),
                BorderBrush = GetBrush("Stroke"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Child = stack,
                MinWidth = 360,
                MaxWidth = 480
            };
            popup.Child = border;
            UiFx.PlayPopupEntrance(border);
            popup.IsOpen = true;
            await RefreshSitesAsync();
        }
        catch (Exception ex) { Log.Error(ex, "ShowSiteDataPopup failed"); }
    }
}
