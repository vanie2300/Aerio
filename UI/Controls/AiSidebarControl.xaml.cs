using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TabloidBrowser;

public partial class AiSidebarControl : UserControl
{
    public AiSidebarControl() => InitializeComponent();

    public event RoutedEventHandler? AiClearChat;
    public event RoutedEventHandler? AiSidebarClose;
    public event RoutedEventHandler? AiSend;
    public event KeyEventHandler? AiInputKeyDown;

    private void AiClearChat_Click(object s, RoutedEventArgs e) => AiClearChat?.Invoke(s, e);
    private void AiSidebarClose_Click(object s, RoutedEventArgs e) => AiSidebarClose?.Invoke(s, e);
    private void AiSend_Click(object s, RoutedEventArgs e) => AiSend?.Invoke(s, e);
    private void AiInput_PreviewKeyDown(object s, KeyEventArgs e) => AiInputKeyDown?.Invoke(s, e);
}
