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
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace TabloidBrowser;

public partial class MainWindow
{
    private IntPtr _windowHandle;
    private bool _isFindBarDragging;
    private bool _isFullScreen;
    private bool _closeCleanupStarted;
    private bool _closeCleanupComplete;
    private WindowState _previousWindowState;
    private Point _findBarDragStart;
    private bool _suppressFindTextChanged;
    private volatile BrowserTab? _pendingReaderTab;
    private string? _readabilityCache;
    private readonly SemaphoreSlim _readabilityCacheLock = new(1, 1);

    private void ApplyWindowMode()
    {
        if (!_isPrivate)
        {
            LocalDataText.Text = "Local";
            return;
        }

        Title = "Tabloid Browser - Private";
        LocalDataText.Text = "Private";
        LocalDataText.Foreground = GetBrush("Pink");
        PrivateButton.Content = "PVT";
        PrivateButton.ToolTip = "Private window";
        PrivateButton.IsEnabled = false;
        StatusText.Text = "Private browsing";
    }

    private Brush GetBrush(string key)
    {
        try { return (Brush)FindResource(key); }
        catch (ResourceReferenceKeyNotFoundException)
        {
            Debug.WriteLine($"Missing resource key: {key}");
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
        AddressBox.Text = _activeTab?.Address ?? string.Empty;
    }

    private void UpdateNavigationButtons()
    {
        var tab = _activeTab;
        var webView = tab?.Kind == BrowserTabKind.Web ? tab.WebView : null;
        BackButton.IsEnabled = webView?.CanGoBack == true;
        ForwardButton.IsEnabled = webView?.CanGoForward == true;
    }

    private void UpdateBookmarkButton()
    {
        var tab = _activeTab;
        var canBookmark = tab is { Kind: BrowserTabKind.Web } && BrowserDataStore.IsHttpUrl(tab.Address);
        BookmarkButton.IsEnabled = canBookmark;

        if (!canBookmark || tab is null)
        {
            BookmarkButton.Content = "\uE734";
            BookmarkButton.BorderBrush = GetBrush("Stroke");
            BookmarkButton.ToolTip = "Open a web page to bookmark";
            return;
        }

        var isBookmarked = _dataStore.IsBookmarked(tab.Address);
        BookmarkButton.Content = isBookmarked ? "\uE735" : "\uE734";
        BookmarkButton.BorderBrush = isBookmarked ? GetBrush("Pink") : GetBrush("Stroke");
        BookmarkButton.ToolTip = isBookmarked ? "Remove bookmark" : "Add bookmark";
    }

    private void UpdateLoadingOverlay()
    {
        var tab = _activeTab;
        var show = tab?.IsLoading == true;
        LoadingOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
            LoadingTitleText.Text = $"Loading {TrimUrl(tab?.Address ?? string.Empty)}";
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
            if (MemoryButton.RenderTransform is ScaleTransform scale)
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
            }
        }
        _lastInactiveCount = inactive;

        MemoryButton.Content = inactive == 0 ? "RAM" : inactive.ToString();
        MemoryButton.ToolTip = inactive == 0
            ? "Click to hibernate all inactive tabs"
            : $"{hibernated} hibernated, {suspended} suspended — click to hibernate all";
        MemoryStatusText.Text = inactive == 0
            ? "0 hibernated — click RAM to hibernate"
            : suspended == 0
                ? $"{hibernated} hibernated — click RAM to hibernate"
                : $"{hibernated} hibernated, {suspended} suspended — click RAM";
    }

    private void FocusStartSearchIfActive()
    {
        var tab = _activeTab;
        if (tab?.Kind != BrowserTabKind.Start)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (tab?.Kind != BrowserTabKind.Start)
            {
                return;
            }

            StartSearchBox.Focus();
            FocusManager.SetFocusedElement(this, StartSearchBox);
            Keyboard.Focus(StartSearchBox);
            StartSearchBox.SelectAll();
        }, DispatcherPriority.ApplicationIdle);
    }

    private async Task ClearBrowsingDataAsync()
    {
        try
        {
            var profile = _activeTab?.WebView?.CoreWebView2?.Profile;
            if (profile is not null)
            {
                await profile.ClearBrowsingDataAsync();
            }
        }
        catch (InvalidOperationException ex)
        {
            Debug.WriteLine(ex);
        }
        catch (COMException ex)
        {
            Debug.WriteLine(ex);
        }

        _dataStore.ClearBrowsingState();
        StatusText.Text = "Local browsing data cleared";
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
        StatusText.Text = $"Imported {count} bookmarks";
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
        StatusText.Text = "Bookmarks exported";
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

    private async void NewTab_Click(object sender, RoutedEventArgs e)
    {
        try { await AddStartTabAsync(); }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    private async void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement { Tag: BrowserTab tab })
            {
                await CloseTabAsync(tab);
            }
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Button { Tag: BrowserTab tab } && tab.WebView?.CoreWebView2 is not null)
            {
                tab.IsMuted = !tab.IsMuted;
                tab.WebView.CoreWebView2.IsMuted = tab.IsMuted;
            }
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    private async void Tab_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (IsFromControl<Button>(e.OriginalSource))
            {
                return;
            }

            if (sender is FrameworkElement { Tag: BrowserTab tab } && tab != _activeTab)
            {
                e.Handled = true;
                await ActivateTabAsync(tab);
            }
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    private static bool IsFromControl<TControl>(object source)
        where TControl : DependencyObject
    {
        for (var current = source as DependencyObject; current is not null; current = GetVisualOrContentParent(current))
        {
            if (current is TControl)
            {
                return true;
            }
        }

        return false;
    }

    private static DependencyObject? GetVisualOrContentParent(DependencyObject current)
    {
        if (current is Visual || current is System.Windows.Media.Media3D.Visual3D)
        {
            return VisualTreeHelper.GetParent(current);
        }

        return current is FrameworkContentElement contentElement ? contentElement.Parent : null;
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found)
            {
                return found;
            }

            var deeper = FindChild<T>(child);
            if (deeper is not null)
            {
                return deeper;
            }
        }
        return null;
    }

    private async void QuickDuckDuckGo_Click(object sender, RoutedEventArgs e)
    {
        try { await NavigateQuickAsync("https://duckduckgo.com"); }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    private async void QuickStartpage_Click(object sender, RoutedEventArgs e)
    {
        try { await NavigateQuickAsync("https://startpage.com"); }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    private async void QuickGitHub_Click(object sender, RoutedEventArgs e)
    {
        try { await NavigateQuickAsync("https://github.com"); }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    private async void QuickHackerNews_Click(object sender, RoutedEventArgs e)
    {
        try { await NavigateQuickAsync("https://news.ycombinator.com"); }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    private static (ModifierKeys Mods, Key Key) ParseShortcut(string combo)
    {
        var mods = ModifierKeys.None;
        var parts = combo.Split('+');
        foreach (var p in parts.Take(parts.Length - 1))
        {
            mods |= p.Trim() switch
            {
                "Ctrl" => ModifierKeys.Control,
                "Shift" => ModifierKeys.Shift,
                "Alt" => ModifierKeys.Alt,
                _ => ModifierKeys.None
            };
        }
        var keyStr = parts.Last().Trim();
        if (Enum.TryParse<Key>(keyStr, true, out var key)) return (mods, key);
        return (ModifierKeys.None, Key.None);
    }

    private static string FormatShortcut((ModifierKeys Mods, Key Key) combo)
    {
        var parts = new List<string>();
        if (combo.Mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (combo.Mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (combo.Mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        parts.Add(combo.Key.ToString());
        return string.Join("+", parts);
    }

    private bool MatchesShortcut(KeyEventArgs e, string shortcutId)
    {
        var combo = BrowserSettings.GetShortcut(_dataStore.Settings.Shortcuts, shortcutId);
        if (string.IsNullOrEmpty(combo)) return false;
        var (mods, key) = ParseShortcut(combo);
        return mods == Keyboard.Modifiers && key == e.Key && key != Key.None;
    }

    private async void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        try
        {
        if (MatchesShortcut(e, "focus_address"))
        {
            AddressBox.Focus();
            AddressBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (MatchesShortcut(e, "new_tab"))
        {
            await AddStartTabAsync();
            e.Handled = true;
            return;
        }

        var activeTab = _activeTab;
        if (MatchesShortcut(e, "close_tab") && activeTab is not null)
        {
            await CloseTabAsync(activeTab);
            e.Handled = true;
            return;
        }

        if (MatchesShortcut(e, "reload"))
        {
            Reload_Click(sender, e);
            e.Handled = true;
            return;
        }

        if (MatchesShortcut(e, "history"))
        {
            await AddNativePageTabAsync(BrowserTabKind.History);
            e.Handled = true;
            return;
        }

        if (MatchesShortcut(e, "print"))
        {
            PrintPage();
            e.Handled = true;
            return;
        }

        if (MatchesShortcut(e, "reader_mode"))
        {
            FireAndForget(ToggleReaderModeAsync(), "ToggleReaderModeAsync");
            e.Handled = true;
            return;
        }

        if (MatchesShortcut(e, "find"))
        {
            ShowFindBar();
            e.Handled = true;
            return;
        }

        if (MatchesShortcut(e, "ai_toggle"))
        {
            ToggleAiSidebar();
            e.Handled = true;
            return;
        }

        // Ctrl+Tab / Ctrl+Shift+Tab to switch tabs
        if (e.Key == Key.Tab && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            var snapshot = _tabs.Where(t => t.Kind == BrowserTabKind.Web || t.Kind == BrowserTabKind.Start).ToList();
            if (snapshot.Count < 2) return;
            var active = _activeTab;
            if (active is null || !snapshot.Contains(active)) return;
            var currentIdx = snapshot.IndexOf(active);
            int nextIdx;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                nextIdx = (currentIdx - 1 + snapshot.Count) % snapshot.Count;
            else
                nextIdx = (currentIdx + 1) % snapshot.Count;
            FireAndForget(ActivateTabAsync(snapshot[nextIdx]), "ActivateTabAsync");
            return;
        }

        // Zoom shortcuts
        if ((e.Key == Key.OemPlus || e.Key == Key.Add) && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (_activeTab is not null) ApplyZoom(_activeTab, 0.1);
            e.Handled = true;
            return;
        }
        if ((e.Key == Key.OemMinus || e.Key == Key.Subtract) && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (_activeTab is not null) ApplyZoom(_activeTab, -0.1);
            e.Handled = true;
            return;
        }
        if ((e.Key == Key.D0 || e.Key == Key.NumPad0) && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ResetZoom();
            e.Handled = true;
            return;
        }

        // Full-screen
        if (e.Key == Key.F11)
        {
            ToggleFullScreen();
            e.Handled = true;
            return;
        }

        // Ctrl+W as a fallback to close the current tab
        if (e.Key == Key.W && Keyboard.Modifiers == ModifierKeys.Control && activeTab is not null)
        {
            await CloseTabAsync(activeTab);
            e.Handled = true;
            return;
        }
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    private void TitleBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
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

        if (IsFromControl<Button>(e.OriginalSource))
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
            StatusText.Text = $"Drag error: {ex.Message}";
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

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized) return;

        if (WindowState != WindowState.Maximized)
        {
            RootGrid.Margin = new Thickness(0);
            MaximizeRestoreBtn.Content = "\uE922";
            MaximizeRestoreBtn.ToolTip = "Maximize";
            return;
        }

        {
            UpdateMaximizedMargins();

            MaximizeRestoreBtn.Content = "\uE923";
            MaximizeRestoreBtn.ToolTip = "Restore";
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

        RootGrid.Margin = new Thickness(
            (info.rcWork.Left - info.rcMonitor.Left) / sx + border,
            (info.rcWork.Top - info.rcMonitor.Top) / sy + border,
            (info.rcMonitor.Right - info.rcWork.Right) / sx + border,
            (info.rcMonitor.Bottom - info.rcWork.Bottom) / sy + border);
    }

    private void ToggleMaximizeRestore()
    {
        if (WindowState == WindowState.Maximized)
            SendMessage(_windowHandle, WM_SYSCOMMAND, new IntPtr(SC_RESTORE), IntPtr.Zero);
        else
            SendMessage(_windowHandle, WM_SYSCOMMAND, new IntPtr(SC_MAXIMIZE), IntPtr.Zero);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var tab = _activeTab;
            if (tab?.WebView?.CanGoBack == true)
            {
                tab.WebView.GoBack();
            }
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var tab = _activeTab;
            if (tab?.WebView?.CanGoForward == true)
            {
                tab.WebView.GoForward();
            }
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        var tab = _activeTab;
        if (tab is null)
        {
            return;
        }

        if (tab.Kind == BrowserTabKind.Web)
        {
            if (tab.IsLoading)
            {
                tab.WebView?.CoreWebView2?.Stop();
                UpdateReloadStopButton();
                return;
            }
            tab.WebView?.Reload();
            return;
        }

        RenderNativeSurface(tab, forceRebuild: true);
    }

    private void Print_Click(object sender, RoutedEventArgs e) => PrintPage();

    private void FindInPage_Click(object sender, RoutedEventArgs e) => ShowFindBar();

    private void ZoomIn_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab is not null) ApplyZoom(_activeTab, 0.1);
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab is not null) ApplyZoom(_activeTab, -0.1);
    }

    private void ResetZoom_Click(object sender, RoutedEventArgs e) => ResetZoom();

    private void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void SiteData_Click(object sender, RoutedEventArgs e) => ShowSiteDataPopup();

    private void ShowFindBar()
    {
        if (FindPopup.IsOpen) { FindTextBox.Focus(); FindTextBox.SelectAll(); return; }
        var cwv = _activeTab?.WebView?.CoreWebView2;
        if (cwv is null) return;
        _suppressFindTextChanged = true;
        FindTextBox.Text = string.Empty;
        _suppressFindTextChanged = false;
        FindMatchCount.Text = string.Empty;
        var host = BrowserHost;
        FindPopup.PlacementTarget = null;
        FindPopup.Placement = System.Windows.Controls.Primitives.PlacementMode.Absolute;
        var screenPoint = host.PointToScreen(new Point(0, 0));
        FindPopup.IsOpen = true;
        FindPopup.UpdateLayout();
        Dispatcher.BeginInvoke(() =>
        {
            if (!FindPopup.IsOpen) return;
            FindPopup.HorizontalOffset = screenPoint.X + host.ActualWidth - FindPopupContent.ActualWidth;
            FindPopup.VerticalOffset = screenPoint.Y;
        }, DispatcherPriority.Loaded);
        FindTextBox.Focus();
        var opts = cwv.Environment.CreateFindOptions();
        opts.SuppressDefaultFindDialog = true;
        FireAndForget(cwv.Find.StartAsync(opts), "Find.StartAsync");
    }

    private void HideFindBar()
    {
        if (!FindPopup.IsOpen) return;
        FindPopup.IsOpen = false;
        FindTextBox.Text = string.Empty;
        FindMatchCount.Text = string.Empty;
        var cwv = _activeTab?.WebView?.CoreWebView2;
        if (cwv is null) return;
        cwv.Find.Stop();
    }

    private void FindTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!FindPopup.IsOpen || _suppressFindTextChanged) return;
        var tab = _activeTab;
        var find = tab?.WebView?.CoreWebView2?.Find;
        if (find is null) return;
        var env = _environment;
        if (env is null) return;
        var text = FindTextBox.Text;
        if (string.IsNullOrEmpty(text))
        {
            find.Stop();
            FindMatchCount.Text = string.Empty;
            return;
        }
        var opts = env.CreateFindOptions();
        opts.FindTerm = text;
        opts.SuppressDefaultFindDialog = true;
        FireAndForget(find.StartAsync(opts), "Find.StartAsync");
    }

    private void FindTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HideFindBar();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter)
        {
            if (Keyboard.Modifiers == ModifierKeys.Shift)
            {
                FindPrev();
            }
            else
            {
                FindNext();
            }
            e.Handled = true;
        }
    }

    private void FindNext_Click(object sender, RoutedEventArgs e) => FindNext();
    private void FindPrev_Click(object sender, RoutedEventArgs e) => FindPrev();
    private void FindClose_Click(object sender, RoutedEventArgs e) => HideFindBar();

    private void FindNext()
    {
        var find = _activeTab?.WebView?.CoreWebView2?.Find;
        if (find is null) return;
        try { find.FindNext(); }
        catch (Exception ex) { Debug.WriteLine($"FindNext failed: {ex}"); }
    }

    private void FindPrev()
    {
        var find = _activeTab?.WebView?.CoreWebView2?.Find;
        if (find is null) return;
        try { find.FindPrevious(); }
        catch (Exception ex) { Debug.WriteLine($"FindPrev failed: {ex}"); }
    }

    private void Find_MatchChanged(object? sender, object? e)
    {
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                var find = _activeTab?.WebView?.CoreWebView2?.Find;
                if (find is null || !FindPopup.IsOpen) return;
                var count = find.MatchCount;
                var index = find.ActiveMatchIndex;
                FindMatchCount.Text = count > 0 ? $"{index + 1}/{count}" : "0/0";
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Find_MatchChanged failed: {ex}");
        }
    }

    private void FindDragGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isFindBarDragging = true;
        _findBarDragStart = e.GetPosition(this);
        FindDragGrip.CaptureMouse();
        FindDragGrip.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void FindDragGrip_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isFindBarDragging || !FindDragGrip.IsMouseCaptured) return;
        var pos = e.GetPosition(this);
        var dx = pos.X - _findBarDragStart.X;
        var dy = pos.Y - _findBarDragStart.Y;
        FindPopup.HorizontalOffset += dx;
        FindPopup.VerticalOffset += dy;
        _findBarDragStart = pos;
    }

    private void FindDragGrip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isFindBarDragging) return;
        _isFindBarDragging = false;
        FindDragGrip.ReleaseMouseCapture();
        FindDragGrip.Cursor = Cursors.Hand;
        e.Handled = true;
    }

    private void PrintPage()
    {
        var cwv = _activeTab?.WebView?.CoreWebView2;
        if (cwv is null) return;
        try
        {
            cwv.ShowPrintUI();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Print failed: {ex.Message}";
        }
    }

    private void ReaderMode_Click(object sender, RoutedEventArgs e) => FireAndForget(ToggleReaderModeAsync(), "ToggleReaderModeAsync");

    private Brush GetReaderActiveBrush() => GetBrush("Cyan");

    private async Task ToggleReaderModeAsync()
    {
        var tab = _activeTab;
        if (tab?.WebView?.CoreWebView2 is null) return;
        var webView = tab.WebView.CoreWebView2;

        try
        {
            if (tab.IsReaderMode)
            {
                Interlocked.Exchange(ref _pendingReaderTab, null);
                await webView.ExecuteScriptAsync(ReaderViewCleanupScript);
                tab.IsReaderMode = false;
                UpdateReaderUi(false);
                return;
            }

            var extraction = await BuildReaderExtractionScriptAsync();
            Interlocked.Exchange(ref _pendingReaderTab, tab);
            await webView.ExecuteScriptAsync(extraction);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ToggleReaderModeAsync failed: {ex}");
        }
    }

    private async Task<string> BuildReaderExtractionScriptAsync()
    {
        var js = await GetReadabilityScriptAsync();
        return $$"""
        (() => {
        {{js}}
        try {
            var doc = document.cloneNode(true);
            var reader = new Readability(doc);
            var article = reader.parse();
            if (article) {
                chrome.webview.postMessage({
                    type: 'reader-content',
                    title: article.title || '',
                    byline: article.byline || null,
                    siteName: article.siteName || null,
                    publishedTime: article.publishedTime || null,
                    content: article.content || ''
                });
            } else {
                chrome.webview.postMessage({ type: 'reader-content', error: 'Could not parse article' });
            }
        } catch(e) {
            chrome.webview.postMessage({ type: 'reader-content', error: e.message });
        }
        })();
        """;
    }

    private async Task RenderReaderViewAsync(BrowserTab tab, ReaderArticle article)
    {
        var webView = tab.WebView?.CoreWebView2;
        if (webView is null) return;

        var json = JsonSerializer.Serialize(article, _jsonOpts);

        var render = $$"""
        (() => {
        function esc(s){return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;')}
        var data = {{json}};
        var id = 'tbl-reader-view';
        if (document.getElementById(id)) return;
        var fs = 20, theme = 'dark';
        var css = document.createElement('style');
        css.id = id + '-css';
        css.textContent =
        ':root{--r-fs:'+fs+'px;--r-lh:1.7;--r-bg:#1a1a1a;--r-text:#e0e0e0;--r-muted:#999;--r-bar:#222;--r-border:#333;--r-link:#74b9ff}'+
        '.r-light{--r-bg:#fafafa;--r-text:#222;--r-muted:#888;--r-bar:#eee;--r-border:#ddd;--r-link:#1a73e8}'+
        '#tbl-reader-view{all:initial;position:fixed;top:0;left:0;right:0;bottom:0;z-index:2147483647;overflow-y:auto;background:var(--r-bg);color:var(--r-text);font:var(--r-fs)/var(--r-lh) Georgia,serif}'+
        '#tbl-reader-bar{position:sticky;top:0;z-index:1;display:flex;align-items:center;gap:8px;padding:8px 16px;background:var(--r-bar);border-bottom:1px solid var(--r-border);font-family:sans-serif}'+
        '#tbl-reader-bar button{background:transparent;color:var(--r-text);border:1px solid var(--r-border);border-radius:4px;padding:4px 10px;cursor:pointer;font-size:12px;font-family:inherit}'+
        '#tbl-reader-bar button:hover{background:rgba(255,255,255,0.1)}'+
        '#tbl-reader-progress{position:fixed;top:0;left:0;height:3px;background:var(--r-link);width:0;z-index:2;transition:width 0.1s}'+
        '#tbl-reader-content{max-width:720px;margin:0 auto;padding:40px 24px}'+
        '#tbl-reader-content h1{font-size:1.8em;margin:0 0 8px;font-weight:700}'+
        '#tbl-reader-content .r-meta{color:var(--r-muted);font-size:0.85em;font-family:sans-serif;margin:0 0 32px}'+
        '#tbl-reader-content .r-meta span{margin-right:12px}'+
        '#tbl-reader-content p{margin:0 0 1.2em}'+
        '#tbl-reader-content img{max-width:100%;height:auto;display:block;margin:1.5em auto;border-radius:4px}'+
        '#tbl-reader-content blockquote{border-left:3px solid var(--r-border);margin:1.2em 0;padding:4px 0 4px 20px;color:var(--r-muted)}'+
        '#tbl-reader-content pre,#tbl-reader-content code{background:rgba(255,255,255,0.06);border-radius:3px;font-size:0.9em}'+
        '#tbl-reader-content pre{padding:16px;overflow-x:auto}'+
        '#tbl-reader-content code{padding:2px 5px}'+
        '#tbl-reader-content pre code{padding:0}'+
        '#tbl-reader-content a{color:var(--r-link)}'+
        '#tbl-reader-content h2,#tbl-reader-content h3,#tbl-reader-content h4{margin:1.4em 0 0.4em}'+
        '#tbl-reader-content ul,#tbl-reader-content ol{padding-left:24px}'+
        '#tbl-reader-content figure{margin:1.5em 0;text-align:center}'+
        '#tbl-reader-content figcaption{font-size:0.85em;color:var(--r-muted);margin-top:4px}';
        document.documentElement.appendChild(css);
        var bar = document.createElement('div');
        bar.id = 'tbl-reader-bar';
        bar.innerHTML =
        '<button onclick="chrome.webview.postMessage({type:\'reader-action\',action:\'exit\'})">\u2716 Exit</button>'+
        '<span style="flex:1;font-size:13px;font-weight:600;overflow:hidden;text-overflow:ellipsis;white-space:nowrap">'+esc(data.title)+'</span>'+
        '<button onclick="document.getElementById(\'tbl-reader-view\').style.setProperty(\'--r-fs\',Math.max(12,parseInt(getComputedStyle(document.getElementById(\'tbl-reader-view\')).getPropertyValue(\'--r-fs\'))-2)+\'px\')">A\u2212</button>'+
        '<button onclick="document.getElementById(\'tbl-reader-view\').style.setProperty(\'--r-fs\',Math.min(36,parseInt(getComputedStyle(document.getElementById(\'tbl-reader-view\')).getPropertyValue(\'--r-fs\'))+2)+\'px\')">A+</button>'+
        '<button onclick="var v=document.getElementById(\'tbl-reader-view\');v.classList.toggle(\'r-light\')">\u263C</button>';
        var progress = document.createElement('div');
        progress.id = 'tbl-reader-progress';
        var main = document.createElement('div');
        main.id = 'tbl-reader-content';
        var meta = '';
        if (data.byline) meta += '<span>\u270E '+esc(data.byline)+'</span>';
        if (data.siteName) meta += '<span>'+esc(data.siteName)+'</span>';
        if (data.publishedTime) meta += '<span>'+esc(data.publishedTime.split('T')[0])+'</span>';
        function sanitize(html){
          var parser = new DOMParser();
          var doc = parser.parseFromString(html, 'text/html');
          var w={'P':[],'B':[],'I':[],'STRONG':[],'EM':[],'U':[],'S':[],'BR':[],'HR':[],'A':['href','title'],'IMG':['src','alt','title'],'UL':[],'OL':[],'LI':[],'H1':[],'H2':[],'H3':[],'H4':[],'H5':[],'H6':[],'BLOCKQUOTE':[],'PRE':[],'CODE':[],'SPAN':['title'],'DIV':[]};
          var p=/^https?|mailto|tel|ftp:/i;
          var all=doc.body.querySelectorAll('*');
          for(var i=all.length-1;i>=0;i--){
            var e=all[i];
            var t=e.tagName.toUpperCase();
            if(!w[t]){
              while(e.firstChild)e.parentNode.insertBefore(e.firstChild,e);
              e.remove();
              continue;
            }
            for(var j=e.attributes.length-1;j>=0;j--){
              var a=e.attributes[j].name.toLowerCase();
              if(!w[t].includes(a)||(['href','src'].includes(a)&&!p.test(e.getAttribute(a)))){
                e.removeAttribute(e.attributes[j].name);
              }
            }
            if(t==='A')e.setAttribute('rel','noopener noreferrer nofollow');
          }
          return doc.body.innerHTML;
        }
        main.innerHTML =
        '<h1>'+esc(data.title)+'</h1>'+
        (meta ? '<div class="r-meta">'+meta+'</div>' : '')+
        sanitize(data.content);
        var view = document.createElement('div');
        view.id = id;
        view.appendChild(bar);
        view.appendChild(progress);
        view.appendChild(main);
        document.body.appendChild(view);
        view.addEventListener('scroll',function(){var p=view.scrollTop/(view.scrollHeight-view.clientHeight);progress.style.width=Math.min(100,p*100)+'%'});
        })();
        """;
        await webView.ExecuteScriptAsync(render);
    }

    private async Task TryInjectReaderDetectionAsync(CoreWebView2 webView)
    {
        try
        {
            await webView.ExecuteScriptAsync(ReaderDetectionScript);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Reader detection failed: {ex}");
        }
    }

    private void SetReaderButtonVisibility(bool visible)
    {
        if (Dispatcher.CheckAccess())
        {
            ApplyReaderButtonVisibility(visible);
        }
        else
        {
            Dispatcher.InvokeAsync(() => ApplyReaderButtonVisibility(visible));
        }
    }

    private void ApplyReaderButtonVisibility(bool visible)
    {
        if (ReaderButton is not null)
            ReaderButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (ReaderModeMenuItem is not null)
            ReaderModeMenuItem.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateReaderUi(bool isActive)
    {
        if (ReaderModeMenuItem is not null)
        {
            ReaderModeMenuItem.IsChecked = isActive;
        }
        if (ReaderButton is not null)
        {
            ReaderButton.BorderBrush = isActive ? GetReaderActiveBrush() : null;
            ReaderButton.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private async Task<string> GetReadabilityScriptAsync()
    {
        if (_readabilityCache is not null) return _readabilityCache;
        await _readabilityCacheLock.WaitAsync();
        try
        {
            if (_readabilityCache is not null) return _readabilityCache;
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "readability.min.js");
            if (File.Exists(path))
            {
                _readabilityCache = await File.ReadAllTextAsync(path).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(_readabilityCache))
                    throw new InvalidOperationException($"Readability script at '{path}' is empty");
                return _readabilityCache;
            }
            throw new FileNotFoundException($"Readability script not found at '{path}'", path);
        }
        finally { _readabilityCacheLock.Release(); }
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
                snapshot = _tabs.Where(tab => tab != _activeTab && tab.Kind == BrowserTabKind.Web && !tab.IsSleeping).ToList();
            }
            finally { _tabOperationLock.Release(); }
            await Task.WhenAll(snapshot.Select(HibernateTabAsync));
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    private async void ClearData_Click(object sender, RoutedEventArgs e)
    {
        try { await ClearBrowsingDataAsync(); }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    private void Bookmark_Click(object sender, RoutedEventArgs e)
    {
        var tab = _activeTab;
        if (tab is not { Kind: BrowserTabKind.Web } || !BrowserDataStore.IsHttpUrl(tab.Address))
        {
            StatusText.Text = "Open a web page before bookmarking";
            return;
        }

        if (_dataStore.IsBookmarked(tab.Address))
        {
            _dataStore.RemoveBookmark(tab.Address);
            StatusText.Text = "Bookmark removed";
        }
        else
        {
            _dataStore.AddBookmark(tab.Title, tab.Address);
            StatusText.Text = "Bookmarked";
        }

        PopulateBookmarkBar();
        UpdateBookmarkButton();
    }

    private async void ShowBookmarks_Click(object sender, RoutedEventArgs e)
    {
        try { await AddNativePageTabAsync(BrowserTabKind.Bookmarks); }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    private async void ShowHistory_Click(object sender, RoutedEventArgs e)
    {
        try { await AddNativePageTabAsync(BrowserTabKind.History); }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    private async void ShowDownloads_Click(object sender, RoutedEventArgs e)
    {
        try { await AddNativePageTabAsync(BrowserTabKind.Downloads); }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    private void Privacy_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = _isPrivate
            ? "Private mode uses an isolated temporary profile"
            : $"Tracker blocking {(_dataStore.Settings.BlockTrackers ? "on" : "off")}; blocked {_blockedTrackerCount} requests";

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

    private void Menu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.ContextMenu is null)
        {
            return;
        }

        ForceDarkMenuItem.IsChecked = _dataStore.Settings.ForceDarkPage;
        var ublock = _dataStore.Settings.Extensions.FirstOrDefault(e => e.Id == "cjpalhdlnbpafiamejdnhcphjbkeiagm");
        UblockMenuItem.IsChecked = ublock?.Enabled ?? false;
        element.ContextMenu.PlacementTarget = element;
        element.ContextMenu.Placement = PlacementMode.Bottom;
        element.ContextMenu.IsOpen = true;
    }

    private void ForceDarkMenu_Checked(object sender, RoutedEventArgs e)
    {
        var settings = _dataStore.Settings with { ForceDarkPage = true };
        _dataStore.SaveSettings(settings);
        ApplyForceDarkToExistingTabs();
    }

    private void ForceDarkMenu_Unchecked(object sender, RoutedEventArgs e)
    {
        var settings = _dataStore.Settings with { ForceDarkPage = false };
        _dataStore.SaveSettings(settings);
        ApplyForceDarkToExistingTabs();
    }

    private async void ShowSettings_Click(object sender, RoutedEventArgs e)
    {
        try { await AddNativePageTabAsync(BrowserTabKind.Settings); }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    private async void UblockToggle_Click(object sender, RoutedEventArgs e)
    {
        const string ublockId = "cjpalhdlnbpafiamejdnhcphjbkeiagm";
        var ext = _dataStore.Settings.Extensions.FirstOrDefault(e => e.Id == ublockId);
        if (ext is null)
        {
            StatusText.Text = "Downloading uBlock Origin...";
            await DownloadAndInstallCrxAsync(ublockId);
            if (_activeTab is not null) RenderNativeSurface(_activeTab, forceRebuild: true);
            return;
        }

        var newState = !ext.Enabled;

        if (_loadedExtensions.TryGetValue(ublockId, out var webExt))
        {
            await webExt.EnableAsync(newState);
        }

        var updated = _dataStore.Settings.Extensions
            .Select(e => e.Id == ublockId ? e with { Enabled = newState } : e)
            .ToList();
        _dataStore.SaveSettings(_dataStore.Settings with { Extensions = updated });

        if (sender is MenuItem menuItem)
            menuItem.IsChecked = newState;

        StatusText.Text = $"{ext.Name} {(newState ? "enabled" : "disabled")}";
    }

    private async void ShowExtensions_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var tab = new BrowserTab
            {
                Kind = BrowserTabKind.Settings,
                Title = GetNativePageTitle(BrowserTabKind.Settings),
                Address = GetNativePageAddress(BrowserTabKind.Settings) + "#Extensions"
            };
            await _tabOperationLock.WaitAsync();
            try { _tabs.Add(tab); }
            finally { _tabOperationLock.Release(); }
            await ActivateTabAsync(tab);
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    private void CleanupOldBuiltinExtensions()
    {
        var builtinIds = new HashSet<string>
        {
            "cjpalhdlnbpafiamejdnhcphjbkeiagm",
            "eppiocemhmnlbhjplcgkofciiegomcon"
        };

        var filtered = _dataStore.Settings.Extensions
            .Where(e => !(builtinIds.Contains(e.Id) && e.IsBuiltIn))
            .ToList();

        if (filtered.Count != _dataStore.Settings.Extensions.Count)
        {
            _dataStore.SaveSettings(_dataStore.Settings with { Extensions = filtered });
        }
    }

    private async Task LoadExtensionsAsync()
    {
        if (_extensionsLoaded || _profile is null)
        {
            return;
        }

        _extensionsLoaded = true;

        CleanupOldBuiltinExtensions();

        foreach (var ext in _dataStore.Settings.Extensions)
        {
            if (ext.Enabled)
            {
                await TryLoadExtensionAsync(ext);
            }
        }
    }

    private async Task<CoreWebView2BrowserExtension?> TryLoadExtensionAsync(ExtensionInfo ext)
    {
        try
        {
            if (!Directory.Exists(ext.FolderPath))
            {
                return null;
            }

            if (_profile is null) return null;
            var webExt = await _profile.AddBrowserExtensionAsync(ext.FolderPath);
            _loadedExtensions[webExt.Id] = webExt;

            if (!ext.Enabled)
            {
                await webExt.EnableAsync(false);
            }

            return webExt;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Failed to load extension '{ext.Name}': {ex.Message}";
            return null;
        }
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeCleanupComplete)
        {
            return;
        }

        e.Cancel = true;
        if (_closeCleanupStarted)
        {
            return;
        }

        _closeCleanupStarted = true;
        try
        {
            await CleanupBeforeCloseAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MainWindow_Closing failed: {ex}");
        }
        finally
        {
            _closeCleanupComplete = true;
            Closing -= MainWindow_Closing;
            Dispatcher.BeginInvoke(() => Close());
        }
    }

    private async Task CleanupBeforeCloseAsync()
    {
        try
        {
            _sleepTimer.Stop();
            _sleepTimer.Tick -= SleepTimer_Tick;
            if (_startBgTimer is not null)
            {
                _startBgTimer.Stop();
                _startBgTimer.Tick -= StartBgTimer_Tick;
            }

            if (_isPrivate)
            {
                await ClearPrivateProfileAsync();
            }
            else
            {
                if (_dataStore.Settings.AiSidebarOpen)
                    _dataStore.SaveSettings(_dataStore.Settings with { AiSidebarOpen = false });

                await CaptureScrollPositionAsync(_activeTab);
                await SaveCurrentSessionAsync();

                if (_dataStore.Settings.ClearDataOnExit)
                {
                    try
                    {
                        var profile = _activeTab?.WebView?.CoreWebView2?.Profile;
                        if (profile is not null)
                        {
                            await profile.ClearBrowsingDataAsync();
                        }
                        _dataStore.ClearBrowsingState();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(ex);
                    }
                }

                BrowserTab[] tabsSnapshot;
                await _tabOperationLock.WaitAsync();
                try { tabsSnapshot = _tabs.ToArray(); }
                finally { _tabOperationLock.Release(); }

                foreach (var tab in tabsSnapshot)
                {
                    if (tab.WebView is not null)
                    {
                        try
                        {
                            tab.RunCleanup();
                            WebViewHost.Children.Remove(tab.WebView);
                            tab.WebView.Dispose();
                            tab.WebView = null;
                        }
                        catch (Exception ex) { Debug.WriteLine($"Tab WebView dispose on close: {ex}"); }
                    }
                }
            }

            try { _carouselWebView?.Dispose(); } catch (Exception ex) { Debug.WriteLine($"Carousel WebView dispose: {ex}"); }
            try { _crxHttpClient.Dispose(); } catch (Exception ex) { Debug.WriteLine($"CrxHttpClient dispose: {ex}"); }
            try { _tabOperationLock.Dispose(); } catch (Exception ex) { Debug.WriteLine($"TabOperationLock dispose: {ex}"); }
            try { _readabilityCacheLock.Dispose(); } catch (Exception ex) { Debug.WriteLine($"ReadabilityCacheLock dispose: {ex}"); }
            try { _aiService?.Dispose(); } catch (Exception ex) { Debug.WriteLine($"AiService dispose: {ex}"); }
            try { _dataStore.Dispose(); } catch (Exception ex) { Debug.WriteLine($"DataStore dispose: {ex}"); }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"CleanupBeforeCloseAsync failed: {ex}");
        }
    }



    private void UpdateReloadStopButton()
    {
        var isLoading = _activeTab?.IsLoading == true;
        ReloadButton.Content = isLoading ? "\uE71A" : "\uE72C";
        ReloadButton.ToolTip = isLoading ? "Stop" : "Reload";
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
        if (ZoomText is null) return;
        if (Math.Abs(z - 1.0) < 0.01)
        {
            ZoomText.Visibility = Visibility.Collapsed;
        }
        else
        {
            ZoomText.Text = $"{(int)(z * 100)}%";
            ZoomText.Visibility = Visibility.Visible;
        }
    }

    private void ToggleFullScreen()
    {
        _isFullScreen = !_isFullScreen;
        if (_isFullScreen)
        {
            _previousWindowState = WindowState;
            WindowState = WindowState.Maximized;
            WindowStyle = WindowStyle.None;
            RootGrid.RowDefinitions[0].Height = new GridLength(0);
            RootGrid.RowDefinitions[1].Height = new GridLength(0);
            RootGrid.RowDefinitions[3].Height = new GridLength(0);
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            RootGrid.RowDefinitions[0].Height = new GridLength(42);
            RootGrid.RowDefinitions[1].Height = new GridLength(42);
            RootGrid.RowDefinitions[3].Height = new GridLength(22);
            WindowState = _previousWindowState;
        }
    }

    private void PopulateBookmarkBar()
    {
        BookmarkBarItems.Children.Clear();
        var bookmarks = _dataStore.Bookmarks;
        if (bookmarks.Count == 0)
        {
            BookmarkBar.Visibility = Visibility.Collapsed;
            return;
        }
        BookmarkBar.Visibility = Visibility.Visible;
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
                catch (Exception ex) { Debug.WriteLine(ex); }
            };
            BookmarkBarItems.Children.Add(btn);
        }
    }

    private async void ShowSiteDataPopup()
    {
        try
        {
        var activeTab = _activeTab;
        var cwv = activeTab?.WebView?.CoreWebView2;
        var address = activeTab?.Address ?? "";
        if (cwv is null) return;
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
            Margin = new Thickness(0, 0, 0, 12)
        });
        try
        {
            var cookies = await cwv.CookieManager.GetCookiesAsync(address);
            stack.Children.Add(new TextBlock
            {
                Text = $"Cookies: {cookies.Count}",
                Foreground = GetBrush("Text"),
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 4)
            });
        }
        catch (Exception ex) { Debug.WriteLine($"GetCookiesAsync failed: {ex}"); stack.Children.Add(new TextBlock
        {
            Text = "Cookies: unavailable",
            Foreground = GetBrush("Muted"),
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 4)
        }); }
        var clearBtn = new Button
        {
            Content = "Clear all site data",
            Height = 30,
            Margin = new Thickness(0, 12, 0, 0),
            Style = TryFindResource("MicroButton") as Style
        };
        clearBtn.Click += async (_, _) =>
        {
            try
            {
                var profile = activeTab?.WebView?.CoreWebView2?.Profile;
                if (profile is not null)
                    await profile.ClearBrowsingDataAsync();
                popup.IsOpen = false;
                StatusText.Text = "Site data cleared";
            }
            catch (Exception ex) { Debug.WriteLine(ex); }
        };
        stack.Children.Add(clearBtn);
        var closeBtn = new Button
        {
            Content = "Close",
            Height = 30,
            Margin = new Thickness(0, 8, 0, 0),
            Style = TryFindResource("MicroButton") as Style
        };
        closeBtn.Click += (_, _) => popup.IsOpen = false;
        stack.Children.Add(closeBtn);
        var border = new Border
        {
            Background = GetBrush("ChromeBg"),
            BorderBrush = GetBrush("Stroke"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(0),
            Child = stack,
            MinWidth = 300
        };
        popup.Child = border;
        popup.IsOpen = true;
        }
        catch (Exception ex) { Debug.WriteLine($"ShowSiteDataPopup failed: {ex}"); }
    }
}

