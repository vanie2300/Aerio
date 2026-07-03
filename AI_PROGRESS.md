# AI Integration Progress

## Phase 1 ✓ — Data Model + Service
- [x] Add 9 AI fields to `BrowserSettings` record
- [x] Create `AI/ChatMessage.cs`, `AI/AiService.cs`
- [x] Add `AiChatHistory` to `BrowserTab`
- [x] Build: 0 errors, 0 warnings

## Phase 2 ✓ — Settings Widgets
- [x] Add `TextRow`, `PasswordRow`, `SliderRow` helpers to `Controls/Widgets.cs`
- [x] Add AI settings section (toggle, provider, endpoint, model, API key, system prompt, max tokens, temperature)
- [x] Wire auto-save for all AI controls
- [x] Build: 0 errors, 0 warnings

## Phase 3 ✓ — Sidebar UI
- [x] Add AI sidebar column to XAML (340px, hidden by default, with header, message list, and input area)
- [x] Create `MainWindow.AI.cs` partial class with sidebar event handlers
- [x] Wire `AiService` for chat completions (per-tab history, "Thinking..." placeholder, error handling)
- [x] Add `Ctrl+Shift+A` shortcut for AI toggle
- [x] Wire `NotifySettingsChanged` into `ApplySettings`
- [x] Persist `AiSidebarOpen` state on show/hide
- [x] Bug fixes: Enter key check, dead code removal, redundancy cleanup
- [x] Build: 0 errors, 0 warnings

## Phase 4 ✓ — Shortcuts & Page Context
- [x] Add `AiPageContextToggle` (ToggleButton) in sidebar input area with "Include page" label
- [x] Add `PageContextScript` JS constant to extract page text (article > main > paragraphs) up to 8000 chars
- [x] Add `GetPageContentAsync()` that runs JS via `CoreWebView2.ExecuteScriptAsync` with error handling
- [x] Inject page context as system message in `AiSend_Click` when toggle is checked (prepended before conversation)
- [x] Build: 0 errors, 0 warnings
