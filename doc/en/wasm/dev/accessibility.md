> [Version française](../../../fr/wasm/dev/accessibility.md)

# Accessibility

Work item `v3/13-accessibility` ([ADR 0105](adr/0105-accessibility-preferences.md), adjusted by
[ADR 0112](adr/0112-accessibility-implementation-adjustments.md)). The former document
[`old/accessibility.md`](old/accessibility.md) is kept for history (phase 2 rules and
manual checklist, dropped by Benoit).

## Purpose

Three preferences, stored as files like every HackerOS setting, applied by the theme to the whole
desktop and read by apps that draw their own content:

- **High contrast**: black surfaces, white text (21:1, and at least 7:1 for the palest text),
  white borders, a 3 px yellow focus outline, on top of all themes.
- **Text size**: 100%, 125%, 150% or 200%.
- **Reduce animations**: transitions, animations, blinking cursors and animated scrolling
  turned off.

## Settings

`accessibility.config`, layered ([`settings-files.md`](../framework/settings-files.md)):
`/etc/hackeros/accessibility.config` (machine default, written as comments on first boot, also
read by the sign-in screen) then `~/.config/hackeros/accessibility.config`.

| Key | Values | Default | `auto` follows |
|---|---|---|---|
| `highContrast` | `auto`, `on`, `off` | `auto` | the host's `prefers-contrast: more` or `forced-colors: active` |
| `textScale` | `1.0`, `1.25`, `1.5`, `2.0` | `1.0` | n/a |
| `reducedMotion` | `auto`, `on`, `off` | `auto` | the host's `prefers-reduced-motion: reduce` |

`animationsEnabled = false` in `appearance.config` counts as `reducedMotion = on`. An invalid value
is ignored (the next layer applies). An administrator can lock a key
(`[locked]`).

```sh
nano ~/.config/hackeros/accessibility.config      # highContrast=on
sudo nano /etc/hackeros/accessibility.config      # machine default
```

**Settings > Accessibility** shows the applied state and writes the user layer: three
groups of radio buttons (contrast, size, animations), each showing where its value comes from. The
"Animations" setting on the Appearance page is gone: "Reduce animations" replaces it
(choosing Auto or Off sets `animationsEnabled = true` for the user).

## Architecture

| Piece | Project | Role |
|---|---|---|
| `AccessibilitySettingsDocuments` | Platform.Core | Document schema, `AccessibilitySettings`, `HostAccessibilitySignals`, `Resolve` (rules for `auto`). |
| `AccessibilityPreferenceService` | Platform.Core | Reads and watches the document, follows `ThemePreferenceService` (for `animationsEnabled`) and the host probe; `Effective` and `Changed`. One per session. |
| `IHostAccessibilityProbe`, `BrowserHostAccessibilityProbe`, `hostAccessibilityProbe.js` | Platform.Core, Platform.Blazor | Host media queries, with no decision logic. |
| `AppAccessibilityGateway` | Platform.Core | An app's view, wired by `AppExecutionContextFactory`. |
| `HackAccessibility`, `HackAccessibilityPreferences`, `IAppAccessibilityGateway` | AppSdk | Public API. |
| `ThemeScope` (`HighContrast`, `TextScale`) | Theming.Blazor | `data-contrast` and `data-text-scale` attributes next to `data-motion`. |
| `themes.css` | Theming.Blazor | High contrast layer, root font, reduced motion rule. |
| `AccessibilityPanel` | Apps.Settings | The Accessibility page. |

The host's `App.razor` initializes the service after appearance and passes to `ThemeScope`
`AnimationsEnabled = !ReducedMotion`, `HighContrast` and `TextScale`.

### In the theme

- **High contrast**: `[data-contrast="high"]` redefines all color, border,
  shadow and focus tokens with `!important`, on the root and on nested `[data-theme]` elements. A
  user theme cannot remove it. `:focus-visible` gets a 3 px yellow outline.
- **Text size**: text sizes are in `rem`;
  `:root:has(.theme-scope[data-text-scale="150"])` sets the root font to 150%. Frames in
  `px` do not move. `--hos-text-scale` holds the factor.
- **Reduced motion**: `data-motion="reduced"` sets transition tokens to 0 and brings every
  animation or transition duration down to 0.01 ms.

New stylesheet: write text sizes in `rem`, never in `px`, and colors with the
`--hos-*` tokens.

### Apps that draw their own content

| App | High contrast | Text size | Reduced motion |
|---|---|---|---|
| Terminal (xterm.js, [`terminal-emulator.md`](terminal-emulator.md)) | 7:1 ANSI palette, black background, yellow cursor and selection | Font re-read from the panel (in `rem`) | Steady cursor |
| Code Editor (Monaco) | `hackeros-high-contrast` theme (base `hc-black`) | Font and line height × factor | Steady cursor, scrolling without animation |
| Hack Paint | Visible frame and grid around the image (the image keeps its colors) | n/a (interface in `rem`) | Still "busy" indicator |

Each one reads `AppContext.Accessibility` on opening and follows `Changed`.

## API

```csharp
using HackerOs.AppSdk.Accessibility;

HackAccessibilityPreferences a11y = await HackAccessibility.GetPreferencesAsync();
int fontSize = a11y.Scale(14);                 // 28 at 200%
bool blink = !a11y.ReducedMotion;
HackAccessibility.PreferencesChanged += (_, _) => { /* re-read and redraw */ };
```

No capability. Without the service (tests, Web Worker processes), the default values apply.

## Tests

- Unit: `AccessibilityPreferenceServiceTests` (Platform.Core: `auto` rules, layers,
  invalid value, host, `animationsEnabled`), `HackAccessibilityTests` (AppSdk),
  `ThemeComponentRenderTests` (`ThemeScope` attributes).
- E2E (`Tests/HackerOs.UI.E2E.Tests/AccessibilityPreferencesTests.cs`, see
  [`building-and-testing.md`](building-and-testing.md)):
  - axe scans of the Terminal, the browser, the calculator, the system monitor, Theme
    Editor, Settings > Backup and Settings > Accessibility, with and without high
    contrast (turned on from the Accessibility page); a serious or critical violation fails;
  - keyboard-only walkthrough: Tab to the launcher, Enter, search, arrow, Enter; twice;
    `` Alt+` ``; `Alt+F7` then arrows and Enter; `Alt+Q`; visible focus at every step;
  - `textScale = 2.0`: root font at 32 px, no horizontal scrollbar and no window
    off screen for the desktop, the explorer, Settings, the Terminal, the calculator, the
    system monitor and the text editor.
- The existing scans (`AccessibilityAndVisualCoverageTests`) remain.
- Defects found by the first scans and fixed: the Terminal's tab bar announced
  a tab list that also contained the "Close" and "+" buttons (it is now a
  button group, the active tab carries `aria-current`); the system monitor's process table
  declared `aria-multiselectable` without being a grid (`role="grid"` added). The
  keyboard walkthrough showed that after `` Alt+` `` keyboard focus stayed on `<body>`:
  the activated window now receives it ([`window-model.md`](../framework/window-model.md)).

## Decisions

See [ADR 0112](adr/0112-accessibility-implementation-adjustments.md): root font through
`:has()`, an `!important` layer that also covers previews, a global reduced motion rule,
a page that only writes the user layer, media queries decided in C#, default values
outside the main thread.

## Tasks

- [x] `accessibility.config` document (schema, default `/etc` file, OS document registry).
- [x] `AccessibilityPreferenceService` and host media query probe.
- [x] `HackAccessibility` in the SDK, gateway wired by the context factory.
- [x] `ThemeScope`: `data-contrast`, `data-text-scale`; effective reduced motion.
- [x] `themes.css`: high contrast layer, root font, reduced motion rule; text sizes in `rem`.
- [x] Settings > Accessibility (English and French); Animations setting removed from Appearance.
- [x] Terminal, Code Editor and Hack Paint follow the preferences.
- [x] Unit tests, axe scans with and without high contrast, keyboard walkthrough, 200% test.
- [x] Documentation, ADR 0112 and index.
