> [Version française](../../../fr/wasm/dev/drag-between-screens.md)

# Drag a window from one screen to another

Work item `multi-monitor/8-drag-between-screens` of the [multi-monitor plan](plans/multi-monitor-plan.md),
implementation of [ADR 0183](adr/0183-drag-a-window-between-screens.md). It builds on the window
portal ([`window-portal.md`](window-portal.md)), the screen service
([`screen-service.md`](screen-service.md)) and the secondary screens' desktop
([`secondary-screen-desktop.md`](secondary-screen-desktop.md)). Without a secondary screen, nothing changes.

## 1. For the user

Grab a window's title bar, bring it over another screen, release: the window moves to that
screen, where the pointer is, with its content intact. During the gesture, the target screen shows
a green outline of the window. Released between two screens or on its own screen, the window moves
as usual.

## 2. Files

| File | Role |
| --- | --- |
| `Platform/HackerOs.Windowing.Blazor/WindowChrome.razor.js` | Learns where each screen window's page starts on the physical desktop, finds the screen under the pointer on every movement of a move, draws and removes the outline, and on release calls `ReportScreenDropAsync` instead of the normal move. All in this one module: tests load it alone from a `data:` URL, where a relative import fails. |
| `Platform/HackerOs.Windowing.Blazor/WindowChrome.razor` | `ReportScreenDropAsync(screen, x, y)`: `MoveWindowToDisplayCommand` then `MoveWindowCommand`. |
| `Platform/HackerOs.Windowing.Blazor/DesktopArea.razor.css` | Style of the `.window-drop-preview` outline. |
| `Platform/HackerOs.Platform.Blazor/Shell/DesktopShell.razor` | Ignores a drop on a screen already closed. |

## 3. Finding the screen under the pointer

```
pointermove (captured by the title bar, in the original browser window)
 ├─ pointer inside this window? ── yes ─► .window-layer[data-display] under (clientX, clientY)
 └─ no ─► for the main tab and each screen surface:
             page origin on the desktop = (screenX - clientX) of the last event seen
                                          + the window's move since
             point = (screenX, screenY) - origin ─► layer under that point
```

- Origins are learned by passive capture listeners for `pointermove`, `pointerdown` and
  `pointerover` on each window; the surface registry (`watch`) announces the screen windows.
  Before any event in a window, its origin is estimated from its position and borders.
- A hidden layer (zero size) is never a target.
- The grab point (offset between the pointer and the window corner when the gesture starts) is
  kept: the window lands at the same place under the pointer. Its top is kept at 0 or more so that
  the title bar stays visible.

## 4. Rules

- The window's element changes document only on release: moving it during the gesture would lose
  the pointer capture.
- The outline only appears on a screen that is another browser window; in the same page
  (harness), the window itself shows where it goes.
- `WindowRuntime` then keeps the position inside the target screen's work area, like any move. A
  modal window follows its owner.
- Only a normal, movable window can be dragged.

## 5. Tests

- `Tests/HackerOs.Platform.Blazor.Tests/WindowPointerGeometryTests.cs`: `ReportScreenDropAsync` sends the
  screen change then the position; invalid screen number, non-finite value or maximized window: no
  command.
- `Tests/HackerOs.E2E.Tests/IndexedDbBrowserContractTests.ScreenDrag.cs`, scenario
  `?scenario=screen-portal`: the window dragged onto screen 2 moves there without being recreated,
  at the drop point (within 2 px), stays live, does not change screen when released between the
  screens, and comes back to screen 1 the same way.
- `Tests/HackerOs.UI.E2E.Tests/ScreenServiceTests.cs`, real Chromium: screen 2 restored, the Text
  Editor dragged out of the main tab to a point of screen 2; screen 2 shows the outline during the
  gesture, then the window. The test computes the point from both pages' origins (in headless
  mode the windows overlap: the chosen point is inside screen 2 and outside the main tab).

## 6. Limits

- Browser zoom other than 100 %: the conversion between desktop and page is approximate.
- Screen windows overlapping on one monitor: the original window wins, then the main tab, then
  the surfaces in opening order.
- No dragging of a maximized or snapped window, and no snapping to an edge of the target screen.
