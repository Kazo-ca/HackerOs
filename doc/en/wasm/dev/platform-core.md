> [Version française](../../../fr/wasm/dev/platform-core.md)

# Platform: `HackerOs.Platform.Core`

## Purpose

`Platform/HackerOs.Platform.Core` contains the OS services, with no graphical
interface: everything that makes up the simulated system (files, processes, shell, sessions,
capabilities, simulated network, logs). It implements the gateways that the SDK
exposes to apps (`IAppExecutionContext`, `Hack*`) and must never be
referenced by an app (boundary rule, ADR 0055). The contracts it
implements are in `Shared/HackerOs.Simulation.Abstractions` and
`Shared/HackerOs.App.Abstractions`; the graphical interface is in
`HackerOs.Platform.Blazor`, browser storage in
`HackerOs.Infrastructure.Browser` ([`browser-infrastructure.md`](browser-infrastructure.md)).

Overview of the layers: [`project-overview.md`](project-overview.md).
About 240 C# files (51,000 lines); the folder is read by subsystem.

## What is where

| Folder | Role | Documents |
|---|---|---|
| `FileSystem/` | File service, mount router, authorization, audit, `/proc` and `/dev` providers, tree bootstrap | [`virtual-proc-and-dev.md`](virtual-proc-and-dev.md), [`hack-file-api.md`](../framework/hack-file-api.md) |
| `Execution/` | Per-instance gateways (`AppFileSystemGateway`, `AppProcessGateway`...), `AppExecutionContextFactory`, background tasks (`BackgroundJobService`) | [`app-sdk.md`](../framework/app-sdk.md), [`background-jobs.md`](../framework/background-jobs.md) |
| `Processes/` | In-memory processes (`InMemoryProcessManager`, control blocks), environment, limits (`ResourceLimitEnforcer`), memory killer (`OomKiller`), sampling | [`process-environment.md`](process-environment.md), [`process-limits.md`](process-limits.md), [`oom-killer.md`](oom-killer.md) |
| `Lifecycle/` | App lifecycle: launch, shutdown, `AutoStart` services, app activation (`AppLifecycleOrchestrator`) | [`app-installation.md`](app-installation.md) |
| `Shell/` | `hsh` interpreter (parsing, expansion, redirections, pipes, jobs, `ulimit`, scripts), completion, `ShellHostService` | [`shell-interpreter.md`](shell-interpreter.md), [`shell-host.md`](../framework/shell-host.md) |
| `Sessions/`, `Elevation/` | Local accounts and passwords (hashing), login session, `sudo`, administrator authentication agent | [`admin-authentication-agent.md`](admin-authentication-agent.md) |
| `Policy/` | Capability granting, dangerous capabilities (`DangerousCapabilityGate`), per-app decisions | [`dangerous-capability-approval.md`](dangerous-capability-approval.md), [`capabilities.md`](../framework/capabilities.md) |
| `Settings/`, `Appearance/` | Settings documents as files (`OsSettingsService`, validators, migration from the old store), appearance, accessibility, user themes | [`settings-files.md`](../framework/settings-files.md), [`sensitive-settings.md`](sensitive-settings.md), [`accessibility.md`](accessibility.md) |
| `Apps/`, `Discovery/` | App catalog and installation (`AppInstallationService`), manifests, entry point discovery | [`app-installation.md`](app-installation.md) |
| `Intents/` | Intent resolution and per-user file associations | [`user-file-associations.md`](user-file-associations.md), [`app-intents.md`](../framework/app-intents.md) |
| `ContextMenus/`, `ComponentProviders/` | OS context menu registry, component contract providers | [`context-menu.md`](../framework/context-menu.md), [`embedded-components.md`](../framework/embedded-components.md) |
| `Network/` | Simulated network: service, listening port table, DNS resolution, relay to the server, simulated sites (`WebAppActivator`) | [`listening-ports-and-dns.md`](../framework/listening-ports-and-dns.md), [`sdk-network.md`](../framework/sdk-network.md), [`server-network-relay.md`](server-network-relay.md) |
| `ServerConnection/` | Optional connection to the server, presence, synchronization scheduler, conflicts, sync client | [`synchronization.md`](synchronization.md), [`server-hosting.md`](server-hosting.md) |
| `Keyring/` | User keyring, envelope encryption, session key | [`sensitive-settings.md`](sensitive-settings.md) |
| `Diagnostics/`, `Metrics/` | Kernel log (`KernelLog`) and `/var/log` files, rotation, redaction, CPU and I/O counters | [`logging.md`](logging.md), [`system-metrics.md`](system-metrics.md) |
| `Backup/`, `Storage/` | Machine backup and restore, space monitoring | [`system-backup.md`](system-backup.md) |
| `Administration/` | SDK administration gateways (system settings, power, backup, storage diagnostics, accounts) | [`sdk-reference.md`](../framework/sdk-reference.md) |
| `Clipboard/`, `Notifications/`, `Time/`, `Power/`, `Isolation/`, `Globalization/`, `Events/` | Clipboard, notifications, simulated clock, shutdown, cross-origin isolation, session language, event bus | [`clipboard.md`](clipboard.md), [`worker-processes.md`](worker-processes.md#cross-origin-isolation-adr-0131-adr-0143) |
| `WebData/` | Database of ASP.Hack sites (`WebSiteDatabaseStore`) | [`asp-hack-site-data.md`](../framework/asp-hack-site-data.md) |

## Services and composition

- `AppExecutionContextFactory` builds the `IAppExecutionContext` of each
  instance (window, service, command) from the platform singletons
  and the granted capabilities. Each gateway checks the capability it
  requires before acting (`AppCapabilityChecker`).
- Services are registered by `AddHackerOsEcosystem`
  (`OS/HackerOs.Ecosystem.Host`), which declares the services of this layer;
  `AddHackerOsPlatformLocalization` (`Globalization/`) is part of it.
- Several services have an `InMemory*` implementation (tests, mode without a
  browser); real persistence comes from the browser infrastructure.

## Things to know

- This project has no dependency on `Platform.Blazor` or on
  `Infrastructure.Browser`: code that touches the browser goes through
  interfaces (for example `IStorageCapacityProbe`, `IHostAccessibilityProbe`).
- Settings, log and process files are files of the virtual
  tree: writing a setting goes through `FileSystemService` and its
  authorization rules, not through a separate store (ADR 0044, ADR 0046).
- Simulated sites (`Network/`) are fictional OS content, declared
  by the OS and not by apps.

## Tests

`Tests/HackerOs.Platform.Core.Tests` covers the subsystems above; the
per-feature tests are in the `Tests/HackerOs.Platform.*` projects,
`Tests/HackerOs.Ecosystem.Tests` and `Tests/HackerOs.Commands.Tests`. Commands:
[`building-and-testing.md`](building-and-testing.md).
