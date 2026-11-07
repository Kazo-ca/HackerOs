> [Version française](../../../fr/wasm/framework/sdk-reference.md)

# Kazo OS SDK reference

## Purpose

List of the public APIs an app can use, library by
library, with the required capability and the document that details them. To
learn how to write an app, start with the
[developer guide](sdk-developer-guide.md). The rules of the SDK boundary
(references, injection, JavaScript) are in [`app-sdk.md`](app-sdk.md).

API conventions:

- The `Kazo*` facades are static and read the **ambient context**
  (`KazoExecutionContext`) that the OS opens around the app's code. Several
  also exist with an explicit `IAppExecutionContext` as the first
  parameter, for code that runs outside this context.
- Names and behaviors follow `System.IO`, `System.Diagnostics`,
  `System.Net` and `System.ServiceProcess`, with the `Hack` prefix and
  asynchronous methods.
- Paths accept `~` and paths relative to the process's current
  directory.
- A missing capability throws `KazoUnauthorizedAccessException`
  (`RequiredCapability`), or `AppGatewayAccessDeniedException` for a
  gateway used directly.

## Libraries

All are published as local NuGet packages of the same name, at the version of
`build/Versions.props` (`2.2.2` today, published by the `Release Kazo OS` workflow when its PR is merged; `2.1.0` on 2026-10-26, the first automatic release, ADR 0173; major 2 dates from the end of the desktop series, ADR 0149 and 0161; `1.2.0` at the end of the `exec` series, ADR 0132 §2; `1.1.0` for the v3.1 series, ADR 0117 §5; `1.0.0` at the SDK freeze, ADR 0114); another
repository consumes them as described in [`building-on-kazoos.md`](building-on-kazoos.md).
The SemVer compatibility promise covers the libraries on this page and
`Kazo.Os.Theming.Blazor`, not the platform (ADR 0114 §2).

This page presents the APIs an app uses day to day; it is not
exhaustive. The complete and exact list of the public 2.0 surface is the
`PublicAPI.Shipped.txt` file of each project (about 940 types at the 1.0 freeze),
checked at build by the public API analyzer: an addition goes into
`PublicAPI.Unshipped.txt`, a removal requires a `*REMOVED*` line (ADR 0114).
Unpublished additions (lines of `Unshipped.txt`) will go out with the next
minor; those that are known are listed in [`sdk-packages.md`](sdk-packages.md#additions-since-200-next-minor).

| Project | Namespaces | For |
|---|---|---|
| `Shared/Kazo.Os.App.Abstractions` | `Kazo.Os.App.Abstractions` | Defining an app (`AppDefinition`, capabilities, manifest). |
| `Shared/Kazo.Os.Simulation.Abstractions` | `Kazo.Os.Simulation.Abstractions.*` | Data types of the simulated system (paths, gateways, logs, notifications, processes). |
| `Shared/Kazo.Os.AppSdk` | `Kazo.Os.AppSdk`, `.IO`, `.Settings`, `.Keyring`, `.Apps`, `.Net`, `.Clipboard`, `.Diagnostics`, `.ServiceProcess`, `.Shell`, `.Administration`, `.Threading` (SDK 1.2) | `Kazo*` facades, execution context, commands and services. |
| `Shared/Kazo.Os.AppSdk.Blazor` | `Kazo.Os.AppSdk.Blazor`, `.Charts`, `.Interop`, `.Settings`, `.Files`, `.ContextMenus`, `.Components` | Windowed apps, dialogs, charts, OS file control and context menu (SDK 2.0). |
| `Shared/Kazo.Os.AppSdk.Terminal` | `Kazo.Os.AppSdk.Terminal` | xterm.js terminal. |
| `Shared/Kazo.Os.AppSdk.Web` | `Kazo.Os.AppSdk.Web.Hosting`, `.Http`, `.Routing`, `.Mvc`, `.Views`, `.Antiforgery`, `.Sessions`, `.Diagnostics`, `.Data`, `.Logging` | ASP.Kazo internal sites in the style of ASP.NET Core MVC ([ASP.Kazo guide](asp-kazo-developer-guide.md), [`asp-kazo-mvc.md`](asp-kazo-mvc.md)). |
| `Shared/Kazo.Os.AppSdk.Web.Blazor` | `Kazo.Os.AppSdk.Web.Blazor.Sites` | Interactive Blazor sites drawn in the browser ([`blazor-sites.md`](blazor-sites.md)). |
| `Shared/Kazo.Os.AppSdk.Icons` | `Kazo.Os.AppSdk.Icons` | Icons. |
| `Shared/Kazo.Os.AppSdk.VirtualKeyboard` | `Kazo.Os.AppSdk.VirtualKeyboard` | Virtual keyboard. |

## Defining an app (`Kazo.Os.App.Abstractions`)

| Type | Role |
|---|---|
| `[AppManifest]` | Marks the class that describes the app; the build generates `manifest.json` from it. `Execution` (`AppExecutionHost`) chooses where a process without an interface runs (ADR 0070). |
| `AppExecutionHost`, `AppExecutionHosts` | `Default`, `MainThread`, `Pool`, `Dedicated`; `DefaultFor(kind)` (command → `Pool`, service → `Dedicated`, window → `MainThread`), `Resolve`, `Of(manifest)`, `IsWorker`. Manifest: `"execution": "mainThread" \| "pool" \| "dedicated"`. See [`worker-processes.md`](../dev/worker-processes.md), [`app-execution-hosts.md`](app-execution-hosts.md). |
| `AppDefinition` | Base class. Required: `Id`, `Name`, `Kind`, `EntryPoint`. Optional: `Version`, `Description`, `PublisherId`, `Category`, `LaunchVisibility`, `Icons`, `Capabilities`, `FileHandlers`, `Terminal`, `Components`, `MobileEntryPoint`, `SingleInstancePerUser`, `AutoStart`, `SupportsBack`, `Window` (`WindowOptions`, ADR 0095, [`window-model.md`](window-model.md); `RememberGeometry`, SDK 1.1, ADR 0116, [`window-geometry.md`](../dev/window-geometry.md)), `Resources`, `Settings`, `Dependencies`, `Intents`, `DeclaredTopicPermissions`, `Localizations`, `Assets`, `OomScoreAdjust` (SDK 1.2, ADR 0126 §4, [`oom-killer.md`](../dev/oom-killer.md)). |
| `AppKind` | `Window`, `Terminal`, `Service`. |
| `AppLaunchVisibility` | `Visible`, `Hidden` (absent from the launcher). |
| `FileHandler(MediaType, Extensions, Actions, Priority)` | File type the app opens. |
| `TerminalCommand(Name, Usage, Aliases)` | Shell command name. |
| `AppComponent(Contract, Type)` | Component that other apps can embed (ADR 0063); see [`embedded-components.md`](embedded-components.md). |
| `AppSettingsSchemaManifest(SchemaVersion, Fields)`, `AppSettingFieldManifest(Key, ValueType, DefaultValue, Scope, Sensitivity, AllowedValues, Group)` | Settings schema; `AppSettingValueType`: `String`, `Integer`, `Boolean`, `Enum`. |
| `AppResourceProfileManifest` | Simulated CPU, memory, disk and network weights (`None` by default). |
| `AppDefinitionMapper.ToManifest(definition)` | Definition → `AppManifest` (useful in tests). |
| `KazoOsSdk` | `Version` (version of the loaded SDK, for example `2.1.0`), `SemanticVersion`, `IsCompatible(range)` (ADR 0098 §3). `AppDefinition.SdkCompatibility` declares a range; by default, the version the app is compiled against is the minimum. |
| `AppManifest` | Validated manifest, read by the OS; `AppContext.Manifest` at runtime. `OomScoreAdj` (field `oomScoreAdj`, SDK 1.2, ADR 0126 §4): value of `AppDefinition.OomScoreAdjust`. |
| `AppCapabilities` | Capability catalog (see below); `Describe`, `IsDangerous`, `GetRisk`, `IsKnown`, `RequiresWindowKind`, `All`. |
| `VirtualPath` | Canonical absolute path (`Parse`, `Value`). |

Details: [`app-model.md`](app-model.md).

### Capabilities

| Constant | Identifier | Class | Gives access to |
|---|---|---|---|
| `FileSystemPrivateRead` / `Write` | `filesystem.private.read` / `.write` | normal | `~/.config/<appId>/`; reading `/etc/<appId>/` |
| `FileSystemUserSelectedRead` / `Write` | `filesystem.user-selected.read` / `.write` | normal | files chosen by the user |
| `FileSystemUserHomeRead` / `Write` | `filesystem.user-home.read` / `.write` | normal | home folder |
| `FileSystemSystemRead` / `Write` | `filesystem.system.read` / `.write` | **dangerous** | everything else (`/etc`, `/var`, `/proc`...); writing `/etc`, `/bin`, `/var` also requires an administrator |
| `AppsLaunch` | `apps.launch` | normal | `KazoApps.LaunchAsync`, `OpenFileAsync`, `EditFileAsync`, `RevealFileAsync`, `OpenUriAsync`, `SendIntentAsync` |
| `AppsManage` | `apps.manage` | **dangerous** | `KazoAppManagement`, `KazoComponentProviders`, `KazoFileMenuEntries` |
| `DialogFileOpen`, `DialogFileSave`, `DialogFolderSelect` | `dialogs.file-open`, `.file-save`, `.folder-select` | normal | file dialogs (windows only) |
| `FileAssociationsRead` | `file-associations.read` | normal | file associations; with `filesystem.user-home.write`, the user's own choice (ADR 0121) |
| `FileAssociationsWrite` | `file-associations.write` | **dangerous** | changing the machine's associations |
| `ProcessList` | `process.list` | normal | seeing other apps' processes |
| `ProcessManage` | `process.manage` | **dangerous** | stopping other apps' processes |
| `ServicesManage` | `services.manage` | **dangerous** | `KazoServiceController` on other apps' services |
| `WindowsManage` | `windows.manage` | **dangerous** | other apps' windows |
| `ScreensManage` | `screens.manage` | normal | `KazoScreens` (secondary screens, ADR 0184) |
| `NotificationsPost` | `notifications.post` | normal | `AppContext.Notifications` |
| `ClipboardRead` / `Write` | `clipboard.read` / `.write` | normal | `KazoClipboard` |
| `NetworkSimulatedRead` / `Write` | `network.simulated.read` / `.write` | normal | simulated network |
| `NetworkRealAccess` | `network.real.access` | **dangerous** | real network through the relay server |
| `NetworkListen` | `network.listen` | normal | `KazoHttpListener` on a prefix outside its manifest |
| `NetworkListenPrivileged` | `network.listen.privileged` | **dangerous** | listening on a port below 1024 |
| `ShellControl` | `shell.control` | normal | acting on the calling shell (`IShellSession`) |
| `ShellContextMenu` | `shell.context-menu` | normal | adding entries to the file menus (`[RegisterFileContextMenu]`, ADR 0151) |
| `ShellHost` | `shell.host` | **dangerous** | `KazoShell` |
| `KeyringReadAll` | `keyring.read-all` | **dangerous** | `KazoKeyring` on other apps' entries |
| `UsersManage` | `users.manage` | **dangerous** | `KazoUserAccounts.ResetPasswordAsync` / `RemovePasswordAsync` (ADR 0120), `CreateAsync` / `DeleteAsync` (ADR 0169) |
| `PermissionsManage` | `permissions.manage` | **dangerous** | `KazoPermissionAdministration` |
| `ServerConnectionManage` | `server.connection.manage` | **dangerous** | `KazoServerConnection` |
| `SystemPower` | `system.power` | **dangerous** | `KazoSystem` (with an administrator) |
| `SystemStorageInspect` | `system.storage.inspect` | **dangerous** | `KazoStorageDiagnostics` |
| `SystemBackup` | `system.backup` | **dangerous** | `KazoSystemBackup` (with an administrator) |

A dangerous capability of a non-system app is only granted after
approval by an administrator. Details: [`capabilities.md`](capabilities.md),
[`dangerous-capability-approval.md`](../dev/dangerous-capability-approval.md).

## Execution context (`Kazo.Os.AppSdk`)

### `IAppExecutionContext`

Built by the OS for each instance (`AppContext` in a window,
parameter of `RunCoreAsync` for a service, `context.App` for a command).

| Member | Role |
|---|---|
| `Manifest`, `InstanceId`, `ProcessId`, `SessionId`, `CancellationToken` | Instance identity; the token is canceled on close, kill, sign-out. |
| `StopRequested`, `StopRequest` | Stop request (`SIGTERM`, ADR 0069), raised once before the token is canceled: `KazoStopRequestedEventArgs` (`Reason`, `Deadline`, `Defer`). Prefer `KazoProcess.Current`. After a kill, each gateway throws `KazoProcessKilledException`. |
| `UserId`, `UserLoginName`, `UserDisplayName`, `HomeDirectory`, `UserAuthority`, `IsRootElevated` | Effective user. |
| `GrantedCapabilities`, `Capabilities` | Capabilities granted to this instance. |
| `FileSystem`, `Watch` | Raw file system (prefer `KazoFile`...) and watching; `Watch` (and `KazoFileSystemWatcher`) also works in a worker since ADR 0124. |
| `Logging` | `Log(severity, message, properties)` → `/var/log/apps/<appId>.log`. |
| `Notifications` | `Post(severity, title, message, actions, expiresAfter)`; `notifications.post`. |
| `Events` | Event bus: the app's topics (`Publish(topic, payload)`, `PublishAsync` which returns the real result even in a worker, ADR 0145, `Subscribe`, `SubscribeChannel`, `RegisterSharedChannel`) and platform events (`Subscribe<TEvent>`). In a worker, payloads of an app type are declared by `[assembly: KazoWireContext(typeof(...))]` (`KazoWireContextAttribute`, SDK 1.2, ADR 0124); see [`app-sdk.md`](app-sdk.md#processes-in-web-workers-adr-0070-and-0071). |
| `Clock` | Simulated time: `UtcNow`, `CurrentTick`, `DelayAsync`, `Schedule`. |
| `Processes` | Processes of the app and of others (`OwnProcess`, `ListProcesses`, `StopAsync`, `Kill`). Prefer `KazoProcess`. Descendants of the app's process (its `KazoBackground` jobs) count as its own, without `process.list` or `process.manage` (ADR 0140 §7). In a worker, `ListProcesses` and `Kill` of another process require an isolated page (ADR 0143 §5), otherwise `NotSupportedException`. |
| `Background` | Background jobs gateway (`IAppBackgroundGateway.RunAsync`, SDK 1.2, ADR 0128); behind `KazoBackground`. By default (a context the OS did not build), the job runs inline with the same serialization. |
| `Intents`, `Catalog`, `AppManagement` | Behind `KazoApps` and `KazoAppManagement`. `IAppIntentGateway`: `LaunchAsync`, `OpenFileAsync`, `EditFileAsync`, `RevealFileAsync`, `OpenUriAsync`, `ShowAppSettingsAsync`, `SendIntentAsync` → `AppIntentResult` (`AppIntentOutcome`, ADR 0096). |
| `Intent`, `IntentReceived` | Intent that launched the instance (`AppIntentDelivery`: `IntentId`, `PayloadJson`, `SenderAppId`, `Arguments`); event raised when the OS hands an intent or a second activation to the already open instance (ADR 0096 §5). See [`app-intents.md`](app-intents.md). |
| `Environment` | Behind `KazoEnvironment`. |
| `Culture`, `FormatCulture` | Language of texts (`en` or `fr`, from the process's `LC_ALL`/`LC_MESSAGES`/`LANG`) and format culture (`fr-CA`, from `LC_TIME`); default members reread on each call (ADR 0104). `context.GetLocalizer<Strings>()` gives an `IStringLocalizer<Strings>` bound to this language ([`localization.md`](localization.md)). |
| `Network`, `Drives`, `Clipboard`, `ShellHost`, `Services` | Behind the facades of the same domain. |
| `CrossOriginIsolation` | Cross-origin isolation state (`IAppCrossOriginIsolationGateway.GetStatusAsync`, ADR 0146, SDK 2.0); behind `KazoCrossOriginIsolation`, no capability. Default member: `NotSupportedException`. |
| `Console` | Behind `KazoConsole` (ADR 0064). |
| `SystemSettings`, `PermissionAdministration`, `ServerConnection`, `SystemPower`, `StorageDiagnostics`, `SystemBackup` | Behind the administration facades. |
| `WebDatabase` | `kazoweb.<appId>` IndexedDB database of a `Web` app (`IAppWebDatabaseGateway`, ADR 0088); behind the web SDK's `KazoWebDb`. Another app receives `UnauthorizedAccessException`. |
| `Accessibility` | Effective accessibility preferences of the session (`IAppAccessibilityGateway`: `GetPreferencesAsync`, `Changed`, ADR 0105 §3); behind `KazoAccessibility`. Without a service (tests, Web Worker): `DefaultAppAccessibilityGateway`, which returns the default values. |
| `Keyring` | User's sign-in keyring (`IAppKeyringGateway`, ADR 0100 §2); behind `KazoKeyring`. Always locked outside a session with a password. |
| `PermissionErrors` | `PermissionDenied` event on each permission refusal. |
| `OneShotElevation` | Single-use elevation tickets (ADR 0042). |

### General facades

| API | Members | Capability | Document |
|---|---|---|---|
| `KazoExecutionContext` | `Current`, `CurrentOrNull`, `WorkingDirectory`, `Enter(context, workingDirectory)`; `KazoExecutionContextMissingException` | none | [`kazo-file-api.md`](kazo-file-api.md) |
| `KazoEnvironment` | `CurrentDirectory`, `SetCurrentDirectoryAsync`, `GetEnvironmentVariable(s)`, `SetEnvironmentVariable`, `GetCommandLineArgs`, `GetFolderPath(KazoSpecialFolder)`, `UserName`, `MachineName`, `ProcessId`, `NewLine`, `Culture`, `FormatCulture` | none | [`process-environment.md`](../dev/process-environment.md) |
| `KazoLocale`, `KazoLocalization`, `KazoStringLocalizer`, `EnglishStringLocalizer<T>` (`Kazo.Os.AppSdk.Globalization`) | Locale rules (`ResolveLanguage`, `ResolveFormatCulture`, `ParseLocaleConf`, `FormatLocaleConf`, `AvailableLocales`); `GetLocalizer<T>()` bound to the process; always-English localizer for tests | none | [`localization.md`](localization.md) |
| `KazoTimeZone` (`Kazo.Os.AppSdk.Globalization`), `IAppExecutionContext.TimeZone` | The process's time zone (`TZ`, else the session's): `Current`, `Now`, `ToLocal`, `TryFind`, `Resolve`, `GetId`, `FormatOffset`, `Available` | none | [`time-zone.md`](time-zone.md) |
| `KazoSpecialFolder` | `UserProfile`, `Desktop`, `MyDocuments`, `ApplicationData`, `LocalApplicationData` | none | [`app-sdk.md`](app-sdk.md#current-user-adr-0056-1) |
| `KazoUser` | `Current`, `Describe(context)`, `Root` → `KazoUserInfo(LoginName, DisplayName, HomeDirectory, IsAdministrator)` | none | [`app-sdk.md`](app-sdk.md#current-user-adr-0056-1) |
| `KazoConsole` (`IAppConsoleGateway`) | `Out`, `Error`, `In`, `IsAttached`, `Write`, `WriteLine[Async]`, `ReadLineAsync`: the instance's standard streams; those of the shell that launched it (a windowed app launched by name writes to the terminal), `/dev/null` from the desktop (ADR 0064) | none | [`shell-interpreter.md`](../dev/shell-interpreter.md#apps-launched-by-name) |

### Files (`Kazo.Os.AppSdk.IO`)

| API | Members | Document |
|---|---|---|
| `KazoFile` | `ReadAllText/Lines/BytesAsync`, `WriteAllText/Lines/BytesAsync`, `AppendAllText/LinesAsync`, `ExistsAsync`, `CopyAsync`, `MoveAsync`, `DeleteAsync`, `OpenRead/Write/AppendAsync`, `Get/SetUnixFileModeAsync`, `CreateSymbolicLinkAsync` (ADR 0064, `ln -s`) | [`kazo-file-api.md`](kazo-file-api.md) |
| `KazoDirectory` | `CreateDirectoryAsync`, `ExistsAsync`, `GetFiles/Directories/FileSystemEntriesAsync`, `EnumerateFileSystemEntriesAsync`, `MoveAsync`, `CopyAsync` (recursive), `DeleteAsync` | [`kazo-file-api.md`](kazo-file-api.md), [`file-explorer.md`](../dev/file-explorer.md) |
| `KazoPath` | `Combine`, `Join`, `GetFileName[WithoutExtension]`, `GetExtension`, `HasExtension`, `ChangeExtension`, `GetDirectoryName`, `IsPathRooted`, `GetFullPath` | [`kazo-file-api.md`](kazo-file-api.md) |
| `KazoFileInfo`, `KazoDirectoryInfo` (`KazoFileSystemInfo`) | `GetAsync`, `RefreshAsync`, `Exists`, `Length`, dates, `UnixFileMode`, `Owner`, `Group`, `Revision`, symbolic links; instance operations | [`kazo-file-api.md`](kazo-file-api.md) |
| `KazoFileStream` | Read stream, or write stream published in one atomic write on close; the write buffer counts in `VmData` when memory is measured | [`kazo-file-api.md`](kazo-file-api.md) |
| `KazoWriteOptions` | `ExpectedRevision` (optimistic concurrency), `CreateMode` | [`kazo-file-api.md`](kazo-file-api.md) |
| `KazoFileSystemWatcher` | `Created`, `Changed`, `Deleted`, `Renamed`, `Error`, `Filter`, `IncludeSubdirectories`, `StartAsync` | [`kazo-file-api.md`](kazo-file-api.md) |
| `KazoTrash` | `MoveToTrashAsync`, `GetItemsAsync`, `RestoreAsync`, `DeleteAsync`, `EmptyAsync`, `TrashDirectory` | [`trash.md`](../dev/trash.md), [`trash-api.md`](trash-api.md) |
| `KazoFileOperations` | `CopyAsync`, `MoveAsync(…, resolveConflict)`, `TransferAsync`, `ImportAsync` (`KazoHostFile`: host files), `MoveToTrashAsync`, `GetCopyName`, `GetName` → `KazoFileTransferResult` (`Completed`, `Skipped`, `Failures`, `Cancelled`, `DescribeFailures`); Nautilus rules (`name (copy).ext`, "Replace / Keep both / Skip" through `KazoFileConflictResolver`), a folder is never copied into itself, an error does not stop the others; with the app's rights; SDK 2.0 (ADR 0161 §2). A window asks the conflict question through `Dialogs.AsFileConflictResolver(AppContext)` | [`file-explorer.md`](../dev/file-explorer.md), [`file-operations-api.md`](file-operations-api.md) |
| `KazoDesktopLayout`, `KazoDesktopCell`, `KazoDesktopOptions`, `KazoDesktopItems`, `KazoDesktopEntry`, `KazoDesktopSettings`, `KazoDesktopLink` (`.Files`) | Desktop grid as pure functions (`Order`, `Arrange`, `Move`, `CellAtPoint`, `GridSize`), `.desktop` shortcuts (`TryParse`, `Format`, `CreateShortcutAsync`; links to a file or a folder `KazoDesktopLink.CreateLinkAsync`, ADR 0185 §13), `~/.config/kazoos/desktop.config` (`ReadAsync`, `SaveOptionsAsync`, `SavePositionsAsync`); SDK 2.0 (ADR 0160) | [`desktop.md`](../dev/desktop.md), [`desktop-api.md`](desktop-api.md) |
| `KazoTextContent` | `TryDecode`, `TryReadAsync` (text or binary, like `file`) | [`text-editor.md`](../dev/text-editor.md) |
| `KazoRecoveryDraft` | `Open(name)`, `SaveAsync`, `TryReadAsync`, `TakeAsync`, `DeleteAsync`: unsaved work draft in `~/.local/state/<appId>/recovery/` (ADR 0069) | [`app-sdk.md`](app-sdk.md#clean-shutdown-and-checkpoints-adr-0069) |
| `KazoDriveInfo` | `GetDrivesAsync`: size and free space (like `df`) | [`system-metrics.md`](../dev/system-metrics.md) |
| `KazoCrossOriginIsolation` (`.Diagnostics`) | `GetStatusAsync` → `KazoCrossOriginIsolationStatus` (`IsIsolated`, `Reason`, `Setting`, `EmbedderPolicy`, `SharedPipes`, `SynchronousWorkerCalls`, `BrowserMemoryMeasurement`, `TimerResolution`); no capability; SDK 2.0 (ADR 0146) | [`worker-processes.md`](../dev/worker-processes.md#cross-origin-isolation-adr-0131-adr-0143) |
| Exceptions | `KazoIOException`, `KazoFileNotFoundException`, `KazoDirectoryNotFoundException`, `KazoFileConflictException`, `KazoUnauthorizedAccessException` | [`kazo-file-api.md`](kazo-file-api.md) |

Capabilities: depending on the path (`filesystem.private.*`, `.user-home.*`,
`.system.*`), then the file's mode bits.

Storage limits (ADR 0094 §1, ADR 0099 §2): `KazoIOException.ErrorCode`
carries the engine code, and `ShellErrorText.Describe` gives the Linux message.

| Code (`FileSystemErrorCode`) | Errno | When | Shell message |
|---|---|---|---|
| `NoSpace` (1800) | `ENOSPC` | The browser refuses the write (quota reached), or less than the root reserve remains and the caller is not an administrator | `No space left on device` |
| `FileTooLarge` (1801) | `EFBIG` | The content exceeds 16 MiB (maximum file size) | `File too large` |
| `TooManyOpenFiles` (1802, SDK 1.2) | `EMFILE` | The process has reached its `nofile` limit of open descriptors (ADR 0125, [`process-limits.md`](../dev/process-limits.md)) | `Too many open files` |

### Settings (`Kazo.Os.AppSdk.Settings`)

| API | Members | Capability | Document |
|---|---|---|---|
| `KazoSettings` | `OpenAsync([context,] documentId)`, `GetAsync<T>`, `SetAsync<T>`, `OpenSystemAsync(documentId)` | `filesystem.private.*`; OS documents: file permissions | [`settings-files.md`](settings-files.md) |
| `KazoSettingsDocument` | `Get<T>`, `GetValue`, `IsMasked`, `GetSource`, `IsLocked`, `SetAsync`, `SetManyAsync`, `ResetAsync`, `GetSecretAsync`, `SetSecretAsync`, `RefreshAsync`, `Changed`, `Warnings` | none | [`settings-files.md`](settings-files.md), [`sensitive-settings.md`](../dev/sensitive-settings.md) |
| `KazoSettingValue` | `QualifiedKey`, `Value`, `Sensitivity`, `Source`, `IsLocked`, `IsMasked`, `DisplayText` (`••••••••`) | none | [`sensitive-settings.md`](../dev/sensitive-settings.md) |
| `KazoSettingLockedException` | Key locked in `[locked]` | none | [`settings-files.md`](settings-files.md) |
| `KazoSettingMaskedException` | Non-public key whose private file is unreadable (`QualifiedKey`, `PrivatePath`) | none | [`sensitive-settings.md`](../dev/sensitive-settings.md) |

### Keyring (`Kazo.Os.AppSdk.Keyring`)

| API | Members | Capability | Document |
|---|---|---|---|
| `KazoKeyring` | `IsUnlocked`, `GetAsync([context,] id)`, `GetAsync(context, appId, id)`, `SetAsync(id, secret, label)`, `DeleteAsync`, `ListAsync`, `ListAllAsync` | none for its own entries; `keyring.read-all` (dangerous) for those of other apps | [`sensitive-settings.md`](../dev/sensitive-settings.md) |
| `KazoKeyringEntry` | `AppId`, `Id`, `Label`, `UpdatedAtUtc` (never the secret) | none | [`sensitive-settings.md`](../dev/sensitive-settings.md) |
| `KazoKeyringLockedException` | Locked keyring | none | [`sensitive-settings.md`](../dev/sensitive-settings.md) |

### Accounts and passwords (`Kazo.Os.AppSdk.Administration`)

| API | Members | Capability | Document |
|---|---|---|---|
| `KazoUserAccounts` | `ListAsync`, `ChangePasswordAsync([context,] courant, nouveau)`, `ResetPasswordAsync([context,] login, nouveau)`, `RemovePasswordAsync([context,] login)`, `CreateAsync([context,] KazoNewUserAccount)`, `DeleteAsync([context,] login, removeHome)`, `ListPasskeysAsync([context])`, `AddPasskeyAsync([context,] nom)`, `RemovePasskeyAsync([context,] id)`, `ValidateNewPassword`, `ValidateNewAccount`, `MinimumPasswordLength` (8), `MaximumPasswordLength` (256), `MaximumDisplayNameLength` (128); types `KazoNewUserAccount`, `KazoPasskey`, `KazoUserAccount.PasskeyCount` | none to list, change one's own password and manage one's passkeys; `users.manage` (dangerous) and an administrator for another account, adding and deleting | [`system-administration.md`](system-administration.md), [`passkeys.md`](../dev/passkeys.md) |
| `KazoUserAccount` | `LoginName`, `DisplayName`, `Authority`, `IsEnabled`, `HasPassword`, `UpdatedAtUtc`, `IsCurrentUser`, `IsAdministrator` (never the hash) | none | [`sensitive-settings.md`](../dev/sensitive-settings.md) |
| `IAppUserAccountsGateway` | gateway behind `KazoUserAccounts` (`IAppExecutionContext.UserAccounts`, default member not supported) | none | [ADR 0120](../dev/adr/0120-passwd-and-administrator-password-reset.md) |

### Apps (`Kazo.Os.AppSdk.Apps`)

| API | Members | Capability | Document |
|---|---|---|---|
| `KazoApps` | `GetInstalledAsync`, `GetHandlersAsync(path)`, `LaunchAsync(appId, args)`, `OpenFileAsync(path)`, `EditFileAsync(path)`, `RevealFileAsync(path)`, `OpenUriAsync(uri, appId)`, `ShowSettingsAsync(appId)`, `SendIntentAsync(intentId, payloadJson, targetAppId, rememberChoice)`, `GetDefaultHandlersAsync()`, `SetDefaultHandlerAsync(target, appId, action)`, `ResetDefaultHandlerAsync(target, action)` (ADR 0121), `Changed`; `KazoFileAssociation`, `KazoFileAssociationLayer`, `KazoDefaultHandlerStatus`, `KazoAppInfo` (with `Capabilities`), `KazoOpenFileResult` (`FellBackToOpen`), `KazoIntentResult` (`KazoIntentStatus`), `KazoAppLaunchException` | reading and `ShowSettingsAsync`: none; defaults: `file-associations.read` (and `filesystem.user-home.write` to write); the rest: `apps.launch` | [`app-sdk.md`](app-sdk.md#app-catalog-adr-0056-2), [`app-intents.md`](app-intents.md), [`file-associations-api.md`](file-associations-api.md), [`user-file-associations.md`](../dev/user-file-associations.md) |
| `KazoAppIcons` | `LucideName(appId, category)`: an app's Lucide icon, fallback of the Start menu and shortcuts when the app declares no image; SDK 2.0 (ADR 0160, ADR 0232) | none | [`desktop.md`](../dev/desktop.md) |
| `KazoAppAssets` | `RelativeUrl(manifest, assetPath)`: the `_content/<assembly>/<path>` URL of a file an app declares; used by `KazoAppInfo.IconUrl` (ADR 0232) | none | [ADR 0232](../dev/adr/0232-illustrated-app-icons-everywhere.md) |
| `KazoBrowser` | `OpenAsync(url)`, `OpenDetachedAsync(url)` (detached window, ADR 0078); `AppId`, `DetachedArgument` (`--detached`) | `apps.launch` | [`detached-display-windows.md`](../dev/detached-display-windows.md), [`display-surface-api.md`](display-surface-api.md) |
| `KazoAppManagement` | `GetAllAsync`, `UninstallAsync`, `ReinstallAsync`; `KazoInstallScope`, `KazoAppInstallState` | `apps.manage` | [`app-installation.md`](../dev/app-installation.md) |
| `KazoAppPackages` | `GetSourcesAsync`, `GetInstalledAsync` (with `PreviousVersion`), `GetInfoAsync`, `InstallAsync`, `RemoveAsync`, `RollbackAsync(appId, scope)` and `RepairAsync(system)` (ADR 0214), sources, block list, catalogs, updates, `RestartToFinishAsync`, theme packs | `apps.manage` | [`app-installation.md`](../dev/app-installation.md#adding-without-a-restart-signing-and-transactions) |

### Processes and services

| API | Members | Capability | Document |
|---|---|---|---|
| `KazoProcess` (`.Diagnostics`) | `GetProcessesAsync`, `GetProcessByIdAsync`, `GetCurrentProcessAsync`, `RefreshAsync`, `GetEnvironmentVariablesAsync`, `GetChildren`, `KillAsync` (`SIGKILL`), `TerminateAsync` (`SIGTERM`, 5 s), `CloseMainWindowAsync`; state, CPU, memory (`WorkingSet64` = `VmRSS`, `PrivateMemorySize64` = `RssAnon`, ADR 0068), I/O, arguments; `UserProcessorTime`, `PrivilegedProcessorTime`, `TotalProcessorTime`, `Responding` (ADR 0067, [`process-resources-api.md`](process-resources-api.md), [`cpu-accounting.md`](../dev/cpu-accounting.md)); `GetResourceLimitsAsync` → `KazoResourceLimits` (`Cpu`, `Rss`, `NoFile`, `NProc`, `Enforcement`: `KazoResourceLimitEnforcement`, `IsCpuMeasured`, `IsMemoryMeasured`), `SetResourceLimitAsync(ResourceLimitKind, soft, hard)` (`getrlimit`/`prlimit`, ADR 0125, SDK 1.2, [`process-limits-api.md`](process-limits-api.md), [`process-limits.md`](../dev/process-limits.md)); exit reasons `ProcessExitReason.CpuLimitExceeded`, `MemoryLimitExceeded` (SDK 1.2, ADR 0125) and `OutOfMemory` for a process killed by the OOM killer (SDK 1.2, [`oom-killer.md`](../dev/oom-killer.md)) | `filesystem.system.read` for `/proc`, `process.list`, `process.manage` | [`task-manager.md`](../dev/task-manager.md) |
| `KazoProcess.Current` → `KazoCurrentProcess`; `KazoProcess.CheckpointAsync` (`.Diagnostics`) | `Id`, `StopToken`, `IsStopRequested`, `StopRequest`, `StopRequested`; checkpoint: throws if a stop is requested, yields beyond 8 ms, otherwise allocation-free | none | [`app-sdk.md`](app-sdk.md#clean-shutdown-and-checkpoints-adr-0069) |
| `KazoStopRequestedEventArgs`, `KazoProcessKilledException` (`.Diagnostics`) | `Reason`, `RequestedAt`, `Deadline`, `GracePeriod`, `Defer(Task)`; exception of the gateways of a killed process, with its `Reason` (`Killed` by default, SDK 1.2, ADR 0140); it derives from `OperationCanceledException` | none | [`app-sdk.md`](app-sdk.md#clean-shutdown-and-checkpoints-adr-0069) |
| `KazoProcess.Nice`, `BasePriority`, `ParseStatNice` (`.Diagnostics`); `IAppProcessGateway.GetNice` / `SetNice`, `ProcessNice` (`Simulation.Abstractions`); `IShellSession.RunNicedAsync` | `nice` priority (-20..19) of a process, read from `/proc/<pid>/stat`; changing it (raising: one's own process; lowering or another user: root); launching a command with an adjustment (SDK 1.2, ADR 0127 §1) | `process.manage` outside one's own process and its children (ADR 0140), `shell.control` | [`app-responsiveness-api.md`](app-responsiveness-api.md), [`process-priority-and-unresponsive-windows.md`](../dev/process-priority-and-unresponsive-windows.md) |
| `ResourceLimitKind`, `ResourceLimit`, `ProcessResourceLimits`, `ResourceLimitChangeResult`, `ProcessLimitExceededException`, `IAppProcessGateway.SetResourceLimitAsync` (`Simulation.Abstractions`, SDK 1.2) | Limits contract (ADR 0125): `ResourceLimit.Unlimited` equals `long.MaxValue`; `ProcessLimitExceededException` (`EAGAIN`, "Resource temporarily unavailable"): refusal by the OS of a launch beyond `nproc`, seen by the app as a launch failure | `process.manage` to change another process's limit | [`process-limits-api.md`](process-limits-api.md), [`process-limits.md`](../dev/process-limits.md) |
| `IAppProcessGateway.KillAsync(pid, reason)` (`Simulation.Abstractions`) | Kill a process while waiting for the OS acknowledgement; used by `KazoProcess.KillAsync`, including from a Web Worker (default method that falls back to `Kill`) | `process.manage` | [`worker-processes.md`](../dev/worker-processes.md) |
| `KazoServiceController` (`.ServiceProcess`) | `GetServicesAsync`, `RefreshAsync`, `RefreshStatusAsync`, `StartAsync`, `StopAsync`, `ResetFailedAsync`, `SetStartTypeAsync`, `Status`, `StartType`, `ActiveState`, `Result`, `Health`, `StatusText`, `ActiveStateChangedAt`, `ExitCode`; `KazoServiceStates.ToSystemdName` (ADR 0097) | `services.manage` on other apps | [`task-manager.md`](../dev/task-manager.md) |

### Background jobs (`Kazo.Os.AppSdk.Threading`, SDK 1.2)

| API | Members | Capability | Document |
|---|---|---|---|
| `KazoBackground` | `RunAsync<TInput, TResult>(job, input, progress, options, cancellationToken)` for a static method (`Func<TInput, KazoBackgroundContext, Task<TResult>>` or `TResult`), `RunAsync<TInput>` without a result (`Func<…, Task>` or `Action<…>`), `RunAsync<TJob, TInput, TResult>(input, …)` for a job class | none | [`background-jobs.md`](background-jobs.md) |
| `KazoBackgroundJob<TInput, TResult>` | Job class: `RunAsync(input, context)`; created by its parameterless constructor in the job's process | none | [`background-jobs.md`](background-jobs.md) |
| `KazoBackgroundContext` | `ProcessId`, `CancellationToken`, `Report(fraction, message)` (at most 10 per second, the last one wins), `CheckpointAsync()` | none | [`background-jobs.md`](background-jobs.md) |
| `KazoBackgroundProgress`, `KazoBackgroundOptions` | `Fraction`, `Message`, `ToString()` (`42% message`); `Name` (displayed in `ps`), `Nice` | none | [`background-jobs.md`](background-jobs.md), [`app-responsiveness-api.md`](app-responsiveness-api.md) |
| `KazoBackgroundJobException` | Job exception: `OriginalTypeName`, `Message`, `OriginalStackTrace` | none | [`background-jobs.md`](background-jobs.md) |
| `IAppBackgroundGateway`, `KazoBackgroundJobRequest`, `KazoBackgroundJobTarget` | Contract between the SDK and the OS: named target (assembly, type, method, input type), JSON input, name, `nice` | none | [`background-jobs.md`](background-jobs.md) |
| Analyzer `KOSBG0001` to `KOSBG0004` | Shipped in the `Kazo.Os.AppSdk` package: static method group, types declared by `KazoWireContext`, the app's assembly, no generics | none | [`background-jobs.md`](background-jobs.md#rules-checked-at-build-time) |

### Network (`Kazo.Os.AppSdk.Net`)

| API | Members | Document |
|---|---|---|
| `KazoDns` | `GetHostAddressesAsync`, `GetHostEntryAsync` | [`sdk-network.md`](sdk-network.md) |
| `KazoPing` | `SendAsync` → `KazoPingReply(Address, Status, RoundtripTime, Ttl)` | [`sdk-network.md`](sdk-network.md) |
| `KazoHttpClient` | `GetAsync`, `GetStringAsync`, `GetByteArrayAsync`, `GetFromJsonAsync`, `PostAsync` (form or content), `PostAsJsonAsync`, `PutAsync`, `PatchAsync`, `DeleteAsync`, `SendAsync`, `DefaultRequestHeaders`, `Cookies` (`KazoCookieContainer`: `Add`, `Remove`, `GetCookies`, `GetAllCookies`, `Clear`); `KazoHttpResponse` (`ContentType`, `MediaType`, `Content`, `ContentBytes`, `GetHeader`, `ReadFromJson`), `KazoHttpRequestException` | [`sdk-network.md`](sdk-network.md), [`http-bodies.md`](http-bodies.md) |
| `KazoHttpContent` | `KazoStringContent`, `KazoByteArrayContent`, `KazoFormUrlEncodedContent`, `KazoJsonContent`; `ContentType`, `MediaType`, `CharSet`, `Length`, `ReadAsString`, `ReadAsByteArray` | [`http-bodies.md`](http-bodies.md) |
| `KazoTcpClient` | `ProbeAsync` (ports), `DetectOperatingSystemAsync`, `ConnectAsync` → `KazoNetworkStream` | [`sdk-network.md`](sdk-network.md) |
| `KazoNetworkStream` | `ReadAsync`, `WriteAsync`, `RemoteEndPoint` (relayed TCP stream) | [`server-network-relay.md`](../dev/server-network-relay.md) |
| `KazoUdpClient` | `ConnectAsync`, `SendAsync`, `ReceiveAsync` → `KazoUdpReceiveResult` | [`server-network-relay.md`](../dev/server-network-relay.md) |
| `KazoHttpListener` | `Prefixes`, `StartAsync`, `GetContextAsync`, `StopAsync`, `Close`; `KazoHttpListenerContext` (`Request`, `Response`), `KazoHttpListenerRequest` (`HttpMethod`, `Url`, `Headers`, `QueryString`, `Cookies`, `Body`, `ReadForm`, `RemoteEndPoint`), `KazoHttpListenerResponse` (`StatusCode`, `ContentType`, `Headers`, `OutputStream`, `RedirectLocation`, `Close`) | [`listening-ports-and-dns.md`](listening-ports-and-dns.md) |
| `KazoIPGlobalProperties` | `GetActiveTcpListenersAsync` → `KazoTcpListenerInfo`, `GetActiveTcpListenerEndPointsAsync` | [`listening-ports-and-dns.md`](listening-ports-and-dns.md) |
| `KazoWebSite`, `KazoWebSiteFile`, `KazoWebSiteDeclaration`, `KazoWebSiteScope` (`.Net.Hosting`) | `.site` files of static sites: `SystemSitesDirectory`, `GetUserSitesDirectory`, `Parse`, `Validate`, `Serialize`, `TryParseBinding`, `ReadAllAsync` | [`static-sites.md`](../dev/static-sites.md), [`static-site-files.md`](static-site-files.md) |
| `WebServerAppBase` (`Kazo.Os.AppSdk`) | `Urls`, `ProcessRequestAsync`, `OnListeningAsync`: base of a web app (`AppKind.Web`) | [`listening-ports-and-dns.md`](listening-ports-and-dns.md) |

Capabilities: `network.simulated.*`; falling back to the real network is decided
by the OS and requires `network.real.access`; listening outside one's `Urls`
requires `network.listen` (and `network.listen.privileged` below 1024).

### Clipboard (`Kazo.Os.AppSdk.Clipboard`)

`KazoClipboard`: `GetTextAsync`, `SetTextAsync`, `ContainsTextAsync`,
`GetFileDropListAsync`, `SetFileDropListAsync` (copy or cut files),
`ContainsFileDropListAsync`, `ClearAsync`, `Changed`. Capabilities
`clipboard.read` and `clipboard.write`. Text is shared with the real
machine according to `/etc/kazoos/clipboard.config`. See [`file-explorer.md`](../dev/file-explorer.md)
and [`clipboard.md`](../dev/clipboard.md), [`clipboard-api.md`](clipboard-api.md).

### Accessibility (`Kazo.Os.AppSdk.Accessibility`)

`KazoAccessibility`: `GetPreferencesAsync()` → `KazoAccessibilityPreferences(HighContrast,
TextScale, ReducedMotion)`, already resolved (`auto` follows the host); `PreferencesChanged`. No
capability. `KazoAccessibilityPreferences.Scale(pixels)` enlarges a size in pixels;
`TextScales` lists `1.0`, `1.25`, `1.5`, `2.0`. The shell already applies the preferences to everything
drawn in HTML with the tokens; an app that draws by itself (canvas, terminal, editor)
reads them here. See [`accessibility.md`](../dev/accessibility.md).

### What's new (`Kazo.Os.AppSdk.WhatsNew`)

| API | Members | Capability |
|---|---|---|
| `KazoWhatsNew` | `IsSupported`, `IsSupportedFor(context)`, `GetAsync` → `KazoChangeList(Groups, NewCount)` (`KazoChangeGroup(Version, Since, Date, Entries)`, `KazoChangeEntry(Id, Category, Title, Description, PullRequestUrl, Links, IsNew)`, `KazoChangeCategory`), `MarkSeenAsync(ids)`, `WatchAsync`: the OS change list in the app's language, and what the session user has already seen (ADR 0186, [`change-list.md`](../dev/change-list.md)). With no served list, the list is empty. | none |

### Embeddable components (`Kazo.Os.AppSdk.Components`)

| Type | Role | Document |
|---|---|---|
| `KazoComponentContracts` | Contract names: `Terminal` (`kazoos.terminal`); reserved to the OS (ADR 0148; ADR 0156 opens the first two to a third-party app chosen by an administrator): `FileView` (`kazoos.file-view`), `FileDialogs` (`kazoos.file-dialogs`), `ContextMenu` (`kazoos.context-menu`). | [`embedded-components.md`](embedded-components.md) |
| `IKazoTerminal`, `KazoTerminalOptions`, `KazoTerminalCommandResult`, `KazoTerminalOutputEventArgs` | Drive an embedded terminal: `RunCommandAsync`, `SetWorkingDirectoryAsync`, `SendInputAsync`, `ReadOutput`, `ClearAsync`, `FocusAsync`, `WorkingDirectory`, `Title`, `IsRunning`, `IsBusy`, `OutputReceived`, `CommandCompleted`, `StateChanged`, `Exited`. | [`embedded-components.md`](embedded-components.md) |

### Shell (`Kazo.Os.AppSdk.Shell` and `Kazo.Os.AppSdk`)

| API | Role | Capability | Document |
|---|---|---|---|
| `KazoShell.StartAsync(options)` → `IKazoShell` | Start an `hsh`: `ExecuteAsync`, `CompleteAsync`, `WorkingDirectory`, `ExitStatus`, `Prompt`, `HasExited`, history, `GetInstalledCommands`, `NeedsMoreInput` (ADR 0064, `>` prompt) | `shell.host` | [`shell-host.md`](shell-host.md) |
| `KazoShellOptions`, `KazoShellExecution`, `KazoShellCompletion`, `KazoCredentialPrompt` | Options, stream of an execution, completion, password entry | — | [`shell-host.md`](shell-host.md), [`terminal-console.md`](../dev/terminal-console.md) |
| `IShellSession` (`TerminalExecutionContext.Shell`) | For a command: `WorkingDirectory`, `Environment`, `History`, `ChangeDirectoryAsync`, `SetVariable`, `UnsetVariable`, `GetCommands` (in the shell's language; `GetCommands(culture)` in another, ADR 0104), `Exit`; ADR 0064: `GetVariable`, `AssignVariable`, `RunAsync`, `Jobs` (`ShellJobInfo`), `KillJob` | `shell.control` | [`shell-commands.md`](../dev/shell-commands.md) |
| `IFullScreenTerminalSession` (`TerminalExecutionContext.FullScreen`) | Alternate screen: `Viewport`, `EnterAlternateScreenAsync`, `RenderAsync`, `ReadKeyAsync`, `LeaveAlternateScreenAsync` | — | [`terminal-emulator.md`](../dev/terminal-emulator.md), [`terminal-api.md`](terminal-api.md) |
| `ShellErrorText` | Short message for a file error, for a command | — | [`app-sdk.md`](app-sdk.md) |

### Administration (`Kazo.Os.AppSdk.Administration`)

| API | Members | Capability |
|---|---|---|
| `KazoPermissionAdministration` | `GetPolicyAsync`, `SetPolicyAsync`, `GetOverviewAsync`, `SetMachineDecisionAsync`, `RemoveOwnDenialAsync` | `permissions.manage` |
| `KazoComponentProviders` | `GetOverviewAsync` (`KazoComponentContractInfo`, `KazoComponentProviderInfo`), `SetProviderAsync(contracts, appId)`: app that provides the file control and its dialogs for the whole machine, `null` = explorer ([`embedded-components.md`](embedded-components.md), ADR 0156) | `apps.manage` + administrator (approval window for a non-system app) |
| `KazoFileMenuEntries` | `GetEntriesAsync` (`KazoFileMenuEntryInfo`), `SetHiddenAsync(appId, name, hidden)`: entries that apps add to the file menus, hidden for the session's user in `~/.config/kazoos/context-menus.config` ([`context-menu.md`](context-menu.md), ADR 0159) | `apps.manage` |
| `KazoServerConnection` | `GetStateAsync`, `ConnectAsync`, `CreateAccountAsync`, `PairAsync`, `TestAsync` (`KazoServerProbe`, ADR 0191), `DisconnectAsync`, `SyncAsync`, `GetConflictsAsync`, `GetRelayPolicyAsync`, `ShowConflictsAsync`, `GetSyncStatusAsync` (`KazoSyncStatus`), `SetSyncPausedAsync`, `SyncNowAsync` ([`synchronization.md`](../dev/synchronization.md)) | `server.connection.manage` |
| `KazoSystem` | `RebootAsync`, `FactoryResetAsync` | `system.power` + administrator |
| `KazoStorageDiagnostics` | `GetSnapshotAsync`, `GetWebSiteDatabasesAsync`, `DeleteWebSiteDatabaseAsync` (the sites' `kazoweb.*` databases, ADR 0088 §4) | `system.storage.inspect` |
| `KazoScreens` | `IsSupported`, `IsSupportedFor(context)`, `GetState` (`KazoScreensState`, `KazoScreenInfo`), `WatchAsync`, `OpenScreenAsync` → `KazoScreenOpenStatus`, `RestoreScreensAsync`, `CloseScreenAsync(number)`, `IdentifyAsync`, `ToggleFullScreenAsync`, `RequestPermissionAsync` → `KazoScreenPermission`; open, full screen and permission only during a click (ADR 0184, [`screens-settings.md`](../dev/screens-settings.md)) | `screens.manage` |
| `KazoSystemBackup` | `IsSupported`, `IsSupportedFor(context)`, `CreateAsync` → `KazoBackupInfo(FileName, SizeBytes, RecordCount, CreatedAtUtc)`, `RestoreAsync(KazoBackupRestoreMode.Merge \| Replace)` → `KazoBackupInfo?` (`null`: picker closed); `KazoBackupInvalidException` for a rejected archive (ADR 0099 §1, [`system-backup-api.md`](system-backup-api.md); implementation: [`system-backup.md`](../dev/system-backup.md)) | `system.backup` + administrator |

Details: [`system-administration.md`](system-administration.md),
[`admin-authentication-agent.md`](../dev/admin-authentication-agent.md).

## Entry points

| Class | Project | To override | Document |
|---|---|---|---|
| `WindowAppBase` | `AppSdk.Blazor` | `OnAppInitialized[Async]`, `OnAppParametersSet[Async]`, `OnAppAfterRender[Async]`, `ShouldRenderApp`, `OnPermissionErrorRaised`, `OnAppStopRequestedAsync` (ADR 0069: the window closes after the returned task), window hooks `OnWindowActivated[Async]`, `OnWindowDeactivated[Async]`, `OnWindowStateChanged[Async]`, `OnWindowResized[Async]`, state `IsWindowActive`, `WindowState`, `WindowSize`, `TrackDisplaySurface` (ADR 0095, [`window-model.md`](window-model.md)), title `WindowTitle`, `SetWindowTitleAsync(title)`, `FormatWindowTitle(document, modified)` (SDK 1.1, ADR 0117/0123, [`window-model.md`](window-model.md) §4), `OnIntentReceivedAsync(AppIntentDelivery)` (ADR 0096 §5); provides `AppContext`, `Manifest`, `LaunchArguments`, `LaunchIntent`, `LaunchFile`, `SendIntentAsync` (shows the chooser if several apps respond), `ChooseAppAsync`, `OpenFileAsync`, `SaveFileAsync`, `SelectFolderAsync`, `MessageBox`, `TextInput` | [guide §3](sdk-developer-guide.md#3-a-windowed-app) |
| `KazoComponentBase` | `AppSdk.Blazor` | Child component of a window that calls `Kazo*` | [`kazo-file-api.md`](kazo-file-api.md) |
| `TerminalAppBase` | `AppSdk` | `ExecuteAsync(TerminalExecutionContext, CancellationToken)` → exit code; the context gives `Arguments`, `StandardInput/Output/Error`, `WorkingDirectory`, `Environment`, `App`, `Shell`, `FullScreen`, and `ReadSecretLineAsync(prompt)` which reads a line with echo off like `getpass(3)` when the input is a terminal (`ITerminalInputMode.EchoEnabled`, ADR 0120) | [guide §4](sdk-developer-guide.md#4-a-shell-command) |
| `WebAppDefinition` | `AppSdk.Web` | `partial` `[AppManifest]` class of a site: `Urls`, `Configure(KazoWebApplicationBuilder)` (services, middlewares, `MapControllers`, `MapGet`...); the build generates the `EntryPoint` (`<Name>WebHost : KazoWebHost`) and the controllers' route table (`KazoController`, `KazoApiController`) | [`asp-kazo-mvc.md`](asp-kazo-mvc.md) |
| `ServiceAppBase` | `AppSdk` | `OnStartAsync(context, token)` (the service is `activating` until it returns, then `active`, ADR 0107), `RunCoreAsync(context, sessionCancellationToken)`, `ReportHealth(KazoServiceHealth, status)` (health and text of `systemctl status`, ADR 0097 §2), `OnStoppingAsync(ServiceStopReason, token)` (called once, as soon as `StopRequested`; `ServiceStopReason.Terminated` for `kill`, "End task", `systemctl stop`), `OnIntentReceivedAsync(AppIntentDelivery, token)` (ADR 0096 §5) | [guide §5](sdk-developer-guide.md#5-a-service) |

Interfaces that a windowed component can implement:

| Interface | Role |
|---|---|
| `IWindowCloseGuard` | `ConfirmCloseAsync`: allow or refuse closing (unsaved changes). |
| `IAppBackHandler` | `CanNavigateBack`, `CanNavigateBackChanged`, `NavigateBackAsync` → `AppBackResult`. Receives the mobile ◁ button, the title-bar Back button (with `SupportsBack => true`), and with nothing declared the mouse Back button, Alt+← and the phone's Back ([ADR 0220](../dev/adr/0220-mouse-back-forward-buttons.md)). `AppBackRequest.Source`: `SystemNavigationBar`, `DesktopChromeButton`, `KeyboardShortcut`, `MouseButton`, `SystemBack`. |
| `IAppForwardHandler` | `CanNavigateForward`, `CanNavigateForwardChanged`, `NavigateForwardAsync(AppForwardRequest)` → `AppBackResult`: mouse Forward button, Alt+→, Browser Forward key ([ADR 0220](../dev/adr/0220-mouse-back-forward-buttons.md)). |

## Websites (ASP.Kazo)

To learn: the [ASP.Kazo developer guide](asp-kazo-developer-guide.md),
which follows the example `Apps/Samples/Kazo.Os.Samples.Intranet`.

### Startup (`Kazo.Os.AppSdk.Web.Hosting`)

| Type | Members | Document |
|---|---|---|
| `WebAppDefinition` | `Urls`, `BlazorRoot`, `Capabilities` (never `filesystem.*`, `KOSAPP0050`), `Configure(builder)`; `Kind` `Web`, `Category` `internet`, `LaunchVisibility` `Hidden` | [`asp-kazo-mvc.md`](asp-kazo-mvc.md) |
| `KazoWebApplicationBuilder` | `Services`, `Json` (`SerializerOptions`, camelCase), `Mvc` (`Filters`), `Antiforgery`, `Logging` (`EnableDebugLog(level)`, `LogRequests`), `Build()`; `ILogger<T>` injectable | [`asp-kazo-mvc.md`](asp-kazo-mvc.md), [`asp-kazo-site-data.md`](asp-kazo-site-data.md#debug-log) |
| `KazoWebApplication` | `Use`, `UseMiddleware<T>`, `UseExceptionPage`, `UseStatusCodePages`, `UseCookieSession`, `UseStaticFiles(configure)`, `Map`, `MapMethods`, `MapGet`/`MapPost`/`MapPut`/`MapPatch`/`MapDelete`, `MapFallback`, `MapControllers`, `MapControllerRoute`, `MapDefaultControllerRoute`, `ProcessAsync(KazoWebRequestData)`, `UnhandledException` | [`asp-kazo-mvc.md`](asp-kazo-mvc.md) |
| `KazoWebHost` | Generated entry class (`<Definition>WebHost`); `CreateApplication(definition, routes, urls, views)` builds the site without the OS (tests) | [guide §9](asp-kazo-developer-guide.md#9-testing-a-site) |
| `KazoStaticFileOptions` | `Assembly`, `ResourcePrefix`, `RequestPath`, `DefaultFileNames`, `CacheControl` of the embedded `wwwroot` | [`asp-kazo-razor-views.md`](asp-kazo-razor-views.md) |
| `KazoBlazorSiteEndpoints` | Host page, `blazor.kazo.js` and `blazor.boot.json` of a site with a `BlazorRoot`; `IsHostPage(html)` | [`blazor-sites.md`](blazor-sites.md) |

### Request and response (`.Http`, `.Sessions`)

| Type | Members |
|---|---|
| `KazoHttpContext` | `Request`, `Response`, `RequestServices` (request scope), `Session`, `HasSession`, `Items`, `Endpoint`, `RequestAborted`, `TraceIdentifier` |
| `KazoHttpRequest` | `Method`, `Path`, `PathAndQuery`, `Query`, `Form`, `Headers`, `Cookies`, `ContentType`, `RouteValues`, `ReadBodyAsString()` |
| `KazoHttpResponse` | `StatusCode`, `Headers`, `ContentType`, `Cookies`, `WriteAsync` |
| `KazoWebRequestData`, `KazoWebResponseData` | Transport-independent exchange (`Create(method, url, ...)`, `ReadAsString()`) |
| `KazoSession` | `GetString`/`SetString`, `GetInt32`/`SetInt32`, `TryGetValue`, `Set`, `Remove`, `Clear`, `Keys`, `Id`, `IsNew`; `.KazoSession` cookie, in the site's memory |

### Controllers (`.Mvc`)

| Type | Role |
|---|---|
| `KazoControllerBase` | `HttpContext`, `Request`, `Response`, `RouteValues`, `ModelState`, `Url`; results `Ok`, `NoContent`, `StatusCode`, `Content`, `File`, `Redirect`, `RedirectPermanent`, `LocalRedirect`, `RedirectToAction`, `Created`, `CreatedAtAction`, `NotFound`, `BadRequest`, `Unauthorized`, `Forbid`, `Conflict`, `Problem`, `ValidationProblem`; `TryValidateModel(model, prefix)` (ADR 0087 §3) |
| `KazoController` | Pages: `View`, `PartialView`, `View<TView>`, `ViewData`, `Json`, `OnActionExecuting`/`OnActionExecuted` |
| `KazoApiController` | API: JSON body by default, automatic `400`, RFC 9457 problems; attribute route required (`KOSWEB0007`) |
| Attributes | `[Route]`, `[HttpGet]`/`[HttpPost]`/`[HttpPut]`/`[HttpPatch]`/`[HttpDelete]` (`Name`, `Order`), `[ActionName]`, `[NonAction]`, `[NonController]`, `[FromRoute]`, `[FromQuery]`, `[FromForm]`, `[FromHeader]`, `[FromBody]`, `[FromServices]` |
| Filters | `IKazoActionFilter`, `IKazoAsyncActionFilter`, `KazoActionFilterAttribute`, `KazoMvcOptions.Filters` |
| `KazoResults` | Results of minimal routes: `Ok`, `Text`, `Json` (with `JsonTypeInfo`), `StatusCode`, `NoContent`, `NotFound`, `BadRequest`, `Created`, `Redirect`, `File`, `Problem` |
| `IKazoUrlHelper` | `Action`, `RouteUrl`, `Content("~/...")`, `IsLocalUrl` |

### Views and anti-forgery (`.Views`, `.Antiforgery`)

| Type | Role | Document |
|---|---|---|
| `KazoView`, `KazoView<TModel>`, `KazoLayout` | Bases of `.razor` views: `Model`, `ViewData` (`Title`), `ViewContext`, `HttpContext`, `Request`, `ModelState`, `Url`, `Layout`; `Body` for a layout | [`asp-kazo-razor-views.md`](asp-kazo-razor-views.md) |
| `KazoLink`, `KazoForm`, `KazoAntiforgeryToken`, `KazoValidationMessage`, `KazoValidationSummary`, `KazoSection`, `KazoRenderSection`, `KazoPartial`, `KazoRoute.Values` | Helpers of `.razor` views | [`asp-kazo-razor-views.md`](asp-kazo-razor-views.md) |
| `KazoRazorPage<TModel>` | Base of `.cshtml` views: `Model`, `ViewData`, `Context`, `Layout`, `RenderBody`, `RenderSection(Async)`, `IsSectionDefined`; `Html` (`ActionLink`, `BeginForm`, `TextBoxFor`, `LabelFor`, `ValidationMessageFor`, `ValidationSummary`, `PartialAsync`, `Raw`...), `Json.Serialize`, `Component.InvokeAsync<T>`, `Url` | [`asp-kazo-cshtml-views.md`](asp-kazo-cshtml-views.md) |
| `[ValidateAntiForgeryToken]`, `[IgnoreAntiforgeryToken]`, `KazoAntiforgery` | Form token (`__RequestVerificationToken` or `RequestVerificationToken` header) and `.KazoAntiforgery` cookie | [`asp-kazo-razor-views.md`](asp-kazo-razor-views.md) |

A view is rendered as static HTML in the browser's iframe: no scoped CSS
and no Blazor event; CSS and JavaScript come from the `wwwroot`.

### Data and log (`.Data`, `.Logging`)

| Type | Role |
|---|---|
| `AddKazoWebDb(services, schema)` | Registers `KazoWebDb`, the site's `kazoweb.<appId>` database, with its versions; in memory (`kazoweb.in-memory`) outside the OS |
| `KazoWebDbSchemaBuilder`, `KazoWebDbVersionBuilder` | `Version(n, v => ...)`; `CreateStore(name, keyPath, autoIncrement)`, `DeleteStore`, `CreateIndex(store, name, keyPath, unique, multiEntry)`, `DeleteIndex` |
| `KazoWebDb` | `Name`, `Version`, `Store<T>(name)`, `BeginTransaction(stores)`, `OpenAsync` |
| `KazoWebDbStore<T>`, `KazoWebDbIndex<T>` | `GetAsync`, `GetAllAsync`, `GetAllKeysAsync`, `CountAsync`; store: `AddAsync`, `PutAsync`, `DeleteAsync`, `ClearAsync`, `Index(name)` |
| `KazoWebDbTransaction`, `KazoWebDbTransactionStore<T>` | Batch of writes (`Add`, `Put`, `Delete`, `Clear`) sent by `CommitAsync`, all or nothing |
| `KazoWebDbKey`, `KazoWebDbKeyRange` | Keys (number, string, `Array(...)`) and ranges (`Only`, `LowerBound`, `UpperBound`, `Bound`) |
| `KazoWebDbException` (`Kazo.Os.AppSdk.WebData`) | `Name` of an IndexedDB `DOMException`: `ConstraintError`, `NotFoundError`, `DataError`, `VersionError`… |
| `KazoWebDbInMemoryBackend` (`Kazo.Os.AppSdk.WebData`) | In-memory gateway for tests |
| `KazoWebLoggingOptions`, `IKazoWebDebugLogSink`, `KazoWebDebugLogMemorySink` | Debug log `/var/log/kazoweb/<appId>.log`; test sink |

Guide: [`asp-kazo-site-data.md`](asp-kazo-site-data.md).

## Blazor components and services

### Dialogs (`Kazo.Os.AppSdk.Blazor`)

| Type | Fields |
|---|---|
| `OpenFileDialogRequest` → `OpenFileDialogResult` | `Title`, `InitialDirectory`, `Filters` (`FileDialogFilter(DisplayName, Extensions, MediaTypes)`), `AllowMultiple`, `RequestedAccess` (`Read`, `ReadWrite`); result `Status`, `Resources` |
| `SaveFileDialogRequest` → `SaveFileDialogResult` | `Title`, `InitialDirectory`, `SuggestedFileName`, `DefaultExtension`, `Filters`, `ConfirmOverwrite`; result `Status`, `Resource` |
| `SelectFolderDialogRequest` → `SelectFolderDialogResult` | `Title`, `InitialDirectory`, `AllowCreateFolder` ("New folder" button in the dialog's toolbar, ADR 0175) |
| `MessageBoxDialogRequest` → `MessageBoxDialogResult` | `Title`, `Content`, `DialogType` (`MessageBoxType`), `Icon` (`MessageBoxIcon`: `None`, `Information`, `Warning`, `Error`, `Question`; next SDK version); result `MessageBoxResult` |
| `TextInputDialogRequest` → `TextInputDialogResult` | `Title`, `Content`, `DefaultValue`, `Placeholder`; result `Status` (`TextInputStatus`) and `Value` |
| `AppChooserDialogRequest` → `AppChooserDialogResult` | "Open with" chooser (ADR 0096): `Title`, `Content`, `Candidates` (`AppChooserOption(AppId, Name, Description)`), `OfferAlwaysUse`; result `Status` (`AppChooserStatus`), `AppId`, `AlwaysUse` |
| `OpenWithDialogRequest`, `FilePropertiesDialogRequest`, `FileConflictDialogRequest` (`.Files`) | Dialogs provided by the explorer (`kazoos.file-dialogs`, ADR 0155 §5): "Open with" (`CandidateAppIds`; result `Status`, `AppId`, `AlwaysUse`), properties (result `Closed`), name conflict (`OfferApplyToAll`; result `Replace`/`KeepBoth`/`Skip` and `ApplyToAll`). Without a provider: `Unavailable` (or `Skip`). |
| `IDialogService`, `IFileDialogService` | Services injected into `WindowAppBase` (`Dialogs`, `FileDialogs`); plus `CredentialPromptAsync`, `ChooseAppAsync`, `OpenWithAsync`, `ShowFilePropertiesAsync` and `ResolveFileConflictAsync`. The Open / Save / Choose a folder dialogs show the OS file control (`kazoos.file-view`) with the app's rights, or the platform's list without a provider (ADR 0157) |

`FileDialogStatus.Selected` indicates a choice; any other status is a
cancellation.

### Other components of `Kazo.Os.AppSdk.Blazor`

| Type | Role | Document |
|---|---|---|
| `KazoLineChart` (`.Charts`) | SVG chart: `Series` (`KazoChartSeries(Name, Values)`), `Title`, `Capacity`, `Maximum`, `MinimumScale`, `ValueFormatter`, `Filled`, `ShowLegend`, `TimeSpanLabel`, `Size`, `Class`; colors `--kos-chart-series-0..7` | [`task-manager.md`](../dev/task-manager.md) |
| `SettingOriginBadge` (`.Settings`) | Origin badge of a setting (default, system, user, locked) | [`settings-files.md`](settings-files.md) |
| `BrowserFileDownloader` (`.Interop`) | `DownloadFileFromStreamAsync(fileName, stream)`: download to the real disk | [`file-explorer.md`](../dev/file-explorer.md) |
| `BrowserFilePicker` (`.Interop`) | `PickAsync(accept)` → `BrowserPickedFile(Name, Content)` or `null`: open a file from the real disk; only right after a click or a key press (otherwise `InvalidOperationException`) | [`system-backup.md`](../dev/system-backup.md) |
| `IDisplaySurfaceService`, `IDisplaySurface`, `DisplaySurfaceContent` (`.Display`) | Windows detached from the host browser (ADR 0078): `OpenAsync(DisplaySurfaceOptions)` → `DisplaySurfaceOpenResult` (`Opened`, `Blocked`, `Unavailable`), `Surfaces`, `SurfacesChanged`, `CloseAllAsync`; surface `Id`, `Title`, `Width`, `Height`, `HasFocus`, `State`, `Changed`, `SetTitleAsync`, `FocusAsync`, `CloseAsync`; `<DisplaySurfaceContent Surface="…">` draws its content there (`Surface` optional: without an open surface, the content stays in place) | [`detached-display-windows.md`](../dev/detached-display-windows.md), [`display-surface-api.md`](display-surface-api.md) |
| `IAppWindowGateway` | An app's window as seen by the SDK (SDK 1.1, ADR 0123): `Title`, `DefaultTitle`, `SetTitleAsync`. Implemented by the platform and given to `WindowAppBase` by cascading value; an app goes through `SetWindowTitleAsync` | [`window-model.md`](window-model.md) |
| `KazoContextMenu`, `KazoMenuItem`, `KazoMenuGroup`, `KazoMenus` (`Menus`), `KazoMenuBuilder`, `KazoMenuDefinition`, `KazoMenuOpeningEventArgs`, `IKazoContextMenu`, `IKazoMenuHost` (`.ContextMenus`) | The OS context menu (ADR 0150, ADR 0154): declared in markup or in C# (`Menus.Define`), attached to any element (`@attributes="@(Menus.Attach(nom, objet))"`), opened from code (`Menus.OpenAsync`); groups, three levels, `Opening` for the attached object; drawn by the platform. `Menus` exists on `WindowAppBase` and `KazoComponentBase`. Icons: `KazoIcons.Vector`/`FromImage` (`Kazo.Os.AppSdk.Icons`) | [`context-menu.md`](context-menu.md) |
| `RegisterFileContextMenuAttribute`, `RegisterFileContextMenuContext` (`.Files`), `MenuSelection`, `KazoFileMenuIntent` (`Kazo.Os.AppSdk.Files`), `KazoFileMenuContext`, `KazoMenuDefinition.FileMenu` | An app's entries in the file menus (ADR 0151, ADR 0159): static `[RegisterFileContextMenu]` method of the `[AppManifest]` class that calls `Register(title, name, target, intent, icon, scope, types, selection, first, after, afterGroup, parent)`, run at build time; capability `shell.context-menu`; the target reads the intent's paths through `KazoFileMenuIntent.ReadPaths`. A file control provider sets `FileMenu` so that the OS adds these entries | [`context-menu.md`](context-menu.md#app-entries-in-the-file-menus-work-item-4) |
| `KazoEmbeddedComponent<TController>` (`.Components`) | Displays another app's component for a contract: `Contract`, `Options`, `Parameters`, `ControllerChanged`, `Unavailable`; `KazoEmbeddableComponent` is the base of the provided component (`Options`, `ControllerReady`, `HostContext`, `UnmatchedParameters`); `KazoContractComponent<TController>` is the base of typed components (`AddProviderParameters`, `Controller`, `DefaultUnavailable`, ADR 0153, ADR 0154) | [`embedded-components.md`](embedded-components.md) |

### Blazor sites (`Kazo.Os.AppSdk.Web.Blazor.Sites`)

| Type | Role |
|---|---|
| `WebAppDefinition.BlazorRoot` (`AppSdk.Web`) | Root component of the site; produces `web.blazor` in the manifest, the host page and `blazor.boot.json`. |
| `KazoSiteRouter` | The site's router: `AppAssembly`, `DefaultLayout`, `NotFound`; Blazor `@page` templates, route values to the page's `[Parameter]`s, nested `@layout`s. |
| `KazoSiteLayoutBase`, `KazoNavLink`, `KazoPageTitle` | Layout (`Body`), link marked `active` (`Match`), tab title. |
| `KazoWebSite` (cascaded) | `Navigation` (the tab's `NavigationManager`: `NavigateTo`, `LocationChanged`, `IsWithinSite`), `Http` (`KazoHttpClient` with `BaseAddress` and the tab's cookies), `AppId`, `Name`, `Title`. |
| Links | An `<a href>` of the site navigates without a request; a link whose `target` is not `_self` (`target="_top"`) loads a new document: this is how a Blazor page reaches the MVC pages of the same site (ADR 0087 §1). |
| `IKazoWebSiteHost`, `IKazoWebSiteProcess`, `KazoWebSiteView` | Browser side: starts the rendering process of a host page and draws the site in the page area. |

Details: [`blazor-sites.md`](blazor-sites.md).

### OS file control (`Kazo.Os.AppSdk.Blazor.Files`, ADR 0148, ADR 0155)

| Type | Role |
|---|---|
| `KazoFileView` | Typed component of the `kazoos.file-view` contract, provided by the explorer. See [`file-control-api.md`](file-control-api.md). Parameters: `InitialDirectory`, `Mode` (`KazoFileViewMode`: `Details`, `Icons`, `Tree`), `ModeChanged`, `Features` (`KazoFileViewFeatures`), `AllowMultiSelect`, `OpenOnTap` (one tap opens, ADR 0171), `Filter`, `SelectedResource`, callbacks `Opening` (`Handled`), `Navigated`, `SelectionChanged`, `Renamed`, `Deleted`, `ContextMenuOpening`, `ErrorOccurred`. |
| `IKazoFileView` | Controller: `CurrentDirectory`, `Items`, `SelectedItems` (`KazoFileViewItem`), `CanPaste`, `IsBusy`, `StateChanged`, `NavigateAsync`, `RefreshAsync`, `RevealAsync`, `ClearSelection`, `RefreshFilter`, `CanExecute`/`ExecuteAsync(command)`, `WhenIdleAsync`. |
| `KazoFileMenuOpeningEventArgs` | `Scope`, `Items`, `Directory`, `Menu` (`KazoMenuDefinition`), `Cancel`. |
| `KazoFileDialogs` (`.Files`) | Names of the dialogs of the `kazoos.file-dialogs` provider: `OpenWith` ("Open with"), `Properties`, `Conflict` (request types: `OpenWithDialogRequest`, `FilePropertiesDialogRequest`, `FileConflictDialogRequest`). |
| `KazoFileOperationDialogs.AsFileConflictResolver(IDialogService, IAppExecutionContext, CancellationToken)` (`.Files`) | Conflict resolver for `KazoFileOperations.MoveAsync` and `TransferAsync`: the "Replace / Keep both / Skip" dialog, modal to the app's window; closing the dialog or a host without a provider skips the item. |
| `FileMenuScope`, `KazoFileMenuItems` (`Kazo.Os.AppSdk.Files`) | Scopes (`File`, `Directory`, `Background`, `Desktop`) and entry identifiers (`open`, `open-with`, `open-with-other`, `open-with:<appId>`, `cut`, `copy`, `paste`, `paste-into`, `rename`, `delete`, `delete-permanently`, `new-folder`, `refresh`, `properties`…). |

Details: [`embedded-components.md`](embedded-components.md#kazofileview-and-ikazofileview).

### File view (`Kazo.Os.AppSdk.FileView`): removed

The `Kazo.Os.AppSdk.FileView` package left the SDK in 2.0 (ADR 0149,
ADR 0161): its code is the internal implementation of the file explorer
(`Kazo.Os.Apps.FileExplorer.FileControl`), which provides `KazoFileView` to
other apps. `FileTransfer` became `KazoFileOperations`. The migration table
for a 1.x app is in [`sdk-packages.md`](sdk-packages.md#sdk-20).

### Terminal (`Kazo.Os.AppSdk.Terminal`)

| Component | Parameters | Document |
|---|---|---|
| `TerminalPanel` | `CallerAppId`, `InitialUsername`, `InitialCwd`, `UseVirtualKeyboard`, `ShowBanner`, `FocusOnStart`, `OnExitRequested`, `OnTitleChanged`; hosts an `hsh` (capability `shell.host`); implements `IKazoTerminal` (`RunCommandAsync`, `SetWorkingDirectoryAsync`, `SendInputAsync`, `ReadOutput`, `ClearAsync`, events) | [`terminal-emulator.md`](../dev/terminal-emulator.md), [`terminal-api.md`](terminal-api.md), [`shell-host.md`](shell-host.md) |
| `TerminalTabs` | `CallerAppId`, `InitialCwd`, `OnLastTabClosed`; one `hsh` per tab | [`terminal-console.md`](../dev/terminal-console.md) |

### Icons and virtual keyboard

| Component | Parameters |
|---|---|
| `KazoIcon` (`AppSdk.Icons`) | `Library` (`IconLibrary`: `Bootstrap`, `FontAwesome`, `Lucide`, `SimpleIcons`), `Name`, `Icon`, `Size`, `Title`; see [`old/icon-library.md`](../dev/old/icon-library.md). |
| `VirtualKeyboard`, `VirtualKeyboardHost` (`AppSdk.VirtualKeyboard`) | `Options` (`VirtualKeyboardOptions`: modifier rows, symbols, arrows, labels), `OnKeyPressed` (`VirtualKeyPressedEvent`), `IsKeyboardVisible`, `ChildContent`. |

## Themes (`Kazo.Os.Theming.Abstractions`)

To read or write a user theme (ADR 0103, detail in
[`theme-editor.md`](../dev/theme-editor.md)):

| Type | Members |
|---|---|
| `ThemeTokenCatalog` | `All`, `InGroup(ThemeTokenGroup)`, `TryGet(name, out ThemeTokenDefinition)`: the 68 editable `--kos-*` tokens |
| `ThemeTokenDefinition` | `Name`, `Type` (`Color`, `Length`, `Duration`, `FontFamily`, `Number`), `Group`, `Label`, `Description` (English), `GetLabel(culture)`, `GetDescription(culture)`, `Example`, `RgbCompanion` |
| `ThemeTokenValidator` | `TryValidate(type or name, value, out normalized, out error)`, `TryValidate(type, value, culture, out normalized, out error)`, `TryGetRgbTriplet`, `ToHexColor` |
| `ThemeFile` | `Parse(text, origin, path)` → `ThemeFileParseResult` (theme + diagnostics), `Serialize(UserTheme)`, `IsValidId`, `Extension` (`.theme`), `MediaType`; file format and tokens: [`theme-files.md`](theme-files.md) |
| `ThemeFileDiagnostic` | `Line`, `Message` (English), `IsError`, `Path`, `GetMessage(culture)`, `ToString(culture)` |
| `UserTheme` | `Id`, `Name`, `BaseThemeId`, `Platforms`, `Tokens`, `Origin`, `SourcePath`, `Supports`, `ToCssProperties` |
| `UserThemeLibrary` | `SystemDirectory`, `GetUserDirectory(home)`, `GetUserThemePath(home, id)`, `Build`, `LoadAsync(listFiles, readFile, home)` |
| `ResolvedTheme` | `Resolve(id, platform, themes)` → `BaseThemeId`, `UserTheme`, `IsFallback` |

`ThemeScope` and `ThemePreview` (`Kazo.Os.Theming.Blazor`) take a
`Tokens` parameter applied on top of the base theme.
`ThemeScope` also takes `HighContrast` and `TextScale` (ADR 0105): attributes `data-contrast`
(`high` or `standard`) and `data-text-scale` (`100`, `125`, `150`, `200`), read by `themes.css`.

## Tasks of this document

- [x] List the SDK libraries and their namespaces.
- [x] App definition types and full table of the 37 capabilities (`keyring.read-all` added by `v3/8`, `system.backup` by `v3/7`, `users.manage` by the follow-up of `v3/8`, ADR 0120).
- [x] Members of `IAppExecutionContext`.
- [x] Each `Kazo*` facade with its members, capability and document.
- [x] Base classes of the entry points and window interfaces.
- [x] Blazor components: dialogs, charts, file view, terminal, icons, virtual keyboard.
- [x] Execution host (`AppExecutionHost`, ADR 0070) and `IAppProcessGateway.KillAsync`.
- [x] ASP.Kazo websites: startup, request, controllers, views, anti-forgery, links of Blazor sites (`web/11`).
- [x] `KazoWebDb` database, a site's debug log, `WebDatabase` gateway, sites' databases in `KazoStorageDiagnostics` (`web/12`).
- [x] Sensitive settings, `KazoKeyring` and `keyring.read-all` (`v3/8-settings-security`, ADR 0100).
- [x] `KazoUserAccounts`, `users.manage`, `ITerminalInputMode` and `ReadSecretLineAsync` (`passwd`, ADR 0120).
- [x] `KazoUserAccounts.CreateAsync`/`DeleteAsync` and access keys (ADR 0168, 0169).
- [x] Intents of ADR 0096: `KazoApps.EditFileAsync`, `RevealFileAsync`, `OpenUriAsync`, `ShowSettingsAsync`, `SendIntentAsync`; `Intent`/`IntentReceived`; app chooser; `FileView.RevealAsync` (`v3/4`).
- [x] Per-user defaults (ADR 0121, `v3/4b`): `KazoApps.GetDefaultHandlersAsync`, `SetDefaultHandlerAsync`, `ResetDefaultHandlerAsync`.
- [x] User themes: tokens, `.theme` format, library and resolution (`v3/11`).
- [x] `KazoSystemBackup`, `BrowserFilePicker`, codes `NoSpace` and `FileTooLarge` (16 MiB) (`v3/7`).
- [x] Localization: `Culture`/`FormatCulture`, `KazoLocale`, `GetLocalizer<T>`, `IShellSession.GetCommands(culture)` (`v3/12`, ADR 0104).
- [x] Window title: `WindowTitle`, `SetWindowTitleAsync`, `FormatWindowTitle`, `IAppWindowGateway` (`v3.1/3`, SDK 1.1, shipped in `PublicAPI.Shipped.txt` by `v3.1/4-sdk-1-1`).
- [x] Background tasks: `KazoBackground`, `KazoBackgroundContext`, `IAppExecutionContext.Background`, `KazoProcessKilledException.Reason`, `KOSBG` analyzer (`exec/5`, ADR 0128 and 0140).
- [x] SDK 1.2 (ADR 0132): `KazoWireContextAttribute` (`exec/1`), process limits and `TooManyOpenFiles` (`exec/2`), `OomScoreAdjust`/`OomScoreAdj` and `OutOfMemory` (`exec/3`), `nice` (`exec/4`), shipped in `PublicAPI.Shipped.txt` by `exec/10-final`.
- [x] SDK 2.0 (`desk/6-sdk-2`, ADR 0149, ADR 0161): `Kazo.Os.AppSdk.FileView` removed, `KazoFileOperations`, OS file control and context menu, shipped in `PublicAPI.Shipped.txt`.
- [x] Final review of the v3 migration (`v3/16-final`): pointer to the `PublicAPI.Shipped.txt` files, exhaustive list of the 1.0 surface.
