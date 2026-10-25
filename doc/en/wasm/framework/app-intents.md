# App intents: reveal, edit, open a URI, settings and app-defined intents

Work item `v3/4-intents` of the wasm v3 migration, according to
[ADR 0096](../dev/adr/0096-added-intents-and-app-defined-intents.md) (which completes
ADR 0010, 0039, 0047, 0050 §7 and 0056).

## Purpose

An app asks the OS to "open this" without knowing which app will do it: this is an
intent. Before this work item, the dispatcher only handled `LaunchApp`,
`OpenFile` and `ExecuteCommand`; "show in folder" and "app
settings" answered "sent" without doing anything, and an identifier declared by
an app always gave `intent.unknown`. The work item adds:

| Intent | Identifier | What the OS does |
|---|---|---|
| `RevealFileIntent(Path)` | `org.hackeros.intent.reveal-file.v1` | Launches the folder handler (`inode/directory`) on the parent folder with `--select=<path>`; the explorer selects the item and scrolls to it. This is `org.freedesktop.FileManager1.ShowItems` reduced to one argument. |
| `EditFileIntent(Path, MediaType, PreferredAppId)` | `org.hackeros.intent.edit-file.v1` | Chooses among the apps whose handler declares the `edit` action; with none, falls back to `open` (like `xdg-open`, which has no "edit" verb) and reports it (`FellBackToOpen`). |
| `OpenUriIntent(Uri, PreferredAppId)` | `org.hackeros.intent.open-uri.v1` | `file://` becomes an `OpenFileIntent`; any other scheme resolves like the media type `x-scheme-handler/<scheme>`, and the app receives the URI as an argument. |
| `ShowAppSettingsIntent(TargetAppId)` | `org.hackeros.intent.show-app-settings.v1` | The target app with `--show-settings` if it declares this intent (it has its own page), otherwise Settings on the app's card (`--app=<id>`). |
| `CustomIntent(IntentId, PayloadJson, TargetAppId, RememberChoice)` | free identifier `reversed.domain.name.vN` | Launches or activates the app that declares the identifier in `Intents`; several: the user chooses in the "Open with" picker, with "Always use this app" remembered in `~/.config/hackeros/intents.config`. |

And the command `xdg-open <file|folder|URI>`.

## Architecture

```text
App ──HackApps.*Async──► IAppIntentGateway ──► AppIntentDispatcher ──► FileAssociationResolver
 │                       (AppIntentGateway,      (capacités, accès,       (/etc/hackeros/
 │                        ou RemoteIntentGateway  erreurs intent.*)        file-associations.json,
 │                        dans un Web Worker)            │                 FileHandlers des manifestes ;
 │                                                       │                 ~/.config/hackeros d'abord,
 │                                                       │                 ADR 0121)
 │                                                       ▼
 └◄── AppContext.Intent / IntentReceived ◄── AppLifecycleOrchestrator.LaunchAsync(AppLaunchRequest.Intent)
```

- **`AppIntentDispatcher`** (`Platform.Core/Intents`): one method per intent
  (`DispatchRevealAsync`, `DispatchEditFileAsync`, `DispatchOpenUriAsync`,
  `DispatchShowSettingsAsync`, `DispatchCustomAsync`). New statuses
  `AccessDenied` and `Invalid`; `FellBackToOpen` on the result.
- **`FileAssociationResolver.ResolveSchemeAsync(scheme, preferredAppId)`**:
  resolves `x-scheme-handler/<scheme>` with the `open` action. The default
  document names the browser for `http` and `https`, next to the
  explorer for `inode/directory`.
- **`IntentChoiceStore`**: reads and writes `intents.config` (lines
  `intentId=appId`, comments kept), like `mimeapps.list`. The folder is
  created as `0755` and the file as `0600`, under the user's identity.
- **`PrincipalFileAccess.ForUser`**: the user's file context
  (read access to the home folder and the system, their groups), to check
  that they can read the folder to display, as the file manager would
  on their behalf.
- **Intent delivery**: `AppLaunchRequest.Intent` (`AppIntentDelivery`:
  `IntentId`, `PayloadJson`, `SenderAppId`, `Arguments`) becomes
  `IAppExecutionContext.Intent` at launch. For an already open instance
  (`SingleInstancePerUser` window, service, site), the orchestrator raises
  `IntentReceived` on its context, like a `GApplication` that receives the
  command line of a second launch; a launch with arguments but
  no intent arrives as a `LaunchApp` activation.
- **Web Workers**: operations `int.edit`, `int.reveal`, `int.uri`,
  `int.settings`, `int.send`; the starting intent travels in the start
  message (`it`) and an intent delivered later in an `intent` message
  (OS → worker). `xdg-open` runs in the pool and behaves as on the main
  thread (parity test).

## Usage and API

### Sending

```csharp
await HackApps.RevealFileAsync("~/Documents/rapport.txt");      // « Afficher dans le dossier »
HackOpenFileResult edit = await HackApps.EditFileAsync("notes.md");
HackIntentResult web = await HackApps.OpenUriAsync("https://hackersearch.hack/?q=nmap");
await HackApps.ShowSettingsAsync();                              // réglages de l'app courante
HackIntentResult sent = await HackApps.SendIntentAsync(
    "com.example.notes.share.v1", """{"text":"Bonjour"}""");
```

| Member | Capability | Failures |
|---|---|---|
| `RevealFileAsync(path)` | `apps.launch` | `HackFileNotFoundException`, `HackUnauthorizedAccessException` (unreadable folder), `HackAppLaunchException` (no folder handler) |
| `EditFileAsync(path)` | `apps.launch` | `HackOpenFileResult`: `Opened`, `ChooserRequired` (candidates), `NoHandler`; `FellBackToOpen` |
| `OpenUriAsync(uri, appId)` | `apps.launch` | `ArgumentException` if the URI is not absolute; `HackIntentResult`: `Delivered`, `ChooserRequired`, `NoHandler` |
| `ShowSettingsAsync(appId)` | none | `HackAppLaunchException` (unknown or disabled app) |
| `SendIntentAsync(id, payload, targetAppId, rememberChoice)` | `apps.launch` | `ArgumentException` (malformed or reserved identifier, payload that is not a JSON object or exceeds 64 KiB, target that does not declare the intent); `HackIntentResult` |

A windowed app rather calls `WindowAppBase.SendIntentAsync(id, payload)`:
if several apps answer, it displays the OS picker
(`ChooseAppAsync`, `AppChooserDialog`) then sends the intent again to the chosen
app, with "Always use this app" if the user ticked it. The picker
is displayed by the caller, as for `OpenFile`: the dispatcher never
makes a window appear in another app.

### Receiving

```csharp
public override IReadOnlyList<AppIntentDeclarationManifest> Intents =>
    [new("com.example.notes.share.v1", PayloadSchemaAssetPath: "schemas/share.json")];
```

- At launch: `LaunchIntent` (window) or `AppContext.Intent`.
- Already open: `protected override Task OnIntentReceivedAsync(AppIntentDelivery intent)`
  (`WindowAppBase`) or `OnIntentReceivedAsync(intent, token)` (`ServiceAppBase`).
  A command, which lives only for the duration of one line, only reads
  `AppContext.Intent`.
- `PayloadSchemaAssetPath` documents the contract for callers; the OS does not
  validate the schema (no JSON Schema library in the OS), only
  the shape: a JSON object of at most 64 KiB.

### Declaring a URI scheme

```csharp
public override IReadOnlyList<FileHandler> FileHandlers =>
    [new("x-scheme-handler/gopher", [], ["open"])];
```

### Dispatcher error codes

| Intent | Codes |
|---|---|
| All except `ShowAppSettings` | `intent.capability-denied` (without `apps.launch`) |
| `RevealFile` | `intent.reveal-file.not-found`, `.denied`, `.chooser-required`, `.no-handler` |
| `EditFile` | `intent.edit-file.chooser-required`, `.no-handler`, `.target-invalid` |
| `OpenUri` | `intent.open-uri.invalid`, `.chooser-required`, `.no-handler`, `.target-invalid`; `file://` gives the `intent.open-file.*` codes |
| `ShowAppSettings` | `intent.show-settings.not-found`, `.disabled`, `.no-settings-app` |
| App intent | `intent.custom.invalid-id`, `.reserved-id`, `.payload-too-large`, `.payload-invalid`, `.target-invalid`, `.chooser-required`, `intent.unknown` (nobody declares it) |

`IAppIntentGateway` translates these statuses into `AppIntentOutcome`: a
`.no-handler` code or `intent.unknown` becomes `NoHandler`, `TargetInvalid` becomes
`Invalid`, and a capability refusal throws `AppGatewayAccessDeniedException`
like `LaunchAsync`.

### `xdg-open`

See [`linux-commands.md`](../dev/linux-commands.md#fichiers): path → `OpenFile`
(a folder as `inode/directory`), URI → `OpenUri`. Codes: 0 sent, 1 command
line, 3 no app, 4 missing file or failure; several apps without a default
give 4 and the list of candidates.

### Explorer and Settings

- Explorer: `<folder>` and `--select=<path>`, details in
  [`file-explorer.md`](../dev/file-explorer.md#ligne-de-commande-et--afficher-dans-le-dossier--adr-0039-adr-0096-1).
- Settings: `--app=<id>` opens the "Application" tab (`AppDetailsPanel`):
  identity, requested capabilities with the state of dangerous capabilities
  (`HackPermissionAdministration`), managed file types and schemes, service
  state, startup mode and Start/Stop buttons for a `Service` app
  (`HackServiceController`, hence Settings' `services.manage` capability),
  and uninstallation (the "Installed Apps" panel filtered on the app).

## Key decisions

- **`RevealFile` requires `apps.launch`** ("same rules as `OpenFile`", ADR
  0096 §1) and checks the path under the user's identity: a missing
  file or an unreadable folder launches nothing. Before, it required no
  capability and checked nothing.
- **`ShowAppSettings` requires no capability**: it opens a system
  surface and carries no data from the caller, like freedesktop's
  settings panels.
- **A built-in identifier cannot be sent as an app intent**
  (`intent.custom.reserved-id`): it has a typed record with its own rules,
  which an untyped send would bypass.
- **The default choice for a scheme lives in `file-associations.json`**, like
  that of a file type: the user's
  (`~/.config/hackeros/file-associations.json`) takes precedence over the machine's
  (`/etc/hackeros/file-associations.json`) since work item `v3/4b`
  ([ADR 0121](../dev/adr/0121-user-file-associations.md),
  [`user-file-associations.md`](../dev/user-file-associations.md)).
- **A terminal has no picker**: `xdg-open` with several apps and
  no default exits with 4 and lists the candidates; the OS never guesses.
- **The explorer finally honors its folder argument**: opening a folder
  from another app always opened the home folder.

## Tests

- `Tests/HackerOs.Platform.Core.Tests/Intents/AppIntentDispatcherTests.cs`:
  each intent and each error code, arguments and `Intent` received by the launched
  app, choice remembered in `intents.config`, delivery to an open instance
  through `IntentReceived`, translation by `AppIntentGateway`.
- `Tests/HackerOs.Commands.Tests/XdgOpenCommandTests.cs` and the
  `xdg-open` scenario of `CommandParityTests` (worker and main thread).
- `Tests/HackerOs.Apps.FileExplorer.Tests/FileExplorerLaunchArgumentsTests.cs`,
  `Tests/HackerOs.Apps.Settings.Tests/SettingsWindowTests.cs` (`--app=`).
- `Tests/HackerOs.Platform.Blazor.Tests/DialogCoordinatorTests.cs` (app
  picker), `Tests/HackerOs.AppSdk.Tests/Apps/HackAppsIntentTests.cs` (facades),
  `Tests/HackerOs.Platform.WorkerHost.Tests/WireCodecIntentTests.cs` (codec).

## Tasks

- [x] `AppIntentIds`: `edit-file`, `open-uri`, `x-scheme-handler/` prefix, `IsWellFormed`, `IsBuiltIn`, 64 KiB.
- [x] Records `EditFileIntent`, `OpenUriIntent`, `CustomIntent`, `AppIntentDelivery`.
- [x] Dispatcher: real `RevealFile`, `EditFile` with fallback to `open`, `OpenUri`, real `ShowAppSettings`, app intents.
- [x] `ResolveSchemeAsync` and default browser for `http`/`https`.
- [x] `intents.config` ("Always use this app").
- [x] `IAppIntentGateway`: `EditFileAsync`, `RevealFileAsync`, `OpenUriAsync`, `ShowAppSettingsAsync`, `SendIntentAsync`.
- [x] `IAppExecutionContext.Intent` and `IntentReceived`; second activation of an open instance.
- [x] `HackApps`; `WindowAppBase` and `ServiceAppBase` (`OnIntentReceivedAsync`, `SendIntentAsync`, `ChooseAppAsync`).
- [x] OS app picker (`AppChooserDialog`).
- [x] Web Workers: operations, `intent` message, codec.
- [x] `xdg-open` command.
- [x] Explorer: starting folder and `--select` (`FileView.RevealAsync`).
- [x] Settings: an app's card (`--app=<id>`).
- [x] Browser: `x-scheme-handler/http` and `https`.
- [x] Tests, this document, `sdk-reference.md`, `app-model.md`, `file-explorer.md`, `linux-commands.md`, `shell-commands.md`, `worker-processes.md`, plan and index.
