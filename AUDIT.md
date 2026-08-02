# Tabloid-Browser Codebase Audit Report

Generated: 2026-07-28

---

## Critical

### 1. Bookmark Persistence Bug
**File:** `Processes/Services/BrowserDataStore.cs:91-128`
**Problem:** `SaveBookmark` reads bookmarks into a `List<BookmarkItem>`, modifies the list (add/edit/delete), but then calls `SaveContent("bookmarks.json", Content)` using the **original raw JSON string** (`Content`) instead of serializing the modified list. Bookmark edits and deletions are silently lost on application restart.

### 2. No Global Exception Handler
**File:** `App.xaml.cs`
**Problem:** There is no `UnhandledException` or `TaskScheduler.UnobservedTaskException` handler. Any unhandled exception on the UI thread or background tasks causes the application to terminate silently with no error dialog, no crash log, and no recovery. The `err.txt` stack overflow crash was only captured by a lucky chance.

### 3. WebView2 Runtime Not Checked
**File:** `UI/MainWindow/MainWindow.WebView2.cs:96`
**Problem:** WebView2 initialization (`EnsureCoreWebView2Async`) is called without first checking if the WebView2 Runtime is installed. If the runtime is missing, the app crashes with an unhelpful system error instead of showing a user-friendly message with a download link.

### 4. Stack Overflow from Font Fallback
**File:** Referenced in `err.txt`
**Problem:** The `PureMechanic` font combined with emoji characters triggers infinite recursion in `System.Windows.Media.FontFamily.GetFontFamilyModel`, causing a stack overflow crash. This is a known WPF rendering bug triggered by fonts with incomplete Unicode coverage.

### 5. Race Condition in Bookmark Save
**File:** `Processes/Services/BrowserDataStore.cs:91-128`
**Problem:** `SaveBookmark` performs a read-modify-write cycle without holding the `ReaderWriterLockSlim` for the entire operation. Two concurrent `SaveBookmark` calls can interleave, causing one update to overwrite the other and lose data.

---

## High

### 6. No HTTPS Enforcement
**File:** `UI/MainWindow/MainWindow.Navigation.cs` (`ParseURL` method)
**Problem:** The URL parser accepts `http://` scheme without warning or automatic upgrade. Users can navigate to insecure sites with no indication. Most modern browsers auto-upgrade to HTTPS.

### 7. AI Prompt Injection
**File:** `UI/MainWindow/MainWindow.AI.cs:119-120`
**Problem:** Raw page HTML is injected directly into the AI prompt via string interpolation (`$"... {pageContent} ..."`). A malicious webpage could contain text like "Ignore all previous instructions and..." which would be passed verbatim to the LLM, potentially manipulating the AI's responses.

### 8. API Keys Stored in Plaintext
**File:** `Processes/Services/BrowserDataStore.cs` (aiSettings.json)
**Problem:** The OpenAI-compatible API key is stored as plaintext JSON in `%LOCALAPPDATA%\TabloidBrowser\Data\aiSettings.json`. Any process or user with read access to this directory can extract the key.

### 9. Global Static State everywhere
**Files:** `MainWindow.xaml.cs:26` (`Instance`), `BrowserDataStore` (all static), `WindowSettings.Current`
**Problem:** `MainWindow.Instance`, `BrowserDataStore` methods, and `WindowSettings.Current` are all static singletons. This makes unit testing impossible, creates hidden coupling between components, and introduces initialization order bugs (e.g., `WindowSettings.Current` is null before `MainWindow` constructor runs).

---

## Medium

### 10. Silent Exception Swallowing
**Files:**
- `UI/MainWindow/MainWindow.Tabs.cs:572` — `catch (Exception) { }` in tab drag
- `UI/MainWindow/MainWindow.UI.cs:1376` — `catch (Exception) { }` in clipboard copy
- `UI/MainWindow/MainWindow.Navigation.cs:294` — logs to debug only
- `Processes/AI/AiService.cs:58` — HTTP errors logged but swallowed

**Problem:** Exceptions are caught and silently discarded with empty catch blocks or debug-only logging. Errors in tab dragging, clipboard operations, navigation, and AI calls are invisible to users and developers, making bugs extremely hard to diagnose.

### 11. No Nullable Reference Types
**Files:** All `.cs` files
**Problem:** The entire project runs without `#nullable enable`. Null reference exceptions are a runtime risk throughout the codebase. The C# compiler cannot catch null-safety issues at compile time.

### 12. Collision-Prone ID Generation
**Files:**
- `Processes/Models/BrowserTab.cs` — `BookmarkItem.ID` uses `new Random().Next(100000, 999999)`
- `Processes/Models/BrowserTab.cs` — `HistoryItem.ID` uses same pattern

**Problem:** With only 900,000 possible values and no uniqueness guarantee, ID collisions become increasingly likely as bookmarks/history grow. `DownloadItem.ID` correctly uses `Guid.NewGuid().ToString()` — the other models should follow suit.

### 13. History Grows Unbounded
**File:** `Processes/Services/BrowserDataStore.cs`
**Problem:** The `History` list (`List<HistoryItem>`) has no maximum size, no TTL, and no pruning. After months of use, this list will contain tens of thousands of entries, all loaded into memory at startup and slowing down the history page.

### 14. Particle Animation on UI Thread
**File:** `UI/MainWindow/MainWindow.Particles.cs:65-69`
**Problem:** `CompositionTarget.Rendering` fires every frame and updates 300+ `Ellipse` elements directly on the UI thread. This causes frame drops and CPU usage on lower-end hardware. Should be moved to a background thread with `WriteableBitmap` rendering, or limited to only run when the start page is visible.

### 15. No Download Type Validation
**File:** `UI/MainWindow/MainWindow.Navigation.cs` (download handler)
**Problem:** Downloads initiated by WebView2 are accepted without any validation of file type. Executable files (`.exe`, `.msi`, `.bat`) are downloaded with no user confirmation or warning.

### 16. Settings Page is a Single Monolithic Page
**File:** `UI/Pages/SettingsPage.xaml` + `UI/MainWindow/MainWindow.UI.cs`
**Problem:** All settings (appearance, privacy, AI, shortcuts, about) are on one page with no search, no categories, no filtering. As features grow, this page becomes increasingly unusable.

### 17. `WindowSettings.Current` Initialization Race
**File:** `UI/MainWindow/MainWindow.UI.cs` + `MainWindow.xaml.cs:78`
**Problem:** `WindowSettings.Current` is set in the `MainWindow` constructor. Any code that reads `WindowSettings.Current` before the constructor completes (e.g., from a static initializer or another service) gets `null`.

---

## Low

### 18. No Unit Tests
**Problem:** There are zero test files, no test project, no test framework referenced. Every refactor risks introducing regressions with no safety net. `ViewModels/TabViewModel.cs` exists but is unused — someone intended to start testing but never did.

### 19. No CI/CD Pipeline
**Problem:** No GitHub Actions, Azure DevOps, or any automated build/test pipeline. Builds are manual via batch scripts. There is no automated verification that changes don't break the build.

### 20. No Code Signing
**Problem:** The published executable is not code-signed. Windows SmartScreen will warn users, and the app cannot be auto-updated safely without signing.

### 21. No Versioning
**Problem:** The `.csproj` has no `<Version>` or `<AssemblyVersion>` property. The app has no concept of its own version, making it impossible to check for updates or report bugs with a version number.

### 22. No Structured Logging
**Problem:** The app uses `Debug.WriteLine` and `System.Diagnostics.Trace` for logging. There is no structured logging framework (Serilog, NLog, etc.), no log levels, no log rotation, and debug output is invisible in release builds.

### 23. No Onboarding Flow
**Problem:** New users are dropped into the browser with no first-run experience, no feature tour, and no guidance on available keyboard shortcuts or settings.

### 24. Missing Basic Browser Features
**Problem:** No Ctrl+F (find in page), no bookmark import/export, no bookmarks toolbar, no download progress indicator, no page print support.

### 25. Hardcoded Strings
**Files:** Throughout all UI code-behind files
**Problem:** UI text, file paths, CSS templates, and error messages are all inline string literals. No resource dictionaries, no localization support, no constants file.

### 26. Unused ViewModel
**File:** `ViewModels/TabViewModel.cs`
**Problem:** Contains a `TabViewModel` class with `INotifyPropertyChanged` that is never instantiated or referenced anywhere in the codebase. Dead code that suggests an abandoned MVVM migration.

### 27. Inconsistent Naming Conventions
**Files:** All `.cs` files
**Problem:** No consistent naming pattern — mix of `PascalCase` private methods, `_camelCase` fields, `camelCase` parameters, and magic number constants without names. No `.editorconfig` or naming convention enforcement.

### 28. No Documentation for Developers
**Problem:** No architecture docs, no code comments on complex logic (particle system, tab drag-detach, tracker blocking), no CONTRIBUTING.md, no CHANGELOG.md, no LICENSE file.
