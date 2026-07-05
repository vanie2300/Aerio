using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TabloidBrowser;

public partial class BrowserPaneControl : UserControl
{
    public BrowserPaneControl() => InitializeComponent();

    public event MouseButtonEventHandler? StartSearchSurfaceMouseDown;
    public event KeyEventHandler? StartSearchKeyDown;
    public event RoutedEventHandler? StartSearchGo;
    public event RoutedEventHandler? QuickDuckDuckGo;
    public event RoutedEventHandler? QuickStartpage;
    public event RoutedEventHandler? QuickGitHub;
    public event RoutedEventHandler? QuickHackerNews;
    public event RoutedEventHandler? FocusSplitTab;
    public event RoutedEventHandler? CloseSplit;

    private void StartSearchSurface_PreviewMouseDown(object s, MouseButtonEventArgs e) => StartSearchSurfaceMouseDown?.Invoke(s, e);
    private void StartSearchBox_PreviewKeyDown(object s, KeyEventArgs e) => StartSearchKeyDown?.Invoke(s, e);
    private void StartSearchGo_Click(object s, RoutedEventArgs e) => StartSearchGo?.Invoke(s, e);
    private void QuickDuckDuckGo_Click(object s, RoutedEventArgs e) => QuickDuckDuckGo?.Invoke(s, e);
    private void QuickStartpage_Click(object s, RoutedEventArgs e) => QuickStartpage?.Invoke(s, e);
    private void QuickGitHub_Click(object s, RoutedEventArgs e) => QuickGitHub?.Invoke(s, e);
    private void QuickHackerNews_Click(object s, RoutedEventArgs e) => QuickHackerNews?.Invoke(s, e);
    private void FocusSplitTab_Click(object s, RoutedEventArgs e) => FocusSplitTab?.Invoke(s, e);
    private void CloseSplit_Click(object s, RoutedEventArgs e) => CloseSplit?.Invoke(s, e);
}
