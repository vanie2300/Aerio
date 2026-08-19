Yes. I’d make each of the 40 tasks into **small, independently completable sub-phases**. That way you can implement Tabloid incrementally, commit after each meaningful step, and avoid turning `MainWindow.AI.cs` into a giant refactor.

Below is the detailed implementation plan.

# Tabloid AI Browser — Full Sub-Phase Roadmap

```text
PHASE 1 — FOUNDATION
    1. Verify existing tab lifecycle
    2. Add TabOwner
    3. Add AiTaskId
    4. Add AiBrowserSettings
    5. Add AiTask
    6. Add AiWorkspace

PHASE 2 — AI TAB MANAGEMENT
    7. Create AiTabManager
    8. Connect open_tab
    9. Add AI tab limit
   10. Make navigate_to task-aware
   11. Protect user active tab
   12. Add AI tab cleanup
   13. Add keep/ownership transfer

PHASE 3 — USER EXPERIENCE
   14. AI tab indicators
   15. AI workspace status
   16. Keep/Close controls
   17. Tab-limit UI
   18. Settings UI

PHASE 4 — WEBSITE INTERACTION
   19. PageObserver
   20. get_page_elements
   21. click_element
   22. type_into_element
   23. scroll_page
   24. press_key

PHASE 5 — AGENT INTELLIGENCE
   25. Observe → Decide → Act
   26. Verify action results
   27. Handle stale elements
   28. Handle navigation changes
   29. Tab reuse

PHASE 6 — SECURITY
   30. Action policy
   31. Confirmation system
   32. Prompt injection protection
   33. AI-owned tab access validation

PHASE 7 — TESTING
   34. Ownership tests
   35. Limit tests
   36. Cleanup tests
   37. Keep-tab tests
   38. Navigation isolation tests
   39. Security tests
   40. WebView2 integration tests
```

---

# PHASE 1 — FOUNDATION

---

# 1. Verify Existing Tab Lifecycle

### Objective

Understand and lock down how Tabloid currently creates, tracks, activates, and destroys WebView2 tabs.

### 1.1 Locate tab state

Find where you currently store:

```text
Tabs
CurrentTab
ActiveTab
WebView2 instances
Tab headers
```

Likely areas:

```text
MainWindow.Tabs.cs
MainWindow.WebView2.cs
MainWindow.Navigation.cs
```

Document the current flow.

### 1.2 Trace tab creation

Follow:

```text
New Tab
 ↓
Create WebView2
 ↓
Initialize WebView2
 ↓
Add to tab collection
 ↓
Add UI
 ↓
Activate
```

### 1.3 Trace tab closing

Document:

```text
Close
 ↓
Dispose WebView2
 ↓
Remove tab
 ↓
Select replacement
```

### 1.4 Trace active-tab changes

Determine exactly where:

```text
ActiveTabId
```

or equivalent state changes.

### 1.5 Test manually

Verify:

* New tab
* Multiple tabs
* Switching
* Closing active tab
* Closing inactive tab
* Reordering
* Navigation

### 1.6 Commit

```text
baseline: document existing tab lifecycle
```

### Completion condition

You know exactly which methods must be called to:

```text
create tab
activate tab
navigate tab
close tab
```

---

# 2. Add `TabOwner`

### Objective

Give every tab an owner.

### 2.1 Create enum

```csharp
public enum TabOwner
{
    User,
    AI
}
```

### 2.2 Add property

Add to your tab state:

```csharp
public TabOwner Owner { get; set; } = TabOwner.User;
```

### 2.3 Update tab creation

Normal browser tab:

```text
Owner = User
```

### 2.4 Don't change AI yet

Existing AI tabs should still behave normally.

### 2.5 Test

Create:

```text
User tab
```

Confirm:

```text
Owner == User
```

### 2.6 Commit

```text
ai: introduce browser tab ownership
```

---

# 3. Add `AiTaskId`

### Objective

Identify which AI task owns an AI tab.

### 3.1 Add property

```csharp
public string? AiTaskId { get; set; }
```

### 3.2 Define invariant

Normal user tab:

```text
Owner = User
AiTaskId = null
```

AI tab:

```text
Owner = AI
AiTaskId != null
```

### 3.3 Add helper

```csharp
bool IsAiOwned()
```

### 3.4 Audit code

Search for all tab creation points.

Make sure ordinary tabs don't accidentally receive a task ID.

### 3.5 Test

Verify the ownership invariant.

### 3.6 Commit

```text
ai: associate browser tabs with agent tasks
```

---

# 4. Add `AiBrowserSettings`

### Objective

Make AI browser behavior user-configurable.

### 4.1 Create model

```csharp
public class AiBrowserSettings
{
    public int MaxAiTabs { get; set; } = 5;

    public bool ProtectActiveUserTab { get; set; } = true;

    public bool CloseAiTabsAfterTask { get; set; } = true;

    public bool AskBeforeExceedingTabLimit { get; set; } = true;
}
```

### 4.2 Add to existing settings

Integrate with `BrowserDataStore`.

Don't create a second persistence system.

### 4.3 Handle old settings files

If users already have settings JSON, missing fields should get defaults.

### 4.4 Validate settings

Prevent:

```text
MaxAiTabs = -10
```

I'd allow something like:

```text
1–20
```

initially.

### 4.5 Save settings

Change a setting.

Restart Tabloid.

Verify it persists.

### 4.6 Commit

```text
ai: add browser agent settings
```

---

# 5. Add `AiTask`

### Objective

Represent one AI operation.

### 5.1 Create model

```csharp
public sealed class AiTask
{
    public string Id { get; init; }

    public string Goal { get; init; }

    public AiTaskStatus Status { get; set; }

    public List<string> TabIds { get; }

    public DateTime StartedAt { get; init; }

    public DateTime? CompletedAt { get; set; }
}
```

### 5.2 Add status enum

```text
Running
Completed
Failed
Cancelled
```

### 5.3 Create task

When an agent request begins:

```text
User message
 ↓
Create AiTask
 ↓
Run agent
```

### 5.4 End task

When the loop finishes:

```text
Completed
```

or:

```text
Failed
Cancelled
```

### 5.5 Don't persist yet

Keep it runtime-only.

### 5.6 Commit

```text
ai: introduce agent task lifecycle
```

---

# 6. Add `AiWorkspace`

### Objective

Give each AI task its own browser context.

### 6.1 Create class

```csharp
public sealed class AiWorkspace
{
    public string TaskId { get; }

    public string? ActiveTabId { get; set; }

    public List<string> TabIds { get; }
}
```

### 6.2 Create workspace with task

```text
AiTask
 ↓
AiWorkspace
```

### 6.3 Track active AI tab

The workspace has:

```text
ActiveTabId
```

separate from the browser's user-selected tab.

### 6.4 Add tab registration

When an AI tab is created:

```text
Workspace.TabIds.Add(tabId)
```

### 6.5 Commit

```text
ai: add task browser workspace
```

---

# PHASE 2 — AI TAB MANAGEMENT

---

# 7. Create `AiTabManager`

### Objective

Centralize AI tab operations.

### 7.1 Create class

```text
Processes/AI/Browser/AiTabManager.cs
```

### 7.2 Implement

```text
GetAiTabs()
GetAiTabCount()
GetTab()
CanCreateAiTab()
CreateAiTab()
CloseAiTab()
CloseTaskTabs()
KeepAiTab()
```

### 7.3 Connect to existing tab system

Do not duplicate WebView2 lifecycle.

Use your existing tab creation/closing methods.

### 7.4 Define ownership rules

Only this manager should change:

```text
Owner
AiTaskId
```

where possible.

### 7.5 Add logging

Log:

```text
AI tab created
AI tab closed
AI tab ownership transferred
```

### 7.6 Commit

```text
ai: introduce AI tab manager
```

---

# 8. Connect `open_tab`

### Objective

Make your existing `open_tab` AI-aware.

### 8.1 Identify current executor path

```text
AiToolExecutor
 ↓
open_tab
```

### 8.2 Detect active AI task

The executor must know:

```text
Current AiTask
```

### 8.3 Route through `AiTabManager`

```text
open_tab
 ↓
AiTabManager.CreateAiTab()
```

### 8.4 Associate task

```text
Owner = AI
AiTaskId = currentTask.Id
```

### 8.5 Add to workspace

```text
workspace.TabIds
```

### 8.6 Navigate

Use existing navigation infrastructure.

### 8.7 Return structured result

Include:

```text
tab_id
url
title
```

### 8.8 Test

Open a user tab first.

Ask AI to open another URL.

Verify both remain.

### 8.9 Commit

```text
ai: make open_tab task-aware
```

---

# 9. Add AI Tab Limit

### Objective

Enforce `MaxAiTabs`.

### 9.1 Count AI tabs

```text
Owner == AI
```

### 9.2 Check before creation

Never check after creating.

### 9.3 Return structured error

```json
{
  "success": false,
  "error_code": "AI_TAB_LIMIT_REACHED"
}
```

### 9.4 Make agent recover gracefully

The agent receives the error as tool output.

It should not crash.

### 9.5 Add logging

```text
AI_TAB_LIMIT_REACHED
```

### 9.6 Test boundaries

Test:

```text
0 / 5
4 / 5
5 / 5
```

### 9.7 Commit

```text
ai: enforce configurable tab limit
```

---

# 10. Make `navigate_to` Task-Aware

### Objective

Stop AI navigation from accidentally navigating the user's tab.

### 10.1 Identify current navigation implementation

Find where `navigate_to` eventually calls WebView2 navigation.

### 10.2 Introduce AI target resolution

```text
AI task
 ↓
workspace.ActiveTabId
 ↓
target tab
```

### 10.3 If no AI tab exists

Create one.

### 10.4 Set AI active tab

```text
workspace.ActiveTabId = newTabId
```

### 10.5 Navigate target

Only navigate that WebView2.

### 10.6 Return tab ID

The AI should know which tab it affected.

### 10.7 Test

User:

```text
Tab 1 → GitHub
```

AI:

```text
navigate_to(Google)
```

Expected:

```text
Tab 1 → GitHub
Tab 2 → Google
```

### 10.8 Commit

```text
ai: isolate agent navigation from user tabs
```

---

# 11. Protect User Active Tab

### Objective

Create a hard safety boundary.

### 11.1 Identify user active tab

Use existing tab state.

### 11.2 Create navigation policy

```text
AI target = User tab
        ↓
BLOCK
```

unless explicitly authorized.

### 11.3 Protect other user tabs too

Don't only protect the active tab.

AI should not modify **any** user-owned tab by default.

### 11.4 Add override mechanism

Later, explicit user instructions can allow it.

Example:

> "Use my current tab to search."

### 11.5 Log blocked action

```text
AI_ACTION_BLOCKED
reason=USER_TAB_PROTECTED
```

### 11.6 Test

Attempt AI navigation against user tab.

Expected:

```text
Blocked
```

### 11.7 Commit

```text
security: protect user-owned browser tabs
```

---

# 12. Add AI Tab Cleanup

### Objective

Automatically clean temporary AI tabs.

### 12.1 Trigger on task completion

```text
Task.Completed
 ↓
Cleanup
```

### 12.2 Check setting

If:

```text
CloseAiTabsAfterTask = false
```

do nothing.

### 12.3 Filter by task

Only:

```text
Owner == AI
AiTaskId == task.Id
```

### 12.4 Close through existing tab lifecycle

Don't directly destroy WebView2 objects.

### 12.5 Handle active AI tab

If the AI tab is currently selected in the UI, choose a sensible user tab afterward.

### 12.6 Test

Task creates 3 tabs.

Task finishes.

Expected:

```text
AI tabs → closed
User tabs → untouched
```

### 12.7 Commit

```text
ai: clean up temporary task tabs
```

---

# 13. Add Keep / Ownership Transfer

### Objective

Allow users to preserve useful AI research.

### 13.1 Implement transfer

```text
AI → User
```

### 13.2 Clear task ID

```text
AiTaskId = null
```

### 13.3 Don't recreate tab

Keep:

```text
WebView2
URL
history
scroll position
page state
```

### 13.4 Remove from workspace

```text
workspace.TabIds.Remove(tabId)
```

### 13.5 Prevent cleanup

Cleanup should now ignore it.

### 13.6 Test

```text
AI creates tab
 ↓
User keeps it
 ↓
Task ends
```

Expected:

```text
Tab remains
Owner = User
```

### 13.7 Commit

```text
ai: allow users to keep agent tabs
```

---

# PHASE 3 — USER EXPERIENCE

# 14. AI Tab Indicators

### Sub-phases

1. Decide visual indicator.
2. Add AI icon/badge.
3. Show only for AI-owned tabs.
4. Remove indicator after ownership transfer.
5. Test dark/light themes.
6. Test tab overflow.
7. Test hover preview.
8. Commit.

```text
🤖 Research
```

should become:

```text
Research
```

after Keep.

---

# 15. AI Workspace Status

Add sidebar information:

```text
AI Task

Research WebView2

Tabs
🤖 Microsoft Docs
🤖 GitHub
🤖 Tutorial

3 / 5 AI tabs
```

### Sub-phases

1. Create workspace view.
2. Bind active task.
3. Show goal.
4. Show tab count.
5. Show AI tabs.
6. Allow selecting an AI tab.
7. Show task status.
8. Commit.

---

# 16. Keep / Close Controls

### Sub-phases

1. Add `Keep` button.
2. Connect to ownership transfer.
3. Add `Close` button.
4. Add `Keep All`.
5. Add `Close All`.
6. Handle already-closed tabs.
7. Handle task completion race conditions.
8. Commit.

---

# 17. Tab Limit UI

### Sub-phases

1. Add setting.
2. Add numeric input.
3. Validate range.
4. Show current usage.
5. Show warning at limit.
6. Handle limit reached.
7. Test persistence.
8. Commit.

Example:

```text
AI Browser Tabs

Maximum:
[ 5 ]

Currently using:
3 / 5
```

---

# 18. Settings UI

### Sub-phases

Create an AI Browser section:

```text
AI Browser
──────────────

Maximum AI tabs
[ 5 ]

☑ Protect my tabs

☑ Close AI tabs after task

☑ Ask before exceeding limit
```

Then:

1. Add UI.
2. Load settings.
3. Bind controls.
4. Save changes.
5. Validate.
6. Test restart.
7. Test defaults.
8. Commit.

---

# PHASE 4 — WEBSITE INTERACTION

# 19. Create `PageObserver`

### Objective

Turn WebView2 pages into structured information.

### Sub-phases

1. Identify DOM extraction strategy.
2. Inject JavaScript.
3. Extract visible text.
4. Extract links.
5. Extract interactive elements.
6. Generate element IDs.
7. Store element map per tab.
8. Add timeout.
9. Handle page navigation.
10. Add tests.
11. Commit.

---

# 20. `get_page_elements`

### Sub-phases

1. Define tool schema.
2. Call PageObserver.
3. Return buttons.
4. Return links.
5. Return textboxes.
6. Return selects.
7. Return checkboxes.
8. Return visibility.
9. Return disabled state.
10. Sanitize text.
11. Limit output size.
12. Commit.

---

# 21. `click_element`

### Sub-phases

1. Define tool.
2. Validate tab ownership.
3. Validate element ID.
4. Check element freshness.
5. Check visibility.
6. Check enabled state.
7. Execute click.
8. Wait for navigation/state change.
9. Return structured result.
10. Add security classification.
11. Test.
12. Commit.

---

# 22. `type_into_element`

### Sub-phases

1. Define schema.
2. Resolve element.
3. Verify textbox.
4. Verify editable.
5. Check sensitive-field rules.
6. Fill value.
7. Return result.
8. Never log typed content.
9. Test.
10. Commit.

---

# 23. `scroll_page`

### Sub-phases

1. Define direction.
2. Define amount.
3. Validate limits.
4. Execute scroll.
5. Wait for rendering.
6. Re-observe page.
7. Return position/result.
8. Test.
9. Commit.

---

# 24. `press_key`

### Sub-phases

1. Define allowed keys.
2. Validate key.
3. Resolve target tab.
4. Execute keyboard action.
5. Handle shortcuts separately.
6. Add confirmation for sensitive shortcuts.
7. Test.
8. Commit.

---

# PHASE 5 — AGENT INTELLIGENCE

# 25. Observe → Decide → Act

Your current loop already exists, so this is an evolution rather than a replacement.

### Sub-phases

1. Define observation state.
2. Add page observation.
3. Add tool results.
4. Tell LLM what changed.
5. Add explicit action state.
6. Add iteration state.
7. Add final verification.
8. Maintain 20-iteration limit.
9. Commit.

---

# 26. Verify Action Results

Every interaction should answer:

```text
Did it work?
```

Instead of:

```text
click → success
```

return:

```text
click
 ↓
page changed?
element disappeared?
URL changed?
expected text appeared?
```

### Sub-phases

1. Capture pre-action state.
2. Execute action.
3. Capture post-action state.
4. Compare.
5. Return result.
6. Detect no-op.
7. Allow retry.
8. Commit.

---

# 27. Handle Stale Elements

Example:

```text
AI sees:

el_10 = Search button
```

Then page changes.

AI tries:

```text
click(el_10)
```

You must detect:

```text
STALE_ELEMENT
```

### Sub-phases

1. Associate element map with page state.
2. Generate page version.
3. Validate version.
4. Reject stale IDs.
5. Request fresh observation.
6. Retry through agent loop.
7. Test.
8. Commit.

---

# 28. Handle Navigation Changes

### Sub-phases

1. Detect WebView2 navigation start.
2. Invalidate element map.
3. Detect navigation completion.
4. Refresh page metadata.
5. Refresh page elements.
6. Update workspace active tab.
7. Notify agent.
8. Test redirects.
9. Test SPA navigation.
10. Commit.

---

# 29. AI Tab Reuse

Only now optimize.

### Sub-phases

1. Define "idle AI tab."
2. Track last usage.
3. Find reusable tabs.
4. Reuse before creating.
5. Respect task ownership.
6. Don't reuse tabs belonging to another active task.
7. Clear stale state.
8. Test.
9. Commit.

---

# PHASE 6 — SECURITY

# 30. Action Policy

Create:

```text
AiBrowserPolicy
```

### Sub-phases

1. Define action categories.
2. Define low-risk actions.
3. Define medium-risk actions.
4. Define high-risk actions.
5. Add policy evaluator.
6. Run policy before tool execution.
7. Return structured denial.
8. Log decision without sensitive data.
9. Test.
10. Commit.

---

# 31. Confirmation System

### Sub-phases

1. Define confirmation request model.
2. Pause agent execution.
3. Display UI.
4. Show exact action.
5. User approves/rejects.
6. Resume or abort.
7. Add timeout.
8. Prevent duplicate execution.
9. Test.
10. Commit.

Example:

```text
AI wants to:

Submit this form to example.com

[Cancel] [Allow once]
```

---

# 32. Prompt Injection Protection

You already have this, so extend it.

### Sub-phases

1. Audit existing 29 patterns.
2. Apply sanitizer to page elements.
3. Apply sanitizer to button names.
4. Apply sanitizer to link labels.
5. Apply sanitizer to placeholders.
6. Preserve user instructions separately.
7. Add explicit data boundaries.
8. Add adversarial tests.
9. Commit.

---

# 33. AI-Owned Tab Access Validation

This is the security boundary.

Every AI browser operation should verify:

```text
Current task
 ↓
Requested tab
 ↓
Does tab belong to this task?
```

If:

```text
Task A
 ↓
tries to access
 ↓
Task B tab
```

reject it.

### Sub-phases

1. Create authorization helper.
2. Validate task ID.
3. Validate ownership.
4. Validate tab existence.
5. Validate workspace membership.
6. Apply to navigation.
7. Apply to reading.
8. Apply to interaction.
9. Test.
10. Commit.

---

# PHASE 7 — TESTING

# 34. Ownership Tests

Test:

```text
Normal tab → User
AI tab → AI
Keep tab → User
Task association works
```

---

# 35. Limit Tests

Test:

```text
0/5
1/5
4/5
5/5
```

Also:

```text
Max = 1
Max = 20
invalid values
```

---

# 36. Cleanup Tests

Test:

```text
Task A tabs close
Task B tabs remain
User tabs remain
Kept tabs remain
```

---

# 37. Keep-Tab Tests

Test:

```text
AI creates tab
 ↓
Keep
 ↓
Task ends
 ↓
Tab survives
```

Also:

```text
Keep All
Close All
Keep one + close others
```

---

# 38. Navigation Isolation Tests

This is one of the most important test suites.

Scenario:

```text
User:
Tab 1 = github.com
Tab 2 = wikipedia.org

AI:
navigate_to(example.com)
```

Expected:

```text
Tab 1 = github.com
Tab 2 = wikipedia.org
Tab 3 = example.com
```

No user tab changes.

---

# 39. Security Tests

Test:

```text
AI → User tab
```

Expected:

```text
BLOCKED
```

Test:

```text
AI Task A → Task B tab
```

Expected:

```text
BLOCKED
```

Test:

```text
page injection → AI instruction
```

Expected:

```text
treated as untrusted content
```

---

# 40. WebView2 Integration Tests

Finally test the entire system.

## Test A — Research

```text
User
 ↓
Ask AI to research
 ↓
AI creates tab
 ↓
Search
 ↓
Read
 ↓
Open related pages
 ↓
Task finishes
 ↓
AI tabs close
```

## Test B — Keep

```text
AI research
 ↓
3 tabs
 ↓
Keep 1
 ↓
Task finishes
 ↓
1 tab remains
```

## Test C — User protection

```text
User reading article
 ↓
AI starts
 ↓
AI navigates
 ↓
User article unchanged
```

## Test D — Tab limit

```text
Max = 2
 ↓
AI opens tab 1
 ↓
AI opens tab 2
 ↓
AI tries tab 3
 ↓
Rejected
```

## Test E — Interaction

```text
AI opens website
 ↓
Observe
 ↓
Find search box
 ↓
Type
 ↓
Click
 ↓
Navigation
 ↓
Observe again
 ↓
Read result
```

---

# Your actual development checkpoints

Rather than thinking of this as 40 huge tasks, I'd create **seven major milestones**.

### Milestone 1 — Tab Ownership

Complete:

```text
1 → 6
```

Result:

> Tabloid understands User tabs vs AI tabs and AI tasks.

---

### Milestone 2 — Safe AI Tabs

Complete:

```text
7 → 13
```

Result:

> AI can create and manage its own tabs without touching the user's tabs.

This is the **first major usable milestone**.

---

### Milestone 3 — AI Tab UX

Complete:

```text
14 → 18
```

Result:

> The user can actually understand and control what the AI is doing.

---

### Milestone 4 — Browser Interaction

Complete:

```text
19 → 24
```

Result:

> The AI can actually interact with websites.

---

### Milestone 5 — Reliable Agent

Complete:

```text
25 → 29
```

Result:

> The AI can observe, act, verify, recover, and reuse resources.

---

### Milestone 6 — Security

Complete:

```text
30 → 33
```

Result:

> The agent has enforceable browser security boundaries rather than relying solely on prompts.

---

### Milestone 7 — Verification

Complete:

```text
34 → 40
```

Result:

> The feature is ready to become part of Tabloid's stable release.

---

## One thing I'd change from the earlier plan

Given that **Tabloid already has 55 tests, CI, `BrowserDataStore`, WebView2 lifecycle management, `AiToolExecutor`, and `ContentSanitizer`**, don't create huge new abstractions unless your current code actually needs them.

The guiding rule should be:

```text
Existing Tab System
        ↑
   AiTabManager
        ↑
    AiWorkspace
        ↑
      AiTask
        ↑
    Agent Loop
```

**Not:**

```text
Existing Tab System
        +
Completely separate AI Browser System
```

The AI should be a **guest inside Tabloid's existing browser**, with explicit ownership and permissions. That will keep the architecture lightweight and consistent with the privacy-first philosophy of the project.
