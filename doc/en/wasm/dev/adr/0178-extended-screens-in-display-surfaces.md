> [Version française](../../../../fr/wasm/dev/adr/0178-extended-screens-in-display-surfaces.md)

# ADR 0178: extended screens in display surfaces

## Status

Accepted on 2026-10-28 at Benoit's request ("we can start the multi-monitor
feature"). Takes over the 2026-10-28 assessment and Benoit's answers (target
browsers, moving windows between screens). Builds on
[ADR 0078](0078-detached-display-windows.md) and
[ADR 0081](0081-detached-display-windows-adjustments.md) (surfaces and portal),
and on [ADR 0095](0095-window-events-sizes-and-keyboard.md) (window keyboard
commands). The "Fix the buttons of maximized windows" thread writes ADR 0177
(memory of the size before snapping): this series brings it in as soon as it is
merged. The interface (taskbars, menus, veil, settings) is in
[ADR 0179](0179-per-screen-taskbars-and-other-screen-menus.md). Work plan:
[`multi-monitor-plan.md`](../plans/multi-monitor-plan.md).

## Context

- A display surface (`IDisplaySurfaceService`) is a `window.open` window of the
  same instance: no second .NET instance. The `DisplaySurfaceContent` portal
  renders any Blazor tree there and keeps its state, cascading parameters and
  app context. [`detached-display-windows.md`](../detached-display-windows.md)
  already announced "a desktop drawn in a surface".
- `WindowRuntime` knows a single screen: one `WorkArea`, set by
  `ChangeViewportCommand`, which `DesktopArea` sends when its size changes.
  Every bound, maximizing and snapping refer to it.
- `DesktopShell` renders one `DesktopArea`, one taskbar and one overlay layer
  (switcher, window menu, context menu, secure dialogs), in the main tab.
- The browser sets the limits. `getScreenDetails()` (list monitors, place a
  window on another one, full screen per monitor, `screenschange` event) only
  exists in desktop Chrome and Edge, with the `window-management` permission. A
  window only opens after a user gesture. An iframe moved from one document to
  another reloads (ADR 0081 §4). A hidden main tab slows down the whole OS.

## Decision

1. **One screen = one surface.** Screen 1 is the main tab. Every additional
   screen is a surface opened by the screen service, which renders a
   `DesktopArea` in it through the portal. There is only one mode: the
   **extended desktop**. Mirroring is impossible (a DOM element lives in one
   place only) and is not offered.
2. **Screens in the window manager** (`HackerOs.Windowing.Abstractions`, public
   API, `PublicAPI.Unshipped.txt`):
   - `DisplayId` (value; `DisplayId.Primary` is screen 1) and
     `WindowRuntimeState.DisplayId` (`Primary` by default).
   - One work area per screen. `ChangeViewportCommand` takes an optional
     `DisplayId` (`Primary` by default: current code does not change). New
     commands `AddDisplayCommand(DisplayId, WindowBounds)`,
     `RemoveDisplayCommand(DisplayId)` and
     `MoveWindowToDisplayCommand(WindowId, DisplayId)`; new events
     `DisplayAddedEvent`, `DisplayRemovedEvent` and `WindowDisplayChangedEvent`.
   - Bounds, maximizing and snapping are computed in the work area of the
     window's screen. There is one focus and one Z order for the whole OS.
   - **Changing screens keeps the visual state.** A maximized window stays
     maximized. A snapped window stays snapped on the same side, with the size
     before snapping from ADR 0177. A normal window keeps its relative position,
     brought back inside the target screen's work area. A minimized window
     changes screens and stays minimized.
   - An owner-modal window follows its owner: moving one moves the other.
   - **Screen removed** (surface closed by the user, lost, or monitor
     unplugged): its windows come back to screen 1 and keep their state, the way
     a surface's content returns under its anchor.
3. **Windows rendered once, moved through a portal.** Each `WindowHost` is
   rendered only once, under a single root, then its element is moved into the
   window layer of its screen's `DesktopArea`. Changing screens recreates
   neither the component nor the app. An iframe in a moved window reloads: the
   OS browser already rewrites its page; other iframe apps lose the iframe's JS
   state, and the docs say so.
4. **Screen service** (`HackerOs.Platform.Blazor`, internal to the OS):
   - In Chrome and Edge, it asks for the `window-management` permission on
     first use (during a click), lists monitors with `getScreenDetails()` and
     opens a new screen's surface on a free monitor (the monitor's
     `left`/`top`). It follows `screenschange`: a removed monitor closes its
     screen (point 2).
   - **Fallback** in other browsers, or without the permission: the surface
     opens like a detached window, the user places it, and its position and size
     are remembered for the next time.
   - **Full screen** for a secondary screen: a button in the screen (one gesture
     per screen). It is never forced.
   - A surface only opens during a gesture. "New screen" (ADR 0179 menus,
     Settings Screens page) opens the surface **during the click**, then moves
     the window into it.
   - After the OS reloads, the surfaces are closed. The OS offers "Restore
     screens": one click reopens those of the previous session, in the same
     order.
   - A secondary screen shows a banner when the main tab is hidden, because the
     browser then slows down the whole OS.
5. **Screen numbers**: screen 1 is the main tab; a new screen takes the lowest
   free number. The number does not change while the screen exists.
6. **Keyboard**: new actions `MoveToPreviousScreen` and `MoveToNextScreen` in
   `keybindings.config` (ADR 0095). Linux set: `Super+Shift+Left` /
   `Super+Shift+Right`. Fallback set: `Ctrl+Alt+Shift+Left` /
   `Ctrl+Alt+Shift+Right`. Menus are described by ADR 0179.
7. **Out of scope**: the mobile interface (`MobileShell`) has a single screen.
   Dragging a window from one screen to another with the mouse comes in a
   separate work item (Benoit's decision: menu and shortcut first). Plan §6 says
   whether an SDK API for screens is part of the series.

## Consequences

- Without a secondary screen, rendering and behavior do not change: one
  `DisplayId.Primary`, and existing commands keep their shape.
- The public types added to `HackerOs.Windowing.Abstractions` go into
  `PublicAPI.Unshipped.txt` and the next minor version.
- The window portal touches all window rendering: it has its own work item and
  browser tests (no component recreated, focus and context menu kept).
- Overlays (window menu, context menu, switcher, dialogs) must appear on the
  screen where the user acts: each screen has its overlay layer (ADR 0179).

## Verification

- `WindowRuntime` unit tests: work areas per screen, changing screens in each
  state, modal following its owner, screen removed.
- Browser tests (`HackerOs.BrowserHarness.Tests`): window moved between two
  `DesktopArea`s without being recreated.
- UI.E2E: second screen opened by "New screen", window moved, screen closed and
  windows back on screen 1; fallback without `getScreenDetails`.

## Tasks

- [ ] `DisplayId`, work areas per screen and commands in `WindowRuntime` (work item 1).
- [ ] Windows rendered once and moved through a portal (work item 2).
- [ ] Screen service, permission, fallback, restore (work item 3).
- [ ] Desktop and overlays in each screen (work item 4).
- [ ] Keyboard shortcuts (work item 6).
