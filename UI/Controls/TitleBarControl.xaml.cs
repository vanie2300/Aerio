using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TabloidBrowser;

public partial class TitleBarControl : UserControl
{
    public TitleBarControl() => InitializeComponent();

    public event RoutedEventHandler? NewTab;
    public event RoutedEventHandler? TabSplit;
    public event RoutedEventHandler? TabPopOut;
    public event RoutedEventHandler? MuteToggle;
    public event RoutedEventHandler? TabClose;
    public event RoutedEventHandler? KeepAiTab;
    public event RoutedEventHandler? Minimize;
    public event RoutedEventHandler? MaximizeRestore;
    public event RoutedEventHandler? CloseWindow;
    public event MouseButtonEventHandler? TabMouseDown;
    public event MouseButtonEventHandler? TabStripMouseDown;
    public event MouseEventHandler? TabStripMouseMove;
    public event MouseButtonEventHandler? TabStripMouseUp;
    public event MouseEventHandler? TabStripLostCapture;
    public event MouseButtonEventHandler? TitleBarMouseDown;
    public event MouseEventHandler? TitleBarMouseMove;
    public event MouseButtonEventHandler? TitleBarMouseUp;

    private void NewTab_Click(object sender, RoutedEventArgs e) => NewTab?.Invoke(sender, e);

    private void SplitTab_Click(object sender, RoutedEventArgs e) => TabSplit?.Invoke(sender, e);

    private void PopOutTab_Click(object sender, RoutedEventArgs e) => TabPopOut?.Invoke(sender, e);

    private void MuteButton_Click(object sender, RoutedEventArgs e) => MuteToggle?.Invoke(sender, e);

    private void CloseTab_Click(object sender, RoutedEventArgs e) => TabClose?.Invoke(sender, e);

    private void KeepAiTab_Click(object sender, RoutedEventArgs e) => KeepAiTab?.Invoke(sender, e);

    private void Minimize_Click(object sender, RoutedEventArgs e) => Minimize?.Invoke(sender, e);

    private void MaximizeRestore_Click(object sender, RoutedEventArgs e) => MaximizeRestore?.Invoke(sender, e);

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => CloseWindow?.Invoke(sender, e);

    private void Tab_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => TabMouseDown?.Invoke(sender, e);

    private void TabStrip_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => TabStripMouseDown?.Invoke(sender, e);

    private void TabStrip_PreviewMouseMove(object sender, MouseEventArgs e) => TabStripMouseMove?.Invoke(sender, e);

    private void TabStrip_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => TabStripMouseUp?.Invoke(sender, e);

    private void TabStrip_LostMouseCapture(object sender, MouseEventArgs e) => TabStripLostCapture?.Invoke(sender, e);

    private void TitleBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => TitleBarMouseDown?.Invoke(sender, e);

    private void TitleBar_PreviewMouseMove(object sender, MouseEventArgs e) => TitleBarMouseMove?.Invoke(sender, e);

    private void TitleBar_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => TitleBarMouseUp?.Invoke(sender, e);
}
