# Headless processes in Web Workers

What an app declares for its execution host, the types that cross a worker's boundary and the page's isolation state are in [`app-execution-hosts.md`](../framework/app-execution-hosts.md).

Implementation of [ADR 0070](adr/0070-worker-hosted-processes.md) and
[0071](adr/0071-always-ready-worker.md), with the adjustments of
[ADR 0072](adr/0072-worker-processes-implementation-adjustments.md);
work item `rebuild/5-worker-processes` of phase 5
([`execution-isolation-plan.md`](plans/execution-isolation-plan.md)). Cross-origin
isolation (shared pipes, synchronous calls, measured memory) follows
[ADR 0131](adr/0131-cross-origin-isolation.md) and
[0143](adr/0143-cross-origin-isolation-implementation-adjustments.md).

## Role

Blazor WebAssembly runs all of .NET on a single thread: a command that loops
without yielding froze the interface, the mouse and `kill`. In the browser,
shell commands and services now run in **Web Workers**, each with its own .NET
runtime:

- an infinite loop (`stress`) no longer freezes the desktop; `echo` answers in
  ~300 ms while it runs;
- the task manager, `ps` and `/proc/hackeros/workers` show "Not responding"
  **during** the blockage;
- `kill -9` (and `kill` after the grace period) terminates the blocked worker
  and replaces it right away;
- the SDK does not change: a command does not know where it runs.

Windowed apps, the shell (`hsh`) and its built-in commands stay on the main
thread.

## Where each process runs

A window stays on the main thread: the trial of rendering in a worker
(ADR 0129) concluded "we stop" ([ADR 0133](adr/0133-window-rendering-in-worker-verdict.md),
[`remote-window-rendering.md`](remote-window-rendering.md)). Its prototype,
switched off by default, is the only one to start a `start` process with `k = 3`.

The 74 commands of `HackerOs.Commands`: 56 in `pool`, 18 in `mainThread`
with the reason in a comment in their definition (`cd`, `exit`, `export`,
`unset`, `jobs`, `read`, `help`, `which`, `kill` drive the shell; `sudo`
asks the authentication agent for a password; `passwd` reads the terminal without echo; `hackbackup` opens
the browser's file picker, which requires the user's gesture; `nano`, `top`, `htop`,
`watch` use the terminal's alternate screen; `nice` launches its command
through the calling shell (`RunNicedAsync`) and `renice` changes a priority,
which no worker gateway offers, ADR 0127); the first 16 reasons were
reviewed by `exec/1-remote-gateways` and still hold (ADR 0138 §9). The sample
service and `hackwebd` run in a dedicated worker since `HackEvents`
and `Watch` cross the boundary (ADR 0124).

All three hosts have workers: `test/test` runs the Ecosystem's `Program.cs` on
the browser side, and `HackerOs.Server` serves the Ecosystem OS as
WebAssembly since ADR 0130 (its former Interactive Server rendering, without
workers, was removed). A host composed without `IWebWorkerRuntime` writes to
`/var/log/syslog` "workers: no Web Worker on this host: AppExecutionHost
(pool, dedicated) is ignored, every process runs in the host process".

## Architecture

```
 fil principal (OS)                               Web Worker n
 ─────────────────────────────                    ─────────────────────────────
 AppLifecycleOrchestrator                         workerBootstrap.js (dotnetSidecar)
   └ IRemoteProcessHost ── WorkerRemoteProcessHost   └ BrowserWorkerEntryPoint [JSExport]
        └ WorkerPool ── WorkerConnection ◄─ JSON ─►      └ WorkerProcessHost
             │            └ RemoteProcessSession           ├ ProcessControlBlock / ProcessTaskScheduler
             │               (appels servis sous           ├ RemoteAppExecutionContext
             │                le contexte du processus)    │   └ passerelles distantes (WorkerRpcClient)
             └ IWebWorkerRuntime (BrowserWebWorkerRuntime  └ battement de cœur (250 ms)
               → workerHost.js, ou Loopback en test)
```

| Project | Types |
|---|---|
| `Shared/HackerOs.App.Abstractions` | `AppExecutionHost`, `AppExecutionHosts` (`DefaultFor`, `Resolve`, `Of`, `IsWorker`), `AppManifestAttribute.Execution`, `AppManifest.Execution`. |
| `Shared/HackerOs.Simulation.Abstractions/Workers` | `IWebWorkerRuntime`, `IWebWorker`, `WebWorkerStartOptions`; `IWorkerPoolStatus`, `WorkerSnapshot`, `WorkerRole`, `WorkerState`. |
| `Platform/HackerOs.Platform.Core` | `IRemoteProcessHost`, `RemoteProcessLaunch`, `RemoteProcessExit`, `IRemoteService`, `ExecutionPlacement` (pipeline group), `SystemStartupSignal`; `AppLifecycleOrchestrator` launches remotely what `ShouldRunRemotely` accepts; `/proc/hackeros/workers`. |
| `Platform/HackerOs.Platform.WorkerHost` | **Protocol**: `WorkerMessages`, `WireCodec` (explicit encoding, no reflection), `RemoteErrors`, `RemoteOps`. **Worker side**: `WorkerProcessHost`, `RemoteAppExecutionContext`, remote gateways (`RemoteGateways`, `RemoteFileSystemGateway`, `RemoteStandardStreams`), `WorkerCpuMeter`, `WorkerAssemblyLoader`, `BrowserWorkerEntryPoint`. **OS side**: `WorkerPool`, `WorkerConnection`, `RemoteProcessSession`, `WorkerRemoteProcessHost`, `WorkerAwareCpuTimeAccountant`, `WorkerAwareMemoryAccountant`, `AddHackerOsWorkerProcesses`. **Tests**: `LoopbackWebWorkerRuntime`. |
| `Infrastructure/HackerOs.Infrastructure.Browser` | `wwwroot/workerHost.js` (creation, `postMessage`, `terminate`, `hardwareConcurrency`, `requestIdleCallback`), `wwwroot/workerBootstrap.js` (the worker's .NET runtime), `Workers/BrowserWebWorkerRuntime`. No host references them: they are imported by the C# (`_content/HackerOs.Infrastructure.Browser/…`). |
| `OS/HackerOs.Ecosystem.Host` | `AddHackerOsBrowserHost` (called by the Ecosystem's `Program.cs`) registers `BrowserWebWorkerRuntime` and the workers **in the browser only** (`OperatingSystem.IsBrowser()`), preloading `HackerOs.Commands.dll` and with `KnownAssemblies` = the catalog's lazy assemblies (ADR 0124 §4); `EcosystemBootCoordinator.MarkSystemStarted` gives the end-of-boot signal. |
| `Apps/System/HackerOs.Apps.SystemMonitor` | `ProcWorkers`/`ParseWorkers`, "Workers" section of the Performance tab. |

### Starting a worker

`workerBootstrap.js` sets `globalThis.dotnetSidecar = true` before importing
`dotnet.js` (otherwise the runtime takes itself for a `pthread` thread and waits
indefinitely), creates the runtime (~0.7 s), wires the
`hackeros-worker.post` import and calls `BrowserWorkerEntryPoint.Start`. The
worker then loads the requested assemblies (`INTERNAL.loadLazyAssembly`),
answers `hello` and starts its heartbeat.

### Protocol

JSON messages with a `t` field:

| OS → worker | Role |
|---|---|
| `init` | Worker number, heartbeat period, CPU measurement. |
| `start` | Launch a process: identity, entry point, streams, environment. |
| `result` | Answer to a `call` (value `v` or error `e`). |
| `stop` | `SIGTERM`: `StopRequested`, then cancellation of the token (ADR 0069). |
| `int` | Ctrl+C: cancellation of the token, without a stop request. |
| `kill` | `SIGKILL` when the worker still answers (step 3): discard the process. |
| `sstop` | `OnStoppingAsync` of a service (logout, shutdown). |
| `clock` | Answer to the heartbeat: the simulated clock, so that the worker's follows. |
| `perm` | Permission error raised by the OS on the process's gateways. |
| `ack` | Output credit: the OS has written `n` characters. |
| `epipe` | The reader of stream `f` has left (broken pipe). |
| `hreq` | HTTP request for a socket opened by `net.listen` in the worker (`web/3`). |
| `bgp` | Progress (`f`, `m`) of task `j` that a worker process launched through `bg.run` (ADR 0128 §6, ADR 0140). |
| `uin` | ADR 0133 trial: input of the page's window host (`d`: event `e`, dialog answer `dlgr`, `close`) for window `p`. |

| Worker → OS | Role |
|---|---|
| `hello` | Runtime and assemblies loaded. |
| `hb` | Heartbeat (250 ms): CPU time per process, runtime memory, busy time. |
| `call` | Gateway call that waits for an answer; `"s": true` for a synchronous call, whose answer comes back through the worker's shared cell (ADR 0143 §5). |
| `note` | Call without an answer (log, environment), kept in order. |
| `cancel` | Cancel a `call` in progress. |
| `out` | Standard or error output of a process. |
| `exit` | End of a process: status, code, error message; for a `HackBackground` task, `jr` (JSON result), `jf` (original exception) and `jx` (task rejected). |
| `killed` | Acknowledgment of a `kill`. |
| `stopdone`, `sstopped` | End of processing of a `stop` / `sstop`. |
| `log` | Diagnostic line from the worker. |
| `hres` | Answer to an `hreq` (`web/3`). |
| `ui` | ADR 0133 trial: output of window `p` to its host on the page (`d`: batch of DOM operations `b`, dialog request `dlg`). |

`start` carries `k`: 0 or absent for a command, 1 for a service, 2 for a
`HackBackground` task (ADR 0140), 3 for a window of the ADR 0133 trial (fields
`ra`/`rt`: the platform runner that renders it).

### Events and file watching (ADR 0124, ADR 0138)

The bus stays in the OS. `RemoteEventGateway` and `RemoteWatchGateway`
(`Worker/RemoteEvents.cs`) send the operations below; `RemoteProcessSession`
creates each subscription **on behalf of the pid** and `RemoteEventRouter`
pushes what it receives through `ev` messages.

| Operation | Form | Role |
|---|---|---|
| `ev.sub` | `call` | Subscribe to a topic (`t`) or to a kernel event type (`e`), with the capacity of a bounded channel. |
| `ev.unsub` | `note` | `Dispose` of the subscription. The end, the `kill` or the death of the worker unsubscribe on the OS side without waiting. |
| `ev.pub` | `note` | Publish to a topic (type name and JSON). Provisional result `Delivered` in the worker, refusal in the app's log (ADR 0138 §3). |
| `ev.pubr` | `call` | `PublishAsync` (ADR 0145): the same publication, with the OS's result (`o`) and the subscribers' faults (`f`) as the answer; nothing in the log. |
| `ev.chan` | `note` | `RegisterSharedChannel` (`VirtualPath` scope only). |
| `ev.ack` | `note` | Return the credit: 256 messages in flight per subscription. |
| `watch.open` | `call` | `WatchAsync`: the OS opens the watch with the pid's permissions. |
| `ev` (OS → worker) | message | A delivery: subscription, JSON, topic, publishing app, date. Delivered in the process's ambient context. |

On the OS side, the buses index these subscriptions by type name (`IWireTopicMessageBus`, `IWireEventBus`): a
publication from a worker is materialized as an object only once per type for
the main-thread subscribers.

Each `call` is served on the main thread by `RemoteProcessSession` **under the
process's execution context** (`HackExecutionContext.Enter` then
`EnterKernel`): the same capability checks and the same accounting as locally.
Output has a credit of 64 KiB in flight; a regular file of at most 16 MiB is
read in one go, beyond that in blocks.

### `HackBackground` tasks (ADR 0128, ADR 0140)

A task launched by `HackBackground.RunAsync` is a child process of the app that
the OS creates on the main thread (`BackgroundJobService`), then places in a
pool worker through `WorkerRemoteProcessHost.RunBackgroundJobAsync`: a `start`
message with `k = 2`, the target `jt` (`asm`, `ty`, `me`, `it`) and the input
`ji`. The worker loads the assembly (`KnownAssemblies`), binds the static
method or the task class, runs the task in the process's context and returns
the result in `exit`. Without a worker, the task runs on the main thread with
the same serialization ([`background-jobs.md`](../framework/background-jobs.md)).

| Operation | Form | Role |
|---|---|---|
| `bg.progress` | `note` | A progress report from the task (`f`, `m`), already limited to 10 per second. |
| `bg.run` | `call` | A process already in a worker launches a task (`jt`, `ji`, `n`, `ni`, number `j`); the OS places it like any other and sends back its progress through `bgp` messages, then the result `{r}`. |

Remote errors: `bgfault` (`ty`, `st`) carries a `HackBackgroundJobException`
with the original type and stack; `killed` carries the reason `rs`.

### Pool, ready worker, overflow (ADR 0070 §2-3, ADR 0071)

| Role | When |
|---|---|
| `pool` | One worker per virtual core: `clamp(CpuCapacity, 1, capacité - 1)`, cores 0..n-1. |
| `ready` | Always one, once the system has started; replaced as soon as it is taken. |
| `dedicated` | Takes the ready worker for a `dedicated` process; released at its end. |
| `overflow` | The ready worker taken by a command when no core is free; removed after 30 s empty. |
| `web` | A single one for all sites (`AppKind.Web`, ADR 0073 §1): the first site takes the ready worker, the following ones share it; terminated when its last site stops. HTTP requests through `hreq`/`hres` ([`listening-ports-and-dns.md`](../framework/listening-ports-and-dns.md)). |

A `pool` or `overflow` worker that hosted a victim of the OOM killer is removed
as soon as it hosts no process any more, then replaced: its WebAssembly memory
never shrinks (ADR 0142 §5, [`oom-killer.md`](oom-killer.md)).

- **Capacity**: `max(2, navigator.hardwareConcurrency)` workers in all. At the
  ceiling, a request waits for a worker to be freed and `/var/log/syslog` notes
  it once ("worker pool saturated").
- **Startup**: at the end-of-boot signal (desktop displayed), then
  `requestIdleCallback` (2 s at most): the ready worker first, then the cores.
  Before this signal, a request starts its own worker and waits for it.
- **Free**: a ready worker with no process, or whose last heartbeat is less
  than 750 ms old **and** arrived at least one period after the last process
  placed on it (ADR 0072 §6): a command that loops as soon as it is launched
  does not attract the next one behind it.
- **Queue** (ADR 0127 §1, `WorkerRunQueue`): at the ceiling, requests wait by
  increasing `nice` then in arrival order; a process with a positive `nice`
  value does not take the ready worker while another worker is starting. Priority does not slice time:
  nothing is preempted.
- **Placement**: the least loaded free core; the steps of a pipeline
  (`ExecutionPlacement.BeginGroup` in `ShellInterpreter`) go to the same
  worker.

### Heartbeat and escalation (ADR 0070 §5)

- Every 250 ms, the worker sends the CPU times of each process
  (`utime`, `stime`, slices, longest slice, allocations) measured by
  `WorkerCpuMeter` on the context-switch hook, its runtime's memory and its busy
  time. They feed `/proc/<pid>/stat`, `status`, `/proc/stat` (the worker's core)
  and `/proc/meminfo` through `WorkerAwareCpuTimeAccountant` and
  `WorkerAwareMemoryAccountant`.
- With no heartbeat for 5 s, the worker's processes become
  **Not responding** and syslog writes "worker N: not responding (pids …)".
  Time without a heartbeat is counted as busy (ADR 0072 §6).
- **Step 3**: `kill` to the worker; if it confirms (`killed`) within the
  second, it keeps serving.
- **Step 4**: otherwise (or after the `SIGTERM` grace period), the OS
  terminates the worker. Its other processes exit `Killed` with "[pid] killed: worker
  N terminated to stop pid P" in syslog, the shell writes "Killed", and the
  ready worker takes over the core.

### `/proc/hackeros/workers`

Present only when the host has workers. The first lines say whether the page is
isolated (ADR 0131 §5, ADR 0143 §8), then comes the pool:

```
cross-origin-isolated: yes
isolation-reason: -
isolation-setting: auto
embedder-policy: credentialless
shared-pipes: yes
sync-calls: yes
memory-measure: browser
timer-resolution-us: 5
capacity 4
pool_size 1
worker role       state          core memory_kb heartbeat_ms busy_ms pids
2      pool       running        0    41216     112          380     57,58
1      ready      idle           -    38912     98           0       -
```

Roles: `pool`, `overflow`, `dedicated`, `ready`, `web`. States: `starting`, `idle`,
`running`, `not-responding`, `terminated`. PIDs (comma-separated)
are filtered by the reader's `/proc` visibility. Reasons for a non-isolated
page: `setting`, `missing-headers`, `insecure-context`, `browser`,
`not-browser` (outside a browser, tests).

### Cross-origin isolation (ADR 0131, ADR 0143)

When the page is isolated (`crossOriginIsolated`), workers gain:

- **shared pipes**: two 1 MiB rings per worker
  (`wwwroot/sharedRing.js`), one per direction, carry all the protocol's text
  messages; the reader drains the ring before any message received through
  `postMessage`, a message that is too big goes through `postMessage`, nobody
  blocks;
- **synchronous calls**: `ListProcesses` (operation `proc.list`) and `Kill`
  of another process wait for the OS's answer through `Atomics.wait` in the
  worker (10 s at most, answer of 1 MiB at most, `"s": true` in the `call`);
  the main thread never waits;
- **memory measured by the browser**: `IWebWorkerMemoryMeter`, every
  20 s (`WorkerPoolOptions.MemoryMeasurePeriod`), attributed through
  `?hackeros-worker=N` in the worker's URL, valid for three periods;
- **5 µs timers** (`performance.now()`), with no code to wire.

Without isolation, everything stays as before: messages only, synchronous
members refused with the same message, memory from the heartbeat.

Who sets the headers: the Ecosystem's service worker (according to
`HackerOs:CrossOriginIsolation` in `appsettings.json`, fallback `require-corp`
with a single reload), the middleware of `test/test` and
`HackerOs.Server`. The Ecosystem served on `localhost` or `127.0.0.1` has no
service worker, hence no isolation (ADR 0143 §3): try it on
`127.0.0.2`.

#### Audit before enabling by default (ADR 0131 §3)

Headless Chromium 140, Release publication of the Ecosystem served on
`127.0.0.2` (service worker active) and `test/test` (middleware), on
2026-10-18. Result: **nothing breaks, `AuditPassed = true`**.

| Surface | Check | Result |
|---|---|---|
| HTML rendering of the simulated browser and ASP.Hack sites | UI E2E suite under isolation: `BrowserHtmlRenderingTests`, `BrowserBlazorSiteTests`, `BrowserIntranetTests`, `BrowserHomePageTests`, `BrowserSiteDataTests`; `srcdoc` iframes inherit the page's COEP | passes |
| Detached windows (`window.open`) | `BrowserDetachedWindowTests` under isolation: the window is same-origin, COOP `same-origin` keeps the link | passes |
| Fallback to the real network, external images | image and `no-cors` `fetch` from another origin without CORP: loaded with `credentialless` | passes (blocked only in `require-corp`, ADR 0143 question 3) |
| Monaco (CodeEditor), xterm.js (Terminal) | new file, typing in Monaco; commands in the terminal | passes, no console error |
| Downloads (`download.js`), `.hosbak` | `blob:` link with `download`: the download starts; `FileExplorerDownloadTests` under isolation | passes |
| PWA installation, service worker update | first load on `127.0.0.2`: installation, single reload (`controllerchange`), isolated page; `HostSmoke` `ecosystem-isolated` | passes |

With isolation, `seq 30000 | wc -l` gives `30000` through the rings,
`sleep 1 & ps` works, and `sample-cmd --processes` answers.

## Usage

Host side:

```csharp
if (OperatingSystem.IsBrowser())
{
    builder.Services.AddSingleton<IWebWorkerRuntime, BrowserWebWorkerRuntime>();
    builder.Services.AddHackerOsWorkerProcesses(o => o with { PreloadAssemblies = ["HackerOs.Commands.dll"] });
}
```

`WorkerPoolOptions`: `HeartbeatPeriod` (250 ms), `UnresponsiveThreshold`
(5 s), `BusyThreshold` (750 ms), `KillAcknowledgeTimeout` (1 s),
`OverflowIdleTimeout` (30 s), `IdleWait` (2 s), `VirtualCores` (from the
hardware profile), `PreloadAssemblies`, `KnownAssemblies`, `MeterCpu`.

`KnownAssemblies` (ADR 0124 §4): the app assemblies known at build time
(the Ecosystem puts the catalog's `LazyAssemblyNames` there). A worker loads any
of them on demand and keeps it for subsequent requests; another one, absent
from its runtime, is refused: the process writes the error to its error output
and exits with code 127. An empty list imposes nothing.

Demonstration command: `stress [-c N] [-t SECONDES] [--yield]` runs N
computation loops without yielding (`--yield`: with stop points).

### Process limits (ADR 0125, ADR 0139)

The limits stay in the OS ([`process-limits.md`](process-limits.md)).

| Operation | Form | Role |
|---|---|---|
| `fs.fdopen` | `call` | Take a descriptor for a write stream (`OpenDescriptorAsync`); `EMFILE` refusal at the limit. |
| `fs.fdclose` | `note` | Return this descriptor; the end of the process returns the others. |
| `proc.rlimit` | `call` | `SetResourceLimitAsync` (`setrlimit`/`prlimit`) on behalf of the pid. |

Enforcement is strict in a worker: the controller sends `SIGXCPU` through a
`stop`, then `SIGKILL` terminates the worker (step 4 of ADR 0069) if the
process does not yield. A read of at most 16 MiB arrives in a single answer:
its descriptor is returned as soon as this answer arrives (ADR 0139 §3).

## Key decisions

- The shell, its scripts and its pipes stay on the main thread; only external
  commands migrate (ADR 0072 §1-2).
- Explicit message encoding (`WireCodec`): no reflection-based serialization in
  a trimmed runtime.
- `LoopbackWebWorkerRuntime` runs each "worker" on a dedicated thread with its
  synchronization context: the tests cover the pool, the protocol and the
  escalation without a browser.
- `IAppProcessGateway.KillAsync` (default method) to kill from a worker
  (ADR 0072 §4).

## Tests

- `Tests/HackerOs.Platform.WorkerHost.Tests` (full OS without an interface,
  workers in local loopback):
  - `CommandParityTests`: **the 74 built-in commands** (`nslookup`, `ausearch`, `xdg-open`, `xdg-mime`, `passwd`, `hackbackup`, `dmesg`, `nice`, `renice` and `prlimit` included) run in both
    modes (main thread, worker) with the same output, the same code and the
    same file tree (`HACKEROS_PARITY_DUMP` to inspect);
  - `WorkerPoolTests`: start before / after the end of boot, ready worker
    taken and replaced, overflow and return to size, ceiling and syslog,
    pipeline on a single worker, command that loops as soon as it is launched,
    `/proc/hackeros/workers`;
  - `EscalationTests`: `kill -9` of a loop (neighbors killed, syslog, core
    replaced), "Not responding" during the blockage, `SIGTERM` then
    escalation, step 3 without terminating the worker, Ctrl+C, error in a
    worker;
  - `DedicatedServiceTests`: service in a dedicated worker, `systemctl stop`;
  - `RemoteEventsTests` (ADR 0124): main thread / worker parity of topics,
    channels, kernel events, `Watch` and refusals; undeclared load;
    unsubscribe on `kill`; sample service in a dedicated worker;
    `sample-cmd` (third-party sample project) loaded on demand; refusal of an
    unknown assembly.
- `Tests/HackerOs.App.Abstractions.Tests`: `Execution` in the mapper, the
  validator, the serializer and the schema.
- `Tests/HackerOs.HostSmoke.Tests` (`build/verify-hosts.sh`): on the three
  hosts (the server serves the same OS since ADR 0130), pipeline,
  `/proc/hackeros/workers`, `echo` during `stress`, "not-responding",
  `kill -9` and `kill`.

## Task list for work item `rebuild/5-worker-processes`

- [x] `AppExecutionHost` in `[AppManifest]`, the mapper, the validator, the schema and `manifest.json` (defaults from ADR 0070 §1).
- [x] Contracts `IWebWorkerRuntime`, `IWorkerPoolStatus`; `IRemoteProcessHost` in the orchestrator.
- [x] `HackerOs.Platform.WorkerHost`: protocol, worker process host, remote gateways, standard streams through messages.
- [x] JS module in `HackerOs.Infrastructure.Browser/wwwroot` and `BrowserWebWorkerRuntime`.
- [x] Pool per virtual core, pipeline placement, dedicated workers.
- [x] Worker always ready after boot, overflow, return to size, ceiling and syslog (ADR 0071).
- [x] Heartbeat, CPU and memory measurements, "Not responding", step 4 of the escalation.
- [x] `/proc/hackeros/workers` and "Workers" section of the task manager.
- [x] Commands: declared or default host; sample service in `MainThread` (reason in a comment), in a dedicated worker since `exec/1-remote-gateways`.
- [x] Server: information line; `test/test` like the Ecosystem.
- [x] Parity tests of the 63 commands, pool and escalation tests.
- [x] `v3/4-intents`: the ADR 0096 intents (`int.edit`, `int.reveal`, `int.uri`, `int.settings`, `int.send`) and the delivery of an intent to a running process (`intent` message, `IntentReceived`) cross the worker boundary; `xdg-open` parity.
- [x] Verification of the three hosts (`build/verify-hosts.sh`).
- [x] Documentation and ADR 0072.

## Task list for work item `exec/1-remote-gateways` (ADR 0124, ADR 0138)

- [x] "Wire" bus on the OS side (`IWireTopicMessageBus`, `IWireEventBus`), same authorization rules.
- [x] Operations `ev.*`, `watch.open` and `ev` message; credit of 256 messages.
- [x] Built-in codecs and `HackWireContextAttribute` (SDK 1.2; moved to `PublicAPI.Shipped.txt` by `exec/10-final`, ADR 0132 §2).
- [x] Unsubscribe at the end, at `kill` and at the worker's death.
- [x] Sample service and `hackwebd` in a dedicated worker; review of the 16 `mainThread` commands.
- [x] `KnownAssemblies`, refusal with code 127; `sample-cmd` in the Ecosystem and the hosts test.
- [x] Parity tests (`RemoteEventsTests`, `WireTopicMessageBusTests`).
- [x] Documentation, ADR 0138, index.

## Task list for work item `exec/2-process-limits` (ADR 0125, ADR 0139)

- [x] Operations `fs.fdopen`, `fs.fdclose` and `proc.rlimit`.
- [x] Process descriptors returned at its end in `RemoteProcessSession`.
- [x] `ProcessLimitsTests` on the main thread and in a worker.

## Task list for work item `exec/5-background-jobs` (ADR 0128, ADR 0140)

- [x] Start `k = 2`, fields `jt`/`ji`, end `jr`/`jf`/`jx`.
- [x] Note `bg.progress`, call `bg.run` and `bgp` messages for a task launched from a worker.
- [x] Errors `bgfault` and reason `rs` of `killed`.
- [x] Main thread / worker parity tests (`BackgroundJobTests`).

## Task list for work item `exec/9-cross-origin-isolation` (ADR 0131, ADR 0143)

- [x] Setting `HackerOs:CrossOriginIsolation`, audit constant and copy in the service worker.
- [x] Headers: the Ecosystem's service worker (fallback `require-corp`), middleware of `test/test` and `HackerOs.Server`.
- [x] Page probe (`crossOriginIsolation.js`), reasons, `syslog` line.
- [x] Shared rings (`sharedRing.js`) in `workerHost.js` and `workerBootstrap.js`.
- [x] Synchronous calls `ListProcesses`/`Kill`, operation `proc.list`, `sample-cmd --processes` and `--kill`.
- [x] Per-worker memory measured by the browser (`IWebWorkerMemoryMeter`).
- [x] Isolation lines of `/proc/hackeros/workers`, Diagnostic badge.
- [x] Follow-up (ADR 0146, Benoit's choice C): `HackCrossOriginIsolation` in the SDK, without a capability, operation `coi.get`; Diagnostic no longer declares `filesystem.system.read`; `sample-cmd --isolation`.
- [x] Audit in Chromium, `AuditPassed = true`.
- [x] Tests: probe and setting, synchronous calls in loopback, rings under Node, `HostSmoke` and `ecosystem-isolated`/`ecosystem-isolation-off` journeys.

## Measurements of the window-in-worker trial (`exec/6`, ADR 0129 and 0133)

Trimmed Release publication, headless Chromium. A ready, empty worker occupies
**55.4 MiB** of WebAssembly linear memory (the runtime and the 87 boot
assemblies, ≈ 20.3 MB of which 8.67 MB for `HackerOs.AppSdk.Icons`) and
5.7 MiB of managed heap. With the Calculator: 66.5 MiB. Opening +≈ 340 ms
(ready worker), click latency p95 +3.3 ms. Details and verdict in
[ADR 0133](adr/0133-window-rendering-in-worker-verdict.md).
