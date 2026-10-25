# App SDK and the SDK boundary

## Purpose

A HackerOS app, system or third-party, obtains OS services only through the
public SDK (plan principle 3, ADR 0055). This document describes:

- what makes up the SDK and what an app may reference and inject;
- how the build checks these rules (codes `HOSAPP0030`, `HOSAPP0031`,
  `HOSSDK0001`, ADR 0059);
- the end of phase 4's temporary exemption list;
- the SDK APIs, as the `rebuild/4-*` work items
  add them (ADR 0056).

Each phase 4 work item that adds an API documents it here, in the
section [SDK API](#api-du-sdk).

To write an app step by step, see the
[developer guide](sdk-developer-guide.md); for the complete list of
public APIs, the [SDK reference](sdk-reference.md).

## What makes up the SDK

| Project | Content |
|---|---|
| `Shared/HackerOs.AppSdk` | `IAppExecutionContext` and its gateways, static `Hack*` facades, entry point base classes (`TerminalAppBase`…). |
| `Shared/HackerOs.AppSdk.Blazor` | Blazor components and services for windowed apps (window, download, charts, embeddable components `HackEmbeddedComponent<T>`…). |
| `Shared/HackerOs.AppSdk.Icons` | Icon catalog. |
| `Shared/HackerOs.AppSdk.Terminal` | xterm.js terminal (`TerminalPanel`, ADR 0061), which hosts a shell through `HackShell`, and tabbed console (`TerminalTabs`). |
| `Shared/HackerOs.AppSdk.VirtualKeyboard` | Virtual keyboard. |

Any `Shared/HackerOs.AppSdk` or `Shared/HackerOs.AppSdk.*` library is
an **SDK library** (MSBuild property `HackerOsSdkLibrary`, computed by
`build/HackerOs.Apps.targets`). A test project named the same way is not
one: only the location under `Shared/` counts.

## Rules

### Project references (ADR 0055 §2)

| Project | May reference | Otherwise |
|---|---|---|
| App project (`HackerOsAppProject=true`, any project under `Apps/`) | `Shared/HackerOs.{App,Simulation,Theming}.Abstractions`, the SDK libraries, `Platform/HackerOs.Theming.Blazor`, another project under `Apps/` | `HOSAPP0030` |
| SDK library | the three `*.Abstractions` above, another SDK library | `HOSSDK0001` |

Only **direct** references of the project count: a transitive reference
is checked in the project that declares it. `PackageReference`s
remain free. The build-order reference to the manifest generator,
added by `HackerOs.Apps.targets`, is ignored.

### Injection (ADR 0055 §3)

An app can obtain by injection only a service whose type is:

1. defined in an SDK library;
2. a MudBlazor type (for example `MudBlazor.IDialogService`);
3. `Microsoft.JSInterop.IJSRuntime`, only to import its own JavaScript
   module.

Two injection points are checked; they are the only ones the platform
honors for an app:

- each `[Inject]` property of each type of the assembly (an `@inject`
  directive compiles to an `[Inject]` property);
- each parameter of the public constructors of the entry points
  (`EntryPoint`, `MobileEntryPoint`), which the host builds with
  `ActivatorUtilities` (ADR 0034 §2).

For an entry point constructor, the following remain allowed: the `AppManifest` that
the host passes explicitly, value types and `string` (never registered
as services: such a constructor is only for tests, for example a timer
interval).

The types of `Simulation.Abstractions` and `App.Abstractions` remain allowed
as **data** (a non-`[Inject]` property, a method parameter).

### JavaScript calls (ADR 0055 §3)

An `Invoke*Async` call on an `IJSRuntime` must have the form:

```csharp
_module = await JS.InvokeAsync<IJSObjectReference>(
    "import", "./_content/<projet de l'app>/<fichier>.js");
```

Calls on the resulting `IJSObjectReference` are free: it is the
app's code. Passing the `IJSRuntime` to a component or service of an SDK
library (for example `BrowserFileDownloader` of `HackerOs.AppSdk.Blazor`) is
allowed: it is the SDK's code that makes the call.

## Verification

| Rule | Where | When | Code |
|---|---|---|---|
| An app's references | target `ValidateHackerOsSdkBoundary` of `build/HackerOs.Apps.targets` | before `ResolveProjectReferences`, so before compilation | `HOSAPP0030` |
| An SDK library's references | same target | same | `HOSSDK0001` |
| `HackerOs.*` packages of an app or SDK library (ADR 0098 §2), direct or transitive | target `_ValidateHackerOsPackageBoundary` | after `ResolveAssemblyReferences`: each compile reference coming from a `HackerOs.*` package must be allowed (app: `*.Abstractions`, `HackerOs.AppSdk*`, `HackerOs.Theming.Blazor`, `HackerOs.App.Build`, `HackerOs.Apps.*`, `HackerOs.Commands`; SDK library: `*.Abstractions` and SDK) | `HOSAPP0030` / `HOSSDK0001` |
| Injection | `SdkBoundaryInspector` of the manifest generator (`Tools/HackerOs.Tools.ManifestGenerator`) | after compilation, with the generation of `manifest.json`; on error, nothing is written | `HOSAPP0031` |
| `IJSRuntime` | source test `SdkBoundarySourceTests` (`Tests/HackerOs.Ecosystem.Tests`) | `dotnet test` | test failure |

The messages name the project, reference or type at fault, and the
ADR rule:

```text
App.csproj : error HOSAPP0030: [My.App] Forbidden project reference 'Platform/HackerOs.Platform.Core/HackerOs.Platform.Core.csproj'. …
App.csproj : error HOSAPP0031: [My.App.MyWindow] [Inject] property 'Sessions' injects 'HackerOs.Simulation.Abstractions.Sessions.ISessionService' …
```

The generator reruns the inspection when `HackerOs.Apps.targets` changes.

Outside the repository (package `HackerOs.App.Build`, see [`sdk-packages.md`](sdk-packages.md)),
the `ProjectReference` rule does not apply (it is written in repository
paths); it is the package rule that protects the boundary, and the injection
inspection (`HOSAPP0031`) runs the same way.

These three checks are always **errors**: no project can
downgrade them to warnings. A project's role comes from its location
(`Apps/` for an app, `Shared/HackerOs.AppSdk*` for an SDK library); a
`.csproj` under these folders that sets `HackerOsAppProject` or
`HackerOsSdkLibrary` itself makes
`SdkBoundarySourceTests.No_app_or_sdk_project_overrides_its_boundary_role` fail, and
`Build_targets_have_no_exemption_mechanism` fails if an exemption or a
`<Warning>` reappears in `HackerOs.Apps.targets`.

## End of temporary exemptions (ADR 0055 §6)

`4-sdk-boundary` had introduced a `HackerOsSdkBoundaryExemption` list in
`build/HackerOs.Apps.targets`: a listed project received warnings
instead of errors, and the `IJSRuntime` test ignored it. Nine projects were on it;
each phase 4 work item removed its own:

| Project | Removed by |
|---|---|
| `HackerOs.Apps.Browser`, `HackerOs.Commands` | `4-sdk-network` ([`sdk-network.md`](sdk-network.md)) |
| `HackerOs.AppSdk.Terminal` | `4-sdk-shell-host` ([`shell-host.md`](shell-host.md)) |
| `HackerOs.Apps.FileExplorer`, `HackerOs.Apps.TextEditor`, `HackerOs.Apps.CodeEditor` | `4-sdk-user-and-apps` |
| `HackerOs.Apps.SystemMonitor` | `4-task-manager` ([`task-manager.md`](../dev/task-manager.md)) |
| `HackerOs.Apps.Settings`, `HackerOs.Apps.Diagnostic` | `4-sdk-system-admin` ([`system-administration.md`](system-administration.md)) |

`4-sdk-boundary-final` (2026-10-11) removed the mechanism: the
`HackerOsSdkBoundaryExemption` item, the target `_HackerOsResolveSdkBoundaryExemption`
and its property `_HackerOsSdkBoundaryExempt`, the `HOSAPP0030` /
`HOSSDK0001` warning of the target `ValidateHackerOsSdkBoundary`, the generator's
`--boundary-exempt` option and the `severity` parameter of
`GeneratorDiagnostic.ToMsBuildLine`. All apps in `Apps/` and all
`HackerOs.AppSdk*` libraries compile without any boundary diagnostic.

The unused `Platform.Core` references of Calculator, HackPaint,
IconViewer, TextEditor and Browser were removed by `4-sdk-boundary`:
Calculator, HackPaint and IconViewer already passed without an exemption.

## How an app obtains an OS service (ADR 0055 §4)

Only through the SDK:

- a **gateway** on `IAppExecutionContext`, specific to the instance, which
  checks the required capability at each call. A new gateway is a
  member of the interface with a default "not supported" implementation, so that existing implementations still compile; the
  trusted factory of Platform Core provides the real one;
- a static `Hack*` **facade** that reads the ambient context (ADR 0043) and
  calls the gateway. This is the recommended form.

A missing capability throws `HackUnauthorizedAccessException` and goes through
`IAppPermissionErrorGateway`.

## SDK API

Static facades that existed at the start of phase 4:

| Facade | Role | Document |
|---|---|---|
| `HackFile`, `HackDirectory`, `HackPath` | files | [`hack-file-api.md`](hack-file-api.md) |
| `HackExecutionContext` | ambient context | [`hack-file-api.md`](hack-file-api.md) |
| `HackEnvironment` | arguments, current directory, variables | [`process-environment.md`](../dev/process-environment.md) |
| `HackSettings` | settings as files | [`settings-files.md`](settings-files.md) |
| `HackTrash` | trash | [`trash.md`](../dev/trash.md), [`trash-api.md`](trash-api.md) |
| `HackDns`, `HackPing`, `HackHttpClient`, `HackTcpClient`, `HackUdpClient` | network, real fallback decided by the OS (`4-sdk-network`), relayed sockets (`v3/10`) | [`sdk-network.md`](sdk-network.md) |
| `HackDriveInfo` | disk size and free space (`4-proc-metrics`) | [`system-metrics.md`](../dev/system-metrics.md) |
| `HackCrossOriginIsolation` | isolated page or not, and what that enables (ADR 0146, SDK 2.0) | [`worker-processes.md`](../dev/worker-processes.md#isolation-cross-origin-adr-0131-adr-0143) |
| `HackClipboard` | session clipboard, text and file list (`4-file-explorer`) | [`file-explorer.md`](../dev/file-explorer.md), [`clipboard-api.md`](clipboard-api.md) |
| `HackTextContent` | text or binary decided on the content, like `file` (`4-file-explorer`) | [`text-editor.md`](../dev/text-editor.md) |

Gateways of `IAppExecutionContext`: `FileSystem`, `Events`,
`Notifications`, `Logging`, `Clock`, `Processes`, `Intents`, `Watch`,
`Services`, `PermissionErrors`, `Environment`, `Catalog` and `AppManagement`
(ADR 0056, `4-sdk-user-and-apps`), `Network` (`4-sdk-network`), `ShellHost`
(`4-sdk-shell-host`), `Drives` (ADR 0057, `4-proc-metrics`),
`Clipboard` (ADR 0056 §12, `4-file-explorer`), `SystemSettings`,
`PermissionAdministration`, `ServerConnection`, `SystemPower` and
`StorageDiagnostics` (`4-sdk-system-admin`), `Console` (ADR 0064).

`ShellErrorText` (short text of a file error, for commands like
`cd`) is also in the SDK, next to `IShellSession` (`4-sdk-network`).
`ShellErrorText.Describe(exception)` remains in English (text identical to
`strerror` in the C locale); `Describe(exception, context)` gives it in the
language of the app's process (ADR 0104). Likewise,
`HackWebSiteFile.Validate(site, scope, context)` translates the validation
messages of a `.site` displayed by an app.

### Current user (ADR 0056 §1)

```csharp
HackUserInfo me = HackUser.Current;     // LoginName, DisplayName, HomeDirectory, IsAdministrator
string docs = HackEnvironment.GetFolderPath(HackSpecialFolder.MyDocuments);   // /home/benoit/Documents
HackUserInfo cached = HackUser.Describe(AppContext);   // sans contexte ambiant (rendu d'un composant)
```

- `HackUser.Current` describes the **effective** user: `HackUser.Root`
  (`root`, `/root`, administrator) for a process launched by `sudo`.
- `HackSpecialFolder`: `UserProfile` (`~`), `Desktop`, `MyDocuments`
  (`~/Documents`), `ApplicationData` (`~/.config`), `LocalApplicationData`
  (`~/.local/share`). The path is computed, not checked.
- No capability. `IAppExecutionContext` gains `UserLoginName` and
  `UserDisplayName` (default implementations; the factory fills them
  from the principal).
- Replaces `ISessionService.CurrentPrincipal` in FileExplorer, TextEditor and
  CodeEditor. Since `BuildRenderTree` does not run in the ambient context,
  a component that displays the home folder uses
  `HackUser.Describe(AppContext)`.

### App catalog (ADR 0056 §2)

```csharp
IReadOnlyList<HackAppInfo> apps = await HackApps.GetInstalledAsync();
IReadOnlyList<HackAppInfo> editors = await HackApps.GetHandlersAsync("~/notes.md");
await HackApps.LaunchAsync("org.hackeros.text-editor", ["/home/benoit/notes.md"]);
HackOpenFileResult opened = await HackApps.OpenFileAsync("~/notes.md");   // Opened, ChooserRequired (+ CandidateAppIds), NoHandler
```

- `HackAppInfo`: `Id`, `Name`, `Description`, `Kind`, `Category`, `Icon`,
  `Version`, `PublisherId`, `IsSystemApp`, `IsHidden`, `FileHandlers`, `Urls`
  (`web.urls` addresses of a web app, empty otherwise; list of external sites of the
  Web Server Manager).
- `GetInstalledAsync`: apps installed for the user, sorted by name;
  an uninstalled app (ADR 0048) is not listed. **No capability.**
- `GetHandlersAsync`: installed windowed apps that declare the extension or
  MIME type; the default first: the user's
  (`~/.config/hackeros/file-associations.json`), otherwise that of
  `/etc/hackeros/file-associations.json` (ADR 0121).
- `GetDefaultHandlersAsync`, `SetDefaultHandlerAsync(target, appId, action)` and
  `ResetDefaultHandlerAsync(target, action)` (ADR 0121, work item `v3/4b`):
  user and machine defaults, per-user choice
  (`file-associations.read`, plus `filesystem.user-home.write` to write);
  see [`user-file-associations.md`](../dev/user-file-associations.md).
- `LaunchAsync` and `OpenFileAsync` go through `IAppIntentGateway`
  (`apps.launch`). Failure: `HackAppLaunchException` (unknown app,
  uninstalled, or startup error).
- `EditFileAsync`, `RevealFileAsync`, `OpenUriAsync`, `ShowSettingsAsync` and
  `SendIntentAsync` (ADR 0096, work item `v3/4`) also go through
  `IAppIntentGateway`; see [`app-intents.md`](app-intents.md).
- `HackApps.Changed` (or `AppContext.Catalog.Changed`, preferable when
  unsubscribing in `Dispose`, outside the ambient context) signals an installation or
  an uninstallation; handlers run in the context of the
  subscribed app.
- Gateway: `IAppCatalogGateway Catalog` (`AppCatalogGateway`, Platform Core).

### App management (ADR 0056 §3)

```csharp
IReadOnlyList<HackAppInstallInfo> all = await HackAppManagement.GetAllAsync();   // App, State, UninstallClosure
IReadOnlyList<string> removed = await HackAppManagement.UninstallAsync("org.hackeros.calculator", HackInstallScope.User);
await HackAppManagement.ReinstallAsync("org.hackeros.calculator", HackInstallScope.Machine);
```

- **Capability `apps.manage`** (dangerous) for all members. Settings now
  declares it.
- `HackAppInstallState`: `Installed` (including a system app, flagged by
  `App.IsSystemApp`), `UninstalledForUser`, `UninstalledForMachine`,
  `Incompatible` (refused at load time: SDK version, ADR 0098 §3) and
  `Damaged` (tampered resource, ADR 0098 §5); `HackAppInstallInfo.Problem`
  then gives the reason, displayed by Settings.
- `Machine` scope: the OS authentication agent asks for an
  administrator ([`admin-authentication-agent.md`](../dev/admin-authentication-agent.md)),
  then the OS writes `/etc/hackeros/apps.config` itself. Refusal or cancellation:
  `HackUnauthorizedAccessException`.
- System app, or a closure that contains one: `InvalidOperationException`,
  before any password prompt. Reinstalling for oneself an app
  uninstalled by the administrator: `InvalidOperationException`. Unknown
  app: `ArgumentException`.
- Gateway: `IAppManagementGateway AppManagement` (`AppManagementGateway`,
  on `IAppInstallationService`).

### Network (ADR 0056 §6)

`HackDns`, `HackPing`, `HackHttpClient` and `HackTcpClient`, gateway
`IAppNetworkGateway Network`, real fallback decided by the OS: see
[`sdk-network.md`](sdk-network.md).

### Disks (ADR 0057 §7)

`HackDriveInfo.GetDrivesAsync()` returns the mounts that have a size
(today `/`), modeled on `System.IO.DriveInfo`: `Name`,
`DriveFormat` (`idbfs`), `DriveType` (`Fixed`), `VolumeLabel` (`hda`),
`TotalSize`, `AvailableFreeSpace`, `TotalFreeSpace`, `RootDirectory`. No
capability, like `df`. Gateway `IAppDriveGateway Drives`, implemented by
`AppDriveGateway` in Platform Core. Values are read once, at the call.
CPU, disk and network measurements, on the other hand, are `/proc` files: see
[`system-metrics.md`](../dev/system-metrics.md).

### Cross-origin isolation (ADR 0146)

`HackCrossOriginIsolation.GetStatusAsync()` (`Diagnostics` namespace) returns
the isolation that the OS decided at startup: `IsIsolated`, `Reason`
(`None`, `Setting`, `MissingHeaders`, `InsecureContext`, `Browser`,
`NotBrowser`), `Setting` (`Auto`, `On`, `Off`), `EmbedderPolicy`,
`SharedPipes`, `SynchronousWorkerCalls`, `BrowserMemoryMeasurement`,
`TimerResolution`. No capability, like `uname`. Gateway
`IAppCrossOriginIsolationGateway CrossOriginIsolation`, implemented by
`AppCrossOriginIsolationGateway` in Platform Core and by the operation
`coi.get` in a worker. Released with SDK 2.0 (no 1.3 was released).

### Processes and services (ADR 0056 §4 and §5)

| API | Gateway | Capability | Replaces | Document |
|---|---|---|---|---|
| `HackProcess` (`GetProcessesAsync`, `GetProcessByIdAsync`, `GetCurrentProcessAsync`, `RefreshAsync`, `GetEnvironmentVariablesAsync`, `KillAsync`, `TerminateAsync`, `CloseMainWindowAsync`, `Current`, `CheckpointAsync`; `WorkingSet64` reads `VmRSS`, `PrivateMemorySize64` reads `RssAnon`, ADR 0068; `UserProcessorTime`, `PrivilegedProcessorTime`, `TotalProcessorTime`, `Responding` read from `/proc/<pid>/stat` and `status`, ADR 0067), `HackCurrentProcess`, `HackStopRequestedEventArgs`, `HackProcessKilledException`, `HackProcessState`, `HackProcessIoCounters` | read: `IAppExecutionContext.FileSystem` on `/proc`; action: `IAppExecutionContext.Processes` (`IAppProcessGateway`) | `filesystem.system.read` to read `/proc`; `process.list` (normal) to see other processes; `process.manage` (dangerous) to stop another process | `IProcessManager`, `AppLifecycleOrchestrator`, `IEventBus` | [`task-manager.md`](../dev/task-manager.md) |
| `HackServiceController` (`GetServicesAsync`, `RefreshAsync`, `StartAsync`, `StopAsync`, `SetStartTypeAsync`), `HackServiceControllerStatus` | `Catalog` (list), `/proc` (state), `Services` (`IAppServiceControlGateway`) | none to list; `services.manage` (dangerous) to act on another app's service or read its startup type | `AppCatalog` filtered on `AppKind.Service`, orchestrator | [`task-manager.md`](../dev/task-manager.md) |

A process's data comes from `/proc`, exactly what `ps` reads; there
is no subscription to process events: a task manager
rereads `/proc` every second.

### Clean shutdown and checkpoints (ADR 0069)

When the OS stops a process politely (`kill`, "End task",
`CloseMainWindowAsync`, logout, shutdown), it moves it to `Stopping`,
raises `StopRequested` **once**, cancels its token, then waits until the
deadline (simulated clock) before killing it. `kill -9` and "Force kill" do not
warn.

| API | Role |
|---|---|
| `HackProcess.Current` (`HackCurrentProcess`: `Id`, `StopToken`, `IsStopRequested`, `StopRequest`, `StopRequested`) | The current process seen from the inside, modeled on `IHostApplicationLifetime.ApplicationStopping`. Also `IAppExecutionContext.StopRequested` / `StopRequest`. |
| `HackStopRequestedEventArgs` (`Reason`, `RequestedAt`, `Deadline`, `GracePeriod`, `Defer(Task)`) | Reason (`CloseRequested`, `Terminated`, `Logout`, `Shutdown`…) and deadline; `Defer` asks the OS to wait for some work (draft, step in progress) before its default action, never beyond the deadline. |
| `HackProcess.CheckpointAsync(token)` | Voluntary checkpoint: throws `OperationCanceledException` if a stop is requested; yields (`Task.Yield`) when the current time slice exceeds 8 ms; otherwise returns immediately, without allocation. |
| `HackProcess.TerminateAsync(timeout)` | `SIGTERM` of another process (reason `Terminated`, 5 s by default), under `process.manage`; `true` if the process ended on its own. |
| `HackProcessKilledException` | Thrown by any gateway called for a killed process (derives from `OperationCanceledException`). |
| `WindowAppBase.OnAppStopRequestedAsync(e)` | Hook of a windowed app; the window closes when the returned task completes (default action of `SIGTERM`). Not called when the user closes the window: `IWindowCloseGuard` handles that. |
| `ServiceAppBase.OnStoppingAsync` | Called only once, as soon as `StopRequested` (`ServiceStopReason.Terminated` for `kill`/"End task"/`systemctl stop`), and at the latest at OS shutdown. |
| `HackRecoveryDraft` (`Open`, `SaveAsync`, `TryReadAsync`, `TakeAsync`, `DeleteAsync`) | Recovery draft in `~/.local/state/<appId>/recovery/` (`0700` folder), to be written on `StopRequested` and offered at the next startup. |

Escalation (ADR 0069 §3): once the deadline has passed, the process is killed (`Timeout`);
its process's scheduler (`ProcessTaskScheduler`, installed for
commands and services) drops its continuations, its subscriptions and
timers are no longer called back, and its gateways throw
`HackProcessKilledException`. Windowed apps stay on the rendering
dispatcher: their windows are closed.

What existing apps do (section 5.1 of the
[phase 5 plan](../dev/plans/execution-isolation-plan.md)):

| App | On `StopRequested` | Long loops with `CheckpointAsync` |
|---|---|---|
| TextEditor | `HackRecoveryDraft` draft if the document is modified; restored at the next startup as a new unsaved buffer | none |
| CodeEditor | recovery snapshot written right away (instead of waiting for the 1.5 s debounce) | folder search, folder walk |
| FileExplorer | waits for the current item; `HackFileOperations` stops between two (atomic) items and reports the others "stopped before this item" (`HackFileTransferResult.Cancelled`) | copy, move, import, trash, zip, extraction, trash restore |
| Settings | waits for the setting write in progress, which no longer uses the process token | none |
| Services (`SampleTickerService`) | `OnStoppingAsync` (once) | none |
| `HackerOs.Commands` | token cancelled: exit 130; killed: 137 | `find`, `grep` (including `-r`), `du`, `zip`, `unzip`, `locate` |

HackPaint has no save: nothing to keep. The other apps have neither
unsaved state nor long loops.

### Processes in Web Workers (ADR 0070 and 0071)

A command or service can run in a Web Worker instead of the main
thread. The app declares it in its manifest, and nothing else changes in
its code:

```csharp
[AppManifest(Execution = AppExecutionHost.Dedicated)] // ou Pool, MainThread
public sealed class MyServiceApp : AppDefinition { ... }
```

| Value | Effect |
|---|---|
| `Default` (omitted) | Command → `Pool`, service → `Dedicated`, window → `MainThread` (ADR 0070 §1). |
| `Pool` | One worker per virtual core; the steps of a pipeline share the same worker. |
| `Dedicated` | A worker of its own (the ready worker, replaced immediately). |
| `MainThread` | Stays on the main thread; to be commented (reason) in the definition. |

In a worker, `IAppExecutionContext` is served by remote gateways
that send each call to the OS, where **the same capability checks**
apply; `stdin`/`stdout`/`stderr` go through messages. `StopRequested`,
`CheckpointAsync`, the 130/137 codes and `OnStoppingAsync` behave as
on the main thread. What is not available in a worker (shell host,
administration, the simulated browser's HTTP page, `StartChild`, synchronous
forms of `Kill` and `ListProcesses`, `WithSelectedHandle`, `SetNice`,
`RegisterSharedChannel` outside a `VirtualPath` scope) throws
`NotSupportedException`: an app that needs it declares `MainThread`.
Exception: when the page is isolated (`crossOriginIsolated`, ADR 0131 and 0143),
the synchronous forms of `Kill` and `ListProcesses` also work in a
worker, which waits for the OS's answer like a blocking system call.
Details: [`worker-processes.md`](../dev/worker-processes.md), [`app-execution-hosts.md`](app-execution-hosts.md).

**Events and file watching in a worker (ADR 0124, ADR
0138).** `HackEvents` and `HackFileSystemWatcher` work in a worker: the OS
holds the subscription on behalf of the process and sends it each message. The payload
crosses as JSON; OS events are already supported, an app type
must be declared by a `JsonSerializerContext` of its assembly:

```csharp
[assembly: HackWireContext(typeof(MyEventsJsonContext))]

[JsonSerializable(typeof(MyTickEvent))]
internal sealed partial class MyEventsJsonContext : JsonSerializerContext;
```

Without a declaration, subscribing or publishing throws in the worker a
`NotSupportedException` that names the type and the attribute; on the main thread,
nothing changes. In a worker, `Publish` returns a provisional result
(`Delivered`): a refusal by the OS (another app's topic, locked channel) is
written to the app's log ([`app-log-files.md`](app-log-files.md)). `PublishAsync` (ADR 0145, SDK 2.0)
returns the OS's real result, subscriber faults included, on the main
thread as in a worker: it is the form to prefer in code that
may run in a worker.

```csharp
TopicPublishResult result = await context.Events.PublishAsync(topic, new MyTickEvent(1), cancellationToken);
if (result.Outcome != TopicPublishOutcome.Delivered) { /* refusé par l'OS */ }
```

A refused channel subscription ends with the exception. Deliveries
are always asynchronous in a worker.

### Charts (ADR 0058 §2)

`HackLineChart` (`HackerOs.AppSdk.Blazor.Charts`) draws one or more
`HackChartSeries` in native SVG, the most recent value on the right, with a
legend and an automatic scale; no library or JavaScript. Colors
come from the theme (`--hos-chart-series-0` to `7`). See
[`task-manager.md`](../dev/task-manager.md).

### Clipboard (ADR 0056 §12)

```csharp
await HackClipboard.SetFileDropListAsync(["~/rapport.txt"], HackClipboardFileOperation.Cut);
HackClipboardFileList? files = await HackClipboard.GetFileDropListAsync();   // Operation + Paths
string? text = await HackClipboard.GetTextAsync();
```

- One clipboard per **session**, shared by all its apps (`SystemClipboard`,
  Platform Core), cleared at logout; file list in the GNOME
  format `x-special/gnome-copied-files`, also readable as text (the paths).
- **Capabilities** `clipboard.read` (read) and `clipboard.write` (write,
  clear), normal. `Changed` requires no capability.
- `GetFileReferencesAsync` / `SetFileReferencesAsync`, old forms of
  "copy", are removed at the SDK 1.0 freeze (ADR 0114 §3). The SDK's old in-memory class `AppClipboardGateway` is
  removed (the real one is in Platform Core).
- Gateway: `IAppClipboardGateway Clipboard` (`AppClipboardGateway`, Platform Core).
- **Sharing with the real machine** (ADR 0065): text copied by an app also goes
  to the host's clipboard, and a paste from the host becomes the current
  entry, according to the `sharing` key of `/etc/hackeros/clipboard.config`.
  An app never talks to `navigator.clipboard`; `BrowserClipboard` was
  removed at the SDK 1.0 freeze (ADR 0114 §3). See [`clipboard.md`](../dev/clipboard.md), [`clipboard-api.md`](clipboard-api.md).
- `HackDirectory.CopyAsync` (recursive copy) and `HackFileOperations`
  (`HackerOs.AppSdk.IO`, formerly `FileTransfer`, transfers of the explorer and of
  any app since SDK 2.0) are described in
  [`file-explorer.md`](../dev/file-explorer.md).

### System administration (ADR 0056 §8 to §11, ADR 0060)

```csharp
await using HackSettingsDocument appearance = await HackSettings.OpenSystemAsync("appearance");
await HackPermissionAdministration.SetMachineDecisionAsync(appId, capability, HackPermissionDecision.Deny);
HackServerConnectionState server = await HackServerConnection.GetStateAsync();
await HackSystem.RebootAsync();
HackStorageSnapshot snapshot = await HackStorageDiagnostics.GetSnapshotAsync();
```

- `HackSettings.OpenSystemAsync`: OS documents (`appearance`,
  `ui-platform-preference`, `security`, `policy`), without a capability; file
  permissions decide. `HackSettingsDocument.SetManyAsync` writes several
  keys in a single modification (a single approval for `/etc`).
- `HackComponentProviders` (`apps.manage`, ADR 0156): the app that provides the
  file control and its dialogs for the whole machine; a non-system
  app goes through the OS approval window.
- `HackPermissionAdministration` (`permissions.manage`), `HackServerConnection`
  (`server.connection.manage`), `HackSystem` (`system.power`),
  `HackStorageDiagnostics` (`system.storage.inspect`): dangerous capabilities.
  Machine changes and power go through the OS authentication
  agent.
- Gateways: `SystemSettings`, `PermissionAdministration`,
  `ServerConnection`, `SystemPower` (not `System`, so as not to hide
  the namespace) and `StorageDiagnostics`.
- Details: [`system-administration.md`](system-administration.md).

### Shell host (ADR 0056 §7)

| API | Gateway | Capability | Replaces | Document |
|---|---|---|---|---|
| `HackShell.StartAsync`, `IHackShell` (`ExecuteAsync`, `CompleteAsync`, `GetInstalledCommands`, `WorkingDirectory`, `ExitStatus`, `Prompt`, history), `HackShellOptions`, `HackShellCompletion`, `HackCredentialPrompt` | `IAppExecutionContext.ShellHost` (`IAppShellHostGateway`) | `shell.host` (dangerous) | `ShellInterpreter`, `ISessionService`, `IElevationService` in `TerminalPanel` | [`shell-host.md`](shell-host.md) |

`4-terminal` adds path completion, `HackShellCompletion.DisplayCandidates`,
`IShellSession.Exit` (command `exit`) and the `TerminalTabs` tabs component
of `HackerOs.AppSdk.Terminal` ([`terminal-console.md`](../dev/terminal-console.md)).

The shell is an `hsh` process that is a child of the app; its commands are its
children and keep their own capabilities. The terminal reads the credentials
(`HackCredentialPrompt`), the OS verifies them.

### Launch arguments of a window

`WindowAppBase.LaunchArguments` returns the process arguments without `argv[0]`
(like `Main(string[] args)`), and `WindowAppBase.LaunchFile` the first one if it is
an absolute path. Opening a file from the explorer or with `open` launches
the associated app with this path: a document app reads its initial file there
(see [`text-editor.md`](../dev/text-editor.md)).

### Window events and sizes (ADR 0095)

`WindowAppBase` receives the hooks `OnWindowActivated`, `OnWindowDeactivated`,
`OnWindowStateChanged` and `OnWindowResized` (with their `Async` versions) and
the state `IsWindowActive`, `WindowState`, `WindowSize`. The types `HackWindowState`,
`HackWindowSize` and `IWindowAppEventTarget` are in `HackerOs.AppSdk.Blazor`;
`WindowOptions` (sizes declared by `AppDefinition.Window`, and since SDK 1.1
`RememberGeometry`, ADR 0116) is in `HackerOs.App.Abstractions`. No app injects a service to receive them:
the OS calls them. See [`window-model.md`](window-model.md).

### Window title (ADR 0117, ADR 0123, SDK 1.1)

`WindowAppBase.SetWindowTitleAsync(title)` replaces the title of the instance's
window (no capability), `WindowTitle` reads it and
`FormatWindowTitle(document, modified)` composes `document — App` with the app's
translated name. The platform gives each window an `IAppWindowGateway`
as a fixed cascading value, never by injection: an app reaches only its
own window and the SDK references no `HackerOs.Windowing.*` type
(ADR 0055). See [`window-model.md`](window-model.md) §4.

### SDK 1.2 API (`exec` series, ADR 0124 to 0128, ADR 0132)

Additions only: a 1.0 or 1.1 app runs without change. Full list
in the [SDK reference](sdk-reference.md).

| API | Role | Capability | Document |
|---|---|---|---|
| `[assembly: HackWireContext(typeof(…))]` (`HackWireContextAttribute`) | Types of an app that cross a worker's boundary: `HackEvents` payloads, inputs and results of `HackBackground` | none | [above](#processus-dans-des-web-workers-adr-0070-et-0071), [ADR 0124](../dev/adr/0124-remote-event-and-watch-gateways.md) |
| `HackProcess.GetResourceLimitsAsync` → `HackResourceLimits`, `SetResourceLimitAsync`; `ProcessLimitExceededException` (`EAGAIN`), `FileSystemErrorCode.TooManyOpenFiles` (`EMFILE`), `ProcessExitReason.CpuLimitExceeded`, `MemoryLimitExceeded` | `setrlimit`-style limits of a process | `process.manage` for another process | [`process-limits.md`](../dev/process-limits.md) |
| `AppDefinition.OomScoreAdjust` (manifest `oomScoreAdj`), `ProcessExitReason.OutOfMemory` | starting `oom_score_adj`, negative for a system app only (`HOSAPP0024`); exit reason of an OOM killer victim | none | [`oom-killer.md`](../dev/oom-killer.md) |
| `HackProcess.Nice`, `BasePriority`, `IShellSession.RunNicedAsync`, `ProcessNice` | `nice` priority of a process; launch a command with an adjustment | `process.manage` outside one's own process and its children; `shell.control` | [`process-priority-and-unresponsive-windows.md`](../dev/process-priority-and-unresponsive-windows.md) |
| `HackBackground.RunAsync`, `HackBackgroundJob<TInput, TResult>`, `HackBackgroundContext`, `IAppExecutionContext.Background`; analyzer `HOSBG0001` to `HOSBG0004` | Long computation in a child process of the app, placed in a worker; static method checked at build time | none | [`background-jobs.md`](background-jobs.md) |

### SDK 2.0 API (desktop series, ADR 0148 to 0161)

A major version: a 1.x app is refused and must be recompiled. The only removal
is the `HackerOs.AppSdk.FileView` package; the migration table is in
[`sdk-packages.md`](sdk-packages.md#migration-dune-app-1x).

| API | Role | Capability | Document |
|---|---|---|---|
| `HackFileView`, `IHackFileView` (`HackerOs.AppSdk.Blazor.Files`) | File control provided by the OS (the default explorer), with the rights of the app that displays it | those of the files shown | [`embedded-components.md`](embedded-components.md), [`file-explorer.md`](../dev/file-explorer.md), [`file-control-api.md`](file-control-api.md) |
| `Menus.Attach`, `Menus.Define`, `HackContextMenu`, `HackMenuDefinition` | OS context menu, multi-level, keyboard and long press | none | [`context-menu.md`](context-menu.md) |
| `[RegisterFileContextMenu]`, `RegisterFileContextMenuContext`, `HackFileMenuItems` | Entries of an app in file menus, registered at build time | `shell.context-menu` | [`context-menu.md`](context-menu.md#entrées-des-apps-dans-les-menus-des-fichiers-chantier-4) |
| `HackFileOperations`, `IDialogService.AsFileConflictResolver` | Copy, move, import, trash, Nautilus-style conflicts (formerly `FileTransfer`) | those of the files | [`file-explorer.md`](../dev/file-explorer.md), [`file-operations-api.md`](file-operations-api.md) |
| `HackDesktopSettings`, `HackDesktopEntry`, `HackDesktopLayout` | Settings, `.desktop` shortcuts and desktop grid | those of the home folder | [`desktop.md`](../dev/desktop.md), [`desktop-api.md`](desktop-api.md) |
| `HackComponentProviders`, `HackFileMenuEntries` | Choose the provider of a contract, hide a menu entry | `apps.manage` | [`embedded-components.md`](embedded-components.md) |
| `IAppEventGateway.PublishAsync`, `HackCrossOriginIsolation` | Planned for a 1.3 that was never released (ADR 0145, 0146) | none | above |

### APIs added after phase 4 (ADR 0061 to 0065)

These APIs follow the same rules (facade or gateway, capability declared in
the `[AppManifest]`); their details are in the
[SDK reference](sdk-reference.md) and in each work item's document.

| API | Role | Capability | Document |
|---|---|---|---|
| `HackConsole`, gateway `IAppExecutionContext.Console` (`IAppConsoleGateway`) | Standard streams of an instance: those of the shell that launched it by name, `/dev/null` from the desktop | none | [`shell-interpreter.md`](../dev/shell-interpreter.md#apps-lancées-par-leur-nom) |
| `IShellSession`: `GetVariable`, `AssignVariable`, `RunAsync`, `Jobs`, `KillJob`; `IHackShell.NeedsMoreInput` | Built-ins `read`, `watch`, `jobs`, `kill %n` written as ordinary commands; continuation prompt | `shell.control`; `shell.host` | [`shell-commands.md`](../dev/shell-commands.md), [`shell-host.md`](shell-host.md) |
| `IFullScreenTerminalSession` (`TerminalExecutionContext.FullScreen`), `TerminalInputReader` | Alternate screen (`nano`, `top`, `htop`) and the terminal's standard input | none | [`terminal-emulator.md`](../dev/terminal-emulator.md), [`terminal-api.md`](terminal-api.md) |
| `HackFile.CreateSymbolicLinkAsync` | `ln -s` | path rights | [`hack-file-api.md`](hack-file-api.md) |
| `AppDefinition.Components` (`AppComponent`), `HackEmbeddedComponent<T>`, `HackContractComponent<T>`, `HackEmbeddableComponent`, `HackComponentContracts`, `IHackTerminal` | Component of one app displayed by another, by contract (`hackeros.terminal`) | those of the app that displays the component (CodeEditor declares `shell.host` for the embedded terminal) | [`embedded-components.md`](embedded-components.md) |
| `HackClipboard` shared with the host (`BrowserClipboard` removed in 1.0) | Copied text goes to the real machine according to `clipboard.config` | `clipboard.read`, `clipboard.write` | [`clipboard.md`](../dev/clipboard.md) |

None of these APIs gives access to an internal service: the apps that
use them (Terminal, CodeEditor, Diagnostics, FileExplorer, `HackerOs.Commands`)
pass the boundary check without exception (see
[Final verification](#vérification-finale-de-la-phase-4-2026-10-16)).

## Decisions

- Codes `HOSAPP0030` and `HOSAPP0031` instead of `HOSAPP0002` and `HOSAPP0003`,
  already taken by ADR 0047 (ADR 0059).
- Exemption per project, not per reference: simpler to remove, and a
  work item fixes a whole project (history: the mechanism was removed
  by `4-sdk-boundary-final`).
- The warnings of an exempted project are not turned into errors:
  `TreatWarningsAsErrors` applies only to the C# compiler, not to MSBuild
  warnings.
- The injection check compares types by name (assembly, full name):
  the app's assembly is loaded in an isolated context.

- `HackAppInfo` adds `PublisherId` and `IsHidden` to ADR 0056's list:
  the "Installed Apps" tab displays the publisher, and a launcher must know
  that an app is hidden. `HackApps.OpenFileAsync` returns a result rather than
  nothing, because the explorer must offer a choice when several apps
  fit. The methods of `HackAppManagement` return the ids written.
- A child component that does not inherit from `HackComponentBase` calls the
  gateways of `AppContext` directly (`AppContext.Catalog`,
  `AppContext.AppManagement`): its event handlers are not in
  the ambient context. *Phase 5:* the Settings panels (`InstalledAppsPanel`,
  `AppPermissionsPanel`) now inherit from `HackComponentBase` and receive
  the context as a cascading value, like all components in `Apps/`
  ([`resource-accounting.md`](../dev/resource-accounting.md#apps-existantes)).

## Final verification of phase 4 (2026-10-16)

Rerun of `rebuild/4-sdk-boundary-final` on `rebuild/base` after PRs #115
to #122 (xterm.js, code editor, embeddable components, POSIX shell and
Linux commands, clipboard shared with the host):

- `dotnet build HackerOs.sln`: 0 warnings, 0 errors, no diagnostic
  `HOSAPP0002`, `HOSAPP0003`, `HOSAPP0030`, `HOSAPP0031` or `HOSSDK0001`.
- No `Apps/` project or `HackerOs.AppSdk*` library references
  `Platform.Core`, `Platform.Blazor`, `Infrastructure.*` or `Server.*`. No
  remnant of the exemption mechanism outside the documents' history.
- Injections in `Apps/`: only `IJSRuntime` (import of the app's
  module), `MudBlazor.IDialogService`, and SDK types.
  `SdkBoundarySourceTests` walks all `.csproj` files of `Apps/`, so also
  `HackerOs.Apps.Terminal` and the new commands.
- Capabilities: each app and command added or modified declares those of the APIs
  it calls (for example `systemctl`: `services.manage`; `htop`:
  `process.manage`; `jobs`, `read`, `watch`: `shell.control`; Terminal and
  CodeEditor: `shell.host`).
- No call to an `[Obsolete]` API in `Apps/`; since the SDK 1.0 freeze, the
  SDK no longer contains any (`BrowserClipboard`, `Get/SetFileReferencesAsync`
  removed, ADR 0114 §3).
- Fixed: the terminal's copy (Ctrl+C on a selection, Ctrl+Shift+C)
  wrote `navigator.clipboard` directly, outside the OS clipboard and
  without honoring `clipboard.config` (ADR 0065). It now raises a real
  `copy` event that the bridge receives as a native copy
  ([`terminal-emulator.md`](../dev/terminal-emulator.md)).

## Task list of work item `rebuild/4-sdk-boundary`

- [x] MSBuild target `ValidateHackerOsSdkBoundary` (`HOSAPP0030`, `HOSSDK0001`), direct references only.
- [x] Property `HackerOsSdkLibrary` for SDK libraries.
- [x] `SdkBoundaryInspector` in the generator (`HOSAPP0031`), `--boundary-exempt` option.
- [x] `HackerOsSdkBoundaryExemption` list (9 projects) and a test that keeps it from growing.
- [x] `IJSRuntime` source test in `HackerOs.Ecosystem.Tests`.
- [x] Removal of unused `Platform.Core` references (Calculator, HackPaint, IconViewer, TextEditor, Browser).
- [x] Test projects `ManifestGenerator.Fixtures.ForbiddenReference` and `ForbiddenInjection`, unit tests of the inspector.
- [x] ADR 0059 (code numbers), this document, `app-model.md`, plan and index up to date.

## Task list of work item `rebuild/4-sdk-user-and-apps`

- [x] `HackUser`, `HackUserInfo`, `HackEnvironment.GetFolderPath`, `HackSpecialFolder`.
- [x] `IAppExecutionContext`: `UserLoginName`, `UserDisplayName`, gateways `Catalog` and `AppManagement`.
- [x] `HackApps`, `HackAppInfo`, `IAppCatalogGateway` and `AppCatalogGateway`.
- [x] `HackAppManagement`, `IAppManagementGateway` and `AppManagementGateway` (`apps.manage`).
- [x] OS authentication agent, broker and window ([`admin-authentication-agent.md`](../dev/admin-authentication-agent.md)).
- [x] Migration of FileExplorer ("Open with", home folder), TextEditor and CodeEditor; FileExplorer loses its reference to `Platform.Core`.
- [x] "Installed Apps" tab of Settings on the SDK; `apps.manage` declared by Settings.
- [x] Exemptions removed: FileExplorer, TextEditor, CodeEditor; Settings remains for `4-sdk-system-admin`.
- [x] Tests: agent, gateways, `HackUser`, window.

## Task list of work item `rebuild/4-file-explorer`

- [x] `HackClipboard`, `HackClipboardFileList`, gateway `IAppExecutionContext.Clipboard`; `SystemClipboard` and `AppClipboardGateway` in Platform Core.
- [x] `HackDirectory.CopyAsync`, `FileTransfer` and the additions to `FileView` ([`file-explorer.md`](../dev/file-explorer.md)).
- [x] Explorer on these APIs; capabilities `clipboard.read`, `clipboard.write`, `notifications.post` declared. No exemption to remove (FileExplorer had already left the list).

## Task list of work item `rebuild/4-sdk-system-admin`

- [x] Capabilities `permissions.manage`, `server.connection.manage`, `system.power`, `system.storage.inspect` (dangerous).
- [x] `HackSettings.OpenSystemAsync`, `HackSettingsDocument.SetManyAsync`, `IAppSystemSettingsGateway`.
- [x] `HackPermissionAdministration`, `HackServerConnection`, `HackSystem`, `HackStorageDiagnostics` and their Platform Core gateways, with the authentication agent.
- [x] `SystemPowerService` (Platform Core) and browser device (`BrowserSystemPowerDevice`, `IndexedDbStorageInspector` in the Ecosystem host).
- [x] Settings and Diagnostics on the public SDK; exemptions removed (empty list).
- [x] Tests `SystemAdministrationGatewayTests`; ADR 0060; doc [`system-administration.md`](system-administration.md).

## Task list of work item `rebuild/4-sdk-boundary-final`

- [x] Verify that the exemption list is empty and that the solution compiles without any `HOSAPP0030`, `HOSAPP0031` or `HOSSDK0001` diagnostic.
- [x] Remove the mechanism from `build/HackerOs.Apps.targets` (item, target `_HackerOsResolveSdkBoundaryExemption`, property, warning).
- [x] Remove the generator's `--boundary-exempt` option and the `warning` severity of `GeneratorDiagnostic`.
- [x] Replace the list's tests with `Build_targets_have_no_exemption_mechanism` and `No_app_or_sdk_project_overrides_its_boundary_role`; the `IJSRuntime` test covers all apps.
- [x] This document, the plan (phase 4 checked) and the index up to date.

### Final verification (2026-10-16)

- [x] Solution build without any boundary diagnostic or warning; references of `Apps/` and the SDK verified.
- [x] No remnant of the exemption mechanism; `SdkBoundarySourceTests` covers the apps added since #113.
- [x] Injections and capabilities of the apps and commands added by PRs #115 to #122.
- [x] Uses of `[Obsolete]` APIs in `Apps/`: none.
- [x] Terminal copy routed through the clipboard bridge (ADR 0065), xterm bundle regenerated.
- [x] `Tests/*.Tests` suites other than E2E green; links in `docs/` verified by script; index and plan up to date.
