> [Version française](../../../../fr/wasm/dev/adr/0232-illustrated-app-icons-everywhere.md)

# ADR 0232: illustrated app icons, shown everywhere

## Status

Accepted on 2026-11-07; Benoit took the four recommendations (thread
"Améliorer les icônes des applications", see "Questions asked to Benoit").
Series `icons/base`.

- **Replaces:** the last point of the decision of [ADR 0164](0164-app-icons-and-window-icon-context-menu.md)
  ("The launcher and the shortcuts keep their existing Lucide selection") and
  the rule of the first two rows of the §4 table of [ADR 0203](0203-icon-packs.md)
  (Start menu and app shortcuts).
- **Does not change:** the window and taskbar icon (ADR 0164), the window icon
  menu, the icon pack format and its priority for a named app (`apps/<id>`),
  the file and folder icons.

## Context

- The fifteen windowed system apps declare `icons/app.svg` (ADR 0164, then
  the Kazo OS Store app): a flat coloured tile and a white Lucide glyph. The
  drawing is fine, but says little about the app, and looks like the small
  symbols of the interface.
- That icon is only seen in the title bar, the taskbar and the mobile home. The
  Start menu, the desktop shortcuts and the explorer draw
  `KazoAppIcons.LucideName`, a Lucide icon chosen **by category**: six apps of
  the `system` category (Kazo OS Store, Theme Editor, System Monitor,
  Diagnostic, Error Log Viewer, Web Server Manager) share the same icon, and the
  apps with no category (Calculator, Terminal, Text Editor, Icon Viewer) get the
  generic `app-window`.
- The published third-party apps (`Os-App-*` repositories) already have an
  illustrated icon: a dark gradient tile and a small colour drawing. Yet an app
  installed from the Kazo OS Store does not show its icon in the Start menu.
- A sheet of three styles (neon, modern gradient, illustrated) was shown to
  Benoit on four apps.

## Decision

### 1. "Illustrated" style (Q1: C)

Each windowed system app gets a new `icons/app.svg`:

- `32×32` tile, `rx="7"` corners, a shared dark diagonal gradient (`#26303a`
  to `#0a0e12`), the same as the third-party apps;
- a **colour** drawing of its own that shows what the app does (not a line
  glyph), with two or three hues and a light gradient;
- readable at 16 and 20 px: solid shapes, no detail under 1 unit, enough
  contrast on the dark tile;
- standalone SVG: no font, no external image, no script; gradient ids stay
  local to the file (the icon is loaded through `<img>`).

| App | Drawing |
| --- | --- |
| Browser | blue globe with continents and an orbit ring |
| Calculator | calculator with a green screen and coloured keys |
| Code Editor | code window with coloured `</>` chevrons |
| Diagnostic | microchip under a magnifying glass |
| Error Log Viewer | log page with a red warning triangle |
| File Explorer | two-tone yellow folder with a sheet |
| Icon Viewer | grid of four coloured shapes |
| Kazo Paint | painter's palette with a brush |
| Settings | metal gear and a small orange gear |
| Kazo OS Store | teal shopping bag with a star |
| System Monitor | screen with a green activity curve |
| Terminal | console with window buttons and a green prompt |
| Text Editor | page with text lines and a pencil |
| Theme Editor | fan of colour swatches |
| Web Server Manager | server rack with LEDs and a globe |

### 2. The app's own icon everywhere (Q2: A)

The Start menu, the app shortcuts (desktop, folders, dialogs) and the mobile
home and drawer draw the same icon as the window, in this order:

1. the icon pack's image for the app (`apps/<id>`);
2. else the image the app declares (first of `presentation.iconAssetPaths`);
3. else the pack's image for its category (`categories/<category>`);
4. else the Lucide icon of its category (`KazoAppIcons.LucideName`).

That is already the rule of the window, the taskbar and the mobile home (ADR
0203 §4, third row), extended to the other surfaces. A pack that draws an app
always replaces it; a pack that only draws categories no longer hides an app's
own icon.

### 3. An icon URL in the SDK

The explorer and the desktop only use the public SDK. `KazoAppInfo` gets the
`IconUrl` property (`string?`, `init`): the relative URL of the declared icon
(`_content/<assembly>/<path>`, escaped segments), or `null`. The computation is
the one of `StaticWebAssetAppAssetSource.RelativeUrl` (ADR 0164), moved into the
SDK (`KazoAppAssets.RelativeUrl`) so there is a single formula. An installed app
already serves its files at that address (ADR 0206, 0221). The addition is
compatible: `Icon` (the package-relative path) does not change.

### 4. Scope (Q3: A)

Only the system apps of this repository get new drawings. The apps of the
`Os-App-*` repositories keep their icon; they benefit from point 2 with no
change on their side. Commands and services, which have no icon, keep the
Lucide icon of their category.

### 5. Licence (Q4: A)

The drawings are made for the project, with no third-party icon library, and
follow the repository's licence. Each file starts with a comment that says so.
Lucide glyphs (ISC) are no longer used in app icons; they remain those of the
interface and of the fallback of point 2.

## Questions asked to Benoit

Asked on 2026-11-06 with a comparison sheet; answered on 2026-11-07: "Je suis
d'accord avec tes propositions" (I agree with your proposals).

1. **Style**: A neon hacker; B modern gradient; **C illustrated**
   (recommended, consistent with the third-party apps). Choice: C.
2. **Own icon everywhere**: **A yes, everywhere, packs keep priority**
   (recommended); B desktop and explorer only; C no. Choice: A.
3. **Apps concerned**: **A system apps only** (recommended); B also a touch-up
   of Webamp; C also Chess. Choice: A.
4. **Licence**: **A drawings made for the project** (recommended); B an
   existing MIT or ISC icon set. Choice: A.

## Consequences

- Apps are recognised at a glance in the Start menu and on the desktop, and a
  third-party app finally shows its icon there.
- The Start menu loads a small SVG image per app instead of an embedded glyph;
  the files are under 3 KB and stay in the browser cache.
- An app that declares no icon keeps the Lucide icon of its category.

## Verification

- Unit tests: `KazoAppAssets.RelativeUrl`, `KazoAppInfo.IconUrl`, the order app
  pack, declared image, category pack, Lucide.
- Manifest generation: the fifteen `icons/app.svg` stay declared assets with
  their hash.
- A rendering sheet of the icons at 64, 32, 20 and 16 px attached to the PR.

## Tasks

- [x] ADR (series `icons/base`, work item `icons/1-adr`).
- [ ] Redraw the system apps' `icons/app.svg` (work item `icons/2-system-apps`).
- [ ] `KazoAppAssets.RelativeUrl` and `KazoAppInfo.IconUrl` in the SDK.
- [ ] Start menu, mobile home and shortcuts: order of point 2.
- [ ] Tests, docs (`window-model.md`, `desktop-api.md`, `sdk-reference.md`),
      `features.md`, change entry.
