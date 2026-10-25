> [Version française](../../../../fr/wasm/dev/adr/0067-measured-cpu-time.md)

# ADR 0067: measured CPU time per process and the "Not responding" state

## Status

Accepted on 2026-10-16.

- **Plan phase:** phase 5: app execution
  ([`execution-isolation-plan.md`](../plans/execution-isolation-plan.md)).
- **Optional:** switch `HackerOsCpuAccounting` /
  `HackerOs:ResourceAccounting:Cpu:Enabled` (ADR 0066).
- **Builds on:** ADR 0043 (ambient execution context), ADR 0057
  (`/proc/stat`, `CpuTimeAccounting`, `ResourceSamplingService`).

## Context

In Blazor WebAssembly, all apps share **a single thread**. The
browser gives no CPU time per app. But at any instant, only one
process runs code: if the OS knows which one, it can time the time
actually spent in each, like a kernel that counts time at each
context switch.

The OS already knows who is running: `HackExecutionContext` carries the current
process in an `AsyncLocal` (ADR 0043), set by the OS at launch, and
by `AmbientComponentDispatch` for component events and rendering.
The `AsyncLocal` follows the code through its asynchronous continuations.

Benoit wants to **measure**, not constrain: no imposed quantum, no automatic
stop. A process that monopolizes the thread is only flagged
"Not responding" in the task manager.

## Decision

### 1. Context switches as measurement points

The ambient frame of `HackExecutionContext` uses the constructor
`AsyncLocal<T>(Action<AsyncLocalValueChangedArgs<T>>)`. The runtime calls this
callback every time the value changes, including when it **restores an
`ExecutionContext`** at the start or end of a continuation
(`ThreadContextChanged == true`). This is the exact equivalent of a context
switch:

- switching to process *P*: *P*'s clock starts;
- leaving *P*: the elapsed time is added to *P*'s CPU time.

The clock is `Stopwatch.GetTimestamp()` (`performance.now()` in the
browser). No `TaskScheduler` and no loop is replaced: the callback
is enough, and it covers events, timers, `Task` continuations and code
launched by the OS.

The callback is installed only if CPU measurement is active (ADR 0066): otherwise
the frame is an ordinary `AsyncLocal`, with no added cost.

### 2. User time and system time

- **`utime`**: time spent in the app's code (ambient frame of its
  process).
- **`stime`**: time spent in the OS gateways called by the app
  (files, network, clipboard). The gateways open a "kernel" subframe of the same
  process; the time is counted there separately.
- Time outside any frame (Blazor rendering, OS loop, system
  timers) is attributed to the session's graphical interface process (the
  "compositor"), like Xorg or `gnome-shell` on Linux. The rest is
  `idle` time.

### 3. Where the time appears

- `/proc/<pid>/stat` (new file, Linux format: `utime` and `stime` in
  `USER_HZ`, fields 14 and 15);
- `/proc/<pid>/status`: `Cpu` becomes the usage measured over the last second,
  with `CpuSource: measured` (ADR 0066);
- `/proc/stat`: `CpuTimeAccounting` (ADR 0057) receives the real `user`,
  `system` and `idle` times instead of the simulated values;
- `HackProcess.TotalProcessorTime`, `UserProcessorTime`,
  `PrivilegedProcessorTime`, modeled on `System.Diagnostics.Process`.

The virtual core is unique as long as processes run on the main thread.
Web Worker processes (ADR 0070) add their cores.

### 4. "Not responding"

The callback knows the duration of each slice. A slice longer than **50 ms**
is a *long task* (browser threshold) and counts in a per-process
counter (`nonvoluntary_ctxt_switches` stays at zero: nothing is preempted).

A process is **Not responding** when a single slice lasts more than
`UnresponsiveThresholdMs` (5 s by default, ADR 0066). On the main thread,
this state is visible **only after the fact**, at the end of the slice, since nothing
else runs in the meantime; it stays displayed until the process has not
responded to a new event in under the threshold. In a Web Worker (ADR 0070),
the OS sees it **during** the blocking, thanks to the worker's heartbeat.

The OS does **nothing** more: no "Wait / Force quit" window,
no stop. The task manager displays "Not responding" in the
state column, and `/proc/<pid>/status` adds `Responding: 0|1`.

### 5. Cost and safeguards

- The callback only reads the clock and adds into an array indexed
  by process: no allocation, no lock on the main thread.
- A performance test compares the time of a reference scenario (open
  ten windows, type in the editor, shell pipeline) with and without the measurement;
  a difference of more than 5% fails the test.
- The counters live in memory and restart from zero at startup.

### 6. Server host

On `HackerOs.Server` (Interactive Server rendering), several threads run
code at the same time: the callback remains correct per thread, but `idle`
time and the compositor no longer have a simple meaning. The measurement is disabled there by
default (ADR 0066) until an ADR specific to this host.

## Consequences

- The displayed CPU becomes real for all apps, without them changing a
  line.
- An infinite synchronous loop is **not** interrupted: it still freezes the
  OS on the main thread. Only a Web Worker (ADR 0070) isolates it.
- `system-metrics.md` and `task-manager.md` document the measurement.

## References

- [`execution-isolation-plan.md`](../plans/execution-isolation-plan.md)
- `Shared/HackerOs.AppSdk/HackExecutionContext.cs`,
  `Shared/HackerOs.AppSdk.Blazor/AmbientComponentDispatch.cs`,
  `Platform/HackerOs.Platform.Core/FileSystem/Virtual/ProcFileSystemProvider.cs`.
- `proc(5)`: `/proc/<pid>/stat`, `/proc/stat`.
- ADR 0043, 0057, 0066, 0068, 0070.
