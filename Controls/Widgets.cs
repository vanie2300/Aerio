using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
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
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 12)
        };

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = GetBrush("Text"),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold
        });
        if (!string.IsNullOrEmpty(detail))
            stack.Children.Add(new TextBlock
        {
            Text = detail,
            Foreground = GetBrush("Muted"),
            FontSize = 12,
            Margin = new Thickness(0, 3, 0, 14),
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
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        stack.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = GetBrush("Text"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 3)
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

        void setValue(int next)
        {
            text.Text = Math.Clamp(next, min, max).ToString();
        }

        var decrement = MatteButton("-", (_, _) => setValue(ClampInt(text.Text, min, max, value) - 1));
        decrement.MinWidth = 34;
        decrement.Width = 34;
        decrement.Height = 32;
        decrement.Margin = new Thickness(0, 0, 6, 0);

        var increment = MatteButton("+", (_, _) => setValue(ClampInt(text.Text, min, max, value) + 1));
        increment.MinWidth = 34;
        increment.Width = 34;
        increment.Height = 32;
        increment.Margin = new Thickness(6, 0, 0, 0);

        text.LostFocus += (_, _) => setValue(ClampInt(text.Text, min, max, value));
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
            FontWeight = FontWeights.SemiBold
        });
        text.Children.Add(new TextBlock
        {
            Text = detail,
            Foreground = GetBrush("Muted"),
            FontSize = 12,
            Margin = new Thickness(0, 3, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });

        var stateText = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var statePill = new Border
        {
            Width = 46,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = stateText
        };

        Grid.SetColumn(statePill, 1);
        grid.Children.Add(text);
        grid.Children.Add(statePill);
        toggle.Content = grid;

        void update()
        {
            var on = toggle.IsChecked == true;
            stateText.Text = on ? "On" : "Off";
            stateText.Foreground = on ? GetBrush("AppBg") : GetBrush("Muted");
            statePill.Background = on ? GetBrush("Cyan") : GetBrush("ButtonBg");
            statePill.BorderBrush = on ? GetBrush("Cyan") : GetBrush("Stroke");
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

    private (FrameworkElement Control, Slider Input, Func<int> GetValue) SliderRow(string label, string detail, int value, int min, int max)
    {
        var currentText = new TextBlock
        {
            Text = value.ToString(),
            Foreground = GetBrush("Text"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            MinWidth = 36,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };

        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = value,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            Height = 28,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        slider.ValueChanged += (_, e) => currentText.Text = ((int)e.NewValue).ToString();

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(slider, 0);
        Grid.SetColumn(currentText, 1);
        row.Children.Add(slider);
        row.Children.Add(currentText);

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
            MinWidth = 36,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };

        var slider = new Slider
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
        };

        slider.ValueChanged += (_, e) => currentText.Text = e.NewValue.ToString("F1");

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(slider, 0);
        Grid.SetColumn(currentText, 1);
        row.Children.Add(slider);
        row.Children.Add(currentText);

        return (SettingRow(label, detail, row), slider, () => slider.Value);
    }

    private static string CarouselHtml()
    {
        return """
<!DOCTYPE html>
<html><head><meta charset='utf-8'><style>
*{margin:0;padding:0;box-sizing:border-box}
body{background:transparent;font-family:system-ui,-apple-system,sans-serif;overflow:hidden;user-select:none}
.carousel{width:460px;margin:0 auto;position:relative;height:385px;overflow:hidden}
.cards{position:relative;height:345px}
.card{position:absolute;left:50%;top:6px;width:270px;border-radius:14px;background:#202020;border:1px solid rgba(255,255,255,.08);
  transition:transform .45s cubic-bezier(.2,.8,.2,1),opacity .4s ease,box-shadow .4s ease;
  will-change:transform,opacity;display:flex;align-items:center;justify-content:center;overflow:hidden}
.card.no-trans{transition:none!important}
.card.hero{z-index:3;padding:22px 20px 18px;opacity:1;
  transform:translate(-50%,0) scale(1);
  box-shadow:0 8px 32px rgba(0,0,0,.4)}
.card.side{z-index:1;padding:14px 12px;opacity:.35;
  box-shadow:0 4px 16px rgba(0,0,0,.25)}
.card.side.left{transform:translate(calc(-50% - 175px),28px) scale(.55)}
.card.side.right{transform:translate(calc(-50% + 175px),28px) scale(.55)}
.card.hidden{z-index:0;padding:14px 12px;opacity:0;pointer-events:none;
  box-shadow:none}
.card.hidden.left{transform:translate(calc(-50% - 320px),28px) scale(.4)}
.card.hidden.right{transform:translate(calc(-50% + 320px),28px) scale(.4)}
.card-inner{text-align:center;width:100%}
.logo-badge{width:88px;height:88px;border-radius:50%;display:flex;align-items:center;justify-content:center;margin:0 auto 8px;
  box-shadow:0 4px 18px rgba(0,0,0,.3)}
.logo-badge img{width:72px;height:72px;border-radius:50%;object-fit:cover}
.logo-letter{color:#fff;font-size:34px;font-weight:700}
.name{color:#f0f0f0;font-weight:700;font-size:26px;margin-bottom:6px}
.accent{height:2px;border-radius:1px;margin:0 auto 8px;width:44px}
.desc{color:#aaa;font-size:11px;line-height:1.4;text-align:center;display:block}
.card:not(.hero) .accent,.card:not(.hero) .desc{display:none}
.glass{position:absolute;top:0;left:0;right:0;bottom:0;pointer-events:none;
  background:linear-gradient(135deg,rgba(255,255,255,.04) 0%,transparent 60%);border-radius:14px}
.nav{display:flex;justify-content:center;align-items:center;gap:10px;position:relative;z-index:10}
.nav button{background:rgba(255,255,255,.08);border:none;border-radius:8px;padding:6px 18px;color:#ccc;font-size:14px;
  cursor:pointer;transition:all .25s ease}
.nav button:hover{background:rgba(255,255,255,.15)}
.nav button:disabled{opacity:.3;cursor:default;background:rgba(255,255,255,.04)}
.selected-badge{display:none;font-size:11px;color:#4ade80;font-weight:600;margin-top:4px;letter-spacing:.5px;text-transform:uppercase}
.selected-badge.show{display:block}
</style></head><body>
<div class='carousel'>
  <div class='cards' id='cards'></div>
  <div class='nav'>
    <button id='prevBtn' onclick='nav(-1)'>◀</button>
    <button id='lockBtn' onclick='toggleLock()'>🔒</button>
    <button id='nextBtn' onclick='nav(1)'>▶</button>
  </div>
</div>
<script>
let data=null,idx=0,locked=true;
window.chrome.webview.addEventListener('message',e=>{
  const m=JSON.parse(e.data);
  if(m.type==='init'){data=m;idx=m.currentIndex;locked=m.locked;build();updateNav();}
  if(m.type==='update'){data=m;build();}
});
function build(){
  const c=document.getElementById('cards');c.innerHTML='';
  for(let i=0;i<data.engines.length;i++)c.appendChild(render(i));
  highlightSelected();
}
function cls(i){
  if(i===idx)return'hero';
  const n=data.engines.length,p=(idx-1+n)%n,nx=(idx+1)%n;
  if(i===p)return'side left';
  if(i===nx)return'side right';
  return((idx-i+n)%n)<((i-idx+n)%n)?'hidden left':'hidden right';
}
function render(i){
  const e=data.engines[i],div=document.createElement('div');
  div.className='card '+cls(i);
  const clr=e.color||'#666';
  const inner=document.createElement('div');
  inner.className='card-inner';
  const badge=document.createElement('div');
  badge.className='logo-badge';
  badge.style.background=clr;
  if(e.icon){
    const img=document.createElement('img');
    img.src='https://assets.local/'+e.icon;
    img.onerror=function(){this.style.display='none';this.nextElementSibling.style.display='block'};
    badge.appendChild(img);
    const letter=document.createElement('span');
    letter.className='logo-letter';
    letter.style.display='none';
    letter.textContent=e.letter;
    badge.appendChild(letter);
  }else{
    const letter=document.createElement('span');
    letter.className='logo-letter';
    letter.textContent=e.letter;
    badge.appendChild(letter);
  }
  inner.appendChild(badge);
  const name=document.createElement('div');
  name.className='name';
  name.textContent=e.name;
  inner.appendChild(name);
  const accent=document.createElement('div');
  accent.className='accent';
  accent.style.background=clr;
  inner.appendChild(accent);
  const desc=document.createElement('div');
  desc.className='desc';
  desc.textContent=e.desc||'';
  inner.appendChild(desc);
  const selBadge=document.createElement('div');
  selBadge.className='selected-badge';
  selBadge.textContent='Active';
  inner.appendChild(selBadge);
  div.appendChild(inner);
  const glass=document.createElement('div');
  glass.className='glass';
  div.appendChild(glass);
  if(i!==idx)div.addEventListener('click',()=>clickTo(i));
  return div;
}
function clickTo(i){if(!locked&&i!==idx){idx=i;apply();updateNav();send('select',idx)}}
function nav(dir){if(locked)return;idx=(idx+dir+data.engines.length)%data.engines.length;apply();updateNav();send('select',idx)}
function apply(){
  const cs=document.querySelectorAll('.card');
  cs.forEach((c,i)=>{
    const nw='card '+cls(i);
    if(c.className===nw)return;
    const was=c.className.includes('hero')||c.className.includes('side');
    const will=nw.includes('hero')||nw.includes('side');
    if(!was&&!will){c.classList.add('no-trans');c.className=nw;requestAnimationFrame(()=>c.classList.remove('no-trans'))}
    else c.className=nw;
  });
}
function toggleLock(){locked=!locked;document.getElementById('lockBtn').textContent=locked?'🔒':'🔓';updateNav();highlightSelected();if(locked)send('lock',idx)}
function updateNav(){
  const p=document.getElementById('prevBtn'),n=document.getElementById('nextBtn');
  p.disabled=locked;n.disabled=locked;
}
function highlightSelected(){
  document.querySelectorAll('.selected-badge').forEach(b=>b.classList.remove('show'));
  if(locked){
    const hero=document.querySelector('.card.hero');
    if(hero){const badge=hero.querySelector('.selected-badge');if(badge)badge.classList.add('show');}
  }
}
function send(type,index){const eng=data.engines[index];window.chrome.webview.postMessage(JSON.stringify({type:type,index:index,engine:eng.name}))}
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
                var letter = c.Name.Length > 0 ? c.Name[..1].ToUpper() : "?";
                engines.Add((c.Name, letter, customBrush, c.UrlTemplate, null));
            }
        }
        RebuildEngineList();

        int currentIndex = engines.FindIndex(e => e.Name == selected);
        if (currentIndex < 0) currentIndex = 0;
        bool isCustomSelected = currentIndex >= builtInNames.Length;
        bool isLocked = true;
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
            Height = 380,
            DefaultBackgroundColor = System.Drawing.Color.Transparent
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
            catch (Exception ex) { Debug.WriteLine($"Carousel WebView2 init failed: {ex}"); _ = Dispatcher.BeginInvoke(() => { if (!Dispatcher.HasShutdownStarted) StatusText.Text = "Carousel failed to load"; }); }
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
                    isLocked = true;
                    var lockIdx = (int?)json?["index"] ?? currentIndex;
                    currentIndex = lockIdx;
                    isCustomSelected = currentIndex >= builtInNames.Length;
                    if (removeBtn is not null)
                        removeBtn.Visibility = isCustomSelected ? Visibility.Visible : Visibility.Collapsed;
                    onChange();
                }
            }
            catch (Exception ex) { Debug.WriteLine($"WebMessageReceived error: {ex}"); }
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
                locked = true
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
            Margin = new Thickness(0, 8, 0, 0)
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
                Debug.WriteLine(ex);
            }
        };

        return (root,
            () => engines[currentIndex].Name,
            () => [.. currentCustoms]);
    }
}

