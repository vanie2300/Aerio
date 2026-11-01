using TabloidBrowser;
using Xunit;

namespace TabloidBrowser.Tests;

public class TrackerPolicyTests
{
    [Theory]
    [InlineData("example.com", "example.com", true)]
    [InlineData("www.example.com", "example.com", true)]
    [InlineData("EXAMPLE.com", "example.COM", true)]
    [InlineData("notexample.com", "example.com", false)]
    [InlineData("example.org", "example.com", false)]
    public void DomainMatches_MatchesExactAndSubdomains(string host, string domain, bool expected)
    {
        Assert.Equal(expected, TrackerPolicy.DomainMatches(host, domain));
    }

    [Theory]
    [InlineData("ads.example.com", "*.example.com", true)]
    [InlineData("example.com", "*.example.com", false)]
    [InlineData("tracker.example.net", "*.example.com", false)]
    public void DomainMatches_SupportsLeadingWildcards(string host, string domain, bool expected)
    {
        Assert.Equal(expected, TrackerPolicy.DomainMatches(host, domain));
    }

    [Fact]
    public void DomainMatches_TrimLeadingDot()
    {
        Assert.True(TrackerPolicy.DomainMatches("www.example.com", ".example.com"));
        Assert.True(TrackerPolicy.DomainMatches("example.com", ".example.com"));
    }

    [Fact]
    public void GetHost_ReturnsHostOrNullOrNullOnParseFailure()
    {
        Assert.Equal("example.com", TrackerPolicy.GetHost("https://example.com/page?q=1"));
        Assert.Null(TrackerPolicy.GetHost("not a uri"));
        Assert.Null(TrackerPolicy.GetHost(null));
    }

    [Fact]
    public void IsSiteAllowed_IgnoresBlankHostsAndUsesDomainMatching()
    {
        var allowlist = new List<string> { "example.com" };

        Assert.True(TrackerPolicy.IsSiteAllowed("https://example.com/x", allowlist));
        Assert.True(TrackerPolicy.IsSiteAllowed("https://cdn.example.com/x", allowlist));
        Assert.False(TrackerPolicy.IsSiteAllowed("https://other.com/x", allowlist));
        Assert.False(TrackerPolicy.IsSiteAllowed("garbage", allowlist));
    }

    [Fact]
    public void IsBlockedTracker_MatchesBlocklistDomains()
    {
        var blocklist = new List<string> { "doubleclick.net" };

        Assert.True(TrackerPolicy.IsBlockedTracker("https://ad.doubleclick.net/pixel", blocklist));
        Assert.False(TrackerPolicy.IsBlockedTracker("https://example.com/pixel", blocklist));
        Assert.False(TrackerPolicy.IsBlockedTracker("junk", blocklist));
    }

    [Theory]
    [InlineData(".exe", true)]
    [InlineData(".MSI", true)]
    [InlineData(".png", false)]
    [InlineData("", false)]
    public void IsDangerousDownloadExtension_ClassifiesExtensions(string extension, bool expected)
    {
        Assert.Equal(expected, TrackerPolicy.IsDangerousDownloadExtension(extension));
    }
}
