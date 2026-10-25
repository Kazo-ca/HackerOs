# ADR 0164: app icons and the window icon context menu

## Status

Accepted on 2026-10-22, at Benoit's request. Replaces the menu access by
left click and right click on the whole title bar of ADR 0095 §3.
The keyboard shortcuts of that decision are kept.

## Context

Windowed system apps declared no file in `AppDefinition.Icons`.
The chrome showed a generic menu button and the taskbar a generic icon.
A declared path, relative to the package, was also used directly as an
image URL, without the static web assets prefix of its library.

## Decision

- The first icon of `presentation.iconAssetPaths` is that of the window and
  of its button in the taskbar. `WindowLaunchCoordinator` resolves this path through
  `StaticWebAssetAppAssetSource.RelativeUrl`, under `_content/<assembly>/…`.
  Taskbar, picker and other projections reuse the URL of the same snapshot.
- The fourteen windowed system apps declare `icons/app.svg` and its image asset.
  The SVGs use Lucide glyphs already embedded (ISC license), in a colored
  tile. The generator computes their SHA-256; no schema change nor
  new SDK contract is needed. Third-party apps use the same
  `Icons` and `Assets` properties.
- In all desktop themes except macOS, the title bar shows this
  icon. macOS hides it through an internal CSS layout token
  `--hos-window-icon-display`, but its dock keeps the app's icon.
- A left click on the icon only activates the window. The window commands menu
  opens on right click **on the icon**, and remains accessible through
  `Alt+Space` / `Alt+F3`. The rest of the title bar no longer opens this menu.
- Without a declared icon, the components keep a generic window symbol.
  The menu uses this symbol, without introducing a hamburger button.
- The assets stay in the apps' libraries; no additional host or server
  required. The launcher and shortcuts keep their existing Lucide selection,
  independent of the window images.

## Verification

- `WindowLaunchCoordinatorTests`: resolution of the icon path and URL escaping.
- Manifest generation: assets present and SHA-256 fingerprints computed.
- `WindowAppIconTests` (UI.E2E): two distinct apps, images actually decoded,
  same source in chrome/taskbar, seven themes, macOS exception, left click,
  right click on the title, right click on the icon and keyboard access.

## Tasks

- [x] Declare the icons of windowed system apps and resolve their URLs.
- [x] Display the icon in chrome/taskbar and respect the macOS exception.
- [x] Reserve the menu for right click on the icon and keep keyboard access.
- [x] Add the regression tests and update the relevant documents.
