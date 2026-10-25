# App execution host: main thread or Web Worker

This document describes what an app declares to choose where its process runs (main thread or Web Worker), what crosses a worker boundary, and how to read the page's isolation state. The protocol, the pool, escalation, process limits and measurements are in [`dev/worker-processes.md`](../dev/worker-processes.md).

## Declaring the host

`[AppManifest(Execution = AppExecutionHost.…)]` declares the host; the generator
writes the result to `manifest.json` (`"execution"`).

| Kind | Default (`Default`) | Allowed values |
|---|---|---|
| Window, embedded component | `mainThread` | `mainThread` (validator: `manifest.execution.window`) |
| Command | `pool` | `pool`, `dedicated`, `mainThread` |
| Service | `dedicated` | `dedicated`, `pool`, `mainThread` |

A window (embedded component included) stays on the main thread: `mainThread` is the only value allowed for it.

## By default

On the app side: nothing to do, other than declaring the host when the default does not fit
(see [`app-sdk.md`](app-sdk.md#processus-dans-des-web-workers-adr-0070-et-0071)).

## What crosses the boundary

The OS event types that have a codec in `WirePayloadCodec` (list in ADR 0138 §6) and the types of an app declared by `[assembly: HackWireContext(typeof(MyJsonContext))]` cross the boundary. Any other type throws a `NotSupportedException` in the worker, with the name of the attribute to add; on the main thread, nothing changes.

Gateway calls run by the OS for a worker process keep the same capability checks as locally.

## Cross-origin isolation

An app reads the state without any capability through `HackCrossOriginIsolation.GetStatusAsync()`
(SDK, `Diagnostics` namespace, ADR 0146): the same facts as the first lines of
`/proc/hackeros/workers`. In a worker, it is the asynchronous call `coi.get`.
Diagnostics uses it for its badge.

## `HackBackground` tasks

A `HackBackground` task launched by an app (`HackBackground.RunAsync`) runs in a pool worker when the host has one, or on the main thread when there is no worker, with the same serialization. Details: [`dev/worker-processes.md`](../dev/worker-processes.md#tâches-hackbackground-adr-0128-adr-0140) and [`background-jobs.md`](background-jobs.md).

## See also

- [`app-sdk.md`](app-sdk.md#processus-dans-des-web-workers-adr-0070-et-0071): SDK-side summary.
- [`app-sdk.md`](app-sdk.md#isolation-cross-origin-adr-0146): `HackCrossOriginIsolation`.
