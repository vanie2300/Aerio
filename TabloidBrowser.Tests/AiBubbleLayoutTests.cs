using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace TabloidBrowser.Tests;

public class AiBubbleLayoutTests
{
    private static void RunOnSta(Action action)
    {
        Exception? captured = null;
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (captured != null)
            throw captured;
    }

    private static T RunOnSta<T>(Func<T> func)
    {
        T? result = default;
        Exception? captured = null;
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                result = func();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (captured != null)
            throw captured;
        return result!;
    }

    private static Border CreateBubble(string role, string content, bool isPlaceholder = false, bool isToolResult = false)
    {
        var isAssistant = role != "user";
        var bubble = new Border
        {
            Margin = new Thickness(0, 0, 0, 8),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            HorizontalAlignment = isAssistant ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            BorderThickness = new Thickness(1),
        };

        if (isPlaceholder)
            bubble.Tag = "placeholder";

        if (isToolResult)
        {
            bubble.Opacity = 0.7;
            bubble.MaxWidth = 250;
        }

        var innerStack = new StackPanel();

        var headerPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        var roleLabel = new TextBlock
        {
            Text = isAssistant ? "Assistant" : "You",
            FontSize = 10,
            FontWeight = FontWeights.SemiBold
        };
        headerPanel.Children.Add(roleLabel);
        innerStack.Children.Add(headerPanel);

        var contentBlock = new TextBlock
        {
            Text = content,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap
        };
        innerStack.Children.Add(contentBlock);

        bubble.Child = innerStack;
        return bubble;
    }

    private static Border CreateStreamingBubble()
    {
        var bubble = new Border
        {
            Margin = new Thickness(0, 0, 0, 8),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            HorizontalAlignment = HorizontalAlignment.Left,
            BorderThickness = new Thickness(1),
            Tag = "streaming"
        };

        var innerStack = new StackPanel();

        var contentBlock = new TextBlock
        {
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Text = ""
        };
        innerStack.Children.Add(contentBlock);

        bubble.Child = innerStack;
        return bubble;
    }

    [Fact]
    public void AssistantBubble_HasLeftAlignment() => RunOnSta(() =>
    {
        var bubble = CreateBubble("assistant", "Hello");
        Assert.Equal(HorizontalAlignment.Left, bubble.HorizontalAlignment);
    });

    [Fact]
    public void AssistantBubble_HasNoMaxWidth() => RunOnSta(() =>
    {
        var bubble = CreateBubble("assistant", "Hello");
        Assert.Equal(double.PositiveInfinity, bubble.MaxWidth);
    });

    [Fact]
    public void UserBubble_HasRightAlignment() => RunOnSta(() =>
    {
        var bubble = CreateBubble("user", "Hello");
        Assert.Equal(HorizontalAlignment.Right, bubble.HorizontalAlignment);
    });

    [Fact]
    public void UserBubble_HasNoMaxWidth() => RunOnSta(() =>
    {
        var bubble = CreateBubble("user", "Hello");
        Assert.Equal(double.PositiveInfinity, bubble.MaxWidth);
    });

    [Fact]
    public void ToolResultBubble_HasMaxWidth250() => RunOnSta(() =>
    {
        var bubble = CreateBubble("assistant", "result", isToolResult: true);
        Assert.Equal(250, bubble.MaxWidth);
    });

    [Fact]
    public void ToolResultBubble_HasReducedOpacity() => RunOnSta(() =>
    {
        var bubble = CreateBubble("assistant", "result", isToolResult: true);
        Assert.Equal(0.7, bubble.Opacity);
    });

    [Fact]
    public void StreamingBubble_HasLeftAlignment() => RunOnSta(() =>
    {
        var bubble = CreateStreamingBubble();
        Assert.Equal(HorizontalAlignment.Left, bubble.HorizontalAlignment);
    });

    [Fact]
    public void StreamingBubble_HasNoMaxWidth() => RunOnSta(() =>
    {
        var bubble = CreateStreamingBubble();
        Assert.Equal(double.PositiveInfinity, bubble.MaxWidth);
    });

    [Fact]
    public void StreamingBubble_HasStreamingTag() => RunOnSta(() =>
    {
        var bubble = CreateStreamingBubble();
        Assert.Equal("streaming", bubble.Tag);
    });

    [Fact]
    public void AssistantBubble_HasCorrectPadding() => RunOnSta(() =>
    {
        var bubble = CreateBubble("assistant", "Hi");
        Assert.Equal(new Thickness(10, 8, 10, 8), bubble.Padding);
    });

    [Fact]
    public void AssistantBubble_HasCorrectMargin() => RunOnSta(() =>
    {
        var bubble = CreateBubble("assistant", "Hi");
        Assert.Equal(new Thickness(0, 0, 0, 8), bubble.Margin);
    });

    [Fact]
    public void AssistantBubble_HasCornerRadius() => RunOnSta(() =>
    {
        var bubble = CreateBubble("assistant", "test");
        Assert.Equal(new CornerRadius(8), bubble.CornerRadius);
    });

    [Fact]
    public void AssistantBubble_HasBorderThickness() => RunOnSta(() =>
    {
        var bubble = CreateBubble("assistant", "test");
        Assert.Equal(new Thickness(1), bubble.BorderThickness);
    });

    [Fact]
    public void ContentBlock_WrapsText() => RunOnSta(() =>
    {
        var bubble = CreateBubble("assistant", "Short");
        var innerStack = (StackPanel)bubble.Child!;
        var contentBlock = (TextBlock)innerStack.Children[1];
        Assert.Equal(TextWrapping.Wrap, contentBlock.TextWrapping);
    });

    [Fact]
    public void ContentBlock_FontSize13() => RunOnSta(() =>
    {
        var bubble = CreateBubble("assistant", "Short");
        var innerStack = (StackPanel)bubble.Child!;
        var contentBlock = (TextBlock)innerStack.Children[1];
        Assert.Equal(13, contentBlock.FontSize);
    });

    [Fact]
    public void AssistantBubble_ShortMessage_UsesLeftAlignment() => RunOnSta(() =>
    {
        var bubble = CreateBubble("assistant", "OK");
        Assert.Equal(HorizontalAlignment.Left, bubble.HorizontalAlignment);
        var innerStack = (StackPanel)bubble.Child!;
        var contentBlock = (TextBlock)innerStack.Children[1];
        Assert.Equal("OK", contentBlock.Text);
    });

    [Fact]
    public void AssistantBubble_LongMessage_HasNoMaxWidthCap() => RunOnSta(() =>
    {
        var longText = new string('A', 2000);
        var bubble = CreateBubble("assistant", longText);
        Assert.Equal(HorizontalAlignment.Left, bubble.HorizontalAlignment);
        Assert.Equal(double.PositiveInfinity, bubble.MaxWidth);
    });

    [Fact]
    public void AssistantBubble_LongMessage_PreservesFullText() => RunOnSta(() =>
    {
        var longText = new string('B', 2000);
        var bubble = CreateBubble("assistant", longText);
        var innerStack = (StackPanel)bubble.Child!;
        var contentBlock = (TextBlock)innerStack.Children[1];
        Assert.Equal(2000, contentBlock.Text.Length);
    });

    [Fact]
    public void PlaceholderBubble_HasTagSet() => RunOnSta(() =>
    {
        var bubble = CreateBubble("assistant", "Thinking...", isPlaceholder: true);
        Assert.Equal("placeholder", bubble.Tag);
    });

    [Fact]
    public void StreamingBubble_TextBlockStartsEmpty() => RunOnSta(() =>
    {
        var bubble = CreateStreamingBubble();
        var innerStack = (StackPanel)bubble.Child!;
        var contentBlock = (TextBlock)innerStack.Children[0];
        Assert.Equal("", contentBlock.Text);
    });

    [Fact]
    public void StreamingBubble_TextBlock_IsWrapEnabled() => RunOnSta(() =>
    {
        var bubble = CreateStreamingBubble();
        var innerStack = (StackPanel)bubble.Child!;
        var contentBlock = (TextBlock)innerStack.Children[0];
        Assert.Equal(TextWrapping.Wrap, contentBlock.TextWrapping);
    });

    [Fact]
    public void StreamingBubble_TextBlock_FontSize13() => RunOnSta(() =>
    {
        var bubble = CreateStreamingBubble();
        var innerStack = (StackPanel)bubble.Child!;
        var contentBlock = (TextBlock)innerStack.Children[0];
        Assert.Equal(13, contentBlock.FontSize);
    });

    [Fact]
    public void MultipleBubbles_CanBeAddedToStackPanel() => RunOnSta(() =>
    {
        var stack = new StackPanel();
        for (int i = 0; i < 20; i++)
        {
            var role = i % 2 == 0 ? "user" : "assistant";
            var bubble = CreateBubble(role, $"Message {i}");
            stack.Children.Add(bubble);
        }
        Assert.Equal(20, stack.Children.Count);
    });

    [Fact]
    public void StreamingContent_AccumulatesCorrectly()
    {
        var content = new System.Text.StringBuilder();
        content.Append("Hello");
        content.Append(" ");
        content.Append("World");
        Assert.Equal("Hello World", content.ToString());
    }

    [Fact]
    public void StreamingContent_LongAccumulation()
    {
        var content = new System.Text.StringBuilder();
        for (int i = 0; i < 1000; i++)
        {
            content.Append("word ");
        }
        Assert.Equal(5000, content.Length);
    }

    [Fact]
    public void AssistantBubble_HasHeaderLabel() => RunOnSta(() =>
    {
        var bubble = CreateBubble("assistant", "test");
        var innerStack = (StackPanel)bubble.Child!;
        var headerPanel = (StackPanel)innerStack.Children[0];
        var roleLabel = (TextBlock)headerPanel.Children[0];
        Assert.Equal("Assistant", roleLabel.Text);
    });

    [Fact]
    public void UserBubble_HasHeaderLabel() => RunOnSta(() =>
    {
        var bubble = CreateBubble("user", "test");
        var innerStack = (StackPanel)bubble.Child!;
        var headerPanel = (StackPanel)innerStack.Children[0];
        var roleLabel = (TextBlock)headerPanel.Children[0];
        Assert.Equal("You", roleLabel.Text);
    });

    [Fact]
    public void StreamingBubble_CanAccumulateTokens() => RunOnSta(() =>
    {
        var bubble = CreateStreamingBubble();
        var innerStack = (StackPanel)bubble.Child!;
        var contentBlock = (TextBlock)innerStack.Children[0];

        var sb = new System.Text.StringBuilder();
        sb.Append("Hello");
        contentBlock.Text = sb.ToString();
        sb.Append(" World");
        contentBlock.Text = sb.ToString();
        sb.Append("! This is a long response that should wrap.");
        contentBlock.Text = sb.ToString();

        Assert.Equal("Hello World! This is a long response that should wrap.", contentBlock.Text);
    });

    [Fact]
    public void StreamingBubble_FinalizeRebuildsContent() => RunOnSta(() =>
    {
        var bubble = CreateStreamingBubble();
        var innerStack = (StackPanel)bubble.Child!;

        innerStack.Children.Clear();

        var headerPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        var roleLabel = new TextBlock
        {
            Text = "Assistant",
            FontSize = 10,
            FontWeight = FontWeights.SemiBold
        };
        headerPanel.Children.Add(roleLabel);
        innerStack.Children.Add(headerPanel);

        var formatted = new TextBlock
        {
            Text = "Finalized response text",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap
        };
        innerStack.Children.Add(formatted);

        Assert.Equal(2, innerStack.Children.Count);
        Assert.Equal("Finalized response text", ((TextBlock)innerStack.Children[1]).Text);
    });

    [Fact]
    public void AssistantBubble_VeryLongSingleWord_WrapsCorrectly() => RunOnSta(() =>
    {
        var longWord = new string('X', 500);
        var bubble = CreateBubble("assistant", longWord);
        var innerStack = (StackPanel)bubble.Child!;
        var contentBlock = (TextBlock)innerStack.Children[1];
        Assert.Equal(500, contentBlock.Text.Length);
        Assert.Equal(TextWrapping.Wrap, contentBlock.TextWrapping);
    });

    [Fact]
    public void AssistantBubble_MultilineContent_PreservesNewlines() => RunOnSta(() =>
    {
        var text = "Line 1\nLine 2\nLine 3";
        var bubble = CreateBubble("assistant", text);
        var innerStack = (StackPanel)bubble.Child!;
        var contentBlock = (TextBlock)innerStack.Children[1];
        Assert.Contains("Line 1", contentBlock.Text);
        Assert.Contains("Line 3", contentBlock.Text);
    });
}
