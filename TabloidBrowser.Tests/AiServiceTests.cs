using Xunit;

namespace TabloidBrowser.Tests;

public class AiServiceTests
{
    [Fact]
    public void IsOpenAICompatible_NimProvider_IsTrue()
    {
        using var svc = new AiService { Provider = "NVIDIA NIM", Endpoint = "https://integrate.api.nvidia.com/v1" };
        Assert.True(svc.IsOpenAICompatible);
    }

    [Fact]
    public void IsOpenAICompatible_OllamaCloud_IsFalse()
    {
        using var svc = new AiService { Provider = "Ollama Cloud", Endpoint = "https://ollama.com" };
        Assert.False(svc.IsOpenAICompatible);
    }

    [Fact]
    public void IsOpenAICompatible_V1Endpoint_IsTrue()
    {
        using var svc = new AiService { Provider = "Custom", Endpoint = "http://localhost:8000/v1" };
        Assert.True(svc.IsOpenAICompatible);
    }
}