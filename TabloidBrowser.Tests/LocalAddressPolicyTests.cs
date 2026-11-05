using Xunit;

namespace TabloidBrowser.Tests;

/// <summary>
/// The address bar must never force-upgrade http→https for local/private
/// network addresses: routers, printers, and intranet hosts commonly serve
/// plain HTTP with self-signed certificates.
/// </summary>
public class LocalAddressPolicyTests
{
    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("10.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("192.168.1.1")]
    [InlineData("172.16.0.5")]
    [InlineData("172.31.255.255")]
    [InlineData("169.254.10.20")]
    [InlineData("router.local")]
    [InlineData("Router.LOCAL")]
    [InlineData("nas")]
    public void IsLocalAddress_PrivateAndLocalHosts_ReturnTrue(string host)
    {
        Assert.True(MainWindow.IsLocalAddress(host));
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]
    [InlineData("11.0.0.1")]
    [InlineData("193.168.1.1")]
    public void IsLocalAddress_PublicHosts_ReturnFalse(string host)
    {
        Assert.False(MainWindow.IsLocalAddress(host));
    }
}
