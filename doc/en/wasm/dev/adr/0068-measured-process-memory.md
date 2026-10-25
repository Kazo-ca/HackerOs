> [Version française](../../../../fr/wasm/dev/adr/0068-measured-process-memory.md)

# ADR 0068: layered measured memory per process

## Status

Accepted on 2026-10-16.

- **Plan phase:** phase 5: app execution
  ([`execution-isolation-plan.md`](../plans/execution-isolation-plan.md)).
- **Optional:** switch `HackerOsMemoryAccounting` /
  `HackerOs:ResourceAccounting:Memory:Enabled` (ADR 0066).
- **Builds on:** ADR 0067 (ambient frames and context switches).

## Context

All apps share the same .NET heap. The runtime does not say which share
of the heap belongs to which app, and the browser only gives a global
measurement, and only with cross-origin isolation, which we rule out.

Counting the *marshaling* (data exchanged between C# and JavaScript) was
considered and ruled out: it measures interop traffic, not memory. A pure
C# app that holds 200 MB would display zero.

Benoit wants to **only measure**: no per-process limit, no OOM
killer.

## Decision

### 1. Four layers

| Layer | Source | Accuracy | `/proc/<pid>/status` field |
|---|---|---|---|
| Image | size of the app's assemblies, known from its `manifest.json` and the host catalog; a shared assembly is counted once, in shared `RssFile` | exact | `VmExe`, `RssFile` |
| OS resources | bytes held by the OS for the process: `HackFileStream` buffers, clipboard entries, pipes, sockets, open windows (fixed cost per window) | exact | `VmData` (kernel share) |
| Allocations | `GC.GetAllocatedBytesForCurrentThread()` read at each context switch (ADR 0067, section 1); a single thread runs code, so the difference goes to the current process | exact in allocated bytes | `HackAllocBytes` |
| Resident memory | after each GC (`GC.RegisterForFullGCNotification` does not exist in WASM: `GC.CollectionCount(0)` is compared at each sample), the real heap (`GC.GetGCMemoryInfo().HeapSizeBytes`) is distributed among processes in proportion to their recent allocations, with an exponential decay | estimate | `VmRSS`, `RssAnon` |

`VmRSS = RssAnon + RssFile + kernel share`. The calculation rules (decay
window, cost of a window) are constants documented in
`system-metrics.md` and covered by tests.

### 2. Reading

- `/proc/<pid>/status` gives the fields above, and `MemSource: measured`
  (ADR 0066);
- `/proc/meminfo` sums the processes and keeps `MemTotal` from the
  `VirtualHardwareProfile`;
- `HackProcess.WorkingSet64` (already exists, read from `VmRSS`) and the new
  `PrivateMemorySize64` (read from `RssAnon`), modeled on
  `System.Diagnostics.Process`.

### 3. No limit

No limit, no OOM killer: exceeding `MemTotal` only saturates the
charts. If a limit becomes useful, a new ADR will decide it.

### 4. In a Web Worker

A process alone in its worker (ADR 0070) has its own runtime: its heap
is **exactly** its own, and the worker sends it with its heartbeat.
In a pooled worker, the distribution of section 1 applies inside the
worker.

## Consequences

- The displayed memory becomes credible for all apps, without any change
  to their code.
- The resident layer remains an estimate, which `system-metrics.md` states
  clearly.
- Reading the allocation counter at each context switch is done in the
  same callback as ADR 0067. If only memory is active, the callback is
  installed for it alone.

## References

- [`execution-isolation-plan.md`](../plans/execution-isolation-plan.md)
- `proc(5)`: `/proc/<pid>/status`, `/proc/meminfo`.
- ADR 0012, 0057, 0066, 0067, 0070.
