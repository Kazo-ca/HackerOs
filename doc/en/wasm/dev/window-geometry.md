> [Version française](../../../fr/wasm/dev/window-geometry.md)

# Window size saved and restored

## Purpose

An app window reopens with the size the user gave it, and maximized if it was,
but always at its cascade position: its position is not remembered. Implemented
by the `v3.1/2-window-geometry` work item according to
[ADR 0116](adr/0116-window-geometry-restore.md), corrected by
[ADR 0122](adr/0122-window-size-only-not-position.md) (size only,
Benoit's answer), in the [`v3-1-series-plan.md`](plans/v3-1-series-plan.md)
series. It is §10 of [ADR 0044](adr/0044-settings-as-files.md).

This is GNOME's model: a GTK app remembers its size and maximized state, the
window manager places the window. HackerOS holds the geometry in C# (ADR 0009);
it therefore writes this size itself, for all apps.

## State file

One file per app and per user, specific to the device, never synchronized:
`~/.local/state/hackeros/windows/<appId>.config`.

```ini
# Window size of org.hackeros.text-editor (ADR 0116, ADR 0122)
width=900
height=640
maximized=false
```

- `width`, `height`: **normal** size in CSS pixels, invariant culture. A
  maximized window keeps its restore size there.
- `maximized`: optional, `false` if missing. Only `true` maximizes: a typo
  leaves the window normal.
- A file without valid `width` and `height` is ignored: the window opens at its
  declared size.
- A file written before ADR 0122 (with `x=` and `y=`) keeps its size; its
  position is ignored, and the next write rewrites it without these keys.
- To forget an app's size: `rm ~/.local/state/hackeros/windows/<appId>.config`.

## Architecture

| Class (Platform.Blazor, `Windows/`) | Role |
|---|---|
| `WindowGeometrySettings` | File path, `Parse` and `Serialize`. |
| `SavedWindowGeometry` | Normal size (`Width`, `Height`) and maximized state, read or to be written. |
| `WindowGeometryPersistence` | Reads and writes the file through `IOsSettingsService`, under the user's identity; writes only if the content changes; a failure (no session, read-only folder) is ignored. |
| `WindowGeometryRecorder` | Listens to `WindowRuntime.EventsApplied`, keeps in memory the last normal size and maximized state of each tracked window and writes it after 500 ms without change, or immediately on close. |
| `WindowLaunchCoordinator` | `PresentAsync` reads the file before opening the window, places the window at its cascade position with the size read and corrected (`RestoredBounds`), maximizes if needed, then hands the window over to the recorder. |

The three services are `Scoped`, like `WindowRuntime`
(`EcosystemServiceCollectionExtensions`).

### Writing

- Triggers: any batch of `WindowRuntime` facts that changes the normal size or
  maximized state of a tracked window (resize, maximize, restore, work area
  change). A move alone writes nothing. The recorder rereads the runtime state
  after each batch rather than each fact, to record the bounds actually applied.
- Batching: resizing transmits samples grouped per frame, with a single interop
  call in flight (ADR 0163); changes of the same app are written once, 500 ms
  after the last one. A move transmits its final position only on release and
  still writes nothing to disk.
- Close: immediate write of the last known size.
- Writes of the same app go one after the other; opening a window immediately
  launches the write that was still waiting and waits for the app's writes to
  finish before reading the file. A window reopened right after being closed,
  or a second window opened during batching, therefore sees the last size.
- Never recorded: the minimized state (the window keeps what it had before) and
  the half-screen of a snapped window (it keeps its size from before snapping
  and reopens like a normal window).

### Reading (ADR 0116 §4, ADR 0122)

1. No file, invalid file or `RememberGeometry = false`: cascade position,
   declared size (`InitialBounds`, behavior as before).
2. Otherwise, cascade position (`InitialBounds`), with the size read brought
   between the minimum and maximum that the app declares **today** (a
   fixed-size window takes its declared size), then to the work area without
   going below the minimum.
3. A second window of the same app takes the next cascade position, like any
   new window, with the same size.
4. `maximized=true`: the window opens maximized, the size read becomes its
   restore size. A fixed-size window never reopens maximized.

## Usage for an app

Nothing to do: any app of kind `Window` remembers its size. To opt out (a
calculator, a wizard that must always open at the same size):

```csharp
public override WindowOptions? Window => new()
{
    DefaultWidth = 320, DefaultHeight = 480,
    RememberGeometry = false,
};
```

`WindowOptions.RememberGeometry` is an addition of SDK 1.1
(`PublicAPI.Shipped.txt` of `HackerOs.App.Abstractions` since `v3.1/4-sdk-1-1`). The generator writes
`window.rememberGeometry` in `manifest.json` only when it is `false`: the
manifests of apps that keep the default stay byte-for-byte identical. The v1
schema accepts it as an optional field, the strict deserializer too.

## What is not covered

- Mobile interface: full screen, it ignores this file.
- Detached windows (ADR 0078, 0081): placed by the host system.
- OS windows that are not apps (file dialogs, authentication agent, "Open
  with"): never tracked.
- Position: never remembered (ADR 0122).
- No "forget size" button in Settings.

## Implementation decisions

- **`PresentAsync` replaces `Present`**: reading the file is asynchronous. The
  shell sometimes presents the same process twice (its own launch and the
  process moving to `Running`); during the read, the second call returns
  without opening a second window, and the window's existence is rechecked after
  the read.
- **Snapping recognized by its shape**: `WindowRuntime` keeps no "snapped" state
  (window-model.md). A normal window that exactly covers the left or right half
  of the work area is treated as snapped and is not recorded.
- **`SaveAsync` without `isEligible`**: the coordinator decides which windows to
  track (`Track`); the removed line was marked `*REMOVED*` in
  `PublicAPI.Unshipped.txt` of Platform.Blazor (outside the SDK's promise, ADR 0114 §2),
  then applied at the 1.1 publication ("Breaking changes outside the SDK" in [`sdk-packages.md`](../framework/sdk-packages.md#breaks-outside-the-sdk)).
- **Failures logged**: `WindowGeometryPersistence` writes a warning when the
  file cannot be read or written, then continues.
- **Content compared before writing**: closing a window whose size has not
  changed does not rewrite the file and does not wake observers.
- **Size only (ADR 0122)**: first delivered with position (ADR 0116 §1), then
  corrected on Benoit's answer; `RestoredBounds` takes the size and the cascade
  index, `MinimumVisibleTitleBarWidth` is gone.
- **Commented header** in the file, like the OS's other documents.

## Tests

- `WindowGeometryPersistenceTests` (Platform.Blazor): path, round trip,
  maximized state, optional `maximized`, invalid file, no rewrite, no session.
- `WindowGeometryRestoreTests` (Platform.Blazor): reopening with the same size
  at the cascade position, new session, move alone without writing, maximized,
  minimized or snapped, batching of a drag, pending write flushed on opening,
  `RememberGeometry = false`, invalid file, file from before ADR 0122, double
  presentation, smaller screen, declared sizes changed, fixed window, untracked
  OS window.
- `AppManifestJsonSerializerTests`, `ManifestSchemaConformanceTests`
  (App.Abstractions): `rememberGeometry` written only at `false`, read back,
  accepted by the schema.
- E2E `WindowGeometryRestoreTests` (UI.E2E): drag a window's title bar and
  corner, close it, reopen it with the same size at its cascade position, reload
  the page, log in again and find it that way.

## Task list

- [x] `WindowOptions.RememberGeometry`, JSON projection, schema, strict deserializer, `PublicAPI.Unshipped.txt`.
- [x] `maximized` key and `SavedWindowGeometry`; `SaveAsync` without `isEligible`, writes only if the content changes.
- [x] `WindowGeometryRecorder`: tracking, 500 ms batching, write on close, minimizing and snapping ignored.
- [x] `WindowLaunchCoordinator.PresentAsync`: reading, correction, second instance, maximized, guard against double presentation.
- [x] `DesktopShell` and dependency injection.
- [x] Unit and E2E tests.
- [x] `window-model.md`, `settings-files.md`, `sdk-developer-guide.md`, `sdk-reference.md`, `app-sdk.md`, v3 review, v3.1 plan, index.
- [x] ADR 0122: size only; position removed from the file, the reading and the tests.
