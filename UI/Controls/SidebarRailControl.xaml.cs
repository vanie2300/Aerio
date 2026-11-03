using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace TabloidBrowser;

public partial class SidebarRailControl : UserControl
{
    public SidebarRailControl() => InitializeComponent();

    private bool _isDragging;

    public event RoutedEventHandler? HomeRequested;
    public event RoutedEventHandler? BookmarksRequested;
    public event RoutedEventHandler? HistoryRequested;
    public event RoutedEventHandler? DownloadsRequested;
    public event RoutedEventHandler? ExtensionsRequested;
    public event RoutedEventHandler? SettingsRequested;
    public event MouseEventHandler? RailDragMoved;
    public event MouseButtonEventHandler? RailDragStarted;
    public event RoutedEventHandler? RailDragEnded;

    public void SetActivePage(BrowserTab? tab)
    {
        var activeButton = tab?.Kind switch
        {
            BrowserTabKind.Start => HomeNavigationButton,
            BrowserTabKind.Bookmarks => BookmarksNavigationButton,
            BrowserTabKind.History => HistoryNavigationButton,
            BrowserTabKind.Downloads => DownloadsNavigationButton,
            BrowserTabKind.Settings when tab.Address.EndsWith("#Extensions", StringComparison.OrdinalIgnoreCase)
                => ExtensionsNavigationButton,
            BrowserTabKind.Settings => SettingsNavigationButton,
            _ => null
        };

        foreach (var button in new[]
                 {
                     HomeNavigationButton,
                     BookmarksNavigationButton,
                     HistoryNavigationButton,
                     DownloadsNavigationButton,
                     ExtensionsNavigationButton,
                     SettingsNavigationButton
                 })
        {
            button.IsChecked = button == activeButton;
        }
    }

    private void Home_Click(object sender, RoutedEventArgs e) => HomeRequested?.Invoke(sender, e);
    private void Bookmarks_Click(object sender, RoutedEventArgs e) => BookmarksRequested?.Invoke(sender, e);
    private void History_Click(object sender, RoutedEventArgs e) => HistoryRequested?.Invoke(sender, e);
    private void Downloads_Click(object sender, RoutedEventArgs e) => DownloadsRequested?.Invoke(sender, e);
    private void Extensions_Click(object sender, RoutedEventArgs e) => ExtensionsRequested?.Invoke(sender, e);
    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(sender, e);

    private void RailDragGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDragging = true;
        RailDragGrip.CaptureMouse();
        RailDragStarted?.Invoke(this, e);
        e.Handled = true;
    }

    private void RailDragGrip_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isDragging && e.LeftButton == MouseButtonState.Pressed)
        {
            RailDragMoved?.Invoke(this, e);
        }
    }

    private void RailDragGrip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        RailDragGrip.ReleaseMouseCapture();
        RailDragEnded?.Invoke(this, new RoutedEventArgs());
        e.Handled = true;
    }

    private void RailDragGrip_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        RailDragEnded?.Invoke(this, new RoutedEventArgs());
    }
}
