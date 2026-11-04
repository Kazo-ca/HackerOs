> [Version française](../../../fr/wasm/framework/building-on-hackeros.md)

# Building on Kazo OS from another repository

## Purpose

The guide for a repository that **uses** Kazo OS without copying its code: any
project that wants its own apps and its own OS host. **No game in this repository** ([ADR 0135](../dev/adr/0135-no-game-in-this-repository.md)). It starts from SDK 1.0, frozen by work item `v3/15`
([ADR 0114](../dev/adr/0114-sdk-1-0-freeze.md)); the current version is **2.1.0**
(desktop and context menu series, [ADR 0149](../dev/adr/0149-sdk-2-0-file-view-removal.md)
and [ADR 0161](../dev/adr/0161-sdk-2-0-implementation-adjustments.md), see "Moving to 2.0";
1.2 comes from the `exec` series, [ADR 0132](../dev/adr/0132-exec-series-sdk-1-2-and-merge-rules.md) §2,
1.1 from the v3.1 series, [ADR 0117](../dev/adr/0117-sdk-window-title-and-sdk-1-1.md) §5).

Details of the packages and of the package mode of `Kazo.Os.App.Build`:
[`sdk-packages.md`](sdk-packages.md). Writing an app (API, examples):
[`sdk-developer-guide.md`](sdk-developer-guide.md) and
[`sdk-reference.md`](sdk-reference.md).

## The shortest way: the `hackeros-solution` template

For an app in its own repository, start from the solution template
([ADR 0176](../dev/adr/0176-app-repository-template.md),
[ADR 0207](../dev/adr/0207-solution-template-implementation.md)): app, host,
tests, a Kazo OS submodule that sets the version, the OS sources in `Debug`,
packages in `Release`, CI and publishing to the Hack Store with a tag.
Everything is in [`app-repository.md`](app-repository.md). The rest of this
guide describes the assembly by hand, useful for a repository with several apps
or its own host.

## 1. What version 1.0 promises

| Your project depends on… | Guarantee |
|---|---|
| the SDK: `Kazo.Os.App.Abstractions`, `Kazo.Os.Simulation.Abstractions`, `Kazo.Os.Theming.Abstractions`, `Kazo.Os.AppSdk*`, `Kazo.Os.Theming.Blazor`, the build contract of `Kazo.Os.App.Build`, the manifest format | an app compiled against `1.x` runs on any OS `1.y` (y ≥ x); a break only comes with a 2.0 |
| the platform: `Kazo.Os.Ecosystem.Host`, `Kazo.Os.Platform.*`, `Kazo.Os.Windowing.*`, `Kazo.Os.Infrastructure.Browser`, the public types of the base apps | versioned with the SDK; a minor can break a **host**, never an app. Every break is listed in [`sdk-packages.md`](sdk-packages.md#breaks-outside-the-sdk) |

The OS refuses an app compiled against a newer SDK than its own, or of a
different major version ("incompatible" state in Settings, ADR 0098 §3). An app
built as `0.9.0-preview.*` must be rebuilt against 1.0.

## 2. Getting the packages

Since 2.1.0, every release publishes all Kazo OS packages (SDK, platform,
base apps, `Kazo.Os.App.Build`, `Kazo.Os.Ecosystem.Host`,
`Kazo.Os.Templates`) to **nuget.org**, with their symbols and Source Link
([ADR 0173](../dev/adr/0173-automatic-releases-from-the-version-file.md)): a
`PackageReference` at the desired version is enough, with no `nuget.config`.

The **local** feed is for trying a version not published yet. In a Kazo OS
clone, on the desired branch:

```bash
git -C ../HackerOs checkout <tag or branch>
../HackerOs/wasm2/KazoOs/build/pack-local.sh      # or pack-local.ps1
# -> ../HackerOs/wasm2/KazoOs/artifacts/local-nupkg/*.2.2.2.nupkg
```

`--output <folder>` writes elsewhere (for example a shared folder of your
repository). The script purges this version from the global NuGet cache: a
repackaged package is never served stale.

## 3. Repository layout

```text
mon-depot/
├─ nuget.config                 # local feed only (build/nuget.config.sample)
├─ Directory.Build.props        # KazoOsVersion épinglée une fois
├─ MonDepot.sln
├─ src/
│  ├─ Contoso.Os/               # l'hôte (dotnet new hackeros-host)
│  ├─ Contoso.Notes/            # une app fenêtrée (hackeros-window)
│  └─ Contoso.Hello/            # une commande (hackeros-command)
└─ tests/
   └─ Contoso.Hello.Tests/
```

`nuget.config` (only with the local feed): the `hackeros-local` source points
to the feed, and the `packageSourceMapping` takes `Kazo.Os.*` only from
there:

```xml
<configuration>
  <packageSources>
    <add key="hackeros-local" value="../HackerOs/wasm2/KazoOs/artifacts/local-nupkg" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="hackeros-local"><package pattern="Kazo.Os.*" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
```

`Directory.Build.props`: a single version for all Kazo OS packages
(they are published together, ADR 0098 §1). The templates write
`<KazoOsVersion>` in each project; remove it from the projects to keep
only this one:

```xml
<Project>
  <PropertyGroup>
    <KazoOsVersion>2.2.2</KazoOsVersion>
  </PropertyGroup>
</Project>
```

## 4. Creating the projects

```bash
dotnet new install Kazo.Os.Templates::2.2.2
# or, from the local feed: dotnet new install <feed>/Kazo.Os.Templates.<version>.nupkg
dotnet new hackeros-window  -n Contoso.Notes -o src/Contoso.Notes --appId com.contoso.notes --publisherId pub.contoso
dotnet new hackeros-command -n Contoso.Hello -o src/Contoso.Hello --appId com.contoso.hello --publisherId pub.contoso
dotnet new hackeros-service -n Contoso.Sync  -o src/Contoso.Sync  --appId com.contoso.sync  --publisherId pub.contoso
dotnet new hackeros-site    -n Contoso.Site  -o src/Contoso.Site  --appId com.contoso.site  --publisherId pub.contoso
dotnet new hackeros-host    -n Contoso.Os    -o src/Contoso.Os
dotnet sln add src/*/*.csproj
```

Each app project references `Kazo.Os.AppSdk*` and `Kazo.Os.App.Build`
(`PrivateAssets="all"`). At build time, `Kazo.Os.App.Build` writes `manifest.json`
from the `[AppManifest]` class, with the SDK version as the minimum, and
refuses a reference outside the SDK (`HOSAPP0030`) or an injection of a platform
service (`HOSAPP0031`).

## 5. Composing the host

The `hackeros-host` host is a complete Blazor WebAssembly PWA: the whole OS comes
from `Kazo.Os.Ecosystem.Host`, the apps are its references.

```xml
<!-- src/Contoso.Os/Contoso.Os.csproj -->
<ItemGroup>
  <!-- Vos apps, en projets… -->
  <ProjectReference Include="../Contoso.Notes/Contoso.Notes.csproj" />
  <ProjectReference Include="../Contoso.Hello/Contoso.Hello.csproj" IsSystemApp="true" />
  <!-- …ou en paquets d'apps. -->
  <PackageReference Include="Contoso.Tools" Version="2.3.0" />
  <!-- Les apps de base que vous gardez (retirez les autres). -->
  <PackageReference Include="Kazo.Os.Apps.Terminal" Version="$(KazoOsVersion)" IsSystemApp="true" />
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
package a `PackageId` outside the `Kazo.Os.*` space, reserved to the Kazo OS feed
by the `packageSourceMapping`.

## 7. Testing

- A command is tested without a browser: create the command with its manifest
  (`AppDefinitionMapper.ToManifest(new MyApp())`), a
  `TerminalExecutionContext` on fake gateways, and
  `KazoExecutionContext.Enter(app)` around `ExecuteAsync`
  ([`sdk-developer-guide.md`](sdk-developer-guide.md), testing a command).
- A host is tested like `Kazo.Os.Templates.E2E.Tests`: `dotnet run` of
  the host, then Playwright (administrator creation, Applications menu,
  Terminal).
- To verify that a version upgrade does not break your already published apps,
  keep, as Kazo OS does, an app package compiled against the old version and
  load it in the current host (`Tests/fixtures/sdk-compat`, ADR 0114 §4).

## 8. Publishing the host

```bash
dotnet publish src/Contoso.Os -c Release
```

The host template enables `PublishTrimmed` in `partial` mode: the framework is
trimmed, the Kazo OS assemblies and all the apps remain whole (roots
added by `Kazo.Os.App.Build`, ADR 0113). The `wwwroot` output is a static
site; the service worker registers only outside `localhost`. See
[`trimmed-publish.md`](../dev/trimmed-publish.md).

## 9. Upgrading

1. Produce the feed of the new version (§2).
2. Change `KazoOsVersion` in `Directory.Build.props`.
3. Minor: rebuild; read "Breaks outside the SDK" in
   [`sdk-packages.md`](sdk-packages.md) if you have a host. Major:
   rebuild **all** apps, the OS refuses those of the old major.
4. The regenerated manifests carry the new minimum: a rebuilt app
   no longer loads on an older OS.

### Moving to 2.0

A major version: OS 2.0 refuses an app compiled against 1.x, and a host that
references one no longer builds (`HOSAPP0021`). Every app must be
recompiled against the 2.x packages (2.2.2 today; verified by `Tests/fixtures/sdk-compat`,
where the 1.0.0 and 1.1.0 apps are refused and the 2.0.0 app runs).

- **Only break**: the `Kazo.Os.AppSdk.FileView` package no longer exists. An
  app that displayed `FileView` displays `KazoFileView` (file control
  provided by the OS, `Kazo.Os.AppSdk.Blazor.Files`); `FileTransfer` becomes
  `KazoFileOperations` (`Kazo.Os.AppSdk.IO`). The full table is in
  [`sdk-packages.md`](sdk-packages.md#migrating-a-1x-app).
- **OS context menu**: `Menus.Attach(name, element)` on any
  element, `Menus.Define` in C#, submenus, keyboard and long press
  (ADR 0150, [`context-menu.md`](context-menu.md)).
- **Entries in file menus**: `[RegisterFileContextMenu]` in the
  `[AppManifest]` class, capability `shell.context-menu`; the entry launches the app
  with an intent and the paths (ADR 0151, 0159).
- **Desktop**: `KazoDesktopSettings`, `.desktop` shortcuts (`KazoDesktopEntry`,
  ADR 0152, 0160, [`desktop.md`](../dev/desktop.md)).
- **Providing an OS control**: an app can provide `hackeros.file-view`
  and `hackeros.file-dialogs`; the administrator chooses the provider
  (ADR 0156, 0158, [`embedded-components.md`](embedded-components.md)).
- **Also**: `IAppEventGateway.PublishAsync` (ADR 0145) and
  `KazoCrossOriginIsolation` (ADR 0146), planned for a 1.3 that was never released.

The breaks outside the SDK for a host (file explorer, code editor,
the host's `IShellIconProvider`) are in
[`sdk-packages.md`](sdk-packages.md#breaks-outside-the-sdk).

### What 1.2 added

A minor: an app compiled against 1.0.0 or 1.1.0 ran without change
on OS 1.2. Its additions are part of 2.0.

- **Heavy tasks out of the window**: `KazoBackground.RunAsync` with a
  static method and serializable arguments, or a class
  `KazoBackgroundJob<TInput, TResult>`; progress, cancellation and
  `CheckpointAsync` through `KazoBackgroundContext`. The analyzer `HOSBG0001` to
  `HOSBG0004`, shipped in `Kazo.Os.AppSdk`, refuses a lambda that captures
  (ADR 0128, 0140, [`background-jobs.md`](background-jobs.md)).
- **Per-process limits**: `KazoProcess.GetResourceLimitsAsync`,
  `SetResourceLimitAsync`, `KazoResourceLimits`; `EMFILE`
  (`FileSystemErrorCode.TooManyOpenFiles`) and `EAGAIN`
  (`ProcessLimitExceededException`) to handle as ordinary I/O errors;
  new exit reasons `CpuLimitExceeded`,
  `MemoryLimitExceeded`, `OutOfMemory` (ADR 0125, 0139,
  [`process-limits.md`](../dev/process-limits.md)).
- **OOM killer**: `AppDefinition.OomScoreAdjust` so that an app is killed
  later (or earlier) when memory runs out; the OOM killer is off by
  default (ADR 0126, 0142, [`oom-killer.md`](../dev/oom-killer.md)).
- **Priority**: `KazoProcess.Nice`, `BasePriority` (ADR 0127,
  [`process-priority-and-unresponsive-windows.md`](../dev/process-priority-and-unresponsive-windows.md)).
- **Worker commands that listen to events**:
  `[assembly: KazoWireContext(typeof(MyJsonContext))]` so that
  `KazoEvents` and `Watch` go through the remote gateway (ADR 0124, 0138,
  [`worker-processes.md`](../dev/worker-processes.md)).

The exact list and the host's break outside the SDK (constructor of
`EcosystemBootCoordinator`) are in
[`sdk-packages.md`](sdk-packages.md#sdk-12).

### What 1.1 adds

A minor: an app compiled against 1.0.0 runs without change on OS
1.1 (verified by the frozen app of `Tests/fixtures/sdk-compat`). To use the
additions, set `KazoOsVersion` to `1.1.0` and rebuild; the app then requires
an OS 1.1 or newer.

- **Window title**: `WindowAppBase.SetWindowTitleAsync`, `WindowTitle`,
  `FormatWindowTitle("rapport.txt", modified: true)` → `*rapport.txt — Mon app`
  (ADR 0117, 0123, [`window-model.md`](window-model.md) §4).
- **Remembered window size**: `WindowOptions.RememberGeometry = false`
  so that a window does not take back its last size (ADR 0116, 0122,
  [`window-geometry.md`](../dev/window-geometry.md)).
- **Accounts and passwords**: `KazoUserAccounts` (`ListAsync`,
  `ChangePasswordAsync`, `ResetPasswordAsync` with the `users.manage` capability),
  `TerminalExecutionContext.ReadSecretLineAsync` for input without echo
  (ADR 0120, [`sensitive-settings.md`](../dev/sensitive-settings.md)).
- **User default applications**: `KazoApps.SetDefaultHandlerAsync`,
  `ResetDefaultHandlerAsync`, `GetDefaultHandlersAsync` (ADR 0121,
  [`user-file-associations.md`](../dev/user-file-associations.md)).

The exact list and the breaks outside the SDK for a host (three changes of
`Kazo.Os.Platform.Blazor` around window geometry) are in
[`sdk-packages.md`](sdk-packages.md#sdk-11).

## Decisions

- **nuget.org since 2.1.0** (ADR 0173); the local feed of ADR 0098 §2 remains
  for a version not published yet.
- **The `hackeros-solution` template** (ADR 0176, ADR 0207) is the
  recommended start for an app in its own repository.
- **Version pinned once** in the third-party repository: Kazo OS packages
  go together, mixing two versions is not supported.

## Tasks

- [x] Compatibility promise and refusal of other majors.
- [x] Local feed, `nuget.config`, pinned version.
- [x] Templates, host, apps as projects and as packages.
- [x] Tests, trimmed publishing, version upgrade.
- [x] SDK 1.1: additions and link to the breaks outside the SDK (`v3.1/4-sdk-1-1`).
- [x] SDK 1.2: additions and link to the break outside the SDK (`exec/10-final`).
- [x] SDK 2.0: current version, break, migration and additions (`desk/6-sdk-2`).
- [x] Packages on nuget.org and pointer to the `hackeros-solution` template (`todo/1-hackstore-solution-template`).
