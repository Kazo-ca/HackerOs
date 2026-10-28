> [Version française](../../../fr/wasm/dev/screens-settings.md)

# Settings ▸ Screens

Work item `multi-monitor/7-screen-settings` of the [multi-monitor plan](plans/multi-monitor-plan.md),
implementing [ADR 0179 §7](adr/0179-per-screen-taskbars-and-other-screen-menus.md), then
[ADR 0184](adr/0184-screens-administration-sdk.md) (work item `multi-monitor/7b-screens-sdk`). The
page reads and writes `screens.config` and drives the open screens through `HackScreens`; it builds on the screen service
([`screen-service.md`](screen-service.md)) and on the per-screen taskbars
([`per-screen-taskbars.md`](per-screen-taskbars.md)), which follow the file.

## 1. What the page shows

| Block | Content | Writes |
| --- | --- | --- |
| Open screens (ADR 0184) | Each open screen: number, monitor name (with the permission), size, "Full screen", "This window" for the screen of the Settings window, a **Close** button on screens 2 to 9. Buttons **Identify**, **New screen**, **Restore screens (N)** when some are left to reopen, **Full screen** / **Leave full screen**. State of the `window-management` permission, with **Allow now** while it was not asked yet. Hidden on a host without screens (mobile, tests). | — (calls `HackScreens`) |
| Taskbar of screen 1 | Choice of `all` (every window of every screen) or `own` (those of screen 1), with the value's origin (default, system, user) and the administrator's lock. | `[taskbar] primary-taskbar` of the user layer |
| Remembered screens | Screen 1 (this browser tab), then each screen 2 to 9 listed in `open` or with a valid place: state "open, or offered at the next sign-in" or "closed", place `width × height at (left, top)`. A **Forget** button on a closed screen that has a place. | Removes the numbered line from the user layer |
| Open and move screens | The other commands: Other screen ▸ New screen (right-click on the icon or on Maximize), `Super+Shift+←/→` shortcuts, the **Restore screens** card, the `⤢` button. | — |

The file is `~/.config/hackeros/screens.config`; `/etc/hackeros/screens.config` gives the system
defaults (an administrator writes it, for instance with `sudo nano`).

## 2. The screen buttons: `HackScreens`

Settings is a base app: it only goes through the public SDK. The file goes through
`HackSettings.OpenSystemAsync(AppContext, "screens")` (a document listed in
`OsSettingsDocumentRegistry`); the open screens go through the system facade `HackScreens`
(ADR 0184, namespace `HackerOs.AppSdk.Administration`), which requires the normal capability
`screens.manage` that Settings declares.

| Call | Effect |
| --- | --- |
| `IsSupportedFor(ctx)` | True only while the screen service runs (browser desktop). Otherwise the page hides the block. |
| `GetState(ctx)`, `WatchAsync` | The screens, the permission, the screens left to restore, the screen of the calling window; the page updates on each change. |
| `OpenScreenAsync`, `RestoreScreensAsync` | Open a browser window during the click. `Blocked`: the page says to allow pop-ups. |
| `CloseScreenAsync(n)` | Closes screen `n`; its windows come back to screen 1. |
| `IdentifyAsync` | An `is-identify` veil with the number on every screen, this one included, for 3 s. |
| `ToggleFullScreenAsync` | Full screen of the calling window's screen (the main tab for screen 1). |
| `RequestPermissionAsync` | Asks for the `window-management` permission during the click. |

The browser accepts a new window, full screen or its permission prompt only during a click: each
button calls `HackScreens` at once, with no wait before.

## 3. Who writes what

| Line | Written by | Followed by |
| --- | --- | --- |
| `[taskbar] primary-taskbar` | Settings ▸ Screens, `ScreenService.SetPrimaryTaskbarAsync` | `ScreenService` (document watch): the taskbars change at once |
| `[screens] open` | `ScreenService` only | Settings ▸ Screens (display) |
| `[screens] 2` to `9` | `ScreenService` (place), Settings ▸ Screens (Forget) | `ScreenService` the next time the screen opens |

The page does not change `open`: the service keeps its list in memory during the session and
rewrites it each time a screen opens or closes. That is why only a closed screen can be forgotten. A
forgotten screen reopens at the default place (1280 × 800, or a free monitor with the permission).

The page follows the file (`HackSettingsDocument.Changed`): a screen opened or moved, or an edit in
the terminal, shows without reloading the page.

## 4. Files

| File | Role |
| --- | --- |
| `Apps/System/HackerOs.Apps.Settings/ScreensPanel.razor(.cs, .css)` | The page, `RememberedScreen`, `RememberedPlace`. |
| `Apps/System/HackerOs.Apps.Settings/SettingsWindow.Navigation.cs` | `screens` entry in the Personalization group (`--page=screens`). |
| `Platform/HackerOs.Platform.Core/Settings/OsSettingsDocumentRegistry.cs` | `screens` document open to apps. |
| `Shared/HackerOs.AppSdk/Administration/HackScreens.cs`, `ScreensAdministrationContracts.cs` | SDK facade and contracts (ADR 0184). |
| `Platform/HackerOs.Platform.Core/Administration/AppScreensGateway.cs`, `IScreenControl.cs` | Trusted gateway: `screens.manage`, calling instance. |
| `Platform/HackerOs.Platform.Blazor/Display/ScreenControl.cs` | `IScreenControl` over `ScreenService` and `WindowRuntime` (screen of the calling window). |
| `Tests/HackerOs.Apps.Settings.Tests/ScreensPanelTests.cs` | Keys equal to the OS schema, `own` choice written, list and places, Forget, outside change, open screens and buttons, refusals, French. |
| `Tests/HackerOs.Platform.Core.Tests/Administration/AppScreensGatewayTests.cs` | Capability required, host without screens, facade. |
| `Tests/HackerOs.UI.E2E.Tests/ScreenServiceTests.cs` | New screen, Identify and Close from Settings in a real browser. |
| `Tests/HackerOs.Tests.Support/MinimalAppExecutionContext.cs` | `systemSettings` and `screens` parameters for panel tests. |

## 5. Limits

- The monitor name only shows with the `window-management` permission (Chrome, Edge).
- A place cannot be forgotten while its screen is open: close it first.
- On mobile, the open screens block is hidden and no secondary screen opens (`MobileShell`).
