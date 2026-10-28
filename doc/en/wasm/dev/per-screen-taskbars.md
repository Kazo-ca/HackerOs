> [Version française](../../../fr/wasm/dev/per-screen-taskbars.md)

# One taskbar per screen

Work item `multi-monitor/5-taskbars` of the [multi-monitor plan](plans/multi-monitor-plan.md),
implementing [ADR 0179 §1](adr/0179-per-screen-taskbars-and-other-screen-menus.md). It builds on
the secondary screens' desktop ([`secondary-screen-desktop.md`](secondary-screen-desktop.md)) and
on the screen service ([`screen-service.md`](screen-service.md)). Without a secondary screen,
nothing changes: screen 1's taskbar shows every window, as before.

## 1. What each taskbar shows

| Taskbar | Windows | Start button | Clock | Notification area, clock panel, session button |
| --- | --- | --- | --- | --- |
| Screen 1 | Per `primary-taskbar`: every window (`all`, the default) or only screen 1's (`own`) | Yes | Yes | Yes |
| Screen 2 and up | The windows of its screen | Yes | Yes (no panel) | No: they stay on screen 1 |

- Clicking the button of a window on another screen (screen 1's taskbar in `all` mode) activates it
  on its screen, without moving it. The click keeps its rules (minimize the active window, restore a
  minimized window).
- A window that changes screen (`MoveWindowToDisplayCommand`, work items 6 and 8) moves from one
  taskbar to the other at the next update.

## 2. The `primary-taskbar` setting

It lives in `screens.config` (ADR 0179 §7), section `[taskbar]`:

```ini
[taskbar]
primary-taskbar = own   ; all | own
```

| Member | Role |
| --- | --- |
| `PrimaryTaskbarMode` (`HackerOs.Platform.Core.Shell`) | `All` or `Own`. |
| `ScreensSettingsDocuments.ReadPrimaryTaskbar(snapshot)` | `own` (ignoring case and spaces) gives `Own`; any other value, or no line, gives `All`. |
| `ScreensSettingsDocuments.FormatPrimaryTaskbar(mode)` | `all` or `own`. |
| `ScreenService.PrimaryTaskbar` | Read at start, then followed: the service watches the file (`WatchAsync`) and reads **only** `primary-taskbar` again on each change. The screens to restore are read at start only. |
| `ScreenService.SetPrimaryTaskbarAsync(mode)` | Changes the value and writes it to the user's file; the tests use it; Settings ▸ Screens writes the file, which the service follows (work item 7). |

A change raises `ScreenService.Changed`, and `DesktopShell` makes screen 1's taskbar read its list
again: editing the file in the terminal is enough, no reload.

## 3. Start menu

- `TaskbarLauncherAdapter` stays the only Start menu state, and remembers the screen it is open on
  (`Display`, internal).
- Each taskbar gets a view of that state (`For(screen)`): its button is "active" only while the menu
  is open on its screen. A screen's button closes the menu open on that screen, otherwise opens it on
  that screen (it then leaves the other screen).
- `DesktopShell` renders `AppLauncher` in the main tab for screen 1, and in the secondary screen's
  content (`ScreenLayer`'s `ScreenTaskbar` fragment) for the others. The menu is `position: fixed`:
  it places itself in its screen's window.
- A screen closed while the menu is open on it closes the menu.

## 4. Which screen a new window opens on

`WindowLaunchCoordinator` picks the window's screen before its first await, then places the window
in that screen's work area (cascade counted among that screen's windows):

| Launch | Screen |
| --- | --- |
| From a screen's Start menu | That screen: `DesktopShell` opens an `OpenOnDisplay(appId, screen)` scope (internal) around the launch and the presentation. |
| From the notification area (server icon) | Screen 1. |
| Otherwise (command, another app, opened file) | The active window's screen, screen 1 when there is none. |
| Requested screen closed meanwhile | Screen 1. |

The scope also covers the presentation started by the process observer (`WindowProcessObserver`),
which may come before the shell's. A modal window always follows its owner (ADR 0178 §2).

## 5. Layout in a secondary screen

```
.secondary-screen (flex, column)
 ├─ .secondary-screen-desktop (flex: 1)  ← DesktopArea: measures the work area
 ├─ AppLauncher (while open on this screen)
 ├─ footer.taskbar-container              ← ScreenLayer's ScreenTaskbar fragment
 └─ overlays, veil, banner, full-screen button
```

The taskbar is outside the `DesktopArea`: the work area the desktop measures (work item 4) leaves it
out by itself, and a maximized window stops above the taskbar.

| File | Role |
| --- | --- |
| `Platform/HackerOs.Platform.Blazor/Shell/TaskbarAdapters.cs` | `TaskbarScreenFilter.Lists`, `TaskbarWindowSourceAdapter`'s filtered list (`Refresh`), `TaskbarLauncherAdapter`'s per-screen views. |
| `Platform/HackerOs.Platform.Blazor/Shell/DesktopShell.razor` | Screen 1's filtered taskbar, each secondary screen's taskbar and Start menu, the launch's screen. |
| `Platform/HackerOs.Platform.Blazor/Display/ScreenLayer.razor` | `ScreenTaskbar` parameter, rendered under the screen's desktop. |
| `Platform/HackerOs.Platform.Blazor/Display/ScreenService.cs` | `PrimaryTaskbar`, `SetPrimaryTaskbarAsync`, watching `screens.config`. |
| `Platform/HackerOs.Platform.Blazor/Windows/WindowLaunchCoordinator.cs` | `OpenOnDisplay`, screen and work area of a new window. |
| `Platform/HackerOs.Platform.Core/Shell/ScreensSettings.cs` | `PrimaryTaskbarMode`, `[taskbar]` section. |

The `HackerOs.Taskbar.Blazor` package does not change: the filter lives in the OS adapter.

## 6. Tests

- `Tests/HackerOs.Platform.Blazor.Tests/Shell/ScreenTaskbarTests.cs`: filter (`all` / `own`,
  secondary screens), lists that follow a window changing screen, per-screen Start menu.
- `Tests/HackerOs.Platform.Blazor.Tests/WindowLaunchCoordinatorTests.cs`: an app from a screen's
  Start menu opens on that screen; another launch on the active window's screen; vanished screen.
- `Tests/HackerOs.Platform.Blazor.Tests/Display/ScreenServiceTests.cs`: `primary-taskbar` read at
  start, followed when the file changes, written to the user's file.
- `Tests/HackerOs.Platform.Blazor.Tests/Display/ScreenLayerTests.cs`: taskbar rendered under the desktop.
- `Tests/HackerOs.Platform.Core.Tests/Shell/ScreensSettingsTests.cs`: reading and writing `[taskbar]`
  on the virtual disk.
- `Tests/HackerOs.UI.E2E.Tests/ScreenServiceTests.cs`, real Chromium: screen 2 has its taskbar, under
  its desktop, without notification area; its Start menu opens there and opens its app there; screen
  1's menu stays on screen 1; screen 1's taskbar lists both windows, then only its own once
  `primary-taskbar = own` is written in the terminal.

## 7. Limits

- A secondary screen's taskbar has no clock panel (notifications, calendar): it stays on screen 1.
- Screen 1's taskbar in `all` mode does not say which screen each window is on.
- Settings ▸ Screens chooses `primary-taskbar` by writing the file, which the service follows
  (work item 7, [`screens-settings.md`](screens-settings.md)).
