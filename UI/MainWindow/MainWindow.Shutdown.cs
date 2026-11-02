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
        WindowContentHost.Background = Brushes.Black;
        CrtShutdownOverlay.Visibility = Visibility.Visible;
        CrtShutdownOverlay.Opacity = 0;
        CrtShutdownLine.Opacity = 0;

        await AnimateElementPropertyAsync(
            CrtShutdownOverlay,
            UIElement.OpacityProperty,
            0,
            1,
            TimeSpan.FromMilliseconds(140),
            Motion.OutCubic);

        RootGrid.Visibility = Visibility.Collapsed;

        MinHeight = 0;
        var currentHeight = ActualHeight;
        var targetHeight = 2.0;
        var verticalOffset = (currentHeight - targetHeight) / 2;
        var verticalEasing = new SineEase { EasingMode = EasingMode.EaseInOut };

        var lineFadeIn = AnimateElementPropertyAsync(
            CrtShutdownLine,
            UIElement.OpacityProperty,
            0,
            1,
            TimeSpan.FromMilliseconds(110),
            Motion.OutCubic);
        var verticalCompletion = AnimateElementPropertyAsync(
            this, HeightProperty, currentHeight, targetHeight, TimeSpan.FromMilliseconds(440), verticalEasing);
        var topCompletion = AnimateElementPropertyAsync(
            this, TopProperty, Top, Top + verticalOffset, TimeSpan.FromMilliseconds(440), verticalEasing);
        await Task.WhenAll(lineFadeIn, verticalCompletion, topCompletion);

        MinWidth = 0;
        var currentWidth = ActualWidth;
        var targetWidth = 1.0;
        var horizontalOffset = (currentWidth - targetWidth) / 2;
        var horizontalEasing = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var lineFadeOut = AnimateElementPropertyAsync(
            CrtShutdownLine,
            UIElement.OpacityProperty,
            1,
            0,
            TimeSpan.FromMilliseconds(220),
            horizontalEasing);
        var horizontalCompletion = AnimateElementPropertyAsync(
            this, WidthProperty, currentWidth, targetWidth, TimeSpan.FromMilliseconds(260), horizontalEasing);
        var leftCompletion = AnimateElementPropertyAsync(
            this, LeftProperty, Left, Left + horizontalOffset, TimeSpan.FromMilliseconds(260), horizontalEasing);
        await Task.WhenAll(lineFadeOut, horizontalCompletion, leftCompletion);
    }

    private Task AnimateElementPropertyAsync(
        IAnimatable target,
        DependencyProperty property,
        double from,
        double to,
        TimeSpan duration,
        IEasingFunction easing)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var animation = new DoubleAnimation(from, to, duration)
        {
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
