> [Version française](../../../../fr/wasm/dev/adr/0180-screen-service-placement-and-memory.md)

# ADR 0180: screen service, placement and memory of the screens

## Status

Accepted on 2026-10-28, chantier `multi-monitor/3-screen-service` of the
[multi-monitor plan](../plans/multi-monitor-plan.md). Refines
[ADR 0178 §4](0178-extended-screens-in-display-surfaces.md) (screen service) and
the `[screens]` format of [ADR 0179 §7](0179-per-screen-taskbars-and-other-screen-menus.md).
Implementation: [`screen-service.md`](../screen-service.md).

## Context

- ADR 0178 §4 sets the goal: one surface per screen, on a free monitor under
  Chrome and Edge with the `window-management` permission, a fallback elsewhere
  with the position remembered, full screen by a button, "Restore screens" after a
  reload, a banner while the main tab is hidden.
- The browser sets three constraints. A window opens only during a gesture, and
  `window.open` uses that gesture up. The permission request
  (`getScreenDetails()`) also needs the gesture, and its answer comes whenever the
  user answers. No event says that a window was moved.
- The ADR 0179 §7 sample (`2=1920,0,1920,1080`) mixes two things: the screens to
  reopen and the place of each screen. A screen the user closed must not be
  offered again, but its place must stay for the next time it opens.

## Decision

1. **Service.** `ScreenService` (`HackerOs.Platform.Blazor.Display`) is an OS
   service, one instance per session like `WindowRuntime`. Its types are public
   for the OS assemblies (`PublicAPI.Unshipped.txt`), but no app SDK API exposes
   them (Benoit's answer of 2026-10-28: later). It opens the surface through
   `DisplaySurfaceService` (an internal method with a placement), then sends
   `AddDisplayCommand`, `ChangeViewportCommand` and `RemoveDisplayCommand` to
   `WindowRuntime`.
2. **Monitor chosen before `window.open`.** The `screens.js` module decides where
   the window goes, in the same gesture:
   - permission granted: the first free monitor from left to right (neither the
     main tab's nor another screen's), its whole available area;
   - permission still to ask: the window opens at its remembered place (or at
     1280 × 800) and the request starts in the same gesture; if the user accepts,
     the window is moved to a free monitor (`moveTo`, `resizeTo`);
   - permission denied or no API (Firefox, Safari): fallback, the window opens at
     its remembered place and the user places it.
3. **Remembered place.** A screen's place is read every second (`screenX`,
   `screenY`, `innerWidth`, `innerHeight`: the values `window.open` takes back)
   and written one second after the last change. It is not read in full screen.
4. **`screens.config`, `[screens]` section.** An `open` key lists the open screens,
   in order; each numbered line keeps its screen's place, even after it closed:

   ```ini
   [screens]
   open = 2,3
   2 = 1920,0,1920,1080
   3 = -1280,0,1280,1024
   ```

   Numbers go from 2 to 9: at most nine screens. The service writes the user layer
   (`~/.config/hackeros/screens.config`). Chantier 7 adds `[taskbar]` and the
   Settings ▸ Screens page.
5. **What is restored.** A screen closed by the user, lost, removed by
   `CloseScreenAsync` or whose monitor was unplugged leaves `open`. A reload,
   closing the tab or logging out keep `open` as it is: the next session offers to
   reopen them.
6. **"Restore screens".** After sign-in, when `open` lists screens, a card above
   screen 1's taskbar offers **Restore screens** and **Dismiss**. Restore reopens
   the screens in order, with their number and place. A browser may accept only one
   window per click: the blocked screens stay offered for the next click, and the
   "window blocked" notification is posted only when no screen opened. Dismissing,
   or opening a screen by hand, drops the offer.
7. **Full screen.** A `⤢` button at the top right of each secondary screen calls
   `requestFullscreen` on that screen's document, during the click made in that
   screen. It is never forced; `Esc` leaves it.
8. **Main tab hidden.** `visibilitychange` on the main tab shows a banner at the
   top of every secondary screen while it is hidden.
9. **Unplugged monitor.** On `screenschange`, a screen whose monitor disappeared is
   closed by the service: its windows come back to screen 1 (ADR 0178 §2).
10. **Work area.** Until chantier 4, a screen's work area is its window's inner
    size. Chantier 4 hands it to the screen's `DesktopArea`, which takes its
    taskbar off.

## Consequences

- Without a secondary screen nothing changes: the service reads `screens.config`,
  does not write it and shows nothing.
- The "New screen" menus (chantier 6) and the Settings ▸ Screens page (chantier 7)
  call `OpenScreenAsync`, `RestoreScreensAsync`, `CloseScreenAsync` and
  `ToggleFullScreenAsync` during a click.
- The SDK's surface contract (`IDisplaySurfaceService`) does not change; the
  placement goes through an internal method of `DisplaySurfaceService`.

## Verification

- `Tests/HackerOs.Platform.Blazor.Tests/Display/ScreenServiceTests.cs`: screen 2
  registered with its size, area followed on resize, screen closed by the user, by
  the OS, unplugged monitor, restore in order at the remembered place, blocked
  window, dismissed offer, place written after a pause, states (hidden tab, full
  screen, permission), host without JavaScript.
- `Tests/HackerOs.Platform.Core.Tests/Shell/ScreensSettingsTests.cs`: reading and
  writing `[screens]`.
- `Tests/HackerOs.UI.E2E.Tests/ScreenServiceTests.cs` (real Chromium, fallback):
  offer after a reload, screen 2 opened at its remembered size, a screen closed by
  hand not offered afterwards.

## Tasks

- [x] `ScreenService`, `screens.js`, placement in `displaySurfaces.js`.
- [x] `[screens]` of `screens.config` (`ScreensSettingsDocuments`).
- [x] "Restore screens" card, full-screen button, hidden-tab banner.
- [x] Unit and UI.E2E tests, docs and indexes.
