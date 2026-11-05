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
                    var fadeOut = new DoubleAnimation(1, 0, Motion.Fast)
                    {
                        EasingFunction = Motion.InOut
                    };
                    var scaleDown = new DoubleAnimation(1, 0.96, Motion.Fast)
                    {
                        EasingFunction = Motion.InOut
                    };
                    container.BeginAnimation(OpacityProperty, fadeOut);
                    if (container.RenderTransform is ScaleTransform st)
                    {
                        st.BeginAnimation(ScaleTransform.ScaleXProperty, scaleDown);
                        st.BeginAnimation(ScaleTransform.ScaleYProperty, scaleDown);
                    }
                    await Task.Delay(Motion.Fast);
                }
                await CloseTabAsync(tab);
            }
        }
        catch (Exception ex) { Log.Error(ex, "Error occurred"); }
    }

    private async void Tab_MouseMiddleButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement { Tag: BrowserTab tab })
            {
                e.Handled = true;
                await CloseTabAsync(tab);
            }
        }
        catch (Exception ex) { Log.Error(ex, "Middle-click tab close failed"); }
    }

    private async void ReopenClosedTab_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            FireAndForget(ReopenLastClosedTabAsync(), "ReopenLastClosedTabAsync");
        }
        catch (Exception ex) { Log.Error(ex, "Reopen closed tab failed"); }
    }

    private async void KeepAiTab_Click(object sender, RoutedEventArgs e)
    {        try
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
                SetStatusTextSafe($"Kept tab \"{tab.Title}\" - it's now yours.");
            }
            else
            {
                SetStatusTextSafe("Couldn't keep that tab - it's no longer AI-owned.");
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
        if (MatchesShortcut(e, "hide_ui"))
        {
            ToggleBrowserChrome();
            e.Handled = true;
            return;
        }

        if (MatchesShortcut(e, "focus_address"))
        {
            RevealBrowserChrome();
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

        if (MatchesShortcut(e, "reopen_closed_tab"))
        {
            FireAndForget(ReopenLastClosedTabAsync(), "ReopenLastClosedTabAsync");
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
            await ToggleFullScreenAsync();
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
}
