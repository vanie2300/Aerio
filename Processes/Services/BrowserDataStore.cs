using System.IO;
using System.Security.Cryptography;
using System.Text;
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

    public BrowserDataStore(string? dataFolder = null)
    {
        _dataFolder = dataFolder ?? Path.Combine(
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
        catch (JsonException ex)
        {
            Log.Error(ex, "Corrupt session file, resetting to empty");
            return BrowserSession.Empty;
        }
        catch (IOException ex)
        {
            Log.Error(ex, "Failed to read session file, resetting to empty");
            return BrowserSession.Empty;
        }
    }

    private void WriteSession(BrowserSession session)
    {
        var tmp = _sessionPath + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(session, JsonOptions));
            File.Move(tmp, _sessionPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); }
            catch (Exception ex) { Log.Debug(ex, "Failed to clean up temporary session file"); }
            throw;
        }
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
            Log.Error(ex, "Failed to save session");
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
            Log.Error(ex, "Failed to clear session");
        }
        finally { _lock.ExitWriteLock(); }
    }

    public void AddBookmark(string title, string url)
    {
        _lock.EnterWriteLock();
        try
        {
            if (BookmarksInternal.Any(b => string.Equals(b.Url, url, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            BookmarksInternal.Insert(0, new BookmarkItem(CleanTitle(title), url, DateTimeOffset.Now));

            if (!SaveBookmarks())
            {
                BookmarksInternal.RemoveAt(0);
            }
        }
        finally { _lock.ExitWriteLock(); }
    }

    public void RemoveBookmark(string url)
    {
        _lock.EnterWriteLock();
        try
        {
            var snapshot = new List<BookmarkItem>(BookmarksInternal);
            BookmarksInternal.RemoveAll(bookmark => string.Equals(bookmark.Url, url, StringComparison.OrdinalIgnoreCase));

            if (BookmarksInternal.Count == snapshot.Count)
            {
                return;
            }

            if (!SaveBookmarks())
            {
                BookmarksInternal.Clear();
                BookmarksInternal.AddRange(snapshot);
            }
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

            if (added > 0 && !SaveBookmarks())
            {
                // The disk write failed — roll back the in-memory inserts so the
                // UI doesn't show bookmarks that will be gone after a restart.
                BookmarksInternal.RemoveRange(0, added);
                added = 0;
            }
        }
        finally { _lock.ExitWriteLock(); }

        return added;
    }

    public void ExportBookmarks(string path)
    {
        // Snapshot under the lock, then write outside it so a slow export
        // destination can't stall every other data operation.
        BookmarkItem[] snapshot;
        _lock.EnterReadLock();
        try
        {
            snapshot = [.. BookmarksInternal];
        }
        finally { _lock.ExitReadLock(); }

        Save(path, [.. snapshot]);
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

    public bool AddHistory(string title, string url)
    {
        if (!IsHttpUrl(url))
        {
            return false;
        }

        _lock.EnterWriteLock();
        try
        {
            var snapshot = new List<HistoryItem>(HistoryInternal);
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

            if (SaveHistory())
            {
                return true;
            }

            HistoryInternal.Clear();
            HistoryInternal.AddRange(snapshot);
            return false;
        }
        finally { _lock.ExitWriteLock(); }
    }

    public bool ClearHistory()
    {
        _lock.EnterWriteLock();
        try
        {
            var snapshot = new List<HistoryItem>(HistoryInternal);
            HistoryInternal.Clear();
            if (SaveHistory())
            {
                return true;
            }

            HistoryInternal.AddRange(snapshot);
            return false;
        }
        finally { _lock.ExitWriteLock(); }
    }

    public bool RemoveHistory(string url, DateTimeOffset visitedAt)
    {
        _lock.EnterWriteLock();
        try
        {
            var snapshot = new List<HistoryItem>(HistoryInternal);
            var removed = HistoryInternal.RemoveAll(item =>
                string.Equals(item.Url, url, StringComparison.OrdinalIgnoreCase) &&
                item.VisitedAt.Equals(visitedAt));
            if (removed == 0 || SaveHistory())
            {
                return true;
            }

            HistoryInternal.Clear();
            HistoryInternal.AddRange(snapshot);
            return false;
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
            try { WriteSession(BrowserSession.Empty); } catch (Exception ex) { Log.Error(ex, "Failed to write empty session during ClearAll"); }
        }
        finally { _lock.ExitWriteLock(); }
    }

    public bool ClearBrowsingState()
    {
        _lock.EnterWriteLock();
        try
        {
            var historySnapshot = new List<HistoryItem>(HistoryInternal);
            var downloadsSnapshot = new List<DownloadItem>(DownloadsInternal);
            var sessionExisted = File.Exists(_sessionPath);
            string? sessionSnapshot = null;
            try
            {
                if (sessionExisted)
                {
                    sessionSnapshot = File.ReadAllText(_sessionPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error(ex, "Failed to snapshot session before clearing browsing data");
                return false;
            }

            HistoryInternal.Clear();
            DownloadsInternal.Clear();
            var historySaved = SaveHistory();
            var downloadsSaved = SaveDownloads();
            if (historySaved && downloadsSaved)
            {
                try
                {
                    WriteSession(BrowserSession.Empty);
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to write empty session during ClearBrowsingState");
                }
            }

            HistoryInternal.AddRange(historySnapshot);
            DownloadsInternal.AddRange(downloadsSnapshot);

            var historyRestored = SaveHistory();
            var downloadsRestored = SaveDownloads();
            var sessionRestored = RestoreSessionSnapshot(sessionExisted, sessionSnapshot);
            if (!historyRestored || !downloadsRestored || !sessionRestored)
            {
                Log.Error(
                    new IOException("One or more browsing data files could not be restored after a failed clear."),
                    "Browsing data rollback was incomplete");
            }

            return false;
        }
        finally { _lock.ExitWriteLock(); }
    }

    private bool RestoreSessionSnapshot(bool existed, string? content)
    {
        var temporaryPath = _sessionPath + ".restore.tmp";
        try
        {
            if (!existed)
            {
                if (File.Exists(_sessionPath))
                {
                    File.Delete(_sessionPath);
                }
                return true;
            }

            File.WriteAllText(temporaryPath, content ?? string.Empty);
            File.Move(temporaryPath, _sessionPath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to restore session after a failed browsing data clear");
            try { File.Delete(temporaryPath); }
            catch (Exception cleanupException)
            {
                Log.Debug(cleanupException, "Failed to remove session restore file");
            }
            return false;
        }
    }

    public bool AddDownload(string fileName, string filePath, string sourceUrl)
    {
        _lock.EnterWriteLock();
        try
        {
            var snapshot = new List<DownloadItem>(DownloadsInternal);
            DownloadsInternal.Insert(0, new DownloadItem(CleanTitle(fileName), filePath, sourceUrl, DateTimeOffset.Now));

            if (DownloadsInternal.Count > 100)
            {
                DownloadsInternal.RemoveRange(100, DownloadsInternal.Count - 100);
            }

            if (SaveDownloads())
            {
                return true;
            }

            DownloadsInternal.Clear();
            DownloadsInternal.AddRange(snapshot);
            return false;
        }
        finally { _lock.ExitWriteLock(); }
    }

    public bool ClearDownloads()
    {
        _lock.EnterWriteLock();
        try
        {
            var snapshot = new List<DownloadItem>(DownloadsInternal);
            DownloadsInternal.Clear();
            if (SaveDownloads())
            {
                return true;
            }

            DownloadsInternal.AddRange(snapshot);
            return false;
        }
        finally { _lock.ExitWriteLock();
        }
    }

    public bool SaveSettings(BrowserSettings settings)
    {
        _lock.EnterWriteLock();
        try
        {
            // Write to disk FIRST; only mutate the in-memory settings after the
            // write succeeded, so memory and disk never diverge on failure.
            var forDisk = settings with
            {
                AiApiKey = EncryptApiKeyForDisk("", settings.AiApiKey),
                AiApiKeys = settings.AiApiKeys?.ToDictionary(kv => kv.Key, kv => EncryptApiKeyForDisk(kv.Key, kv.Value))
            };
            var tmp = _settingsPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(forDisk, JsonOptions));
            File.Move(tmp, _settingsPath, overwrite: true);
            Settings = settings;
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "SaveSettings failed");
            try { File.Delete(_settingsPath + ".tmp"); } catch (Exception delEx) { Log.Debug(delEx, "Failed to delete settings temp file"); }
            return false;
        }
        finally { _lock.ExitWriteLock(); }
    }

    private bool SaveBookmarks()
    {
        return Save(_bookmarksPath, BookmarksInternal);
    }

    private bool SaveHistory()
    {
        return Save(_historyPath, HistoryInternal);
    }

    private bool SaveDownloads()
    {
        return Save(_downloadsPath, DownloadsInternal);
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
        catch (JsonException ex)
        {
            Log.Error(ex, "Corrupt data file at {Path}, resetting to empty", path);
            return [];
        }
        catch (IOException ex)
        {
            Log.Error(ex, "Failed to read data file at {Path}", path);
            return [];
        }
    }

    private static bool Save<T>(string path, List<T> items)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (dir is not null) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(items, JsonOptions));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Save failed for {Path}", path);
            try { File.Delete(path + ".tmp"); }
            catch (Exception cleanupException) { Log.Debug(cleanupException, "Failed to clean up temporary data file for {Path}", path); }
            return false;
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

            var legacyDecrypted = DecryptKeyTracked("", settings.AiApiKey ?? "");
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
                SearchKeywords = settings.SearchKeywords ?? new Dictionary<string, string>(BrowserSettings.DefaultSearchKeywords, StringComparer.OrdinalIgnoreCase),
                NewTabShortcuts = settings.NewTabShortcuts ?? [.. BrowserSettings.DefaultNewTabShortcuts],
                AiEndpoint = string.IsNullOrWhiteSpace(settings.AiEndpoint) ? BrowserSettings.Default.AiEndpoint : settings.AiEndpoint,
                AiModel = string.IsNullOrWhiteSpace(settings.AiModel) ? BrowserSettings.Default.AiModel : settings.AiModel,
                AiApiKey = legacyDecrypted,
                AiApiKeys = MigrateProviderApiKeys(
                    settings.AiApiKeys?.ToDictionary(kv => kv.Key, kv => DecryptKeyTracked(kv.Key, kv.Value ?? "")),
                    settings.AiProvider,
                    legacyDecrypted),
                AiSystemPrompt = string.IsNullOrWhiteSpace(settings.AiSystemPrompt) ? BrowserSettings.Default.AiSystemPrompt : settings.AiSystemPrompt,
                AiMaxTokens = settings.AiMaxTokens <= 0 ? BrowserSettings.Default.AiMaxTokens : settings.AiMaxTokens,
                AiProvider = string.IsNullOrWhiteSpace(settings.AiProvider) ? BrowserSettings.Default.AiProvider : settings.AiProvider,
                AiMaxIterations = settings.AiMaxIterations <= 0 ? BrowserSettings.Default.AiMaxIterations : settings.AiMaxIterations,
                BackgroundOpacity = settings.BackgroundOpacity is >= 0 and <= 1 ? settings.BackgroundOpacity : BrowserSettings.Default.BackgroundOpacity,
                AiTemperature = settings.AiTemperature < 0 || settings.AiTemperature > 2 ? BrowserSettings.Default.AiTemperature : settings.AiTemperature,
                AiPolicy = settings.AiPolicy is null
                    ? AiBrowserPolicy.Default
                    : settings.AiPolicy with
                    {
                        MaxOpenTabs = Math.Clamp(settings.AiPolicy.MaxOpenTabs, 1, 50)
                    }
            };
        }
        catch (JsonException ex)
        {
            Log.Error(ex, "Corrupt settings file, reverting to defaults");
            return BrowserSettings.Default;
        }
        catch (IOException ex)
        {
            Log.Error(ex, "Failed to read settings file");
            return BrowserSettings.Default;
        }
    }

    private static Dictionary<string, string>? MigrateProviderApiKeys(
        Dictionary<string, string>? keys, string provider, string legacyKey)
    {
        if (string.IsNullOrWhiteSpace(legacyKey))
            return keys;
        if (keys is not null && keys.ContainsKey(provider))
            return keys;
        return BrowserSettings.StoreProviderApiKey(keys, provider, legacyKey);
    }

    private const string EncryptedApiKeyPrefix = "$dpapi$";

    /// <summary>
    /// Raw ciphertext blobs that failed DPAPI decryption at load time (slot key
    /// "" is the legacy AiApiKey). Preserved so a transient DPAPI failure
    /// (password reset, profile roamed without credentials) does not permanently
    /// erase the stored keys on the next settings save.
    /// </summary>
    private readonly Dictionary<string, string> _preservedKeyBlobs = [];

    private string EncryptApiKeyForDisk(string slotKey, string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext) &&
            _preservedKeyBlobs.TryGetValue(slotKey, out var blob) &&
            blob.StartsWith(EncryptedApiKeyPrefix, StringComparison.Ordinal))
        {
            // The stored blob could not be decrypted and no new key was entered —
            // keep the original ciphertext on disk instead of overwriting it
            // with an empty value.
            return blob;
        }
        return EncryptApiKey(plaintext);
    }

    private string DecryptKeyTracked(string slotKey, string stored)
    {
        var decrypted = DecryptApiKey(stored);
        if (string.IsNullOrEmpty(decrypted) &&
            stored.StartsWith(EncryptedApiKeyPrefix, StringComparison.Ordinal))
        {
            _preservedKeyBlobs[slotKey] = stored;
        }
        else
        {
            _preservedKeyBlobs.Remove(slotKey);
        }
        return decrypted;
    }

    private static string EncryptApiKey(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
            return string.Empty;

        try
        {
            var plainBytes = Encoding.UTF8.GetBytes(plaintext);
            var encryptedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
            return EncryptedApiKeyPrefix + Convert.ToBase64String(encryptedBytes);
        }
        catch (CryptographicException ex)
        {
            Log.Error(ex, "Failed to encrypt API key via DPAPI – storing as plaintext fallback");
            return plaintext;
        }
    }

    private static string DecryptApiKey(string stored)
    {
        if (string.IsNullOrEmpty(stored))
            return string.Empty;

        if (!stored.StartsWith(EncryptedApiKeyPrefix, StringComparison.Ordinal))
            return stored;

        try
        {
            var base64 = stored[EncryptedApiKeyPrefix.Length..];
            var encryptedBytes = Convert.FromBase64String(base64);
            var plainBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (CryptographicException ex)
        {
            Log.Error(ex, "Failed to decrypt API key – DPAPI may have been reset or data is corrupted");
            return string.Empty;
        }
        catch (FormatException ex)
        {
            Log.Error(ex, "Failed to decode stored API key – data is corrupted");
            return string.Empty;
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

        if (bg == "Default")
            return BrowserSettings.Default.Background;

        return bg;
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

public sealed record SessionTab(string Title, string Url, double ScrollX = 0, double ScrollY = 0, List<ChatMessage>? AiChatHistory = null);

public sealed record SitePermissionRule(string Host, string Permission, string State);

public sealed record SearchEngineDef(string Name, string UrlTemplate, string Keyword = "");

public sealed record NewTabShortcut(string Name, string Url);

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
    string AiProvider = "Ollama Cloud",
    string AiEndpoint = "https://ollama.com",
    string AiModel = "",
    string AiApiKey = "",
    Dictionary<string, string>? AiApiKeys = null,
    string AiSystemPrompt = "You are a helpful assistant controlling a live browser. " +
        "You have tools (search, open_tab, navigate, read_page, list_elements, click, fill, close_tab) to browse and interact with web pages. " +
        "To find information, call the search tool to get a result list; open a page only when the user asks (use open_tab or navigate). Never navigate to a search engine site directly. " +
        "Use the appropriate tool to carry out the user's request (e.g. fill forms, click buttons, navigate to URLs). Answer concisely.",
    int AiMaxTokens = 2048,
    double AiTemperature = 0.7,
    bool EnableAdBlock = true,
    bool UblockEnabled = true,
    DateTimeOffset? UblockUpdateCheckUtc = null,
    string UblockVersion = "",
    string AiSearchBackend = "DuckDuckGo",
    string AiSearchEndpoint = "",
    int AiMaxIterations = 10,
    bool AiPageContextConfirmation = true,
    Dictionary<string, bool>? AiToolsEnabled = null,
    string LastSeenVersion = "",
    AiBrowserPolicy? AiPolicy = null,
    Dictionary<string, string>? SearchKeywords = null,
    bool AutoHideBrowserChrome = false,
    List<NewTabShortcut>? NewTabShortcuts = null)
{
    public static Dictionary<string, string> DefaultSearchKeywords { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["YouTube"] = "yt",
        ["Wikipedia"] = "wiki"
    };

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

    public static List<NewTabShortcut> DefaultNewTabShortcuts { get; } =
    [
        new("Google", "https://www.google.com"),
        new("YouTube", "https://www.youtube.com"),
        new("GitHub", "https://github.com"),
        new("Reddit", "https://www.reddit.com"),
        new("Gmail", "https://mail.google.com")
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

    public static string GetApiKey(BrowserSettings settings, string? provider)
    {
        if (settings.AiApiKeys is { } keys)
            return !string.IsNullOrWhiteSpace(provider) && keys.TryGetValue(provider, out var stored)
                ? stored
                : "";
        return settings.AiApiKey;
    }

    public static Dictionary<string, string>? StoreProviderApiKey(
        Dictionary<string, string>? existing, string provider, string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return existing;

        var keys = existing is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(existing, StringComparer.OrdinalIgnoreCase);
        keys[provider] = key;
        return keys;
    }

    public static Dictionary<string, string> SetProviderApiKey(
        Dictionary<string, string>? existing, string provider, string key)
    {
        var keys = existing is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(existing, StringComparer.OrdinalIgnoreCase);
        keys[provider] = key;
        return keys;
    }

    public static Dictionary<string, string> AiProviderPresets { get; } = new()
    {
        ["Ollama Cloud"] = "https://ollama.com",
        ["Ollama (local)"] = "http://localhost:11434",
        ["NVIDIA NIM"] = "https://integrate.api.nvidia.com/v1",
        ["OpenAI"] = "https://api.openai.com/v1",
        ["OpenRouter"] = "https://openrouter.ai/api/v1",
        ["Groq"] = "https://api.groq.com/openai/v1",
        ["DeepSeek"] = "https://api.deepseek.com/v1",
        ["Together AI"] = "https://api.together.xyz/v1",
        ["Cerebras"] = "https://api.cerebras.ai/v1",
        ["OpenAI-compatible (custom)"] = ""
    };

    public static string GetProviderEndpoint(string provider) =>
        AiProviderPresets.TryGetValue(provider, out var ep) ? ep : "";

    public static bool UsesNativeOllamaApi(string? provider) =>
        string.Equals(provider, "Ollama Cloud", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(provider, "Ollama (local)", StringComparison.OrdinalIgnoreCase);

    public static BrowserSettings Default { get; } = new(
        SearchEngine: "DuckDuckGo",
        ClearDataOnExit: false,
        TabSleepMinutes: 2,
        Accent: "Purple",
        Theme: "Dark",
        AutoHideBrowserChrome: false,
        NewTabShortcuts: DefaultNewTabShortcuts,
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
        AiProvider: "Ollama Cloud",
        AiEndpoint: "https://ollama.com",
        AiModel: "",
        AiApiKey: "",
        AiSystemPrompt: "You are a helpful assistant controlling a live browser. " +
            "You have tools (search, open_tab, navigate, read_page, list_elements, click, fill, close_tab) to browse and interact with web pages. " +
            "To find information, call the search tool to get a result list; open a page only when the user asks (use open_tab or navigate). Never navigate to a search engine site directly. " +
            "Use the appropriate tool to carry out the user's request (e.g. fill forms, click buttons, navigate to URLs). Answer concisely.",
        AiMaxTokens: 2048,
        AiTemperature: 0.7,
        EnableAdBlock: true,
        UblockEnabled: true,
        UblockUpdateCheckUtc: null,
        UblockVersion: "",
        AiSearchBackend: "DuckDuckGo",
        AiSearchEndpoint: "",
        AiMaxIterations: 10,
        AiPageContextConfirmation: true,
        AiToolsEnabled: null,
        LastSeenVersion: "",
        AiPolicy: new AiBrowserPolicy(),
        SearchKeywords: new Dictionary<string, string>(DefaultSearchKeywords, StringComparer.OrdinalIgnoreCase));
}
