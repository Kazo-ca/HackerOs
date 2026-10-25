# Window Runtime

## Purpose

The Platform window runtime is the sole C# owner of desktop window identity,
app/process linkage, geometry, visual state, focus, z-order, modality, and close
coordination. Razor will render immutable snapshots; JavaScript will later report
pointer gestures but never own state.

## Architecture

- `WindowRuntimeState` is one complete renderer-independent snapshot.
- `WindowId`, `WindowBounds`, and `WindowConstraints` validate primitive values.
- `WindowCommand` records describe requested transitions.
- `WindowEvent` records describe completed transitions in deterministic order.
- `WindowRuntime` atomically applies commands and exposes snapshots back-to-front.
- `WindowLaunchCoordinator` projects successful lifecycle process launches into
  visible windows and restores/focuses existing singleton windows.

`WindowRuntime`, `WindowRuntimeState`, and the geometry/message types now live
in the standalone `HackerOs.Windowing.Core` project (see
[`window-taskbar-export-plan.md`](window-taskbar-export-plan.md)), not in
`HackerOs.Platform.Blazor`. Two consequences of that extraction:

- The model no longer references `ProcessId`/`AppInstanceId` directly. It owns
  windows through the opaque `WindowOwnerId` struct; `HackerOs.Platform.Blazor`
  adapts its own process/app-instance identity to `WindowOwnerId` at the
  boundary, so a non-HackerOS host can drive the same engine.
- `WindowRuntimeState` carries an optional `Content` (`RenderFragment?`) and
  `OnRequestClose` (`Func<Task>?`) so a host can attach arbitrary Blazor
  content (an app's UI, or a dialog) to a window without the engine knowing
  concrete component types. This is a deliberate, approved exception to
  "renderer-independent": `HackerOs.Windowing.Core` still has zero project
  references and only takes a package dependency on
  `Microsoft.AspNetCore.Components` for the `RenderFragment` delegate type —
  it has no `.razor` files, no MudBlazor, and no JS interop. It contains no
  component reference, MudBlazor type, DOM element, or JS interop handle
  beyond that one delegate type. Icon identity is a package-local asset path
  rather than rendered content.

## Transition Rules

- Creating a window assigns the next z-order and transfers focus to it.
- Focusing a visible window brings it to front; minimized windows must restore.
- Move and resize operate only on normal windows; resize honors constraints.
- Maximize captures normal geometry and uses the current desktop work area.
- Restore reapplies captured geometry; viewport changes update maximized bounds.
- Minimize removes focus and focuses the highest remaining visible window.
- Close request is idempotent and does not remove state.
- Forced close removes state and refocuses the highest visible window.
- Snapshots are ordered by z-order with window ID as a stable tie-breaker.

Advanced viewport clamping is owned by `P2-WIN-008`; owner-modal blocking and
focus trapping are owned by `P2-WIN-011`. The current contracts already carry
the information those transitions require.

## Usage

Create one `WindowRuntime` for a desktop work area, submit one command at a time,
render `Windows`, and route emitted events to lifecycle/taskbar integrations.
Callers must not keep a second mutable window model.

The desktop shell launches through `AppLifecycleOrchestrator`, then passes the
manifest and successful `AppLaunchResult` to `WindowLaunchCoordinator`, which
derives a `WindowOwnerId` from the process's `ProcessId`/`AppInstanceId`, so
the desktop, taskbar, lifecycle, and close coordinator all refer to the same
simulated OS process even though the runtime itself only knows the opaque
owner id.

## Key Decisions

- State snapshots are immutable and browser-independent.
- Commands express intent; only the runtime performs mutations.
- Close request and final removal are distinct lifecycle phases.
- Z-order is monotonic within one runtime for deterministic event ordering.

## Completed Tasks

- [x] `P2-WIN-001` Define authoritative C# window state.
- [x] `P2-WIN-002` Define create/focus/geometry/state/close/viewport messages.
- [x] `P2-WIN-003` Implement and test the deterministic headless state machine.
- [x] `P2-WIN-004` Render desktop area, hosts, and chrome with scoped CSS.
- [x] `P2-WIN-005` Validate and render dynamic `WindowAppBase` entry points.
- [x] `P2-WIN-006` Run private framework setup before sealed app post-render hooks.
- [x] `P2-WIN-007` Report Pointer Events deltas from collocated JavaScript to C#.
- [x] `P2-WIN-008` Constrain desktop/mobile geometry and restore bounds in C#.
- [x] `P2-WIN-009` Provide labelled icon controls and reduced-motion behavior.
- [x] `P2-WIN-010` Coordinate confirmation, process cancellation, and removal.
- [x] `P2-WIN-011` Enforce owner-modal blocking and deterministic focus return.
- [x] `P2-WIN-012` Persist eligible geometry in `AppUserDevice` settings only.
- [x] Project successful launcher results into desktop and taskbar window state.

## Browser geometry projection

The runtime remains authoritative for geometry. Blazor projects its validated
`WindowBounds` and z-order through one generated invariant-culture `style`
attribute on `WindowHost`; all visual styling remains scoped CSS. This narrow
exception was approved after real Chrome testing proved typed CSS `attr()` did
not apply numeric position and dimensions. JavaScript still owns only Pointer
Events and reports deltas back to C#.

## Browser proof

The browser harness renders two real `WindowHost` components in Chrome and
verifies z-order, mouse drag, touch-typed edge resize, maximize/restore,
agreement between C# state and the rendered box, and clean console/network
output. A Pointer Capture `NotFoundError` falls back to uncaptured element
listeners instead of cancelling the gesture.

Chrome also starts the drag directly on a background window. The chrome stops
the host pointer event and sequences focus before geometry in the same C# delta
callback, preventing a focus rerender from racing the first move.
