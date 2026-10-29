using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TabloidBrowser;

public sealed record AddressSuggestion(string Title, string Url, string Source);

public partial class NavToolbarControl : UserControl
{
    public NavToolbarControl() => InitializeComponent();

    public event EventHandler? AddressTextChanged;
    public event Action<AddressSuggestion>? AddressSuggestionSelected;
    public event RoutedEventHandler? Back;
    public event RoutedEventHandler? Forward;
    public event RoutedEventHandler? Reload;
    public event RoutedEventHandler? Home;
    public event RoutedEventHandler? Privacy;
    public event KeyEventHandler? AddressKeyDown;
    public event RoutedEventHandler? Go;
    public event RoutedEventHandler? Bookmark;
    public event RoutedEventHandler? ReaderMode;
    public event RoutedEventHandler? Private;
    public event RoutedEventHandler? SleepTabs;
    public event RoutedEventHandler? AiToggle;
    public event RoutedEventHandler? Menu;
    public event RoutedEventHandler? NewTab;
    public event RoutedEventHandler? FullScreen;
    public event RoutedEventHandler? ForceDarkChecked;
    public event RoutedEventHandler? ForceDarkUnchecked;
    public event RoutedEventHandler? FindInPage;
    public event RoutedEventHandler? Print;
    public event RoutedEventHandler? ShowBookmarks;
    public event RoutedEventHandler? ShowHistory;
    public event RoutedEventHandler? ShowDownloads;
    public event RoutedEventHandler? ShowExtensions;
    public event RoutedEventHandler? ShowHymns;
    public event RoutedEventHandler? ZoomIn;
    public event RoutedEventHandler? ZoomOut;
    public event RoutedEventHandler? ResetZoom;
    public event RoutedEventHandler? ClearData;
    public event RoutedEventHandler? SiteData;
    public event RoutedEventHandler? UblockToggle;
    public event RoutedEventHandler? ShowSettings;

    public void SetAddressSuggestions(IReadOnlyList<AddressSuggestion> suggestions)
    {
        AddressSuggestionsList.ItemsSource = suggestions;
        AddressSuggestionsPopup.IsOpen = suggestions.Count > 0;
        AddressSuggestionsBorder.Width = Math.Max(280, AddressBox.ActualWidth);
        AddressSuggestionsList.SelectedIndex = -1;
    }

    public void CloseAddressSuggestions() => AddressSuggestionsPopup.IsOpen = false;

    private void AddressBox_TextChanged(object s, TextChangedEventArgs e) => AddressTextChanged?.Invoke(s, EventArgs.Empty);

    private void AddressBox_PreviewMouseLeftButtonDown(object s, MouseButtonEventArgs e)
    {
        if (AddressBox.IsKeyboardFocusWithin)
        {
            return;
        }

        e.Handled = true;
        AddressBox.Focus();
        AddressBox.SelectAll();
    }

    private void Back_Click(object s, RoutedEventArgs e) => Back?.Invoke(s, e);
    private void Forward_Click(object s, RoutedEventArgs e) => Forward?.Invoke(s, e);
    private void Reload_Click(object s, RoutedEventArgs e) => Reload?.Invoke(s, e);
    private void Home_Click(object s, RoutedEventArgs e) => Home?.Invoke(s, e);
    private void Privacy_Click(object s, RoutedEventArgs e) => Privacy?.Invoke(s, e);
    private void AddressBox_PreviewKeyDown(object s, KeyEventArgs e)
    {
        if (AddressSuggestionsPopup.IsOpen)
        {
            if (e.Key is Key.Down or Key.Up)
            {
                var count = AddressSuggestionsList.Items.Count;
                if (count > 0)
                {
                    var step = e.Key == Key.Down ? 1 : -1;
                    var selected = AddressSuggestionsList.SelectedIndex;
                    AddressSuggestionsList.SelectedIndex = selected < 0
                        ? (step > 0 ? 0 : count - 1)
                        : (selected + step + count) % count;
                    AddressSuggestionsList.ScrollIntoView(AddressSuggestionsList.SelectedItem);
                    e.Handled = true;
                    return;
                }
            }
            else if (e.Key == Key.Escape)
            {
                CloseAddressSuggestions();
                e.Handled = true;
                return;
            }
            else if ((e.Key is Key.Enter or Key.Return) &&
                     AddressSuggestionsList.SelectedItem is AddressSuggestion suggestion)
            {
                AddressSuggestionSelected?.Invoke(suggestion);
                CloseAddressSuggestions();
                e.Handled = true;
                return;
            }
        }

        AddressKeyDown?.Invoke(s, e);
    }

    private void AddressSuggestions_MouseLeftButtonUp(object s, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(AddressSuggestionsList, e.OriginalSource as DependencyObject) is ListBoxItem item &&
            item.DataContext is AddressSuggestion suggestion)
        {
            AddressSuggestionSelected?.Invoke(suggestion);
            CloseAddressSuggestions();
        }
    }

    private void Go_Click(object s, RoutedEventArgs e) => Go?.Invoke(s, e);
    private void Bookmark_Click(object s, RoutedEventArgs e) => Bookmark?.Invoke(s, e);
    private void ReaderMode_Click(object s, RoutedEventArgs e) => ReaderMode?.Invoke(s, e);
    private void Private_Click(object s, RoutedEventArgs e) => Private?.Invoke(s, e);
    private void SleepTabs_Click(object s, RoutedEventArgs e) => SleepTabs?.Invoke(s, e);
    private void AiToggle_Click(object s, RoutedEventArgs e) => AiToggle?.Invoke(s, e);
    private void Menu_Click(object s, RoutedEventArgs e) => Menu?.Invoke(s, e);
    private void NewTab_Click(object s, RoutedEventArgs e) => NewTab?.Invoke(s, e);
    private void FullScreen_Click(object s, RoutedEventArgs e) => FullScreen?.Invoke(s, e);
    private void ForceDarkMenu_Checked(object s, RoutedEventArgs e) => ForceDarkChecked?.Invoke(s, e);
    private void ForceDarkMenu_Unchecked(object s, RoutedEventArgs e) => ForceDarkUnchecked?.Invoke(s, e);
    private void FindInPage_Click(object s, RoutedEventArgs e) => FindInPage?.Invoke(s, e);
    private void Print_Click(object s, RoutedEventArgs e) => Print?.Invoke(s, e);
    private void ShowBookmarks_Click(object s, RoutedEventArgs e) => ShowBookmarks?.Invoke(s, e);
    private void ShowHistory_Click(object s, RoutedEventArgs e) => ShowHistory?.Invoke(s, e);
    private void ShowDownloads_Click(object s, RoutedEventArgs e) => ShowDownloads?.Invoke(s, e);
    private void ShowExtensions_Click(object s, RoutedEventArgs e) => ShowExtensions?.Invoke(s, e);
    private void ShowHymns_Click(object s, RoutedEventArgs e) => ShowHymns?.Invoke(s, e);
    private void ZoomIn_Click(object s, RoutedEventArgs e) => ZoomIn?.Invoke(s, e);
    private void ZoomOut_Click(object s, RoutedEventArgs e) => ZoomOut?.Invoke(s, e);
    private void ResetZoom_Click(object s, RoutedEventArgs e) => ResetZoom?.Invoke(s, e);
    private void ClearData_Click(object s, RoutedEventArgs e) => ClearData?.Invoke(s, e);
    private void SiteData_Click(object s, RoutedEventArgs e) => SiteData?.Invoke(s, e);
    private void UblockToggle_Click(object s, RoutedEventArgs e) => UblockToggle?.Invoke(s, e);
    private void ShowSettings_Click(object s, RoutedEventArgs e) => ShowSettings?.Invoke(s, e);
}
