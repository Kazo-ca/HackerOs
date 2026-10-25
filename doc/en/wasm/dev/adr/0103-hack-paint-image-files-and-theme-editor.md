# ADR 0103: Hack Paint images in OS files and theme editor (user themes as token files)

## Status

Accepted on 2026-10-17 (work item `v3/1-adr`, for the work item `v3/11-apps`).
§2.4 follows Benoit's answer of 2026-10-17: a theme contains only
verified tokens.

- **Complements:** [ADR 0041](0041-shared-theme-catalog-and-scoped-tokens.md)
  (theme catalog, CSS tokens, "data only" themes) for themes
  created by the user, which ADR 0041 did not provide for; ADR 0044
  (settings as files); ADR 0055 (apps on the public SDK).
- **Plan:** [`wasm-v3-completion-plan.md`](../plans/wasm-v3-completion-plan.md),
  line `v3/11-apps`; Benoit's answer 4 (port the theme editor, as a
  system app on the public SDK).

## Context

- Hack Paint (`Apps/System/HackerOs.Apps.HackPaint`) declares the capabilities to
  read and write the home folder and file dialogs, but
  has no Open, no Save, no Export: its JS draws RGBA pixels on a
  canvas.
- The theme editor of `src/` (`src/apps/theme-editor.ts`, 1,510 lines, plus
  help, documentation and previews) saves a JSON in
  `/etc/themes/<id>.json` and injects free CSS (`customCss`), which ADR 0041
  considers dangerous.
- In `wasm2`, one can only choose a theme from the built-in catalog, in
  `/etc/hackeros/appearance.config` and `~/.config/hackeros/appearance.config`
  (keys `desktopThemeId`, `mobileThemeId`, `accent`, `animationsEnabled`).

## Decision

### 1. Hack Paint reads and writes images

- **Open**: PNG, JPEG, BMP, GIF (first frame) and WebP, decoded by the
  browser (`createImageBitmap` in the component's JS module) into RGBA pixels
  handed to C#. The file is read by `HackFile` (C#); the JS never touches the
  file system.
- **Save / Save as**: PNG by default, JPEG as an option (adjustable quality),
  encoded by the canvas (`toBlob`) then written by `HackFile`. The dialogs
  are those of the SDK (`OpenFileAsync`, `SaveFileAsync`).
- Maximum image size: 4,096 × 4,096 pixels, and 16 MiB per file
  (ADR 0094); beyond that, a message says so before decoding.
- Window title `nom.png — Hack Paint`, with `*` when the image is modified,
  and confirmation on close (ADR 0006) if it is not saved.
- Hack Paint declares `FileHandlers` for these types, actions `open` and `edit`;
  it is the default `edit` handler (ADR 0096) for images.
  It also opens through "Open with" and by its name with a path as an
  argument.

### 2. Theme editor

#### 2.1 App

New system app `org.hackeros.theme-editor`
(`Apps/System/HackerOs.Apps.ThemeEditor`), on the public SDK only. It
takes from `src/`: editing by token groups (colors, typography,
spacing, windows, taskbar, animations), live preview, the help
and the token documentation; it does not take the free CSS (§2.4).

#### 2.2 Theme file

- A theme is a text file in the settings format (ADR 0044), extension
  **`.theme`** (like freedesktop's `index.theme`):

  ```ini
  [theme]
  id = neon-crypt
  name = Neon Crypt
  base = hackeros
  platforms = desktop, mobile

  [tokens]
  --hos-bg-base = #0b0f0b
  --hos-accent = #39ff14
  --hos-font-mono = "JetBrains Mono", monospace
  ```

- `base` designates a catalog theme: a missing token keeps its value.
- Locations, like the themes of a Linux desktop: `~/.config/hackeros/themes/`
  (the user) and `/etc/hackeros/themes/` (the administrator, for the whole
  machine). The file is the export: you copy it, send it, open it.
- The catalog seen by Settings and by the editor = built-in themes + themes
  from `/etc` + themes from `~`. An identifier in `~` that reuses a built-in
  or `/etc` identifier is rejected (no shadowing).
- `appearance.config` can designate a user theme by its `id`.

#### 2.3 Applying the tokens

- `HackerOs.Theming.Abstractions` publishes the **token list** with their type
  (color, length, duration, font family, number); the editor's help
  is generated from this list.
- On read, each value is checked according to its type; an invalid line
  or an unknown token is ignored with a warning (ADR 0044 §5).
- `ThemeScope` applies a user theme's tokens through
  `style.setProperty` on its root element, via a small JS module of
  `HackerOs.Theming.Blazor` (no `style="…"` in the markup, ADR 0007).

#### 2.4 No free CSS (Benoit's choice)

A theme contains only verified tokens: no free CSS, no remote font or image,
no script. This is the "data only" rule of ADR 0041 §5
extended to user themes. The `customCss` of `src/` is not ported.

### 3. Code Editor

`v3/11` rechecks the points still open from the old list
(`P4-W3-002`, `P4-W3-007`: offline assets, large files, worker cleanup)
against the workshop of ADR 0062 and fixes what is missing; no
new decision.

## Consequences

- `v3/11` modifies Hack Paint, creates the Theme Editor app, the token list, the
  loading of file-based themes and their application, with tests
  (opening and saving an image, invalid theme ignored, user
  theme applied after reload) and axe scans of both apps
  (`v3/13`).
- Settings lists the user themes and opens the editor.
- New doc `theme-editor.md`, updates to `project-overview.md`,
  `settings-files.md` and `sdk-reference.md` (token list).
