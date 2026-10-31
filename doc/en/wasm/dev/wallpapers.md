> [Version française](../../../fr/wasm/dev/wallpapers.md)

# Wallpaper images

Decision: [ADR 0202](adr/0202-desktop-wallpaper-images.md). Issue
[#254](https://github.com/Kazo-ca/HackerOs/issues/254).

## Purpose

The user chooses a PNG, JPEG or WebP image as the desktop background, for every
screen or for one secondary screen, in Settings ▸ Appearance ▸ Wallpaper.
Without an image, the desktop keeps its theme's background
(`--hos-desktop-background` and the grid).

## Settings

In `appearance.config` (`/etc/hackeros/` then `~/.config/hackeros/`):

```ini
wallpaper = /home/alice/Pictures/beach.webp
wallpaperFit = fill
wallpaperScreen2 = builtin:nebula
```

| Key | Value |
| --- | --- |
| `wallpaper` | empty (theme background), `builtin:<id>` or an absolute path; screen 1 and screens without their own value |
| `wallpaperFit` | `fill` (default), `fit`, `stretch`, `center`, `tile` |
| `wallpaperScreen2` to `wallpaperScreen9` | a screen's own value; empty = same as screen 1 |

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
| `AppearanceSettingsDocuments` | `HackerOs.Platform.Core` | Declares the `wallpaper`, `wallpaperFit`, `wallpaperScreen2..9` keys. |
| `WallpaperService`, `WallpaperPreferences`, `WallpaperImage` | `HackerOs.Platform.Core` | Follows `appearance.config`, reads a file as the session user, refuses what is not an image, keeps one copy per file shown. |
| `DesktopWallpaper` (`.razor`, `.razor.cs`, `.razor.css`, `.razor.js`) | `HackerOs.Platform.Blazor` | Draws a screen's image; `blob:` address for a file, static address for a shipped image; `fit-*` classes. |
| `DesktopArea.WallpaperContent` | `HackerOs.Windowing.Blazor` | Layer between the theme's background and the icons. `DesktopShell` fills it for screen 1 and every secondary screen. |
| `WallpaperPanel` | `HackerOs.Apps.Settings` | Thumbnails, "Browse…", "Fit", screen choice; public SDK only. |

## Points of attention

- The shell never trusts the file name: a file that is not PNG, JPEG or WebP,
  too large, missing or unreadable leaves the theme's background and writes a
  `wallpaper:` warning to the log.
- Image addresses are made absolute in `DesktopWallpaper.razor.js` so a
  secondary screen (another document) loads them too.
- The panel reads at most 60 images for their thumbnails.

## Tests

- `Tests/HackerOs.Platform.Core.Tests/Appearance/WallpaperTests.cs`: catalog
  rules, schema, service (shipped image, file, own screen, fallback).
- `Tests/HackerOs.Apps.Settings.Tests/WallpaperPanelTests.cs`: panel
  (thumbnails, per-screen choice, "Browse…", fit, French).

## Outside this work item

Mobile wallpaper and "Set as wallpaper" in the File Explorer: `TODO` issues #297 and #298
(ADR 0202 §6).
