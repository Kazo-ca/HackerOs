> [Version française](../../../../fr/wasm/dev/adr/0126-oom-killer.md)

# ADR 0126: OOM killer disabled by default and guard on the real browser's memory

## Status

Accepted on 2026-10-18.

- **Plan:** [`execution-followups-plan.md`](../plans/execution-followups-plan.md),
  decisions 3 and 4 (answers Q2 "simulated, estimate if possible" and Q3
  "disabled"); implemented by `exec/3-oom-killer`, after
  `exec/2-process-limits`.
- **Replaces:** ADR 0068 §3 ("no limit, no OOM killer").
- **Complements:** ADR 0068 (memory layers), ADR 0069 (escalation),
  ADR 0125 (per-process `rss` limit).

## Context

ADR 0068 measures the memory of each process in layers (.NET heap, bytes
of open files, worker) and displays `MemTotal` of the simulated hardware profile.
Exceeding `MemTotal` only saturates the graphs. Benoit chose to keep
`MemTotal` simulated, to estimate occupancy as accurately as possible, and **not** to
enable the OOM killer by default: the default response to an app that gets in the way
is the "Application is not responding" box (ADR 0127), not a kill.

## Decision

### 1. Activation

- OS document `/etc/hackeros/memory.config` (ADR 0044), section
  `[oom-killer]`:

  ```text
  [oom-killer]
  enabled=false
  min-free-percent=5
  ```

- `enabled=true` is not enough: memory must also be measured
  (`MemSource` different from `simulated`, ADR 0066). Otherwise, the OS writes it once
  in `/var/log/syslog` ("oom-killer: disabled, memory is not measured") and
  kills nothing: nothing is killed on invented values.
- The file belongs to `system`: modifying it requires an administrator.
  Settings displays it in System › Memory, with a note about the measurement.

### 2. Trigger and choice of victim

- The OS samples every second. When `MemAvailable` falls below
  `min-free-percent` of `MemTotal` **for two consecutive samples**, it picks
  a victim.
- A process's `oom_score` = `VmRSS × 1000 / MemTotal + oom_score_adj`,
  bounded to `[0, 2000]`; `oom_score_adj = -1000` exempts. The highest score
  loses; on a tie, the most recent.
- The victim goes through `SIGKILL` (ADR 0069, step 3; step 4 in a worker),
  reason `ProcessExitReason.OutOfMemory` (SDK 1.2 addition, ADR 0132).
  No new victim is chosen until the previous one has exited.

### 3. Traces, like the kernel

- Line in `/var/log/kern.log` and `/var/log/syslog`:
  `Out of memory: Killed process 1234 (hackpaint) total-vm:…kB, anon-rss:…kB, file-rss:…kB, shmem-rss:0kB, UID:1000 oom_score_adj:0`.
- New command **`dmesg`**: the session's kernel buffer (lines
  `kern.*` since startup, timestamped in seconds since boot);
  `dmesg -T` for local time.
- New file `/proc/vmstat`, to begin with the `oom_kill` counter.
- A killed window app leaves a notification for the user:
  "Hack Paint was closed: not enough memory" (texts in English and
  French, ADR 0104).

### 4. `oom_score` and `oom_score_adj`

- `/proc/<pid>/oom_score` (read) and `/proc/<pid>/oom_score_adj` (read and
  write, `-1000` to `1000`). The owner can raise the value; lowering it
  requires an administrator, like `CAP_SYS_RESOURCE`. The value is
  inherited by children.
- A **system** app can declare a negative value in its definition
  (`AppDefinition.OomScoreAdjust`, SDK 1.2 addition); a non-system app that
  does so is rejected at build time by the manifest generator (new
  `HOSAPP` code chosen by the work item). The desktop, the window manager and
  the session services receive `-1000`, the system services `-500`.

### 5. Guard on the real browser's memory

- The OS tracks the WebAssembly memory of the main runtime and of each worker
  (size of `WebAssembly.Memory`, 4 GiB ceiling in wasm32, or
  `jsHeapSizeLimit` when the browser provides it).
- At 80% of the ceiling, it writes a warning to `kern.log` and a
  notification only once per runtime: "The page is approaching the browser's
  memory limit".
- If the OOM killer is enabled, it additionally picks a victim **among the
  processes of that runtime** (the targeted worker, or the main thread) even if
  the simulated `MemAvailable` stays high. Killing a worker's process gives
  memory back to the browser; on the main thread, the OS drops the process's
  references and requests a collection.

### 6. Estimation

Occupancy remains that of ADR 0068; when cross-origin isolation is
active (ADR 0131), a worker's layer becomes the real measurement of
`measureUserAgentSpecificMemory()`. The `MemSource` field
(ADR 0066) says which one is used.

## Consequences

- Apps that keep state (editors, CodeEditor, explorer, Settings)
  keep their recovery draft when they are killed, as with
  "Force kill".
- `build/verify-hosts.sh` gains a walkthrough with reduced simulated `MemTotal`,
  OOM killer enabled, `stress --vm` (option added by the work item), `Out of memory` line checked in `dmesg`.
- Docs: [`memory-accounting.md`](../memory-accounting.md),
  [`virtual-proc-and-dev.md`](../virtual-proc-and-dev.md),
  [`linux-commands.md`](../linux-commands.md) (`dmesg`), new
  `oom-killer.md`.

## References

- ADR 0044, 0066, 0068, 0069, 0104, 0125, 0127, 0131, 0132.
- `proc(5)` (`oom_score`, `oom_score_adj`), `dmesg(1)`.
