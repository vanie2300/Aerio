using System.IO;
using System.Net.Http;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LiquidGlass;
using LiquidGlass.Wpf;

namespace TabloidBrowser;

public partial class BrowserPaneControl : UserControl
{
    private static readonly HttpClient ShortcutIconClient = new()
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    private List<NewTabShortcut> _shortcuts = [];
    private List<NewTabShortcut> _shortcutDraft = [];
    private List<(TextBox Name, TextBox Url, TextBox IconUrl)> _shortcutEditorInputs = [];
    private bool _showQuickLinks = true;

    public BrowserPaneControl()
    {
        InitializeComponent();
        BrowserPaneHost.SizeChanged += (_, _) => UpdateNewTabDialogCardWidth();
    }

    public event MouseButtonEventHandler? StartSearchSurfaceMouseDown;
    public event KeyEventHandler? StartSearchKeyDown;
    public event RoutedEventHandler? StartSearchGo;
    public event RoutedEventHandler? NewTabDialogSubmit;
    public event Action<string>? NewTabShortcutSelected;
    public event Func<List<NewTabShortcut>, bool>? NewTabShortcutsChanged;

    private void StartSearchSurface_PreviewMouseDown(object s, MouseButtonEventArgs e) => StartSearchSurfaceMouseDown?.Invoke(s, e);
    private void StartSearchBox_PreviewKeyDown(object s, KeyEventArgs e) => StartSearchKeyDown?.Invoke(s, e);
    private void StartSearchGo_Click(object s, RoutedEventArgs e) => StartSearchGo?.Invoke(s, e);
    private void NewTabDialogSubmit_Click(object s, RoutedEventArgs e) => NewTabDialogSubmit?.Invoke(s, e);
    private void NewTabShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url })
        {
            NewTabShortcutSelected?.Invoke(url);
        }
    }

    public void SetNewTabShortcuts(IEnumerable<NewTabShortcut>? shortcuts, bool showQuickLinks = true)
    {
        _shortcuts = NewTabShortcutPolicy.Normalize(shortcuts);
        _showQuickLinks = showQuickLinks;
        NewTabQuickLinksSection.Visibility = _showQuickLinks ? Visibility.Visible : Visibility.Collapsed;
        RenderNewTabShortcuts();
    }

    private void RenderNewTabShortcuts()
    {
        RenderShortcutTiles(NewTabHeroShortcutsPanel, itemsPerRow: 6, loadIcons: _showQuickLinks);
        RenderShortcutTiles(NewTabShortcutsPanel, itemsPerRow: 6, loadIcons: true);
    }

    private void RenderShortcutTiles(Panel panel, int itemsPerRow, bool loadIcons)
    {
        panel.Children.Clear();
        StackPanel? row = null;

        void AddTile(Button tile)
        {
            if (row is null || row.Children.Count == itemsPerRow)
            {
                row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 4)
                };
                panel.Children.Add(row);
            }

            row.Children.Add(tile);
        }

        foreach (var shortcut in _shortcuts)
        {
            var fallback = new TextBlock
            {
                Text = shortcut.Name[..1].ToUpperInvariant(),
                Foreground = (Brush)FindResource("Accent"),
                FontSize = 19,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var icon = new Image
            {
                Tag = shortcut.Url,
                Width = 27,
                Height = 27,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var iconContent = new Grid { Height = 38 };
            iconContent.Children.Add(fallback);
            iconContent.Children.Add(icon);
            var tileContent = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(3, 5, 3, 4)
            };
            tileContent.Children.Add(iconContent);
            tileContent.Children.Add(new TextBlock
            {
                Text = shortcut.Name,
                Foreground = (Brush)FindResource("InkSecondary"),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 3, 0, 0),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 64
            });
            Border? tileHover = null;
            Button tile;
            if (!ReferenceEquals(panel, NewTabHeroShortcutsPanel))
            {
                var tileCard = new Grid();
                tileCard.Children.Add(new Border
                {
                    Background = (Brush)FindResource("StartGlassHighlight"),
                    CornerRadius = new CornerRadius(15),
                    IsHitTestVisible = false
                });
                tileHover = new Border
                {
                    Background = (Brush)FindResource("Accent"),
                    CornerRadius = new CornerRadius(15),
                    Opacity = 0,
                    IsHitTestVisible = false
                };
                tileCard.Children.Add(tileHover);
                tileCard.Children.Add(tileContent);
                tile = new Button
                {
                    Tag = shortcut.Url,
                    Width = 96,
                    Height = 76,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(0),
                    Margin = new Thickness(0),
                    Cursor = Cursors.Hand,
                    ToolTip = $"Open {shortcut.Name}",
                    Content = new Border
                    {
                        Width = 72,
                        Height = 72,
                        CornerRadius = new CornerRadius(16),
                        Background = (Brush)FindResource("StartGlassSurface"),
                        BorderBrush = (Brush)FindResource("StartGlassEdge"),
                        BorderThickness = new Thickness(1),
                        Child = tileCard
                    }
                };
            }
            else
            {
                tile = new GlassButton
                {
                    Tag = shortcut.Url,
                    Width = 72,
                    Height = 72,
                    Margin = new Thickness(12, 2, 12, 2),
                    Padding = new Thickness(0),
                    Cursor = Cursors.Hand,
                    ToolTip = $"Open {shortcut.Name}",
                    Material = GlassMaterial.Regular,
                    Radius = 16,
                    IsInteractive = true,
                    Content = tileContent
                };
            }
            if (tileHover is not null)
            {
                tile.MouseEnter += (_, _) => AnimateShortcutHover(tileHover, 0.16);
                tile.MouseLeave += (_, _) => AnimateShortcutHover(tileHover, 0);
            }
            tile.Click += NewTabShortcut_Click;
            System.Windows.Automation.AutomationProperties.SetName(tile, $"Open {shortcut.Name}");
            AddTile(tile);
            if (loadIcons)
            {
                _ = LoadShortcutIconAsync(icon, fallback, shortcut.Url, shortcut.Name, shortcut.IconUrl);
            }
        }

        var addIcon = new TextBlock
        {
            Text = "\uE710",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            Foreground = (Brush)FindResource("Accent"),
            FontSize = 19,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var addTileContent = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(3, 5, 3, 4)
        };
        var addIconContent = new Grid { Height = 38 };
        addIconContent.Children.Add(addIcon);
        addTileContent.Children.Add(addIconContent);
        addTileContent.Children.Add(new TextBlock
        {
            Text = "Add",
            Foreground = (Brush)FindResource("InkSecondary"),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 3, 0, 0)
        });
        Border? addTileHover = null;
        Button addTile;
        if (!ReferenceEquals(panel, NewTabHeroShortcutsPanel))
        {
            var addTileCard = new Grid();
            addTileCard.Children.Add(new Border
            {
                Background = (Brush)FindResource("StartGlassHighlight"),
                CornerRadius = new CornerRadius(15),
                IsHitTestVisible = false
            });
            addTileHover = new Border
            {
                Background = (Brush)FindResource("Accent"),
                CornerRadius = new CornerRadius(15),
                Opacity = 0,
                IsHitTestVisible = false
            };
            addTileCard.Children.Add(addTileHover);
            addTileCard.Children.Add(addTileContent);
            addTile = new Button
            {
                Width = 96,
                Height = 76,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Margin = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = "Add a shortcut",
                Content = new Border
                {
                    Width = 72,
                    Height = 72,
                    CornerRadius = new CornerRadius(16),
                    Background = (Brush)FindResource("StartGlassSurface"),
                    BorderBrush = (Brush)FindResource("StartGlassEdge"),
                    BorderThickness = new Thickness(1),
                    Child = addTileCard
                }
            };
        }
        else
        {
            addTile = new GlassButton
            {
                Width = 72,
                Height = 72,
                Margin = new Thickness(12, 2, 12, 2),
                Padding = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = "Add a shortcut",
                Material = GlassMaterial.Regular,
                Radius = 16,
                IsInteractive = true,
                Content = addTileContent
            };
        }
        if (addTileHover is not null)
        {
            addTile.MouseEnter += (_, _) => AnimateShortcutHover(addTileHover, 0.16);
            addTile.MouseLeave += (_, _) => AnimateShortcutHover(addTileHover, 0);
        }
        addTile.Click += CustomizeShortcuts_Click;
        System.Windows.Automation.AutomationProperties.SetName(addTile, "Add a shortcut");
        AddTile(addTile);
    }

    private static void AnimateShortcutHover(UIElement overlay, double opacity)
    {
        overlay.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(opacity, TimeSpan.FromMilliseconds(160)));
    }

        private void CustomizeShortcuts_Click(object sender, RoutedEventArgs e)
        {
            _shortcutDraft = _shortcuts.Select(shortcut => shortcut with { }).ToList();
            ShortcutNameInput.Clear();
            ShortcutUrlInput.Clear();
            ShortcutIconInput.Clear();
            ShortcutManagerStatus.Text = "";
            RenderShortcutEditor();
            ShortcutManagerDialog.IsOpen = true;
            ShortcutNameInput.Focus();
        }

        private void RenderShortcutEditor()
        {
            ShortcutEditorList.Children.Clear();
            _shortcutEditorInputs.Clear();
            for (var index = 0; index < _shortcutDraft.Count; index++)
            {
                var shortcutIndex = index;
                var shortcut = _shortcutDraft[index];
                var nameInput = new TextBox
                {
                    Text = shortcut.Name,
                    Height = 38,
                    Padding = new Thickness(8, 0, 8, 0),
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0)
                };
                var urlInput = new TextBox
                {
                    Text = shortcut.Url,
                    Height = 38,
                    Padding = new Thickness(8, 0, 8, 0),
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0)
                };
                var iconInput = new TextBox
                {
                    Text = shortcut.IconUrl ?? "",
                    Height = 38,
                    Padding = new Thickness(8, 0, 8, 0),
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0),
                    ToolTip = "Optional HTTP or HTTPS image URL"
                };
                _shortcutEditorInputs.Add((nameInput, urlInput, iconInput));

                var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.24, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.42, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.34, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(nameInput);
                Grid.SetColumn(urlInput, 1);
                row.Children.Add(urlInput);
                Grid.SetColumn(iconInput, 2);
                row.Children.Add(iconInput);

                var upButton = CreateShortcutEditorButton("\xE74A", "Move up");
                upButton.IsEnabled = index > 0;
                upButton.Click += (_, _) => MoveShortcut(shortcutIndex, -1);
                Grid.SetColumn(upButton, 3);
                row.Children.Add(upButton);
                var downButton = CreateShortcutEditorButton("\xE74B", "Move down");
                downButton.Margin = new Thickness(3, 0, 0, 0);
                downButton.IsEnabled = index < _shortcutDraft.Count - 1;
                downButton.Click += (_, _) => MoveShortcut(shortcutIndex, 1);
                Grid.SetColumn(downButton, 4);
                row.Children.Add(downButton);
                var removeButton = new Button
                {
                    Content = "Remove",
                    MinWidth = 58,
                    Height = 30,
                    Margin = new Thickness(6, 0, 0, 0),
                    Foreground = (Brush)FindResource("Danger"),
                    Background = Brushes.Transparent,
                    BorderBrush = (Brush)FindResource("Stroke"),
                    BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand,
                    ToolTip = $"Remove {shortcut.Name}"
                };
                removeButton.Click += (_, _) =>
                {
                    CaptureShortcutEditorInputs();
                    _shortcutDraft.RemoveAt(shortcutIndex);
                    RenderShortcutEditor();
                };
                Grid.SetColumn(removeButton, 5);
                row.Children.Add(removeButton);
                ShortcutEditorList.Children.Add(row);
            }

            if (_shortcutDraft.Count == 0)
            {
                ShortcutEditorList.Children.Add(new TextBlock
                {
                    Text = "No shortcuts yet. Add one above.",
                    Foreground = (Brush)FindResource("Muted"),
                    FontSize = 12,
                    Margin = new Thickness(4, 8, 4, 8)
                });
            }
        }

        private Button CreateShortcutEditorButton(string glyph, string description) => new()
        {
            Content = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            Width = 28,
            Height = 34,
            Padding = new Thickness(0),
            Style = FindResource("IconButton") as Style,
            ToolTip = description
        };

        private void CaptureShortcutEditorInputs()
        {
            for (var index = 0; index < _shortcutEditorInputs.Count && index < _shortcutDraft.Count; index++)
            {
                var (nameInput, urlInput, iconInput) = _shortcutEditorInputs[index];
                _shortcutDraft[index] = _shortcutDraft[index] with
                {
                    Name = nameInput.Text.Trim(),
                    Url = urlInput.Text.Trim(),
                    IconUrl = iconInput.Text.Trim()
                };
            }
        }

        private void MoveShortcut(int index, int offset)
        {
            CaptureShortcutEditorInputs();
            var target = index + offset;
            if (target < 0 || target >= _shortcutDraft.Count)
            {
                return;
            }

            (_shortcutDraft[index], _shortcutDraft[target]) = (_shortcutDraft[target], _shortcutDraft[index]);
            RenderShortcutEditor();
        }

        private void ShortcutManagerAdd_Click(object sender, RoutedEventArgs e)
        {
            var name = ShortcutNameInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(name) ||
                !NewTabShortcutPolicy.TryNormalizeShortcutUrl(ShortcutUrlInput.Text, out var url) ||
                !NewTabShortcutPolicy.TryNormalizeIconUrl(ShortcutIconInput.Text, out var iconUrl))
            {
                ShortcutManagerStatus.Text = "Enter a name, a valid website URL, and an optional HTTP(S) icon URL.";
                return;
            }

            if (_shortcutDraft.Any(shortcut => string.Equals(shortcut.Url, url, StringComparison.OrdinalIgnoreCase)))
            {
                ShortcutManagerStatus.Text = "That URL is already in your shortcuts.";
                return;
            }

            CaptureShortcutEditorInputs();
            _shortcutDraft.Add(new NewTabShortcut(name, url, iconUrl));
            ShortcutNameInput.Clear();
            ShortcutUrlInput.Clear();
            ShortcutIconInput.Clear();
            ShortcutManagerStatus.Text = "";
            RenderShortcutEditor();
            ShortcutNameInput.Focus();
        }

        private void ShortcutManagerSave_Click(object sender, RoutedEventArgs e)
        {
            CaptureShortcutEditorInputs();
            var normalized = new List<NewTabShortcut>();
            foreach (var shortcut in _shortcutDraft)
            {
                if (string.IsNullOrWhiteSpace(shortcut.Name) ||
                    !NewTabShortcutPolicy.TryNormalizeShortcutUrl(shortcut.Url, out var url) ||
                    !NewTabShortcutPolicy.TryNormalizeIconUrl(shortcut.IconUrl, out var iconUrl))
                {
                    ShortcutManagerStatus.Text = "Each shortcut needs a name, a valid website URL, and an optional HTTP(S) icon URL.";
                    return;
                }

                if (normalized.Any(existing => string.Equals(existing.Url, url, StringComparison.OrdinalIgnoreCase)))
                {
                    ShortcutManagerStatus.Text = "Each shortcut URL must be unique.";
                    return;
                }

                normalized.Add(new NewTabShortcut(shortcut.Name.Trim(), url, iconUrl));
            }

            var saved = NewTabShortcutsChanged?.Invoke([.. normalized]) ?? false;
            if (!saved)
            {
                ShortcutManagerStatus.Text = "Couldn't save shortcuts. Please try again.";
                return;
            }

            _shortcuts = normalized;
            RenderNewTabShortcuts();
            ShortcutManagerDialog.IsOpen = false;
        }

        private void ShortcutManagerCancel_Click(object sender, RoutedEventArgs e) =>
            ShortcutManagerDialog.IsOpen = false;

    /// <summary>
    /// Cross-fade for WebView/tab swaps. WebView2 is an HwndHost, so its WPF
    /// opacity can't animate — a full-size scrim fades away instead, masking
    /// the hard content cut.
    /// </summary>
    public void PlaySwapScrim()
    {
        SwapScrim.BeginAnimation(OpacityProperty, null);
        SwapScrim.Opacity = 1;
        SwapScrim.BeginAnimation(OpacityProperty, Motion.Fade(1, 0, Motion.Base));
    }

    /// <summary>Replays the start-page hero entrance (returning Home).</summary>
    public void PlayStartIntro()
    {
        var transform = StartHero.RenderTransform as TranslateTransform;
        if (transform is null)
        {
            transform = new TranslateTransform(20, 0);
            StartHero.RenderTransform = transform;
        }

        if (!SystemParameters.ClientAreaAnimation)
        {
            StartHero.BeginAnimation(OpacityProperty, null);
            StartHero.Opacity = 1;
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            transform.Y = 0;
            return;
        }

        StartHero.BeginAnimation(OpacityProperty, null);
        StartHero.Opacity = 0;
        transform.BeginAnimation(TranslateTransform.YProperty, null);
        transform.Y = 20;

        StartHero.BeginAnimation(OpacityProperty, Motion.Fade(0, 1, Motion.Hero));
        var rise = new DoubleAnimation(20, 0, Motion.Hero) { EasingFunction = Motion.OutCubic };
        transform.BeginAnimation(TranslateTransform.YProperty, rise);
    }

    /// <summary>Accent focus ring on the start-page search field.</summary>
    public void FocusStartSearch()
    {
        StartSearchBox.Focus();
    }

    public bool IsNewTabDialogOpen => NewTabDialog.IsOpen;

    public void ShowNewTabDialog()
    {
        NewTabDialogAddressBox.Clear();
        UpdateNewTabDialogCardWidth();
        NewTabDialog.IsOpen = true;
        NewTabDialogCard.Opacity = 0;
        var transform = new TranslateTransform(0, 12);
        NewTabDialogCard.RenderTransform = transform;
        NewTabDialogCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        Dispatcher.BeginInvoke(() =>
        {
            NewTabDialogAddressBox.Focus();
            Keyboard.Focus(NewTabDialogAddressBox);
        }, DispatcherPriority.Input);
    }

    private void UpdateNewTabDialogCardWidth()
    {
        if (BrowserPaneHost.ActualWidth <= 0)
        {
            return;
        }

        NewTabDialogCard.Width = Math.Min(640, Math.Max(0, BrowserPaneHost.ActualWidth - 48));
    }

    private async Task LoadShortcutIconAsync(
        Image icon,
        TextBlock fallback,
        string shortcutUrl,
        string shortcutName,
        string? configuredIconUrl)
    {
        var uri = new Uri(shortcutUrl);
        var iconUrls = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredIconUrl))
        {
            iconUrls.Add(configuredIconUrl);
        }

        iconUrls.Add($"{uri.Scheme}://{uri.Host}/favicon.ico");
        iconUrls.Add($"https://www.google.com/s2/favicons?domain_url={Uri.EscapeDataString(shortcutUrl)}&sz=64");
        Exception? lastError = null;
        foreach (var iconUrl in iconUrls)
        {
            try
            {
                var imageBytes = await ShortcutIconClient.GetByteArrayAsync(iconUrl);
                using var stream = new MemoryStream(imageBytes);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!string.Equals(icon.Tag?.ToString(), shortcutUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    icon.Source = image;
                    fallback.Visibility = Visibility.Collapsed;
                });
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        if (lastError is not null)
        {
            Log.Debug(lastError, "Failed to load favicon for new-tab shortcut {Shortcut}", shortcutName);
        }
    }

    public void HideNewTabDialog()
    {
        NewTabDialog.IsOpen = false;
    }

    private void NewTabDialogCancel_Click(object sender, RoutedEventArgs e) => HideNewTabDialog();

    private void NewTabDialogBackdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, sender))
        {
            HideNewTabDialog();
            e.Handled = true;
        }
    }

    private void NewTabDialogCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void NewTabDialogAddressBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Return)
        {
            e.Handled = true;
            NewTabDialogSubmit?.Invoke(sender, new RoutedEventArgs());
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            HideNewTabDialog();
        }
    }

    private void StartSearchBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        SearchBorder.BorderBrush = (Brush)FindResource("Accent");
    }

    private void StartSearchBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        SearchBorder.BorderBrush = (Brush)FindResource("Stroke");
    }
}
