# ADR 0066: optional resource measurement, at build time and at startup

## Status

Accepted on 2026-10-16.

- **Plan phase:** phase 5: app execution
  ([`execution-isolation-plan.md`](../plans/execution-isolation-plan.md)).
- **Frames:** ADR 0067 (measured CPU time) and ADR 0068 (measured memory).
- **Builds on:** ADR 0012 (resource simulator, `ResourceProfile`),
  ADR 0057 (`/proc/stat`, `ResourceSamplingService`).

## Context

Today a process's CPU and memory are **simulated**: the
`ResourceSimulator` draws each sample from the `ResourceProfile` declared by
the app (ADR 0012), and `/proc/<pid>/status` displays these values. Phase 5 adds
a **real measurement** of the execution time and allocations of each
process (ADR 0067 and 0068).

Benoit wants to be able to turn this measurement off: it adds work on each
context switch (especially for the CPU), and a use we did not
foresee could suffer from it. It must therefore be disabled **at build time** (the code
is not shipped) and **at startup** (the code is shipped but never
hooked in).

Constraint: hosts publish with `PublishTrimmed=False`
(`OS/HackerOs.Ecosystem/HackerOs.Ecosystem.csproj`). A plain .NET feature switch
(`[FeatureSwitchDefinition]`) would therefore remove no code from the binary.

## Decision

### 1. Two independent measurements

The CPU measurement (ADR 0067) and the memory measurement (ADR 0068) each have their
own switch. Turning one off does not affect the other.

### 2. At build time: a separate project, referenced conditionally

The measurement code lives in a dedicated project,
`Platform/HackerOs.Platform.ResourceAccounting`, which depends on Platform Core
and not the reverse. Platform Core only knows contracts
(`ICpuTimeAccountant`, `IMemoryAccountant`, in Simulation Abstractions) and
their null implementations.

Two MSBuild properties, read by `build/HackerOs.ResourceAccounting.targets`
imported by each host:

```xml
<HackerOsCpuAccounting>true</HackerOsCpuAccounting>       <!-- default -->
<HackerOsMemoryAccounting>true</HackerOsMemoryAccounting> <!-- default -->
```

- If both are `false`, the host does not reference the project: the measurement
  code **is not in the binary**.
- Each property also becomes a `RuntimeHostConfigurationOption`
  (`HackerOs.ResourceAccounting.Cpu.IsSupported`,
  `HackerOs.ResourceAccounting.Memory.IsSupported`), read by
  `AppContext.TryGetSwitch`. The day publishing enables trimming, the
  guard properties marked `[FeatureSwitchDefinition]` also remove the
  code of a disabled measurement while the other remains.

### 3. At startup: `appsettings.json`

The Ecosystem host receives a `wwwroot/appsettings.json` (read by
`WebAssemblyHostBuilder.Configuration`); `test/test` and `HackerOs.Server`
read their own.

```json
{
  "HackerOs": {
    "ResourceAccounting": {
      "Cpu":    { "Enabled": true, "UnresponsiveThresholdMs": 5000 },
      "Memory": { "Enabled": true }
    }
  }
}
```

- The value is read **once, at service composition**. When disabled,
  the OS registers the null implementation and **installs no hook**: no
  "enabled?" test in the hot path.
- A measurement disabled at build time always wins: `Enabled: true` is then
  ignored, with an information line in `/var/log/syslog`.
- Changing the value requires a page reload, like a kernel boot
  parameter.
- This setting is a **host** setting, not a parameter of the simulated OS: it
  does not go in `/etc/hackeros` (ADR 0044), which the OS user
  could modify.

Default value of `HackerOs.Server`: both measurements **disabled** until
ADR 0067 has been validated on a multithreaded host (section 6 of ADR
0067).

### 4. Measurement disabled: exact return to current behavior

When a measurement is disabled, its value falls back to the `ResourceSimulator`, as
today. Nothing changes for apps or for `/proc`, except an indicator:

- `/proc/<pid>/status` adds `CpuSource: simulated|measured` and
  `MemSource: simulated|measured`;
- the task manager displays a discreet "estimated" note next to the simulated
  columns.

The processes of game content, which do not run code, always
stay simulated.

## Consequences

- A host can ship a binary with no measurement at all, or disable it by
  configuration without recompiling.
- Tests cover the four combinations (CPU and memory, enabled or not)
  and verify that no hook is installed when the measurement is disabled.
- `system-metrics.md` documents the two sources and their indicator.

## References

- [`execution-isolation-plan.md`](../plans/execution-isolation-plan.md)
- ADR 0012, 0044, 0057, 0067, 0068.
