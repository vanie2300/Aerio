using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace TabloidBrowser;

public partial class MainWindow
{
    private AiService? _aiService;
    private CancellationTokenSource? _aiCts;
    private CancellationTokenSource? _fetchCts;
    private double _aiSidebarWidth = 340;

    private const string PageContextScript =
        """
        (() => {
            var text = '';
            var article = document.querySelector('article');
            if (article) {
                text = article.innerText;
            } else {
                var main = document.querySelector('main');
                if (main) {
                    text = main.innerText;
                } else {
                    for (var el of document.querySelectorAll('p, li, pre, td, th, blockquote')) {
                        text += el.innerText + '\n';
                        if (text.length > 8000) break;
                    }
                }
            }
            return text.substring(0, 8000);
        })();
        """;

    private async Task<string?> GetPageContentAsync()
    {
        try
        {
            var tab = _activeTab;
            if (tab?.WebView?.CoreWebView2 is null) return null;

            var json = await tab.WebView.CoreWebView2.ExecuteScriptAsync(PageContextScript);
            if (json is null) return null;

            var pageText = System.Text.Json.JsonSerializer.Deserialize<string>(json);
            return string.IsNullOrWhiteSpace(pageText) ? null : pageText.Trim();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"GetPageContentAsync failed: {ex}");
            return null;
        }
    }

    private void ToggleAiSidebar()
    {
        if (AiSidebar.Visibility == Visibility.Visible)
        {
            HideAiSidebar();
        }
        else
        {
            ShowAiSidebar();
        }
    }

    private void ShowAiSidebar()
    {
        var settings = _dataStore.Settings;
        if (!settings.AiEnabled)
        {
            ShowAiSetupPopup();
            return;
        }

        EnsureAiService(settings);
        ClearAiMessages();
        LoadAiMessages();
        AiSidebarColumn.Width = new GridLength(_aiSidebarWidth);
        AiSidebar.Visibility = Visibility.Visible;
        AiSidebar.Opacity = 0;
        AiSidebar.RenderTransform = new TranslateTransform(20, 0);
        AiSplitter.Visibility = Visibility.Visible;

        var slideFade = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.25))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        AiSidebar.BeginAnimation(OpacityProperty, slideFade);
        var slideX = new DoubleAnimation(20, 0, TimeSpan.FromSeconds(0.3))
        {
            EasingFunction = new CircleEase { EasingMode = EasingMode.EaseOut }
        };
        (AiSidebar.RenderTransform as TranslateTransform)?.BeginAnimation(TranslateTransform.XProperty, slideX);

        _dataStore.SaveSettings(settings with { AiSidebarOpen = true });
        AiInputBox.Focus();
    }

    private Popup? _aiSetupPopup;
    private bool _isAiSetupOpen;

    private void ShowAiSetupPopup()
    {
        if (_isAiSetupOpen) return;
        RemovePopupBlur();

        if (Content is Grid rootGrid)
        {
            rootGrid.Effect = new BlurEffect
            {
                Radius = 14,
                KernelType = KernelType.Gaussian
            };

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

        _isAiSetupOpen = true;

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
            _fetchCts?.Cancel();
            _fetchCts?.Dispose();
            _fetchCts = null;
            RemovePopupBlur();
            _aiSetupPopup = null;
            _isAiSetupOpen = false;
        };

        popup.Child = BuildAiSetupContent(popup);
        _aiSetupPopup = popup;
        popup.IsOpen = true;
    }

    private FrameworkElement BuildAiSetupContent(Popup popup)
    {
        var settings = _dataStore.Settings;
        var outerBorder = new Border
        {
            Width = 460,
            Background = GetBrush("ChromeBg"),
            BorderBrush = GetBrush("Stroke"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(24, 20, 24, 20),
            Effect = new DropShadowEffect
            {
                BlurRadius = 24,
                Opacity = 0.3
            }
        };

        var stack = new StackPanel();

        var header = new TextBlock
        {
            Text = "AI Setup",
            Foreground = GetBrush("Text"),
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        };
        stack.Children.Add(header);

        var sub = new TextBlock
        {
            Text = "Configure your AI assistant to get started.",
            Foreground = GetBrush("Muted"),
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 18)
        };
        stack.Children.Add(sub);

        var toggleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
        var enableToggle = new CheckBox
        {
            IsChecked = true,
            Foreground = GetBrush("Text"),
            FontSize = 14,
            Content = "Enable AI assistant"
        };
        toggleRow.Children.Add(enableToggle);
        stack.Children.Add(toggleRow);

        var providerKeys = BrowserSettings.AiProviderPresets.Keys.ToArray();
        var providerCombo = new ComboBox
        {
            ItemsSource = providerKeys,
            Height = 32,
            Background = GetBrush("InputBg"),
            Foreground = GetBrush("Text"),
            BorderBrush = GetBrush("Stroke")
        };
        providerCombo.SelectedItem = string.IsNullOrWhiteSpace(settings.AiProvider) ? "Ollama" : settings.AiProvider;
        if (providerCombo.SelectedItem is null && providerKeys.Length > 0)
            providerCombo.SelectedItem = providerKeys[0];

        var endpointBox = new TextBox
        {
            Text = settings.AiEndpoint,
            Height = 32,
            Background = GetBrush("InputBg"),
            Foreground = GetBrush("Text"),
            BorderBrush = GetBrush("Stroke"),
            Padding = new Thickness(8, 5, 8, 0)
        };

        var modelCombo = new ComboBox
        {
            IsEditable = true,
            Text = settings.AiModel,
            Height = 32,
            Background = GetBrush("InputBg"),
            Foreground = GetBrush("Text"),
            BorderBrush = GetBrush("Stroke")
        };
        modelCombo.SelectionChanged += (_, _) =>
        {
            if (modelCombo.SelectedItem is string s)
                modelCombo.Text = s;
        };

        var apiKeyBox = new PasswordBox
        {
            Height = 32,
            Background = GetBrush("InputBg"),
            Foreground = GetBrush("Text"),
            BorderBrush = GetBrush("Stroke"),
            Padding = new Thickness(8, 5, 8, 0)
        };
        apiKeyBox.Password = settings.AiApiKey;

        var statusLabel = new TextBlock
        {
            Foreground = GetBrush("Muted"),
            FontSize = 11,
            Margin = new Thickness(0, 4, 0, 0)
        };

        _fetchCts = null;

        async Task FetchModelsAsync(string ep, string key, string prov, CancellationToken ct)
        {
            try
            {
                statusLabel.Text = "Discovering models...";
                await Task.Delay(500, ct);
                ct.ThrowIfCancellationRequested();
                if (!_isAiSetupOpen) return;
                var models = await AiService.FetchModelsAsync(ep, key, prov, ct);
                ct.ThrowIfCancellationRequested();
                if (!_isAiSetupOpen) return;
                if (models.Count > 0)
                {
                    var prev = modelCombo.Text;
                    modelCombo.ItemsSource = models;
                    modelCombo.Text = prev;
                    statusLabel.Text = $"Discovered {models.Count} models";
                }
                else
                {
                    statusLabel.Text = "Could not reach endpoint — check URL or API key";
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Debug.WriteLine($"Model discovery: {ex}"); statusLabel.Text = "Could not reach endpoint — check URL or API key"; }
        }

        async void DebounceFetchModels()
        {
            _fetchCts?.Cancel();
            _fetchCts?.Dispose();
            _fetchCts = new CancellationTokenSource();
            var ct = _fetchCts.Token;
            try
            {
                await Task.Delay(800, ct);
                ct.ThrowIfCancellationRequested();
                var op = Dispatcher.InvokeAsync(() =>
                    FetchModelsAsync(endpointBox.Text, apiKeyBox.Password,
                        providerCombo.Text?.Trim() ?? providerCombo.SelectedItem?.ToString() ?? "Ollama",
                        ct));
                await op.Task.Unwrap();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Debug.WriteLine($"DebounceFetchModels error: {ex}"); }
        }

        void RefreshEndpoint()
        {
            var prov = providerCombo.Text?.Trim() ?? providerCombo.SelectedItem?.ToString() ?? "Ollama";
            var preset = BrowserSettings.GetProviderEndpoint(prov);
            if (!string.IsNullOrWhiteSpace(preset))
                endpointBox.Text = preset;
        }
        providerCombo.SelectionChanged += (_, _) => RefreshEndpoint();
        endpointBox.TextChanged += (_, _) => DebounceFetchModels();

        stack.Children.Add(SettingRow("Provider", "API provider type.", providerCombo));
        stack.Children.Add(SettingRow("Endpoint URL", "Full URL to the OpenAI-compatible API.", endpointBox));
        stack.Children.Add(SettingRow("Model", "Model name (e.g. llama3.2, gpt-4o-mini).", modelCombo));
        stack.Children.Add(SettingRow("API key", "Optional API key if required.", apiKeyBox));
        stack.Children.Add(statusLabel);

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };

        var cancelBtn = MatteButton("Cancel", (_, _) => popup.IsOpen = false);
        var testBtn = new Button
        {
            Content = "Test Connection",
            MinWidth = 92,
            Height = 30,
            Padding = new Thickness(12, 0, 12, 0),
            Margin = new Thickness(8, 0, 0, 0),
            Style = FindResource("MicroButton") as Style
        };
        testBtn.Click += async (_, _) =>
        {
            testBtn.IsEnabled = false;
            statusLabel.Text = "Testing...";
            try
            {
                var prov = providerCombo.Text?.Trim() ?? providerCombo.SelectedItem?.ToString() ?? "Ollama";
                var ep = endpointBox.Text?.Trim() ?? "";
                var model = modelCombo.Text?.Trim() ?? modelCombo.SelectedItem?.ToString() ?? "";
                var key = apiKeyBox.Password;
                var models = await AiService.FetchModelsAsync(ep, key, prov, CancellationToken.None);
                if (models.Count > 0)
                {
                    var prev = modelCombo.Text;
                    modelCombo.ItemsSource = models;
                    modelCombo.Text = prev;
                    statusLabel.Text = $"Connected — discovered {models.Count} models";
                }
                else
                {
                    statusLabel.Text = "Could not reach endpoint — check URL or API key";
                }
            }
            catch (Exception ex)
            {
                statusLabel.Text = $"Connection failed: {ex.Message}";
            }
            finally
            {
                testBtn.IsEnabled = true;
            }
        };
        var saveBtn = new Button
        {
            Content = "Save && Open",
            MinWidth = 92,
            Height = 30,
            Padding = new Thickness(12, 0, 12, 0),
            Margin = new Thickness(8, 0, 0, 0),
            Style = FindResource("MicroButton") as Style,
            IsDefault = true
        };
        saveBtn.Click += (_, _) =>
        {
            var prov = providerCombo.Text?.Trim() ?? providerCombo.SelectedItem?.ToString() ?? "Ollama";
            var ep = endpointBox.Text?.Trim() ?? "";
            var model = modelCombo.Text?.Trim() ?? modelCombo.SelectedItem?.ToString() ?? "";
            var key = apiKeyBox.Password;

            _dataStore.SaveSettings(settings with
            {
                AiEnabled = true,
                AiSidebarOpen = true,
                AiProvider = prov,
                AiEndpoint = ep,
                AiModel = model,
                AiApiKey = key
            });

            ApplySettings();
            popup.IsOpen = false;
            ShowAiSidebar();
        };

        buttonRow.Children.Add(cancelBtn);
        buttonRow.Children.Add(testBtn);
        buttonRow.Children.Add(saveBtn);
        stack.Children.Add(buttonRow);

        outerBorder.Child = stack;
        return outerBorder;
    }

    private void LoadAiMessages()
    {
        var history = _activeTab?.AiChatHistory;
        if (history is null || history.Count == 0) return;

        foreach (var msg in history)
        {
            AddAiMessage(msg.Role, msg.Content);
        }
    }

    private void HideAiSidebar()
    {
        _aiCts?.Cancel();
        _aiCts?.Dispose();
        var actualWidth = AiSidebarColumn.ActualWidth;
        if (actualWidth > 10)
            _aiSidebarWidth = actualWidth;

        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.2))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        var slideX = new DoubleAnimation(0, 20, TimeSpan.FromSeconds(0.2))
        {
            EasingFunction = new CircleEase { EasingMode = EasingMode.EaseIn }
        };
        fadeOut.Completed += (_, _) =>
        {
            AiSidebarColumn.Width = new GridLength(0);
            AiSidebar.Visibility = Visibility.Collapsed;
            AiSidebar.Opacity = 1;
            AiSidebar.RenderTransform = null;
            AiSplitter.Visibility = Visibility.Collapsed;
        };
        AiSidebar.BeginAnimation(OpacityProperty, fadeOut);
        (AiSidebar.RenderTransform as TranslateTransform)?.BeginAnimation(TranslateTransform.XProperty, slideX);

        _dataStore.SaveSettings(_dataStore.Settings with { AiSidebarOpen = false });
    }

    private void EnsureAiService(BrowserSettings settings)
    {
        if (_aiService is null)
        {
            _aiService = new AiService();
        }

        _aiService.Endpoint = settings.AiEndpoint;
        _aiService.Model = settings.AiModel;
        _aiService.ApiKey = settings.AiApiKey;
        _aiService.SystemPrompt = settings.AiSystemPrompt;
        _aiService.MaxTokens = settings.AiMaxTokens;
        _aiService.Temperature = settings.AiTemperature;
    }

    private async void AiSend_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = AiInputBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text)) return;

            var tab = _activeTab;
            if (tab is null) return;

            var settings = _dataStore.Settings;
            if (!settings.AiEnabled) return;

            AiInputBox.IsEnabled = false;
            AiSendBtn.IsEnabled = false;

            EnsureAiService(settings);

            _aiCts?.Cancel();
            _aiCts?.Dispose();
            _aiCts = new CancellationTokenSource();
            var ct = _aiCts.Token;

            AddAiMessage("user", text);
            AiInputBox.Clear();

            AddAiMessage("assistant", "Thinking...", isPlaceholder: true);

            var history = tab.AiChatHistory ??= [];
            history.Add(new ChatMessage("user", text));

            List<ChatMessage> messages;

            if (AiPageContextToggle.IsChecked == true)
            {
                var pageContent = await GetPageContentAsync();
                if (pageContent is not null)
                {
                    messages = new List<ChatMessage>(history.Count + 1);
                    messages.Add(new ChatMessage("system",
                        "You are browsing the following page content. Use it to answer the user's question:\n\n" + pageContent));
                    messages.AddRange(history);
                }
                else
                {
                    messages = history;
                }
            }
            else
            {
                messages = history;
            }

            var reply = await _aiService!.CompleteAsync(messages, ct);
            ct.ThrowIfCancellationRequested();

            RemoveLastAiPlaceholder();
            history.Add(new ChatMessage("assistant", reply));
            AddAiMessage("assistant", reply);
        }
        catch (OperationCanceledException ex)
        {
            RemoveLastAiPlaceholder();
            Debug.WriteLine($"AiSend canceled: {ex.Message}");
        }
        catch (Exception ex)
        {
            RemoveLastAiPlaceholder();
            AddAiMessage("assistant", $"Error: {ex.Message}");
            Debug.WriteLine($"AiSend failed: {ex}");
        }
        finally
        {
            AiInputBox.IsEnabled = true;
            AiSendBtn.IsEnabled = true;
            AiInputBox.Focus();
        }
    }

    private void AiInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            AiSend_Click(sender, e);
        }
    }

    private void AddAiMessage(string role, string content, bool isPlaceholder = false)
    {
        var isAssistant = role != "user";
        var bubble = new Border
        {
            MaxWidth = 300,
            Margin = new Thickness(0, 0, 0, 8),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            HorizontalAlignment = isAssistant ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            Background = isAssistant
                ? GetBrush("InputBg")
                : GetBrush("Cyan"),
            BorderBrush = isAssistant
                ? GetBrush("SubtleStroke")
                : Brushes.Transparent,
            BorderThickness = new Thickness(1),
        };

        if (isPlaceholder)
        {
            bubble.Tag = "placeholder";
        }

        var innerStack = new StackPanel();

        var headerPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        var roleLabel = new TextBlock
        {
            Text = isAssistant ? "Assistant" : "You",
            Foreground = isAssistant ? GetBrush("Cyan") : Brushes.White,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold
        };
        headerPanel.Children.Add(roleLabel);
        innerStack.Children.Add(headerPanel);

        if (isPlaceholder)
        {
            var placeholderText = new TextBlock
            {
                Text = content,
                Foreground = GetBrush("Muted"),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap
            };
            innerStack.Children.Add(placeholderText);
        }
        else
        {
            var formatted = FormatMarkdownContent(content, isAssistant);
            innerStack.Children.Add(formatted);
        }

        if (isAssistant)
        {
            var copyBtn = new Button
            {
                Content = "Copy",
                FontSize = 9,
                Height = 18,
                Padding = new Thickness(6, 0, 6, 0),
                Margin = new Thickness(0, 3, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                Cursor = Cursors.Hand,
                Style = FindResource("MicroButton") as Style
            };
            copyBtn.Click += (_, _) =>
            {
                try
                {
                    Clipboard.SetText(content);
                    copyBtn.Content = "Copied!";
                    _ = Task.Delay(2000).ContinueWith(_ =>
                    {
                        try
                        {
                            copyBtn.Dispatcher.Invoke(() => copyBtn.Content = "Copy");
                        }
                        catch (Exception ex) { Debug.WriteLine($"Copy button reset failed: {ex}"); }
                    });
                }
                catch (Exception ex) { Debug.WriteLine($"Copy failed: {ex}"); }
            };
            innerStack.Children.Add(copyBtn);
        }

        bubble.Child = innerStack;
        AiMessagesStack.Children.Add(bubble);

        if (AiMessagesScroll is not null)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (AiMessagesScroll is not null)
                    AiMessagesScroll.ScrollToBottom();
            }, System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private static UIElement FormatMarkdownContent(string text, bool isAssistant)
    {
        var stack = new StackPanel();
        var fg = isAssistant ? "Text" : "White";
        var codeBg = isAssistant ? "ChromeAlt" : "#50FFFFFF";
        var muted = isAssistant ? "Muted" : "#C0FFFFFF";

        var lines = text.Split('\n');
        var inCodeBlock = false;
        var codeLines = new List<string>();

        void FlushCodeBlock()
        {
            if (codeLines.Count == 0) return;
            var block = new Border
            {
                Background = GetBrushStatic(codeBg),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 6)
            };
            var codeText = new TextBlock
            {
                Text = string.Join("\n", codeLines),
                Foreground = GetBrushStatic(fg),
                FontSize = 12,
                FontFamily = new FontFamily("Consolas"),
                TextWrapping = TextWrapping.Wrap
            };
            block.Child = codeText;
            stack.Children.Add(block);
            codeLines.Clear();
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            if (line.TrimStart().StartsWith("```"))
            {
                if (inCodeBlock)
                {
                    FlushCodeBlock();
                    inCodeBlock = false;
                }
                else
                {
                    FlushCodeBlock();
                    inCodeBlock = true;
                }
                continue;
            }

            if (inCodeBlock)
            {
                codeLines.Add(line);
                continue;
            }

            FlushCodeBlock();

            if (string.IsNullOrWhiteSpace(line))
            {
                stack.Children.Add(new Rectangle { Height = 6, Fill = Brushes.Transparent });
                continue;
            }

            var trimmed = line.TrimStart();
            var indent = line.Length - trimmed.Length;

            // Headers
            var headerLevel = 0;
            var headerText = trimmed;
            if (trimmed.StartsWith("### "))
            {
                headerLevel = 3;
                headerText = trimmed[4..];
            }
            else if (trimmed.StartsWith("## "))
            {
                headerLevel = 2;
                headerText = trimmed[3..];
            }
            else if (trimmed.StartsWith("# "))
            {
                headerLevel = 1;
                headerText = trimmed[2..];
            }

            if (headerLevel > 0)
            {
                var tb = new TextBlock
                {
                    Foreground = GetBrushStatic(fg),
                    FontSize = headerLevel == 1 ? 18 : headerLevel == 2 ? 15 : 13,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 0, 0, 4),
                    TextWrapping = TextWrapping.Wrap
                };
                BuildInlineContent(tb.Inlines, headerText, fg);
                stack.Children.Add(tb);
                continue;
            }

            // List items
            var listMatch = System.Text.RegularExpressions.Regex.Match(trimmed, @"^[-*+]\s+(.*)");
            if (listMatch.Success)
            {
                var bulletPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 2) };
                var bullet = new TextBlock
                {
                    Text = "•",
                    Foreground = GetBrushStatic(fg),
                    FontSize = 13,
                    Margin = new Thickness(0, 0, 6, 0)
                };
                bulletPanel.Children.Add(bullet);

                var itemText = new TextBlock
                {
                    Foreground = GetBrushStatic(fg),
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap
                };
                BuildInlineContent(itemText.Inlines, listMatch.Groups[1].Value, fg);
                bulletPanel.Children.Add(itemText);
                stack.Children.Add(bulletPanel);
                continue;
            }

            // Numbered list
            var numMatch = System.Text.RegularExpressions.Regex.Match(trimmed, @"^(\d+)\.\s+(.*)");
            if (numMatch.Success)
            {
                var numPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 2) };
                var numLabel = new TextBlock
                {
                    Text = numMatch.Groups[1].Value + ".",
                    Foreground = GetBrushStatic(fg),
                    FontSize = 13,
                    Margin = new Thickness(0, 0, 6, 0)
                };
                numPanel.Children.Add(numLabel);

                var itemText = new TextBlock
                {
                    Foreground = GetBrushStatic(fg),
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap
                };
                BuildInlineContent(itemText.Inlines, numMatch.Groups[2].Value, fg);
                numPanel.Children.Add(itemText);
                stack.Children.Add(numPanel);
                continue;
            }

            // Regular paragraph
            var para = new TextBlock
            {
                Foreground = GetBrushStatic(fg),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4)
            };
            BuildInlineContent(para.Inlines, line, fg);
            stack.Children.Add(para);
        }

        FlushCodeBlock();
        return stack;
    }

    private static void BuildInlineContent(InlineCollection inlines, string text, string fg)
    {
        // Process **bold**, *italic*, `code` inline
        var parts = System.Text.RegularExpressions.Regex.Split(text,
            @"(\*\*.*?\*\*|\*.*?\*|`[^`]+`)");

        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part)) continue;

            if (part.StartsWith("**") && part.EndsWith("**") && part.Length > 4)
            {
                inlines.Add(new Bold(new Run(part[2..^2])
                {
                    Foreground = GetBrushStatic(fg)
                }));
            }
            else if (part.StartsWith("*") && part.EndsWith("*") && part.Length > 2 && !part.StartsWith("**"))
            {
                inlines.Add(new Italic(new Run(part[1..^1])
                {
                    Foreground = GetBrushStatic(fg)
                }));
            }
            else if (part.StartsWith("`") && part.EndsWith("`") && part.Length > 2)
            {
                inlines.Add(new Run(part[1..^1])
                {
                    Foreground = GetBrushStatic(fg),
                    Background = GetBrushStatic("#40CCCCCC"),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12
                });
            }
            else
            {
                inlines.Add(new Run(part) { Foreground = GetBrushStatic(fg) });
            }
        }
    }

    private static Brush GetBrushStatic(string key)
    {
        if (Application.Current.TryFindResource(key) is Brush b)
            return b;
        if (Application.Current.MainWindow?.TryFindResource(key) is Brush wb)
            return wb;
        if (key.StartsWith("#"))
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(key));
        return Brushes.White;
    }

    private void RemoveLastAiPlaceholder()
    {
        for (int i = AiMessagesStack.Children.Count - 1; i >= 0; i--)
        {
            if (AiMessagesStack.Children[i] is Border { Tag: "placeholder" })
            {
                AiMessagesStack.Children.RemoveAt(i);
                return;
            }
        }
    }

    private void AiSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _aiSidebarWidth = Math.Max(200, Math.Min(600, AiSidebarColumn.ActualWidth));
    }

    private void ClearAiMessages()
    {
        AiMessagesStack.Children.Clear();
    }

    private void AiClearChat_Click(object sender, RoutedEventArgs e)
    {
        ClearAiMessages();
        if (_activeTab?.AiChatHistory is { } history)
            history.Clear();
    }

    private void AiSidebarClose_Click(object sender, RoutedEventArgs e)
    {
        HideAiSidebar();
    }

    private void AiToggle_Click(object sender, RoutedEventArgs e)
    {
        ToggleAiSidebar();
    }

    internal void NotifySettingsChanged()
    {
        if (_aiService is not null)
        {
            var settings = _dataStore.Settings;
            _aiService.Endpoint = settings.AiEndpoint;
            _aiService.Model = settings.AiModel;
            _aiService.ApiKey = settings.AiApiKey;
            _aiService.SystemPrompt = settings.AiSystemPrompt;
            _aiService.MaxTokens = settings.AiMaxTokens;
            _aiService.Temperature = settings.AiTemperature;

            if (!settings.AiEnabled)
                HideAiSidebar();
        }

        if (_dataStore.Settings.AiSidebarOpen && _dataStore.Settings.AiEnabled)
        {
            ShowAiSidebar();
        }
        else
        {
            HideAiSidebar();
        }
    }
}
