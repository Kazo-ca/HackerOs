> [Version française](../../../../fr/wasm/dev/adr/0190-theme-packs.md)

# ADR 0190: theme packs in the Hack Store (v2)

## Status

Accepted on 2026-10-29 by Benoit as the **Hack Store v2 plan** ("Plan the Hack
Store" thread, answers at the end). Nothing is to be coded before Store v1
([ADR 0188](0188-hack-store-and-app-catalog.md)). Complements
[ADR 0041](0041-shared-theme-catalog-and-scoped-tokens.md) (data-only themes)
and [ADR 0103](0103-hack-paint-image-files-and-theme-editor.md) (`.theme`
files).

## Context

- A theme is a `.theme` file of checked tokens, with no free CSS or script
  (ADR 0103 §2.4), read from `/etc/hackeros/themes/` and
  `~/.config/hackeros/themes/`.
- The OS has **no** system today for user-chosen image wallpapers, icon packs,
  sounds or cursors.
- Benoit wants packs that carry themes, wallpapers, icons, sounds and cursors.

## Decision

### 1. The pack package

A **theme pack** is a zip archive with the `.hacktheme` extension:

```text
hackeros-theme-pack.json   identity, version, name, content, SHA-256 of each file
themes/*.theme             themes (ADR 0103)
wallpapers/*.png|*.jpg|*.webp
icons/                     icon pack (PNG only, see §3)
sounds/*.ogg|*.mp3         OS event sounds
cursors/*.png              cursors, with their hotspot in the JSON
preview.png                preview shown by the Store
```

Each part is optional, but a pack contains at least one.

### 2. Publishing and installing

- A pack is published in the catalog like an app (ADR 0188 §4 to §6), with
  `kind: "theme-pack"` in `catalog.json` and a `store.json` that points to the
  pack's repository. The CI checks formats, sizes and dimensions instead of
  building.
- A pack contains **no code**: it installs **for yourself without an
  administrator** from a trusted source (rules of
  [ADR 0189](0189-app-sources-trust-and-per-user-install.md) §3), into
  `~/.local/share/hackeros/theme-packs/<packId>/`; for the system, into
  `/usr/share/hackeros/theme-packs/` with the ADR 0189 §4 validation.
- The theme, wallpaper, icon, sound and cursor catalogs also read the folders
  of installed packs. A pack id that reuses an existing id is refused (no
  shadowing, as in ADR 0103).
- The Store has a **Themes** tab next to apps.

### 3. Data-only rules

- No SVG in a pack (an SVG can contain script): PNG, JPEG or WebP images only.
- Maximum sizes checked at install and by the CI (for example 5 MB per
  wallpaper, 1 MB per sound, 30 MB per pack; exact values set by the work
  item).
- No remote address in the files.

### 4. OS systems to build first

Each part of a pack needs an OS system that does not exist yet. Each is a
separate work item, done before the Store accepts that part:

1. User-chosen image wallpapers (Settings ▸ Appearance).
2. Icon packs (replacing app and file icons, ADR 0026 and 0164).
3. OS event sounds (notification, error, sign-in), with a setting to mute them.
4. Cursor themes.

`.theme` themes already exist: a pack that only holds themes can be accepted as
soon as the Store handles packs.

## Consequences

- Store v2 reuses the whole ADR 0188 pipeline (reviewed folder, CI, `packages`
  branch) for code-free content.
- Four new systems to design; each will have its own ADR.

## Benoit's answers

- **Q10** B: packs with themes, wallpapers, icons, sounds and cursors.
- **Q29** A: the full format is described here; each new OS system is a
  separate work item, done before the Store accepts that part.

## Tasks

- [ ] `.hacktheme` format and checks (catalog CI and install).
- [ ] Per-user and system install, catalogs that read packs.
- [ ] Hack Store Themes tab.
- [ ] Work item: image wallpapers.
- [ ] Work item: icon packs.
- [x] Work item: OS event sounds ([ADR 0204](0204-os-event-sounds.md)).
- [ ] Work item: cursor themes.
