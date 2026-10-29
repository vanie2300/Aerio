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
using Microsoft.Web.WebView2.Wpf;

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
    private int _chromeAnimationGeneration;
    private Point _findBarDragStart;
    private bool _suppressFindTextChanged;
    private volatile BrowserTab? _pendingReaderTab;
    private string? _readabilityCache;
    private readonly SemaphoreSlim _readabilityCacheLock = new(1, 1);

    private void ChromeAutoHideTimer_Tick(object? sender, EventArgs e)
    {
        if (!IsVisible || WindowState == WindowState.Minimized ||
            !_autoHideBrowserChromeActive || _isFullScreen)
        {
            return;
        }

        if (!GetCursorPos(out var cursor))
        {
            Log.Debug("Unable to read cursor position for browser chrome auto-hide");
            return;
        }

        if (!GetWindowRect(_windowHandle, out var windowRect))
        {
            Log.Debug("Unable to read window bounds for browser chrome auto-hide");
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
                _chromeHideAt = DateTime.MaxValue;
            }
            return;
        }

        if (TitleBar.IsKeyboardFocusWithin || NavToolbar.IsKeyboardFocusWithin ||
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
        if (_browserChromeHidden == !visible)
        {
            return;
        }

        _browserChromeHidden = !visible;
        var generation = ++_chromeAnimationGeneration;
        var chromeElements = new FrameworkElement[] { TitleBar, NavToolbar, StatusBar };
        var duration = TimeSpan.FromMilliseconds(190);

        if (visible)
        {
            RootGrid.RowDefinitions[0].Height = new GridLength(40);
            RootGrid.RowDefinitions[1].Height = new GridLength(40);
            RootGrid.RowDefinitions[3].Height = new GridLength(22);
        }

        foreach (var element in chromeElements)
        {
            var transform = element.RenderTransform as TranslateTransform;
            if (transform is null)
            {
                transform = new TranslateTransform();
                element.RenderTransform = transform;
            }

            element.BeginAnimation(OpacityProperty, null);
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            element.Opacity = visible ? 1 : 0;
            transform.Y = visible ? 0 : -8;

            var opacityAnimation = new DoubleAnimation(visible ? 0 : 1, visible ? 1 : 0, duration)
            {
                EasingFunction = new CubicEase { EasingMode = visible ? EasingMode.EaseOut : EasingMode.EaseIn },
                FillBehavior = FillBehavior.Stop
            };
            if (element == StatusBar && !visible)
            {
                opacityAnimation.Completed += (_, _) =>
                {
                    if (generation != _chromeAnimationGeneration || !_browserChromeHidden)
                    {
                        return;
                    }

                    RootGrid.RowDefinitions[0].Height = new GridLength(0);
                    RootGrid.RowDefinitions[1].Height = new GridLength(0);
                    RootGrid.RowDefinitions[3].Height = new GridLength(0);
                    foreach (var chromeElement in chromeElements)
                    {
                        chromeElement.Opacity = 1;
                        if (chromeElement.RenderTransform is TranslateTransform chromeTransform)
                        {
                            chromeTransform.Y = 0;
                        }
                    }
                };
            }
            element.BeginAnimation(OpacityProperty, opacityAnimation);
            transform.BeginAnimation(
                TranslateTransform.YProperty,
                new DoubleAnimation(visible ? -8 : 0, visible ? 0 : -8, duration)
                {
                    EasingFunction = new CubicEase { EasingMode = visible ? EasingMode.EaseOut : EasingMode.EaseIn },
                    FillBehavior = FillBehavior.Stop
                });
        }
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
            : $"{hibernated} hibernated, {suspended} suspended — click to hibernate all";
        StatusBar.MemoryStatusText.Text = inactive == 0
            ? "0 hibernated — click RAM to hibernate"
            : suspended == 0
                ? $"{hibernated} hibernated — click RAM to hibernate"
                : $"{hibernated} hibernated, {suspended} suspended — click RAM";
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

    private void NewTab_Click(object sender, RoutedEventArgs e)
    {
        try { OpenNewTabDialog(); }
        catch (Exception ex) { Log.Error(ex, "Error occurred"); }
    }

    private void OpenNewTabDialog()
    {
        BrowserPane.ShowNewTabDialog();
    }

    private bool SaveNewTabShortcuts(List<NewTabShortcut> shortcuts)
    {
        if (!_dataStore.SaveSettings(_dataStore.Settings with { NewTabShortcuts = [.. shortcuts] }))
        {
            StatusBar.StatusText.Text = "Couldn't save new-tab shortcuts";
            return false;
        }

        StatusBar.StatusText.Text = "New-tab shortcuts saved";
        return true;
    }

    private async void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement { Tag: BrowserTab tab })
            {
                if (TitleBar.TabStrip.ItemContainerGenerator.ContainerFromItem(tab) is FrameworkElement container)
                {
                    var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.12))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                    };
                    var scaleDown = new DoubleAnimation(1, 0.85, TimeSpan.FromSeconds(0.12))
                    {
                        EasingFunction = new CircleEase { EasingMode = EasingMode.EaseIn }
                    };
                    container.BeginAnimation(OpacityProperty, fadeOut);
                    if (container.RenderTransform is ScaleTransform st)
                    {
                        st.BeginAnimation(ScaleTransform.ScaleXProperty, scaleDown);
                        st.BeginAnimation(ScaleTransform.ScaleYProperty, scaleDown);
                    }
                    await Task.Delay(130);
                }
                await CloseTabAsync(tab);
            }
        }
        catch (Exception ex) { Log.Error(ex, "Error occurred"); }
    }

    private async void KeepAiTab_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not FrameworkElement { Tag: BrowserTab tab } || !tab.IsAiOwned)
            {
                return;
            }

            if (_aiTabManager is null)
            {
                SetStatusTextSafe("Keep failed: AI services not initialized.");
                return;
            }

            if (await _aiTabManager.KeepTabAsync(tab.Id))
            {
                _currentAiContext?.Workspace.KeepTab(tab.Id);
                RefreshWorkspaceUi();
                SetStatusTextSafe($"Kept tab \"{tab.Title}\" — it's now yours.");
            }
            else
            {
                SetStatusTextSafe("Couldn't keep that tab — it's no longer AI-owned.");
            }
        }
        catch (Exception ex) { Log.Error(ex, "KeepAiTab failed"); }
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
        catch (Exception ex) { Log.Error(ex, "Error occurred"); }
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
        catch (Exception ex) { Log.Error(ex, "Error occurred"); }
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
            NavToolbar.AddressBox.Focus();
            NavToolbar.AddressBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (MatchesShortcut(e, "new_tab"))
        {
            OpenNewTabDialog();
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
        catch (Exception ex) { Log.Error(ex, "Error occurred"); }
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
        if (WindowState == WindowState.Minimized) return;

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
        catch (Exception ex) { Log.Error(ex, "Error occurred"); }
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
        catch (Exception ex) { Log.Error(ex, "Error occurred"); }
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
        if (FindBar.FindPopup.IsOpen) { FindBar.FindTextBox.Focus(); FindBar.FindTextBox.SelectAll(); return; }
        var cwv = _activeTab?.WebView?.CoreWebView2;
        if (cwv is null) return;
        _suppressFindTextChanged = true;
        FindBar.FindTextBox.Text = string.Empty;
        _suppressFindTextChanged = false;
        FindBar.FindMatchCount.Text = string.Empty;
        var host = BrowserHost;
        FindBar.FindPopup.PlacementTarget = null;
        FindBar.FindPopup.Placement = System.Windows.Controls.Primitives.PlacementMode.Absolute;
        var screenPoint = host.PointToScreen(new Point(0, 0));
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice;
        if (transform.HasValue)
            screenPoint = transform.Value.Transform(screenPoint);
        FindBar.FindPopup.IsOpen = true;
        FindBar.FindPopupContent.Opacity = 0;
        FindBar.FindPopupContent.RenderTransform = new TranslateTransform(0, -8);
        FindBar.FindPopup.UpdateLayout();
        Dispatcher.BeginInvoke(() =>
        {
            if (!FindBar.FindPopup.IsOpen) return;
            FindBar.FindPopup.HorizontalOffset = screenPoint.X + host.ActualWidth - FindBar.FindPopupContent.ActualWidth;
            FindBar.FindPopup.VerticalOffset = screenPoint.Y;
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.15))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var slideDown = new DoubleAnimation(-8, 0, TimeSpan.FromSeconds(0.2))
            {
                EasingFunction = new CircleEase { EasingMode = EasingMode.EaseOut }
            };
            FindBar.FindPopupContent.BeginAnimation(OpacityProperty, fadeIn);
            (FindBar.FindPopupContent.RenderTransform as TranslateTransform)?.BeginAnimation(TranslateTransform.YProperty, slideDown);
        }, DispatcherPriority.Loaded);
        FindBar.FindTextBox.Focus();
        var opts = cwv.Environment.CreateFindOptions();
        opts.SuppressDefaultFindDialog = true;
        FireAndForget(cwv.Find.StartAsync(opts), "Find.StartAsync");
    }

    private void HideFindBar()
    {
        if (!FindBar.FindPopup.IsOpen) return;
        FindBar.FindPopup.IsOpen = false;
        FindBar.FindPopupContent.Opacity = 1;
        FindBar.FindPopupContent.RenderTransform = null;
        FindBar.FindTextBox.Text = string.Empty;
        FindBar.FindMatchCount.Text = string.Empty;
        var cwv = _activeTab?.WebView?.CoreWebView2;
        if (cwv is null) return;
        cwv.Find.Stop();
    }

    private void FindTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!FindBar.FindPopup.IsOpen || _suppressFindTextChanged) return;
        var tab = _activeTab;
        var find = tab?.WebView?.CoreWebView2?.Find;
        if (find is null) return;
        var env = _environment;
        if (env is null) return;
        var text = FindBar.FindTextBox.Text;
        if (string.IsNullOrEmpty(text))
        {
            find.Stop();
            FindBar.FindMatchCount.Text = string.Empty;
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
        catch (Exception ex) { Log.Error(ex, "FindNext failed"); }
    }

    private void FindPrev()
    {
        var find = _activeTab?.WebView?.CoreWebView2?.Find;
        if (find is null) return;
        try { find.FindPrevious(); }
        catch (Exception ex) { Log.Error(ex, "FindPrev failed"); }
    }

    private void Find_MatchChanged(object? sender, object? e)
    {
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                var find = _activeTab?.WebView?.CoreWebView2?.Find;
                if (find is null || !FindBar.FindPopup.IsOpen) return;
                var count = find.MatchCount;
                var index = find.ActiveMatchIndex;
                FindBar.FindMatchCount.Text = count > 0 ? $"{index + 1}/{count}" : "0/0";
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Find_MatchChanged failed");
        }
    }

    private void FindDragGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isFindBarDragging = true;
        _findBarDragStart = e.GetPosition(this);
        FindBar.FindDragGrip.CaptureMouse();
        FindBar.FindDragGrip.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void FindDragGrip_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isFindBarDragging || !FindBar.FindDragGrip.IsMouseCaptured) return;
        var pos = e.GetPosition(this);
        var dx = pos.X - _findBarDragStart.X;
        var dy = pos.Y - _findBarDragStart.Y;
        FindBar.FindPopup.HorizontalOffset += dx;
        FindBar.FindPopup.VerticalOffset += dy;
        _findBarDragStart = pos;
    }

    private void FindDragGrip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isFindBarDragging) return;
        _isFindBarDragging = false;
        FindBar.FindDragGrip.ReleaseMouseCapture();
        FindBar.FindDragGrip.Cursor = Cursors.Hand;
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
            StatusBar.StatusText.Text = $"Print failed: {ex.Message}";
        }
    }

    private void ReaderMode_Click(object sender, RoutedEventArgs e) => FireAndForget(ToggleReaderModeAsync(), "ToggleReaderModeAsync");

    private Brush GetReaderActiveBrush() => GetBrush("Accent");

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
            Log.Error(ex, "ToggleReaderModeAsync failed");
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
            Log.Error(ex, "Reader detection failed");
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
        if (NavToolbar.ReaderButton is not null)
            NavToolbar.ReaderButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (NavToolbar.ReaderModeMenuItem is not null)
            NavToolbar.ReaderModeMenuItem.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateReaderUi(bool isActive)
    {
        if (NavToolbar.ReaderModeMenuItem is not null)
        {
            NavToolbar.ReaderModeMenuItem.IsChecked = isActive;
        }
        if (NavToolbar.ReaderButton is not null && NavToolbar.ReaderButton.Visibility == Visibility.Visible)
        {
            NavToolbar.ReaderButton.BorderBrush = isActive ? GetReaderActiveBrush() : null;
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

    private const string UblockFolderName = "cjpalhdlnbpafiamejdnhcphjbkeiagm";

    private static bool IsUblockExtension(ExtensionInfo ext) =>
        string.Equals(Path.GetFileName(ext.FolderPath.TrimEnd('\\', '/')), UblockFolderName, StringComparison.OrdinalIgnoreCase);

    private string UblockFolderPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TabloidBrowser",
        "Extensions",
        UblockFolderName);

    private CoreWebView2BrowserExtension? _ublockExtension;

    private void Menu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.ContextMenu is null)
        {
            return;
        }

        NavToolbar.ForceDarkMenuItem.IsChecked = _dataStore.Settings.ForceDarkPage;
        NavToolbar.UblockMenuItem.IsChecked = _dataStore.Settings.UblockEnabled;
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
        catch (Exception ex) { Log.Error(ex, "Error occurred"); }
    }

    private async void UblockToggle_Click(object sender, RoutedEventArgs e)
    {
        var newState = !_dataStore.Settings.UblockEnabled;
        _dataStore.SaveSettings(_dataStore.Settings with { UblockEnabled = newState });

        if (newState)
        {
            await EnsureUblockReadyAsync();
        }
        else if (_ublockExtension is not null)
        {
            try { await _ublockExtension.EnableAsync(false); }
            catch (Exception ex) { Log.Error(ex, "uBlock disable failed"); }
        }

        if (sender is MenuItem menuItem)
            menuItem.IsChecked = newState;

        StatusBar.StatusText.Text = newState ? "uBlock Origin enabled" : "uBlock Origin disabled";
    }

    private async Task EnsureUblockReadyAsync()
    {
        if (!_dataStore.Settings.UblockEnabled)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(_privateUserDataFolder))
        {
            return;
        }

        var folder = UblockFolderPath;
        if (!File.Exists(Path.Combine(folder, "manifest.json")))
        {
            SetStatusTextSafe("Installing uBlock Origin...");
            if (!await DownloadUblockAsync(folder))
            {
                return;
            }

            var installedVersion = ReadManifestVersion(folder);
            if (installedVersion is not null && !string.Equals(_dataStore.Settings.UblockVersion, installedVersion, StringComparison.Ordinal))
            {
                _dataStore.SaveSettings(_dataStore.Settings with { UblockVersion = installedVersion });
            }
        }

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(90);
        while (_profile is null && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(500);
        }

        if (_profile is null)
        {
            return;
        }

        if (_ublockExtension is null)
        {
            _ublockExtension = await Dispatcher.InvokeAsync(async () =>
            {
                try { return await _profile!.AddBrowserExtensionAsync(folder); }
                catch (Exception ex) { Log.Error(ex, "uBlock load failed"); return null; }
            }).Task.Unwrap();
        }

        if (_ublockExtension is not null)
        {
            await Dispatcher.InvokeAsync(async () =>
            {
                try { await _ublockExtension.EnableAsync(_dataStore.Settings.UblockEnabled); }
                catch (Exception ex) { Log.Error(ex, "uBlock enable failed"); }
            }).Task.Unwrap();
        }

        await CheckUblockUpdateAsync();
    }

    private async Task CheckUblockUpdateAsync()
    {
        try
        {
            if (!_dataStore.Settings.UblockEnabled)
            {
                return;
            }

            var lastCheck = _dataStore.Settings.UblockUpdateCheckUtc;
            if (lastCheck is not null && DateTimeOffset.UtcNow - lastCheck < TimeSpan.FromDays(30))
            {
                return;
            }

            var installed = _dataStore.Settings.UblockVersion;
            var latest = await QueryLatestUblockVersionAsync();

            if (IsNewerVersion(latest, installed))
            {
                var folder = UblockFolderPath;
                if (await DownloadUblockAsync(folder))
                {
                    var version = ReadManifestVersion(folder);
                    if (version is not null)
                    {
                        if (_profile is not null)
                        {
                            _ublockExtension = await Dispatcher.InvokeAsync(async () =>
                            {
                                try
                                {
                                    var webExt = await _profile!.AddBrowserExtensionAsync(folder);
                                    await webExt.EnableAsync(true);
                                    return webExt;
                                }
                                catch (Exception ex) { Log.Error(ex, "uBlock reinstall failed"); return null; }
                            }).Task.Unwrap();
                        }

                        _dataStore.SaveSettings(_dataStore.Settings with
                        {
                            UblockVersion = version,
                            UblockUpdateCheckUtc = DateTimeOffset.UtcNow
                        });
                        SetStatusTextSafe($"uBlock Origin updated to v{version}");
                        return;
                    }
                }
            }

            _dataStore.SaveSettings(_dataStore.Settings with { UblockUpdateCheckUtc = DateTimeOffset.UtcNow });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "uBlock update check failed");
        }
    }

    private async Task<string?> QueryLatestUblockVersionAsync()
    {
        try
        {
            var url = $"https://clients2.google.com/service/update2/crx?response=redirect&prodversion=106.0.0.0&acceptformat=crx3&x=id%3D{UblockFolderName}%26installsource%3Dondemand%26uc";
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await _crxHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var finalUri = response.RequestMessage?.RequestUri?.ToString();
            return finalUri is null ? null : ParseVersionFromCrxUri(finalUri);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "uBlock version query failed");
            return null;
        }
    }

    private static string? ParseVersionFromCrxUri(string uri)
    {
        var match = Regex.Match(uri, @"_(\d+(?:_\d+)+)\.crx$", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Replace('_', '.') : null;
    }

    private static bool IsNewerVersion(string? latest, string? installed)
    {
        if (string.IsNullOrWhiteSpace(latest)) return false;
        if (string.IsNullOrWhiteSpace(installed)) return true;
        return Version.TryParse(latest, out var latestVersion)
            && Version.TryParse(installed, out var installedVersion)
            && latestVersion > installedVersion;
    }

    private async Task<bool> DownloadUblockAsync(string targetDir)
    {
        try
        {
            var url = $"https://clients2.google.com/service/update2/crx?response=redirect&prodversion=106.0.0.0&acceptformat=crx3&x=id%3D{UblockFolderName}%26installsource%3Dondemand%26uc";
            var tempDir = Path.Combine(Path.GetTempPath(), "TabloidBrowser", "CrxDownloads");
            Directory.CreateDirectory(tempDir);
            var crxPath = Path.Combine(tempDir, $"{UblockFolderName}.crx");

            using var response = await _crxHttpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            await using (var fileStream = new FileStream(crxPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await response.Content.CopyToAsync(fileStream);
            }

            var bytes = await File.ReadAllBytesAsync(crxPath);
            try { File.Delete(crxPath); } catch (Exception ex) { Log.Debug(ex, "Failed to delete CRX temp file"); }

            var zipStart = FindZipStart(bytes);
            if (zipStart < 0)
            {
                return false;
            }

            if (Directory.Exists(targetDir))
            {
                Directory.Delete(targetDir, recursive: true);
            }
            Directory.CreateDirectory(targetDir);

            using var zipStream = new MemoryStream(bytes, zipStart, bytes.Length - zipStart);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
            var normalizedTarget = Path.GetFullPath(targetDir) + Path.DirectorySeparatorChar;
            foreach (var entry in archive.Entries)
            {
                var destPath = Path.GetFullPath(Path.Combine(targetDir, entry.FullName));
                if (!destPath.StartsWith(normalizedTarget, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destPath);
                }
                else
                {
                    var parentDir = Path.GetDirectoryName(destPath);
                    if (parentDir is not null) Directory.CreateDirectory(parentDir);
                    entry.ExtractToFile(destPath, overwrite: true);
                }
            }

            return File.Exists(Path.Combine(targetDir, "manifest.json"));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "uBlock download failed");
            return false;
        }
    }

    private static string? ReadManifestVersion(string folder)
    {
        try
        {
            var manifestPath = Path.Combine(folder, "manifest.json");
            if (!File.Exists(manifestPath)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            return document.RootElement.TryGetProperty("version", out var version) ? version.GetString() : null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to read uBlock manifest version");
            return null;
        }
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
        catch (Exception ex) { Log.Error(ex, "Error occurred"); }
    }

    private void CleanupOldBuiltinExtensions()
    {
        var builtinFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "cjpalhdlnbpafiamejdnhcphjbkeiagm",
            "eppiocemhmnlbhjplcgkofciiegomcon"
        };

        var extensions = _dataStore.Settings.Extensions;
        var ublockExt = extensions.FirstOrDefault(IsUblockExtension);
        var filtered = extensions
            .Where(e => !builtinFolders.Contains(Path.GetFileName(e.FolderPath.TrimEnd('\\', '/'))))
            .ToList();

        if (filtered.Count != extensions.Count || ublockExt is not null)
        {
            _dataStore.SaveSettings(_dataStore.Settings with
            {
                Extensions = filtered,
                UblockEnabled = ublockExt is null ? _dataStore.Settings.UblockEnabled : ublockExt.Enabled
            });
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
            StatusBar.StatusText.Text = $"Failed to load extension '{ext.Name}': {ex.Message}";
            return null;
        }
    }

private bool _isClosing;
private bool _allowClose;
private Task? _deferredCloseTask;

private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        try { _aiCts?.Cancel(); } catch (Exception ex) { Log.Debug(ex, "AI cancellation on close failed"); }

        if (_allowClose)
        {
            DisposeWebViewsAndExit();
            return;
        }

        if (_isClosing)
        {
            if (_deferredCloseTask is { IsCompleted: false })
            {
                // State save is still running — defer the close until it
                // finishes instead of disposing the lock/data store under it.
                e.Cancel = true;
                return;
            }
            DisposeWebViewsAndExit();
            return;
        }

        e.Cancel = true;
        _isClosing = true;
        _deferredCloseTask = DeferredCloseAsync();
    }

private async Task DeferredCloseAsync()
    {
        try { await SaveStateBeforeCloseAsync(); }
        catch (Exception ex) { Log.Error(ex, "Save state on close failed"); }

        try
        {
            _allowClose = true;
            await Dispatcher.InvokeAsync(Close);
        }
        catch (Exception ex) { Log.Error(ex, "Dispatcher.Close failed during shutdown"); Environment.Exit(1); }
    }

private void DisposeWebViewsAndExit()
    {
        try
        {
            var snapshot = _tabs.ToArray();
            foreach (var tab in snapshot)
            {
                if (tab.WebView is not null)
                {
                    try
                    {
                        tab.RunCleanup();
                        RemoveElementFromParent(tab.WebView);
                        tab.WebView.Dispose();
                        tab.WebView = null;
                        tab.IsWebViewInitialized = false;
                    }
                    catch (Exception ex) { Log.Error(ex, "Tab WebView dispose on close"); }
                }
            }

            if (_isPrivate && !string.IsNullOrWhiteSpace(_privateUserDataFolder) && Directory.Exists(_privateUserDataFolder))
            {
                try { Directory.Delete(_privateUserDataFolder, recursive: true); }
                catch (Exception ex) { Log.Error(ex, "Private profile delete failed"); }
            }

            try { _carouselWebView?.Dispose(); } catch (Exception ex) { Log.Error(ex, "Failed to dispose carousel WebView"); }
            try { _crxHttpClient.Dispose(); } catch (Exception ex) { Log.Error(ex, "Failed to dispose CRX HTTP client"); }
            try { _tabOperationLock.Dispose(); } catch (Exception ex) { Log.Error(ex, "Failed to dispose tab operation lock"); }
            try { _readabilityCacheLock.Dispose(); } catch (Exception ex) { Log.Error(ex, "Failed to dispose readability cache lock"); }
            try { _aiService?.Dispose(); } catch (Exception ex) { Log.Error(ex, "Failed to dispose AI service"); }
            try { _searchService?.Dispose(); } catch (Exception ex) { Log.Error(ex, "Failed to dispose search service"); }
            try { _dataStore.Dispose(); } catch (Exception ex) { Log.Error(ex, "Failed to dispose data store"); }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "DisposeWebViewsAndExit failed");
        }
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
            _previousWindowState = WindowState;
            WindowState = WindowState.Maximized;
            WindowStyle = WindowStyle.None;
            SetBrowserChromeVisible(false);
        }
        else
        {
            WindowStyle = WindowStyle.None;
            SetBrowserChromeVisible(true);
            WindowState = _previousWindowState;
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
                Text = "Loading saved sites…",
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
                Text = "Loading sites…",
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
                    Text = "Loading sites…",
                    Foreground = GetBrush("Muted"),
                    FontSize = 12,
                    Margin = new Thickness(10, 14, 10, 14)
                });
                siteCount.Text = "Loading saved sites…";
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
