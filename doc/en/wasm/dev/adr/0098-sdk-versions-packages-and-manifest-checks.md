# ADR 0098: NuGet versions and packages of the SDK and the platform, manifest checks at load time, templates and freeze of SDK 1.0

## Status

Accepted on 2026-10-17 (work item `v3/1-adr`, for the work items
`v3/6-sdk-versioning` and `v3/15-sdk-freeze`).

- **Complements:** [ADR 0019](0019-sdk-versioning-and-compatibility.md) (SemVer,
  SDK boundary, check of `sdkCompatibility` "at load time", never
  done), ADR 0047 (generated manifest, `BuiltInApplications.json`), ADR 0048
  (`IsSystemApp` on the host reference), ADR 0055 and 0059 (SDK
  boundary, `HOSAPP0030`/`HOSAPP0031`).
- **Replaces:** the developer build profile (`BuildProfileManifest`) of the
  migration analysis §14.1, already made useless by ADR 0047 and 0048.
- **Plan:** [`wasm-v3-completion-plan.md`](../plans/wasm-v3-completion-plan.md),
  lines `v3/6` and `v3/15`; gaps R3, R4, R5, R20, R21; deferred decision
  no. 11; Benoit's answer 2 (NuGet packages).

## Context

- The SDK libraries (`HackerOs.App.Abstractions`, `HackerOs.AppSdk`,
  `HackerOs.AppSdk.Blazor`, `.Terminal`, `.Web`, `.Web.Blazor`, `.FileView`,
  `HackerOs.Simulation.Abstractions`) have neither a version nor a `PackageId`. Only
  three `Shared` projects and six windowing projects
  import `Platform/Packaging.props` (single version `0.1.0-local`, public API
  analyzer). An app can only be built inside this repository
  (`ProjectReference` to `Shared/*`, `HOSAPP0030` otherwise).
- `sdkCompatibility` is only checked in its form
  (`AppManifestValidator.cs:34-38`); no current SDK version exists at
  runtime, and the generator hard-codes `1.0.0` as the minimum
  (`AppDefinitionMapper.cs:51`).
- Two apps can declare the same command name: `ShellCommandResolver`
  takes the first one found (`ShellCommandResolver.cs:43-56`).
- The SHA-256 hash of an asset is only checked in its form
  (`AppManifestValidator.cs:393-396`).
- `BuildProfileManifest`, its validator and its serializer are only used by
  their tests.
- The game repository (ADR 0092) must be able to build apps **and** a complete
  OS host with its own apps, without copying this repository.

## Decision

### 1. A single version for everything published

- All published libraries share one number, defined once in
  `build/Versions.props` (`HackerOsVersion`), imported by
  `Directory.Build.props`. `Platform/Packaging.props` inherits it instead of
  carrying `0.1.0-local`.
- Until the freeze: `0.9.0-preview.<n>`, `n` incremented by each work item that
  changes a published API. At the freeze (`v3/15`): `1.0.0`. Afterwards SemVer
  (ADR 0019): minor for an addition, major for a break.

### 2. Packages

| Package | Content | For |
|---|---|---|
| `HackerOs.App.Abstractions`, `HackerOs.AppSdk`, `HackerOs.AppSdk.Blazor`, `HackerOs.AppSdk.Terminal`, `HackerOs.AppSdk.Web`, `HackerOs.AppSdk.Web.Blazor`, `HackerOs.AppSdk.FileView`, `HackerOs.AppSdk.Icons`, `HackerOs.AppSdk.VirtualKeyboard`, `HackerOs.Theming.Abstractions` | the public SDK | writing an app |
| `HackerOs.App.Build` | `build/HackerOs.Apps.targets` as `buildTransitive`, the manifest generator and the SDK boundary rules | letting an app outside the repository produce its `manifest.json` as is done here |
| `HackerOs.Platform.*`, `HackerOs.Infrastructure.Browser`, `HackerOs.Windowing.*`, `HackerOs.Theming.Blazor`, `HackerOs.MobileShell.Blazor` | the platform | composing an OS host |
| `HackerOs.Ecosystem.Host` | the host composition (today in the `HackerOs.Ecosystem` exe: `EcosystemServiceCollectionExtensions`, root components), moved into a library; the exe becomes a shell that calls it | letting another repository build its own PWA host with its apps in addition to the base apps |
| `HackerOs.Apps.*`, `HackerOs.Commands` | the base apps and commands | including them in another host |
| `HackerOs.Templates` | the `dotnet new` templates (point 6) | starting a project |

- `Simulation.Abstractions` is published because the SDK exposes it, but remains
  outside the allowed references of an app (ADR 0055).
- `build/pack-local.sh` (and `.ps1`) runs `dotnet pack` on everything
  publishable into `artifacts/local-nupkg` (not versioned in git). A sample `nuget.config`
  shows how another repository points to this folder. GitHub
  Packages will come when CI works again; nothing is pushed to nuget.org.
- The host targets accept an app **`PackageReference`** with the same
  metadata as a `ProjectReference` (`IsSystemApp="true"`, lazy
  loading). The `HOSAPP0030`/`HOSAPP0031` rules accept the SDK packages
  like the SDK projects.

### 3. SDK compatibility checked at load time

- `HackerOs.App.Abstractions` exposes `HackerOsSdk.Version`, taken from its
  assembly version at build time.
- The generator writes as the minimum **the SDK version the app was
  compiled against** (instead of the hard-coded `1.0.0`), unless the app declares something else.
- `AppCatalog` rejects an app whose range does not contain `HackerOsSdk.Version`
  (SemVer precedence, prereleases included): code `catalog.sdk.incompatible`,
  app listed as "incompatible" in Settings with the requested range, never
  launched.
- Support policy (deferred decision no. 11): the OS loads any app
  compiled against a version **of the same major** lower than or equal to
  its own; an API is removed only at the next major, after at
  least one minor marked `[Obsolete]` (ADR 0019).

### 4. Duplicate command names

- **At build time**: the aggregation of `BuiltInApplications.json` rejects two apps
  that declare the same command name or alias, or a shell built-in
  name (ADR 0064), with the new code **`HOSAPP0022`**.
- **At load time** (apps coming from elsewhere later): a system app keeps the
  name; otherwise the first in activation order keeps it. The other
  app's command is not registered, the app remains launchable, and the warning
  `catalog.command.duplicate` is written to `/var/log/syslog`.

### 5. Asset hash checked

- The generator computes the SHA-256 of each declared asset at build time.
- The platform checks an asset's hash at its **first read** in the
  session (icon, payload schema, embedded file) and keeps the result
  in memory. On a mismatch: the asset is rejected, the app is marked "damaged"
  in Settings, and the event goes to `/var/log/syslog`. Lazily loaded
  assemblies remain covered by the integrity of Blazor's boot manifest.

### 6. `dotnet new` templates

`HackerOs.Templates` provides `hackeros-window` (window app),
`hackeros-command` (command), `hackeros-service` (service),
`hackeros-site` (ASP.Hack site) and `hackeros-host` (PWA host that includes the base
apps and the repository's apps). Each template is built and tested by a test
that runs `dotnet new` then `dotnet build` against the local feed.

### 7. Build profile removed

`BuildProfileManifest`, `BuildProfileValidator`, `BuildProfileJsonSerializer`
and their tests are deleted. The selection of shipped apps is the list of
host references (ADR 0047, 0048). The value
`CapabilityGrantSource.BuildProfile` remains if saved grants already
use it (stored value), otherwise it goes too.

### 8. SDK 1.0 freeze (`v3/15`)

- From `v3/6` on, every published library has the public API analyzer, with
  its APIs in `PublicAPI.Unshipped.txt`.
- `v3/15` moves everything to `PublicAPI.Shipped.txt`, publishes `1.0.0`, and adds
  a test that loads into the current OS an app compiled against the `1.0.0` package
  kept in `Tests/fixtures`.

## Consequences

- `v3/6` and `v3/3` both touch `AppDefinition`: whichever is merged second
  takes over from the other (plan, §4).
- `sdk-developer-guide.md` gains "build an app from another repository"
  (`v3/6`) then the complete third-party repository guide (`v3/15`);
  `building-and-testing.md`, `project-overview.md`, `sdk-reference.md` and
  `app-installation.md` are updated.
- Moving the host composition into `HackerOs.Ecosystem.Host` does not change
  behavior; `build/verify-hosts.sh` checks it.
