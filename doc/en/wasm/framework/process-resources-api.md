# Process resources for apps (CPU, memory, OOM killer)

This document describes what an app sees and can do with the CPU time, memory and OOM killer of a process: `HackProcess`, `/proc/<pid>/stat`, `/proc/<pid>/status`, `/proc/meminfo` and `/proc/<pid>/oom_score_adj`. The implementation is in [`../dev/cpu-accounting.md`](../dev/cpu-accounting.md), [`../dev/memory-accounting.md`](../dev/memory-accounting.md) and [`../dev/oom-killer.md`](../dev/oom-killer.md).

## What measurement changes for an app

No app changes a single line: everything goes through the ambient frame that the OS already opens
around their code (ADR 0043).

## CPU time

### Reading

```bash
cat /proc/42/stat
# 42 (code-editor) R 1 42 42 0 -1 0 0 0 0 0 153 27 0 0 20 0 1 0 1234 4194304 1024 18446744073709551615 0 0 ... 0
grep -E 'Cpu|Responding|LongTasks|ctxt' /proc/42/status
cat /proc/stat
```

```csharp
HackProcess editor = await HackProcess.GetProcessByIdAsync(42);
TimeSpan user = editor.UserProcessorTime;          // utime
TimeSpan system = editor.PrivilegedProcessorTime;  // stime
TimeSpan total = editor.TotalProcessorTime;
bool responding = editor.Responding;               // false : « Not responding »
```

| File | Measurement on | Measurement off |
|---|---|---|
| `/proc/<pid>/stat` | measured `utime`, `stime` (fields 14 and 15, `USER_HZ`) | `utime` rebuilt from the simulator's bounded history (one sample per second), `stime` at `0` |
| `/proc/<pid>/status` | `Cpu` = usage over the last second, `CpuSource: measured`, then `Responding`, `LongTasks`, `voluntary_ctxt_switches`, `nonvoluntary_ctxt_switches` | exactly the file as before: simulated `Cpu`, `CpuSource: simulated`, without the four fields |
| `/proc/stat` | `cpu0` = the main thread (`user`, `system` = `stime` + compositor, `idle`); core `k` adds the busy time of the pool worker that carries it (ADR 0070, [`worker-processes.md`](../dev/worker-processes.md)); without a worker, the other cores stay `idle` | simulated time (ADR 0057) |

A process that has not run anything yet (or a simulated process,
which runs no code) is not measured: it keeps its simulated values.

## Behavior under load

- **Warning (ADR 0127 §4)**: a windowed app runs on the main thread.
  A long loop without `await` freezes the whole desktop there, mouse included, and
  no dialog can be displayed during the freeze. Heavy computation goes into
  `HackBackground.RunAsync` (ADR 0128); a long loop that must stay on the
  thread calls `CheckpointAsync` (ADR 0069) often enough to yield control.

An unresponsive window is described in [`app-responsiveness-api.md`](app-responsiveness-api.md).

## Kernel sub-frame

- **Kernel sub-frame reserved for the OS.** `EnterKernel` is internal to the SDK and
  visible to Platform Core only: an app cannot declare its own
  code as system time.

## Memory

### Measured or simulated

A process is measured if the host's catalog knows its image (all
built-in apps) or if it has run code under its frame (the shell, an
app installed at runtime). The others (simulated processes without code) remain
simulated, as ADR 0066 §4 requires.
### Reading

```text
$ cat /proc/42/status
...
VmRSS:	4712 kB
RssAnon:	3000 kB
RssFile:	1200 kB
VmData:	512 kB
VmExe:	40 kB
HackAllocBytes:	123456789
Cpu:	0.0
CpuSource:	simulated
MemSource:	measured
```

```text
$ cat /proc/meminfo
MemTotal:        4194304 kB
MemFree:         4063232 kB
MemAvailable:    4063232 kB
AnonPages:        102400 kB
Mapped:            20480 kB
Slab:               4096 kB
```

- `MemTotal` stays that of the `VirtualHardwareProfile`.
- Used memory = `AnonPages + Mapped + Slab` + simulated memory of the
  processes that remained simulated.
- `HackProcess.WorkingSet64` reads `VmRSS`; `HackProcess.PrivateMemorySize64`
  reads `RssAnon` (and equals `WorkingSet64` when memory is simulated), like
  `System.Diagnostics.Process`.

## Memory and OOM killer

The OOM killer is disabled by default and only acts if memory is measured (`MemSource` other than `simulated`). An app killed by it ends with the reason `ProcessExitReason.OutOfMemory` (SDK 1.2); a windowed app sees its window close and the user receives a notification (ADR 0142 §9).

### Files

| Path | Access | Content |
| --- | --- | --- |
| `/proc/<pid>/oom_score` | read | Current score. |
| `/proc/<pid>/oom_score_adj` | read, write | `-1000` to `1000`. The owner raises the value; lowering it below the floor requires `sudo`. |
| `/proc/vmstat` | read | `nr_free_pages`, then `nr_anon_pages`, `nr_mapped`, `nr_slab_unreclaimable` if measured, then `oom_kill`. |
| `/dev/kmsg` | read; root write | `prio,seq,usec,-;message`. |
| `/var/log/kern.log` | read | Kernel lines (rotation 1 MiB × 5, ADR 0045). |
### Declaring a value in an app

```csharp
public sealed class MyServiceDefinition : AppDefinition
{
    // Seule une app système peut déclarer une valeur négative (HOSAPP0024).
    public override int? OomScoreAdjust => -200;
}
```

The generated manifest carries `"oomScoreAdj": -200`. A system service without
a value receives `-500`; other processes inherit from their parent.
