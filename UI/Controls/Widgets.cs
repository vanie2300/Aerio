using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Web.WebView2.Core;

namespace TabloidBrowser;

public partial class MainWindow
{
    private Border SettingsSection(string title, string detail, params UIElement[] children)
    {
        var root = new Border
        {
            Background = GetBrush("ChromeBg"),
            BorderBrush = GetBrush("SubtleStroke"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(20),
            Margin = new Thickness(0, 0, 0, 10)
        };
        var stack = new StackPanel();

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        titleRow.Children.Add(new Border
        {
            Width = 3, Height = 18,
            Background = GetBrush("Cyan"),
            CornerRadius = new CornerRadius(1.5),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        titleRow.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = GetBrush("Text"),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold
        });
        stack.Children.Add(titleRow);

        if (!string.IsNullOrEmpty(detail))
            stack.Children.Add(new TextBlock
        {
            Text = detail,
            Foreground = GetBrush("Muted"),
            FontSize = 12,
            Margin = new Thickness(11, 3, 0, 16),
            TextWrapping = TextWrapping.Wrap
        });

        foreach (var child in children)
        {
            stack.Children.Add(child);
        }

        root.Child = stack;
        return root;
    }

    private FrameworkElement SettingRow(string label, string detail, FrameworkElement control)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        stack.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = GetBrush("Text"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });
        if (!string.IsNullOrEmpty(detail))
            stack.Children.Add(new TextBlock
        {
            Text = detail,
            Foreground = GetBrush("Muted"),
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(control);
        return stack;
    }

    private (FrameworkElement Control, Func<string> GetValue) ChoiceGroup(string[] values, string selected, Func<string, Brush>? swatchFactory = null)
    {
        if (values is null || values.Length == 0)
        {
            return (new UniformGrid { Margin = new Thickness(0) }, () => selected);
        }

        var selectedValue = values.Contains(selected) ? selected : values[0];
        var buttons = new List<Button>();
        var panel = new UniformGrid
        {
            Columns = values.Length,
            Margin = new Thickness(0)
        };

        void refresh()
        {
            foreach (var button in buttons)
            {
                var isSelected = string.Equals(button.Tag?.ToString(), selectedValue, StringComparison.Ordinal);
                button.Background = isSelected ? GetBrush("ButtonPressed") : GetBrush("ButtonBg");
                button.BorderBrush = isSelected ? GetBrush("Cyan") : GetBrush("Stroke");
                button.Opacity = isSelected ? 1 : 0.78;
            }
        }

        foreach (var value in values)
        {
            var button = new Button
            {
                Height = 34,
                Padding = new Thickness(12, 0, 12, 0),
                Margin = new Thickness(0, 0, 8, 0),
                Style = FindResource("MicroButton") as Style
            };
            button.Click += (_, _) =>
            {
                selectedValue = value;
                refresh();
            };
            button.Tag = value;

            if (swatchFactory is not null)
            {
                button.Content = ChoiceWithSwatch(value, swatchFactory(value));
            }
            else
            {
                button.Content = value;
            }

            buttons.Add(button);
            panel.Children.Add(button);
        }

        if (panel.Children.Count > 0 && panel.Children[^1] is FrameworkElement last)
        {
            last.Margin = new Thickness(0);
        }

        refresh();
        return (panel, () => selectedValue);
    }

    private StackPanel ChoiceWithSwatch(string text, Brush brush)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        panel.Children.Add(new Border
        {
            Width = 10,
            Height = 10,
            CornerRadius = new CornerRadius(5),
            Background = brush,
            BorderBrush = GetBrush("SubtleStroke"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 7, 0)
        });
        panel.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = GetBrush("Text"),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        return panel;
    }

    private Brush AccentSwatch(string accent)
    {
        return new SolidColorBrush(accent switch
        {
            "Blue" => Color.FromRgb(0, 122, 204),
            "Purple" => Color.FromRgb(114, 77, 223),
            "Gray" => Color.FromRgb(128, 128, 128),
            "Green" => Color.FromRgb(106, 153, 85),
            "White" => Colors.White,
            _ => Color.FromRgb(226, 231, 240)
        });
    }

    private (FrameworkElement Control, Func<string> GetValue) NumericStepper(int value, int min, int max)
    {
        var text = Input(Math.Clamp(value, min, max).ToString());
        text.HorizontalContentAlignment = HorizontalAlignment.Center;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        int current = value;
        void setValue(int next)
        {
            current = Math.Clamp(next, min, max);
            text.Text = current.ToString();
        }

        var decrement = MatteButton("-", (_, _) => setValue(ClampInt(text.Text, min, max, current) - 1));
        decrement.MinWidth = 34;
        decrement.Width = 34;
        decrement.Height = 32;
        decrement.Margin = new Thickness(0, 0, 6, 0);

        var increment = MatteButton("+", (_, _) => setValue(ClampInt(text.Text, min, max, current) + 1));
        increment.MinWidth = 34;
        increment.Width = 34;
        increment.Height = 32;
        increment.Margin = new Thickness(6, 0, 0, 0);

        text.LostFocus += (_, _) => setValue(ClampInt(text.Text, min, max, current));
        text.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);

        Grid.SetColumn(text, 1);
        Grid.SetColumn(increment, 2);
        grid.Children.Add(decrement);
        grid.Children.Add(text);
        grid.Children.Add(increment);

        return (grid, () => text.Text);
    }

    private ToggleButton ToggleRow(string title, string detail, bool isChecked)
    {
        var toggle = new ToggleButton
        {
            IsChecked = isChecked,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 8),
            MinHeight = 58
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
        text.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = GetBrush("Text"),
            FontSize = 13,
            FontWeight = FontWeights.Bold
        });
        text.Children.Add(new TextBlock
        {
            Text = detail,
            Foreground = GetBrush("Muted"),
            FontSize = 12,
            Margin = new Thickness(0, 3, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });

        var thumb = new Border
        {
            Width = 16, Height = 16,
            CornerRadius = new CornerRadius(8),
            Background = GetBrush("Text"),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0)
        };
        var pillGrid = new Grid();
        pillGrid.Children.Add(thumb);
        var statePill = new Border
        {
            Width = 46,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = pillGrid
        };

        Grid.SetColumn(statePill, 1);
        grid.Children.Add(text);
        grid.Children.Add(statePill);
        toggle.Content = grid;

        void update()
        {
            var on = toggle.IsChecked == true;
            statePill.Background = on ? GetBrush("Cyan") : GetBrush("ButtonBg");
            statePill.BorderBrush = on ? GetBrush("Cyan") : GetBrush("Stroke");
            thumb.Background = on ? GetBrush("AppBg") : GetBrush("Text");
            var anim = new ThicknessAnimation(
                on ? new Thickness(27, 0, 0, 0) : new Thickness(2, 0, 0, 0),
                TimeSpan.FromSeconds(0.12))
            {
                EasingFunction = new CircleEase { EasingMode = EasingMode.EaseOut }
            };
            thumb.BeginAnimation(Border.MarginProperty, anim);
        }

        toggle.Checked += (_, _) => update();
        toggle.Unchecked += (_, _) => update();
        update();

        return toggle;
    }

    private Button MatteButton(string text, RoutedEventHandler action, Thickness? margin = null)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 92,
            Height = 30,
            Padding = new Thickness(12, 0, 12, 0),
            Margin = margin ?? new Thickness(0),
            Style = FindResource("MicroButton") as Style
        };

        button.Click += action;
        return button;
    }

    private ComboBox Combo(string[] values, string selected)
    {
        var safeValues = values ?? [];
        var combo = new ComboBox
        {
            ItemsSource = safeValues,
            SelectedItem = safeValues.Contains(selected) ? selected : (safeValues.Length > 0 ? safeValues[0] : ""),
            Height = 32,
            Background = GetBrush("InputBg"),
            Foreground = GetBrush("Text"),
            BorderBrush = GetBrush("Stroke")
        };

        return combo;
    }

    private ComboBox ComboWithPlaceholder(IEnumerable<string> values, string selected, string placeholder)
    {
        var items = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (items.Count == 0)
        {
            items.Add(placeholder);
        }

        var selectedItem = items.FirstOrDefault(item => string.Equals(item, selected, StringComparison.OrdinalIgnoreCase)) ?? items[0];
        var combo = new ComboBox
        {
            ItemsSource = items,
            SelectedItem = selectedItem,
            Height = 32,
            Background = GetBrush("InputBg"),
            Foreground = GetBrush("Text"),
            BorderBrush = GetBrush("Stroke")
        };

        return combo;
    }

    private ComboBox EditableCombo(IEnumerable<string> values, string selected)
    {
        var items = (values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var combo = new ComboBox
        {
            ItemsSource = items,
            IsEditable = true,
            Text = selected,
            Height = 32,
            Background = GetBrush("InputBg"),
            Foreground = GetBrush("Text"),
            BorderBrush = GetBrush("Stroke")
        };

        if (items.Contains(selected, StringComparer.OrdinalIgnoreCase))
        {
            combo.SelectedItem = items.First(item => string.Equals(item, selected, StringComparison.OrdinalIgnoreCase));
        }

        return combo;
    }

    private static string GetComboText(ComboBox combo)
    {
        return combo.Text?.Trim() ?? combo.SelectedItem?.ToString()?.Trim() ?? string.Empty;
    }

    private TextBox Input(string value)
    {
        return new TextBox
        {
            Text = value,
            Height = 32,
            Background = GetBrush("InputBg"),
            Foreground = GetBrush("Text"),
            BorderBrush = GetBrush("Stroke"),
            Padding = new Thickness(8, 5, 8, 0)
        };
    }

    private TextBox Multiline(string value)
    {
        return new TextBox
        {
            Text = value,
            MinHeight = 88,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = GetBrush("InputBg"),
            Foreground = GetBrush("Text"),
            BorderBrush = GetBrush("Stroke"),
            Padding = new Thickness(8)
        };
    }

    private CheckBox Check(string text, bool isChecked)
    {
        return new CheckBox
        {
            Content = text,
            IsChecked = isChecked,
            Foreground = GetBrush("Text"),
            Margin = new Thickness(0, 0, 0, 12)
        };
    }

    private (FrameworkElement Control, TextBox Input, Func<string> GetValue) TextRow(string label, string detail, string value)
    {
        var box = new TextBox
        {
            Text = value,
            Height = 32,
            Background = GetBrush("InputBg"),
            Foreground = GetBrush("Text"),
            BorderBrush = GetBrush("Stroke"),
            Padding = new Thickness(8, 5, 8, 0)
        };
        return (SettingRow(label, detail, box), box, () => box.Text?.Trim() ?? "");
    }

    private (FrameworkElement Control, PasswordBox Input, Func<string> GetValue) PasswordRow(string label, string detail, string value)
    {
        var box = new PasswordBox
        {
            Height = 32,
            Background = GetBrush("InputBg"),
            Foreground = GetBrush("Text"),
            BorderBrush = GetBrush("Stroke"),
            Padding = new Thickness(8, 5, 8, 0)
        };
        box.Password = value;
        return (SettingRow(label, detail, box), box, () => box.Password);
    }

    private Slider StyleSlider(Slider slider)
    {
        slider.Foreground = GetBrush("Cyan");
        slider.Resources.Add(SystemColors.ControlBrushKey, GetBrush("ButtonBg"));
        slider.Resources.Add(SystemColors.ControlLightBrushKey, GetBrush("ButtonPressed"));
        slider.Resources.Add(SystemColors.ControlLightLightBrushKey, GetBrush("ButtonHover"));
        slider.Resources.Add(SystemColors.HighlightBrushKey, GetBrush("Cyan"));
        slider.Resources.Add(SystemColors.WindowBrushKey, GetBrush("InputBg"));
        return slider;
    }

    private Border ValueChip(TextBlock text)
    {
        return new Border
        {
            Background = GetBrush("ButtonBg"),
            BorderBrush = GetBrush("SubtleStroke"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 3, 8, 3),
            MinWidth = 36,
            HorizontalAlignment = HorizontalAlignment.Right,
            Child = text
        };
    }

    private (FrameworkElement Control, Slider Input, Func<int> GetValue) SliderRow(string label, string detail, int value, int min, int max)
    {
        var currentText = new TextBlock
        {
            Text = value.ToString(),
            Foreground = GetBrush("Text"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var slider = StyleSlider(new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = value,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            Height = 28,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        });

        slider.ValueChanged += (_, e) => currentText.Text = ((int)e.NewValue).ToString();

        var chip = ValueChip(currentText);
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(slider, 0);
        Grid.SetColumn(chip, 1);
        row.Children.Add(slider);
        row.Children.Add(chip);

        return (SettingRow(label, detail, row), slider, () => (int)slider.Value);
    }

    private (FrameworkElement Control, Slider Input, Func<double> GetValue) SliderRow(string label, string detail, double value, double min, double max)
    {
        var currentText = new TextBlock
        {
            Text = value.ToString("F1"),
            Foreground = GetBrush("Text"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var slider = StyleSlider(new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = value,
            TickFrequency = 0.1,
            IsSnapToTickEnabled = true,
            SmallChange = 0.1,
            LargeChange = 0.5,
            Height = 28,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        });

        slider.ValueChanged += (_, e) => currentText.Text = e.NewValue.ToString("F1");

        var chip = ValueChip(currentText);
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(slider, 0);
        Grid.SetColumn(chip, 1);
        row.Children.Add(slider);
        row.Children.Add(chip);

        return (SettingRow(label, detail, row), slider, () => slider.Value);
    }

    private static string CarouselHtml()
    {
        return """
<!DOCTYPE html>
<html><head><meta charset='utf-8'><style>
/* ─── design tokens ─────────────────────────────────────────── */
:root{
  --card-w:212px; --card-h:146px; --card-radius:16px;
  --hero-scale:1.16; --side-scale:.7;
  --side-offset:160px; --hidden-offset:320px;
  --stage-pad:38px;
  --stage-h:calc(var(--card-h)*var(--hero-scale) + var(--stage-pad)*2);
  --ease:cubic-bezier(.32,.72,.24,1);
  --dur-slow:540ms; --dur-fast:200ms;
  --bg-card:#202226;
  --line:rgba(255,255,255,.09);
  --line-strong:rgba(255,255,255,.18);
  --text:#f2f2f2; --text-dim:#a9afb8; --muted:#8b9099;
  --ok:#34d399;
}
/* ─── base ──────────────────────────────────────────────────── */
*{margin:0;padding:0;box-sizing:border-box}
html,body{height:100%}
body{background:transparent;font-family:system-ui,-apple-system,'Segoe UI',sans-serif;overflow:hidden;user-select:none;color:var(--text)}
.carousel{width:100%;height:100%;display:flex;flex-direction:column;justify-content:center;overflow:hidden}
/* ─── card band: fixed-height area reserved for the cards ───── */
.stage{position:relative;flex:0 0 var(--stage-h);width:100%;overflow:hidden}
.cards{position:absolute;inset:0}
/* ─── cards ─────────────────────────────────────────────────── */
.card{position:absolute;left:50%;top:50%;
  width:var(--card-w);height:var(--card-h);
  border-radius:var(--card-radius);
  background:var(--bg-card);border:1px solid var(--line);
  display:flex;align-items:center;justify-content:center;overflow:hidden;
  transition:transform var(--dur-slow) var(--ease),opacity var(--dur-fast) ease,
             border-color var(--dur-fast) ease,box-shadow var(--dur-slow) var(--ease);
  will-change:transform,opacity}
.card.no-trans{transition:none!important}
.card.hero{z-index:3;opacity:1;border-color:var(--line-strong);
  transform:translate(-50%,-50%) scale(var(--hero-scale));
  box-shadow:0 10px 26px rgba(0,0,0,.42)}
.card.prev,.card.next{z-index:1;opacity:.5;
  box-shadow:0 8px 20px rgba(0,0,0,.3)}
.card.prev{transform:translate(-50%,-50%) translateX(calc(-1*var(--side-offset))) scale(var(--side-scale))}
.card.next{transform:translate(-50%,-50%) translateX(var(--side-offset)) scale(var(--side-scale))}
.card.hidden{z-index:0;opacity:0;pointer-events:none;box-shadow:none}
.card.hidden.from-left{transform:translate(-50%,-50%) translateX(calc(-1*var(--hidden-offset))) scale(var(--side-scale))}
.card.hidden.from-right{transform:translate(-50%,-50%) translateX(var(--hidden-offset)) scale(var(--side-scale))}
.card.selectable{cursor:pointer}
.card.selectable:hover{opacity:.72;border-color:var(--line-strong)}
/* ─── card content ──────────────────────────────────────────── */
.card-inner{position:relative;width:100%;padding:14px 16px 12px;text-align:center}
.logo-badge{position:relative;width:52px;height:52px;border-radius:50%;margin:0 auto 7px;
  display:flex;align-items:center;justify-content:center;overflow:hidden;
  box-shadow:0 4px 14px rgba(0,0,0,.35)}
.logo-badge img{position:absolute;inset:0;width:100%;height:100%;object-fit:cover;border-radius:50%}
.logo-letter{font-size:20px;font-weight:700;color:#fff}
.name{font-size:13.5px;font-weight:600;color:var(--text);margin-bottom:5px;
  white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.accent{display:block;width:38px;height:2.5px;border-radius:2px;margin:0 auto 5px}
.desc{font-size:10.5px;line-height:1.35;color:var(--muted);
  display:-webkit-box;-webkit-line-clamp:2;-webkit-box-orient:vertical;overflow:hidden}
.card:not(.hero) .desc,.card:not(.hero) .accent{display:none}
/* ─── hero status chip ───────────────────────────────────────── */
.status{display:none}
.card.hero .status{display:flex}
.status{position:absolute;top:12px;left:12px;align-items:center;gap:5px;
  font-size:10px;font-weight:700;letter-spacing:.5px;text-transform:uppercase;
  color:#101010;background:var(--ok);padding:3px 9px;border-radius:999px}
.status::before{content:'';width:5px;height:5px;border-radius:50%;background:currentColor}
/* ─── lock toggle: floating pill under the selected card ─────── */
.lock-toggle{position:absolute;left:50%;z-index:5;
  bottom:calc((var(--stage-pad) - 26px)/2);
  transform:translateX(-50%);
  display:flex;align-items:center;justify-content:center;gap:6px;
  height:26px;padding:0 12px;border-radius:999px;white-space:nowrap;
  font-size:11px;font-weight:600;letter-spacing:.3px;
  background:rgba(255,255,255,.14);border:1px solid rgba(255,255,255,.26);
  color:var(--text);cursor:pointer;
  box-shadow:0 4px 12px rgba(0,0,0,.4);
  transition:background var(--dur-fast) ease,border-color var(--dur-fast) ease,
             color var(--dur-fast) ease}
.lock-toggle:hover{background:rgba(255,255,255,.26);border-color:rgba(255,255,255,.42)}
.lock-toggle.locked{background:var(--ok);border-color:var(--ok);color:#0d0d0d}
.lock-toggle.locked:hover{background:#2ec393;border-color:#2ec393}
.lock-toggle svg{flex:0 0 auto}
/* ─── navigation ────────────────────────────────────────────── */
.nav{display:flex;align-items:center;justify-content:center;gap:14px;
  flex:0 0 auto;padding:10px 0 4px}
.nav-btn{width:36px;height:36px;border-radius:10px;padding:0;
  background:rgba(255,255,255,.14);border:1px solid rgba(255,255,255,.24);
  color:#fff;cursor:pointer;
  display:flex;align-items:center;justify-content:center;
  transition:background var(--dur-fast) ease,border-color var(--dur-fast) ease}
.nav-btn:hover:not(:disabled){background:rgba(255,255,255,.26);border-color:rgba(255,255,255,.42)}
.nav-btn:disabled{opacity:.35;cursor:default}
.dots{display:flex;align-items:center;gap:6px}
.dot{width:6px;height:6px;border-radius:999px;padding:0;
  background:rgba(255,255,255,.3);border:none;cursor:pointer;
  transition:width var(--dur-slow) var(--ease),background var(--dur-fast) ease}
.dot.active{width:18px;background:var(--ok)}
.dot:disabled{cursor:default}
/* ─── a11y + motion ─────────────────────────────────────────── */
.visually-hidden{position:absolute;width:1px;height:1px;overflow:hidden;clip:rect(0 0 0 0);white-space:nowrap}
@media (prefers-reduced-motion:reduce){*{transition:none!important}}
</style></head><body>
<div class='carousel'>
  <div class='stage'>
    <div class='cards' id='cards' role='listbox' aria-label='Search engine selection'></div>
    <button type='button' class='lock-toggle' id='lockToggle' onclick='toggleLock()' aria-pressed='false' aria-label='Lock engine selection'></button>
  </div>
  <nav class='nav' aria-label='Search engine navigation'>
    <button type='button' class='nav-btn' id='prevBtn' onclick='nav(-1)' aria-label='Previous engine'><svg viewBox='0 0 24 24' width='16' height='16' fill='none' stroke='currentColor' stroke-width='2.2' stroke-linecap='round' stroke-linejoin='round'><path d='M15 18l-6-6 6-6'/></svg></button>
    <div class='dots' id='dots' role='tablist' aria-label='Engine pages'></div>
    <button type='button' class='nav-btn' id='nextBtn' onclick='nav(1)' aria-label='Next engine'><svg viewBox='0 0 24 24' width='16' height='16' fill='none' stroke='currentColor' stroke-width='2.2' stroke-linecap='round' stroke-linejoin='round'><path d='M9 6l6 6-6 6'/></svg></button>
  </nav>
  <span class='visually-hidden' id='announcer' aria-live='polite'></span>
</div>
<script>
let data=null,idx=0,locked=false;
const $=id=>document.getElementById(id);
const wrap=(a,n)=>(a+n)%n;
const posOf=(i,n)=> i===idx?'hero' : i===wrap(idx-1,n)?'prev' : i===wrap(idx+1,n)?'next' : 'hidden';
const fromLeft=(i,n)=> wrap(idx-i,n) < wrap(i-idx,n);
const ICON_LOCK='<svg viewBox="0 0 16 16" width="12" height="12" fill="currentColor" aria-hidden="true"><path d="M8 1.5A3.5 3.5 0 0 0 4.5 5v1H4a1.5 1.5 0 0 0-1.5 1.5V13A1.5 1.5 0 0 0 4 14.5h8a1.5 1.5 0 0 0 1.5-1.5V7.5A1.5 1.5 0 0 0 12 6h-.5V5A3.5 3.5 0 0 0 8 1.5Zm0 2A1.5 1.5 0 0 1 9.5 5v1h-3V5A1.5 1.5 0 0 1 8 3.5Z"/></svg>';
const ICON_UNLOCK='<svg viewBox="0 0 16 16" width="12" height="12" fill="currentColor" aria-hidden="true"><path d="M8 1.5A3.5 3.5 0 0 0 4.5 5v1h2V5a1.5 1.5 0 0 1 3 0 1 1 0 0 0 2 0A3.5 3.5 0 0 0 8 1.5Zm-4 5h8a1.5 1.5 0 0 1 1.5 1.5V13A1.5 1.5 0 0 1 12 14.5H4A1.5 1.5 0 0 1 2.5 13V8A1.5 1.5 0 0 1 4 6.5Z"/></svg>';

window.chrome.webview.addEventListener('message',e=>{
  const m=JSON.parse(e.data);
  if(m.type==='init'){data=m;idx=m.currentIndex;locked=!!m.locked;build();renderLock();updateNav();announce();}
  if(m.type==='update'){data=m;idx=m.currentIndex;locked=!!m.locked;build();renderLock();updateNav();announce();}
});

function build(){
  const cards=$('cards');
  cards.innerHTML='';
  for(let i=0;i<data.engines.length;i++){
    const card=document.createElement('article');
    card.className='card';
    card.dataset.i=i;card.dataset.pos='init';
    card.setAttribute('role','option');
    card.setAttribute('aria-label',data.engines[i].name);
    card.addEventListener('click',()=>goTo(i));
    card.innerHTML=cardHtml(data.engines[i]);
    cards.appendChild(card);
  }
  paint();renderDots();
}
function cardHtml(e){
  const clr=e.color||'#666';
  const badge=e.icon
    ? '<div class="logo-badge" style="background:'+clr+'"><span class="logo-letter">'+esc(e.letter)+'</span><img src="https://assets.local/'+e.icon+'" alt="" onerror="this.remove()"></div>'
    : '<div class="logo-badge" style="background:'+clr+'"><span class="logo-letter">'+esc(e.letter)+'</span></div>';
  return '<div class="card-inner">'
    +'<span class="status">Active</span>'
    +badge
    +'<div class="name">'+esc(e.name)+'</div>'
    +'<span class="accent" style="background:'+clr+'"></span>'
    +'<p class="desc">'+esc(e.desc||'')+'</p>'
    +'</div>';
}
function esc(s){return String(s).replace(/&/g,'\x26amp;').replace(/</g,'\x26lt;').replace(/>/g,'\x26gt;').replace(/"/g,'\x26quot;').replace(/'/g,'\x26#39;')}
function goTo(i){if(locked||i===idx)return;idx=i;select();}
function nav(dir){if(locked)return;idx=wrap(idx+dir,data.engines.length);select();}
function select(){paint();renderDots();announce();send('select',idx);}
function toggleLock(){
  locked=!locked;
  renderLock();
  updateNav();paint();
  sendLock(locked,idx);
}
function renderLock(){
  const t=$('lockToggle');
  const stateLabel=locked?'Locked':'Lock';
  const actionLabel=locked?'Unlock engine selection':'Lock engine selection';
  t.innerHTML=(locked?ICON_LOCK:ICON_UNLOCK)+'<span>'+stateLabel+'</span>';
  t.classList.toggle('locked',locked);
  t.title=actionLabel;
  t.setAttribute('aria-label',actionLabel);
  t.setAttribute('aria-pressed',locked?'true':'false');
}
function paint(){
  const n=data.engines.length;
  document.querySelectorAll('.card').forEach(card=>{
    const i=+card.dataset.i;
    const next=posOf(i,n);
    const prev=card.dataset.pos;
    card.dataset.pos=next;
    card.className='card '+next+(next==='hidden'?(fromLeft(i,n)?' from-left':' from-right'):'');
    card.classList.toggle('no-trans',prev==='hidden'&&next==='hidden');
    card.classList.toggle('selectable',!locked&&next!=='hero');
    card.setAttribute('aria-selected',next==='hero'?'true':'false');
  });
}
function renderDots(){
  const dots=$('dots');
  dots.innerHTML='';
  for(let i=0;i<data.engines.length;i++){
    const d=document.createElement('button');
    d.type='button';
    d.className='dot'+(i===idx?' active':'');
    d.setAttribute('aria-label','Go to '+data.engines[i].name);
    if(i===idx)d.setAttribute('aria-current','true');
    d.addEventListener('click',()=>goTo(i));
    dots.appendChild(d);
  }
  updateNav();
}
function updateNav(){
  $('prevBtn').disabled=locked;
  $('nextBtn').disabled=locked;
  document.querySelectorAll('.dot').forEach(d=>d.disabled=locked);
}
function announce(){$('announcer').textContent='Selected engine: '+data.engines[idx].name;}
function send(type,index){const eng=data.engines[index];window.chrome.webview.postMessage(JSON.stringify({type:type,index:index,engine:eng.name}));}
function sendLock(lockedState,index){const eng=data.engines[index];window.chrome.webview.postMessage(JSON.stringify({type:'lock',index:index,engine:eng.name,locked:lockedState}));}
window.chrome.webview.postMessage(JSON.stringify({type:'ready'}));
</script></body></html>
""";
    }

    private (FrameworkElement Control, Func<string> GetEngine, Func<List<SearchEngineDef>> GetCustomEngines)
        SearchEngineCarousel(string selected, List<SearchEngineDef> customEngines, Action onChange)
    {
        var builtInNames = new[] { "DuckDuckGo", "Startpage", "Brave", "SearXNG" };
        var builtInLetters = new[] { "D", "S", "B", "X" };
        var builtInIcons = new[] { "ddg.png", "startpage.png", "brave.png", "searxng.png" };
        var builtInColors = new Color[]
        {
            Color.FromRgb(222, 88, 51),
            Color.FromRgb(74, 144, 217),
            Color.FromRgb(251, 84, 43),
            Color.FromRgb(107, 107, 107)
        };
        var builtInDescs = new[]
        {
            "Private search engine that doesn't track your searches.",
            "Google search results delivered with complete privacy.",
            "Independent search engine built on its own index.",
            "Self-hostable metasearch engine aggregator."
        };

        var customBrush = new SolidColorBrush(Color.FromRgb(114, 77, 223));
        List<SearchEngineDef> currentCustoms = [.. customEngines];

        var engines = new List<(string Name, string Letter, Brush Color, string Description, string? IconFile)>();
        void RebuildEngineList()
        {
            engines.Clear();
            for (int i = 0; i < builtInNames.Length; i++)
                engines.Add((builtInNames[i], builtInLetters[i], new SolidColorBrush(builtInColors[i]), builtInDescs[i], builtInIcons[i]));
            foreach (var c in currentCustoms)
            {
                var letter = c.Name.Length > 0 ? c.Name[..1].ToUpperInvariant() : "?";
                engines.Add((c.Name, letter, customBrush, c.UrlTemplate, null));
            }
        }
        RebuildEngineList();

        int currentIndex = engines.FindIndex(e => e.Name == selected);
        if (currentIndex < 0) currentIndex = 0;
        bool isCustomSelected = currentIndex >= builtInNames.Length;
        bool isLocked = false;
        bool addingEngine = false;
        bool webReady = false;

        Button removeBtn = null!, addBtn = null!;
        Grid root = null!;

        // WebView2 carousel
        Microsoft.Web.WebView2.Wpf.WebView2 wv = null!;
        if (_carouselWebView is not null)
        {
            _carouselWebView.WebMessageReceived -= OnWebMessageReceived;
            _carouselWebView.Dispose();
        }
        wv = new Microsoft.Web.WebView2.Wpf.WebView2
        {
            Width = 470,
            Height = 316,
            DefaultBackgroundColor = System.Drawing.Color.Transparent,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        _carouselWebView = wv;

        async Task InitWebViewAsync()
        {
            try
            {
                await wv.EnsureCoreWebView2Async(null);
                var assetsPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets");
                wv.CoreWebView2.SetVirtualHostNameToFolderMapping("assets.local", assetsPath,
                    Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.DenyCors);
                wv.CoreWebView2.NavigateToString(CarouselHtml());
            }
            catch (Exception ex) { Log.Error(ex, "Carousel WebView2 init failed"); _ = Dispatcher.BeginInvoke(() => { if (!Dispatcher.HasShutdownStarted) StatusBar.StatusText.Text = "Carousel failed to load"; }); }
        }

        void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var msg = e.TryGetWebMessageAsString();
                if (string.IsNullOrEmpty(msg)) return;
                var json = System.Text.Json.Nodes.JsonNode.Parse(msg);
                var type = json?["type"]?.ToString();

                if (type == "ready")
                {
                    webReady = true;
                    PushState();
                }
                else if (type == "select")
                {
                    var idx = (int?)json?["index"] ?? currentIndex;
                    currentIndex = idx;
                    isCustomSelected = currentIndex >= builtInNames.Length;
                    if (removeBtn is not null)
                        removeBtn.Visibility = isCustomSelected ? Visibility.Visible : Visibility.Collapsed;
                    onChange();
                }
                else if (type == "lock")
                {
                    var lockIdx = (int?)json?["index"] ?? currentIndex;
                    bool lockedVal = json?["locked"]?.GetValue<bool>() ?? true;
                    isLocked = lockedVal;
                    currentIndex = lockIdx;
                    isCustomSelected = currentIndex >= builtInNames.Length;
                    if (removeBtn is not null)
                        removeBtn.Visibility = isCustomSelected ? Visibility.Visible : Visibility.Collapsed;
                    onChange();
                }
            }
            catch (Exception ex) { Log.Error(ex, "WebMessageReceived error"); }
        }
        wv.WebMessageReceived += OnWebMessageReceived;

        void PushState()
        {
            if (!webReady || wv.CoreWebView2 == null) return;
            var ci = Math.Clamp(currentIndex, 0, engines.Count - 1);
            var msgObj = new
            {
                type = "init",
                engines = engines.Select(e => new
                {
                    name = e.Name,
                    letter = e.Letter,
                    color = e.Color is SolidColorBrush scb ? $"#{scb.Color.R:x2}{scb.Color.G:x2}{scb.Color.B:x2}" : "#666",
                    desc = e.Description,
                    icon = e.IconFile ?? ""
                }).ToList(),
                currentIndex = ci,
                selected = engines[ci].Name,
                locked = isLocked
            };
            wv.CoreWebView2.PostWebMessageAsString(JsonSerializer.Serialize(msgObj));
        }

        void SendRefresh()
        {
            if (!webReady || wv.CoreWebView2 == null) return;
            var ci = Math.Clamp(currentIndex, 0, engines.Count - 1);
            var msgObj = new
            {
                type = "update",
                engines = engines.Select(e => new
                {
                    name = e.Name,
                    letter = e.Letter,
                    color = e.Color is SolidColorBrush scb ? $"#{scb.Color.R:x2}{scb.Color.G:x2}{scb.Color.B:x2}" : "#666",
                    desc = e.Description,
                    icon = e.IconFile ?? ""
                }).ToList(),
                currentIndex = ci,
                selected = engines[ci].Name,
                locked = isLocked
            };
            wv.CoreWebView2.PostWebMessageAsString(JsonSerializer.Serialize(msgObj));
        }

        _ = InitWebViewAsync();

        // ── Add / Remove buttons ──
        addBtn = MatteButton("+ Add engine", (_, _) => ShowAddForm());
        removeBtn = MatteButton("Remove", (_, _) =>
        {
            if (!isCustomSelected || currentCustoms.Count == 0) return;
            var engineName = engines[currentIndex].Name;
            var idx = currentCustoms.FindIndex(c => c.Name == engineName);
            if (idx < 0) return;
            currentCustoms.RemoveAt(idx);
            RebuildEngineList();
            if (currentIndex >= engines.Count) currentIndex = engines.Count - 1;
            isCustomSelected = currentIndex >= builtInNames.Length;
            removeBtn.Visibility = isCustomSelected ? Visibility.Visible : Visibility.Collapsed;
            SendRefresh();
            onChange();
        });
        removeBtn.Visibility = isCustomSelected ? Visibility.Visible : Visibility.Collapsed;

        // ── Add engine form ──
        void ShowAddForm()
        {
            if (addingEngine) return;
            addingEngine = true;
            addBtn.IsEnabled = false;

            var nameBox = new TextBox
            {
                Height = 30, Background = GetBrush("InputBg"),
                Foreground = GetBrush("Text"), BorderBrush = GetBrush("Stroke"),
                Padding = new Thickness(8, 5, 8, 5)
            };
            var urlBox = new TextBox
            {
                Height = 30, Background = GetBrush("InputBg"),
                Foreground = GetBrush("Text"), BorderBrush = GetBrush("Stroke"),
                Padding = new Thickness(8, 5, 8, 5)
            };

            var formGrid = new Grid();
            for (int i = 0; i < 6; i++)
                formGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            formGrid.Margin = new Thickness(4);

            var nameLabel = new TextBlock { Text = "Name", Foreground = GetBrush("Text"), FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 3) };
            var urlLabel = new TextBlock { Text = "URL template", Foreground = GetBrush("Text"), FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 3) };
            var urlHint = new TextBlock { Text = "Use {q} for the query placeholder", Foreground = GetBrush("Muted"), FontSize = 11, Margin = new Thickness(0, 0, 0, 2) };

            var saveBtn = MatteButton("Save", (_, _) =>
            {
                var n = nameBox.Text?.Trim();
                var u = urlBox.Text?.Trim();
                if (string.IsNullOrEmpty(n) || string.IsNullOrEmpty(u)) return;
                currentCustoms.Add(new SearchEngineDef(n, u));
                RebuildEngineList();
                int newIdx = engines.FindIndex(e => e.Name == n);
                if (newIdx >= 0)
                {
                    currentIndex = newIdx;
                    isCustomSelected = currentIndex >= builtInNames.Length;
                }
                addBtn.IsEnabled = true;
                addingEngine = false;
                removeBtn.Visibility = isCustomSelected ? Visibility.Visible : Visibility.Collapsed;
                SendRefresh();
                onChange();
                // restore webview
                root.Children.Remove(formGrid);
                wv.Visibility = Visibility.Visible;
            }, new Thickness(0, 0, 6, 0));

            var cancelBtn = MatteButton("Cancel", (_, _) =>
            {
                addingEngine = false;
                addBtn.IsEnabled = true;
                root.Children.Remove(formGrid);
                wv.Visibility = Visibility.Visible;
            });

            var formBtnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            formBtnRow.Children.Add(saveBtn);
            formBtnRow.Children.Add(cancelBtn);

            Grid.SetRow(nameLabel, 0); Grid.SetRow(nameBox, 1);
            Grid.SetRow(urlLabel, 2); Grid.SetRow(urlHint, 3);
            Grid.SetRow(urlBox, 4); Grid.SetRow(formBtnRow, 5);
            formGrid.Children.Add(nameLabel); formGrid.Children.Add(nameBox);
            formGrid.Children.Add(urlLabel); formGrid.Children.Add(urlHint);
            formGrid.Children.Add(urlBox); formGrid.Children.Add(formBtnRow);

            wv.Visibility = Visibility.Collapsed;
            root.Children.Add(formGrid);
            nameBox.Focus();
        }

        // ── Assemble ──
        root = new Grid { Focusable = true };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 16, 0, 0)
        };
        btnRow.Children.Add(addBtn);
        btnRow.Children.Add(removeBtn);

        Grid.SetRow(wv, 0);
        Grid.SetRow(btnRow, 1);
        root.Children.Add(wv);
        root.Children.Add(btnRow);

        root.PreviewKeyDown += async (_, e) =>
        {
            if (!webReady || wv.CoreWebView2 == null) return;
            try
            {
                if (e.Key == Key.Left) await wv.CoreWebView2.ExecuteScriptAsync("nav(-1)");
                else if (e.Key == Key.Right) await wv.CoreWebView2.ExecuteScriptAsync("nav(1)");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error occurred");
            }
        };

        return (root,
            () => engines[currentIndex].Name,
            () => [.. currentCustoms]);
    }
}