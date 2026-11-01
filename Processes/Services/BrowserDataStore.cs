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
        // A unique temp name per write: several windows share this data folder,
        // and a fixed ".tmp" name made concurrent saves collide with
        // "file is being used by another process" (observed in the logs).
        var tmp = $"{_sessionPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(session, JsonOptions));
            MoveWithRetry(tmp, _sessionPath);
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

            // Hot path: this runs on the UI thread for every completed page
            // navigation. Persist on a short debounce instead of rewriting the
            // whole file per navigation; durability is preserved by the flush
            // on Dispose and by the immediate saves in Clear/Remove operations.
            ScheduleHistorySave();
            return true;
        }
        finally { _lock.ExitWriteLock(); }
    }

    private static readonly TimeSpan HistorySaveDebounce = TimeSpan.FromSeconds(2);
    private readonly object _historyFlushGate = new();
    private System.Threading.Timer? _historySaveTimer;
    private bool _historySavePending;

    private void ScheduleHistorySave()
    {
        _historySavePending = true;
        if (_historySaveTimer is null)
        {
            _historySaveTimer = new System.Threading.Timer(
                _ => FlushPendingHistorySave(), null, HistorySaveDebounce, Timeout.InfiniteTimeSpan);
        }
        else
        {
            _historySaveTimer.Change(HistorySaveDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void FlushPendingHistorySave()
    {
        // Serialize flushes so a timer flush and a dispose flush can't write
        // out of order (the later snapshot must win on disk).
        lock (_historyFlushGate)
        {
            if (!_historySavePending)
            {
                return;
            }
            _historySavePending = false;

            List<HistoryItem> snapshot;
            _lock.EnterReadLock();
            try
            {
                snapshot = [.. HistoryInternal];
            }
            finally { _lock.ExitReadLock(); }

            if (!Save(_historyPath, snapshot))
            {
                // Keep the new data in memory; the next add reschedules a flush.
                _historySavePending = true;
            }
        }
    }

    /// <summary>Persists any debounced writes immediately (used on Dispose and by tests).</summary>
    public bool FlushPendingSaves()
    {
        lock (_historyFlushGate)
        {
            var pending = _historySavePending;
            _historySavePending = false;
            if (!pending)
            {
                return true;
            }

            List<HistoryItem> snapshot;
            _lock.EnterReadLock();
            try
            {
                snapshot = [.. HistoryInternal];
            }
            finally { _lock.ExitReadLock(); }

            if (!Save(_historyPath, snapshot))
            {
                _historySavePending = true;
                return false;
            }

            return true;
        }
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
        var temporaryPath = $"{_sessionPath}.{Guid.NewGuid():N}.restore.tmp";
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
            var tmp = $"{_settingsPath}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(forDisk, JsonOptions));
            MoveWithRetry(tmp, _settingsPath);
            Settings = settings;
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "SaveSettings failed");
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
        // A unique temp name per write: several windows share this data folder,
        // and a fixed ".tmp" name made concurrent saves collide.
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (dir is not null) Directory.CreateDirectory(dir);
            File.WriteAllText(tmp, JsonSerializer.Serialize(items, JsonOptions));
            MoveWithRetry(tmp, path);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Save failed for {Path}", path);
            try { File.Delete(tmp); }
            catch (Exception cleanupException) { Log.Debug(cleanupException, "Failed to clean up temporary data file for {Path}", path); }
            return false;
        }
    }

    /// <summary>
    /// Atomic replace with a short retry: the destination can be transiently
    /// locked by antivirus/indexing or another window's replace on some file
    /// systems. Two quick retries cover the observed 100 ms-scale contention.
    /// </summary>
    private static void MoveWithRetry(string tmp, string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(tmp, path, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(40);
            }
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
                AiEndpoint = string.IsNullOrWhiteSpace(settings.AiEndpoint) || !AiEndpoints.IsSecure(settings.AiEndpoint)
                    ? BrowserSettings.Default.AiEndpoint
                    : settings.AiEndpoint,
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

    internal const string EncryptedApiKeyPrefix = "$dpapi$";

    /// <summary>
    /// Raw ciphertext blobs that failed DPAPI decryption at load time (slot key
    /// "" is the legacy AiApiKey). Preserved so a transient DPAPI failure
    /// (password reset, profile roamed without credentials) does not permanently
    /// erase the stored keys on the next settings save.
    /// </summary>
    private readonly Dictionary<string, string> _preservedKeyBlobs = [];

    internal string EncryptApiKeyForDisk(string slotKey, string plaintext)
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

        try
        {
            return EncryptApiKey(plaintext);
        }
        catch (CryptographicException ex)
        {
            // Fail closed: DPAPI failure must never cause the key to be
            // written to disk in plaintext. The in-memory settings keep the
            // key for the current session; the user re-enters it next launch.
            Log.Error(ex, "DPAPI encryption failed for the {Slot} API key; the key will not be persisted", string.IsNullOrEmpty(slotKey) ? "default" : slotKey);
            return string.Empty;
        }
    }

    internal string DecryptKeyTracked(string slotKey, string stored)
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

    /// <summary>
    /// Test seam allowing the DPAPI protect call to be replaced (e.g. to
    /// simulate a CryptographicException). Production code never sets this.
    /// </summary>
    internal static Func<byte[], byte[]>? DataProtectOverride;

    private static string EncryptApiKey(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
            return string.Empty;

        // Deliberately no fallback here: a DPAPI failure propagates so the
        // disk layer can fail closed instead of storing plaintext.
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var encryptedBytes = DataProtectOverride?.Invoke(plainBytes)
            ?? ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
        return EncryptedApiKeyPrefix + Convert.ToBase64String(encryptedBytes);
    }

    internal static string DecryptApiKey(string stored)
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
        // Origin-boundary validation lives in SitePermissionPolicy: hosts must
        // parse as real hostnames and matching is exact-host.
        return SitePermissionPolicy.NormalizeRules(rules);
    }

    public static bool IsHttpUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    public void Dispose()
    {
        _historySaveTimer?.Dispose();
        _historySaveTimer = null;
        try
        {
            FlushPendingSaves();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to flush pending history save on dispose");
        }
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
