using Xunit;

namespace TabloidBrowser.Tests;

/// <summary>
/// Per-site zoom persistence: values survive restarts, are keyed by origin,
/// clamped to the supported range, and 100% removes the override.
/// </summary>
public class SiteZoomPersistenceTests : IDisposable
{
    private readonly string _tempDir;

    public SiteZoomPersistenceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"AerioZoomTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void SetSiteZoom_SurvivesReload_AndIsKeyedByOrigin()
    {
        using (var store = new BrowserDataStore(_tempDir))
        {
            store.SetSiteZoom("https://example.com/some/page?q=1", 1.5);
            store.SetSiteZoom("https://other.example", 0.75);
        }

        using var reloaded = new BrowserDataStore(_tempDir);

        // Any path on the same origin resolves to the saved zoom.
        Assert.Equal(1.5, reloaded.GetSiteZoom("https://example.com/another/page"));
        Assert.Equal(0.75, reloaded.GetSiteZoom("https://other.example"));
        Assert.Equal(1.0, reloaded.GetSiteZoom("https://never-configured.example"));
    }

    [Fact]
    public void SetSiteZoom_HundredPercent_RemovesOverride()
    {
        using var store = new BrowserDataStore(_tempDir);

        store.SetSiteZoom("https://example.com", 1.75);
        Assert.Equal(1.75, store.GetSiteZoom("https://example.com"));

        store.SetSiteZoom("https://example.com", 1.0);
        Assert.Equal(1.0, store.GetSiteZoom("https://example.com"));
        Assert.DoesNotContain("example.com", File.ReadAllText(Path.Combine(_tempDir, "settings.json")));
    }

    [Theory]
    [InlineData(10.0, 5.0)]
    [InlineData(0.05, 0.25)]
    [InlineData(-3.0, 0.25)]
    public void SetSiteZoom_ClampsToSupportedRange(double requested, double expected)
    {
        using var store = new BrowserDataStore(_tempDir);

        store.SetSiteZoom("https://example.com", requested);

        Assert.Equal(expected, store.GetSiteZoom("https://example.com"));
    }

    [Fact]
    public void GetSiteZoom_InvalidOrNonWebUrl_ReturnsDefault()
    {
        using var store = new BrowserDataStore(_tempDir);

        Assert.Equal(1.0, store.GetSiteZoom(null));
        Assert.Equal(1.0, store.GetSiteZoom(""));
        Assert.Equal(1.0, store.GetSiteZoom("not a url"));
        Assert.Equal(1.0, store.GetSiteZoom("tabloid://settings"));
        Assert.Equal(1.0, store.GetSiteZoom("file:///C:/page.html"));
    }

    [Fact]
    public void SetSiteZoom_InvalidUrl_IsIgnored()
    {
        using var store = new BrowserDataStore(_tempDir);

        store.SetSiteZoom("tabloid://settings", 2.0);
        store.SetSiteZoom(null, 2.0);
        store.SetSiteZoom("https://example.com", double.NaN);

        Assert.Equal(1.0, store.GetSiteZoom("tabloid://settings"));
        Assert.Equal(1.0, store.GetSiteZoom("https://example.com"));
    }

    [Fact]
    public void SetSiteZoom_SchemeAndPortAreIndependentOrigins()
    {
        using var store = new BrowserDataStore(_tempDir);

        store.SetSiteZoom("https://example.com", 1.25);
        store.SetSiteZoom("http://example.com", 0.5);
        store.SetSiteZoom("https://example.com:8443", 2.0);

        Assert.Equal(1.25, store.GetSiteZoom("https://example.com"));
        Assert.Equal(0.5, store.GetSiteZoom("http://example.com"));
        Assert.Equal(2.0, store.GetSiteZoom("https://example.com:8443"));
    }

    [Fact]
    public void SaveSettings_PreservesExistingSiteZoomLevels()
    {
        using var store = new BrowserDataStore(_tempDir);

        store.SetSiteZoom("https://example.com", 1.4);

        // An unrelated settings save (theme change) must not drop zoom overrides.
        Assert.True(store.SaveSettings(store.Settings with { Theme = "Light" }));

        Assert.Equal(1.4, store.GetSiteZoom("https://example.com"));
    }
}
