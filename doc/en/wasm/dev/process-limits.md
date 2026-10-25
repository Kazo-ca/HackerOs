> [Version française](../../../fr/wasm/dev/process-limits.md)

# Process limits (`ulimit`, `prlimit`, `limits.conf`)

Work item `exec/2-process-limits` (2026-10-18). Decisions: ADR 0125 (per-process
limits) and ADR 0139 (implementation adjustments).

This document covers the implementation of process limits: tables, controller, reading of `limits.conf`, shell commands and host setting. What an app sees and can do (defaults, effects, inheritance, `/proc/<pid>/limits`, SDK API) is in [`../framework/process-limits-api.md`](../framework/process-limits-api.md).

## Architecture

| Element | Project | Role |
|---|---|---|
| `ResourceLimitKind`, `ResourceLimit`, `ProcessResourceLimits`, `ResourceLimitChangeResult`, `ProcessLimitExceededException` | Simulation Abstractions | Contract. `ResourceLimit.Unlimited` equals `long.MaxValue`. |
| `IProcessResourceLimitTable` / `InMemoryProcessResourceLimitTable` | Abstractions / Platform.Core | Limits of each live process, next to `ProcessRecord` (ADR 0139 §1). `setrlimit` rules: lowering is always allowed, raising the soft limit up to the hard limit, raising the hard limit for elevated root only. Disabled by the host setting. |
| `IProcessFileDescriptorTable` / `InMemoryProcessFileDescriptorTable` | Abstractions / Platform.Core | Open descriptors: 0, 1, 2 reserved, lowest free number from 3, `EMFILE` at the soft limit. |
| `InMemoryProcessManager` | Platform.Core | Creates the entry at `Start` (parent, then session, then kernel), checks `NPROC` (`EAGAIN`), removes entries at exit. Exempt: `ResourceLimitExempt` (elevated root) and the system scope (`hackeros-svc`). |
| `ResourceLimitEnforcer` | Platform.Core | Every quarter of a simulated second: hard CPU limit → `SIGKILL`, soft → `SIGXCPU` (stop request from ADR 0069, reason `CpuLimitExceeded`), memory → `SIGKILL` (`MemoryLimitExceeded`). |
| `ResourceLimitsConfiguration` / `LoginResourceLimitsProvider` | Platform.Core | `pam_limits`-style reading of `limits.conf` and `limits.d/*.conf`, at the first root process of the session. Invalid lines go to `/var/log/auth.log`. |
| `AppFileSystemGateway` (`OpenDescriptorAsync`, reads) and `AppProcessGateway.SetResourceLimitAsync` | Platform.Core | Take a descriptor per read or write stream; change a limit (`process.manage` for another process). |
| `ProcFileSystemProvider` | Platform.Core | `/proc/<pid>/limits` and `/proc/<pid>/fd/`. |
| Operations `fs.fdopen`, `fs.fdclose`, `proc.rlimit` | WorkerHost | The same calls from a worker ([`worker-processes.md`](worker-processes.md)). |
| `ulimit` (built-in) | Platform.Core, `ShellInterpreter.Ulimit.cs` | Reads and changes the limits of the shell's process. |
| `prlimit` | Commands | Reads and changes the limits of a process. |
| `HackProcess.GetResourceLimitsAsync` / `SetResourceLimitAsync`, `HackResourceLimits` | AppSdk | Public API (SDK 1.2, `PublicAPI.Unshipped.txt`). |
| `AddHackerOsResourceLimits(configuration)` | Ecosystem.Host | Reads `HackerOs:ResourceLimits:Enabled` for the host. |

## Usage

### Shell

```text
alice@hackeros:~$ ulimit -a
file size                   (blocks, -f) unlimited
max memory size             (kbytes, -m) unlimited
open files                          (-n) 1024
cpu time                   (seconds, -t) unlimited
max user processes                  (-u) 4096
alice@hackeros:~$ ulimit -S -n 512      # souple seulement
alice@hackeros:~$ ulimit -Hn 600000
hsh: ulimit: open files: cannot modify limit: Operation not permitted
alice@hackeros:~$ sh -c 'ulimit -S -t 1; stress --yield'; echo "exit=$?"
CPU time limit exceeded
exit=152
```

- Without `-S` or `-H`, `ulimit` displays the soft limit and changes both.
- `ulimit` alone displays the file size: `unlimited`. `ulimit 100`
  answers "file size: cannot modify limit: Operation not permitted".
- Commands launched afterwards inherit the new limits; a `sh -c`
  keeps its own to itself.

### `prlimit`

```text
alice@hackeros:~$ prlimit --pid 7 --nofile
RESOURCE DESCRIPTION              SOFT   HARD UNITS
NOFILE   max number of open files 1024 524288 files
alice@hackeros:~$ prlimit --pid 7 --nofile=100:200 --nproc=50
```

Options: `--pid`/`-p`, `--cpu`/`-t`, `--rss`/`-m`, `--nofile`/`-n`,
`--nproc`/`-u` (with `=soft:hard`, `=soft:` or `=:hard`), `--noheadings`.
Without `--pid`, `prlimit` targets its own process.

### `/etc/security/limits.conf`

`pam_limits` format: `<domain> <type> <item> <value>`.

```text
# domaine  type  élément  valeur
@devs      soft  nofile   4096
alice      hard  cpu      60
*          -     nproc    500
```

- Domain: user, `@group` or `*`. Type: `soft`, `hard` or `-`
  (both). Applied items: `cpu` (minutes, as on Linux), `rss` (KiB),
  `nofile`, `nproc`. The other Linux items are accepted and ignored.
- The `limits.d/*.conf` files are read afterwards, in name order.
- The shipped file is entirely commented out (`system:system`, `0644`):
  it changes nothing to the defaults. A change takes effect at the next login.

### Host

```json
{ "HackerOs": { "ResourceLimits": { "Enabled": true } } }
```

`false` turns off all limits: `ulimit` no longer changes anything
("cannot modify limit"), no check takes place.

## Key decisions

- Tables next to `ProcessRecord`, which stays frozen (API 1.1 shipped).
- A single `SIGXCPU`, then the grace period from ADR 0069; the hard limit
  is checked first, so `ulimit -t 1` gives `SIGKILL` as on Linux.
- `NPROC` checked by the manager at launch; elevated root and
  `hackeros-svc` exempt.
- `limits.conf` read once per session.

Details: ADR 0139.

## Tests

- `Tests/HackerOs.Platform.Core.Tests/Processes/InMemoryProcessManagerTests.Limits.cs`
  and `ResourceLimitsConfigurationTests.cs`: tables, `setrlimit` rules,
  disabled limits, exemptions, descriptors, `pam_limits`, controller.
- `Tests/HackerOs.Platform.WorkerHost.Tests/ProcessLimitsTests.cs`: end to
  end, on the main thread and in a worker (`ulimit`, `prlimit`,
  `/proc/self/limits`, `EMFILE`, `EAGAIN`, exits 152 and 137).
- `Tests/HackerOs.AppSdk.Tests/HackResourceLimitsTests.cs`: reading of
  `/proc/<pid>/limits`.
- `CommandParityTests`: `prlimit` in a worker and on the main thread.
- `build/verify-hosts.sh`: limits step on the three hosts and
  `ecosystem-limits-off` run.

## Task list

- [x] Contract in Simulation Abstractions, limit and descriptor tables.
- [x] Inheritance at launch, session limits, `NPROC` and `EAGAIN`.
- [x] Reading of `limits.conf` and `limits.d/`, `auth.log` journal, shipped commented file.
- [x] CPU and memory controller, `SIGXCPU` (152), `SIGKILL` (137).
- [x] Descriptors for reads and write streams, `EMFILE`.
- [x] `/proc/<pid>/limits` and `/proc/<pid>/fd/`.
- [x] Built-in `ulimit`, `prlimit` command, messages in English and French.
- [x] Worker operations (`fs.fdopen`, `fs.fdclose`, `proc.rlimit`).
- [x] SDK API (`PublicAPI.Unshipped.txt`).
- [x] Setting `HackerOs:ResourceLimits:Enabled` on the three hosts.
- [x] Tests, `verify-hosts.sh`, ADR 0139, documentation and index.
