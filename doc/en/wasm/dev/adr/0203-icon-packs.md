> [Version française](../../../../fr/wasm/dev/adr/0203-icon-packs.md)

# ADR 0203: icon packs

## Status

Accepted on 2026-10-30. Work item 9 of the Hack Store plan, issue
[#255](https://github.com/Kazo-ca/HackerOs/issues/255), `todo/base` series
(branch `todo/7-icon-packs`). Questions Q1 to Q4 were asked to Benoit in the
"Ajouter les packs d'icônes" thread; the recommendations hold unless he answers
otherwise.

- **Delivers:** [ADR 0190](0190-theme-packs.md) §4, item 2 (icon packs),
  before the Store accepts icons in a theme pack.
- **Complements:** [ADR 0026](0026-icon-library-support.md) (built-in vector
  icons) and [ADR 0164](0164-app-icons-and-window-icon-context-menu.md) (an
  app's declared icon, window and taskbar); their decisions do not change: a
  pack only comes before them.

## Context

- App icons come from two sources: the image the app declares
  (`presentation.iconAssetPaths`, ADR 0164) for the window, the taskbar and the
  mobile home, and a Lucide icon picked by `HackAppIcons` from the id or the
  category for the Start menu and the desktop shortcuts.
- Files and folders are drawn by `IShellIconProvider`
  (`DefaultShellIconProvider`, Lucide icons by extension) in the File Explorer's
  file control.
- ADR 0190 §3 requires code-free packs: PNG, JPEG or WebP, no SVG.

## Decision

### 1. The format of a pack

An icon pack is a **folder** holding `icon-pack.json` and PNG images named by
convention, like a freedesktop icon theme:

```text
icon-pack.json                 {"id","name","version?","author?","description?"}
apps/<appId>.png               one app (apps/org.hackeros.terminal.png)
categories/<category>.png      the apps of a category (categories/developer.png)
files/folder.png               every folder
files/file.png                 a file whose extension has no image
files/<extension>.png          files/txt.png, files/zip.png…
```

- `id`: 1 to 64 characters `a-z 0-9 . - _`, never `builtin`; `name`: at most
  64 characters.
- **PNG only** (signature checked), lower case names. An SVG, a fake PNG or a
  badly named file is refused with a warning; the rest of the pack applies.
- Limits: 256 KiB per image, 8 MiB and 1,024 images per pack, 16 KiB for
  `icon-pack.json`.

### 2. Where packs live (Q1: A)

As on Linux, in this order:

1. `/usr/share/icons/<folder>/` (for everyone, owned by root);
2. `/usr/share/hackeros/theme-packs/<pack>/icons/` (theme packs of ADR 0190);
3. `~/.local/share/icons/<folder>/` (for oneself);
4. `~/.local/share/hackeros/theme-packs/<pack>/icons/`.

A folder without `icon-pack.json` is skipped without a warning. An id already
taken by a pack read before is refused: a pack never hides another one (same
rule as themes, ADR 0103). System folders are read as the system, the user's
with the user's rights.

### 3. Choosing the pack

- New OS settings document **`icons`**: `/etc/hackeros/icons.config` (machine
  default) and `~/.config/hackeros/icons.config` (user), key `iconPackId`,
  `builtin` by default (built-in icons). It is apart from `appearance.config`,
  as the icon theme is apart from the GTK theme.
- An id that names no installed pack keeps the built-in icons.
- **Settings ▸ Appearance ▸ Icon pack** lists the packs with a preview of four
  images and writes the layer chosen by the page's picker ("for me" or "system
  default", which asks an administrator).

### 4. What a pack replaces (Q2: A; Q4: A)

Everywhere, the pack is asked first, then the built-in icon is drawn:

| Surface | Rule |
| --- | --- |
| Start menu | `apps/<id>`, else `categories/<category>`, else Lucide |
| App shortcuts on the desktop and in folders | same |
| Window, taskbar, mobile home and drawer | `apps/<id>`, else the declared image (ADR 0164), else `categories/<category>`, else the generic symbol |
| Files and folders (File Explorer, desktop, dialogs) | `files/folder` for a folder; `files/<ext>`, else `files/file`, else the built-in icon |

- The small symbols of the interface (clock, power, arrows) stay the theme's.
- A change applies at once, without a restart: the shell (`IconPackService`)
  and each file control (`ExplorerFileView`, with the host app's rights, through
  the public SDK) follow `icons.config`. Open windows change icon through the
  window manager's new `SetWindowIconCommand` (event `WindowIconChangedEvent`),
  like an X11 client rewriting `_NET_WM_ICON`.
- Images are served as `data:` URLs: no file is exposed by the server, and the
  OS stays standalone (no host required).
- Editing the images of the pack in use applies at the next selection or
  sign-in.

### 5. The "Néon" sample pack (Q3: A)

The kernel embeds a "Néon" pack (13 system apps, 5 categories, 20 file types;
dark tiles, Lucide glyphs, ISC license, cyan for apps, green for categories,
magenta for files). `FileSystemSeeder` copies it to `/usr/share/icons/neon/`
(root, 0644) when it is missing, never overwriting an administrator's change.
It is there to try the feature and as a model.

## Consequences

- The Store can accept the `icons/` folder of a theme pack: the catalog already
  reads it.
- A well-made pack also replaces third-party app icons by their id.
- No change to the public SDK (`HackerOs.AppSdk`): the new types live in
  `HackerOs.Theming.Abstractions` (`IconPackLibrary`, `IconPack`),
  `HackerOs.Platform.Core` (`IconPackService`, `IconPackSettingsDocuments`,
  `BuiltInIconPacks`), `HackerOs.Platform.Blazor` (`ShellAppIcon`),
  `HackerOs.Windowing.Abstractions` (`SetWindowIconCommand`) and File Explorer
  (`IconPackShellIconProvider`).

## Verification

- `IconPackLibraryTests`: folder order, no shadowing, refused manifests, PNG
  only, `data:` URLs, lookup rules.
- `IconPackServiceTests`: Néon pack installed at boot, choice applied live,
  unknown id, a user's pack read with the user's rights.
- `WindowIconTests`: the command changes the icon and nothing else.
- `IconPackShellIconProviderTests`: pack images, built-in icons for the rest.

## Tasks

- [x] Format, catalog and limits (`IconPackLibrary`).
- [x] `icons` document and `IconPackService`.
- [x] Start menu, mobile home, windows and taskbar (`ShellAppIcon`,
  `SetWindowIconCommand`).
- [x] Files, folders and shortcuts of the file control.
- [x] Settings ▸ Appearance ▸ Icon pack.
- [x] "Néon" pack installed in `/usr/share/icons/neon/`.
- [x] Tests and documentation ([`icon-packs.md`](../icon-packs.md)).
