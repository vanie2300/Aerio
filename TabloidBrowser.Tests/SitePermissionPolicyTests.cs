using TabloidBrowser;
using Xunit;

namespace TabloidBrowser.Tests;

public class SitePermissionPolicyTests
{
    // --- NormalizeOrigin: scheme + canonical host + effective port ---

    [Theory]
    [InlineData("https://Example.com:443/page", "https://example.com")]
    [InlineData("http://example.com:80/a", "http://example.com")]
    [InlineData("https://example.com:8443/a", "https://example.com:8443")]
    [InlineData("https://example.com", "https://example.com")]
    [InlineData("  https://example.com/x?y=1#frag  ", "https://example.com")]
    [InlineData("https://[::1]:8080/x", "https://[::1]:8080")]
    public void NormalizeOrigin_UsesSchemeHostAndPort(string value, string expected)
    {
        Assert.Equal(expected, SitePermissionPolicy.NormalizeOrigin(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("example.com")] // bare host: not an origin
    [InlineData("ftp://example.com")]
    [InlineData("file:///C:/page.html")]
    [InlineData("data:text/html,hello")]
    [InlineData("javascript:alert(1)")]
    [InlineData("about:blank")]
    [InlineData("https://user:pass@example.com/")] // credentials: rejected
    [InlineData("https://")] // scheme-only junk must not become a host
    public void NormalizeOrigin_RejectsNonOrigins(string? input)
    {
        Assert.Null(SitePermissionPolicy.NormalizeOrigin(input));
    }

    // --- StateFor: exact-origin matching, no cross-origin bleed ---

    [Fact]
    public void CameraGrant_DoesNotBleedAcrossOrigins()
    {
        var rules = new[]
        {
            new SitePermissionRule("https://example.com", "Camera", "Allow")
        };

        Assert.Equal("Allow", SitePermissionPolicy.StateFor(rules, "https://example.com/page", "Camera"));
        Assert.Equal("Default", SitePermissionPolicy.StateFor(rules, "http://example.com", "Camera"));
        Assert.Equal("Default", SitePermissionPolicy.StateFor(rules, "https://example.com:8443", "Camera"));
        Assert.Equal("Default", SitePermissionPolicy.StateFor(rules, "https://sub.example.com", "Camera"));
    }

    [Fact]
    public void StateFor_NeverMatchesLookalikeHosts()
    {
        var rules = new[]
        {
            new SitePermissionRule("https://example.com", "Camera", "Allow")
        };

        Assert.Equal("Default", SitePermissionPolicy.StateFor(rules, "https://evilexample.com/", "Camera"));
        Assert.Equal("Default", SitePermissionPolicy.StateFor(rules, "https://example.com.evil.com/", "Camera"));
    }

    [Fact]
    public void StateFor_SharesPermissionAcrossPathsOfSameOrigin()
    {
        var rules = new[]
        {
            new SitePermissionRule("https://example.com", "Camera", "Deny")
        };

        Assert.Equal("Deny", SitePermissionPolicy.StateFor(rules, "https://example.com/a/b/c?x=1", "Camera"));
    }

    [Fact]
    public void StateFor_PermissionsDoNotBleedAcrossKinds()
    {
        var rules = new[]
        {
            new SitePermissionRule("https://example.com", "Camera", "Allow")
        };

        Assert.Equal("Default", SitePermissionPolicy.StateFor(rules, "https://example.com/", "Microphone"));
    }

    [Fact]
    public void StateFor_NonHttpSchemes_NeverInheritGrants()
    {
        var rules = new[]
        {
            new SitePermissionRule("https://example.com", "Camera", "Allow")
        };

        Assert.Equal("Default", SitePermissionPolicy.StateFor(rules, "file:///page", "Camera"));
        Assert.Equal("Default", SitePermissionPolicy.StateFor(rules, "about:blank", "Camera"));
        Assert.Equal("Default", SitePermissionPolicy.StateFor(rules, "javascript:x", "Camera"));
        Assert.Equal("Default", SitePermissionPolicy.StateFor(rules, null, "Camera"));
    }

    [Fact]
    public void StateFor_Ipv6OriginsAreExact()
    {
        var rules = new[]
        {
            new SitePermissionRule("https://[::1]:8080", "Camera", "Allow")
        };

        Assert.Equal("Allow", SitePermissionPolicy.StateFor(rules, "https://[::1]:8080/x", "Camera"));
        Assert.Equal("Default", SitePermissionPolicy.StateFor(rules, "https://[::1]/", "Camera"));
    }

    // --- NormalizeRules: validation + legacy hostname migration ---

    [Fact]
    public void NormalizeRules_MigratesLegacyHostnameRulesToHttpsOriginOnly()
    {
        var rules = new[]
        {
            new SitePermissionRule("example.com", "Camera", "Allow")
        };

        var normalized = SitePermissionPolicy.NormalizeRules(rules);

        var rule = Assert.Single(normalized);
        Assert.Equal("https://example.com", rule.Host);
        // Idempotent: a second pass makes no further changes.
        var second = SitePermissionPolicy.NormalizeRules(normalized);
        Assert.Equal(normalized, second);
    }

    [Fact]
    public void NormalizeRules_MigratesLegacyDenyConservatively()
    {
        var rules = new[]
        {
            new SitePermissionRule("blocked.example", "Location", "Deny")
        };

        var normalized = SitePermissionPolicy.NormalizeRules(rules);

        var rule = Assert.Single(normalized);
        Assert.Equal("https://blocked.example", rule.Host);
        Assert.Equal("Deny", rule.State);
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
            new SitePermissionRule("keep.com", "camera", "Allow") // duplicate after normalization
        };

        var normalized = SitePermissionPolicy.NormalizeRules(rules).OrderBy(r => r.Host).ToArray();

        Assert.Equal(2, normalized.Length);
        Assert.Equal("https://blockme.com", normalized[0].Host);
        Assert.Equal("Deny", normalized[0].State); // Block normalizes to Deny and is kept
        Assert.Equal("https://keep.com", normalized[1].Host);
        Assert.Equal("camera", normalized[1].Permission);
        Assert.Equal("Allow", normalized[1].State);
    }

    [Fact]
    public void NormalizeRules_MigratedLegacyRule_MergesWithExistingHttpsRule()
    {
        var rules = new[]
        {
            new SitePermissionRule("https://example.com", "Camera", "Deny"),
            new SitePermissionRule("example.com", "Camera", "Allow") // legacy duplicate
        };

        var normalized = SitePermissionPolicy.NormalizeRules(rules);

        // Distinct keeps the first occurrence: the explicit origin rule wins.
        var rule = Assert.Single(normalized);
        Assert.Equal("https://example.com", rule.Host);
        Assert.Equal("Deny", rule.State);
    }

    // --- helpers ---

    [Fact]
    public void OriginFromUserInput_ResolvesBareHostsToHttpsOrigin()
    {
        Assert.Equal("https://example.com", SitePermissionPolicy.OriginFromUserInput("example.com"));
        Assert.Equal("https://example.com:8443", SitePermissionPolicy.OriginFromUserInput("https://example.com:8443/x"));
        Assert.Null(SitePermissionPolicy.OriginFromUserInput("bad host"));
        Assert.Null(SitePermissionPolicy.OriginFromUserInput(null));
    }

    [Fact]
    public void NormalizeHost_StripsSchemePortPathAndCredentials()
    {
        Assert.Equal("example.com", SitePermissionPolicy.NormalizeHost("https://user:pass@example.com:8443/path"));
        Assert.Equal("example.com", SitePermissionPolicy.NormalizeHost("  Example.COM  "));
        Assert.Null(SitePermissionPolicy.NormalizeHost("bad host name"));
        Assert.Null(SitePermissionPolicy.NormalizeHost(null));
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
