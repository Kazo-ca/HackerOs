> [Version française](../../../../fr/wasm/dev/adr/0205-cursor-themes.md)

# ADR 0205: cursor themes

## Status

Accepted on 2026-10-30 (series `todo/base`, work item `todo/9-cursor-themes`,
issue [#257](https://github.com/Kazo-ca/HackerOs/issues/257)). This is the
"new OS system, with its own ADR" that §4 of
[ADR 0190](0190-theme-packs.md) asks for before the Hack Store accepts the
`cursors/` folder of a pack.

- **Complements:** [ADR 0190](0190-theme-packs.md) (theme packs, "data only"
  rules); [ADR 0103](0103-hack-paint-image-files-and-theme-editor.md) (themes
  as files in `/etc` and `~`, no masking);
  [ADR 0044](0044-settings-as-files.md) (layered settings files).
- **Questions asked to Benoit** (end of the document): the theme of a new
  installation and the reach of the theme inside apps. Benoit chose the
  recommendations on 2026-10-31 (System; everywhere): nothing changes.

## Context

- The OS pointer is the browser's. Each surface asks for a cursor by its CSS
  name (`cursor: pointer`, `cursor: ew-resize`…) in its `.razor.css` files, as
  an app of a real OS asks for a cursor by its name (`left_ptr`, `hand2`…) and
  lets the cursor theme pick the image.
- Nothing let the user choose cursors; issue #257 asks for PNG cursors with
  their hot spot, applied to the OS surface, chosen in Settings ▸ Appearance.

## Decision

### 1. A cursor theme is a folder

Like an X11 cursor theme (`~/.icons/<theme>/cursors/`), a theme is a
**folder** holding a `cursor.theme` index and its PNG images:

```ini
[cursor-theme]
id = neon-crypt
name = Neon Crypt

[cursors]
default = arrow.png 2 1
pointer = hand.png 11 1
text = ibeam.png 16 16
```

- A `[cursors]` line gives a **cursor name** (the CSS `cursor` keywords, listed
  in `CursorNames.All`), the folder's **PNG file** and the **hot spot** (column
  and row of the pixel that points).
- A missing name follows a **fallback**, like the symbolic links of an X11
  theme: `n-resize`, `s-resize` and `row-resize` take `ns-resize`,
  `col-resize` takes `ew-resize`, `grabbing` takes `grab`, `progress` takes
  `wait`, `all-scroll` takes `move`, and so on. With neither image nor fallback
  (for example `copy`), the device's cursor stays: an arrow would lose the
  meaning.
- Tolerant reading (ADR 0044 §5): a bad line or a refused image is ignored with
  a warning; only a missing or invalid identity (`id`, `name`) refuses the
  theme.
- "Data only" rules (ADR 0190 §3): PNG only, checked by its signature and its
  `IHDR` header without decoding it; at most **64 KiB** and **128 × 128
  pixels** (browsers ignore a larger cursor); hot spot inside the image; plain
  file name of the folder (no path, no `..`, no remote address, no SVG). A PNG
  read from the files becomes a `data:image/png;base64,` URL.
- Places: `/etc/hackeros/cursors/<folder>/` (administrator, for everyone) and
  `~/.config/hackeros/cursors/<folder>/` (the user). An `id` that repeats a
  built-in theme or an `/etc` theme is refused (no masking, as in ADR 0103).

### 2. Built-in themes

- **System** (`system`): no image, the device's cursors. This is the default
  (question 1).
- **HackerOS Neon** (`hackeros-neon`) and **Gothic Crimson**
  (`gothic-crimson`): 15 PNG cursors of 32 × 32 each (arrow, hand, I-beam,
  hourglass, busy arrow, help, crosshair, move, not allowed, open and closed
  hand, four double arrows), shipped by `HackerOs.Theming.Blazor`
  (`wwwroot/cursors/<id>/`) and drawn by `build/tools/generate-cursor-themes.py`.
  Their hot spots are declared in `CursorThemeCatalog`; a test checks every
  file.

### 3. The choice is a settings file

New layered document **`cursors.config`**, key `theme` (open string, default
`system`): `/etc/hackeros/cursors.config` is the machine's choice (also used by
the sign-in screen) and `~/.config/hackeros/cursors.config` the user's, like
`XCURSOR_THEME`. An `id` that names no theme (deleted folder) gives the
device's cursors. The document is open to apps through
`HackSettings.OpenSystemAsync` (registry of ADR 0056 §8) and shipped commented
in a new installation.

### 4. Applied to the whole surface

- `CursorThemeService` (Platform Core) reads `cursors.config`, resolves the
  `id` against the built-in themes and the folders (read with the system's
  rights for `/etc` and the user's for `~`), and reads again when the file or
  the session changes.
- `CursorThemeSurface` (`HackerOs.Theming.Blazor`), rendered once by
  `App.razor`, passes the images to the library's `cursorTheme.js` module,
  which:
  1. rewrites every same-origin CSS rule whose `cursor` is a name of the theme
     (`cursor: pointer` becomes `cursor: url(…) 11 1, pointer`), including imported sheets (`@import`), `@media` and `@layer`, keeping `!important`, and puts it back when
     the theme changes;
  2. adds a small `<style>` element for what no rule names: the document's
     arrow, links and text fields;
  3. watches the sheets added later (apps loaded on demand).
- Apps have nothing to change (question 2): they ask for a cursor name, the OS
  supplies the image. The device's cursors stay for: sheets of another origin,
  inline `style` attributes and `<iframe>`s (sites of the built-in browser).
- The CSS name stays as the fallback in every value: if an image does not load,
  the browser shows the device's cursor.
- On mobile there is no pointer: the setting changes nothing.

### 5. Settings ▸ Appearance ▸ Cursors

A row of choices (`role="radiogroup"`) shows each theme with a preview strip
(arrow, hand, I-beam, hourglass, move, resize) on a checkerboard; folder themes
carry their origin (`/etc` or `~`) and the reading warnings are listed under the
choices. The choice is written to the user layer; an administrator's lock
(`[locked]` in `/etc/hackeros/cursors.config`) disables the row. The panel reads
the folders with the public SDK (`HackDirectory`, `HackFile`), like the rest of
Settings (ADR 0055).

### 6. Hack Store packs

The `cursors/` folder of a `.hacktheme` pack (ADR 0190 §1) will have the shape
of §1: a theme folder with its `cursor.theme`. Its installation (work item #253)
copies the folder to `/etc/hackeros/cursors/` or `~/.config/hackeros/cursors/`
and reuses the checks of `CursorThemeFile`.

## Consequences

- New public types in `HackerOs.Theming.Abstractions` (`CursorNames`,
  `CursorImage`, `CursorTheme`, `CursorThemeCatalog`, `CursorThemeFile`,
  `CursorThemeLibrary`) and `HackerOs.Theming.Blazor` (`CursorThemeSurface`),
  in `PublicAPI.Unshipped.txt` for the next minor version.
- A theme copied into a folder shows in Settings the next time the page opens
  and applies when chosen; changing the images of the current theme needs
  choosing it again or opening the session again.
- Ordinary text (paragraphs) shows the theme's arrow instead of the device's
  I-beam, since the arrow is set on the document; input fields keep the theme's
  I-beam.

## Questions asked to Benoit

1. **Theme of a new installation**: A) System (recommended, keeps the size and
   accessibility set on the device); B) HackerOS Neon; C) Gothic Crimson.
2. **Reach**: A) everywhere, apps included (recommended, like a real OS, no app
   to change); B) only the OS interface (desktop, taskbar, window frames),
   marking every surface.

**Benoit's answers (2026-10-31):** 1 A, System; 2 A, everywhere, apps
included. This is what the code already does.

## Tasks

- [x] `cursor.theme` format, PNG checks, `/etc` and `~` library.
- [x] Two drawn built-in themes and their script.
- [x] `cursors.config`, `CursorThemeService`, applied by `CursorThemeSurface`
      and `cursorTheme.js`.
- [x] Settings ▸ Appearance ▸ Cursors.
- [x] Tests: format, service, panel, and an end-to-end test in Chromium.
- [x] Docs (this document, [`cursor-themes.md`](../../framework/cursor-themes.md)),
      `features.md`, change list entry.
