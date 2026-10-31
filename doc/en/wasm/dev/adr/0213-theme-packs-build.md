> [Version française](../../../../fr/wasm/dev/adr/0213-theme-packs-build.md)

# ADR 0213: `.hacktheme` theme packs and the Hack Store Themes tab, implementation

## Status

Accepted on 2026-10-31, work item `todo/18-theme-packs` (`TODO` issue
[#253](https://github.com/Kazo-ca/HackerOs/issues/253), Hack Store v2).
Implements [ADR 0190](0190-theme-packs.md) §1 to §3, on the Store of
[ADR 0212](0212-hack-store-app-build.md) and the trust rules of
[ADR 0189](0189-app-sources-trust-and-per-user-install.md) and
[ADR 0208](0208-hack-store-sources-trust-build.md). Builds on the systems of
ADRs [0202](0202-desktop-wallpaper-images.md) (wallpapers),
[0203](0203-icon-packs.md) (icons), [0204](0204-os-event-sounds.md) (sounds)
and [0205](0205-cursor-themes.md) (cursors).

- **Refines:** ADR 0190 §1 (exact shape of the `icons/`, `sounds/` and
  `cursors/` folders, which reuse the formats of ADRs 0203 to 0205).
- **Supersedes:** ADR 0205 §6 (the install does **not copy** the `cursors/`
  folder to `/etc/hackeros/cursors/` or `~/.config/hackeros/cursors/`: it is
  read where the pack is unpacked, §3 below).

Four questions were asked to Benoit (Q1 to Q4 below); the recommendations are
applied while waiting for his answers.

## Context

ADR 0190 describes the theme pack and decides it is installed for oneself
without an administrator, or for the system with the two-step approval. The
four systems it asked for exist since the `todo/base` series: wallpapers
(0202), icon packs (0203), sounds (0204) and cursors (0205). Wallpapers and
icons already read `/usr/share/hackeros/theme-packs/<pack>/` and
`~/.local/share/hackeros/theme-packs/<pack>/`. What was left: the exact shape
of the archive, its checks, how it is written to disk, how themes, sounds and
cursors read it, and the Store tab.

## Decision

### 1. The `.hacktheme` format

A zip archive of at most 30 MiB and at most 2,000 files:

```text
hackeros-theme-pack.json      schemaVersion 1, id, version, name, author?, description?,
                              files { "path": "sha256" } (every file except itself)
preview.png                   preview, PNG, at most 1 MiB (optional)
themes/<id>.theme             ADR 0103 themes, at most 32, no read error
wallpapers/*.png|jpg|jpeg|webp  ADR 0202 wallpapers, 5 MiB each, at most 32
icons/icon-pack.json          ADR 0203 icon pack
icons/{apps,categories,files}/*.png
sounds/index.theme            ADR 0204 sound theme (Directories=)
sounds/<folder>/<name>.oga|ogg|mp3   1 MiB each
cursors/cursor.theme          ADR 0205 cursor theme
cursors/*.png                 images named by cursor.theme, hotspot checked
```

- The id follows the app rule (`org.example.neon`) and the sound theme rule
  (a pack's sound theme has the pack's id).
- Every file of the archive is listed in `files` with its SHA-256, and every
  listed file exists and matches. An extra file is refused.
- Names are lower case, with no `..` and no absolute path.
- The **type** of every image and sound is read from its first bytes, never
  from its name: a `.png` holding an SVG or a script is refused. No SVG, no
  file of another kind (`.js`, `.css`, `.html`…).
- Text files (`.theme`, `.json`, `index.theme`, `cursor.theme`) are UTF-8 and
  hold no `://`, `url(` or `@import`: no remote address.
- Errors have a `themes.pack.<reason>` code (`too-large`, `no-description`,
  `unlisted-file`, `missing-file`, `hash-mismatch`, `empty`,
  `unexpected-file`, `bad-file`, `file-too-large`, `remote-address`,
  `unsupported`, `bad-description`) that the Store translates.

The code is `ThemePack.Read` in `HackerOs.Platform.Core` (Apps/ThemePacks).
The catalog CI will apply the same rules (Q2).

### 2. In the catalog

A `catalog.json` entry carries `kind: "theme-pack"` (an entry with no `kind`
stays an app). Its versions point to a `.hacktheme` of at most 30 MiB. An
invalid pack id or an unknown kind is skipped with a warning.
`AppCatalogListing.Find(appId)` finds apps only; `Find(id, kind)` picks the
kind. The source of an installed pack is `catalog:<name>/<packId>@<version>`,
as for an app (ADR 0212 §1).

### 3. Unpacked as is, read in place (Q1 A)

- For oneself: `~/.local/share/hackeros/theme-packs/<packId>/`, written as the
  user, without an administrator, from a trusted catalog.
- For the system: `/usr/share/hackeros/theme-packs/<packId>/`, after the
  two-step approval (fresh administrator sign-in, then the approval window,
  ADR 0189 §4), written as the system. An untrusted source always goes through
  both steps; a pack blocked by `[blocked-apps]` (or its category) is only
  installed for the system (ADR 0208).
- The write is atomic: the pack is written to `<root>.partial/<packId>`, the
  old copy is renamed `<packId>.old`, the new one takes its place, and the old
  one is deleted last. An update therefore leaves no file of the old version.
- Every folder holds a `.hackeros-install` record (`key=value`: id, name,
  version, source, parts, installed by) used to list the packs.
- A system install removes the personal copy of the same pack (one copy at a
  time, ADR 0189 §3).
- The catalogs read the pack folders **where they are**, system first, then
  personal:
  - themes: `/etc/hackeros/themes/`, the system `theme-packs/*/themes/`,
    `~/.config/hackeros/themes/`, the user's `theme-packs/*/themes/`
    (`UserThemeLibrary.LoadAsync` takes a function that lists folders);
  - cursors: `/etc/hackeros/cursors/`, the system `theme-packs/*/cursors/`,
    `~/.config/hackeros/cursors/`, then the user's;
  - sounds: the `<packId>` theme is looked up in `/usr/share/sounds/<id>`,
    `/usr/share/hackeros/theme-packs/<id>/sounds`,
    `~/.local/share/sounds/<id>`, then
    `~/.local/share/hackeros/theme-packs/<id>/sounds`;
  - wallpapers and icons: already read by ADRs 0202 and 0203.
- A theme or cursor id that repeats an existing id is refused, as for the
  ADR 0103 files (no shadowing).
- The appearance settings also watch the pack folders: an installed pack
  shows up in Settings with no restart.

### 4. SDK

`HackAppPackages` gains `GetInstalledThemePacksAsync`,
`InstallThemePackAsync(source, scope)` and `RemoveThemePackAsync(packId, scope)`,
with the types `HackInstalledThemePack`, `HackThemePackInstallReport`,
`HackThemePackParts` and `HackCatalogItemKind`, and `HackCatalogApp.Kind`.
The new `IAppManagementGateway` methods have a default implementation that
throws `NotSupportedException`: an older host stays compatible. Everything is
in `PublicAPI.Unshipped.txt` for the next minor. A pack holds no code:
`RestartRequired` is always `false`.

### 5. The Store Themes tab

- A **Themes** tab next to Home, Installed and Updates (`--page=themes` opens
  it directly). The apps no longer show the `theme-pack` entries.
- One card per pack: 16:9 preview (or a colour palette with no preview), name,
  trust, version, size, parts (themes, wallpapers, icons, sounds, cursors) and
  state.
- Buttons: "Install for me", "Install for everyone" (two steps), "Update",
  "Open Appearance" and "Remove". The search filters the packs in the tab.
- When several catalogs list the same pack, the first in the OS's order wins,
  unless the pack was installed from another one.
- An installed pack no catalog lists any more stays visible so it can be
  removed.

### 6. After the install (Q3 A) and updates (Q4 A)

Installing a pack applies nothing: the Store shows "Installed" and the "Open
Appearance" button opens Settings ▸ Appearance, where the user picks the
pack's theme, wallpaper, icons, sounds or cursors. Pack updates show in the
Themes tab only, not in the apps badge (`AppUpdateMonitor`).

## Consequences

- A pack can be installed as soon as a catalog publishes it; the catalog CI
  still has to check `kind: "theme-pack"` (Q2).
- `AppPackageInstaller` becomes a partial class;
  `AppPackageInstaller.ThemePacks.cs` holds the pack install and
  `AppManagementGateway.ThemePacks.cs` its gateway.
- A theme, cursor or sound of a removed pack leaves the lists; the setting
  that named it falls back to the default, as for a deleted file.

## Questions asked to Benoit

1. **Q1, where a pack's files live.** A (recommended): unpacked as is in
   `theme-packs/<id>/` and read in place by each catalog. B: copy each part
   into the existing folders (`/etc/hackeros/cursors/`…). A keeps a clean
   removal and an atomic update.
2. **Q2, catalog CI.** A (recommended): a `TODO` issue on
   `HackerOs-AppMarketPlace` to check `kind: "theme-pack"`, done once
   `todo/base` is on `main`. B: do it in this work item.
3. **Q3, after the install.** A (recommended): nothing is applied, an "Open
   Appearance" button. B: apply the whole pack at once.
4. **Q4, pack updates in the badge.** A (recommended): no, Themes tab only.
   B: yes, like apps.

## Tasks

- [x] `.hacktheme` format and install checks (`ThemePack`).
- [x] `kind: "theme-pack"` in `catalog.json`.
- [x] Install for oneself and for the system, atomic update, removal,
      `.hackeros-install` record.
- [x] Themes, cursors and sounds read from the pack folders.
- [x] `HackAppPackages` SDK and gateway.
- [x] Hack Store Themes tab.
- [x] Tests: format, gateway, pack folders, Store tab.
- [ ] Catalog CI for `kind: "theme-pack"` (Q2, `TODO` issue on
      `HackerOs-AppMarketPlace`).
