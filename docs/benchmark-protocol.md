# Aerio Benchmark Protocol (Phase 6)

Purpose: reproducible comparison of Aerio against Zephium (or any prior baseline)
on the same Windows machine, and the pre-release verification checklist.

> Requirements: unlocked desktop, machine otherwise idle (close heavy
> background work), same power profile for every run, same session state.

## Tools

`scripts/measure-browser.ps1` — measurement harness. Works for any WebView2-based
browser via parameters.

```powershell
# Cold-start benchmark (3 runs, session reset before each):
powershell -File scripts\measure-browser.ps1 -ExePath <path-to-exe> `
    -ProcessName Aerio -WebviewFilter TabloidBrowser -OutFile results-aerio.json

# For a framework-dependent build with the runtime in a non-default location:
#   add  -DotNetRoot <dotnet-root-dir>

# Idle CPU / RAM of an already-running instance (30 s observation):
powershell -File scripts\measure-browser.ps1 -ObserveRunning `
    -ProcessName Aerio -WebviewFilter TabloidBrowser

# Zephium (adjust the child-process filter to its user-data folder name):
powershell -File scripts\measure-browser.ps1 -ExePath <path-to-Zephium.exe> `
    -ProcessName Zephium -WebviewFilter <ZephyUserFolder> -OutFile results-zephium.json
```

## Metrics and how to take them

| Metric | Method |
|---|---|
| Installer download size | file size of `Aerio-Setup-win-x64.exe` / Zephium installer |
| Installed footprint | size of `C:\Program Files\Aerio` (+ WebView2 user-data folder is excluded — runtime is shared) |
| Cold-start time | harness: `WindowCreateSec` (UI up) and `SettleSec` (startup burst done); median of 3 runs |
| Idle RAM | harness cold-start snapshot, or ObserveRunning after 5 min of real use |
| Idle CPU | harness `ObserveRunning` on the Start page, and on a single real web page |
| Multi-tab RAM (1/5/10/20 tabs) | manual, see below |
| Multi-tab CPU | harness `ObserveRunning` while the tabs are open |

### Multi-tab procedure (manual, per tab count: 1, 5, 10, 20)

1. Launch the browser fresh (no restored session).
2. Open the target number of tabs by actually navigating each to a real page
   (use the same 5–10 representative sites for both browsers, same order).
3. Wait 5 minutes (Aerio's default sleep timer will hibernate background tabs —
   record that as the hibernated steady state).
4. Run the harness in `-ObserveRunning` mode for 30 s.
5. Optionally wake every tab and immediately sample again (loaded steady state).
6. Record: main process RAM, WebView2-tree RAM and process count, idle CPU %.

The 1/5/10/20 comparison is only meaningful if both browsers get the same sites
in the same order with the same wait times.

## Recorded baseline (Aerio, during the optimization phases)

| Scenario | Value |
|---|---|
| Idle CPU, Start page (before Phase 2) | 20.1% of one core |
| Idle CPU, Start page (after Phase 2) | 0.8–3.1% (varies with machine load) |
| Settings carousel (before Phase 2) | spawned a second full WebView2 browser process tree |
| Settings carousel (after) | +1 renderer in the shared tree |
| Publish payload (online installer) | 30.2 MB raw → 8.9 MB installer |
| Offline installer | 258.7 MB (self-contained + WebView2 runtime bundled) |
| Portable ZIP | 70.7 MB |

## Recorded results (2026-11-06, installed release build)

Environment: AMD Ryzen 7 7435HS (8C/16T), 8 GB RAM, NVIDIA RTX 2050,
Windows 11 build 26300. Cold-start runs reset the session file; observation
windows are 30 s via `measure-browser.ps1 -ObserveRunning`; microbenchmarks
are the `PerformanceRegressionTests` ad-block cases against the real cached
EasyList (53,233 candidate rules, 430 in the fallback scan lists).

| Metric | Value | Method |
|---|---|---|
| Cold start, window created | **1.20 s** median of 3 (1.08–1.88) | harness cold-start, session reset per run |
| Cold start, fully settled | **3.22 s** median of 3 | CPU-idle heuristic + 12 s floor |
| Startup RAM (main process) | ~176 MB private | harness snapshot at settle |
| Idle CPU, Start page (particles on) | **0.2% of one core** | 30 s ObserveRunning |
| YouTube playback CPU | **42% of one core** steady (79% during page-load burst) | ObserveRunning during active playback |
| YouTube playback RAM | ~1.5 GB tree private, 13 processes (renderer 376 MB + GPU 374 MB dominate) | parent-based tree snapshot |
| Ad-block lookup, worst case | **~48 µs/request** (19.2 ms / 400 non-matching lookups) | unit microbenchmark |
| Ad-block compile at startup | 46.3 ms (UI thread, before first await) | unit microbenchmark |
| 20 tabs, hibernated steady state | **~1.0 GB tree private, 3.3% of one core idle** — only 2–3 renderers alive; background tabs suspended/discarded by the 2-min timer + memory pressure | 30 s ObserveRunning + tree snapshot after batch-opening 20 real sites |
| Online installer | 8.9 MB | file size |
| Offline installer | 261.0 MB | file size |
| App payload on disk | ~59 MB (53 files) | build output |

### ReadyToRun A/B (2026-11-06, same source, same machine, harness 3-run medians)

`PublishReadyToRun=true` pre-compiles the app's assemblies at publish time,
removing most app-side JIT work from every launch. Framework-dependent R2R
precompiles only the app's own code (the shared runtime keeps its JIT), so
the size cost is small.

| Metric | R2R off | R2R on | Delta |
|---|---:|---:|---|
| Window created (median) | 1.16 s | **1.00 s** | −14% |
| Window created (cold-cache first run) | 2.07 s | **1.54 s** | −26% |
| Fully settled (median) | 3.18 s | **3.02 s** | −5% |
| Startup CPU (median core-seconds) | 4.41 | **3.23** | −27% |
| Publish size | 28.7 MB | 31.1 MB | +2.4 MB (+8%) |
| Startup RAM (main, private) | ~175 MB | ~179 MB | +4 MB |

Every paired run favored R2R (1.54<2.07, 0.99<1.16, 1.00<1.12); the startup
CPU reduction is the most robust signal (least scheduling noise). Adopted in
`ci.yml` and `release.yml` publish steps. The settle window (≈3 s) is
dominated by WebView2 environment creation and async startup, not app JIT —
the next startup lever to probe is deferring extension loading until after
first paint.

### Published reference points (for context, not same-hardware comparisons)

- **Engine**: Aerio renders with the WebView2 Evergreen runtime — the same
  Blink engine as Microsoft Edge. Speedometer 3.1 aggregations place Edge at
  #2 of 17 Windows browsers (BrowseRating, 2026-10-03: Edge 20.23, Chrome
  19.37, Firefox 19.18); Aerio inherits that position by construction.
- **Cold start**: published lab figures of Edge 1.6 s / Chrome 1.8 s /
  Firefox 2.1 s (GuideTechly, 2026-06-02, i5-12400 + NVMe) — different
  hardware, so band comparison only; Aerio's 1.20 s window-create sits at the
  favorable end of that band.
- **RAM by tab count** (architecture-based estimates, SuperchargeBrowser,
  2026-07-12): Firefox ~0.8/2.0/3.8 GB, Edge ~1.1/2.8/4.5 GB, Chrome
  ~1.2/3.5/6.5 GB at 10/30/50 tabs. Aerio's hibernation implements the same
  sleep→discard chain as Edge's Sleeping Tabs (suspend first, full WebView
  disposal under memory pressure) with a 2-minute default timer, and its
  measured ~1.5 GB tree **during active YouTube playback** sits in Edge's
  10-idle-tab band under a heavier workload.

## Known measurement pitfalls

- **Framework-dependent builds** need the .NET 10 Desktop Runtime installed
  machine-wide (the installer does this); otherwise the app shows the .NET
  "runtime not found" dialog, which the harness will mistake for a window.
  Use `-DotNetRoot` for local runs against a user-dir runtime.
- **Locked desktop / secure desktop**: WPF pauses rendering and WebView2
  environment creation can stall; cursor probes fail. Never benchmark locked.
- **Background load**: regex compilation of the ad-block list (50k+ rules,
  ~46 ms measured) at startup stretches under CPU contention and will skew
  settle-time numbers.
- **Session state changes startup**: the harness resets the session file for
  cold-start runs; for ObserveRunning runs, let the browser fully restore first.
- Measure installer size, installed size, and runtime RAM as **separate**
  metrics (roadmap rule 5) — never as one number.
