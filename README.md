# Aerio

Aerio is a personal Windows browser built with C#, WPF, and Microsoft WebView2.

## Features

- Compact dark gaming-styled UI with light theme option
- WebView2-based browsing with tabbed navigation
- Address/search bar with bookmark/history suggestions, customizable search keywords (`yt cats`, `wiki octopus`), and multiple search engines (DuckDuckGo, Startpage, Brave, SearXNG, custom)
- Start page with quick search, quick links, and customizable new-tab shortcuts
- Local bookmarks, history, and downloads pages
- Bookmark import/export
- Per-site camera, microphone, location, notification, clipboard, autoplay, and other permission rules
- Inactive tab hibernation (configurable timeout) with RAM usage display
- Audio detection and per-tab mute toggle
- Native page caching for fast tab switching
- Start page background customization (Default gradient or custom image)
- Accent color picker (Blue, Purple, Gray, Green, White)
- Aerio application icon
- Tracker blocking with custom blocklist and per-site allowlist
- Ad blocking and uBlock Origin extension support
- Session restore, private browsing mode
- Optional WebView2 developer tools
- Reader mode for clean article reading
- Chrome extension support
- Find in page (Ctrl+F)
- Zoom controls and full-screen mode
- Configurable keyboard shortcuts
- Force dark pages
- AI chat sidebar for local Ollama, Ollama Cloud, OpenAI, NVIDIA NIM, and custom OpenAI-compatible APIs
- AI browser tools for search, page reading, navigation, and page interaction
- Manual GitHub release update checks

## Project structure

```
TabloidBrowser/
├── TabloidBrowser.csproj
├── Run-TabloidBrowser.cmd
├── installer/Aerio.iss                      — Inno Setup installer (online + offline modes)
├── scripts/measure-browser.ps1              — Performance benchmark harness
├── docs/                                    — Benchmark protocol, release checklist, user guides
├── UI/
│   ├── App.xaml / App.xaml.cs              — Application entry
│   ├── MainWindow.xaml / MainWindow.xaml.cs — Window chrome, tab strip, core logic
│   ├── MainWindow/                          — Partial class files
│   │   ├── MainWindow.AI.cs                — AI sidebar, agent loop, tool execution
│   │   ├── MainWindow.Navigation.cs        — URL parsing, navigation
│   │   ├── MainWindow.Particles.cs         — Start page particle animation
│   │   ├── MainWindow.Permissions.cs       — Site permission management
│   │   ├── MainWindow.Tabs.cs              — Tab creation, switching, hibernation
│   │   ├── MainWindow.WebView2.cs          — WebView2 initialization and events
│   │   ├── MainWindow.UI.cs                — Chrome auto-hide, status, popups
│   │   ├── MainWindow.TabActions.cs        — Tab keyboard/mouse actions, shortcuts
│   │   ├── MainWindow.FindAndNav.cs        — Navigation buttons, find bar, print
│   │   ├── MainWindow.Reader.cs            — Reader mode
│   │   ├── MainWindow.Extensions.cs        — Extensions and uBlock management
│   │   ├── MainWindow.Shutdown.cs          — Close/save/dispose flow
│   │   └── MainWindow.Updates.cs           — GitHub release update checks
│   ├── Controls/                            — Reusable UI widgets
│   │   ├── AiSidebarControl.xaml(.cs)      — AI chat sidebar
│   │   ├── BrowserPaneControl.xaml(.cs)    — Main browser content area
│   │   ├── FindBarControl.xaml(.cs)        — Find in page bar
│   │   ├── NavToolbarControl.xaml(.cs)     — Navigation toolbar
│   │   ├── StatusBarControl.xaml(.cs)      — Bottom status bar
│   │   ├── TitleBarControl.xaml(.cs)       — Custom title bar
│   │   ├── AnimatedTabPanel.cs             — Tab strip panel animation
│   │   └── Widgets.cs                      — Reusable UI components
│   ├── NativePages/                         — Native page builders (MainWindow partials)
│   │   ├── PageBuilders.cs                 — Bookmarks, history, downloads, shared helpers
│   │   ├── PageBuilders.Settings.cs        — Settings page
│   │   ├── PageBuilders.Hymns.cs           — Hymn presenter
│   │   ├── PageBuilders.SiteData.cs        — Site permissions popup
│   │   ├── PageBuilders.Extensions.cs      — Extensions page
│   │   └── PageBuilders.AiManagement.cs    — AI management page
│   ├── Styles/                              — XAML resource dictionaries
│   └── Assets/                              — Static resources
├── Processes/
│   ├── AI/                                  — AI assistant services
│   │   ├── AiService.cs                    — AI provider API client
│   │   ├── AiToolDefinitions.cs            — Tool definitions and executor
│   │   ├── AiTabManager.cs                 — AI-owned tab lifecycle
│   │   ├── WebSearchService.cs             — Web search
│   │   ├── ContentSanitizer.cs             — Prompt-injection defenses
│   │   └── ChatMessage.cs                  — Chat message model
│   ├── Models/                              — Data models
│   │   └── BrowserTab.cs                   — Tab model (plus kind, theme, extension models)
│   └── Services/                            — Business logic / persistence
│       ├── BrowserDataStore.cs             — Settings and browser data persistence
│       ├── AddressSuggestionService.cs     — Address-bar suggestions
│       ├── SearchKeywordService.cs         — Search keyword handling
│       ├── AdBlockEngine.cs                — Ad/tracker blocking engine
│       ├── TrackerPolicy.cs                — Tracker allow/blocklist matching
│       ├── TabSleepPolicy.cs               — Tab hibernation decisions
│       ├── SystemMemoryPressure.cs         — Memory pressure signal
│       ├── NewTabShortcutPolicy.cs         — Shortcut validation
│       ├── HymnService.cs                  — Hymn data
│       └── AerioUpdateService.cs           — GitHub release update checks
├── TabloidBrowser.Tests/                     — xUnit test project
└── .github/workflows/                        — Build and tagged-release automation
```

## Data storage

```text
%LOCALAPPDATA%\TabloidBrowser\Data
```

## Requirements

- Windows 10 version 2004 (build 19041) or later, or Windows 11
- Microsoft Edge WebView2 Runtime (preinstalled on Windows 10/11; installed automatically by the setup installer when missing)

The .NET 10 SDK is additionally required to build or run from source. The published app comes in two flavors: the **online installer** ships the lightweight framework-dependent app and downloads the .NET 10 Desktop Runtime during setup only if it is missing; the **offline installer** bundles the full self-contained build plus the offline WebView2 Evergreen Runtime installer. The portable ZIP is self-contained and only requires the WebView2 Runtime to be present.

## Run

Double-click:

```text
Run-TabloidBrowser.cmd
```

Or run from PowerShell:

```powershell
& "$env:USERPROFILE\.dotnet\dotnet.exe" restore
& "$env:USERPROFILE\.dotnet\dotnet.exe" run
```

## Releases

Every push to `main` runs tests, builds both Windows installers and the portable app, and automatically creates a GitHub Release with a new patch version (for example, `v1.4.0` becomes `v1.4.1`). The release includes `Aerio-Setup-win-x64.exe` (online, framework-dependent), `Aerio-Setup-offline-win-x64.exe` (self-contained), and `Aerio-win-x64.zip`. The version is incremented from the highest existing `vMAJOR.MINOR.PATCH` tag. Pushes to `dev` and pull requests targeting `main` build and upload Actions artifacts without publishing a release.

Pushing a version tag such as `v1.4.1` directly also runs the Windows release workflow. It runs the test suite, publishes both the framework-dependent and self-contained x64 builds, creates both installers, uploads the self-contained app folder as the `Aerio-Windows-x64` workflow artifact, and attaches both installers and the portable ZIP to the GitHub release.

To install, download and run `Aerio-Setup-win-x64.exe`. It installs Aerio, checks for the WebView2 Evergreen Runtime and the .NET 10 Desktop Runtime, and downloads either one automatically only if missing. Administrator permission is required to install system components. For machines without internet during setup, `Aerio-Setup-offline-win-x64.exe` bundles everything. Alternatively, download and extract `Aerio-win-x64.zip` and run `Aerio.exe` from the extracted folder; the portable ZIP requires the WebView2 Runtime to be installed separately.

In Aerio, open **Settings → Advanced → Updates → Check for updates** to check for a newer release. If one is available, choose **Download installer** to download and optionally run the Windows installer without opening GitHub.

## Contributing

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

### Development Guidelines

- Follow existing code style and conventions
- Add comments for complex logic
- Run the test suite with `dotnet test TabloidBrowser.Tests\TabloidBrowser.Tests.csproj`
- Test UI changes with both light and dark themes
- Update documentation when changing user-facing behavior or settings
