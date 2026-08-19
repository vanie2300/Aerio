# Tabloid Browser

Tabloid Browser is a personal Windows browser shell built with C#, WPF, and Microsoft WebView2.

## Features

- Compact dark gaming-styled UI with light theme option
- WebView2-based browsing with tabbed navigation
- Address/search bar with multiple search engine support (DuckDuckGo, Startpage, Brave, SearXNG, custom)
- Start page with quick search and quick links
- Local bookmarks, history, and downloads pages
- Bookmark import/export
- Per-site camera, microphone, location, notification, clipboard, and autoplay permission rules
- Inactive tab hibernation (configurable timeout) with RAM usage display
- Audio detection and per-tab mute toggle
- Native page caching for fast tab switching
- Start page background customization (Default gradient or custom image)
- Accent color picker (Blue, Purple, Gray, Green, White)
- Custom application icon
- Tracker blocking with custom blocklist and per-site allowlist
- Ad blocking (uBlock Origin-style EasyList filtering)
- Session restore, private browsing mode
- Developer tools (F12) toggle
- Reader mode for clean article reading
- Chrome extension support
- Find in page (Ctrl+F)
- Zoom controls
- Split pane view
- Keyboard shortcuts
- Force dark pages
- AI chat sidebar with OpenAI-compatible API support
- AI agent with tool calling (web search, page reading, navigation)
- Multiple AI provider support (Ollama, OpenAI, Groq, OpenRouter)
- AI settings management (system prompt, temperature, max tokens)

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
│       ├── the app icon.ico
│       ├── The app icon.png
│       ├── 4578.jpg
│       └── Fonts/
├── Processes/
│   ├── AI/                                  — AI assistant services
│   │   ├── AiService.cs                    — OpenAI-compatible API client
│   │   ├── AiTools.cs                      — Tool calling models and parsers
│   │   ├── AiToolDefinitions.cs            — Tool definitions and executor
│   │   ├── WebSearchService.cs             — Web search (DuckDuckGo, SearXNG, custom)
│   │   └── ChatMessage.cs                  — Chat message model
│   ├── Models/                              — Data models
│   │   ├── BrowserTab.cs                   — Tab model
│   │   ├── BrowserTabKind.cs               — Tab type enum
│   │   ├── Theme.cs                        — Theme definitions
│   │   └── ExtensionInfo.cs                — Chrome extension model
│   └── Services/                            — Business logic / persistence
│       ├── BrowserDataStore.cs             — Settings, bookmarks, history persistence
│       ├── AdBlockEngine.cs                — Ad/tracker blocking engine
│       └── MediaService.cs                 — Media detection service
```

## Data storage

```text
%LOCALAPPDATA%\TabloidBrowser\Data
```

## Requirements

- Windows 10 or Windows 11
- .NET 8 SDK or newer
- Microsoft Edge WebView2 Runtime

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

## Installed SDK

This workspace was verified with the user-profile .NET SDK at:

```text
%USERPROFILE%\.dotnet\dotnet.exe
```

## Contributing

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

### Development Guidelines

- Follow existing code style and conventions
- Add comments for complex logic
- Test changes with both light and dark themes
- Verify AI features work with Ollama (local) and cloud providers
