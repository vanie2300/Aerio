# Plan: Local AI Integration in Tabloid Browser

## Overview

Add an AI chat/summary panel that runs against a **local LLM** (via [Ollama](https://ollama.com) / [llama.cpp](https://github.com/ggml-org/llama.cpp) server) or any **OpenAI-compatible API** (including [opencode](https://opencode.ai) or OpenAI / Claude / Groq).  

The feature lives as a **sidebar panel** that can:
- Chat with the current page's content as context
- Summarise articles, ask questions about the page, rewrite text
- Be invoked from a toolbar button or keyboard shortcut

---

## Target Architecture

```
┌─────────────────────────────────────────────────────┐
│  MainWindow (WPF)                                   │
│  ┌─────────────┬──────────────────────────────────┐ │
│  │  Tab Strip  │  WebView2 / Native Page          │ │
│  ├─────────────┤                                  │ │
│  │  AI Toggle  │                                  │ │
│  ├─────────────┴──────────────────────────────────┤ │
│  │  AI Sidebar (Collapsible)                      │ │
│  │  ┌──────────────────────────────────────────┐  │ │
│  │  │  Chat messages (ScrollViewer)            │  │ │
│  │  │  ┌────────────────────────────────────┐  │  │ │
│  │  │  │  User:   summarise this page       │  │  │ │
│  │  │  │  AI:     This page is about...     │  │  │ │
│  │  │  └────────────────────────────────────┘  │  │ │
│  │  │  [Type message...                ] [Send]│  │ │ │
│  │  │  [System prompt config            ] [⚙] │  │ │ │
│  │  └──────────────────────────────────────────┘  │ │
│  └─────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────┘

┌──────────────────────────────────────────┐
│  AiService (background)                  │
│  ├── CompletionClient (HTTP to Ollama    │
│  │   or OpenAI-compatible endpoint)      │
│  ├── ContextBuilder (page text,          │
│  │   user messages → prompt)             │
│  └── ChatHistory (in-memory per session) │
└──────────────────────────────────────────┘
```

---

## Settings Model

Add these fields to the existing `BrowserSettings` record (`Services/BrowserDataStore.cs:511`):

```csharp
bool AiEnabled,             // master toggle
bool AiSidebarOpen,         // persisted sidebar state
string AiProvider,          // "Ollama" | "OpenAI" | "Custom"
string AiEndpoint,          // http://localhost:11434/v1
string AiModel,             // "llama3.2", "qwen2.5", "gpt-4o-mini"
string AiApiKey,            // only needed for remote providers
string AiSystemPrompt,      // default system prompt
int AiMaxTokens,            // 2048
double AiTemperature        // 0.7
```

**Default values** (in `BrowserSettings.Default`):

```csharp
AiEnabled: false,
AiSidebarOpen: false,
AiProvider: "Ollama",
AiEndpoint: "http://localhost:11434/v1",
AiModel: "",
AiApiKey: "",
AiSystemPrompt: "You are a helpful assistant. Answer concisely.",
AiMaxTokens: 2048,
AiTemperature: 0.7
```

---

## Settings UI

Add a **"Local AI"** section to the settings page (`NativePages/PageBuilders.cs:138` `BuildSettingsPage`), reusing the existing widget helpers:

| Widget | Setting | Helper |
|--------|---------|--------|
| Toggle | Enable AI | `ToggleRow("Enable AI", "...", settings.AiEnabled)` |
| Text | API Endpoint | `TextRow("Endpoint", settings.AiEndpoint)` |
| Combo | Provider | `ChoiceGroup(["Ollama", "OpenAI", "Custom"], ...)` |
| Text | Model name | `TextRow("Model", settings.AiModel)` |
| Password | API Key | `PasswordRow("API Key", settings.AiApiKey)` |
| Text (multiline) | System prompt | `Multiline(settings.AiSystemPrompt)` |
| Numeric | Max tokens | `NumericStepper(settings.AiMaxTokens / 100, 1, 64)` → multiply by 100 |
| Slider | Temperature | (new helper: `SliderRow(0, 2, 0.1, settings.AiTemperature)`) |
| Button | Test connection | `MatteButton("Test", async (_, _) => { ... })` |

The `Save()` lambda already constructs a new `BrowserSettings(...)` record — just pass through the new fields.

---

## Service Layer

### NuGet packages (none required)

The OpenAI Chat Completions API is plain HTTP + JSON. The existing `System.Text.Json` and `HttpClient` in `Microsoft.Web.WebView2` (implicit) are sufficient. No extra packages needed.

### `Services/AiService.cs` — new file

```csharp
namespace TabloidBrowser;

public sealed class AiService : IDisposable
{
    private readonly HttpClient _http = new();

    public string Endpoint   { get; set; } = "http://localhost:11434/v1";
    public string Model      { get; set; } = "";
    public string ApiKey     { get; set; } = "";
    public string SystemPrompt { get; set; } = "";
    public int    MaxTokens  { get; set; } = 2048;
    public double Temperature { get; set; } = 0.7;

    // ── Chat Completion ──────────────────────────────────
    public async Task<string> CompleteAsync(
        List<ChatMessage> messages,
        CancellationToken ct = default)
    {
        var body = new
        {
            model = Model,
            messages = BuildMessages(messages),
            max_tokens = MaxTokens,
            temperature = Temperature,
            stream = false
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint.TrimEnd('/') + "/chat/completions")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json")
        };

        if (!string.IsNullOrWhiteSpace(ApiKey))
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", ApiKey);

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";
    }

    private List<object> BuildMessages(List<ChatMessage> messages)
    {
        var list = new List<object>(messages.Count + 1);
        if (!string.IsNullOrWhiteSpace(SystemPrompt))
            list.Add(new { role = "system", content = SystemPrompt });
        foreach (var m in messages)
            list.Add(new { role = m.Role, content = m.Content });
        return list;
    }

    // ── Streaming (future) ───────────────────────────────
    // SSE endpoint: Endpoint/chat/completions with stream: true
    // Parse "data: {...}" lines, yield content tokens via
    // IAsyncEnumerable<string> or event callback.

    public void Dispose() => _http.Dispose();
}

public record ChatMessage(string Role, string Content);
```

### Compatibility

| Server | Endpoint | Auth | Notes |
|--------|----------|------|-------|
| Ollama | `http://localhost:11434/v1` | none | Built-in OpenAI compat layer |
| llama.cpp server | `http://localhost:8080/v1` | none | `--jinja` for chat templates |
| opencode | `http://localhost:1738/v1` | none | OpenAI-compatible |
| OpenAI | `https://api.openai.com/v1` | Bearer key | Requires paid API key |
| Groq | `https://api.groq.com/openai/v1` | Bearer key | Free tier available |
| Anthropic | `https://api.anthropic.com/v1` | `x-api-key` | Different schema — needs adapter |

For Ollama/llama.cpp/opencode the same `/v1/chat/completions` schema works unchanged.

---

## UI — AI Sidebar

### Toggle Button

Add in `MainWindow.xaml` alongside the existing toolbar buttons (bookmark, history, etc.):

```xaml
<ToggleButton x:Name="AiToggle"
    ToolTip="AI Chat"
    Click="AiToggle_Click"
    Style="{StaticResource ToolbarButton}">
    <TextBlock Text="AI" FontSize="11" FontWeight="Bold"/>
</ToggleButton>
```

### Sidebar Panel

Add a `Grid` column that collapses/expands in `MainWindow.xaml`:

```xaml
<ColumnDefinition Width="Auto" x:Name="AiSidebarColumn"/>
```

The sidebar is a programmatically-built panel (like native pages), populated via:

```csharp
private UIElement BuildAiSidebar()
{
    var panel = new Grid { Width = 320, Background = GetBrush("AppBg") };
    // divider, ScrollViewer for messages, input TextBox + Send button,
    // system prompt editor popup, context checkbox
    return panel;
}
```

### Keyboard shortcut: `Ctrl+Shift+A`

Add to the shortcut dictionary in `BrowserSettings.DefaultShortcuts`:

```csharp
["ai_toggle"] = "Ctrl+Shift+A",
```

Handle in `MainWindow_PreviewKeyDown`:

```csharp
private async void MainWindow_PreviewKeyDown(object? sender, KeyEventArgs e)
{
    // ... existing shortcut handling ...
    if (MatchShortcut("ai_toggle", e))
    {
        AiToggle.IsChecked = !AiToggle.IsChecked;
        AiToggle_Click(null, null);
        e.Handled = true;
    }
}
```

---

## Implementation Steps

### Phase 1 — Foundation (est. 1 session)

1. Add AI fields to `BrowserSettings` record
2. Add defaults in `BrowserSettings.Default`
3. Handle migration of old settings (missing fields → use defaults — already handled by `??` in `LoadSettings`)

### Phase 2 — Service (est. 1 session)

4. Create `Services/AiService.cs` with `CompleteAsync`
5. Wire `AiService` lifecycle in `MainWindow` constructor / `MainWindow_Closing` dispose
6. Test with Ollama `/v1/chat/completions` endpoint

### Phase 3 — Settings UI (est. 1 session)

7. Add AI section to `BuildSettingsPage` in `PageBuilders.cs`
8. Wire settings to `AiService` properties via `Save()` / `ApplySettings()`
9. Add "Test connection" button

### Phase 4 — Sidebar (est. 2 sessions)

10. Add `AiToggle` button to toolbar in `MainWindow.xaml`
11. Add sidebar column / grid overlay in `MainWindow.xaml`
12. Build `BuildAiSidebar()` with message list, input, send
13. Implement send → `AiService.CompleteAsync` → display response
14. Disable send during active request, handle cancellation

### Phase 5 — Page context (est. 1 session)

15. "Send page content as context" checkbox in sidebar
16. Extract visible text from active `WebView2` via `ExecuteScriptAsync("document.body.innerText")`
17. Prepend to messages as `{ role: "user", content: "Page content:\n..." }`

### Phase 6 — Polish (est. 1 session)

18. Streaming support via SSE (`IAsyncEnumerable<string>`)
19. Chat history export / clear button
20. Token counting and context window management
21. Keyboard shortcut handler

---

## File Change Summary

| File | Change |
|------|--------|
| `Services/BrowserDataStore.cs` | Add AI fields to `BrowserSettings` record + defaults |
| `Services/AiService.cs` | **New** — HTTP client for chat completions |
| `MainWindow.xaml` | Add AI toggle button + sidebar column |
| `MainWindow.xaml.cs` | Wire `AiService` lifecycle, sidebar build, send handler, keyboard shortcut |
| `NativePages/PageBuilders.cs` | Add AI section in settings page |
| `Models/BrowserTabKind.cs` | No change needed (sidebar is not a native page tab) |

---

## Open Questions

- **Streaming**: First iteration can be request/response. Add SSE streaming later for better UX.
- **Context extraction**: Full page `innerText` can be huge. Consider truncating to `AiMaxTokens` or using `Readability.js` (already bundled) to extract article content before sending.
- **Model listing**: Could call `GET /v1/models` at the endpoint to populate a dropdown. Nice-to-have.
- **Per-tab state**: Should chat history be per-tab or global? Per-tab is more useful (each tab has its own conversation about its page).
