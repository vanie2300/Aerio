using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace TabloidBrowser;

/// <summary>
/// Bottom-right downloads shelf: shows live download progress, then keeps the
/// finished items until dismissed (or auto-hides a few seconds after the last
/// download settles).
/// </summary>
public partial class DownloadShelfControl : UserControl
{
    private const int MaxItems = 8;
    private static readonly TimeSpan AutoHideDelay = TimeSpan.FromSeconds(6);

    private readonly ObservableCollection<DownloadShelfItem> _items = [];
    private readonly DispatcherTimer _autoHideTimer = new() { Interval = AutoHideDelay };

    public DownloadShelfControl()
    {
        InitializeComponent();
        ItemsList.ItemsSource = _items;
        _autoHideTimer.Tick += (_, _) =>
        {
            _autoHideTimer.Stop();
            if (!_items.Any(item => item.IsRunning))
            {
                Visibility = Visibility.Collapsed;
            }
        };
    }

    public event Action<DownloadShelfItem>? OpenRequested;
    public event Action<DownloadShelfItem>? RevealRequested;
    public event Action<DownloadShelfItem>? CancelRequested;
    public event Action<DownloadShelfItem>? RemoveRequested;
    public event Action? ShowAllRequested;

    /// <summary>Adds (or re-shows) an item and reveals the shelf.</summary>
    public void Add(DownloadShelfItem item)
    {
        if (item is null) return;

        _items.Insert(0, item);
        while (_items.Count > MaxItems)
        {
            _items.RemoveAt(_items.Count - 1);
        }

        Visibility = Visibility.Visible;
        RestartAutoHide();
    }

    /// <summary>Call after a running item changes state.</summary>
    public void NotifyStateChanged(DownloadShelfItem item)
    {
        if (item is null) return;

        if (item.IsRunning)
        {
            Visibility = Visibility.Visible;
        }

        RestartAutoHide();
    }

    private void RestartAutoHide()
    {
        _autoHideTimer.Stop();
        if (_items.Any(item => item.IsRunning))
        {
            return; // keep the shelf up while something is still downloading
        }

        if (_items.Count > 0)
        {
            _autoHideTimer.Start();
        }
    }

    private void ShowAll_Click(object sender, RoutedEventArgs e)
    {
        Visibility = Visibility.Collapsed;
        ShowAllRequested?.Invoke();
    }

    private void Dismiss_Click(object sender, RoutedEventArgs e) => Visibility = Visibility.Collapsed;

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (ItemFrom(sender) is { } item)
        {
            RevealRequested?.Invoke(item);
        }
    }

    private void PrimaryAction_Click(object sender, RoutedEventArgs e)
    {
        var item = ItemFrom(sender);
        if (item is null) return;

        switch (item.State)
        {
            case DownloadShelfState.Completed:
                OpenRequested?.Invoke(item);
                break;
            case DownloadShelfState.Interrupted:
                _items.Remove(item);
                RemoveRequested?.Invoke(item);
                break;
            default:
                CancelRequested?.Invoke(item);
                break;
        }
    }

    private static DownloadShelfItem? ItemFrom(object sender) =>
        (sender as FrameworkElement)?.DataContext as DownloadShelfItem;
}
