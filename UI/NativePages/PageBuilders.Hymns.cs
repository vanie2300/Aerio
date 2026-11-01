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
                var detail = parts.Count > 0 ? string.Join("   Â·   ", parts) : result.HymnaryUrl;

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
                summary.Text = "Search failed â€” check your connection and try again.";
                resultsPanel.Children.Clear();
                resultsPanel.Children.Add(EmptyText(ex.Message));
            }
        }

        searchBox.TextChanged += (_, _) =>
        {
            searchCts?.Cancel();
            // Don't dispose immediately â€” the previous debounce task may still
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
}
