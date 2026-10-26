using Xunit;

namespace TabloidBrowser.Tests;

public class AiBrowserPolicyTests
{
    [Fact]
    public void AiBrowserPolicy_Defaults_AreCorrect()
    {
        var policy = new AiBrowserPolicy();
        Assert.Equal(3, policy.MaxOpenTabs);
        Assert.False(policy.AllowNavigateActiveTab);
        Assert.False(policy.CleanupOnTaskComplete);
    }

    [Fact]
    public void AiBrowserPolicy_CanBeCustomized()
    {
        var policy = new AiBrowserPolicy(
            MaxOpenTabs: 5,
            AllowNavigateActiveTab: true,
            CleanupOnTaskComplete: false);
        Assert.Equal(5, policy.MaxOpenTabs);
        Assert.True(policy.AllowNavigateActiveTab);
        Assert.False(policy.CleanupOnTaskComplete);
    }

    [Fact]
    public void BrowserSettings_Default_IncludesAiPolicy()
    {
        var defaults = BrowserSettings.Default;
        Assert.NotNull(defaults.AiPolicy);
        Assert.Equal(3, defaults.AiPolicy!.MaxOpenTabs);
    }
}
