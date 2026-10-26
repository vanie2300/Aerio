using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;

namespace TabloidBrowser;

public partial class MainWindow
{
    private UIElement BuildBookmarksPage(BrowserTab tab)
    {
        var stack = new StackPanel();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 12) };
        actions.Children.Add(MatteButton("Import", (_, _) => ImportBookmarks()));
        actions.Children.Add(MatteButton("Export", (_, _) => ExportBookmarks(), new Thickness(8, 0, 0, 0)));
        stack.Children.Add(actions);

        var searchBox = Input("");
        searchBox.Margin = new Thickness(0, 0, 0, 12);
        stack.Children.Add(searchBox);

        var entriesPanel = new StackPanel();
        stack.Children.Add(entriesPanel);

        void PopulateEntries(string query)
        {
            entriesPanel.Children.Clear();
            var bookmarks = string.IsNullOrWhiteSpace(query)
                ? _dataStore.Bookmarks.ToList()
                : _dataStore.Bookmarks
                    .Where(b => b.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                b.Url.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .ToList();

            if (bookmarks.Count == 0)
            {
                entriesPanel.Children.Add(EmptyText(
                    string.IsNullOrWhiteSpace(query) ? "No bookmarks yet." : "No matching bookmarks."));
                return;
            }

            foreach (var item in bookmarks)
            {
                entriesPanel.Children.Add(BuildEntry(
                    item.Title,
                    TrimUrl(item.Url),
                    "Open",
                    async (_, _) =>
                    {
                        try { await NavigateWebAsync(tab, item.Url); }
                        catch (Exception ex) { Log.Error(ex, "Error occurred"); }
                    },
                    "Remove",
                    (_, _) =>
                    {
                        _dataStore.RemoveBookmark(item.Url);
                        PopulateEntries(searchBox.Text?.Trim() ?? "");
                    }));
            }
        }

        PopulateEntries("");
        searchBox.TextChanged += (_, _) => PopulateEntries(searchBox.Text?.Trim() ?? "");

        return BuildPageShell(tab, "Bookmarks", stack);
    }

    private UIElement BuildHistoryPage(BrowserTab tab)
    {
        var stack = new StackPanel();
        var clear = MatteButton("Clear history", (_, _) =>
        {
            _dataStore.ClearHistory();
            RenderNativeSurface(tab, forceRebuild: true);
        });
        clear.HorizontalAlignment = HorizontalAlignment.Right;
        clear.Margin = new Thickness(0, 0, 0, 12);
        stack.Children.Add(clear);

        var searchBox = Input("");
        searchBox.Margin = new Thickness(0, 0, 0, 12);
        stack.Children.Add(searchBox);

        var entriesPanel = new StackPanel();
        stack.Children.Add(entriesPanel);

        void PopulateEntries(string query)
        {
            entriesPanel.Children.Clear();
            var history = string.IsNullOrWhiteSpace(query)
                ? _dataStore.History.ToList()
                : _dataStore.History
                    .Where(h => h.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                h.Url.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .ToList();

            if (history.Count == 0)
            {
                entriesPanel.Children.Add(EmptyText(
                    string.IsNullOrWhiteSpace(query) ? "Nothing visited yet." : "No matching history."));
                return;
            }

            foreach (var item in history)
            {
                entriesPanel.Children.Add(BuildEntry(
                    item.Title,
                    $"{TrimUrl(item.Url)}  -  {item.VisitedAt:g}",
                    "Open",
                    async (_, _) =>
                    {
                        try { await NavigateWebAsync(tab, item.Url); }
                        catch (Exception ex) { Log.Error(ex, "Error occurred"); }
                    },
                    "Remove",
                    (_, _) =>
                    {
                        _dataStore.RemoveHistory(item.Url, item.VisitedAt);
                        PopulateEntries(searchBox.Text?.Trim() ?? "");
                    }));
            }
        }

        PopulateEntries("");
        searchBox.TextChanged += (_, _) => PopulateEntries(searchBox.Text?.Trim() ?? "");

        return BuildPageShell(tab, "History", stack);
    }

    private UIElement BuildHymnsPage(BrowserTab tab)
    {
        var stack = new StackPanel();

        var intro = new TextBlock
        {
            Text = "Search hymn texts by title or Scripture reference on Hymnary.org, then click a result to read its lyrics.",
            Foreground = GetBrush("Muted"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };
        stack.Children.Add(intro);

        var hint = new TextBlock
        {
            Text = "Examples:  Amazing Grace   |   John 3:16   |   Psalm 136",
            Foreground = GetBrush("Muted"),
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 6)
        };
        stack.Children.Add(hint);

        var searchBox = Input("");
        searchBox.Margin = new Thickness(0, 0, 0, 10);
        stack.Children.Add(searchBox);

        var summary = new TextBlock
        {
            Foreground = GetBrush("Muted"),
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 8)
        };
        stack.Children.Add(summary);

        var resultsPanel = new StackPanel();
        stack.Children.Add(resultsPanel);

        var lyricsPanel = new StackPanel { Visibility = Visibility.Collapsed };
        stack.Children.Add(lyricsPanel);

        CancellationTokenSource? searchCts = null;
        var lastResults = new List<HymnSearchResult>();
        string lastQuery = "";

        void ShowResults(List<HymnSearchResult> results, string query)
        {
            lastResults = results;
            lastQuery = query;
            resultsPanel.Children.Clear();
            resultsPanel.Visibility = Visibility.Visible;
            lyricsPanel.Visibility = Visibility.Collapsed;

            if (results.Count == 0)
            {
                summary.Text = HymnService.LooksLikeScriptureQuery(query)
                    ? $"No hymns found for Scripture {query}."
                    : "No matching hymns found.";
                resultsPanel.Children.Add(EmptyText(
                    "Try a different title, or a Scripture reference like \"John 3:16\"."));
                return;
            }

            summary.Text = $"{results.Count} matching hymn{(results.Count == 1 ? "" : "s")}.";

            foreach (var result in results)
            {
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(result.FirstLine)) parts.Add(result.FirstLine);
                if (!string.IsNullOrWhiteSpace(result.Scripture)) parts.Add(result.Scripture);
                if (!string.IsNullOrWhiteSpace(result.Meter)) parts.Add(result.Meter);
                if (!string.IsNullOrWhiteSpace(result.Authors)) parts.Add(result.Authors);
                if (!string.IsNullOrWhiteSpace(result.Languages)) parts.Add(result.Languages);
                if (result.InstanceCount > 0) parts.Add($"{result.InstanceCount} hymnals");
                var detail = parts.Count > 0 ? string.Join("   ·   ", parts) : result.HymnaryUrl;

                resultsPanel.Children.Add(BuildEntry(
                    result.Title,
                    detail,
                    "Lyrics",
                    (_, _) => _ = ShowLyricsAsync(result),
                    "Open",
                    async (_, _) =>
                    {
                        try { await NavigateWebAsync(tab, result.HymnaryUrl); }
                        catch (Exception ex) { Log.Error(ex, "Error occurred"); }
                    }));
            }
        }

        int lyricsRequestId = 0;

        async Task ShowLyricsAsync(HymnSearchResult result)
        {
            var requestId = ++lyricsRequestId;
            try
            {
                resultsPanel.Visibility = Visibility.Collapsed;
                lyricsPanel.Children.Clear();
                lyricsPanel.Visibility = Visibility.Visible;

                lyricsPanel.Children.Add(new TextBlock
                {
                    Text = $"Loading lyrics for {result.Title}...",
                    Foreground = GetBrush("Muted"),
                    FontSize = 13,
                    Margin = new Thickness(0, 4, 0, 10)
                });

                var lyrics = await HymnService.FetchLyricsAsync(result.Slug, result.Title);
                if (requestId != lyricsRequestId)
                {
                    return; // a newer request superseded this one
                }
                lyricsPanel.Children.Clear();

                var titleBlock = new TextBlock
                {
                    Text = lyrics?.Title ?? result.Title,
                    Foreground = GetBrush("Text"),
                    FontSize = 22,
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                };
                lyricsPanel.Children.Add(titleBlock);

                if (lyrics is null || lyrics.Stanzas.Count == 0)
                {
                    lyricsPanel.Children.Add(new TextBlock
                    {
                        Text = "Couldn't load lyrics for this hymn. It may not have a text file available on Hymnary.org.",
                        Foreground = GetBrush("Muted"),
                        FontSize = 13,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 8, 0, 0)
                    });
                }
                else
                {
                    foreach (var stanza in lyrics.Stanzas)
                    {
                        lyricsPanel.Children.Add(new TextBlock
                        {
                            Text = stanza,
                            Foreground = GetBrush("Text"),
                            FontSize = 14,
                            TextWrapping = TextWrapping.Wrap,
                            LineHeight = 26,
                            Margin = new Thickness(0, 14, 0, 0)
                        });
                    }
                }

                var actionRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 18, 0, 4)
                };
                actionRow.Children.Add(MatteButton("Back to results", (_, _) => ShowResults(lastResults, lastQuery)));
                actionRow.Children.Add(MatteButton("Open on Hymnary.org", async (_, _) =>
                {
                    try { await NavigateWebAsync(tab, result.HymnaryUrl); }
                    catch (Exception ex) { Log.Error(ex, "Error occurred"); }
                }, new Thickness(8, 0, 0, 0)));
                lyricsPanel.Children.Add(actionRow);
            }
            catch (Exception ex)
            {
                if (requestId == lyricsRequestId)
                {
                    Log.Error(ex, "Failed to load hymn lyrics");
                    StatusBar.StatusText.Text = $"Couldn't load lyrics: {ex.Message}";
                }
            }
        }

        async Task RunSearchAsync(string query)
        {
            try
            {
                if (HymnService.LooksLikeScriptureQuery(query))
                {
                    summary.Text = $"Searching hymns for Scripture {query}...";
                }
                else
                {
                    summary.Text = "Searching Hymnary.org...";
                }

                var results = await HymnService.SearchAsync(query);
                if (!string.Equals(searchBox.Text?.Trim(), query, StringComparison.Ordinal))
                {
                    return;
                }

                ShowResults(results.ToList(), query);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Hymn search failed");
                summary.Text = "Search failed — check your connection and try again.";
                resultsPanel.Children.Clear();
                resultsPanel.Children.Add(EmptyText(ex.Message));
            }
        }

        searchBox.TextChanged += (_, _) =>
        {
            searchCts?.Cancel();
            // Don't dispose immediately — the previous debounce task may still
            // be registering on its token (ObjectDisposedException race).
            searchCts = new CancellationTokenSource();
            var ct = searchCts.Token;

            var text = searchBox.Text?.Trim() ?? "";
            if (text.Length == 0)
            {
                searchCts = null;
                summary.Text = "";
                resultsPanel.Children.Clear();
                lyricsPanel.Visibility = Visibility.Collapsed;
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(450, ct);
                    ct.ThrowIfCancellationRequested();
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (!ct.IsCancellationRequested)
                        {
                            _ = RunSearchAsync(text);
                        }
                    });
                }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
            }, ct);
        };

        searchBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || String.IsNullOrWhiteSpace(searchBox.Text))
            {
                return;
            }

            searchCts?.Cancel();
            searchCts = null;
            _ = RunSearchAsync(searchBox.Text.Trim());
            e.Handled = true;
        };

        Dispatcher.BeginInvoke(() => searchBox.Focus(), DispatcherPriority.Loaded);

        return BuildPageShell(tab, "Hymns", stack);
    }

    private UIElement BuildDownloadsPage(BrowserTab tab)
    {
        var stack = new StackPanel();
        var clear = MatteButton("Clear list", (_, _) =>
        {
            _dataStore.ClearDownloads();
            RenderNativeSurface(tab, forceRebuild: true);
        });
        clear.HorizontalAlignment = HorizontalAlignment.Right;
        clear.Margin = new Thickness(0, 0, 0, 12);
        stack.Children.Add(clear);

        if (_dataStore.Downloads.Count == 0)
        {
            stack.Children.Add(EmptyText("No downloads yet."));
            return BuildPageShell(tab, "Downloads", stack);
        }

        foreach (var item in _dataStore.Downloads)
        {
            if (item.FilePath is null)
            {
                continue;
            }

            stack.Children.Add(BuildEntry(
                item.FileName,
                $"{item.SourceUrl}  -  {item.DownloadedAt:g}",
                "Folder",
                (_, _) => OpenDownloadFolder(Path.GetDirectoryName(item.FilePath) ?? item.FilePath)));
        }

        return BuildPageShell(tab, "Downloads", stack);
    }

    private UIElement BuildSettingsPage(BrowserTab tab)
    {
        var settings = _dataStore.Settings;

        Action onChange = () => { };
        var searchCtrl = SearchEngineCarousel(settings.SearchEngine, settings.CustomEngines, () => onChange());
        var accent = AccentCircleGroup(["Blue", "Purple", "Gray", "Green", "White"], settings.Accent);
        var theme = Segmented(["Dark", "Light"], settings.Theme);
        var customBgPath = settings.Background is not "Default" && File.Exists(settings.Background)
            ? settings.Background
            : null;
        var savedCustomBgPath = customBgPath;
        var background = Segmented(["Default", "Custom"], customBgPath is not null ? "Custom" : "Default");

        var customBgName = new TextBlock
        {
            Text = customBgPath is not null ? Path.GetFileName(customBgPath) : "No image selected",
            Foreground = GetBrush("InkSecondary"),
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 230
        };

        var customBgBadge = new Border
        {
            Background = GetBrush("Accent"),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(5, 1, 5, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Visibility = customBgPath is not null ? Visibility.Visible : Visibility.Collapsed,
            Child = new TextBlock
            {
                Text = "Custom",
                Foreground = Brushes.White,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold
            }
        };

        var bgPreview = new Border
        {
            Height = 120,
            CornerRadius = new CornerRadius(8),
            BorderBrush = GetBrush("Stroke"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 12, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 200
        };
        if (customBgPath is not null)
        {
            try
            {
                var img = new BitmapImage(new Uri(customBgPath));
                bgPreview.Background = new ImageBrush(img)
                {
                    Stretch = Stretch.UniformToFill,
                    Opacity = settings.BackgroundOpacity
                };
            }
            catch (Exception ex) { Log.Debug(ex, "Failed to load background image preview"); }
        }
        var bgOpacity = SettingsSliderRow("Background opacity",
            "Brightness of the background image from 0% (invisible) to 100% (fully opaque).",
            settings.BackgroundOpacity, 0.0, 1.0, v => $"{(int)Math.Round(v * 100)}%", controlWidth: 170);
        bgOpacity.Input.AutoToolTipPlacement = AutoToolTipPlacement.TopLeft;
        bgOpacity.Input.ValueChanged += (_, _) =>
        {
            var val = bgOpacity.Input.Value;
            if (bgPreview.Background is ImageBrush brush)
                brush.Opacity = val;
            if (BrowserPane.StartOverlay.Background is ImageBrush mainBrush)
                mainBrush.Opacity = val;
        };
        bgPreview.Visibility = customBgPath is not null ? Visibility.Visible : Visibility.Collapsed;
        bgOpacity.Control.Visibility = customBgPath is not null ? Visibility.Visible : Visibility.Collapsed;

        var browseBg = SettingsButton("Choose image");
        browseBg.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog
            {
                Title = "Choose background image",
                Filter = "Image files|*.jpg;*.jpeg;*.png;*.bmp;*.gif"
            };
            if (dialog.ShowDialog() == true)
            {
                customBgPath = dialog.FileName;
                savedCustomBgPath = dialog.FileName;
                var saved = _dataStore.Settings with { Background = dialog.FileName };
                _dataStore.SaveSettings(saved);
                ApplySettings();
                customBgName.Text = Path.GetFileName(dialog.FileName);
                customBgBadge.Visibility = Visibility.Visible;
                try
                {
                    var img = new BitmapImage(new Uri(dialog.FileName));
                    bgPreview.Background = new ImageBrush(img)
                    {
                        Stretch = Stretch.UniformToFill,
                        Opacity = bgOpacity.Input.Value
                    };
                }
                catch (Exception ex) { Log.Debug(ex, "Failed to load selected background image"); }
            }
        };

        var uploadLine = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 12, 0, 0),
            Visibility = customBgPath is not null ? Visibility.Visible : Visibility.Collapsed
        };
        uploadLine.Children.Add(browseBg);
        uploadLine.Children.Add(customBgName);
        uploadLine.Children.Add(customBgBadge);

        var sleep = NumericStepper(settings.TabSleepMinutes, 1, 60);
        var quickLinks = SettingsToggleRow("Quick links",
            "Show shortcuts below the search box.",
            settings.ShowQuickLinks);
        var blockTrackers = SettingsToggleRow("Block common trackers",
            "Use a domain blocklist to prevent requests to known tracker domains.",
            settings.BlockTrackers);
        var adBlock = SettingsToggleRow("Block ads",
            "Filter advertising requests using a built-in EasyList-style list. Uses a bundled list offline and refreshes from EasyList.",
            settings.EnableAdBlock);
        var clearOnExit = SettingsToggleRow("Clear browsing state on exit",
            "Remove browsing history, download history, and session recovery data when Tabloid closes.",
            settings.ClearDataOnExit);
        var enableAutofill = SettingsToggleRow("Form autofill",
            "Offer to fill saved details in forms.",
            settings.EnableAutofill);
        var enablePasswordSave = SettingsToggleRow("Save passwords",
            "Offer to remember website passwords.",
            settings.EnablePasswordSave);
        var enableDevTools = SettingsToggleRow("Developer tools",
            "Enable F12 developer tools to inspect web pages.",
            settings.EnableDevTools);
        var forceDark = SettingsToggleRow("Force dark pages",
            "Apply a dark filter to websites. Some pages may not render correctly.",
            settings.ForceDarkPage);
        var shortcutOverrides = settings.Shortcuts is not null
            ? new Dictionary<string, string>(settings.Shortcuts)
            : new Dictionary<string, string>();

        var blocklist = Multiline(string.Join(Environment.NewLine, settings.TrackerBlocklist));
        blocklist.MinHeight = 140;
        blocklist.Background = GetBrush("FieldBg");
        blocklist.BorderBrush = GetBrush("StrokeStrong");

        var allowlist = Multiline(string.Join(Environment.NewLine, settings.TrackerAllowlist));
        allowlist.MinHeight = 110;
        allowlist.Background = GetBrush("FieldBg");
        allowlist.BorderBrush = GetBrush("StrokeStrong");

        var blocklistPanel = new Border
        {
            Background = GetBrush("SurfaceBg"),
            BorderBrush = GetBrush("Stroke"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(15, 13, 15, 13),
            Margin = new Thickness(0, 14, 0, 0)
        };
        var blocklistStack = new StackPanel();
        blocklistStack.Children.Add(new TextBlock
        {
            Text = "Blocked domains",
            Foreground = GetBrush("InkSecondary"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        });
        blocklistStack.Children.Add(blocklist);
        blocklistStack.Children.Add(new TextBlock
        {
            Text = "One domain per line. Use * for wildcards, e.g. *doubleclick.*",
            Foreground = GetBrush("Muted"),
            FontSize = 11.5,
            Margin = new Thickness(0, 7, 0, 10)
        });
        var listActions = new StackPanel { Orientation = Orientation.Horizontal };
        var resetBlocklist = SettingsButton("Reset blocklist");
        var clearAllowlist = SettingsButton("Clear allowlist", ghost: true);
        clearAllowlist.Margin = new Thickness(8, 0, 0, 0);
        listActions.Children.Add(resetBlocklist);
        listActions.Children.Add(clearAllowlist);
        blocklistStack.Children.Add(listActions);
        blocklistPanel.Child = blocklistStack;

        var allowlistPanel = new Border
        {
            Background = GetBrush("SurfaceBg"),
            BorderBrush = GetBrush("Stroke"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(15, 13, 15, 13),
            Margin = new Thickness(0, 14, 0, 0)
        };
        var allowlistStack = new StackPanel();
        allowlistStack.Children.Add(new TextBlock
        {
            Text = "Allowed sites",
            Foreground = GetBrush("InkSecondary"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        });
        allowlistStack.Children.Add(allowlist);
        allowlistStack.Children.Add(new TextBlock
        {
            Text = "Domains listed here bypass the built-in tracker blocking.",
            Foreground = GetBrush("Muted"),
            FontSize = 11.5,
            Margin = new Thickness(0, 7, 0, 0)
        });
        allowlistPanel.Child = allowlistStack;

        var aiEnabled = SettingsToggleRow("Enable AI assistant",
            "Show the AI chat sidebar powered by Ollama Cloud or NVIDIA NIM.",
            settings.AiEnabled);

        var aiProviderCombo = new ComboBox
        {
            Height = 35,
            MinWidth = 400,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        aiProviderCombo.ItemsSource = BrowserSettings.AiProviderPresets.Keys.ToList();
        aiProviderCombo.SelectedItem = BrowserSettings.AiProviderPresets.Keys.Contains(settings.AiProvider)
            ? settings.AiProvider : "Ollama Cloud";

        var aiModelCombo = new ComboBox
        {
            IsEditable = true,
            Text = settings.AiModel,
            Height = 35,
            MinWidth = 565,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        aiModelCombo.SelectionChanged += (_, _) =>
        {
            if (aiModelCombo.SelectedItem is string s)
                aiModelCombo.Text = s;
        };

        var aiApiKeyBox = new PasswordBox
        {
            Height = 35,
            Width = 565,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = GetBrush("FieldBg"),
            Foreground = GetBrush("Text"),
            BorderBrush = GetBrush("StrokeStrong"),
            Padding = new Thickness(10, 6, 10, 0)
        };
        aiApiKeyBox.Password = BrowserSettings.GetApiKey(settings, settings.AiProvider);

        var aiApiKeyHint = new TextBlock
        {
            Text = "Each provider keeps its own key — switch providers and your saved key returns automatically.",
            Foreground = GetBrush("Muted"),
            FontSize = 11.5,
            Margin = new Thickness(0, 7, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };

        CancellationTokenSource? _modelFetchToken = null;

        async Task FetchModelsAsync(string ep, string key, CancellationToken ct)
        {
            try
            {
                await Task.Delay(500, ct);
                ct.ThrowIfCancellationRequested();
                var models = await AiService.FetchModelsAsync(ep, key, aiProviderCombo.Text, ct);
                ct.ThrowIfCancellationRequested();
                if (PresentationSource.FromDependencyObject(aiModelCombo) is null) return;
                if (models.Count > 0)
                {
                    var prev = aiModelCombo.Text;
                    aiModelCombo.ItemsSource = models;
                    aiModelCombo.Text = prev;
                    StatusBar.StatusText.Text = $"Discovered {models.Count} models";
                }
                else
                {
                    StatusBar.StatusText.Text = "Could not reach endpoint — check API key";
                }
            }
            catch (OperationCanceledException) { Log.Debug("Model discovery cancelled"); }
            catch (Exception ex) { Log.Error(ex, "Model discovery"); }
        }

        async void DebounceFetchModels()
        {
            _modelFetchToken?.Cancel();
            _modelFetchToken?.Dispose();
            _modelFetchToken = new CancellationTokenSource();
            var ct = _modelFetchToken.Token;
            try
            {
                await Task.Delay(800, ct);
                ct.ThrowIfCancellationRequested();
                var ep = BrowserSettings.GetProviderEndpoint(aiProviderCombo.Text);
                var op = Dispatcher.InvokeAsync(() =>
                    FetchModelsAsync(ep, aiApiKeyBox.Password, ct));
                await op.Task.Unwrap();
            }
            catch (OperationCanceledException) { Log.Debug("Model fetch debounce cancelled"); }
            catch (Exception ex) { Log.Error(ex, "DebounceFetchModels error"); }
        }
        var testConnectionBtn = SettingsButton("Test connection");
        testConnectionBtn.Click += (_, _) =>
        {
            var ep = BrowserSettings.GetProviderEndpoint(aiProviderCombo.Text);
            _modelFetchToken?.Cancel();
            _modelFetchToken?.Dispose();
            _modelFetchToken = new CancellationTokenSource();
            var op = Dispatcher.InvokeAsync(() =>
                FetchModelsAsync(ep, aiApiKeyBox.Password, _modelFetchToken.Token));
            FireAndForget(op.Task.Unwrap(), "TestConnection");
        };
        DebounceFetchModels();

        var aiSystemPrompt = Multiline(settings.AiSystemPrompt);
        aiSystemPrompt.MinHeight = 114;
        aiSystemPrompt.MaxWidth = 630;
        aiSystemPrompt.HorizontalAlignment = HorizontalAlignment.Left;
        aiSystemPrompt.Background = GetBrush("FieldBg");
        aiSystemPrompt.BorderBrush = GetBrush("StrokeStrong");
        var aiMaxTokens = SettingsSliderRow("Max tokens",
            "Maximum number of output tokens per response.",
            settings.AiMaxTokens, 256, 16384, v => ((int)v).ToString("N0"), controlWidth: 235);
        var aiTemperature = SettingsSliderRow("Temperature",
            "Controls response variability, depending on model support.",
            settings.AiTemperature, 0.0, 2.0, v => v.ToString("0.0"), controlWidth: 235);
        var aiMaxOpenTabs = SettingsSliderRow("Maximum open tabs",
            "The maximum number of AI-owned tabs open per task (oldest is closed when exceeded).",
            settings.AiPolicy?.MaxOpenTabs ?? AiBrowserPolicy.Default.MaxOpenTabs, 1, 50, v => ((int)v).ToString(), controlWidth: 235);

        void Save()
        {
            var saved = new BrowserSettings(
                SearchEngine: searchCtrl.GetEngine(),
                ClearDataOnExit: clearOnExit.IsChecked == true,
                TabSleepMinutes: ClampInt(sleep.GetValue(), 1, 60, BrowserSettings.Default.TabSleepMinutes),
                Accent: accent.GetValue(),
                Theme: theme.GetValue(),
                ShowQuickLinks: quickLinks.IsChecked == true,
                Background: background.GetValue() == "Custom" && customBgPath is not null
                    ? customBgPath
                    : background.GetValue() == "Custom" && savedCustomBgPath is not null
                        ? savedCustomBgPath
                        : "Default",
                BackgroundOpacity: bgOpacity.GetValue(),
                BlockTrackers: blockTrackers.IsChecked == true,
                EnableAdBlock: adBlock.IsChecked == true,
                TrackerBlocklist: ParseDomains(blocklist.Text, BrowserSettings.Default.TrackerBlocklist),
                TrackerAllowlist: ParseDomains(allowlist.Text, []),
                SitePermissions: _dataStore.Settings.SitePermissions,
                EnableAutofill: enableAutofill.IsChecked == true,
                EnablePasswordSave: enablePasswordSave.IsChecked == true,
                EnableDevTools: enableDevTools.IsChecked == true,
                ForceDarkPage: forceDark.IsChecked == true,
                Extensions: _dataStore.Settings.Extensions,
                CustomEngines: searchCtrl.GetCustomEngines(),
                Shortcuts: shortcutOverrides is { Count: > 0 } ? new Dictionary<string, string>(shortcutOverrides) : null,
                AiEnabled: aiEnabled.IsChecked == true,
                AiSidebarOpen: _dataStore.Settings.AiSidebarOpen,
                AiProvider: aiProviderCombo.SelectedItem?.ToString() ?? "Ollama Cloud",
                AiEndpoint: BrowserSettings.GetProviderEndpoint(aiProviderCombo.SelectedItem?.ToString() ?? "Ollama Cloud"),
                AiModel: GetComboText(aiModelCombo),
                AiApiKey: aiApiKeyBox.Password,
                AiApiKeys: BrowserSettings.StoreProviderApiKey(
                    _dataStore.Settings.AiApiKeys,
                    aiProviderCombo.SelectedItem?.ToString() ?? "Ollama Cloud",
                    aiApiKeyBox.Password),
                AiSystemPrompt: aiSystemPrompt.Text?.Trim() ?? "",
                AiMaxTokens: (int)aiMaxTokens.GetValue(),
                AiTemperature: aiTemperature.GetValue(),
                AiPolicy: new AiBrowserPolicy(
                    MaxOpenTabs: ClampInt(((int)aiMaxOpenTabs.Input.Value).ToString(), 1, 50, AiBrowserPolicy.Default.MaxOpenTabs),
                    AllowNavigateActiveTab: _dataStore.Settings.AiPolicy?.AllowNavigateActiveTab ?? false,
                    CleanupOnTaskComplete: _dataStore.Settings.AiPolicy?.CleanupOnTaskComplete ?? false),
                UblockEnabled: _dataStore.Settings.UblockEnabled,
                UblockUpdateCheckUtc: _dataStore.Settings.UblockUpdateCheckUtc,
                UblockVersion: _dataStore.Settings.UblockVersion,
                AiSearchBackend: _dataStore.Settings.AiSearchBackend,
                AiSearchEndpoint: _dataStore.Settings.AiSearchEndpoint,
                AiMaxIterations: _dataStore.Settings.AiMaxIterations,
                AiPageContextConfirmation: _dataStore.Settings.AiPageContextConfirmation,
                AiToolsEnabled: _dataStore.Settings.AiToolsEnabled,
                LastSeenVersion: _dataStore.Settings.LastSeenVersion);

            _dataStore.SaveSettings(saved);
            ApplySettings();
        }
        onChange = Save;

        foreach (var btn in accent.Buttons)
        {
            btn.Click += (_, _) => Save();
        }

        foreach (var btn in theme.Buttons)
        {
            btn.Click += (_, _) => Save();
        }

        foreach (var btn in background.Buttons)
        {
            btn.Click += (_, _) =>
            {
                var isCustom = string.Equals(btn.Tag?.ToString(), "Custom", StringComparison.Ordinal);
                uploadLine.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
                bgPreview.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
                bgOpacity.Control.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
                if (isCustom && customBgPath is null && savedCustomBgPath is not null)
                {
                    customBgPath = savedCustomBgPath;
                    customBgName.Text = Path.GetFileName(savedCustomBgPath);
                    customBgBadge.Visibility = Visibility.Visible;
                    try
                    {
                        var img = new BitmapImage(new Uri(savedCustomBgPath));
                        bgPreview.Background = new ImageBrush(img)
                        {
                            Stretch = Stretch.UniformToFill,
                            Opacity = bgOpacity.Input.Value
                        };
                    }
                    catch (Exception ex) { Log.Debug(ex, "Failed to load background image"); }
                }
                Save();
            };
        }

        if (sleep.Control is Grid sleepGrid)
        {
            foreach (var child in sleepGrid.Children)
            {
                if (child is Button btn) btn.Click += (_, _) => Save();
            }
            foreach (var child in sleepGrid.Children)
            {
                if (child is TextBox tb) tb.LostFocus += (_, _) => Save();
            }
        }

        quickLinks.Checked += (_, _) => Save();
        quickLinks.Unchecked += (_, _) => Save();
        blockTrackers.Checked += (_, _) => Save();
        blockTrackers.Unchecked += (_, _) => Save();
        adBlock.Checked += (_, _) => Save();
        adBlock.Unchecked += (_, _) => Save();
        clearOnExit.Checked += (_, _) => Save();
        clearOnExit.Unchecked += (_, _) => Save();
        enableAutofill.Checked += (_, _) => Save();
        enableAutofill.Unchecked += (_, _) => Save();
        enablePasswordSave.Checked += (_, _) => Save();
        enablePasswordSave.Unchecked += (_, _) => Save();
        enableDevTools.Checked += (_, _) => Save();
        enableDevTools.Unchecked += (_, _) => Save();
        forceDark.Checked += (_, _) => Save();
        forceDark.Unchecked += (_, _) => Save();

        blocklist.LostFocus += (_, _) => Save();
        allowlist.LostFocus += (_, _) => Save();
        resetBlocklist.Click += (_, _) =>
        {
            blocklist.Text = string.Join(Environment.NewLine, BrowserSettings.Default.TrackerBlocklist);
            Save();
            StatusBar.StatusText.Text = "Tracker blocklist reset";
        };
        clearAllowlist.Click += (_, _) =>
        {
            allowlist.Clear();
            Save();
            StatusBar.StatusText.Text = "Allowlist cleared";
        };

        aiEnabled.Checked += (_, _) => Save();
        aiEnabled.Unchecked += (_, _) => Save();
        aiApiKeyBox.LostFocus += (_, _) => { Save(); DebounceFetchModels(); };
        aiProviderCombo.SelectionChanged += (_, _) =>
        {
            if (aiProviderCombo.SelectedItem is string s)
            {
                var storedKey = BrowserSettings.GetApiKey(settings, s);
                if (!string.IsNullOrEmpty(storedKey))
                    aiApiKeyBox.Password = storedKey;
            }
            Save();
            DebounceFetchModels();
        };
        aiSystemPrompt.LostFocus += (_, _) => Save();
        aiModelCombo.LostFocus += (_, _) => Save();
        aiMaxTokens.Input.LostFocus += (_, _) => Save();
        aiTemperature.Input.LostFocus += (_, _) => Save();
        aiMaxOpenTabs.Input.LostFocus += (_, _) => Save();

        var clearData = SettingsButton("Clear local browsing data");
        clearData.Click += async (_, _) =>
        {
            try
            {
                await ClearBrowsingDataAsync();
                RenderNativeSurface(tab, forceRebuild: true);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error occurred");
            }
        };

        var resetAll = SettingsButton("Reset all settings…", destructive: true);
        resetAll.Click += (_, _) =>
        {
            _dataStore.SaveSettings(BrowserSettings.Default);
            ApplySettings();
            RenderNativeSurface(tab, forceRebuild: true);
        };

        // ── Pages: each category is a list of reference-style pref-sections ──
        var pages = new Dictionary<string, (string Title, string Subtitle, FrameworkElement[] Sections)>
        {
            ["Appearance"] = ("Appearance", "Choose how Tabloid looks and feels.",
            [
                PrefSection("Browser theme", "Applies to the browser interface and settings.",
                    SettingsRow2("Color mode", null, theme.Control)),
                PrefSection("Accent color", "Used for selected tabs, active controls, and focus states.",
                    SettingsRow2("Highlight color", null, accent.Control)),
                PrefSection("New tab page", "Customize what you see when you open a tab.",
                    SettingsRow2("Background", "Use a simple background or your own image.", background.Control),
                    uploadLine,
                    bgPreview,
                    bgOpacity.Control,
                    quickLinks)
            ]),
            ["Engine"] = ("Search engine", "Select the search provider used in the address bar.",
            [
                PrefSection("Default search engine", "Browse available providers.", Centered(searchCtrl.Control, 660))
            ]),
            ["Tabs"] = ("Tabs & memory", "Manage tabs and reduce resource use.",
            [
                PrefSection("Memory saver", "Free up memory when tabs are not being used.",
                    SettingsRow2("Hibernate after",
                        "Minutes of inactivity before Tabloid unloads a background tab. Opening a hibernated tab reloads its contents.",
                        sleep.Control),
                    SettingsNote("About hibernation",
                        "Some pages may need to reload before you can resume working. Hibernated tabs restore their last URL and scroll position."))
            ]),
            ["Privacy && Security"] = ("Privacy & security", "Control tracking protection and browsing permissions.",
            [
                PrefSection("Tracking protection", "Built-in controls for ads and common tracking domains.",
                    blockTrackers,
                    adBlock),
                PrefSection("Exceptions", "Manage domains blocked or allowed by the built-in filter.",
                    blocklistPanel,
                    allowlistPanel),
                PrefSection("Passwords & forms", null,
                    enableAutofill,
                    enablePasswordSave)
            ]),
            ["Data"] = ("Browsing data", "Manage history, local files, and session information.",
            [
                PrefSection("Data retention", null, clearOnExit),
                PrefSection("Clear data", "Choose which data types to remove.",
                    SettingsRow2("Clear local browsing data",
                        "Review data categories before clearing them.",
                        clearData)),
                PrefSection("Reset", null,
                    SettingsRow2("Reset all settings",
                        "Restore Tabloid settings to their defaults. This is separate from clearing browsing data.",
                        resetAll),
                    "dangerTitle")
            ]),
            ["Extensions"] = ("Extensions", "Manage optional tools that extend your browser.",
            [
                PrefSection("Installed extensions", "Built-in content blocking and extensions you have added manually.",
                    BuildExtensionsContent(settings))
            ]),
            ["Advanced"] = ("Advanced", "Developer settings and keyboard shortcuts.",
            [
                PrefSection("Developer settings", null,
                    enableDevTools,
                    forceDark),
                PrefSection("Keyboard shortcuts", "Common browser commands.",
                    BuildShortcutRows(shortcutOverrides, () => onChange()))
            ]),
            ["AI"] = ("AI assistant", "Configure the assistant available in the browser sidebar.",
            [
                PrefSection("Assistant", null, aiEnabled),
                PrefSection("Connection", "Select a service and enter its credentials.",
                    FieldLabelRow("Provider", aiProviderCombo),
                    FieldLabelRow("Model", aiModelCombo),
                    FieldLabelRow("API key", StackOf(aiApiKeyBox, aiApiKeyHint)),
                    testConnectionBtn),
                PrefSection("Assistant instructions", null,
                    FieldLabelRow("System prompt", aiSystemPrompt)),
                PrefSection("Generation", "Advanced response settings.",
                    aiMaxTokens.Control,
                    aiTemperature.Control,
                    aiMaxOpenTabs.Control)
            ])
        };

        // ── Sidebar navigation (reference layout) ──
        var navItems = new (string Key, string Label, string Icon, string? Caption)[]
        {
            ("Appearance", "Appearance", "\xE790", "General"),
            ("Engine", "Search engine", "\xE774", null),
            ("Tabs", "Tabs & memory", "\xE90D", null),
            ("Privacy && Security", "Privacy & security", "\xE72E", "Privacy"),
            ("Data", "Browsing data", "\xE752", null),
            ("Extensions", "Extensions", "\xE7B9", "Tools"),
            ("AI", "AI assistant", "\xE745", null),
            ("Advanced", "Advanced", "\xE71A", null)
        };

        var initialCategory = navItems[0].Key;
        if (tab.Address is { } addr && addr.Contains('#'))
        {
            var fragment = addr.Split('#')[^1];
            if (pages.ContainsKey(fragment))
            {
                initialCategory = fragment;
            }
        }
        string selectedCategory = initialCategory;

        var headingTitle = new TextBlock
        {
            FontSize = 27,
            FontWeight = FontWeights.SemiBold,
            Foreground = GetBrush("Text"),
            LayoutTransform = new ScaleTransform(1, 1)
        };
        var headingSubtitle = new TextBlock
        {
            FontSize = 12.5,
            Foreground = GetBrush("Muted"),
            Margin = new Thickness(0, 7, 0, 0)
        };
        var headingPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 25) };
        headingPanel.Children.Add(headingTitle);
        headingPanel.Children.Add(headingSubtitle);
        var headingBorder = new Border
        {
            Child = headingPanel,
            BorderBrush = GetBrush("Stroke"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Margin = new Thickness(0, 0, 0, 5),
            Padding = new Thickness(0, 0, 0, 25)
        };

        var contentHost = new StackPanel();
        var contentScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = contentHost
        };

        var navButtons = new List<Button>();
        var navIndicators = new List<Border>();
        var navLabels = new List<FrameworkElement>();

        var navStack = new StackPanel();
        navStack.Children.Add(new TextBlock
        {
            Text = "Settings",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = GetBrush("Text"),
            Margin = new Thickness(12, 0, 12, 21)
        });

        string? lastCaption = null;
        for (int i = 0; i < navItems.Length; i++)
        {
            var (key, label, icon, caption) = navItems[i];
            var captured = key;

            if (caption is not null && caption != lastCaption)
            {
                navStack.Children.Add(new TextBlock
                {
                    Text = caption.ToUpperInvariant(),
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = GetBrush("Muted"),
                    Margin = new Thickness(12, 23, 12, 7)
                });
            }
            lastCaption = caption;

            var indicator = new Border
            {
                Width = 2,
                Height = 17,
                CornerRadius = new CornerRadius(2),
                Background = GetBrush("Accent"),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0,
                Margin = new Thickness(-11, 0, 9, 0)
            };

            var iconText = new TextBlock
            {
                Text = icon,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 15,
                VerticalAlignment = VerticalAlignment.Center
            };
            var labelText = new TextBlock
            {
                Text = label,
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(11, 0, 0, 0)
            };

            var linkContent = new StackPanel { Orientation = Orientation.Horizontal };
            linkContent.Children.Add(indicator);
            linkContent.Children.Add(iconText);
            linkContent.Children.Add(labelText);

            var link = new Button
            {
                Content = linkContent,
                Height = 36,
                Padding = new Thickness(11, 0, 11, 0),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0)
            };
            link.Click += (_, _) => SelectCategory(captured);
            navButtons.Add(link);
            navIndicators.Add(indicator);
            navLabels.Add(labelText);
            navStack.Children.Add(link);
        }

        navStack.Children.Add(new Border
        {
            BorderBrush = GetBrush("Stroke"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Margin = new Thickness(10, 42, 10, 0),
            Padding = new Thickness(2, 18, 2, 0),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "Tabloid browser", FontSize = 11, Foreground = GetBrush("InkSecondary") },
                    new TextBlock { Text = "Preferences", FontSize = 11, Foreground = GetBrush("Muted"), Margin = new Thickness(0, 2, 0, 0) }
                }
            }
        });

        var sidebarBorder = new Border
        {
            Background = GetBrush("SidebarBg"),
            BorderBrush = GetBrush("Stroke"),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = navStack,
                Padding = new Thickness(17, 30, 13, 25)
            }
        };

        void SelectCategory(string category)
        {
            selectedCategory = category;
            var (title, subtitle, sections) = pages[category];
            headingTitle.Text = title;
            headingSubtitle.Text = subtitle;

            contentHost.Children.Clear();
            foreach (var section in sections)
            {
                contentHost.Children.Add(section);
            }

            var fadeIn = new DoubleAnimation(0, 1, Motion.Fast)
            {
                EasingFunction = Motion.Out
            };
            contentHost.BeginAnimation(OpacityProperty, fadeIn);
            contentScroll.ScrollToTop();

            for (int i = 0; i < navItems.Length; i++)
            {
                var isSelected = navItems[i].Key == category;
                navButtons[i].Background = isSelected ? GetAccentTintBrush() : Brushes.Transparent;
                navIndicators[i].BeginAnimation(OpacityProperty,
                    new DoubleAnimation(isSelected ? 1.0 : 0.0, TimeSpan.FromSeconds(0.12)));
                navButtons[i].Foreground = isSelected ? GetBrush("Text") : GetBrush("InkSecondary");
            }
        }

        navStack.PreviewKeyDown += (_, e) =>
        {
            int idx = Array.FindIndex(navItems, c => c.Key == selectedCategory);
            int next = e.Key == Key.Down ? idx + 1 : e.Key == Key.Up ? idx - 1 : -1;
            if (next >= 0 && next < navItems.Length)
            {
                SelectCategory(navItems[next].Key);
                navButtons[next].Focus();
                e.Handled = true;
            }
        };

        SelectCategory(selectedCategory);

        // ── Root layout: sidebar | content ──
        var root = new Grid { Background = GetBrush("AppBg") };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(224) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Grid.SetColumn(sidebarBorder, 0);
        root.Children.Add(sidebarBorder);

        var contentFrame = new Grid { Margin = new Thickness(0) };
        contentFrame.Children.Add(contentScroll);
        contentScroll.Padding = new Thickness(42, 40, 48, 72);
        contentScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;

        var contentHostWrapper = new Grid { MaxWidth = 940, HorizontalAlignment = HorizontalAlignment.Left };
        var contentStack = new StackPanel();
        contentStack.Children.Add(headingBorder);
        contentStack.Children.Add(contentHost);
        contentHostWrapper.Children.Add(contentStack);
        contentScroll.Content = contentHostWrapper;

        Grid.SetColumn(contentFrame, 1);
        root.Children.Add(contentFrame);

        var backToTop = new Button
        {
            Content = new TextBlock { Text = "\u2191", FontSize = 16, Foreground = GetBrush("Text") },
            Width = 34, Height = 34,
            Cursor = Cursors.Hand,
            Visibility = Visibility.Collapsed,
            Opacity = 0,
            Padding = new Thickness(0)
        };
        Panel.SetZIndex(backToTop, 10);
        backToTop.Click += (_, _) => contentScroll.ScrollToTop();
        contentScroll.ScrollChanged += (_, _) =>
        {
            tab.NativeScrollY = contentScroll.VerticalOffset;
            bool show = contentScroll.VerticalOffset > 200;
            if (show && backToTop.Visibility != Visibility.Visible)
            {
                backToTop.Visibility = Visibility.Visible;
                backToTop.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 0.85, TimeSpan.FromSeconds(0.15))
                {
                    EasingFunction = new CircleEase { EasingMode = EasingMode.EaseOut }
                });
            }
            else if (!show && backToTop.Visibility == Visibility.Visible)
            {
                var fadeOut = new DoubleAnimation(backToTop.Opacity, 0, TimeSpan.FromSeconds(0.12));
                fadeOut.Completed += (_, _) => backToTop.Visibility = Visibility.Collapsed;
                backToTop.BeginAnimation(OpacityProperty, fadeOut);
            }
        };
        contentScroll.Loaded += (_, _) =>
            Dispatcher.BeginInvoke(
                () => contentScroll.ScrollToVerticalOffset(Math.Max(0, tab.NativeScrollY)),
                DispatcherPriority.Loaded);

        Grid.SetColumn(backToTop, 1);
        backToTop.HorizontalAlignment = HorizontalAlignment.Right;
        backToTop.VerticalAlignment = VerticalAlignment.Bottom;
        backToTop.Margin = new Thickness(0, 0, 16, 16);
        root.Children.Add(backToTop);

        // ── Responsive: collapse the sidebar to icons on narrow windows ──
        void ApplyResponsiveWidth(double width)
        {
            var compact = width < 860;
            root.ColumnDefinitions[0].Width = new GridLength(compact ? 62 : 224);
            foreach (var label in navLabels)
                label.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            foreach (var indicator in navIndicators)
            {
                indicator.Margin = compact ? new Thickness(-7, 0, 7, 0) : new Thickness(-11, 0, 9, 0);
                indicator.Height = compact ? 14 : 17;
            }
            foreach (var child in navStack.Children)
            {
                if (child is TextBlock { FontSize: <= 10 } caption) caption.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            }
            var bottomPanel = (navStack.Children[^1] as Border)?.Child as StackPanel;
            if (bottomPanel is not null)
                bottomPanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        }
        root.SizeChanged += (_, e) => ApplyResponsiveWidth(e.NewSize.Width);
        ApplyResponsiveWidth(root.ActualWidth > 0 ? root.ActualWidth : 940);

        return root;
    }

    private static FrameworkElement StackOf(params FrameworkElement[] elements)
    {
        var stack = new StackPanel();
        foreach (var e in elements) stack.Children.Add(e);
        return stack;
    }

    private FrameworkElement FieldLabelRow(string label, FrameworkElement control)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 15) };
        stack.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = GetBrush("InkSecondary"),
            Margin = new Thickness(0, 0, 0, 7)
        });
        stack.Children.Add(control);
        return stack;
    }

    private FrameworkElement Centered(FrameworkElement content, double maxWidth)
    {
        var host = new Grid { MaxWidth = maxWidth, HorizontalAlignment = HorizontalAlignment.Center };
        host.Children.Add(content);
        return host;
    }

    private Brush GetAccentTintBrush()
    {
        if (GetBrush("Accent") is SolidColorBrush solid)
            return new SolidColorBrush(Color.FromArgb(33, solid.Color.R, solid.Color.G, solid.Color.B));
        return GetBrush("SurfaceRaised");
    }

    // ───────────────────────── Reference-style settings components ─────────────────────────

    /// <summary>Reference .pref-section: 190px label column + rows column, hairline top border.</summary>
    private FrameworkElement PrefSection(string title, string? description, params object[] children)
    {
        bool dangerTitle = children.Contains("dangerTitle");
        var items = children.Where(c => c is UIElement).Cast<UIElement>().ToArray();

        var head = new StackPanel();
        head.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = dangerTitle ? GetBrush("Danger") : GetBrush("Text"),
            TextWrapping = TextWrapping.Wrap
        });
        if (!string.IsNullOrEmpty(description))
        {
            head.Children.Add(new TextBlock
            {
                Text = description,
                FontSize = 12,
                Foreground = GetBrush("Muted"),
                Margin = new Thickness(0, 7, 0, 0),
                MaxWidth = 183,
                TextWrapping = TextWrapping.Wrap
            });
        }

        var rows = new StackPanel();
        bool first = true;
        foreach (var child in items)
        {
            if (!first)
            {
                rows.Children.Add(new Border
                {
                    Height = 1,
                    Background = GetBrush("SubtleStroke"),
                    Margin = new Thickness(0, 2, 0, 2)
                });
            }
            first = false;
            rows.Children.Add(child);
        }

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(head, 0);
        grid.Children.Add(head);
        Grid.SetColumn(rows, 1);
        grid.Children.Add(rows);

        var border = new Border
        {
            Child = grid,
            BorderBrush = GetBrush("Stroke"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 26, 0, 30),
            Margin = new Thickness(0, 0, 0, 2)
        };
        return border;
    }

    /// <summary>Reference .setting: name+desc left, control right, vertically centered.</summary>
    private FrameworkElement SettingsRow2(string name, string? desc, FrameworkElement control)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            Foreground = GetBrush("Text"),
            TextWrapping = TextWrapping.Wrap
        });
        if (!string.IsNullOrEmpty(desc))
        {
            text.Children.Add(new TextBlock
            {
                Text = desc,
                FontSize = 11.5,
                Foreground = GetBrush("Muted"),
                Margin = new Thickness(0, 4, 0, 0),
                MaxWidth = 500,
                TextWrapping = TextWrapping.Wrap
            });
        }

        var grid = new Grid { MinHeight = 44 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);
        control.VerticalAlignment = VerticalAlignment.Center;
        control.Margin = new Thickness(22, 6, 0, 6);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    /// <summary>Compact reference .switch bound to a ToggleButton for event wiring.</summary>
    private ToggleButton SettingsSwitch(bool isChecked)
    {
        var toggle = new ToggleButton
        {
            IsChecked = isChecked,
            Style = FindResource("SwitchToggleButton") as Style,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        return toggle;
    }

    /// <summary>.setting row whose control is a reference switch.</summary>
    private ToggleButton SettingsToggleRow(string name, string? desc, bool isChecked)
    {
        var toggle = SettingsSwitch(isChecked);
        var row = SettingsRow2(name, desc, toggle);
        toggle.Tag = row;
        return toggle;
    }

    /// <summary>Reference .segmented control.</summary>
    private (FrameworkElement Control, Func<string> GetValue, List<Button> Buttons) Segmented(string[] values, string selected)
    {
        var selectedValue = values.Contains(selected) ? selected : values[0];
        var buttons = new List<Button>();
        var panel = new Border
        {
            Background = GetBrush("FieldBg"),
            BorderBrush = GetBrush("Stroke"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(3),
            Child = new StackPanel { Orientation = Orientation.Horizontal }
        };
        var host = (StackPanel)panel.Child;

        void Refresh()
        {
            foreach (var button in buttons)
            {
                var isSelected = string.Equals(button.Tag?.ToString(), selectedValue, StringComparison.Ordinal);
                button.Background = isSelected ? GetBrush("SurfaceRaised") : Brushes.Transparent;
                button.Foreground = isSelected ? GetBrush("Text") : GetBrush("InkSecondary");
                button.FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }

        foreach (var value in values)
        {
            var button = new Button
            {
                Content = value,
                Tag = value,
                Style = FindResource("SegmentButton") as Style
            };
            button.Click += (_, _) =>
            {
                selectedValue = value;
                Refresh();
            };
            buttons.Add(button);
            host.Children.Add(button);
        }
        Refresh();

        return (panel, () => selectedValue, buttons);
    }

    /// <summary>Reference .color-options: round accent swatches with a check on the selected one.</summary>
    private (FrameworkElement Control, Func<string> GetValue, List<Button> Buttons) AccentCircleGroup(string[] names, string selected)
    {
        var selectedValue = names.Contains(selected) ? selected : names[0];
        var buttons = new List<Button>();
        var panel = new StackPanel { Orientation = Orientation.Horizontal };

        void Refresh()
        {
            foreach (var button in buttons)
            {
                var isSelected = string.Equals(button.Tag?.ToString(), selectedValue, StringComparison.Ordinal);
                button.Opacity = isSelected ? 1 : 0.75;
            }
        }

        foreach (var name in names)
        {
            var swatch = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                Background = AccentSwatch(name),
                Child = new TextBlock
                {
                    Text = "\uE73E",
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 10,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Visibility = string.Equals(name, selectedValue, StringComparison.Ordinal)
                        ? Visibility.Visible : Visibility.Collapsed
                }
            };
            var button = new Button
            {
                Content = swatch,
                Tag = name,
                Width = 33,
                Height = 33,
                Padding = new Thickness(4),
                BorderThickness = new Thickness(1),
                BorderBrush = string.Equals(name, selectedValue, StringComparison.Ordinal)
                    ? GetBrush("StrokeStrong") : Brushes.Transparent,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand
            };
            button.Click += (_, _) =>
            {
                selectedValue = name;
                Refresh();
                // Re-render check marks
                int idx = 0;
                foreach (var b in buttons)
                {
                    var inner = (b.Content as Border)?.Child as TextBlock;
                    if (inner is not null)
                        inner.Visibility = string.Equals(b.Tag?.ToString(), selectedValue, StringComparison.Ordinal)
                            ? Visibility.Visible : Visibility.Collapsed;
                    b.BorderBrush = string.Equals(b.Tag?.ToString(), selectedValue, StringComparison.Ordinal)
                        ? GetBrush("StrokeStrong") : Brushes.Transparent;
                    idx++;
                }
            };
            buttons.Add(button);
            panel.Children.Add(button);
        }
        Refresh();

        return (panel, () => selectedValue, buttons);
    }

    /// <summary>Reference .button with optional ghost/destructive variants.</summary>
    private Button SettingsButton(string text, bool ghost = false, bool destructive = false)
    {
        var button = new Button
        {
            Content = text,
            MinHeight = 32,
            Padding = new Thickness(12, 6, 12, 6),
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = Cursors.Hand
        };
        if (destructive)
        {
            button.Background = Brushes.Transparent;
            button.Foreground = GetBrush("Danger");
            button.BorderBrush = GetBrush("Danger");
            button.Opacity = 0.9;
        }
        else if (ghost)
        {
            button.Background = Brushes.Transparent;
            button.Foreground = GetBrush("InkSecondary");
            button.BorderBrush = GetBrush("Stroke");
        }
        return button;
    }

    /// <summary>Reference .slider-line row: slider + tabular value, control column width configurable.</summary>
    private (FrameworkElement Control, Slider Input, Func<double> GetValue) SettingsSliderRow(
        string name, string? desc, double value, double min, double max,
        Func<double, string> format, double controlWidth = 240)
    {
        var valueLabel = new TextBlock
        {
            Text = format(value),
            FontSize = 12,
            Foreground = GetBrush("InkSecondary"),
            MinWidth = 46,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };

        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = value,
            MinWidth = 100,
            VerticalAlignment = VerticalAlignment.Center
        };

        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(slider);
        line.Children.Add(valueLabel);

        void Update() => valueLabel.Text = format(slider.Value);
        slider.ValueChanged += (_, _) => Update();
        Update();

        var host = new Grid { Width = controlWidth, HorizontalAlignment = HorizontalAlignment.Right };
        host.Children.Add(line);

        var control = SettingsRow2(name, desc, host);
        return (control, slider, () => slider.Value);
    }

    /// <summary>Reference .muted-panel inline note.</summary>
    private FrameworkElement SettingsNote(string title, string desc)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = GetBrush("InkSecondary")
        });
        stack.Children.Add(new TextBlock
        {
            Text = desc,
            FontSize = 11.5,
            Foreground = GetBrush("Muted"),
            Margin = new Thickness(0, 5, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });
        return new Border
        {
            Background = GetBrush("FieldBg"),
            BorderBrush = GetBrush("Stroke"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(14, 12, 16, 14),
            Child = stack
        };
    }


    private void RemovePopupBlur()
    {
        if (Content is Grid root)
        {
            root.Effect = null;
        }

        if (_popupOverlay is not null && Content is Grid parent)
        {
            parent.Children.Remove(_popupOverlay);
            _popupOverlay = null;
        }
    }

    private async void ShowSitePermissionsPopup()
    {
        try
        {
            if (_isPermissionsPopupOpen) return;
            if (_isChangelogOpen && _changelogPopup is not null) _changelogPopup.IsOpen = false;

            try { await SyncProfilePermissionSettingsAsync(); } catch (Exception ex) { Log.Error(ex, "Error occurred"); }

            RemovePopupBlur();

            if (Content is Grid rootGrid)
            {
                UiFx.PlayBackdropBlur(rootGrid);

                _popupOverlay = new Grid
                {
                    Background = new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)),
                    IsHitTestVisible = false,
                    Opacity = 0
                };
                Grid.SetRowSpan(_popupOverlay, rootGrid.RowDefinitions.Count);
                rootGrid.Children.Add(_popupOverlay);

                var overlayFade = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.15));
                _popupOverlay.BeginAnimation(Grid.OpacityProperty, overlayFade);
            }

            _isPermissionsPopupOpen = true;
            NavToolbar.PrivacyButton.BorderBrush = GetBrush("Accent");
            NavToolbar.PrivacyButton.Background = GetBrush("ButtonPressed");

            var popup = new Popup
            {
                PlacementTarget = this,
                Placement = PlacementMode.Center,
                StaysOpen = false,
                AllowsTransparency = true,
                PopupAnimation = PopupAnimation.Fade
            };

            popup.Closed += (_, _) =>
            {
                RemovePopupBlur();
                _sitePermissionsPopup = null;
                _isPermissionsPopupOpen = false;
                UpdatePrivacyIndicator();
                NavToolbar.PrivacyButton.Background = GetBrush("ButtonBg");
            };

            popup.Child = BuildSitePermissionsPopupContent(popup);
            if (popup.Child is FrameworkElement sitePermContent)
                UiFx.PlayPopupEntrance(sitePermContent);
            _sitePermissionsPopup = popup;
            popup.IsOpen = true;
        }
        catch (Exception ex)
        {
            _isPermissionsPopupOpen = false;
            Log.Error(ex, "Error occurred");
        }
    }

    private UIElement BuildSitePermissionsPopupContent(Popup popup)
    {
        var settings = _dataStore.Settings;
        var suggestedHost = GetSuggestedPermissionHost();
        var allHosts = GetSitePermissionHosts(settings.SitePermissions, suggestedHost);
        var selectedHost = NormalizePermissionHost(suggestedHost) ?? allHosts.FirstOrDefault() ?? string.Empty;

        var siteCombo = new ComboBox
        {
            ItemsSource = allHosts,
            Height = 32,
            Background = GetBrush("InputBg"),
            Foreground = GetBrush("Text"),
            BorderBrush = GetBrush("Stroke")
        };
        if (allHosts.Contains(selectedHost, StringComparer.OrdinalIgnoreCase))
        {
            siteCombo.SelectedItem = allHosts.First(h => string.Equals(h, selectedHost, StringComparison.OrdinalIgnoreCase));
        }
        var siteHeader = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(selectedHost) ? "No site selected" : $"Permissions for {selectedHost}",
            Foreground = GetBrush("Text"),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12),
            Padding = new Thickness(10, 8, 10, 8),
            Background = GetBrush("ChromeAlt"),
        };

        var permValues = new Dictionary<string, string>();
        var pillRefreshers = new Dictionary<string, Action>();

        foreach (var (key, _) in SitePermissionDefinitions)
        {
            permValues[key] = PermissionStateToUi(
                GetSitePermissionState(settings.SitePermissions, selectedHost, key));
        }

        FrameworkElement BuildPills(string key)
        {
            var pills = new List<Button>();
            var panel = new StackPanel { Orientation = Orientation.Horizontal };

            void refresh()
            {
                var current = permValues[key];
                foreach (var pill in pills)
                {
                    var isSelected = string.Equals(pill.Tag?.ToString(), current, StringComparison.Ordinal);
                    pill.Background = isSelected ? GetBrush("ButtonPressed") : GetBrush("ButtonBg");
                    pill.BorderBrush = isSelected ? GetBrush("Accent") : GetBrush("Stroke");
                    pill.FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal;
                    pill.Opacity = isSelected ? 1 : 0.78;
                }
            }

            foreach (var state in SitePermissionStateOptions)
            {
                var capturedState = state;
                var pill = new Button
                {
                    Content = state,
                    Tag = state,
                    MinWidth = 44,
                    Height = 22,
                    Margin = new Thickness(0, 0, 3, 0),
                    Padding = new Thickness(6, 0, 6, 0),
                    Cursor = Cursors.Hand,
                    FontSize = 11,
                    BorderThickness = new Thickness(1),
                    Background = GetBrush("ButtonBg"),
                    BorderBrush = GetBrush("Stroke"),
                    Foreground = GetBrush("Text"),
                };
                pill.Click += async (_, _) =>
                {
                    try
                    {
                        permValues[key] = capturedState;
                        refresh();
                        await SaveAsync();
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Error occurred");
                    }
                };
                pills.Add(pill);
                panel.Children.Add(pill);
            }

            pillRefreshers[key] = refresh;
            refresh();
            return panel;
        }

        FrameworkElement BuildPermRow(string label, string key)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var labelBlock = new TextBlock
            {
                Text = label,
                Foreground = GetBrush("Text"),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 13
            };
            var pills = BuildPills(key);

            Grid.SetColumn(pills, 2);
            row.Children.Add(labelBlock);
            row.Children.Add(pills);
            return row;
        }

        FrameworkElement BuildPermGroup(string groupName, IEnumerable<(string Key, string Label)> perms)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            panel.Children.Add(new TextBlock
            {
                Text = groupName.ToUpperInvariant(),
                Foreground = GetBrush("Muted"),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6)
            });
            foreach (var (key, label) in perms)
            {
                panel.Children.Add(BuildPermRow(label, key));
            }
            return panel;
        }

        void LoadHost(string host)
        {
            var norm = NormalizePermissionHost(host);
            if (string.IsNullOrWhiteSpace(norm))
            {
                StatusBar.StatusText.Text = "Enter a site domain or URL";
                return;
            }
            selectedHost = norm;
            siteCombo.Text = norm;
            siteHeader.Text = $"Permissions for {norm}";

            foreach (var (key, _) in SitePermissionDefinitions)
            {
                permValues[key] = PermissionStateToUi(
                    GetSitePermissionState(_dataStore.Settings.SitePermissions, norm, key));
                if (pillRefreshers.TryGetValue(key, out var refresh))
                {
                    refresh();
                }
            }
            StatusBar.StatusText.Text = $"Loaded permissions for {norm}";
        }

        siteCombo.SelectionChanged += (_, _) =>
        {
            if (siteCombo.SelectedItem is string host && !string.IsNullOrWhiteSpace(host))
            {
                LoadHost(host);
            }
        };

        async Task SaveAsync()
        {
            var host = NormalizePermissionHost(GetComboText(siteCombo)) ?? NormalizePermissionHost(selectedHost);
            if (string.IsNullOrWhiteSpace(host))
            {
                StatusBar.StatusText.Text = "Choose or enter a site before saving permissions";
                return;
            }

            var states = SitePermissionDefinitions.ToDictionary(
                def => def.Key,
                def => PermissionStateFromUi(permValues[def.Key]));
            var updated = UpdateSitePermissionRules(_dataStore.Settings.SitePermissions, host, states);

            _dataStore.SaveSettings(_dataStore.Settings with { SitePermissions = updated });
            await ApplySitePermissionProfileStateAsync(host, states);

            selectedHost = host;
            siteCombo.Text = host;
            siteCombo.ItemsSource = GetSitePermissionHosts(updated, host);
            StatusBar.StatusText.Text = $"Saved permissions for {host}";
        }

        var resetBtn = MatteButton("Reset to Ask", async (_, _) =>
        {
            try
            {
                foreach (var key in permValues.Keys)
                {
                    permValues[key] = "Ask";
                    if (pillRefreshers.TryGetValue(key, out var refresh))
                    {
                        refresh();
                    }
                }
                await SaveAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error occurred");
            }
        }, new Thickness(0, 0, 8, 0));
        var closeBtn = MatteButton("Close", (_, _) => popup.IsOpen = false);

        var actionBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 4, 0, 4)
        };
        actionBar.Children.Add(resetBtn);
        actionBar.Children.Add(closeBtn);

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = "Site Permissions",
            Foreground = GetBrush("Text"),
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });
        stack.Children.Add(new TextBlock
        {
            Text = _isPrivate
                ? "Private windows use an isolated temporary profile."
                : "Control protected browser features for the current or saved site.",
            Foreground = GetBrush("Muted"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });
        stack.Children.Add(siteHeader);

        stack.Children.Add(SettingRow("Switch site", "Choose a saved site from the list.", siteCombo));

        var grouped = PermissionGroupDefinitions
            .GroupBy(def => def.Group)
            .ToList();

        foreach (var group in grouped)
        {
            stack.Children.Add(BuildPermGroup(
                group.Key,
                group.Select(def => (def.Key, def.Label))));
        }

        stack.Children.Add(actionBar);

        var scrollBarStyle = FindResource("DarkScrollBar") as Style;
        var scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = stack
        };
        if (scrollBarStyle is not null)
            scrollViewer.Resources.Add(typeof(ScrollBar), scrollBarStyle);

        var popupBorder = new Border
        {
            Width = 560,
            MaxHeight = Math.Max(420, ActualHeight - 120),
            Background = GetBrush("ChromeBg"),
            BorderBrush = GetBrush("Stroke"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16),
            Child = scrollViewer
        };

        TextElement.SetFontFamily(popupBorder, new System.Windows.Media.FontFamily("Segoe UI"));
        return popupBorder;
    }

    private Border BuildEntry(string title, string detail, string primaryText, RoutedEventHandler primaryAction, string? secondaryText = null, RoutedEventHandler? secondaryAction = null)
    {
        var root = new Border
        {
            Background = GetBrush("ChromeBg"),
            BorderBrush = GetBrush("SubtleStroke"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 8)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var hover = new Border
        {
            Background = GetBrush("ButtonHover"),
            CornerRadius = new CornerRadius(5),
            Opacity = 0,
            IsHitTestVisible = false,
            Margin = new Thickness(-6)
        };
        Grid.SetColumnSpan(hover, 3);
        grid.Children.Add(hover);
        root.MouseEnter += (_, _) => hover.BeginAnimation(Border.OpacityProperty, Motion.Fade(0, 1, Motion.Fast));
        root.MouseLeave += (_, _) => hover.BeginAnimation(Border.OpacityProperty, Motion.Fade(1, 0, TimeSpan.FromSeconds(0.12)));
        root.Cursor = Cursors.Hand;

        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, Foreground = GetBrush("Text"), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = detail, Foreground = GetBrush("Muted"), FontSize = 12, Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        var primary = MatteButton(primaryText, primaryAction);
        primary.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(primary, 1);
        grid.Children.Add(primary);

        if (secondaryText is not null && secondaryAction is not null)
        {
            var secondary = MatteButton(secondaryText, secondaryAction, new Thickness(8, 0, 0, 0));
            Grid.SetColumn(secondary, 2);
            grid.Children.Add(secondary);
        }

        root.Child = grid;
        return root;
    }

    private FrameworkElement BuildSitePermissionList(IEnumerable<SitePermissionRule> rules, Action<string> loadSite)
    {
        var stack = new StackPanel();
        var groupedRules = rules
            .Where(rule => !string.IsNullOrWhiteSpace(rule.Host))
            .GroupBy(rule => rule.Host, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (groupedRules.Count == 0)
        {
            stack.Children.Add(EmptyText("No sites have saved permission rules yet."));
            return stack;
        }

        foreach (var group in groupedRules)
        {
            var host = group.Key;
            stack.Children.Add(BuildEntry(
                host,
                SitePermissionSummary(group),
                "Open",
                (_, _) => loadSite(host)));
        }

        return stack;
    }

    private static string SitePermissionSummary(IEnumerable<SitePermissionRule> rules)
    {
        var allowed = rules
            .Where(rule => rule.State == "Allow")
            .Select(rule => PermissionLabel(rule.Permission))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(permission => permission, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var blocked = rules
            .Where(rule => rule.State == "Deny")
            .Select(rule => PermissionLabel(rule.Permission))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(permission => permission, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var parts = new List<string>();

        if (allowed.Count > 0)
        {
            parts.Add($"Allowed: {string.Join(", ", allowed)}");
        }

        if (blocked.Count > 0)
        {
            parts.Add($"Blocked: {string.Join(", ", blocked)}");
        }

        return parts.Count == 0 ? "All permissions ask first" : string.Join("  -  ", parts);
    }

    private TextBlock EmptyText(string text)
    {
        return new TextBlock
        {
            Text = text,
            Foreground = GetBrush("Muted"),
            FontSize = 14,
            Margin = new Thickness(0, 8, 0, 0)
        };
    }

    private UIElement BuildExtensionsContent(BrowserSettings settings)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };

        var ublockToggle = ToggleRow("uBlock Origin",
            "Built-in ad blocker. Installed and updated automatically from the Chrome Web Store.",
            _dataStore.Settings.UblockEnabled);
        ublockToggle.Checked += async (_, _) =>
        {
            _dataStore.SaveSettings(_dataStore.Settings with { UblockEnabled = true });
            await EnsureUblockReadyAsync();
            StatusBar.StatusText.Text = "uBlock Origin enabled";
        };
        ublockToggle.Unchecked += async (_, _) =>
        {
            _dataStore.SaveSettings(_dataStore.Settings with { UblockEnabled = false });
            if (_ublockExtension is not null)
            {
                try { await _ublockExtension.EnableAsync(false); }
                catch (Exception ex) { Log.Error(ex, "uBlock disable failed"); }
            }
            StatusBar.StatusText.Text = "uBlock Origin disabled";
        };
        stack.Children.Add(ublockToggle);

        var loadBtn = MatteButton("Load unpacked", async (_, _) =>
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select extension folder"
            };

            if (dialog.ShowDialog() == true)
            {
                if (_profile is null)
                {
                    StatusBar.StatusText.Text = "WebView is not initialized yet.";
                    return;
                }

                try
                {
                    var existing = _dataStore.Settings.Extensions;

                    if (existing.Any(e => string.Equals(e.FolderPath, dialog.FolderName, StringComparison.OrdinalIgnoreCase)))
                    {
                        StatusBar.StatusText.Text = "Extension is already loaded.";
                        return;
                    }

                    var webExt = await _profile.AddBrowserExtensionAsync(dialog.FolderName);
                    var newExt = new ExtensionInfo(webExt.Id, webExt.Name, dialog.FolderName, true, false);
                    var updated = existing.Append(newExt).ToList();
                    _loadedExtensions[webExt.Id] = webExt;
                    _dataStore.SaveSettings(_dataStore.Settings with { Extensions = updated });
                    StatusBar.StatusText.Text = $"Loaded extension: {webExt.Name}";
                    if (_activeTab is not null) RenderNativeSurface(_activeTab, forceRebuild: true);
                }
                catch (Exception ex)
                {
                    StatusBar.StatusText.Text = $"Failed to load extension: {ex.Message}";
                }
            }
        });

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 0)
        };
        buttonRow.Children.Add(loadBtn);

        var crxBtn = MatteButton("Install from CRX...", async (_, _) =>
        {
            try
            {
                var dialog = new OpenFileDialog
                {
                    Title = "Select extension CRX file",
                    Filter = "CRX files (*.crx)|*.crx|All files (*.*)|*.*"
                };

                if (dialog.ShowDialog() == true)
                {
                    await InstallCrxAsync(dialog.FileName);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error occurred");
            }
        }, new Thickness(8, 0, 0, 0));

        buttonRow.Children.Add(crxBtn);
        stack.Children.Add(buttonRow);

        var storeLink = new TextBlock
        {
            Text = "Get more extensions from Chrome Web Store",
            Foreground = GetBrush("Accent"),
            FontSize = 12,
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 8, 0, 12),
            TextDecorations = TextDecorations.Underline
        };
        storeLink.MouseDown += async (_, _) =>
        {
            try
            {
                var tab = _activeTab;
                if (tab is not null && tab.WebView?.CoreWebView2 is not null)
                {
                    await NavigateWebAsync(tab, "https://chromewebstore.google.com/category/extensions");
                }
                else
                {
                    await AddWebTabAsync("https://chromewebstore.google.com/category/extensions");
                }
            }
            catch (Exception ex) { Log.Error(ex, "Error occurred"); }
        };
        stack.Children.Add(storeLink);

        var dropZone = new Border
        {
            Height = 100,
            Margin = new Thickness(0, 0, 0, 12),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(2),
            BorderBrush = GetBrush("SubtleStroke"),
            Background = GetBrush("ButtonBg"),
            AllowDrop = true,
            Child = new TextBlock
            {
                Text = "Drop .crx file here to install",
                Foreground = GetBrush("Muted"),
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        dropZone.DragEnter += (_, e) =>
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                dropZone.BorderBrush = GetBrush("Accent");
                dropZone.Background = GetBrush("ButtonPressed");
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        };

        dropZone.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
        };

        dropZone.DragLeave += (_, _) =>
        {
            dropZone.BorderBrush = GetBrush("SubtleStroke");
            dropZone.Background = GetBrush("ButtonBg");
        };

        dropZone.Drop += async (_, e) =>
        {
            try
            {
                dropZone.BorderBrush = GetBrush("SubtleStroke");
                dropZone.Background = GetBrush("ButtonBg");

                if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                var crxFile = files?.FirstOrDefault(f => f.EndsWith(".crx", StringComparison.OrdinalIgnoreCase));
                if (crxFile is null)
                {
                    StatusBar.StatusText.Text = "Please drop a .crx file.";
                    return;
                }

                await InstallCrxAsync(crxFile);
                e.Handled = true;
            }
            catch (Exception ex) { Log.Error(ex, "Error occurred"); }
        };

        stack.Children.Add(dropZone);

        if (settings.Extensions.Count == 0)
        {
            stack.Children.Add(EmptyText("No extensions loaded."));
            return stack;
        }

        foreach (var ext in settings.Extensions)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 8, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            var nameBlock = new TextBlock
            {
                Text = ext.Name,
                Foreground = GetBrush("Text"),
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Width = 200,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var builtInBadge = ext.IsBuiltIn ? new Border
            {
                Child = new TextBlock
                {
                    Text = "Built-in",
                    Foreground = new SolidColorBrush(Color.FromRgb(255, 255, 255)),
                    FontSize = 10,
                    Padding = new Thickness(4, 1, 4, 1)
                },
                Background = GetBrush("Accent"),
                CornerRadius = new CornerRadius(3),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0)
            } : null;

            var toggle = new CheckBox
            {
                IsChecked = ext.Enabled,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0),
                IsEnabled = !ext.IsBuiltIn
            };

            var capturedExt = ext;
            toggle.Checked += async (_, _) =>
            {
                try
                {
                    await SetExtensionEnabledAsync(capturedExt, true);
                    UpdateExtensionEnabled(capturedExt.Id, true);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error occurred");
                }
            };
            toggle.Unchecked += async (_, _) =>
            {
                try
                {
                    await SetExtensionEnabledAsync(capturedExt, false);
                    UpdateExtensionEnabled(capturedExt.Id, false);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error occurred");
                }
            };

            var removeBtn = MatteButton("Remove", async (_, _) =>
            {
                try
                {
                    await RemoveExtensionAsync(capturedExt);
                    var filtered = _dataStore.Settings.Extensions.Where(e => e.Id != capturedExt.Id).ToList();
                    _dataStore.SaveSettings(_dataStore.Settings with { Extensions = filtered });
                    if (_activeTab is not null) RenderNativeSurface(_activeTab, forceRebuild: true);
                }
                catch (Exception ex)
                {
                    StatusBar.StatusText.Text = $"Failed to remove extension: {ex.Message}";
                }
            });

            removeBtn.IsEnabled = !ext.IsBuiltIn;

            row.Children.Add(nameBlock);
            if (builtInBadge is not null)
            {
                row.Children.Add(builtInBadge);
            }
            row.Children.Add(toggle);

            var manageBtn = MatteButton("Manage", async (_, _) =>
            {
                try
                {
                    var tab = _activeTab;
                    if (tab is not null && tab.WebView?.CoreWebView2 is not null)
                    {
                        await NavigateWebAsync(tab, $"chrome-extension://{capturedExt.Id}/popup.html");
                    }
                    else
                    {
                        await AddWebTabAsync($"chrome-extension://{capturedExt.Id}/popup.html");
                    }
                }
                catch (Exception ex) { Log.Error(ex, "Error occurred"); }
            });
            row.Children.Add(manageBtn);

            row.Children.Add(removeBtn);
            stack.Children.Add(row);
        }

        return stack;
    }

    private async Task SetExtensionEnabledAsync(ExtensionInfo ext, bool enabled)
    {
        if (_loadedExtensions.TryGetValue(ext.Id, out var webExt))
        {
            try
            {
                await webExt.EnableAsync(enabled);
            }
            catch (Exception ex)
            {
                StatusBar.StatusText.Text = $"Failed to {(enabled ? "enable" : "disable")} extension: {ex.Message}";
            }
        }
    }

    private async Task RemoveExtensionAsync(ExtensionInfo ext)
    {
        if (_loadedExtensions.TryGetValue(ext.Id, out var webExt))
        {
            try
            {
                await webExt.RemoveAsync();
            }
            catch (Exception ex)
            {
                StatusBar.StatusText.Text = $"Failed to remove extension: {ex.Message}";
            }
            _loadedExtensions.Remove(ext.Id);
        }
    }

    private void UpdateExtensionEnabled(string id, bool enabled)
    {
        var updated = _dataStore.Settings.Extensions
            .Select(e => e.Id == id ? e with { Enabled = enabled } : e)
            .ToList();
        _dataStore.SaveSettings(_dataStore.Settings with { Extensions = updated });
    }

    private async Task InstallCrxAsync(string crxPath)
    {
        string extractDir;
        string? manifestError = null;
        string? webExtId = null;
        string? webExtName = null;

        try
        {
            var bytes = await File.ReadAllBytesAsync(crxPath);

            int zipStart = FindZipStart(bytes);
            if (zipStart < 0)
            {
                await Dispatcher.InvokeAsync(() => StatusBar.StatusText.Text = "Invalid CRX file: ZIP data not found.");
                return;
            }

            var crxDirName = Path.GetFileNameWithoutExtension(crxPath);
            extractDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TabloidBrowser",
                "Extensions",
                crxDirName);

            if (Directory.Exists(extractDir))
            {
                Directory.Delete(extractDir, recursive: true);
            }

            Directory.CreateDirectory(extractDir);

            using var zipStream = new MemoryStream(bytes, zipStart, bytes.Length - zipStart);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

            var normalizedExtractDir = Path.GetFullPath(extractDir) + Path.DirectorySeparatorChar;
            foreach (var entry in archive.Entries)
            {
                var destPath = Path.GetFullPath(Path.Combine(extractDir, entry.FullName));
                if (!destPath.StartsWith(normalizedExtractDir, StringComparison.OrdinalIgnoreCase))
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

            var manifestPath = Path.Combine(extractDir, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                manifestError = "Invalid CRX file: manifest.json not found.";
                Directory.Delete(extractDir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() => StatusBar.StatusText.Text = $"Failed to install CRX: {ex.Message}");
            return;
        }

        if (manifestError is not null)
        {
            await Dispatcher.InvokeAsync(() => StatusBar.StatusText.Text = manifestError);
            return;
        }

        await Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                if (_profile is null)
                {
                    StatusBar.StatusText.Text = "WebView is not initialized yet.";
                    return;
                }

                var webExt = await _profile.AddBrowserExtensionAsync(extractDir);
                webExtName = webExt.Name;
                webExtId = webExt.Id;

                var existing = _dataStore.Settings.Extensions;
                if (existing.Any(e => e.Id == webExt.Id))
                {
                    StatusBar.StatusText.Text = $"Extension '{webExtName}' is already installed.";
                    return;
                }

                var newExt = new ExtensionInfo(webExt.Id, webExtName, extractDir, true, false);
                _loadedExtensions[webExt.Id] = webExt;
                _dataStore.SaveSettings(_dataStore.Settings with { Extensions = existing.Append(newExt).ToList() });
                StatusBar.StatusText.Text = $"Installed extension: {webExtName}";
                if (_activeTab is not null) RenderNativeSurface(_activeTab, forceRebuild: true);
            }
            catch (Exception ex)
            {
                StatusBar.StatusText.Text = $"Failed to install CRX: {ex.Message}";
            }
        }).Task.Unwrap();
    }

    private static int FindZipStart(byte[] data)
    {
        // CRX3 files: parse the header length from the Cr24 container instead of
        // blind-scanning — headers larger than 2 KB were rejected, and PK bytes
        // inside the header blob produced bogus offsets.
        if (data.Length >= 12 &&
            data[0] == 'C' && data[1] == 'r' && data[2] == '2' && data[3] == '4')
        {
            var headerLength = BitConverter.ToUInt32(data, 8);
            var zipStart = 12 + (int)Math.Min(headerLength, int.MaxValue - 12);
            if (zipStart + 4 <= data.Length &&
                data[zipStart] == 0x50 && data[zipStart + 1] == 0x4B && data[zipStart + 2] == 0x03 && data[zipStart + 3] == 0x04)
            {
                return zipStart;
            }
        }

        int maxSearch = Math.Min(data.Length, 2048);
        for (int i = 0; i <= maxSearch - 4; i++)
        {
            if (data[i] == 0x50 && data[i + 1] == 0x4B && data[i + 2] == 0x03 && data[i + 3] == 0x04)
            {
                return i;
            }
        }
        return -1;
    }

    private FrameworkElement BuildShortcutRows(Dictionary<string, string> overrides, Action onChange)
    {
        var panels = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };

        var defs = new (string Id, string Label)[]
        {
            ("new_tab", "New tab"),
            ("close_tab", "Close tab"),
            ("focus_address", "Focus address bar"),
            ("reload", "Reload page"),
            ("history", "Open history"),
            ("find", "Find in page"),
            ("print", "Print"),
            ("reader_mode", "Reader mode"),
        };

        foreach (var (id, label) in defs)
        {
            var current = overrides.TryGetValue(id, out var ov) ? ov : BrowserSettings.DefaultShortcuts.GetValueOrDefault(id);
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var labelText = new TextBlock
            {
                Text = label,
                Foreground = GetBrush("Text"),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(labelText, 0);
            row.Children.Add(labelText);

            var listening = false;
            var shortcutBtn = new Button
            {
                Content = current,
                Padding = new Thickness(10, 4, 10, 4),
                MinWidth = 100,
                FontSize = 11,
                Cursor = System.Windows.Input.Cursors.Hand
            };

            shortcutBtn.Click += (_, _) =>
            {
                if (listening) return;
                listening = true;
                shortcutBtn.Content = "Press keys...";
                Keyboard.Focus(shortcutBtn);
            };

            shortcutBtn.LostFocus += (_, _) =>
            {
                if (!listening) return;
                listening = false;
                shortcutBtn.Content = current;
            };

            shortcutBtn.KeyDown += (_, e) =>
            {
                if (!listening) return;
                e.Handled = true;

                if (e.Key == Key.Escape)
                {
                    listening = false;
                    shortcutBtn.Content = current;
                    return;
                }

                if (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl ||
                    e.Key == Key.LeftShift || e.Key == Key.RightShift ||
                    e.Key == Key.LeftAlt || e.Key == Key.RightAlt ||
                    e.Key == Key.LWin || e.Key == Key.RWin ||
                    e.Key == Key.None)
                {
                    return;
                }

                var key = e.Key == Key.System ? e.SystemKey : e.Key;
                if (key == Key.None) return;

                var mods = Keyboard.Modifiers;
                if (e.Key == Key.System) mods |= ModifierKeys.Alt;
                var combo = FormatKbShortcut((mods, key));
                current = combo;
                shortcutBtn.Content = combo;
                listening = false;

                if (combo == BrowserSettings.DefaultShortcuts.GetValueOrDefault(id))
                    overrides.Remove(id);
                else
                    overrides[id] = combo;

                onChange();
            };

            Grid.SetColumn(shortcutBtn, 1);
            row.Children.Add(shortcutBtn);
            panels.Children.Add(row);
        }

        return panels;
    }

    private static string FormatKbShortcut((ModifierKeys Mods, Key Key) combo)
    {
        var parts = new List<string>();
        if (combo.Mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (combo.Mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (combo.Mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        parts.Add(combo.Key.ToString());
        return string.Join("+", parts);
    }

    private UIElement BuildAiManagementPage(BrowserTab tab)
    {
        var settings = _dataStore.Settings;
        var toolsEnabled = settings.AiToolsEnabled ?? new Dictionary<string, bool>();

        var stack = new StackPanel();

        var subtitle = new TextBlock
        {
            Text = "Enable or disable individual tools that the AI agent can use.",
            Foreground = GetBrush("Muted"),
            FontSize = 13,
            Margin = new Thickness(0, 2, 0, 16)
        };
        stack.Children.Add(subtitle);

        var toolDefs = new (string Id, string Name, string Description)[]
        {
            ("search", "Search", "Search the web for information"),
            ("search_and_read", "Search & Read", "Search, navigate to a result, and read it"),
            ("read_page", "Read Page", "Read current page content, metadata, or links"),
            ("navigate", "Navigate", "Navigate the current tab to a URL"),
            ("open_tab", "Open Tab", "Open a URL in a new background tab"),
            ("close_tab", "Close Tab", "Close an AI-owned background tab"),
            ("click", "Click Element", "Click an interactive element by selector"),
            ("fill", "Fill Field", "Fill an input field with text"),
            ("list_elements", "List Elements", "List all interactive elements on the page"),
            ("scroll", "Scroll Page", "Scroll the page up or down"),
        };

        var toolToggles = new Dictionary<string, CheckBox>();

        foreach (var (id, name, description) in toolDefs)
        {
            var isEnabled = !toolsEnabled.TryGetValue(id, out var enabled) || enabled;
            var toggle = new CheckBox
            {
                IsChecked = isEnabled,
                Foreground = GetBrush("Text"),
                FontSize = 14,
                Content = name,
                Margin = new Thickness(0, 0, 0, 2)
            };
            toolToggles[id] = toggle;

            var desc = new TextBlock
            {
                Text = description,
                Foreground = GetBrush("Muted"),
                FontSize = 12,
                Margin = new Thickness(24, 0, 0, 8)
            };

            stack.Children.Add(toggle);
            stack.Children.Add(desc);
        }

        stack.Children.Add(new Separator
        {
            Background = GetBrush("Stroke"),
            Margin = new Thickness(0, 16, 0, 16)
        });

        var settingsHeader = new TextBlock
        {
            Text = "Agent Settings",
            Foreground = GetBrush("Text"),
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        };
        stack.Children.Add(settingsHeader);

        var maxIter = SliderRow("Max iterations",
            "Maximum number of tool-calling iterations per request.",
            settings.AiMaxIterations, 1, 20);

        var maxOpenTabs = SliderRow("Max open tabs",
            "Maximum AI-owned tabs the agent can keep open per task (oldest is closed when exceeded).",
            settings.AiPolicy?.MaxOpenTabs ?? AiBrowserPolicy.Default.MaxOpenTabs, 1, 50);

        var confirmSensitive = ToggleRow(
            "Confirm sensitive actions",
            "Ask for confirmation before navigating or opening tabs when page context is active.",
            settings.AiPageContextConfirmation);

        stack.Children.Add(maxIter.Control);
        stack.Children.Add(maxOpenTabs.Control);
        stack.Children.Add(SettingRow("Search engine", "The AI's web search tool uses your browser's selected search engine.", new TextBlock
        {
            Text = settings.SearchEngine,
            Foreground = GetBrush("Text"),
            VerticalAlignment = VerticalAlignment.Center
        }));
        stack.Children.Add(confirmSensitive);

        stack.Children.Add(new Separator
        {
            Background = GetBrush("Stroke"),
            Margin = new Thickness(0, 16, 0, 16)
        });

        var resetBtn = MatteButton("Reset to defaults", (_, _) =>
        {
            foreach (var (id, _, _) in toolDefs)
                toolToggles[id].IsChecked = true;
            maxIter.Input.Value = 10;
            maxOpenTabs.Input.Value = AiBrowserPolicy.Default.MaxOpenTabs;
            confirmSensitive.IsChecked = true;
            SaveAiSettings();
        });
        resetBtn.HorizontalAlignment = HorizontalAlignment.Left;
        stack.Children.Add(resetBtn);

        void SaveAiSettings()
        {
            var newToolsEnabled = new Dictionary<string, bool>();
            foreach (var (id, _, _) in toolDefs)
                newToolsEnabled[id] = toolToggles[id].IsChecked == true;

            // Re-read the live settings so fields this page doesn't own (changed
            // elsewhere since the page was built) are not clobbered by a stale
            // snapshot.
            var saved = _dataStore.Settings with
            {
                AiToolsEnabled = newToolsEnabled,
                AiMaxIterations = ClampInt(((int)maxIter.Input.Value).ToString(), 1, 20, 10),
                AiPageContextConfirmation = confirmSensitive.IsChecked == true,
                AiPolicy = new AiBrowserPolicy(
                    MaxOpenTabs: ClampInt(((int)maxOpenTabs.Input.Value).ToString(), 1, 50, AiBrowserPolicy.Default.MaxOpenTabs),
                    AllowNavigateActiveTab: _dataStore.Settings.AiPolicy?.AllowNavigateActiveTab ?? false,
                    CleanupOnTaskComplete: _dataStore.Settings.AiPolicy?.CleanupOnTaskComplete ?? false)
            };
            _dataStore.SaveSettings(saved);
            ApplySettings();
            NotifySettingsChanged();
            StatusBar.StatusText.Text = "AI settings saved";
        }

        foreach (var toggle in toolToggles.Values)
            toggle.Checked += (_, _) => SaveAiSettings();
        foreach (var toggle in toolToggles.Values)
            toggle.Unchecked += (_, _) => SaveAiSettings();
        maxIter.Input.LostFocus += (_, _) => SaveAiSettings();
        maxOpenTabs.Input.LostFocus += (_, _) => SaveAiSettings();
        confirmSensitive.Checked += (_, _) => SaveAiSettings();
        confirmSensitive.Unchecked += (_, _) => SaveAiSettings();

        return BuildPageShell(tab, "AI Management", stack);
    }
}



