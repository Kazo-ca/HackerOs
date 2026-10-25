> [Version française](../../../../fr/wasm/dev/adr/0112-accessibility-implementation-adjustments.md)

# ADR 0112: implementation adjustments for accessibility

## Status

Accepted on 2026-10-17 (work item `v3/13-accessibility`).

- **Complements:** [ADR 0105](0105-accessibility-preferences.md). Clarifies its §1, §2, §3 and §4;
  changes none of its decisions.
- Number: `0112`, reserved for this work item.

## Context

The implementation of ADR 0105 ran into six points that the ADR does not settle.

## Decision

1. **Text size through the root font.** `ThemeScope` is a `display: contents` element
   in the middle of the page, whereas a `rem` size is measured on `<html>`. `themes.css`
   therefore sets the root font with `:root:has(.theme-scope[data-text-scale="200"])`
   (`font-size: 200%`), without JavaScript. The 164 text sizes in `px` of the stylesheets of the
   platform, the system apps and the SDK libraries were converted to `rem` (value ÷ 16,
   identical at 100%). Frames, borders and margins in `px` do not change: only text
   grows. The samples (`Apps/Samples`) and web pages keep their sizes.
   `--hos-text-scale` (1 to 2) remains exposed for components that size things by script.
2. **High contrast `!important`, previews included.** The tokens of a user theme are
   set as an inline style on the theme root (ADR 0103); only an `!important`
   declaration from a stylesheet takes precedence. The layer also applies to nested
   `[data-theme]` elements (Settings previews, Theme Editor): in high contrast, the
   previews show the layer, as under Windows high contrast.
3. **Reduced motion beyond tokens.** `data-motion="reduced"` already set the transition tokens to 0.
   Some components declare hard-coded durations ("busy" animation of Hack
   Paint, window opening): a global rule brings them down to 0.01 ms, which keeps the
   `animationend`/`transitionend` events that some code depends on. `ThemeScope` receives
   `AnimationsEnabled = !ReducedMotion`: `animationsEnabled = false` and `reducedMotion = on`
   give the same rendering.
4. **Settings writes the user layer only.** The Accessibility page writes
   `~/.config/hackeros/accessibility.config`. The machine default (`/etc`, also read by
   the login screen) is edited with `sudo nano`, like `/etc/locale.conf` for the language.
   "Reduce animations" there is a three-value choice (Auto, On, Off) like
   contrast. Choosing Auto or Off also sets `animationsEnabled = true` again in the user
   layer of `appearance.config`, otherwise the old switch would keep animations
   off; an administrator lock remains, and the page reports it.
5. **Host preferences through media queries.** `auto` follows `prefers-contrast: more`,
   `forced-colors: active` and `prefers-reduced-motion: reduce`, read by a small module
   (`hostAccessibilityProbe.js`, Platform.Blazor) that decides nothing; the resolution is in C#
   (`AccessibilitySettingsDocuments.Resolve`). A host without a browser answers "nothing requested".
6. **Processes without an interface.** `IAppExecutionContext.Accessibility` has a default
   member that answers the default values (`DefaultAppAccessibilityGateway`). A Web
   Worker process (ADR 0070) has no service and receives it: it draws nothing. Reading the preferences
   requires no capability.

## Consequences

- An app that draws by itself reads `HackAccessibility`. The Terminal (xterm.js), the Code Editor
  (Monaco) and Hack Paint do so; their JavaScript bundles are rebuilt (`npm run build`),
  the outputs remain identical to the source.
- A component that writes a text size in `px` no longer follows the text size: the
  new stylesheets use `rem`.
- The tests of ADR 0105 §4 are in `AccessibilityPreferencesTests` (UI.E2E); see
  [`accessibility.md`](../accessibility.md).
