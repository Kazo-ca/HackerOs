> [Version française](../../../fr/wasm/framework/process-limits-api.md)

# Process limits for apps

This document describes what an app sees and can do with the limits of its process (`HackProcess`, `/proc/<pid>/limits`). The implementation (tables, controller, reading of `limits.conf`, shell commands) is in [`../dev/process-limits.md`](../dev/process-limits.md).

## Role

Each process carries four `setrlimit(2)`-style limits, with a soft value
(the one that applies) and a hard value (its ceiling):

| Limit | `ulimit` | `/proc/<pid>/limits` | Default (soft / hard) | Effect when reached |
|---|---|---|---|---|
| `cpu` | `-t` (seconds) | `Max cpu time` | unlimited / unlimited | soft: `SIGXCPU` (exit 152, "CPU time limit exceeded"); hard: `SIGKILL` (137, "Killed") |
| `rss` | `-m` (KiB) | `Max resident set` | unlimited / unlimited | `SIGKILL` (137) |
| `nofile` | `-n` | `Max open files` | 1024 / 524288 | open refused: `EMFILE`, "Too many open files" |
| `nproc` | `-u` | `Max processes` | 4096 / 4096 | launch refused: `EAGAIN`, "hsh: fork: Resource temporarily unavailable" |

## Inheritance and enforcement

These are the Linux kernel defaults. A process inherits the limits of its
parent; a process with no parent takes those of the session, read from
`/etc/security/limits.conf` at login.

Enforcement is **strict in a Web Worker** (the OS terminates the worker) and
**best-effort on the main thread** (checked between two slices of work).
The `cpu` and `rss` limits only apply where CPU and memory are
measured ([`resource-accounting.md`](../dev/resource-accounting.md)); otherwise
`/proc/<pid>/limits` reports them as inactive.

## Descriptors

- One descriptor = one open read or write stream; single-block writes,
  folder listings and sockets do not count.

## `/proc/<pid>/limits` file

```text
Limit                     Soft Limit           Hard Limit           Units     
Max cpu time              unlimited            unlimited            seconds   
…
Max open files            1024                 524288               files     
# enforcement: strict (worker)
# cpu, rss: inactive (not measured)
```

The `# enforcement:` line is `strict (worker)`, `best-effort (main thread)`
or `off (HackerOs:ResourceLimits:Enabled=false)`. `/proc/<pid>/fd/` (folder
`0500`) lists the open descriptors as links.

## SDK API

```csharp
HackProcess self = await HackProcess.GetCurrentProcessAsync();
HackResourceLimits limits = await self.GetResourceLimitsAsync();
await self.SetResourceLimitAsync(ResourceLimitKind.NoFile, 256, limits.NoFile.Hard);
```

Errors: `ArgumentException` (`EINVAL`), `HackUnauthorizedAccessException`
(`EPERM`), `InvalidOperationException` (`ESRCH`).
