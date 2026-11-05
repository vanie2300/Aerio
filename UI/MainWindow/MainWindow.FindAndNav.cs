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

    private async void FullScreen_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ToggleFullScreenAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to toggle fullscreen");
        }
    }

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
}
