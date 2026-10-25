# HackerOS Accessibility Evidence and WCAG 2.2 AA Checklist

## Overview

HackerOS targets **WCAG 2.2 Level AA**. This document records the intended contract separately from evidence that has actually been collected. It is not a blanket conformance claim.

---

## 1. Key Accessibility Standards

### A. Color Contrast & Legibility
- **Text Contrast:** Text against background surfaces maintains at least a **4.5:1** contrast ratio (e.g. `#c9d1d9` text on `#0d1117` background = 14.2:1).
- **UI Component Boundaries:** Interactive controls, focus rings, and border indicators maintain at least a **3:1** contrast ratio.

### B. Keyboard Navigation & Focus Management
- **Full Keyboard Accessibility:** Every interactive button, menu item, window control, input field, and tree node is reachable and operable via keyboard.
- **Focus Indicators:** All focusable elements show a distinct, high-contrast focus ring:
  ```css
  :focus-visible {
    outline: 2px solid var(--hos-focus-ring, #42d392);
    outline-offset: 2px;
  }
  ```
- **Modal Dialog Trapping:** Modal file dialogs and prompt popups trap keyboard focus within the dialog container until closed or cancelled using `Escape`.

### C. WAI-ARIA & Screen Reader Semantics
- **Window Windows:** Use `role="region"` or `role="dialog"` with `aria-labelledby` pointing to the window title bar.
- **Modal Dialogs:** Use `role="dialog" aria-modal="true"` with explicit `aria-label` or `aria-labelledby`.
- **Taskbar & Launcher:** The launcher trigger exposes `aria-expanded` and
  `aria-haspopup`; the Start menu has a stable accessible name, native-button
  launch/quick-link controls, and an active keyboard item mirrored by
  `aria-activedescendant`. Pin toggles expose `aria-pressed`, while move-up/down
  buttons provide a keyboard/touch alternative to drag-and-drop.

### D. Reduced Motion Support
- Users requesting reduced motion via the browser/operating system have theme
  transitions disabled automatically. The saved HackerOS animation toggle sets
  the same bounded motion state on `ThemeScope`; neither path injects CSS:
  ```css
  @media (prefers-reduced-motion: reduce) {
    [data-theme] {
      --hos-transition-fast: 0ms;
      --hos-transition-normal: 0ms;
    }
  }
  ```

---

## 2. Automated & Manual Accessibility Testing

1. **Automated representative check:** `IndexedDbBrowserContractTests.Representative_platform_surfaces_have_no_axe_violations` uses `Deque.AxeCore.Playwright` 4.12.0 and currently verifies that the idle desktop, a window, a dialog, the full-screen Terminal renderer, the local Monaco Code Editor surface, and the Hack Paint canvas surface contain no serious or critical axe findings. Moderate findings are still reported by axe and are not treated as resolved.
2. **Automated real-app check (desktop + mobile):** `HackerOs.UI.E2E.Tests.AccessibilityAndVisualCoverageTests.Representative_real_app_surfaces_have_no_axe_violations_and_no_mobile_overflow` scans the real desktop shell, App Launcher, File Explorer, Settings, Code Editor, and Hack Paint windows — not the isolated harness — at both 1280×800 and a representative 375×812 mobile check, persists a screenshot per surface/viewport to `artifacts/playwright/accessibility/`, and exercises a live HackerOS → Windows 7 → HackerOS theme change through Settings. This scan found and drove fixes for real serious/critical violations: a missing `<title>` on two of the three Blazor hosts (`test/test` and `HackerOs.Server` were never loading the app's own dark-theme CSS at all — see `docs/pwa-release.md` and the `HackerOs.Windowing.Blazor` `WindowHost.razor` fix below), a MudMenu context-menu activator with no accessible name, a broken `aria-labelledby` on every window (`WindowHost.razor` passed the literal string `"TitleId"` instead of `@TitleId` to `WindowChrome`, so no window dialog ever had a working accessible name), unlabeled File Explorer row checkboxes, a CodeMirror content region with no accessible name, an ARIA-prohibited `aria-label` on a plain `role`-less div, a Code Editor tab strip that misused `role="tablist"`/`role="tab"` without the required structure, an unfocusable scrollable Hack Paint canvas viewport, and insufficient color contrast (File Explorer loading text, Settings' unthemed MudTabs header). All are fixed; this test passes as of the fixes.
3. **Still-required expanded automation:** the complete Terminal window and taskbar still need dedicated scans (only the full-screen Terminal *renderer* and an isolated taskbar scenario are covered so far). Keyboard order/traps/restoration, Escape behavior, 200% zoom, long text, reduced motion, and RTL also still require executable coverage — none of that exists yet, automated or manual.
4. **Human evidence checklist:** these items remain unchecked until a person performs the steps and records the browser/OS/assistive-technology versions and artifacts. Full instructions (what to run, exact steps, where to record results): `docs/Human-test-needed-p2-GATE-003.md`.
   - [ ] Launch Terminal from the Start menu using only `Tab`, arrow keys, and `Enter`.
   - [ ] Pin Terminal, move it up/down, unpin it, reload, and confirm every operation works using only the keyboard and is announced correctly.
   - [ ] Inspect all nine theme previews and real shell states at 200% zoom, including focus/selected/error contrast in the light themes.
   - [ ] Move and resize a window with the documented keyboard commands and confirm a visible focus indicator throughout.
   - [ ] Open a modal dialog, verify focus cannot escape it, close it with `Escape`, and confirm focus returns to the invoking control.
   - [ ] Navigate the File Explorer tree using `Up`, `Down`, `Right`, and `Left` and verify state changes are announced.
   - [ ] Complete a screen-reader pass over the desktop, launcher, taskbar, window chrome, dialogs, and each first-party app.

Store Playwright traces, screenshots, console output, and network logs under `artifacts/playwright/`. Store human test notes and screen-reader recordings under `artifacts/accessibility/manual/<date>/`. Do not check a human-evidence item from automated output alone.
