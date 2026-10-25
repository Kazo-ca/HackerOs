> [Version française](../../../../fr/wasm/dev/plans/ecosystem-rebuild-plan.md)

# HackerOS ecosystem rebuild plan

**Status:** plan approved with Benoit on 2026-10-10. The four phases are
done and merged into `rebuild/base` (phase 4 finished on 2026-10-11 by
`rebuild/4-sdk-boundary-final`); section 12 points to the document of each
work item. This document replaces
`old/implementation-status.md` and `old/integration-task-list.md` as the
direction reference for the ecosystem. These two documents stay in
[`old/`](../old/) as history.

## 1. Purpose

Put the `wasm2/HackerOs` ecosystem back on a good footing. The current
system works, but it is more complicated than the TypeScript legacy in `src/`
without being more pleasant to use or simpler to extend. The goal is twofold,
and both parts matter equally:

1. **For the app developer:** adding an application must be simple.
   The file API looks like `System.IO`, the manifest is a C# class, and
   a single `ProjectReference` is enough to install an app.
2. **For the user:** the OS behaves like a real Linux system.
   Everything is a file (settings, logs, processes), the shell is complete,
   and the base apps are at least as rich as in the legacy.

One hypothesis guides the whole plan: a less complicated system leaves less
room for integration bugs.

The scope is the ecosystem: file system, app model, OS services and
base apps (file explorer, console, task manager, text editor). Other apps, the server and mobile are out of scope.
**No game in this repository** ([ADR 0135](../adr/0135-no-game-in-this-repository.md)).

## 2. Starting point

### 2.1 What is solid and kept

- The file engine (`HackerOs.Simulation.Abstractions.FileSystem`,
  `Platform.Core/FileSystem`): canonical paths, entry identities,
  optimistic revisions, atomic transactions, symbolic links,
  owner/group/other permissions, mounts by path.
- IndexedDB persistence in deduplicated blocks
  (`Infrastructure/HackerOs.Infrastructure.Browser`).
- Windowing and the taskbar (`HackerOs.Windowing.*`,
  `HackerOs.Taskbar.Blazor`), mobile, the theme.
- The process manager (parent/child tree, cascading kill), events,
  topic messaging, notifications, clipboard,
  file watching, sessions and `sudo`.

### 2.2 What is wrong

| Subject | Problem found | Reference |
|---|---|---|
| File API | Writing a string requires `CreateAsync` with mode bits, then `WriteAsync` with the expected revision, then a home-made `IFileSystemContentSource` class. | `Apps/System/HackerOs.Apps.TextEditor/TextEditorWindow.razor` (≈ l. 364-460) |
| Manifest | Defined twice: `app.manifest.json` and a C# copy `StaticManifest` (32 apps). | `Apps/**/app.manifest.json` |
| Adding an app | A project, plus three entries in `HackerOs.Ecosystem.csproj` (`ProjectReference`, `BlazorWebAssemblyLazyLoad`, `EmbeddedResource`). | `OS/HackerOs.Ecosystem/HackerOs.Ecosystem.csproj`, `BuildKnownLazyApps.cs` |
| Settings | The `/etc/hackeros` mount is never wired into the real OS: the router is created with the IndexedDB provider alone. `SettingsFileSystemProvider` exists only in tests. Settings live in a separate store, and security comes from the settings service, not from file permissions. | `OS/HackerOs.Ecosystem/EcosystemServiceCollectionExtensions.cs` (≈ l. 157) |
| Logs | `/var/log` is created but stays empty. Logs go to an IndexedDB store read only by Error Log Viewer. | `Platform.Core/Diagnostics/` |
| Shell | No pipes, redirections, `&&` or environment variables. The legacy had pipes and `env`. | `Shared/HackerOs.AppSdk.Terminal/ShellParser.cs`, `adr/0014-shell-grammar-boundary.md` |
| Commands | 28 projects, one per command. `cd`, `pwd`, `clear` and `help` are hard-coded in the terminal. | `Apps/Commands/`, comment in `HackerOs.Ecosystem.csproj` |
| Trash | Nonexistent: every deletion is permanent. | n/a |
| Permissions | All declared capabilities are granted automatically at login, without asking anything. | `Platform.Core/Policy/CleanProfileCapabilityGrantSeeder.cs` |
| System apps | They inject internal services (`ISessionService`, `AppCatalog`, `AppLifecycleOrchestrator`, `IJSRuntime`…). An external developer cannot write an equivalent. | `Apps/System/*/*.razor` (`@inject`) |
| Base apps | Explorer without cut/copy/paste or trash. Task manager limited to processes and services. Editor without replace. | see section 7 |

## 3. Guiding principles

1. **Rebuild in place.** We work in `wasm2/HackerOs`. We keep the
   file engine, IndexedDB and windowing, and replace the layers above
   them. We do not create a new root folder.
2. **Everything is a file.** Settings, logs, processes and devices
   are readable, and modifiable when allowed, through the file
   system. File permissions are authoritative.
3. **A single public SDK.** System apps use exactly the same SDK
   as third-party apps. If a system app needs something, we
   add it to the SDK, protected by a capability.
4. **Simplicity for the developer.** The engine's rigor (revisions,
   transactions, streams) stays inside. The developer sees a familiar
   API.
5. **Imitate Linux.** When a behavior exists on Linux, we reproduce it,
   unless there is an explicit reason.
6. **New ADRs rather than modifications.** Each decision that contradicts an
   accepted ADR is the subject of a new ADR that replaces it (section 9).

## 4. Decision register

Decisions made with Benoit during the discussion of 2026-10-10.

| # | Subject | Decision |
|---|---|---|
| D1 | Goal | Simplicity for the developer and quality for the user, on equal footing. |
| D2 | File engine | We keep it and add a simple facade in the SDK. |
| D3 | File API | `System.IO`-style API, with the prefix `Hack` on each type: `HackFile`, `HackDirectory`, `HackPath`, `HackFileInfo`, `HackDirectoryInfo`, `HackFileStream`… |
| D4 | API form | Static, like `System.IO`. The calling app is identified by an ambient execution context, invisible to the developer. Asynchronous methods only (IndexedDB is asynchronous and the browser has only one thread). |
| D5 | File errors | Exceptions, like `System.IO`: `HackFileNotFoundException`, `HackDirectoryNotFoundException`, `HackUnauthorizedAccessException`, `HackIOException`… |
| D6 | Manifest | A C# class marked `[AppManifest]`, separate from the window, that inherits from an abstract base. Essential properties `abstract` (required), the others `virtual` with a default value. |
| D7 | Discovery | At compile time. A generator produces `manifest.json` in the app's folder. The host aggregates the referenced apps into `BuiltInApplications.json`, which the OS reads at startup. The JSON is never written by hand. It will also serve the future app store. |
| D8 | Installation | A `ProjectReference` in the host is enough. No other line to add. |
| D9 | System apps | Defined by a trusted list in the host. An app cannot declare itself system. |
| D10 | Uninstallation | The app is hidden everywhere, its launch is blocked and its file associations are removed. It remains reinstallable from the list. |
| D11 | Scope of uninstallation | An administrator uninstalls for the whole machine. A user uninstalls for their profile only. System apps are not uninstallable. |
| D12 | Shell | Pipes `\|`, redirections `>` and `>>`, `&&`, environment variables. |
| D13 | Processes | Each process has its current directory and environment, inherited from the parent. |
| D14 | Commands | All commands become real commands, with no exception hard-coded in the terminal. They are grouped in a single project. |
| D15 | Trash | A trash shared between the explorer and `rm`. |
| D16 | Settings | They are real files, the only source of truth. File permissions decide access: if you can modify the file, you can modify the setting. |
| D17 | Invalid file | The write is accepted. The app ignores invalid keys, uses its default values and writes a warning to the log. |
| D18 | Settings levels | Layered: `/etc/<app>/` (system), then `~/.config/<app>/` (user). The user inherits system values and can override them, except keys locked by an administrator. |
| D19 | Logs | Real text files in `/var/log`, with limited size and rotation. |
| D20 | `/proc` and `/dev` | Purely virtual: computed on read, never written to IndexedDB. |
| D21 | Base apps | Public SDK only. |
| D22 | Base app features | At least parity with the legacy, plus the additions in section 7. |
| D23 | Dangerous permissions | Capabilities are classified as normal and dangerous. System apps receive them automatically. Any other app triggers a special approval window. |
| D24 | Approval | Only an administrator can approve, with their password, like `sudo`. |
| D25 | Default policy | "Allow with approval". An administrator can switch to "Always block". |
| D26 | Order | 1. Files; 2. App model; 3. Shell, commands and trash; 4. Full SDK and base apps. |
| D27 | Later | Execution isolation of apps (watchdog, Web Workers). |
| D28 | Documentation | Plan in French, file names in English. Old documents are moved into `docs/old/`, except `docs/adr/`. |

## 5. Phase 1: Files

### 5.1 `System.IO`-style `Hack*` API

New public namespace in the SDK (proposed: `HackerOs.AppSdk.IO`), with no
dependency on Blazor, usable by window, terminal and service apps.

**Planned types**, modeled on `System.IO`:

| Type | .NET equivalent | Example members |
|---|---|---|
| `HackFile` | `File` | `ReadAllTextAsync`, `WriteAllTextAsync`, `AppendAllTextAsync`, `ReadAllBytesAsync`, `WriteAllBytesAsync`, `ReadAllLinesAsync`, `ExistsAsync`, `CopyAsync`, `MoveAsync`, `DeleteAsync`, `OpenReadAsync`, `OpenWriteAsync` |
| `HackDirectory` | `Directory` | `CreateDirectoryAsync`, `ExistsAsync`, `GetFilesAsync`, `GetDirectoriesAsync`, `EnumerateFileSystemEntriesAsync`, `MoveAsync`, `DeleteAsync(recursive)` |
| `HackPath` | `Path` | `Combine`, `GetFileName`, `GetExtension`, `GetDirectoryName`, `GetFullPath` (with `~` and current directory) |
| `HackFileInfo` / `HackDirectoryInfo` | `FileInfo` / `DirectoryInfo` | `Length`, `LastWriteTimeUtc`, `Exists`, `Permissions`, `Owner`, `RefreshAsync` |
| `HackFileStream` | `FileStream` | Read/write stream that publishes the content on `DisposeAsync` |
| `HackFileSystemWatcher` | `FileSystemWatcher` | Created, modified, deleted, renamed events (on top of the existing watching) |

**Internal operation:**
- Each call goes through the existing gateway `IAppFileSystemGateway`,
  so capabilities and permissions are still checked exactly as
  today.
- The calling app is identified by an ambient context (of type
  `AsyncLocal`) that the OS sets before running app code. The
  developer has nothing to pass.
- `WriteAllTextAsync` creates the file if it does not exist, with default
  permissions derived from a umask (`0644` for a file, `0755` for a folder),
  then writes. The revision is handled inside: the facade reads the current
  revision and retries once if it changed. An advanced API remains
  available for whoever wants strict conflict control (the text
  editor, for example).
- `FileSystemErrorCode` codes are translated into `Hack*` exceptions, in
  the spirit of `System.IO` exceptions.
- No synchronous methods: impossible without blocking the browser's single
  thread.

### 5.2 Settings as files

Implementation: [`settings-files.md`](../../framework/settings-files.md).

- **Source of truth:** the file. The separate IndexedDB `settings` store
  disappears for app and system settings. A migration copies the existing
  values into files at first startup.
- **Locations:**
  - system: `/etc/<app>/<document>.config` (and `/etc/hackeros/` for the OS);
  - user: `~/.config/<app>/<document>.config`.
- **Layers:** default value declared by the app, then `/etc`, then
  `~/.config`. A key that the administrator locks in `/etc` cannot
  be overridden. The lock syntax remains to be defined in the ADR (section 10).
- **Security:** file permissions only. `/etc/<app>/` belongs to
  `root` (`0644`), `~/.config/<app>/` to the user.
- **Format:** the line-by-line `.config` format of ADR 0011 (`key=value`,
  `[Group]`, `#` comment) is kept.
- **Errors:** an invalid write is accepted. On read, the app ignores
  invalid keys, keeps its default values and writes a warning
  to `/var/log/apps/<app>.log`.
- **Typed API:** the app reads and writes typed values through a small SDK
  API (proposed name: `HackSettings`) that merges the layers and
  watches the files to signal changes. The Settings app and
  `nano /etc/...` see and modify the same files.

### 5.3 Logs

- Text files:
  - `/var/log/syslog`: system;
  - `/var/log/auth.log`: logins, `sudo`, permission approvals;
  - `/var/log/apps/<app>.log`: one file per app.
- Line format inspired by syslog: ISO date, level, source, message.
- Size limited per file, with rotation `.1`, `.2`… and a maximum number
  of archives (values in the ADR).
- `ILogger` and `IAppLoggingGateway` write to these files. Error Log
  Viewer, `cat`, `tail` and `grep` read them.
- Batched background writing so as not to slow down the app that logs.

### 5.4 Virtual `/proc` and `/dev`

- Two new providers mounted in the existing router
  (`FileSystemMountRouter`), read-only, computed on each read and
  never persisted.
- `/proc/<pid>/`: `status` (name, state, user, parent), `cmdline`,
  `environ`, `cwd` (link), plus `/proc/meminfo`, `/proc/cpuinfo`, `/proc/uptime`
  and `/proc/loadavg`, fed by the existing resource simulator.
- `/dev/null` (write absorbed, empty read), `/dev/zero`, `/dev/random` and
  `/dev/urandom`.
- `ps` and the task manager read `/proc`.

## 6. Phase 2: App model

### 6.1 Manifest as a C# class

```csharp
[AppManifest]
public sealed class TextEditorApp : AppDefinition
{
    public override string Id => "org.hackeros.text-editor";
    public override string Name => "Text Editor";
    public override AppKind Kind => AppKind.Window;
    public override Type EntryPoint => typeof(TextEditorWindow);

    public override string Version => "1.0.0";
    public override IReadOnlyList<string> Capabilities =>
        [AppCapabilities.FileSystemUserHomeRead, AppCapabilities.FileSystemUserHomeWrite];
    public override IReadOnlyList<FileHandler> FileHandlers =>
        [new("text/plain", [".txt", ".md", ".log"], ["open", "edit"])];
}
```

- `AppDefinition` (proposed name) replaces `StaticManifest` and
  `app.manifest.json` as the single source.
- `abstract` properties: identifier, name, app kind, entry point.
- `virtual` properties with a default value: version, description, publisher,
  category, icon, capabilities, file associations, command
  metadata, per-platform entry points (desktop, mobile), single instance,
  resource profile.
- The current validation (`AppManifestValidator`) runs at compile time,
  in the generator, and the error fails the build.

### 6.2 Generation and discovery

1. **In each app project:** an MSBuild task run after
   compilation instantiates the `[AppManifest]` class and writes `manifest.json`
   to the app's folder. A Roslyn generator cannot write to the source
   folder, hence the MSBuild task. The file is generated: it is never
   edited by hand.
2. **In the host:** an MSBuild task gathers the `manifest.json` files of the
   referenced projects into `BuiltInApplications.json`, embedded in the host.
3. **At startup:** the OS reads `BuiltInApplications.json`. It therefore knows
   all the apps without loading their DLLs, and lazy loading is
   kept.
4. **A single reference:** a shared `.targets` file automatically adds
   `BlazorWebAssemblyLazyLoad` and the embedded resource for
   each referenced app. The manual lists in `HackerOs.Ecosystem.csproj`
   and `BuildKnownLazyApps.cs` disappear.

### 6.3 System apps and uninstallation

- **Trusted list:** the host declares its system apps (for example in
  a host configuration file or an MSBuild property on the
  `ProjectReference`). An app cannot register itself there.
- **Uninstall** = hide from the launcher and search, block
  launch, remove file associations. The app's data is not
  deleted.
- **Reinstall** from Settings > Installed apps.
- **Scope:**
  - administrator: for the whole machine (state in `/etc/hackeros/apps.config`);
  - user: for their profile (state in `~/.config/hackeros/apps.config`).
- System apps do not offer an uninstall button.
- The existing activation mechanism (ADR 0032) is reused and extended.

### 6.4 Dangerous permissions

- **Classification:** each capability of the `AppCapabilities` catalog is marked
  normal or dangerous. Dangerous, at a minimum: reading or writing
  outside the home folder, writing to `/etc`, killing the processes
  of other apps, app management (install, uninstall, enable),
  system settings, raw network access. The exact list is set by the ADR.
- **System apps:** they receive their dangerous capabilities automatically.
- **Other apps:** normal capabilities are still granted automatically,
  as today. A dangerous capability is never granted without
  approval: `CleanProfileCapabilityGrantSeeder` is modified accordingly.
- **Special approval window:** visually distinct from ordinary
  dialogs (warning banner, list of requested permissions and
  their risks). It requires an administrator's password, like
  `sudo`. Each decision is written to `/var/log/auth.log`.
- **Policy:** administrator setting in `/etc/hackeros/security.config`,
  with two values: "allow with approval" (default) and "always
  block".

## 7. Phase 3: Shell, commands and trash

### 7.1 Shell grammar

- Pipes `cmd1 | cmd2 | cmd3`: the standard output of one command becomes
  the standard input of the next.
- Redirections `>` (replace) and `>>` (append) to a file.
- `&&`: run the next command only if the previous one succeeded (exit
  code 0).
- Environment variables: expansion of `$VAR` and `${VAR}`, assignment by
  `export VAR=value`, display by `env`. Base variables: `HOME`, `USER`,
  `PWD`, `PATH`, `SHELL`.
- Replaces ADR 0014, which excluded these elements from the first slice.

### 7.2 Per-process environment

- Each process has a current directory and an environment dictionary,
  copied from the parent at creation.
- Visible in `/proc/<pid>/environ` and `/proc/<pid>/cwd`.
- Accessible in the SDK (proposed name: `HackEnvironment`, modeled on
  `System.Environment`).

### 7.3 Commands

- The 28 `Apps/Commands/HackerOs.Commands.*` projects are merged into a single
  project (proposed name: `HackerOs.Commands`). Each command keeps its class
  and its `[AppManifest]` declaration.
- `cd`, `pwd`, `clear` and `help` become normal catalog commands. As on
  Linux, `cd` and `export` act on the shell itself: the SDK gives them
  controlled access to the calling shell's session.
- The format of third-party commands stays the same: a Terminal-type app
  with its manifest class.

### 7.4 Trash

- Standard Linux location (freedesktop specification):
  `~/.local/share/Trash/files/` for files, `~/.local/share/Trash/info/`
  for the origin and deletion date.
- The explorer and `rm` send to the trash. The explorer offers
  restore, delete permanently and empty trash.
- SDK API for apps (proposed name: `HackTrash`).

## 8. Phase 4: Full SDK and base apps

### 8.1 The SDK is enough for system apps

- Remove from system apps any injection of an internal service
  (`ISessionService`, `AppCatalog`, `AppLifecycleOrchestrator`,
  `IPersistentAppCatalogRepository`, synchronization services,
  direct `IJSRuntime`…).
- For each need, add to the SDK a public API protected by a
  capability, normal or dangerous according to section 6.4. Examples: identity of
  the current user, reading the app catalog, starting and stopping
  apps or services, browser file download and upload.
- An architecture test fails the build if a project under `Apps/`
  references `HackerOs.Platform.Core` or `HackerOs.Platform.Blazor`.

### 8.2 Target features

At least parity with the legacy (`src/apps/`), plus these additions.

**File explorer**
- Existing to keep: navigation, history, creation, renaming,
  deletion, zip, properties, "open with", upload and download.
- To add: cut, copy, paste (with the SDK clipboard),
  trash (section 7.4), drag and drop.

**Task manager**
- Existing to keep: list of processes and services, kill, service
  startup mode.
- To add: performance tab with CPU and memory charts, disk
  and network tabs as in the legacy, per-process detail, filter,
  multiple selection. All data comes from `/proc`.

**Text editor**
- Existing to keep: open, save, save as, search,
  detection of write conflicts.
- To add: replace and replace all.

**Console**
- Existing to keep: history and current directory.
- To add: Tab completion (commands and paths), multiple tabs.
  Pipes, redirections, `&&` and variables come from phase 3.

## 9. Out of scope for now

- Execution isolation of apps (watchdog, Web Workers): an app with an
  infinite loop still freezes the whole OS. **Taken up by phase 5**
  ([`execution-isolation-plan.md`](execution-isolation-plan.md), ADR 0066 to
  0070, 2026-10-16).
- App store and installation of third-party apps at runtime. The generated
  `manifest.json` format is already designed for it.
- Server, synchronization, mobile and simulated network: not
  modified by this plan, except for adaptation to the new APIs.

## 10. ADRs to write

Each ADR below explicitly replaces the cited existing ADR, which is
not modified.

| New ADR | Replaces | Content |
|---|---|---|
| [0043](../adr/0043-hack-file-api-and-ambient-context.md) `Hack*` file API and ambient context | n/a | D3, D4, D5, section 5.1 |
| [0044](../adr/0044-settings-as-files.md) Settings as files | 0004, 0011 (partially), 0029 | D16 to D18, section 5.2, lock syntax |
| [0045](../adr/0045-logs-as-files.md) Logs as files | n/a | D19, section 5.3, rotation sizes |
| [0046](../adr/0046-virtual-proc-and-dev-filesystems.md) Virtual file systems `/proc` and `/dev` | completes 0008 | D20, section 5.4 |
| [0047](../adr/0047-app-definition-and-generated-manifests.md) Manifest as a C# class and generation | 0010 (in part) | D6, D7, D8, section 6.1-6.2 |
| [0048](../adr/0048-system-apps-and-uninstall.md) System apps and uninstallation | completes 0032, partly replaces 0033 | D9 to D11, section 6.3 |
| [0049](../adr/0049-dangerous-capabilities-and-admin-approval.md) Dangerous permissions and approval | completes 0002, 0003, 0042 | D23 to D25, section 6.4 |
| [0050](../adr/0050-shell-grammar-and-process-environment.md) Shell grammar and process environment | 0014 | D12, D13, section 7.1-7.2 |
| [0051](../adr/0051-unified-commands-project.md) Grouped commands, no hard-coded exception | 0034 (section 1) | D14, section 7.3 |
| [0052](../adr/0052-trash.md) Trash | n/a | D15, section 7.4 |
| [0055](../adr/0055-base-apps-on-public-sdk-only.md) Base apps on the public SDK only | completes 0019 | D21, section 8.1: permitted references and injections, build verification, authentication agent |
| [0056](../adr/0056-sdk-apis-for-system-apps.md) SDK APIs that replace internal services | completes 0044, 0048, 0049 | section 8.1: one API and one capability per need of system apps |
| [0057](../adr/0057-proc-metrics-for-task-manager.md) System metrics in `/proc` and `HackDriveInfo` | completes 0046 | section 8.2, task manager |
| [0058](../adr/0058-base-app-features.md) Base app features | n/a | D22, section 8.2, work items `rebuild/4-*` |
| [0059](../adr/0059-sdk-boundary-diagnostic-codes.md) SDK boundary diagnostic codes | 0055 (code numbers) | `HOSAPP0030` and `HOSAPP0031`, the numbers of ADR 0055 being already taken |
| [0060](../adr/0060-system-administration-sdk-adjustments.md) Adjustments to the system administration API | 0056 (sections 8, 9 and 11, in part) | section 8.1: `SystemPower`, `HackPermissionDecision`, writing OS documents, host clipboard for Diagnostic |

## 11. Details to settle in the ADRs

These points do not influence the plan's direction. They are settled in
the corresponding ADR, with the value proposed below by default.

- `rm` and the trash: `rm` sends to the trash and `rm --permanent`
  deletes permanently. **Proposed.**
- Administrator lock syntax in `/etc`: a `[locked]` section that
  lists the non-overridable keys. **Proposed.**
- Log rotation size: 1 MiB per file, 5 archives. **Proposed.**
- Location of the system apps list: MSBuild property
  `IsSystemApp="true"` on the host's `ProjectReference`. **Proposed.**

## 12. Task list

### Preparation
- [x] Discuss the findings and clear up ambiguities with Benoit (25 questions).
- [x] Move the old documents into `docs/old/`, except `docs/adr/`, and fix the links.
- [x] Write this plan and the new `docs/README.md` index.
- [x] Write the ADRs of section 10, phase by phase, before the code of each phase.
  - [x] Phase 1: ADR 0043 to 0046 (2026-10-10).
  - [x] Phase 2: ADR 0047 to 0049 (2026-10-10), with their `rebuild/2-*` work items.
  - [x] Phase 3: ADR 0050 to 0052 (2026-10-10), with their `rebuild/3-*` work items (table in ADR 0052).
  - [x] Phase 4: ADR 0055 to 0058 (2026-10-10), with their `rebuild/4-*` work items (table in ADR 0058).

### Phase 1: Files
- [x] Create the types `HackFile`, `HackDirectory`, `HackPath`, `HackFileInfo`, `HackDirectoryInfo`, `HackFileStream`, `HackFileSystemWatcher`.
- [x] Create the `Hack*` exceptions and the translation from `FileSystemErrorCode`.
- [x] Set up the ambient execution context set by the OS. See [`hack-file-api.md`](../../framework/hack-file-api.md).
- [x] Replace the separate settings store with files, with migration of existing values ([`settings-files.md`](../../framework/settings-files.md)).
- [x] Implement the `/etc` then `~/.config` layers, the administrator lock and tolerance for invalid files ([`settings-files.md`](../../framework/settings-files.md)).
- [x] Create the typed settings API and adapt the Settings app ([`settings-files.md`](../../framework/settings-files.md)).
- [x] Write logs to `/var/log` with rotation, and adapt Error Log Viewer ([`logging.md`](../logging.md)).
- [x] Mount `/proc` and `/dev` as virtual ([`virtual-proc-and-dev.md`](../virtual-proc-and-dev.md)).
- [x] Contract tests for each point ("Tests" section of each document).

### Phase 2: App model
- [x] Create `AppDefinition` and the `[AppManifest]` attribute ([`app-model.md`](../../framework/app-model.md)).
- [x] Create the MSBuild task that generates `manifest.json` per app, with validation.
- [x] Create the `BuiltInApplications.json` aggregation task and the "one reference is enough" `.targets`.
- [x] Migrate the existing apps, then delete the hand-written `app.manifest.json` files and `StaticManifest` ([`app-model.md`](../../framework/app-model.md#migrating-the-apps)).
- [x] Implement the system apps list, machine and user uninstallation, and reinstallation.
  - [x] System apps list: `AppCatalog.IsSystemApp`, `IsSystemApp="true"` on the host's `ProjectReference`s (`rebuild/2-system-apps`, [`app-model.md`](../../framework/app-model.md#system-apps-adr-0048-1)).
  - [x] Machine and user uninstallation, reinstallation (`rebuild/2-app-uninstall`, ADR 0048, [`app-installation.md`](../app-installation.md)).
- [x] Classify capabilities, create the approval window and the security policy.
  - [x] Classification of capabilities, removal of `settings.*` and `diagnostics.*`, seeder according to system status (`rebuild/2-capability-classification`, [`capabilities.md`](../../framework/capabilities.md)).
  - [x] Approval window and security policy (`rebuild/2-dangerous-approval`, ADR 0049, [`dangerous-capability-approval.md`](../dangerous-capability-approval.md)).

### Phase 3: Shell, commands and trash
- [x] Extend the parser: pipes, redirections, `&&`, variables (`rebuild/3-shell-interpreter`, ADR 0050 and 0054, [`shell-interpreter.md`](../shell-interpreter.md)).
- [x] Add current directory and environment per process (`rebuild/3-process-environment`, ADR 0050 and 0053, [`process-environment.md`](../process-environment.md)).
- [x] Merge the 28 command projects and make `cd`, `pwd`, `clear`, `help` normal.
  - [x] Merge of the 28 projects into `HackerOs.Commands`, a single `ProjectReference` in the host (`rebuild/3-commands-merge`, ADR 0051 §1, [`app-model.md`](../../framework/app-model.md#built-in-commands-a-single-project-adr-0051)).
  - [x] `cd`, `pwd`, `clear`, `help`, `sudo`, `export`, `unset`, `env` from the catalog, `IShellSession` and `shell.control`, no more hard-coded names in the terminal (`rebuild/3-shell-commands`, ADR 0051 §2-5, [`shell-commands.md`](../shell-commands.md)).
- [x] Implement the trash and the `HackTrash` API (`rebuild/3-trash`, ADR 0052, [`trash.md`](../trash.md)).

### Phase 4: Full SDK and base apps
- [x] Inventory the internal services injected by system apps and add their equivalents to the SDK.
  - [x] `HackUser`, `HackEnvironment.GetFolderPath`, `HackApps`, `HackAppManagement` and the OS authentication agent; FileExplorer, TextEditor and CodeEditor leave the exemption list (`rebuild/4-sdk-user-and-apps`, ADR 0055 §5 and 0056 §1-3, [`app-sdk.md`](../../framework/app-sdk.md), [`admin-authentication-agent.md`](../admin-authentication-agent.md)).
  - [x] `HackSettings.OpenSystemAsync`, `HackPermissionAdministration`, `HackServerConnection`, `HackSystem`, `HackStorageDiagnostics`; Settings and Diagnostic leave the exemption list, which is empty (`rebuild/4-sdk-system-admin`, ADR 0056 §8-11 and 0060, [`system-administration.md`](../../framework/system-administration.md)).
- [x] Add the architecture test that forbids internal references from `Apps/`.
  - [x] Build verification of references and injections, `IJSRuntime` test, temporary exemption list (`rebuild/4-sdk-boundary`, ADR 0055 and 0059, [`app-sdk.md`](../../framework/app-sdk.md)).
  - [x] Shell host: `HackShell`, capability `shell.host`, `TerminalPanel` without `Platform.Core`, system CodeEditor (`rebuild/4-sdk-shell-host`, ADR 0056 §7, [`shell-host.md`](../../framework/shell-host.md)).
  - [x] Exemption mechanism removed: every boundary violation is an error, no project can escape it, the whole solution compiles without a boundary diagnostic (`rebuild/4-sdk-boundary-final`, ADR 0055 §6, [`app-sdk.md`](../../framework/app-sdk.md#end-of-temporary-exemptions-adr-0055-6)).
- [x] Explorer: cut, copy, paste, trash, drag and drop (`rebuild/4-file-explorer`, ADR 0056 §12 and 0058 §1, [`file-explorer.md`](../file-explorer.md)).
  - [x] OS clipboard shared with the host (text, pasted files, `clipboard.config` setting), Diagnostic on `HackClipboard` (`rebuild/4-file-explorer`, ADR 0065, [`clipboard.md`](../clipboard.md)).
- [x] Task manager: performance, disk, network, detail, filter, multiple selection, through `/proc` (`rebuild/4-proc-metrics` and `rebuild/4-task-manager`, ADR 0057 and 0058 §2, [`system-metrics.md`](../system-metrics.md), [`task-manager.md`](../task-manager.md)).
- [x] Editor: replace and replace all (`rebuild/4-text-editor`, ADR 0058 §3, [`text-editor.md`](../text-editor.md)).
- [x] Console: Tab completion and tabs (`rebuild/4-terminal`, ADR 0058 §4, [`terminal-console.md`](../terminal-console.md)).

### After phase 4 (outside the initial plan, ADR 0061 to 0065)
- [x] Terminal on xterm.js, line discipline in C#, alternate screen (`rebuild/terminal-xterm`, PR #115, ADR 0061, [`terminal-emulator.md`](../terminal-emulator.md)).
- [x] VS Code-style workspace code editor (`rebuild/code-editor-workspace` and `rebuild/code-editor-dock`, PRs #117 to #119, ADR 0062, [`code-editor.md`](../code-editor.md)) and embeddable app components, terminal embedded through `IHackTerminal` (ADR 0063, [`embedded-components.md`](../../framework/embedded-components.md)).
- [x] POSIX shell, scripts, jobs, apps launched by name, Linux commands, `nano` (PRs #120 and #121, ADR 0064, [`shell-interpreter.md`](../shell-interpreter.md), [`linux-commands.md`](../linux-commands.md)).
- [x] OS clipboard shared with the host (PR #122, ADR 0065, [`clipboard.md`](../clipboard.md)), see phase 4 above.

### Final verification of phase 4 (2026-10-16)

Re-run of `rebuild/4-sdk-boundary-final` on `rebuild/base` after PRs #115
to #122, before the merge of phases 1 to 4 into `main` (PR #114):

- [x] SDK boundary: the solution compiles with no warning or
  diagnostic `HOSAPP0002`, `HOSAPP0003`, `HOSAPP0030`, `HOSAPP0031` or
  `HOSSDK0001`; no reference from `Apps/` or the SDK to `Platform.*`,
  `Infrastructure.*` or `Server.*`; no more exemption mechanism;
  `SdkBoundarySourceTests` covers all apps, new ones included.
- [x] Apps and components added since #113: public SDK only, capabilities
  declared in their `[AppManifest]`, no injection of an internal service.
- [x] No `[Obsolete]` API used in `Apps/`.
- [x] One drift fixed: the terminal's copy bypassed the OS clipboard
  and `clipboard.config`; it now goes through the bridge of ADR
  0065 ([`terminal-emulator.md`](../terminal-emulator.md)).
- [x] All `Tests/*.Tests` suites excluding E2E pass; documentation with no
  dead link, complete indexes (`docs/README.md`, `docs/old/README.md`).

Detail: [`app-sdk.md`](../../framework/app-sdk.md#final-verification-of-phase-4-2026-10-16).

## 13. References

- [`old/README.md`](../old/README.md): index of the old documentation.
- [`old/virtual-filesystem.md`](../old/virtual-filesystem.md): file engine kept.
- [`old/settings-system.md`](../old/settings-system.md): current settings system.
- [`old/app-contracts.md`](../old/app-contracts.md) and [`old/blazor-app-sdk.md`](../old/blazor-app-sdk.md): current app contracts.
- [`old/ecosystem-maturity-gap-analysis.md`](../old/ecosystem-maturity-gap-analysis.md): previously identified gaps, including isolation.
- [`adr/`](../adr/): architecture decisions in force.
- `src/`: TypeScript legacy, read-only behavior reference.
