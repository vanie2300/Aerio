# Aerio Browser — Smooth Hide UI Development Phases

**Target:** Original Aerio (`vanie2300/Aerio`) — C# / WPF / WebView2  
**Feature:** Smooth, whole-browser chrome hide/reveal  
**Status:** Partial implementation in the working tree; Windows runtime validation pending  
**Scope:** Top tab strip, navigation/address toolbar, action buttons, and bottom status bar. **Do not change or animate the sidebar.**

## Objective

Keep the top and bottom browser chrome in the normal window layout. Hide/reveal should smoothly animate their reserved row heights and opacity, allowing the webpage area to expand and contract without abrupt jumps, reloads, or WebView2 recreation.

### Non-negotiable rules

- Keep the existing visual design, themes, tabs, shortcuts, and browsing functionality.
- **Exclude the sidebar entirely.** Do not edit its width, visibility, styles, behavior, or animations.
- Reuse the existing `MainWindow` layout and controls. Do not build a second tab bar or toolbar.
- Keep the WebView2 instance alive throughout each transition; its viewport is expected to resize smoothly as chrome space is inserted or removed.
- Distinguish **Hide UI** from **F11 fullscreen** and from maximizing the window.
- Always provide a keyboard and mouse way to reveal the browser controls.
- Respect reduced-motion preferences.
- Inspect the actual current repository before choosing concrete control names or making patches.
- Use a feature branch with tests; do not claim a fix is complete based solely on compilation.

---

## Phase 1 — Inspect and Map the Existing UI

**Goal:** Locate the real elements controlling Aerio's top and bottom browser chrome.

### Tasks

- [ ] Check out the current merged `main` and record its commit SHA.
- [ ] Inspect `UI/MainWindow.xaml` and related XAML/code-behind files; identify their actual paths and control names.
- [ ] Locate tab strip, navigation controls, address bar, action buttons, window title/drag region, and status bar.
- [ ] Identify all `Grid.RowDefinitions`, nested containers, bindings, visibility switches, and window state handlers.
- [ ] Locate the WebView2 content host and confirm how it is sized and managed.
- [ ] Audit `F11`, focus-address (`Ctrl+L`), tab switching, search, context menus, and current keyboard shortcuts.
- [ ] Record each chrome row's original height and minimum constraints.
- [ ] Determine whether custom title-bar controls share a row with the tabs.
- [ ] Check how DPI scaling, maximized windows, and multi-monitor movement affect sizing.
- [ ] Verify that sidebar-related elements can remain completely untouched.

**Deliverable:** A layout map mapping existing element names to chrome categories and their actual sizes. No functional changes yet.

**Exit criterion:** All target elements are identified and the WebView2 layout dependency is understood.

---

## Phase 2 — Build One Shared Chrome Resizing Controller

**Goal:** Drive all participating UI regions from a single transition progress value.

### Architecture

Create a dedicated controller or `MainWindow` partial implementation, using repository naming conventions. Proposed name: `ChromeVisibilityController` or `MainWindow.Chrome.cs` **only if it fits the current architecture**.

Core state:

- `ChromeMode`: `Normal` or `Hidden` (an intermediate `RevealedTemporarily` state may be represented separately).
- `Progress`: clamped to `0.0`–`1.0` where `0 = fully visible` and `1 = fully hidden`.
- Smoothly interpolate the chrome row heights; the browser viewport expands and contracts with the available layout space.
- One cancellable/reversible animation, not independent timers for each row.
- Input guard for focused address bar, open menus, dialogs, and window dragging.

### Tasks

- [ ] Implement a single `ApplyChromeProgress(double progress)` method.
- [ ] Animate the top, navigation, and status row heights together with chrome opacity.
- [ ] Use `CompositionTarget.Rendering` **only while the animation is active**, unsubscribing afterward.
- [ ] Use elapsed time from a monotonic clock (`Stopwatch`) rather than counting frames.
- [ ] Use approximately **200–240 ms**, cubic ease-out as an initial *button-triggered* motion setting.
- [ ] When a new transition begins mid-animation, start from the current progress to avoid snapping.
- [ ] In instant/reduced-motion mode, update the final state without subscribing to rendering callbacks.
- [ ] Avoid calling `UpdateLayout()` on every frame.
- [ ] Unsubscribe animation handlers when the window closes/disposes.

### Smoothness rule

- **User-driven dragging:** follow the pointer directly, without easing or delayed animation.
- **Button/shortcut hide/reveal:** animate row heights and opacity together using easing.
- Neither path may recreate, reload, or zoom/scale WebView2.

**Deliverable:** A tested controller independent of chrome visuals.

**Exit criterion:** Rapid hide/show reversals settle at exactly the requested state without duplicate callbacks.

---

## Phase 3 — Connect Top and Bottom Chrome

**Goal:** Keep the existing chrome in its normal layout rows and smoothly return its reserved space to the webpage when hidden.

| Element | Visible state | Hidden state | Treatment |
|---|---:|---:|---|
| Tab strip | 40 DIP layout row | Row smoothly shrinks to zero | Existing controls; no duplicate tab strip |
| Navigation/address toolbar | 40 DIP layout row | Row smoothly shrinks to zero | Existing controls |
| Toolbar buttons | Existing layout | Fade with navigation row | Existing commands |
| Bottom status bar | 22 DIP layout row | Row smoothly shrinks to zero | Existing control |
| WebView2 content area | Remaining space | Expands into released chrome space | Same instance; no reload/recreation |
| Sidebar | Existing state | **Unchanged** | **No changes** |

### Tasks

- [ ] Place chrome controls in the normal top and bottom grid rows and preserve existing hit testing.
- [ ] Animate row heights and opacity from one shared progress value.
- [ ] Disable hit testing for fully hidden chrome so the webpage receives mouse input.
- [ ] Keep tab selected state, address field value, and toolbar commands unchanged.
- [ ] Check window title controls remain reachable when the chrome is hidden.
- [ ] Preserve native window moving/maximizing behavior; provide a reveal route for window controls.
- [ ] Verify status messages do not alter the animation or cause unexpected layout changes.

**Deliverable:** Fully synchronized top + bottom chrome animation.

**Exit criterion:** The chrome rows and webpage area resize smoothly; the existing WebView2 remains alive and does not reload.

---

## Phase 4 — Hide, Reveal, and Restore Interactions

**Goal:** Make immersive mode convenient without locking the user out of browser controls.

### Interactions

- [ ] Add a configurable **Hide UI** command and a discoverable menu entry.
- [ ] Proposed default shortcut: `Ctrl+Shift+H`; check for existing shortcut collisions before assigning.
- [ ] `Ctrl+L` reveals chrome if needed and focuses the address bar.
- [ ] A thin top-edge trigger temporarily reveals top controls while chrome is hidden.
- [ ] Use a small hover activation threshold/delay to avoid accidental reveals.
- [ ] After the pointer leaves, delay auto-hide briefly, then hide only when safe.
- [ ] Keep chrome visible while the address bar is focused, a popup/menu is open, or a dialog is active.
- [ ] Provide an explicit Restore UI command and reliable keyboard fallback.
- [ ] Define `Esc` behavior carefully so webpage `Esc` handling isn't unexpectedly hijacked; do not override it blindly.
- [ ] Keep fullscreen (F11) and hide UI as independent state flags.
- [ ] Preserve prior chrome mode when entering or exiting fullscreen, as specified by the app's settings.

### Layout transition

Keep the existing chrome as regular grid children rather than WPF popup overlays. Animate the three chrome row heights and their opacity from the same reversible progress value so the browser area gets space back gradually.

**Deliverable:** Accessible toggle, reveal, and restore behavior.

**Exit criterion:** Users can always reach the address bar, tab controls, and window controls.

---

## Phase 5 — Performance and Visual Polish

**Goal:** Keep the page responsive while the browser area smoothly changes size with the chrome rows.

### Tasks

- [ ] Profile repeated layout passes during animation using Windows/WPF diagnostics.
- [ ] Verify no WebView2 navigation/reinitialization or tab re-templating occurs.
- [ ] Test a static page, scrolling page, video, and multiple open tabs.
- [ ] Test maximize, restore, resize, 100% / 125% / 150% DPI, and multiple monitors.
- [ ] Avoid expensive animated blur, shadows, and unnecessary opacity layers.
- [ ] Remove redundant listeners, timers, layout invalidations, and any forced sync calls.
- [ ] Ensure transition progress is derived from elapsed time; it should remain consistent at 60 Hz and 120 Hz.
- [ ] Keep mouse-driven sizing completely immediate and button-driven sizing interruptible.
- [ ] Support reduced motion / animation disabled settings.

### Measurement

Capture a baseline and post-change comparison on the same machine: frame time, perceived stutter, UI-thread utilization, browser responsiveness, and any WebView2 flashes. Avoid promising a fixed FPS until measured.

**Deliverable:** Performance notes with reproducible test conditions.

**Exit criterion:** No visible stutter, flicker, lost input, or unexpected layout jumps under tested workloads.

---

## Phase 6 — Regression and Edge-Case Testing

**Goal:** Ensure immersive mode doesn't break normal browser behavior.

| Scenario | Expected outcome |
|---|---|
| Hide while YouTube is playing | Playback continues; WebView2 isn't recreated |
| Toggle rapidly 20 times | No frozen intermediate state or event subscription leaks |
| Interrupt hide with show | Reverse smoothly from current progress |
| Focus address bar while hidden | Chrome reveals and address bar receives focus |
| Open popup or application menu | Chrome doesn't auto-hide while interacting |
| Change active tab during/after transition | Correct tab stays selected |
| Create/close tabs while hidden | Tab model remains correct |
| Enter/exit F11 fullscreen | Fullscreen remains independent of Hide UI |
| Minimize and restore | Chrome returns to the intended state |
| Maximize and use window controls | Minimize/maximize/close remain reachable |
| Change DPI or screen | No clipped controls or invalid row dimensions |
| Restart Aerio | Saved preference applies without a flash or broken state |
| Network error page | Chrome controls remain recoverable |
| Sidebar exists or is toggled elsewhere | Sidebar behavior remains unchanged |

### Automated tests

- [ ] Test progress clamping and interpolation (`0`, intermediate values, `1`).
- [ ] Test interruption and reversal state transitions.
- [ ] Test temporary reveal vs persistent mode transitions.
- [ ] Test focus/menu guards against automatic hiding.
- [ ] Test reduced-motion instantaneous completion.
- [ ] Test persisted preference loading with invalid or missing settings.
- [ ] Test no duplicated rendering subscriptions after repeated toggles.

**Deliverable:** Passing automated tests plus Windows manual-test results.

**Exit criterion:** No outstanding critical/high regressions caused by the feature.

---

## Phase 7 — Merge and Release Validation

**Goal:** Integrate the feature safely after it works with the current Aerio UI.

- [ ] Confirm clean build and all existing tests pass.
- [ ] Review the diff specifically for accidental sidebar changes (there should be **none**).
- [ ] Confirm WebView2 instances are not disposed or replaced during hide/show.
- [ ] Run static analysis and inspect new warnings.
- [ ] Verify menu labels, keyboard shortcut settings, and accessibility.
- [ ] Confirm theme compatibility and window controls.
- [ ] Record measured animation behavior and any remaining limitations.
- [ ] Verify CI on the feature branch and on merged `main`.
- [ ] Update user-facing settings/help documentation.
- [ ] Merge only after code review and Windows smoke testing.

**Deliverable:** Merge-ready feature with tests, review notes, and release verification.

---

## Recommended Implementation Order

1. Identify the actual existing row/control names from the latest merged Aerio repository.
2. Build the shared progress controller and unit-test it.
3. Integrate tab strip + navigation row, then status row.
4. Add manual Hide UI and Restore UI commands.
5. Add safe pointer-edge reveal and auto-hide guards.
6. Profile animated layout passes and verify the WebView2 remains alive without stutter or reloads.
7. Run complete Windows regression tests, then merge.

## Definition of Done

- [ ] Top tab strip, navigation/address toolbar, and bottom status bar smoothly insert/remove their layout space.
- [ ] The WebView2 viewport expands and contracts smoothly with the animated layout.
- [ ] No WebView2 recreation, reload, stretch, or zoom artifacts.
- [ ] User-triggered hide/reveal is smooth and reversible.
- [ ] Keyboard and mouse restore paths are always available.
- [ ] Sidebar was not edited.
- [ ] Windows controls, shortcuts, tabs, fullscreen, and navigation still work.
- [ ] Reduced-motion support works.
- [ ] Automated tests, Windows manual tests, and CI pass.

## Reference Documentation

- Microsoft Learn — WPF frame-by-frame rendering: https://learn.microsoft.com/en-us/dotnet/desktop/wpf/graphics-multimedia/how-to-render-on-a-per-frame-interval-using-compositiontarget
- Microsoft Learn — WPF layout performance: https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/optimizing-performance-layout-and-design
- Microsoft Learn — WebView2 in WPF: https://learn.microsoft.com/en-us/microsoft-edge/webview2/platforms/wpf

> **Implementation status:** The working tree now contains a row-based chrome insertion/removal animation, a configurable hide/show shortcut, address-bar and edge reveal paths, and reduced-motion handling. The browser area resizes as the chrome rows animate. This does not complete every roadmap phase, and the UI has not yet been validated at runtime on Windows. Verify the current source and test the listed edge cases before considering the feature complete.
