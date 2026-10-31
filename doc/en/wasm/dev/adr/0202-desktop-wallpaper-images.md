> [Version française](../../../../fr/wasm/dev/adr/0202-desktop-wallpaper-images.md)

# ADR 0202: wallpaper images

## Status

Accepted on 2026-10-30. Work item 9 of the Hack Store plan, the OS system that
[ADR 0190](0190-theme-packs.md) §4 asks for before the Store accepts wallpapers
in a pack. Issue [#254](https://github.com/Kazo-ca/HackerOs/issues/254),
`todo/base` series (branch `todo/6-wallpaper-images`).

- **Completes:** [ADR 0044](0044-settings-as-files.md) §8 (layered
  `appearance.config`), [ADR 0103](0103-hack-paint-image-files-and-theme-editor.md)
  ("data only" themes), [ADR 0178](0178-extended-screens-in-display-surfaces.md)
  (each screen's desktop), [ADR 0190](0190-theme-packs.md) §1 to §3 (the packs'
  `wallpapers/` folder and the "data only" rules).
- **Does not change:** the themes and their `--hos-desktop-background` token,
  which stay the background when no image is chosen.

## Context

- The desktop background comes only from the theme: the
  `--hos-desktop-background` token (a color or gradient) and a decorative grid.
- ADR 0190 plans packs with a `wallpapers/*.png|*.jpg|*.webp` folder, no SVG,
  with a maximum size per image.
- The extended desktop (ADR 0178) gives every screen its own `DesktopArea`.

## Decision

### 1. The settings, in `appearance.config`

Three more keys in the `appearance` document (user over system, lockable like
the others):

| Key | Value | Default |
| --- | --- | --- |
| `wallpaper` | empty (the theme's background), `builtin:<id>` or the absolute path of an image | empty |
| `wallpaperFit` | `fill`, `fit`, `stretch`, `center` or `tile` | `fill` |
| `wallpaperScreen2` to `wallpaperScreen9` | same form as `wallpaper`; empty = same as screen 1 | empty |

Screen 1 and every screen without its own value use `wallpaper`. The limit of 9
screens is the one of `screens.config` (ADR 0180).

### 2. Where images come from

- **Shipped with the OS**: 4 WebP images (`hacker-grid`, `circuit`, `aurora`,
  `nebula`) in `HackerOs.Theming.Blazor/wwwroot/wallpapers/`, served as static
  files (`_content/HackerOs.Theming.Blazor/wallpapers/`). They take no room in
  the user's storage, as the built-in themes live in code.
- **System**: `/usr/share/hackeros/wallpapers/`, placed by an administrator,
  offered to everyone.
- **User**: `~/.local/share/hackeros/wallpapers/`.
- **Installed packs**: the `wallpapers/` folder of every pack of
  `/usr/share/hackeros/theme-packs/` and `~/.local/share/hackeros/theme-packs/`
  (ADR 0190 §2). The folder is read as soon as it exists; installing packs is
  still issue #253.
- **Any file** picked with "Browse…": its path is kept as it is (Q2). If it is
  moved or deleted, the desktop goes back to the theme's background.

### 3. "Data only" rules

- PNG, JPEG or WebP only. The type is read from the **first bytes** of the
  file, never from its name: a `.png` holding an SVG is refused.
- 16 MB at most per image (`WallpaperCatalog.MaxImageBytes`). The 5 MB limit
  per pack wallpaper stays the catalog CI's (ADR 0190 §3).
- No remote address: a value is empty, `builtin:<id>` or a path of the virtual
  filesystem.
- `wallpaperFit`: `fill` covers the screen (cropping), `fit` shows the whole
  image (the theme's background around it), `stretch` distorts, `center` keeps
  the real size in the middle, `tile` repeats the image.

### 4. Display by the shell

- `WallpaperService` (Platform Core) follows `appearance.config` like
  `ThemePreferenceService`, and reads everything again when the session changes.
- It reads a file **as the session user**: the file's permissions decide, a
  user cannot show a file they could not open. A missing, unreadable, too large
  or non-image file leaves the theme's background, with one warning in the log
  (once per file).
- `DesktopWallpaper` (Platform Blazor) draws a screen's image in the new
  `DesktopArea.WallpaperContent` parameter, over the theme's background and
  grid, under the icons and the windows. A file's bytes become a `blob:`
  address (`DesktopWallpaper.razor.js`), released when the image changes.
  Screen 1 and every secondary screen have their own.

### 5. Settings ▸ Appearance ▸ Wallpaper

- A grid of thumbnails: "Theme background", the shipped images, then the
  images of the system, user and pack folders, and the files already chosen
  elsewhere (60 at most).
- "Browse…" opens the file-open dialog (PNG, JPEG, WebP).
- A "Fit" list writes `wallpaperFit`.
- With several screens, a "Screen 1 and the others" / "Screen N" choice; for a
  secondary screen, "Same as screen 1" clears its own value.
- The page's "For me / Default for everyone" choice applies to the wallpaper
  too (the system layer asks an administrator, ADR 0042).
- The panel uses only the public SDK (`HackSettings`, `HackDirectory`,
  `HackFile`, `HackScreens`), like the rest of Settings.

### 6. Outside this work item

- **Mobile** (Q1): the mobile home screen keeps its theme's background.
  `TODO` issue [#297](https://github.com/Kazo-ca/HackerOs/issues/297).
- **"Set as wallpaper"** in the File Explorer's context menu of images (Q3):
  `TODO` issue [#298](https://github.com/Kazo-ca/HackerOs/issues/298).

## Consequences

- The Store can accept the `wallpapers/` part of a pack as soon as packs
  install (#253): the folders are already read.
- `HackerOs.Theming.Abstractions` gains `WallpaperCatalog`, `WallpaperFit` and
  `BuiltInWallpaper`; `HackerOs.Windowing.Blazor` gains
  `DesktopArea.WallpaperContent`. No change to the apps' SDK.
- A 16 MB image stays in memory while a screen shows it (one copy per file,
  shared between screens).

## Questions asked to Benoit

Asked on 2026-10-30 in the "Ajouter des fonds d'écran en image" thread, with
the recommendation applied while waiting for his answer:

- **Q1** Mobile: A (recommended) desktop only, `TODO` issue for mobile; B the
  mobile home screen too.
- **Q2** Image picked outside the library: A (recommended) keep its path; B
  copy it into `~/.local/share/hackeros/wallpapers/`.
- **Q3** "Set as wallpaper" in the File Explorer: A in this work item; B
  (recommended) separate `TODO` issue.
- **Q4** Wallpapers shipped with the OS: A (recommended) 4 WebP images; B none.

**Answer (2026-10-31)**: Benoit accepts the four recommendations (Q1 A, Q2 A,
Q3 B, Q4 A). What is built does not change; mobile and the "Set as wallpaper"
menu stay in issues #297 and #298.

## Tasks

- [x] `WallpaperCatalog`, `WallpaperFit`, `BuiltInWallpaper` (Theming Abstractions).
- [x] `wallpaper`, `wallpaperFit`, `wallpaperScreen2..9` keys in `appearance.config`.
- [x] `WallpaperService`: read as the user, type read from the bytes, size limit.
- [x] `DesktopWallpaper` and `DesktopArea.WallpaperContent`, for every screen.
- [x] Wallpaper panel in Settings ▸ Appearance.
- [x] 4 shipped wallpapers (WebP).
- [x] Tests: catalog, schema, service, panel.
- [x] Mobile (#297): [ADR 0217](0217-mobile-home-wallpaper.md).
- [ ] "Set as wallpaper" in the File Explorer (#298).
