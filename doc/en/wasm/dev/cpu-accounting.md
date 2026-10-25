> [Version française](../../../fr/wasm/dev/cpu-accounting.md)

# Measured CPU time per process and "Not responding"

This document covers the implementation of CPU time measurement. What an app sees (`HackProcess`, `/proc/<pid>/stat`, `/proc/<pid>/status`, `/proc/stat`) and the rules to follow are in [`../framework/process-resources-api.md`](../framework/process-resources-api.md).

Implementation of the `rebuild/5-cpu-accounting` work item of phase 5
([`execution-isolation-plan.md`](plans/execution-isolation-plan.md)):
[ADR 0067](adr/0067-measured-cpu-time.md). It builds on the switches and the
context-switch hook of ADR 0066
([`resource-accounting.md`](resource-accounting.md)).

## Role

- Time how long each process actually spends on the browser thread: `utime`
  in its own code, `stime` in the OS gateways it calls (files, network,
  clipboard, disk space).
- Charge the time outside any process to the session compositor (Blazor
  rendering, OS loop) or to `idle` time.
- Report *long tasks* (a slice longer than 50 ms) and the "Not responding"
  state (a slice longer than `UnresponsiveThresholdMs`), without ever
  interrupting anything.
- Expose these measurements where Linux exposes them: `/proc/<pid>/stat`,
  `/proc/<pid>/status`, `/proc/stat`, and `HackProcess` like
  `System.Diagnostics.Process`.

## Architecture

| Element | Project | Role |
|---|---|---|
| `ProcessCpuTimes`, `MeasuredThreadCpuTimes`, `ICpuTimeAccountant.TryGetProcessTimes` / `TryGetThreadTimes` | `Shared/HackerOs.Simulation.Abstractions` (`Processes/ResourceAccountingContracts.cs`) | contracts read by `/proc` and `CpuTimeAccounting` |
| `ExecutionContextSwitch.FromKernel` / `ToKernel`, `IExecutionModeSwitchObserver` | same | mode (user or kernel) of the process left and of the process entered; mode change without process change |
| `HackExecutionContext.EnterKernel` (internal) | `Shared/HackerOs.AppSdk` | "kernel" sub-frame of the current process; does nothing without a hook |
| `AppFileSystemGateway`, `AppNetworkGateway`, `AppClipboardGateway`, `AppDriveGateway` | `Platform/HackerOs.Platform.Core` | open the kernel sub-frame around each call |
| `MeasuredCpuTimeAccountant` | `Platform/HackerOs.Platform.ResourceAccounting` | the accountant: slices, `utime`/`stime`, compositor, `idle`, long tasks, responsiveness |
| `ContextSwitchDispatcher` | same | also broadcasts mode changes to the measurements that observe them |
| `ResourceAccountingServiceCollectionExtensions` | `OS/HackerOs.Ecosystem` | creates the accountant when CPU measurement is active and registers it on the dispatcher |
| `CpuTimeAccounting` | `Platform/HackerOs.Platform.Core/Metrics` | real `/proc/stat` when the accountant measures |
| `ProcFileSystemProvider` | `Platform/HackerOs.Platform.Core/FileSystem/Virtual` | `/proc/<pid>/stat` (new), measured fields of `status` |
| `HackProcess` | `Shared/HackerOs.AppSdk/Diagnostics` | `UserProcessorTime`, `PrivilegedProcessorTime`, `TotalProcessorTime`, `Responding` |
| `ProcessRow.Responding`, `ProcessesTab` | `Apps/System/HackerOs.Apps.SystemMonitor` | "Not responding" in the status column |

### What the accountant sees

```
             Enter(P)        EnterKernel      end of gateway     Dispose(P)
time ───────────┼───────────────┼──────────────────┼──────────────────┼──────────
outside frame   │  utime of P   │   stime of P     │   utime of P     │ outside frame
(idle or        └──────────── one slice of P (a context switch) ──────┘ (compositor
 compositor)                                                                or idle)
```

- **Process switch** (`OnContextSwitch`): the time elapsed since the last
  event of this thread goes to the process leaving (`utime` or `stime`
  depending on `FromKernel`); its slice ends. Outside a frame, it goes to the
  compositor or to `idle`.
- **Mode switch** (`OnModeSwitch`): the elapsed time goes to the mode left;
  the slice continues (same process).
- Async continuations restore the frame: an `await` in a gateway resumes in
  kernel mode, an `await` in the app resumes in user mode. The kernel
  sub-frame is a copy of the app's frame (same process, same current
  directory).

### Compositor or `idle`

The hook sees the thread leave a process, not the browser event loop emptying.
An interval outside a frame is therefore classified according to how the
process was left:

| Exit | What follows | Charge |
|---|---|---|
| the OS closes the scope itself (event handler, render, launch) | the OS continues: Blazor rendering, window manager | compositor, up to `CompositorSliceBudget` (4 ms, one render pass), the rest to `idle` |
| the runtime restores an empty context (end of a continuation, of a timer) | return to the event loop | `idle` |
| startup, before any process | nothing | `idle` |

Like Xorg or `gnome-shell`, the compositor time is `system` time in
`/proc/stat`. `idle` is the rest of the time elapsed since the accountant was
created: `user + system + idle` always equals the elapsed time.

### Slices, long tasks and "Not responding"

- A **slice** is an uninterrupted occupation of the thread by a process, user
  and kernel modes combined. Each completed slice counts in
  `voluntary_ctxt_switches` (the process gave up the thread);
  `nonvoluntary_ctxt_switches` stays at `0`: nothing is preempted.
- A slice longer than **50 ms** is a *long task* (`LongTasks:`).
- A slice longer than `UnresponsiveThresholdMs` (5 s by default,
  `HackerOs:ResourceAccounting:Cpu:UnresponsiveThresholdMs`) puts the process
  in **Not responding** (`Responding: 0`); it becomes `1` again as soon as it
  finishes a slice under the threshold, that is, it responds quickly to the
  next event.
- On the main thread, the state is only visible **after the fact**, at the end
  of the slice: nothing else runs meanwhile. An infinite loop still freezes
  the OS; only a Web Worker (ADR 0070) isolates it.
- A window whose process stays "Not responding" gets the suffix "(Not
  responding)", a gray veil and the box "Wait | End the application" (ADR 0127
  §2-3, [`process-priority-and-unresponsive-windows.md`](process-priority-and-unresponsive-windows.md)).
- In a Web Worker (commands and services, [`worker-processes.md`](worker-processes.md)),
  the worker measures the slices of its processes with the same hook and sends
  them in its heartbeat (250 ms); the OS adds them to the kernel time spent
  serving their calls. A worker with no heartbeat for 5 s puts its processes
  in **Not responding during** the blockage, and this time counts as busy
  (ADR 0072 §6).

### Cost

Resolution: in the browser, `Stopwatch.GetTimestamp` reads
`performance.now()`, rounded to 100 µs, or 5 µs when the page is isolated
(ADR 0131 §4, ADR 0143 §7); `/proc/hackeros/workers` displays the measured
value (`timer-resolution-us`).

The hot path reads the clock (`Stopwatch.GetTimestamp`), a few `[ThreadStatic]`
fields, and adds with `Interlocked` into a slot found by PID in an array: no
lock, no allocation, except the first time a process runs (its slot) and when
PIDs exceed the array (it doubles, under a lock, off the hot path). The
per-thread state keeps the count correct when tests or the server run code on
several threads.

`EnterKernel` returns a struct: a gateway call allocates nothing for the
scope, and without a hook (measurements off) it does nothing at all.

## Usage

How an app reads these measurements is described in [`../framework/process-resources-api.md`](../framework/process-resources-api.md). What follows concerns configuration.

### Configure

See [`resource-accounting.md`](resource-accounting.md): MSBuild property
`HackerOsCpuAccounting`, `HackerOs:ResourceAccounting:Cpu` section of the
`appsettings.json` files (`Enabled`, `UnresponsiveThresholdMs`). When off,
the measurement installs nothing (if memory is off too, no hook) and `/proc`
returns exactly the files from before phase 5, plus the new simulated `stat`.

## Decisions and clarifications relative to ADR 0067

- **The compositor is not a process.** The session has no graphical interface
  process in the process table; the compositor time is held by the accountant
  (`MeasuredThreadCpuTimes.Compositor`) and counted as `system` in
  `/proc/stat`, without a `/proc/<pid>` line. The day the session has such a
  process, it will suffice to charge this counter to it.
- **Compositor budget.** The hook does not see the return to the event loop;
  the 4 ms budget per interval prevents the compositor from absorbing the wait
  for events. This is an approximation: long OS work outside any process is
  partly counted as `idle`.
- **Kernel mode goes through a separate interface.** The
  `IExecutionContextSwitchObserver` contract does not change meaning (process
  switches only); `ExecutionContextSwitch` gains two optional fields
  (`FromKernel`, `ToKernel`) and mode changes go to
  `IExecutionModeSwitchObserver`, which the memory measurement can ignore.
- **Usage over the last second computed at read time.** The accountant keeps,
  per process, the total at the last sample; a read more than one second after
  the previous sample takes a new one. No additional timer.
- **`/proc/<pid>/stat` exists even with measurement off.** It is a new Linux
  file; simulated, it stays consistent with `Cpu` of `status`.

## Tests

| Suite | Covers |
|---|---|
| `HackerOs.Platform.ResourceAccounting.Tests` (`MeasuredCpuTimeAccountantTests`) | `utime` in a scope, `stime` in a real gateway (directly and through the composed dispatcher as in the OS), nested processes, long tasks and "Not responding" then return, compositor and budget, `idle` after a continuation, sum equal to elapsed time, usage over the last second, never-run process, array growth, real async continuations |
| same (`RealAppsMeasurementTests`) | plan section 5.1: the real Calculator (window), the real `echo` (command) and the sample service, without changing their code; `/proc/<pid>/stat` (52 fields, `utime`/`stime`) and `status` (`CpuSource: measured`, `Responding: 1`) |
| same (`CpuAccountingOverheadTests`, `Performance` category) | reference scenario (ten windows, typing in an editor with rendering on each key and gateway calls, `echo \| grep` pipeline) with and without measurement: a gap of more than 5% fails the test (best of 9 alternating rounds, three attempts) |
| `HackerOs.Platform.Core.Tests` | format of measured and simulated `/proc/<pid>/stat`, fields added to `status` only when measured, measured `/proc/stat` on one core, `HackProcess.*ProcessorTime`, `Responding`, `ParseStatTimes` |
| `HackerOs.Ecosystem.Tests` | accountant created and registered only if CPU measurement is active (16 combinations), threshold read from configuration, `/proc/stat` composed with the measurement |
| `HackerOs.Apps.SystemMonitor.Tests` | "Not responding" in the status column and the sort |

Checked by hand: a `HackerOs.Ecosystem` build with
`-p:HackerOsCpuAccounting=false -p:HackerOsMemoryAccounting=false` contains no
`HackerOs.Platform.ResourceAccounting*` file.

## Task list

- [x] Contracts: `ProcessCpuTimes`, `MeasuredThreadCpuTimes`, `TryGetProcessTimes`, `TryGetThreadTimes`, kernel mode in `ExecutionContextSwitch`, `IExecutionModeSwitchObserver`.
- [x] Kernel sub-frame `HackExecutionContext.EnterKernel`, no cost when the hook is absent.
- [x] File, network, clipboard and disk gateways in kernel mode.
- [x] `MeasuredCpuTimeAccountant`: `utime`/`stime`, compositor, `idle`, slices, long tasks, "Not responding".
- [x] Mode change broadcast by `ContextSwitchDispatcher`; composition when CPU measurement is active.
- [x] `/proc/<pid>/stat` (`proc(5)` format), measured fields of `/proc/<pid>/status`, real `/proc/stat` through `CpuTimeAccounting`.
- [x] `HackProcess.UserProcessorTime`, `PrivilegedProcessorTime`, `TotalProcessorTime`, `Responding`.
- [x] "Not responding" status column of the task manager (display only).
- [x] Measurement off: previous behavior (verified at build and by tests).
- [x] Overhead test (< 5%).
- [x] Existing apps (section 5.1): no app code changes; tests on a real window, command and service.
- [x] Documentation: this document, `resource-accounting.md`, `system-metrics.md`, `task-manager.md`, `virtual-proc-and-dev.md`, `app-sdk.md`, `sdk-reference.md`, plan, index.
