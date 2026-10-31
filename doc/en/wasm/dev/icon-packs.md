> [Version française](../../../fr/wasm/dev/icon-packs.md)

# Icon packs

Decision: [ADR 0203](adr/0203-icon-packs.md). Issue: [#255](https://github.com/Kazo-ca/HackerOs/issues/255).

## Purpose

An icon pack replaces the icons of apps, folders and files with PNG images. It
is chosen in **Settings ▸ Appearance ▸ Icon pack**; the Start menu, the desktop,
File Explorer, the windows, the taskbar and the mobile home change at once.

## Making a pack

1. Create a folder, for example `~/.local/share/icons/my-pack/` (for yourself)
   or `/usr/share/icons/my-pack/` (for everyone, with `sudo`).
2. Add `icon-pack.json`:

   ```json
   { "id": "my-pack", "name": "My pack", "version": "1.0.0", "author": "Me", "description": "Round icons" }
   ```

3. Add PNG images (64 × 64 advised, at most 256 KiB each, lower case names):

   | File | Replaces |
   | --- | --- |
   | `apps/<appId>.png` | one app, for example `apps/org.hackeros.terminal.png` |
   | `categories/<category>.png` | the apps of a category (`developer`, `graphics`, `internet`, `system`, `utilities`) |
   | `files/folder.png` | every folder |
   | `files/<extension>.png` | the files of one extension, for example `files/txt.png` |
   | `files/file.png` | the other files |

4. Open Settings ▸ Appearance and pick the pack.

The sample pack `/usr/share/icons/neon/` is a model: `cp -r
/usr/share/icons/neon ~/.local/share/icons/my-pack` then change the `id`.

Rules: PNG only (no SVG, which can hold script), at most 8 MiB and 1,024 images
per pack, an `id` already taken by another pack is refused. Refusals go to the
system log (`icons: …`) and Settings shows how many there are.

## Architecture

| Part | Project | Role |
| --- | --- | --- |
| `IconPackLibrary`, `IconPack`, `IconPackInfo`, `IconPackFileAccess` | `HackerOs.Theming.Abstractions` | Reads the folders through the caller's file access, validation, `data:` URLs, lookup rules |
| `IconPackSettingsDocuments` | `HackerOs.Platform.Core` | `icons` document (`iconPackId`, `builtin` by default) declared in `OsSettingsDocumentRegistry` |
| `IconPackService` | `HackerOs.Platform.Core` | The shell's selected pack, re-read when `icons.config` or the session changes, `Changed` event |
| `BuiltInIconPacks`, `FileSystemSeeder` | `HackerOs.Platform.Core` | Embedded "Néon" pack, copied to `/usr/share/icons/neon/` in the background after the login (`InstallIconPacksAsync`, started once per boot by `LocalSessionService`). Like dpkg's `.list` files, `/var/lib/hackeros/installed/icon-packs.list` records the installed files: a boot whose list matches skips the install, and an install cut by a reload completes its empty files at the next boot |
| `ShellAppIcon` | `HackerOs.Platform.Blazor` | App icon of the Start menu and the mobile home |
| `WindowLaunchCoordinator.IconFor`, `SetWindowIconCommand` | `HackerOs.Platform.Blazor`, `HackerOs.Windowing.*` | Window and taskbar icon, changed live |
| `ExplorerFileView` (`IconPack` part), `IconPackShellIconProvider` | File Explorer | Files, folders and shortcuts, read with the host app's rights through the public SDK |
| `IconPackPanel` | Settings | Pack list, preview, writes the chosen layer |

## Key decisions

- A pack is a folder, not an archive: it is made with `mkdir`, `cp` and Hack
  Paint. The theme packs of ADR 0190 bring their `icons/` folder.
- `icons` document apart from `appearance.config`, so the theme and the icons
  are not mixed (and the `appearance` schema does not change).
- The interface symbols are not part of a pack (Q2).

## Task list

See the tasks of [ADR 0203](adr/0203-icon-packs.md#tasks).
