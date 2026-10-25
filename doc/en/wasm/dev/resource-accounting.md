# Optional resource measurement: switches and context-switch hook

Implementation of work item `rebuild/5-accounting-switches` of phase 5
([`execution-isolation-plan.md`](plans/execution-isolation-plan.md)):
[ADR 0066](adr/0066-optional-resource-accounting.md), and the common hook of
[ADR 0067](adr/0067-measured-cpu-time.md) §1 that work items
`5-cpu-accounting` and `5-memory-accounting` will use.

## Role

- Choose **at build** whether the CPU and memory measurement code is shipped,
  and **at startup** (`appsettings.json`) whether it is wired in.
- Install, when at least one measurement is active, the **context-switch
  hook**: the callback of the ambient `AsyncLocal` of
  `HackExecutionContext`, which sees each process enter and leave the
  thread, including in async continuations.
- Turned off (or not yet implemented), return **exactly** to the current
  simulator, with an indicator: `CpuSource:` / `MemSource:` in
  `/proc/<pid>/status`, "estimated" label in the task manager.

Both measurements are done. CPU through `MeasuredCpuTimeAccountant` (work item
`5-cpu-accounting`, [`cpu-accounting.md`](cpu-accounting.md)), memory
since work item `5-memory-accounting`
([`memory-accounting.md`](memory-accounting.md)): its `AllocationTracker` is
an observer of the hook, and `MeasuredMemoryAccountant` replaces the null
accountant.

## Architecture

| Element | Project | Role |
|---|---|---|
| `ICpuTimeAccountant`, `IMemoryAccountant`, `ResourceMeasurementSource` | `Shared/HackerOs.Simulation.Abstractions` (`Processes/ResourceAccountingContracts.cs`) | contracts read by `/proc`; `TryGet…` false = simulated value |
| `IExecutionContextSwitchObserver`, `ExecutionContextSwitch` | same | hook observer contract: process left, process entered, change made by the runtime (`ThreadContextChanged`), and since ADR 0067 the mode of each (`FromKernel`, `ToKernel`) |
| `IExecutionModeSwitchObserver` | same | mode changes (entering or leaving an OS gateway) without a process change; optional (ADR 0067, [`cpu-accounting.md`](cpu-accounting.md)) |
| `NullCpuTimeAccountant`, `NullMemoryAccountant` | `Platform/HackerOs.Platform.Core` (`Processes/Accounting/`) | null implementations; Platform Core never depends on the measurement project |
| `ResourceAccountingPlan`, `ResourceAccountingSettings`, `ResourceAccountingBuildSupport` | same | decision made once: build × setting → active measurements, required hook, lines for syslog |
| `HackExecutionContext` (`InstallContextSwitchHook`, internal) | `Shared/HackerOs.AppSdk` | replaces the ambient frame with an `AsyncLocal<T>(valueChangedHandler)`; visible only to the measurement project (`InternalsVisibleTo`) |
| `ContextSwitchHook`, `ContextSwitchDispatcher`, `ResourceAccountingFeatures` | `Platform/HackerOs.Platform.ResourceAccounting` (new) | hook installation, broadcast to the measurements (process changes, and mode changes to observers that want them), `[FeatureSwitchDefinition]` switches |
| `MeasuredCpuTimeAccountant` | same | CPU measurement (ADR 0067), registered on the broadcaster when `plan.CpuActive` |
| `ResourceAccountingServiceCollectionExtensions` | `OS/HackerOs.Ecosystem.Host` | reading of `HackerOs:ResourceAccounting`, composition, `#if HACKEROS_RESOURCE_ACCOUNTING` |
| `build/HackerOs.ResourceAccounting.targets` | `build/` | MSBuild properties, `RuntimeHostConfigurationOption`, conditional reference |

### The hook

```
HackExecutionContext.Enter(P) ──┐
continuation restaurée (await) ─┼─► AsyncLocal<frame>(OnFrameChanged)
fin de continuation, minuterie ─┘         │ processus différent ?
                                          ▼
                          ContextSwitchDispatcher.OnContextSwitch(in switch)
                                  │                 │
                         mesure CPU (0067)   mesure mémoire (0068)
```

- Without an active measurement, the frame stays an ordinary `AsyncLocal`: no callback,
  no "enabled?" test in the hot path.
- The callback only signals real process changes (not a nested frame
  of the same process, not OS code resuming OS code). It never
  throws: an exception in the restoration of an
  `ExecutionContext` would stop the runtime; an observer that throws is
  ignored for that change.
- Replacing the `AsyncLocal` would make flows that captured the old one lose their context:
  installation is therefore refused if a scope is open
  (live scope counter) or if a hook already exists. The OS installs it
  at service composition, before any app code, and never removes it.
- The broadcaster copies its array of observers at registration: reading
  in the hot path is done without a lock or allocation.

## Usage

### At build

```xml
<!-- Directory.Build.props, ou en ligne de commande : -p:HackerOsCpuAccounting=false -->
<HackerOsCpuAccounting>false</HackerOsCpuAccounting>
<HackerOsMemoryAccounting>false</HackerOsMemoryAccounting>
```

Both default to `true`. The three hosts import
`build/HackerOs.ResourceAccounting.targets` at the end of their `.csproj`.

| Properties | Effect |
|---|---|
| at least one `true` | reference to `HackerOs.Platform.ResourceAccounting`, constant `HACKEROS_RESOURCE_ACCOUNTING`; each property becomes the runtime option `HackerOs.ResourceAccounting.{Cpu,Memory}.IsSupported` |
| both `false` | no reference: the measurement code is **not in the binary**; the composition compiles its `#else` branch |

The composition lives in `HackerOs.Ecosystem.Host` (which also imports these targets),
which the Ecosystem, `test/test` and `HackerOs.Server` reference. The package
`HackerOs.Ecosystem.Host` is produced with both measurements compiled; a host
of another repository turns them off at runtime (`appsettings.json` or its own
`RuntimeHostConfigurationOption` options). To remove the code from the **whole** graph,
pass the properties on the command line or in a `Directory.Build.props`.
Set only in the `.csproj` of `test/test` or of the server, they turn off
the runtime option of that host (the measurement is ignored at startup, with the
syslog line) but the DLL still arrives through `HackerOs.Ecosystem.Host`.

### At startup

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

| Host | File | Shipped default |
|---|---|---|
| `OS/HackerOs.Ecosystem` | `wwwroot/appsettings.json` (read by `WebAssemblyHostBuilder`, also served to the WASM island of `test/test`) | both enabled |
| `test/test` | `appsettings.json` (server-side composition, prerendering) | both enabled |
| `Server/HackerOs.Server` | none: it serves the Ecosystem's OS and its `wwwroot/appsettings.json` (ADR 0130) | as the Ecosystem (enabled) |

Each host calls, before `AddHackerOsEcosystem`:

```csharp
builder.Services.AddHackerOsResourceAccounting(builder.Configuration, BuildKnownLazyApps.Images);
```

(`AddHackerOsBrowserHost` does it for a WebAssembly host, with the images of its
`HostAppCatalog`; see [`sdk-packages.md`](../framework/sdk-packages.md).)

- The section is read **once**; changing it requires reloading the page.
- A missing value takes its default; an unreadable value too, with a warning
  line in syslog.
- A measurement turned off at build wins: `Enabled: true` is ignored and
  `/var/log/syslog` says so (category `resource-accounting`, written by
  `EcosystemBootCoordinator` as soon as the log is attached), followed by a
  summary line, for example
  `resource accounting: cpu off (not built), memory on`.
- A host or a test that does not call `AddHackerOsResourceAccounting` receives
  `ResourceAccountingPlan.Disabled` and the null accountants.

### Registering a measurement

The CPU measurement is registered this way, at composition (before any app
code); the memory measurement registers its `AllocationTracker` the same
way:

```csharp
// Dans AddHackerOsResourceAccounting, quand plan.CpuActive :
MeasuredCpuTimeAccountant cpu = new(plan.Settings.UnresponsiveThreshold);
services.AddSingleton(new CpuAccountingRegistration(dispatcher.Register(cpu)));
services.AddSingleton(cpu);
services.AddSingleton<ICpuTimeAccountant>(cpu);
```

`AddHackerOsResourceAccounting` registers the null accountants (and
`NullKernelMemoryLedger`) with `TryAddSingleton`: a measurement registered before
them replaces them. Memory already does this (`AddMeasuredMemory`, under
`ResourceAccountingFeatures.IsMemoryAccountingSupported`).

### Reading

- `/proc/<pid>/status`: `Cpu` and `VmRSS` take the measured value when the
  accountant answers, otherwise the simulator's; `CpuSource:` and `MemSource:`
  are `measured` or `simulated`
  ([`virtual-proc-and-dev.md`](virtual-proc-and-dev.md)). A measured memory
  adds its layers after `VmRSS`, and `/proc/meminfo` its lines
  ([`memory-accounting.md`](memory-accounting.md)).
- Task manager: the header of the CPU and Memory columns carries an `estimated`
  label (small, dotted, monospace) as long as at least one row
  is simulated. The app reads `HackProcess.Status`: the SDK does not change.

## Existing apps

Section 5.1 of the plan: the hook charges to the compositor the code run outside
any frame, so all the code of the apps in `Apps/` must run in the frame of
its process.

| Entry point | Who opens the frame |
|---|---|
| Command (`TerminalAppBase.ExecuteAsync`), service (`ServiceAppBase`) | the OS, around the call; the frame follows continuations, timers and `Task.Run` |
| Window (`WindowAppBase`) | parameters, lifecycle, events, after-render |
| Child component | `HackComponentBase` (or `HackEmbeddableComponent`), context received as a cascade from `WindowAppRenderer` |
| MudBlazor dialog (rendered outside the window) | the dialog component receives the window's context as a parameter (the old `FileExplorerDialogBase` and its `OwnerAppContext`); since `desk/3` (ADR 0155), the explorer's dialogs are provided dialogs rendered by the platform in a modal window that gives the calling app's context as a cascade (`ProvidedFileDialog`) |
| `[JSInvokable]` method | the method itself, as its first statement: `using IDisposable scope = HackExecutionContext.Enter(AppContext);` |

Markup rendering and component disposal stay outside a frame: it is
the compositor's work (ADR 0067 §2).

Migration done in this work item:

- to `HackComponentBase`: `NetworkTab`, `PerformanceTab`, `DiskTab`
  (System Monitor), `CodeEditorSearchView`, `CodeEditorExplorer`,
  `CodeEditorQuickOpenPalette`, `CodeEditorCursorStatus`,
  `SimulatedPageRenderer` (Browser), `AppPermissionsPanel` and
  `InstalledAppsPanel` (Settings, which no longer take `AppContext` as a
  parameter but receive it as a cascade);
- to `FileExplorerDialogBase`: `FileConflictDialog`, `OpenWithDialog`,
  `FilePropertiesDialog` (since removed by `desk/3`, replaced by
  `ExplorerFileDialogs`, ADR 0155);
- frame opened as the first statement: `SystemMonitorWindow.SetWindowVisible`
  and the four JavaScript callbacks of `CodeEditorWindow`.

`AmbientFrameSourceTests` (`HackerOs.Ecosystem.Tests`) reads all the sources of
`Apps/` and fails if a component does not derive (directly or through a base of
the app) from `WindowAppBase`, `HackComponentBase` or `HackEmbeddableComponent`, if
a `[JSInvokable]` method does not enter the frame first, or if code
uses `ExecutionContext.SuppressFlow`, `UnsafeQueueUserWorkItem`,
`UnsafeRegister` or `new Thread(`. Before the migration, it reported the thirteen
components and the five callbacks above.

## Decisions

- **Hook in `HackExecutionContext`, reserved installation.** The frame is
  private to the SDK; only `HackerOs.Platform.ResourceAccounting` sees the internal
  members that swap it. An app can neither install nor observe the
  hook.
- **Swapping the `AsyncLocal` rather than an always-present callback.** An
  `AsyncLocal` with a callback costs something at every context restoration; without a
  measurement, it does not exist.
- **Installation at composition, not at OS startup.** This is the only
  moment when no scope is open; the Ecosystem tests pass a fake
  installer to cover all combinations without touching the test process's
  hook.
- **Source per process.** `TryGetCpuUsage` / `TryGetProcessMemory` answer
  process by process: simulated processes, which do not run
  code, will stay simulated even with a measurement active (ADR 0066 §4).
- **Manual reading of the configuration** (no reflection binding):
  compatible with the solution's trimming analysis.

## Tests

| Suite | Covers |
|---|---|
| `HackerOs.Platform.ResourceAccounting.Tests` | hook: enter and leave, continuations (`ThreadContextChanged`), `Task.Run`, nested processes, same process ignored, double installation and open scope refused, observer that throws, removal; broadcaster; independent switches. Parallelism disabled (global hook). |
| `HackerOs.Platform.Core.Tests` (`ResourceAccountingPlanTests`) | the 16 build × setting combinations, syslog lines, null accountants |
| `HackerOs.Platform.Core.Tests` (`ProcFileSystemProviderTests`) | format of `status`, return to the simulator, independent measured sources |
| `HackerOs.Ecosystem.Tests` (`ResourceAccountingCompositionTests`) | hook installed only if a measurement is active (16 combinations), no hook through the real path when everything is off, reading of the section, shipped `appsettings.json` |
| `HackerOs.Apps.SystemMonitor.Tests` | "estimated" label per column |
| `HackerOs.Ecosystem.Tests` (`AmbientFrameSourceTests`) | all the code of the apps in `Apps/` in an ambient frame (components, JavaScript callbacks, no escape from the context), with a check that the analysis does detect a fault |

Verified by hand: a build of `HackerOs.Ecosystem` with both properties
at `false` contains no `HackerOs.Platform.ResourceAccounting*` file and
its `runtimeconfig.json` carries both options at `false`.

## Task list

- [x] Contracts `ICpuTimeAccountant`, `IMemoryAccountant`, `ResourceMeasurementSource`, `IExecutionContextSwitchObserver` (Simulation Abstractions).
- [x] Null implementations and `ResourceAccountingPlan` (Platform Core).
- [x] `AsyncLocal<T>(valueChangedHandler)` hook installable in `HackExecutionContext`, refused if a scope is open.
- [x] Project `HackerOs.Platform.ResourceAccounting`: switches, broadcaster, installation.
- [x] `build/HackerOs.ResourceAccounting.targets`, import in the three hosts, conditional reference, `RuntimeHostConfigurationOption`.
- [x] `appsettings.json` of the three hosts (created for the Ecosystem, turned off for the server), reading at composition, syslog lines.
- [x] `CpuSource` / `MemSource` in `/proc/<pid>/status`, "estimated" label in the task manager.
- [x] Tests of the combinations, no hook when everything is off, hook test observer.
- [x] Documentation and index.
- [x] Existing apps (plan §5.1): `Apps/` components on `HackComponentBase`, explorer dialogs on `FileExplorerDialogBase`, `[JSInvokable]` callbacks in the frame, test `AmbientFrameSourceTests`.
