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
            if (_dataStore.ClearHistory())
            {
                RenderNativeSurface(tab, forceRebuild: true);
            }
            else
            {
                StatusBar.StatusText.Text = "Couldn't save the cleared history";
            }
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
                        if (_dataStore.RemoveHistory(item.Url, item.VisitedAt))
                        {
                            PopulateEntries(searchBox.Text?.Trim() ?? "");
                        }
                        else
                        {
                            StatusBar.StatusText.Text = "Couldn't remove the history item";
                        }
                    }));
            }
        }

        PopulateEntries("");
        searchBox.TextChanged += (_, _) => PopulateEntries(searchBox.Text?.Trim() ?? "");

        return BuildPageShell(tab, "History", stack);
    }

    private UIElement BuildDownloadsPage(BrowserTab tab)
    {
        var stack = new StackPanel();
        var clear = MatteButton("Clear list", (_, _) =>
        {
            if (_dataStore.ClearDownloads())
            {
                RenderNativeSurface(tab, forceRebuild: true);
            }
            else
            {
                StatusBar.StatusText.Text = "Couldn't save the cleared downloads list";
            }
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
    private static FrameworkElement StackOf(params FrameworkElement[] elements)
    {
        var stack = new StackPanel();
        foreach (var e in elements) stack.Children.Add(e);
        return stack;
    }

    private FrameworkElement FieldLabelRow(string label, FrameworkElement control)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };
        stack.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 13.5,
            FontWeight = FontWeights.Medium,
            Foreground = GetBrush("Text"),
            Margin = new Thickness(0, 0, 0, 9)
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
        // A restrained tint lets the label and thin purple indicator lead.
        if (GetBrush("Accent") is SolidColorBrush solid)
            return new SolidColorBrush(Color.FromArgb(0x15, solid.Color.R, solid.Color.G, solid.Color.B));
        return GetBrush("SurfaceRaised");
    }

}
