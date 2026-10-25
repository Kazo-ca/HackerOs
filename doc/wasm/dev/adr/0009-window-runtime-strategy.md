# ADR 0009: Purpose-Built Window Runtime

## Status

Accepted on 2026-08-01.

## Context

HackerOS needs desktop-style windows linked to app instances and simulated
processes. The runtime must support deterministic focus and z-order, taskbar
minimize/restore, modality, external app component types, mobile constraints,
keyboard and screen-reader behavior, scoped assets, Release trimming, and
pointer/touch drag and resize.

ADR 0006 already seals `WindowAppBase` lifecycle methods because a previous
implementation let app overrides silently skip mandatory JavaScript setup.
Window apps must remain unaware of window-manager implementations and direct
browser interop.

No window-management or component-framework package is referenced by the current
solution. MudBlazor is separately gated by D-013 for menus, grids, tabs, forms,
dialogs, and shell controls; it does not define HackerOS process/taskbar/window
lifecycle semantics.

## Options considered

### Purpose-built C# runtime

Advantages:

- directly models HackerOS app, process, taskbar, modality, and intent behavior;
- keeps authoritative state browser-independent and deterministically testable;
- renders external app component types through normal Blazor primitives;
- adds only the pointer interop and chrome required by the product; and
- avoids adopting a library-specific public SDK surface.

Costs:

- HackerOS owns geometry constraints, focus rules, accessibility, and gesture
  testing; and
- browser behavior must be proven against published Release output.

### General Blazor component/dialog framework

General frameworks can supply menus, dialogs, tabs, and accessible controls, but
they do not own desktop z-order, taskbar state, process cancellation, restore
geometry, or external app lifecycle. Using one as the window manager would still
require a parallel HackerOS state machine and synchronization layer.

### Third-party desktop/window manager

A third-party window package could reduce initial chrome work, but adoption would
require proof that it supports sealed framework lifecycle, dynamic external Razor
components, HackerOS-owned taskbar/focus state, scoped assets, pointer and touch
input, keyboard/screen-reader semantics, reduced motion, mobile constraints, and
Release trimming. No current dependency has supplied that evidence. Wrapping an
unproven package would add payload and a second state owner before reducing risk.

## Decision

Build a purpose-built window runtime in
`Platform/HackerOs.Platform.Blazor`, backed by browser-independent state and
transition logic.

C# is authoritative for:

- window identity and app/process/instance linkage;
- geometry, restore geometry, constraints, state, z-order, and active focus;
- ownership, modality, blocked interaction, and close state;
- create, focus, move, resize, minimize, maximize, restore, viewport-change,
  close-request, and forced-close transitions; and
- taskbar-visible state and deterministic event ordering.

Blazor components render desktop area, window host, and window chrome from that
state. Window content is a validated `WindowAppBase` component type supplied by
the trusted app descriptor and bound to one `IAppExecutionContext`.

JavaScript is limited to a collocated `WindowChrome.razor.js` module using Pointer
Events. It captures pointer gestures and reports coordinates/deltas to C#; it does
not own geometry, z-order, lifecycle, or mutate Blazor-owned DOM. Keyboard window
commands remain in C#.

Platform-owned lifecycle code imports and disposes mandatory modules. App
components continue using sealed `WindowAppBase` lifecycle hooks and cannot skip
framework setup by omitting a base call.

MudBlazor or another approved general UI library may later render menus, forms,
and dialogs behind platform wrappers. It does not become the window state owner.
A different window package requires a superseding ADR and the same proof gate.

## Proof gate

Production adoption of the browser chrome requires a small published Release
proof before the window runtime is considered complete. The proof must:

1. render a dynamically supplied `WindowAppBase` component;
2. import the collocated JS module through framework-owned lifecycle code;
3. drag and resize with real mouse Pointer Events;
4. repeat the gesture with touch emulation;
5. preserve C# authoritative geometry and z-order;
6. support keyboard move/resize or equivalent accessible commands, focus
   indicators, labels, and reduced motion;
7. constrain geometry after viewport changes on desktop and mobile; and
8. pass Release publish/trimming with no module, console, or network errors.

Failure of the proof blocks browser chrome adoption and requires revisiting this
ADR. Headless state-machine work may proceed because it does not depend on the
interop choice.

## Consequences

- Window behavior remains testable without Blazor or a browser.
- The platform has one authoritative state owner instead of synchronizing a
  library model with HackerOS process/taskbar state.
- Pointer JavaScript remains small, isolated, and replaceable.
- Accessibility and geometry invariants are explicit platform responsibilities.
- A published-browser proof is mandatory before completing the window runtime.
- D-013 remains independent and cannot silently replace this decision.

## References

- ADR 0006: Seal the Window Component Lifecycle
- ADR 0007: Enforce Collocated Razor Assets
- `docs/blazor-app-sdk.md`
- `docs/window-runtime.md`
- `doc/wasm/dev/plans/wasm-v3-migration-analyse.md`