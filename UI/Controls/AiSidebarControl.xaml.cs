using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace TabloidBrowser;

public partial class AiSidebarControl : UserControl
{
    private const int MaxTabTitleLength = 22;

    private RotateTransform? _spinnerTransform;
    private DoubleAnimation? _spinnerAnimation;

    public AiSidebarControl() => InitializeComponent();

    public event RoutedEventHandler? AiClearChat;
    public event RoutedEventHandler? AiSidebarClose;
    public event RoutedEventHandler? AiSend;
    public event KeyEventHandler? AiInputKeyDown;
    public event EventHandler<string>? WorkspaceKeepTab;
    public event EventHandler<string>? WorkspaceCloseTab;
    public event RoutedEventHandler? WorkspaceKeepAll;
    public event RoutedEventHandler? WorkspaceCloseAll;

    public void ShowToolActivity(string toolName)
    {
        AiActivityLabel.Text = GetToolActivityLabel(toolName);

        if (_spinnerTransform is null)
            BuildSpinner();

        if (_spinnerAnimation is not null && _spinnerTransform is not null)
            _spinnerTransform.BeginAnimation(RotateTransform.AngleProperty, _spinnerAnimation);

        AiActivityStrip.BeginAnimation(OpacityProperty, null);
        AiActivityStrip.Visibility = Visibility.Visible;
        var fadeIn = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(180)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        AiActivityStrip.BeginAnimation(OpacityProperty, fadeIn);
    }

    public void HideToolActivity()
    {
        _spinnerTransform?.BeginAnimation(RotateTransform.AngleProperty, null);

        AiActivityStrip.BeginAnimation(OpacityProperty, null);
        var fadeOut = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(140)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        fadeOut.Completed += (_, _) => AiActivityStrip.Visibility = Visibility.Collapsed;
        AiActivityStrip.BeginAnimation(OpacityProperty, fadeOut);
    }

    private void BuildSpinner()
    {
        var center = new Point(6.5, 6.5);
        const double radius = 5.5;

        var arc = new Path
        {
            Stroke = (Brush)FindResource("Cyan"),
            StrokeThickness = 1.75,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 13,
            Height = 13
        };

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(center.X, center.Y - radius), false, false);
            ctx.ArcTo(new Point(center.X + (radius * -0.866), center.Y - (radius * 0.5)), new Size(radius, radius), 0, true, SweepDirection.Clockwise, true, false);
        }
        geo.Freeze();
        arc.Data = geo;

        var transform = new RotateTransform(0, center.X, center.Y);
        arc.RenderTransform = transform;

        var animation = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(0.8)))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };

        AiActivitySpinner.Children.Clear();
        AiActivitySpinner.Children.Add(arc);
        _spinnerTransform = transform;
        _spinnerAnimation = animation;
    }

    private static string GetToolActivityLabel(string toolName) => toolName.ToLowerInvariant() switch
    {
        "search" or "web_search" or "search_and_read" or "search_followup" => "Searching the web...",
        "read_page" or "get_page_content" or "get_page_metadata" or "extract_page_links"
            or "get_selected_text" or "list_elements" or "get_interactive_elements" => "Reading page...",
        "navigate" or "navigate_to" or "open_tab" or "close_tab" => "Opening / navigating...",
        "click" or "click_element" or "fill" or "fill_field" or "scroll" or "scroll_page" => "Interacting with page...",
        _ => $"Using {toolName}..."
    };

    private void AiClearChat_Click(object s, RoutedEventArgs e) => AiClearChat?.Invoke(s, e);
    private void AiSidebarClose_Click(object s, RoutedEventArgs e) => AiSidebarClose?.Invoke(s, e);
    private void AiSend_Click(object s, RoutedEventArgs e) => AiSend?.Invoke(s, e);
    private void AiInput_PreviewKeyDown(object s, KeyEventArgs e) => AiInputKeyDown?.Invoke(s, e);
    private void WorkspaceKeepAll_Click(object s, RoutedEventArgs e) => WorkspaceKeepAll?.Invoke(s, e);
    private void WorkspaceCloseAll_Click(object s, RoutedEventArgs e) => WorkspaceCloseAll?.Invoke(s, e);

    public void ShowWorkspace(string goal, int tabCount, int maxTabs, AiTaskStatus status)
    {
        WorkspacePanel.Visibility = Visibility.Visible;
        WorkspaceGoal.Text = goal.Length > 40 ? goal[..37] + "..." : goal;
        WorkspaceTabCount.Text = $"{tabCount} / {maxTabs}";
        WorkspaceStatus.Text = FormatTaskStatus(status);
        WorkspaceStatus.Foreground = status == AiTaskStatus.Running
            ? (Brush)FindResource("Cyan")
            : (Brush)FindResource("Muted");
        RebuildTabsList([]);
    }

    public void HideWorkspace()
    {
        WorkspacePanel.Visibility = Visibility.Collapsed;
        WorkspaceTabsList.Children.Clear();
    }

    public void UpdateWorkspace(int tabCount, int maxTabs, AiTaskStatus status)
    {
        if (WorkspacePanel.Visibility != Visibility.Visible) return;
        WorkspaceTabCount.Text = $"{tabCount} / {maxTabs}";
        WorkspaceStatus.Text = FormatTaskStatus(status);
        WorkspaceStatus.Foreground = status == AiTaskStatus.Running
            ? (Brush)FindResource("Cyan")
            : (Brush)FindResource("Muted");
    }

    public void UpdateWorkspaceTabs(List<(string Id, string Title, bool IsAiOwned)> tabs, int maxTabs, AiTaskStatus status)
    {
        if (WorkspacePanel.Visibility != Visibility.Visible) return;
        WorkspaceTabCount.Text = $"{tabs.Count} / {maxTabs}";
        WorkspaceStatus.Text = FormatTaskStatus(status);
        WorkspaceStatus.Foreground = status == AiTaskStatus.Running
            ? (Brush)FindResource("Cyan")
            : (Brush)FindResource("Muted");
        RebuildTabsList(tabs);
    }

    private void RebuildTabsList(List<(string Id, string Title, bool IsAiOwned)> tabs)
    {
        WorkspaceTabsList.Children.Clear();

        if (tabs.Count == 0)
        {
            WorkspaceTabsList.Children.Add(new TextBlock
            {
                Text = "No AI tabs",
                Foreground = (Brush)FindResource("Muted"),
                FontSize = 10,
                Margin = new Thickness(0, 2, 0, 0)
            });
            return;
        }

        foreach (var (id, title, isAiOwned) in tabs)
        {
            var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });

            var icon = new TextBlock
            {
                Text = isAiOwned ? "\U0001F916 " : "",
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(icon, 0);

            var titleText = new TextBlock
            {
                Text = TruncateTitle(title),
                Foreground = (Brush)FindResource("Text"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 180
            };
            Grid.SetColumn(titleText, 0);
            Grid.SetColumnSpan(titleText, 1);

            var innerGrid = new Grid();
            innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var keepBtn = new Button
            {
                Content = "\u2714",
                FontSize = 9,
                Width = 20,
                Height = 18,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 2, 0),
                ToolTip = "Keep tab (transfer to user)",
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                Foreground = (Brush)FindResource("Muted"),
                Style = FindResource("IconButton") as Style
            };
            keepBtn.Click += (_, _) => WorkspaceKeepTab?.Invoke(this, id);
            Grid.SetColumn(keepBtn, 0);

            var closeBtn = new Button
            {
                Content = "\u2716",
                FontSize = 9,
                Width = 20,
                Height = 18,
                Padding = new Thickness(0),
                Margin = new Thickness(2, 0, 0, 0),
                ToolTip = "Close tab",
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                Foreground = (Brush)FindResource("Muted"),
                Style = FindResource("IconButton") as Style
            };
            closeBtn.Click += (_, _) => WorkspaceCloseTab?.Invoke(this, id);
            Grid.SetColumn(closeBtn, 1);

            innerGrid.Children.Add(keepBtn);
            innerGrid.Children.Add(closeBtn);
            Grid.SetColumn(innerGrid, 1);

            var titleRow = new Grid();
            titleRow.Children.Add(titleText);
            titleRow.Children.Add(icon);
            Grid.SetColumn(titleRow, 0);

            row.Children.Add(titleRow);
            row.Children.Add(innerGrid);

            var rowBorder = new Border
            {
                Child = row,
                Padding = new Thickness(2, 1, 2, 1),
                CornerRadius = new CornerRadius(3),
                Background = Brushes.Transparent
            };
            rowBorder.MouseEnter += (_, _) =>
                rowBorder.Background = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255));
            rowBorder.MouseLeave += (_, _) =>
                rowBorder.Background = Brushes.Transparent;

            WorkspaceTabsList.Children.Add(rowBorder);
        }
    }

    private static string TruncateTitle(string title)
    {
        if (string.IsNullOrEmpty(title)) return "(untitled)";
        return title.Length > MaxTabTitleLength ? title[..(MaxTabTitleLength - 2)] + ".." : title;
    }

    private static string FormatTaskStatus(AiTaskStatus status) => status switch
    {
        AiTaskStatus.Running => "Running",
        AiTaskStatus.Completed => "Completed",
        AiTaskStatus.Failed => "Failed",
        AiTaskStatus.Cancelled => "Cancelled",
        _ => ""
    };
}
