> [Version française](../../../../fr/wasm/dev/adr/0184-screens-administration-sdk.md)

# ADR 0184: system screens API for Settings (`HackScreens`)

## Status

Accepted on 2026-10-28, follow-up of work item `multi-monitor/7-screen-settings` of the
[multi-monitor plan](../plans/multi-monitor-plan.md). Benoit's choice (2026-10-28): a small SDK
API for system apps, so that Settings ▸ Screens drives the screens. Revisits the "later" answer to
question Q1 of the plan for this administration API only; the screens API for every app (screen of
my window, placing a window) stays for later. Implementation: [`screens-settings.md`](../screens-settings.md).

## Context

- [ADR 0179 §7](0179-per-screen-taskbars-and-other-screen-menus.md) puts in Settings ▸ Screens the
  list of open screens with the monitor name, the Identify, New screen, Restore screens and Full
  screen buttons, and the state of the `window-management` permission.
- Settings is a base app: it only goes through the public SDK. `ScreenService` (ADR 0180) is an OS
  service in `HackerOs.Platform.Blazor`, out of its reach. The first version of the page (PR #233)
  only reads and writes `screens.config`.
- The other administration pages share one model: a static `Hack*` facade in
  `HackerOs.AppSdk.Administration`, an `IApp*Gateway` on `IAppExecutionContext` (unsupported by
  default), a trusted gateway in Platform Core that checks a capability, and an OS service behind
  it (ADR 0056 §8 to 11, ADR 0159 §8).
- The browser imposes its gestures: `window.open`, the permission request (`getScreenDetails()`)
  and `requestFullscreen` must run during a click, and full screen must be asked by the document
  that receives the click.

## Decision

1. **Facade.** `HackScreens` in `HackerOs.AppSdk.Administration`, each member with an
   explicit-context overload:

   | Member | Role |
   | --- | --- |
   | `IsSupported`, `IsSupportedFor(context)` | The host opens screens (desktop browser). |
   | `GetState(context)` | `HackScreensState`: screens, permission, `CanOpenScreen`, screens to restore, the caller's screen. |
   | `WatchAsync(context, onChanged)` | Callback on each change (screen opened, closed, renamed, full screen, permission). |
   | `OpenScreenAsync` | Opens a screen; `HackScreenOpenStatus`: `Opened`, `Blocked`, `Unavailable`, `LimitReached`. |
   | `RestoreScreensAsync` | Reopens the screens of the last session; returns how many opened. |
   | `CloseScreenAsync(number)` | Closes a secondary screen; its windows come back to screen 1. |
   | `IdentifyAsync` | Shows the veil and the number of each screen (ADR 0179 §6) for 3 seconds. |
   | `ToggleFullScreenAsync` | Full screen of the screen where the caller's window is (the main tab for screen 1). |
   | `RequestPermissionAsync` | Asks for the `window-management` permission; returns the new state. |

   `HackScreenInfo(Number, MonitorName, Width, Height, IsFullScreen)`: screen 1 always comes
   first. `HackScreenPermission`: `Unknown`, `Unsupported`, `Prompt`, `Granted`, `Denied`.
2. **Capability.** A new **normal** capability `screens.manage` ("Manage screens") is required by
   every member but `IsSupported`. Normal, because everything goes through a user click and
   nothing leaves the machine: a browser window opens or closes, and the windows of a closed
   screen come back to screen 1. Settings declares it.
3. **Gateway.** `IAppScreensGateway` (SDK); `AppScreensGateway` (Platform Core) checks the
   capability and calls `IScreenControl` (Platform Core), which `ScreenControl` (Platform Blazor)
   implements over `ScreenService` and `WindowRuntime`. The caller's screen is the one of its
   focused window, else of its first window, else screen 1.
4. **Gestures.** Calls that open a window or go full screen must come from a click; the gateway
   does not defer them. Without a gesture, `OpenScreenAsync` returns `Blocked` and
   `ToggleFullScreenAsync` returns `false`.
5. **Identify.** `ScreenService.IdentifyAsync` lights the veil of every screen, the caller's
   window's screen included, with no highlighted square; it goes out after 3 seconds, when the
   pointer passes over it (as the menu's veil) or at a new call.
6. **Version.** The API goes into the SDK's `PublicAPI.Unshipped.txt`; the SDK version goes from
   2.1 to 2.2 at the next release (ADR 0173), decided by Benoit with the merge to `main`.

## Consequences

- Settings ▸ Screens shows the open screens (monitor name when Chrome or Edge gives it, size, full
  screen), the permission state with a button to ask for it, and the Identify, New screen, Restore
  screens, Close (per screen) and Full screen buttons. The file part of PR #233 stays (screen 1's
  taskbar, remembered places, Forget).
- A host without screens (mobile, tests, host without JavaScript) keeps the gateway unsupported:
  the page hides the buttons and keeps the file part.
- A third-party app that declares `screens.manage` can open and close screens during the user's
  clicks; it does not see the other apps' windows.

## Verification

- Unit tests: facade (missing capability, host without screens), trusted gateway, `ScreenControl`
  (state, caller's screen, identify then going out), Settings panel (buttons, states, French).
- UI.E2E: Settings ▸ Screens opens screen 2 with New screen, Identify shows the veil on screen 2,
  Close closes it.
- SDK compatibility: `PublicAPI.Unshipped.txt`, compatibility fixture unchanged.

## Tasks

- [ ] `HackScreens`, `IAppScreensGateway`, `screens.manage` capability.
- [ ] `AppScreensGateway`, `IScreenControl`, `ScreenControl`, identification by the service.
- [ ] Settings ▸ Screens buttons, tests, docs and index.
