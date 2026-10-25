> [Version française](../../../fr/wasm/dev/project-overview.md)

# HackerOS project overview

## Purpose

HackerOS simulates a Linux-like operating system in the browser,
to serve as an environment in which to develop applications. **No game in this repository** ([ADR 0135](adr/0135-no-game-in-this-repository.md)). It is
the C# and Blazor WebAssembly port of the original JavaScript simulator
(`src/`, kept read-only as a behavior reference).

This document is the entry point for understanding the code of `wasm2/HackerOs`:
the solution's projects, their layers, the hosts, what happens at
startup and the rules that apply at build. To build and run the
project, see [`building-and-testing.md`](building-and-testing.md). To write
an app, see [`sdk-developer-guide.md`](../framework/sdk-developer-guide.md).

Two principles guide everything else
([`ecosystem-rebuild-plan.md`](plans/ecosystem-rebuild-plan.md), section 1):

1. **For the app developer**, adding an app is simple: the file API
   looks like `System.IO` (`HackFile`, `HackDirectory`…), the manifest is a
   C# class, and a single `ProjectReference` installs the app.
2. **For the user**, the OS behaves like a real Linux: everything is a
   file (settings in `/etc` and `~/.config`, logs in `/var/log`,
   processes in `/proc`), the shell is complete, the base apps are rich.

## Layers

Dependencies always go from top to bottom. An app sees only the SDK.

```text
 Hôtes            OS/HackerOs.Ecosystem (PWA)   test/test (débogage)   Server/HackerOs.Server
                         │
 Apps             Apps/System/*   Apps/Commands/HackerOs.Commands   Apps/Samples/*
                         │   (SDK public uniquement, vérifié au build : ADR 0055)
 SDK              Shared/HackerOs.AppSdk*  (Hack*, IAppExecutionContext, composants)
                         │
 Contrats         Shared/HackerOs.App.Abstractions   Shared/HackerOs.Simulation.Abstractions
                  Shared/HackerOs.Theming.Abstractions
                         │
 Plateforme       Platform/HackerOs.Platform.Core (services de l'OS, sans interface)
                  Platform/HackerOs.Platform.Blazor (bureau, fenêtres, dialogues)
                  Platform/HackerOs.Windowing.*, Taskbar.Blazor, MobileShell.Blazor, Theming.Blazor
                         │
 Infrastructure   Infrastructure/HackerOs.Infrastructure.Browser (IndexedDB, interop navigateur)
```

The platform implements the contracts; the SDK knows only their
interfaces. Apps receive an `IAppExecutionContext` built by the
platform and never reference `Platform.*` or `Infrastructure.*`
(errors `HOSAPP0030`, `HOSAPP0031`, `HOSSDK0001`, see
[`app-sdk.md`](../framework/app-sdk.md)).

## Solution projects

`HackerOs.sln` groups all the projects below (.NET 10, `global.json`).

### Contracts (`Shared/`)

| Project | Content |
|---|---|
| `HackerOs.App.Abstractions` | App model: `AppDefinition` and `[AppManifest]`, `AppManifest` and its validator, `AppKind`, capability catalog `AppCapabilities`, intents, platforms, canonical serialization of manifests. |
| `HackerOs.Simulation.Abstractions` | Contracts of the simulated system: file system (`VirtualPath`, requests and results), app gateways (`IAppFileSystemGateway`, `IAppEventGateway`, `IAppLoggingGateway`…), processes, sessions, settings, network, notifications, clock, metrics. |
| `HackerOs.Theming.Abstractions` | Theme contracts (tokens, appearance). |

### App SDK (`Shared/HackerOs.AppSdk*`)

| Project | Content |
|---|---|
| `HackerOs.AppSdk` | `Hack*` facades (files, environment, settings, network, processes, clipboard, administration…), `IAppExecutionContext`, base classes `TerminalAppBase`, `ServiceAppBase` and `WebServerAppBase`. |
| `HackerOs.AppSdk.Blazor` | `WindowAppBase`, `HackComponentBase`, dialogs (files, message, input, "Open with", properties, name conflict), `HackLineChart` charts, browser download and clipboard; contracts provided by the OS at runtime (`HackContractComponent<T>`, `HackFileView`), OS context menu (`Menus.Attach`) and `[RegisterFileContextMenu]` (SDK 2.0). |
| `HackerOs.AppSdk.Icons` | Icon libraries and `HackerIcon` component. |
| `HackerOs.AppSdk.Terminal` | xterm.js terminal emulator (`TerminalPanel`, `TerminalTabs`). |
| `HackerOs.AppSdk.Web` | ASP.Hack: internal sites in the style of ASP.NET Core MVC (pipeline, routing, controllers, session; [`asp-hack-mvc.md`](../framework/asp-hack-mvc.md)). |
| `HackerOs.AppSdk.Web.Blazor` | Interactive Blazor sites: router, navigation, site view drawn by the browser ([`blazor-sites.md`](../framework/blazor-sites.md)). |
| `HackerOs.AppSdk.VirtualKeyboard` | Virtual keyboard for mobile. |

The detail of each API is in the [SDK reference](../framework/sdk-reference.md).

### Platform (`Platform/`)

| Project | Content |
|---|---|
| `HackerOs.Platform.Core` | OS services, with no graphical interface: app catalog, lifecycle and processes, file system and mounts (`/proc`, `/dev`), file-based settings, logs, policy and capabilities, elevation (`sudo`), sessions, `hsh` shell and its interpreter, simulated network, clipboard, notifications, clock, metrics. The SDK gateways are implemented there ([`platform-core.md`](platform-core.md): folder map). |
| `HackerOs.Platform.ResourceAccounting` | Optional per-process CPU and memory measurement (ADR 0066 to 0068): context-switch hook on the ambient frame, broadcaster to the measurements, switches. Depends on Platform Core; a host references it only if `HackerOsCpuAccounting` or `HackerOsMemoryAccounting` is `true` ([`resource-accounting.md`](resource-accounting.md)). |
| `HackerOs.Platform.WorkerHost` | Processes without an interface in Web Workers (ADR 0070, 0071): on the OS side, the worker pool (one per virtual core, ready worker, overflow, dedicated workers), the sessions that serve calls under the same capability checks, the heartbeat and escalation; on the worker side, the .NET runtime that runs the command or service with remote gateways. Referenced by the Ecosystem host in the browser only ([`worker-processes.md`](worker-processes.md)). |
| `HackerOs.Platform.RemoteRendering` | Trial of ADR 0129, verdict "we stop here" (ADR 0133): a Blazor window rendered by a `Renderer` in a worker, its batches translated into DOM operations applied by `RemoteWindowHost` on the page. Off by default (`HackerOs:RemoteWindowRendering`); no app must depend on it ([`remote-window-rendering.md`](remote-window-rendering.md)). |
| `HackerOs.Platform.Blazor` | OS interface: desktop, Start menu, window host (`WindowAppRenderer`), dialogs, approval window for dangerous capabilities, authentication agent, lazy loading of apps. |
| `HackerOs.Windowing.Abstractions`, `.Core`, `.Blazor` | Window manager (state, moving, resizing, snapping). |
| `HackerOs.Taskbar.Blazor` | Taskbar. |
| `HackerOs.MobileShell.Blazor` | Mobile interface. |
| `HackerOs.Theming.Blazor` | Theme application. |

### Infrastructure (`Infrastructure/`)

`HackerOs.Infrastructure.Browser`: IndexedDB persistence (files in deduplicated
blocks, legacy settings, catalog, sessions), backup and
restore, connection to the optional server, browser interop, startup of
Web Workers (`workerHost.js`, `workerBootstrap.js`). Everything that touches the
browser is here ([`browser-infrastructure.md`](browser-infrastructure.md)).

### Apps (`Apps/`)

| Folder | Content |
|---|---|
| `Apps/System/` | The 15 base window apps: Desktop (hidden surface rendered by the shell under the windows, [`desktop.md`](desktop.md)), Terminal, File Explorer, Text Editor, Code Editor, Settings ([`settings-app.md`](settings-app.md)), System Monitor (task manager), Error Log Viewer, Diagnostic, Browser, Calculator, HackPaint ([`hack-paint.md`](hack-paint.md)), Theme Editor ([`theme-editor.md`](theme-editor.md)), Icon Viewer, Web Server Manager (with the `hackwebd` service, [`static-sites.md`](static-sites.md)). |
| `Apps/Commands/HackerOs.Commands` | The 74 shell commands in a single project (`ls`, `cat`, `cd`, `cp`, `mv`, `rm`, `grep`, `nano`, `sudo`, `ping`, `curl`, `nmap`, and since ADR 0064 `sort`, `cut`, `zip`, `top`, `htop`, `systemctl`, `ip`, `test`, `read`…: [`linux-commands.md`](linux-commands.md)), one `[AppManifest]` class per command (ADR 0051); they run in the Web Worker pool except the 18 marked `Execution = AppExecutionHost.MainThread` (ADR 0070). |
| `Apps/Samples/` | Sample apps, one per kind (window, terminal, service, platform, Blazor site, full ASP.Hack site); they are not installed in the host, except the two sites `HackerOs.Samples.BlazorSite` (`http://blazor-sample.hack/`, ADR 0084) and `HackerOs.Samples.Intranet` (`http://intranet.hack/`, ADR 0087, [ASP.Hack guide](../framework/asp-hack-developer-guide.md)), hidden apps installed so that the browser can serve them. |

An app is **system** when the host declares it so on its `ProjectReference`
(`IsSystemApp="true"`): it cannot be uninstalled and automatically receives its
dangerous capabilities. List and rules: [`app-model.md`](../framework/app-model.md#system-apps-adr-0048-1).

### Hosts

| Project | Role |
|---|---|
| `OS/HackerOs.Ecosystem` | **The shipped host**: standalone Blazor WebAssembly PWA (`wwwroot/index.html`, service worker), no server. It references `HackerOs.Ecosystem.Host` and the apps, aggregates their manifests into `BuiltInApplications.json` at build, and its `Program.cs` calls `AddHackerOsBrowserHost`. Everything must work with it alone, offline. |
| `OS/HackerOs.Ecosystem.Host` | **The OS itself**, as a Razor library published as a package (ADR 0098 §2): desktop (`App.razor`), progress screen, `EcosystemBootCoordinator`, service composition (`AddHackerOsEcosystem`, `AddHackerOsBrowserHost`), resource measurement, browser adapters, design tokens (`_content/HackerOs.Ecosystem.Host/css/app.css`), `HostAppCatalog`. A host from another repository references it ([`sdk-packages.md`](../framework/sdk-packages.md)). |
| `test/test` | Debug Blazor Web App: renders the same component tree in `InteractiveWebAssembly` to benefit from the debugger. Not a distribution target. |
| `Server/HackerOs.Server` | **Optional** ASP.NET Core server: synchronization, accounts, HTTP/TCP/UDP relay to the real network ([`server-network-relay.md`](server-network-relay.md), policy and quotas in `appsettings.json`). It also serves the Ecosystem's OS as is, run in WebAssembly in the browser like the PWA; this OS connects to it on its own and shows the state in the notification area (ADR 0130, 0134, [`server-hosting.md`](server-hosting.md)). No app must depend on it. |
| `Server/HackerOs.Server.Contracts` | Contracts shared between the server and the client. |

More details on the hosts in [`old/hosting-model.md`](old/hosting-model.md)
(predates the plan, still accurate on this point).

### Tools, samples and tests

| Folder | Content |
|---|---|
| `Tools/HackerOs.Tools.ManifestGenerator` | Generates `manifest.json` from each app's `[AppManifest]` class, checks the SDK boundary and aggregates `BuiltInApplications.json` in the host ([`app-model.md`](../framework/app-model.md)). |
| `Tools/HackerOs.Tools.WebRouteGenerator` | Roslyn source generator wired into each app: route table of ASP.Hack controllers and site entry, `HOSWEB*` errors (ADR 0082, [`asp-hack-mvc.md`](../framework/asp-hack-mvc.md)). |
| `Tools/HackerOs.App.Build` | Build integration package for another repository: `build/HackerOs.Apps.targets`, manifest generator, route generator ([`sdk-packages.md`](../framework/sdk-packages.md)). |
| `Templates/HackerOs.Templates` | `dotnet new` templates: `hackeros-window`, `-command`, `-service`, `-site`, `-host` (ADR 0098 §6). |
| `Tools/HackerOs.Tools.BackgroundAnalyzer` | Roslyn analyzer for calls to `HackBackground.RunAsync`: checks each call against the rules for background tasks (ADR 0128). |
| `Tools/icon-import` | Node script for importing icon libraries. |
| `Samples/HackerOs.Windowing.SampleHost` | Sample host for the window manager alone. |
| `Tests/` | One xUnit test project per code project, MSBuild integration tests of the generator, Playwright E2E suites (`HackerOs.E2E.Tests`, `HackerOs.UI.E2E.Tests`, `HackerOs.Pwa.E2E.Tests`), the startup test of the three hosts (`HackerOs.HostSmoke.Tests`, launched by `build/verify-hosts.sh`), the trimmed publication test (`HackerOs.TrimmedPublish.E2E.Tests`, launched by `build/publish-trimmed.sh`), the templates test (`HackerOs.Templates.E2E.Tests`) and the SDK compatibility test (`HackerOs.SdkCompatibility.Tests`, compatibility apps kept in `Tests/fixtures/sdk-compat`: 1.0.0 and 1.1.0 refused, 2.0.0 admitted; ADR 0114, ADR 0149). `HackerOs.Tests.Support` provides fake contexts and gateways; `HackerOs.E2E.Support`, the Chromium launch shared by the Playwright suites. |
| `build/HackerOs.Apps.targets` | MSBuild targets of the app model (generation, aggregation, SDK boundary, manifest packaging). |
| `build/Versions.props`, `build/pack-local.sh`/`.ps1` | Single version of everything published and production of local packages in `artifacts/local-nupkg` ([`sdk-packages.md`](../framework/sdk-packages.md)). |
| `build/HackerOs.ResourceAccounting.targets` | Properties `HackerOsCpuAccounting` and `HackerOsMemoryAccounting` ([`resource-accounting.md`](resource-accounting.md)). |
| `build/asp-hack/`, `build/tools/ship-public-api.sh`, `build/nuget.config.sample`, `build/run-test-suite.ps1`, `build/assemble-pages.ps1` | ASP.Hack generators and checks (`HackRazorCompat.cs`, `_ViewImports.cshtml`), publication of the SDK's public surface, `nuget.config` template for another repository, full test suite, assembly of the Pages site (ADR 0167). |

## The simulated system

### File system

A single Unix tree, persisted in IndexedDB, with owners, groups,
mode bits, symbolic links, optimistic revisions and atomic writes.
The locations to know:

| Path | Content |
|---|---|
| `/home/<login>` | Home folder; `~/.config/<appId>/` app settings, `~/.local/share/Trash` trash, `~/.local/share/keyrings/login.keyring` encrypted keyring (ADR 0100). |
| `/root` | `root`'s folder (processes launched by `sudo`). |
| `/etc/hackeros/` | OS configuration: `apps.config`, `security.config`, `app-permissions.config`, `appearance.config`, `policy.config`, `ui-platform-preference.config`, `file-associations.json`; `themes/` (machine-wide `.theme` themes, ADR 0103). |
| `/etc/<appId>/` | System layer of an app's settings, with the `[locked]` section; sensitive keys in `<doc>.private.config` (`0600`, ADR 0100). |
| `/etc/environment`, `/etc/hostname` | Login environment and machine name. |
| `/var/log/` | `syslog`, `auth.log`, `audit.log` (writes under `/etc`, `/bin`, `/var`, ADR 0100), `apps/<appId>.log`, with rotation. |
| `/proc/` | Virtual: processes, `stat`, `diskstats`, `net/dev`, `mounts`… |
| `/dev/` | Virtual: `null`, `zero`, `random`, `urandom`. |

Documents: [`hack-file-api.md`](../framework/hack-file-api.md),
[`settings-files.md`](../framework/settings-files.md), [`logging.md`](logging.md),
[`virtual-proc-and-dev.md`](virtual-proc-and-dev.md),
[`trash.md`](trash.md).

### Processes, shell and capabilities

- Each launched app is a **process** (identifier, parent, arguments,
  current directory, environment variables, I/O counters),
  visible in `/proc` ([`process-environment.md`](process-environment.md),
  [`system-metrics.md`](system-metrics.md)).
- The `hsh` shell is a child process of the terminal; it handles pipes,
  redirections, `&&`, variables and `$?`. Each command is itself a
  process ([`shell-interpreter.md`](shell-interpreter.md),
  [`shell-host.md`](../framework/shell-host.md), [`shell-commands.md`](shell-commands.md)).
- In the browser, commands and services run in **Web Workers** (one per
  virtual core, plus a ready worker): an infinite loop no longer freezes the OS,
  `kill -9` terminates the worker
  ([`worker-processes.md`](worker-processes.md), `/proc/hackeros/workers`).
- An app declares **capabilities** in its definition. Normal capabilities
  are granted at login; a **dangerous** capability of a non-system app
  requires an administrator's approval
  ([`capabilities.md`](../framework/capabilities.md),
  [`dangerous-capability-approval.md`](dangerous-capability-approval.md)).
- `sudo` and the OS authentication agent have an administrator confirm
  without ever showing the password to the app
  ([`admin-authentication-agent.md`](admin-authentication-agent.md)).

### Startup

1. The host's `Program.cs` calls `AddHackerOsBrowserHost`, which registers the
   services (`AddHackerOsEcosystem`) and the catalog read from
   `BuiltInApplications.json` (`HostAppCatalog`, `BuildKnownLazyApps` in the Ecosystem).
2. `EcosystemBootCoordinator` mounts storage, attaches the log writer
   to `/var/log`, installs `/etc/hackeros/apps.config`, starts the simulated
   clock and the metrics sampling, then validates policy, sessions and
   catalog.
3. On first startup, the screen asks to create the local administrator
   profile; afterwards, it offers login.
4. At login, the OS grants capabilities, starts the `AutoStart`
   services and displays the desktop. App DLLs are loaded on
   demand, at first launch (`BlazorWebAssemblyLazyLoad`).

## Rules checked at build

| Rule | Where | Document |
|---|---|---|
| Warnings treated as errors, trimming compatibility analysis | `Directory.Build.props` | n/a |
| Trimmed Release publication of the host: roots generated from the manifests, whole `HackerOs.*` assemblies (ADR 0106, 0113) | `build/HackerOs.Apps.targets`, tested by `build/publish-trimmed.sh` | [`trimmed-publish.md`](trimmed-publish.md) |
| No inline CSS or JavaScript in a `.razor` (ADR 0007) | target `ValidateHackerOsRazorAssets`, `Directory.Build.targets` | [`AGENTS.md`](../../../../AGENTS.md) |
| Each project under `Apps/` has a valid `[AppManifest]` class; `manifest.json` is regenerated (`HOSAPP0001`…) | `build/HackerOs.Apps.targets` | [`app-model.md`](../framework/app-model.md) |
| An app uses only the SDK (`HOSAPP0030`, `HOSAPP0031`, `HOSSDK0001`) | `build/HackerOs.Apps.targets` and generator | [`app-sdk.md`](../framework/app-sdk.md) |
| The SDK's public surface does not change without a trace: `PublicAPI.Shipped.txt`/`Unshipped.txt` (RS0016, RS0017); an app compiled against the current major (2.x) is admitted, a 1.x app refused (ADR 0114, ADR 0149) | public API analyzer, `Tests/HackerOs.SdkCompatibility.Tests` | [`sdk-packages.md`](../framework/sdk-packages.md) |

## Work organization

- New code goes **only** in `wasm2/HackerOs/`; `src/` is read-only
  ([`AGENTS.md`](../../../../AGENTS.md)).
- The direction is set by [`ecosystem-rebuild-plan.md`](plans/ecosystem-rebuild-plan.md);
  architecture decisions are ADRs in [`adr/`](adr/), never
  modified after acceptance.
- Documents are written in French in `doc/fr/` and translated to English in
  `doc/en/` (same folders and file names, in English); every change updates
  both languages. Any addition or renaming of a document updates the index
  [`README.md`](../README.md) in the same change.
- Each series of work items has its integration branch: `rebuild/base`
  (phases 1 to 4, merged into `main`), `web/base` (ASP.Hack and Blazor sites),
  then `v3/base` (end of the wasm v3 migration, started from `web/base`), `v3.1/base`
  and `exec/base` (follow-ups of phase 5, started from `v3.1/base`). Each
  work item starts from its series' branch (`v3/<n>-<subject>`…) and returns to it;
  only the repository owner merges into `main`. On 2026-10-18, at
  Benoit's request, `exec/base` (which contains all the other series) was
  merged into `main`: `main` now carries all the work.
- State: the wasm v3 migration is finished, the SDK was frozen at 1.0.0
  (ADR 0114), published as 1.1.0 by series v3.1 (ADR 0117 §5), as 1.2.0
  at the end of the `exec` series (ADR 0132 §2) then as **2.0.0** at the end of the
  desktop and context menu series (`desk/base`: file control
  provided by the explorer, OS context menu, app entries, desktop
  icons; ADR 0149, 0161, [`sdk-packages.md`](../framework/sdk-packages.md#sdk-20),
  report in [`desktop-and-context-menu-plan.md`](plans/desktop-and-context-menu-plan.md#13-final-verification)); the migration status report is in
  [`wasm-v3-final-status.md`](plans/wasm-v3-final-status.md), that of the `exec` series
  (limits, OOM killer, priorities, background tasks, Server host in WebAssembly,
  cross-origin isolation) in
  [`execution-followups-plan.md`](plans/execution-followups-plan.md#11-final-verification). The published version is **2.0.3** (`build/Versions.props`, published on
  2026-10-24); another repository can build on SDK 2.x
  ([`building-on-hackeros.md`](../framework/building-on-hackeros.md)).

## Tasks of this document

- [x] Describe the layers and the dependency rule.
- [x] List each project of the solution and its role.
- [x] Summarize the simulated system (files, processes, shell, capabilities) with links to the detailed documents.
- [x] Describe the startup sequence.
- [x] List the rules checked at build.
- [x] Final review of the v3 migration (`v3/16-final`): integration branches and project state.
- [x] Final review of the `exec` series (`exec/10-final`): SDK 1.2, integration branches.
- [x] Final review of the desktop series (`desk/6-sdk-2`): SDK 2.0, `FileView` outside the SDK, compatibility rule.
