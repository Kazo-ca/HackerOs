> [Version française](../../../fr/wasm/dev/window-portal.md)

# Window portal between screens

Work item `multi-monitor/2-window-portal` of the
[multi-monitor plan](plans/multi-monitor-plan.md), implementing
[ADR 0178 §3](adr/0178-extended-screens-in-display-surfaces.md). It builds on the screens
of the window manager ([`window-runtime-displays.md`](window-runtime-displays.md)). Without a
secondary screen, nothing changes on screen: there is only one `DesktopArea`, screen 1's.

## 1. Principle

Each window is rendered **only once**, in screen 1's `DesktopArea`. When it is on another
screen, its element is moved into the window layer (`.window-layer`) of that screen's
`DesktopArea`. Changing screen moves the element; the `WindowHost` component, the app and
their state are never recreated. The renderer keeps updating the window where it is, as for
the content of a display surface ([`detached-display-windows.md`](detached-display-windows.md)).

## 2. Parts

| Part | Role |
| --- | --- |
| `WindowLayers` (`HackerOs.Windowing.Blazor`, public) | Window layers of each screen. Each `DesktopArea` given the same instance registers its layer after its first render and removes it when it goes away. `TryGetLayer(screen)`, `Displays`, `Changed` event. |
| `DesktopArea.DisplayId` | Screen the area shows (screen 1 by default). Its size changes go to that screen's work area (`ChangeViewportCommand(area, screen)`). The screen must be added to the manager (`AddDisplayCommand`) before its area renders. |
| `DesktopArea.Layers` | The shared layers. Screen 1's area then renders every window; the other areas render none and receive theirs through the portal. Without `Layers`, an area renders only the windows of its own screen (no portal: changing screen recreates the window). |
| `WindowPortal` (public, used by `DesktopArea`) | Wraps each `WindowHost`. It leaves an empty anchor in the window list and moves the window's element into the target layer, or back under the anchor. |
| `WindowPortal.js` | Keeps each element by a key, not by `ElementReference`: Blazor finds a referenced element with a query on the document, which no longer finds an element left in a layer that was just removed. |

`DesktopShell` creates a `WindowLayers` and passes it to its `DesktopArea` and to the area of
each secondary screen, rendered in its surface (work item 4, [`secondary-screen-desktop.md`](secondary-screen-desktop.md)).

## 3. Rules

- **The anchor never moves.** The window list is a keyed list (`@key`); Blazor inserts a new
  element next to its actual siblings. The siblings are the anchors, which stay in screen 1's
  layer; only the inner element leaves.
- **Neutral boxes.** The anchor and the wrapper are `display: contents`: the window is placed
  in the layer as before (position, Z order, ADR 0160 `pass-through`).
- **Screen without a layer.** A window whose screen has no layer (screen not shown yet, or
  area removed) stays in screen 1's layer. When a screen's area goes away with windows in it,
  they come back under their anchor without being recreated; they go back if the area returns.
  The manager itself brings them back to screen 1 with `RemoveDisplayCommand` (work item 3).
- **Focus.** If the keyboard focus was inside the window, it is given back to the same control
  after the move. `WindowHost.razor.js` reads the focus in the window's document
  (`ownerDocument`), for screens in a surface.
- **Iframes.** A moved iframe reloads (ADR 0078 §4, ADR 0178 §3): the OS browser rewrites its
  page, other iframe apps lose the iframe's JS state.

## 4. Tests

- `Tests/HackerOs.E2E.Tests/IndexedDbBrowserContractTests.ScreenPortal.cs`, scenario
  `?scenario=screen-portal` of `HackerOs.BrowserHarness.Tests` (two screens side by side in one
  page): the window moves into screen 2's layer without being recreated (same instance number),
  keeps its counter, the typed text and the focus, still receives its clicks, comes back to
  screen 1, then comes back under its anchor when screen 2's area goes away and returns there
  when the area comes back.
