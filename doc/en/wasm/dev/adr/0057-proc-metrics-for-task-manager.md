> [Version française](../../../../fr/wasm/dev/adr/0057-proc-metrics-for-task-manager.md)

# ADR 0057: System metrics in `/proc` and `HackDriveInfo`

## Status

Accepted on 2026-10-10.

- **Plan phase:** phase 4: complete SDK and base apps
  ([`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), section 8.2,
  task manager: "All data comes from `/proc`").
- **Completes:** ADR 0046. The files described here are added to the existing
  `procfs` provider; its rules (read-only, computed on read, never
  persisted, size `0`, PID visibility) apply unchanged.
- **Builds on:** ADR 0012 (clock and processes), ADR 0050
  (`/proc/<pid>/environ` and its visibility rule), ADR 0056 (`HackProcess`
  reads `/proc`).

## Context

The plan asks the task manager for a performance tab (CPU and memory
charts), disk and network tabs as in the legacy
(`src/apps/system-monitor.ts`), and a per-process detail, all read from
`/proc`. Today `/proc` offers `meminfo`, `cpuinfo`, `uptime`, `loadavg`
and, per process, `status` (including `VmRSS` and `Cpu`), `cmdline`, `environ` and
`cwd`. What is missing:

- a cumulative CPU time counter, to plot usage per core and total usage
  over time;
- any disk measurement: neither throughput nor used space;
- any network measurement;
- per-process input/output.

The resource simulator (`IResourceSimulator`) produces load samples
(`CpuUsage`, `MemoryUsageBytes`, `StorageIoBytes`, `NetworkIoBytes`)
drawn from the declared activity of the apps. The bytes actually read and written by
the apps are counted nowhere.

## Decision

### 1. Principle: two sources

- **CPU and memory** stay simulated: they come from the simulator, like
  `status`, `meminfo` and `loadavg` today.
- **Disk and network** are **counted for real**: the file gateway
  counts the bytes that each process reads and writes, the IndexedDB persistence
  counts the operations and the bytes actually sent to storage, and the
  network gateway (ADR 0056, section 6) counts the requests and the bytes
  exchanged. A `cat` of a large file is therefore visible in the task
  manager.

The counters live in memory, start from zero at boot and are
never persisted, as on Linux.

### 2. `/proc/stat`

Linux format, times in `USER_HZ` (100 per simulated second) since
boot:

```text
cpu  4705 0 1210 93620 0 0 0 0 0 0
cpu0 2402 0 611 46790 0 0 0 0 0 0
cpu1 2303 0 599 46830 0 0 0 0 0 0
btime 1791640800
processes 312
procs_running 3
procs_blocked 0
```

- One `cpuN` line per core (`ceil(CpuCapacity)`, like `cpuinfo`), and the
  `cpu` line that sums them.
- Columns `user`, `nice`, `system`, `idle`, `iowait`, `irq`, `softirq`,
  `steal`, `guest`, `guest_nice`. The simulator does not distinguish the kernel:
  busy time goes into `user` for apps and into `system` for OS
  processes (system `AppKind.Service` and processes without an app);
  `nice`, `iowait`, `irq` and the rest stay at `0`.
- The counters **accumulate** the usage of each simulator tick: a reader
  computes the usage between two reads by difference, like `top`.
- `btime`: OS boot time in Unix seconds. `processes`: number of
  processes created since boot. `procs_running`: active processes.

### 3. `/proc/<pid>/io`

```text
rchar: 18342
wchar: 2210
syscr: 12
syscw: 3
read_bytes: 16384
write_bytes: 2048
cancelled_write_bytes: 0
```

- `rchar` / `wchar`: bytes read and written by the process, files and
  network combined. `syscr` / `syscw`: number of read and
  write calls to the gateway.
- `read_bytes` / `write_bytes`: the part that touches persistent storage
  (the IndexedDB file system), excluding `/proc`, `/dev` and network.
- `cancelled_write_bytes`: bytes of a file deleted before being read back,
  always `0` in this phase.
- Permissions `0400`, owner = the process's user, with the same visibility
  rule as `environ` (ADR 0050): the owner, or the `System`
  authority.

### 4. `/proc/diskstats`

One line per disk, Linux format (first 14 fields):

```text
   8       0 hda 1204 0 9632 410 388 0 3104 220 0 630 630
```

- A single disk, `hda`, major 8 minor 0: the OS's IndexedDB database.
- **Completed** reads and writes, 512-byte sectors read and written,
  milliseconds spent, measured around the IndexedDB transactions by the
  persistence layer.
- Virtual providers (`/proc`, `/dev`) are not disks and do not
  appear there.

### 5. `/proc/net/dev`

Linux format, two interfaces:

```text
Inter-|   Receive                                                |  Transmit
 face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed
    lo:       0       0    0    0    0     0          0         0        0       0    0    0    0     0       0          0
  eth0:  482113     214    3    0    0     0          0         0    38211     214    0    0    0     0       0          0
```

- `eth0`: the simulated network and the real fallback (ADR 0056, section 6). A
  "packet" is a request or a response; `errs` counts hosts not
  found and failures.
- `lo`: reserved, always zero in this phase.

### 6. `/proc/mounts`

`/etc/fstab` format, one line per mount of the router
(`FileSystemMountRouter`):

```text
/dev/hda / idbfs rw 0 0
proc /proc proc ro 0 0
devfs /dev devfs rw 0 0
```

The `idbfs` and `devfs` types are specific to HackerOS; `proc` is the one from
Linux.

### 7. `HackDriveInfo`

Disk space is not a file on Linux (`statfs`). The SDK exposes it
like `System.IO.DriveInfo`:

```csharp
foreach (HackDriveInfo drive in await HackDriveInfo.GetDrivesAsync())
{
    // drive.Name = "/", drive.DriveFormat = "idbfs", drive.TotalSize, drive.AvailableFreeSpace
}
```

- `GetDrivesAsync` returns the mounts of `/proc/mounts` that have a size:
  today only `/`.
- **Total size:** a new property of the virtual hardware profile,
  `VirtualHardwareProfile.StorageCapacityBytes` (8 GiB by default), bounded by
  the browser quota when it is known (`navigator.storage.estimate()`,
  called by `Infrastructure.Browser`, ADR 0055: never by the app).
- **Used space:** bytes of the blocks stored by the deduplicated
  persistence. `AvailableFreeSpace` = total size − used space, never
  negative.
- **Capability: none**, like `df` on Linux.

### 8. What the task manager reads

| Screen | Source |
|---|---|
| Processes (list, CPU, memory) | `HackProcess` → `/proc/<pid>/status` |
| Process detail | `/proc/<pid>/status`, `cmdline`, `cwd`, `environ` (if readable), `io` |
| Performance: total and per-core CPU | `/proc/stat`, by difference |
| Performance: memory | `/proc/meminfo` |
| Disk: throughput | `/proc/diskstats`, by difference |
| Disk: space | `HackDriveInfo` |
| Network: throughput per interface | `/proc/net/dev`, by difference |
| Services | `HackServiceController` (ADR 0056) |

## Consequences

- `ps`, `top` (if it is ever written) and any third-party app read the same
  data as the task manager, without a dedicated API.
- The file gateway, the persistence and the network gateway add a
  per-operation count; it remains an integer increment, with no allocation.
- Disk and network show the real activity of the apps, while the CPU
  stays simulated. This is intended: the browser gives no reliable
  per-app CPU measurement, but it knows exactly the bytes exchanged.

## Rejected alternatives

- **Disk and network drawn from the simulator** (`StorageIoBytes`,
  `NetworkIoBytes`): consistent with the CPU, but a copied file would not
  move the chart; the user would see activity unrelated to
  what they are doing.
- **A `HackPerformanceCounter` API** instead of files: simpler to
  consume, but contrary to "everything is a file" (decision D20) and to the
  plan's directive.
- **Disk space in a `/proc` file**: Linux does not do this;
  `DriveInfo` is the .NET equivalent a developer expects.

## References

- `proc(5)`, `proc_pid_io(5)`, `proc_stat(5)`; Linux kernel documentation,
  `Documentation/admin-guide/iostats.rst`.
- ADR 0012, 0046, 0050, 0055, 0056;
  [`virtual-proc-and-dev.md`](../virtual-proc-and-dev.md).
- `Platform/HackerOs.Platform.Core/FileSystem/Virtual/ProcFileSystemProvider.cs`,
  `Shared/HackerOs.Simulation.Abstractions/Processes/ResourceSimulationContracts.cs`.
