> [Version française](../../../fr/wasm/dev/screen-service.md)

# Screen service

Chantier `multi-monitor/3-screen-service` of the [multi-monitor plan](plans/multi-monitor-plan.md),
implementing [ADR 0178 §4](adr/0178-extended-screens-in-display-surfaces.md), refined
by [ADR 0180](adr/0180-screen-service-placement-and-memory.md). The service opens
each secondary screen in a window of the host browser, registers it in the window
manager ([`window-runtime-displays.md`](window-runtime-displays.md)) and remembers it
from one session to the next. The "New screen" menus (work item 6) use it; the
Settings ▸ Screens page (work item 7, [`screens-settings.md`](screens-settings.md)) only reads and
writes `screens.config`. This work item shows the "Restore screens" offer.

## 1. Architecture

```
Main tab (screen 1)                                 Window of a secondary screen (window.open)
───────────────────                                 ──────────────────────────────────────────
DesktopShell                                         <body class="hackeros-surface">
 └─ ScreenLayer                                       └─ .secondary-screen[data-display=2]
     ├─ one <DisplaySurfaceContent> per screen ─ moves ►  ├─ the screen's desktop (chantier 4; its number meanwhile)
     └─ "Restore screens" card                            ├─ "main tab hidden" banner
                                                          └─ full-screen button ⤢
ScreenService ── AddDisplay / ChangeViewport / RemoveDisplay ──► WindowRuntime
 ├─ DisplaySurfaceService.OpenScreenSurfaceAsync ──► displaySurfaces.js open(…, placement)
 │                                                     └─ screens.js prepare / settle
 └─ screens.js: permission, getScreenDetails, screenschange, place read every second,
                full screen, the main tab's visibilitychange
~/.config/hackeros/screens.config [screens]: open = 2,3 ; 2 = left,top,width,height
```

| File | Role |
| --- | --- |
| `Platform/HackerOs.Platform.Blazor/Display/ScreenService.cs` | The service: numbers, opening, restoring, closing, full screen, memory, commands sent to `WindowRuntime`. |
| `Platform/HackerOs.Platform.Blazor/Display/ScreenLayer.razor(.css)` | Renders the screens through the surface portal, banner, full-screen button, restore card. |
| `Platform/HackerOs.Platform.Blazor/Display/DisplaySurfaceService.cs` | `OpenScreenSurfaceAsync` (internal): opening with a placement, optional blocked notification. |
| `Platform/HackerOs.Platform.Blazor/wwwroot/screens.js` | Window Management API, choice of the free monitor, move after the permission, place, full screen, hidden tab. |
| `Platform/HackerOs.Platform.Blazor/wwwroot/displaySurfaces.js` | `open` takes an optional placement and calls `screens.js` before and after `window.open`. |
| `Platform/HackerOs.Platform.Core/Shell/ScreensSettings.cs` | `ScreenGeometry` and `ScreensSettingsDocuments`: the `[screens]` section of `screens.config`. |
| `OS/HackerOs.Ecosystem.Host/EcosystemServiceCollectionExtensions.cs` | `DisplaySurfaceService` (also as `IDisplaySurfaceService`) and `ScreenService`, per session. |

## 2. Service API

| Member | Role |
| --- | --- |
| `StartAsync()` | Reads `screens.config` (screens to restore, places) and follows the permission and the main tab. Called by `ScreenLayer` on first render. |
| `OpenScreenAsync()` | Opens a screen with the smallest free number, during a gesture. Drops the restore offer. |
| `RestoreScreensAsync()` | Reopens the previous session's screens, in order, during a gesture; those the browser blocks stay offered. |
| `DismissRestoreAsync()` | Drops the offer and writes it. |
| `CloseScreenAsync(screen)` | Closes a screen; it is no longer restored. |
| `ToggleFullScreenAsync(screen)` | Full screen for the screen, from a click made in that screen. |
| `Screens`, `RestorableScreens`, `Permission`, `IsPrimaryHidden`, `CanOpenScreen`, `Changed` | State read by the UI. |
| `PrimaryTaskbar`, `SetPrimaryTaskbarAsync(mode)` | `[taskbar]`'s `primary-taskbar` setting, followed during the session (work item 5, [`per-screen-taskbars.md`](per-screen-taskbars.md)). |

`ScreenOpenStatus`: `Opened`, `Blocked` (window refused), `Unavailable` (host
without JavaScript), `LimitReached` (numbers 2 to 9 taken). `ScreenPermission`:
`Unknown`, `Unsupported`, `Prompt`, `Granted`, `Denied`.

## 3. Opening a screen

1. The number is the smallest free one (`WindowRuntime.NextDisplayId`), or the
   restored screen's own number when it is free.
2. `screens.js` picks the place before `window.open` (ADR 0180 §2): a free monitor
   when the permission is granted; otherwise the remembered place (or 1280 × 800),
   and the permission request starts in the same gesture. If the user accepts, the
   window is moved to a free monitor and `OnScreenPlaced` gives the monitor's name.
3. Once the surface is open, the service sends `AddDisplayCommand(screen, 0, 0,
   width, height)`, then `ChangeViewportCommand` on each size change.
4. `open` in `screens.config` is rewritten.

## 4. Closing a screen

| Cause | Windows | `open` |
| --- | --- | --- |
| The user closes the window, or it goes elsewhere (`Lost`) | Come back to screen 1 (`RemoveDisplayCommand`) | The screen leaves it |
| Monitor unplugged (`screenschange`) | Same | The screen leaves it |
| `CloseScreenAsync` | Same | The screen leaves it |
| Reload, tab closed, logout | Same (the session ends) | Unchanged: offered to the next session |

The screen's place stays in its numbered line.

## 5. Inside a screen's window

- **Desktop**: `ScreenLayer.ScreenContent` receives the screen's desktop (work item 4,
  [`secondary-screen-desktop.md`](secondary-screen-desktop.md)); without it, the screen's number
  on the desktop colour.
- **Full screen**: `⤢` button at the top right, `⤡` to leave (or `Esc`).
- **Banner**: "The main HackerOS tab is hidden…" while the main tab is hidden
  (`visibilitychange`).
- **Work area**: the window's inner size on open, then the size of the screen's desktop,
  which measures it itself (work item 4); the service no longer changes it when the window
  is resized.

## 6. Tests

- `Tests/HackerOs.Platform.Blazor.Tests/Display/ScreenServiceTests.cs`: scripted JS
  modules, real `WindowRuntime`, in-memory settings.
- `Tests/HackerOs.Platform.Core.Tests/Shell/ScreensSettingsTests.cs`: reading and
  writing `[screens]` on the virtual disk.
- `Tests/HackerOs.UI.E2E.Tests/ScreenServiceTests.cs`: real Chromium; the file is
  written from the terminal, the OS is reloaded, **Restore screens** opens screen 2
  at 900 × 700, then the screen closed by hand is no longer offered.
- `Tests/HackerOs.UI.E2E.Tests/BrowserDetachedWindowTests.cs` stays green: without a
  placement, `displaySurfaces.js` opens as before.

## 7. Limits

- Automatic placement, unplugged monitors and monitor names: Chrome and Edge only.
  Without the API the user places the window, and the OS remembers its place.
- The place is read every second: a move right before a reload may not be kept.
- A browser may open only one window per click: restoring three screens may take
  three clicks.
- No secondary screen on mobile (`MobileShell` does not render `ScreenLayer`).
