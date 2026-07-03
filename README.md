# Tabloid Browser

Tabloid Browser is a personal Windows browser shell built with C#, WPF, and Microsoft WebView2.

## Features

- Compact dark gaming-styled UI with light theme option
- WebView2-based browsing with tabbed navigation
- Address/search bar with multiple search engine support
- Start page with DuckDuckGo quick search and quick links
- Local bookmarks, history, and downloads pages
- Per-site camera, microphone, location, notification, clipboard, and autoplay permission rules
- Inactive tab hibernation (configurable timeout) with RAM usage display
- Audio detection and per-tab mute toggle
- Native page caching for fast tab switching
- Start page background customization (Default gradient or custom image)
- Accent color picker (Blue, Purple, Gray, Green, White)
- Custom application icon
- Tracker blocking with custom blocklist and per-site allowlist
- Session restore, private browsing mode
- Developer tools (F12) toggle

## Project structure

```
TabloidBrowser/
├── App.xaml / App.xaml.cs          — Application entry
├── MainWindow.xaml / .xaml.cs      — Window chrome, tab strip, start page, core logic
├── Models/                          — Data models
│   ├── BrowserTab.cs
│   ├── BrowserTabKind.cs
│   └── Theme.cs
├── Services/                        — Business logic / persistence
│   └── BrowserDataStore.cs
├── Controls/                        — Reusable UI widgets
│   └── Widgets.cs
├── NativePages/                     — Native page builders (settings, bookmarks, etc.)
│   └── PageBuilders.cs
└── Assets/                          — Static resources (icons, images)
    ├── Icon.ico / Icon.png
    └── 4578.jpg
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
