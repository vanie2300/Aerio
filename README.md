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
- AI chat sidebar with Ollama Cloud and NVIDIA NIM support
- AI browser tools for search, page reading, navigation, and page interaction
- Manual GitHub release update checks

## Project structure

```
TabloidBrowser/
├── TabloidBrowser.csproj
├── Run-TabloidBrowser.cmd
├── UI/
│   ├── App.xaml / App.xaml.cs              — Application entry
│   ├── MainWindow.xaml / MainWindow.xaml.cs — Window chrome, tab strip, core logic
│   ├── MainWindow/                          — Partial class files
│   │   ├── MainWindow.AI.cs                — AI sidebar, agent loop, tool execution
│   │   ├── MainWindow.Navigation.cs        — URL parsing, navigation, downloads
│   │   ├── MainWindow.Particles.cs         — Start page particle animation
│   │   ├── MainWindow.Permissions.cs       — Site permission management
│   │   ├── MainWindow.Tabs.cs              — Tab creation, switching, hibernation
│   │   ├── MainWindow.Updates.cs           — GitHub release update checks
│   │   ├── MainWindow.UI.cs                — Settings, menus, bookmarks, history UI
│   │   └── MainWindow.WebView2.cs          — WebView2 initialization and events
│   ├── Controls/                            — Reusable UI widgets
│   │   ├── AiSidebarControl.xaml(.cs)      — AI chat sidebar
│   │   ├── BrowserPaneControl.xaml(.cs)    — Main browser content area
│   │   ├── FindBarControl.xaml(.cs)        — Find in page bar
│   │   ├── NavToolbarControl.xaml(.cs)     — Navigation toolbar
│   │   ├── StatusBarControl.xaml(.cs)      — Bottom status bar
│   │   ├── TitleBarControl.xaml(.cs)       — Custom title bar
│   │   ├── AnimatedTabPanel.cs             — Tab strip panel animation
│   │   └── Widgets.cs                      — Reusable UI components
│   ├── NativePages/
│   │   └── PageBuilders.cs                 — Native page builders (settings, bookmarks, etc.)
│   ├── Styles/                             — XAML resource dictionaries
│   │   ├── BrushesAndEffects.xaml
│   │   ├── ButtonStyles.xaml
│   │   ├── ComboBoxStyles.xaml
│   │   ├── EasingFunctions.xaml
│   │   ├── MenuStyles.xaml
│   │   ├── ScrollBarStyles.xaml
│   │   └── SharedResources.xaml
│   └── Assets/                             — Static resources
│       ├── Aerio.ico
│       ├── Aerio.png
│       ├── the app icon.ico
│       ├── startpage.png
│       └── Fonts/
├── Processes/
│   ├── AI/                                  — AI assistant services
│   │   ├── AiService.cs                    — AI provider API client
│   │   ├── AiTools.cs                      — Tool calling models and parsers
│   │   ├── AiToolDefinitions.cs            — Tool definitions and executor
│   │   ├── WebSearchService.cs             — Web search
│   │   └── ChatMessage.cs                  — Chat message model
│   ├── Models/                              — Data models
│   │   ├── BrowserTab.cs                   — Tab model
│   │   ├── BrowserTabKind.cs               — Tab type enum
│   │   ├── Theme.cs                        — Theme definitions
│   │   └── ExtensionInfo.cs                — Chrome extension model
│   └── Services/                            — Business logic / persistence
│       ├── BrowserDataStore.cs             — Settings and browser data persistence
│       ├── AddressSuggestionService.cs     — Address-bar suggestions
│       ├── SearchKeywordService.cs         — Search keyword handling
│       ├── AdBlockEngine.cs                — Ad/tracker blocking engine
│       ├── MediaService.cs                 — Media detection service
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
- Microsoft Edge WebView2 Runtime

The .NET 8 SDK is additionally required to build or run from source. The published Windows ZIP is self-contained and does not require the .NET runtime to be installed separately.

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

Pushes to `main` or `dev` and pull requests targeting `main` run CI: it tests the project, publishes the self-contained Windows x64 app, and uploads the complete app folder as the `Aerio-Windows-x64` Actions artifact. Open the successful workflow run and download that artifact to get a build without creating a release.

Pushing a version tag such as `v1.4.1` runs the Windows release workflow. It runs the test suite, publishes a self-contained x64 build, uploads the complete Windows app folder as the `Aerio-Windows-x64` workflow artifact, and attaches `Aerio-win-x64.zip` to the GitHub release.

To install a release, download and extract `Aerio-win-x64.zip`, then run `Aerio.exe` from the extracted folder. Keep the other files beside it; the executable depends on bundled app resources. The target PC still needs the Microsoft Edge WebView2 Runtime.

In Aerio, open **Settings → Advanced → Updates → Check for updates** to check for a newer release. If one is available, Aerio opens its GitHub release page; download and replace the portable app files manually after closing Aerio.

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
