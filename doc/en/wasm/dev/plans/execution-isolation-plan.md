> [Version française](../../../../fr/wasm/dev/plans/execution-isolation-plan.md)

# Phase 5: app execution: CPU and memory measurement, clean stop, Web Workers

**Status:** plan approved with Benoit on 2026-10-16, **finished on 2026-10-16**
(PRs #124 to #129, then final verification `rebuild/5-final`, section 6). It extends
[`ecosystem-rebuild-plan.md`](ecosystem-rebuild-plan.md) (phases 1 to 4,
merged into `main` by PR #114) and lifts the "execution isolation"
exclusion from its section 9. The decisions are in ADRs 0066 to 0071.

## 1. Purpose

- **Measure** the real CPU time and memory of each process, in place
  of the values simulated by `ResourceProfile`, and display "Not responding"
  for a process that monopolizes its thread.
- Allow a process to **terminate cleanly** when the OS asks it
  to stop, and to yield during a long operation.
- **Isolate** processes without an interface (commands, services) in Web
  Workers, so that an infinite loop no longer freezes the OS and a kill is real.

## 2. Starting point

- Blazor WebAssembly runs all apps on **a single thread**, in a single
  .NET heap. The browser measures neither CPU nor memory per app.
- `/proc/<pid>/status` (`VmRSS`, `Cpu`) and `/proc/stat` display values
  drawn from the app's `ResourceProfile` (ADR 0012, 0057). Disk and network
  are already really counted.
- `HackExecutionContext` knows at every moment which process is running code
  (`AsyncLocal`, ADR 0043): this is the basis of the measurement.
- `StopAsync` already does `SIGTERM` then `SIGKILL`; "End task" does a
  `kill -9`.
- The hosts publish without trimming (`PublishTrimmed=False`).

## 3. Principles

1. **Measure, do not constrain.** No imposed quantum, no memory limit,
   no OOM killer, no "Force Quit" window.
2. **Optional.** Each measurement can be turned off at build (the code is not shipped) and
   in `appsettings.json` (no hook installed). Turned off, we return
   exactly to the current behavior.
3. **Mimic the Metal.** Context switches, `utime`/`stime`, compositor,
   `SIGTERM`/`SIGKILL`, workers = virtual cores, `proc(5)` formats.
4. **The SDK does not change for existing apps.** An app gains the measurement
   without changing a line; a command does not know whether it runs in a
   worker.
5. **Existing apps follow each new feature in the same work item.**
   Each work item inventories the apps, commands and services of
   `Apps/` concerned and migrates them before its PR (section 5.1): it is not
   left to a later work item.
6. **The three hosts stay operational after each work item.**
   `OS/HackerOs.Ecosystem` (standalone PWA, no server), `test/test` (debug
   Blazor Web App) and `Server/HackerOs.Server` (Interactive Server rendering)
   must build, publish and **start up to the desktop** before any
   merge. The startup verification (section 5.2) is part of the "done"
   of each `rebuild/5-*` PR, with measurements on and off.

## 4. Decision register

| # | Decision | ADR |
|---|---|---|
| 1 | Independent CPU and memory measurements, which can be turned off at build (project `HackerOs.Platform.ResourceAccounting` referenced conditionally) and at startup (`appsettings.json`); turned off, return to the simulator with `CpuSource`/`MemSource: simulated` and the "estimated" label. | 0066 |
| 2 | CPU time measured by the value-change callback of the ambient `AsyncLocal`; `utime`, `stime`, time outside a frame charged to the compositor; `/proc/<pid>/stat`. | 0067 |
| 3 | "Not responding" when a slice exceeds `UnresponsiveThresholdMs` (5 s); display only. | 0067 |
| 4 | Memory in four layers: image, OS resources, allocations per slice, real heap distributed after each GC; no limit. Marshaling counting is dropped. | 0068 |
| 5 | `StopRequested` (reason, deadline), `CheckpointAsync`, `TerminateAsync`, `ProcessExitReason.Terminated`; four-step escalation; "End task" becomes `SIGTERM`, "Force kill" is added. | 0069 |
| 6 | Hybrid model: window apps on the main thread, commands in a pool of one worker per virtual core, services in a dedicated worker with a reserve; remote gateways; heartbeat; only a silent worker is terminated. | 0070 |
| 7 | Once the system has started, always at least one ready worker; it serves dedicated processes and pool overflow when no worker is free; cap `navigator.hardwareConcurrency`; `/proc/hackeros/workers`. | 0071 |
| 8 | `HackerOs.Server`: measurements off by default, no worker, until an ADR specific to this host. | 0066, 0067, 0070 |

## 5. Work items

Each branch starts from `rebuild/base` and its PR targets `rebuild/base`.

| Branch | Content | Depends on |
|---|---|---|
| `rebuild/5-accounting-switches` | ADR 0066: MSBuild properties, `build/HackerOs.ResourceAccounting.targets`, `RuntimeHostConfigurationOption`, project `HackerOs.Platform.ResourceAccounting` (empty of measurement, with the **context-switch hook** common to both measurements), null contracts in Simulation Abstractions, hosts' `wwwroot/appsettings.json`, `CpuSource`/`MemSource`, "estimated" label. | n/a |
| `rebuild/5-cpu-accounting` | ADR 0067: `utime`/`stime` counting, compositor, `/proc/<pid>/stat`, real `/proc/stat`, `HackProcess.*ProcessorTime`, long tasks, "Not responding" and `Responding:`, overhead test (< 5%). | `5-accounting-switches` |
| `rebuild/5-memory-accounting` | ADR 0068: four layers, fields of `/proc/<pid>/status`, `/proc/meminfo`, `PrivateMemorySize64`. | `5-accounting-switches` |
| `rebuild/5-graceful-stop` | ADR 0069: `StopRequested`, `CheckpointAsync`, `TerminateAsync`, `Terminated`, per-process scheduler and `HackProcessKilledException`, polite "End task" and "Force kill", `CheckpointAsync` in long commands. | n/a |
| `rebuild/5-worker-processes` | ADR 0070 and 0071: always-ready worker after boot, overflow and cap, `/proc/hackeros/workers`, `AppExecutionHost` in the manifest, `HackerOs.Platform.WorkerHost`, remote gateways and parity tests, pool and reserve, heartbeat, step 4 escalation, first for `HackerOs.Commands`. | the other four |
| `rebuild/5-host-smoke` | Section 5.2: `build/verify-hosts.sh`, Playwright startup test of the three hosts with fallback to the container's Chromium, combinations of measurements; also fixes the launching of existing E2E suites in a container. | n/a |
| `rebuild/5-final` | End-to-end verification, `system-metrics.md`, `task-manager.md`, `app-sdk.md`, `sdk-reference.md`, index, this list up to date. | all |

### 5.1 What each work item does for existing apps

| Work item | Migration of existing apps |
|---|---|
| `5-accounting-switches` | Check that all app code goes through an ambient frame (`WindowAppBase`, `HackComponentBase`, `TerminalAppBase`, `ServiceAppBase`); add a test that fails if an app in `Apps/` runs code outside a frame, otherwise its time would go to the compositor. |
| `5-cpu-accounting`, `5-memory-accounting` | No app code change; the manifest generator provides the assembly size (image layer) for **all** apps; tests of `/proc/<pid>/stat` and `status` on a sample of each family (window, command, service). |
| `5-graceful-stop` | Each window app and each service that holds state (editors, explorer during a copy, Settings, services) reacts to `StopRequested`: finishes or cancels its current operation cleanly, with no silent loss (an editor with unsaved changes keeps a recovery draft). Every long loop in `Apps/` (commands, recursive copy in the explorer, CodeEditor search, zip) calls `CheckpointAsync`. |
| `5-worker-processes` | Each command of `HackerOs.Commands` and each service declares its `AppExecutionHost` (or gets the right default); the parity tests run **all** built-in commands in both modes; an app that cannot run in a worker stays on `MainThread`, with the reason in a comment. |

### 5.2 Host verification

Work item `rebuild/5-host-smoke` adds a startup test of the three hosts,
launched by a single command (`build/verify-hosts.sh`):

- builds the three hosts and publishes Ecosystem;
- launches each host (published Ecosystem behind a static server, `test/test`
  and `HackerOs.Server` through `dotnet run`), opens it in Chromium through Playwright
  (fallback to `/opt/pw-browsers/chromium` when the version Playwright expects
  is absent, the case in containers);
- waits for the login screen then the desktop, with no error in the console, opens
  the terminal, runs `ps` and `cat /proc/self/status`, opens the task
  manager, then closes everything;
- redoes the Ecosystem run with measurements turned off at build
  (`-p:HackerOsCpuAccounting=false -p:HackerOsMemoryAccounting=false`) and through
  `appsettings.json`.

As long as this test is not merged, each work item does the same verification by
hand and describes it in its PR. Once merged, each `rebuild/5-*` PR
gives its output.

Order:

1. In parallel: `5-accounting-switches` and `5-graceful-stop`.
2. After `5-accounting-switches`, in parallel: `5-cpu-accounting` and
   `5-memory-accounting`.
3. `5-worker-processes`.
4. `5-final`.

## 6. Task list

### Preparation
- [x] Discuss the options with Benoit (optional measurement, measurement only,
  "Not responding" only, pool or worker per process, stop token).
- [x] Write this plan and ADRs 0066 to 0070 (2026-10-16).

### Optional measurement (ADR 0066)
Done by `rebuild/5-accounting-switches` ([`resource-accounting.md`](../resource-accounting.md)).
- [x] MSBuild properties, `.targets` and conditional reference.
- [x] `appsettings.json` of the three hosts and reading at composition.
- [x] Return to the simulator, `CpuSource`/`MemSource`, "estimated" label.
- [x] Tests of the four combinations, no hook when turned off.
- [x] Common context-switch hook (`AsyncLocal` with callback, observer contract `IExecutionContextSwitchObserver`, broadcaster), installed only if a measurement is active.

### CPU (ADR 0067)
Done by `rebuild/5-cpu-accounting` ([`cpu-accounting.md`](../cpu-accounting.md)).
- [x] `utime`/`stime`/compositor counting on the context-switch hook (hook delivered by `5-accounting-switches`).
- [x] `/proc/<pid>/stat`, real `/proc/stat`, `HackProcess.*ProcessorTime`.
- [x] Long tasks, "Not responding", `Responding:` and task manager column.
- [x] Overhead test.

### Memory (ADR 0068)
Done by `rebuild/5-memory-accounting` ([`memory-accounting.md`](../memory-accounting.md)).
- [x] Image, OS resources, allocations, resident layers.
- [x] `/proc/<pid>/status`, `/proc/meminfo`, `PrivateMemorySize64`.

### Clean stop (ADR 0069)
- [x] `StopRequested`, `TerminateAsync`, `ProcessExitReason.Terminated`.
- [x] `CheckpointAsync` and its use in long commands.
- [x] Per-process scheduler, discarded continuations, `HackProcessKilledException`.
- [x] "End task" as `SIGTERM`, "Force kill" added.

Implementation details (`rebuild/5-graceful-stop`, detail in
[`app-sdk.md`](../../framework/app-sdk.md#clean-shutdown-and-checkpoints-adr-0069)):

- The kernel holds a `ProcessControlBlock` per process (stop request,
  "killed" flag, `ProcessTaskScheduler`). A `SIGKILL` is: `Killed`
  (`kill -9`, "Force kill") and `Timeout` (end of the grace period, reason kept
  so as not to change `CloseMainWindowAsync`); a closed window, a
  logout or a disabled app keep access to the OS for their cleanup.
- The per-process scheduler is installed for commands and services
  (entry point launched by the orchestrator), as a synchronization context and
  as a `TaskScheduler`; window apps stay on the render dispatcher.
- `HackStopRequestedEventArgs.Defer(Task)` lets an app finish some work
  before the default action; the default action of `SIGTERM` for a window
  app is to close its window, the process then ends with the reason of
  the request (`Terminated`) instead of waiting for the delay.
- Services and, at logout or shutdown, windows also receive
  `StopRequested` (`Logout`, `Shutdown`); a window that the user
  closes does not receive it (`IWindowCloseGuard` has already asked the question).
- The slice of `CheckpointAsync` is a per-thread timestamp
  (`HackProcessSlice`), restarted by the scheduler at each continuation;
  ADR 0067 may replace it with the measurement.

### Web Workers (ADR 0070)
- [x] `AppExecutionHost` in `[AppManifest]` and the manifest generator. (`5-worker-processes`, [`worker-processes.md`](../worker-processes.md))
- [x] Process host in a worker and remote gateways, parity tests. (`5-worker-processes`, [`worker-processes.md`](../worker-processes.md))
- [x] Pool (one worker per virtual core), dedicated reserve, pipeline placement. (`5-worker-processes`, [`worker-processes.md`](../worker-processes.md))
- [x] Heartbeat, "Not responding" during blocking, worker termination. (`5-worker-processes`, [`worker-processes.md`](../worker-processes.md))
- [x] Always-ready worker after boot, pool overflow, cap, `/proc/hackeros/workers` (ADR 0071). (`5-worker-processes`, [`worker-processes.md`](../worker-processes.md))

### Existing apps (section 5.1)
- [x] All apps in `Apps/` run their code in an ambient frame (test `AmbientFrameSourceTests`, `5-accounting-switches`).
- [x] CPU time: `/proc/<pid>/stat` and `status` tested on a real window (Calculator), command (`echo`) and service (sample service), without changing their code (`5-cpu-accounting`).
- [x] Window apps and services with state: `StopRequested` handled (TextEditor and CodeEditor: draft; FileExplorer: current item then clean stop; Settings: write in progress; services: `OnStoppingAsync` once).
- [x] Long loops in `Apps/`: `CheckpointAsync` (commands, `FileTransfer`, explorer zip, trash, CodeEditor search and traversal).
- [x] Commands and services: `AppExecutionHost` declared, parity tested for all commands. (`5-worker-processes`, [`worker-processes.md`](../worker-processes.md))

### Hosts (section 5.2)
- [x] `build/verify-hosts.sh`: the three hosts build, publish and start up to the desktop; fallback to `/opt/pw-browsers/chromium` for all Playwright suites (`rebuild/5-host-smoke`).
- [x] Run with measurements turned off at build and through `appsettings.json` (`CpuSource`/`MemSource` verified for each run).
- [x] Each `rebuild/5-*` PR gives the output of the verification: output of `build/verify-hosts.sh` in #128 and #129; #124 to #127, merged before the script, describe the same run done by hand (rule of section 5.2).

### End of phase
- [x] Final verification and documentation up to date (`rebuild/5-final`, below).

### Final verification (`rebuild/5-final`, 2026-10-16)

On `rebuild/base` at `47de11c` (after #129), in a cloud container:

- `dotnet build HackerOs.sln -c Release`: 0 errors, 0 warnings.
- The 35 `Tests/*.Tests` projects excluding E2E (about 2,400 tests) pass, including
  `HackerOs.Platform.ResourceAccounting.Tests` (52), `HackerOs.Platform.WorkerHost.Tests`
  (82, parity of the 63 commands) and `HackerOs.Platform.Core.Tests` (890).
- `build/verify-hosts.sh`: the first run failed on `test-host`. The startup
  test read the output of `ps` as soon as the `PID` header appeared, whereas the output of a
  command launched in a Web Worker arrives in several writes; the process
  lines were not yet displayed. `HostBootSmokeTests` now waits for the next
  prompt after the output of `ps` and `cat /proc/self/status`
  (`OutputFinished`). Two full runs afterwards:

```text
== HackerOS host verification (47de11c) ==
build HackerOs.Ecosystem         PASS
build test                       PASS
build HackerOs.Server            PASS
build HackerOs.HostSmoke.Tests   PASS
publish ecosystem                PASS
publish ecosystem-build-off      PASS
ecosystem                        PASS  30s
ecosystem-build-off              PASS  31s
ecosystem-settings-off           PASS  30s
test-host                        PASS  46s
server                           PASS  36s
RESULT: PASS
```

- Documentation reread against the code: `system-metrics.md`, `task-manager.md`,
  `app-sdk.md`, `sdk-reference.md`, `project-overview.md`, `virtual-proc-and-dev.md`
  and `linux-commands.md` already describe ADRs 0066 to 0072 (each work item
  updated them); the counts (32 capabilities, 75 apps including 63 commands) are exact.
- Left out of this phase, noted in
  [`building-and-testing.md`](../building-and-testing.md#known-state): two failures of
  `HackerOs.UI.E2E.Tests` due to the app (accessibility, mobile "Back" button), fixed
  afterwards by `rebuild/5-ui-a11y-mkdir`, and the PWA suite that requires IPv6.

## 7. Out of scope

- Imposed quantum, memory limits, OOM killer, "Force Quit" window.
- Cross-origin isolation (COOP/COEP), WASM threads, `SharedArrayBuffer`.
- Window apps in a worker.
- Workers and measurements on `HackerOs.Server`.

These points are taken up by [`execution-followups-plan.md`](execution-followups-plan.md)
(plan approved on 2026-10-17, executed after the wasm v3 migration).

## 8. References

- ADR [0066](../adr/0066-optional-resource-accounting.md),
  [0067](../adr/0067-measured-cpu-time.md),
  [0068](../adr/0068-measured-process-memory.md),
  [0069](../adr/0069-graceful-stop-and-checkpoints.md),
  [0070](../adr/0070-worker-hosted-processes.md),
  [0071](../adr/0071-always-ready-worker.md).
- [`system-metrics.md`](../system-metrics.md), [`task-manager.md`](../task-manager.md),
  [`app-sdk.md`](../../framework/app-sdk.md).
