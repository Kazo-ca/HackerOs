> [Version française](../../../fr/wasm/framework/desktop-api.md)

# Desktop: API for apps

This document describes what an app uses for the desktop: the `HackDesktop*` SDK types, the `desktop.config` file, `.desktop` shortcuts, the Desktop mode of the file control and the shortcut-creation intent. The Desktop app, the shell, the menus and the OS surfaces are described in [`dev/desktop.md`](../dev/desktop.md).

## SDK types

| Item | Project | Role |
| --- | --- | --- |
| `HackDesktopLayout`, `HackDesktopCell`, `HackDesktopOptions`, `HackDesktopItems` | `AppSdk` (`Files/HackDesktop.cs`) | The grid as pure functions, shared by every provider. |
| `HackDesktopEntry` | `AppSdk` (`Files/HackDesktopEntry.cs`) | Reading and writing `.desktop` files, `CreateShortcutAsync`. |
| `HackDesktopSettings` | `AppSdk` (`Files/HackDesktopSettings.cs`) | Reading and writing `desktop.config`. |
| `HackAppIcons.LucideName` | `AppSdk` (`Apps/`) | The Lucide icon of an app (the one in the Start menu), reused by shortcuts. |

## `desktop.config` file

```ini
[view]
icon-size=medium      # small | medium | large
auto-arrange=false
align-to-grid=true
sort=name             # name | size | type | date
show-home=true
show-trash=true

[positions]
::home=0,0
::trash=0,1
notes.txt=1,0
```

- User file only; there is no `/etc` layer.
- A key in `[positions]` is a file name (or `::home`, `::trash`) where
  `%`, `=`, `\`, `#`, `;`, `[`, `]`, control characters and leading or trailing
  spaces are written `%XX`; the value is `column,row`, starting at 0.
- An invalid line is ignored. Writing `[view]` keeps the comments and unknown
  keys; `[positions]` is rewritten entirely.
- The desktop follows the file: editing it with `nano` or Settings redraws
  the icons.

```csharp
HackDesktopSettingsDocument settings = await HackDesktopSettings.ReadAsync();
await HackDesktopSettings.SaveOptionsAsync(settings.Options with { IconSize = HackDesktopIconSize.Large });
```

## Grid (`HackDesktopLayout`)


| Member | Role |
| --- | --- |
| `CellSize(size)` | Size of a cell: small 76 × 84, medium 92 × 100, large 116 × 128 CSS pixels. |
| `GridSize(width, height, size)` | Columns and rows that fit (at least one of each). |
| `Order(items, sort, showHome, showTrash)` | Order of new items: Home folder, Trash, then folders and files according to the sort. |
| `Arrange(keys, saved, columns, rows, autoArrange, alignToGrid)` | Displayed cell of each item, positions to save and `Changed`. A position outside the screen is kept (the item is shown elsewhere). |
| `Move(current, moved, anchor, target, columns, rows, autoArrange)` | Positions after a drag: offsets kept, occupied cells skipped; with automatic arrangement, insertion at the target cell. |
| `CellAtPoint(x, y, size, columns, rows)` | Cell under a point. |

Cells run from top to bottom, then from left to right.

## Shortcuts (`HackDesktopEntry`)

```ini
[Desktop Entry]
Type=Application
Name=Text Editor
Exec=org.hackeros.text-editor "/home/alice/My notes.txt"
Icon=file-text
```

`TryParse`, `Format`, `IsDesktopFile`, `IsAppId`, `SplitExec`, `JoinExec` and
`CreateShortcutAsync(folder, appId, name)` (writes `Name.desktop`, then
`Name (2).desktop`...). The codes `%f`, `%U`... are ignored; `%%` gives `%`. A
file that is not a valid shortcut remains a text file.

In every file control provided by the explorer, a shortcut shows the app's
name and icon, without an extension; opening it launches the app with its
arguments; a missing app greys out the icon and opening it explains why.

## File control in Desktop mode

```razor
<HackFileView InitialDirectory="@desktop"
              Mode="HackFileViewMode.Desktop"
              DesktopOptions="@_options"
              DesktopPositions="@_positions"
              DesktopPositionsChanged="SavePositionsAsync"
              ContextMenuOpening="OnContextMenuOpening" />
```

The host keeps the file; the provider places, moves and reports the positions
to save (`DesktopPositionsChanged`) after a move or when a new item takes a
cell. Nothing is saved before the folder has been read, so that positions are
never erased.

## Launch and intents

Settings opens directly on a tab with `--page=appearance` or
`--page=desktop`.


"Send to ▸ Desktop (create shortcut)" goes through the `org.hackeros.desktop.create-shortcut.v1` intent, and the file is written with the rights of the Desktop app.

The `background` and `desktop` entries of apps (ADR 0159) are added to the background menu; see [`context-menu.md`](context-menu.md).
