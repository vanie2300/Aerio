Absolutely. The roadmap is solid conceptually, but I would **restructure it before implementation**.

The biggest issue is that the current plan mixes **architecture, implementation, UX, security, and testing** in a way that can cause you to build something and only later discover that the underlying ownership model is wrong.

I would also make **Keep/Close, authorization, task context, and lifecycle cleanup first-class parts of the architecture**, rather than treating them as UI features.

# Aerio AI Browser — Revised Implementation Roadmap

## Core architecture

The architecture should remain:

```text
                    ┌──────────────────────┐
                    │    Existing Tabs     │
                    │   WebView2 Lifecycle │
                    └──────────┬───────────┘
                               │
                               ▼
                    ┌──────────────────────┐
                    │    AiTabManager      │
                    │ create / close /     │
                    │ keep / ownership     │
                    └──────────┬───────────┘
                               │
                ┌──────────────┴──────────────┐
                ▼                             ▼
        ┌──────────────┐              ┌──────────────┐
        │   AiTask     │              │ AiWorkspace  │
        │ goal/status  │              │ task tabs    │
        └──────────────┘              └──────────────┘
                │                             │
                └──────────────┬──────────────┘
                               ▼
                       ┌───────────────┐
                       │ Agent Loop    │
                       │ Observe       │
                       │ Decide        │
                       │ Act           │
                       └───────┬───────┘
                               ▼
                       ┌───────────────┐
                       │ Browser Tools │
                       └───────────────┘
```

The key principle:

> **AI does not get a second browser. AI gets controlled access to the existing browser's tab system.**

---

# PHASE 0 — EXISTING SYSTEM AUDIT

Before adding anything, establish exactly what already exists.

## 0.1 Audit existing tab model

Find:

```text
Tab model/state
Tabs collection
Active tab
Tab ID
WebView2 reference
Tab creation
Tab closing
Tab activation
Navigation
Tab UI
```

Inspect:

```text
MainWindow.Tabs.cs
MainWindow.WebView2.cs
MainWindow.Navigation.cs
BrowserDataStore
AiToolExecutor
existing AI services
existing tests
```

## 0.2 Trace lifecycle

Document:

```text
CREATE
  ↓
WebView2 initialization
  ↓
Tab registration
  ↓
UI registration
  ↓
Activation
```

and:

```text
CLOSE
  ↓
WebView2 cleanup
  ↓
Tab removal
  ↓
UI removal
  ↓
Active-tab replacement
```

## 0.3 Audit current AI tools

Determine exactly how these currently work:

```text
open_tab
navigate_to
close_tab
search
read_page
```

Do **not** redesign them yet.

## 0.4 Audit existing security

Inspect:

```text
ContentSanitizer
prompt injection handling
tool validation
AI permissions
confirmation mechanisms
```

## 0.5 Establish baseline tests

Run the existing:

```text
55+ tests
```

and record the baseline.

### Milestone 0

> Existing browser behavior is understood and protected from regression.

---

# PHASE 1 — TAB OWNERSHIP FOUNDATION

This phase establishes the data model.

## 1.1 Add `TabOwner`

```csharp
public enum TabOwner
{
    User,
    AI
}
```

Tab state:

```csharp
public TabOwner Owner { get; internal set; } = TabOwner.User;
```

Prefer `internal set` rather than allowing arbitrary code to change ownership.

---

## 1.2 Add `AiTaskId`

```csharp
public string? AiTaskId { get; internal set; }
```

Invariant:

```text
User tab
    Owner = User
    AiTaskId = null

AI tab
    Owner = AI
    AiTaskId != null
```

Add:

```csharp
public bool IsAiOwned()
{
    return Owner == TabOwner.AI;
}
```

---

# 1.3 Add `AiTask`

```csharp
public sealed class AiTask
{
    public string Id { get; init; } = Guid.NewGuid().ToString();

    public string Goal { get; init; } = string.Empty;

    public AiTaskStatus Status { get; internal set; }

    public DateTime StartedAt { get; init; }

    public DateTime? CompletedAt { get; internal set; }
}
```

Status:

```csharp
public enum AiTaskStatus
{
    Running,
    Completed,
    Failed,
    Cancelled
}
```

Don't persist this yet.

---

# 1.4 Add `AiWorkspace`

```csharp
public sealed class AiWorkspace
{
    public string TaskId { get; }

    public string? ActiveTabId { get; internal set; }

    public List<string> TabIds { get; } = new();

    public AiWorkspace(string taskId)
    {
        TaskId = taskId;
    }
}
```

Important:

```text
Browser ActiveTab
```

and:

```text
Workspace ActiveTab
```

are **not the same thing**.

The user can be viewing Tab 1 while the AI is operating on Tab 3.

---

# 1.5 Add `AiTaskContext`

This is missing from the original roadmap and I consider it important.

Instead of passing `AiTask` and `AiWorkspace` separately everywhere:

```csharp
public sealed class AiTaskContext
{
    public AiTask Task { get; }
    public AiWorkspace Workspace { get; }

    public AiTaskContext(
        AiTask task,
        AiWorkspace workspace)
    {
        Task = task;
        Workspace = workspace;
    }
}
```

Then:

```text
AiToolExecutor
       ↓
AiTaskContext
       ↓
AiWorkspace
       ↓
AiTabManager
```

This makes it much harder for a tool to accidentally operate without knowing which task it belongs to.

---

# PHASE 2 — SETTINGS

Add settings before implementing tab limits and cleanup.

```csharp
public sealed class AiBrowserSettings
{
    public int MaxAiTabs { get; set; } = 5;

    public bool ProtectUserTabs { get; set; } = true;

    public bool CloseAiTabsAfterTask { get; set; } = true;

    public bool AskBeforeExceedingTabLimit { get; set; } = true;
}
```

I would rename:

```text
ProtectActiveUserTab
```

to:

```text
ProtectUserTabs
```

because your actual security requirement is:

> AI cannot modify **any** User-owned tab by default.

Not just the active one.

Validate:

```text
1–20
```

and use `BrowserDataStore`.

Missing properties in old JSON should receive defaults.

### Milestone 2

> Tab ownership, tasks, workspaces, and AI settings exist without changing normal browser behavior.

---

# PHASE 3 — `AiTabManager`

This becomes the **single authority for AI tab lifecycle and ownership**.

Location:

```text
Processes/AI/Browser/AiTabManager.cs
```

Core API:

```csharp
GetAiTabs()
GetAiTabCount()

GetTab(tabId)

CanCreateAiTab()

CreateAiTab(context)

CloseAiTab(context, tabId)

CloseTaskTabs(context)

KeepAiTab(context, tabId)

KeepAllTaskTabs(context)
```

I'd also add:

```csharp
CanAccessTab(context, tabId)
```

and:

```csharp
ResolveAiTab(context, tabId)
```

### Ownership rule

Other components should **not** directly do:

```csharp
tab.Owner = TabOwner.AI;
```

Instead:

```text
AiTabManager
       ↓
changes ownership
```

This gives you one place to enforce invariants.

---

# PHASE 4 — AI TAB SECURITY BOUNDARY

This should happen **before** connecting the tools.

Create:

```text
AiBrowserAuthorization
```

or:

```text
AiBrowserPolicy
```

Its fundamental question is:

```text
Who is asking?
        ↓
Which task?
        ↓
Which tab?
        ↓
Does that task own the tab?
        ↓
Is the action permitted?
```

Example:

```csharp
AuthorizeTabAccess(
    AiTaskContext context,
    string tabId
)
```

Possible results:

```text
ALLOWED
USER_TAB_PROTECTED
TAB_NOT_FOUND
TAB_BELONGS_TO_OTHER_TASK
TASK_NOT_ACTIVE
```

This is much safer than relying on individual tools to remember ownership checks.

---

# PHASE 5 — CONNECT `open_tab`

Now make:

```text
open_tab
```

task-aware.

Flow:

```text
Agent
 ↓
AiToolExecutor
 ↓
AiTaskContext
 ↓
AiTabManager.CreateAiTab()
 ↓
Existing Tab System
 ↓
WebView2
```

Creation:

```text
Owner = AI
AiTaskId = task.Id
```

Registration:

```text
workspace.TabIds.Add(tabId)
workspace.ActiveTabId = tabId
```

Return:

```json
{
  "success": true,
  "tab_id": "...",
  "url": "...",
  "title": "..."
}
```

---

# PHASE 6 — AI TAB LIMIT

Before creating:

```text
Current AI tabs
        ↓
MaxAiTabs
        ↓
Can create?
```

If not:

```json
{
  "success": false,
  "error_code": "AI_TAB_LIMIT_REACHED"
}
```

Never create first and check afterward.

Test:

```text
0/5
1/5
4/5
5/5
Max = 1
Max = 20
Invalid values
```

---

# PHASE 7 — AI NAVIGATION ISOLATION

Now modify:

```text
navigate_to
```

to resolve its target through the task workspace.

```text
navigate_to
      ↓
AiTaskContext
      ↓
workspace.ActiveTabId
      ↓
AiBrowserAuthorization
      ↓
target WebView2
```

If there is no AI tab:

```text
Create AI tab
        ↓
Set ActiveTabId
        ↓
Navigate
```

### Important rule

Never use:

```csharp
CurrentTab
```

as the AI's implicit navigation target.

That's the source of one of the most dangerous bugs in an AI browser.

---

# PHASE 8 — AI TAB CLEANUP

When:

```text
AiTask
   ↓
Completed / Failed / Cancelled
```

run:

```text
AiTabManager.CloseTaskTabs()
```

Filter:

```text
Owner == AI
AND
AiTaskId == task.Id
```

Never clean based solely on:

```text
Owner == AI
```

because another AI task may still be running.

---

# PHASE 9 — KEEP / OWNERSHIP TRANSFER

This should be treated as a **core lifecycle operation**, not merely a UI feature.

## Keep

```text
AI tab
 ↓
Authorization
 ↓
AiTabManager.KeepAiTab()
 ↓
Owner = User
AiTaskId = null
 ↓
Remove from workspace
```

Do **not** recreate the tab.

The same WebView2 remains alive.

Preserve:

```text
URL
history
scroll position
page state
session state
```

### Keep All

```text
KeepAllTaskTabs()
```

transfers only that task's tabs.

### Critical invariant

After Keep:

```text
Owner == User
AiTaskId == null
```

Therefore task cleanup cannot touch it.

---

# PHASE 10 — CLOSE CONTROLS

Now build the UI.

AI-owned tab:

```text
┌─────────────────────────────────────┐
│ 🤖 Microsoft Docs        [Keep] [×] │
└─────────────────────────────────────┘
```

Controls:

```text
Keep
Close
```

Workspace:

```text
AI Research

🤖 GitHub             [Keep] [×]
🤖 Microsoft Docs     [Keep] [×]
🤖 Stack Overflow     [Keep] [×]

[Keep All] [Close All]
```

### Behavior

```text
Keep
 ↓
AiTabManager.KeepAiTab()

Close
 ↓
AiTabManager.CloseAiTab()

Keep All
 ↓
AiTabManager.KeepAllTaskTabs()

Close All
 ↓
AiTabManager.CloseTaskTabs()
```

The UI should **not** directly manipulate:

```text
Owner
AiTaskId
WebView2
Workspace.TabIds
```

---

# PHASE 11 — AI TAB INDICATORS

Show AI ownership visually:

```text
🤖 Research
🤖 GitHub
🤖 Documentation
```

After Keep:

```text
Research
GitHub
Documentation
```

The AI indicator disappears because ownership changed.

---

# PHASE 12 — AI WORKSPACE

Create a workspace panel:

```text
AI Browser
────────────────────

Research WebView2

Tabs
🤖 Microsoft Docs
🤖 GitHub
🤖 Tutorial

3 / 5 AI tabs

Status: Running
```

Allow:

```text
Select AI tab
Keep
Close
Keep All
Close All
```

But selecting an AI tab **must not necessarily change the browser's user-active tab**.

You need to decide whether "inspect AI tab" means:

```text
show it to user
```

or:

```text
make it the global browser active tab
```

I'd recommend keeping these concepts separate.

---

# PHASE 13 — WEBSITE OBSERVATION

Only after tab isolation works:

```text
PageObserver
```

Responsibilities:

```text
Visible text
Links
Buttons
Inputs
Selects
Checkboxes
Element IDs
Visibility
Disabled state
```

Each observation gets a page/version identity:

```text
tabId
pageVersion
elementId
```

This is necessary for stale-element detection later.

---

# PHASE 14 — WEBSITE ACTIONS

Implement:

```text
get_page_elements
click_element
type_into_element
scroll_page
press_key
```

Every operation must perform:

```text
Task authorization
        ↓
Tab authorization
        ↓
Element validation
        ↓
Security policy
        ↓
Action
        ↓
Verification
```

Not:

```text
Tool
 ↓
WebView2
```

---

# PHASE 15 — ACTION VERIFICATION

Every action should return something stronger than:

```text
success: true
```

For example:

```json
{
  "success": true,
  "action": "click",
  "tab_id": "tab-123",
  "page_changed": true,
  "url_changed": true
}
```

The agent should know whether the action actually produced the expected result.

---

# PHASE 16 — STALE ELEMENTS & NAVIGATION

Implement:

```text
pageVersion
```

When navigation occurs:

```text
invalidate old element map
        ↓
new page version
        ↓
new observation
```

Then:

```text
click(old_element)
        ↓
STALE_ELEMENT
        ↓
agent observes again
```

Also handle:

```text
redirects
SPA navigation
navigation start
navigation complete
```

---

# PHASE 17 — TAB REUSE

Only after everything else is stable.

Define:

```text
idle AI tab
```

Then:

```text
Need new tab?
       ↓
Reusable task-owned tab?
       ↓
Yes → reuse
No  → create
```

Never reuse:

```text
User tabs
Another active task's tabs
```

---

# PHASE 18 — SECURITY POLICY

Now create the broader:

```text
AiBrowserPolicy
```

Classify:

### Low risk

```text
Read page
Scroll
Observe
Navigate
Open tab
```

### Medium risk

```text
Click
Type
```

### High risk

```text
Submit forms
Purchases
Account changes
Destructive actions
Sensitive operations
```

Policy:

```text
Tool
 ↓
Action classification
 ↓
Policy
 ↓
Allow / Deny / Confirm
```

---

# PHASE 19 — CONFIRMATION

For sensitive actions:

```text
AI wants to:

Submit this form to example.com

[Cancel] [Allow once]
```

The confirmation system must:

```text
pause task
 ↓
wait for user
 ↓
approve/reject
 ↓
resume/abort
```

Also protect against double execution.

---

# PHASE 20 — PROMPT INJECTION

Extend your existing `ContentSanitizer`.

Treat all website content as:

```text
UNTRUSTED DATA
```

Never allow page content to become equivalent to:

```text
SYSTEM INSTRUCTION
USER INSTRUCTION
SECURITY POLICY
```

Sanitize:

```text
visible text
button labels
link labels
placeholders
ARIA labels
DOM text
```

Maintain explicit boundaries between:

```text
USER REQUEST
AI INSTRUCTIONS
WEBSITE CONTENT
TOOL RESULTS
```

---

# PHASE 21 — AGENT LOOP

Now evolve the existing loop:

```text
OBSERVE
   ↓
DECIDE
   ↓
ACT
   ↓
VERIFY
   ↓
OBSERVE
```

With:

```text
iteration limit
error recovery
stale-element recovery
navigation recovery
tab management
final verification
```

Keep your existing 20-iteration safety limit unless testing shows a better value is needed.

---

# PHASE 22 — TESTING

Instead of leaving testing until the very end, test each milestone as you go.

## Ownership

```text
User tab → User
AI tab → AI
Keep → User
Task ID correct
```

## Isolation

```text
AI → own tab       ALLOW
AI → user tab      BLOCK
Task A → Task B    BLOCK
```

## Limits

```text
0/5
4/5
5/5
1 max
20 max
invalid
```

## Cleanup

```text
Task A → closes
Task B → survives
User → survives
Kept → survives
```

## Keep

```text
AI tab
 ↓
Keep
 ↓
Task finishes
 ↓
Tab remains
```

## Navigation

```text
User Tab 1 → GitHub
User Tab 2 → Wikipedia

AI → example.com

Expected:

Tab 1 → GitHub
Tab 2 → Wikipedia
AI Tab → example.com
```

---

# PHASE 23 — END-TO-END TESTING

### Test A — Research

```text
User
 ↓
Ask AI to research
 ↓
AI creates tabs
 ↓
AI navigates
 ↓
AI reads
 ↓
AI opens related pages
 ↓
Task completes
 ↓
Temporary AI tabs close
```

### Test B — Keep

```text
AI creates 3 tabs
 ↓
User keeps 1
 ↓
Task completes
 ↓
2 close
 ↓
1 remains as User tab
```

### Test C — User protection

```text
User reading GitHub
 ↓
AI starts
 ↓
AI navigates
 ↓
GitHub tab unchanged
```

### Test D — Limit

```text
Max = 2

AI tab 1 → success
AI tab 2 → success
AI tab 3 → rejected
```

### Test E — Website interaction

```text
Open
 ↓
Observe
 ↓
Find search box
 ↓
Type
 ↓
Click
 ↓
Navigate
 ↓
Observe again
 ↓
Read
 ↓
Verify
```

---

# Final Milestones

I would reduce the original 40 tasks to these **8 implementation milestones**:

| Milestone | Scope                        | Result                                            |
| --------- | ---------------------------- | ------------------------------------------------- |
| **0**     | Existing-system audit        | Know exactly what we're integrating with          |
| **1**     | Ownership + Task + Workspace | AI has an identity and controlled browser context |
| **2**     | AiTabManager + limits        | AI can safely create/manage tabs                  |
| **3**     | Isolation + cleanup + Keep   | **Core AI tab system works safely**               |
| **4**     | AI tab UX                    | User can see and control AI tabs                  |
| **5**     | Page observation + actions   | AI can interact with websites                     |
| **6**     | Agent reliability            | Observe → Act → Verify → Recover                  |
| **7**     | Security + confirmation      | Enforceable browser security                      |
| **8**     | Integration testing          | Stable feature                                    |

## The most important architectural change

The original roadmap puts:

```text
Keep / Close Controls
```

at Phase 3, but the **actual Keep operation belongs in the core lifecycle**.

So the correct separation is:

```text
CORE
─────────────────────────────

AiTabManager
 ├── CreateAiTab()
 ├── CloseAiTab()
 ├── CloseTaskTabs()
 ├── KeepAiTab()
 └── KeepAllTaskTabs()


UI
─────────────────────────────

Keep button
Close button
Keep All
Close All
AI badges
Workspace panel
```

That means the UI is simply a client of the browser-management layer.

### The final ownership lifecycle

```text
                    CREATE
                       │
                       ▼
                ┌─────────────┐
                │ AI-owned    │
                │             │
                │ Owner = AI  │
                │ TaskId = X  │
                └──────┬──────┘
                       │
              ┌────────┴────────┐
              │                 │
            KEEP              CLOSE
              │                 │
              ▼                 ▼
       ┌─────────────┐     Existing
       │ User-owned  │     close lifecycle
       │             │
       │ Owner=User  │
       │ TaskId=null │
       └──────┬──────┘
              │
              ▼
       Task cleanup
              │
              ▼
           IGNORED
```

**This is the version I'd actually use for implementation.** It keeps your existing WebView2/tab infrastructure intact, makes `AiTabManager` the ownership authority, makes `AiTaskContext` explicit, puts authorization before browser operations, and makes **Keep a real ownership transfer rather than a special "don't close this tab" flag**.


review everything in the codebase find bugs and find solutions, before implementing the fix please ask me first then suggest whats need to be done in everything