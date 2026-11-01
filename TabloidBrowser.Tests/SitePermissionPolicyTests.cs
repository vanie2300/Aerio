using TabloidBrowser;
using Xunit;

namespace TabloidBrowser.Tests;

public class SitePermissionPolicyTests
{
    [Theory]
    [InlineData("https://example.com/page?x=1", "example.com")]
    [InlineData("http://EXAMPLE.com", "example.com")]
    [InlineData("https://example.com:8443/path", "example.com")]
    [InlineData("example.com", "example.com")]
    [InlineData("  Example.COM  ", "example.com")]
    [InlineData("https://user:pass@example.com/", "example.com")]
    public void NormalizeHost_StripsSchemePortPathAndCredentials(string input, string expected)
    {
        Assert.Equal(expected, SitePermissionPolicy.NormalizeHost(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad host name")]
    [InlineData("https://")]
    public void NormalizeHost_RejectsNonHosts(string? input)
    {
        Assert.Null(SitePermissionPolicy.NormalizeHost(input));
    }

    [Fact]
    public void StateFor_MatchesExactHostOnly()
    {
        var rules = new[]
        {
            new SitePermissionRule("example.com", "camera", "Allow")
        };

        Assert.Equal("Allow", SitePermissionPolicy.StateFor(rules, "https://example.com/page", "camera"));
        Assert.Equal("Default", SitePermissionPolicy.StateFor(rules, "https://evilexample.com/", "camera"));
        Assert.Equal("Default", SitePermissionPolicy.StateFor(rules, "https://example.com.evil.com/", "camera"));
        Assert.Equal("Default", SitePermissionPolicy.StateFor(rules, "https://sub.example.com/", "camera"));
        Assert.Equal("Default", SitePermissionPolicy.StateFor(rules, "https://other.com/", "camera"));
        Assert.Equal("Default", SitePermissionPolicy.StateFor(rules, "not a url", "camera"));
        Assert.Equal("Default", SitePermissionPolicy.StateFor(rules, null, "camera"));
    }

    [Fact]
    public void StateFor_PermissionsDoNotBleedAcrossKinds()
    {
        var rules = new[]
        {
            new SitePermissionRule("example.com", "camera", "Allow")
        };

        Assert.Equal("Default", SitePermissionPolicy.StateFor(rules, "https://example.com/", "microphone"));
    }

    [Fact]
    public void NormalizeRules_DropsInvalidEntriesAndNormalizesState()
    {
        var rules = new[]
        {
            new SitePermissionRule("https://Keep.com/", " camera ", "Allow"),
            new SitePermissionRule("bad host", "camera", "Allow"),
            new SitePermissionRule("", "camera", "Allow"),
            new SitePermissionRule("drop.com", "", "Allow"),
            new SitePermissionRule("blockme.com", "camera", "Block"),
            new SitePermissionRule("default.com", "camera", "Default"),
            new SitePermissionRule("keep.com", "camera", "Allow") // duplicate of the first after normalization
        };

        var normalized = SitePermissionPolicy.NormalizeRules(rules).OrderBy(r => r.Host).ToArray();

        Assert.Equal(2, normalized.Length);
        Assert.Equal("blockme.com", normalized[0].Host);
        Assert.Equal("Deny", normalized[0].State); // Block normalizes to Deny and is kept
        Assert.Equal("keep.com", normalized[1].Host);
        Assert.Equal("camera", normalized[1].Permission);
        Assert.Equal("Allow", normalized[1].State);
    }

    [Fact]
    public void NormalizeState_MapsBlockToDeny()
    {
        Assert.Equal("Allow", SitePermissionPolicy.NormalizeState("Allow"));
        Assert.Equal("Deny", SitePermissionPolicy.NormalizeState("Block"));
        Assert.Equal("Deny", SitePermissionPolicy.NormalizeState("Deny"));
        Assert.Equal("Default", SitePermissionPolicy.NormalizeState("nonsense"));
        Assert.Equal("Default", SitePermissionPolicy.NormalizeState(null));
    }

    [Fact]
    public void NormalizeRules_HandlesNullAndEmpty()
    {
        Assert.Empty(SitePermissionPolicy.NormalizeRules(null));
        Assert.Empty(SitePermissionPolicy.NormalizeRules([]));
    }
}
