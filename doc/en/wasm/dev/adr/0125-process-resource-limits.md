> [Version française](../../../../fr/wasm/dev/adr/0125-process-resource-limits.md)

# ADR 0125: per-process resource limits (`setrlimit`, `ulimit`, `limits.conf`)

## Status

Accepted on 2026-10-18. A question is asked to Benoit (§6, default
values); what follows applies the recommended answer while awaiting it.

- **Plan:** [`execution-followups-plan.md`](../plans/execution-followups-plan.md),
  decision 2 (answer Q1: "best effort" on the main thread); implemented
  by `exec/2-process-limits`.
- **Replaces:** the principle "measure, do not constrain" of phase 5
  ([`execution-isolation-plan.md`](../plans/execution-isolation-plan.md) §3) for the
  limits of this ADR.
- **Complements:** ADR 0067 (CPU time), 0068 (memory), 0069 (escalation).

## Context

Phase 5 measures the CPU and memory of each process (ADR 0066 to 0068)
but limits none. A process can also open descriptors or
launch children without bound. Linux handles this through `setrlimit`: a
**soft** limit (the one that applies) and a **hard** limit (the ceiling of the
soft one), inherited at `fork`, set at login by `pam_limits` from
`/etc/security/limits.conf`.

## Decision

### 1. Limits retained

| Resource | Linux name | `ulimit` | Measure | Overrun |
|---|---|---|---|---|
| CPU time | `RLIMIT_CPU` | `-t` (seconds) | `utime + stime` (ADR 0067) | soft: `SIGXCPU`, then every second; hard: `SIGKILL` |
| Resident memory | `RLIMIT_RSS` | `-m` (KiB) | measured `VmRSS` (ADR 0068) | `SIGKILL`, reason `MemoryLimitExceeded` |
| Open descriptors | `RLIMIT_NOFILE` | `-n` | descriptor table (`/proc/<pid>/fd`) | open refused: `EMFILE`, "Too many open files" |
| User processes | `RLIMIT_NPROC` | `-u` | live processes of the UID | launch refused: `EAGAIN`, "Resource temporarily unavailable" |

- Linux has ignored `RLIMIT_RSS` since 2.6; HackerOS enforces it, because it is the
  only memory it measures. The documentation says so.
- `RLIM_INFINITY` is written `unlimited`.
- A limit based on a measurement (CPU, memory) is **inactive** when that
  measurement is turned off (`CpuSource`/`MemSource: simulated`, ADR 0066); `ulimit`
  still displays it, and `/proc/<pid>/limits` notes it (§4).
- `NOFILE` and `NPROC` are counted by the OS itself: they apply
  everywhere, on the main thread as in a worker.
- A host setting `HackerOs:ResourceLimits:Enabled` (`appsettings.json`,
  `true` by default) turns off all limits: the OS then behaves as
  today and everything is `unlimited`.

### 2. Strict enforcement in a worker, best effort on the main thread

- **In a worker**, `SIGXCPU` raises `StopRequested` with the new reason
  `ProcessExitReason.CpuLimitExceeded`; if the process does not end, the hard limit
  leads to `SIGKILL` through the escalation of ADR 0069, up to step 4 (worker
  terminated). Exceeding memory kills immediately (step 3 then 4):
  nothing allows refusing a .NET allocation.
- **On the main thread**, the OS only sees the measurement between two slices
  (ADR 0067 §4): `SIGXCPU` and `SIGKILL` are sent at the end of the slice that
  crosses the limit, and escalation stops at step 3. A loop that never yields
  is not stopped.
- Exit codes: 152 (128 + `SIGXCPU`, 24) if the process ends on
  `SIGXCPU`, 137 after `SIGKILL`. The shell writes "CPU time limit exceeded" or
  "Killed", like bash.

### 3. Sources and inheritance

- **Kernel values** (before any configuration): those of §6.
- **`/etc/security/limits.conf`** and **`/etc/security/limits.d/*.conf`**
  (alphabetical order), `pam_limits` format:
  `<domain> <type> <item> <value>`, domain `user`, `@group` or
  `*`, type `soft`, `hard` or `-`, items `cpu`, `rss`, `nofile`, `nproc`.
  Read at session opening (first process of the session); an invalid line
  is ignored and written to `/var/log/auth.log`, like `pam_limits`.
  Files `system:system 0644`; the OS ships a fully commented `limits.conf`,
  like distributions do.
- **Inheritance**: a child receives its parent's limits at launch.
- **Change**: a process lowers its limits freely, raises the soft one
  up to the hard one; only an elevated process (`sudo`, ADR 0042) raises the hard one.

### 4. Surfaces

- **`ulimit`** built into `hsh` (it acts on the shell and its future children):
  `-a`, `-S`, `-H`, `-t`, `-m`, `-n`, `-u`. Without a letter, bash targets `-f` (file
  size); HackerOS has no such limit, so `ulimit` alone displays
  `unlimited` and `ulimit <n>` answers "ulimit: file size: cannot modify limit:
  Operation not permitted".
- **`prlimit`**: `prlimit --pid <pid> [--cpu=soft:hard] [--rss=…] [--nofile=…]
  [--nproc=…]`, on a process of the same user, or on any process
  with `sudo`.
- **`/proc/<pid>/limits`**: Linux's table (`Limit`, `Soft Limit`,
  `Hard Limit`, `Units`) for the four rows, followed by a
  comment line specific to HackerOS: `# enforcement: strict (worker)`,
  `# enforcement: best-effort (main thread)` or `# cpu, rss: inactive (not measured)`.
- **SDK** (SDK 1.2 additions, ADR 0132): `HackProcess.Current.GetResourceLimits()`
  (record `HackResourceLimits`), `HackProcess.SetResourceLimitAsync(kind,
  soft, hard)`, `ProcessExitReason.CpuLimitExceeded` and `MemoryLimitExceeded`,
  `FileSystemErrorCode.TooManyOpenFiles` (`EMFILE`) translated to `HackIOException`,
  and a launch failure that carries `EAGAIN`. The exact names are set by
  the work item in `PublicAPI.Unshipped.txt`, in the spirit of this list.

### 5. Exemptions

An elevated `root` is not subject to `NPROC` (like `CAP_SYS_RESOURCE`). The system
processes of the `hackeros-svc` account (ADR 0107) receive the kernel limits,
not those of `limits.conf` (they do not go through a login).

### 6. Default values (question asked to Benoit)

Applied while awaiting his answer (recommended): values of a common Linux
distribution for the counters, unlimited for what depends on a measurement.

| Item | Soft | Hard |
|---|---|---|
| `cpu` | `unlimited` | `unlimited` |
| `rss` | `unlimited` | `unlimited` |
| `nofile` | 1024 | 524288 |
| `nproc` | 4096 | 4096 |

The other proposed choice: everything `unlimited` by default, nothing changes for an
app until an administrator writes `limits.conf`. If Benoit chooses it,
a short ADR replaces this paragraph.

## Consequences

- Limits contract in Simulation Abstractions, limits table in
  `ProcessRecord`, inheritance at launch, check in the descriptor table
  and at process launch.
- Each command treats `EMFILE` and `EAGAIN` as an ordinary I/O error
  (message, exit code 1).
- `build/verify-hosts.sh` gains the walkthrough `ulimit -t 1` then `stress`, exit
  152, in a worker.
- Docs: [`linux-commands.md`](../linux-commands.md) (`ulimit`, `prlimit`),
  [`virtual-proc-and-dev.md`](../virtual-proc-and-dev.md) (`limits`),
  [`worker-processes.md`](../worker-processes.md), new
  `process-limits.md`.

## References

- ADR 0042, 0066, 0067, 0068, 0069, 0070, 0107, 0132.
- `setrlimit(2)`, `limits.conf(5)`, `prlimit(1)`, `proc(5)`.
