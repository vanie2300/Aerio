# Aerio Release Checklist (Phase 6)

Complete every section before publishing a release. CI (`.github/workflows/ci.yml`)
builds both installers automatically on every push to `main`; tagged pushes run
`.github/workflows/release.yml` and publish the GitHub release.

## 1. Pre-flight

- [ ] `dotnet test TabloidBrowser.Tests` — full suite green (expect ≥ 348 tests).
- [ ] `dotnet list package --vulnerable --include-transitive` — clean.
- [ ] Version bumped in `TabloidBrowser.csproj` (`<Version>`) and installer
      default in `installer/Aerio.iss` (`AppVersion`).

## 2. Build verification (CI artifacts, or locally)

- [ ] Online installer `Aerio-Setup-win-x64.exe` built and under the 50 MB target.
- [ ] Offline installer `Aerio-Setup-offline-win-x64.exe` built.
- [ ] Portable ZIP `Aerio-win-x64.zip` built (self-contained).
- [ ] CI size report (step summary) shows all three artifacts with expected sizes.

## 3. Install-path testing (clean VM strongly recommended)

- [ ] **First install — online installer** on a machine *with* .NET 10 Desktop
      Runtime and WebView2: installs without downloading anything, launches.
- [ ] **First install — online installer** on a machine *without* .NET 10:
      setup downloads the runtime automatically, then completes and launches.
- [ ] **First install — offline installer** on a machine without either runtime:
      installs fully offline, WebView2 runtime installed if missing, launches.
- [ ] **Update path**: install the previous release, run the new installer over
      it — settings, bookmarks, history, and session survive the upgrade.
- [ ] **Uninstall**: clean removal; user data under
      `%LOCALAPPDATA%\TabloidBrowser` remains (by design) unless cleared in-app.
- [ ] **Recovery**: after a kill -9 style termination, the next launch restores
      the last saved session without user-visible corruption.

## 4. Feature verification (interactive)

- [ ] Tabs: open, switch, drag-reorder, pop-out to window, close; audio tab
      shows indicator and is never hibernated while playing.
- [ ] Navigation: address bar, back/forward/reload, search keywords (yt/wiki),
      suggestions from history + bookmarks, external protocol links.
- [ ] Tab hibernation: background tab sleeps after the configured idle time,
      wakes on activation with scroll position preserved.
- [ ] Session restore: tabs and scroll positions return after restart.
- [ ] Bookmarks: add, remove, import, export; bookmark bar.
- [ ] Downloads: normal download recorded, executable types prompt, interrupt
      shows status message.
- [ ] Ad/tracker blocking: privacy indicator counts blocked requests; site
      allowlist respected.
- [ ] Reader mode: detection, enter/exit, content rendering.
- [ ] Find in page, zoom, print, full screen, private window (separate profile
      folder, cleaned on exit).
- [ ] Settings: theme/accent/background/custom background image, tab sleep
      timing, search engine carousel, keyboard shortcut editor, shortcuts tiles.
- [ ] Auto-hide browser controls: chrome slides away after idle and returns
      from the top edge; the collapse/expand animates smoothly (rows and fade
      together, no snap); reversing mid-animation doesn't jump.
- [ ] Extensions: uBlock Origin enabled, settings page lists it; extension
      install from CRX prompt works.
- [ ] AI (with a real provider key): chat, streaming updates, tool calls
      (search, open/navigate, read page), page-context confirmation, workspace
      tab management, prompt-injection redaction visible on a page with
      injected instructions.
- [ ] Self-update: check for updates finds the latest published release.

## 5. Publish

- [ ] Tag `vX.Y.Z` and push (or let CI's automatic versioning release from main).
- [ ] GitHub release contains: online installer, offline installer, portable ZIP,
      generated release notes; sizes match the CI size report.
- [ ] Benchmark results from `docs/benchmark-protocol.md` appended to the
      release notes (Aerio vs Zephium table).
