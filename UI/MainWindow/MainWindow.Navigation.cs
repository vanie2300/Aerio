using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace TabloidBrowser;

public partial class MainWindow
{
    private readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static bool TryGetJsonDouble(JsonElement element, string propertyName, out double value)
    {
        value = 0;
        return element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetDouble(out value);
    }

    private static string FormatScriptNumber(double value)
    {
        return Math.Max(0, value).ToString("0.###", CultureInfo.InvariantCulture);
    }

    private void RenderNativeSurface(BrowserTab tab, bool forceRebuild = false)
    {
        BrowserPane.WebViewHost.Children.Clear();
        _attachedWebView = null;
        BrowserPane.WebViewHost.Visibility = Visibility.Hidden;
        BrowserPane.LoadingOverlay.Visibility = Visibility.Collapsed;

        if (tab.Kind == BrowserTabKind.Start)
        {
            BrowserPane.InternalPageHost.Visibility = Visibility.Collapsed;
            BrowserPane.StartOverlay.Visibility = Visibility.Visible;
            BrowserPane.StartSearchBox.Text = string.Empty;
            FocusStartSearchIfActive();
            if (_dataStore.Settings.Background == "Default")
                _startBgTimer?.Start();
            return;
        }

        _startBgTimer?.Stop();
        BrowserPane.StartOverlay.Visibility = Visibility.Collapsed;
        BrowserPane.InternalPageHost.Visibility = Visibility.Visible;
        BrowserPane.InternalPageHost.Children.Clear();

        UIElement page;
        if (!forceRebuild && tab.CachedContent is not null)
        {
            page = tab.CachedContent;
            BrowserPane.InternalPageHost.Children.Add(page);
            if (tab.NativeScrollY > 0 && FindChild<ScrollViewer>(page) is { } scroll)
            {
                Dispatcher.BeginInvoke(
                    () => scroll.ScrollToVerticalOffset(Math.Max(0, tab.NativeScrollY)),
                    DispatcherPriority.Loaded);
            }
        }
        else
        {
            page = BuildNativePage(tab);
            BrowserPane.InternalPageHost.Children.Add(page);
        }

        page.Opacity = 0;
        Dispatcher.BeginInvoke(() =>
        {
            if (!BrowserPane.InternalPageHost.Children.Contains(page)) return;
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.15))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            page.BeginAnimation(OpacityProperty, fadeIn);
        }, DispatcherPriority.Loaded);
    }

    private async Task NavigateWebAsync(BrowserTab tab, string input, bool restoreScroll = false)
    {
        if (TryGetNativePageKind(input, out var nativeKind))
        {
            OpenNativePageInTab(tab, nativeKind);
            return;
        }

        var target = NormalizeInput(input);
        if (string.IsNullOrWhiteSpace(target))
        {
            OpenNativePageInTab(tab, BrowserTabKind.Start);
            return;
        }

        tab.Kind = BrowserTabKind.Web;
        tab.Address = target;
        tab.Title = "Loading...";
        tab.IsLoading = true;
        tab.RestoredUrl = null;
        tab.IsRestoredPlaceholder = false;
        tab.AllowNextNavigationScrollRestore = restoreScroll;
        tab.PendingScrollRestore = restoreScroll && HasScrollPosition(tab);
        if (!restoreScroll)
        {
            tab.ScrollX = 0;
            tab.ScrollY = 0;
        }
        RememberSitePermissionsUrl(tab);

        try
        {
            if (_activeTab == tab)
            {
                await EnsureWebViewAsync(tab, attachBeforeInitialize: true);
                AttachActiveWebView(tab);
                UpdateAddressBar();
                UpdateLoadingOverlay();
            }
            else
            {
                await EnsureWebViewAsync(tab);
            }

            tab.WebView?.CoreWebView2?.Navigate(target);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "NavigateWebAsync failed");
            if (!tab.IsWebViewInitialized && tab.WebView is not null)
            {
                tab.RunCleanup();
                BrowserPane.WebViewHost.Children.Remove(tab.WebView);
                tab.WebView.Dispose();
                tab.WebView = null;
            }

            tab.IsLoading = false;
            StatusBar.StatusText.Text = $"Navigation failed: {ex.Message}";

            if (_activeTab == tab)
            {
                OpenNativePageInTab(tab, BrowserTabKind.Start);
                UpdateLoadingOverlay();
                UpdateNavigationButtons();
                UpdateAddressBar();
            }
        }
    }

    private void OpenNativePageInTab(BrowserTab tab, BrowserTabKind kind)
    {
        var isChangingNativePage = tab.Kind != kind;
        tab.Kind = kind;
        tab.Title = GetNativePageTitle(kind);
        tab.Address = GetNativePageAddress(kind);
        tab.IsLoading = false;
        tab.RestoredUrl = null;
        tab.IsRestoredPlaceholder = false;
        tab.ScrollX = 0;
        tab.ScrollY = 0;
        tab.PendingScrollRestore = false;
        tab.AllowNextNavigationScrollRestore = false;
        if (isChangingNativePage)
        {
            tab.NativeScrollY = 0;
            tab.CachedContent = null;
        }

        if (_activeTab == tab)
        {
            RenderNativeSurface(tab);
            UpdateAddressBar();
            UpdateNavigationButtons();
            UpdateBookmarkButton();
        }
    }

    private bool TryHandleTabloidUri(BrowserTab tab, string uri)
    {
        if (TryGetNativePageKind(uri, out var kind))
        {
            OpenNativePageInTab(tab, kind);
            return true;
        }

        return false;
    }

    private UIElement BuildNativePage(BrowserTab tab)
    {
        var page = tab.Kind switch
        {
            BrowserTabKind.Bookmarks => BuildBookmarksPage(tab),
            BrowserTabKind.History => BuildHistoryPage(tab),
            BrowserTabKind.Downloads => BuildDownloadsPage(tab),
            BrowserTabKind.Settings => BuildSettingsPage(tab),
            BrowserTabKind.AiManagement => BuildAiManagementPage(tab),
            BrowserTabKind.Hymns => BuildHymnsPage(tab),
            _ => new Grid()
        };
        tab.CachedContent = page;
        return page;
    }

    private UIElement BuildPageShell(BrowserTab tab, string title, params UIElement[] children)
    {
        var root = new Grid { Background = GetBrush("AppBg") };
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new StackPanel
            {
                Width = 820,
                Margin = new Thickness(0, 34, 0, 34),
                HorizontalAlignment = HorizontalAlignment.Center
            }
        };
        scroll.ScrollChanged += (_, _) => tab.NativeScrollY = scroll.VerticalOffset;
        scroll.Loaded += OnScrollLoaded;
        void OnScrollLoaded(object s, RoutedEventArgs e)
        {
            scroll.Loaded -= OnScrollLoaded;
            var sv = (ScrollViewer)s;
            var localTab = tab;
            Dispatcher.BeginInvoke(
                () => { if (sv.IsLoaded && _tabs.Contains(localTab)) sv.ScrollToVerticalOffset(Math.Max(0, localTab.NativeScrollY)); },
                DispatcherPriority.Loaded);
        }

        var stack = (StackPanel)scroll.Content;
        stack.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = GetBrush("Text"),
            FontSize = 30,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 18)
        });

        foreach (var child in children)
        {
            stack.Children.Add(child);
        }

        root.Children.Add(scroll);
        return root;
    }

    private string NormalizeInput(string input)
    {
        input = input.Trim();

        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        if (Uri.TryCreate(input, UriKind.Absolute, out var absolute) &&
            (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps || absolute.Scheme == Uri.UriSchemeFile))
        {
            if (absolute.Scheme == Uri.UriSchemeHttp && !IsLocalAddress(absolute.Host))
            {
                // Rebuild the URI instead of string-replacing "http://" — a global
                // replace also mangles http:// URLs inside the query string.
                var upgraded = new UriBuilder(absolute)
                {
                    Scheme = Uri.UriSchemeHttps,
                    Port = -1
                };
                return upgraded.Uri.ToString();
            }
            return absolute.ToString();
        }

        if (input.Contains('.') && !input.Contains(' ') && !input.Contains('@'))
        {
            return $"https://{input}";
        }

        var template = GetSearchUrlTemplate(_dataStore.Settings.SearchEngine, _dataStore.Settings.CustomEngines);
        var escapedQuery = Uri.EscapeDataString(input);
        return template.Contains("{q}")
            ? template.Replace("{q}", escapedQuery)
            : template + escapedQuery;
    }

    private static bool IsLocalAddress(string host)
    {
        // Uri.Host keeps IPv6 literals bracketed ("[::1]").
        var bare = host?.Trim('[', ']') ?? "";
        return string.Equals(bare, "localhost", StringComparison.OrdinalIgnoreCase) ||
               bare == "127.0.0.1" ||
               bare == "::1";
    }

    private static string GetSearchUrlTemplate(string searchEngine, List<SearchEngineDef>? customEngines = null)
    {
        return searchEngine switch
        {
            "DuckDuckGo" => "https://duckduckgo.com/?q=",
            "Startpage" => "https://www.startpage.com/sp/search?query=",
            "Brave" => "https://search.brave.com/search?q=",
            "SearXNG" => "https://searx.be/search?q=",
            "Google" => "https://www.google.com/search?q=",
            _ when customEngines?.FirstOrDefault(e => e.Name == searchEngine) is { } engine
                => engine.UrlTemplate,
            _ => "https://duckduckgo.com/?q="
        };
    }

    private static string TrimUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return string.IsNullOrWhiteSpace(url) ? "page" : url;
        }

        return uri.Host + uri.PathAndQuery;
    }

    private static bool TryGetNativePageKind(string input, out BrowserTabKind kind)
    {
        kind = BrowserTabKind.Start;
        var value = input.Trim().TrimEnd('/');

        if (!value.StartsWith("tabloid://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var page = value["tabloid://".Length..].ToLowerInvariant();
        kind = page switch
        {
            "start" => BrowserTabKind.Start,
            "bookmarks" => BrowserTabKind.Bookmarks,
            "history" => BrowserTabKind.History,
            "downloads" => BrowserTabKind.Downloads,
            "settings" => BrowserTabKind.Settings,
            "ai" => BrowserTabKind.AiManagement,
            "hymns" => BrowserTabKind.Hymns,
            _ => BrowserTabKind.Start
        };

        return page is "start" or "bookmarks" or "history" or "downloads" or "settings" or "ai" or "hymns";
    }

    private static string GetNativePageTitle(BrowserTabKind kind)
    {
        return kind switch
        {
            BrowserTabKind.Bookmarks => "Bookmarks",
            BrowserTabKind.History => "History",
            BrowserTabKind.Downloads => "Downloads",
            BrowserTabKind.Settings => "Settings",
            BrowserTabKind.AiManagement => "AI Management",
            BrowserTabKind.Hymns => "Hymns",
            _ => "New tab"
        };
    }

    private static string GetNativePageAddress(BrowserTabKind kind)
    {
        return kind switch
        {
            BrowserTabKind.Bookmarks => "tabloid://bookmarks",
            BrowserTabKind.History => "tabloid://history",
            BrowserTabKind.Downloads => "tabloid://downloads",
            BrowserTabKind.Settings => "tabloid://settings",
            BrowserTabKind.AiManagement => "tabloid://ai",
            BrowserTabKind.Hymns => "tabloid://hymns",
            _ => string.Empty
        };
    }

    private void AddressBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsSubmitKey(e.Key) || _activeTab is null)
        {
            return;
        }

        e.Handled = true;
        NavigateFromAddressBar();
    }

    private void Go_Click(object sender, RoutedEventArgs e) => NavigateFromAddressBar();

    private async void NavigateFromAddressBar()
    {
        try
        {
            var tab = _activeTab;
            if (tab is not null)
            {
                await NavigateWebAsync(tab, NavToolbar.AddressBox.Text);
                Keyboard.ClearFocus();
            }
        }
        catch (Exception ex) { Log.Error(ex, "Error occurred"); }
    }

    private void StartSearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsSubmitKey(e.Key))
        {
            return;
        }

        e.Handled = true;
        NavigateFromStartSearch();
    }

    private void StartSearchGo_Click(object sender, RoutedEventArgs e) => NavigateFromStartSearch();

    private void StartSearchSurface_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsFromControl<TextBox>(e.OriginalSource) || IsFromControl<Button>(e.OriginalSource))
        {
            return;
        }

        BrowserPane.StartSearchBox.Focus();
        Keyboard.Focus(BrowserPane.StartSearchBox);
        e.Handled = true;
    }

    private async void NavigateFromStartSearch()
    {
        try
        {
            var tab = _activeTab;
            if (tab is not null)
            {
                await NavigateWebAsync(tab, BrowserPane.StartSearchBox.Text);
                Keyboard.ClearFocus();
            }
        }
        catch (Exception ex) { Log.Error(ex, "Error occurred"); }
    }

    private static bool IsSubmitKey(Key key) => key is Key.Enter or Key.Return;

    private async Task NavigateQuickAsync(string url)
    {
        var tab = _activeTab;
        if (tab is not null)
        {
            await NavigateWebAsync(tab, url);
        }
    }
}
