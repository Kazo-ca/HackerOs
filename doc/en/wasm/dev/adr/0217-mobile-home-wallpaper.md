> [Version française](../../../../fr/wasm/dev/adr/0217-mobile-home-wallpaper.md)

# ADR 0217: image wallpaper on the mobile home screen

## Status

Accepted on 2026-10-31; Benoit chose the recommendations (see "Questions asked
to Benoit"). Issue
[#297](https://github.com/Kazo-ca/HackerOs/issues/297), `todo/base` series
(branch `todo/23-mobile-wallpaper`).

- **Completes:** [ADR 0202](0202-desktop-wallpaper-images.md) (desktop image
  wallpapers), whose §6 left mobile out of that work item.
- **Does not change:** the "data only" rules of ADR 0202 §3 (PNG, JPEG, WebP
  read from the bytes, 16 MB, no remote address), the image folders of
  ADR 0202 §2 and the desktop keys.

## Context

- ADR 0202 draws an image behind the icons of every desktop screen. The mobile
  interface (`MobileShell`) keeps its theme's background, the
  `--hos-mobile-surface-background` token.
- `appearance.config` already keeps two themes, `desktopThemeId` and
  `mobileThemeId`, so switching form factor loses neither choice.
- Mobile themes (Android, iOS…) make their background part of their style
  (iOS's bright gradients, Android's dark gradient).
- A phone is in portrait: a landscape image chosen for the desktop is heavily
  cropped there.

## Decision

### 1. Two more keys in `appearance.config` (Q1, Q2)

| Key | Value | Default |
| --- | --- | --- |
| `mobileWallpaper` | empty (the mobile theme's background), `builtin:<id>` or the absolute path of an image | empty |
| `mobileWallpaperFit` | `fill`, `fit`, `stretch`, `center` or `tile` | `fill` |

The values have the same shape and rules as `wallpaper` and `wallpaperFit`
(ADR 0202 §1 and §3), in the same layers (user over system, lockable). Empty
keeps the **mobile theme's** background: the desktop choice is not forced on
the phone, as with themes.

### 2. Rendering by the mobile shell

- `WallpaperPreferences` also reads both keys; `WallpaperService` gains
  `GetMobileImageAsync`, which follows the same rules as `GetImageAsync`: read
  **as the session user**, type read from the bytes, size limit, fallback to the
  theme's background with one warning in the log. A file chosen for both the
  desktop and mobile is read once.
- `DesktopWallpaper` gains the `Mobile` parameter: it then shows the
  `mobileWallpaper` image. `MobileShell` places it at the back of its
  workspace, under the home screen, the drawer and the apps.
- While an image is shown, the home screen makes its background transparent and
  adds a shadow to the app names and the heading, so they stay readable on any
  image. The dock keeps its blurred glass.
- Mobile apps fill the screen (`SingleSurfaceArea`): the image changes nothing
  there, it shows on the home screen.

### 3. The app drawer stays opaque (Q3)

The drawer keeps its theme's `--hos-bg-base` background: the image shows on the
home screen and in the strip above the drawer, and the list stays readable on a
bright image.

### 4. Mobile themes

No theme changes: their background is still what shows while `mobileWallpaper`
is empty. A chosen image replaces it on every mobile theme, high contrast
included, as on the desktop (ADR 0202 §4).

### 5. Settings ▸ Appearance ▸ Wallpaper

- The screen choice becomes "Screen 1 and the others", "Screen N"… then
  **"Mobile"**, always shown (even with a single screen).
- With "Mobile", the grid writes `mobileWallpaper` (the first thumbnail is
  "Mobile theme background") and the "Fit" list writes `mobileWallpaperFit`.
- The panel still uses the public SDK only.

## Consequences

- The desktop and mobile each have their own image and fit; a landscape
  desktop image does not crop the phone screen unless asked for.
- The File Explorer's "Set as wallpaper" menu (#298, ADR 0218) can write
  `mobileWallpaper` while the mobile interface is active.
- No change to the app SDK.

## Questions asked to Benoit

Asked on 2026-10-31 in the "Afficher le fond d'écran sur mobile" thread, with
the recommendation applied while his answer is pending:

- **Q1** Setting: A (recommended) `mobileWallpaper` key, empty = mobile theme's
  background; B `mobileWallpaper` key, empty = the desktop image; C the same
  `wallpaper` key as screen 1.
- **Q2** Fit: A (recommended) `mobileWallpaperFit` key; B the desktop's
  `wallpaperFit`.
- **Q3** App drawer: A (recommended) opaque as today; B blurred glass that lets
  the image show.

**Answer (2026-10-31)**: Benoit chooses the three recommendations (Q1 A, Q2 A,
Q3 A). What is built does not change.

## Tasks

- [x] ADR (fr + en) and indexes.
- [x] `mobileWallpaper` and `mobileWallpaperFit` keys in `appearance.config`.
- [x] `WallpaperPreferences` and `WallpaperService.GetMobileImageAsync`.
- [x] `DesktopWallpaper Mobile="true"` in `MobileShell`, transparent and readable home screen.
- [x] "Mobile" choice in Settings ▸ Appearance ▸ Wallpaper.
- [x] Tests: schema, service, panel.
