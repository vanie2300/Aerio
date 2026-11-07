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
    private static readonly TimeSpan WindowBoundsTransitionDuration = TimeSpan.FromMilliseconds(180);

    private IntPtr _windowHandle;
    private bool _isFindBarDragging;
    private bool _isFullScreen;
    private bool _fullScreenTransitionInProgress;
    private RECT _fullScreenRestoreBounds;
    private RECT _fullScreenNormalBounds;
    private bool _windowBoundsTransitionInProgress;
    private RECT _windowRestoreBounds;
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
    private int _sidebarRailAnimationVersion;
    private bool _sidebarRailTargetVisible;
    private bool _sidebarRailManuallyPinned;
    private bool _sidebarRailPositionInitialized;
    private bool _isSidebarRailDragging;
    private Point _sidebarRailPosition;
    private Point _sidebarRailDragPointer;
    private Point _sidebarRailDragOrigin;
    private bool _sidebarEdgeTogglePositionFailureLogged;
    private Point _findBarDragStart;
    private bool _suppressFindTextChanged;
    private volatile BrowserTab? _pendingReaderTab;
    private string? _readabilityCache;
    private readonly SemaphoreSlim _readabilityCacheLock = new(1, 1);

    private bool _chromeProbeFailureLogged;

    private void ChromeAutoHideTimer_Tick(object? sender, EventArgs e)
    {
        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            HideSidebarOverlays();
            return;
        }

        if (!IsActive)
        {
            HideSidebarOverlays();
            return;
        }

        ShowSidebarEdgeToggleWindow();
        UpdateSidebarRailVisibility();

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

        if (_isFullScreen)
            return;

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

    private void UpdateSidebarRailVisibility()
    {
        if (_sidebarRailManuallyPinned)
            ShowSidebarRail();
        else
            HideSidebarRail();
    }

    private void HideSidebarOverlays()
    {
        _sidebarEdgeToggleWindow.Hide();
        _sidebarRailTargetVisible = false;
        _sidebarRailAnimationVersion++;
        SidebarRail.BeginAnimation(UIElement.OpacityProperty, null);
        SidebarRail.RailTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        SidebarRail.Opacity = 0;
        SidebarRail.RailTranslate.X = -10;
        SidebarRail.IsHitTestVisible = false;
        SidebarRailPopup.IsOpen = false;
    }

    private void SidebarRailToggle_Click(object sender, RoutedEventArgs e)
    {
        _sidebarRailManuallyPinned = !_sidebarRailManuallyPinned;
        UpdateSidebarRailVisibility();
    }

    private void SidebarRail_RailDragStarted(object sender, MouseButtonEventArgs e)
    {
        if (!SidebarRailPopup.IsOpen || !GetCursorPos(out var cursor))
            return;

        _isSidebarRailDragging = true;
        _sidebarRailDragPointer = RootGrid.PointFromScreen(new Point(cursor.X, cursor.Y));
        _sidebarRailDragOrigin = _sidebarRailPosition;
        e.Handled = true;
    }

    private void SidebarRail_RailDragMoved(object sender, MouseEventArgs e)
    {
        if (!_isSidebarRailDragging || !GetCursorPos(out var cursor))
            return;

        var pointer = RootGrid.PointFromScreen(new Point(cursor.X, cursor.Y));
        var minY = TitleBarRow.ActualHeight + NavToolbarRow.ActualHeight;
        var maxX = Math.Max(0, RootGrid.ActualWidth - SidebarRail.Width);
        var maxY = Math.Max(minY, RootGrid.ActualHeight - SidebarRail.Height);
        _sidebarRailPosition = new Point(
            Math.Clamp(_sidebarRailDragOrigin.X + pointer.X - _sidebarRailDragPointer.X, 0, maxX),
            Math.Clamp(_sidebarRailDragOrigin.Y + pointer.Y - _sidebarRailDragPointer.Y, minY, maxY));
        UpdateSidebarRailPosition();
        e.Handled = true;
    }

    private void SidebarRail_RailDragEnded(object sender, RoutedEventArgs e)
    {
        _isSidebarRailDragging = false;
    }

    private void ShowSidebarRail()
    {
        if (_sidebarRailTargetVisible)
            return;

        _sidebarRailTargetVisible = true;
        if (!SidebarRailPopup.IsOpen)
        {
            UpdateSidebarRailPosition();
            SidebarRailPopup.IsOpen = true;
            SidebarRail.Opacity = 0;
            SidebarRail.RailTranslate.X = -10;
        }

        SidebarRail.IsHitTestVisible = true;
        if (!SystemParameters.ClientAreaAnimation)
        {
            SidebarRail.Opacity = 1;
            SidebarRail.RailTranslate.X = 0;
            return;
        }

        _sidebarRailAnimationVersion++;
        var fadeIn = new DoubleAnimation(
            SidebarRail.Opacity,
            1,
            TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var slideIn = new DoubleAnimation(
            SidebarRail.RailTranslate.X,
            0,
            TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        SidebarRail.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        SidebarRail.RailTranslate.BeginAnimation(TranslateTransform.XProperty, slideIn);
    }

    private void HideSidebarRail()
    {
        if (!SidebarRailPopup.IsOpen || !_sidebarRailTargetVisible)
            return;

        _sidebarRailTargetVisible = false;
        if (!SystemParameters.ClientAreaAnimation)
        {
            SidebarRail.IsHitTestVisible = false;
            SidebarRailPopup.IsOpen = false;
            return;
        }

        var animationVersion = ++_sidebarRailAnimationVersion;
        var fadeOut = new DoubleAnimation(
            SidebarRail.Opacity,
            0,
            TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        var slideOut = new DoubleAnimation(
            SidebarRail.RailTranslate.X,
            -10,
            TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        fadeOut.Completed += (_, _) =>
        {
            if (animationVersion != _sidebarRailAnimationVersion ||
                _sidebarRailTargetVisible)
            {
                return;
            }

            SidebarRail.IsHitTestVisible = false;
            SidebarRailPopup.IsOpen = false;
        };
        SidebarRail.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        SidebarRail.RailTranslate.BeginAnimation(TranslateTransform.XProperty, slideOut);
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
        TitleBarRow.Height = new GridLength(38 * visibleChromeHeight);
        NavToolbarRow.Height = new GridLength(42 * visibleChromeHeight);
        TitleBar.Opacity = opacity;
        NavToolbar.Opacity = opacity;
        var hitTestVisible = opacity > 0.01;
        TitleBar.IsHitTestVisible = hitTestVisible;
        NavToolbar.IsHitTestVisible = hitTestVisible;
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
            return;
        }

        Title = "Aerio - Private";
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
        UpdateAddressBarChrome();
        UpdateWindowTitle();
    }

    /// <summary>
    /// Refreshes the omnibox chrome (favicon, security glyph, bookmark star)
    /// from the active tab's current state.
    /// </summary>
    private void UpdateAddressBarChrome()
    {
        var tab = _activeTab;
        NavToolbar.SetAddressBarFavicon(tab?.Favicon);

        var isWebUrl = tab?.Kind == BrowserTabKind.Web &&
                       BrowserDataStore.IsHttpUrl(tab.Address);
        var bookmarked = isWebUrl && _dataStore.IsBookmarked(tab!.Address);
        NavToolbar.SetBookmarkState(isWebUrl, bookmarked);

        // The shield reflects both tracker blocking and connection security
        // (red on plain-HTTP sites), so refresh it with every address change.
        UpdatePrivacyIndicator();
    }

    /// <summary>
    /// Mirrors the active page title into the OS window title (Alt-Tab,
    /// taskbar) the way real browsers do.
    /// </summary>
    private void UpdateWindowTitle()
    {
        var title = _activeTab?.Title;
        string windowTitle;
        if (string.IsNullOrWhiteSpace(title))
        {
            windowTitle = "Aerio";
        }
        else
        {
            if (title.Length > 90)
            {
                title = title[..90].TrimEnd() + "…";
            }
            windowTitle = $"{title} - Aerio";
        }

        if (!string.Equals(Title, windowTitle, StringComparison.Ordinal))
        {
            Title = windowTitle;
        }
    }

    private void UpdateNavigationButtons()
    {
        var tab = _activeTab;
        var webView = tab?.Kind == BrowserTabKind.Web ? tab.WebView : null;
        NavToolbar.BackButton.IsEnabled = webView?.CanGoBack == true;
        NavToolbar.ForwardButton.IsEnabled = webView?.CanGoForward == true;
    }

    private void UpdateLoadingOverlay()
    {
        var tab = _activeTab;
        var show = tab?.IsLoading == true;
        if (show)
        {
            BrowserPane.LoadingOverlay.SetLoadingMessage($"Loading {TrimUrl(tab?.Address ?? string.Empty)}");
            BrowserPane.LoadingOverlay.SetLoadingSpinnerActive(true);
        }
        else
        {
            BrowserPane.LoadingOverlay.SetLoadingSpinnerActive(false);
        }
    }

    private void UpdateMemoryStatus()
    {
        List<BrowserTab> snapshot;
        try
        {
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
        }
        catch (ObjectDisposedException)
        {
            // Teardown disposed the tab lock while a queued update was still
            // pending (observed as a Dispatcher unhandled exception during
            // window close); nothing left to refresh.
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

        NavToolbar.MemoryButton.Content = "RAM";
        NavToolbar.MemoryButton.ToolTip = new StackPanel
        {
            Children =
            {
                new TextBlock
                {
                    Text = "Tab memory",
                    FontWeight = FontWeights.SemiBold,
                    Foreground = GetBrush("Text")
                },
                new TextBlock
                {
                    Text = $"{hibernated} hibernated · {suspended} suspended",
                    Foreground = GetBrush("InkSecondary"),
                    Margin = new Thickness(0, 3, 0, 0)
                },
                new TextBlock
                {
                    Text = "Click RAM to hibernate inactive tabs",
                    Foreground = GetBrush("Muted"),
                    Margin = new Thickness(0, 3, 0, 0)
                }
            }
        };
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

    private async void TitleBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsFromControl<Button>(e.OriginalSource))
            return;

        if (e.ClickCount == 2)
        {
            _dragReady = false;
            try
            {
                await ToggleMaximizeRestoreAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to toggle maximize/restore from the title bar");
            }
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
        if (_minimizeTransitionInProgress)
        {
            return;
        }

        _minimizeTransitionInProgress = true;
        RootGrid.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = new ScaleTransform(1, 1);
        RootGrid.RenderTransform = scale;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.9, Motion.Fast)
        {
            EasingFunction = Motion.InOut
        });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.9, Motion.Fast)
        {
            EasingFunction = Motion.InOut
        });
        RootGrid.BeginAnimation(OpacityProperty, Motion.Fade(1, 0.45, Motion.Fast));
        MinimizeAfterAnimationAsync();
    }

    private async void MinimizeAfterAnimationAsync()
    {
        try
        {
            await Task.Delay(Motion.Fast);
            SendMessage(_windowHandle, WM_SYSCOMMAND, new IntPtr(SC_MINIMIZE), IntPtr.Zero);
        }
        catch (Exception ex)
        {
            ResetMinimizeAnimation();
            Log.Error(ex, "Failed to minimize the window after the transition animation");
        }
    }

    private void ResetMinimizeAnimation()
    {
        if (!_minimizeTransitionInProgress)
        {
            return;
        }

        _minimizeTransitionInProgress = false;
        RootGrid.BeginAnimation(OpacityProperty, null);
        RootGrid.Opacity = 1;
        RootGrid.RenderTransform = Transform.Identity;
    }

    private async void MaximizeRestore_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ToggleMaximizeRestoreAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to toggle maximize/restore");
        }
    }

    private void CloseWindow_Click(object? sender, RoutedEventArgs e) => Close();

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        UpdateResizeBandInset();
        UpdateResizeGripVisibility();
        if (WindowState == WindowState.Minimized)
        {
            // Pause idle work while minimized: chrome auto-hide probing and the
            // start-page particle loop have nothing to do off-screen.
            _chromeAutoHideTimer.Stop();
            _startBgTimer?.Stop();
            return;
        }

        ResetMinimizeAnimation();
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

    /// <summary>
    /// Reserves the WindowChrome resize band (10 DIPs, left/right/bottom) so
    /// the WebView2 child HWND never covers it. The OS hit-tests the deepest
    /// window under the cursor: when the child overlapped the band, real mouse
    /// drags on the border reached the child and returned HTCLIENT — the
    /// border was dead even though the top-level WM_NCHITTEST path reported
    /// HTBOTTOM. Maximized and fullscreen windows cannot be resized, so they
    /// stay edge-to-edge.
    /// </summary>
    private void UpdateResizeBandInset()
    {
        var inset = WindowState == WindowState.Normal && !_isFullScreen
            ? new Thickness(10, 0, 10, 10)
            : new Thickness(0);
        if (BrowserHost.Margin != inset)
        {
            BrowserHost.Margin = inset;
        }
    }

    private void UpdateResizeGripVisibility()
    {
        ResizeGripLayer.Visibility =
            WindowState == WindowState.Normal &&
            !_isFullScreen &&
            ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void ResizeGrip_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (WindowState != WindowState.Normal ||
            _isFullScreen ||
            _windowBoundsTransitionInProgress ||
            sender is not FrameworkElement grip)
        {
            return;
        }

        var hitTest = grip.Name switch
        {
            "ResizeLeftGrip" => HTLEFT,
            "ResizeRightGrip" => HTRIGHT,
            "ResizeTopGrip" => HTTOP,
            "ResizeBottomGrip" => HTBOTTOM,
            "ResizeTopLeftGrip" => HTTOPLEFT,
            "ResizeTopRightGrip" => HTTOPRIGHT,
            "ResizeBottomLeftGrip" => HTBOTTOMLEFT,
            "ResizeBottomRightGrip" => HTBOTTOMRIGHT,
            _ => 0
        };
        if (hitTest == 0)
        {
            return;
        }

        e.Handled = true;
        if (!ReleaseCapture())
        {
            Log.Debug("Mouse capture was not active before resizing window");
        }

        SendMessage(_windowHandle, WM_NCLBUTTONDOWN, new IntPtr(hitTest), IntPtr.Zero);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            UpdateMaximizedBounds(hwnd, lParam);
            // Let WPF apply MinWidth/MinHeight and its normal resize limits.
        }

        if (msg == WM_NCHITTEST &&
            WindowState == WindowState.Normal &&
            !_isFullScreen &&
            ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip &&
            GetWindowRect(hwnd, out var windowRect))
        {
            var hitTestPoint = lParam.ToInt64();
            var cursorX = unchecked((short)(hitTestPoint & 0xFFFF));
            var cursorY = unchecked((short)((hitTestPoint >> 16) & 0xFFFF));
            var dpi = VisualTreeHelper.GetDpi(this);
            var borderX = (int)Math.Ceiling(10 * dpi.DpiScaleX);
            var borderY = (int)Math.Ceiling(10 * dpi.DpiScaleY);
            var onLeft = cursorX >= windowRect.Left && cursorX < windowRect.Left + borderX;
            var onRight = cursorX < windowRect.Right && cursorX >= windowRect.Right - borderX;
            var onTop = cursorY >= windowRect.Top && cursorY < windowRect.Top + borderY;
            var onBottom = cursorY < windowRect.Bottom && cursorY >= windowRect.Bottom - borderY;

            var hitTest = onTop
                ? onLeft ? HTTOPLEFT : onRight ? HTTOPRIGHT : HTTOP
                : onBottom
                    ? onLeft ? HTBOTTOMLEFT : onRight ? HTBOTTOMRIGHT : HTBOTTOM
                    : onLeft ? HTLEFT : onRight ? HTRIGHT : 0;
            if (hitTest != 0)
            {
                handled = true;
                return new IntPtr(hitTest);
            }
        }

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
        // The native maximized bounds already fit the work area. A content
        // margin cannot expose the taskbar because it leaves the HWND in place.
        RootGrid.Margin = new Thickness(0);
    }

    internal static void UpdateMaximizedBounds(IntPtr hwnd, IntPtr lParam)
    {
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(monitor, ref info))
        {
            return;
        }

        var bounds = GetMaximizedWorkArea(info);
        var minMax = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        minMax.ptMaxPosition.X = bounds.Left - info.rcMonitor.Left;
        minMax.ptMaxPosition.Y = bounds.Top - info.rcMonitor.Top;
        minMax.ptMaxSize.X = bounds.Right - bounds.Left;
        minMax.ptMaxSize.Y = bounds.Bottom - bounds.Top;
        Marshal.StructureToPtr(minMax, lParam, false);
    }

    private static RECT GetMaximizedWorkArea(MONITORINFO info)
    {
        var bounds = info.rcWork;
        var abd = new APPBARDATA
        {
            cbSize = Marshal.SizeOf<APPBARDATA>(),
            rc = info.rcMonitor
        };
        // Keep two physical pixels available for the shell's auto-hide trigger.
        // Query this monitor, including when it is left/above the primary one.
        for (var edge = ABE_LEFT; edge <= ABE_BOTTOM; edge++)
        {
            abd.uEdge = edge;
            if (SHAppBarMessage(ABM_GETAUTOHIDEBAREX, ref abd) == IntPtr.Zero)
            {
                continue;
            }

            switch (edge)
            {
                case ABE_LEFT:
                    bounds.Left = Math.Max(bounds.Left, info.rcMonitor.Left + 2);
                    break;
                case ABE_TOP:
                    bounds.Top = Math.Max(bounds.Top, info.rcMonitor.Top + 2);
                    break;
                case ABE_RIGHT:
                    bounds.Right = Math.Min(bounds.Right, info.rcMonitor.Right - 2);
                    break;
                case ABE_BOTTOM:
                    bounds.Bottom = Math.Min(bounds.Bottom, info.rcMonitor.Bottom - 2);
                    break;
            }
        }
        return bounds;
    }

    private async Task ToggleMaximizeRestoreAsync()
    {
        if (_windowBoundsTransitionInProgress || _isFullScreen)
        {
            return;
        }

        _windowBoundsTransitionInProgress = true;
        try
        {
            if (WindowState != WindowState.Maximized)
            {
                if (!GetWindowRect(_windowHandle, out _windowRestoreBounds))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                var monitor = MonitorFromWindow(_windowHandle, MONITOR_DEFAULTTONEAREST);
                var monitorInfo = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
                if (!GetMonitorInfoW(monitor, ref monitorInfo) ||
                    !GetWindowRect(_windowHandle, out var currentBounds))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                await AnimateWindowBoundsAsync(currentBounds, GetMaximizedWorkArea(monitorInfo));
                // Animation runs in Normal state and updates WPF's RestoreBounds.
                // Restore the original normal geometry before maximizing so F11
                // and OS restore commands cannot leave a monitor-sized Normal window.
                SetWindowBounds(_windowRestoreBounds);
                WindowState = WindowState.Maximized;
            }
            else
            {
                if (!GetWindowRect(_windowHandle, out var maximizedBounds))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                var restoreBounds = ResolveRestoreBounds(_windowRestoreBounds);
                WindowState = WindowState.Normal;
                if (!HasUsableSize(restoreBounds))
                {
                    // The window may have been maximized by the OS (Win+Up, Aero
                    // Snap, system menu) before this handler ever captured restore
                    // bounds. Let WPF restore to its own tracked bounds instead of
                    // animating to an empty 0x0 rectangle.
                    return;
                }

                SetWindowBounds(maximizedBounds);
                await AnimateWindowBoundsAsync(maximizedBounds, restoreBounds);
            }
        }
        catch (InvalidOperationException ex)
        {
            Log.Error(ex, "Maximize/restore transition failed");
        }
        catch (Win32Exception ex)
        {
            Log.Error(ex, "Maximize/restore transition failed");
        }
        finally
        {
            _windowBoundsTransitionInProgress = false;
        }
    }

    /// <summary>
    /// Returns bounds that are safe to animate to. Prefers the rect captured
    /// before an in-app maximize; falls back to WPF's OS-tracked restore bounds
    /// (converted from DIPs to physical pixels) for externally maximized windows.
    /// </summary>
    private RECT ResolveRestoreBounds(RECT preferred)
    {
        if (HasUsableSize(preferred))
        {
            return preferred;
        }

        var restore = RestoreBounds;
        if (!double.IsFinite(restore.Left) || !double.IsFinite(restore.Top) ||
            !double.IsFinite(restore.Width) || !double.IsFinite(restore.Height) ||
            restore.Width <= 0 || restore.Height <= 0)
        {
            return preferred;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var sx = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        var sy = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;
        return new RECT
        {
            Left = (int)Math.Round(restore.Left * sx),
            Top = (int)Math.Round(restore.Top * sy),
            Right = (int)Math.Round(restore.Right * sx),
            Bottom = (int)Math.Round(restore.Bottom * sy)
        };
    }

    private static bool HasUsableSize(RECT bounds) =>
        bounds.Right - bounds.Left > 0 && bounds.Bottom - bounds.Top > 0;

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

        UpdateAddressBarChrome();
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
        SetZoom(tab, tab.ZoomLevel + delta, persist: true);
    }

    private void ResetZoom()
    {
        var tab = _activeTab;
        if (tab is null) return;
        SetZoom(tab, 1.0, persist: true);
    }

    /// <summary>
    /// Applies a zoom level to a tab, mirrors it onto the WebView, and (when
    /// persisting) saves it for the site's origin so it survives navigation.
    /// </summary>
    private void SetZoom(BrowserTab tab, double zoom, bool persist)
    {
        tab.ZoomLevel = Math.Clamp(zoom, BrowserDataStore.MinSiteZoom, BrowserDataStore.MaxSiteZoom);
        if (tab.WebView is not null)
        {
            tab.WebView.ZoomFactor = tab.ZoomLevel;
        }

        if (persist)
        {
            _dataStore.SetSiteZoom(tab.Address, tab.ZoomLevel);
        }

        UpdateZoomDisplay(showToast: true);
    }

    /// <summary>
    /// Restores the zoom level saved for the site a tab just navigated to.
    /// </summary>
    private void ApplySavedSiteZoom(BrowserTab tab)
    {
        var saved = _dataStore.GetSiteZoom(tab.Address);
        tab.ZoomLevel = saved;
        if (tab.WebView is not null)
        {
            tab.WebView.ZoomFactor = saved;
        }

        if (_activeTab == tab && Math.Abs(saved - 1.0) > 0.001)
        {
            UpdateZoomDisplay(showToast: true);
        }
    }

    private void UpdateZoomDisplay(bool showToast = false)
    {
        if (!showToast)
        {
            return;
        }

        var tab = _activeTab;
        var z = tab?.ZoomLevel ?? 1.0;
        var hasOverride = Math.Abs(z - 1.0) > 0.001;
        var host = tab is not null && Uri.TryCreate(tab.Address, UriKind.Absolute, out var uri)
            ? uri.Host
            : null;

        StatusBar.StatusText.Text = hasOverride && !string.IsNullOrEmpty(host)
            ? $"Zoom: {(int)(z * 100)}% (saved for {host})"
            : $"Zoom: {(int)(z * 100)}%";
    }

    private async Task ToggleFullScreenAsync()
    {
        if (_fullScreenTransitionInProgress)
        {
            return;
        }

        _fullScreenTransitionInProgress = true;
        try
        {
            var tab = _activeTab;
            var (scrollX, scrollY) = await CaptureScrollPositionForFullScreenAsync(tab);
            if (!_isFullScreen)
            {
                _previousWindowState = WindowState;
                if (!GetWindowRect(_windowHandle, out var currentBounds))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                _fullScreenRestoreBounds = currentBounds;
                // Preserve the window's TRUE normal bounds before the
                // fullscreen geometry is applied. SetWindowBounds below runs
                // while WindowState is Normal, so entering fullscreen from a
                // maximized window would otherwise write the maximized rect
                // into the normal bounds — observed as a "restored" window
                // stuck at full-monitor size that blocked the taskbar reveal
                // strip and the bottom resize border.
                _fullScreenNormalBounds = WindowState == WindowState.Maximized
                    ? ResolveRestoreBounds(default)
                    : currentBounds;
                if (!HasUsableSize(_fullScreenNormalBounds))
                {
                    _fullScreenNormalBounds = currentBounds;
                }
                _isFullScreen = true;
                UpdateResizeBandInset();
                UpdateResizeGripVisibility();
                _chromeHiddenBeforeFullScreen = _browserChromeHidden;
                _chromeManuallyPinnedVisible = false;
                WindowState = WindowState.Normal;
                SetWindowBounds(currentBounds);
                WindowStyle = WindowStyle.None;
                RootGrid.Margin = new Thickness(0);
                SetBrowserChromeVisible(false);

                var monitor = MonitorFromWindow(_windowHandle, MONITOR_DEFAULTTONEAREST);
                var monitorInfo = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
                if (!GetMonitorInfoW(monitor, ref monitorInfo))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                await AnimateWindowBoundsAsync(currentBounds, monitorInfo.rcMonitor);
            }
            else
            {
                if (!GetWindowRect(_windowHandle, out var currentBounds))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                await AnimateWindowBoundsAsync(currentBounds, _fullScreenRestoreBounds);
                // Repair the normal bounds BEFORE applying the previous state:
                // the fullscreen window ran in Normal state at monitor size, so
                // WPF's tracked restore geometry is the fullscreen rect.
                // Restoring through it would leave a full-monitor "normal"
                // window (blocked taskbar, dead bottom resize border).
                WindowState = WindowState.Normal;
                if (HasUsableSize(_fullScreenNormalBounds))
                {
                    SetWindowBounds(_fullScreenNormalBounds);
                }

                if (_previousWindowState == WindowState.Maximized)
                {
                    WindowState = WindowState.Maximized;
                }
                _isFullScreen = false;
                UpdateResizeBandInset();
                UpdateResizeGripVisibility();
                if (WindowState == WindowState.Maximized)
                {
                    UpdateMaximizedMargins();
                }
                else
                {
                    RootGrid.Margin = new Thickness(0);
                }

                _chromeManuallyPinnedVisible = false;
                SetBrowserChromeVisible(!_chromeHiddenBeforeFullScreen);
                _chromeHideAt = _autoHideBrowserChromeActive
                    ? DateTime.UtcNow.AddSeconds(2)
                    : DateTime.MaxValue;
            }

            if (tab?.Kind == BrowserTabKind.Web &&
                tab.WebView?.CoreWebView2 is not null &&
                (scrollX >= 1.0 || scrollY >= 1.0))
            {
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
                await RestoreScrollPositionAsync(tab, scrollX, scrollY, preferTrackedElement: true);
            }
        }
        catch (InvalidOperationException ex)
        {
            Log.Error(ex, "Fullscreen transition failed");
        }
        catch (COMException ex)
        {
            Log.Error(ex, "Fullscreen transition failed");
        }
        catch (TaskCanceledException ex)
        {
            Log.Error(ex, "Fullscreen transition failed");
        }
        catch (Win32Exception ex)
        {
            Log.Error(ex, "Fullscreen transition failed");
        }
        finally
        {
            _fullScreenTransitionInProgress = false;
        }
    }

    private async Task AnimateWindowBoundsAsync(RECT from, RECT to)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < WindowBoundsTransitionDuration)
        {
            var progress = Math.Clamp(stopwatch.Elapsed.TotalMilliseconds / WindowBoundsTransitionDuration.TotalMilliseconds, 0, 1);
            var eased = progress * progress * (3 - 2 * progress);
            var bounds = new RECT
            {
                Left = (int)Math.Round(from.Left + (to.Left - from.Left) * eased),
                Top = (int)Math.Round(from.Top + (to.Top - from.Top) * eased),
                Right = (int)Math.Round(from.Right + (to.Right - from.Right) * eased),
                Bottom = (int)Math.Round(from.Bottom + (to.Bottom - from.Bottom) * eased)
            };
            SetWindowBounds(bounds);
            await Task.Delay(16);
        }

        SetWindowBounds(to);
    }

    private void SetWindowBounds(RECT bounds)
    {
        if (!SetWindowPos(
                _windowHandle,
                IntPtr.Zero,
                bounds.Left,
                bounds.Top,
                bounds.Right - bounds.Left,
                bounds.Bottom - bounds.Top,
                SwpNoZOrder | SwpNoActivate))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
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
                Text = "Loading saved sites...",
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
                Text = "Loading sites...",
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
                    Text = "Loading sites...",
                    Foreground = GetBrush("Muted"),
                    FontSize = 12,
                    Margin = new Thickness(10, 14, 10, 14)
                });
                siteCount.Text = "Loading saved sites...";
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
