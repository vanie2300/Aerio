using Xunit;

namespace TabloidBrowser.Tests;

public class AdBlockEngineTests
{
    [Fact]
    public void HostBlockRule_BlocksRequest()
    {
        var snapshot = AdBlockEngine.Compile(new[]
        {
            "||example-ads.com^"
        });

        Assert.True(snapshot.ShouldBlock("https://example-ads.com/some/ad.js"));
        Assert.True(snapshot.ShouldBlock("https://www.example-ads.com/some/ad.js"));
        Assert.False(snapshot.ShouldBlock("https://notexample-ads.com/ad.js"));
        Assert.False(snapshot.ShouldBlock("https://example.com/page"));
    }

    [Fact]
    public void ExceptionRule_OverridesBlockRule()
    {
        var snapshot = AdBlockEngine.Compile(new[]
        {
            "||example-ads.com^",
            "@@||allowed.example-ads.com^"
        });

        // The @@ whitelist wins even though a blocking rule matches.
        Assert.False(snapshot.ShouldBlock("https://allowed.example-ads.com/ad.js"));
        // Other hosts on the same ad domain remain blocked.
        Assert.True(snapshot.ShouldBlock("https://blocked.example-ads.com/ad.js"));
        // But the exception's own sub-path stays unblocked too.
        Assert.False(snapshot.ShouldBlock("https://allowed.example-ads.com/page?q=1"));
    }

    [Fact]
    public void DomainOptionRules_AreDropped_NotMisapplied()
    {
        // A $domain= rule is scoped to specific sites; dropping it must not
        // turn it into a block-everywhere rule.
        var snapshot = AdBlockEngine.Compile(new[]
        {
            "/banner.js$domain=example.com"
        });

        Assert.False(snapshot.ShouldBlock("https://any-other-site.com/banner.js"));
        Assert.False(snapshot.ShouldBlock("https://example.com/banner.js"));
    }

    [Fact]
    public void CommentAndCosmeticRules_AreIgnored()
    {
        var snapshot = AdBlockEngine.Compile(new[]
        {
            "! comment line",
            "[Adblock Plus 2.0]",
            "##.ad-banner",
            "#@#.promo",
            "",
            "   "
        });

        Assert.Equal(0, snapshot.RuleCount);
        Assert.False(snapshot.ShouldBlock("https://example.com/page"));
    }

    [Fact]
    public void BundledDomains_StillBlock()
    {
        var snapshot = AdBlockEngine.Compile(AdBlockEngineTestAccessor.BundledAdDomains);

        Assert.True(snapshot.ShouldBlock("https://ad.doubleclick.net/ddm/adj/x"));
        Assert.True(snapshot.ShouldBlock("https://www.googletagmanager.com/gtm.js"));
        // Main-frame exemptions are enforced by the caller (resource context),
        // so engine-level ShouldBlock on a non-ad page must stay false.
        Assert.False(snapshot.ShouldBlock("https://en.wikipedia.org/wiki/Main_Page"));
    }

    [Fact]
    public void CompiledEngine_ContainsRules()
    {
        // The real engine must always have rules loaded (bundled fallback at minimum).
        Assert.True(AdBlockEngine.Active.RuleCount > 100);
    }
}

public static class AdBlockEngineTestAccessor
{
    public static string[] BundledAdDomains =>
        (string[])typeof(AdBlockEngine)
            .GetField("BundledAdDomains", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null)!;
}
