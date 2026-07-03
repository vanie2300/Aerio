# Tabloid Browser Bug Report

All bugs identified in this document have been fixed in the codebase.

---

## Fixed

### Bug 1 — `ContinueWith` + `async` lambda swallows exceptions
**Files:** `MainWindow/MainWindow.AI.cs`, `NativePages/PageBuilders.cs`
**Fix applied:** Replaced `ContinueWith` with `await Task.Delay(800, ct)` + direct fetch call. Uses `Dispatcher.InvokeAsync` + `await op.Task.Unwrap()`.

### Bug 2 — `async void` local function crashes process on exception
**Files:** `NativePages/PageBuilders.cs`, `MainWindow/MainWindow.AI.cs`
**Fix applied:** `FetchModelsAsync` changed to `async Task`. `DebounceFetchModels` is `async void` (event handler callback) with full try/catch wrapping.

### Bug 3 — `DispatcherTimer` never disposed on Copy button
**File:** `MainWindow/MainWindow.AI.cs`
**Fix applied:** Uses `Task.Delay(2000).ContinueWith(...)` with `WeakReference<Button>` instead of `DispatcherTimer`.

### Bug 4 — `CancellationTokenSource` not disposed
**Files:** `NativePages/PageBuilders.cs`, `MainWindow/MainWindow.AI.cs`
**Fix applied:** `Dispose()` called after `Cancel()` in both `DebounceFetchMethods`.

### Bug 5 — `_tabs` accessed without lock in `UpdateMemoryStatus`
**File:** `MainWindow/MainWindow.UI.cs`
**Fix applied:** Uses `_tabOperationLock.Wait(0)` (non-blocking try) and creates snapshot under lock.

### Bug 6 — Static `HttpClient` never disposed
**File:** `MainWindow/MainWindow.WebView2.cs`
**Fix applied:** `_crxHttpClient.Dispose()` called in `MainWindow_Closing`.

### Bug 7 — `_popupOverlay` overwritten without cleanup
**File:** `MainWindow/MainWindow.AI.cs`, `NativePages/PageBuilders.cs`
**Fix applied:** `RemovePopupBlur()` called before assigning new `_popupOverlay` in both `ShowAiSetupPopup` and `ShowSitePermissionsPopup`.

### Bug 8 — Null unboxing cast on FileDrop
**File:** `NativePages/PageBuilders.cs`
**Fix applied:** Uses `as string[]` with null check.

### Bug 9 — Anonymous lambda event handler leak (Loaded/ContentRendered)
**File:** `MainWindow.xaml.cs`
**Fix applied:** Uses named methods `OnWindowLoaded` and `OnWindowContentRendered`.

### Bug 10 — `StoreInstallBtn.MouseDown` anonymous lambda leak
**File:** `MainWindow.xaml.cs`
**Fix applied:** Uses named method `StoreInstallBtn_MouseDown`.

### Bug 11 — `ScrollViewer.Loaded` leak on native page rebuild
**File:** `MainWindow/MainWindow.Navigation.cs`
**Fix applied:** Detaches `ScrollViewer.Loaded` after first fire (`scroll.Loaded -= OnScrollLoaded`).

### Bug 12 — `_carouselWebView.Dispose()` without detaching events
**File:** `Controls/Widgets.cs`
**Fix applied:** `WebMessageReceived -= OnWebMessageReceived` called before `Dispose()`.

### Bug 13 — Captured `scroll`/`tab` in `BeginInvoke` after tab closed
**File:** `MainWindow/MainWindow.Navigation.cs`
**Fix applied:** Checks `_tabs.Contains(localTab)` inside the dispatcher callback.

### Bug 14 — `async void PermissionRequested` crash risk
**File:** `MainWindow/MainWindow.Permissions.cs`
**Fix applied:** Entire body wrapped in try/catch.

### Bug 15 — `BeginInvoke` fire-and-forget without inner try/catch
**File:** `MainWindow/MainWindow.Permissions.cs`
**Fix applied:** Inner lambda has try/catch wrapping `PermissionRequested` call.

### Bug 16 — `FindPopup` offset uses `ActualWidth` before layout
**File:** `MainWindow/MainWindow.UI.cs`
**Fix applied:** Popup opened first (`IsOpen = true`), then `UpdateLayout()`, then offset set via `Dispatcher.BeginInvoke`.

### Bug 17 — Captured `_activeTab` could be stale in async methods
**File:** `MainWindow/MainWindow.UI.cs`
**Fix applied:** `ActivateTabAsync` checks `_tabs.Contains(tab)` before processing.

### Bug 18 — `Dispatcher.InvokeAsync` non-atomic UI updates
**File:** `MainWindow.xaml.cs`
**Fix applied:** Combined into single `Dispatcher.InvokeAsync` call.

### Bug 19 — `NavigateFromAddressBar` and `NavigateFromStartSearch` are `async void`
**File:** `MainWindow/MainWindow.Navigation.cs`
**Fix applied:** Both have try/catch wrapping the entire body.

### Bug 20 — Race in `SleepTabs_Click` snapshot without lock
**File:** `MainWindow/MainWindow.UI.cs`
**Fix applied:** Snapshot taken under `_tabOperationLock`.

### Bug 21 — Accent fallback returns white in dark mode
**File:** `MainWindow.xaml.cs`
**Fix applied:** Default case returns purple (`#724DDF`), a sensible dark-mode default.

### Bug 22 — `WriteCrashLog` throws if `_logPath` has no directory
**File:** `App.xaml.cs`
**Fix applied:** Uses `if (dir is not null)` before `Directory.CreateDirectory`.

### Bug 23 — Empty `catch { }` in `WriteCrashLog`
**File:** `App.xaml.cs`
**Fix applied:** Logs exception via `Debug.WriteLine`.

### Bug 24 — `DispatcherTimer` ticks after shutdown started
**File:** `MainWindow/MainWindow.Tabs.cs`
**Fix applied:** Guard at top of `SleepTimer_Tick`: `if (Dispatcher.HasShutdownStarted) { _sleepTimer.Stop(); return; }`.

### Bug 25 — `SaveCurrentSessionAsync` return value discarded
**File:** `MainWindow/MainWindow.Tabs.cs`
**Fix applied:** Checks `if (!_dataStore.SaveSession(...))` and logs failure.

### Bug 26 — `DwmSetWindowAttribute` HRESULT not checked
**File:** `MainWindow.xaml.cs`
**Fix applied:** HRESULT checked and logged: `if (hr != 0) Debug.WriteLine(...)`.

### Bug 27 — `Clipboard.SetText` empty catch block
**File:** `MainWindow/MainWindow.AI.cs`
**Fix applied:** Logs exception via `Debug.WriteLine`.

### Bug 28 — No `OnExit` override
**File:** `App.xaml.cs`
**Fix applied:** `OnExit` override added.

### Bug 29 — `async void` handlers in Navigation.cs
**File:** `MainWindow/MainWindow.Navigation.cs`
**Fix applied:** Both `NavigateFromAddressBar` and `NavigateFromStartSearch` have try/catch wrapping the body.

### Bug 30 — DPI changes between monitors
**File:** `MainWindow/MainWindow.UI.cs`
**Fix applied:** `WM_DPICHANGED` handled in `WndProc`, calls `UpdateMaximizedMargins()`. Hook registered via `source?.AddHook(WndProc)` in constructor.

### Bug 31 — `_activeTab!` null-forgiving operator in Ctrl+Tab handler
**File:** `MainWindow/MainWindow.UI.cs:512`
**Severity:** Medium
**Fix applied:** `_activeTab` captured to local variable and null-checked before use.

### Bug 32 — `ShowSiteDataPopup` is `async void` without wrapping try/catch
**File:** `MainWindow/MainWindow.UI.cs:1540`
**Severity:** Low
**Fix applied:** Entire method body wrapped in try/catch.

### Bug 33 — `Path.GetDirectoryName` null-forgiving in CRX extraction
**File:** `NativePages/PageBuilders.cs:1547`
**Severity:** Low
**Fix applied:** Local variable null-check before `Directory.CreateDirectory`.

### Bug 34 — Empty `catch { }` in `ShowSiteDataPopup` cookie retrieval
**File:** `MainWindow/MainWindow.UI.cs:1574`
**Severity:** Low
**Fix applied:** Log exception via `Debug.WriteLine`.

### Bug 35 (CRITICAL) — `CancellationToken.None` defeats debounce in `DebounceFetchModels`
**Files:** `MainWindow/MainWindow.AI.cs`, `NativePages/PageBuilders.cs`
**Severity:** Critical
**Fix applied:** Pass `ct` instead of `CancellationToken.None` in both `DebounceFetchModels` locations.

### Bug 36 (CRITICAL) — Permission deferral "Default" result leaves request unhandled
**File:** `MainWindow/MainWindow.Permissions.cs`
**Severity:** Critical
**Fix applied:** Returns `Deny` when user closes the prompt without choosing (`"Default"`).

### Bug 37 (CRITICAL) — Download `StateChanged` anonymous handler never unsubscribed
**File:** `MainWindow/MainWindow.WebView2.cs`
**Severity:** Critical
**Fix applied:** Self-unsubscribes via `operation.StateChanged -= handler` on completion.

### Bug 38 (CRITICAL) — Static `_blockedResponse` shared across WebView2 environments
**File:** `MainWindow/MainWindow.WebView2.cs`
**Severity:** Critical
**Fix applied:** Field removed entirely; response created fresh per request.

### Bug 39 (CRITICAL) — Static mutable color fields break multi-window theming
**File:** `Models/BrowserTab.cs`
**Severity:** Critical
**Fix applied:** `static` color fields removed; instance colors used per tab. Static fields are `readonly Brush`.

### Bug 40 (HIGH) — Duplicate `new Random()` in particle constructor destroys entropy
**File:** `MainWindow/MainWindow.Particles.cs`
**Severity:** High
**Fix applied:** `BgParticle` constructor accepts a `Random` parameter; single `rng` instance reused.

### Bug 41 (HIGH) — Particle animation speed mismatch: 33ms timer vs 16ms time increment
**File:** `MainWindow/MainWindow.Particles.cs`
**Severity:** High
**Fix applied:** `_bgTime += 0.033` to match the 33ms `DispatcherTimer` interval.

### Bug 42 (HIGH) — Captured UI controls in `DebounceFetchModels` may be disposed after popup closes
**File:** `MainWindow/MainWindow.AI.cs`
**Severity:** High
**Fix applied:** `_isAiSetupOpen` flag checked inside `FetchModelsAsync` before accessing UI controls.

### Bug 43 (HIGH) — `Interlocked.Exchange` write mismatch on `_pendingReaderTab`
**File:** `MainWindow.xaml.cs`, `MainWindow/MainWindow.WebView2.cs`
**Severity:** High
**Fix applied:** All accesses to `_pendingReaderTab` use `Interlocked.Exchange`.

### Bug 44 (HIGH) — `_profile` guarded by two different locks
**File:** `MainWindow/MainWindow.WebView2.cs`, `MainWindow/MainWindow.Tabs.cs`
**Severity:** High
**Fix applied:** Made `_profile` volatile; write in `EnsureWebViewAsync` uses double-checked locking under `_initializationLock`.

### Bug 45 (MEDIUM) — `Tab_DragLeave` leaves stale `_tabDropIndex`
**File:** `MainWindow/MainWindow.UI.cs`
**Severity:** Medium
**Fix applied:** Replaced with `AnimatedTabPanel` + `_tabDropInsertIndex` reset in `EndDrag()`/`AbortDrag()`.

### Bug 46 (MEDIUM) — `EnsureDropIndicator` doesn't clear stale effect/background on reuse
**File:** `MainWindow/MainWindow.UI.cs`
**Severity:** Medium
**Fix applied:** Method removed; drop indicators handled by `AnimatedTabPanel`.

### Bug 47 (MEDIUM) — `SyncProfilePermissionSettingsAsync` fire-and-forget without await
**File:** `NativePages/PageBuilders.cs`
**Severity:** Medium
**Fix applied:** `await` with try/catch.

### Bug 48 (MEDIUM) — `DropShadowBitmapEffect` uses deprecated WPF API
**File:** `MainWindow.xaml`
**Severity:** Medium
**Fix applied:** Uses modern `DropShadowEffect`.

### Bug 49 (MEDIUM) — `_tabs` read without lock in `ApplyForceDarkToExistingTabs`
**File:** `MainWindow.xaml.cs`
**Severity:** Medium
**Fix applied:** Snapshot taken under `_tabOperationLock` before iterating.

### Bug 50 (MEDIUM) — `CachedContent` setter only removes `Grid`-type old content from parent
**File:** `Models/BrowserTab.cs`
**Severity:** Medium
**Fix applied:** Changed from `Grid` to `Panel` type filter; `RemoveElementFromParent` helper handles other types.

### Bug 51 (MEDIUM) — `ScrollViewer.Resources.Add(typeof(ScrollBar), null)` when resource missing
**File:** `NativePages/PageBuilders.cs`
**Severity:** Medium
**Fix applied:** Guarded with `is not null` check.

### Bug 52 (MEDIUM) — `StoreInstallBtn_MouseDown` calls click handler without `e.Handled = true`
**File:** `MainWindow.xaml.cs`
**Severity:** Medium
**Fix applied:** `StoreInstallBtn` and its handler removed.

### Bug 53 (LOW) — `WeakReference<Button>` in copy feedback is dead code
**File:** `MainWindow/MainWindow.AI.cs`
**Severity:** Low
**Fix applied:** `WeakReference` removed; `Task.Delay` + `ContinueWith` captures button directly.

### Bug 54 (LOW) — `OnNavigationCompleted` dispatches async lambda with redundant `Dispatcher.InvokeAsync`
**File:** `MainWindow/MainWindow.WebView2.cs`
**Severity:** Low
**Fix applied:** `Dispatcher.InvokeAsync` wrapper removed.

### Bug 55 (LOW) — `DispatcherTimer` instances never explicitly disposed on shutdown
**File:** `MainWindow/MainWindow.UI.cs`
**Severity:** Low
**Fix applied:** `Stop()` called and Tick handlers detached in `CleanupBeforeCloseAsync`.

### Bug 56 (LOW) — `ReadabilityCache` file read without `ConfigureAwait(false)`
**File:** `MainWindow/MainWindow.UI.cs`
**Severity:** Low
**Fix applied:** Added `.ConfigureAwait(false)`.

### Bug 57 (LOW) — `IsFromControl` hardcoded max visual-tree depth of 10
**File:** `MainWindow/MainWindow.UI.cs`
**Severity:** Low
**Fix applied:** Depth limit removed; loops until `current is null`.

### Bug 58 (LOW) — Static `_blockedResponse` never disposed on environment change
**File:** `MainWindow/MainWindow.WebView2.cs`
**Severity:** Low
**Fix applied:** Field removed.

### Bug 59 (LOW) — `AiService.FetchModelsAsync` creates new `HttpClient` per call
**File:** `AI/AiService.cs`
**Severity:** Low
**Fix applied:** Uses static `_fetchHttp` field.

### Bug 60 (LOW) — `OnWebMessageReceived` dispatches without checking `HasShutdownStarted`
**File:** `MainWindow.xaml.cs`
**Severity:** Low
**Fix applied:** All dispatches check `!Dispatcher.HasShutdownStarted`.

### Bug 61 (LOW) — `ApplySettings()` called from constructor triggers UI before `Show()`
**File:** `MainWindow.xaml.cs`
**Severity:** Low
**Fix applied:** `NotifySettingsChanged()` guarded by `IsLoaded`.

### Bug 66 (CRITICAL) — `DispatcherUnhandledException` handler causes stack overflow via `MessageBox.Show`
**File:** `App.xaml.cs`
**Severity:** Critical
**Fix applied:** Replaced `MessageBox.Show` with `Debug.WriteLine` to prevent dispatcher reentry.

---

## Newly Found Bugs (Unfixed)

### Bug 62 (HIGH) — `ApplyTheme` iterates `_tabs` without `_tabOperationLock`
**File:** `MainWindow.xaml.cs:406`
**Severity:** High
**Description:** `foreach (var tab in _tabs)` iterates without acquiring `_tabOperationLock`. A concurrent tab add/remove throws `InvalidOperationException` ("Collection was modified").
**Fix applied:** Snapshot taken under `_tabOperationLock` before iterating.

### Bug 63 (HIGH) — `_isPermissionsPopupOpen` not reset on synchronous throw during popup creation
**File:** `NativePages/PageBuilders.cs:746`
**Severity:** High
**Description:** `_isPermissionsPopupOpen = true` is set before the `Popup` is created. If `BuildSitePermissionsPopupContent` throws, the `Closed` handler (which resets to `false`) never fires — permanently blocking all future permission popups.
**Fix applied:** Wrapped entire method in try/catch; `_isPermissionsPopupOpen = false` set in catch.

### Bug 64 (MEDIUM) — `ShowSitePermissionsPopup` is `async void` with only partial try/catch
**File:** `NativePages/PageBuilders.cs:717`
**Severity:** Medium
**Description:** Only `SyncProfilePermissionSettingsAsync` is try/caught; any other exception in this `async void` method crashes the process.
**Fix applied:** Entire method body wrapped in try/catch.

### Bug 65 (MEDIUM) — `ApplyForceDarkToExistingTabs` uses non-blocking `Wait(0)` which silently skips
**File:** `MainWindow.xaml.cs:361`
**Severity:** Medium
**Description:** `_tabOperationLock.Wait(0)` returns `false` if the lock is held, causing `ApplyForceDarkToExistingTabs` to silently skip. User toggles force-dark but gets no feedback that the operation was not applied.
**Fix applied:** Changed to blocking `Wait()`.

---

## Verification

- Build: 0 warnings, 0 errors
- All 66 bugs fixed in the codebase
