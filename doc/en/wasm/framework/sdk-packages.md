> [Version française](../../../fr/wasm/framework/sdk-packages.md)

# NuGet packages, SDK version and templates

## Purpose

Allow another repository to build Kazo OS apps
**and** a complete OS host with its own apps, without copying this repository
([ADR 0098](../dev/adr/0098-sdk-versions-packages-and-manifest-checks.md) §1, §2, §6).
Everything published carries a single version, comes out as NuGet packages in a
**local** feed (`artifacts/local-nupkg`), and five `dotnet new` templates
create the projects. Since [ADR 0173](../dev/adr/0173-automatic-releases-from-the-version-file.md),
every version merged into `main` is also published to nuget.org and to the
GitHub Releases ([`releases.md`](../dev/releases.md)).

The step-by-step guide for a third-party repository (layout, host, tests, version
upgrade) is [`building-on-kazoos.md`](building-on-kazoos.md).

The checks done at load time (SDK version, duplicate commands,
resource fingerprints, ADR 0098 §3 to §5) are described in
[`app-model.md`](app-model.md#sdk-version-commands-integrity-adr-0098-3-5).

## Single version

- `build/Versions.props` defines `KazoOsVersion` (today `2.2.2`; `2.1.0` was the first automatic release of ADR 0173),
  imported by `Directory.Build.props`: each project of the solution carries this
  `Version`.
- Before the freeze: `0.9.0-preview.<n>`. Since the SDK 1.0 freeze (work item
  `v3/15`, [ADR 0114](../dev/adr/0114-sdk-1-0-freeze.md)): SemVer (ADR 0019),
  patch for no API change, minor for an addition, major for a
  breaking SDK change (see "SDK 1.0 freeze" and "SDK 2.0").
- `KazoOsSdk.Version` (`Kazo.Os.App.Abstractions`) is this version at
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
| `Kazo.Os.App.Abstractions`, `Kazo.Os.Simulation.Abstractions`, `Kazo.Os.Theming.Abstractions`, `Kazo.Os.AppSdk`, `.Blazor`, `.Terminal`, `.Web`, `.Web.Blazor`, `.Icons`, `.VirtualKeyboard` | the public SDK, what an app references (`.FileView` is no longer part of it since 2.0: its code is in the file explorer); `Kazo.Os.AppSdk` also carries the `KOSBG` analyzer for `KazoBackground` in `analyzers/dotnet/cs/` (ADR 0140, [`background-jobs.md`](background-jobs.md)) |
| `Kazo.Os.App.Build` | the build integration (see below) |
| `Kazo.Os.Platform.Core`, `.Blazor`, `.ResourceAccounting`, `.WorkerHost`, `Kazo.Os.Infrastructure.Browser`, `Kazo.Os.Windowing.Abstractions`, `.Core`, `.Blazor`, `Kazo.Os.Taskbar.Blazor`, `Kazo.Os.Theming.Blazor`, `Kazo.Os.MobileShell.Blazor`, `Kazo.Os.Server.Contracts` | the platform, what a host composes (`Server.Contracts` is a dependency of `Platform.Core`) |
| `Kazo.Os.Ecosystem.Host` | the OS itself: desktop, startup, services, browser adapters, design tokens |
| `Kazo.Os.Apps.*` (13 apps), `Kazo.Os.Commands` | the base apps, each with its manifests |
| `Kazo.Os.Templates` | the `dotnet new` templates |

### Producing and consuming the local feed

```bash
build/pack-local.sh                 # ou build/pack-local.ps1 ; --output <dossier> pour un autre flux
```

The script builds a solution filter (`artifacts/packages.slnf`) of the
projects that import `Packaging.props`, runs `dotnet pack -c Release` into
`artifacts/local-nupkg`, then removes this version of each `Kazo.Os.*` package
from the global NuGet cache: without that, a new package of the same version would be
masked by the old one.

In the other repository, `build/nuget.config.sample` serves as a template: a
`kazoos-local` source pointing to this folder, nuget.org for the rest, and a
`packageSourceMapping` that only takes `Kazo.Os.*` from the local feed.

### `Kazo.Os.App.Build`

A package without an assembly (`Tools/Kazo.Os.App.Build`), to be referenced with
`PrivateAssets="all"` so that it does not propagate to the project's consumers:

| Path in the package | Role |
|---|---|
| `buildTransitive/Kazo.Os.App.Build.targets` | sets `KazoOsBuildFromPackage=true` and imports the next file |
| `kazoos-build/Kazo.Os.Apps.targets` | **the same file** as the repository's `build/Kazo.Os.Apps.targets` (outside `build/`, where NuGet only expects `Kazo.Os.App.Build.targets`) |
| `kazoos-build/asp-kazo/` | `_ViewImports.cshtml` and compatibility types for ASP.Kazo `.cshtml` views (ADR 0086) |
| `tools/generator/` | the manifest generator, launched by `dotnet <dll>` (without the native executable) |
| `analyzers/dotnet/cs/` | the ASP.Kazo route generator (ADR 0082) |

In **package mode**, a project that references `Kazo.Os.App.Build` is an app,
unless it declares `KazoOsAppHost=true` (a host). Differences with the repository:

- the generator comes from the package instead of a `ProjectReference`;
- the boundary rule on `ProjectReference` (written in repository paths)
  does not apply; the one on packages does
  (`_ValidateKazoOsPackageBoundary`, `KOSAPP0030`, see
  [`app-sdk.md`](app-sdk.md#verification)): every compile reference
  coming from a `Kazo.Os.*` package, direct or transitive, must be allowed;
- injection inspection (`KOSAPP0031`), the `KOSAPP*` codes and the resource
  fingerprints are identical.

### App packages

When an app project is packaged, the `_KazoOsPackageAppManifests` target
(hooked onto `TargetsForTfmSpecificContentInPackage`) adds:

- its generated manifests under `kazoos/manifests/` and their translations
  (`localization/<culture>.json`, ADR 0104) under `kazoos/localization/` (added
  by `v3/14`: without them, a host using packages failed with `KOSAPP0040`);
- `buildTransitive/<PackageId>.targets`, which declares them as
  `KazoOsPackagedApp` items:

```xml
<KazoOsPackagedApp Include="$(MSBuildThisFileDirectory)../kazoos/manifests/manifest.json"
                     PackageId="Kazo.Os.Apps.Calculator"
                     AssemblyFileName="Kazo.Os.Apps.Calculator.dll"
                     AssemblyPath="$(MSBuildThisFileDirectory)../lib/net10.0/Kazo.Os.Apps.Calculator.dll" />
```

An app's `wwwroot/` files remain static web assets of the
package (`_content/<PackageId>/…`), where `StaticWebAssetAppAssetSource` reads them.

A host aggregates these items with its `ProjectReference`s
(`_KazoOsCollectPackagedAppManifests`): `IsSystemApp` comes from its
`PackageReference` to the package; an app package that arrived as a dependency is
installed too (like a package manager installs a dependency), but
never as a system app. Assemblies are loaded lazily like the
others.

## `Kazo.Os.Ecosystem.Host`

The `OS/Kazo.Os.Ecosystem` executable contained the whole OS. It is split in two:

| Project | Content |
|---|---|
| `OS/Kazo.Os.Ecosystem.Host` (Razor library, published) | `App.razor`, `LoginProgressScreen`, `EcosystemBootCoordinator`, `EcosystemHostState`, `AddKazoOsEcosystem`, `AddKazoOsResourceAccounting`, `AddKazoOsBrowserHost`, `HostAppCatalog`, WebAssembly adapters (lazy transport, environment), `Administration/`, `WebData/`, `wwwroot/css/app.css` |
| `OS/Kazo.Os.Ecosystem` (this repository's PWA) | `wwwroot/` (page, service worker, web manifest, icons), the app references, `BuildKnownLazyApps` and `BuildKnownLazyAssemblies` (facades over its `HostAppCatalog`), a three-line `Program.cs` |

The namespace remains `Kazo.Os.Ecosystem`: `Kazo.Os.Ecosystem.App` now lives
in the `Kazo.Os.Ecosystem.Host` assembly. `test/test` and the server
still reference the exe (for its aggregated catalog) and receive the
library transitively. The design tokens are served at
`_content/Kazo.Os.Ecosystem.Host/css/app.css` by the three hosts.

```csharp
// Program.cs d'un hôte WebAssembly
var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.AddKazoOsBrowserHost(HostAppCatalog.Load(typeof(Program).Assembly));
await builder.Build().RunAsync();
```

`AddKazoOsBrowserHost(apps, options)` registers the root component (only
if the `#app` element, `RootSelector`, exists: a WebAssembly island of a Blazor
Web App has none), the lazy loader limited to the catalog's assemblies, the
app resource source (`StaticWebAssetAppAssetSource` over
`_content/`), resource measurement with the catalog's images, all the
OS services and the Web Worker pool (`WorkerPreloadAssemblies`, by default
`Kazo.Os.Commands.dll` when the host has it).

The package is produced with both resource measurements compiled in
(ADR 0066); a host turns them off at runtime through `appsettings.json`.

## `dotnet new` templates

`Templates/Kazo.Os.Templates` (package `Kazo.Os.Templates`):

| Template | Creates | Packages |
|---|---|---|
| `kazoos-window` | windowed app (`WindowAppBase`, isolated CSS, `_Imports.razor`) | `Kazo.Os.AppSdk`, `.Blazor`, `Kazo.Os.App.Build` |
| `kazoos-command` | `hello` shell command (`TerminalAppBase`) | `Kazo.Os.AppSdk`, `Kazo.Os.App.Build` |
| `kazoos-service` | service started at sign-in (`ServiceAppBase`, simulated clock) | same |
| `kazoos-site` | ASP.Kazo site (`WebAppDefinition`, embedded `wwwroot/`) | `Kazo.Os.AppSdk.Web`, `Kazo.Os.App.Build` |
| `kazoos-host` | Blazor WebAssembly host: `Program.cs`, `index.html`, `appsettings.json`, the base apps as packages (system apps as in the Ecosystem) | `Kazo.Os.Ecosystem.Host`, `Kazo.Os.App.Build`, `Kazo.Os.Apps.*`, `Kazo.Os.Commands` |
| `kazoos-solution` | a whole app repository: app, host, tests, Kazo OS submodule (sources in `Debug`, packages in `Release`), CI, publishing to the Kazo OS Store ([`app-repository.md`](app-repository.md), ADR 0207) | those of the app and the host, at the submodule's version |

App templates start with `IsTrimmable=true`; `kazoos-host` publishes as a
trimmed Release (`PublishTrimmed=true`, `TrimMode=partial`) and
`Kazo.Os.App.Build` keeps the `Kazo.Os.*` packages and each installed
app whole, and enables trimming analysis in an app project that
does not set it (ADR 0106 §4, ADR 0113, [`trimmed-publish.md`](../dev/trimmed-publish.md)).

App template parameters: `--appId` (default `com.example.my-app`) and
`--publisherId` (default `pub.example`); the project name (`-n`) becomes
the namespace and the display name. `kazoos-solution` also takes
`--repository` (`owner/name`). Every template has a
`.template.config/ide.host.json` (icon, parameter labels) for Visual Studio and
Rider (ADR 0176 §7), and the app templates declare `KazoOsAppProject=true`
(ADR 0207 §4).

Each generated project declares `<KazoOsVersion>` once and uses it for
all its `Kazo.Os.*` references. In the template sources, the value is
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
  `*.Abstractions`, `Kazo.Os.AppSdk*`, `Kazo.Os.Theming.Blazor`, the build contract of
  `Kazo.Os.App.Build` and the manifest format. The platform, the host and the
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
| 1.1.0 | `Kazo.Os.Platform.Blazor` | `WindowGeometryPersistence.RestoreAsync` returns `SavedWindowGeometry?` instead of `WindowBounds?`; `SaveAsync` takes `SavedWindowGeometry` instead of `(WindowBounds, isEligible)` (ADR 0116, 0122: only the size and maximized state are remembered) | go through `SavedWindowGeometry`, or through `WindowGeometryRecorder` |
| 1.1.0 | `Kazo.Os.Platform.Blazor` | `WindowGeometrySettings.Parse` returns `SavedWindowGeometry?`; `Serialize(WindowBounds)` becomes `Serialize(appId, SavedWindowGeometry)` | adapt the call |
| 1.1.0 | `Kazo.Os.Platform.Blazor` | `WindowLaunchCoordinator.Present(...)` (two overloads) replaced by `PresentAsync(...)`; the constructor now takes the geometry recorder | call `PresentAsync`; let dependency injection build the coordinator (`AddKazoOsBrowserHost`) |
| 1.2.0 | `Kazo.Os.Ecosystem.Host` | the `EcosystemBootCoordinator` constructor takes three more optional parameters (`OomKiller? oomKiller`, `IOsSettingsService? settings`, `CrossOriginIsolationProbe? isolation`, ADR 0126, 0131): binary break, the source code still compiles | recompile the host; let dependency injection build the coordinator (`AddKazoOsEcosystem`) |
| 2.0.0 | `Kazo.Os.Ecosystem.Host` | no longer registers `IShellIconProvider` (ADR 0161 §1) | nothing: the file control keeps its default icons |
| 2.0.0 | `Kazo.Os.Apps.FileExplorer` | the types of the old `FileView` are in `Kazo.Os.Apps.FileExplorer.FileControl` and derive from `KazoComponentBase`; `FileConflictDialog`, `FilePropertiesDialog`, `OpenWithDialog`, `FileExplorerDialogBase`, `PublishSiteContextMenuProvider`, `ZipFileContextMenuProvider` and `FileExplorerState.ViewMode` are removed (desk/3 to desk/5) | go through the `kazoos.file-view` and `kazoos.file-dialogs` contracts |
| 2.0.0 | `Kazo.Os.Apps.CodeEditor` | `CodeEditorExplorer` loses `OnDeleteRequested`, `OwnerAppId`, `Watch` and `Dispose`; the public constructor of `CodeEditorWorkspace` is removed (desk/3) | use `KazoFileView` |

## SDK 1.1

Published by the work item `v3.1/4-sdk-1-1` ([ADR 0117](../dev/adr/0117-sdk-window-title-and-sdk-1-1.md) §5):
each `PublicAPI.Unshipped.txt` is merged into its `PublicAPI.Shipped.txt`
(sorted lines, `*REMOVED*` applied) and `KazoOsVersion` becomes `1.1.0`.
SDK additions, all compatible with a 1.0 app (new interface members
have a default implementation):

| Library | Addition | Decision |
|---|---|---|
| `Kazo.Os.AppSdk.Blazor` | `WindowAppBase.SetWindowTitleAsync`, `WindowTitle`, `FormatWindowTitle`, `IAppWindowGateway` | ADR 0117, 0123 |
| `Kazo.Os.App.Abstractions` | `WindowOptions.RememberGeometry`; capability `users.manage` | ADR 0116, 0122; ADR 0120 |
| `Kazo.Os.AppSdk` | `KazoUserAccounts`, `KazoUserAccount`, `IAppUserAccountsGateway`, `IAppExecutionContext.UserAccounts`, `ITerminalInputMode`, `TerminalExecutionContext.ReadSecretLineAsync` | ADR 0120 |
| `Kazo.Os.AppSdk` | `KazoApps.GetDefaultHandlersAsync`, `SetDefaultHandlerAsync`, `ResetDefaultHandlerAsync`, `KazoFileAssociation`, `KazoFileAssociationLayer`, `KazoDefaultHandlerStatus` | ADR 0121 |
| `Kazo.Os.AppSdk.Terminal` | `TerminalInputReader.EchoEnabled` | ADR 0120 |
| `Kazo.Os.Simulation.Abstractions` | `ILocalUserRepository.SetCredentialAsync` | ADR 0120 |

An app rebuilt against 1.1.0 carries `sdkCompatibility.minimumVersion:
1.1.0` and is no longer admitted by an OS 1.0. The compatibility app compiled as
1.0.0 still loads and runs on OS 1.1 (see below).

## SDK 1.2

Published only once, at the end of the `exec` series, by the work item
`exec/10-final` ([ADR 0132](../dev/adr/0132-exec-series-sdk-1-2-and-merge-rules.md) §2,
Benoit's answer E4 A): `build/tools/ship-public-api.sh` merges each
`PublicAPI.Unshipped.txt` into its `PublicAPI.Shipped.txt` (795 lines, sorted
in ordinal order, `*REMOVED*` applied) and `KazoOsVersion` becomes `1.2.0`.
SDK additions, all compatible with a 1.0 or 1.1 app (each new interface
member has a default implementation, each new enumeration value takes a number at the end):

| Library | Addition | Decision |
|---|---|---|
| `Kazo.Os.AppSdk` | `KazoWireContextAttribute` (`[assembly: KazoWireContext(typeof(...))]`), for the event and watch gateways called from a worker | ADR 0124, 0138 |
| `Kazo.Os.AppSdk` | `KazoProcess.GetResourceLimitsAsync`, `SetResourceLimitAsync`, `KazoResourceLimits`, `KazoResourceLimitEnforcement`, `KazoProcessKilledException.Reason` and its constructor with a reason | ADR 0125, 0139 |
| `Kazo.Os.AppSdk` | `KazoProcess.Nice`, `BasePriority`, `ParseStatNice`; `IShellSession.RunNicedAsync` | ADR 0127 |
| `Kazo.Os.AppSdk` | `KazoBackground.RunAsync` (five overloads), `KazoBackgroundJob<TInput, TResult>`, `KazoBackgroundContext`, `KazoBackgroundOptions`, `KazoBackgroundProgress`, `KazoBackgroundJobException`, `IAppBackgroundGateway`, `IAppExecutionContext.Background`; analyzer `KOSBG0001` to `KOSBG0004` in the package | ADR 0128, 0140 |
| `Kazo.Os.App.Abstractions` | `AppDefinition.OomScoreAdjust`, `AppManifest.OomScoreAdj` (manifest `oomScoreAdj`) | ADR 0126, 0142 |
| `Kazo.Os.Simulation.Abstractions` | `ResourceLimit`, `ResourceLimitKind`, `ProcessResourceLimits`, `ResourceLimitChangeResult`, `ProcessLimitExceededException`, `IProcessResourceLimitTable`, `IProcessFileDescriptorTable`, `ProcessFileDescriptor`, `NoDescriptor`, `IAppFileSystemGateway.OpenDescriptorAsync`, `IAppProcessGateway.SetResourceLimitAsync`, `ProcessStartRequest.ResourceLimits` and `ResourceLimitExempt`, `FileSystemErrorCode.TooManyOpenFiles`, `ProcessExitReason.CpuLimitExceeded`, `MemoryLimitExceeded`, `OutOfMemory` | ADR 0125, 0126, 0139 |
| `Kazo.Os.Simulation.Abstractions` | `ProcessNice`, `IProcessManager.GetNice`/`SetNice`, `IAppProcessGateway.GetNice`/`SetNice`, `ProcessStartRequest.Nice` and `OomScoreAdjust` | ADR 0126, 0127 |
| `Kazo.Os.Simulation.Abstractions` | `WorkerSnapshot.RuntimeHeapBytes`, `IWebWorkerMemoryMeter`, `ISynchronousWebWorker`, `SynchronousAnswerOutcome` | ADR 0131, 0142, 0143 |

The other published libraries (platform, host, server, base apps)
receive the series' additions without the SDK promise; the only break is
listed in "Breaks outside the SDK". An app rebuilt against 1.2.0 carries
`sdkCompatibility.minimumVersion: 1.2.0` and is no longer admitted by an OS 1.0 or
1.1. The compatibility apps compiled as 1.0.0 and 1.1.0 load and
run on OS 1.2 (see below).

```bash
build/tools/ship-public-api.sh --check   # ce qu'une publication verserait, sans rien changer
build/tools/ship-public-api.sh           # à la publication, puis KazoOsVersion dans build/Versions.props
```

No 1.3 was published: the additions that were waiting for it (`PublishAsync`,
ADR 0145; `KazoCrossOriginIsolation`, ADR 0146) went into 2.0.

## SDK 2.0

Published by the work item `desk/6-sdk-2`, at the end of the desktop and context menu
series ([ADR 0149](../dev/adr/0149-sdk-2-0-file-view-removal.md),
[ADR 0161](../dev/adr/0161-sdk-2-0-implementation-adjustments.md); question Q5 of the
[plan](../dev/plans/desktop-and-context-menu-plan.md), Benoit's choice "SDK 2.0
now"): `build/tools/ship-public-api.sh` merges 1561 lines of
`PublicAPI.Unshipped.txt` into `PublicAPI.Shipped.txt` and `KazoOsVersion`
becomes `2.0.0`.

### Break: `Kazo.Os.AppSdk.FileView` leaves the SDK

The `Kazo.Os.AppSdk.FileView` package no longer exists. Its code (view, tree,
details, icons, breadcrumb, drop zone, `IFileViewContextMenuProvider` menus,
`ShellIcon`, `IShellIconProvider`) is the internal implementation of the file
explorer (`Apps/System/Kazo.Os.Apps.FileExplorer/FileControl/`), which provides
the control to other apps through the `kazoos.file-view` contract (ADR 0148). The
other SDK libraries lose no member: the major comes from this single removal.

An app compiled against 1.x carries `sdkCompatibility.minimumVersion: 1.x`. A
host that references it no longer builds (`KOSAPP0021`, the same rule as the
catalog applied at build time); an OS 2.0 that finds it in its catalog refuses it
at startup (`catalog.sdk.incompatible`), does not load it, and Settings ▸
Installed Apps shows it as "Incompatible". It must be recompiled
against the 2.x packages (2.2.2 today).

### Migrating a 1.x app

| In 1.x | In 2.0 |
|---|---|
| `<FileView …/>` (`Kazo.Os.AppSdk.FileView`) | `<KazoFileView InitialDirectory="…" …/>` (`Kazo.Os.AppSdk.Blazor.Files`) and its controller `IKazoFileView` ([`file-explorer.md`](../dev/file-explorer.md)) |
| `IFileViewContextMenuProvider`, `FileViewMenuItemCollection` | `ContextMenuOpening` of `KazoFileView`, or an entry registered by `[RegisterFileContextMenu]` ([`context-menu.md`](context-menu.md)) |
| `FileTransfer.CopyAsync` / `MoveAsync` / `TransferAsync` / `ImportAsync` / `MoveToTrashAsync` / `GetCopyName` | the same methods of `KazoFileOperations` (`Kazo.Os.AppSdk.IO`) |
| `FileTransferResult`, `FileTransferMode`, `FileConflictChoice`, `FileConflictResolution`, `FileTransferConflict`, `FileConflictResolver`, `FileTransferFailure`, `FileTransferItemResult`, `FileTransferHostFile` | `KazoFileTransferResult`, `KazoFileTransferMode`, `KazoFileConflictChoice`, `KazoFileConflictResolution`, `KazoFileTransferConflict`, `KazoFileConflictResolver`, `KazoFileTransferFailure`, `KazoFileTransferItemResult`, `KazoHostFile` |
| a conflict resolver written by the app | `Dialogs.AsFileConflictResolver(AppContext)`: the "Replace / Keep both / Skip" dialog of the `kazoos.file-dialogs` provider |
| `ShellIcon`, `ShellIconDescriptor` | `KazoIcons.Vector(…)` / `KazoIcons.FromImage(…)` (`Kazo.Os.AppSdk.Icons`) to describe an icon; rendering belongs to the OS |

Then set `KazoOsVersion` to `2.2.2` (or the latest published 2.x) and recompile.

### Additions

All the other SDK changes are additions:

| Library | Addition | Decision |
|---|---|---|
| `Kazo.Os.AppSdk.Blazor` | `KazoContractComponent<TController>`, `KazoComponentResolution`: host of a contract provided by an app | ADR 0148, 0153 |
| `Kazo.Os.AppSdk.Blazor` | `KazoContextMenu`, `IKazoContextMenu`, `KazoMenus` (`Menus.Attach`, `Menus.Define`, `OpenAsync`), `KazoMenuBuilder`, `KazoMenuDefinition`, `KazoMenuItem`, `KazoMenuGroup`, `KazoMenuOpeningEventArgs`, `IKazoMenuHost` | ADR 0150, 0154 |
| `Kazo.Os.AppSdk.Blazor` | `KazoFileView`, `IKazoFileView`, `KazoFileViewItem`, `KazoFileViewMode`, `KazoFileViewFeatures`, their events, `KazoFileMenuOpeningEventArgs`, `KazoFileMenuContext` | ADR 0148, 0155, 0160 |
| `Kazo.Os.AppSdk.Blazor` | `IDialogService.OpenWithAsync`, `ShowFilePropertiesAsync`, `ResolveFileConflictAsync` and their types, `KazoFileDialogs`, `KazoFileOperationDialogs.AsFileConflictResolver` | ADR 0155, 0157, 0161 |
| `Kazo.Os.AppSdk.Blazor` | `[RegisterFileContextMenu]`, `RegisterFileContextMenuContext` | ADR 0151, 0159 |
| `Kazo.Os.AppSdk` | `KazoFileOperations` and its types (`KazoFileTransferResult`..., `KazoHostFile`) | ADR 0161 §2 |
| `Kazo.Os.AppSdk` | `KazoFileMenuItems`, `FileMenuScope`, `MenuSelection`, `KazoFileMenuIntent`, `KazoFileMenuEntries`, `KazoFileMenuEntryInfo` | ADR 0151, 0159 |
| `Kazo.Os.AppSdk` | `KazoComponentProviders`, `KazoComponentContractInfo`, `KazoComponentProviderInfo` (provider choice by the administrator) | ADR 0156, 0158 |
| `Kazo.Os.AppSdk` | `KazoDesktopSettings`, `KazoDesktopSettingsDocument`, `KazoDesktopOptions`, `KazoDesktopLayout`, `KazoDesktopCell`, `KazoDesktopEntry`, `KazoDesktopItems`, `KazoDesktopSort`, `KazoDesktopIconSize`, `KazoDesktopArrangement`, `KazoAppIcons` | ADR 0152, 0160 |
| `Kazo.Os.AppSdk` | `KazoCrossOriginIsolation` and its types, `IAppCrossOriginIsolationGateway` | ADR 0146 |
| `Kazo.Os.AppSdk.Icons` | `KazoIcons` (`Vector`, `FromImage`), `KazoSurfaceIcon`, `KazoIconKind` | ADR 0150, 0151 |
| `Kazo.Os.App.Abstractions` | `AppManifest.ContextMenus`, `AppContextMenuEntryManifest` and its titles and icons, `AppContextMenus`, `AppComponentContracts`, `AppComponentProviderScope`, capability `shell.context-menu` | ADR 0151, 0153, 0156 |
| `Kazo.Os.Simulation.Abstractions` | `IAppEventGateway.PublishAsync` | ADR 0145 |

### SDK 2.1

Published on 2026-10-26, the first version released by the `Release Kazo OS`
workflow ([ADR 0173](../dev/adr/0173-automatic-releases-from-the-version-file.md),
[`releases.md`](../dev/releases.md)): `v2.1.0` tag, GitHub Release and nuget.org.
`build/tools/ship-public-api.sh` folded 5880 lines of `PublicAPI.Unshipped.txt`
into `PublicAPI.Shipped.txt`: the additions that arrived after the 2.0 cut
(passkeys and account administration, ADR 0168 and 0169;
`KazoFileView.OpenOnTap`, ADR 0171; Settings side menu, ADR 0170; the table
below) and the record members that public API analyzer 5.x now tracks
(`<Clone>$`, `Deconstruct`, `Equals`). No `*REMOVED*` line: a minor version
under SemVer, a 2.0 app runs without being recompiled.

| Library | Addition | Decision |
|---|---|---|
| `Kazo.Os.Simulation.Abstractions` | `ProcessStartRequest.ParentExitGracePeriod`: delay of a child after its parent ends (stop request, then kill) | ADR 0147 |
| `Kazo.Os.Platform.Core` | `BackgroundJobService.ParentExitGracePeriod` (5 s) | ADR 0147 |

### SDK 2.2

Prepared on 2026-10-31 (branch `release/2.2.0`), published by the
`Release Kazo OS` workflow when its PR lands in `main`: `v2.2.0` tag, GitHub
Release and nuget.org. `build/tools/ship-public-api.sh` folded 3020 lines of
`PublicAPI.Unshipped.txt` into `PublicAPI.Shipped.txt`: the additions of the
`todo/base` series and those that reached `main` since 2.1.0. The only
`*REMOVED*` lines touch `Kazo.Os.Apps.Browser` (the `BrowserResponse`
constructor receives the source of the response, ADR 0210), a base app and not
an SDK library: a minor version under SemVer, a 2.x app runs without being
recompiled. The `kazoos-solution` template needs this version for its
`Release` profile (ADR 0207 §11).

| Library | Addition | Decision |
|---|---|---|
| `Kazo.Os.AppSdk` | `KazoAppPackages`, `KazoPackageSources`, `KazoPackagePolicy`, `KazoPackageTrust`, `KazoPackageScope`, `KazoInstalledPackage`, `KazoPackageInstallReport`, `KazoPackageException`: install an app for oneself or for everyone, trusted sources, block list | ADR 0182, 0208 |
| `Kazo.Os.AppSdk` | `KazoAppCatalog`, `KazoCatalogApp`, `KazoCatalogAppVersion`, `KazoCatalogCategory`, `KazoCatalogItemKind`, `KazoPackageCatalog`, `KazoPackageUpdate`: Kazo OS Store catalogs and updates | ADR 0189, 0212 |
| `Kazo.Os.AppSdk` | `KazoInstalledThemePack`, `KazoThemePackInstallReport`, `KazoThemePackParts`: theme packs | ADR 0213 |
| `Kazo.Os.AppSdk` | `KazoWhatsNew`, `KazoChangeList`, `KazoChangeGroup`, `KazoChangeEntry`, `KazoChangeCategory`: the change list | ADR 0186 |
| `Kazo.Os.AppSdk` | `KazoScreens`, `KazoScreenInfo` and their types: screens of the extended desktop | ADR 0184 |
| `Kazo.Os.AppSdk` | `KazoTimeZone`, `IAppExecutionContext.TimeZone`; `KazoServerProbe`, `KazoServerConnection.PairAsync` and `TestAsync`; `KazoHttpResponseSource`; `KazoDirectorySize`; `KazoDesktopLink` | ADR 0193, 0191, 0210, 0185 |
| `Kazo.Os.Simulation.Abstractions` | `IHostFolder`, `IHostFolderAccess` and their types (computer folder mounted as a drive); `OsSoundEvent`, `SoundThemeIndex` (system sounds); `FileSystemTreeSize` | ADR 0181, 0204, 0185 |
| `Kazo.Os.Theming.Abstractions` | `WallpaperCatalog`, `BuiltInWallpaper`, `WallpaperFit` (image wallpapers); `IconPacks.IconPack`, `IconPackLibrary` and their types (icon packs); `CursorTheme`, `CursorThemeLibrary` and their types (cursor themes); `ThemePackLayout` (theme packs) | ADR 0202, 0203, 0205, 0213 |
| `Kazo.Os.AppSdk.Blazor` | `MessageBoxIcon` | ADR 0185 |

### SDK 2.2.1

Prepared on 2026-11-01 (branch `release/2.2.1`), published by the
`Release Kazo OS` workflow when its PR lands in `main`. Benoit chose a patch
number although the public API grows: `build/tools/ship-public-api.sh` folded
190 lines of `PublicAPI.Unshipped.txt` into `PublicAPI.Shipped.txt`. The only
`*REMOVED*` lines touch `Kazo.Os.Apps.FileExplorer` (Back moves from the mobile
window to the window, ADR 0220), a base app and not an SDK library: a 2.x app
runs without being recompiled. It brings to app authors what the Kazo OS Store
needs: every static resource of an app in its package and an installed app's
scoped CSS (ADR 0221), the satellite assemblies of installed apps (ADR 0222)
and the signing tool (ADR 0219, since removed by its 2026-11-07 amendment).

| Library | Addition | Decision |
|---|---|---|
| `Kazo.Os.AppSdk.Blazor` | `IAppForwardHandler`, `AppForwardRequest`, `AppBackSource.MouseButton` and `.SystemBack`: mouse Back/Forward buttons and the tab's Back | ADR 0220 |
| `Kazo.Os.AppSdk.Blazor` | `RegisterFileContextMenuContext.RestrictToScreen` and `RestrictToScreens` | ADR 0218 |
| `Kazo.Os.AppSdk` | `MenuScreens`, `KazoFileMenuIntent.ReadEntry` | ADR 0218 |
| `Kazo.Os.App.Abstractions` | `AppContextMenuEntryManifest.Screen` and `.Screens`, `AppContextMenus.MinScreenNumber`, `MaxScreenNumber`, `ScreensSingle`, `ScreensMultiple` | ADR 0218 |

### SDK 2.2.2

Prepared on 2026-11-01 (branch `release/2.2.2`), published by the
`Release Kazo OS` workflow when its PR lands in `main`. A patch: no SDK change.
The only API lines are the new value of
`AppPackageDefaults.OfficialCatalogSigningKey` (`Kazo.Os.Platform.Core`, not an
SDK library): the official catalog's public key, so every Kazo OS Store package
must carry the catalog's signature (ADR 0214 §2).

## SDK 3.0

Series `rename/base` ([ADR 0228](../dev/adr/0228-rename-to-kazoos.md),
[ADR 0229](../dev/adr/0229-kazo-os-display-name-and-new-address.md)):
HackerOS becomes Kazo OS, and `build/Versions.props` moves to `3.0.0`. No
feature changes: only names change, but every assembly name changes, so an app
compiled against 2.x is **refused** and must be rebuilt against the new
packages.

### Migrating a 2.x app

| In 2.x | In 3.0 |
|---|---|
| `HackerOs.*` packages (`HackerOs.AppSdk`, `HackerOs.App.Build`, `HackerOs.Templates`...) | `Kazo.Os.*` packages (`Kazo.Os.AppSdk`, `Kazo.Os.App.Build`, `Kazo.Os.Templates`...) |
| `HackerOs.*` namespaces | `Kazo.Os.*` namespaces |
| `Hack*` types (`HackFile`, `HackDirectory`, `HackPath`, `HackProcess`...) | `Kazo*` types (`KazoFile`, `KazoDirectory`, `KazoPath`, `KazoProcess`...) |
| `HackerOsSdk`, MSBuild property `HackerOsVersion` | `KazoOsSdk`, `KazoOsVersion` |
| `HackerIcon` component | `KazoIcon` component |
| `HackIcon` record (an icon described for an OS menu, made by `HackIcons`) | `KazoSurfaceIcon` (made by `KazoIcons`): the name `KazoIcon` is taken by the component |
| host methods `AddHackerOs*` (`AddHackerOsEcosystem`, `AddHackerOsBrowserHost`...) | `AddKazoOs*` |

### System names (work item 5)

Work item `rename/5-system-names` renames every name the OS shows, writes or
stores (Benoit, 2026-11-04: "what is written on disk changes too"). Kazo OS
**writes only the new names**. It **still reads** the old names that come from
outside the machine (packages, configuration, commands typed by habit); the
data already stored in the browser under the old names is rewritten by the
migration of work item 6 ([`rename-migration.md`](../dev/rename-migration.md), ADR 0230).

| Before | After | Old name still read |
|---|---|---|
| commands `hackpkg`, `hackbackup` (app ids `org.hackeros.commands.hackpkg`...) | `kazopkg`, `kazobackup` (`org.kazoos.commands.kazopkg`...) | yes: an alias that writes `hackpkg: this name is deprecated, use kazopkg` on standard error, then runs the new command (`DeprecatedCommandNames`, one minor version) |
| app packages `.hackapp`, theme packs `.hacktheme` | `.kazoapp`, `.kazotheme` | yes: `index.json` and `catalog.json` accept both extensions (`AppPackage.LegacyExtension`, `ThemePackLayout.LegacyExtension`) |
| description `hackeros-package.json`, `hackeros-theme-pack.json` | `kazoos-package.json`, `kazoos-theme-pack.json` | yes (`AppPackage.LegacyDescriptionFileName`, `ThemePackLayout.LegacyManifestFileName`) |
| folder `hackeros/manifests/` (and `hackeros/localization/`) of app NuGet packages | `kazoos/manifests/`, `kazoos/localization/` | yes: a 2.x package keeps its own `buildTransitive/*.targets`, which point to its folder |
| configuration section `HackerOs` (`HackerOs:Proxy`...) | `KazoOs` | yes: a `HackerOs:*` key answers under `KazoOs:*` when the new section lacks it (`KazoOsConfigurationNames.MapLegacySettings`, server, PWA and service worker) |
| server environment variables `HACKEROS_*` | `KAZOOS_*` | yes: both prefixes are read, the new one wins |
| connection string `HackerOsDb`, file `hackeros-server.json`, default database `hackeros.db` | `KazoOsDb`, `kazoos-server.json`, `kazoos.db` | yes for the connection string and the file; `hackeros.db` is renamed `kazoos.db` at start when the configured `kazoos.db` does not exist yet (ADR 0230) |
| key `X-HackerOS-Origin` of `.trashinfo` files | `X-KazoOS-Origin` | yes |
| variable `HACKEROS_PREVIOUS_VERSION` handed to an updated app | `KAZOOS_PREVIOUS_VERSION` | yes: both are handed |
| service worker caches `hackeros-cache-*` | `kazoos-cache-*` | deleted on activation |
| app ids `org.hackeros.*`, publisher `pub.hackeros`, capabilities, contracts and intents `hackeros.*` (`hackeros.file-view`...) | `org.kazoos.*`, `pub.kazoos`, `kazoos.*` | no: the stored ones are rewritten at boot and at restore (work item 6, ADR 0230) |
| IndexedDB databases `hackeros*`, `hackweb.*`, `localStorage` / `sessionStorage` keys, theme ids (`hackeros`, `hackeros-neon`...), sound theme `hackeros` | `kazoos*`, `kazoweb.*`, `kazoos...` | no: the databases are copied under the new names, the keys renamed, at boot (work item 6, ADR 0230) |
| paths `/etc/hackeros`, `~/.config/hackeros`, `~/.local/share/hackeros`, `~/.local/state/hackeros`, `/usr/share/hackeros`, `/usr/lib/hackeros`, `/var/lib/hackeros`, `/proc/hackeros`, `/etc/hackweb`, `/var/log/hackweb`, `~/.config/hackweb`, `~/.hackeros/aliases` | `/etc/kazoos`, `~/.config/kazoos`... `/proc/kazoos`, `/etc/kazoweb`, `/var/log/kazoweb`, `~/.config/kazoweb`, `~/.kazoos/aliases` | no: work item 6 moves the existing files at boot (ADR 0230) |
| service `hackwebd`, resolver `hackdns`, default host name `hackeros`, service account `hackeros-svc` | `kazowebd`, `kazodns`, `kazoos`, `kazoos-svc` | no (an `/etc/hostname` already written is kept) |
| CSS variables `--hackeros-*`, attributes `data-hack-menu*`, sites' script `_framework/blazor.hack.js`, C# names `IHack*` / `Hack*` / `Hacker*` left by work item 3 | `--kazoos-*`, `data-kazo-menu*`, `_framework/blazor.kazo.js`, `IKazo*` / `Kazo*` | no |
| ASP.Hack, Hack Store, Hack Paint | ASP.Kazo (folder `build/asp-kazo/`, header `X-Powered-By: ASP.Kazo`), Kazo OS Store, Kazo Paint | — |
| installable server `hackeros-server`, Windows service `HackerOSServer`, Unix account `hackeros`, image `ghcr.io/kazo-ca/hackeros-server`, route `/api/hackeros/*`, `hackeros-host.json` | `kazoos-server`, `KazoOSServer`, `kazoos`, `ghcr.io/kazo-ca/kazoos-server`, `/api/kazoos/*`, `kazoos-host.json` | no: a new install goes next to the old one |
| `dotnet new` templates `hackeros-window`, `-command`, `-service`, `-site`, `-host`, `-solution`; branch `hackeros-packages` of app repositories; submodule `external/HackerOs` | `kazoos-window`... `kazoos-solution`; `kazoos-packages`; `external/KazoOs` | no: an app repository publishes on `kazoos-packages` from its next release |

Kept on purpose: the simulated `.hack` domain and the simulated sites
(`hackersearch.net`, `hackmail.com`, `hackerz.forum`), the `hacker-grid`
wallpaper and the "hacker console" look (a style, not the name of the OS), the
old address `hackos.kazo.ca` and its redirect worker, the names of accepted
ADRs and of past branches, the recorded fixtures (`Tests/fixtures/indexeddb`,
`Tests/fixtures/sdk-compat`), the salt `hackeros-server-account` (it derives
passwords already stored), and the GitHub secrets `HACKEROS_APP_SIGNING_KEY`,
`HACK_STORE_TOKEN`, `HACK_STORE_SIGNING_KEY` (renaming them would make every
repository recreate its secret).

### Short names (work item 9)

Work item `rename/9-kos-short-names` (Benoit, 2026-11-04) replaces the
remaining short "HOS" prefixes with "KOS". The rename is done by a script
(`git grep -l` then `perl`), except in accepted ADRs, already published change
lists and the recorded fixtures.

| Before | After | Old name still read |
|---|---|---|
| backups `.hosbak` | `.kosbak` | yes: the file picker still offers `.hosbak` (`IndexedDbBackupService.LegacyArchiveExtension`); the archive is recognized by its content, not its name |
| diagnostic codes `HOSAPP*`, `HOSSDK*`, `HOSWEB*`, `HOSBG*`, `HOSCHG*` (manifest generator, analyzers, MSBuild targets, change list tool) | `KOSAPP*`, `KOSSDK*`, `KOSWEB*`, `KOSBG*`, `KOSCHG*` (same numbers) | no: an app's `NoWarn`, `WarningsAsErrors` or `#pragma warning disable` names the new code |
| CSS variables `--hos-*` (design-system tokens) | `--kos-*` | yes in a `.theme` file (a theme pack published with `--hos-*` is read under the `--kos-*` names); stored themes and settings are rewritten by the pre-3.0 migration (`RenamedNames`); no in an app's CSS: it must be rebuilt |
| attributes `data-hos-*` (`data-hos-theme`, `data-hos-motion`, `data-hos-accent`) | `data-kos-*` | no |
| classes and ids `hos-*` (`hos-chart*` of `KazoLineChart`, `hos-cursor-theme`) | `kos-*` | no |

### Compatibility app

`Tests/fixtures/sdk-compat/packages` keeps one app per published version that
received one, each compiled **once** by `produce-fixture.sh`, outside the
repository, against the packages of its version, and **never rebuilt**:

| Package | Compiled against | Content |
|---|---|---|
| `Contoso.Sdk1Compat.1.0.0.nupkg` | 1.0.0 (`v3/15`) | window `com.contoso.sdk1-window` (saves and rereads a file), command `sdk1-compat` |
| `Contoso.Sdk11Compat.1.1.0.nupkg` | 1.1.0, packages from the top of `v3.1/base` (`exec/10-final`, ADR 0132 §1) | window `com.contoso.sdk11-window` that names itself with `SetWindowTitleAsync` and `FormatWindowTitle` (`note.txt — SDK 1.1 Window`), `rememberGeometry: false`; command `sdk11-compat` that also calls `KazoUserAccounts.ValidateNewPassword` |
| `Contoso.Sdk2Compat.2.0.0.nupkg` | 2.0.0, packages from the top of `desk/6-sdk-2` (ADR 0161 §3) | window `com.contoso.sdk2-window` that displays the OS file control (`KazoFileView`) and registers the entry "Inspect with SDK 2.0" in the file menu (`[RegisterFileContextMenu]`, intent `com.contoso.sdk2.inspect.v1`); command `sdk2-compat` that copies a file through `KazoFileOperations.CopyAsync` (`sdk2-compat (copy).txt`) |
| `Contoso.Sdk3Compat.3.0.0.nupkg` | 3.0.0, `Kazo.Os.*` packages from the top of `rename/3-namespaces` (ADR 0228) | the same app as 2.0.0 under the new names: window `com.contoso.sdk3-window` (`KazoFileView`, entry "Inspect with SDK 3.0", intent `com.contoso.sdk3.inspect.v1`); command `sdk3-compat` (`KazoFileOperations.CopyAsync`) |

The tests load the current major's app (3.0.0) into the OS; those of
1.x and 2.x remain as proof of the **refusal** of an app from another major
(ADR 0149 §5, ADR 0228).

```bash
build/pack-local.sh
Tests/fixtures/sdk-compat/produce-fixture.sh --project Contoso.Sdk3Compat   # against the 3.0.0 feed
```

## Tests

| Where | What |
|---|---|
| `Tests/Kazo.Os.Templates.E2E.Tests` | packages everything (`pack-local`, or the `KAZOOS_LOCAL_FEED` feed), installs the templates in an isolated `DOTNET_CLI_HOME`, generates and compiles each app template outside the repository with an isolated NuGet cache (manifest, kind, minimum SDK version checked); then generates a host, adds a generated windowed app and a command to it, compiles it (aggregated catalog checked), launches it, creates the profile in Chromium, opens the app (`Clicked 0 times` then `1`) and runs `hello Morpheus` in the terminal (`Hello, Morpheus!`). |
| `Tests/Kazo.Os.Templates.E2E.Tests` (`A_host_runs_SDK_2_app_packages_and_refuses_SDK_1_x_packages`) | a generated host with `PackageReference` to the 1.0.0 and 1.1.0 compatibility apps (source `kazoos-sdk-fixtures`) does not build (`KOSAPP0021` for each of the four apps); with the 2.0.0 app instead, it starts in Chromium, the 2.0 window displays the OS file control and `sdk2-compat still binds` runs in the terminal (ADR 0149 §5, ADR 0161 §3) |
| `Tests/Kazo.Os.SdkCompatibility.Tests` | the 2.0.0 compatibility app: admitted by the current catalog, menu entry registered and `KazoFileView` referenced, every Kazo OS type and member it references exists, every method compiles against the current SDK (JIT), the command runs; the 1.0.0 and 1.1.0 apps: refused (`catalog.sdk.incompatible`) (ADR 0149 §5, ADR 0161 §3) |
| `Tests/Kazo.Os.App.Abstractions.Tests` (`KazoOsSdkTests`) | version reading, range compatibility |
| `Tests/Kazo.Os.Tools.ManifestGenerator.Tests` | SDK version read from the reference, fingerprints and `KOSAPP0023`, `KOSAPP0022`, app newer than the host's SDK (`KOSAPP0021`) |
| `Tests/Kazo.Os.Platform.Core.Tests` | `AppCatalog` (refusal, dependency cascade, command owner), `AppAssetStore`, `ShellCommandResolver` |
| `build/verify-hosts.sh` | the three hosts start after the extraction of `Kazo.Os.Ecosystem.Host` |

Also checked by hand: an app that adds the reference
`Kazo.Os.Platform.Core` fails with `KOSAPP0030 Forbidden package`.

## Decisions

- **A single version** for everything, rather than a version per package: the SDK, the
  platform and the apps evolve together; since the freeze, the SemVer promise
  only covers the SDK (ADR 0114 §2).
- **The same `Kazo.Os.Apps.targets`** in the repository and in the package, with a
  `KazoOsBuildFromPackage` switch, so that no rule diverges.
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
- [x] `KazoOsSdk`, minimum written by the generator, refusal at load, "incompatible" state in Settings.
- [x] `KOSAPP0022` and single owner of a command at load.
- [x] Resource fingerprints (`KOSAPP0023`, `AppAssetStore`, "damaged" state).
- [x] Removal of `BuildProfileManifest`, its validator and its serializer.
- [x] `Kazo.Os.App.Build` package and package mode of `Kazo.Os.Apps.targets` (package boundary, aggregation of apps as packages, manifests in app packages).
- [x] `Kazo.Os.Ecosystem.Host` extracted, `AddKazoOsBrowserHost`, `HostAppCatalog`, design tokens moved, three hosts updated.
- [x] `build/pack-local.sh` / `.ps1`, `build/nuget.config.sample`.
- [x] Five templates and their end-to-end test.
- [x] SDK 1.0 freeze (`v3/15`, ADR 0114): everything in `Shipped`, version `1.0.0`, obsolete APIs removed, compatibility app and its tests, guide [`building-on-kazoos.md`](building-on-kazoos.md).
- [x] SDK 1.1 (`v3.1/4-sdk-1-1`, ADR 0117 §5): `Unshipped` merged into `Shipped`, version `1.1.0`, breaks outside the SDK listed, 1.0 compatibility app still loaded.
- [x] SDK 1.2 (`exec/10-final`, ADR 0132 §2): `build/tools/ship-public-api.sh`, `Unshipped` merged into `Shipped`, version `1.2.0`, break outside the SDK listed, 1.1.0 compatibility app added, 1.0.0 and 1.1.0 loaded.
- [x] SDK 2.0 (`desk/6-sdk-2`, ADR 0149, ADR 0161): `Kazo.Os.AppSdk.FileView` removed from the SDK (code in the explorer), `KazoFileOperations`, `Unshipped` merged into `Shipped`, version `2.0.0`, breaks outside the SDK listed, 2.0.0 compatibility app added, 1.x apps refused.
- [x] Documentation (this file, `app-model`, `app-sdk`, `sdk-reference`, `sdk-developer-guide`, `building-and-testing`, `project-overview`, resource measurement, notes of archived documents) and index.
