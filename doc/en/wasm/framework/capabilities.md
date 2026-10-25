# Capabilities: catalog, classification and grant

## Goal

A capability is an exact right that an app declares in its definition
(`AppDefinition.Capabilities`) and that the OS grants it, or not, at login.
This document describes the `AppCapabilities` catalog, its classification into normal
and dangerous capabilities (ADR 0049, section 1) and the seeder's grant rule
(ADR 0049, section 2).

The administrator approval window, `security.config`,
`app-permissions.config` and `auth.log` (ADR 0049, sections 3 to 8) are described
in [`dangerous-capability-approval.md`](../dev/dangerous-capability-approval.md). At
login, a dangerous capability of a non-system app is **never**
granted: only the launch gateway grants it, after an "allow" decision by an
administrator.

## Catalog and classification

`Shared/HackerOs.App.Abstractions/AppCapabilities.cs` contains, for each
capability, a `CapabilityDescription`: identifier, displayed title, `CapabilityRisk`
classification and, for a dangerous capability, the risk sentence shown to the
administrator. The catalog (`AppCapabilities.All`) is exactly the set of
keys of this table: a capability cannot be added without choosing its
classification.

| Capability | Classification |
|---|---|
| `filesystem.private.*`, `filesystem.user-selected.*`, `filesystem.user-home.*` | normal |
| `filesystem.system.read`, `filesystem.system.write` | **dangerous** (Diagnostics reads isolation through `HackCrossOriginIsolation`, without a capability, and no longer declares it, ADR 0146) |
| `apps.launch` | normal |
| `apps.manage` | **dangerous** (required by `HackAppManagement`, ADR 0056 §3, and `HackComponentProviders`, ADR 0156; declared by Settings) |
| `dialogs.file-open`, `dialogs.file-save`, `dialogs.folder-select` | normal |
| `file-associations.read` | normal |
| `file-associations.write` | **dangerous** |
| `process.list` | normal |
| `process.manage`, `services.manage`, `windows.manage` | **dangerous** |
| `notifications.post`, `clipboard.read`, `clipboard.write` | normal |
| `network.simulated.read`, `network.simulated.write` | normal |
| `network.real.access` | **dangerous** (also checked by the optional server before relaying, ADR 0102 §4 and 0108 §1) |
| `network.listen` | normal (listen on a port that is not a URL of its manifest, [`listening-ports-and-dns.md`](listening-ports-and-dns.md)) |
| `network.listen.privileged` | **dangerous** (listen on a port below 1024, like `CAP_NET_BIND_SERVICE`) |
| `shell.control` | normal (ADR 0051: acts only on the shell of the user who typed the command, see [`shell-commands.md`](../dev/shell-commands.md)) |
| `shell.context-menu` | normal (ADR 0151 §6: add entries to file menus; without it, the generator produces `HOSAPP0027` and the OS ignores the entries, see [`context-menu.md`](context-menu.md#entrées-des-apps-dans-les-menus-des-fichiers-chantier-4)) |
| `permissions.manage` | **dangerous** (`HackPermissionAdministration`, ADR 0056 §9; declared by Settings) |
| `server.connection.manage` | **dangerous** (`HackServerConnection`, ADR 0056 §10; declared by Settings) |
| `system.power` | **dangerous** (`HackSystem`: restart, reset, ADR 0056 §11; declared by Settings) |
| `system.storage.inspect` | **dangerous** (`HackStorageDiagnostics`, ADR 0056 §11; declared by Diagnostics, the only non-system app in the base to request an approval at first launch) |
| `system.backup` | **dangerous** (`HackSystemBackup`: back up and restore the whole machine, ADR 0099 §1; declared by Settings, and by `hackbackup` which requires `sudo`, see [`system-backup.md`](../dev/system-backup.md)) |
| `shell.host` | **dangerous** (ADR 0056 §7: host a shell whose commands act with their own rights, see [`shell-host.md`](shell-host.md)) |
| `users.manage` | **dangerous** (ADR 0120: reset or remove another account's password through `HackUserAccounts`, with an administrator; declared by Settings and `passwd`; see [`sensitive-settings.md`](../dev/sensitive-settings.md)). Risk sentence: "Reset or remove the password of another account, and therefore open its session." Changing one's own password requires none. |
| `keyring.read-all` | **dangerous** (ADR 0100 §2: read the keyring entries of all apps, for a password manager; see [`sensitive-settings.md`](../dev/sensitive-settings.md)). Risk sentence: "Read the passwords, tokens and keys that all your applications have saved." |

The risk sentences are those of the table in ADR 0049.

**Removed from the catalog:** `settings.read`, `settings.write`,
`settings.system.read`, `settings.system.write` (ADR 0044: settings are
files, protected by `filesystem.*`) and `diagnostics.read`,
`diagnostics.clear` (ADR 0045: logs are files). An app definition
that cites them no longer compiles, and a `manifest.json` that names them is
rejected by the validator (`manifest.capability.unknown`). `SettingsScopePolicy`,
which was only used by tests and read `settings.system.*`, is removed.

## API

```csharp
CapabilityRisk risk = AppCapabilities.GetRisk(AppCapabilities.ProcessManage); // Dangerous
bool dangerous = AppCapabilities.IsDangerous("network.real.access");          // true
CapabilityDescription d = AppCapabilities.Describe(AppCapabilities.FileSystemSystemWrite);
// d.Title          : "Modifier les fichiers du système"
// d.RiskDescription: "Modifier des fichiers du système, dont /etc et les paramètres de toutes les apps."
```

- `GetRisk` and `Describe` throw `ArgumentException` for an identifier outside
  the catalog.
- `IsDangerous` returns `false` for any identifier outside the catalog, in
  particular topic permissions (`topic-publish:` / `topic-subscribe:`,
  ADR 0040), which a manifest can also list and which are not classified.

## Grant at login

`LocalSessionService` calls, for each manifest of the catalog,
`CleanProfileCapabilityGrantSeeder.SeedDeclaredCapabilities(repository, manifest, userId, isSystemApp)`
with `isSystemApp = AppCatalog.IsSystemApp(manifest.Id)` (ADR 0048).

| App | Declared normal capability | Declared dangerous capability |
|---|---|---|
| System | granted, source `BuildProfile` | granted, source `SystemPolicy` |
| Non-system | granted, source `BuildProfile` | **not granted**; the launch gateway grants it (source `AdministratorApproval`) if an "allow" decision exists |

An undeclared capability is never granted. `AppLifecycleOrchestrator`
computes an instance's capabilities from the grant repository: a non-system
app that uses a dangerous capability therefore receives
`HackUnauthorizedAccessException`, as for any missing capability.

In the Ecosystem host, no non-system app declares a dangerous
capability: the base experience does not change.

## `policy.config` policy

The `capabilityGrantsLocked` key, never evaluated, is removed from the schema of
`/etc/hackeros/policy.config`. Only `allowRuntimePackageInstall` remains.
An old `capabilityGrantsLocked=…` line in the file is now an
unknown key.

## Tests

- `AppCapabilitiesTests` (`HackerOs.App.Abstractions.Tests`): exact classification
  of each capability according to the ADR, catalog equal to the classified table, each
  capability described (risk sentence only for dangerous ones),
  `settings.*` and `diagnostics.*` absent, topic permissions never
  dangerous.
- `CleanProfileCapabilityGrantSeederTests` (`HackerOs.Platform.Core.Tests`):
  source `BuildProfile` for a normal capability, `SystemPolicy` for a
  dangerous capability of a system app, no dangerous grant for a
  non-system app.
- `BuildKnownLazyAppsTests.No_non_system_app_of_the_host_declares_a_dangerous_capability`
  (`HackerOs.Ecosystem.Tests`).
- `PolicySettingsDocumentsTests`: `capabilityGrantsLocked` absent from the schema.

## Tasks

- [x] `rebuild/2-capability-classification`: `CapabilityRisk`, `GetRisk`,
  `IsDangerous`, `Describe`, `apps.manage`, removal of `settings.*` and
  `diagnostics.*` (catalog, app definitions, generated manifests, tests,
  `SettingsScopePolicy`), seeder according to system status, removal of
  `capabilityGrantsLocked`.
- [x] `rebuild/2-dangerous-approval` (see
  [`dangerous-capability-approval.md`](../dev/dangerous-capability-approval.md)): `security.config`,
  `app-permissions.config`, `IDangerousCapabilityGate` at launch, special
  window, terminal text mode, `auth.log`, Permissions view of Settings.
- [x] `v3/7-storage-backup`: dangerous capability `system.backup` (ADR 0099).
