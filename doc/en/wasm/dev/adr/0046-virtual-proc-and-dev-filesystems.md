> [Version française](../../../../fr/wasm/dev/adr/0046-virtual-proc-and-dev-filesystems.md)

# ADR 0046: Virtual `/proc` and `/dev` file systems

## Status

Accepted on 2026-10-10.

- **Plan phase:** phase 1, Files ([`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), section 5.4, decision D20).
- **Replaces:** no ADR. **Complements:** ADR 0008 (adds the entry type
  `CharacterDevice`, reserved for virtual providers).

## Context

On Linux, `ps`, `top` and task managers read `/proc`, and
scripts redirect to `/dev/null`. HackerOS already has the necessary data:
`IProcessManager` (process tree), `IResourceSimulator` (CPU,
memory, I/O samples), `VirtualHardwareProfile` and `ISimulationClock`. It also already has
the mount mechanism: `FileSystemMountRouter` picks the most
specific mount and accepts additional mounts. But the OS composition
today only mounts the IndexedDB provider.

## Decision

### 1. Two providers, never persisted

| Mount | Class | `ProviderId` |
|---|---|---|
| `/proc` | `ProcFileSystemProvider` | `procfs` |
| `/dev` | `DevFileSystemProvider` | `devfs` |

- Location: `Platform/HackerOs.Platform.Core/FileSystem/Virtual/`, namespace
  `HackerOs.Platform.Core.FileSystem.Virtual`. Both implement
  `IFileSystemProvider`.
- **Computed on every read.** No call to storage, nothing is written to
  IndexedDB (D20).
- Wiring in `EcosystemServiceCollectionExtensions`, used by both
  WASM hosts:
  `new FileSystemMountRouter(indexedDbProvider, [new FileSystemMount(VirtualPath.Parse("/proc"), procfs), new FileSystemMount(VirtualPath.Parse("/dev"), devfs)])`.
  The mounts are protected (`isProtectedRoot: true`).
- **Visible mount points:** the router does not merge mounts into the
  parent's listing. `FileSystemSeeder` therefore creates the empty folders `/proc`
  (`system:system 0555`) and `/dev` (`system:system 0755`) in the root
  provider, like real mount points. `ls /` shows them, and the router then
  masks them with the virtual provider.
- **Entry identity:** a stable `FileSystemEntryId`, taken from the first 16
  bytes of the SHA-256 of `"<providerId>:<path>"`. For `/proc/<pid>`, the PID
  is never reused during a boot (ADR 0012), so the identity stays
  stable.
- **Revision:** `ISimulationClock.CurrentTick` at the time of reading. Timestamps
  are the process start time for `/proc/<pid>`, the OS start
  time otherwise, and the current time for file contents.
- **Size:** `0` for all virtual files, as on Linux. Content
  is produced as a stream: a reader reads to the end of the stream, without relying on the
  size.
- **Watching:** no `FileSystemChangeEvent` event is emitted for
  these mounts. `inotify` does not work on `/proc` on Linux either.

### 2. Operations not allowed

Entries are exposed with read-only mode bits. Any
creation, deletion, move, copy to the mount or permission change
answers `PermissionDenied`, like Linux's `EACCES` denial. A
write to a file in `/proc` also answers `PermissionDenied`. `CopyAsync`
**from** `/proc` to another mount is handled by the ADR 0043 facade,
which copies the stream.

Authorization follows the ordinary `FileSystemService` path: capability depending on
the path (`/proc` and `/dev` being outside the home folder, this requires
`filesystem.system.read`, or `filesystem.system.write` to write to `/dev`),
authority, then the provider's mode bits.

### 3. Contents of `/proc`

**Process visibility:** `/proc` lists only active processes (not
`Stopped`). The caller always sees its own process. It sees the others
if it has the `process.list` capability or `System` authority, like
`IAppProcessGateway.ListProcesses`. An invisible PID answers `NotFound`.

| Path | Type and mode | Content |
|---|---|---|
| `/proc/<pid>/` | folder `0555`, owner = the process's user | n/a |
| `/proc/<pid>/status` | file `0444` | see below |
| `/proc/<pid>/cmdline` | file `0444` | `appId`, then the arguments, separated and terminated by `\0`. **Phase 1: `appId` only**, since `ProcessRecord` does not have the arguments yet (phase 3). |
| `/proc/<pid>/environ` | file `0400` | `KEY=value\0...`. **Phase 1: empty.** Filled by the per-process environment of phase 3 (D13). |
| `/proc/<pid>/cwd` | symbolic link `0777` | current directory. **Phase 1: `/home/<user>`.** Phase 3: the process's current directory. |
| `/proc/self` | symbolic link `0777` | `/proc/<caller's pid>`. Absent if the caller has no process. |
| `/proc/meminfo` | file `0444` | `MemTotal`, `MemFree`, `MemAvailable` in kB |
| `/proc/cpuinfo` | file `0444` | one block per virtual core |
| `/proc/uptime` | file `0444` | `<seconds since boot> <idle seconds>` |
| `/proc/loadavg` | file `0444` | `<1 min> <5 min> <15 min> <active>/<total> <last pid>` |

Format of `/proc/<pid>/status`, `Key:\tvalue` pairs in this order:

```text
Name:	<app short name, truncated to 15 characters as on Linux>
State:	<letter> (<word>)
Pid:	<pid>
PPid:	<parent pid, or 0>
Uid:	<UserId>
AppId:	<appId>
Kind:	<window|terminal|service>
Session:	<SessionId>
VmRSS:	<memory of the last sample, in kB> kB
Cpu:	<CpuUsage of the last sample, in %>
```

Mapping `ProcessState` → `State`: `Created` → `I (idle)`, `Starting` →
`S (sleeping)`, `Running` → `R (running)`, `Stopping` → `T (stopping)`, `Faulted`
→ `Z (zombie)`.

Sources of the figures:

- per-process memory and CPU: last `ProcessResourceSample` from
  `IResourceSimulator.GetHistory(pid)`;
- `MemTotal`: `VirtualHardwareProfile.MemoryCapacityBytes`;
- `MemFree`: `MemTotal` minus the `TotalMemoryUsageBytes` of the last
  `SystemResourceSample`;
- number of `cpuinfo` cores: `ceil(VirtualHardwareProfile.CpuCapacity)`;
- `uptime`: `CurrentTick × TickDuration` of `ISimulationClock`;
- `loadavg`: sliding averages of `TotalCpuUsage` over the bounded system
  history.

To know the caller (`/proc/self`), a property
`long? ProcessId { get; init; }` is added to `AppOperationContext`
(`Shared/HackerOs.App.Abstractions/AppOperationContext.cs`). It is the raw value, because
`App.Abstractions` does not reference the `ProcessId` type.
`AppExecutionContextFactory` fills it in.

### 4. Contents of `/dev`

New member `FileSystemEntryKind.CharacterDevice` (complements ADR 0008).
`ls -l` displays it with type `c`. The IndexedDB provider refuses to
persist this type (`UnsupportedOperation`). The `switch` statements on `FileSystemEntryKind`
in the platform and the base apps must be reviewed for this new case.

| Path | Mode | Read | Write |
|---|---|---|---|
| `/dev/null` | `0666` | immediate end of stream | accepted and ignored |
| `/dev/zero` | `0666` | infinite stream of `0x00` bytes | accepted and ignored |
| `/dev/random` | `0666` | infinite stream of random bytes | accepted and ignored |
| `/dev/urandom` | `0666` | same as `/dev/random`, as on modern Linux | accepted and ignored |

- Randomness comes from `System.Security.Cryptography.RandomNumberGenerator`, not
  from `ISimulationRandom`: ADR 0012 reserves the latter for deterministic
  simulation and forbids it for any cryptographic use.
- **Infinite streams:** they read in 4 KiB blocks and honor cancellation.
  `HackFile.ReadAll*` (ADR 0043) throws `HackIOException` on a
  `CharacterDevice` entry, instead of freezing the browser's only thread. One must
  go through `HackFile.OpenReadAsync`, as `head -c 16 /dev/urandom` would.
- A write to a device succeeds without changing the revision or emitting
  an event.

### 5. Consumers

- `ps` and the task manager will read `/proc`. The migration of `ps` happens
  in phase 3 with the commands, and that of the task manager in phase 4
  (plan, section 8.2). They keep `IAppProcessGateway` for actions (kill).
- The `> /dev/null` redirection arrives with the shell grammar of phase 3.

### 6. Contract tests

In `Tests/HackerOs.Platform.Core.Tests/FileSystem/Virtual/`:

- listing of `/` containing `proc` and `dev`;
- exact format of `status`, `meminfo`, `uptime` and `loadavg` with a deterministic
  clock and simulator;
- PID visibility depending on `process.list`;
- resolution of `/proc/self`;
- refusal of mutations (`PermissionDenied`);
- empty read of `/dev/null` and ignored write;
- bounded stream read of `/dev/zero`;
- absence of any write to the simulated IndexedDB repository.

## Consequences

- `cat /proc/meminfo`, `ls /proc` and `echo x > /dev/null` (phase 3)
  behave as on Linux.
- The contents of `/proc` are always up to date, with no storage cost. In
  exchange, each read recomputes the content; a task manager
  that polls often must keep a reasonable pace (1 s).
- The new `CharacterDevice` type affects all exhaustive `switch` statements on
  `FileSystemEntryKind` (explorer, `ls`, `FileView`).

## References

- ADR 0008: virtual file system model (complemented).
- ADR 0012: processes, clock, resources and randomness.
- ADR 0043: `Hack*` API.
- [`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), sections 5.4, 7.2 and 8.2.
- `Platform/HackerOs.Platform.Core/FileSystem/FileSystemMountRouter.cs`, `Platform/HackerOs.Platform.Core/Processes/`.
