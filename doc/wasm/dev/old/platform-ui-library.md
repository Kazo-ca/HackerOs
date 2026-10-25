# Platform UI Library

## Purpose

Define where MudBlazor may reduce complex interaction risk without becoming the
owner of HackerOS shell, window, lifecycle, or public SDK behavior.

## Approved Boundary

ADR 0016 approves MudBlazor 9.7.0, MIT licensed, for menus, grids, tabs,
validated forms, dialogs, and comparable complex selectors. Platform-owned
wrappers are preferred for reusable workflows. Native Blazor and scoped CSS
remain required for desktop, window chrome, taskbar, launcher layout, and simple
controls.

MudBlazor types must not appear in App Abstractions, App SDK contracts,
Simulation Abstractions, Platform Core, or Browser Infrastructure. Theme values
derive from HackerOS design tokens, remote fonts are excluded, and package
scripts/styles are loaded only by the interactive host composition.

## Status

- [x] Select MudBlazor 9.7.0 and exact usage boundary (`P2-UI-001`).
- [x] Complete published Release, trimming, payload, accessibility, and mobile
  proof (`P2-UI-002`).
- [x] Record approved wrappers and app usage conventions (`P2-UI-003`).

## Release Proof

`PlatformComplexControlsProof` is a retained Platform wrapper exercising a menu,
tabs, required form field, validation, button, live status regions, and scoped
responsive styling. The standalone browser harness registers Mud services and
providers and loads only package-local CSS/JavaScript; no remote font or CDN is
used.

The .NET 10 Release publish completed with trimming enabled and no warning. Its
published `wwwroot` measured 19,408,628 bytes total. Direct MudBlazor payload is:

| Asset | Raw bytes | Brotli bytes |
| --- | ---: | ---: |
| `MudBlazor.wasm` | 681,749 | 194,930 |
| `MudBlazor.min.css` | 611,420 | 41,399 |
| `MudBlazor.min.js` | 70,190 | 15,176 |
| **Direct runtime total** | **1,363,359** | **251,505** |

The source map is published but is not a runtime download. This retained proof
provides a baseline; later app additions must measure incremental payload and
must not assume every Mud component is free after initial adoption.

The Playwright proof runs in installed headless Chrome at 1280x800 and 375x812.
It opens the menu, selects an action, changes tabs, verifies invalid and valid
form states, queries controls by accessible roles/labels, checks horizontal
overflow, captures a non-empty mobile screenshot, and rejects console or failed
network requests. Mud menu items required explicit wrapper-owned `menuitem`
roles; this is now part of the wrapper convention.

## Wrapper Conventions

- Place reusable complex controls under `Platform/HackerOs.Platform.Blazor/Controls/`
  or the owning Platform feature directory.
- Name wrappers for the HackerOS workflow, not the underlying Mud component.
- Keep domain state and commands in renderer-independent contracts; wrappers
  receive values and callbacks only.
- Do not expose `MudBlazor` types in public parameters used across assemblies.
- Add missing ARIA roles/labels in the wrapper and test by role, not CSS class.
- Keep layout/visual overrides in the matching `.razor.css` file.
- Use Platform theme tokens; do not use remote fonts, inline styles, or inline
  scripts.
- Register Mud services/providers once in the interactive host composition.
- Prefer native semantic HTML for simple controls and text.
- Add component/browser coverage for keyboard, mobile containment, validation,
  console errors, and failed asset requests when introducing a new category.

Approved initial categories remain menus, tabs, grids, validated forms, dialogs,
and complex selectors. Each category still requires an actual workflow wrapper;
`PlatformComplexControlsProof` is evidence, not a product-facing generic panel.

## Key Decision

MudBlazor is an implementation dependency for approved complex UI, not a
HackerOS domain framework or window manager. ADR 0009 remains authoritative for
window runtime behavior.