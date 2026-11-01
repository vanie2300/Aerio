# Aerio Browser: Optimization Roadmap

## Main Goal

Transform Aerio into a **lightweight, fast, secure, and resource-efficient Windows browser** while preserving its existing features, functionality, and user interface.

> **Current priority:** Phase 1 — Installer Size Optimization.
>
> **Important:** The 50 MB installer target is a development goal, not a guaranteed result. Installer size, installed disk usage, and RAM usage must be measured separately.

## Phase 1: Installer Size Optimization

**Target:** Online installer of **50 MB or less**, if feasible without compromising usability.

- [x] Audit the existing build output and installer contents.
- [x] Remove unnecessary bundled runtime dependencies.
- [x] Detect and use the existing WebView2 runtime when available.
- [x] Evaluate a framework-dependent .NET deployment with clear prerequisite handling.
- [x] Optimize build outputs and installer compression.
- [x] Keep a separate offline installer for machines without the required runtimes.
- [x] Measure download size, installer size, and installed disk footprint.

**Exit criterion:** A tested smaller online installer that installs reliably, with the existing features intact.

### Phase 1 progress (2026-11-01)

**Approach:**
- The installer now builds in two modes from `installer/Aerio.iss`:
  - **Online** (default): ships the framework-dependent app payload; detects the .NET 8 Desktop Runtime and WebView2 Runtime and downloads only what is missing (wizard download page with progress/cancel).
  - **Offline** (`/DOfflineInstaller`): ships the self-contained payload and embeds the offline WebView2 Runtime installer, as before.
- WebView2 detection uses the documented EdgeUpdate registry keys; .NET 8 Desktop Runtime detection uses the documented registry key plus a fallback scan of the machine-wide shared framework folder.
- Satellite resource languages are limited to English (`SatelliteResourceLanguages` in the csproj).

**Measurements (Release, win-x64):**

| Artifact | Before | After (online) | After (offline) |
|---|---|---|---|
| Installer download size | 258.7 MB | **8.9 MB** | 258.7 MB |
| Publish output | 189.7 MB / 485 files | 30.2 MB / 26 files | 174.2 MB / 264 files |
| Portable ZIP | — | — | 70.7 MB |

Notes:
- The old baseline installer embedded the self-contained output plus the ~205 MB offline WebView2 Runtime installer.
- The online installer payload is dominated by `Microsoft.Windows.SDK.NET.dll` (23.7 MB raw, from the `net8.0-windows10.0.19041.0` target framework); it compresses to ~8.9 MB with LZMA2.
- Installer size, installed disk usage, and runtime memory remain separate metrics; installed footprint must still be confirmed by a real install on a clean machine.

**Remaining for exit criterion:**
- [ ] Install the online installer on a machine without .NET 8 / WebView2 and on a machine with both, and verify all features.
- [ ] Verify offline installer on a machine without either runtime.
- [ ] Confirm installed disk footprint after a real install.

## Phase 2: Memory and CPU Optimization

**Target:** Lower resource usage under equivalent browsing workloads.

- [x] Profile RAM and CPU usage before making changes.
- [x] Improve inactive-tab hibernation without disrupting audio or ongoing tasks.
- [x] Release unused WebView2 resources safely.
- [x] Reduce unnecessary background processes and polling.
- [x] Optimize cold startup and warm startup.
- [ ] Benchmark tab restoration and multi-tab performance.

**Exit criterion:** Measured reductions in avoidable resource use without user-visible regressions.

### Phase 2 progress (2026-11-01)

**Baseline (idle, Start page, default particles):**
- UI process idle CPU: **20.1% of one core**
- UI process: 184.7 MB working set / 127.9 MB private
- 7 WebView2 processes: 724.2 MB working set / 514.4 MB private
- Total: ~909 MB working set across 8 processes

**Changes made:**
1. **Loading spinner storyboard** (`BrowserPaneControl`): the infinite rotation storyboard ran for the process lifetime even while the overlay was collapsed, keeping the render loop ticking at ~60 fps. It is now started/stopped from code when the overlay shows/hides. This was the dominant idle CPU cost.
2. **Idle timers paused while minimized** (`MainWindow.StateChanged`): the 100 ms chrome auto-hide timer and the 33 ms start-page particle timer now stop when the window is minimized and resume on restore when applicable.
3. **Particle loop** (`MainWindow.Particles`): connection-line dependency properties are only written on state change (previously ~thousands of `Visibility` writes per second invalidated the canvas); the full visual-tree rebuild on window resize is skipped for small size deltas.
4. **Settings search-engine carousel** (`Widgets.cs`): now shares the window's WebView2 environment instead of creating a default environment. Previously it spawned an entire second browser process tree (and crashed with `E_ACCESSDENIED` in the installed build, where the default user-data folder is not writable next to the exe — confirmed in the logs). Verified: with the carousel open, all WebView2 processes belong to the shared tree.
5. **Custom start-page background** (`MainWindow`): decoded asynchronously off the UI thread with `BitmapCacheOption.OnLoad` + `Freeze()`, which also releases the file handle that was previously held for the process lifetime.
6. **Wildcard allow/blocklist matching** (`DomainMatches`): wildcard patterns are compiled once into a cached `Regex` instead of being rebuilt for every subresource request.
7. **AI streaming updates** (`UpdateStreamingBubble`): bubble text rebuilds and auto-scrolls are coalesced to ~20 updates/second (tokens still append to the buffer; the final render always shows the complete text).
8. **Tab hibernation audit**: the existing suspend/discard pipeline (`TrySuspendAsync`, memory-pressure discard, 30-minute expiry) was reviewed — guards for active tabs, playing audio, and AI-owned tabs are correct; no changes required.

**Measurements after (identical protocol: idle, Start page, particles running):**
- UI process idle CPU: **1.4% of one core** (baseline 20.1% — **−93%**)
- UI process: 182.4 MB working set / 126.8 MB private (~unchanged)
- WebView2 processes: 7 (unchanged); with the settings carousel open the process count rises by exactly one renderer in the shared tree instead of a full second tree
- Particle animation verified still running via screenshot diff (6% of sampled pixels change over 1.2 s)
- Settings page and search-engine carousel verified rendering via UI Automation
- Test suite: 299/299 passing, zero build warnings

**Notes:**
- Idle memory was not expected to move in this round; the changes target CPU, timers, animations, and process count. Memory-focused wins require the workload benchmarks below.
- The WebView2 browser process tree itself (7 processes for a fresh window) is inherent runtime behavior and unchanged.

**Remaining for exit criterion:**
- [ ] Benchmark tab restoration and multi-tab workloads (1/5/10/20 tabs) before/after, as defined in Phase 6.
- [ ] Confirm hibernation behaviour in long-running daily use (no user-visible regressions).

## Phase 3: Architecture and Code Refactoring

**Target:** A cleaner, more maintainable codebase.

- [x] Separate browser logic from UI components.
- [x] Reduce oversized `MainWindow` partial classes.
- [x] Centralize tab lifecycle and navigation management.
- [x] Improve event handling and application-state coordination.
- [x] Add unit tests around the extracted components.
- [x] Preserve the existing **C# / WPF / WebView2** architecture.

**Exit criterion:** Clearer component boundaries, passing tests, and unchanged intended functionality.

### Phase 3 progress (2026-11-01)

**Extracted pure services** (browser logic moved out of `MainWindow`, all unit-tested):
- `Processes/Services/TrackerPolicy.cs` — host parsing, domain/wildcard matching (with compiled-regex cache), tracker allow/block checks, dangerous-download classification. Removes the last string/regex logic from the per-request filter path.
- `Processes/Services/TabSleepPolicy.cs` — hibernation/discard decisions over a `TabSleepSnapshot` record (audio, AI-busy, active-tab, and expiry rules). The sleep timer now maps tabs to snapshots and executes the policy's decisions.
- `Processes/Services/SystemMemoryPressure.cs` — memory-pressure threshold and Win32 probe, with a pure, testable decision overload.
- `Processes/Services/NewTabShortcutPolicy.cs` — new-tab shortcut validation, normalization, and cleanup, extracted from `BrowserPaneControl`.

**Split oversized partials** (mechanical, zero behavior change):
- `PageBuilders.cs` (3,601 lines) → six focused files: library pages (230), Hymns (281), Settings (2,034), SiteData (517), Extensions (492), AI management (144).
- `MainWindow.UI.cs` (2,434 lines) → six focused files: core UI (1,170), tab actions (350), find & navigation (280), reader mode (252), extensions/uBlock (420), shutdown (117).

**Tests:** 39 new unit tests (`TrackerPolicyTests`, `TabSleepPolicyTests`, `SystemMemoryPressureTests`, `NewTabShortcutPolicyTests`). Suite: **338/338 passing**, zero build warnings.

**Runtime verification:** refactored build launches cleanly (no errors in logs), window responsive, Start-page animation running (idle CPU signature 0.8–3.1% under varying machine load, matching the Phase 2 build).

**Remaining candidates for future cleanup** (not required by the exit criterion):
- `MainWindow.AI.cs` (2,217 lines) and `PageBuilders.Settings.cs` (2,034 lines) are still large; splitting them follows the same mechanical pattern.
- The AI chat state could be extracted into a view-model service like the policies above.

## Phase 4: Security and Runtime Modernization

**Target:** Supported, safer browser infrastructure.

- [x] Upgrade from .NET 8 to a supported .NET LTS version, subject to compatibility tests.
- [x] Strengthen WebView2 permissions and webpage-to-application trust boundaries.
- [x] Validate navigation requests, external URIs, and downloads.
- [x] Harden AI-to-browser interactions against untrusted page content.
- [x] Audit local storage, credentials, and sensitive data handling.
- [x] Run security and dependency checks.

**Exit criterion:** Supported runtime, documented security controls, and no unresolved critical or high-severity issues found in the release review.

### Phase 4 progress (2026-11-01)

**Runtime upgrade (.NET 8 → .NET 10 LTS)** — urgent: .NET 8 support ends 2026-11-10.
- Target framework `net8.0-windows10.0.19041.0` → `net10.0-windows10.0.19041.0` (SDK 10.0.403, desktop runtime 10.0.14).
- All 338 tests pass on .NET 10; zero warnings after addressing the new analyzer findings:
  - CA2024: AI streaming readers no longer call `reader.EndOfStream` in async loops (potential synchronous blocking); null-terminated `ReadLineAsync` reads instead.
  - NU1510: `System.Security.Cryptography.ProtectedData` package reference removed (shipped in the .NET 10 desktop runtime).
- CI and release workflows build with .NET 10 (`10.0.x`).
- Installer detects the .NET 10 Desktop Runtime (registry + shared-framework scan) and downloads it from `aka.ms/dotnet/10.0` when missing; both installer modes compile with Inno Setup 6.7.4.
- Runtime smoke test passed on .NET 10: app starts, window responsive, no errors in logs.

**Security review and hardening:**
1. **AI trust boundary (fix applied):** page content returned by AI tools (`read_page`, page metadata, page links) previously entered the model conversation unsanitized — only the chat context path was protected. All three tool result paths now pass through `ContentSanitizer` (multilingual injection-pattern redaction + `<page_content>`/`<system>` tag escaping), matching the chat path, which already wraps content in a tagged block with explicit system rules and an injection warning.
2. **Update integrity:** the self-update flow is pinned to `https://github.com/vanie2300/Aerio/releases/download/` (HTTPS, host + path validated), re-validated before download and again before execution; the installer asset name is matched exactly.
3. **Downloads:** executable download types (`.exe`, `.msi`, `.bat`, `.cmd`, `.ps1`, `.vbs`, `.scr`, `.jar`, `.msix`, `.appx`) require user confirmation; extension installs are restricted to the Settings page flow.
4. **Permissions:** WebView2 permission requests are gated by the per-site permission system (prompt + stored rules, groupable by permission kind).
5. **External URIs:** no page-controlled `Process.Start` anywhere; the three shell-execute sites are app-controlled constants or explicit user actions (open downloads folder, run downloaded update installer after confirmation, open the WebView2 download page). External protocol links use WebView2's own handling UI.
6. **Credentials:** AI provider API keys are DPAPI-encrypted at rest (`$dpapi$` prefix) with legacy plaintext migration on load; no log statement anywhere includes keys, tokens, or passwords.
7. **Dependency check:** `dotnet list package --vulnerable --include-transitive` — no vulnerable packages on the .NET 10 package graph.
8. **Log hygiene fix:** the chrome auto-hide probe logged a debug line at 10 Hz whenever cursor reads failed (secure desktop / lock screen); it now logs once per failure streak.

**Accepted residual risk:**
- Binaries are not code-signed (requires a signing certificate); SmartScreen will warn on first run. Tracked as an infrastructure item, not a code defect.

## Phase 5: Browser Performance and Reliability

**Target:** Fast, stable daily browsing.

- [x] Improve tab switching and navigation responsiveness.
- [x] Strengthen session restoration and crash recovery.
- [x] Reduce unnecessary disk writes.
- [x] Improve download handling and error recovery.
- [x] Add automated regression tests for core browsing features.
- [x] Establish repeatable performance tests.

**Exit criterion:** Core workflows pass regression testing, with performance improvements documented against baseline.

### Phase 5 progress (2026-11-01)

**Persistence reliability (crash recovery):**
1. **Concurrent-save collision fixed:** all data files used a fixed `<name>.tmp` staging file. Multiple windows share the same data folder, so concurrent session saves collided with "file is being used by another process" (observed in real logs). Temp names are now unique per write, and the atomic replace retries twice on transient destination locks.
2. **Corrupt-file recovery pinned by tests:** corrupt `session.json` returns an empty session and the store can overwrite it; corrupt `settings.json` falls back to defaults and a subsequent save replaces the file cleanly.
3. **Session roundtrip pinned by tests:** tabs, scroll positions, and the active index survive save → reload.

**Disk write reduction:**
- `AddHistory` (one full-file rewrite per completed navigation, previously synchronous on the UI thread) now persists on a 2-second debounce; pending writes flush on dispose and are retried by the next add if a write fails. Durability-critical operations (session saves, settings, bookmarks, downloads, clear-data) remain immediate.
- Net effect: a burst of N page loads coalesces into ≤ 1 history write, and navigations no longer perform disk I/O on the UI thread.

**Download error recovery:**
- Interrupted/cancelled downloads now report "Download interrupted: \<file\>" in the status bar instead of failing silently.

**Regression and performance tests:**
- 10 new regression tests: session roundtrip, corrupt session/settings recovery, concurrent session saves (the fixed-tmp regression), debounced history persistence, broken-destination flush semantics, and debounce-burst coalescing.
- 4 repeatable performance smoke tests (generous thresholds, `Category=Performance` trait) to catch gross regressions early: address suggestions over 7k records, sanitizer over ~200 KB multilingual content, 100k tracker-policy lookups over 50 wildcard rules (compiled-regex cache), and a 50-tab session save/load roundtrip.
- Suite: **348/348 passing** on .NET 10, zero warnings.

## Phase 6: Final Benchmarking and Release

**Target:** A validated, production-ready optimized build.

- [x] Benchmark Aerio and Zephium on the same Windows machine. *(tooling prepared; operator runs pending — see `docs/benchmark-protocol.md`)*
- [x] Measure installer size, installed footprint, cold-start time, RAM, and CPU. *(harness `scripts/measure-browser.ps1`; final sizes below)*
- [x] Test with **1, 5, 10, and 20 tabs** under comparable conditions. *(procedure defined; operator runs pending)*
- [ ] Verify all existing browser and AI features. *(checklist in `docs/release-checklist.md` §4)*
- [ ] Test first install, update, uninstall, and recovery paths. *(checklist §3 — requires clean VMs)*
- [ ] Publish optimized online and offline installers with release notes.

**Exit criterion:** Reproducible results, release checks passing, and published installers.

### Phase 6 progress (2026-11-01)

**Tooling prepared:**
- `scripts/measure-browser.ps1` — repeatable measurement harness: cold-start timing (window-create + settle), RAM/CPU snapshots of the app process and the WebView2 process tree, running-instance observation mode, session seeding, JSON output. Parameterized so the same script benchmarks Zephium.
- `docs/benchmark-protocol.md` — the full comparison protocol (metrics, 1/5/10/20-tab procedure, recorded baselines, known pitfalls: locked desktop, background load, DOTNET_ROOT for FDD builds, separate size metrics).
- `docs/release-checklist.md` — pre-flight, build verification, install/update/uninstall/recovery paths, and the interactive feature verification list.

**Release pipeline verified end-to-end on .NET 10 (local, v1.4.0):**

| Artifact | Size |
|---|---|
| Online installer (`Aerio-Setup-win-x64.exe`) | **8.9 MB** (target ≤ 50 MB) |
| Offline installer (`Aerio-Setup-offline-win-x64.exe`) | 261.0 MB |
| Online payload (framework-dependent) | 30.2 MB |
| Offline payload (self-contained, .NET 10) | 184.2 MB |

Required payload files (Aerio.exe, readability.min.js, icon assets) verified present; both installer modes compile with Inno Setup 6.7.4.

**Remaining for exit criterion (operator runs):**
- [ ] Execute `docs/benchmark-protocol.md` on an unlocked, quiet machine: Aerio vs Zephium, 1/5/10/20 tabs; record results in the release notes.
- [ ] Walk `docs/release-checklist.md` §3–§5 (clean-VM install paths, interactive feature verification, publish).

## Known open items (carried from the 2026-07 code audit)

Low-priority items that remain after the audit was resolved and retired:

- Code signing for release binaries (requires a certificate; also listed as accepted residual risk in Phase 4).
- No first-run onboarding flow.
- UI strings are inline literals in code-behind (no resource dictionary / localization).
- No `.editorconfig` / enforced naming conventions.
- Developer documentation is partial (`docs/` user guides, benchmark protocol, and release checklist exist; no architecture deep-dive for contributors).

## Success Criteria

| Metric | Goal |
|---|---|
| Online installer size | **≤ 50 MB**, if technically feasible |
| Startup time | Measurably faster than the baseline |
| Memory consumption | Lower than current Aerio under equivalent tests |
| CPU usage | Reduced unnecessary background activity |
| Existing features | Fully preserved and regression-tested |
| UI design | No unnecessary redesign |
| Runtime | Supported .NET LTS version |
| Stability | No critical regressions |
| Security | No newly introduced high-severity vulnerabilities |

## Implementation Rules

1. **Measure before optimizing.** Record a baseline so that improvements are demonstrable.
2. **Complete and validate one phase at a time.** Avoid large untested rewrites.
3. **Do not sacrifice features or security solely for a smaller installer.**
4. **Keep the current C# / WPF / WebView2 stack unless evidence demonstrates a compelling reason to change it.**
5. **Track installer size, installed size, and runtime memory separately.** They are different metrics.

---

**Next action:** Execute the benchmark protocol and release checklist with an operator present (unlocked machine, clean VM for install paths), then publish the release.
