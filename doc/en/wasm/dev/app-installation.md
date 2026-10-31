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

## Apps installed from a GitHub repository

ADR [0182](adr/0182-install-apps-from-github.md) adds a third origin of apps,
next to system apps and built-in apps: a `.hackapp` package published by the
app's GitHub repository on its `hackeros-packages` branch, read through
`raw.githubusercontent.com`.

```text
hackpkg install Kazo-ca/HackerOs-App-Webamp            # for yourself if the source is trusted
hackpkg install Kazo-ca/HackerOs-App-Webamp@1.0.0 --system   # for everyone, admin approval
hackpkg update                                         # every installed app
hackpkg list | info <repo> | remove <appId> [--user|--system]
hackpkg rollback <appId> [--user|--system]             # goes back to the kept version (ADR 0214)
hackpkg repair [--system]                              # finishes or undoes an interrupted install
hackpkg sources [add <source> [--trusted] | remove <source>]
hackpkg policy [block|unblock app|category|capability <value>]
```

ADR [0189](adr/0189-app-sources-trust-and-per-user-install.md) (built as
described by ADR [0208](adr/0208-hack-store-sources-trust-build.md)) adds
trusted sources, the install for oneself, the block list and the two-step
approval:

- **Sources**: `/etc/hackeros/app-sources.config` (created at startup, mode
  0644) has four sections. `[hosts]` gives the download host; `[catalogs]` the
  catalogs (`official=https://…/catalog.json`); `[trusted]` the sources
  (`catalog:<name>`, `github.com/<owner>` or `github.com/<owner>/<repo>`) set
  to `true` (trusted) or `false` (added but untrusted); `[updates]` the
  `interval-hours` (1 to 720, 6 by default). The defaults are fixed when the
  host is compiled (official catalog, `catalog:official` and
  `github.com/Kazo-ca` trusted) by the MSBuild properties
  `HackerOsAppCatalogs`, `HackerOsTrustedAppSources` and
  `HackerOsAppUpdateIntervalHours`. An administrator adds a source without
  redeploying, with `hackpkg sources add`, Settings ▸ Apps ▸ Sources and block
  list, or `sudo nano`.
- **Trust levels**: *verified* for the official catalog, *trusted* for a
  built-in source or one set to `true`, *untrusted* otherwise.
- **Block list**: `/etc/hackeros/app-policy.config` (created at startup) has
  `[blocked-apps]`, `[blocked-categories]` and `[blocked-capabilities]`, with
  defaults fixed at compile time (`HackerOsBlockedApps`,
  `HackerOsBlockedAppCategories`, `HackerOsBlockedAppCapabilities`). A blocked
  app cannot be installed for oneself: only for everyone, after approval.
- **Install for oneself**: without `--system`, an app from a trusted source,
  not blocked and not already installed for the system, is installed in
  `~/.local/share/hackeros/apps/<appId>/<version>/` with a line in
  `[installed]` of `~/.config/hackeros/installed-apps.config`, without an
  administrator. One copy at a time: an install for everyone removes the
  personal copies, and a personal copy of an app already installed for the
  system is refused.
- **Two-step approval**: an install for everyone, an untrusted source (even for
  oneself) and an uninstall for everyone ask, every time and even under
  `sudo`, for a fresh administrator sign-in, then the window that shows the
  app, its version, its size, its source, its trust level and its capabilities
  (`AppInstallApprovalOverlay`). The same window serves the Store and
  `hackpkg`. `--allow-untrusted` no longer exists. Editing the sources or the
  block list asks for one administrator authentication.
- **Catalog**: it is built when the page starts, with the personal apps of
  every user; each session only sees its own (`InstalledPackageCatalog`). If
  two users have different versions of the same app, the newest one is loaded
  for both.
- **Checks before the administrator prompt**: SHA-256 of the package
  (`index.json`) and of each file, identifier, `sdkCompatibility`, APIs present
  in the OS, app on the main thread.
- **Writing for everyone**: `/usr/lib/hackeros/apps/<appId>/<version>/` and a line
  `<appId>=github:owner/repo@version` in `[installed]` of
  `/etc/hackeros/apps.config`. The previous version's folder is kept for
  `hackpkg rollback` (only one, [ADR 0214](adr/0214-hack-store-hot-add-signing-transactions.md));
  `hackpkg update` keeps each copy where it is.
- **No restart** ([ADR 0214](adr/0214-hack-store-hot-add-signing-transactions.md)):
  the app appears at once. Only the update, removal or rollback of an app already
  loaded in the page waits for a restart of HackerOS; `hackpkg list` shows
  `(restart pending)` until then.
- **Dangerous capabilities**: asked at first launch, as for any non-system app.
- **Publishing**: `dotnet build -c Release -t:HackerOsPack` in the app project
  writes the package and `index.json` to `bin/hackeros-packages` (property
  `HackerOsPackageOutput`), to push to the `hackeros-packages` branch; the
  `publish-packages.yml` workflow of the `hackeros-solution` template does it on
  a `v*` tag ([ADR 0219](adr/0219-app-authors-signing-tool.md)). The
  package holds every static resource of the project, declared or not: the
  `wwwroot/`, the collocated JavaScript (`.razor.js`) and the scoped CSS bundle
  (`<assembly>.bundle.scp.css`); those the app does not declare are added, with
  their SHA-256, to the **package's** manifest only
  ([ADR 0221](adr/0221-pack-all-static-web-assets-of-an-app.md)).
- **`wwwroot/` resources** ([ADR 0206](adr/0206-serve-installed-app-assets.md)):
  at the app's first launch in the session, each declared resource is read
  through `AppAssetStore` (hash checked) and copied into the
  `hackeros-installed-apps` cache; the service worker serves it at
  `_content/<assembly>/<path>`, the same address as an app compiled into the
  OS (tags, `import()`, CSS, Web Workers and their `importScripts`). On
  `localhost`, the OS registers `service-worker.js?mode=installed-apps`, which
  serves only those files. Without a service worker, the files are not served
  (line in `/var/log/syslog`).
- **Scoped CSS** ([ADR 0221](adr/0221-pack-all-static-web-assets-of-an-app.md)):
  once `<assembly>.bundle.scp.css` is copied into the cache, the OS adds a
  `<link rel="stylesheet">` to it to the page, once per session, as the host's
  `styles.css` sheet does for a compiled app.
- **Limits**: see the section of the same name in ADR 0182 (main thread only,
  third-party NuGet dependencies not carried); the `wwwroot` resources limit is
  lifted by ADR 0206.

Tests: `AppPackageGatewayTests`, `AppSourcesAndPolicyTests`,
`InstalledAppsImageTests`, `AppPackageWriterTests`, `AppPackageTests`
(Platform.Core), `AppInstallApprovalOverlayTests` (Platform.Blazor),
`HackpkgCommandTests` (Commands.Wave5), the `hackpkg` scenario of
`CommandParityTests`, `InstalledAppAssetPublisherTests` (Ecosystem),
`PackagedWebAssetsTests` (Tools.ManifestGenerator) and
`TrimmedPublishTests.InstalledApps.cs` (test package
`Tests/fixtures/installed-app` installed by `hackpkg` in the trimmed publish,
run by `build/publish-trimmed.sh`).

## The Hack Store and the catalogs

ADR [0212](adr/0212-hack-store-app-build.md) builds the **Hack Store** app
(`org.hackeros.store`, ADR [0188](adr/0188-hack-store-and-app-catalog.md)): a
system app that cannot be uninstalled, pinned by default in the Start menu.

```text
hackpkg install org.example.notes          # looked up in the catalogs
hackpkg install org.example.notes@1.0.0    # a published version
hackpkg install catalog:official/org.example.notes
hackpkg info org.example.notes
```

- **Catalogs**: each `[catalogs]` entry names a `catalog.json`
  (`schemaVersion` 1, `packages` branch of the catalog repository). The file's
  paths (package, icon, screenshots, descriptions) are relative to
  `catalog.json`. The OS keeps it 5 minutes; an invalid app or version is
  skipped with a warning in `/var/log/syslog`.
- **Source written**: `<appId>=catalog:<name>/<appId>@<version>` in
  `[installed]`. The trust is that of `catalog:<name>`; the official catalog
  is *verified*. The SHA-256 and the size come from the catalog; the other
  checks are those of a GitHub app.
- **App ID alone**: looked up in every catalog. When one lists it, it is
  installed from that one; when several list it, the install is refused
  (`apps.package.ambiguous`) and the message gives every possible
  `catalog:<name>/<appId>` to type again
  ([ADR 0215](adr/0215-hack-store-answers-notification-and-ambiguous-app-id.md) §2).
- **The app**: home by categories, search (name, short description, tags, ID,
  ignoring case and accents), app page (icon, screenshots, Markdown
  description drawn without HTML, version, size, license, category, date,
  repository, capabilities, trust), Install for me or for everyone, Open,
  Update, Uninstall, **Installed** tab with "Update all". When an app waits for
  a restart, the banner offers **Restart to finish** (no administrator).
  Launch arguments: `--page=installed`, `--app=<appId>`.
- **Updates**: `AppUpdateMonitor` checks at sign-in then every
  `[updates] interval-hours` hours and posts a notification that opens the
  Installed tab. No badge on the Store's icon
  ([ADR 0215](adr/0215-hack-store-answers-notification-and-ambiguous-app-id.md) §1).
- **Default pin**: `start-menu.json` records `defaultPinsApplied` per profile;
  the Store is added once, to a new profile as to an existing one, and does
  not come back after an unpin.

Tests: `AppCatalogDocumentTests`, the catalog tests of
`AppPackageGatewayTests`, `StartMenuPreferencesServiceTests`
(Platform.Core) and `HackerOs.Apps.Store.Tests` (`StoreCatalog`,
`StoreMarkdown`, manifest).

## Adding without a restart, signing and transactions

ADR [0214](adr/0214-hack-store-hot-add-signing-transactions.md) (issue #251)
completes the Hack Store.

- **Live catalog**: after each install, update, removal or rollback, the gateway
  calls `IInstalledAppsActivator` (`InstalledAppsActivator` in
  `HackerOs.Ecosystem.Host`). It reads the installed apps again, replaces the
  content of the page's one `AppCatalog` (`InstalledAppsBoot.Live`,
  `AppCatalog.ReplaceContents`, `AppCatalog.Changed` event) and adds the
  assemblies to the lazy-loading registry. An app with an assembly already
  loaded keeps its version until the restart
  (`HackPackageInstallReport.RestartRequired`).
- **Signing**: optional `signature` field of each version (`index.json`,
  `catalog.json`), ECDSA P-256 / SHA-256 in IEEE P1363, base64. Public keys are
  in `[signing-keys]` of `/etc/hackeros/app-sources.config`
  (`catalog:official=MFkw…`, several keys separated by commas) and in the host's
  `HackerOsAppSigningKeys` MSBuild property, always required. A source with a key
  refuses an unsigned package (`apps.package.unsigned`) or a badly signed one
  (`apps.package.bad-signature`). Verification goes through Web Crypto in the
  browser (`BrowserAppPackageSignatureVerifier`, `webAuthn.js` `verify`), through
  `ECDsa` elsewhere.
- **Signing by an app's author** ([ADR 0219](adr/0219-app-authors-signing-tool.md),
  issue #321): `dotnet build -t:HackerOsKeyGen -p:HackerOsSigningKeyFile=<file.pem>`
  creates the pair (private key in a new file, public key printed, `HOSAPP0061`
  without the property); `HackerOsPack` signs each package with the
  `HACKEROS_APP_SIGNING_KEY` variable (secret of the app repository) or
  `HackerOsSigningKeyFile`, and checks the signature before writing it. Without
  a key, the package is published unsigned with warning `HOSAPP0063`.
  `index.json` keeps the signatures of the versions already published. Generator
  commands: `keygen --private-key <file.pem>`, `sign --package <file.hackapp>`.
  To require an author's signature, an administrator adds their public key with
  `sudo nano /etc/hackeros/app-sources.config`:

  ```text
  [signing-keys]
  github.com/contoso=MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE…
  ```

  `github.com/<owner>` covers all their repositories, `github.com/<owner>/<repo>`
  only one; two keys separated by a comma during a key change.
- **Transactions**: `<root>/<appId>/.transaction` journal, files written to
  `<version>.partial/` then renamed, the `[installed]` line as commit point,
  clean-up afterwards. `hackpkg repair [--system]` finishes or undoes what an
  interruption left (`AppPackageInstaller.RecoverAsync`, `RepairAsync`).
  `update.minimumUpgradableVersion` refuses the in-place update of a version
  that is too old (`apps.package.reinstall-required`).
- **Rollback**: an update keeps the version it replaces; `hackpkg rollback
  <appId>` and the Store's "Back to <version>" button put it back, with the
  approvals of an install of the same scope.
- **Data**: when an installed app whose version changed is launched,
  `AppDataGuard` copies `~/.config/<appId>/` and `~/.local/share/<appId>/` to
  `~/.local/share/hackeros/app-backups/<appId>/<old version>/`, then gives the
  app `HACKEROS_PREVIOUS_VERSION` (update) or puts back the backup of the version
  it returns to (rollback). The last version launched is recorded in
  `~/.config/hackeros/app-data-versions.config`. At most two backups per app.

Tests: `AppPackageGatewayTests.Transactions.cs`, `AppDataGuardTests`,
`AppPackageSignatureTests`, `AppCatalogTests` (Platform.Core),
`InstalledAppsBootTests` (Ecosystem), `HackpkgCommandTests` (Commands.Wave5).

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
- [x] Hack Store: catalogs, `hackpkg install <appId>`, updates by notification, default pin (ADR 0212, ADR 0215).
- [x] Adding without a restart, signing, transactional install, `hackpkg rollback` and `repair`, data backup (ADR 0214).
