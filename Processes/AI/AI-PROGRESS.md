# AI Browser Searching Agent — Progress & Plan

## Overview

Transform the existing AI chat sidebar into a full browser searching agent with tool calling, web search, and comprehensive management UI.

---

## Phase 1 — Tool Calling Infrastructure

### 1.1 Extend `AiService` for tool/function calling

**File:** `AI/AiService.cs`

- Add `List<AiTool>? Tools` parameter to `CompleteAsync`
- Add `string? ToolChoice` parameter (`"auto"` | `"none"` | `"required"`)
- Modify request body to include `tools` array when tools are provided
- Parse `tool_calls` from response:

```csharp
public sealed record AiTool(
    string Name,
    string Description,
    object Parameters  // JSON schema object
);

public sealed record AiToolCall(
    string Id,
    string Name,
    string Arguments  // JSON string
);

public sealed record AiResponse(
    string? Content,
    List<AiToolCall>? ToolCalls
);
```

Return type changes from `Task<string>` to `Task<AiResponse>`.

### 1.2 New models

**File:** `AI/AiTools.cs`

```csharp
public sealed record AiTool(string Name, string Description, object Parameters);
public sealed record AiToolCall(string Id, string Name, string Arguments);
public sealed record AiToolResult(string ToolCallId, string Content);
public sealed record AiResponse(string? Content, List<AiToolCall>? ToolCalls);

public sealed record SearchResult(string Title, string Url, string Snippet);
```

### 1.3 Build request body

Request shape when tools are present:

```json
{
  "model": "llama3.2",
  "messages": [...],
  "tools": [
    {
      "type": "function",
      "function": {
        "name": "web_search",
        "description": "...",
        "parameters": {
          "type": "object",
          "properties": {
            "query": { "type": "string", "description": "..." }
          },
          "required": ["query"]
        }
      }
    }
  ],
  "tool_choice": "auto"
}
```

### 1.4 Parse response

Parse `choices[0].message.tool_calls[]`:

```json
{
  "choices": [{
    "message": {
      "role": "assistant",
      "content": null,
      "tool_calls": [{
        "id": "call_xxx",
        "type": "function",
        "function": {
          "name": "web_search",
          "arguments": "{\"query\": \"latest AI news\"}"
        }
      }]
    }
  }]
}
```

---

## Phase 2 — Web Search Service

### 2.1 New file: `AI/WebSearchService.cs`

```csharp
public sealed record SearchResult(string Title, string Url, string Snippet);

public sealed class WebSearchService
{
    public string Backend { get; set; } = "duckduckgo";   // duckduckgo | searxng | custom
    public string? Endpoint { get; set; }                  // for searxng / custom

    public async Task<List<SearchResult>> SearchAsync(string query, CancellationToken ct);
}
```

#### DuckDuckGo Lite (default, no API key)

```
GET https://lite.duckduckgo.com/lite/?q={query}
```

Response is an HTML table. Parsing:
- `class="result-link"` → URL + title from `<a>` tags
- `class="result-snippet"` → snippet text

#### SearXNG (optional, configurable)

```
GET {endpoint}/search?q={query}&format=json
```

Response is JSON. Parsing:
- `results[].title` → title
- `results[].url` → URL
- `results[].content` → snippet

#### Custom (generic)

```
GET {endpoint}?q={query}
```

Try to parse as JSON (same as SearXNG format), fall back to HTML parsing.

### 2.2 Rate limiting

- Max 1 request per second
- Queue/batch if multiple search calls come in the same agent iteration

---

## Phase 3 — Tool Definitions

### 3.1 Tool 1: `web_search(query)`

```json
{
  "name": "web_search",
  "description": "Search the web for current information. Returns a list of results with title, URL, and snippet.",
  "parameters": {
    "type": "object",
    "properties": {
      "query": { "type": "string", "description": "The search query" }
    },
    "required": ["query"]
  }
}
```

Execution: `WebSearchService.SearchAsync(query, ct)`
Returns: Formatted list — `1. Title\n   URL\n   Snippet\n`

### 3.2 Tool 2: `search_and_read(query, result_index?)`

```json
{
  "name": "search_and_read",
  "description": "Search the web, navigate to the Nth result, and read the page content. Combines search + navigate + read in one call.",
  "parameters": {
    "type": "object",
    "properties": {
      "query": { "type": "string", "description": "The search query" },
      "result_index": { "type": "integer", "description": "Which result to open (0-based). Default: 0" }
    },
    "required": ["query"]
  }
}
```

Execution:
1. Call `WebSearchService.SearchAsync(query)`
2. Pick result at `result_index` (default 0)
3. Navigate current tab to result's URL via `NavigateToUrlAsync`
4. Wait for page load
5. Call `GetPageContentAsync()` (existing JS extraction)
6. Return search results summary + page content

### 3.3 Tool 3: `search_followup(query)`

```json
{
  "name": "search_followup",
  "description": "Get related search queries and suggestions. Use when the initial search didn't find good results.",
  "parameters": {
    "type": "object",
    "properties": {
      "query": { "type": "string", "description": "The search query to get follow-ups for" }
    },
    "required": ["query"]
  }
}
```

Execution: Fetch DuckDuckGo suggestions/related queries.
Returns: List of alternative query phrasings.

### 3.4 Tool 4: `get_page_content()`

```json
{
  "name": "get_page_content",
  "description": "Get the text content of the currently open page. Returns up to ~8000 characters.",
  "parameters": { "type": "object", "properties": {} }
}
```

Execution: Call existing `GetPageContentAsync()` (JS `document.body.innerText` extraction).
Returns: Page text (truncated to ~8000 chars).

### 3.5 Tool 5: `get_page_metadata(url?)`

```json
{
  "name": "get_page_metadata",
  "description": "Get lightweight metadata from a page — title, meta description, OG tags, headings. Faster than reading full content.",
  "parameters": {
    "type": "object",
    "properties": {
      "url": { "type": "string", "description": "URL to check. If empty, uses current page." }
    }
  }
}
```

If URL provided, create a hidden WebView2, navigate, extract metadata, then dispose.
If no URL, extract from current page via JS.

Returns: `Title, Description, Headings: [H1, H2...]`

### 3.6 Tool 6: `batch_read(urls[])`

```json
{
  "name": "batch_read",
  "description": "Read multiple pages in parallel. Use when you need content from several search results at once.",
  "parameters": {
    "type": "object",
    "properties": {
      "urls": {
        "type": "array",
        "items": { "type": "string", "description": "URL to read" }
      }
    },
    "required": ["urls"]
  }
}
```

Execution:
1. For each URL, create a temporary WebView2, navigate, wait for load, extract text
2. Run up to 3 in parallel (concurrency limit)
3. Dispose temporary WebViews after extraction

Returns: Content for each URL, labeled by URL.

**Note:** This is expensive (creates/destroys WebView2 instances). For performance, we may limit to 3 URLs per call.

### 3.7 Tool 7: `extract_page_links(source)`

```json
{
  "name": "extract_page_links",
  "description": "Extract all hyperlinks with their visible text from a page or from the last search results.",
  "parameters": {
    "type": "object",
    "properties": {
      "source": { "type": "string", "description": "Optional URL. If empty, extracts from current page or last search." }
    }
  }
}
```

If called after a search with no URL, returns links from the cached search results.
If called with URL or from current page, extracts `<a>` tags via JS.

Returns: Structured list — `[text](url)` per link.

### 3.8 Tool 8: `navigate_to(url)`

```json
{
  "name": "navigate_to",
  "description": "Navigate the current tab to a URL and read its content.",
  "parameters": {
    "type": "object",
    "properties": {
      "url": { "type": "string", "description": "Full URL to navigate to" }
    },
    "required": ["url"]
  }
}
```

Execution:
1. Call `NavigateToUrlAsync(url)` in the active tab (in the source MainWindow)
2. Wait for navigation to complete
3. Call `GetPageContentAsync()` to extract text

Returns: Page text content.

### 3.9 Tool 9: `open_tab(url, title)`

```json
{
  "name": "open_tab",
  "description": "Open a URL in a new permanent background tab. The tab stays until manually closed.",
  "parameters": {
    "type": "object",
    "properties": {
      "url": { "type": "string", "description": "URL to open" },
      "title": { "type": "string", "description": "Display title for the tab" }
    },
    "required": ["url", "title"]
  }
}
```

Execution:
1. Call `AddWebTabAsync(url, activate: false)` — creates a tab without switching to it
2. Wait for initial navigation

Returns: Confirmation with tab index.

### 3.10 Tool 10: `show_preview_tabs(results[])`

```json
{
  "name": "show_preview_tabs",
  "description": "Open multiple tabs as previews. Click one — the others auto-close. The selected tab becomes permanent.",
  "parameters": {
    "type": "object",
    "properties": {
      "results": {
        "type": "array",
        "items": {
          "type": "object",
          "properties": {
            "url": { "type": "string", "description": "Full URL" },
            "title": { "type": "string", "description": "Page title" }
          },
          "required": ["url", "title"]
        }
      }
    },
    "required": ["results"]
  }
}
```

Execution:
1. For each result, call `AddWebTabAsync(url, activate: false)` to create background tabs
2. Mark each new tab with `IsTempPreview = true` (new flag on `BrowserTab`)
3. When the user activates one of these tabs (clicks it), iterate `_tabs` and close all other `IsTempPreview` tabs
4. The activated preview tab gets `IsTempPreview = false` (becomes permanent)

---

## Phase 4 — Agent Loop

### 4.1 New method: `AgentCompleteAsync`

In `MainWindow.AI.cs`, modify `AiSend_Click` to call an agent loop instead of single `CompleteAsync`:

```
AgentLoop(messages, tools, maxIterations=10):

1. Call AiService.CompleteAsync(messages, tools, toolChoice="auto")
2. If response contains tool_calls:
    a. For each tool_call (in parallel where possible):
        - Add "🔍 ToolName(args)" inline bubble to chat
        - Execute the tool
        - Add tool result as ChatMessage("tool", result)
    b. Increment iteration counter
    c. If iteration >= maxIterations:
        - Return "I couldn't complete this task in the allowed steps."
    d. Go to step 1
3. If response contains content:
    - Add assistant bubble with content
    - Return
```

### 4.2 Tool execution dispatch

```csharp
private async Task<string> ExecuteToolAsync(AiToolCall call, CancellationToken ct)
{
    return call.Name switch
    {
        "web_search" => await ExecuteWebSearchAsync(call.Arguments, ct),
        "search_and_read" => await ExecuteSearchAndReadAsync(call.Arguments, ct),
        "search_followup" => await ExecuteSearchFollowupAsync(call.Arguments, ct),
        "get_page_content" => await GetPageContentAsync() ?? "",
        "get_page_metadata" => await ExecuteGetPageMetadataAsync(call.Arguments, ct),
        "batch_read" => await ExecuteBatchReadAsync(call.Arguments, ct),
        "extract_page_links" => await ExecuteExtractLinksAsync(call.Arguments, ct),
        "navigate_to" => await ExecuteNavigateToAsync(call.Arguments, ct),
        "open_tab" => await ExecuteOpenTabAsync(call.Arguments, ct),
        "show_preview_tabs" => await ExecuteShowPreviewTabsAsync(call.Arguments, ct),
        _ => $"Error: Unknown tool '{call.Name}'"
    };
}
```

### 4.3 Parallel execution

Tools that don't depend on each other can run in parallel within the same iteration:
- `web_search` + `get_page_content` → parallel (independent)
- Multiple `batch_read` URLs → parallel (concurrency limit 3)
- `navigate_to` must be sequential (it changes the current tab)

Simple rule: batch all tool calls in one iteration, run them in parallel, collect results, send back in one go.

### 4.4 Guardrails

- Max 10 iterations per user message
- Cancel via existing `_aiCts.Cancel()`
- Tool errors return error message to AI so it can retry/adapt
- Empty search results return "No results found"
- Page content truncated to ~8000 chars per page

---

## Phase 5 — AI Management UI

### 5.1 New fields in `BrowserSettings`

```csharp
// Add to BrowserSettings record:
Dictionary<string, bool>? AiToolsEnabled,   // null = all enabled
string? AiSearchBackend,                    // "duckduckgo" | "searxng" | "custom"
string? AiSearchEndpoint,                   // custom search URL
int AiMaxIterations,                        // default 10
bool AiAutoSearch,                          // default true
```

Defaults:
```csharp
AiToolsEnabled: null,
AiSearchBackend: "duckduckgo",
AiSearchEndpoint: "",
AiMaxIterations: 10,
AiAutoSearch: true,
```

### 5.2 New flag on `BrowserTab`

```csharp
// Add to BrowserTab class:
public bool IsTempPreview { get; set; }  // for show_preview_tabs
```

### 5.3 UI: AI Management native page (`ai://manage`)

**Layout:** Three tabbed sections within a native page:

#### 🔌 Connection

| Control | Field | Default |
|---------|-------|---------|
| Toggle | Enable AI assistant | off |
| Dropdown | Provider | Ollama |
| Text | Endpoint URL | `http://localhost:11434/v1` |
| Password | API key | (empty) |
| Dropdown (editable) | Model | (empty, auto-discovered) |
| Button | Test Connection | — |
| Status label | Connection status | — |

Provider selection updates endpoint URL from presets (Ollama → `http://localhost:11434/v1`, OpenAI → `https://api.openai.com/v1`, Groq → `https://api.groq.com/openai/v1`, OpenRouter → `https://openrouter.ai/api/v1`).

Endpoint or API key changes trigger debounced model discovery (800ms delay).

#### 🧰 Tools

Checkbox list with description per tool:

```
☑ web_search(query)              — Search the web
☑ search_and_read(query, idx)    — Search + read one result
☑ search_followup(query)         — Get related search suggestions
☑ get_page_content()             — Read current page
☑ get_page_metadata(url)         — Lightweight page summary
☑ batch_read(urls[])             — Read multiple pages
☑ extract_page_links(source)     — Extract links from page
☑ navigate_to(url)               — Navigate + read
☑ open_tab(url, title)           — Open background tab
☑ show_preview_tabs(results[])   — Preview tabs (click to keep)
```

Tool toggles stored in `AiToolsEnabled` dictionary (tool name → bool).

#### ⚙ Behavior

| Control | Field | Range / Values | Default |
|---------|-------|----------------|---------|
| Textarea | System prompt | multiline | "You are a helpful assistant. Answer concisely." |
| Slider | Max tokens | 256–16384 | 2048 |
| Slider | Temperature | 0.0–2.0 | 0.7 |
| Slider | Max iterations | 1–20 | 10 |
| Toggle | Auto-search | — | true |
| Dropdown | Search backend | DuckDuckGo Lite / SearXNG / Custom | DuckDuckGo Lite |
| Text | Search endpoint | (shown for SearXNG/Custom) | (empty) |

#### 📊 Data

| Control | Description |
|---------|-------------|
| Label | Token count (estimated) for current tab |
| Label | Message count for current tab |
| Button | Clear chat history (current tab) |
| Button | Clear all history (all tabs) |
| Button | Export chat as JSON |

### 5.4 Access points

- **AI sidebar header**: Gear icon ⚙ button → opens `ai://manage`
- **Settings page**: Existing AI section links to `ai://manage`
- **Menu**: Browser menu > AI Management

### 5.5 Preview tab behavior (`show_preview_tabs`)

When `ActivateTabAsync` runs (user clicks a tab):
```csharp
// After activating the tab, if it's a temp preview, clean up other previews
var tempPreviews = _tabs.Where(t => t.IsTempPreview && t != tab).ToList();
foreach (var t in tempPreviews)
    await CloseTabAsync(t);
tab.IsTempPreview = false; // becomes permanent
```

Tab strip visual hint: preview tabs could have slightly lower opacity or a badge to distinguish them. (Phase 2 polish.)

---

## Phase 6 — Implementation Order

### Sprint 1: Foundation
1. Extend `AiService.CompleteAsync` to support `tools` parameter and return `AiResponse` with `ToolCalls`
2. Add `AiTool`, `AiToolCall`, `AiResponse` records to `AI/AiTools.cs`
3. Add `IsTempPreview` to `BrowserTab`
4. Add new `BrowserSettings` fields

### Sprint 2: Core Tools
5. Implement `WebSearchService` (DuckDuckGo Lite backend)
6. Implement tools: `web_search`, `get_page_content`, `navigate_to`, `search_and_read`
7. Build agent loop in `MainWindow.AI.cs`

### Sprint 3: Advanced Tools
8. Implement: `batch_read`, `extract_page_links`, `get_page_metadata`, `search_followup`
9. Implement: `open_tab`, `show_preview_tabs` + auto-close logic
10. Preview tab visual hints

### Sprint 4: Management UI
11. Build `ai://manage` native page (Connection tab)
12. Add Tools tab with toggles
13. Add Behavior tab (system prompt, sliders, search backend)
14. Add Data tab (history management)
15. Connect all settings to `BrowserDataStore`

### Sprint 5: Polish
16. Parallel tool execution within one iteration
17. Token counting / context window estimation
18. Chat export/import
19. Error recovery in agent loop
20. Keyboard shortcuts for management page

---

## Files to create

| File | Contents |
|------|----------|
| `AI/AiTools.cs` | `AiTool`, `AiToolCall`, `AiToolResult`, `AiResponse` records. Tool schema builder. Tool dispatcher. |
| `AI/WebSearchService.cs` | `SearchResult` record. `WebSearchService` class with DuckDuckGo Lite + SearXNG + custom backends. |

## Files to modify

| File | Changes |
|------|---------|
| `AI/AiService.cs` | Add tools to request, parse tool_calls from response, return `AiResponse` |
| `AI/ChatMessage.cs` | Add `"tool"` role support (record already has Role + Content, just need to use it) |
| `MainWindow/MainWindow.AI.cs` | Agent loop, tool execution dispatch, UI for tool call display |
| `MainWindow.xaml` | AI sidebar header gear icon, preview tab styling |
| `MainWindow/MainWindow.Tabs.cs` | `ActivateTabAsync` cleanup of temp preview tabs |
| `Models/BrowserTab.cs` | Add `IsTempPreview` flag |
| `Services/BrowserDataStore.cs` | Add AI tool/search settings to `BrowserSettings` record |
| `NativePages/PageBuilders.cs` | Add `ai://manage` page routing, tool toggle UI |

## Files unchanged

| File | Reason |
|------|--------|
| `MainWindow/MainWindow.WebView2.cs` | No changes needed |
| `MainWindow/MainWindow.UI.cs` | No changes needed |
| `MainWindow/MainWindow.Navigation.cs` | No changes needed |
| `MainWindow/MainWindow.Permissions.cs` | No changes needed |
| `MainWindow/MainWindow.Particles.cs` | No changes needed |
| `MainWindow.xaml.cs` | No changes needed (already routes native pages) |
| `Controls/AnimatedTabPanel.cs` | No changes needed |
| `Controls/Widgets.cs` | No changes needed |
