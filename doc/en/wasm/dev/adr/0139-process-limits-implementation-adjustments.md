> [Version française](../../../../fr/wasm/dev/adr/0139-process-limits-implementation-adjustments.md)

# ADR 0139: implementation adjustments for process limits

## Status

Accepted on 2026-10-18, work item `exec/2-process-limits`.

- **Complements:** ADR 0125 (per-process limits). Replaces none of its
  decisions. It specifies the points the code had to settle and fixes the
  verification walkthrough of ADR 0125, "Consequences" section.
- **Doc:** [`process-limits.md`](../process-limits.md).

## Context

ADR 0125 sets the four limits (`cpu`, `rss`, `nofile`, `nproc`), their
sources (`limits.conf`, inheritance), their surfaces (`ulimit`, `prlimit`,
`/proc/<pid>/limits`, SDK) and their enforcement (strict in a worker, best effort on
the main thread). While writing it, several details appeared: where the limits live,
when `SIGXCPU` is sent, what a descriptor is in an OS with no
`open(2)` call, and the exact SDK names.

## Decision

### 1. Tables next to the process, not in `ProcessRecord`

The limits and descriptors live in two tables held by the process
manager, like the environment table of ADR 0050:
`IProcessResourceLimitTable` and `IProcessFileDescriptorTable` (Simulation
Abstractions). The entry is born at `Start` (parent's limits, otherwise the
session's, otherwise the kernel's) and disappears when the process ends.
`ProcessRecord` does not change: it is a frozen snapshot and its fields are part
of the released 1.1 API.

### 2. `SIGXCPU` once, then the grace period

A controller (`ResourceLimitEnforcer`) compares the measurement to the limits every
quarter of a simulated second. The order is Linux's:

1. **hard** CPU limit reached: `SIGKILL`, reason `CpuLimitExceeded`,
   exit 137, "Killed";
2. **soft** limit reached: `SIGXCPU` only once, in the form of the stop
   request of ADR 0069 (reason `CpuLimitExceeded`, 5 s delay). If
   the process ends on this request, its exit is 152 and the shell writes
   "CPU time limit exceeded". Otherwise the grace period leads to `SIGKILL`;
3. memory beyond the limit: `SIGKILL` right away, reason
   `MemoryLimitExceeded`.

ADR 0125 spoke of a `SIGXCPU` "then every second". A stop request
already in progress is not sent again (ADR 0069), so there is only one.

Consequence for verification: with `ulimit -t 1`, the soft and hard limits
are 1, and the hard one goes first. The process therefore receives `SIGKILL` (137),
as under Linux. The `build/verify-hosts.sh` walkthrough uses
`sh -c 'ulimit -S -t 1; stress --yield'`: only the soft limit is set, and
`stress --yield` stops on the request, which gives exit 152. A
`stress` without `--yield` never yields. It therefore ends with `SIGKILL`
after the grace period: the worker is terminated (step 4 of ADR 0069).

### 3. What counts as a descriptor

HackerOS has no `open(2)` call. A descriptor is taken:

- by each read (`IAppFileSystemGateway.ReadAsync`), until its
  stream is released;
- by each write stream (`HackFileStream` for writing), by means of
  `IAppFileSystemGateway.OpenDescriptorAsync`, until it is closed.

Numbers 0, 1 and 2 are the standard streams (`/dev/tty` for a terminal,
`/dev/null` otherwise). A new descriptor takes the smallest free number from
3 on. It is refused (`EMFILE`) when its number reaches the soft
limit: with `ulimit -n 3`, no file opens, as under Linux.
`/proc/<pid>/fd/` lists the open descriptors.

Not counted: single-block writes (`WriteAllText` and the rest of
`WriteAsync`, which have no open stream), folder listings and
sockets. In a worker, a file of at most 16 MiB is read in a single
response (ADR 0070). Its descriptor is therefore closed as soon as this response,
even if the app keeps the stream longer.

### 4. `NPROC` checked at launch, by the manager

`InMemoryProcessManager.Start` counts the user's live processes.
It throws `ProcessLimitExceededException` (`EAGAIN`) when this count reaches
the caller's soft limit. Exempt are:

- a launch marked `ResourceLimitExempt` (elevated root);
- a system-scope launch (`hackeros-svc`).

The orchestrator translates this exception into a launch failure with code
`lifecycle.process.eagain`. The shell then writes
"hsh: fork: Resource temporarily unavailable" and exits with 1. A script
(`sh script`, `sh -c`) gives the same message.

### 5. `pam_limits` at the session's first root process

The files are read at the first launch of a process with no parent in the
session, then kept in memory until the next session. A change to
`limits.conf` therefore applies to the next login. Each invalid line is
written once to `/var/log/auth.log` (actor `pam_limits`). The Linux items
that HackerOS does not enforce (`core`, `stack`, `as`…) are accepted without a
message.

### 6. SDK names (SDK 1.2 additions, `PublicAPI.Unshipped.txt`)

- `HackProcess.GetResourceLimitsAsync()` (asynchronous instance method:
  it reads `/proc/<pid>/limits`) and
  `HackProcess.SetResourceLimitAsync(ResourceLimitKind, long soft, long hard)`.
  The errors are `ArgumentException` (`EINVAL`),
  `HackUnauthorizedAccessException` (`EPERM`) and `InvalidOperationException`
  (`ESRCH`).
- `HackResourceLimits` (with `Parse`) and `HackResourceLimitEnforcement`
  (`Off`, `BestEffort`, `Strict`).
- Simulation Abstractions:
  - `ResourceLimitKind`, `ResourceLimit`, `ProcessResourceLimits`,
    `ResourceLimitChangeResult`, `ProcessLimitExceededException`;
  - the two tables of §1;
  - `ProcessStartRequest.ResourceLimits` and `ResourceLimitExempt`;
  - `IAppFileSystemGateway.OpenDescriptorAsync` and
    `IAppProcessGateway.SetResourceLimitAsync`, with a default
    implementation so that existing gateways compile;
  - `NoDescriptor`;
  - `ProcessExitReason.CpuLimitExceeded` and `MemoryLimitExceeded`;
  - `FileSystemErrorCode.TooManyOpenFiles` (1802).

### 7. Surfaces

- `ulimit` also accepts `-f`. `ulimit unlimited` (or `-f unlimited`) succeeds
  without changing anything. A number answers "cannot modify limit: Operation not
  permitted", as ADR 0125 says.
- `prlimit` without `--pid` targets its own process, as in util-linux. It
  requires `process.list` and `filesystem.system.read` to read `/proc`, like
  `ps`, and `process.manage` to change another process.
- `/proc/<pid>/limits` gains the line
  `# enforcement: off (HackerOs:ResourceLimits:Enabled=false)` when the host has
  turned the limits off.
- Each host that composes the OS (the Ecosystem and `test/test`; `HackerOs.Server`
  serves the Ecosystem since ADR 0130) reads the setting through
  `AddHackerOsResourceLimits(configuration)`, before `AddHackerOsEcosystem`.

## Consequences

- `build/verify-hosts.sh` gains a step and a pass. The step reads
  `/proc/self/limits` and runs the walkthrough of §2 on each host: 152 where the
  CPU is measured, normal exit where it is not. The
  `ecosystem-limits-off` pass publishes with the limits turned off.
- `cat`, `grep` and `wc` say "Too many open files" instead of "No such
  file or directory" when opening fails with `EMFILE`, through the same translator
  (`CommandIO.ReadErrno`) as the other read commands.

## References

- ADR 0050, 0066, 0067, 0068, 0069, 0070, 0107, 0125, 0132.
