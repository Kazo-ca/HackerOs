# ADR 0129: trial of rendering a Blazor window in a worker, and criteria for going ahead

## Status

Accepted on 2026-10-18 **as a trial**. Nothing is delivered to apps by this ADR.

- **Plan:** [`execution-followups-plan.md`](../plans/execution-followups-plan.md),
  decision 7, step 2 (Benoit's answer: "keep the trial"); led by
  `exec/6-remote-renderer-poc`. The follow-up (`exec/7-window-workers`) only happens
  if the trial passes the criteria of §4.
- **Reserve:** ADR **0133** for the "go ahead" or "stop"
  decision, written by `exec/6` with the measurements. If we go ahead, that ADR 0133
  replaces ADR 0070 §1 for windows.

## Context

A window app runs on the main thread because a worker has no
DOM (ADR 0070 §1). `HackBackground` (ADR 0128) moves computations out, not the
interface logic. A display server (X11, Wayland) shows another
way: the client computes what to draw, the server draws and sends back
the inputs. Blazor already does this in server mode (render batches sent to the
browser through SignalR), but the binary format of these batches
(`RenderBatchWriter`) and the JavaScript applier are internal to the framework.

## Decision

### 1. What the trial builds

- In a worker: a `Renderer` derived from the public class
  `Microsoft.AspNetCore.Components.RenderTree.Renderer` (.NET 10), which renders the
  window's root component and serializes each `RenderBatch` (public types
  `RenderTreeFrame`, `RenderTreeEdit`) in a format **of our own**,
  versioned, encoded by `WireCodec`.
- On the main thread: a platform window host that receives the batches
  and applies them to the DOM of the window's content through a small
  JavaScript module specific to the component (`RemoteWindowHost.razor.js`, AGENTS.md I.2),
  then sends back to the worker the DOM events (`click`, `input`, `change`,
  `keydown`, `focus`…) and bound values.
- The chrome (title bar, borders, dialog of ADR 0127) remains drawn by
  the main thread: a window that freezes in its worker can still be moved and closed.
- Subject of the trial: the **Calculator** (no JavaScript interop of its own, isolated
  CSS, keyboard).

### 2. What the trial does not do

- No reflection on private framework members, no call to the internal APIs
  of `blazor.web.js`: this is the condition for surviving .NET
  updates.
- No JavaScript interop of an app in the worker: an app that has some
  (CodeEditor and Monaco, Terminal and xterm.js) stays on the main thread
  regardless.
- No .NET WebAssembly threads (`WasmEnableThreads`, rejected by Benoit,
  question Q8).

### 3. Measurements requested

On Chromium, reference profile of `build/verify-hosts.sh`, in trimmed
Release (ADR 0106):

- latency added between a click and the next frame (p50, p95), compared to the
  same Calculator on the main thread;
- memory of a window worker (runtime and assemblies);
- window opening time;
- average size of a render batch;
- coverage: isolated CSS, `@bind`, focus and `Tab`, window manager
  shortcuts (ADR 0095), detached window (ADR 0078), SDK dialog.

### 4. Criteria for going ahead

ADR 0133 concludes "go ahead" if **all** are met; otherwise "stop"
and nothing changes for apps.

| Criterion | Threshold |
|---|---|
| Added latency, p95 | ≤ 16 ms (one frame at 60 Hz) |
| Memory of a window worker | ≤ 40 MiB |
| Window opening | ≤ 1 s more than on the main thread, worker already ready (ADR 0071) |
| Calculator UI tests | all green, without modifying the app |
| Accessibility | axe and keyboard suite green |
| Framework | no internal or private API used |

## Consequences

- `exec/6` delivers a prototype behind a host setting turned off by default,
  the measurements in [`worker-processes.md`](../worker-processes.md) and ADR
  0133.
- If we go ahead, `exec/7` allows `AppExecutionHost.Dedicated` for windows without
  their own JavaScript interop (the manifest validator forbids it
  today) and takes inventory of the base apps.

## References

- ADR 0070, 0071, 0078, 0095, 0106, 0127, 0128.
- X11 protocol, Wayland protocol; ASP.NET Core `Renderer` and `RenderBatch`.
