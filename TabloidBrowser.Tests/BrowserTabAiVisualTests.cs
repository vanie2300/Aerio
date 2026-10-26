using System.Windows.Media;
using Xunit;

namespace TabloidBrowser.Tests;

public class BrowserTabAiVisualTests
{
    [Fact]
    public void IsAiOwned_True_WhenOwnerIsAI()
    {
        var tab = new BrowserTab { Owner = TabOwner.AI };
        Assert.True(tab.IsAiOwned);
    }

    [Fact]
    public void IsAiOwned_False_WhenOwnerIsUser()
    {
        var tab = new BrowserTab { Owner = TabOwner.User };
        Assert.False(tab.IsAiOwned);
    }

    [Fact]
    public void AccentBrush_ReturnsCyan_ForAiOwnedTab()
    {
        var tab = new BrowserTab { Owner = TabOwner.AI };
        var brush = tab.AccentBrush;
        Assert.IsType<SolidColorBrush>(brush);
        var color = ((SolidColorBrush)brush).Color;
        Assert.Equal(Color.FromRgb(0, 180, 220), color);
    }

    [Fact]
    public void AccentBrush_ReturnsDifferentColor_ForUserTab()
    {
        var userTab = new BrowserTab { Owner = TabOwner.User, IsActive = true };
        var aiTab = new BrowserTab { Owner = TabOwner.AI };
        var userBrush = userTab.AccentBrush;
        var aiBrush = aiTab.AccentBrush;
        Assert.NotEqual(
            ((SolidColorBrush)userBrush).Color,
            ((SolidColorBrush)aiBrush).Color);
    }

    [Fact]
    public void AccentBrush_CachesValue()
    {
        var tab = new BrowserTab { Owner = TabOwner.AI };
        var first = tab.AccentBrush;
        var second = tab.AccentBrush;
        Assert.Same(first, second);
    }

    [Fact]
    public void AccentBrush_InvalidatesCache_WhenOwnerChanges()
    {
        var tab = new BrowserTab { Owner = TabOwner.User, IsActive = true };
        var first = tab.AccentBrush;
        tab.Owner = TabOwner.AI;
        var second = tab.AccentBrush;
        Assert.NotSame(first, second);
        Assert.Equal(Color.FromRgb(0, 180, 220), ((SolidColorBrush)second).Color);
    }

    [Fact]
    public void PageVersion_DefaultsTo1()
    {
        var tab = new BrowserTab();
        Assert.Equal(1, tab.PageVersion);
    }

    [Fact]
    public void PageVersion_CanBeIncremented()
    {
        var tab = new BrowserTab();
        tab.PageVersion++;
        tab.PageVersion++;
        Assert.Equal(3, tab.PageVersion);
    }

    [Fact]
    public void Owner_Change_InvalidateIsAiOwned()
    {
        var tab = new BrowserTab { Owner = TabOwner.AI };
        Assert.True(tab.IsAiOwned);
        tab.Owner = TabOwner.User;
        Assert.False(tab.IsAiOwned);
    }
}
