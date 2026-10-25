> [Version française](../../../fr/wasm/dev/memory-accounting.md)

# Measured per-process memory, in layers

This document covers the implementation of layered memory measurement. What an app sees (`HackProcess.WorkingSet64`, `PrivateMemorySize64`, fields of `/proc/<pid>/status` and `/proc/meminfo`, measured or simulated) is in [`../framework/process-resources-api.md`](../framework/process-resources-api.md).

Implementation of the `rebuild/5-memory-accounting` work item of phase 5
([`execution-isolation-plan.md`](plans/execution-isolation-plan.md)):
[ADR 0068](adr/0068-measured-process-memory.md), wired onto the switches and
the context-switch hook of [ADR 0066](adr/0066-optional-resource-accounting.md)
([`resource-accounting.md`](resource-accounting.md)).

## Role

All apps share the same .NET heap: the runtime does not say which part belongs
to which app. The measurement reconstructs each process's memory in four
layers, like `/proc/<pid>/status` on Linux, **without changing app code**:

| Layer | Source | Precision | `/proc/<pid>/status` field |
|---|---|---|---|
| Image | size of the app's assemblies, measured by the host build | exact | `VmExe` (the app's assembly), `RssFile` (+ shared assemblies) |
| OS resources | bytes the OS holds for the process: `HackFileStream` buffers, clipboard, pipes, in-flight sockets, fixed cost per window | exact | `VmData` (kernel part) |
| Allocations | `GC.GetAllocatedBytesForCurrentThread()` read at each context switch | exact | `HackAllocBytes` |
| Resident | real heap distributed after each GC in proportion to recent allocations, with exponential decay | estimate | `RssAnon` |

`VmRSS = RssAnon + RssFile + VmData`. The measurement limits nothing by
itself: the per-process `rss` limit comes from ADR 0125
([`process-limits.md`](process-limits.md)) and the OOM killer, **disabled by
default**, from ADR 0126 (which replaces ADR 0068 §3) and ADR 0142
([`oom-killer.md`](oom-killer.md)); it only acts if memory is measured.

Measurement off (at build by `HackerOsMemoryAccounting=false`, or by
`HackerOs:ResourceAccounting:Memory:Enabled=false`): exact behavior from
before phase 5 (simulated `VmRSS`, no layer line, three-line `meminfo`,
`MemSource: simulated`).

## Architecture

| Element | Project | Role |
|---|---|---|
| `IMemoryAccountant` (`TryGetProcessMemory`, `TryGetSystemMemory`), `ProcessMemoryUsage`, `SystemMemoryUsage` | `Shared/HackerOs.Simulation.Abstractions` (`Processes/`) | contract read by `/proc`; `false` = simulated value |
| `IKernelMemoryLedger`, `KernelMemoryCharge`, `KernelMemoryKind` | same (`Processes/MemoryAccountingContracts.cs`) | ledger of the memory held by the OS for a process |
| `NullMemoryAccountant`, `NullKernelMemoryLedger` | `Platform/HackerOs.Platform.Core` (`Processes/Accounting/`) | measurement off: nothing is measured, no charge costs anything |
| `AppImage`, `AppImageAssembly`, `BuiltInApplication.Image` | `Shared/HackerOs.App.Abstractions` | image of an app in `BuiltInApplications.json` |
| `AppImageInspector` | `Tools/HackerOs.Tools.ManifestGenerator` | reads the assembly references (metadata, without loading code) and measures the files |
| `HostAppCatalog.Images` (`BuildKnownLazyApps.Images` in the Ecosystem) | `OS/HackerOs.Ecosystem.Host` | host catalog images, by app id |
| `AllocationTracker` | `Platform/HackerOs.Platform.ResourceAccounting` (`Memory/`) | allocations layer: observer of the context-switch hook |
| `KernelMemoryLedger` | same | OS resources layer |
| `MeasuredMemoryAccountant`, `MemoryAccountingConstants` | same | the four layers, heap distribution, the constants |
| `IManagedHeapProbe`, `GcManagedHeapProbe` | same | what is read from the GC (replaceable in tests) |

### Image

The host build measures each app's image during aggregation
(`CollectHackerOsBuiltInApplications`, [`app-model.md`](../framework/app-model.md)):

- `GetHackerOsAppManifests` also returns the path of the app's assembly
  (`AssemblyPath`) and its dependency list (`DependenciesFile`, the file that
  `GenerateHackerOsAppManifests` already writes);
- `AppImageInspector` takes the size of the app's assembly (unshared),
  follows its references, direct or not, and measures those it finds in the
  list (shared);
- the runtime and the framework (`System.*`, `Microsoft.*`, `netstandard`,
  `mscorlib`) do not count: they are the OS itself, like the kernel and the
  libc shared by all processes. Reference assemblies (`ref` folder) are
  ignored.

Several apps in the same assembly (the commands of `HackerOs.Commands`) each
have this assembly as `VmExe`, like each BusyBox applet maps the BusyBox
binary. `/proc/meminfo` counts each assembly **once** (`Mapped`), regardless
of the number of processes that map it.

An app with no known image (installed at runtime) has no image layer.

### OS resources

An OS service that holds memory for a process opens a `KernelMemoryCharge` on
the ledger, resizes it when its buffer changes and releases it at the end:

| Object | Who charges | Size | Charged to |
|---|---|---|---|
| `HackFileStream` write buffer | the stream, through `IAppFileSystemGateway.ChargeStreamBuffer()` | capacity of the `MemoryStream` | the app's process; released when the stream closes |
| Clipboard | `SystemClipboard`, through `AppClipboardGateway` | 2 bytes per character (MIME types and contents) | the process that copied; released when the content is replaced or cleared; nothing for the OS or the host |
| Shell pipe | `ShellPipe`, created by `ShellInterpreter` | 2 bytes per pending character | the shell process that created the pipe; released when reading closes or when writing is closed and the pipe drained |
| Socket | `AppNetworkGateway`, for each call | `SocketBufferBytes` = 144 KiB (`tcp_rmem` 128 KiB + `tcp_wmem` 16 KiB) | the app's process, for the duration of the call |
| Window | `MeasuredMemoryAccountant` (direct read) | `WindowBytes` = 512 KiB | each windowed app process |

`IAppFileSystemGateway.ChargeStreamBuffer()` is a default interface method
that returns `KernelMemoryCharge.None`: test doubles and existing gateways do
not change.

### Allocations

`AllocationTracker` is registered on the `ContextSwitchDispatcher` from
composition. At each context switch, it reads the thread's allocation counter
and attributes the difference since the previous reading to the process that
just left the thread (`From`); without a process (rendering, compositor, OS
services), it goes to the OS. The last reading and the current process are
kept per thread: before each `/proc` read, the current slice is attributed
(`FlushCurrentThread`), so a process reading its own `/proc/self/status`
already sees its allocations there, like a running task under Linux. The hot
path is lock-free and, once the process counter is created, allocation-free.

### Resident

At each read (`status`, `meminfo`), `MeasuredMemoryAccountant`:

1. ages the weight of each living process (and of the OS) by
   `0.5^(Δt / ResidentHalfLife)` and adds what it has allocated since the
   previous read;
2. forgets dead processes (allocations and charges);
3. if the generation 0 GC count has changed (WebAssembly has no
   `GC.RegisterForFullGCNotification`), or on the first read, takes the heap
   size again (`GC.GetGCMemoryInfo().HeapSizeBytes`, or
   `GC.GetTotalMemory(false)` if the runtime answers zero), removes the
   kernel part already named, and distributes the rest among the processes
   and the OS in proportion to their weights. The OS part goes to `Slab` in
   `meminfo`.

Between two GCs the distribution does not move: a process launched since the
last GC does not yet have `RssAnon`.

### Process in a worker

A process in a Web Worker (ADR 0070) additionally receives, in `RssAnon`, its
share of the memory of its worker's runtime (distributed among the worker's
processes) and its allocations measured in the worker; `/proc/meminfo` counts
the runtime of each worker. See [`worker-processes.md`](worker-processes.md).
When the page is isolated (ADR 0131, 0143 §6), a worker's runtime memory comes
from `performance.measureUserAgentSpecificMemory()` (every 20 s, valid for
three periods) instead of the heartbeat; `MemSource` stays `measured`, and
`/proc/hackeros/workers` says `memory-measure: browser`.

## Constants

Defined in `MemoryAccountingConstants` and `AppNetworkGateway`, recalled in
[`system-metrics.md`](system-metrics.md), verified by
`MeasuredMemoryAccountantTests.The_constants_are_the_documented_ones` and the
load tests:

| Constant | Value | Reason |
|---|---|---|
| `ResidentHalfLife` | 10 s | a process that has just built a large document stays heavy for a while; an idle process fades out in a minute (six half-lives, under 2%) |
| `WindowBytes` | 512 KiB | what the window manager and compositor keep per window (surface, render tree, DOM nodes), invisible to the allocation counter since rendering runs outside the app's frame |
| `AppNetworkGateway.SocketBufferBytes` | 144 KiB | Linux default TCP buffers (`tcp_rmem` 128 KiB, `tcp_wmem` 16 KiB) |

## Usage

Reading `/proc/<pid>/status`, `/proc/meminfo` and the `HackProcess` properties is described in [`../framework/process-resources-api.md`](../framework/process-resources-api.md).

### Charging an OS resource (OS code)

```csharp
using KernelMemoryCharge buffer = ledger.Charge(pid, KernelMemoryKind.Pipe);
buffer.Resize(bufferedChars * sizeof(char));   // on each change
// Dispose releases everything
```

### Composition

`AddHackerOsResourceAccounting` (`HackerOs.Ecosystem.Host`) composes the
measurement when `plan.MemoryActive` and
`ResourceAccountingFeatures.IsMemoryAccountingSupported`: `AllocationTracker`
registered on the dispatcher, `KernelMemoryLedger` (`IKernelMemoryLedger`),
`MeasuredMemoryAccountant` (`IMemoryAccountant`), with the images the host
passes to it (`AddHackerOsResourceAccounting(configuration, images)`;
`AddHackerOsBrowserHost` passes `HostAppCatalog.Images`, the server and
`test/test` pass `BuildKnownLazyApps.Images`; without images, no app has an
image layer). Otherwise, the null implementations are registered.
`AddHackerOsEcosystem` passes the ledger to the clipboard, the shell, and the
file and network gateways.

## Decisions

- **Image in `BuiltInApplications.json`, not in `manifest.json`.** The
  versioned manifest keeps exactly schema 1 (ADR 0047); sizes change at every
  build and have no place in a PR diff. The host catalog is produced in `obj/`
  at every build: it receives an optional `image` field.
- **Framework outside the image.** Counting `System.*` in each app would make
  all apps identical giants; these assemblies are the OS.
- **Lazy reading, no sampler.** The distribution happens when someone reads
  `/proc`; nobody reads, no work. The `ResourceSamplingService` is not touched
  (it still feeds the simulated CPU; the CPU measurement, ADR 0067, does not
  use it either: see [`cpu-accounting.md`](cpu-accounting.md)).
- **`IMemoryAccountant` contract widened.** `TryGetResidentBytes(pid)` becomes
  `TryGetProcessMemory(process, active, …)` and
  `TryGetSystemMemory(active, …)`: the accountant needs the app type (window)
  and the id (image), and the table of living processes to distribute the
  heap, without itself depending on the process manager (singleton versus
  scoped service).
- **Kernel part removed from the heap before distribution.** The buffers of
  streams and the clipboard live in the heap: without this removal, they would
  be counted twice in `VmRSS`.
- **Pipe charged to the shell.** Under Linux, a pipe's pages are charged to
  the memory group of whoever creates it; here, the shell.

## Tests

| Suite | Covers |
|---|---|
| `HackerOs.Platform.ResourceAccounting.Tests` | `AllocationTracker` (attribution between two switches, first read, forgetting, real hook), `KernelMemoryLedger`, `MeasuredMemoryAccountant` (constants, image, kernel part and window, distribution, only after a GC, half-life, kernel part removed, shared assemblies counted once, simulated or measured, dead processes), real `ls` command under the real hook |
| `HackerOs.Samples.ServiceApp.Tests` | real service (`SampleTickerService`) measured under the real hook, without changing its code |
| `HackerOs.Ecosystem.Tests` | all catalog apps have their image; the text editor (window) measured with the real size of its DLL; composition: measurement, ledger and observer present exactly when memory is active |
| `HackerOs.Platform.Core.Tests` | format of measured `status` and `meminfo`, simulated process unchanged; clipboard and pipe charges; `HackProcess.PrivateMemorySize64` |
| `HackerOs.AppSdk.Tests` | write stream buffer charged then released, nothing for a read stream |
| `HackerOs.Network.Tests` | socket charged for the duration of the call |
| `HackerOs.App.Abstractions.Tests`, `HackerOs.Tools.ManifestGenerator.Tests` | strict `image` field, round trip; aggregation and image measurement; framework excluded |

## Task list

- [x] Contracts: `ProcessMemoryUsage`, `SystemMemoryUsage`, widened `IMemoryAccountant`, `IKernelMemoryLedger`, `KernelMemoryCharge`; null implementations.
- [x] Image layer: `AppImageInspector`, `image` field of `BuiltInApplications.json`, MSBuild targets, `BuildKnownLazyApps.Images`, for all apps.
- [x] OS resources layer: write streams, clipboard, pipes, sockets, per-window cost.
- [x] Allocations layer: `AllocationTracker` on the context-switch hook.
- [x] Resident layer: heap distribution after GC, exponential decay, documented and tested constants.
- [x] Composition according to ADR 0066; measurement off = exact previous behavior.
- [x] `/proc/<pid>/status`, `/proc/meminfo`, `HackProcess.PrivateMemorySize64`.
- [x] Tests on a real windowed app, command and service, without changing their code.
- [x] Documentation and index.
