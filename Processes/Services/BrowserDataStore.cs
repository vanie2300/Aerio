using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace TabloidBrowser;

public sealed class BrowserDataStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly string _dataFolder;
    private readonly string _bookmarksPath;
    private readonly string _historyPath;
    private readonly string _settingsPath;
    private readonly string _sessionPath;
    private readonly string _downloadsPath;
    private readonly ReaderWriterLockSlim _lock = new();

    public BrowserDataStore()
    {
        _dataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TabloidBrowser",
            "Data");

        _bookmarksPath = Path.Combine(_dataFolder, "bookmarks.json");
        _historyPath = Path.Combine(_dataFolder, "history.json");
        _settingsPath = Path.Combine(_dataFolder, "settings.json");
        _sessionPath = Path.Combine(_dataFolder, "session.json");
        _downloadsPath = Path.Combine(_dataFolder, "downloads.json");

        Directory.CreateDirectory(_dataFolder);
        BookmarksInternal = Load<BookmarkItem>(_bookmarksPath);
        HistoryInternal = Load<HistoryItem>(_historyPath);
        DownloadsInternal = Load<DownloadItem>(_downloadsPath);
        TrimLoadedData();
        Settings = LoadSettings();
    }

    public IReadOnlyList<BookmarkItem> Bookmarks 
    { 
        get 
        { 
            _lock.EnterReadLock(); 
            try { return BookmarksInternal.ToList(); } 
            finally { _lock.ExitReadLock(); } 
        } 
    }
    private List<BookmarkItem> BookmarksInternal { get; } = [];

    public IReadOnlyList<HistoryItem> History 
    { 
        get 
        { 
            _lock.EnterReadLock(); 
            try { return HistoryInternal.ToList(); } 
            finally { _lock.ExitReadLock(); } 
        } 
    }
    private List<HistoryItem> HistoryInternal { get; } = [];

    public IReadOnlyList<DownloadItem> Downloads 
    { 
        get 
        { 
            _lock.EnterReadLock(); 
            try { return DownloadsInternal.ToList(); } 
            finally { _lock.ExitReadLock(); } 
        } 
    }
    private List<DownloadItem> DownloadsInternal { get; } = [];

    public BrowserSettings Settings { get; private set; }

    public BrowserSession LoadSession()
    {
        if (!File.Exists(_sessionPath))
        {
            return BrowserSession.Empty;
        }

        try
        {
            return JsonSerializer.Deserialize<BrowserSession>(File.ReadAllText(_sessionPath), JsonOptions)
                ?? BrowserSession.Empty;
        }
        catch (JsonException)
        {
            return BrowserSession.Empty;
        }
        catch (IOException)
        {
            return BrowserSession.Empty;
        }
    }

    private void WriteSession(BrowserSession session)
    {
        var tmp = _sessionPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(session, JsonOptions));
        File.Move(tmp, _sessionPath, overwrite: true);
    }

    public bool SaveSession(BrowserSession session)
    {
        _lock.EnterWriteLock();
        try
        {
            WriteSession(session);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SaveSession failed: {ex.Message}");
            return false;
        }
        finally { _lock.ExitWriteLock(); }
    }

    public void ClearSession()
    {
        _lock.EnterWriteLock();
        try
        {
            WriteSession(BrowserSession.Empty);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ClearSession failed: {ex.Message}");
        }
        finally { _lock.ExitWriteLock(); }
    }

    public void AddBookmark(string title, string url)
    {
        if (IsBookmarked(url))
        {
            return;
        }

        _lock.EnterWriteLock();
        try
        {
            BookmarksInternal.Insert(0, new BookmarkItem(CleanTitle(title), url, DateTimeOffset.Now));
            SaveBookmarks();
        }
        finally { _lock.ExitWriteLock(); }
    }

    public void RemoveBookmark(string url)
    {
        _lock.EnterWriteLock();
        try
        {
            BookmarksInternal.RemoveAll(bookmark => string.Equals(bookmark.Url, url, StringComparison.OrdinalIgnoreCase));
            SaveBookmarks();
        }
        finally { _lock.ExitWriteLock(); }
    }

    public int ImportBookmarks(string path)
    {
        var imported = Load<BookmarkItem>(path);
        var added = 0;

        _lock.EnterWriteLock();
        try
        {
            foreach (var bookmark in imported.Where(bookmark => IsHttpUrl(bookmark.Url)))
            {
                if (BookmarksInternal.Any(b => string.Equals(b.Url, bookmark.Url, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                BookmarksInternal.Insert(0, new BookmarkItem(CleanTitle(bookmark.Title), bookmark.Url, bookmark.CreatedAt));
                added++;
            }

            if (added > 0)
            {
                SaveBookmarks();
            }
        }
        finally { _lock.ExitWriteLock(); }

        return added;
    }

    public void ExportBookmarks(string path)
    {
        _lock.EnterReadLock();
        try
        {
            Save(path, BookmarksInternal);
        }
        finally { _lock.ExitReadLock(); }
    }

    public bool IsBookmarked(string url)
    {
        _lock.EnterReadLock();
        try
        {
            return BookmarksInternal.Any(bookmark => string.Equals(bookmark.Url, url, StringComparison.OrdinalIgnoreCase));
        }
        finally { _lock.ExitReadLock(); }
    }

    public void AddHistory(string title, string url)
    {
        if (!IsHttpUrl(url))
        {
            return;
        }

        _lock.EnterWriteLock();
        try
        {
            if (HistoryInternal.FirstOrDefault() is { } latest &&
                string.Equals(latest.Url, url, StringComparison.OrdinalIgnoreCase))
            {
                HistoryInternal[0] = latest with
                {
                    Title = CleanTitle(title),
                    VisitedAt = DateTimeOffset.Now
                };
            }
            else
            {
                HistoryInternal.Insert(0, new HistoryItem(CleanTitle(title), url, DateTimeOffset.Now));
            }

            if (HistoryInternal.Count > 500)
            {
                HistoryInternal.RemoveRange(500, HistoryInternal.Count - 500);
            }

            SaveHistory();
        }
        finally { _lock.ExitWriteLock(); }
    }

    public void ClearHistory()
    {
        _lock.EnterWriteLock();
        try
        {
            HistoryInternal.Clear();
            SaveHistory();
        }
        finally { _lock.ExitWriteLock(); }
    }

    public void RemoveHistory(string url, DateTimeOffset visitedAt)
    {
        _lock.EnterWriteLock();
        try
        {
            HistoryInternal.RemoveAll(item =>
                string.Equals(item.Url, url, StringComparison.OrdinalIgnoreCase) &&
                item.VisitedAt.Equals(visitedAt));
            SaveHistory();
        }
        finally { _lock.ExitWriteLock(); }
    }

    public void ClearAll()
    {
        _lock.EnterWriteLock();
        try
        {
            BookmarksInternal.Clear();
            HistoryInternal.Clear();
            DownloadsInternal.Clear();
            SaveBookmarks();
            SaveHistory();
            SaveDownloads();
            try { WriteSession(BrowserSession.Empty); } catch (Exception ex) { Debug.WriteLine($"ClearSession failed: {ex.Message}"); }
        }
        finally { _lock.ExitWriteLock(); }
    }

    public void ClearBrowsingState()
    {
        _lock.EnterWriteLock();
        try
        {
            HistoryInternal.Clear();
            DownloadsInternal.Clear();
            SaveHistory();
            SaveDownloads();
            try { WriteSession(BrowserSession.Empty); } catch (Exception ex) { Debug.WriteLine($"ClearSession failed: {ex.Message}"); }
        }
        finally { _lock.ExitWriteLock(); }
    }

    public void AddDownload(string fileName, string filePath, string sourceUrl)
    {
        _lock.EnterWriteLock();
        try
        {
            DownloadsInternal.Insert(0, new DownloadItem(CleanTitle(fileName), filePath, sourceUrl, DateTimeOffset.Now));

            if (DownloadsInternal.Count > 100)
            {
                DownloadsInternal.RemoveRange(100, DownloadsInternal.Count - 100);
            }

            SaveDownloads();
        }
        finally { _lock.ExitWriteLock(); }
    }

    public void ClearDownloads()
    {
        _lock.EnterWriteLock();
        try
        {
            DownloadsInternal.Clear();
            SaveDownloads();
        }
        finally { _lock.ExitWriteLock();
        }
    }

    public void SaveSettings(BrowserSettings settings)
    {
        _lock.EnterWriteLock();
        try
        {
            Settings = settings;
            var tmp = _settingsPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Settings, JsonOptions));
            File.Move(tmp, _settingsPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SaveSettings failed: {ex}");
        }
        finally { _lock.ExitWriteLock(); }
    }

    private void SaveBookmarks()
    {
        Save(_bookmarksPath, BookmarksInternal);
    }

    private void SaveHistory()
    {
        Save(_historyPath, HistoryInternal);
    }

    private void SaveDownloads()
    {
        Save(_downloadsPath, DownloadsInternal);
    }

    private void TrimLoadedData()
    {
        _lock.EnterWriteLock();
        try
        {
            if (HistoryInternal.Count > 500)
            {
                HistoryInternal.RemoveRange(500, HistoryInternal.Count - 500);
                SaveHistory();
            }

            if (DownloadsInternal.Count > 100)
            {
                DownloadsInternal.RemoveRange(100, DownloadsInternal.Count - 100);
                SaveDownloads();
            }
        }
        finally { _lock.ExitWriteLock(); }
    }

    private static List<T> Load<T>(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path), JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    private static void Save<T>(string path, List<T> items)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (dir is not null) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(items, JsonOptions));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Save failed for {path}: {ex.Message}");
        }
    }

    private BrowserSettings LoadSettings()
    {
        if (!File.Exists(_settingsPath))
        {
            return BrowserSettings.Default;
        }

        try
        {
            var settings = JsonSerializer.Deserialize<BrowserSettings>(File.ReadAllText(_settingsPath), JsonOptions)
                ?? BrowserSettings.Default;

            return settings with
            {
                SearchEngine = string.IsNullOrWhiteSpace(settings.SearchEngine) ? BrowserSettings.Default.SearchEngine : settings.SearchEngine,
                Accent = NormalizeAccent(settings.Accent),
                Theme = string.Equals(settings.Theme, "Light", StringComparison.OrdinalIgnoreCase) ? "Light" : BrowserSettings.Default.Theme,
                Background = NormalizeBackground(settings.Background),
                TabSleepMinutes = settings.TabSleepMinutes <= 0 ? BrowserSettings.Default.TabSleepMinutes : settings.TabSleepMinutes,
                TrackerBlocklist = settings.TrackerBlocklist is { Count: > 0 } ? settings.TrackerBlocklist : BrowserSettings.Default.TrackerBlocklist,
                TrackerAllowlist = settings.TrackerAllowlist ?? [],
                SitePermissions = NormalizeSitePermissions(settings.SitePermissions),
                Extensions = settings.Extensions ?? [],
                CustomEngines = settings.CustomEngines ?? [],
                AiEndpoint = string.IsNullOrWhiteSpace(settings.AiEndpoint) ? BrowserSettings.Default.AiEndpoint : settings.AiEndpoint,
                AiModel = string.IsNullOrWhiteSpace(settings.AiModel) ? BrowserSettings.Default.AiModel : settings.AiModel,
                AiApiKey = settings.AiApiKey ?? BrowserSettings.Default.AiApiKey,
                AiSystemPrompt = string.IsNullOrWhiteSpace(settings.AiSystemPrompt) ? BrowserSettings.Default.AiSystemPrompt : settings.AiSystemPrompt,
                AiMaxTokens = settings.AiMaxTokens <= 0 ? BrowserSettings.Default.AiMaxTokens : settings.AiMaxTokens,
                BackgroundOpacity = settings.BackgroundOpacity is >= 0 and <= 1 ? settings.BackgroundOpacity : BrowserSettings.Default.BackgroundOpacity,
                AiTemperature = settings.AiTemperature < 0 || settings.AiTemperature > 2 ? BrowserSettings.Default.AiTemperature : settings.AiTemperature
            };
        }
        catch (JsonException)
        {
            return BrowserSettings.Default;
        }
        catch (IOException)
        {
            return BrowserSettings.Default;
        }
    }

    private static string CleanTitle(string title)
    {
        title = title.Replace(" (sleep)", string.Empty, StringComparison.Ordinal);
        return string.IsNullOrWhiteSpace(title) ? "Untitled" : title.Trim();
    }

    private static string NormalizeAccent(string? accent)
    {
        return accent switch
        {
            "Blue" or "Gray" or "Green" or "White" or "Purple" => accent,
            _ => BrowserSettings.Default.Accent
        };
    }

    private static string NormalizeBackground(string? bg)
    {
        if (string.IsNullOrWhiteSpace(bg))
            return BrowserSettings.Default.Background;

        return bg switch
        {
            "Default" => bg,
            _ => bg
        };
    }

    private static List<SitePermissionRule> NormalizeSitePermissions(List<SitePermissionRule>? rules)
    {
        if (rules is null)
        {
            return [];
        }

    return rules
        .Where(rule => !string.IsNullOrWhiteSpace(rule.Host) &&
            !string.IsNullOrWhiteSpace(rule.Permission))
        .Select(rule => new SitePermissionRule(
            rule.Host.Trim().ToLowerInvariant(),
            rule.Permission.Trim(),
            NormalizePermissionState(rule.State)))
        .Where(rule => rule.State is "Allow" or "Deny")
        .Distinct()
        .ToList();
    }

    private static string NormalizePermissionState(string? state)
    {
        return state switch
        {
            "Allow" => "Allow",
            "Deny" or "Block" => "Deny",
            _ => "Default"
        };
    }

    public static bool IsHttpUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    public void Dispose()
    {
        _lock.Dispose();
    }
}

public sealed record BookmarkItem(string Title, string Url, DateTimeOffset CreatedAt);

public sealed record HistoryItem(string Title, string Url, DateTimeOffset VisitedAt);

public sealed record DownloadItem(string FileName, string FilePath, string SourceUrl, DateTimeOffset DownloadedAt);

public sealed record BrowserSession(List<SessionTab> Tabs, int ActiveIndex)
{
    public static BrowserSession Empty { get; } = new([], 0);
}

public sealed record SessionTab(string Title, string Url, double ScrollX = 0, double ScrollY = 0);

public sealed record SitePermissionRule(string Host, string Permission, string State);

public sealed record SearchEngineDef(string Name, string UrlTemplate);

public sealed record BrowserSettings(
    string SearchEngine,
    bool ClearDataOnExit,
    int TabSleepMinutes,
    string Accent,
    string Theme,
    bool ShowQuickLinks,
    string Background,
    bool BlockTrackers,
    List<string> TrackerBlocklist,
    List<string> TrackerAllowlist,
    List<SitePermissionRule> SitePermissions,
    bool EnableAutofill,
    bool EnablePasswordSave,
    bool EnableDevTools,
    bool ForceDarkPage,
    List<ExtensionInfo> Extensions,
    List<SearchEngineDef> CustomEngines,
    Dictionary<string, string>? Shortcuts,
    double BackgroundOpacity = 1.0,
    bool AiEnabled = false,
    bool AiSidebarOpen = false,
    string AiProvider = "Ollama",
    string AiEndpoint = "http://localhost:11434/v1",
    string AiModel = "",
    string AiApiKey = "",
    string AiSystemPrompt = "You are a helpful assistant. Answer concisely.",
    int AiMaxTokens = 2048,
    double AiTemperature = 0.7)
{
    public static List<string> DefaultTrackerBlocklist { get; } =
    [
        "google-analytics.com",
        "googletagmanager.com",
        "doubleclick.net",
        "facebook.net",
        "connect.facebook.net",
        "hotjar.com",
        "segment.io",
        "mixpanel.com",
        "scorecardresearch.com",
        "adsystem.com"
    ];

    public static Dictionary<string, string> DefaultShortcuts { get; } = new()
    {
        ["new_tab"] = "Ctrl+T",
        ["close_tab"] = "Ctrl+W",
        ["focus_address"] = "Ctrl+L",
        ["reload"] = "Ctrl+R",
        ["history"] = "Ctrl+H",
        ["find"] = "Ctrl+F",
        ["print"] = "Ctrl+P",
        ["reader_mode"] = "Ctrl+Shift+R",
        ["ai_toggle"] = "Ctrl+Shift+A",
    };

    public static string GetShortcut(Dictionary<string, string>? overrides, string id) =>
        overrides is not null && overrides.TryGetValue(id, out var v) ? v : DefaultShortcuts.GetValueOrDefault(id, "");

    public static Dictionary<string, string> AiProviderPresets { get; } = new()
    {
        ["Ollama"] = "http://localhost:11434/v1",
        ["OpenAI"] = "https://api.openai.com/v1",
        ["Groq"] = "https://api.groq.com/openai/v1",
        ["OpenRouter"] = "https://openrouter.ai/api/v1",
        ["Custom"] = ""
    };

    public static string GetProviderEndpoint(string provider) =>
        AiProviderPresets.TryGetValue(provider, out var ep) ? ep : "";

    public static BrowserSettings Default { get; } = new(
        SearchEngine: "DuckDuckGo",
        ClearDataOnExit: false,
        TabSleepMinutes: 2,
        Accent: "Purple",
        Theme: "Dark",
        ShowQuickLinks: true,
        Background: "Default",
        BackgroundOpacity: 1.0,
        BlockTrackers: true,
        TrackerBlocklist: DefaultTrackerBlocklist,
        TrackerAllowlist: [],
        SitePermissions: [],
        EnableAutofill: false,
        EnablePasswordSave: false,
        EnableDevTools: false,
        ForceDarkPage: false,
        Extensions: [],
        CustomEngines: [],
        Shortcuts: null,
        AiEnabled: false,
        AiSidebarOpen: false,
        AiProvider: "Ollama",
        AiEndpoint: "http://localhost:11434/v1",
        AiModel: "",
        AiApiKey: "",
        AiSystemPrompt: "You are a helpful assistant. Answer concisely.",
        AiMaxTokens: 2048,
        AiTemperature: 0.7);
}
