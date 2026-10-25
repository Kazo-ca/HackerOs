> [Version française](../../../fr/wasm/dev/task-manager.md)

# Task manager

Implementation of the `rebuild/4-task-manager` work item of phase 4: ADR 0056
sections 4 and 5 (`HackProcess`, `HackServiceController`) and ADR 0058 section 2
(rewrite of `HackerOs.Apps.SystemMonitor`). The metrics read come from the
`4-proc-metrics` work item ([`system-metrics.md`](system-metrics.md),
ADR 0057).

## Role

The "System Monitor" app (`org.hackeros.system-monitor`) is HackerOS's task
manager. Like `top`, it rereads `/proc` every second; everything it displays, a
third-party app or `ps` can read as well.

| Tab | Content | Source |
| --- | --- | --- |
| Processes | PID, name, user, state, CPU, memory, disk read and write per second; sort, filter, multiple selection, "End task" (`SIGTERM`), "Force kill" (`SIGKILL`, context menu), details | `HackProcess` → `/proc/<pid>/status`, `stat`, `cmdline`, `cwd`, `io` |
| Performance | summary (former Overview tab), total or per-core CPU chart, memory chart, hardware section (former "Hardware & System Info" tab) | `/proc/stat`, `/proc/meminfo`, `/proc/uptime`, `/proc/loadavg`, `/proc/cpuinfo` |
| Disk | used and free space of each disk; read and write rates, active time, counters | `HackDriveInfo`, `/proc/diskstats` |
| Network | received and sent rates per interface, packets, errors, drops | `/proc/net/dev` |
| Services | installed services, state, PID, start type, start and stop | `HackServiceController` |

The app now references only the SDK: `IProcessManager`, `AppCatalog`,
`AppLifecycleOrchestrator` and `IEventBus` are gone, and its ADR 0055 exemption
is removed from `build/HackerOs.Apps.targets`.

## Architecture

### SDK

| Type | Project | Role |
| --- | --- | --- |
| `HackProcess`, `HackProcessState`, `HackProcessIoCounters` | `HackerOs.AppSdk` (`Diagnostics/`) | a process, modeled on `System.Diagnostics.Process`, read from `/proc` through the app's file gateway |
| `HackServiceController`, `HackServiceControllerStatus` | `HackerOs.AppSdk` (`ServiceProcess/`) | a service, modeled on `System.ServiceProcess.ServiceController` |
| `HackLineChart`, `HackChartSeries`, `HackChartGeometry`, `HackChartSize` | `HackerOs.AppSdk.Blazor` (`Charts/`) | native SVG line chart, without a library or JavaScript |

### App (`Apps/System/HackerOs.Apps.SystemMonitor`)

| File | Role |
| --- | --- |
| `SystemMonitorWindow.razor` (+ `.cs`, `.css`, `.js`) | window: refresh loop, `MudTabs` tabs, "End task" and "Force kill" with confirmation |
| `Metrics/ProcFiles.cs` | parsers for `stat`, `meminfo`, `cpuinfo`, `uptime`, `loadavg`, `diskstats`, `net/dev` (Linux formats) |
| `Metrics/ProcReader.cs` | one read of `/proc` through the SDK: processes, system files, disks |
| `Metrics/SystemMetrics.cs` | differences between two reads and history of 60 values (`MetricHistory`) |
| `Metrics/Units.cs` | readable sizes, rates, percentages and durations, culture-independent |
| `Processes/ProcessTable.cs` | sort, filter, multiple selection, process of the details panel |
| `Processes/EndTaskRunner.cs` | stopping a selection, refusals reported process by process |
| `Tabs/*.razor` | one component per tab and the details panel, each with its isolated CSS |

All the tab components inherit from `HackComponentBase`: their event handlers
run in the app's ambient context (ADR 0043). `PerformanceTab`, `DiskTab` and
`NetworkTab`, which do not call the SDK, were switched to it in phase 5 so that
their time is counted for the app and not for the compositor (ADR 0067,
[`resource-accounting.md`](resource-accounting.md#existing-apps)).

## How it works

### Refresh

- A one-second `PeriodicTimer` (`SystemMonitorWindow.RefreshInterval`) calls
  `ProcReader.ReadAsync`, then `SystemMetrics.Apply` and
  `ProcessTable.SetRows`. A read in progress is never doubled.
- **Pause**: when the window is minimized (the desktop hides it) or the browser
  tab is hidden, the `SystemMonitorWindow.razor.js` module signals it
  (`IntersectionObserver`, `visibilitychange`) and the loop skips its reads. On
  restore, a read is done right away. A "Pause" button also suspends refreshing
  by hand.
- **Differences**: the first read serves as a base. CPU is the share of busy
  time between two reads of `/proc/stat`, like `top` (no clock needed, busy
  time and idle time are on the same simulated clock). Disk, network and
  per-process rates are real bytes divided by the real elapsed time. A counter
  that goes backwards (OS restarted) gives `0`.

### Processes

- "estimated" mention (ADR 0066): the header of the CPU and Memory columns
  carries a small dotted label as long as at least one row comes from the
  simulator (`CpuSource:` / `MemSource:` of `/proc/<pid>/status` different from
  `measured`, read in `HackProcess.Status`). Its tooltip says so. See
  [`resource-accounting.md`](resource-accounting.md). With measured memory
  (ADR 0068, [`memory-accounting.md`](memory-accounting.md)), the Memory column
  shows the measured `VmRSS` for all built-in apps and the label disappears
  from that column as soon as all rows are measured; a process's details show
  the layers (`RssAnon`, `RssFile`, `VmData`, `VmExe`, `HackAllocBytes`) in its
  full `status`. The app has not changed: it still reads `HackProcess`.
- "Not responding" (ADR 0067): when `/proc/<pid>/status` carries
  `Responding: 0` (`HackProcess.Responding` false), the state column displays
  "Not responding" in red in place of the state, with a tooltip; sorting by
  state takes it into account. Display only: the manager stops nothing by
  itself, "End task" remains its only means. See
  [`cpu-accounting.md`](cpu-accounting.md). For a windowed app, the window
  itself also displays "(Not responding)" and the "Wait | End application" box
  (ADR 0127,
  [`process-priority-and-unresponsive-windows.md`](process-priority-and-unresponsive-windows.md)).
  For a process in a Web Worker (ADR 0070), the state appears **during** the
  blockage: its worker has not beaten for 5 s, and the rest of the interface
  keeps responding; "End task" then "Force kill" terminate the worker
  ([`worker-processes.md`](worker-processes.md)).
- Priority (ADR 0127 §1): "Priority" column (`ProcessColumn.Priority`,
  `ProcessRow.Nice`) displayed through a toggle button
  (`ProcessTable.ShowPriority`, hidden at the start); it shows the `nice` value
  read from `/proc/<pid>/stat` and can be sorted. The manager only displays it:
  it is changed with `renice`. See [`process-priority-and-unresponsive-windows.md`](process-priority-and-unresponsive-windows.md).
- Background tasks: a `HackBackground.RunAsync` task is a child process of its
  app (ADR 0128, 0140); it appears as a separate row, named after the task
  (`Name:` of `status`, `Job:` and `Host:` lines), and "End task" stops it like
  the others ([`background-jobs.md`](../framework/background-jobs.md)).
- A process killed by the OOM killer (ADR 0126) or by its `rss`/`cpu` limit
  (ADR 0125) simply disappears from the table at the next read; an OOM killer
  victim leaves its trace in `dmesg`
  ([`oom-killer.md`](oom-killer.md), [`process-limits.md`](process-limits.md)).
- Sort by column (click on the header; same column: reverse direction; measure
  column: largest first). On equal values, order by PID, so that rows do not
  jump every second.
- Text filter on the name, the app id and the PID, case-insensitive. A selected
  row that the filter hides is deselected: "End task" never reaches an
  invisible row.
- Selection: click, Ctrl+click (toggle), Shift+click (range from the last
  click, added with Ctrl+Shift), Ctrl+A, Esc. The selection is kept by PID from
  one read to the next.
- Keyboard in the table: Delete for "End task", Shift+Delete for "Force kill",
  Enter for details. Right-click on a row: menu Details, End task, Force kill
  (the row is selected alone if it was not).
- **End task** (ADR 0069 §4, `SIGTERM`): a confirmation window lists the
  processes, then each receives `HackProcess.TerminateAsync`: it is notified
  (`StopRequested`, reason `Terminated`), its token is cancelled, and it has 5
  seconds to finish before the OS kills it. A windowed app keeps a draft of its
  unsaved changes then its window closes. The selected processes are signaled
  together, not one after the other: three blocked processes do not cost 15
  seconds.
- **Force kill** (context menu, `SIGKILL`): separate confirmation ("it cannot
  react"), then `HackProcess.KillAsync`: immediate, without `StopRequested`,
  descendants included.
- In both cases, a refusal (`process.manage` missing) is noted for that process
  and does not stop the others; a process already finished counts as stopped.
  The result is displayed above the table, in selection order.
- **Details** (double click, Enter or "Details"): overview, command line,
  current directory, clickable parent and children, `io` counters, full
  `status`, environment (`environ`) if readable. `io` and `environ` are `0400`:
  for another user's process, the panel says so instead of showing an error.

### Services

Columns State (`active`, `activating`, `deactivating`, `inactive`, `failed`
with its reason, ADR 0097) and Health (health and text reported by the
service). A `failed` row is highlighted and offers "Reset" (`reset-failed`).
The state comes from the service manager (`RefreshStatusAsync` at each refresh);
only an OS that does not provide it falls back to `/proc`. The list and the
start types are read when the tab opens and after each action; the state and
the PID follow the processes read every second by the window, so a service that
stops appears stopped without reloading the list. An unreadable start type
(service of another app without `services.manage`) is shown as "unknown".

## SDK API

### `HackProcess` (ADR 0056 §4)

```csharp
HackProcess[] all = await HackProcess.GetProcessesAsync();     // par PID croissant
HackProcess me = await HackProcess.GetCurrentProcessAsync();
HackProcess p = await HackProcess.GetProcessByIdAsync(42);     // ArgumentException s'il n'existe pas
await p.RefreshAsync();                                        // HasExited si /proc/42 a disparu
IReadOnlyDictionary<string, string>? env = await p.GetEnvironmentVariablesAsync();
bool closedByItself = await p.CloseMainWindowAsync(TimeSpan.FromSeconds(5));
bool endedByItself = await p.TerminateAsync();                 // SIGTERM, 5 s (ADR 0069)
await p.KillAsync();                                           // SIGKILL
```

- Properties: `Id`, `ParentId`, `ProcessName`, `AppId`, `Kind`, `UserId`,
  `UserName`, `SessionId`, `State`, `StateCode`, `StartTime`, `WorkingSet64`
  (`VmRSS`), `PrivateMemorySize64` (`RssAnon` when memory is measured,
  otherwise equal to `WorkingSet64`, ADR 0068), `CpuUsage`, `Arguments`, `CommandLine`, `WorkingDirectory`, `IoCounters`,
  `ReadBytes`, `WriteBytes`, `Status`, `HasExited`; `GetChildren(liste)`.
- CPU times (ADR 0067, like `System.Diagnostics.Process`):
  `UserProcessorTime` (`utime`), `PrivilegedProcessorTime` (`stime`),
  `TotalProcessorTime`, read from `/proc/<pid>/stat` (10 ms resolution);
  `Responding` (`Responding:` of `status`, always true when CPU is not
  measured). `ParseStatTimes(texte)` parses a `stat` line. See
  [`cpu-accounting.md`](cpu-accounting.md).
- Priority (ADR 0127 §1): `Nice` (field 19 of `stat`, from -20 to 19) and
  `BasePriority` (`20 + Nice`, the `PR` of `top`); `ParseStatNice(texte)`.
  Changing the priority goes through `IAppProcessGateway.SetNice` (`renice`
  command).
- Limits (ADR 0125, [`process-limits.md`](process-limits.md)):
  `GetResourceLimitsAsync()` reads `/proc/<pid>/limits` (`HackResourceLimits`:
  `Cpu`, `Rss`, `NoFile`, `NProc`, `Enforcement`, `IsCpuMeasured`,
  `IsMemoryMeasured`); `SetResourceLimitAsync(kind, souple, dure)` changes one
  like `prlimit` (`process.manage` for another process, `sudo` to raise a hard
  limit or touch another user's process).
- Reading: `/proc` (list and creation date of `/proc/<pid>` as `StartTime`),
  `status`, `stat`, `cmdline`, `cwd` (link target), `io`, `environ`.
  Visibility of `/proc`: its own process, the others with `process.list`.
  Reading `/proc` requires `filesystem.system.read`. Through the gateway
  (`IAppProcessGateway`), an app also sees, stops and kills its own descendants
  (its `HackBackground` tasks) without `process.list` or `process.manage`
  (ADR 0140 §7).
- Action: `KillAsync` (`SIGKILL`: immediate, descendants included, no
  `StopRequested`), `TerminateAsync` (`SIGTERM` of another process, reason
  `Terminated`, `DefaultTerminateTimeout` = 5 s) and `CloseMainWindowAsync`
  (same polite stop with the reason `CloseRequested`, `DefaultCloseTimeout`) go
  through `IAppProcessGateway`. Both polite stops return `false` when the OS had
  to kill the process at the deadline. See ADR 0069 and
  [`app-sdk.md`](../framework/app-sdk.md#clean-shutdown-and-checkpoints-adr-0069). Exceptions:
  `HackUnauthorizedAccessException` (`process.manage` capability missing for
  another process), `InvalidOperationException` (process already finished).

### `HackServiceController` (ADR 0056 §5)

```csharp
HackServiceController[] services = await HackServiceController.GetServicesAsync();
HackServiceController sync = services[0];
await sync.StartAsync();
await sync.SetStartTypeAsync(ServiceStartMode.Automatic);
```

- Properties: `ServiceName` (app id), `DisplayName`, `Description`,
  `Status` (`Stopped`, `StartPending`, `StopPending`, `Running`, values of
  `ServiceControllerStatus`, deduced from the active state: `failed` gives
  `Stopped`), `StartType` (`ServiceStartMode?`), `ProcessId`.
- systemd-style states (ADR 0097): `ActiveState` (`HackServiceActiveState`:
  `Inactive`, `Activating`, `Active`, `Deactivating`, `Failed`), `Result`
  (`HackServiceResult`: `Success`, `ExitCode`, `Exception`, `Killed`,
  `Timeout`), `Health` (`HackServiceHealth`), `StatusText`,
  `ActiveStateChangedAt`, `ExitCode`. `ResetFailedAsync()` clears `failed`;
  `RefreshStatusAsync()` rereads the state alone. `HackServiceStates.ToSystemdName`
  gives systemd's words.
- List: `HackApps.GetInstalledAsync` filtered on `AppKind.Service`, sorted by
  name. State: `HackProcess`. Start type and actions:
  `IAppServiceControlGateway`.
- Capabilities: none to list; `services.manage` to act on another app's service
  and to read its start type. Failures: `HackUnauthorizedAccessException`
  (refusal), `InvalidOperationException` (service uninstalled, disabled or
  failed).

### `HackLineChart` (`HackerOs.AppSdk.Blazor.Charts`)

```razor
<HackLineChart Title="Total CPU usage"
               Series="@([new HackChartSeries("Total", cpuHistory)])"
               Maximum="100" Filled="true" Size="HackChartSize.Large"
               ValueFormatter="FormatPercent" />
```

- Parameters: `Series`, `Title`, `Capacity` (60 by default), `Maximum`
  (`null`: automatic scale rounded to 1, 2 or 5 × 10ⁿ, never below
  `MinimumScale`), `ValueFormatter`, `Filled`, `ShowLegend`, `TimeSpanLabel`,
  `Size` (`Small`, `Medium`, `Large`), `Class`.
- The most recent value is at the right edge; SVG coordinates are written in
  invariant culture.
- Colors: `--hos-chart-series-0` to `--hos-chart-series-7`, falling back to the
  theme's accent and state colors.

## Key decisions and clarifications relative to ADR 0056 and 0058

- **`UserName`**: `/proc/<pid>/status` gives an opaque `Uid` and HackerOS has no
  `/etc/passwd`. The SDK names the calling process's user; for another one,
  `UserName` is the id, like `ps` with an unknown uid. The tab shortens a
  32-character id to `uid:xxxxxxxx`.
- **Nullable `StartType`**: reading the start type of another app's service
  follows the same rule as changing it (`services.manage`). Without it, the list
  stays readable, the type is `null`.
- **Additions to the ADR 0056 list**: `SessionId`, `StateCode`, `Arguments`,
  `IoCounters`, `Status`, `GetEnvironmentVariablesAsync`, `GetChildren`,
  `GetCurrentProcessAsync`, `HackServiceController.ProcessId` and `Description`:
  everything that a process's details and the Services tab display, with no
  source other than `/proc` and the catalog. `ParseStatus` and `ParseIo` are
  public for apps that read `/proc` themselves.
- **Chart in the SDK**: ADR 0058 asks for native SVG components;
  `HackLineChart` lives in `HackerOs.AppSdk.Blazor` so that another app
  (Diagnostic, a third-party app) reuses it, as `AGENTS.md` requires.
- **Minimized window detected in JavaScript**: the SDK does not expose the
  window's state. The window's module observes its own visibility, the one
  point that C# does not see; it is imported from
  `./_content/HackerOs.Apps.SystemMonitor/` (ADR 0055 §3). The same module
  prevents Ctrl+A from selecting the whole page when the table has focus.
- **Tabs not kept** (`KeepPanelsAlive="false"`): only the visible tab renders
  every second. State that must survive a tab change (sort, filter, selection,
  open details) lives in `ProcessTable`, owned by the window.
- **Manifest**: `filesystem.system.read` is added to `process.list`,
  `process.manage` and `services.manage`, as for `ps`.
- **Resource profile at launch** (fix in Platform Core):
  `AppLifecycleOrchestrator` launched each process with
  `ResourceProfile.None`, so that the simulator returned 0 % CPU and 0 bytes of
  memory for all, and the charts stayed flat. It now passes the manifest's
  `Resources` weights, as ADR 0012 provides; the manifest validator already
  enforces the same bounds as `ResourceProfile`.
- **No observer effect on the disk.** The manager must not become the first
  reader of the disk it monitors:
  - `HackProcess` reads the `cwd` link like `readlink`, without resolving its
    target. Following the link made the current directory of each process be
    resolved in IndexedDB, one segment per transaction, every second (about 40
    reads per second measured on `hda`, against 4 at rest);
  - disk space (`HackDriveInfo`, which walks the IndexedDB entries) is reread
    only every 10 reads (`SystemMonitorWindow.DriveRefreshEvery`), just as one
    does not run `df` in a loop.

## Tests

- `Tests/HackerOs.Platform.Core.Tests/Execution/HackProcessTests.cs`:
  `HackProcess` and `HackServiceController` on the real `/proc` provider and
  `AppFileSystemGateway`; visibility without `process.list`, private `io` and
  `environ`, finished process, refusal and output of `KillAsync`, deadline of
  `CloseMainWindowAsync`, reason and deadline of `TerminateAsync`, services
  (state, unreadable type, disabled, refusal).
- `Tests/HackerOs.Platform.Core.Tests/Lifecycle/AppLifecycleOrchestratorTests.cs`:
  the process receives the `Resources` weights of its manifest.
- `Tests/HackerOs.AppSdk.Blazor.Tests/HackChartGeometryTests.cs`: automatic
  scale, point position, area, invariant culture.
- `Tests/HackerOs.Apps.SystemMonitor.Tests/`: `/proc` parsers
  (`ProcFilesTests`), differences and history (`SystemMetricsTests`), sort,
  filter, selection and "End task" (`ProcessTableTests`), "End task" as
  `SIGTERM` 5 s and "Force kill" as `SIGKILL`, selection signaled together
  (`EndTaskTests`), units (`UnitsTests`), manifest (`SystemMonitorWindowTests`),
  English and French texts and French units (`SystemMonitorLocalizationTests`).

## Languages (ADR 0104)

The app's texts are in `Resources/Strings.resx` (English) and
`Strings.fr.resx` (French), read with `AppContext.GetLocalizer<Strings>()`.
Numbers follow `AppContext.FormatCulture`: `Units` has overloads that take a
culture (`1,5 Mio`, `12,5 %`, `2 j 03:14:15` in French); the overloads without
a culture stay invariant. The names and values read from `/proc` (fields of
`status`, systemd states) are not translated.

## Task list for work item `rebuild/4-task-manager`

- [x] `HackProcess`: reading of `/proc/<pid>/status`, `cmdline`, `cwd`, `io`, `environ`; `KillAsync`, `CloseMainWindowAsync` through `IAppProcessGateway`.
- [x] `HackServiceController`: list through `HackApps`, state through `HackProcess`, actions and start type through `IAppServiceControlGateway`.
- [x] `HackLineChart`: native SVG chart in `HackerOs.AppSdk.Blazor`.
- [x] `/proc` parsers and computation of rates by difference, 60 s history.
- [x] Table model: sort, filter, multiple selection (Ctrl, Shift, Ctrl+A), details.
- [x] Window rewrite: tabs Processes, Performance, Disk, Network, Services; details; confirmed "End task", refusal per process; one-second refresh suspended when the window is minimized.
- [x] Removal of `Platform.Core` and of the ADR 0055 exemption; manifest (`filesystem.system.read`).
- [x] Fix: manifest resource profile passed to the process by `AppLifecycleOrchestrator` (CPU and memory finally non-zero).
- [x] Verification in the browser (host `test/test`): tabs, sort, Shift+click, details, filter, "End task", minimized-window pause; fix of the observer effect on `hda`.
- [x] Tests (SDK, chart, app).
- [x] Documentation: this document, `app-sdk.md`, `system-metrics.md`, `virtual-proc-and-dev.md`, index.

## Work item `rebuild/5-graceful-stop` (ADR 0069)

- [x] "End task" goes from `KillAsync` to `TerminateAsync` (`SIGTERM`, 5 s), with its confirmation (ADR 0058 §2).
- [x] "Force kill" (`SIGKILL`) in the rows' context menu and on Shift+Delete, with its own confirmation.
- [x] Selection signaled in parallel, report in selection order (`EndTaskRunner`, `EndTaskMode`).
- [x] Tests: `EndTaskTests`, `HackProcessTests.TerminateAsync_*`.

## Work item `rebuild/5-worker-processes` (ADR 0070, 0071)

- [x] `Metrics/ProcFiles.cs` reads `/proc/hackeros/workers` (`ProcWorkers`, `ProcWorker`, `ParseWorkers`); `ProcReader` reads it if it exists.
- [x] Performance tab: "Workers" section (capacity, pool size, one card per worker with role, state, core, memory, processes); "Not responding" in red.
  Since `exec/9` (ADR 0131, 0143), when the page is isolated, a card's memory is the one the browser measures for that worker (`memory-measure: browser` in `/proc/hackeros/workers`); the app has not changed.
- [x] "Not responding" visible while a command blocks in a worker; "Force kill" terminates the worker.
- [x] Tests: `ProcFilesTests` (`ParseWorkers`).
