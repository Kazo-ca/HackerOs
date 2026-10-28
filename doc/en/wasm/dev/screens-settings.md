> [Version française](../../../fr/wasm/dev/screens-settings.md)

# Settings ▸ Screens

Work item `multi-monitor/7-screen-settings` of the [multi-monitor plan](plans/multi-monitor-plan.md),
implementing [ADR 0179 §7](adr/0179-per-screen-taskbars-and-other-screen-menus.md). The page reads
and writes `screens.config`; it builds on the screen service
([`screen-service.md`](screen-service.md)) and on the per-screen taskbars
([`per-screen-taskbars.md`](per-screen-taskbars.md)), which follow the file.

## 1. What the page shows

| Block | Content | Writes |
| --- | --- | --- |
| Taskbar of screen 1 | Choice of `all` (every window of every screen) or `own` (those of screen 1), with the value's origin (default, system, user) and the administrator's lock. | `[taskbar] primary-taskbar` of the user layer |
| Remembered screens | Screen 1 (this browser tab), then each screen 2 to 9 listed in `open` or with a valid place: state "open, or offered at the next sign-in" or "closed", place `width × height at (left, top)`. A **Forget** button on a closed screen that has a place. | Removes the numbered line from the user layer |
| Open and move screens | Where the commands are: Other screen ▸ New screen (right-click on the icon or on Maximize), `Super+Shift+←/→` shortcuts, the **Restore screens** card, the `⤢` button, the window management permission (Chrome, Edge). | — |

The file is `~/.config/hackeros/screens.config`; `/etc/hackeros/screens.config` gives the system
defaults (an administrator writes it, for instance with `sudo nano`).

## 2. Why the page has no screen buttons

Settings is a base app: it only goes through the public SDK, and the series does not touch the apps'
SDK (question Q1 of the plan). So the page does not call `ScreenService`: it opens the `screens`
document with `HackSettings.OpenSystemAsync(AppContext, "screens")`, which the OS now lists in
`OsSettingsDocumentRegistry`. The New screen, Restore, Identify (the veil when hovering the submenu)
and Full screen commands stay where they already are.

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
| `Tests/HackerOs.Apps.Settings.Tests/ScreensPanelTests.cs` | Keys equal to the OS schema, `own` choice written, list and places, Forget, outside change, French. |
| `Tests/HackerOs.Tests.Support/MinimalAppExecutionContext.cs` | `systemSettings` parameter for panel tests. |

## 5. Limits

- No monitor name: the file does not keep it, and the SDK does not expose the screen service.
- No state of the `window-management` permission on the page, for the same reason; the page explains
  when the browser asks for it.
- A place cannot be forgotten while its screen is open: close it first.
- On mobile, the page stays visible but no secondary screen opens (`MobileShell`).
