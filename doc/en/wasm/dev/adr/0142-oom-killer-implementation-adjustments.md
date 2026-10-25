# ADR 0142: adjustments to the implementation of the OOM killer

## Status

Accepted on 2026-10-18, work item `exec/3-oom-killer`.

- **Completes:** ADR 0126 (OOM killer and real-browser guard). Clarifies the
  points the code had to settle and corrects two assumptions of ADR 0126:
  the WebAssembly memory ceiling (§4) and the location of the setting in
  Settings (§8).
- **Doc:** [`oom-killer.md`](../oom-killer.md).

## Context

ADR 0126 fixes the setting (`/etc/hackeros/memory.config`), the choice of the
victim by `oom_score`, the kernel traces, `oom_score_adj` and the real-browser
guard. While writing it, several details appeared: where `oom_score_adj` lives,
who may lower it, what receives `-1000` in an OS where the desktop is not a
process, which figure to compare with the browser ceiling and how a worker
really gives its memory back.

## Decision

### 1. A table next to the process

`oom_score_adj` lives in a table held by the process manager
(`InMemoryProcessOomScoreTable`), like the limits of ADR 0139 §1: the
entry is born at `Start` and disappears when the process ends. `ProcessRecord`
does not change. `ProcessStartRequest.OomScoreAdjust` (an addition of SDK 1.2)
gives the initial value; without it, the child inherits its parent's entry,
otherwise `0`.

### 2. Floor: lowering requires an administrator

Each entry keeps a **value** and a **floor**, like Linux's
`oom_score_adj_min`. A write to `/proc/<pid>/oom_score_adj`
by the owner can raise the value or bring it back down to the floor;
going below the floor requires elevated root (`sudo`) or the system account, and
then moves the floor (like `CAP_SYS_RESOURCE`). A value declared at
launch sets the value and the floor. A child inherits both.

### 3. Default values

- A **system** app applies its declared value as is; a non-system app
  declared negative is brought back to `0` at launch (the build already refuses
  it, §6; the runtime protects itself against a manifest installed by hand).
- Without a declared value, a **system service** receives `-500`
  (`SystemServiceOomScoreAdjust`); everything else inherits.
- The desktop, the window manager and the session services are
  **not processes** in HackerOS: they are never candidates, which is
  equivalent to the `-1000` that ADR 0126 §4 gave them.

### 4. Browser ceiling: 2 GiB, and the managed heap

- The ceiling of each runtime is the maximum that the .NET runtime declares for
  its WebAssembly memory: 32,768 pages of 64 KiB, **2 GiB**, not the 4 GiB of
  wasm32 that ADR 0126 §5 assumed. `jsHeapSizeLimit` measures the JavaScript
  heap, not the WebAssembly memory: it is not used.
- The figure compared with the ceiling is the **managed heap** (`GC.GetGCMemoryInfo`, and
  `RuntimeHeapBytes` reported by each worker), not the size of
  `WebAssembly.Memory`: that memory never shrinks, so once grown it
  would look full forever.

### 5. A worker that hosted a victim is replaced

Killing a process of a worker does not give the memory back to the browser: the
worker's WebAssembly memory keeps its size. Just before the `SIGKILL`, the OOM killer
marks the victim's worker (`IWorkerMemoryReclaimer.RecycleAfterOomKill`);
when that worker no longer hosts any process, the pool removes it and starts
another (roles `pool` and `overflow`; the "ready" worker of ADR 0071 is
always new). On the main thread, the OS drops the references and forces a
collection, as ADR 0126 §5 said.

### 6. Code `HOSAPP0024`

The manifest generator refuses a non-system app whose definition
declares a negative `OomScoreAdjust`: error `HOSAPP0024` when aggregating
`BuiltInApplications.json`, where it is known which apps are system apps. The
manifest schema bounds `oomScoreAdj` to `-1000…1000`
(`manifest.oomScoreAdj.range`).

### 7. Kernel log, `/dev/kmsg`, `dmesg`, `/proc/vmstat`

- `KernelLog` is a 1,024-line buffer of the **session**: reloading the
  page is a reboot, the buffer starts empty again. Each line also goes to
  `/var/log/kern.log` and `/var/log/syslog`.
- `/dev/kmsg` (mode `0644`, format `prio,seq,usec,-;message`) exists only if
  the host provides this log. Writing to it requires root, as on Linux.
- `dmesg` reads `/dev/kmsg`; options `-T` (local time, via `/proc/uptime`),
  `-x` (level in plain text), `-l LEVEL[,LEVEL…]`, `-k` (accepted: everything is
  kernel) and `-h`.
- `/proc/vmstat` shows `nr_free_pages`, then `nr_anon_pages`, `nr_mapped`
  and `nr_slab_unreclaimable` when memory is measured, then `oom_kill`.

### 8. Top-level Memory tab in Settings

Settings has no "System" group: the page is a **Memory** tab
after Users (and not "System › Memory" of ADR 0126 §1). It shows
`MemTotal`, `MemAvailable`, the measurement source and `oom_kill`, and writes the
`memory` document in the system layer, which triggers the administrator
prompt.

### 9. Notifications

Only a killed **windowed** app leaves a notification (source
`org.hackeros.kernel`, translated app name). A killed command already shows
"Killed" and code 137 in its terminal. The real-browser guard warning
goes to the session user, once per runtime.

### 10. `stress --vm`

`stress` gains `-m N`/`--vm N`, `--vm-bytes B` (`256M` by default, suffixes
`K`, `M`, `G`) and `--vm-keep`: each worker allocates in 1 MiB blocks,
touches one byte every 4 KiB, and passes a checkpoint between two blocks
(ADR 0069). Without `--vm-keep`, it frees and starts again.

### 11. Verification

- Host setting `HackerOs:Hardware:MemoryMiB`: fixes `MemTotal` of the simulated
  hardware profile.
- `build/verify-hosts.sh` gains the `ecosystem-oom` scenario: a copy of the
  publication with `MemoryMiB=512` (`OOM_MEMORY_MIB`). The test enables the OOM
  killer with `printf … | sudo tee /etc/hackeros/memory.config`, launches
  `stress --vm 1 --vm-bytes` computed from `/proc/meminfo`, waits for exit
  137 and "Killed", then checks `dmesg` and `oom_kill 1` in `/proc/vmstat`.

## Consequences

- `ProcessExitReason.OutOfMemory`, `ProcessStartRequest.OomScoreAdjust`,
  `AppDefinition.OomScoreAdjust` and `AppManifest.OomScoreAdj` enter
  `PublicAPI.Unshipped.txt` and will be published with SDK 1.2 (ADR 0132).
- One more built-in command, `dmesg`: 74 commands and 92 apps with `nice` and `renice` from `exec/4`.
- A replaced worker costs a runtime startup; this only happens after a
  kill by the OOM killer.

## References

- ADR 0066, 0068, 0069, 0071, 0104, 0125, 0126, 0132, 0139.
- `proc(5)` (`oom_score`, `oom_score_adj`, `vmstat`), `dmesg(1)`, `stress(1)`.
