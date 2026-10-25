> [Version française](../../../../fr/wasm/dev/adr/0048-system-apps-and-uninstall.md)

# ADR 0048: System apps, machine or user uninstall, and reinstall

## Status

Accepted on 2026-10-10.

- **Plan phase:** phase 2, App model ([`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), section 6.3, decisions D9 to D11).
- **Complements:** ADR 0032. The `AppEnablementRegistry` registry and launch
  blocking are reused; the notion of a "disabled app" is replaced by "uninstalled
  app", and its source of truth moves from the IndexedDB store to two
  files.
- **Partially replaces:** ADR 0033, for the "AppCatalog" synchronization domain
  only (the "FileAssociations" domain is untouched).
- **Default value retained** (plan, section 11): list of system apps through the
  `IsSystemApp="true"` metadata on the host's `ProjectReference`.

## Context

Today, nothing distinguishes one system app from another: ADR 0002 asks
that this status come from a trust policy, but no code carries it.
ADR 0032 made disabling real ("Installed Apps" tab of the Settings
app, `IsEnabled` state in `IPersistentAppCatalogRepository`), but:

- the state is an IndexedDB flag, invisible from the shell, contrary to the
  "everything is a file" principle;
- it applies to the whole machine, with no user scope;
- a disabled app remains visible in the launcher, search and file
  associations;
- any app, including the terminal or Settings, can be
  disabled.

## Decision

### 1. Trust list of system apps (D9)

- The host marks its system apps on the `ProjectReference`:

  ```xml
  <ProjectReference Include="..\..\Apps\System\HackerOs.Apps.Terminal\HackerOs.Apps.Terminal.csproj"
                    IsSystemApp="true" />
  ```

- The aggregation of ADR 0047 copies this value into the `isSystemApp` field of
  `BuiltInApplications.json`. It applies to **all** apps of the referenced
  project.
- **An app cannot declare itself a system app.** `AppDefinition` has no
  property for this, and the `manifest.json` schema has no field. Only
  the host, which is compiled trusted code, writes `isSystemApp`. An app
  installed at runtime by the future store will always be non-system.
- At runtime, `AppCatalog` exposes `IsSystemApp(string appId)` and
  `SystemAppIds`, fed by `BuiltInApplications.json`.

**System apps of the Ecosystem host:**

| System | Non-system (uninstallable) |
|---|---|
| Terminal, File Explorer, Settings, Text Editor, System Monitor, Error Log Viewer, and all commands | Browser, Calculator, Code Editor, Diagnostic, HackPaint, Icon Viewer |

The commands are all system: they form the base of the OS, like
coreutils on Linux. The phase 3 ADR on grouping commands will say whether
network tools (`curl`, `nmap`) move into a separate non-system project.

System status has two effects: the app is **not uninstallable** (section 3)
and it receives its dangerous capabilities **automatically** (ADR 0049).

### 2. Uninstalling (D10)

Uninstalling an app:

- hides it from the launcher, search, the Start menu and "Open
  with" lists;
- blocks its launch through all paths (`AppLifecycleOrchestrator.LaunchAsync`,
  intents, file associations, `launch`, terminal command resolution,
  which answers `command not found`);
- stops its running instances, like the disabling of ADR 0032;
- removes its file associations: `FileAssociationResolver` ignores an
  uninstalled app. The preferences in `/etc/hackeros/file-associations.json` are
  not rewritten, so a reinstall finds them again;
- **deletes no data**: `~/.config/<appId>/`, `/etc/<appId>/`, logs and the
  user's files remain in place.

**Dependencies:** uninstalling an app also uninstalls the installed apps that
depend on it as required dependencies (closure already computed by ADR 0032). The
confirmation box lists these apps. If a system app is part of this closure,
the uninstall is refused. Reinstalling an app also reinstalls its uninstalled
required dependencies in the same scope.

### 3. Scope and state files (D11)

State lives in two `.config` files (grammar of ADR 0011, kept by
ADR 0044):

| Scope | File | Owner and mode | Who writes |
|---|---|---|---|
| Machine | `/etc/hackeros/apps.config` | `system:system 0644` | an administrator, through `sudo` or the approval prompt of ADR 0042 |
| User | `~/.config/hackeros/apps.config` | the user, `0644` | the user themselves |

Format:

```text
# /etc/hackeros/apps.config
[uninstalled]
org.hackeros.hack-paint=true
org.hackeros.calculator=true
```

- Keys are app identifiers: the file is read by
  `ConfigDocumentFormat.Parse` without a schema (not by `LayeredConfigResolver`), and
  only the keys of the `[uninstalled]` section count.
- `true`: uninstalled. `false` or absence: installed. Any other value is
  ignored with a warning in `/var/log/syslog`.
- An identifier unknown to this build is ignored **without** a warning and its
  line is kept: the same file can serve several builds.
- A system app identifier is ignored with a warning: editing the
  file with `nano` does not allow bypassing section 1.
- A missing or unreadable file means "nothing uninstalled".

**Effective state** for the session user: an app is uninstalled if
it is so in the machine file **or** in their user file.

- A user cannot reinstall for themselves an app uninstalled for the
  machine; the Settings app shows "Uninstalled by administrator".
- An administrator chooses the scope in the confirmation box: "For me"
  or "For all users". A standard user only has "For me".

### 4. Installation service

`IAppInstallationService` / `AppInstallationService`
(`Platform/HackerOs.Platform.Core/Apps/`):

```csharp
public interface IAppInstallationService
{
    AppInstallationState GetState(string appId);   // Installed, UninstalledForUser, UninstalledForMachine, System
    IReadOnlyList<string> GetUninstallClosure(string appId);
    ValueTask<AppInstallationResult> UninstallAsync(string appId, AppInstallationScope scope, CancellationToken ct = default);
    ValueTask<AppInstallationResult> ReinstallAsync(string appId, AppInstallationScope scope, CancellationToken ct = default);
    event EventHandler? Changed;
}

public enum AppInstallationScope { User, Machine }
```

- It writes the files through the `Hack*` API (ADR 0043) **with the caller's
  context**. File permissions are authoritative: a machine write by a
  user without rights throws `HackUnauthorizedAccessException`, which triggers
  the administrator approval prompt of ADR 0042.
- It re-reads both files at sign-in and watches them with
  `HackFileSystemWatcher`: a `sudo nano /etc/hackeros/apps.config` takes effect
  without a restart.
- It feeds `AppEnablementRegistry` (`MarkDisabled` / `MarkEnabled`), which remains
  the single launch control point. On sign-out, the user layer
  is removed.
- Every uninstall and reinstall is written to `/var/log/syslog`
  (ADR 0045), for example
  `apps: org.hackeros.calculator uninstalled (scope=machine, by=alice)`.

In this phase, the Settings app uses this service directly. Its exposure
in the public SDK, protected by the dangerous capability `apps.manage` (ADR 0049),
arrives in phase 4 (plan, section 8.1).

### 5. Settings > Installed Apps

The tab from ADR 0032 is reworked:

- one row per catalog app: name, publisher, version, "System" badge;
- **Uninstall** button for an installed non-system app, with confirmation
  (dependency closure, scope choice for an administrator);
- **Reinstall** button for an app uninstalled in a scope that
  the user can modify; otherwise the note "Uninstalled by
  administrator";
- no button for a system app;
- the enable/disable switch disappears.

The launcher (`AppLauncher`) now filters out uninstalled apps, which closes
the gap that ADR 0032 left open.

### 6. End of the `IsEnabled` flag and migration

- `IPersistentAppCatalogRepository` keeps the manifest snapshot, but
  `IsEnabled` is no longer read or written by the OS. `AppLifecycleOrchestrator.DisableAsync`
  and `EnableAsync` become internal to the installation service.
- **Migration on first boot** (`AppEnablementToFilesMigration`, system
  context): each `IsEnabled = false` record whose app is not a system app
  is written to `[uninstalled]` in `/etc/hackeros/apps.config`, since the
  disabling of ADR 0032 applied to the whole machine. Marker:
  `/var/lib/hackeros/migrations/app-enablement-to-files`. The IndexedDB store and
  its schema version do not change
  (see [`old/common-pitfalls.md`](../old/common-pitfalls.md)).
- **Synchronization:** the "AppCatalog" domain of ADR 0033
  (`AppCatalogSyncService`, `IAppCatalogSyncService`) is removed. The user
  layer is under `/home/<user>` and therefore follows the "FileSystem" domain of
  ADR 0030; `/etc` is not synchronized, as for ADR 0044.

### 7. Contract tests

- `isSystemApp` comes from the `ProjectReference`; an app cannot obtain it
  any other way.
- Uninstalling a system app is refused by the service, and a line that
  uninstalls it in `apps.config` is ignored with a warning.
- Machine scope without rights: `HackUnauthorizedAccessException`; with administrator
  approval: file written.
- Effective state = machine OR user; a user does not reinstall a
  machine uninstall.
- An uninstalled app appears neither in the launcher nor in "Open with", cannot
  be launched through any path and its instances are stopped; its data remains.
- Dependency closure on uninstall and on reinstall.
- Change to `apps.config` through the file API picked up without a
  restart.
- Migration: an `IsEnabled = false` becomes an `[uninstalled]` line, only
  once.

## Consequences

- `cat /etc/hackeros/apps.config` shows what the administrator removed, and
  editing the file has the same effect as the Settings app.
- The terminal, the explorer and Settings can no longer disappear by
  mistake.
- Uninstalling destroys nothing: a reinstall finds the app's settings and
  associations again.
- There is no longer a "disabled" state distinct from "uninstalled". A non-system
  app that one wants to keep installed but blocked has no dedicated setting; this
  need has not been expressed.
- The enablement synchronization between devices from ADR 0033 disappears;
  only the user layer travels, with the rest of `/home`.

## Implementation work items

| Branch | Content | Depends on |
|---|---|---|
| `rebuild/2-system-apps` | `IsSystemApp` read from `BuiltInApplications.json`, `AppCatalog.IsSystemApp` and `SystemAppIds`, metadata set on the Ecosystem's `ProjectReference`s according to section 1, tests. | `2-builtin-applications` (ADR 0047) |
| `rebuild/2-app-uninstall` | `IAppInstallationService`, `apps.config` files, watching, filtering (launcher, search, associations, intents, terminal, `launch`), dependency closure, log, migration from `IsEnabled`, removal of `AppCatalogSyncService`, new Settings tab. | `2-system-apps` |

## References

- ADR 0002 (trust authority), ADR 0030 (file synchronization),
  ADR 0032 (complemented), ADR 0033 (partially replaced), ADR 0042 (approval
  prompt), ADR 0043 (`Hack*` API), ADR 0044 (settings as files),
  ADR 0045 (logs), ADR 0047 (`BuiltInApplications.json`), ADR 0049
  (dangerous capabilities).
- [`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), sections 6.3 and 11.
