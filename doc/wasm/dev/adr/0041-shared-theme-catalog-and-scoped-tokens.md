# ADR 0041: Shared Theme Catalog and Scoped CSS Tokens

## Status

Accepted on 2026-08-19.

## Context

The first WASM appearance implementation stored only an accent and an animation
preference. Its Settings app used JavaScript to write two custom properties to
`document.documentElement`, so a saved preference was not restored until the
Settings window had been opened. Window chrome, the taskbar, the desktop, and
the mobile shell also owned overlapping hard-coded colors. That made a complete
OS theme impossible without copying CSS into every host or injecting arbitrary
theme CSS, an unsafe behavior present in the read-only legacy implementation.

HackerOS has three host compositions and exports windowing/taskbar Razor class
libraries. The theme contract therefore has to remain reusable without making
the browser-independent window runtime depend on Blazor, MudBlazor, or a host.

## Decision

1. `HackerOs.Theming.Abstractions` is the browser-independent source of truth
   for built-in theme identifiers, platform compatibility, display metadata,
   accent support, and the persisted `ThemePreferences` value. It contains no
   Razor, MudBlazor, DOM, filesystem, or host dependency.
2. `HackerOs.Theming.Blazor` owns the visual contract. Its static
   `themes.css` asset defines semantic custom properties for every built-in
   theme, while `ThemeScope` selects a validated theme through data attributes
   on one ancestor. CSS isolation still applies to component layout; inherited
   semantic properties cross Razor-library boundaries without copying the
   theme stylesheet into a host or every window.
3. The Ecosystem composition root owns the active `ThemeScope`. It selects the
   saved desktop or mobile theme from the current UI platform and initializes
   appearance preferences at startup. Component libraries consume tokens and
   do not read settings directly.
4. Appearance schema version 2 stores independent desktop and mobile theme
   identifiers, the HackerOS accent, and the motion preference. Version 1 is
   accepted and migrated in memory so existing profiles retain their accent and
   animation choice.
5. Themes are data-only. A new built-in theme may add catalog metadata and a
   static, reviewed CSS selector, but may not contain executable JavaScript,
   arbitrary user CSS, remote fonts, or remote images. This keeps the theme
   boundary consistent with design-system decision D-013 and the offline PWA.

## Consequences

- The OS shell, windowing package, taskbar package, mobile shell, Settings app,
  and previews share one semantic token vocabulary and one static asset.
- A standalone consumer of the exported window/taskbar projects can wrap its
  surface in `ThemeScope` and receive the same visuals without depending on the
  full HackerOS host.
- Theme selection survives reload and is applied before the user opens
  Settings. Switching the platform selects the corresponding saved theme.
- MudBlazor remains a Platform/host concern; neither theme project exposes a
  MudBlazor type.
- Static assets are served through
  `_content/HackerOs.Theming.Blazor/themes.css`; hosts must not create a second
  copy or add a manual script/style bootstrap dependency.

## References

- [`../theming.md`](../old/theming.md) — theme API, token contract, and extension
  checklist.
- [`../design-system.md`](../old/design-system.md) — component styling and theme
  security rules.
- [ADR 0016](0016-platform-ui-library.md) — MudBlazor ownership boundary.
- [ADR 0007](0007-enforce-collocated-razor-assets.md) — collocated component
  assets and the inline-style/script prohibition.
