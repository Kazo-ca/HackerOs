# Virtual `/proc` and `/dev`

Implementation of section 5.4 of the [rebuild plan](plans/ecosystem-rebuild-plan.md)
(decision D20) according to [ADR 0046](adr/0046-virtual-proc-and-dev-filesystems.md).

## Role

HackerOS exposes system state like Linux: `/proc` describes processes, memory,
CPU and load; `/dev` offers the `null`, `zero`, `random` and `urandom` devices,
plus `kmsg` when the host provides the kernel log (ADR 0126). Both mounts are
**computed on every read** and **never written to IndexedDB**.

## Architecture

Code: `Platform/HackerOs.Platform.Core/FileSystem/Virtual/`.

| Type | Role |
| --- | --- |
| `IVirtualFileSystemProvider` | Marker of a computed provider, never persisted. `FileSystemService` emits no `FileSystemChangeEvent` for its paths. |
| `VirtualFileSystemProvider` | Common base: stable identity (SHA-256 of `"<providerId>:<chemin>"`), revision = `CurrentTick + 1`, `PermissionDenied` refusal of any creation, deletion, move, copy and `chmod`. |
| `ProcFileSystemProvider` (`procfs`) | Mounted on `/proc`. Reads `IProcessManager`, `IResourceSimulator`, `VirtualHardwareProfile` and `ISimulationClock`, and the counters of `ProcMetricsSources` (ADR 0057, [`system-metrics.md`](system-metrics.md)). |
| `DevFileSystemProvider` (`devfs`) | Mounted on `/dev`. Four `CharacterDevice` entries as `0666`, plus `kmsg` as `0644` when the host provides the kernel log (`KernelLog`, ADR 0126). |

Wiring: `EcosystemServiceCollectionExtensions` passes both mounts to the
`FileSystemMountRouter`, for both WASM hosts. The process manager is resolved
**on demand**: it depends on the session service, which depends on the
`FileSystemSeeder`, which reaches `/proc` through the router. An immediate
resolution would close a cycle.

Mount points: the router does not merge mounts into the parent's listing.
`FileSystemSeeder` therefore receives the router and creates `/proc` (`0555`)
and `/dev` (`0755`), empty, **directly in the root provider**. `ls /` shows
them, then the virtual provider masks them.

Contracts touched:

- `FileSystemEntryKind.CharacterDevice` and `CharacterDeviceMetadata`
  (`Shared/HackerOs.Simulation.Abstractions/FileSystem/FileSystemEntryMetadata.cs`).
  The IndexedDB and memory providers refuse to create this kind
  (`UnsupportedOperation`). `ls -l` displays it with `c` (and links with `l`).
- `AppOperationContext.ProcessId` (`long?`), filled in by
  `AppExecutionContextFactory`. It serves `/proc/self` and visibility.

## Content

### `/proc`

| Path | Type and mode | Content |
| --- | --- | --- |
| `/proc/<pid>/` | folder `0555`, owner = the process's user | none |
| `/proc/<pid>/status` | file `0444` | see below |
| `/proc/<pid>/cmdline` | file `0444` | arguments separated and terminated by NUL ([`process-environment.md`](process-environment.md)); `appId\0` without an environment entry |
| `/proc/<pid>/environ` | file `0400` | `NOM=valeur` pairs terminated by NUL (phase 3); empty without an entry |
| `/proc/<pid>/cwd` | link `0777` | the process's current directory (phase 3); `/home/<login>` without an entry |
| `/proc/<pid>/stat` | file `0444` | one line in `proc(5)` format, 52 fields; `utime` and `stime` (14 and 15) in `USER_HZ`, measured when CPU accounting is active, otherwise `utime` rebuilt from the simulator (phase 5, ADR 0067, [`cpu-accounting.md`](cpu-accounting.md)); `priority` and `nice` (18 and 19) from the process's `nice` value (ADR 0127, [`process-priority-and-unresponsive-windows.md`](process-priority-and-unresponsive-windows.md)) |
| `/proc/<pid>/limits` | file `0444` | `proc(5)` table limited to the four enforced limits (`Max cpu time`, `Max resident set`, `Max processes`, `Max open files`), then `# enforcement: strict (worker)`, `best-effort (main thread)` or `off (HackerOs:ResourceLimits:Enabled=false)` and, without measurement, `# cpu, rss: inactive (not measured)` (ADR 0125 and 0139, [`process-limits.md`](process-limits.md)) |
| `/proc/<pid>/fd/` | folder `0500` | one link per open descriptor: `0`, `1`, `2` to `/dev/tty` (terminal) or `/dev/null`, then the file read or written (`exec/2`) |
| `/proc/<pid>/oom_score` | file `0444` | `VmRSS × 1000 / MemTotal + oom_score_adj`, bounded to `[0, 2000]`; `0` for an exempt process (`oom_score_adj` = `-1000`) (ADR 0126, [`oom-killer.md`](oom-killer.md)) |
| `/proc/<pid>/oom_score_adj` | file `0644` | `-1000` to `1000`; the owner raises the value, lowering it below the floor requires `sudo` (ADR 0142 §2) |
| `/proc/<pid>/io` | file `0400` | `rchar`, `wchar`, `syscr`, `syscw`, `read_bytes`, `write_bytes`, `cancelled_write_bytes` (phase 4, ADR 0057) |
| `/proc/self` | link `0777` | `/proc/<caller's pid>`, absent without a process |
| `/proc/meminfo` | file `0444` | `MemTotal`, `MemFree`, `MemAvailable` in kB; with measured memory (ADR 0068), also `AnonPages`, `Mapped`, `Slab` |
| `/proc/vmstat` | file `0444` | `nr_free_pages`, then `nr_anon_pages`, `nr_mapped`, `nr_slab_unreclaimable` with measured memory, then `oom_kill` (`exec/3`, ADR 0142 §7) |
| `/proc/cpuinfo` | file `0444` | one block per core (`ceil(CpuCapacity)`) |
| `/proc/uptime` | file `0444` | `<seconds since startup> <idle seconds>` |
| `/proc/loadavg` | file `0444` | `<1 min> <5 min> <15 min> <active>/<total> <last pid>` |
| `/proc/stat` | file `0444` | `cpu` and `cpuN` lines in `USER_HZ`, `btime`, `processes`, `procs_running`, `procs_blocked` (phase 4); real times of the main thread on `cpu0` when CPU accounting is active (ADR 0067) |
| `/proc/diskstats` | file `0444` | one line for `hda` (major 8, minor 0), 14 fields (phase 4) |
| `/proc/net/` | folder `0555` | contains `dev` and `tcp` |
| `/proc/net/dev` | file `0444` | kernel header, one line for `lo` and one for `eth0` (phase 4); local traffic is counted on `lo` since `web/3` |
| `/proc/net/tcp` | file `0444` | listening TCP sockets in `proc(5)` format (little-endian hexadecimal address, state `0A`, `uid`, inode), read from `ListeningPortTable`; `localhost` daemons included (`web/3`, [`listening-ports-and-dns.md`](../framework/listening-ports-and-dns.md)) |
| `/proc/mounts` | file `0444` | one line per mount, `fstab` format (phase 4) |

Example of `/proc/42/status`:

```text
Name:	code-editor-del
State:	R (running)
Pid:	42
PPid:	7
Uid:	aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
AppId:	org.hackeros.apps.code-editor-deluxe
Kind:	window
Session:	bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
VmRSS:	3072 kB
Cpu:	12.5
CpuSource:	simulated
MemSource:	simulated
```

`Name` is the last segment of the `appId`, truncated to 15 characters. For a
`HackBackground` task (ADR 0128, ADR 0140, [`background-jobs.md`](../framework/background-jobs.md)),
`Name` is the task's name (`code-editor:sea`, truncated the same way) and two
lines follow `Session`: `Job:` (the full name, `code-editor:search`) and
`Host:` (`MainThread` or `Worker`). `Cpu` is a percentage of one core
(`CpuUsage × 100`), without the `%` sign, to stay easy to parse. `CpuSource` and
`MemSource` (ADR 0066) say where `Cpu` and `VmRSS` come from: `measured` when
the phase 5 accounting answers for this process, `simulated` otherwise
(accounting switched off, not yet implemented, or simulated process without
code); see [`resource-accounting.md`](resource-accounting.md).

When a process's memory is measured (ADR 0068,
[`memory-accounting.md`](memory-accounting.md)), five lines follow `VmRSS`, in
Linux order: `RssAnon`, `RssFile`, `VmData` (kernel share), `VmExe` in kB, then
`HackAllocBytes` (bytes allocated since the process started, without a unit).
`VmRSS = RssAnon + RssFile + VmData`. A simulated process has none of these
lines: the format is exactly that of before phase 5.

When CPU is measured (ADR 0067), `Cpu` is the usage of the last second and four
lines follow `MemSource`:

```text
Responding:	1
LongTasks:	3
voluntary_ctxt_switches:	1520
nonvoluntary_ctxt_switches:	0
```

`Responding: 0` signals a "Not responding" process; `LongTasks` counts its
slices longer than 50 ms. With accounting switched off, these lines do not
appear: the file is exactly that of before phase 5. Details:
[`cpu-accounting.md`](cpu-accounting.md).

Calculation choices:

- `MemAvailable` equals `MemFree`: the simulation has no cache to free.
- The idle time of `uptime` is accumulated over all cores, as on Linux:
  `uptime × (cores − average usage)`, the average usage coming from the bounded
  system history.
- `loadavg` averages `TotalCpuUsage` over the history samples within each
  window. Since the history is bounded, the 5- and 15-minute windows reduce to
  the available samples.
- Timestamps: process start for `/proc/<pid>`, OS start
  (`now − tick × tick duration`) otherwise; the content of files is dated now.
  File size is always `0`.

Metric files (phase 4, [ADR 0057](adr/0057-proc-metrics-for-task-manager.md)).
Their counters and how they are taken are described in
[`system-metrics.md`](system-metrics.md). Examples:

```text
$ cat /proc/stat
cpu  4705 0 1210 93620 0 0 0 0 0 0
cpu0 2402 0 611 46790 0 0 0 0 0 0
cpu1 2303 0 599 46830 0 0 0 0 0 0
btime 1791640800
processes 312
procs_running 3
procs_blocked 0

$ cat /proc/42/io
rchar: 18342
wchar: 2210
syscr: 12
syscw: 3
read_bytes: 16384
write_bytes: 2048
cancelled_write_bytes: 0

$ cat /proc/diskstats
   8       0 hda 1204 0 9632 410 388 0 3104 220 0 630 630

$ cat /proc/net/dev
Inter-|   Receive                                                |  Transmit
 face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed
    lo:       0       0    0    0    0     0          0         0        0       0    0    0    0     0       0          0
  eth0:  482113     214    3    0    0     0          0         0    38211     214    0    0    0     0       0          0

$ cat /proc/mounts
/dev/hda / idbfs rw 0 0
devfs /dev devfs rw 0 0
proc /proc proc ro 0 0
```

- `stat` accumulates simulated CPU time since startup: a reader computes usage
  by difference between two reads, like `top`. `processes` is the last PID
  assigned (PIDs are assigned in order from 1).
- `io` follows the visibility rule of `environ`: the owner, or the `System`
  authority.
- Since phase 4, `ResourceSamplingService` advances the simulator every
  simulated second: `status`, `meminfo`, `uptime` and `loadavg` no longer stay
  at zero in a real host.

- `/proc/hackeros/workers` (ADR 0071 §5) exists only if the host has Web
  Workers: first the page's cross-origin isolation
  (`cross-origin-isolated: yes|no`, reason, setting, COEP, shared pipes,
  synchronous calls, memory measurement, timer resolution; ADR 0131
  §5, ADR 0143 §8), then capacity, pool size, then one line per worker (role,
  state, core, memory, heartbeat age, busy time, visible PIDs). Format:
  [`worker-processes.md`](worker-processes.md#prochackerosworkers).

Visibility: only active processes appear. The caller always sees their own
process, and the others with `process.list` or the `System` authority. An
invisible PID answers `NotFound`.

### `/dev`

| Path | Read | Write |
| --- | --- | --- |
| `/dev/null` | immediate end of stream | absorbed |
| `/dev/zero` | infinite stream of `0x00` | absorbed |
| `/dev/random`, `/dev/urandom` | infinite stream from `RandomNumberGenerator` | absorbed |
| `/dev/kmsg` (`0644`, only if the host provides the kernel log) | kernel lines of the session, `prio,seq,usec,-;message`; read by `dmesg` | root only: line appended to the log (`exec/3`, ADR 0142 §7) |

Infinite streams return at most 4 KiB per read and respect cancellation: a
reader bounds its own read. In asynchronous reading, they yield to the browser
every 16 blocks, so that a `cat /dev/zero` leaves the interface responsive and
can be interrupted. A write drains the received stream, succeeds without
changing revision and emits no event.

## Usage

Everything goes through the ordinary file API (`IFileSystemService`, then
`IAppFileSystemGateway` on the app side). Reading `/proc` or `/dev` requires
`filesystem.system.read`; writing to `/dev` requires `filesystem.system.write`.

`ps` now reads `/proc`: it lists `/proc`, reads each `/proc/<pid>/status` and
takes the creation time of `/proc/<pid>` as the start time. Its manifest
declares `filesystem.system.read` in addition to `process.list`. The `STAT`
column displays the Linux letter (`R`, `S`...). `kill` keeps
`IAppProcessGateway` to act.

## Key decisions

- **`ps` migration brought forward.** ADR 0046 (section 5) placed this migration
  in phase 3. It is done as early as phase 1, as section 5.4 of the plan asks,
  because it depends only on `/proc`. The phase 3 commands rework keeps it. The
  task manager moved to `/proc` in phase 4 ([`task-manager.md`](task-manager.md),
  `HackProcess`).
- **No change events**: the `IVirtualFileSystemProvider` marker is checked at
  the only place that publishes, `FileSystemService`.
- **Mutations refused by the provider itself**, in addition to the mode bits:
  even the `System` authority, which ignores modes, receives `PermissionDenied`.
  Two writes are an exception since the `exec` series: `/proc/<pid>/oom_score_adj`
  (the only writable file of `/proc`, ADR 0126 §4 and 0142 §2) and a line
  written to `/dev/kmsg` by root (ADR 0142 §7); neither is persisted.

## Tests

- `Tests/HackerOs.Platform.Core.Tests/FileSystem/Virtual/`: listing of `/`,
  exact formats of `status`, `meminfo`, `cpuinfo`, `uptime` and `loadavg`,
  visibility according to `process.list`, `/proc/self`, refusal of mutations,
  `/dev/null`, bounded read of `/dev/zero`, cancellation, refusal of
  `CharacterDevice` by storage, and no write to the root repository.
- `Tests/HackerOs.Commands.Wave5.Tests/PsCommandTests.cs`: `ps` against the
  real `/proc` provider.
- `Tests/HackerOs.Platform.Core.Tests/FileSystem/Virtual/ProcMetricsTests.cs`:
  phase 4 metric files (see [`system-metrics.md`](system-metrics.md)).

## Task list

- [x] Add `FileSystemEntryKind.CharacterDevice` and `CharacterDeviceMetadata`.
- [x] Have the IndexedDB and memory providers refuse `CharacterDevice`.
- [x] Add `AppOperationContext.ProcessId` and fill it in.
- [x] Write the `VirtualFileSystemProvider` base and the `IVirtualFileSystemProvider` marker.
- [x] Write `ProcFileSystemProvider`.
- [x] Write `DevFileSystemProvider`.
- [x] Cut change events for virtual mounts.
- [x] Create the `/proc` and `/dev` mount points in `FileSystemSeeder`.
- [x] Mount both providers in `EcosystemServiceCollectionExtensions`.
- [x] Display `c` and `l` in `ls -l`.
- [x] Make `ps` read `/proc`.
- [x] Write the contract tests.
- [x] `/proc/<pid>/limits` and `/proc/<pid>/fd/` (`exec/2-process-limits`).
- [x] `/proc/<pid>/oom_score`, `oom_score_adj`, `oom_kill` of `/proc/vmstat` and `/dev/kmsg` (`exec/3-oom-killer`).
