# ADR 0016: Platform UI Library Boundary

## Status

Accepted on 2026-08-02.

## Context

HackerOS needs accessible menus, data grids, tabs, forms, dialogs, and option
controls. Reimplementing every complex interaction would add keyboard, focus,
validation, mobile, and screen-reader risk. At the same time, HackerOS owns a
distinct desktop shell, process lifecycle, window state machine, and Gothic/
hacker visual language that must not become library-owned public SDK behavior.

MudBlazor 9.7.0 is MIT licensed and declares full support for .NET 8, 9, and 10.
It requires its CSS/JavaScript static assets, service registration, and providers.
Its Release trimming, payload, scoped CSS, theme, accessibility, and mobile
behavior still require the published-browser proof in `P2-UI-002`.

## Decision

Adopt MudBlazor 9.7.0 for complex controls only, behind Platform-owned wrappers.

Approved categories are:

- menus and context menus;
- data grids and dense sortable/filterable tables;
- tabs;
- validated forms and complex field controls;
- modal dialogs; and
- complex option selectors where native controls do not meet the workflow.

Native Blazor markup and scoped CSS remain authoritative for:

- desktop and work-area composition;
- window chrome, geometry, focus, z-order, and taskbar behavior;
- launcher and shell layout;
- simple buttons, icon buttons, labels, status text, and basic inputs; and
- app-specific visual canvases or terminal/editor hosts.

MudBlazor never owns process, app lifecycle, capability, intent, filesystem,
window, modality, or recovery domain state. Public App SDK contracts do not
expose MudBlazor types. Reusable complex controls are wrapped by components in
`HackerOs.Platform.Blazor`; apps consume those wrappers when a platform control
exists and may use approved MudBlazor components locally only without leaking
them through cross-project contracts.

Themes map HackerOS design tokens into a restrained Mud theme. Components keep
their own scoped CSS; no inline style or script exception is introduced. Remote
font dependencies are prohibited so published offline operation remains intact.

## Proof Gate

`P2-UI-002` must publish a Release WASM proof and record:

1. successful trimming with no unexplained warnings;
2. static asset and service/provider initialization with no console/network
   failures;
3. compressed and uncompressed payload impact;
4. scoped CSS and HackerOS token interoperability;
5. keyboard and screen-reader semantics for representative approved controls;
6. desktop and mobile layout without overlap or clipped text; and
7. the MIT license reference.

Failure of this proof reopens D-013 before complex Platform Blazor components
are implemented. It does not change ADR 0009: C# remains the authoritative
window state owner.

## Consequences

- Complex interaction primitives reuse a maintained .NET 10 component library.
- Shell identity and window behavior remain purpose-built and deterministic.
- Wrappers limit coupling and leave room to replace individual controls.
- The package and assets increase payload and must be measured continuously.
- MudBlazor upgrades require explicit compatibility and visual regression review.

## References

- ADR 0009: Purpose-Built Window Runtime
- MudBlazor 9.7.0 package and upstream MIT license
- MudBlazor version support and installation documentation
- `docs/platform-ui-library.md`