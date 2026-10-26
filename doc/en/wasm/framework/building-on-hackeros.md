> [Version française](../../../fr/wasm/framework/building-on-hackeros.md)

# Building on HackerOS from another repository

## Purpose

The guide for a repository that **uses** HackerOS without copying its code: any
project that wants its own apps and its own OS host. **No game in this repository** ([ADR 0135](../dev/adr/0135-no-game-in-this-repository.md)). It starts from SDK 1.0, frozen by work item `v3/15`
([ADR 0114](../dev/adr/0114-sdk-1-0-freeze.md)); the current version is **2.1.0**
(desktop and context menu series, [ADR 0149](../dev/adr/0149-sdk-2-0-file-view-removal.md)
and [ADR 0161](../dev/adr/0161-sdk-2-0-implementation-adjustments.md), see "Moving to 2.0";
1.2 comes from the `exec` series, [ADR 0132](../dev/adr/0132-exec-series-sdk-1-2-and-merge-rules.md) §2,
1.1 from the v3.1 series, [ADR 0117](../dev/adr/0117-sdk-window-title-and-sdk-1-1.md) §5).

Details of the packages and of the package mode of `HackerOs.App.Build`:
[`sdk-packages.md`](sdk-packages.md). Writing an app (API, examples):
[`sdk-developer-guide.md`](sdk-developer-guide.md) and
[`sdk-reference.md`](sdk-reference.md).

## 1. What version 1.0 promises

| Your project depends on… | Guarantee |
|---|---|
| the SDK: `HackerOs.App.Abstractions`, `HackerOs.Simulation.Abstractions`, `HackerOs.Theming.Abstractions`, `HackerOs.AppSdk*`, `HackerOs.Theming.Blazor`, the build contract of `HackerOs.App.Build`, the manifest format | an app compiled against `1.x` runs on any OS `1.y` (y ≥ x); a break only comes with a 2.0 |
| the platform: `HackerOs.Ecosystem.Host`, `HackerOs.Platform.*`, `HackerOs.Windowing.*`, `HackerOs.Infrastructure.Browser`, the public types of the base apps | versioned with the SDK; a minor can break a **host**, never an app. Every break is listed in [`sdk-packages.md`](sdk-packages.md#breaks-outside-the-sdk) |

The OS refuses an app compiled against a newer SDK than its own, or of a
different major version ("incompatible" state in Settings, ADR 0098 §3). An app
built as `0.9.0-preview.*` must be rebuilt against 1.0.

## 2. Getting the packages

HackerOS packages are published only to a **local** feed (never on
nuget.org). In a HackerOS clone, on the desired version:

```bash
git -C ../HackerSimulator checkout <tag ou branche>
../HackerSimulator/wasm2/HackerOs/build/pack-local.sh      # ou pack-local.ps1
# -> ../HackerSimulator/wasm2/HackerOs/artifacts/local-nupkg/*.2.1.0.nupkg
```

`--output <folder>` writes elsewhere (for example a shared folder of your
repository). The script purges this version from the global NuGet cache: a
repackaged package is never served stale.

## 3. Repository layout

```text
mon-depot/
├─ nuget.config                 # copié de build/nuget.config.sample
├─ Directory.Build.props        # HackerOsVersion épinglée une fois
├─ MonDepot.sln
├─ src/
│  ├─ Contoso.Os/               # l'hôte (dotnet new hackeros-host)
│  ├─ Contoso.Notes/            # une app fenêtrée (hackeros-window)
│  └─ Contoso.Hello/            # une commande (hackeros-command)
└─ tests/
   └─ Contoso.Hello.Tests/
```

`nuget.config`: the `hackeros-local` source points to the feed, and the
`packageSourceMapping` takes `HackerOs.*` only from there:

```xml
<configuration>
  <packageSources>
    <add key="hackeros-local" value="../HackerSimulator/wasm2/HackerOs/artifacts/local-nupkg" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="hackeros-local"><package pattern="HackerOs.*" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
```

`Directory.Build.props`: a single version for all HackerOS packages
(they are published together, ADR 0098 §1). The templates write
`<HackerOsVersion>` in each project; remove it from the projects to keep
only this one:

```xml
<Project>
  <PropertyGroup>
    <HackerOsVersion>2.1.0</HackerOsVersion>
  </PropertyGroup>
</Project>
```

## 4. Creating the projects

```bash
dotnet new install ../HackerSimulator/wasm2/HackerOs/artifacts/local-nupkg/HackerOs.Templates.2.1.0.nupkg
dotnet new hackeros-window  -n Contoso.Notes -o src/Contoso.Notes --appId com.contoso.notes --publisherId pub.contoso
dotnet new hackeros-command -n Contoso.Hello -o src/Contoso.Hello --appId com.contoso.hello --publisherId pub.contoso
dotnet new hackeros-service -n Contoso.Sync  -o src/Contoso.Sync  --appId com.contoso.sync  --publisherId pub.contoso
dotnet new hackeros-site    -n Contoso.Site  -o src/Contoso.Site  --appId com.contoso.site  --publisherId pub.contoso
dotnet new hackeros-host    -n Contoso.Os    -o src/Contoso.Os
dotnet sln add src/*/*.csproj
```

Each app project references `HackerOs.AppSdk*` and `HackerOs.App.Build`
(`PrivateAssets="all"`). At build time, `HackerOs.App.Build` writes `manifest.json`
from the `[AppManifest]` class, with the SDK version as the minimum, and
refuses a reference outside the SDK (`HOSAPP0030`) or an injection of a platform
service (`HOSAPP0031`).

## 5. Composing the host

The `hackeros-host` host is a complete Blazor WebAssembly PWA: the whole OS comes
from `HackerOs.Ecosystem.Host`, the apps are its references.

```xml
<!-- src/Contoso.Os/Contoso.Os.csproj -->
<ItemGroup>
  <!-- Vos apps, en projets… -->
  <ProjectReference Include="../Contoso.Notes/Contoso.Notes.csproj" />
  <ProjectReference Include="../Contoso.Hello/Contoso.Hello.csproj" IsSystemApp="true" />
  <!-- …ou en paquets d'apps. -->
  <PackageReference Include="Contoso.Tools" Version="2.3.0" />
  <!-- Les apps de base que vous gardez (retirez les autres). -->
  <PackageReference Include="HackerOs.Apps.Terminal" Version="$(HackerOsVersion)" IsSystemApp="true" />
</ItemGroup>
```

- `IsSystemApp="true"`: system app (not uninstallable, dangerous capabilities
  granted, ADR 0048, ADR 0049).
- The build aggregates the manifests of all apps into
  `BuiltInApplications.json`, embedded in the host's assembly;
  `HostAppCatalog.Load` reads it back and the apps' assemblies are loaded
  lazily.
- `dotnet run` serves the host; the first startup asks to create the
  administrator.

## 6. Distributing an app as a package

`dotnet pack` of an app project produces a package that embeds its manifests
(`hackeros/manifests/`), their translations and a `buildTransitive` that
declares them to the host: a host never needs its source code. Give the
package a `PackageId` outside the `HackerOs.*` space, reserved to the HackerOS feed
by the `packageSourceMapping`.

## 7. Testing

- A command is tested without a browser: create the command with its manifest
  (`AppDefinitionMapper.ToManifest(new MyApp())`), a
  `TerminalExecutionContext` on fake gateways, and
  `HackExecutionContext.Enter(app)` around `ExecuteAsync`
  ([`sdk-developer-guide.md`](sdk-developer-guide.md), testing a command).
- A host is tested like `HackerOs.Templates.E2E.Tests`: `dotnet run` of
  the host, then Playwright (administrator creation, Applications menu,
  Terminal).
- To verify that a version upgrade does not break your already published apps,
  keep, as HackerOS does, an app package compiled against the old version and
  load it in the current host (`Tests/fixtures/sdk-compat`, ADR 0114 §4).

## 8. Publishing the host

```bash
dotnet publish src/Contoso.Os -c Release
```

The host template enables `PublishTrimmed` in `partial` mode: the framework is
trimmed, the HackerOS assemblies and all the apps remain whole (roots
added by `HackerOs.App.Build`, ADR 0113). The `wwwroot` output is a static
site; the service worker registers only outside `localhost`. See
[`trimmed-publish.md`](../dev/trimmed-publish.md).

## 9. Upgrading

1. Produce the feed of the new version (§2).
2. Change `HackerOsVersion` in `Directory.Build.props`.
3. Minor: rebuild; read "Breaks outside the SDK" in
   [`sdk-packages.md`](sdk-packages.md) if you have a host. Major:
   rebuild **all** apps, the OS refuses those of the old major.
4. The regenerated manifests carry the new minimum: a rebuilt app
   no longer loads on an older OS.

### Moving to 2.0

A major version: OS 2.0 refuses an app compiled against 1.x, and a host that
references one no longer builds (`HOSAPP0021`). Every app must be
recompiled against the 2.x packages (2.1.0 today; verified by `Tests/fixtures/sdk-compat`,
where the 1.0.0 and 1.1.0 apps are refused and the 2.0.0 app runs).

- **Only break**: the `HackerOs.AppSdk.FileView` package no longer exists. An
  app that displayed `FileView` displays `HackFileView` (file control
  provided by the OS, `HackerOs.AppSdk.Blazor.Files`); `FileTransfer` becomes
  `HackFileOperations` (`HackerOs.AppSdk.IO`). The full table is in
  [`sdk-packages.md`](sdk-packages.md#migrating-a-1x-app).
- **OS context menu**: `Menus.Attach(name, element)` on any
  element, `Menus.Define` in C#, submenus, keyboard and long press
  (ADR 0150, [`context-menu.md`](context-menu.md)).
- **Entries in file menus**: `[RegisterFileContextMenu]` in the
  `[AppManifest]` class, capability `shell.context-menu`; the entry launches the app
  with an intent and the paths (ADR 0151, 0159).
- **Desktop**: `HackDesktopSettings`, `.desktop` shortcuts (`HackDesktopEntry`,
  ADR 0152, 0160, [`desktop.md`](../dev/desktop.md)).
- **Providing an OS control**: an app can provide `hackeros.file-view`
  and `hackeros.file-dialogs`; the administrator chooses the provider
  (ADR 0156, 0158, [`embedded-components.md`](embedded-components.md)).
- **Also**: `IAppEventGateway.PublishAsync` (ADR 0145) and
  `HackCrossOriginIsolation` (ADR 0146), planned for a 1.3 that was never released.

The breaks outside the SDK for a host (file explorer, code editor,
the host's `IShellIconProvider`) are in
[`sdk-packages.md`](sdk-packages.md#breaks-outside-the-sdk).

### What 1.2 added

A minor: an app compiled against 1.0.0 or 1.1.0 ran without change
on OS 1.2. Its additions are part of 2.0.

- **Heavy tasks out of the window**: `HackBackground.RunAsync` with a
  static method and serializable arguments, or a class
  `HackBackgroundJob<TInput, TResult>`; progress, cancellation and
  `CheckpointAsync` through `HackBackgroundContext`. The analyzer `HOSBG0001` to
  `HOSBG0004`, shipped in `HackerOs.AppSdk`, refuses a lambda that captures
  (ADR 0128, 0140, [`background-jobs.md`](background-jobs.md)).
- **Per-process limits**: `HackProcess.GetResourceLimitsAsync`,
  `SetResourceLimitAsync`, `HackResourceLimits`; `EMFILE`
  (`FileSystemErrorCode.TooManyOpenFiles`) and `EAGAIN`
  (`ProcessLimitExceededException`) to handle as ordinary I/O errors;
  new exit reasons `CpuLimitExceeded`,
  `MemoryLimitExceeded`, `OutOfMemory` (ADR 0125, 0139,
  [`process-limits.md`](../dev/process-limits.md)).
- **OOM killer**: `AppDefinition.OomScoreAdjust` so that an app is killed
  later (or earlier) when memory runs out; the OOM killer is off by
  default (ADR 0126, 0142, [`oom-killer.md`](../dev/oom-killer.md)).
- **Priority**: `HackProcess.Nice`, `BasePriority` (ADR 0127,
  [`process-priority-and-unresponsive-windows.md`](../dev/process-priority-and-unresponsive-windows.md)).
- **Worker commands that listen to events**:
  `[assembly: HackWireContext(typeof(MyJsonContext))]` so that
  `HackEvents` and `Watch` go through the remote gateway (ADR 0124, 0138,
  [`worker-processes.md`](../dev/worker-processes.md)).

The exact list and the host's break outside the SDK (constructor of
`EcosystemBootCoordinator`) are in
[`sdk-packages.md`](sdk-packages.md#sdk-12).

### What 1.1 adds

A minor: an app compiled against 1.0.0 runs without change on OS
1.1 (verified by the frozen app of `Tests/fixtures/sdk-compat`). To use the
additions, set `HackerOsVersion` to `1.1.0` and rebuild; the app then requires
an OS 1.1 or newer.

- **Window title**: `WindowAppBase.SetWindowTitleAsync`, `WindowTitle`,
  `FormatWindowTitle("rapport.txt", modified: true)` → `*rapport.txt — Mon app`
  (ADR 0117, 0123, [`window-model.md`](window-model.md) §4).
- **Remembered window size**: `WindowOptions.RememberGeometry = false`
  so that a window does not take back its last size (ADR 0116, 0122,
  [`window-geometry.md`](../dev/window-geometry.md)).
- **Accounts and passwords**: `HackUserAccounts` (`ListAsync`,
  `ChangePasswordAsync`, `ResetPasswordAsync` with the `users.manage` capability),
  `TerminalExecutionContext.ReadSecretLineAsync` for input without echo
  (ADR 0120, [`sensitive-settings.md`](../dev/sensitive-settings.md)).
- **User default applications**: `HackApps.SetDefaultHandlerAsync`,
  `ResetDefaultHandlerAsync`, `GetDefaultHandlersAsync` (ADR 0121,
  [`user-file-associations.md`](../dev/user-file-associations.md)).

The exact list and the breaks outside the SDK for a host (three changes of
`HackerOs.Platform.Blazor` around window geometry) are in
[`sdk-packages.md`](sdk-packages.md#sdk-11).

## Decisions

- **A local feed rather than nuget.org** (ADR 0098 §2): GitHub Packages
  will come when the repository's CI works again.
- **Version pinned once** in the third-party repository: HackerOS packages
  go together, mixing two versions is not supported.

## Tasks

- [x] Compatibility promise and refusal of other majors.
- [x] Local feed, `nuget.config`, pinned version.
- [x] Templates, host, apps as projects and as packages.
- [x] Tests, trimmed publishing, version upgrade.
- [x] SDK 1.1: additions and link to the breaks outside the SDK (`v3.1/4-sdk-1-1`).
- [x] SDK 1.2: additions and link to the break outside the SDK (`exec/10-final`).
- [x] SDK 2.0: current version, break, migration and additions (`desk/6-sdk-2`).
