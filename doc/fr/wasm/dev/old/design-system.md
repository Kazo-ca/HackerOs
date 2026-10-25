# HackerOS Design System & Visual Tokens Specification

## Overview

The HackerOS Design System provides a polished HackerOS default while allowing
the complete shell to adopt the built-in historical desktop and mobile themes.
It combines MudBlazor platform wrappers with component-scoped CSS files
(`.razor.css`) and the shared semantic properties owned by
`HackerOs.Theming.Blazor`. See [`theming.md`](theming.md) for the catalog,
runtime scope, persistence contract, and extension instructions.

---

## 1. Core Color Palette & Design Tokens

Colors, surfaces, typography, borders, status, shape, motion, window chrome,
taskbar/launcher, and mobile navigation are controlled by semantic `--hos-*`
custom properties declared once in
`Platform/HackerOs.Theming.Blazor/wwwroot/themes.css`. Component CSS consumes
those properties with a safe fallback; it does not redefine a theme palette or
branch on a theme ID. Compatibility `--hackeros-*` aliases exist only to keep
older app CSS working while it moves to the semantic vocabulary.

One validated `ThemeScope` at the composition root supplies the active values.
Do not copy the RCL stylesheet into a host, inject tokens into
`document.documentElement`, or wrap each window in another theme scope.

---

## 2. Component Styling Rules (AGENTS.md Compliance)

1. **Scoped CSS Files Mandatory:**
   - Every Blazor component (`MyComponent.razor`) **MUST** place its component-specific styles in a matching scoped CSS file (`MyComponent.razor.css`).
   - Inline styles (`style="..."`) and embedded `<style>` tags in `.razor` files are **PROHIBITED**.

2. **MudBlazor Wrapper Integration:**
   - Use MudBlazor controls (`MudMenu`, `MudTabs`, `MudDataGrid`, `MudButton`) wrapped in platform components to maintain visual consistency.
   - MudBlazor themes are configured to map directly to HackerOS custom tokens.

---

## 3. Icons

Icons (Bootstrap Icons, Font Awesome, Lucide, Simple Icons, and MudBlazor's bundled
Material Design set) render as inline SVG colored via inherited `fill`/`stroke:
currentColor` — never a hardcoded fill color — so every icon automatically matches
the surrounding text color and adapts to theme changes with no per-icon work. Use the
`HackerIcon` component (`HackerOs.AppSdk.Icons`) rather than raw `<svg>` markup or an
icon font; see [`icon-library.md`](icon-library.md). Default size is 20px; use larger
sizes (e.g. 28–56px) sparingly, for emphasis in detail panels or empty states.

## 4. Theme Boundary Security (DECISION: D-013)

Themes in HackerOS customize visual appearance through reviewed static CSS
Custom Properties selected by IDs stored in
`/etc/hackeros/appearance.json` (schema version 2).
- **PROHIBITED:** Themes are data-only and static CSS assets. Themes CANNOT inject arbitrary JavaScript scripts, dynamic code, or unverified executable bundles into the browser runtime context.
