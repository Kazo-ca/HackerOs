# Startup Performance

## Purpose

This document describes the standalone Blazor WebAssembly startup path, its
measured Debug payload, and the optimizations that keep persistent boot work off
the critical path where correctness permits.

## Startup architecture

The browser must download and initialize the eager .NET runtime and platform
assemblies before Razor can render the HackerOS boot surface. The ten first-party
system app assemblies are build-known lazy assets and aren't fetched until their
first launch.

After the WebAssembly runtime starts, `EcosystemBootCoordinator` creates the
filesystem root first. Settings seeding, policy validation, catalog
reconciliation, administrator-group validation, and user loading then run as
independent IndexedDB operations. Their results are joined before authentication
is displayed so recovery behavior remains deterministic.

## Measured Debug payload

The August 2026 .NET 10 Debug build produces approximately 12.4 MB of compressed
eager framework assets. MudBlazor accounts for approximately 2.28 MB. The ten
lazy system app assemblies total approximately 162 KB compressed and are not the
cause of initial download latency.

A trimmed Release publish reduces all compressed framework assets to about
3.38 MB, of which approximately 128 KB is the ten lazy app assemblies. The eager
Release transfer set is therefore about 3.26 MB, roughly 74% smaller than Debug.
Debug builds also disable the WebAssembly Jiterpreter while a debugger is
attached, so they intentionally start and run more slowly than published Release
builds.

## Optimizations

- App manifests remain lazy and are excluded from eager assembly loading.
- Unchanged catalog records are no longer rewritten on every reload.
- Canonical settings documents are seeded in one IndexedDB transaction.
- Independent post-root storage checks run concurrently.
- Published app manifests use package-qualified paths, preventing the ten
  referenced system apps from colliding at `app.manifest.json` during publish.
- Reflection-discovered lazy entry-point assemblies are explicit linker roots,
  keeping them available as lazy assets without making them eager downloads.
- The filesystem root remains the ordered first operation because every later
  session depends on a valid canonical database and root entry.

## Development usage

Use Debug when C# breakpoints are required:

```powershell
dotnet run --project OS/HackerOs.Ecosystem/HackerOs.Ecosystem.csproj --configuration Debug --launch-profile http
```

Use Release when evaluating user-perceived startup performance:

```powershell
dotnet run --project OS/HackerOs.Ecosystem/HackerOs.Ecosystem.csproj --configuration Release --launch-profile http
```

Release publish trimming and compression make it the representative deployment
measurement. Debug startup should not be used as a production performance
baseline.

## Task status

- [x] Measure eager, lazy, and symbol payload groups.
- [x] Confirm all first-party app assemblies remain lazy.
- [x] Avoid unchanged catalog writes during boot.
- [x] Batch settings seeding into one transaction.
- [x] Parallelize independent post-root storage validation.
- [x] Restore a clean multi-app Release publish path.
- [x] Preserve reflection-discovered lazy apps through Release trimming.
- [ ] Split MudBlazor-dependent platform UI from the eager shell assembly.
