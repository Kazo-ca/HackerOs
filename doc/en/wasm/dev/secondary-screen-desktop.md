> [Version française](../../../fr/wasm/dev/secondary-screen-desktop.md)

# Desktop and overlays of the secondary screens

Work item `multi-monitor/4-secondary-desktop` of the [multi-monitor plan](plans/multi-monitor-plan.md),
implementing [ADR 0178 §1](adr/0178-extended-screens-in-display-surfaces.md) (each screen's
desktop) and [ADR 0179 §2](adr/0179-per-screen-taskbars-and-other-screen-menus.md) (overlays on
the screen where the user acts, secure dialogs on screen 1). It builds on the window portal
([`window-portal.md`](window-portal.md)) and the screen service
([`screen-service.md`](screen-service.md)). Without a secondary screen, nothing changes.

## 1. A secondary screen's desktop

`DesktopShell` gives `ScreenLayer.ScreenContent` one `DesktopArea` per screen:

| Parameter | Value |
| --- | --- |
| `Windows` | Every window of `WindowRuntime`. |
| `Layers` | The same `WindowLayers` as screen 1's area: the screen's windows arrive through the portal. |
| `DisplayId` | The screen's number. |
| `OnGesture` | `DesktopShell`'s handler: the area sends the screen's size (`ChangeViewportCommand(area, screen)`). |

- **Wallpaper only** (Benoit's answer of 2026-10-28): no `BackgroundContent`, so no icons. The
  wallpaper comes from the theme variables (`--hos-desktop-background`, grid), which the surface
  inherits from the main tab.
- Windows stay rendered by screen 1's area, with its callbacks (focus, close, window menu): a
  secondary screen's area renders none.
- The screen's number on the desktop colour (work item 3) only shows when `ScreenContent` is
  missing.

## 2. Work area

A secondary screen's work area is the size of **its desktop**, no longer of its window: the
screen's taskbar (work item 5) will stay outside it.

- `ScreenService` only sends `AddDisplayCommand` with the window's size, on open. It no longer
  changes the area when the window is resized.
- `DesktopArea.razor.js` measures the area with a `ResizeObserver` **of the window that holds
  it**: an observer only follows the layout of its own window, and the area moves into the
  screen's window right after its first render. The module makes its observer again when the area
  changes document (checked every 200 ms for 10 s after the first render).
- A size sent by the area of a screen already removed is ignored by `DesktopShell`.

## 3. Overlays on the screen where the user acts

The window menu, the window switcher and the keyboard move banner (`WindowManagerOverlays`) and
the OS context menu (`ContextMenuOverlay`) render once, inside `ScreenOverlayHost`:

```
DesktopShell
 └─ ScreenOverlayHost (.screen-overlays, display: contents)
     ├─ WindowManagerOverlays
     └─ ContextMenuOverlay
ScreenLayer ─ per screen ─► .secondary-screen
                             ├─ DesktopArea (DisplayId = screen)
                             └─ [data-screen-overlay-slot] ◄── screenOverlays.js moves .screen-overlays here
```

| File | Role |
| --- | --- |
| `Platform/HackerOs.Platform.Blazor/Display/ScreenOverlayHost.razor` | The overlays' container; connects `screenOverlays.js` on first render. |
| `Platform/HackerOs.Platform.Blazor/wwwroot/screenOverlays.js` | Listens to `pointerdown`, `contextmenu` and `keydown` in the capture phase on each screen's window, and moves the container into that screen's slot **before** the overlays react. |
| `Platform/HackerOs.Platform.Blazor/wwwroot/HackerOs.Platform.Blazor.lib.module.js` | The surface registry has `watch(…)`: a module learns when each surface opens (`attach`) and closes (`detach`). |

Rules:

- **Screen 1**: the slot is the container's original place, in `DesktopShell`.
- **Other surfaces** (a page detached by the OS browser, ADR 0078): no slot, the overlays do not
  move, and the page keeps the browser's menu.
- **Screen closed** while it holds the overlays (`pagehide`, or surface removed): the container
  comes back to screen 1, even when it already came back into the tab with the screen's content.
- **Keyboard shortcuts**: `WindowManagerOverlays.razor.js` listens to `keydown` and `keyup` on
  `document` (no longer `window`). The surface registry copies these listeners onto each screen:
  `Alt+``, `Alt+Space`, etc. work in a secondary screen's window.
- **Placement**: the window menu and the context menu place themselves in the window of the
  screen that shows them (`ownerDocument.defaultView`), and the context menu reads that
  document's focus.
- The OS context menu also opens in a secondary screen (a document that has a
  `[data-screen-overlay-slot]`).

## 4. Secure dialogs

Secure dialogs (dangerous capability approval, ADR 0049; administrator authentication) stay on
screen 1. While one of them waits (`DangerousCapabilityApprovalBroker.Current` or
`AdministratorAuthenticationBroker.Current`), every secondary screen shows a veil: "A security
request is waiting on screen 1…" and a **Go to screen 1** button, which asks the browser to bring
the main tab forward (the browser may refuse; the message then says where to go). The veil
covers the whole screen and takes the clicks.

## 5. Tests

- `Tests/HackerOs.Platform.Blazor.Tests/Display/ScreenLayerTests.cs`: screen 2's desktop and slot,
  the veil for a pending approval and for a pending authentication.
- `Tests/HackerOs.Platform.Blazor.Tests/Display/ScreenServiceTests.cs`: a resized window no longer
  changes the screen's work area.
- `Tests/HackerOs.UI.E2E.Tests/ScreenServiceTests.cs`, real Chromium: restored screen 2 shows its
  desktop (no icons, the window's size); `Alt+`` pressed in screen 2 opens the switcher in
  screen 2 and not in the main tab; back on screen 1 it opens in the tab; the closed screen gives
  the overlays back to screen 1.

## 6. Limits

- Full screen through the shortcut (`ToggleFullScreen`) still applies to the main tab; a
  secondary screen has its `⤢` button.
- Menu tokens of elements removed inside a secondary screen are released only when the session
  ends (the removal observer follows the main document).
- A window cannot change screen from the interface yet: menus and shortcuts in work item 6,
  dragging in work item 8.
