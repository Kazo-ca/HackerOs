> [Version française](../../../fr/wasm/dev/taskbar-context-menus.md)

# Taskbar context menus

Work item 4 of the taskbar series (issue [#242](https://github.com/Kazo-ca/HackerOs/issues/242)),
implementation of [ADR 0194](adr/0194-taskbar-context-menus.md). It builds on the OS context menu
([ADR 0150](adr/0150-os-context-menu.md)), the window menu and "Other screen ▸"
([`other-screen-menus.md`](other-screen-menus.md)) and the per-screen taskbars
([`per-screen-taskbars.md`](per-screen-taskbars.md)).

## 1. What the user sees

| Where | Menu |
| --- | --- |
| A window's button | New window · Maximize/Restore, Minimize, Move, Resize, Snap left, Snap right, Full screen, Close · Other screen ▸ |
| The rest of the taskbar (including the Start button and the clock) | Task manager · Show the desktop, Cascade windows, Tile windows · Taskbar settings |

The menu opens with a right click, a long press, the Menu key or `Shift+F10`, on the taskbar of
every screen. A right click does not activate the window.

- **New window** starts the app once more on the taskbar's screen. It is disabled for a
  `SingleInstancePerUser` app or one missing from the catalog.
- **Task manager** opens the system monitor (`org.hackeros.system-monitor`) on the taskbar's
  screen.
- **Show the desktop** minimizes the windows of the taskbar's screen.
- **Cascade** and **tile** arrange the windows of the taskbar's screen (section 3).
- **Taskbar settings** opens Settings on the "Screens" page (`--page=screens`), which holds the
  `primary-taskbar` setting.

Entries with nothing to do are disabled.

## 2. The code

| Element | Role |
| --- | --- |
| `ITaskbarContextMenus` (package `HackerOs.Taskbar.Blazor`) | Gives the HTML attributes of the taskbar (`TaskbarAttributes`) and of each window button (`WindowItemAttributes(id)`). The `Taskbar.ContextMenus` parameter sets them with `@attributes`. Without it nothing changes. |
| `TaskbarContextMenus` (`Platform.Blazor/Shell`) | Creates a scope of the OS menu and registers `taskbar-window-menu` (attached object: the window and the taskbar's screen) and `taskbar-menu` (attached object: the screen). The menu is filled when it opens (`Opening`). `For(display)` gives each taskbar's object. |
| `WindowMenuDefinitions` (`Platform.Blazor/Windows`) | Builds the window menu entries and "Other screen ▸", for the title bar (`WindowManagerOverlays`) and for the taskbar. |
| `WindowArrangement` (`Platform.Blazor/Windows`) | `ShowDesktop`, `Cascade`, `Tile` and `ArrangeableWindows` over a `WindowRuntime` and a screen. |
| `DesktopShell` | Creates `TaskbarContextMenus` and hands it to screen 1's taskbar and to each secondary screen's; starts apps on the taskbar's screen (`LaunchFromTaskbarAsync`). |

The texts are in `Platform.Blazor/Resources/Strings*.resx` (keys `TaskbarMenu.*`).

## 3. Cascade and tile

Windows concerned (`ArrangeableWindows`): those of the screen, not minimized, without an owner,
not modal, and not blocked by a dialog. A fixed (non-movable) window stays where it is.

- A maximized window is restored first. Restoring a background window brings it to the front: the
  original order is then put back with `FocusWindowCommand`s from back to front.
- Each window gets a `MoveWindowCommand` (which takes it off its tile), then, when its size changes
  and it is resizable, a `ResizeWindowCommand` and a `CompleteWindowResizeCommand`. The runtime
  bounds the position and size as for a manual move, and `WindowGeometryRecorder` saves the new
  size (ADR 0116).
- **Cascade**: window `i` at `(x + 28·(i mod 8), y + 28·(i mod 8))` of the work area, keeping its
  size (`WindowLaunchCoordinator.CascadeStep`).
- **Tile**: `ceil(√n)` columns, `ceil(n / columns)` rows, whole-pixel cells (`TileCell`), filled
  row by row from back to front.

## 4. Tests

- `Tests/HackerOs.Taskbar.Blazor.Tests/TaskbarContextMenusTests.cs`: attributes set on the taskbar
  and the buttons, none without a host.
- `Tests/HackerOs.Platform.Blazor.Tests/Shell/TaskbarContextMenusTests.cs`: entries and state of
  both menus, launches on the taskbar's screen, disabled entries.
- `Tests/HackerOs.Platform.Blazor.Tests/WindowArrangementTests.cs`: show the desktop, cascade, tile,
  maximized, snapped, fixed and minimized windows and dialogs.
- `WindowMenuContextMenuTests` keeps the title bar's menu unchanged.
