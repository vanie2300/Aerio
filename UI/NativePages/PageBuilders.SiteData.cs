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
    private void RemovePopupBlur()
    {
        if (Content is Grid root)
        {
            root.Effect = null;
        }

        if (_popupOverlay is not null && Content is Grid parent)
        {
            parent.Children.Remove(_popupOverlay);
            _popupOverlay = null;
        }
    }

    private async void ShowSitePermissionsPopup()
    {
        try
        {
            if (_isPermissionsPopupOpen) return;
            if (_isChangelogOpen && _changelogPopup is not null) _changelogPopup.IsOpen = false;

            try { await SyncProfilePermissionSettingsAsync(); } catch (Exception ex) { Log.Error(ex, "Error occurred"); }

            RemovePopupBlur();

            if (Content is Grid rootGrid)
            {
                UiFx.PlayBackdropBlur(rootGrid);

                _popupOverlay = new Grid
                {
                    Background = new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)),
                    IsHitTestVisible = false,
                    Opacity = 0
                };
                Grid.SetRowSpan(_popupOverlay, rootGrid.RowDefinitions.Count);
                rootGrid.Children.Add(_popupOverlay);

                var overlayFade = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.15));
                _popupOverlay.BeginAnimation(Grid.OpacityProperty, overlayFade);
            }

            _isPermissionsPopupOpen = true;
            NavToolbar.PrivacyButton.BorderBrush = GetBrush("Accent");
            NavToolbar.PrivacyButton.Background = GetBrush("ButtonPressed");

            var popup = new Popup
            {
                PlacementTarget = this,
                Placement = PlacementMode.Center,
                StaysOpen = false,
                AllowsTransparency = true,
                PopupAnimation = PopupAnimation.Fade
            };

            popup.Closed += (_, _) =>
            {
                RemovePopupBlur();
                _sitePermissionsPopup = null;
                _isPermissionsPopupOpen = false;
                UpdatePrivacyIndicator();
                NavToolbar.PrivacyButton.Background = GetBrush("ButtonBg");
            };

            popup.Child = BuildSitePermissionsPopupContent(popup);
            if (popup.Child is FrameworkElement sitePermContent)
                UiFx.PlayPopupEntrance(sitePermContent);
            _sitePermissionsPopup = popup;
            popup.IsOpen = true;
        }
        catch (Exception ex)
        {
            _isPermissionsPopupOpen = false;
            Log.Error(ex, "Error occurred");
        }
    }

    private UIElement BuildSitePermissionsPopupContent(Popup popup)
    {
        var settings = _dataStore.Settings;
        var suggestedHost = GetSuggestedPermissionHost();
        var allHosts = GetSitePermissionHosts(settings.SitePermissions, suggestedHost);
        var selectedHost = NormalizePermissionHost(suggestedHost) ?? allHosts.FirstOrDefault() ?? string.Empty;

        var siteCombo = new ComboBox
        {
            ItemsSource = allHosts,
            Height = 32,
            Background = GetBrush("InputBg"),
            Foreground = GetBrush("Text"),
            BorderBrush = GetBrush("Stroke")
        };
        if (allHosts.Contains(selectedHost, StringComparer.OrdinalIgnoreCase))
        {
            siteCombo.SelectedItem = allHosts.First(h => string.Equals(h, selectedHost, StringComparison.OrdinalIgnoreCase));
        }
        var siteHeader = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(selectedHost) ? "No site selected" : $"Permissions for {selectedHost}",
            Foreground = GetBrush("Text"),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12),
            Padding = new Thickness(10, 8, 10, 8),
            Background = GetBrush("ChromeAlt"),
        };

        var permValues = new Dictionary<string, string>();
        var pillRefreshers = new Dictionary<string, Action>();

        foreach (var (key, _) in SitePermissionDefinitions)
        {
            permValues[key] = PermissionStateToUi(
                GetSitePermissionState(settings.SitePermissions, selectedHost, key));
        }

        FrameworkElement BuildPills(string key)
        {
            var pills = new List<Button>();
            var panel = new StackPanel { Orientation = Orientation.Horizontal };

            void refresh()
            {
                var current = permValues[key];
                foreach (var pill in pills)
                {
                    var isSelected = string.Equals(pill.Tag?.ToString(), current, StringComparison.Ordinal);
                    pill.Background = isSelected ? GetBrush("ButtonPressed") : GetBrush("ButtonBg");
                    pill.BorderBrush = isSelected ? GetBrush("Accent") : GetBrush("Stroke");
                    pill.FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal;
                    pill.Opacity = isSelected ? 1 : 0.78;
                }
            }

            foreach (var state in SitePermissionStateOptions)
            {
                var capturedState = state;
                var pill = new Button
                {
                    Content = state,
                    Tag = state,
                    MinWidth = 44,
                    Height = 22,
                    Margin = new Thickness(0, 0, 3, 0),
                    Padding = new Thickness(6, 0, 6, 0),
                    Cursor = Cursors.Hand,
                    FontSize = 11,
                    BorderThickness = new Thickness(1),
                    Background = GetBrush("ButtonBg"),
                    BorderBrush = GetBrush("Stroke"),
                    Foreground = GetBrush("Text"),
                };
                pill.Click += async (_, _) =>
                {
                    try
                    {
                        permValues[key] = capturedState;
                        refresh();
                        await SaveAsync();
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Error occurred");
                    }
                };
                pills.Add(pill);
                panel.Children.Add(pill);
            }

            pillRefreshers[key] = refresh;
            refresh();
            return panel;
        }

        FrameworkElement BuildPermRow(string label, string key)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var labelBlock = new TextBlock
            {
                Text = label,
                Foreground = GetBrush("Text"),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 13
            };
            var pills = BuildPills(key);

            Grid.SetColumn(pills, 2);
            row.Children.Add(labelBlock);
            row.Children.Add(pills);
            return row;
        }

        FrameworkElement BuildPermGroup(string groupName, IEnumerable<(string Key, string Label)> perms)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            panel.Children.Add(new TextBlock
            {
                Text = groupName.ToUpperInvariant(),
                Foreground = GetBrush("Muted"),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6)
            });
            foreach (var (key, label) in perms)
            {
                panel.Children.Add(BuildPermRow(label, key));
            }
            return panel;
        }

        void LoadHost(string host)
        {
            var norm = NormalizePermissionHost(host);
            if (string.IsNullOrWhiteSpace(norm))
            {
                StatusBar.StatusText.Text = "Enter a site domain or URL";
                return;
            }
            selectedHost = norm;
            siteCombo.Text = norm;
            siteHeader.Text = $"Permissions for {norm}";

            foreach (var (key, _) in SitePermissionDefinitions)
            {
                permValues[key] = PermissionStateToUi(
                    GetSitePermissionState(_dataStore.Settings.SitePermissions, norm, key));
                if (pillRefreshers.TryGetValue(key, out var refresh))
                {
                    refresh();
                }
            }
            StatusBar.StatusText.Text = $"Loaded permissions for {norm}";
        }

        siteCombo.SelectionChanged += (_, _) =>
        {
            if (siteCombo.SelectedItem is string host && !string.IsNullOrWhiteSpace(host))
            {
                LoadHost(host);
            }
        };

        async Task SaveAsync()
        {
            var host = NormalizePermissionHost(GetComboText(siteCombo)) ?? NormalizePermissionHost(selectedHost);
            if (string.IsNullOrWhiteSpace(host))
            {
                StatusBar.StatusText.Text = "Choose or enter a site before saving permissions";
                return;
            }

            var states = SitePermissionDefinitions.ToDictionary(
                def => def.Key,
                def => PermissionStateFromUi(permValues[def.Key]));
            var updated = UpdateSitePermissionRules(_dataStore.Settings.SitePermissions, host, states);

            _dataStore.SaveSettings(_dataStore.Settings with { SitePermissions = updated });
            await ApplySitePermissionProfileStateAsync(host, states);

            selectedHost = host;
            siteCombo.Text = host;
            siteCombo.ItemsSource = GetSitePermissionHosts(updated, host);
            StatusBar.StatusText.Text = $"Saved permissions for {host}";
        }

        var resetBtn = MatteButton("Reset to Ask", async (_, _) =>
        {
            try
            {
                foreach (var key in permValues.Keys)
                {
                    permValues[key] = "Ask";
                    if (pillRefreshers.TryGetValue(key, out var refresh))
                    {
                        refresh();
                    }
                }
                await SaveAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error occurred");
            }
        }, new Thickness(0, 0, 8, 0));
        var closeBtn = MatteButton("Close", (_, _) => popup.IsOpen = false);

        var actionBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 4, 0, 4)
        };
        actionBar.Children.Add(resetBtn);
        actionBar.Children.Add(closeBtn);

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = "Site Permissions",
            Foreground = GetBrush("Text"),
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });
        stack.Children.Add(new TextBlock
        {
            Text = _isPrivate
                ? "Private windows use an isolated temporary profile."
                : "Control protected browser features for the current or saved site.",
            Foreground = GetBrush("Muted"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });
        stack.Children.Add(siteHeader);

        stack.Children.Add(SettingRow("Switch site", "Choose a saved site from the list.", siteCombo));

        var grouped = PermissionGroupDefinitions
            .GroupBy(def => def.Group)
            .ToList();

        foreach (var group in grouped)
        {
            stack.Children.Add(BuildPermGroup(
                group.Key,
                group.Select(def => (def.Key, def.Label))));
        }

        stack.Children.Add(actionBar);

        var scrollBarStyle = FindResource("DarkScrollBar") as Style;
        var scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = stack
        };
        if (scrollBarStyle is not null)
            scrollViewer.Resources.Add(typeof(ScrollBar), scrollBarStyle);

        var popupBorder = new Border
        {
            Width = 560,
            MaxHeight = Math.Max(420, ActualHeight - 120),
            Background = GetBrush("ChromeBg"),
            BorderBrush = GetBrush("Stroke"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16),
            Child = scrollViewer
        };

        TextElement.SetFontFamily(popupBorder, new System.Windows.Media.FontFamily("Segoe UI"));
        return popupBorder;
    }

    private Border BuildEntry(string title, string detail, string primaryText, RoutedEventHandler primaryAction, string? secondaryText = null, RoutedEventHandler? secondaryAction = null)
    {
        var root = new Border
        {
            Background = GetBrush("ChromeBg"),
            BorderBrush = GetBrush("SubtleStroke"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 8)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var hover = new Border
        {
            Background = GetBrush("ButtonHover"),
            CornerRadius = new CornerRadius(5),
            Opacity = 0,
            IsHitTestVisible = false,
            Margin = new Thickness(-6)
        };
        Grid.SetColumnSpan(hover, 3);
        grid.Children.Add(hover);
        root.MouseEnter += (_, _) => hover.BeginAnimation(Border.OpacityProperty, Motion.Fade(0, 1, Motion.Fast));
        root.MouseLeave += (_, _) => hover.BeginAnimation(Border.OpacityProperty, Motion.Fade(1, 0, TimeSpan.FromSeconds(0.12)));
        root.Cursor = Cursors.Hand;

        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, Foreground = GetBrush("Text"), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = detail, Foreground = GetBrush("Muted"), FontSize = 12, Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        var primary = MatteButton(primaryText, primaryAction);
        primary.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(primary, 1);
        grid.Children.Add(primary);

        if (secondaryText is not null && secondaryAction is not null)
        {
            var secondary = MatteButton(secondaryText, secondaryAction, new Thickness(8, 0, 0, 0));
            Grid.SetColumn(secondary, 2);
            grid.Children.Add(secondary);
        }

        root.Child = grid;
        return root;
    }

    private FrameworkElement BuildSitePermissionList(IEnumerable<SitePermissionRule> rules, Action<string> loadSite)
    {
        var stack = new StackPanel();
        var groupedRules = rules
            .Where(rule => !string.IsNullOrWhiteSpace(rule.Host))
            .GroupBy(rule => rule.Host, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (groupedRules.Count == 0)
        {
            stack.Children.Add(EmptyText("No sites have saved permission rules yet."));
            return stack;
        }

        foreach (var group in groupedRules)
        {
            var host = group.Key;
            stack.Children.Add(BuildEntry(
                host,
                SitePermissionSummary(group),
                "Open",
                (_, _) => loadSite(host)));
        }

        return stack;
    }

    private static string SitePermissionSummary(IEnumerable<SitePermissionRule> rules)
    {
        var allowed = rules
            .Where(rule => rule.State == "Allow")
            .Select(rule => PermissionLabel(rule.Permission))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(permission => permission, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var blocked = rules
            .Where(rule => rule.State == "Deny")
            .Select(rule => PermissionLabel(rule.Permission))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(permission => permission, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var parts = new List<string>();

        if (allowed.Count > 0)
        {
            parts.Add($"Allowed: {string.Join(", ", allowed)}");
        }

        if (blocked.Count > 0)
        {
            parts.Add($"Blocked: {string.Join(", ", blocked)}");
        }

        return parts.Count == 0 ? "All permissions ask first" : string.Join("  -  ", parts);
    }

    /// <summary>Reference .empty-state: dashed panel for empty or failed lists.</summary>
    private FrameworkElement EmptyText(string text)
    {
        var label = new TextBlock
        {
            Text = text,
            Foreground = GetBrush("Muted"),
            FontSize = 12,
            LineHeight = 12 * 1.6,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 16, 0)
        };
        var grid = new Grid { MinHeight = 72 };
        grid.Children.Add(new System.Windows.Shapes.Rectangle
        {
            Stroke = GetBrush("StrokeStrong"),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 2, 2 },
            RadiusX = 8,
            RadiusY = 8,
            Fill = Brushes.Transparent
        });
        grid.Children.Add(label);
        return grid;
    }
}
