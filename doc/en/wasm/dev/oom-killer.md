> [Version française](../../../fr/wasm/dev/oom-killer.md)

# OOM killer and browser memory guard

This document covers the implementation of the OOM killer and the browser guard. What an app sees (`/proc` files and `/dev/kmsg`, `AppDefinition.OomScoreAdjust`, the `OutOfMemory` termination reason) is in [`../framework/process-resources-api.md`](../framework/process-resources-api.md).

Work item `exec/3-oom-killer`. Decisions: [ADR 0126](adr/0126-oom-killer.md)
and its adjustments [ADR 0142](adr/0142-oom-killer-implementation-adjustments.md).

## Role

When the available memory of the simulated system becomes too low, the OS picks
a process by its `oom_score`, kills it (`SIGKILL`, exit 137, "Killed") and
writes it to the kernel log, like the Linux `oom_killer`. It also monitors
the real memory of the page (.NET runtime of the main thread and of each
Web Worker) and warns before the browser crashes the tab.

The OOM killer is **disabled by default** and acts only if memory is
**measured** (`MemSource` other than `simulated`, ADR 0066): nothing is killed
on invented values. The browser guard always warns; it kills
only if the OOM killer is enabled.

## Architecture

| Component | Project | Role |
| --- | --- | --- |
| `OomKiller` | Platform.Core | Samples every simulated second; two consecutive samples below the threshold trigger the choice of a victim. Browser guard at 80% of the ceiling. `KillCount` counter. |
| `OomKillerSettings`, `MemorySettingsDocuments` | Platform.Core | Document `/etc/hackeros/memory.config`, section `[oom-killer]`. |
| `SystemMemoryProbe` | Platform.Core | `MemTotal` (simulated profile), measured `MemAvailable` and `VmRSS` (ADR 0068), `oom_score`. |
| `InMemoryProcessOomScoreTable` | Platform.Core | `oom_score_adj` of each process, with its floor (ADR 0142 §1-2). |
| `KernelLog` | Platform.Core | Kernel buffer of the session (1,024 lines), copied to `/var/log/kern.log` and `syslog`; read by `/dev/kmsg`. |
| `BrowserRuntimeMemorySource` | Platform.Core | Managed heap of each runtime against the 2 GiB ceiling (ADR 0142 §4). |
| `IWorkerMemoryReclaimer` (`WorkerPool`) | WorkerHost | Replaces the worker that hosted a victim as soon as it is empty (ADR 0142 §5). |
| `NotificationQueueOomNotifier` | Platform.Core | Translated notifications: app with a closed window, browser limit close. |
| `MemoryPanel` | Apps.Settings | **Memory** tab of Settings. |
| `dmesg`, `stress --vm` | Commands | Reading the kernel log; test memory load. |

`EcosystemServiceCollectionExtensions` registers the table, the log and
the OOM killer; `EcosystemBootCoordinator` attaches it to the settings and starts it
once the catalog and the accounts are ready.

### Choosing the victim

- `oom_score = VmRSS × 1000 / MemTotal + oom_score_adj`, bounded to `[0, 2000]`.
- `oom_score_adj = -1000` exempts. The highest score loses; on a tie, the highest
  PID.
- No new victim as long as the previous one has not exited.
- Browser guard: the victim is chosen only among the processes of the
  targeted runtime (`CONSTRAINT_MEMCG`, `oom_memcg=/hackeros/<runtime>`).

### Traces

```text
[  812.402113] oom-kill:constraint=CONSTRAINT_NONE,nodemask=(null),cpuset=/,mems_allowed=0,global_oom,task_memcg=/,task=stress,pid=42,uid=1000
[  812.402150] Out of memory: Killed process 42 (stress) total-vm:503808kB, anon-rss:503808kB, file-rss:0kB, shmem-rss:0kB, UID:1000 oom_score_adj:0
```

## Usage

### Enabling

```sh
sudo nano /etc/hackeros/memory.config
```

```ini
[oom-killer]
enabled=true
min-free-percent=5
```

Or Settings › Memory (requires administrator rights). `min-free-percent` ranges from
`0` to `50`; an out-of-range value is clamped and reported in `syslog`.

The files `/proc/<pid>/oom_score`, `oom_score_adj`, `/proc/vmstat` and `/dev/kmsg`, as well as the declaration of `OomScoreAdjust` in an app, are described in [`../framework/process-resources-api.md`](../framework/process-resources-api.md).

### Commands

- `dmesg [-T] [-x] [-l NIVEAU[,NIVEAU…]] [-k] [-h]`.
- `stress --vm N [--vm-bytes B] [--vm-keep]` (`B`: `256M` by default,
  suffixes `K`, `M`, `G`).
- `echo 500 > /proc/<pid>/oom_score_adj`.

### Host setting

`HackerOs:Hardware:MemoryMiB` (appsettings) sets `MemTotal` of the simulated profile.
`build/verify-hosts.sh` uses it in the `ecosystem-oom` scenario
(`OOM_MEMORY_MIB`, 512 by default).

## Decisions

- Disabled by default, active only with a measurement (Benoit's answer to Q3).
- Browser ceiling at 2 GiB, compared with the managed heap (ADR 0142 §4).
- Worker of a victim replaced to really give the memory back (ADR 0142 §5).
- The desktop and the window manager are not processes, so they are
  never candidates (ADR 0142 §3).
- Notification only for an app with a window (ADR 0142 §9).
- The kernel log is that of the session: reloading the page is a
  reboot.

## Task list

- [x] `/etc/hackeros/memory.config` setting, migration and schema.
- [x] Measurement of `MemAvailable`, `oom_score`, victim choice, `OutOfMemory` kill.
- [x] `oom_score_adj` in a table, floor, inheritance, writing in `/proc`.
- [x] Kernel log, `/dev/kmsg`, `kern.log`, `dmesg`.
- [x] `/proc/vmstat` with `oom_kill`.
- [x] Notification for a killed app with a window (English, French).
- [x] Guard of the real browser, worker replacement.
- [x] `AppDefinition.OomScoreAdjust`, `oomScoreAdj` of the manifest, `HOSAPP0024`, `-500` for system services.
- [x] Memory tab of Settings.
- [x] `stress --vm`.
- [x] `ecosystem-oom` scenario of `build/verify-hosts.sh`.
