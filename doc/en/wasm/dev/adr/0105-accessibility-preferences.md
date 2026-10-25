> [Version française](../../../../fr/wasm/dev/adr/0105-accessibility-preferences.md)

# ADR 0105: accessibility preferences as files (high contrast, text size, reduced motion) and automated tests

## Status

Accepted on 2026-10-17 (work item `v3/1-adr`, for the work item `v3/13-accessibility`).

- **Complements:** [ADR 0041](0041-shared-theme-catalog-and-scoped-tokens.md)
  (theme tokens, `data-motion` attribute of `ThemeScope`), ADR 0044 (settings
  as files), [ADR 0095](0095-window-events-sizes-and-keyboard.md) (window
  keyboard commands).
- **Plan:** [`wasm-v3-completion-plan.md`](../plans/wasm-v3-completion-plan.md),
  line `v3/13-accessibility`; gap R11. The manual accessibility
  checklist is dropped by Benoit: only automated tests.

## Context

- Reduced motion is only handled in CSS (`prefers-reduced-motion` in
  `themes.css` and about ten component stylesheets) and by the
  `animationsEnabled` setting of `appearance.config`. Nothing for contrast or
  text size; no SDK API.
- The axe scans (`AccessibilityAndVisualCoverageTests`) cover the explorer,
  Settings, the Code Editor, Hack Paint and the desktop, not the Terminal, the
  browser, the calculator or the system monitor. No keyboard walkthrough
  is tested.

## Decision

### 1. Preferences

- File `accessibility.config`, layered like any setting:
  `/etc/hackeros/accessibility.config` (machine default) and
  `~/.config/hackeros/accessibility.config` (user). Keys:

  | Key | Values | Default |
  |---|---|---|
  | `highContrast` | `auto`, `on`, `off` | `auto` (follows the host's `prefers-contrast: more` and `forced-colors: active`) |
  | `textScale` | `1.0`, `1.25`, `1.5`, `2.0` | `1.0` |
  | `reducedMotion` | `auto`, `on`, `off` | `auto` (follows the host's `prefers-reduced-motion`) |

- `animationsEnabled` of `appearance.config` is still read: `false` is equivalent to
  `reducedMotion = on`. Settings now displays only one setting, "Reduce
  animations", in a new **Accessibility** page with the other two.
- The login screen uses the preferences from `/etc` (no user
  is known yet).

### 2. Application through the theme

- `ThemeScope` adds `data-contrast="high"` and the variable
  `--hos-text-scale` next to `data-motion`.
- `themes.css` gains a high-contrast layer that redefines the color,
  border and focus tokens for **all** themes (text contrast of
  at least 7:1, 3 px focus indicator), and the platform text sizes
  switch to `rem` multiplied by `--hos-text-scale`.
- The user themes of ADR 0103 cannot disable the high-contrast
  layer: it applies on top.

### 3. SDK

- `HackAccessibility.GetPreferencesAsync()` returns the effective preferences
  (after `auto`) and `HackAccessibility.PreferencesChanged` signals a change.
- An app that draws by itself (Hack Paint's canvas, the Terminal's xterm.js,
  the Code Editor's Monaco) applies them: enlarged terminal and editor
  font, high-contrast theme for Monaco and xterm, animations turned off.

### 4. Automated tests

- Axe scans added for the Terminal, the browser, the calculator, the
  system monitor, Theme Editor, the Settings backup page and the
  Accessibility page; each scan also runs with `highContrast = on`. A
  serious or critical violation fails the test (current rule).
- An E2E keyboard walkthrough test, without a mouse: open the launcher, launch
  an app, switch from one window to another (Alt+`), move a window
  (Alt+F7), close (Alt+Q), with focus always visible.
- A test checks that `textScale = 2.0` does not make the desktop or the
  windows of the system apps overflow (existing overflow rule).

## Consequences

- `v3/13` adds the settings document, the Accessibility page, the
  high-contrast layer, `HackAccessibility`, the adaptation of the three apps that draw
  by themselves and the tests.
- `settings-files.md`, `sdk-reference.md`, `building-and-testing.md` (axe tests)
  and `terminal-emulator.md` are updated.
