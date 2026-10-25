# ADR 0133: window rendering in a worker, verdict of the trial: we stop

## Status

Accepted on 2026-10-18 by `exec/6-remote-renderer-poc`, with the recommendation
submitted to Benoit on a decision card in the "Exec 6" thread. The
recommendation (option A below) is applied while awaiting his answer;
if he chooses another option, a new ADR replaces this one.

- **Concludes:** the trial of [ADR 0129](0129-window-rendering-in-worker-trial.md).
- **Does not replace** ADR 0070 §1: windows stay on the main
  thread (the replacement was only planned if we went ahead).
- **Doc:** [`remote-window-rendering.md`](../remote-window-rendering.md)
  (trial architecture, operation format, measurements, task list);
  [`worker-processes.md`](../worker-processes.md) (`ui`, `uin` messages,
  `start` `k = 3`).

## Context

ADR 0129 asked for a prototype that renders the Calculator in a Web Worker,
measurements on the trimmed Release publish and a verdict according to six
criteria, **all** required to go ahead. The prototype is built and works:
the Calculator opens in a dedicated worker, draws itself identically, responds
to the mouse and keyboard, and closes cleanly. It stays in the repository
behind the setting `HackerOs:RemoteWindowRendering:Enabled`, turned off by default.

## Measurements

Trimmed Release publish of the Ecosystem (`artifacts/publish-trimmed`),
headless Chromium, 300 clicks on the Calculator keys, 6
openings per mode. Test that produces them:
`Measures_the_calculator_on_the_page_and_in_a_worker`
(`Tests/HackerOs.TrimmedPublish.E2E.Tests`), which writes
`remote-window-measurements.json`.

| Measurement | On the page (setting off) | In a worker (setting on) | Difference |
|---|---|---|---|
| Opening, first | 140 ms | 606 ms | +466 ms |
| Opening, subsequent (worker ready) | 42 to 54 ms (≈ 46) | 358 to 413 ms (≈ 385) | +≈ 340 ms |
| Click → DOM modified, p50 | 2.2 ms | 3.4 ms | +1.2 ms |
| Click → DOM modified, p95 | 4.1 ms | 7.4 ms | +3.3 ms |
| Click → next frame, p50 | 4 ms | 19.7 ms | at most one frame later |
| Click → next frame, p95 | 20.2 ms | 20.1 ms | ≈ 0 |
| Worker linear memory | 55.4 MiB (worker ready, empty) | **66.5 MiB** | +11.1 MiB for the window |
| Page linear memory | 138 MiB | 138 MiB | unchanged |
| First batch | n/a | 5,337 bytes (85 nodes) | |
| Following batch, average | n/a | 72 bytes, 140 µs of encoding | |

The memory is that of the WebAssembly linear memory of the worker's runtime
(`HEAPU8.length`), the only one the browser lets you read in a worker without
cross-origin isolation (ADR 0131). `/proc/hackeros/workers` shows the managed
heap: 5,694 KiB empty, 5,866 KiB with the window.

### Why 55 MiB for an empty worker

Each worker loads the whole boot assembly set (`workerBootstrap.js`,
ADR 0070): 87 assemblies, ≈ 20.3 MB, plus 1.98 MB of base assemblies.
`HackerOs.AppSdk.Icons` alone weighs 8.67 MB; the native runtime 3 MB,
ICU 0.55 MB. The window itself only adds ≈ 11 MiB. The cost is therefore
that of one more .NET runtime, not of rendering.

## Criteria of ADR 0129 §4

| Criterion | Threshold | Result | Met |
|---|---|---|---|
| Added latency, p95 | ≤ 16 ms | +3.3 ms up to the DOM, ≈ 0 up to the frame | yes |
| Memory of a window worker | ≤ 40 MiB | 66.5 MiB | **no** |
| Opening | ≤ 1 s more | +≈ 340 ms (worker ready), +466 ms (first) | yes |
| Calculator UI tests | green, app unchanged | dedicated E2E test green (clicks, keyboard, CSS, `Tab`, shortcuts, close); app unchanged | yes |
| Accessibility | axe and keyboard green | axe: no serious or critical violation; native `Tab` | yes |
| Framework | no internal or private API | none; but warning `BL0006` (see below) | yes, with a reservation |

## Decision

### 1. We stop

One criterion is not met (memory, 66.5 MiB against 40 MiB). According to ADR
0129 §4, **`exec/7-window-workers` does not happen** and nothing changes for
apps:

- windows stay on the main thread (ADR 0070 §1, unchanged);
- the manifest validator still rejects `AppExecutionHost.Dedicated` for
  a window (`manifest.execution.window`);
- a heavy computation in a window goes out through `HackBackground.RunAsync`
  (ADR 0128), which is the recommended path.

### 2. The prototype stays, turned off

The trial code stays in the repository, with no effect as long as the setting is
off:

- `Platform/HackerOs.Platform.RemoteRendering` (new project): the
  worker's `Renderer`, the DOM operation translator, the page's window host
  and its collocated JavaScript module;
- `HackerOs.Platform.WorkerHost`: messages `ui` and `uin`, `start` `k = 3`,
  `WorkerRemoteProcessHost.StartWindowAsync`, `RemoteWindowProcess`;
- `HackerOs.Platform.Blazor`: `IRemoteWindowRendering`, consulted by
  `WindowAppRenderer` when the service exists;
- the Ecosystem's `appsettings.json`: `HackerOs:RemoteWindowRendering`
  (`Enabled: false`, `Apps: ["org.hackeros.calculator"]`).

Keeping it costs little (no service registered when the setting is off) and
allows redoing the measurements if the cost of a worker drops (option D).
These types are public additions to the platform (`PublicAPI.Unshipped.txt`
of the platform), not to the apps' SDK: no app can depend on them.

### 3. What the trial built (for the record)

- **The worker translates, the page executes** (X11 model). Rather than sending
  the `RenderBatch` and replaying it on the page side, the worker keeps a mirror of
  Blazor's logical tree (components, flattened regions, elements, texts,
  markup) and writes DOM operations on numbered nodes: create an
  element, a text, markup, remove, set or remove an attribute, change
  a text, listen to an event, move, `preventDefault` and
  `stopPropagation` flags. The page only has a small executor
  (`RemoteWindowHost.razor.js`); all the logic stays in C# (AGENTS.md I.2).
  ADR 0129 §1 spoke of a format encoded by `WireCodec`: the format is indeed
  ours and versioned with the C#, but as JSON in the `ui` message, like the
  other protocol messages.
- **Events**: the page sends back `{h, n, a}` (handler number, name,
  event properties); the worker rebuilds Blazor's `EventArgs` and, for `input` and `change`, an `EventFieldInfo` so that `@bind`
  does not overwrite what the user types.
- **SDK dialogs**: the message box and the text input are
  opened by the page host with the process context; the credentials
  request answers "canceled" (no password enters
  a worker); file dialogs are refused.
- **Process**: a dedicated worker, taken from the ready worker (ADR 0071) and terminated at
  close; the window has its pid, its remote context and its gateways
  (ADR 0070, 0124).

### 4. Observed coverage

| Point of ADR 0129 §3 | Result |
|---|---|
| Isolated CSS | pixel-identical (screenshots compared) |
| Keyboard, `Tab` | yes; `Tab` is native, the DOM is the page's real DOM |
| Programmatic focus (`FocusAsync`) | **no**: no `IJSRuntime` in the worker |
| Window manager shortcuts (ADR 0095) | yes (`Alt+F7`, `Alt+Q`): the chrome stays on the page |
| `@bind` | yes, tested by unit tests (text and checkbox) |
| SDK dialogs | message and input relayed (unit tests); files refused |
| Detached window (ADR 0078) | **no**: the detached surface goes through JavaScript interop |
| Process language | by construction (`fr` satellites loaded before the app, as for a command); not verified in a browser |

### 5. Risk noted: `BL0006`

`RenderBatch`, `RenderTreeFrame`, `RenderTreeEdit` and `Renderer` are public,
but Blazor's analyzer (`BL0006`) warns that they "will change in a future
version". The criterion "no internal API" is met to the letter; in
practice, a .NET update can break the translator. The files that
read them disable `BL0006` with this reason, and the unit tests
(`Tests/HackerOs.Platform.RemoteRendering.Tests`) serve as a safeguard at
each version upgrade.

## Options submitted to Benoit

| Option | What happens | Implications |
|---|---|---|
| **A. We stop (recommended, applied)** | `exec/7` does not happen; prototype kept, turned off. | Nothing changes for apps; the heavy load goes through `HackBackground`. No memory cost. |
| B. One shared worker for all worker windows | `exec/7` rewrites the trial: a single runtime for all "worker" windows, ≈ 11 MiB per window afterwards. | Meets the memory criterion from the 2nd window on, but a window that loops freezes all the other windows of that worker: we lose the isolation that justified the trial. New ADR to change the criterion. |
| C. We go ahead anyway, one worker per window | `exec/7` as planned, accepting ≈ 66 MiB per window. | Full isolation, but 5 windows cost ≈ 330 MiB more; programmatic focus and detached windows remain impossible. New ADR that raises the threshold. |
| D. First lighten a worker's assembly set | Separate work item: a worker only loads what it needs (without `AppSdk.Icons`, etc.), then the measurements are redone. | Also benefits commands and services in a worker; the verdict remains "we stop" until the new measurement. |

## Consequences

- `exec/7-window-workers` is removed from the `exec` series (plan of the
  step 5 follow-ups, decision 7); `exec/9` and `exec/10` no longer wait for it.
- The setting `HackerOs:RemoteWindowRendering` remains documented as a trial,
  turned off by default, with no guarantee.
- `build/verify-hosts.sh` and the trimmed publish are verified with the
  setting off and on.

## References

- ADR 0070, 0071, 0078, 0095, 0106, 0113, 0124, 0128, 0129, 0131, 0132.
- `Microsoft.AspNetCore.Components.RenderTree.Renderer` (.NET 10).
