using Xunit;

namespace TabloidBrowser.Tests;

public class AiAuthorizerTests
{
    private static AiAuthorizer CreateAuthorizer(
        bool allowNavigateActive = false,
        BrowserTab? activeTab = null)
    {
        var policy = new AiBrowserPolicy(AllowNavigateActiveTab: allowNavigateActive);
        return new AiAuthorizer(policy, () => activeTab);
    }

    [Fact]
    public void Authorize_AllowsReadTools()
    {
        var auth = CreateAuthorizer();
        var result = auth.Authorize("web_search", null, requiresWrite: false);
        Assert.True(result.IsAllowed);
        Assert.Null(result.DenialReason);
    }

    [Fact]
    public void Authorize_DeniesWriteWithNullTarget()
    {
        var auth = CreateAuthorizer();
        var result = auth.Authorize("navigate_to", null, requiresWrite: true);
        Assert.False(result.IsAllowed);
        Assert.Contains("No target tab", result.DenialReason!);
    }

    [Fact]
    public void Authorize_DeniesWriteToActiveUserTab()
    {
        var tab = new BrowserTab { Owner = TabOwner.User };
        var auth = CreateAuthorizer(activeTab: tab);
        var result = auth.Authorize("navigate_to", tab, requiresWrite: true);
        Assert.False(result.IsAllowed);
        Assert.Contains("active tab", result.DenialReason!);
    }

    [Fact]
    public void Authorize_AllowsWriteToActiveTab_WhenPolicyPermits()
    {
        var tab = new BrowserTab { Owner = TabOwner.User };
        var auth = CreateAuthorizer(allowNavigateActive: true, activeTab: tab);
        var result = auth.Authorize("navigate_to", tab, requiresWrite: true);
        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void Authorize_AllowsWriteToActiveAiOwnedTab()
    {
        var aiTab = new BrowserTab { Owner = TabOwner.AI };
        var auth = CreateAuthorizer(activeTab: aiTab);
        var result = auth.Authorize("navigate_to", aiTab, requiresWrite: true);
        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void Authorize_DeniesWriteToNonActiveUserTab()
    {
        var activeTab = new BrowserTab { Owner = TabOwner.User };
        var targetTab = new BrowserTab { Owner = TabOwner.User };
        var auth = CreateAuthorizer(activeTab: activeTab);
        var result = auth.Authorize("navigate_to", targetTab, requiresWrite: true);
        Assert.False(result.IsAllowed);
        Assert.Contains("user", result.DenialReason!);
    }

    [Fact]
    public void Authorize_AllowsWriteToAiOwnedTab()
    {
        var activeTab = new BrowserTab { Owner = TabOwner.User };
        var aiTab = new BrowserTab { Owner = TabOwner.AI };
        var auth = CreateAuthorizer(activeTab: activeTab);
        var result = auth.Authorize("navigate_to", aiTab, requiresWrite: true);
        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void Authorize_AllowsOpenTab_WithoutWriteAuth()
    {
        var auth = CreateAuthorizer();
        var result = auth.Authorize("open_tab", null, requiresWrite: false);
        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void Authorize_AllowsAiTabWrite_WhenAnotherAiTabIsActive()
    {
        var activeAiTab = new BrowserTab { Owner = TabOwner.AI };
        var targetAiTab = new BrowserTab { Owner = TabOwner.AI };
        var auth = CreateAuthorizer(activeTab: activeAiTab);
        var result = auth.Authorize("click", targetAiTab, requiresWrite: true);
        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void Authorize_DeniesUserTabWrite_WhenAiTabIsActive()
    {
        var activeAiTab = new BrowserTab { Owner = TabOwner.AI };
        var targetUserTab = new BrowserTab { Owner = TabOwner.User };
        var auth = CreateAuthorizer(activeTab: activeAiTab);
        var result = auth.Authorize("click", targetUserTab, requiresWrite: true);
        Assert.False(result.IsAllowed);
        Assert.False(string.IsNullOrWhiteSpace(result.DenialReason));
    }

    [Fact]
    public void Authorize_DenialsAlwaysCarryAReason()
    {
        var userTab = new BrowserTab { Owner = TabOwner.User };
        var otherUserTab = new BrowserTab { Owner = TabOwner.User };
        var auth = CreateAuthorizer(activeTab: userTab);

        Assert.False(string.IsNullOrWhiteSpace(auth.Authorize("navigate_to", null, true).DenialReason));
        Assert.False(string.IsNullOrWhiteSpace(auth.Authorize("navigate_to", userTab, true).DenialReason));
        Assert.False(string.IsNullOrWhiteSpace(auth.Authorize("navigate_to", otherUserTab, true).DenialReason));
    }
}
