# Phase 0 — Existing System Audit

## 0.1 Tab Model

### BrowserTab class (`Processes/Models/BrowserTab.cs`)

Key properties:

```text
Kind            BrowserTabKind       Start | Web | Bookmarks | History | Downloads | Settings | AiManagement
WebView         WebView2?            null until initialized
Address         string               Current URL
Title           string               Display title
IsActive        bool                 Whether this is the active tab
IsLoading       bool                 Navigation in progress
IsSleeping      bool                 Hibernated
IsReaderMode    bool                 Reader view active
AiChatHistory   List<ChatMessage>?   Per-tab AI chat history
LastActiveUtc   DateTimeOffset       For sleep timer
ZoomLevel       double               Page zoom
ScrollX/Y       double               Saved scroll position
RestoredUrl     string?              URL to navigate on activation (lazy restore)
IsRestoredPlaceholder bool           Tab is a placeholder awaiting navigation
CleanupActions  List<Action>         Event handler unsubscribe callbacks
```

**Missing from roadmap:**
- No `Owner` field (User vs AI)
- No `AiTaskId` field
- No tab ID — identity is by object reference (`ReferenceEquals`)
- No creation timestamp

### Tabs collection (`MainWindow.Tabs.cs`)

```text
_tabs           ObservableCollection<BrowserTab>    Main collection
_activeTab      BrowserTab?                         Current active tab
_tabOperationLock SemaphoreSlim(1,1)                Concurrency guard
```

All tab mutations go through `_tabOperationLock`. The collection is bound to the UI tab strip.

### BrowserTabKind enum

```text
Start           Start page (quick links)
Web             Regular webpage
Bookmarks       Native bookmarks page
History         Native history page
Downloads       Native downloads page
Settings        Native settings page
AiManagement    Native AI management page
```

AI tabs would need to be `Web` kind. No way to distinguish AI-owned Web tabs from user-owned Web tabs.

---

## 0.2 Lifecycle

### CREATE flow

```text
AddStartTabAsync()  /  AddWebTabAsync(url)  /  AddNativePageTabAsync(kind)
       │
       ▼
_tabOperationLock.WaitAsync()
       │
       ▼
new BrowserTab { Kind, Title, Address }
       │
       ▼
_tabs.Add(tab)
       │
       ▼
_tabOperationLock.Release()
       │
       ▼
ActivateTabAsync(tab)
       │
       ├─ EnsureWebViewAsync(tab)          ← Creates WebView2, attaches events
       ├─ AttachActiveWebView(tab)         ← Adds WebView2 to UI host
       └─ NavigateWebAsync(tab, url)       ← For Web tabs with a URL
```

### WebView2 initialization (`MainWindow.WebView2.cs:169-291`)

```text
EnsureWebViewAsync(tab)
       │
       ├─ Creates WebView2 control
       ├─ Calls EnsureCoreWebView2Async(_environment)
       ├─ Registers events:
       │    NavigationStarting
       │    NavigationCompleted
       │    WebResourceRequested
       │    WebMessageReceived
       │    PermissionRequested
       │    NewWindowRequested
       │    DocumentTitleChanged
       │    DownloadStarting
       │    ContextMenuRequested
       │    IsDocumentPlayingAudioChanged
       │    Find.MatchCountChanged
       │    IsMutedChanged
       │
       ├─ Injects scripts:
       │    ScrollTrackingScript
       │    ForceDarkScript (if enabled)
       │    __tblBridge bridge
       │
       ├─ Loads extensions (first tab only)
       ├─ Sets _webViewToTabMap[cwv] = tab
       └─ tab.IsWebViewInitialized = true
```

### CLOSE flow (`MainWindow.Tabs.cs:687-754`)

```text
CloseTabAsync(tab)
       │
       ▼
_tabOperationLock.WaitAsync()
       │
       ├─ index = _tabs.IndexOf(tab)
       ├─ wasActive = (_activeTab == tab)
       ├─ _activeTab = null (if wasActive)
       ├─ _tabs.Remove(tab)
       │
       ├─ If tab.WebView != null:
       │    ├─ tab.RunCleanup()              ← Unsubscribes all events
       │    ├─ RemoveElementFromParent(wv)   ← Removes from visual tree
       │    ├─ tab.WebView.Dispose()
       │    ├─ tab.WebView = null
       │    ├─ tab.IsWebViewInitialized = false
       │    └─ Clears _profile if no more WebView2s alive
       │
       ├─ If _tabs.Count == 0:
       │    └─ AddStartTabAsync()            ← Always keeps at least one tab
       │
       └─ If wasActive:
            └─ nextTab = _tabs[clamp(index-1)]
       │
       ▼
_tabOperationLock.Release()
       │
       ▼
ActivateTabAsync(nextTab)   ← If there was an active tab
```

### Activation flow (`MainWindow.Tabs.cs:271-336`)

```text
ActivateTabAsync(tab)
       │
       ├─ prevTab.IsActive = false
       ├─ _activeTab = tab
       ├─ tab.IsActive = true
       ├─ tab.IsSleeping = false
       │
       ├─ If Web tab:
       │    ├─ EnsureWebViewAsync(tab)
       │    └─ AttachActiveWebView(tab)     ← Adds WebView2 to host
       │
       ├─ If Native tab:
       │    └─ RenderNativeSurface(tab)     ← Builds HTML in WebView2
       │
       └─ Updates: address bar, nav buttons, bookmark button, memory, reader, zoom
```

### Sleep flow

```text
SleepTimer_Tick (every 60s)
       │
       ├─ Finds tabs where:
       │    tab != activeTab
       │    tab.Kind == Web
       │    tab.IsSleeping == false
       │    tab.LastActiveUtc older than TabSleepMinutes
       │
       ├─ HibernateTabAsync(tab):
       │    ├─ CaptureScrollPosition
       │    ├─ TrySuspendAsync (WebView2 TrySuspend)
       │    │    └─ If success: tab.IsSleeping = true, tab.SuspendedForSleep = true
       │    └─ If fail: DisposeTabWebView
       │         └─ tab.WebView = null, tab.RestoredUrl = url, tab.IsRestoredPlaceholder = true
       │
       └─ DiscardSuspendedTabAsync (after 30min suspended):
            └─ DisposeTabWebView
```

---

## 0.3 Current AI Tools

### Architecture

```text
MainWindow.AI.cs
    ├─ AgentLoopAsync()              ← Main loop (max iterations)
    ├─ EnsureAiService()             ← Creates AiService + AiToolExecutor
    └─ AiSend_Click()                ← Entry point from UI

AiToolDefinitions.cs
    ├─ GetAllTools(enabledTools)     ← Returns filtered tool list
    └─ AiToolExecutor                ← Executes each tool

AiService.cs
    ├─ CompleteWithToolsAsync()      ← Non-streaming
    └─ CompleteStreamingAsync()      ← SSE streaming
```

### How each tool works today

| Tool | What it does | Operates on |
|------|-------------|-------------|
| `web_search` | Calls `WebSearchService.SearchAsync()` | External API only |
| `search_and_read` | Searches, navigates to result N, reads page | **User's active tab** |
| `search_followup` | Gets search suggestions | External API only |
| `get_page_content` | Runs JS on current page to extract text | **User's active tab** |
| `get_page_metadata` | Runs JS to extract title/meta/headings | **User's active tab** |
| `extract_page_links` | Runs JS to extract `<a>` elements | **User's active tab** |
| `navigate_to` | Navigates to URL, then reads content | **User's active tab** |
| `open_tab` | Creates new tab with URL | **Shared tab list** |

### Critical finding: No tab isolation

```text
AiToolExecutor._navigateTo
    → MainWindow.NavigateToUrlAsync(url)
    → NavigateWebAsync(_activeTab, url)     ← CHANGES THE USER'S ACTIVE TAB

AiToolExecutor._openTab
    → MainWindow.OpenBackgroundTabAsync(url, title)
    → _tabs.Add(tab)                         ← ADDS TO SHARED TAB LIST
    → NavigateWebAsync(tab, url)
```

The AI directly mutates the user's browsing state:
- `navigate_to` overwrites whatever the user is looking at
- `open_tab` creates tabs the user didn't ask for
- `search_and_read` navigates the user away from their current page
- `get_page_content` reads the user's page without any authorization check

### Agent loop (`MainWindow.AI.cs:780-839`)

```text
AgentLoopAsync(messages, ct, pageContextActive)
       │
       ├─ BeginStreamingBubble()
       │
       └─ while (iteration < maxIterations):
            │
            ├─ CompleteStreamingAsync(messages, onContentDelta, tools, "auto", ct)
            │
            ├─ If response has ToolCalls:
            │    ├─ CommitStreamingContent()
            │    ├─ For each tool call:
            │    │    ├─ If sensitive tool + page context active:
            │    │    │    └─ ConfirmSensitiveToolAsync()  ← MessageBox
            │    │    ├─ AddAiMessage("assistant", "Using {tool}...")
            │    │    ├─ ExecuteToolAsync(toolCall, ct)
            │    │    ├─ AddAiMessage("assistant", "Used {tool}", isToolResult)
            │    │    └─ messages.Add(tool result)
            │    └─ iteration++
            │
            └─ If no tool calls: return response
```

Sensitive tool check (`IsSensitiveTool`):
```text
"navigate_to" → sensitive
"open_tab"    → sensitive
Everything else → not sensitive
```

Only triggers confirmation when `pageContextActive && settings.AiPageContextConfirmation`.

---

## 0.4 Existing Security

### ContentSanitizer (`Processes/AI/ContentSanitizer.cs`)

- 20 regex patterns for prompt injection detection
- Redacts matched patterns (replaces with `***`)
- Escapes `<page_content>`, `</page_content>`, `<system>`, `</system>` tags
- Used in `AiSend_Click` before inserting page context into messages
- `ContainsSuspiciousPatterns()` adds a WARNING to the system message

### Site Permissions (`MainWindow.Permissions.cs`)

- 13 permission types (Camera, Microphone, Geolocation, etc.)
- Per-host rules stored in `BrowserSettings.SitePermissions`
- Default: all permissions set to "Ask" (user must decide)
- WebView2 `PermissionRequested` event intercepted, deferral used
- Rules saved to settings, synced with WebView2 profile
- Private mode: permissions never saved

### AI Tool Confirmation

- Only `navigate_to` and `open_tab` are flagged as "sensitive"
- Confirmation only happens when page context is active AND `AiPageContextConfirmation` is enabled
- Uses `MessageBox.Show()` (blocks UI thread via `Dispatcher.Invoke`)
- No confirmation for `get_page_content`, `get_page_metadata`, `extract_page_links`
- No confirmation for `search_and_read` (which navigates the user's tab)

### What's missing for roadmap

- No `AiBrowserPolicy` / risk classification
- No per-tool authorization beyond the simple `IsSensitiveTool` check
- No concept of "which task is asking" or "which tab does this task own"
- No tab ownership boundary — all tools operate on user's tabs directly

---

## 0.5 Baseline Tests

```
Total:   55
Passed:  55
Failed:  0
Time:    ~1.5s
```

### Test distribution

| File | Tests | Coverage |
|------|-------|----------|
| `AiToolDefinitionsTests.cs` | 9 | Tool filtering, enable/disable, uniqueness |
| `AiResponseParserTests.cs` | 7 | Response parsing edge cases |
| `BrowserDataStoreTests.cs` | 13 | Settings defaults, records, shortcuts, providers |
| `ContentSanitizerTests.cs` | 14 | Injection patterns, sanitization, edge cases |

### What's NOT tested

- `AiToolExecutor` (no unit tests — only integration via agent loop)
- `AiService` HTTP calls (no mocking)
- `AgentLoopAsync` (UI-coupled, not testable)
- `NavigateWebAsync` / `CloseTabAsync` / `ActivateTabAsync` (UI-coupled)
- Tab lifecycle (CREATE/CLOSE/ACTIVATE)
- Token budget management
- Retry logic

---

## Summary: Gap Analysis

| Roadmap requirement | Current state | Gap |
|---------------------|--------------|-----|
| `TabOwner` enum | Does not exist | Need to add to `BrowserTab` |
| `AiTaskId` | Does not exist | Need to add to `BrowserTab` |
| `AiTask` class | Does not exist | Need to create |
| `AiWorkspace` | Does not exist | Need to create |
| `AiTaskContext` | Does not exist | Need to create |
| `AiTabManager` | Does not exist | Need to create |
| `AiBrowserPolicy` | Does not exist | Need to create |
| Tab limits | No limits | Need `MaxAiTabs` setting |
| Tab cleanup on task end | No cleanup | Need `CloseTaskTabs()` |
| Keep/ownership transfer | No concept | Need `KeepAiTab()` |
| AI tab indicators | No visual distinction | Need 🤖 badge |
| Page observation (click/type/scroll) | No tools | Need new tools + JS |
| Action verification | No verification | Need post-action checks |
| Stale element detection | No concept | Need `pageVersion` |
| Tab reuse | No concept | Need idle tab detection |

### Highest-risk items

1. **`navigate_to` changes user's active tab** — This is the most dangerous current behavior. The AI can navigate the user away from whatever they're doing.
2. **No tab ownership** — AI-created tabs are indistinguishable from user tabs.
3. **No cleanup** — AI tabs persist forever, cluttering the tab bar.
4. **No authorization** — Any tool can operate on any tab without checking who owns it.
