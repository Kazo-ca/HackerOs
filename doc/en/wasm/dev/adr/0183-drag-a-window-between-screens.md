> [Version française](../../../../fr/wasm/dev/adr/0183-drag-a-window-between-screens.md)

# ADR 0183: drag a window from one screen to another

## Status

Accepted on 2026-10-28, work item `multi-monitor/8-drag-between-screens` of the
[multi-monitor plan](../plans/multi-monitor-plan.md). Completes
[ADR 0178 §7](0178-extended-screens-in-display-surfaces.md), which left mouse dragging to a
separate work item (Benoit's decision: menu and shortcut first). Builds on
[ADR 0163](0163-window-pointer-preview-and-invariant-coordinates.md) (move preview) and on the
window portal ([`window-portal.md`](../window-portal.md)).
Implementation: [`drag-between-screens.md`](../drag-between-screens.md).

## Context

- A window moves by its title bar. `WindowChrome.razor.js` captures the pointer
  (`setPointerCapture`), draws a preview with a CSS translation and sends a single
  `MoveWindowCommand` on release (ADR 0163). The position is kept inside the work area of the
  window's screen.
- Each secondary screen is another browser window (a surface). While the pointer is captured,
  the browser sends every pointer event to the window where the gesture started, even when the
  pointer is over another window: the target window receives nothing.
- Each pointer event gives two positions: in the page (`clientX`/`clientY`) and on the physical
  desktop (`screenX`/`screenY`). No API gives directly where another window's page sits on the
  desktop.
- Screens are already marked in the DOM: the window layer of each `DesktopArea` carries
  `data-display` (work item 2), and the surface registry (`watch`) announces each screen
  document (work item 4).

## Decision

1. **Screen under the pointer.** During a move, `windowScreenDrop.js` looks for the screen under
   the pointer on every movement:
   - if the pointer is inside the browser window where the gesture started, that window's layers
     decide (position in the page);
   - otherwise, the other screen windows (main tab and surfaces), from the position on the
     physical desktop. Each window's page knows where it starts on the desktop from the last
     pointer event it saw (`screenX - clientX`), corrected if the window moved since; before any
     event, from its position and borders (`screenX`, `outerWidth - innerWidth`).
   - Between two screens (no layer under the pointer), there is no target.
2. **Screen changed on release only.** The window's element does not change document during the
   gesture: moving it would lose the pointer capture. During the gesture, the window follows the
   pointer on its screen (ADR 0163 preview), and the target screen shows an **outline** the size
   of the window where it will land (only when the target screen is another browser window).
3. **Drop.** Released over another screen, the window lands at the pointer position minus the
   point where the title bar was grabbed, its top kept at 0 or more.
   `WindowChrome.ReportScreenDropAsync(screen, x, y)` (public, `PublicAPI.Unshipped.txt`) sends
   `MoveWindowToDisplayCommand` then `MoveWindowCommand`: the portal moves the window without
   recreating it, and the position is kept inside the target screen's work area. A modal window
   still follows its owner (ADR 0178 §2).
4. **Without a target**, or over its own screen, the move stays as it is today. `DesktopShell`
   ignores a drop on a screen closed during the gesture.
5. **Scope.** Only a normal, movable window can be dragged (as today). Dragging a maximized or
   snapped window, and snapping to an edge of the target screen, are not part of this work item.
   No change on mobile.

## Consequences

- No change without a secondary screen: the module finds a single layer, the window's own.
- Browser zoom skews the conversion (`screenX` and `clientX` no longer share a scale); at 100 %
  the position is exact.
- Two screen windows overlapping on the same monitor: the window where the gesture started wins,
  then the main tab, then the surfaces in opening order.
- Tests: the harness `screen-portal` scenario (two screens in one page) and a real Chromium test
  where the window leaves the main tab for screen 2.
