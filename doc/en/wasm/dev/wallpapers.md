> [Version française](../../../fr/wasm/dev/wallpapers.md)

# Wallpaper images

Decisions: [ADR 0202](adr/0202-desktop-wallpaper-images.md) (desktop),
[ADR 0217](adr/0217-mobile-home-wallpaper.md) (mobile home screen) and
[ADR 0218](adr/0218-set-as-wallpaper-file-menu.md) ("Set as wallpaper" in the
menu of images). Issues [#254](https://github.com/Kazo-ca/HackerOs/issues/254),
[#297](https://github.com/Kazo-ca/HackerOs/issues/297) and
[#298](https://github.com/Kazo-ca/HackerOs/issues/298).

## Purpose

The user chooses a PNG, JPEG or WebP image as the desktop background, for every
screen or for one secondary screen, in Settings ▸ Appearance ▸ Wallpaper.
Without an image, the desktop keeps its theme's background
(`--hos-desktop-background` and the grid).

The mobile home screen has its own image and fit (the "Mobile" choice of the
same panel); without an image, it keeps its mobile theme's background
(`--hos-mobile-surface-background`).

## Settings

In `appearance.config` (`/etc/hackeros/` then `~/.config/hackeros/`):

```ini
wallpaper = /home/alice/Pictures/beach.webp
wallpaperFit = fill
wallpaperScreen2 = builtin:nebula
mobileWallpaper = builtin:aurora
mobileWallpaperFit = fill
```

| Key | Value |
| --- | --- |
| `wallpaper` | empty (theme background), `builtin:<id>` or an absolute path; screen 1 and screens without their own value |
| `wallpaperFit` | `fill` (default), `fit`, `stretch`, `center`, `tile` |
| `wallpaperScreen2` to `wallpaperScreen9` | a screen's own value; empty = same as screen 1 |
| `mobileWallpaper` | same shape as `wallpaper`, for the mobile home screen; empty = mobile theme's background |
| `mobileWallpaperFit` | fit on mobile, same values as `wallpaperFit` |

A `nano ~/.config/hackeros/appearance.config` changes the wallpaper on save.

## Image sources

| Source | Location |
| --- | --- |
| Shipped with the OS | `builtin:hacker-grid`, `builtin:circuit`, `builtin:aurora`, `builtin:nebula` (static files `_content/HackerOs.Theming.Blazor/wallpapers/*.webp`) |
| System | `/usr/share/hackeros/wallpapers/` |
| User | `~/.local/share/hackeros/wallpapers/` |
| Installed packs | `/usr/share/hackeros/theme-packs/<pack>/wallpapers/`, `~/.local/share/hackeros/theme-packs/<pack>/wallpapers/` (ADR 0190) |
| Any other file | picked with "Browse…", path kept as it is |

## Architecture

| Element | Project | Role |
| --- | --- | --- |
| `WallpaperCatalog`, `WallpaperFit`, `BuiltInWallpaper` | `HackerOs.Theming.Abstractions` | Shipped images, folders, extensions, type read from the bytes (`TryDetectMediaType`), 16 MB limit, `wallpaperFit` values. |
| `AppearanceSettingsDocuments` | `HackerOs.Platform.Core` | Declares the `wallpaper`, `wallpaperFit`, `wallpaperScreen2..9`, `mobileWallpaper`, `mobileWallpaperFit` keys. |
| `WallpaperService`, `WallpaperPreferences`, `WallpaperImage` | `HackerOs.Platform.Core` | Follows `appearance.config`, reads a file as the session user, refuses what is not an image, keeps one copy per file shown. `GetImageAsync(screen)` for the desktop, `GetMobileImageAsync()` for mobile. |
| `DesktopWallpaper` (`.razor`, `.razor.cs`, `.razor.css`, `.razor.js`) | `HackerOs.Platform.Blazor` | Draws a screen's image; `blob:` address for a file, static address for a shipped image; `fit-*` classes. With `Mobile="true"`, the mobile image; `MobileShell` places it behind the home screen, which turns transparent and shadows its texts. |
| `DesktopArea.WallpaperContent` | `HackerOs.Windowing.Blazor` | Layer between the theme's background and the icons. `DesktopShell` fills it for screen 1 and every secondary screen. |
| `WallpaperPanel` | `HackerOs.Apps.Settings` | Thumbnails, "Browse…", "Fit", screen or "Mobile" choice; public SDK only. |

## "Set as wallpaper" in the file menus

The menu of a PNG, JPEG or WebP image (File Explorer, desktop, file dialogs)
offers "Set as wallpaper" right after "Open with" (ADR 0218):

- with a single open screen, a plain entry: the image goes to `wallpaper`
  (every screen without its own image);
- with several screens, a submenu "Screen 1 and the others", then
  "Screen 2"… for each open secondary screen (`wallpaperScreen<n>`).

The entries belong to `org.hackeros.settings.wallpaper` (`SetWallpaperApp`), a
hidden service declared by the Settings project and run on the main thread.
`SetWallpaperService` reads the file as the session user, checks its bytes and
its size like the shell, writes the user layer of `appearance.config`, posts a
"Wallpaper changed" notification (or the error: locked key, unreadable file,
not an image), then stops. No window opens; the path is kept as it is.

## Points of attention

- The shell never trusts the file name: a file that is not PNG, JPEG or WebP,
  too large, missing or unreadable leaves the theme's background and writes a
  `wallpaper:` warning to the log.
- Image addresses are made absolute in `DesktopWallpaper.razor.js` so a
  secondary screen (another document) loads them too.
- The panel reads at most 60 images for their thumbnails.

## Tests

- `Tests/HackerOs.Platform.Core.Tests/Appearance/WallpaperTests.cs`: catalog
  rules, schema, service (shipped image, file, own screen, mobile, fallback).
- `Tests/HackerOs.Apps.Settings.Tests/WallpaperPanelTests.cs`: panel
  (thumbnails, per-screen or mobile choice, "Browse…", fit, French).
- `Tests/HackerOs.Apps.Settings.Tests/SetWallpaperServiceTests.cs`: menu
  entries, screen of each entry, written key, refused file, locked key,
  notification.
- `Tests/HackerOs.Platform.Blazor.Tests/ContextMenus/FileMenuEntriesTests.cs`:
  screen filter (plain entry or submenu of the open screens).

## Outside this work item

The mobile app drawer stays opaque, except on a theme that sets `--hos-mobile-drawer-background` and `--hos-mobile-drawer-backdrop-filter` (iOS: frosted glass, ADR 0217 §3). "Set as wallpaper" writes the
desktop keys only: the mobile image is chosen in Settings (the SDK does not tell
an app which interface is active).
