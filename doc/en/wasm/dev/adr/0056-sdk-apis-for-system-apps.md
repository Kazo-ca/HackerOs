# ADR 0056: SDK APIs that replace internal services

## Status

Accepted on 2026-10-10.

- **Plan phase:** phase 4: complete SDK and base apps
  ([`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), section 8.1).
- **Applies:** ADR 0055 (an app gets an OS service only through the SDK;
  authentication agent).
- **Completes:** ADR 0049: five dangerous capabilities enter the catalog
  (section 11). ADR 0048: `HackerOs.Apps.CodeEditor` joins the host's trust
  list (section 7). ADR 0044: `HackSettings` also opens the OS documents
  (section 8).
- **Builds on:** ADR 0043 (`Hack*` facades, exceptions), ADR 0046 and 0057
  (`/proc`), ADR 0051 (`IShellSession`, `shell.control`).

## Context

ADR 0055 forbids apps from using the internal services listed in its context.
For each of them, a public API protected by a capability is needed, covering
the app's real need. Some of it already exists in the SDK and is simply not
used:

| Need | Already in the SDK |
|---|---|
| Launch an app, open a file | `IAppIntentGateway` (`apps.launch`) |
| List and kill processes | `IAppProcessGateway` (`process.list`, `process.manage`) |
| Start, stop, configure a service | `IAppServiceControlGateway` (`services.manage`) |
| User and machine name, current directory | `HackEnvironment` |
| Clipboard (text and file references) | `IAppClipboardGateway` |
| Download a file to the browser | `BrowserFileDownloader` (`HackerOs.AppSdk.Blazor`) |
| Read a setting, watch a file | `HackSettings`, `HackFileSystemWatcher` |

## Decision

Each section gives the static facade (recommended form), the gateway on
`IAppExecutionContext` that carries it, the required capability, and the
internal service it replaces. "Administrator" means that the operation also
goes through the authentication agent (ADR 0055, section 5). Names follow
`System.*` with the `Hack` prefix (decision D3). All methods are
asynchronous (decision D4) except pure accessors.

### 1. Current user

Replaces `ISessionService.CurrentPrincipal` (FileExplorer, TextEditor,
CodeEditor, Settings).

```csharp
HackUserInfo me = HackUser.Current;          // LoginName, DisplayName, HomeDirectory, IsAdministrator
string docs = HackEnvironment.GetFolderPath(HackSpecialFolder.MyDocuments);  // /home/benoit/Documents
```

- `HackUser.Current` describes the **effective** user of the process: `root`
  and `/root` for a process launched by `sudo` (ADR 0042), the session user
  otherwise. `IsAdministrator` is `true` for an administrator or for root.
- `HackSpecialFolder` (modeled on `Environment.SpecialFolder`):
  `UserProfile` (`~`), `Desktop` (`~/Desktop`), `MyDocuments`
  (`~/Documents`), `ApplicationData` (`~/.config`), `LocalApplicationData`
  (`~/.local/share`).
- Gateway: none new; the factory fills `IAppExecutionContext`
  (`UserId`, `HomeDirectory`, `IsRootElevated`, `UserAuthority`), completed
  by `UserLoginName` and `UserDisplayName`.
- **Capability: none.** On Linux, every process knows its user
  (`getuid`, `getpwuid`).

### 2. App catalog

Replaces `AppCatalog` and `IAppEnablementRegistry` (FileExplorer, Settings,
SystemMonitor).

```csharp
IReadOnlyList<HackAppInfo> apps = await HackApps.GetInstalledAsync();
IReadOnlyList<HackAppInfo> editors = await HackApps.GetHandlersAsync("/home/benoit/notes.md");
await HackApps.LaunchAsync("org.hackeros.text-editor", ["/home/benoit/notes.md"]);
await HackApps.OpenFileAsync("/home/benoit/notes.md");
```

- `HackAppInfo`: `Id`, `Name`, `Description`, `Kind`, `Category`, `Icon`,
  `Version`, `IsSystemApp`, `FileHandlers`. No entry point and no .NET
  type.
- `GetInstalledAsync` returns only the apps installed for the current user
  (ADR 0048: an uninstalled app is hidden everywhere).
  `GetHandlersAsync` applies the file associations and the same filter
  ("Open with").
- `HackApps.Changed` signals an installation or an uninstallation.
- `LaunchAsync` and `OpenFileAsync` are facades over `IAppIntentGateway`.
- Gateway: `IAppCatalogGateway Catalog`.
- **Capabilities:** none for reading (the list of installed apps is
  public, like `/usr/share/applications`); `apps.launch` (normal) to
  launch.

### 3. App management

Replaces `IAppInstallationService` and `AppCatalog` in the "Installed
Apps" tab of Settings.

```csharp
IReadOnlyList<HackAppInstallInfo> all = await HackAppManagement.GetAllAsync();
await HackAppManagement.UninstallAsync("org.hackeros.calculator", HackInstallScope.User);
await HackAppManagement.ReinstallAsync("org.hackeros.calculator", HackInstallScope.Machine);
```

- `HackAppInstallInfo`: the app (`HackAppInfo`), its state (`Installed`,
  `UninstalledForUser`, `UninstalledForMachine`) and the list of apps that
  would go away with it (the current `GetUninstallClosure`).
- `User` scope: the user for themselves. `Machine` scope:
  **administrator**.
- A system app throws `InvalidOperationException` (ADR 0048: never
  uninstallable).
- Gateway: `IAppManagementGateway AppManagement`.
- **Capability:** `apps.manage` (dangerous, already in the catalog and reserved
  for this API by ADR 0049).

### 4. Processes

Replaces `IProcessManager` and `AppLifecycleOrchestrator` (SystemMonitor).

```csharp
HackProcess[] all = await HackProcess.GetProcessesAsync();
HackProcess p = await HackProcess.GetProcessByIdAsync(42);
await p.RefreshAsync();
await p.KillAsync();
```

- Modeled on `System.Diagnostics.Process`: `Id`, `ParentId`, `ProcessName`,
  `AppId`, `Kind`, `UserName`, `State`, `StartTime`, `WorkingSet64`,
  `CpuUsage`, `CommandLine`, `WorkingDirectory`, `ReadBytes`, `WriteBytes`.
- **The data comes from `/proc`** (plan, section 8.2): the facade reads
  `/proc/<pid>/status`, `cmdline`, `cwd` and `io` (ADR 0046 and 0057) through
  the file gateway. It therefore sees exactly what `ps` sees: its own
  process, and the others with `process.list`.
- `KillAsync` and `CloseMainWindowAsync` (polite stop with a timeout, which
  replaces `AppLifecycleOrchestrator.StopAsync`) go through `IAppProcessGateway`:
  killing is a system call, not a file write.
- **Capabilities:** `process.list` (normal) to see other processes;
  `process.manage` (dangerous) to stop a process other than one's own.
  This is the current rule of `AppProcessGateway`, unchanged.
- No subscription to raw OS events: a task manager rereads `/proc` at a
  regular interval, like `top`.

### 5. Services

Replaces `AppCatalog` (filtered on `AppKind.Service`) and the orchestrator in
the Services tab of SystemMonitor.

```csharp
HackServiceController[] services = await HackServiceController.GetServicesAsync();
HackServiceController svc = services[0];
await svc.StartAsync();
await svc.SetStartTypeAsync(ServiceStartMode.Automatic);
```

- Modeled on `System.ServiceProcess.ServiceController`: `ServiceName` (the
  app id), `DisplayName`, `Status` (`Running`, `Stopped`), `StartType`.
- The list comes from `HackApps.GetInstalledAsync`; the state, from
  `HackProcess`.
- `StartAsync`, `StopAsync`, `SetStartTypeAsync`: facades over
  `IAppServiceControlGateway`.
- **Capabilities:** none to list; `services.manage` (dangerous) to
  act.

### 6. Network

Replaces `ISimulatedNetworkService` (Browser, `ping`, `curl`, `cat`, `nmap`)
and, in the commands, `IServerConnectionService` and `IProxyClient`.

```csharp
IPAddress[] ips = await HackDns.GetHostAddressesAsync("hackmail.sim");
HackPingReply reply = await HackPing.SendAsync("10.0.0.5", TimeSpan.FromSeconds(1));
using HackHttpClient http = new(cookies);
HackHttpResponse page = await http.GetAsync("http://hackmail.sim/inbox");
IReadOnlyList<HackPortProbeResult> ports = await HackTcpClient.ProbeAsync("10.0.0.5", 1, 1024);
```

- `HackDns`: resolution and reverse resolution. `HackPing`: one echo.
  `HackHttpClient`: `GetAsync`, `PostAsync`, `SendAsync`, redirects
  followed, cookies in a `HackCookieContainer` that the app keeps (the
  browser has one per tab). The response carries the status, the headers, the
  body and, for a simulated site, the structured page that the browser uses.
  `HackTcpClient.ProbeAsync`: port probing for `nmap`.
- **Fallback to the real network:** the OS, no longer the app, decides. It tries
  the simulated network first; if the host does not exist there, the app has
  `network.real.access` and the optional server is connected, it goes through
  the server's proxy (ADR 0023, 0035), handling the access token itself.
  The app sees neither the token nor the server state. Without a server, the result
  is "host not found"; no exception signals the absence of the server.
- The data types of the simulated network (`SimulatedPage`...) stay in
  `Simulation.Abstractions`; only the facades and `IAppNetworkGateway
  Network` are new.
- **Capabilities:** `network.simulated.read` (normal) for DNS, ping, GET and
  probing; `network.simulated.write` (normal) for POST;
  `network.real.access` (dangerous) for the real fallback. Classifications
  unchanged.
- `HackerOs.Commands` loses its references to `Platform.Core` and
  `Server.Contracts`. `ShellErrorText.Describe`, the only other borrowing (`cd`),
  moves into the SDK (`HackerOs.AppSdk`, next to `IShellSession`).

### 7. Shell host

Replaces `ShellInterpreter`, `ISessionService` and `IElevationService` in
`TerminalPanel` (library `HackerOs.AppSdk.Terminal`, used by
Terminal and CodeEditor).

```csharp
await using IHackShell shell = await HackShell.StartAsync(new HackShellOptions
{
    WorkingDirectory = HackEnvironment.GetFolderPath(HackSpecialFolder.UserProfile),
    CredentialPrompt = myTerminalPrompt,
});
int status = await shell.ExecuteAsync("ls ~ | grep notes", stdin, stdout, stderr, ct);
HackShellCompletion completion = await shell.CompleteAsync("cat ~/Doc", cursor: 9);
```

- `HackShell.StartAsync` creates an interactive `hsh` shell for the current
  user: a **child process** of the caller, with its directory and its
  environment (ADR 0050). The commands it launches are its children, as
  on Linux (terminal, shell, command).
- `IHackShell`: `ExecuteAsync` (one line, with streams), `CompleteAsync`
  (section 4 of ADR 0058), `GetInstalledCommands`, `WorkingDirectory`,
  `ExitStatus` (`$?`), `DisposeAsync` (end of the shell process).
- Credential requests (`sudo`, approval of dangerous capabilities
  in text mode, ADR 0049) go through the `HackCredentialPrompt` provided
  by the caller: the terminal reads the masked input, the OS verifies it. The
  terminal no longer verifies anything itself.
- Distinct from `IShellSession` (ADR 0051): `IShellSession` is what a
  **command** sees of the shell that launched it (`shell.control`); `IHackShell`
  is what a **terminal emulator** sees of the shell it hosts.
- Gateway: `IAppShellHostGateway ShellHost`.
- **Capability: `shell.host` (new, dangerous).** A command launched by the
  shell runs with **its own** capabilities, not those of the host app, and without
  a visible window: an app that only has `notifications.post` could otherwise
  read all of `~` through `cat`. Risk: "Run commands on your behalf, with
  the rights of those commands."
- **Trust list:** `HackerOs.Apps.CodeEditor` receives
  `IsSystemApp="true"` in the host (ADR 0048), like Terminal. It is an app
  shipped with the OS, and its terminal panel must not trigger an
  administrator approval on first launch.

### 8. OS settings

Replaces `ThemePreferenceService` in Settings.

```csharp
await using HackSettingsDocument appearance = await HackSettings.OpenSystemAsync("appearance");
string theme = appearance.Get<string>("theme");
await appearance.SetAsync("theme", "gothic");                         // ~/.config/hackeros/appearance.config
await appearance.SetAsync("theme", "gothic", SettingLayer.System);    // /etc/hackeros/appearance.config
```

- `HackSettings.OpenSystemAsync(documentId)` opens an OS document
  (`/etc/hackeros/<doc>.config` and `~/.config/hackeros/<doc>.config`), with the
  schema declared by the OS. Same layers, `[locked]` lock and tolerance of
  invalid keys as for an app (ADR 0044).
- **Capability: none.** The file's rights decide
  (decision D16): everyone reads, the user writes their own layer, the `/etc`
  layer belongs to root. A refused write throws
  `UnauthorizedAccessException`; the app can offer to relaunch with `sudo`.
- `ThemePreferenceService` stays in the platform: it already watches these
  files and applies the theme when they change.

### 9. Permission administration

Replaces `AppPermissionAdministration` (taken from `IServiceProvider`) and
`ISessionService` in the Permissions tab of Settings.

```csharp
DangerousCapabilityPolicy policy = await HackPermissionAdministration.GetPolicyAsync();
IReadOnlyList<HackAppPermissionInfo> rows = await HackPermissionAdministration.GetOverviewAsync();
await HackPermissionAdministration.SetMachineDecisionAsync(appId, capability, HackPermissionDecision.Allow);
await HackPermissionAdministration.RemoveOwnDenialAsync(appId, capability);
```

- `SetPolicyAsync` and `SetMachineDecisionAsync`: **administrator**. They no longer
  take a password as a parameter: the agent asks for it.
- `RemoveOwnDenialAsync`: the user for themselves, without administrator.
- Gateway: `IAppPermissionAdministrationGateway PermissionAdministration`.
- **Capability: `permissions.manage` (new, dangerous).** Risk:
  "Grant or remove dangerous permissions for other apps."

### 10. Optional server and synchronization

Replaces `IServerConnectionService`, `IFileSystemSyncService`,
`IGrantsSyncService` and `IFileAssociationsSyncService` in Settings.

```csharp
HackServerConnectionState state = await HackServerConnection.GetStateAsync();
await HackServerConnection.ConnectAsync(serverUrl, login, password);
await HackServerConnection.SyncAsync(HackSyncDomain.FileSystem, HackSyncDirection.Push);
IReadOnlyList<HackSyncConflict> conflicts = await HackServerConnection.GetConflictsAsync();
```

- `GetStateAsync`, `ConnectAsync` (existing account), `CreateAccountAsync`,
  `DisconnectAsync`, `SyncAsync(domain, direction)` for `FileSystem`, `Grants` and
  `FileAssociations`, `GetConflictsAsync`.
- The password of the **server account** is entered by the app: it is a
  credential of the remote service, not a local password (ADR 0055,
  section 5).
- Without a configured server, the state is "not connected"; nothing throws
  an exception because of the absence of the server (`AGENTS.md`).
- Gateway: `IAppServerConnectionGateway ServerConnection`.
- **Capability: `server.connection.manage` (new, dangerous).** Risk:
  "Connect this machine to a server and send your files and
  permissions to it."

### 11. System: reboot, reset, storage inspection

Replaces the `IJSRuntime` calls of Settings (`location.reload`, deletion
of the IndexedDB database) and `Infrastructure.Browser` in Diagnostic.

```csharp
await HackSystem.RebootAsync();
await HackSystem.FactoryResetAsync();
HackStorageSnapshot snapshot = await HackStorageDiagnostics.GetSnapshotAsync();
```

- `RebootAsync`: flushes pending writes (journals, ADR 0045), terminates
  the processes, then reloads the page. **Administrator**, like `reboot`
  on Linux.
- `FactoryResetAsync`: deletes the OS's IndexedDB database then reboots.
  **Administrator.**
- `GetSnapshotAsync`: the snapshot that Diagnostic reads today (stores,
  records, resolved paths), with data types moved into
  the SDK. The implementation stays in `Infrastructure.Browser`.
- Diagnostic copies to the clipboard through `IAppClipboardGateway`
  (`clipboard.write`) instead of `navigator.clipboard`, and saves the JSON
  through `BrowserFileDownloader`.
- Gateways: `IAppSystemGateway System`, `IAppStorageDiagnosticsGateway
  StorageDiagnostics`.
- **Capabilities (new, dangerous):**
  - `system.power` (reboot and reset). Risk: "Reboot
    or completely erase this machine."
  - `system.storage.inspect` (raw storage snapshot). Risk: "Read
    the machine's raw storage, including the files of all
    users."
- Diagnostic is not a system app: its first launch asks for
  administrator approval. This is intended, the snapshot bypasses
  file rights.

### 12. File clipboard

Completes `IAppClipboardGateway` for cut and copy in the explorer
(ADR 0058).

```csharp
await HackClipboard.SetFileDropListAsync(paths, HackClipboardFileOperation.Cut);
HackClipboardFileList? list = await HackClipboard.GetFileDropListAsync();   // Operation + Paths
```

- Modeled on `Clipboard.SetFileDropList`; the content follows GNOME's
  `x-special/gnome-copied-files` format: first line `copy` or `cut`,
  then one `file://` URI per line. An app that reads the clipboard as text
  sees the paths there.
- The current `SetFileReferencesAsync` and `GetFileReferencesAsync` become
  forms of "copy" and are marked `[Obsolete]` (ADR 0019).
- **Capabilities:** `clipboard.read`, `clipboard.write` (normal, unchanged).

### 13. Capability summary

| Capability | Classification | New | API |
|---|---|---|---|
| `apps.launch` | normal | no | `HackApps.LaunchAsync`, `OpenFileAsync` |
| `apps.manage` | dangerous | no | `HackAppManagement` |
| `process.list` | normal | no | `HackProcess` (other processes) |
| `process.manage` | dangerous | no | `HackProcess.KillAsync` (process other than one's own) |
| `services.manage` | dangerous | no | `HackServiceController` (actions) |
| `network.simulated.read` / `.write` | normal | no | `HackDns`, `HackPing`, `HackHttpClient`, `HackTcpClient` |
| `network.real.access` | dangerous | no | real fallback of the same APIs |
| `shell.host` | dangerous | **yes** | `HackShell` |
| `permissions.manage` | dangerous | **yes** | `HackPermissionAdministration` |
| `server.connection.manage` | dangerous | **yes** | `HackServerConnection` |
| `system.power` | dangerous | **yes** | `HackSystem` |
| `system.storage.inspect` | dangerous | **yes** | `HackStorageDiagnostics` |
| `clipboard.read` / `.write` | normal | no | `HackClipboard` |
| - | - | - | `HackUser`, `HackEnvironment.GetFolderPath`, `HackApps` (read), `HackSettings.OpenSystemAsync` |

Each app updates its `[AppManifest]` definition to declare what it
uses. A system app receives these capabilities automatically (ADR 0049).

## Consequences

- Each system app is rewritten on these APIs; the work items are in
  ADR 0058.
- The logic of the network fallback, the server token, the administrator check
  and the database erasure moves into Platform Core,
  behind a testable gateway.
- The task manager depends on `/proc`: a field missing from
  the screen is first missing in `/proc`, and ADR 0057 adds it there.
- Five new dangerous capabilities: a third-party app that declares one
  goes through the approval window.

## Rejected alternatives

- **A single "system administration" capability** for permissions,
  server, power and storage: fewer lines in the catalog, but an
  administrator would not know what they are approving.
- **`shell.host` as normal:** it would make the capability useless, since
  commands have their own rights (see section 7).
- **Storage snapshot as files under `/sys/kernel/debug`**, like
  `debugfs`: attractive for "everything is a file", but the snapshot is a single
  JSON document, not a tree, and a third virtual provider would be needed
  for a single development app.
- **Process events pushed** to the task manager: more
  reactive, but the OS event bus would have to be exposed; periodic reading of `/proc`
  is enough and it is what `top` does.

## References

- [`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), sections 4
  (D3, D4, D5, D16, D21), 8.1 and 8.2.
- ADR 0019, 0023, 0035, 0042, 0043, 0044, 0045, 0046, 0048, 0049, 0050, 0051,
  0055, 0057, 0058.
- `Shared/HackerOs.Simulation.Abstractions/Gateways/AppGatewayContracts.cs`,
  `Shared/HackerOs.AppSdk/Clipboard/IAppClipboardGateway.cs`,
  `Platform/HackerOs.Platform.Core/Policy/AppPermissionAdministration.cs`,
  `Platform/HackerOs.Platform.Core/Apps/AppInstallationContracts.cs`.
