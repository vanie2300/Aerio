using Xunit;
using System.Text.Json;
using TabloidBrowser;

namespace TabloidBrowser.Tests;

public class BrowserDataStoreTests : IDisposable
{
    private readonly string _tempDir;

    public BrowserDataStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"TabloidBrowserTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void BrowserSettings_Default_HasCorrectValues()
    {
        var defaults = BrowserSettings.Default;
        Assert.Equal("DuckDuckGo", defaults.SearchEngine);
        Assert.Equal("Purple", defaults.Accent);
        Assert.Equal("Dark", defaults.Theme);
        Assert.False(defaults.AiEnabled);
        Assert.Equal("Ollama Cloud", defaults.AiProvider);
        Assert.Equal(2048, defaults.AiMaxTokens);
        Assert.Equal(0.7, defaults.AiTemperature);
        Assert.True(defaults.EnableAdBlock);
        Assert.True(defaults.BlockTrackers);
    }

    [Fact]
    public void BrowserSettings_AiDefaults_HaveCorrectValues()
    {
        var defaults = BrowserSettings.Default;
        Assert.Equal("DuckDuckGo", defaults.AiSearchBackend);
        Assert.Equal("", defaults.AiSearchEndpoint);
        Assert.Equal(10, defaults.AiMaxIterations);
        Assert.True(defaults.AiPageContextConfirmation);
        Assert.Null(defaults.AiToolsEnabled);
    }

    [Fact]
    public void BrowserSettings_DefaultShortcuts_ContainsExpectedKeys()
    {
        var shortcuts = BrowserSettings.DefaultShortcuts;
        Assert.True(shortcuts.ContainsKey("new_tab"));
        Assert.True(shortcuts.ContainsKey("close_tab"));
        Assert.True(shortcuts.ContainsKey("focus_address"));
        Assert.True(shortcuts.ContainsKey("ai_toggle"));
        Assert.Equal("Ctrl+T", shortcuts["new_tab"]);
        Assert.Equal("Ctrl+Shift+A", shortcuts["ai_toggle"]);
    }

    [Fact]
    public void BrowserSettings_AiProviderPresets_ContainsExpectedProviders()
    {
        var presets = BrowserSettings.AiProviderPresets;
        Assert.Equal(10, presets.Count);
        Assert.True(presets.ContainsKey("Ollama Cloud"));
        Assert.Equal("https://ollama.com", presets["Ollama Cloud"]);
        Assert.Equal("http://localhost:11434", presets["Ollama (local)"]);
        Assert.True(presets.ContainsKey("NVIDIA NIM"));
        Assert.Equal("https://integrate.api.nvidia.com/v1", presets["NVIDIA NIM"]);
        Assert.Equal("https://api.openai.com/v1", presets["OpenAI"]);
        Assert.Equal("https://openrouter.ai/api/v1", presets["OpenRouter"]);
        Assert.Equal("https://api.groq.com/openai/v1", presets["Groq"]);
        Assert.Equal("https://api.deepseek.com/v1", presets["DeepSeek"]);
        Assert.Equal("https://api.together.xyz/v1", presets["Together AI"]);
        Assert.Equal("https://api.cerebras.ai/v1", presets["Cerebras"]);
        Assert.Equal("", presets["OpenAI-compatible (custom)"]);
    }

    [Fact]
    public void BrowserSettings_GetProviderEndpoint_ReturnsPreset()
    {
        Assert.Equal("https://ollama.com", BrowserSettings.GetProviderEndpoint("Ollama Cloud"));
        Assert.Equal("https://integrate.api.nvidia.com/v1", BrowserSettings.GetProviderEndpoint("NVIDIA NIM"));
        Assert.Equal("http://localhost:11434", BrowserSettings.GetProviderEndpoint("Ollama (local)"));
        Assert.Equal("", BrowserSettings.GetProviderEndpoint("Unknown"));
    }

    [Fact]
    public void BrowserSettings_GetApiKey_DoesNotReuseAnotherProvidersKey()
    {
        var settings = BrowserSettings.Default with
        {
            AiProvider = "OpenAI",
            AiApiKey = "openai-key",
            AiApiKeys = new Dictionary<string, string> { ["OpenAI"] = "openai-key" }
        };

        Assert.Equal("openai-key", BrowserSettings.GetApiKey(settings, "OpenAI"));
        Assert.Equal("", BrowserSettings.GetApiKey(settings, "Ollama (local)"));
    }

    [Fact]
    public void BrowserSettings_GetShortcut_ReturnsDefault()
    {
        Assert.Equal("Ctrl+T", BrowserSettings.GetShortcut(null, "new_tab"));
        Assert.Equal("Ctrl+T", BrowserSettings.GetShortcut(new Dictionary<string, string>(), "new_tab"));
    }

    [Fact]
    public void BrowserSettings_GetShortcut_ReturnsOverride()
    {
        var overrides = new Dictionary<string, string> { ["new_tab"] = "Ctrl+Shift+T" };
        Assert.Equal("Ctrl+Shift+T", BrowserSettings.GetShortcut(overrides, "new_tab"));
    }

    [Fact]
    public void BrowserSettings_RecordEquality_Works()
    {
        var a = BrowserSettings.Default;
        var b = BrowserSettings.Default;
        Assert.Equal(a, b);
    }

    [Fact]
    public void BrowserSettings_WithExpression_CreatesNewInstance()
    {
        var original = BrowserSettings.Default;
        var modified = original with { AiEnabled = true };
        Assert.False(original.AiEnabled);
        Assert.True(modified.AiEnabled);
    }

    [Fact]
    public void BrowserSettings_DefaultTrackerBlocklist_ContainsExpectedDomains()
    {
        var blocklist = BrowserSettings.DefaultTrackerBlocklist;
        Assert.Contains("google-analytics.com", blocklist);
        Assert.Contains("doubleclick.net", blocklist);
        Assert.Contains("facebook.net", blocklist);
        Assert.True(blocklist.Count >= 10);
    }

    [Fact]
    public void BookmarkItem_Record_Works()
    {
        var item = new BookmarkItem("Test", "https://example.com", DateTimeOffset.Now);
        Assert.Equal("Test", item.Title);
        Assert.Equal("https://example.com", item.Url);
    }

    [Fact]
    public void HistoryItem_Record_Works()
    {
        var item = new HistoryItem("Test", "https://example.com", DateTimeOffset.Now);
        Assert.Equal("Test", item.Title);
        Assert.Equal("https://example.com", item.Url);
    }

    [Fact]
    public void DownloadItem_Record_Works()
    {
        var item = new DownloadItem("test.pdf", "/tmp/test.pdf", "https://example.com/test.pdf", DateTimeOffset.Now);
        Assert.Equal("test.pdf", item.FileName);
        Assert.Equal("https://example.com/test.pdf", item.SourceUrl);
    }

    [Fact]
    public void BrowserSession_Empty_ReturnsEmptySession()
    {
        var session = BrowserSession.Empty;
        Assert.Empty(session.Tabs);
        Assert.Equal(0, session.ActiveIndex);
    }

    [Fact]
    public void BrowserSettings_LastSeenVersion_DefaultsToEmpty()
    {
        Assert.Equal("", BrowserSettings.Default.LastSeenVersion);
    }
}
