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

## Known measurement pitfalls

- **Framework-dependent builds** need the .NET 10 Desktop Runtime installed
  machine-wide (the installer does this); otherwise the app shows the .NET
  "runtime not found" dialog, which the harness will mistake for a window.
  Use `-DotNetRoot` for local runs against a user-dir runtime.
- **Locked desktop / secure desktop**: WPF pauses rendering and WebView2
  environment creation can stall; cursor probes fail. Never benchmark locked.
- **Background load**: regex compilation of the ad-block list (48k rules) at
  startup stretches under CPU contention and will skew settle-time numbers.
- **Session state changes startup**: the harness resets the session file for
  cold-start runs; for ObserveRunning runs, let the browser fully restore first.
- Measure installer size, installed size, and runtime RAM as **separate**
  metrics (roadmap rule 5) — never as one number.
