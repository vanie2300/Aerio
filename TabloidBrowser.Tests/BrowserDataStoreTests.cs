using Xunit;
using System.Security.Cryptography;
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

    [Fact]
    public void BrowserDataStore_HistoryAndDownloads_PersistAcrossInstances()
    {
        using (var store = new BrowserDataStore(_tempDir))
        {
            Assert.True(store.AddHistory("Example", "https://example.com"));
            Assert.True(store.AddDownload(
                "example.pdf",
                Path.Combine(_tempDir, "example.pdf"),
                "https://example.com/example.pdf"));
        }

        using var reloadedStore = new BrowserDataStore(_tempDir);
        Assert.Single(reloadedStore.History);
        Assert.Equal("https://example.com", reloadedStore.History[0].Url);
        Assert.Single(reloadedStore.Downloads);
        Assert.Equal("example.pdf", reloadedStore.Downloads[0].FileName);
    }

    [Fact]
    public void BrowserDataStore_HistoryAdd_IsPersistedByFlush()
    {
        using (var store = new BrowserDataStore(_tempDir))
        {
            Assert.True(store.AddHistory("Example", "https://example.com"));
            // The debounce means the file may not exist yet — the flush persists it.
            Assert.True(store.FlushPendingSaves());
        }

        using var reloadedStore = new BrowserDataStore(_tempDir);
        Assert.Single(reloadedStore.History);
        Assert.Equal("https://example.com", reloadedStore.History[0].Url);
    }

    [Fact]
    public void BrowserDataStore_FailedHistoryWrite_KeepsMemoryAndReportsFlushFailure()
    {
        using var store = new BrowserDataStore(_tempDir);
        Assert.True(store.AddHistory("Existing", "https://existing.example"));
        File.Delete(Path.Combine(_tempDir, "history.json"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "history.json"));

        // Hot-path history adds are recorded in memory and persisted on a
        // debounce; a broken destination surfaces as a failed flush instead of
        // silently discarding the entry.
        Assert.True(store.AddHistory("New", "https://new.example"));
        Assert.False(store.FlushPendingSaves());
        Assert.Equal(2, store.History.Count);
        Assert.Empty(Directory.GetFiles(_tempDir, "*.tmp"));
    }

    [Fact]
    public void BrowserDataStore_FailedClear_RestoresHistoryAndDownloads()
    {
        using var store = new BrowserDataStore(_tempDir);
        Assert.True(store.AddHistory("Existing", "https://existing.example"));
        Assert.True(store.AddDownload(
            "existing.pdf",
            Path.Combine(_tempDir, "existing.pdf"),
            "https://example.com/existing.pdf"));
        var history = store.History.ToArray();
        var downloads = store.Downloads.ToArray();

        File.Delete(Path.Combine(_tempDir, "history.json"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "history.json"));
        Assert.False(store.ClearHistory());
        Assert.Equal(history, store.History);

        File.Delete(Path.Combine(_tempDir, "downloads.json"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "downloads.json"));
        Assert.False(store.ClearDownloads());
        Assert.Equal(downloads, store.Downloads);
    }

    [Fact]
    public void BrowserDataStore_ClearBrowsingState_PersistsClearedState()
    {
        using (var store = new BrowserDataStore(_tempDir))
        {
            Assert.True(store.AddHistory("Example", "https://example.com"));
            Assert.True(store.AddDownload(
                "example.pdf",
                Path.Combine(_tempDir, "example.pdf"),
                "https://example.com/example.pdf"));
            Assert.True(store.SaveSession(new BrowserSession(
                [new SessionTab("Example", "https://example.com")],
                0)));

            Assert.True(store.ClearBrowsingState());
        }

        using var reloadedStore = new BrowserDataStore(_tempDir);
        Assert.Empty(reloadedStore.History);
        Assert.Empty(reloadedStore.Downloads);
        Assert.Empty(reloadedStore.LoadSession().Tabs);
    }

    [Fact]
    public void BrowserDataStore_FailedClearBrowsingState_RestoresMemoryAndSession()
    {
        using var store = new BrowserDataStore(_tempDir);
        Assert.True(store.AddHistory("Existing", "https://existing.example"));
        Assert.True(store.AddDownload(
            "existing.pdf",
            Path.Combine(_tempDir, "existing.pdf"),
            "https://example.com/existing.pdf"));
        Assert.True(store.SaveSession(new BrowserSession(
            [new SessionTab("Existing", "https://existing.example")],
            0)));
        var history = store.History.ToArray();
        var downloads = store.Downloads.ToArray();

        File.Delete(Path.Combine(_tempDir, "downloads.json"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "downloads.json"));

        Assert.False(store.ClearBrowsingState());

        Assert.Equal(history, store.History);
        Assert.Equal(downloads, store.Downloads);
        Assert.Single(store.LoadSession().Tabs);
    }

    [Fact]
    public void BrowserDataStore_SessionRoundtrip_PersistsTabsScrollAndActiveIndex()
    {
        var session = new BrowserSession(
        [
            new SessionTab("First", "https://first.example", 12.5, 340),
            new SessionTab("Second", "https://second.example", 0, 99)
        ], 1);

        using (var store = new BrowserDataStore(_tempDir))
        {
            Assert.True(store.SaveSession(session));
        }

        using var reloadedStore = new BrowserDataStore(_tempDir);
        var restored = reloadedStore.LoadSession();
        Assert.Equal(2, restored.Tabs.Count);
        Assert.Equal(1, restored.ActiveIndex);
        Assert.Equal("Second", restored.Tabs[1].Title);
        Assert.Equal("https://second.example", restored.Tabs[1].Url);
        Assert.Equal(340, restored.Tabs[0].ScrollY);
    }

    [Fact]
    public void BrowserDataStore_CorruptSessionFile_ReturnsEmptySession()
    {
        File.WriteAllText(Path.Combine(_tempDir, "session.json"), "{ not valid json !!!");

        using var store = new BrowserDataStore(_tempDir);
        var session = store.LoadSession();

        Assert.Empty(session.Tabs);
        Assert.Equal(0, session.ActiveIndex);

        // The store must still be able to overwrite the corrupt file.
        Assert.True(store.SaveSession(new BrowserSession(
            [new SessionTab("Recovered", "https://example.com")], 0)));
        Assert.Single(store.LoadSession().Tabs);
    }

    [Fact]
    public void BrowserDataStore_CorruptSettingsFile_FallsBackToDefaults()
    {
        File.WriteAllText(Path.Combine(_tempDir, "settings.json"), "definitely not json");

        using var store = new BrowserDataStore(_tempDir);
        Assert.Equal(BrowserSettings.Default.SearchEngine, store.Settings.SearchEngine);
        Assert.Equal(BrowserSettings.Default.Theme, store.Settings.Theme);

        // And a subsequent save must replace the corrupt file cleanly.
        Assert.True(store.SaveSettings(BrowserSettings.Default with { Accent = "Green" }));
        using var reloadedStore = new BrowserDataStore(_tempDir);
        Assert.Equal("Green", reloadedStore.Settings.Accent);
    }

    [Fact]
    public void BrowserDataStore_ConcurrentSessionSaves_AllSucceed()
    {
        // Regression: a fixed ".tmp" name made saves from concurrent windows
        // collide with "file is being used by another process".
        using var store = new BrowserDataStore(_tempDir);
        var results = Enumerable.Range(0, 12)
            .AsParallel()
            .Select(i =>
            {
                var session = new BrowserSession(
                    [new SessionTab($"Tab {i}", $"https://example.com/{i}")], 0);
                return store.SaveSession(session);
            })
            .ToArray();

        Assert.All(results, Assert.True);
        Assert.DoesNotContain(Directory.GetFiles(_tempDir, "*.tmp"), f => File.Exists(f));
        Assert.Single(store.LoadSession().Tabs);
    }

    [Fact]
    public void BrowserDataStore_HistoryDebounceBurst_CoalescesToPersistedState()
    {
        using (var store = new BrowserDataStore(_tempDir))
        {
            for (var i = 0; i < 20; i++)
            {
                Assert.True(store.AddHistory($"Page {i}", $"https://example.com/{i}"));
            }
            Assert.Equal(20, store.History.Count);
            Assert.True(store.FlushPendingSaves());
            Assert.True(store.FlushPendingSaves()); // idempotent
        }

        using var reloadedStore = new BrowserDataStore(_tempDir);
        Assert.Equal(20, reloadedStore.History.Count);
    }

    [Fact]
    public void BrowserDataStore_SaveSettings_NeverWritesPlaintextApiKeyToDisk()
    {
        const string apiKey = "super-secret-key-12345";
        using (var store = new BrowserDataStore(_tempDir))
        {
            Assert.True(store.SaveSettings(BrowserSettings.Default with { AiApiKey = apiKey }));
        }

        var raw = File.ReadAllText(Path.Combine(_tempDir, "settings.json"));
        Assert.DoesNotContain(apiKey, raw);
        Assert.Contains(BrowserDataStore.EncryptedApiKeyPrefix, raw);

        // The key must survive a restart via DPAPI decryption. On load the
        // legacy field is decrypted back into AiApiKey.
        using var reloadedStore = new BrowserDataStore(_tempDir);
        Assert.Equal(apiKey, reloadedStore.Settings.AiApiKey);
    }

    [Fact]
    public void BrowserDataStore_SaveSettings_WhenDpapiFails_FailsClosedWithoutPlaintext()
    {
        var original = BrowserDataStore.DataProtectOverride;
        BrowserDataStore.DataProtectOverride = _ =>
            throw new CryptographicException("simulated DPAPI failure");
        try
        {
            const string apiKey = "another-secret-key-67890";
            using var store = new BrowserDataStore(_tempDir);
            Assert.True(store.SaveSettings(BrowserSettings.Default with { AiApiKey = apiKey }));

            var raw = File.ReadAllText(Path.Combine(_tempDir, "settings.json"));
            Assert.DoesNotContain(apiKey, raw);
            Assert.DoesNotContain(BrowserDataStore.EncryptedApiKeyPrefix, raw);

            // The in-memory settings keep the key so the session stays usable;
            // persistence simply fails closed.
            Assert.Equal(apiKey, store.Settings.AiApiKey);
        }
        finally
        {
            BrowserDataStore.DataProtectOverride = original;
        }
    }

    [Fact]
    public void BrowserDataStore_DecryptApiKey_RejectsGarbageWithoutThrowing()
    {
        Assert.Equal("", BrowserDataStore.DecryptApiKey(""));
        Assert.Equal("plain value", BrowserDataStore.DecryptApiKey("plain value"));
        Assert.Equal("", BrowserDataStore.DecryptApiKey(BrowserDataStore.EncryptedApiKeyPrefix + "not-base64!!!"));
    }
}
