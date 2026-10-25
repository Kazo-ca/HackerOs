# ADR 0160: desktop, implementation adjustments

## Status

Accepted on 2026-10-20.

- **Work item:** `desk/5-desktop` (plan
  [`desktop-and-context-menu-plan.md`](../plans/desktop-and-context-menu-plan.md) §12,
  work item 5).
- **Adjusts:** ADR 0152 (§1 desktop host, §2 grid, §3 positions and
  options, §4 system icons, §5 shortcuts, §6 menu, §8 Settings). Its
  decisions remain true and are delivered as is; this ADR fixes what
  they left open.

## Context

ADR 0152 decides that the desktop displays `~/Desktop` through the file
control in **Desktop mode**, with positions remembered in
`~/.config/hackeros/desktop.config`, the Home folder and Trash icons,
`.desktop` shortcuts and the `Desktop` scope of the menu. While writing it, several
points had to be fixed: who runs the desktop and with what rights, what
the control's contract carries for Desktop mode, where the grid computation lives,
how the Start menu creates a shortcut. The usage guide is
[`desktop.md`](../desktop.md).

## Decision

### 1. The desktop is a process: the system app `org.hackeros.desktop`

The file control runs with the rights of the app that displays it
(ADR 0148 §4): it needs an app context. The desktop is therefore a system
app, **Desktop** (`org.hackeros.desktop`, project `HackerOs.Apps.Desktop`),
hidden from the Start menu, single instance, which uses only the public SDK.
This is the role of `explorer.exe` under Windows or `xfdesktop` under Xfce.

- The desktop shell (`DesktopShell`) starts it when the
  session opens (`DesktopSurfaceHost`, in `DesktopArea.BackgroundContent`) through the
  normal path (`AppLifecycleOrchestrator.LaunchAsync`): a real process,
  visible in `ps` and the task manager, with the capabilities of its
  manifest for the session user.
- The shell renders its entry component **outside any window**, in the
  background layer, with its context and a menu scope in cascade, as
  `WindowAppRenderer` does in a window. Before launch,
  `WindowLaunchCoordinator.ReserveShellSurface(appId)` reserves the app as a
  shell surface: `PresentAsync` never opens a window for it, whoever
  launches it (counted reservation, released when the shell
  closes).
- `kill` or "End task" stops the desktop: the icons disappear. If it
  **crashes** (`Faulted`), the shell restarts it once, as Windows
  restarts Explorer; stopped voluntarily, it comes back at the next
  login.
- Without the app (host that does not reference it, disabled app), the background stays empty.
- The background layer becomes interactive: `DesktopArea.BackgroundInteractive`
  (new parameter, `false` by default) makes it clickable and readable by
  screen readers, and lets the clicks of the window layer through where
  there is no window.

### 2. The Desktop mode contract

`HackFileView` (ADR 0155 §2) gains, changing nothing for the other modes:

| Member | Role |
| --- | --- |
| `HackFileViewMode.Desktop` | Desktop grid. |
| `DesktopOptions` (`HackDesktopOptions`) | Icon size, automatic rearrangement, alignment, sort, system icons displayed. |
| `DesktopPositions` | Cell of each item, by name (`::home`, `::trash` for the system icons). |
| `DesktopPositionsChanged` | The positions to save, after a move or when a new item has taken a cell. |

The host (the Desktop app) reads and writes the file; the provider draws, places
and moves. A third-party provider of the control (ADR 0156) receives the same
parameters. The types are in `HackerOs.AppSdk.Files` (without Blazor):
`HackDesktopCell`, `HackDesktopIconSize`, `HackDesktopSort`,
`HackDesktopOptions`, `HackDesktopItems`, `HackDesktopLayout`.

### 3. The grid: `HackDesktopLayout`, a pure SDK function

Placement is a pure, tested SDK function, so that every provider
places the icons the same way:

- cells in **columns from top to bottom, then from left to right**; the number of
  columns and rows comes from the desktop size measured by the
  provider and from the cell size (small 76 × 84, medium 92 × 100,
  large 116 × 128 pixels);
- an item without a position takes the **first free cell**; the order of
  new items is: system icons, then the `sort` order (folders
  first);
- two items saved on the same cell: the second goes to the next free
  cell, and this is saved;
- an off-screen position (shrunken desktop) is **not rewritten**:
  the item is shown on the free cell closest to its position if
  `align-to-grid=true`, otherwise on the last free cell (ADR 0152 §3);
- a name absent from the folder is not saved, hence forgotten at the next
  save;
- **automatic rearrangement**: items are arranged without gaps in
  the order of their saved positions (new ones at the end, in sort
  order); dragging an icon inserts it at the targeted cell, the others
  shift;
- **move**: the dragged items keep their offset relative to the one being
  held; an occupied cell sends the item to the next free cell.

"Sort by" is an action: it writes `sort` and clears `[positions]`; everything
becomes new and is arranged in sort order.

### 4. `desktop.config`

- A **user file only**, `~/.config/hackeros/desktop.config`,
  like a Linux desktop's configuration file under `~/.config/`: no
  OS document or `/etc` layer (the desktop is specific to each
  user, and `[positions]` has no schema). The format is read and written
  by `HackDesktopSettings` (SDK, `HackerOs.AppSdk.Files`): `Parse`,
  `WithOptions`, `WithPositions`, `ReadAsync`, `SaveOptionsAsync`,
  `SavePositionsAsync`, with the rights of the ambient app.
- `[view]` (`icon-size`, `auto-arrange`, `align-to-grid`, `sort`, `show-home`,
  `show-trash`) is rewritten line by line (`ConfigDocumentEditor`): comments
  and unknown keys remain. `[positions]` is rewritten in
  full, at the end of the file. A wrong line or value is ignored and
  the default applies; the folder is created if missing.
- The Desktop app follows `~/.config/hackeros` (`HackFileSystemWatcher`) and
  redraws when the file changes (Settings, an editor, `nano`).
- A key is a file name; `%`, `=`, `\`, `#`, `;`, `[`, `]`, control
  characters and edge spaces are encoded there as `%XX`.
  The value is `column,row`, starting from 0.

### 5. System icons

The **provider** draws Home folder and Trash in Desktop mode according to
`ShowHome` and `ShowTrash`, with their menus (Open, Properties; Open,
Empty Trash, Properties) and their drops (into `~`, into the trash).
Opening the Trash launches the explorer with the argument `trash:///`, like
Nautilus. The Trash icon is full when `~/.local/share/Trash/files`
contains an item. These menus do not carry `HackMenuDefinition.FileMenu`:
the apps' entries (ADR 0151) are not added to them.

### 6. `.desktop` shortcuts

- `HackDesktopEntry` (SDK) reads and writes the format: `[Desktop Entry]`,
  `Type=Application`, `Name`, `Exec=<appId> [arguments]` (arguments quoted
  as the freedesktop specification says), optional `Icon`.
  `CreateShortcutAsync` writes `<Name>.desktop`, `<Name> (2).desktop`… in a
  folder, with the rights of the ambient context.
- The **provider** shows them everywhere (desktop, explorer, dialogs) with
  the app's name and icon, without extension; opening them launches the app
  (`IAppIntentGateway.LaunchAsync`) with its arguments; an app absent from
  the user's catalog grays out the icon, and opening it explains why. A malformed
  file remains a text file. A host that takes over opening
  (`Opening.Handled`, an Open dialog) keeps control.
- **Dragging an app from the Start menu** sets the type
  `application/vnd.hackeros.app+json` (`{"appId":…}`); the file control
  accepts it everywhere as a drop that creates a shortcut in the targeted folder
  (on the desktop: at the drop cell).
- **"Send to ▸ Desktop (create shortcut)"**: the Start menu now has
  the OS menu on each app (Open, Pin / Unpin,
  Send to ▸ Desktop). The shell sends the intent
  `org.hackeros.desktop.create-shortcut.v1` (`{"appId":…}`) to the desktop
  instance, which writes the file with its own rights. The entry appears only
  if the desktop is running.

### 7. Desktop menu

In Desktop mode, the background has the `Desktop` scope (ADR 0159 §5: the apps'
`background` and `desktop` entries are added to it). The provider gives the
empty-space menu (New Folder, Paste, Refresh, Properties); the Desktop app adds to it
through `ContextMenuOpening`:

- "View ▸": Large / Medium / Small icons, Auto arrange,
  Align to grid, Show Home folder, Show
  Trash (checkboxes);
- "Sort by ▸": Name, Size, Type, Date modified;
- "Background...": launches Settings with `--page=appearance`;
- "Desktop settings...": launches Settings with `--page=desktop`.

Opening a desktop folder opens an explorer window, not a
navigation within the desktop.

### 8. Settings ▸ Desktop

A **Desktop** tab, after Appearance, sets `[view]` of the user's file
through `HackDesktopSettings` (icon size, automatic
rearrangement, alignment, system icons, sort) and can forget the positions.
Settings accepts `--page=appearance` and `--page=desktop`. The index of the
"Application" tab (`--app=`) is now counted from the fixed tabs
and Backup (it was off by one since the File Menus
tab).

### 9. Mobile

`MobileShell` does not start the desktop: no icons (ADR 0152 §7).

## Consequences

- One more app in the ecosystem and its host templates; the desktop is
  loaded at login, with the explorer's assembly (provider of the
  control).
- New unshipped APIs (`PublicAPI.Unshipped.txt`) in `AppSdk`
  (`HackDesktop*`, `HackDesktopEntry`, `HackDesktopSettings`, `HackAppIcons`,
  `HackFileMenuItems.EmptyTrash`), `AppSdk.Blazor`, `AppSdk.FileView`,
  `Windowing.Blazor` and `Platform.Blazor`, published with SDK 2.0
  (work item 6). `empty-trash` joins the default entries known to the
  manifest (`AppContextMenus.DefaultItemIds`).
- [`settings-files.md`](../../framework/settings-files.md) gains `desktop.config`,
  [`embedded-components.md`](../../framework/embedded-components.md) the Desktop mode
  parameters.

## Alternatives rejected

- **An app context fabricated by the shell, without a process**: a manifest
  outside the catalog breaks the gateways that read the catalog, and the desktop
  would be invisible in `ps`.
- **The desktop in the Explorer app (two manifests in one project)**: a
  multi-app project changes the manifest and resource paths of the
  explorer for no gain.
- **The provider writes `desktop.config` itself**: a third-party provider
  would have to know the format; the host keeps the file, the provider sees
  only positions.
- **Positions in pixels**: ADR 0152 fixes cells; a cell stays
  correct when the icon size changes.

## References

- ADR 0037, 0043, 0044, 0048, 0148, 0150, 0151, 0152, 0154, 0155, 0156, 0158,
  0159.
- [`desktop.md`](../desktop.md),
  [`desktop-and-context-menu-plan.md`](../plans/desktop-and-context-menu-plan.md),
  [`trash.md`](../trash.md), [`settings-files.md`](../../framework/settings-files.md).
