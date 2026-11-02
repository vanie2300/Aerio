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
private bool _isClosing;
private bool _allowClose;
private bool _crtShutdownInProgress;
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
                // State save is still running â€” defer the close until it
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
            if (!Dispatcher.HasShutdownStarted)
                await PlayCrtShutdownAnimationAsync();
        }
        catch (Exception ex) { Log.Error(ex, "CRT shutdown animation failed"); }

        try
        {
            _allowClose = true;
            await Dispatcher.InvokeAsync(Close);
        }
        catch (Exception ex) { Log.Error(ex, "Dispatcher.Close failed during shutdown"); Environment.Exit(1); }
    }

    private async Task PlayCrtShutdownAnimationAsync()
    {
        if (_crtShutdownInProgress)
            return;

        _crtShutdownInProgress = true;
        try
        {
            if (RootGrid.ActualWidth <= 0 || RootGrid.ActualHeight <= 0)
                return;

            BitmapSource? snapshot = null;
            try
            {
                var dpi = VisualTreeHelper.GetDpi(RootGrid);
                var pixelWidth = Math.Max(1, (int)Math.Ceiling(RootGrid.ActualWidth * dpi.DpiScaleX));
                var pixelHeight = Math.Max(1, (int)Math.Ceiling(RootGrid.ActualHeight * dpi.DpiScaleY));
                var renderedFrame = new RenderTargetBitmap(
                    pixelWidth,
                    pixelHeight,
                    dpi.PixelsPerInchX,
                    dpi.PixelsPerInchY,
                    PixelFormats.Pbgra32);
                renderedFrame.Render(RootGrid);
                renderedFrame.Freeze();
                snapshot = renderedFrame;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Could not capture WPF frame for CRT shutdown; using the neutral CRT fallback");
            }

            CrtFrameScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            CrtBeamScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            CrtSnapshot.BeginAnimation(UIElement.OpacityProperty, null);
            CrtBeam.BeginAnimation(UIElement.OpacityProperty, null);
            CrtWebViewFrameHost.Children.Clear();
            CrtFrame.Source = snapshot;
            CrtSnapshot.Opacity = 1;
            CrtFrameScale.ScaleX = 1;
            CrtFrameScale.ScaleY = 1;
            CrtBeam.Opacity = 0;
            CrtBeamScale.ScaleX = 1;

            await CaptureActiveWebViewFrameAsync();

            WindowContentHost.Background = Brushes.Black;
            CrtShutdownOverlay.Visibility = Visibility.Visible;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Task.Delay(TimeSpan.FromMilliseconds(16));
            RootGrid.Visibility = Visibility.Collapsed;

            var collapseEase = new CubicEase { EasingMode = EasingMode.EaseIn };
            var smoothEase = new SineEase { EasingMode = EasingMode.EaseInOut };
            var fadeEase = new CubicEase { EasingMode = EasingMode.EaseOut };

            await Task.WhenAll(
                AnimateElementPropertyAsync(
                    CrtFrameScale,
                    ScaleTransform.ScaleYProperty,
                    1,
                    0.004,
                    TimeSpan.FromMilliseconds(480),
                    collapseEase),
                AnimateElementPropertyAsync(
                    CrtBeam,
                    UIElement.OpacityProperty,
                    0,
                    1,
                    TimeSpan.FromMilliseconds(190),
                    fadeEase,
                    TimeSpan.FromMilliseconds(290)),
                AnimateElementPropertyAsync(
                    CrtSnapshot,
                    UIElement.OpacityProperty,
                    1,
                    0,
                    TimeSpan.FromMilliseconds(130),
                    fadeEase,
                    TimeSpan.FromMilliseconds(350)));

            await Task.WhenAll(
                AnimateElementPropertyAsync(
                    CrtBeamScale,
                    ScaleTransform.ScaleXProperty,
                    1,
                    0.002,
                    TimeSpan.FromMilliseconds(330),
                    smoothEase),
                AnimateElementPropertyAsync(
                    CrtBeam,
                    UIElement.OpacityProperty,
                    1,
                    0,
                    TimeSpan.FromMilliseconds(130),
                    fadeEase,
                    TimeSpan.FromMilliseconds(300)));

            CrtFrame.Source = null;
            CrtWebViewFrameHost.Children.Clear();
        }
        finally
        {
            _crtShutdownInProgress = false;
        }
    }

    private async Task CaptureActiveWebViewFrameAsync()
    {
        var coreWebView = _activeTab?.WebView?.CoreWebView2;
        var webView = _activeTab?.WebView;
        if (coreWebView is null || webView is null ||
            BrowserPane.WebViewHost.Visibility != Visibility.Visible ||
            webView.ActualWidth <= 0 || webView.ActualHeight <= 0)
        {
            return;
        }

        try
        {
            using var stream = new MemoryStream();
            await coreWebView.CapturePreviewAsync(
                CoreWebView2CapturePreviewImageFormat.Png,
                stream).WaitAsync(TimeSpan.FromSeconds(1));
            stream.Position = 0;

            var pageFrame = new BitmapImage();
            pageFrame.BeginInit();
            pageFrame.CacheOption = BitmapCacheOption.OnLoad;
            pageFrame.StreamSource = stream;
            pageFrame.EndInit();
            pageFrame.Freeze();

            var pageOrigin = webView.TransformToAncestor(RootGrid).Transform(new Point(0, 0));
            var rootOrigin = RootGrid.TransformToAncestor(WindowContentHost).Transform(new Point(0, 0));
            var pageImage = new Image
            {
                Source = pageFrame,
                Width = webView.ActualWidth,
                Height = webView.ActualHeight,
                Stretch = Stretch.Fill
            };
            Canvas.SetLeft(pageImage, rootOrigin.X + pageOrigin.X);
            Canvas.SetTop(pageImage, rootOrigin.Y + pageOrigin.Y);
            CrtWebViewFrameHost.Children.Add(pageImage);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not capture WebView2 for CRT shutdown; using the WPF frame fallback");
            CrtWebViewFrameHost.Children.Clear();
        }
    }

    private Task AnimateElementPropertyAsync(
        IAnimatable target,
        DependencyProperty property,
        double from,
        double to,
        TimeSpan duration,
        IEasingFunction easing,
        TimeSpan? delay = null)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(duration),
            BeginTime = delay ?? TimeSpan.Zero,
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd
        };
        animation.Completed += (_, _) => completion.TrySetResult(true);
        target.BeginAnimation(property, animation);
        return completion.Task;
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
}
