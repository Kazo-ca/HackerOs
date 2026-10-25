# ADR 0116: window geometry saved and restored

## Status

Accepted on 2026-10-17 (work item `v3.1/1-adr`, for the work item
`v3.1/2-window-geometry`). A question is asked to Benoit in the work item
thread (§1); what follows applies the recommended answer while awaiting it.

- **Implements:** [ADR 0044](0044-settings-as-files.md) §10 (window
  geometry in `~/.local/state/hackeros/windows/<appId>.config`, device-specific,
  never synchronized).
- **Complements:** [ADR 0095](0095-window-events-sizes-and-keyboard.md) §2
  (sizes declared by the app) and [ADR 0009](0009-window-runtime-strategy.md)
  (geometry held by the window manager in C#).
- **Plan:** [`v3-1-series-plan.md`](../plans/v3-1-series-plan.md); gap
  "geometry never read back" of [`wasm-v3-final-status.md`](../plans/wasm-v3-final-status.md).

## Context

- `WindowGeometryPersistence` and `WindowGeometrySettings`
  (`Platform/HackerOs.Platform.Blazor/Windows/WindowGeometrySettings.cs`)
  know how to read and write `x=`, `y=`, `width=`, `height=` in an app's state
  file. The service is registered in dependency injection
  (`EcosystemServiceCollectionExtensions.cs:820`), but **no code calls
  it**: nothing writes the geometry, nothing reads it back.
  `settings-files.md` nevertheless presents it as "restores and saves".
- `WindowLaunchCoordinator.InitialBounds` places each new window in a cascade
  (8 positions offset by 28 px) at the size declared by the app
  (ADR 0095 §2), brought back to the work area without going below the minimum.
- `SaveAsync` expects an `isEligible` flag that no manifest field
  provides.
- On a real desktop, two models exist: Windows and macOS reopen a
  window where it was; under GNOME, the window manager does not do so
  and each GTK app saves at least its size and maximized state.
  HackerOS holds the geometry in C# (ADR 0009); it is therefore up to it to
  save it, not to each app.

## Decision

### 1. All windows remember size, position and maximized state (question 1, default applied)

- Each window of an app of kind `Window` remembers, per app and per user,
  its size, its position and whether it was maximized.
- `WindowOptions` (`HackerOs.App.Abstractions`) receives
  `RememberGeometry` (boolean, true by default). An app that sets it to false
  (a calculator that wants to always open at the same place, a wizard)
  always opens at its cascade position. The generator writes
  `window.rememberGeometry` in `manifest.json` only when it is false;
  schema v1 adds it as an optional field (compatible addition).
- This is an addition to the SDK: the property goes into `PublicAPI.Unshipped.txt` and
  ships with 1.1 ([ADR 0117](0117-sdk-window-title-and-sdk-1-1.md) §4).
- OS windows that are not apps (file dialogs, authentication
  agent, "Open with" picker) remember nothing.

### 2. File format

The existing file keeps its four keys and receives a fifth:

```ini
# Géométrie de la fenêtre de org.hackeros.text-editor (ADR 0116)
x=312
y=96
width=900
height=640
maximized=false
```

- `x`, `y`, `width`, `height` are always the **normal** geometry (not
  maximized, not snapped), in CSS pixels, invariant culture. A maximized
  window therefore keeps its restore geometry, as under X11.
- `maximized` is optional; absent, it is `false`. A file without the
  four numbers is still ignored: the window opens at its cascade position (the
  current rule of `WindowGeometrySettings.Parse`).
- The minimized state is never saved: a window never reopens minimized.
- A window snapped to the left or right saves its geometry from before
  snapping (snapping keeps none, `window-model.md`): it reopens like
  a normal window.

### 3. When the geometry is written

- At the end of a move or a resize (pointer released or keyboard
  command Alt+F7, Alt+F8 confirmed), at maximize, at restore
  and at window close. Never at every pixel: the `WindowRuntime`
  events that already feed `OnWindowResized`
  (ADR 0095 §1) serve as the trigger, with a 500 ms debounce.
- The write goes through `IOsSettingsService` under the user's identity,
  as today; a failure (no session, read-only home folder) is ignored, the geometry
  being a convenience.
- The file is written only if it changes, so as not to wake the file
  watchers at every close.

### 4. When the geometry is read back

`WindowLaunchCoordinator` reads the file before opening the window, in place
of `InitialBounds`:

1. file missing or invalid, or `RememberGeometry` false: cascade position
   (current behavior);
2. the size read is brought between the minimum and the maximum **declared
   today** by the app (the manifest may have changed since the write), then
   to the work area, without going below the minimum;
3. the position is corrected so that the title bar remains reachable: at
   least 48 px of the bar visible horizontally, and the top of the window
   within the work area. A screen smaller than last time (other
   device, reduced browser window) therefore brings the window back onto the screen,
   like a window manager that loses a monitor;
4. if a window of the same app is already open (second instance), the
   new one opens offset by 28 px from the geometry read, so as not to
   exactly cover the first;
5. `maximized=true` opens the window maximized, with the geometry read as the
   restore geometry.

### 5. What is not covered

- **Mobile interface:** full screen, it ignores this file (like the sizes
  of ADR 0095).
- **Detached windows** (ADR 0078, 0081): their browser window is
  placed by the host system; they neither read nor write this file.
- **Forgetting the geometry:** deleting the file
  (`rm ~/.local/state/hackeros/windows/<appId>.config`) is enough, like clearing
  an app's state on Linux. No button in Settings for now.
- **Synchronization:** none, the file is under `~/.local/state`
  (ADR 0044 §10); another device keeps its own positions.

## Consequences

- `v3.1/2-window-geometry` wires `WindowGeometryPersistence` into
  `WindowLaunchCoordinator` and into the `WindowRuntime` events, adds
  `maximized`, `WindowOptions.RememberGeometry`, the schema field and its
  validator check, and removes the `isEligible` parameter of
  `SaveAsync` (platform type, outside the SDK promise, ADR 0114 §2; the
  removed line is written `*REMOVED*`).
- Unit tests: size and position correction (smaller screen, changed
  minimum, second instance), maximized state, `RememberGeometry` false,
  invalid file. E2E test: move and resize a window, close
  it, reopen it at the same place; reload the page and find the same
  place.
- `window-model.md`, `settings-files.md`, `app-sdk.md`, `sdk-reference.md` and
  the manifest schema doc describe the behavior and the field.

## Rejected options

- **Only on request from the app** (`RememberGeometry` false by default):
  no existing app would benefit without being modified, and the gap
  would remain whole for apps from other repositories.
- **Size only, without position** (GNOME model): more faithful to a Linux
  desktop, but the OS already holds the geometry itself; keeping only the size
  would surprise most users, used to Windows or macOS.
- **One geometry per instance**: there is no stable identity of an
  instance between two openings; one file per app is enough.
