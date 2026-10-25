> [Version française](../../../../fr/wasm/dev/adr/0114-sdk-1-0-freeze.md)

# ADR 0114: SDK 1.0 freeze, scope of the compatibility promise and compatibility app

## Status

Accepted on 2026-10-17 (work item `v3/15-sdk-freeze`). Two questions are
asked to Benoit in the work item thread (§2 and §3); what follows applies the
recommended answer while awaiting it.

- **Implements:** [ADR 0098](0098-sdk-versions-packages-and-manifest-checks.md)
  §8 (SDK 1.0 freeze).
- **Clarifies:** [ADR 0019](0019-sdk-versioning-and-compatibility.md) (SemVer and
  deprecation: which packages the promise covers, binary compatibility
  test), ADR 0098 §1 (single version).
- **Completes:** the removal announced by ADR 0065 ("`BrowserClipboard` remains
  for one version, `[Obsolete]`, then will be removed") and by ADR 0056 §12
  (`Get/SetFileReferencesAsync` forms).
- **Document:** [`sdk-packages.md`](../../framework/sdk-packages.md),
  [`building-on-hackeros.md`](../../framework/building-on-hackeros.md).

## Context

- Since `v3/6`, the 39 published libraries go through the public API analyzer,
  with their whole surface in `PublicAPI.Unshipped.txt`, at version
  `0.9.0-preview.1`.
- The OS rejects an app whose minimum SDK does not have the same major as its own
  (ADR 0098 §3). An app compiled at `0.9.0-preview` is therefore rejected by a
  `1.0.0` OS: moving to 1.0 already breaks all preview apps, whatever
  is removed.
- All libraries carry a single number (ADR 0098 §1). A break
  in any one of them therefore forces, under strict SemVer, a new major
  of everything, SDK included.
- The SDK still contained three `[Obsolete]` APIs: `BrowserClipboard`
  (`HackerOs.AppSdk.Blazor`, replaced by `HackClipboard.SetTextAsync`) and
  `IAppClipboardGateway.GetFileReferencesAsync` / `SetFileReferencesAsync`
  (replaced by `Get/SetFileDropListAsync`). No app in the repository
  uses them (rule checked by `app-sdk.md`).
- ADR 0019 §3 asked for an automatic test "an app compiled against an earlier
  version of the SDK loads and runs", never written.

## Decision

### 1. Version 1.0.0, whole surface in `Shipped`

- `build/Versions.props`: `HackerOsVersion` = `1.0.0`.
- Each `PublicAPI.Unshipped.txt` is moved into its `PublicAPI.Shipped.txt`
  (sorted lines, no duplicates) and keeps only `#nullable enable`.
- After the freeze, an added API goes into `Unshipped` (`RS0016`); a removed
  or changed API is rejected at build time (`RS0017`) until it is
  written `*REMOVED*` in `Unshipped`. Any break is therefore visible in
  review, whatever the package.
- A release moves `Unshipped` into `Shipped` and increases the version:
  patch (`1.0.x`) with no API change, minor (`1.x.0`) for an addition,
  major for an SDK break (§2).

### 2. The promise covers the SDK (question 2, default applied)

The SemVer promise "an app compiled against `1.x` runs on any `1.y` OS,
y ≥ x" covers what an app compiles, that is, the packages that the boundary
rule allows it (`HOSAPP0030`), apps excluded:

| Covered (break = major) | Versioned along, no promise (break possible in a minor) |
|---|---|
| `HackerOs.App.Abstractions`, `HackerOs.Simulation.Abstractions`, `HackerOs.Theming.Abstractions`, `HackerOs.AppSdk`, `.Blazor`, `.Terminal`, `.Web`, `.Web.Blazor`, `.FileView`, `.Icons`, `.VirtualKeyboard`, `HackerOs.Theming.Blazor`; the build contract of `HackerOs.App.Build` (properties, items, `HOSAPP*` codes); the manifest format | `HackerOs.Platform.*`, `HackerOs.Windowing.*`, `HackerOs.Infrastructure.Browser`, `HackerOs.Taskbar.Blazor`, `HackerOs.MobileShell.Blazor`, `HackerOs.Server.Contracts`, `HackerOs.Ecosystem.Host`, the public types of the base apps (`HackerOs.Apps.*`, `HackerOs.Commands`) |

- A break outside the SDK is still written `*REMOVED*` and is described in the
  "Breaks outside the SDK" section of [`sdk-packages.md`](../../framework/sdk-packages.md)
  with the version that brings it. A host from another repository may have to
  adapt in a minor; an app never.
- What is observable by an app remains covered even when the code is in
  the platform: identifiers and intents of the base apps, documented OS file
  paths and formats (`/etc`, `~/.config`, `/var/log`), shell commands
  and their options.
- **Why:** the `exec/*` work items will touch the platform again
  (execution, server); freezing its types too would impose an SDK 2.0 for
  a change that no app sees.

### 3. Removals done before the freeze (question 1, default applied)

`BrowserClipboard` (and its module `wwwroot/clipboard.js`),
`IAppClipboardGateway.GetFileReferencesAsync` and `SetFileReferencesAsync` are
removed. This is the only moment when they cost nothing: 1.0 already rejects
preview apps (context). 1.0 contains no `[Obsolete]` API.

### 4. Compatibility app kept in `Tests/fixtures`

- `Tests/fixtures/sdk-compat/src/Contoso.Sdk1Compat`: an app from an "other
  repository" (window `com.contoso.sdk1-window` and command `sdk1-compat`) that
  writes and rereads a file through `HackFile`, reads its arguments and writes to
  standard output. Its empty `Directory.Build.*` cut off those of the repository.
- `produce-fixture.sh` builds it against the packages of a local feed, in
  a temporary folder and an isolated NuGet cache, and writes
  `packages/Contoso.Sdk1Compat.1.0.0.nupkg`, **committed and never
  rebuilt** (the folder's `.gitignore` lifts the exclusion of `.nupkg`).
- `Tests/HackerOs.SdkCompatibility.Tests` (unit suite, no browser):
  the current catalog accepts its manifests; each HackerOS type and member
  referenced by its metadata still exists; each method is compiled by
  the JIT against the current SDK (`RuntimeHelpers.PrepareMethod`, which throws
  `MissingMethodException` on a changed signature); the command, created
  as the OS creates it, runs. Verified: changing the return type of
  `HackFile.ReadAllTextAsync(string, CancellationToken)` makes two of
  these tests fail.
- `HackerOs.Templates.E2E.Tests`: a host generated by `hackeros-host`, with
  a `PackageReference` to this package (source `hackeros-sdk-fixtures`), starts
  in Chromium; the window and the command of 1.0 work.
- At a 2.0: `produce-fixture.sh` produces a `2.0.0` app; the 1.0 one
  becomes a **rejection** test (different major, ADR 0098 §3), since the 2.0 OS
  must no longer load it.

### 5. Third-party repository guide

[`building-on-hackeros.md`](../../framework/building-on-hackeros.md) describes a repository that
builds apps and a host on HackerOS: local feed, `nuget.config`,
pinned version, templates, apps and host, app packages, trimmed publish,
tests, version upgrade and what the promise covers.

## Consequences

- The generated manifests carry `sdkCompatibility.minimumVersion: 1.0.0`.
- An app or host built at `0.9.0-preview.*` must be rebuilt
  against the `1.0.0` packages (the game repository, ADR 0092).
- Each work item that touches a public API updates `Unshipped`; the next
  release chooses the version from this file (§1, §2).
- The `HackerOs.SdkCompatibility.Tests` unit suite runs with
  `dotnet test HackerOs.sln`; the E2E test with the templates suite.

## Rejected options

- **Promise on all packages** (question 2): safe for a third-party host,
  but any break in the platform would impose an SDK 2.0.
- **Keep the obsolete APIs until 2.0** (question 1): nothing is gained,
  the apps that would use them are already rejected by 1.0.
- **Compare APIs with a tool (`Microsoft.DotNet.ApiCompat`)** against the
  1.0.0 packages: useful later for the whole surface, but does not prove
  that a real app loads and runs; the public API analyzer already
  reports removals.
