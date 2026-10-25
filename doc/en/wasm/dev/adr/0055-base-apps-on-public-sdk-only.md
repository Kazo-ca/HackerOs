> [Version française](../../../../fr/wasm/dev/adr/0055-base-apps-on-public-sdk-only.md)

# ADR 0055: Base apps on the public SDK only

## Status

Accepted on 2026-10-10.

- **Plan phase:** phase 4, Complete SDK and base apps
  ([`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), section 8.1,
  decision D21, guiding principle 3).
- **Complements:** ADR 0019. Its rules 1 and 2 (an app only consumes the public
  SDK, Platform Core and the browser infrastructure stay hidden from it)
  were never verified: ten projects under `Apps/` reference
  `HackerOs.Platform.Core`. This ADR makes them verifiable at build time, extends the
  list of permitted references to the SDK libraries that have appeared since, and
  adds the rule on injection. The rest of ADR 0019 (versioning,
  deprecation) does not change.
- **Builds on:** ADR 0016 (MudBlazor allowed in apps), ADR 0043
  (ambient context and static `Hack*` facades), ADR 0048 (trust list
  of system apps), ADR 0049 (normal and dangerous capabilities,
  approval window, `auth.log`).
- The APIs added to the SDK to replace each internal service are described
  in ADR 0056.

## Context

Principle 3 of the plan wants a system app to have nothing more than a third-party
app. Today, that is false on three levels.

**Project references.** State of `rebuild/base` on 2026-10-10 (dfd9c47):

| Project | Forbidden reference | Used for |
|---|---|---|
| `HackerOs.Apps.Settings` | `Platform.Core`, `Infrastructure.Browser` | catalog, uninstall, permissions, theme, server and synchronization, clearing IndexedDB |
| `HackerOs.Apps.SystemMonitor` | `Platform.Core` | `AppCatalog`, `AppLifecycleOrchestrator` |
| `HackerOs.Apps.FileExplorer` | `Platform.Core` | `AppCatalog`, `IAppEnablementRegistry` |
| `HackerOs.Apps.Diagnostic` | `Platform.Core`, `Infrastructure.Browser` | IndexedDB inspector |
| `HackerOs.Apps.Browser` | `Platform.Core` | none (only an unused `using`) |
| `HackerOs.Apps.Calculator`, `HackPaint`, `IconViewer`, `TextEditor` | `Platform.Core` | none |
| `HackerOs.Commands` | `Platform.Core`, `Server.Contracts` | `ShellErrorText` (`cd`), `IServerConnectionService` and `IProxyClient` (`ping`, `curl`, `cat`, `nmap`) |
| `HackerOs.AppSdk.Terminal` (SDK library) | `Platform.Core` | `ShellInterpreter`, `IElevationService` |

**Injection.** Even without a forbidden reference, an app can inject a raw
OS service, because their interfaces live in shared projects
(`HackerOs.Simulation.Abstractions`, `HackerOs.App.Abstractions`). These
services check no capability:

| Injected service | Interface project | Apps |
|---|---|---|
| `ISessionService` | Simulation.Abstractions | FileExplorer, TextEditor, CodeEditor, Settings, `TerminalPanel` |
| `IProcessManager`, `IEventBus` | Simulation.Abstractions | SystemMonitor |
| `ISimulatedNetworkService` | Simulation.Abstractions | Browser, `ping`, `curl`, `cat`, `nmap` |
| `IElevationService` | App.Abstractions | `TerminalPanel` |
| `IServiceProvider` | .NET | Settings (`AppPermissionsPanel` pulls `AppPermissionAdministration` from it) |
| `IJSRuntime` for anything other than importing its module | .NET | Diagnostic (`navigator.clipboard`), Settings (`location.reload`, deletion of the IndexedDB database) |

**Verification.** Nothing prevents adding such a dependency today:
no test or build step checks an app project's references or
injections.

## Decision

### 1. Scope

The rules apply:

- to every app project, that is, every project whose `HackerOsAppProject` is
  `true` (projects under `Apps/`, ADR 0047);
- to the SDK libraries: `Shared/HackerOs.AppSdk` and
  `Shared/HackerOs.AppSdk.*`. An app must not be able to reach the OS through
  an SDK library that would do it on its behalf.

The hosts (`OS/`, `test/`, `Server/`), `Platform/`, `Infrastructure/` and the
test projects are not concerned.

### 2. Permitted project references

An app project can only reference:

| Project | Role |
|---|---|
| `Shared/HackerOs.App.Abstractions` | manifest, `AppDefinition`, capabilities, `VirtualPath` |
| `Shared/HackerOs.Simulation.Abstractions` | data types (processes, paths, simulated network...) |
| `Shared/HackerOs.Theming.Abstractions` | theme tokens and definitions |
| `Shared/HackerOs.Game.Abstractions` | game content |
| `Shared/HackerOs.AppSdk` and `Shared/HackerOs.AppSdk.*` | the SDK |
| `Platform/HackerOs.Theming.Blazor` | theme components (catalog, preview); it only depends on `Theming.Abstractions` |
| another project under `Apps/` | code sharing between apps |

Any other `ProjectReference` fails the build with **`HOSAPP0002`**, which
names the project and the reference. This is notably the case for
`Platform.Core`, `Platform.Blazor`, `Windowing.*`, `Taskbar.Blazor`,
`MobileShell.Blazor`, `Infrastructure.*`, `Server.*` (including
`Server.Contracts`) and the hosts. `PackageReference`s remain free:
a NuGet package does not give access to OS services.

An SDK library can only reference the four `*.Abstractions` projects
above and other SDK libraries. Any other reference
fails with **`HOSSDK0001`**.

The check is an MSBuild target in `build/HackerOs.Apps.targets`,
run before compilation, next to manifest generation.

### 3. Permitted injection

In an app project, a service can only be obtained by injection
(`@inject`, `[Inject]` property, constructor parameter of an entry class
built by `ActivatorUtilities`, ADR 0034 section 2) if its
type is:

1. defined in an SDK library (`HackerOs.AppSdk`, `HackerOs.AppSdk.*`);
2. a MudBlazor service (ADR 0016), for example `MudBlazor.IDialogService`;
3. `Microsoft.JSInterop.IJSRuntime`, **only** to import a JavaScript module
   of the app itself
   (`import`, `./_content/<app project>/...`), as provided by
   `AGENTS.md`. Any other call (`navigator.*`, `location.*`, `eval`, module
   of another project) is forbidden.

Everything else is forbidden, in particular: the types of
`HackerOs.Simulation.Abstractions` and `HackerOs.App.Abstractions`
(`ISessionService`, `IProcessManager`, `IEventBus`,
`ISimulatedNetworkService`, `IElevationService`, `IAppCatalogRepository`...),
`IServiceProvider`, `ILogger<T>` (logging goes through
`IAppLoggingGateway`, ADR 0045) and `NavigationManager`.

These types remain usable as **data**: an app can handle a
`VirtualPath`, a `ProcessRecord` or an `AppManifest`. Only obtaining an
OS service outside the SDK is forbidden.

Checks:

- **`HOSAPP0003`**, at build: the manifest generator already loads the app's
  assembly (ADR 0047). It additionally walks all its types and reports
  each `[Inject]` property and each constructor parameter of an entry class
  whose type is not permitted. The build fails.
- **Source test** (`HackerOs.Ecosystem.Tests`): for each
  `.razor` and `.cs` file under `Apps/`, each `Invoke*Async` call on an
  `IJSRuntime` must have the identifier `"import"` and the path
  `./_content/<project name>/`. A call on the `IJSObjectReference` of the imported
  module is free: it is the app's code.
- SDK libraries are not subject to `HOSAPP0003`, but
  `HOSSDK0001` forbids them from referencing the implementation: they can
  therefore only inject SDK contracts or abstractions. A review rejects
  an SDK library that injects a raw `Simulation.Abstractions` service in order
  to hand it to an app.

### 4. How an app obtains an OS service

Only through the SDK:

- a **gateway** on `IAppExecutionContext`, specific to the instance, which
  checks the required capability on every call;
- a static `Hack*` **facade** that reads the ambient context (ADR 0043) and
  calls the gateway. This is the form recommended to developers.

Each new gateway follows the model of existing gateways
(`Intents`, `Services`, `Watch`): an interface member with a default
"not supported" implementation, so that existing implementations
of `IAppExecutionContext` still compile (ADR 0019). The Platform Core trusted
factory provides the real implementation.

A missing capability throws `HackUnauthorizedAccessException` (decision D5) and
goes through `IAppPermissionErrorGateway`, like the file API. The capability required
by each API, and its classification, are set by ADR 0056. A new
capability enters `AppCapabilities` with its classification and risk
sentence, as ADR 0049 requires.

### 5. OS authentication agent

Some operations require an administrator in addition to the app's capability
(uninstalling for the whole machine, changing the security policy,
resetting the machine...). Like `polkit` on Linux, **the app never sees
the password**:

1. the app calls the SDK API, without credentials;
2. the gateway checks the capability, then asks the OS authentication
   agent to confirm an administrator;
3. the agent shows its own window, drawn by the platform and not by
   the app, of the same visual family as the approval window of
   ADR 0049: app name, operation requested in plain language, an administrator's
   identifier and password. In a terminal, the request is made in text
   mode, like `sudo`;
4. verification reuses the `sudo` verifier (ADR 0042); the
   result is written to `/var/log/auth.log` (app, operation,
   administrator, outcome);
5. refusal or cancellation: `HackUnauthorizedAccessException`.

A process launched by `sudo` (`IsRootElevated`, ADR 0042) already acts as root:
the agent asks for nothing more. APIs that go through the agent are marked
"administrator" in ADR 0056.

Passwords that the app must legitimately enter for a third-party service (the
optional server account, for example) are not concerned.

### 6. Transition

The violations listed in the context will not disappear in a single
change. The MSBuild target therefore reads a temporary exemption list,
`HackerOsSdkBoundaryExemption`, declared in `build/HackerOs.Apps.targets`:
one item per project still in violation, with the ADR and the work item that
will fix it. An exempted project produces a warning instead of an error.

- Each work item that fixes a project removes its exemption in the same
  change.
- The last work item of the phase (ADR 0058, `4-sdk-boundary-final`) removes the
  exemption mechanism.
- No new project can be added to the list.

### 7. System apps and trust list

The host's trust list (ADR 0048) does not change shape. A system
app automatically receives the dangerous capabilities it declares: it
uses the same SDK as any other app, but without an approval window. A non-system
app that declares a dangerous capability goes through the approval window
of ADR 0049 at launch. The list changes
that this phase entails are decided by ADR 0056.

## Consequences

- A third-party app can do everything a system app does, with the same
  APIs and the same capabilities; only approval differs.
- The build catches a forbidden dependency as soon as it is added, instead of
  a review.
- The network commands, the terminal and the Settings app lose their direct
  access: the logic for server fallback, access tokens and
  administrator verification moves into Platform Core, behind the SDK.
- The architecture is a bit heavier on the platform side: each need of a
  system app becomes a gateway to write and test.

## Alternatives rejected

- **Architecture test only on `Platform.Core` and `Platform.Blazor`**
  (plan text, section 8.1): it lets `Infrastructure.Browser`,
  `Server.Contracts` and the injection of `Simulation.Abstractions`
  services slip through, which are the majority of real violations.
- **Move the service interfaces out of `Simulation.Abstractions`**
  so that apps can no longer name them: correct in the long run, but this
  project is also consumed by the server and by Platform Core; moving it
  would touch many files while adding nothing to the type-based
  check of `HOSAPP0003`.
- **Roslyn analyzer** instead of the MSBuild target and the generator: more
  precise for the `IJSRuntime` call, but one more project to maintain; the
  generator already loads the assembly.

## References

- [`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), sections 3 and 8.1.
- ADRs 0016, 0019, 0034, 0042, 0043, 0047, 0048, 0049; ADR 0056 (APIs),
  ADR 0058 (work items).
- `build/HackerOs.Apps.targets`, `Tools/HackerOs.Tools.ManifestGenerator`,
  `Shared/HackerOs.AppSdk/IAppExecutionContext.cs`.
