> [Version française](../../../fr/wasm/dev/app-installation.md)

# Uninstalling and reinstalling apps

Phase 2 of the [rebuild plan](plans/ecosystem-rebuild-plan.md) (section 6.3, decisions
D10 and D11), work item `rebuild/2-app-uninstall` of [ADR 0048](adr/0048-system-apps-and-uninstall.md).
System apps (D9) are described in [`app-model.md`](../framework/app-model.md#system-apps-adr-0048-1).

## Goal

Uninstalling an app removes it from the OS without erasing anything: it disappears from the
launcher, from search, from "Open With" and from the terminal, can no longer be launched
by any route, and its open windows close. Its settings
(`~/.config/<appId>/`, `/etc/<appId>/`), its logs and the user's
files stay in place: a reinstall finds everything again.

The state lives in two text files, as under Linux: `cat` shows what is
removed, `nano` has the same effect as the Settings app. It replaces the IndexedDB
`IsEnabled` flag of ADR 0032.

## `apps.config` files

| Scope | File | Owner and mode | Who writes |
| --- | --- | --- | --- |
| Machine | `/etc/hackeros/apps.config` | `system:system 0644` | an administrator, through `sudo` or the OS authentication agent (ADR 0055 §5) |
| User | `~/.config/hackeros/apps.config` | the user, `0644` | the user themselves |

```text
# /etc/hackeros/apps.config
[uninstalled]
org.hackeros.hack-paint=true
org.hackeros.calculator=true
```

Reading rules (`AppsConfigDocument.Read`):

- `.config` grammar of ADR 0011, read line by line without a schema; only the
  keys of `[uninstalled]` count and the last occurrence of a key wins;
- `true`: uninstalled; `false` or absence: installed; any other value: line
  ignored with a warning in `/var/log/syslog`;
- identifier unknown to this build: ignored **without** a warning, line
  kept (the same file can serve several builds);
- identifier of a system app, or of an app on which a system app depends: ignored with
  a warning. Editing the file therefore does not bypass the protection;
- file missing or unreadable: nothing uninstalled.

Writes (`ConfigDocumentEditor`) touch only the line concerned:
comments, order and unknown identifiers are preserved. Reinstalling removes
the line.

### Effective state

An app is uninstalled for the session if it is listed in the machine file
**or** in the user's file, or if it depends (non-optional dependency,
transitively) on an app thus uninstalled.

| `AppInstallationState` | Meaning |
| --- | --- |
| `Installed` | neither listed nor dependent on a listed app |
| `UninstalledForUser` | uninstalled through the user's file |
| `UninstalledForMachine` | uninstalled through the machine file: only an administrator reinstalls it |
| `System` | system app, never uninstallable |

## Architecture

| Type | Project | Role |
| --- | --- | --- |
| `IAppInstallationService`, `AppInstallationService` | `Platform/HackerOs.Platform.Core/Apps` | reads, watches and writes the two files; keeps `AppEnablementRegistry` equal to the effective state |
| `AppsConfigDocument` | same | paths, tolerant reading, line-by-line writing, dependency closures |
| `AppEnablementToFilesMigration` | same | creates a commented `/etc/hackeros/apps.config` and copies into it once the old `IsEnabled = false` entries |
| `AppEnablementRegistry` | `Platform/HackerOs.Platform.Core/Lifecycle` | single launch checkpoint, now fed by the service; `Changed` event |
| `InstalledAppsPanel` | `Apps/System/HackerOs.Apps.Settings` | "Installed Apps" tab of the Settings app |

Single flow, whatever the origin of the change (Settings app, `nano`,
`/home` synchronization, login, logout):

1. `ReloadAsync` rereads the two files (system context for `/etc`, user
   context for their file);
2. the effective state is computed (union, then dependents);
3. newly uninstalled apps are stopped and disabled, dependents
   first, through `AppLifecycleOrchestrator.DisableAsync` (instances stopped,
   `AppDisabledEvent` published); reinstalled apps are re-enabled,
   dependencies first, through `EnableAsync`;
4. `Changed` is raised.

At startup, `EcosystemBootCoordinator` runs the migration then
`AppInstallationService.StartAsync`, which applies the machine file and sets up
the watches (`SettingsFilesWatcher`) before any login. Login
and logout (`SessionActivatedEvent`, `SessionLoggedOutEvent`) reread
the user layer and move its watch.

### Filtering

All routes consult `IAppEnablementRegistry`:

| Route | Effect of an uninstalled app |
| --- | --- |
| `AppLifecycleOrchestrator.LaunchAsync` | `AppLaunchStatus.Disabled` |
| `AppIntentDispatcher` (intents, `launch`) | refusal; `launch` answers "is not installed" |
| `FileAssociationResolver` | neither default nor candidate; the preferences of `/etc/hackeros/file-associations.json` and `~/.config/hackeros/file-associations.json` are **not** rewritten (a stale entry is skipped, ADR 0121) |
| Launcher and search (`LauncherAppProjection`) | absent; the open menu updates on `Changed` |
| Explorer, "Open With..." | absent from the list |
| File control and dialogs (`HackComponentHost`) | skipped; if it was the chosen provider, the explorer takes over (`component.fallback`); an uninstall for the machine removes its line from `/etc/hackeros/components.config` (ADR 0156 §4, ADR 0158 §6) |
| Terminal (`ShellCommandResolver`, `ShellInterpreter`) | `command not found`, absent from `help` and from completion |

## Usage

```csharp
public interface IAppInstallationService
{
    event EventHandler? Changed;
    AppInstallationState GetState(string appId);
    IReadOnlyList<string> GetUninstallClosure(string appId);
    ValueTask<AppInstallationResult> UninstallAsync(string appId, AppInstallationScope scope,
        IAppFileSystemGateway? writer = null, CancellationToken cancellationToken = default);
    ValueTask<AppInstallationResult> ReinstallAsync(string appId, AppInstallationScope scope,
        IAppFileSystemGateway? writer = null, CancellationToken cancellationToken = default);
}
```

- `GetUninstallClosure(appId)`: the apps that depend on it (dependents first)
  then the app itself; the confirmation dialog lists them.
- `PlanUninstallAsync(appId)` (ADR 0097 §5): the closure and the active
  processes it will stop (`AppProcessInfo`: PID, app, name, user), without
  changing anything. Settings lists them in the confirmation ("These processes
  will be stopped"). The result of `UninstallAsync` gives
  `StoppedProcesses`, with `Forced` for those killed after their delay; each is
  written to `/var/log/syslog`. In the SDK: `HackAppManagement.PlanUninstallAsync`
  and `UninstallWithReportAsync` (`HackUninstallPlan`, `HackUninstallReport`).
- `UninstallAsync` writes the entire closure in the scope's file.
  Refusals: `apps.app.system:<id>`, `apps.closure.system-app:<system id>`,
  `apps.app.not-found:<id>`, `apps.session.required`.
- `ReinstallAsync` removes the app and its mandatory dependencies listed in the
  same scope. Refusal in user scope if the app (or a dependency) is
  uninstalled for the machine: `apps.uninstalled-by-administrator:<id>`.
- `writer`: gateway whose rights decide on a **machine** write,
  normally the calling app's `AppContext.FileSystem`. A refusal throws
  `HackUnauthorizedAccessException`, which triggers the administrator approval
  prompt of ADR 0042; the app retries once approval is given.
  Without `writer`, the session user writes with their own rights. The user
  scope is always written as the session user.
- Each change is logged in `/var/log/syslog`, for example
  `apps: org.hackeros.calculator uninstalled (scope=machine, by=alice)`.

Since phase 4 (`rebuild/4-sdk-user-and-apps`), apps no longer use this
service directly: they go through the SDK's `HackAppManagement`, behind the
dangerous capability `apps.manage` (ADR 0056 §3, [`app-sdk.md`](../framework/app-sdk.md)).
For the machine scope, the gateway first asks the OS authentication
agent to confirm an administrator
([`admin-authentication-agent.md`](admin-authentication-agent.md)), then the OS
writes `/etc/hackeros/apps.config` itself as `system`: the `writer` parameter
receives this system gateway, and the prompt of ADR 0042 is no longer
used for this file. The list of installed apps is read
by `HackApps` without a capability.

### Settings app, "Installed Apps" tab

- one line per windowed app (name, publisher, version), then the terminal
  commands in a collapsed section; services remain managed by the Services tab
  of the System Monitor;
- "System" badge without a button for a system app;
- **Uninstall** for an installed app, with confirmation: list of the
  dependents taken along, "Only for me" / "For all users" choice for an
  administrator (a standard user has only their own scope); "For all
  users" opens the authentication agent window, where the administrator
  retypes their password;
- for a site (`Web` app) taken along by the uninstall, the confirmation
  announces that its browser database `hackweb.<id>` will be deleted; it is deleted
  after the uninstall by `UninstallAsync`, the only data that an
  uninstall erases (ADR 0088 §4, [`asp-hack-site-data.md`](../framework/asp-hack-site-data.md));
- **Reinstall** for an app uninstalled in a scope that the user
  can modify; otherwise the notice "Uninstalled by the administrator";
- the old enable/disable switch is gone;
- since phase 4, the panel uses only the SDK: `AppContext.AppManagement`
  for the list and the changes, `AppContext.Catalog.Changed` to
  refresh, `HackUser.Describe(AppContext)` to know whether the user is
  an administrator.

## Implementation decisions

- **A single state.** "Disabled" and "uninstalled" are now one. The
  IndexedDB store keeps the manifest snapshot, but the OS no longer reads or
  writes `IsEnabled`; its schema version does not change.
- **`DisableAsync` and `EnableAsync` become internal** to
  `HackerOs.Platform.Core` (visible to tests through `InternalsVisibleTo`): only
  the installation service calls them. The orchestrator no longer receives
  `IOsSettingsService` or `IPersistentAppCatalogRepository`.
- **Dependents uninstalled implicitly.** A hand-written line for
  a library also uninstalls its dependents, even if they are not
  listed: an app whose mandatory dependency is missing cannot run.
  The Settings app writes the entire closure, so that reinstalling
  is explicit.
- **Machine file created at startup** by the migration (system context),
  so that it belongs to `system` and later writes modify
  only its content.
- **Synchronization.** The "AppCatalog" domain of ADR 0033
  (`AppCatalogSyncService`, `AppCatalogSyncPayload`) is removed. The user
  file is under `/home` and travels with the "FileSystem" domain;
  `/etc` is not synchronized. The `SyncDomain.AppCatalog` enumeration remains
  on the server side for already stored data.
- **Deviation from the ADR's signature:** `UninstallAsync` and `ReinstallAsync`
  take an additional `writer` parameter. The ADR's "caller's context" is thus
  passed explicitly, as for `IOsSettingsService`
  (ADR 0044), rather than through the ambient `Hack*` context, which does not exist in
  a Blazor event handler.
- **`GetState` of an unknown identifier** throws `ArgumentException`: the ADR
  does not provide an "unknown" state.
- **English interface**, like the rest of the Settings app.

## Contract tests

`Tests/HackerOs.Platform.Core.Tests/Apps/AppInstallationServiceTests.cs`
(ADR 0048 section 7):

- user uninstall: file written, launch refused, instance
  stopped, data kept, log line;
- reinstall: line removed, launch possible again;
- system app and closure containing a system app refused;
- `apps.config` lines of a system app or with an invalid value ignored with a
  warning, unknown identifier silently ignored;
- machine scope without rights: `HackUnauthorizedAccessException`; approved
  write: file written, effect for all users;
- effective state machine OR user, user layer removed at
  logout, user reinstall refused for a machine
  uninstall;
- dependency closure at uninstall and at reinstall;
- modification of `apps.config` through the file API taken into account without
  a restart;
- editing that preserves comments and unknown identifiers;
- migration: `IsEnabled = false` becomes a `[uninstalled]` line, only once,
  never for a system app, file owned by `system`.

Elsewhere: `ShellCommandResolverTests.Uninstalled_commands_are_neither_resolved_nor_listed`,
`EcosystemBootCoordinatorTests.BootAsync_moves_persisted_disabled_entries_into_the_machine_apps_config`,
and the existing tests of the launcher, the association resolver and the
intent dispatcher, which already rely on the registry.

## Tasks

- [x] `IAppInstallationService`, `AppInstallationService`, `AppsConfigDocument`.
- [x] Tolerant reading, warnings in `/var/log/syslog`, unknown identifiers kept.
- [x] Watching of the two files, reload at login and logout.
- [x] Dependency closure (uninstall, reinstall), refusal of system apps.
- [x] Stopping of instances, `AppEnablementRegistry.Changed`.
- [x] Filtering: launcher, search, "Open With...", associations, intents, `launch`, terminal.
- [x] `AppEnablementToFilesMigration` migration and removal of the `IsEnabled` hydration at startup.
- [x] Removal of `AppCatalogSyncService` and its contract.
- [x] "Installed Apps" tab redone (`InstalledAppsPanel`).
- [x] Contract tests and documentation.
