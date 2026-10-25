# ADR 0152: desktop icons

## Status

Accepted on 2026-10-19.

- **Benoit's decision:** plan [`desktop-and-context-menu-plan.md`](../plans/desktop-and-context-menu-plan.md)
  approved on 2026-10-19, questions Q1 (`~/Desktop` plus Home folder and
  Trash), Q2 (grid and remembered free position, automatic
  rearrangement), Q18 (`Desktop` scope of the menu), Q20 (mobile: menu yes, icons
  no), Q21 (`.desktop` files).
- **Builds on:** ADR 0148 (control provided by the explorer), ADR 0150
  (OS menu), ADR 0151 (file menus and registered entries).

## Context

`~/Desktop` is created for each user (`FileSystemSeeder`) and
`DesktopArea.BackgroundContent` expects content, but the desktop is empty.
The file control already knows how to list, rename, drag and drop; it lacks a
free grid layout and memory of positions.

## Decision

### 1. The desktop displays the file control

`DesktopShell` fills `DesktopArea.BackgroundContent` with `HackFileView`
(ADR 0148) in **Desktop mode** (`FileViewMode.Desktop`) on `~/Desktop`, with
the rights of the user's session. The desktop has no list code of its own:
a fix to the explorer reaches it too.

### 2. Desktop mode

- Grid of cells whose size follows the icon size (small,
  medium, large), in columns from top to bottom then left to right, like
  Windows.
- Each icon occupies one cell. Dragging an icon moves it to the nearest
  cell (snapped); an occupied cell sends the icon to the next free cell.
- Selection by click, Ctrl+click, Shift+click and **lasso**; in-place renaming
  (F2 or slow click); double-click or Enter opens; Delete moves to the trash.
- Drag and drop with the explorer and between windows (ADR 0037 §4), with
  the same move, copy and conflict rules as the explorer.
- The desktop background (wallpaper) remains that of the theme; Desktop mode is
  transparent.

### 3. Positions

`~/.config/hackeros/desktop.config` (OS settings format,
[`settings-files.md`](../../framework/settings-files.md)):

```ini
[positions]
notes.txt=0,2
Projets=1,0

[view]
auto-arrange=false
align-to-grid=true
icon-size=medium
show-home=true
show-trash=true
sort=name
```

- A new file takes the **first free cell** in grid
  order.
- A name absent from the folder is **forgotten at the next save**.
- Renaming or moving an icon rewrites its line.
- **Auto arrange** (`auto-arrange=true`) sorts according to `sort`
  (name, size, type, date) and arranges continuously; dragging an icon then changes
  the order instead of the cell.
- **Align to grid** brings an off-grid icon (resized window)
  back to the nearest cell.
- When the screen shrinks, an icon outside the screen is displayed on the
  last free cell, without changing its saved position.

### 4. System icons

- **Home folder** (`~`) and **Trash** are **virtual** entries
  added by the desktop, not files of `~/Desktop`. Their place is
  in `[positions]` under the reserved names `::home` and `::trash`.
- Displayed according to `show-home` and `show-trash` (Settings ▸ Desktop, and the
  desktop's "View" menu).
- Home folder menu: Open, Properties. Trash menu: Open,
  Empty Trash (confirmation), Properties. The Trash icon shows
  whether it is full ([`trash.md`](../trash.md)).
- Dropping an item on the Trash moves it to the trash; on Home folder,
  moves it into `~`.

### 5. App shortcuts: `.desktop` files

- `~/Desktop/<name>.desktop`, freedesktop "Desktop Entry" format:
  `[Desktop Entry]`, `Type=Application`, `Name`, `Exec=<appId> [arguments]`,
  `Icon` (optional; the app's icon otherwise).
- Created by dragging an app from the Start menu to the desktop, or through
  "Send to ▸ Desktop (create shortcut)" in the context menu of an
  app in the Start menu.
- Everywhere the file control shows them (desktop, explorer,
  dialogs): app icon and name, extension hidden; double-click = launching
  the app through `LaunchApp` with its arguments, and its own rights.
- A `.desktop` whose app is uninstalled or disabled is displayed **grayed out**;
  opening it explains that the app is not installed.
- A malformed `.desktop` is displayed as a text file.

### 6. Desktop menu

The `Desktop` scope (ADR 0151 §1) is the empty-space menu plus "View ▸"
(icon size, Auto arrange, Align to grid,
Show Home folder, Show Trash), "Sort by ▸" and
"Background..." (opens Settings ▸ Appearance). Entries registered with
the `Desktop` scope are added to it. The menus of a file or folder on the
desktop are the usual `File` and `Directory` menus.

### 7. Mobile

No icons on the desktop: the mobile launcher stays as is. The OS menu
(long press) applies to the rest (ADR 0150 §5).

### 8. Settings ▸ Desktop

A Settings page sets `[view]`: icon size, rearrangement,
alignment, sorting, system icons displayed.

## Consequences

- `~/Desktop` becomes visible: files an app writes there appear.
- [`settings-files.md`](../../framework/settings-files.md) gains `desktop.config`.
- Work item 5 of the plan delivers Desktop mode, the positions, the system
  icons, the `.desktop` files, the `Desktop` menu and the Settings page.

## Alternatives rejected

- **A separate desktop component**: two list codes to maintain; the
  explorer's control is the source of truth (ADR 0148).
- **Grid without free position**: rejected by Benoit (Q2).
- **Shortcuts in a custom format**: `.desktop` is the well-known Linux format
  and can be read in the terminal.

## References

- ADR 0037, 0148, 0150, 0151.
- [`desktop-and-context-menu-plan.md`](../plans/desktop-and-context-menu-plan.md) §5,
  [`trash.md`](../trash.md), [`settings-files.md`](../../framework/settings-files.md).
