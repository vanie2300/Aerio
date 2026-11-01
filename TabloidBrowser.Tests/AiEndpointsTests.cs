using TabloidBrowser;
using Xunit;

namespace TabloidBrowser.Tests;

public class AiEndpointsTests
{
    [Theory]
    [InlineData("https://ollama.com", true)]
    [InlineData("https://api.openai.com/v1", true)]
    [InlineData("https://localhost:11434", true)]
    [InlineData("http://localhost:11434", true)]
    [InlineData("http://127.0.0.1:8080", true)]
    [InlineData("http://[::1]:11434", true)]
    [InlineData("http://evil.com", false)]
    [InlineData("http://192.168.1.10:11434", false)]
    [InlineData("http://api.example.com/v1", false)]
    [InlineData("http://localhost.evil.example", false)] // lookalike hostname is not loopback
    [InlineData("ftp://api.example.com", false)]
    [InlineData("ollama.com", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("   ", false)]
    public void IsSecure_AllowsHttpsAndLoopbackHttpOnly(string? endpoint, bool expected)
    {
        Assert.Equal(expected, AiEndpoints.IsSecure(endpoint));
    }

    [Fact]
    public void EnsureSecure_ReturnsTrimmedEndpoint()
    {
        Assert.Equal("https://ollama.com", AiEndpoints.EnsureSecure("  https://ollama.com  "));
    }

    [Fact]
    public void EnsureSecure_ThrowsForMissingEndpoint()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => AiEndpoints.EnsureSecure("  "));
        Assert.Contains("Settings", ex.Message);
    }

    [Fact]
    public void EnsureSecure_ThrowsForInsecureRemoteEndpoint()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => AiEndpoints.EnsureSecure("http://evil.com/api"));
        Assert.Contains("HTTPS", ex.Message);
        Assert.Contains("never sent over plain HTTP", ex.Message);
    }

    [Fact]
    public void EnsureSecure_AllowsLocalOllama()
    {
        Assert.Equal("http://localhost:11434", AiEndpoints.EnsureSecure("http://localhost:11434"));
    }

    [Fact]
    public void CreateSecureHttpClient_DisablesAutoRedirects()
    {
        // A 3xx from an HTTPS endpoint must never carry the Authorization
        // header to a downgraded scheme or a different origin.
        using var client = AiEndpoints.CreateSecureHttpClient();
        var handlerField = typeof(HttpMessageInvoker).GetField(
            "_handler",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var handler = Assert.IsAssignableFrom<HttpClientHandler>(handlerField?.GetValue(client));
        Assert.False(handler.AllowAutoRedirect);
    }
}
