> [Version française](../../../fr/wasm/dev/desktop.md)

# Desktop: icons, shortcuts and `desktop.config`

The `HackDesktop*` types, the `desktop.config` file, the `.desktop` shortcuts and the shortcut-creation intent intended for apps are in [`desktop-api.md`](../framework/desktop-api.md).

Work item `desk/5-desktop` of the plan
[`desktop-and-context-menu-plan.md`](plans/desktop-and-context-menu-plan.md) (§5).
The decisions are in [ADR 0152](adr/0152-desktop-icons.md) and
[ADR 0160](adr/0160-desktop-implementation-adjustments.md); this document
describes the delivered code.

Since ADR 0166, the desktop hides names starting with a dot;
`~/Desktop/.mobile/layout.conf` contains the mobile layout and creates no
icon on the desktop. The explorer can reveal this hidden folder.

## Purpose

The desktop background shows the files of `~/Desktop` as an icon grid, with
the **Home** and **Trash** system icons, like the Windows or Xfce desktop:

- each icon keeps its cell, remembered in
  `~/.config/hackeros/desktop.config`; a new file takes the first free cell;
  "Auto-arrange" lines everything up with no gaps;
- drag to move (snapped to the grid), lasso selection, Ctrl and Shift to
  extend, arrows, Enter, F2 and Delete;
- drag and drop with the explorer and between windows, drop onto a folder,
  onto Home (copy or move to `~`) or onto Trash;
- **app shortcuts**: `.desktop` files in freedesktop format, created by
  dragging an app from the Start menu onto the desktop (or into an explorer
  folder) or through "Send to ▸ Desktop (create shortcut)";
- the OS context menu, scope `Desktop`, with "View ▸", "Sort by ▸",
  "Background…" and "Desktop settings…";
- a **Settings ▸ Desktop** tab.

On mobile, there are no desktop icons (ADR 0152 §7).

## Architecture

```text
DesktopShell ──► DesktopArea.BackgroundContent (BackgroundInteractive)
                   └─ DesktopSurfaceHost ── launches org.hackeros.desktop (real process)
                        └─ DesktopSurface (Desktop app, rights from its manifest)
                             └─ HackFileView Mode=Desktop ──► registry ──► ExplorerFileView
                                                                └─ FileView ─► FileViewDesktop (grid)
```

| Element | Project | Role |
| --- | --- | --- |
| `DesktopSurfaceHost` | `Platform.Blazor` (`Shell/`) | Reserves the app as the shell surface (`WindowLaunchCoordinator.ReserveShellSurface`), launches it for the session user, renders its entry component outside a window with the app context and a menu scope, relaunches it once if it crashes. `CreateShortcutAsync(appId)` sends the shortcut intent. |
| `DesktopArea.BackgroundInteractive` | `Windowing.Blazor` | Renders the background layer clickable and readable; the windows layer lets the pointer through outside the windows. |
| **Desktop** app (`org.hackeros.desktop`) | `Apps/System/HackerOs.Apps.Desktop` | Hidden, single-instance system app, public SDK only. Creates `~/Desktop` if missing, reads and writes `desktop.config`, tracks `~/.config/hackeros`, completes the background menu, receives the shortcut intent. |
| `FileViewDesktop` | explorer, `FileControl/` (`AppSdk.FileView` before SDK 2.0) | The Desktop mode of the internal control: measured grid (`ResizeObserver`), selection, lasso, keyboard, drag and drop, system icons. Collocated script `FileViewDesktop.razor.js`. |
| `ExplorerFileView` (`ExplorerFileView.Desktop.cs`) | `Apps.FileExplorer` | The provider: menus and actions for Home and Trash, full or empty Trash state, shortcut decoration (app name and icon, grayed out if the app is missing), launching a shortcut, creating shortcuts on an app drop. |
| Start menu (`AppLauncher.Menu.cs`, `AppLauncher.razor.js`) | `Platform.Blazor` | OS menu on each app (Open, Pin, Send to ▸ Desktop) and dragging an app (`application/vnd.hackeros.app+json`). |
| `DesktopPanel` | `Apps.Settings` | The Settings ▸ Desktop tab. |

Since the desktop is a process, it appears in `ps` and the task manager;
`kill` closes it (the icons disappear until the next login), a crash
relaunches it once.

## Usage and API

### Menus

- **Background** (scope `Desktop`): New folder, Paste, Refresh, Provider
  properties, plus, at the top, "View ▸" (sizes, Auto-arrange, Align to grid,
  Show Home, Show Trash, checked with the `check` icon) and "Sort by ▸"
  (clears the positions), and at the end "Background…" (`--page=appearance`)
  and "Desktop settings…" (`--page=desktop`). The `background` and `desktop`
  entries of apps (ADR 0159) are added to it.
- **Home**: Open, Properties. **Trash**: Open, Empty trash (with
  confirmation), Properties. Opening Trash launches the explorer with
  `trash:///`.
- **Start menu**, on each app: Open, Pin / Unpin, "Send to ▸ Desktop (create
  shortcut)", grayed out when the desktop is not running.

### Settings ▸ Desktop

Tab after Appearance: icon size, auto-arrange, alignment, system icons, sort,
"Forget icon positions".

## Key decisions

- **The desktop is an app** (ADR 0160 §1): the file control runs with the
  rights of an app, and a real process is seen and killed like `explorer.exe`
  or `xfdesktop`.
- **No window for the surface**: the reservation by app id in
  `WindowLaunchCoordinator` prevents any window, even if someone launches the
  app by hand.
- **User file, codec in the SDK** (ADR 0160 §4): no OS document or schema for
  `[positions]`; every provider and Settings use the same code.
- **Cells, not pixels**: a cell remains correct when the icon size changes.
- **Shortcut written by the desktop**: "Send to ▸ Desktop" goes through the
  `org.hackeros.desktop.create-shortcut.v1` intent, and the file is written
  with the rights of the Desktop app, not of the shell.

## Tests

- `HackerOs.AppSdk.Tests/Files/HackDesktopTests.cs`: order, placement,
  collisions, off-screen, auto-arrange, moves, shortcuts (reading, writing,
  rejection, numbered names), `desktop.config` (reading, writing, encoded
  keys, folder creation).
- `HackerOs.Apps.FileExplorer.FileControl.Tests/FileViewDesktopTests.cs`: app
  drag payload.
- `HackerOs.Platform.Blazor.Tests/WindowLaunchCoordinatorTests.cs`:
  surface reservation.
- `HackerOs.Apps.Settings.Tests/SettingsWindowTests.cs`: `--page=`.
- `HackerOs.Ecosystem.Tests/BuildKnownLazyAppsTests.cs`: the Desktop app is a
  system app loaded on demand.

## Tasks (work item `desk/5-desktop`)

- [x] ADR 0160 (adjustments to ADR 0152).
- [x] SDK contract: `HackDesktop*`, `HackDesktopEntry`, `HackDesktopSettings`,
  `HackAppIcons`, `HackFileMenuItems.EmptyTrash`.
- [x] `HackFileView`: `HackFileViewMode.Desktop`, `DesktopOptions`,
  `DesktopPositions`, `DesktopPositionsChanged`.
- [x] `FileViewDesktop`: grid, selection, lasso, keyboard, renaming,
  drag and drop, system icons.
- [x] Explorer provider: system menus, full or empty Trash, decorated and
  launched shortcuts, app drop, `trash:///`.
- [x] Desktop app and `DesktopSurfaceHost`, interactive background layer,
  surface reservation.
- [x] Start menu: OS menu, app drag, "Send to ▸ Desktop".
- [x] Settings ▸ Desktop and `--page=`.
- [x] Tests and documentation (this document, ADR, index, linked documents).
