# System metrics: CPU, disk, network and disk space

Implementation of [ADR 0057](adr/0057-proc-metrics-for-task-manager.md),
work item `rebuild/4-proc-metrics` of phase 4 (work item table of
[ADR 0058](adr/0058-base-app-features.md)). The files produced are described in
[`virtual-proc-and-dev.md`](virtual-proc-and-dev.md); this document describes
the counters that feed them.

## Role

The task manager ([`task-manager.md`](task-manager.md), work item
`4-task-manager`), `ps` and any third-party app read system metrics from
`/proc`, as on Linux:

| File or API | Metric | Source |
| --- | --- | --- |
| `/proc/stat` | cumulative CPU time per core, in `USER_HZ` | **measured** when CPU accounting is active (ADR 0067, [`cpu-accounting.md`](cpu-accounting.md)), **simulated** otherwise: resource simulator |
| `/proc/<pid>/stat` | `utime` and `stime` of a process; `priority` and `nice` | time: **measured** at context switch (ADR 0067), otherwise rebuilt from the simulator; priority: `nice` value from the process manager (`nice`, `renice`, ADR 0127, [`process-priority-and-unresponsive-windows.md`](process-priority-and-unresponsive-windows.md)) |
| `/proc/<pid>/io` | bytes read and written by a process | **counted**: file service and network gateway |
| `/proc/<pid>/limits`, `/proc/<pid>/fd/` | limits `cpu`, `rss`, `nofile`, `nproc` and open descriptors (ADR 0125, 0139) | limits table and descriptor table of the process manager ([`process-limits.md`](process-limits.md)) |
| `/proc/<pid>/oom_score`, `/proc/vmstat` (`oom_kill`) | OOM killer score; number of processes killed since startup (ADR 0126, 0142) | measured `VmRSS`, `oom_score_adj` table, OOM killer counter ([`oom-killer.md`](oom-killer.md)) |
| `/proc/diskstats` | requests, sectors and time of the `hda` disk | **counted**: IndexedDB transactions of the file system |
| `/proc/net/dev` | bytes and packets of `lo` and `eth0` | **counted**: apps' network gateway |
| `/proc/mounts` | mount table | mount router |
| `HackDriveInfo` | size and free space of `/` | hardware profile, stored content, browser quota |
| `/proc/hackeros/workers` | cross-origin isolation of the page (ADR 0131, 0143), then the OS's Web Workers: role, state, core, runtime memory, heartbeat age, busy time, processes (ADR 0070) | **measured**: heartbeat of each worker, every 250 ms; isolated page, memory of each worker measured by the browser (`performance.measureUserAgentSpecificMemory()`, every 20 s, `memory-measure: browser`) ([`worker-processes.md`](worker-processes.md)); absent when the host has no workers |

A `cat` of a large file raises the process's `rchar` and the sectors read of
`hda`; a page loaded by the browser raises `eth0`. The browser does not measure
CPU per app: since phase 5, the OS times it itself at each context switch
(ADR 0067), when accounting is active; memory is measured likewise (ADR 0068,
"Measured memory" section below).

### Two sources for a process's CPU and memory (ADR 0066)

| Source | When | Indicator |
| --- | --- | --- |
| **simulated**: `IResourceSimulator`, the app's `ResourceProfile` profile | accounting switched off at build (`HackerOsCpuAccounting`, `HackerOsMemoryAccounting`) or in `appsettings.json` (`HackerOs:ResourceAccounting`), accounting not yet implemented, simulated process without code | `CpuSource: simulated`, `MemSource: simulated`; "estimated" mention in the task manager |
| **measured**: `ICpuTimeAccountant`, `IMemoryAccountant` fed by the context-switch hook | accounting delivered and enabled, and the accountant answers for this process | `CpuSource: measured`, `MemSource: measured` |

The `/proc` provider receives the two accountants through `ProcMetricsSources`
(`CpuAccountant`, `MemoryAccountant`) and falls back to the simulator as soon
as an accountant does not answer. Since the `exec` series,
`ProcMetricsSources` also carries the limits tables (`ResourceLimits`) and
descriptor tables (`FileDescriptors`), the `HackBackground` tasks
(`BackgroundJobs`), the `oom_score_adj` table (`OomScores`), the `OomKills`
counter and the isolation state (`CrossOriginIsolation`). The
`5-accounting-switches` work item delivers the switches, the hook and the null
accountants; the `5-memory-accounting` work item measures memory (next
section); the `5-cpu-accounting` work item measures CPU with
`MeasuredCpuTimeAccountant` ([`cpu-accounting.md`](cpu-accounting.md)).
Details: [`resource-accounting.md`](resource-accounting.md).

### Measured memory (ADR 0068)

When memory accounting is delivered and enabled, a process's memory is
reconstructed in four layers ([`memory-accounting.md`](memory-accounting.md)):

| Layer | `/proc/<pid>/status` field | Source | Accuracy |
| --- | --- | --- | --- |
| Image | `VmExe`, `RssFile` | size of the assemblies, measured by the host build (`BuiltInApplications.json`) | exact |
| OS resources | `VmData` | stream buffers, clipboard, pipes, in-flight sockets, fixed cost per window | exact |
| Allocations | `HackAllocBytes` | `GC.GetAllocatedBytesForCurrentThread()` at each context switch | exact |
| Resident | `RssAnon` | real heap distributed after each GC in proportion to recent allocations | **estimate** |

`VmRSS = RssAnon + RssFile + VmData`. `/proc/meminfo` keeps `MemTotal` from the
hardware profile and adds `AnonPages`, `Mapped` (each assembly counted once) and
`Slab` (kernel share of the processes and share of the heap allocated by the OS
itself). These measured figures are also the ones enforced by a process's `rss`
limit (`ulimit -m`, ADR 0125, [`process-limits.md`](process-limits.md)) and by
the OOM killer, **disabled by default** and enabled in
`/etc/hackeros/memory.config` (ADR 0126, 0142, [`oom-killer.md`](oom-killer.md)):
neither acts on simulated memory.

In an isolated page (ADR 0131, ADR 0143 §6), the runtime memory of each Web
Worker comes from the browser (`performance.measureUserAgentSpecificMemory()`,
every 20 s, valid for three periods) instead of the heartbeat; it is
distributed among the worker's processes as before, and `MemSource` stays
`measured` ([`memory-accounting.md`](memory-accounting.md)).

Calculation rules (constants of `MemoryAccountingConstants` and
`AppNetworkGateway`, covered by tests):

| Rule | Value |
| --- | --- |
| Half-life of a process's weight in the heap distribution (`ResidentHalfLife`) | 10 s |
| Fixed cost of an open window (`WindowBytes`) | 512 KiB |
| Buffers of an in-flight network operation (`SocketBufferBytes`) | 144 KiB |
| Clipboard, pipes | 2 bytes per character held |
| Buffer of a write stream | buffer capacity |

The resident layer remains an estimate: the runtime does not say who owns an
object of the shared heap. A process launched since the last GC does not have
an `RssAnon` yet.

The counters live in memory, start from zero at startup and are never
persisted.

## Architecture

### Contracts

`Shared/HackerOs.Simulation.Abstractions/Metrics/SystemMetricsContracts.cs`:

| Contract | Role |
| --- | --- |
| `IProcessIoAccounting`, `ProcessIoCounters` | counters of `/proc/<pid>/io`; `Retain` forgets finished processes |
| `IBlockDeviceStatistics`, `BlockDeviceCounters`, `BlockIoDirection` | statistics of `hda` (major 8, minor 0); `Begin` then `Complete` per request |
| `INetworkInterfaceStatistics`, `NetworkInterfaceCounters`, `NetworkInterfaceNames` | counters of `lo` and `eth0` |
| `ICpuTimeAccounting`, `CpuTimes` | `user`, `nice`, `system`, `idle` times per core |
| `IStorageUsageProvider`, `StorageUsage` | used space and browser quota |

`VirtualHardwareProfile.StorageCapacityBytes` (8 GiB by default,
`DefaultStorageCapacityBytes`) gives the disk size.

### Implementations

| Type | Project | Role |
| --- | --- | --- |
| `ProcessIoAccounting`, `BlockDeviceStatistics`, `NetworkInterfaceStatistics`, `CpuTimeAccounting` | `Platform.Core/Metrics` | in-memory counters, under lock |
| `ResourceSamplingService` | `Platform.Core/Processes` | the scheduler's "tick": once per simulated second, samples each active process and accumulates CPU time |
| `FileSystemService` | `Platform.Core/FileSystem` | counts each process's reads and writes |
| `AppNetworkGateway` + `NetworkTrafficMeter` | `Platform.Core/Network` | counts each app's traffic |
| `ProcFileSystemProvider` + `ProcMetricsSources` | `Platform.Core/FileSystem/Virtual` | renders the files |
| `AppDriveGateway` | `Platform.Core/Execution` | the OS's `statfs` behind `HackDriveInfo` |
| `IndexedDbInteropAdapter` | `Infrastructure.Browser/Interop` | times each transaction and reports it to `hda` |
| `IndexedDbStorageUsageProvider` | `Infrastructure.Browser/FileSystem` | real used space and quota of the browser (`navigator.storage.estimate()`, ADR 0099 §2); with no known quota, deduplicated content |

Wiring: `EcosystemServiceCollectionExtensions` registers the counters as
`Scoped` (one instance per machine, like the process manager), passes them to
the `/proc` provider (`ProcMetricsSources`), to the file service, to the
IndexedDB provider and to `AppExecutionContextFactory`
(`SystemMetricsServices`). `EcosystemBootCoordinator` starts
`ResourceSamplingService` right after the clock engine.

## How each metric is taken

### CPU (`/proc/stat`)

CPU accounting active (ADR 0067): `CpuTimeAccounting` ignores simulated
samples and renders on `cpu0` the real times of the main thread (`user` = sum
of the `utime`, `system` = sum of the `stime` plus the compositor, `idle` = the
rest); the other cores of the hardware profile stay `idle`. With Web Workers
(ADR 0070), core `k` additionally receives the busy time of the pool worker
that carries that core (`cpu0` is therefore shared between the main thread and
the core 0 worker), and the `utime` of a process in a worker comes from its
worker's heartbeat, plus the kernel time spent on the main thread serving its
calls; a worker that no longer beats counts this time as busy by its processes
(see [`worker-processes.md`](worker-processes.md)). See
[`cpu-accounting.md`](cpu-accounting.md). The `nice` column of `/proc/stat`
stays at `0` (`CpuTimes.Nice`): the time of a process whose priority was
changed by `nice`/`renice` (ADR 0127) is still counted in `user`. What follows describes the simulated mode,
always used when accounting is switched off.

Before this work item, nothing called `IResourceSimulator.Tick` in a real host:
`status`, `meminfo` and `loadavg` stayed at zero. `ResourceSamplingService`
subscribes to the simulated clock and, every simulated second:

1. reads the active processes;
2. asks the simulator for a sample (`Tick`);
3. adds the interval to `CpuTimeAccounting`;
4. forgets the I/O counters of vanished processes.

`CpuTimeAccounting` gives each core `100 × seconds` units per interval. A
process starts on core `pid mod cores` and overflows onto the following ones
when it is full, like a scheduler with affinity: two busy processes land on
different cores. The time goes into `system` for OS processes (services
declared system by the host and processes unknown to the catalog), into `user`
otherwise; the rest of the core goes into `idle`.

The simulator derives each process's usage from its `ResourceProfile`, that is,
from the `Resources` weights declared by the manifest (ADR 0012). Until the
`4-task-manager` work item, `AppLifecycleOrchestrator` launched each process
with `ResourceProfile.None`: CPU and memory stayed at zero despite sampling.
The orchestrator now passes the manifest's profile (see
[`task-manager.md`](task-manager.md)).

One second of sampling keeps the simulator's bounded history (120 samples) over
two minutes, enough for the charts.

### Per-process I/O (`/proc/<pid>/io`)

`FileSystemService` is the only path to files, for app gateways as well as
commands, and the only place that knows a path's mount. When the caller has a
PID (`AppOperationContext.ProcessId`):

- **Read**: the returned stream is wrapped; each read call counts one `syscr`
  and the bytes returned in `rchar`. A `head -c 10` of a large file costs ten
  bytes.
- **Write**: the content source is wrapped; the bytes the provider pulls from it
  go into `wchar`, with one `syscw` per write.
- **Storage**: `read_bytes` and `write_bytes` receive the same bytes when the
  mount is not virtual (`/proc` and `/dev` are excluded).

`AppNetworkGateway` adds network traffic to `rchar` and `wchar`, never to
`read_bytes` or `write_bytes`. A system operation without a process is counted
nowhere. `cancelled_write_bytes` stays at `0`.

### Disk (`/proc/diskstats`)

The IndexedDB provider passes `IBlockDeviceStatistics` to its three adapters
(metadata read, metadata write, content). `IndexedDbInteropAdapter.ExecuteAsync`
marks the request in flight, times it and completes it:

- read-only transaction: one read; read-write: one write;
- size: the file's bytes for content, a 4 KiB block for a metadata transaction
  (the size of an inode update on Linux);
- a failed transaction counts in busy time, not in completed requests;
- looking up a path is a single transaction whatever its depth (`resolvePath`
  operation, ADR 0090), therefore a single read, like a kernel whose directory
  entry cache avoids rereading each prefix.

`BlockDeviceStatistics` holds `io_ticks` (time with at least one request in
flight) and `time_in_queue` (time weighted by queue depth) like the kernel: at
each queue change, it adds the time elapsed since the previous one. The
"merged" fields stay at `0`: there is no queue to merge.

### Network (`/proc/net/dev`)

`AppNetworkGateway` remains the single point of passage ([`sdk-network.md`](../framework/sdk-network.md)).
Each public method calls its implementation, then `NetworkTrafficMeter` counts
the exchange on `eth0`:

| Operation | Sent | Received |
| --- | --- | --- |
| DNS (`Resolve`, `ReverseLookup`) | IP + UDP + RFC 1035 question | question + 16 bytes per address |
| Ping | 84 bytes (IP + ICMP + 56) | 84 bytes if the host answers |
| HTTP | IP + TCP + request line, headers, cookies, form | IP + TCP + status line, headers, body; one extra round trip per redirect |
| TCP probe | a 40-byte SYN per port | a SYN-ACK or RST per unfiltered port |
| OS detection | 16 probes | 16 replies if the host is known |

A packet is a request or a reply (ADR 0057). A host not found, unreachable, a
timeout or a refused connection count the request and a receive error (`errs`).
A call refused for lack of capability does not go out: nothing is counted.
Since `web/3-listener-dns`, an exchange with a local port (target resolved to
loopback by `hackdns`) is counted on `lo` and not on `eth0`
([`listening-ports-and-dns.md`](../framework/listening-ports-and-dns.md)).

### Mounts (`/proc/mounts`)

`MountDescription.From` describes each mount of the router: `procfs` →
`proc /proc proc ro`, `devfs` → `devfs /dev devfs rw`, any other provider →
`/dev/hda <point> idbfs rw`. The `/proc` provider reads the router on demand,
because the router contains it.

### Disk space (`HackDriveInfo`)

`AppDriveGateway` returns a `HackDriveSnapshot` per non-virtual mount
(currently `/`):

- **size**: `StorageCapacityBytes`, brought down to the browser quota when it is
  smaller;
- **used**: since ADR 0099 §2, everything the origin stores according to
  `navigator.storage.estimate()` (files, accounts, settings, site databases).
  When the browser gives no quota, it is still the sum of the lengths of the
  distinct contents referenced by a file entry (two identical files count once,
  like their deduplicated blocks);
- **free**: size − used, never negative.

No capability is required, like `df`.

## Usage

```csharp
// Usage CPU entre deux lectures, comme top.
string before = await HackFile.ReadAllTextAsync("/proc/stat");
await Task.Delay(1000);
string after = await HackFile.ReadAllTextAsync("/proc/stat");

foreach (HackDriveInfo drive in await HackDriveInfo.GetDrivesAsync())
{
    Console.WriteLine($"{drive.Name} {drive.DriveFormat} {drive.AvailableFreeSpace}/{drive.TotalSize}");
}
```

Reading `/proc` requires `filesystem.system.read`. `/proc/<pid>/io` is `0400`:
only the owner and the `System` authority read it.

## Key decisions and clarifications relative to ADR 0057

- **File counting in `FileSystemService`.** The ADR speaks of "the file
  gateway": this is the service behind all the gateways
  (`AppFileSystemGateway`, commands, `HackFile`). Only it knows whether a path
  touches storage or a virtual mount.
- **`ResourceSamplingService` is new.** The ADR assumes the simulator advances
  at each tick; no code called it. Sampling happens every simulated second, not
  every 100 ms tick, to keep two minutes of history.
- **`hda` counts only the file system.** The other IndexedDB stores (users,
  groups, authorizations, synchronization) are not files; they do not go
  through this instrumented adapter.
- **Metadata and packet sizes are estimated.** IndexedDB does not give the size
  of its metadata writes, nor does the simulated network give that of its
  packets: a 4 KiB block per metadata transaction, and standard IP, TCP, UDP
  and ICMP headers around the data actually exchanged.
- **Disk time in real time.** The milliseconds of `diskstats` are measured on
  the real clock: this is the real time spent by IndexedDB.
- **`HackDriveInfo` reads its values once.** The browser only answers
  asynchronously; `GetDrivesAsync` returns frozen instances, to be requested
  again for fresh values.
- **Real browser figures (ADR 0099 §2).** `df` shows the used space and quota
  of `navigator.storage.estimate()`; the root reserve and the low-space alert
  use the same figures
  ([`system-backup.md`](system-backup.md#espace-de-stockage-adr-0099-2)).
- **Used space without orphan blocks.** Blocks awaiting cleanup are no longer
  referenced by any file and are not counted.

## Tests

- `Tests/HackerOs.Platform.Core.Tests/FileSystem/Virtual/ProcMetricsTests.cs`:
  exact format of `stat`, `diskstats`, `net/dev`, `mounts`; overflow from one
  core to the next; sampling per simulated second; `io` counted by the file
  service, without storage bytes for `/proc`; private `io`; busy time weighted
  by the queue.
- `Tests/HackerOs.Platform.Core.Tests/Execution/AppDriveGatewayTests.cs`: size,
  quota, free space never negative, `HackDriveInfo` in the ambient context.
- `Tests/HackerOs.Network.Tests/Wave4NetworkTests.ProcMetrics.cs`: DNS, ping,
  HTTP and capability refusal counted (or not) on `eth0` and in `rchar`/`wchar`.
- `Tests/HackerOs.Infrastructure.Browser.Tests/IndexedDbFileSystemReaderTests.cs`:
  deduplicated used space, transactions reported to `hda`.

## Task list for work item `rebuild/4-proc-metrics`

- [x] `Metrics` contracts in Simulation Abstractions; `VirtualHardwareProfile.StorageCapacityBytes`.
- [x] In-memory counters in `Platform.Core/Metrics`.
- [x] `ResourceSamplingService` started at boot: the simulator finally advances.
- [x] Per-process file counting in `FileSystemService`.
- [x] Traffic counting in `AppNetworkGateway` (`NetworkTrafficMeter`).
- [x] Counting of the file system's IndexedDB transactions for `hda`.
- [x] `/proc/stat`, `/proc/<pid>/io`, `/proc/diskstats`, `/proc/net/dev`, `/proc/mounts`.
- [x] `HackDriveInfo`, `IAppDriveGateway`, `IAppExecutionContext.Drives`, `AppDriveGateway`, `IndexedDbStorageUsageProvider`.
- [x] Wiring in the Ecosystem host.
- [x] Tests.
- [x] Documentation: this document, `virtual-proc-and-dev.md`, `app-sdk.md`, `sdk-network.md`, index.
