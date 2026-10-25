> [Version française](../../../../fr/wasm/dev/adr/0124-remote-event-and-watch-gateways.md)

# ADR 0124: remote event and file watch gateways, pool open to any app assembly

## Status

Accepted on 2026-10-18.

- **Plan:** [`execution-followups-plan.md`](../plans/execution-followups-plan.md),
  decision 1; written by the work item `exec/0-adr` (branch `exec/1-adr`), implemented
  by `exec/1-remote-gateways`.
- **Replaces:** ADR 0072 §3 for `HackEvents` and `Watch` (the rest of this
  paragraph, shell host and administration, is unchanged).
- **Complements:** ADR 0070 §4 (loading assemblies in a worker),
  ADR 0080 §1 (`hackwebd` on the main thread, which becomes moot).

## Context

Since ADR 0072, the web and v3 phases have added remote gateways to the
execution context of a worker (`RemoteAppExecutionContext`): catalog
and default applications, intents, service control and notification,
clipboard, notifications, listening ports, a site's database. The following remains, on
2026-10-18 on `v3.1/base`:

| Member | Behavior in a worker |
|---|---|
| `IAppEventGateway` (6 members: typed events and topics) | `NotSupportedException` (`RemoteEventGateway`) |
| `IAppFileSystemWatchGateway.WatchAsync` | `NotSupportedException` (`RemoteWatchGateway`) |
| `IAppProcessGateway.StartChild`, synchronous `Kill` and `ListProcesses` | `NotSupportedException`; the asynchronous forms exist (`KillAsync`, `GetProcessesAsync`) |
| `IAppFileSystemGateway.WithSelectedHandle` | `NotSupportedException` |

Two apps remain on `MainThread` for this reason alone: the sample service
(`ServiceSampleApp`, typed topic `SampleTickerEvent`) and `hackwebd`
(`HackFileSystemWatcher` on its configuration folders, ADR 0080 §1).

In addition, the pool preloads `HackerOs.Commands.dll` and loads the other
assemblies through `INTERNAL.loadLazyAssembly` at the first `start`; a command
from another assembly (a sample project, an app packaged by NuGet in a
host) has never been tested in a worker. Installing apps **at runtime**
remains deferred (ADR 0094): all app assemblies are known at build time
(`BuildKnownLazyAssemblies`).

## Decision

### 1. Events between a worker and the OS

The event bus stays in the OS, on the main thread. A worker accesses it
through messages, like an X11 client talks to the server:

- **Subscription**: `Subscribe` and `SubscribeChannel` send the operation
  `ev.sub` (topic or type, channel capacity). The OS creates the subscription **on behalf
  of the pid**, with its rights, and pushes each message through an `ev` message
  (subscription identifier, serialized payload). The worker delivers the messages
  in order, in the process's ambient context (like `DeliverIntent`).
- **Unsubscription**: `Dispose` sends `ev.unsub`; the end of the process, the
  death of its worker and `SIGKILL` unsubscribe on the OS side without waiting for the worker.
- **Publication**: `Publish` sends `ev.pub` and waits for the
  `TopicPublishResult` (or the list of `EventDispatchFault`) from the OS.
  `RegisterSharedChannel` goes through `ev.chan` with the same policy as a local
  call.
- **Pressure**: a bounded channel keeps its capacity on the OS side; when it overflows,
  the channel's rule applies (as on the main thread). A callback
  subscription has a credit of 256 messages in flight; beyond that, the OS waits for
  the worker's acknowledgement, as for streams (64 KiB credit, ADR 0072 §2).

### 2. What crosses: declared serializable payloads

An object from the OS heap cannot be handed to another runtime. A payload
crosses if its type is **serializable by System.Text.Json with generated
metadata** (trimming-compatible, ADR 0113):

- the event types of the SDK and of the OS contracts (`FileSystemChangeEvent`
  and the other public events) get their codec in the workers protocol
  (`WireCodec`, without reflection);
- a type defined by the app is declared by a `JsonSerializerContext` of its
  assembly, named by a new SDK attribute:
  `[assembly: HackWireContext(typeof(MyAppJsonContext))]`. The same attribute
  serves the tasks of ADR 0128.

A type that is neither throws, **in the worker**, a
`NotSupportedException` whose message names the type and the attribute to add.
On the main thread, nothing changes: objects are still passed by reference.

### 3. File watching

`WatchAsync` sends `watch.open` (path, scope). The OS opens the watch
on behalf of the pid, with its read rights, and pushes the
`FileSystemChangeEvent` through the channel of §1. Closing, process death and
overflow follow §1.

### 4. Pool open to any assembly known at build time

- The pool loads on demand **any** assembly of `BuildKnownLazyAssemblies`
  (entry point validated by the OS discovery, ADR 0113), and keeps it
  loaded for this worker's subsequent requests.
- `PreloadAssemblies` remains a host setting; it only contains
  `HackerOs.Commands.dll` by default.
- An assembly that is not in the list is rejected (`TypeLoadException`
  reported as a launch failure, exit code 127). Runtime installation
  does not change this rule until its own ADR exists.

### 5. What remains refused in a worker

`StartChild` and the synchronous forms of `Kill` and `ListProcesses` remain
refused (their asynchronous forms are enough); `WithSelectedHandle`, the shell
host and administration too. An app that needs them keeps `MainThread`
with the reason in a comment (ADR 0070 §1).

### 6. Migration

- `ServiceSampleApp` and `hackwebd` move to a dedicated worker (kind `Service`,
  ADR 0070); their reason comment disappears.
- Each other app on `MainThread` is reviewed: the reason stays written if it
  still holds.

## Consequences

- New protocol operations (`ev.sub`, `ev.unsub`, `ev.pub`, `ev.chan`,
  `watch.open`, `ev` message) documented in
  [`worker-processes.md`](../worker-processes.md).
- New public API `HackWireContextAttribute` (SDK 1.2, ADR 0132).
- Parity tests: each `HackEvents` and `Watch` scenario runs on the main
  thread and in the workers' local loop (`LoopbackWebWorkerRuntime`)
  with the same result; a command from the third-party sample project runs in a
  worker in the E2E tests.
- An undeclared payload fails in a worker and works on the main thread:
  this is intended (the error message says so), and the SDK guide explains it.

## References

- ADR 0070, 0072, 0080, 0094, 0113, 0128, 0132.
- [`worker-processes.md`](../worker-processes.md),
  [`hack-file-api.md`](../../framework/hack-file-api.md).
