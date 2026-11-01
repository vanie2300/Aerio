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
}
