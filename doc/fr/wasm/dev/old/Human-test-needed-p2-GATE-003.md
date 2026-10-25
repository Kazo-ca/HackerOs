# Human accessibility test needed — P2-GATE-003

## Why this document exists

`P2-GATE-003` requires desktop/mobile screenshots and accessibility checks showing
no overlap, clipped text, inaccessible controls, or blank third-party canvases.
The automated half of this is now real, executable evidence:

- `Tests/HackerOs.E2E.Tests/IndexedDbBrowserContractTests.Representative_platform_surfaces_have_no_axe_violations`
  — 7 isolated-harness scenarios (idle, window, dialog, full-screen Terminal,
  local Monaco Code Editor, Hack Paint canvas, taskbar).
- `Tests/HackerOs.UI.E2E.Tests/AccessibilityAndVisualCoverageTests.Representative_real_app_surfaces_have_no_axe_violations_and_no_mobile_overflow`
  — the real desktop shell, launcher, File Explorer, Settings, Code Editor, and
  Hack Paint windows, axe-scanned and screenshotted at both desktop (1280×800)
  and mobile (375×812) viewports, with screenshots persisted to
  `artifacts/playwright/accessibility/` instead of being discarded.

What automated axe scanning **cannot** verify is whether the app is actually
usable with a keyboard alone or a screen reader — that requires a person.
`docs/accessibility.md` section 2.3 lists this checklist as unchecked; this
document gives the exact steps to run it. Do not check an item from automated
output alone — each one needs a person to actually do it.

## What to run

The real app (not the isolated test harness) is the right target for this
pass, since it has actual navigation between apps.

```powershell
cd wasm2/HackerOs
dotnet run --configuration Release --project test/test/test.csproj --urls http://127.0.0.1:5252
```

Open `http://127.0.0.1:5252` in a real browser (Chrome/Edge for the screen-reader
pass, since NVDA/JAWS/VoiceOver support varies — see the browser-support note
below) and create a local profile when prompted (any login/display name,
password ≥ 8 characters).

Alternative: a fresh `dotnet publish` of `OS/HackerOs.Ecosystem` served
statically also works and is closer to production (see
`Tests/HackerOs.Pwa.E2E.Tests/PublishedAppHost.cs` for exactly how the
automated PWA suite does this, if you want the identical setup) — either
target is fine for this checklist.

## Checklist

Record results directly in `docs/accessibility.md` section 2.3 by changing
`- [ ]` to `- [x] YYYY-MM-DD — <your name>, <browser/OS/AT versions>` for each
item, and save any recordings/notes under
`artifacts/accessibility/manual/<date>/`.

1. **Keyboard-only Terminal launch**
   - Using only `Tab`, arrow keys, and `Enter` (no mouse), open the App
     Launcher, find Terminal, and launch it.
   - Confirm: the launcher is reachable and operable by keyboard alone, and a
     visible focus indicator is present at every step.

2. **Keyboard window move/resize**
   - With a window focused, use the documented keyboard commands to move and
     then resize it (check `docs/window-taskbar-export-plan.md` or the
     in-app window menu for the exact key bindings if unfamiliar).
   - Confirm: the window actually moves/resizes, and the focus indicator
     stays visible throughout — not just at the start.

3. **Modal dialog focus trap**
   - Open any modal file dialog (e.g. File Explorer's Open dialog, or Text
     Editor's Save As).
   - Tab repeatedly and confirm focus never leaves the dialog.
   - Press `Escape` and confirm the dialog closes and focus returns to the
     control that opened it (not to the top of the page).

4. **File Explorer tree keyboard navigation**
   - Open File Explorer, focus the file listing, and navigate with `Up`,
     `Down`, `Right`, and `Left`.
   - Confirm: selection changes are reachable by keyboard, and (with a screen
     reader running) each move is announced with the item's name.

5. **Full screen-reader pass**
   - With a screen reader running (NVDA on Windows, VoiceOver on macOS, or
     Chrome/Edge + a Windows screen reader — record which one you used),
     navigate the desktop, the App Launcher, the taskbar, window chrome
     (title, minimize/maximize/close), at least one modal dialog, and each
     first-party app (Terminal, File Explorer, Text Editor, Settings, Code
     Editor, Hack Paint).
   - Confirm: every control announces a meaningful name and role, and nothing
     is silently skipped or announced as "unlabeled button"/"unlabeled group".
     If something IS unlabeled, note the exact control and surface — that's a
     real finding to file, not a reason to skip the item.

## Known automated-coverage gaps (not part of this checklist, but related)

`docs/accessibility.md` section 2.2 also lists 200% zoom, long text, reduced
motion, and RTL as needing executable coverage — none of that exists yet
either, automated or manual. That's separate follow-up work, not blocking
this specific checklist.
