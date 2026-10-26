> [Version française](../../../fr/wasm/framework/sdk-packages.md)

# NuGet packages, SDK version and templates

## Purpose

Allow another repository to build HackerOS apps
**and** a complete OS host with its own apps, without copying this repository
([ADR 0098](../dev/adr/0098-sdk-versions-packages-and-manifest-checks.md) §1, §2, §6).
Everything published carries a single version, comes out as NuGet packages in a
**local** feed (`artifacts/local-nupkg`), and five `dotnet new` templates
create the projects. Since [ADR 0173](../dev/adr/0173-automatic-releases-from-the-version-file.md),
every version merged into `main` is also published to nuget.org and to the
GitHub Releases ([`releases.md`](../dev/releases.md)).

The step-by-step guide for a third-party repository (layout, host, tests, version
upgrade) is [`building-on-hackeros.md`](building-on-hackeros.md).

The checks done at load time (SDK version, duplicate commands,
resource fingerprints, ADR 0098 §3 to §5) are described in
[`app-model.md`](app-model.md#sdk-version-commands-integrity-adr-0098-3-5).

## Single version

- `build/Versions.props` defines `HackerOsVersion` (today `2.0.3`, Pages publishing of ADR 0167),
  imported by `Directory.Build.props`: each project of the solution carries this
  `Version`.
- Before the freeze: `0.9.0-preview.<n>`. Since the SDK 1.0 freeze (work item
  `v3/15`, [ADR 0114](../dev/adr/0114-sdk-1-0-freeze.md)): SemVer (ADR 0019),
  patch for no API change, minor for an addition, major for a
  breaking SDK change (see "SDK 1.0 freeze" and "SDK 2.0").
- `HackerOsSdk.Version` (`HackerOs.App.Abstractions`) is this version at
  runtime (assembly informational version, without `+sha` metadata).
  The generator writes it as the `sdkCompatibility.minimumVersion` of each app;
  `AppCatalog` refuses an app whose range does not accept the host's SDK (same
  major as the minimum, minimum ≤ SDK ≤ maximum).

## Packages

A project is published when it imports `Platform/Packaging.props`;
`Directory.Build.props` makes all the others non-packable. `Packaging.props`
brings the common metadata (author, license, repository, SourceLink, `.snupkg`
symbols, XML documentation) and the **public API analyzer**: each
library lists its surface in `PublicAPI.Shipped.txt` (1.0, frozen) and
`PublicAPI.Unshipped.txt` (what has changed since, see "SDK 1.0 freeze"). XML documentation warnings are errors
(`TreatWarningsAsErrors`), so every public member is documented.

| Packages | Content |
|---|---|
| `HackerOs.App.Abstractions`, `HackerOs.Simulation.Abstractions`, `HackerOs.Theming.Abstractions`, `HackerOs.AppSdk`, `.Blazor`, `.Terminal`, `.Web`, `.Web.Blazor`, `.Icons`, `.VirtualKeyboard` | the public SDK, what an app references (`.FileView` is no longer part of it since 2.0: its code is in the file explorer); `HackerOs.AppSdk` also carries the `HOSBG` analyzer for `HackBackground` in `analyzers/dotnet/cs/` (ADR 0140, [`background-jobs.md`](background-jobs.md)) |
| `HackerOs.App.Build` | the build integration (see below) |
| `HackerOs.Platform.Core`, `.Blazor`, `.ResourceAccounting`, `.WorkerHost`, `HackerOs.Infrastructure.Browser`, `HackerOs.Windowing.Abstractions`, `.Core`, `.Blazor`, `HackerOs.Taskbar.Blazor`, `HackerOs.Theming.Blazor`, `HackerOs.MobileShell.Blazor`, `HackerOs.Server.Contracts` | the platform, what a host composes (`Server.Contracts` is a dependency of `Platform.Core`) |
| `HackerOs.Ecosystem.Host` | the OS itself: desktop, startup, services, browser adapters, design tokens |
| `HackerOs.Apps.*` (13 apps), `HackerOs.Commands` | the base apps, each with its manifests |
| `HackerOs.Templates` | the `dotnet new` templates |

### Producing and consuming the local feed

```bash
build/pack-local.sh                 # ou build/pack-local.ps1 ; --output <dossier> pour un autre flux
```

The script builds a solution filter (`artifacts/packages.slnf`) of the
projects that import `Packaging.props`, runs `dotnet pack -c Release` into
`artifacts/local-nupkg`, then removes this version of each `HackerOs.*` package
from the global NuGet cache: without that, a new package of the same version would be
masked by the old one.

In the other repository, `build/nuget.config.sample` serves as a template: a
`hackeros-local` source pointing to this folder, nuget.org for the rest, and a
`packageSourceMapping` that only takes `HackerOs.*` from the local feed.

### `HackerOs.App.Build`

A package without an assembly (`Tools/HackerOs.App.Build`), to be referenced with
`PrivateAssets="all"` so that it does not propagate to the project's consumers:

| Path in the package | Role |
|---|---|
| `buildTransitive/HackerOs.App.Build.targets` | sets `HackerOsBuildFromPackage=true` and imports the next file |
| `hackeros-build/HackerOs.Apps.targets` | **the same file** as the repository's `build/HackerOs.Apps.targets` (outside `build/`, where NuGet only expects `HackerOs.App.Build.targets`) |
| `hackeros-build/asp-hack/` | `_ViewImports.cshtml` and compatibility types for ASP.Hack `.cshtml` views (ADR 0086) |
| `tools/generator/` | the manifest generator, launched by `dotnet <dll>` (without the native executable) |
| `analyzers/dotnet/cs/` | the ASP.Hack route generator (ADR 0082) |

In **package mode**, a project that references `HackerOs.App.Build` is an app,
unless it declares `HackerOsAppHost=true` (a host). Differences with the repository:

- the generator comes from the package instead of a `ProjectReference`;
- the boundary rule on `ProjectReference` (written in repository paths)
  does not apply; the one on packages does
  (`_ValidateHackerOsPackageBoundary`, `HOSAPP0030`, see
  [`app-sdk.md`](app-sdk.md#verification)): every compile reference
  coming from a `HackerOs.*` package, direct or transitive, must be allowed;
- injection inspection (`HOSAPP0031`), the `HOSAPP*` codes and the resource
  fingerprints are identical.

### App packages

When an app project is packaged, the `_HackerOsPackageAppManifests` target
(hooked onto `TargetsForTfmSpecificContentInPackage`) adds:

- its generated manifests under `hackeros/manifests/` and their translations
  (`localization/<culture>.json`, ADR 0104) under `hackeros/localization/` (added
  by `v3/14`: without them, a host using packages failed with `HOSAPP0040`);
- `buildTransitive/<PackageId>.targets`, which declares them as
  `HackerOsPackagedApp` items:

```xml
<HackerOsPackagedApp Include="$(MSBuildThisFileDirectory)../hackeros/manifests/manifest.json"
                     PackageId="HackerOs.Apps.Calculator"
                     AssemblyFileName="HackerOs.Apps.Calculator.dll"
                     AssemblyPath="$(MSBuildThisFileDirectory)../lib/net10.0/HackerOs.Apps.Calculator.dll" />
```

An app's `wwwroot/` files remain static web assets of the
package (`_content/<PackageId>/…`), where `StaticWebAssetAppAssetSource` reads them.

A host aggregates these items with its `ProjectReference`s
(`_HackerOsCollectPackagedAppManifests`): `IsSystemApp` comes from its
`PackageReference` to the package; an app package that arrived as a dependency is
installed too (like a package manager installs a dependency), but
never as a system app. Assemblies are loaded lazily like the
others.

## `HackerOs.Ecosystem.Host`

The `OS/HackerOs.Ecosystem` executable contained the whole OS. It is split in two:

| Project | Content |
|---|---|
| `OS/HackerOs.Ecosystem.Host` (Razor library, published) | `App.razor`, `LoginProgressScreen`, `EcosystemBootCoordinator`, `EcosystemHostState`, `AddHackerOsEcosystem`, `AddHackerOsResourceAccounting`, `AddHackerOsBrowserHost`, `HostAppCatalog`, WebAssembly adapters (lazy transport, environment), `Administration/`, `WebData/`, `wwwroot/css/app.css` |
| `OS/HackerOs.Ecosystem` (this repository's PWA) | `wwwroot/` (page, service worker, web manifest, icons), the app references, `BuildKnownLazyApps` and `BuildKnownLazyAssemblies` (facades over its `HostAppCatalog`), a three-line `Program.cs` |

The namespace remains `HackerOs.Ecosystem`: `HackerOs.Ecosystem.App` now lives
in the `HackerOs.Ecosystem.Host` assembly. `test/test` and the server
still reference the exe (for its aggregated catalog) and receive the
library transitively. The design tokens are served at
`_content/HackerOs.Ecosystem.Host/css/app.css` by the three hosts.

```csharp
// Program.cs d'un hôte WebAssembly
var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.AddHackerOsBrowserHost(HostAppCatalog.Load(typeof(Program).Assembly));
await builder.Build().RunAsync();
```

`AddHackerOsBrowserHost(apps, options)` registers the root component (only
if the `#app` element, `RootSelector`, exists: a WebAssembly island of a Blazor
Web App has none), the lazy loader limited to the catalog's assemblies, the
app resource source (`StaticWebAssetAppAssetSource` over
`_content/`), resource measurement with the catalog's images, all the
OS services and the Web Worker pool (`WorkerPreloadAssemblies`, by default
`HackerOs.Commands.dll` when the host has it).

The package is produced with both resource measurements compiled in
(ADR 0066); a host turns them off at runtime through `appsettings.json`.

## `dotnet new` templates

`Templates/HackerOs.Templates` (package `HackerOs.Templates`):

| Template | Creates | Packages |
|---|---|---|
| `hackeros-window` | windowed app (`WindowAppBase`, isolated CSS, `_Imports.razor`) | `HackerOs.AppSdk`, `.Blazor`, `HackerOs.App.Build` |
| `hackeros-command` | `hello` shell command (`TerminalAppBase`) | `HackerOs.AppSdk`, `HackerOs.App.Build` |
| `hackeros-service` | service started at sign-in (`ServiceAppBase`, simulated clock) | same |
| `hackeros-site` | ASP.Hack site (`WebAppDefinition`, embedded `wwwroot/`) | `HackerOs.AppSdk.Web`, `HackerOs.App.Build` |
| `hackeros-host` | Blazor WebAssembly host: `Program.cs`, `index.html`, `appsettings.json`, the base apps as packages (system apps as in the Ecosystem) | `HackerOs.Ecosystem.Host`, `HackerOs.App.Build`, `HackerOs.Apps.*`, `HackerOs.Commands` |

App templates start with `IsTrimmable=true`; `hackeros-host` publishes as a
trimmed Release (`PublishTrimmed=true`, `TrimMode=partial`) and
`HackerOs.App.Build` keeps the `HackerOs.*` packages and each installed
app whole, and enables trimming analysis in an app project that
does not set it (ADR 0106 §4, ADR 0113, [`trimmed-publish.md`](../dev/trimmed-publish.md)).

App template parameters: `--appId` (default `com.example.my-app`) and
`--publisherId` (default `pub.example`); the project name (`-n`) becomes
the namespace and the display name.

Each generated project declares `<HackerOsVersion>` once and uses it for
all its `HackerOs.*` references. In the template sources, the value is
`0.0.0-dev`; packaging copies `content/` into `obj/` and writes the
repository's version there (`XmlPoke`), so that a generated project references exactly the packages
packaged with it.

The entry points of the command and service templates have the
`(AppManifest manifest)` constructor through which the OS starts an app, plus a
parameterless constructor for unit tests.

## SDK 1.0 freeze

[ADR 0098](../dev/adr/0098-sdk-versions-packages-and-manifest-checks.md) §8 and
[ADR 0114](../dev/adr/0114-sdk-1-0-freeze.md):

- The whole surface of the 39 libraries (38 since 2.0) is in `PublicAPI.Shipped.txt`;
  each `PublicAPI.Unshipped.txt` only contains `#nullable enable`.
- After the freeze, an addition goes into `Unshipped` (`RS0016`, the fix
  `dotnet format analyzers <csproj> --diagnostics RS0016 --severity info`
  writes it); a removal or a change is refused (`RS0017`) until
  `*REMOVED*<line>` is written in `Unshipped`. A release merges
  `Unshipped` into `Shipped` and chooses the version according to its content.
- **Promise**: an app compiled against `1.x` runs on any OS `1.y`
  (y ≥ x). It covers what an app compiles against: the three published
  `*.Abstractions`, `HackerOs.AppSdk*`, `HackerOs.Theming.Blazor`, the build contract of
  `HackerOs.App.Build` and the manifest format. The platform, the host and the
  public types of the base apps follow the same version **without** this
  promise: a minor can break a host there (never an app), and each
  break is listed below.
- The three `[Obsolete]` APIs (`BrowserClipboard`,
  `IAppClipboardGateway.Get/SetFileReferencesAsync`) were removed before the
  freeze: 1.0 contains none.
- An app compiled as `0.9.0-preview.*` is refused by OS 1.0 (different
  major): it must be rebuilt.

### Breaks outside the SDK

Public API breaks of the platform, the host or the base apps, per
version (`*REMOVED*` lines merged into `Shipped` on release). No
app is affected: these types are not part of the SDK.

| Version | Package | Break | What to do |
|---|---|---|---|
| 1.1.0 | `HackerOs.Platform.Blazor` | `WindowGeometryPersistence.RestoreAsync` returns `SavedWindowGeometry?` instead of `WindowBounds?`; `SaveAsync` takes `SavedWindowGeometry` instead of `(WindowBounds, isEligible)` (ADR 0116, 0122: only the size and maximized state are remembered) | go through `SavedWindowGeometry`, or through `WindowGeometryRecorder` |
| 1.1.0 | `HackerOs.Platform.Blazor` | `WindowGeometrySettings.Parse` returns `SavedWindowGeometry?`; `Serialize(WindowBounds)` becomes `Serialize(appId, SavedWindowGeometry)` | adapt the call |
| 1.1.0 | `HackerOs.Platform.Blazor` | `WindowLaunchCoordinator.Present(...)` (two overloads) replaced by `PresentAsync(...)`; the constructor now takes the geometry recorder | call `PresentAsync`; let dependency injection build the coordinator (`AddHackerOsBrowserHost`) |
| 1.2.0 | `HackerOs.Ecosystem.Host` | the `EcosystemBootCoordinator` constructor takes three more optional parameters (`OomKiller? oomKiller`, `IOsSettingsService? settings`, `CrossOriginIsolationProbe? isolation`, ADR 0126, 0131): binary break, the source code still compiles | recompile the host; let dependency injection build the coordinator (`AddHackerOsEcosystem`) |
| 2.0.0 | `HackerOs.Ecosystem.Host` | no longer registers `IShellIconProvider` (ADR 0161 §1) | nothing: the file control keeps its default icons |
| 2.0.0 | `HackerOs.Apps.FileExplorer` | the types of the old `FileView` are in `HackerOs.Apps.FileExplorer.FileControl` and derive from `HackComponentBase`; `FileConflictDialog`, `FilePropertiesDialog`, `OpenWithDialog`, `FileExplorerDialogBase`, `PublishSiteContextMenuProvider`, `ZipFileContextMenuProvider` and `FileExplorerState.ViewMode` are removed (desk/3 to desk/5) | go through the `hackeros.file-view` and `hackeros.file-dialogs` contracts |
| 2.0.0 | `HackerOs.Apps.CodeEditor` | `CodeEditorExplorer` loses `OnDeleteRequested`, `OwnerAppId`, `Watch` and `Dispose`; the public constructor of `CodeEditorWorkspace` is removed (desk/3) | use `HackFileView` |

## SDK 1.1

Published by the work item `v3.1/4-sdk-1-1` ([ADR 0117](../dev/adr/0117-sdk-window-title-and-sdk-1-1.md) §5):
each `PublicAPI.Unshipped.txt` is merged into its `PublicAPI.Shipped.txt`
(sorted lines, `*REMOVED*` applied) and `HackerOsVersion` becomes `1.1.0`.
SDK additions, all compatible with a 1.0 app (new interface members
have a default implementation):

| Library | Addition | Decision |
|---|---|---|
| `HackerOs.AppSdk.Blazor` | `WindowAppBase.SetWindowTitleAsync`, `WindowTitle`, `FormatWindowTitle`, `IAppWindowGateway` | ADR 0117, 0123 |
| `HackerOs.App.Abstractions` | `WindowOptions.RememberGeometry`; capability `users.manage` | ADR 0116, 0122; ADR 0120 |
| `HackerOs.AppSdk` | `HackUserAccounts`, `HackUserAccount`, `IAppUserAccountsGateway`, `IAppExecutionContext.UserAccounts`, `ITerminalInputMode`, `TerminalExecutionContext.ReadSecretLineAsync` | ADR 0120 |
| `HackerOs.AppSdk` | `HackApps.GetDefaultHandlersAsync`, `SetDefaultHandlerAsync`, `ResetDefaultHandlerAsync`, `HackFileAssociation`, `HackFileAssociationLayer`, `HackDefaultHandlerStatus` | ADR 0121 |
| `HackerOs.AppSdk.Terminal` | `TerminalInputReader.EchoEnabled` | ADR 0120 |
| `HackerOs.Simulation.Abstractions` | `ILocalUserRepository.SetCredentialAsync` | ADR 0120 |

An app rebuilt against 1.1.0 carries `sdkCompatibility.minimumVersion:
1.1.0` and is no longer admitted by an OS 1.0. The compatibility app compiled as
1.0.0 still loads and runs on OS 1.1 (see below).

## SDK 1.2

Published only once, at the end of the `exec` series, by the work item
`exec/10-final` ([ADR 0132](../dev/adr/0132-exec-series-sdk-1-2-and-merge-rules.md) §2,
Benoit's answer E4 A): `build/tools/ship-public-api.sh` merges each
`PublicAPI.Unshipped.txt` into its `PublicAPI.Shipped.txt` (795 lines, sorted
in ordinal order, `*REMOVED*` applied) and `HackerOsVersion` becomes `1.2.0`.
SDK additions, all compatible with a 1.0 or 1.1 app (each new interface
member has a default implementation, each new enumeration value takes a number at the end):

| Library | Addition | Decision |
|---|---|---|
| `HackerOs.AppSdk` | `HackWireContextAttribute` (`[assembly: HackWireContext(typeof(...))]`), for the event and watch gateways called from a worker | ADR 0124, 0138 |
| `HackerOs.AppSdk` | `HackProcess.GetResourceLimitsAsync`, `SetResourceLimitAsync`, `HackResourceLimits`, `HackResourceLimitEnforcement`, `HackProcessKilledException.Reason` and its constructor with a reason | ADR 0125, 0139 |
| `HackerOs.AppSdk` | `HackProcess.Nice`, `BasePriority`, `ParseStatNice`; `IShellSession.RunNicedAsync` | ADR 0127 |
| `HackerOs.AppSdk` | `HackBackground.RunAsync` (five overloads), `HackBackgroundJob<TInput, TResult>`, `HackBackgroundContext`, `HackBackgroundOptions`, `HackBackgroundProgress`, `HackBackgroundJobException`, `IAppBackgroundGateway`, `IAppExecutionContext.Background`; analyzer `HOSBG0001` to `HOSBG0004` in the package | ADR 0128, 0140 |
| `HackerOs.App.Abstractions` | `AppDefinition.OomScoreAdjust`, `AppManifest.OomScoreAdj` (manifest `oomScoreAdj`) | ADR 0126, 0142 |
| `HackerOs.Simulation.Abstractions` | `ResourceLimit`, `ResourceLimitKind`, `ProcessResourceLimits`, `ResourceLimitChangeResult`, `ProcessLimitExceededException`, `IProcessResourceLimitTable`, `IProcessFileDescriptorTable`, `ProcessFileDescriptor`, `NoDescriptor`, `IAppFileSystemGateway.OpenDescriptorAsync`, `IAppProcessGateway.SetResourceLimitAsync`, `ProcessStartRequest.ResourceLimits` and `ResourceLimitExempt`, `FileSystemErrorCode.TooManyOpenFiles`, `ProcessExitReason.CpuLimitExceeded`, `MemoryLimitExceeded`, `OutOfMemory` | ADR 0125, 0126, 0139 |
| `HackerOs.Simulation.Abstractions` | `ProcessNice`, `IProcessManager.GetNice`/`SetNice`, `IAppProcessGateway.GetNice`/`SetNice`, `ProcessStartRequest.Nice` and `OomScoreAdjust` | ADR 0126, 0127 |
| `HackerOs.Simulation.Abstractions` | `WorkerSnapshot.RuntimeHeapBytes`, `IWebWorkerMemoryMeter`, `ISynchronousWebWorker`, `SynchronousAnswerOutcome` | ADR 0131, 0142, 0143 |

The other published libraries (platform, host, server, base apps)
receive the series' additions without the SDK promise; the only break is
listed in "Breaks outside the SDK". An app rebuilt against 1.2.0 carries
`sdkCompatibility.minimumVersion: 1.2.0` and is no longer admitted by an OS 1.0 or
1.1. The compatibility apps compiled as 1.0.0 and 1.1.0 load and
run on OS 1.2 (see below).

```bash
build/tools/ship-public-api.sh --check   # ce qu'une publication verserait, sans rien changer
build/tools/ship-public-api.sh           # à la publication, puis HackerOsVersion dans build/Versions.props
```

No 1.3 was published: the additions that were waiting for it (`PublishAsync`,
ADR 0145; `HackCrossOriginIsolation`, ADR 0146) went into 2.0.

## SDK 2.0

Published by the work item `desk/6-sdk-2`, at the end of the desktop and context menu
series ([ADR 0149](../dev/adr/0149-sdk-2-0-file-view-removal.md),
[ADR 0161](../dev/adr/0161-sdk-2-0-implementation-adjustments.md); question Q5 of the
[plan](../dev/plans/desktop-and-context-menu-plan.md), Benoit's choice "SDK 2.0
now"): `build/tools/ship-public-api.sh` merges 1561 lines of
`PublicAPI.Unshipped.txt` into `PublicAPI.Shipped.txt` and `HackerOsVersion`
becomes `2.0.0`.

### Break: `HackerOs.AppSdk.FileView` leaves the SDK

The `HackerOs.AppSdk.FileView` package no longer exists. Its code (view, tree,
details, icons, breadcrumb, drop zone, `IFileViewContextMenuProvider` menus,
`ShellIcon`, `IShellIconProvider`) is the internal implementation of the file
explorer (`Apps/System/HackerOs.Apps.FileExplorer/FileControl/`), which provides
the control to other apps through the `hackeros.file-view` contract (ADR 0148). The
other SDK libraries lose no member: the major comes from this single removal.

An app compiled against 1.x carries `sdkCompatibility.minimumVersion: 1.x`. A
host that references it no longer builds (`HOSAPP0021`, the same rule as the
catalog applied at build time); an OS 2.0 that finds it in its catalog refuses it
at startup (`catalog.sdk.incompatible`), does not load it, and Settings ▸
Installed Apps shows it as "Incompatible". It must be recompiled
against the 2.x packages (2.0.3 today).

### Migrating a 1.x app

| In 1.x | In 2.0 |
|---|---|
| `<FileView …/>` (`HackerOs.AppSdk.FileView`) | `<HackFileView InitialDirectory="…" …/>` (`HackerOs.AppSdk.Blazor.Files`) and its controller `IHackFileView` ([`file-explorer.md`](../dev/file-explorer.md)) |
| `IFileViewContextMenuProvider`, `FileViewMenuItemCollection` | `ContextMenuOpening` of `HackFileView`, or an entry registered by `[RegisterFileContextMenu]` ([`context-menu.md`](context-menu.md)) |
| `FileTransfer.CopyAsync` / `MoveAsync` / `TransferAsync` / `ImportAsync` / `MoveToTrashAsync` / `GetCopyName` | the same methods of `HackFileOperations` (`HackerOs.AppSdk.IO`) |
| `FileTransferResult`, `FileTransferMode`, `FileConflictChoice`, `FileConflictResolution`, `FileTransferConflict`, `FileConflictResolver`, `FileTransferFailure`, `FileTransferItemResult`, `FileTransferHostFile` | `HackFileTransferResult`, `HackFileTransferMode`, `HackFileConflictChoice`, `HackFileConflictResolution`, `HackFileTransferConflict`, `HackFileConflictResolver`, `HackFileTransferFailure`, `HackFileTransferItemResult`, `HackHostFile` |
| a conflict resolver written by the app | `Dialogs.AsFileConflictResolver(AppContext)`: the "Replace / Keep both / Skip" dialog of the `hackeros.file-dialogs` provider |
| `ShellIcon`, `ShellIconDescriptor` | `HackIcons.Vector(…)` / `HackIcons.FromImage(…)` (`HackerOs.AppSdk.Icons`) to describe an icon; rendering belongs to the OS |

Then set `HackerOsVersion` to `2.0.3` (or the latest published 2.0.x) and recompile.

### Additions

All the other SDK changes are additions:

| Library | Addition | Decision |
|---|---|---|
| `HackerOs.AppSdk.Blazor` | `HackContractComponent<TController>`, `HackComponentResolution`: host of a contract provided by an app | ADR 0148, 0153 |
| `HackerOs.AppSdk.Blazor` | `HackContextMenu`, `IHackContextMenu`, `HackMenus` (`Menus.Attach`, `Menus.Define`, `OpenAsync`), `HackMenuBuilder`, `HackMenuDefinition`, `HackMenuItem`, `HackMenuGroup`, `HackMenuOpeningEventArgs`, `IHackMenuHost` | ADR 0150, 0154 |
| `HackerOs.AppSdk.Blazor` | `HackFileView`, `IHackFileView`, `HackFileViewItem`, `HackFileViewMode`, `HackFileViewFeatures`, their events, `HackFileMenuOpeningEventArgs`, `HackFileMenuContext` | ADR 0148, 0155, 0160 |
| `HackerOs.AppSdk.Blazor` | `IDialogService.OpenWithAsync`, `ShowFilePropertiesAsync`, `ResolveFileConflictAsync` and their types, `HackFileDialogs`, `HackFileOperationDialogs.AsFileConflictResolver` | ADR 0155, 0157, 0161 |
| `HackerOs.AppSdk.Blazor` | `[RegisterFileContextMenu]`, `RegisterFileContextMenuContext` | ADR 0151, 0159 |
| `HackerOs.AppSdk` | `HackFileOperations` and its types (`HackFileTransferResult`..., `HackHostFile`) | ADR 0161 §2 |
| `HackerOs.AppSdk` | `HackFileMenuItems`, `FileMenuScope`, `MenuSelection`, `HackFileMenuIntent`, `HackFileMenuEntries`, `HackFileMenuEntryInfo` | ADR 0151, 0159 |
| `HackerOs.AppSdk` | `HackComponentProviders`, `HackComponentContractInfo`, `HackComponentProviderInfo` (provider choice by the administrator) | ADR 0156, 0158 |
| `HackerOs.AppSdk` | `HackDesktopSettings`, `HackDesktopSettingsDocument`, `HackDesktopOptions`, `HackDesktopLayout`, `HackDesktopCell`, `HackDesktopEntry`, `HackDesktopItems`, `HackDesktopSort`, `HackDesktopIconSize`, `HackDesktopArrangement`, `HackAppIcons` | ADR 0152, 0160 |
| `HackerOs.AppSdk` | `HackCrossOriginIsolation` and its types, `IAppCrossOriginIsolationGateway` | ADR 0146 |
| `HackerOs.AppSdk.Icons` | `HackIcons` (`Vector`, `FromImage`), `HackIcon`, `HackIconKind` | ADR 0150, 0151 |
| `HackerOs.App.Abstractions` | `AppManifest.ContextMenus`, `AppContextMenuEntryManifest` and its titles and icons, `AppContextMenus`, `AppComponentContracts`, `AppComponentProviderScope`, capability `shell.context-menu` | ADR 0151, 0153, 0156 |
| `HackerOs.Simulation.Abstractions` | `IAppEventGateway.PublishAsync` | ADR 0145 |

### Additions since 2.0.0 (next minor)

These additions from the work item `exec/5` (which arrived through `main` after the 2.0 cut)
are in `PublicAPI.Unshipped.txt`; they will go out with the next
release (2.1.0, a minor according to SemVer since they are additions).
`HackerOsVersion` stays `2.0.3` until then.

| Library | Addition | Decision |
|---|---|---|
| `HackerOs.Simulation.Abstractions` | `ProcessStartRequest.ParentExitGracePeriod`: delay of a child after its parent ends (stop request, then kill) | ADR 0147 |
| `HackerOs.Platform.Core` | `BackgroundJobService.ParentExitGracePeriod` (5 s) | ADR 0147 |

### Compatibility app

`Tests/fixtures/sdk-compat/packages` keeps one app per published version that
received one, each compiled **once** by `produce-fixture.sh`, outside the
repository, against the packages of its version, and **never rebuilt**:

| Package | Compiled against | Content |
|---|---|---|
| `Contoso.Sdk1Compat.1.0.0.nupkg` | 1.0.0 (`v3/15`) | window `com.contoso.sdk1-window` (saves and rereads a file), command `sdk1-compat` |
| `Contoso.Sdk11Compat.1.1.0.nupkg` | 1.1.0, packages from the top of `v3.1/base` (`exec/10-final`, ADR 0132 §1) | window `com.contoso.sdk11-window` that names itself with `SetWindowTitleAsync` and `FormatWindowTitle` (`note.txt — SDK 1.1 Window`), `rememberGeometry: false`; command `sdk11-compat` that also calls `HackUserAccounts.ValidateNewPassword` |

| `Contoso.Sdk2Compat.2.0.0.nupkg` | 2.0.0, packages from the top of `desk/6-sdk-2` (ADR 0161 §3) | window `com.contoso.sdk2-window` that displays the OS file control (`HackFileView`) and registers the entry "Inspect with SDK 2.0" in the file menu (`[RegisterFileContextMenu]`, intent `com.contoso.sdk2.inspect.v1`); command `sdk2-compat` that copies a file through `HackFileOperations.CopyAsync` (`sdk2-compat (copy).txt`) |

The tests load the current major's app (2.0.0) into the OS; those of
1.x remain as proof of the **refusal** of an app from another major
(ADR 0149 §5).

```bash
build/pack-local.sh
Tests/fixtures/sdk-compat/produce-fixture.sh --project Contoso.Sdk2Compat   # contre le flux 2.0.0
```

## Tests

| Where | What |
|---|---|
| `Tests/HackerOs.Templates.E2E.Tests` | packages everything (`pack-local`, or the `HACKEROS_LOCAL_FEED` feed), installs the templates in an isolated `DOTNET_CLI_HOME`, generates and compiles each app template outside the repository with an isolated NuGet cache (manifest, kind, minimum SDK version checked); then generates a host, adds a generated windowed app and a command to it, compiles it (aggregated catalog checked), launches it, creates the profile in Chromium, opens the app (`Clicked 0 times` then `1`) and runs `hello Morpheus` in the terminal (`Hello, Morpheus!`). |
| `Tests/HackerOs.Templates.E2E.Tests` (`A_host_runs_SDK_2_app_packages_and_refuses_SDK_1_x_packages`) | a generated host with `PackageReference` to the 1.0.0 and 1.1.0 compatibility apps (source `hackeros-sdk-fixtures`) does not build (`HOSAPP0021` for each of the four apps); with the 2.0.0 app instead, it starts in Chromium, the 2.0 window displays the OS file control and `sdk2-compat still binds` runs in the terminal (ADR 0149 §5, ADR 0161 §3) |
| `Tests/HackerOs.SdkCompatibility.Tests` | the 2.0.0 compatibility app: admitted by the current catalog, menu entry registered and `HackFileView` referenced, every HackerOS type and member it references exists, every method compiles against the current SDK (JIT), the command runs; the 1.0.0 and 1.1.0 apps: refused (`catalog.sdk.incompatible`) (ADR 0149 §5, ADR 0161 §3) |
| `Tests/HackerOs.App.Abstractions.Tests` (`HackerOsSdkTests`) | version reading, range compatibility |
| `Tests/HackerOs.Tools.ManifestGenerator.Tests` | SDK version read from the reference, fingerprints and `HOSAPP0023`, `HOSAPP0022`, app newer than the host's SDK (`HOSAPP0021`) |
| `Tests/HackerOs.Platform.Core.Tests` | `AppCatalog` (refusal, dependency cascade, command owner), `AppAssetStore`, `ShellCommandResolver` |
| `build/verify-hosts.sh` | the three hosts start after the extraction of `HackerOs.Ecosystem.Host` |

Also checked by hand: an app that adds the reference
`HackerOs.Platform.Core` fails with `HOSAPP0030 Forbidden package`.

## Decisions

- **A single version** for everything, rather than a version per package: the SDK, the
  platform and the apps evolve together; since the freeze, the SemVer promise
  only covers the SDK (ADR 0114 §2).
- **The same `HackerOs.Apps.targets`** in the repository and in the package, with a
  `HackerOsBuildFromPackage` switch, so that no rule diverges.
- **Package boundary checked on resolved references** (direct and
  transitive), not only on the project's `PackageReference`s: an intermediate
  library cannot bring the platform into an app.
- **App packages carry their manifests**: a host never needs an app's
  source code to catalog it, like `dpkg` reads the control of a
  `.deb`.
- **NuGet cache purged by `pack-local`** and isolated cache in the test: a
  repackaged `preview` version is never served stale.
- **`HostAppCatalog` in the library**, the aggregated catalog in the host's
  assembly: the library does not know a host's apps.

## Tasks

- [x] `build/Versions.props`, version carried by each project, `Packaging.props` with no fixed version.
- [x] 37 libraries published with metadata, public API analyzer and generated `PublicAPI.*.txt` lists.
- [x] XML documentation of every public member (≈ 300 warnings fixed, none suppressed).
- [x] `HackerOsSdk`, minimum written by the generator, refusal at load, "incompatible" state in Settings.
- [x] `HOSAPP0022` and single owner of a command at load.
- [x] Resource fingerprints (`HOSAPP0023`, `AppAssetStore`, "damaged" state).
- [x] Removal of `BuildProfileManifest`, its validator and its serializer.
- [x] `HackerOs.App.Build` package and package mode of `HackerOs.Apps.targets` (package boundary, aggregation of apps as packages, manifests in app packages).
- [x] `HackerOs.Ecosystem.Host` extracted, `AddHackerOsBrowserHost`, `HostAppCatalog`, design tokens moved, three hosts updated.
- [x] `build/pack-local.sh` / `.ps1`, `build/nuget.config.sample`.
- [x] Five templates and their end-to-end test.
- [x] SDK 1.0 freeze (`v3/15`, ADR 0114): everything in `Shipped`, version `1.0.0`, obsolete APIs removed, compatibility app and its tests, guide [`building-on-hackeros.md`](building-on-hackeros.md).
- [x] SDK 1.1 (`v3.1/4-sdk-1-1`, ADR 0117 §5): `Unshipped` merged into `Shipped`, version `1.1.0`, breaks outside the SDK listed, 1.0 compatibility app still loaded.
- [x] SDK 1.2 (`exec/10-final`, ADR 0132 §2): `build/tools/ship-public-api.sh`, `Unshipped` merged into `Shipped`, version `1.2.0`, break outside the SDK listed, 1.1.0 compatibility app added, 1.0.0 and 1.1.0 loaded.
- [x] SDK 2.0 (`desk/6-sdk-2`, ADR 0149, ADR 0161): `HackerOs.AppSdk.FileView` removed from the SDK (code in the explorer), `HackFileOperations`, `Unshipped` merged into `Shipped`, version `2.0.0`, breaks outside the SDK listed, 2.0.0 compatibility app added, 1.x apps refused.
- [x] Documentation (this file, `app-model`, `app-sdk`, `sdk-reference`, `sdk-developer-guide`, `building-and-testing`, `project-overview`, resource measurement, notes of archived documents) and index.
