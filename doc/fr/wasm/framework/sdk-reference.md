> [English version](../../../en/wasm/framework/sdk-reference.md)

# Référence du SDK Kazo OS

## Objet

Liste des API publiques qu'une app peut utiliser, bibliothèque par
bibliothèque, avec la capacité exigée et le document qui les détaille. Pour
apprendre à écrire une app, commencer par le
[guide du développeur](sdk-developer-guide.md). Les règles de la frontière du
SDK (références, injection, JavaScript) sont dans [`app-sdk.md`](app-sdk.md).

Conventions de l'API :

- Les façades `Kazo*` sont statiques et lisent le **contexte ambiant**
  (`KazoExecutionContext`) que l'OS ouvre autour du code de l'app. Plusieurs
  existent aussi avec un `IAppExecutionContext` explicite en premier
  paramètre, pour le code qui tourne hors de ce contexte.
- Les noms et les comportements suivent `System.IO`, `System.Diagnostics`,
  `System.Net` et `System.ServiceProcess`, avec le préfixe `Kazo` et des
  méthodes asynchrones.
- Les chemins acceptent `~` et les chemins relatifs au répertoire courant du
  processus.
- Une capacité manquante lève `KazoUnauthorizedAccessException`
  (`RequiredCapability`), ou `AppGatewayAccessDeniedException` pour une
  passerelle utilisée directement.

## Bibliothèques

Toutes sont publiées en paquets NuGet locaux du même nom, à la version de
`build/Versions.props` (`2.2.2` aujourd'hui, publiée par le workflow `Release Kazo OS` à la fusion de sa PR ; `2.1.0` le 2026-10-26, première publication automatique, ADR 0173 ; la majeure 2 date de la fin de la série du bureau, ADR 0149 et 0161 ; `1.2.0` à la fin de la série `exec`, ADR 0132 §2 ; `1.1.0` pour la série v3.1, ADR 0117 §5 ; `1.0.0` au gel du SDK, ADR 0114) ; un autre
dépôt les consomme comme le décrit [`building-on-kazoos.md`](building-on-kazoos.md).
La promesse de compatibilité SemVer couvre les bibliothèques de cette page et
`Kazo.Os.Theming.Blazor`, pas la plateforme (ADR 0114 §2).

Cette page présente les API qu'une app utilise au quotidien ; elle n'est pas
exhaustive. La liste complète et exacte de la surface publique 2.0 est le
fichier `PublicAPI.Shipped.txt` de chaque projet (environ 940 types au gel de la 1.0),
vérifié au build par l'analyseur d'API publique : un ajout va dans
`PublicAPI.Unshipped.txt`, un retrait exige une ligne `*REMOVED*` (ADR 0114).
Les ajouts non publiés (lignes de `Unshipped.txt`) partiront avec la prochaine
mineure ; ceux qui sont connus sont listés dans [`sdk-packages.md`](sdk-packages.md#ajouts-depuis-la-200-prochaine-mineure).

| Projet | Espaces de noms | Pour |
|---|---|---|
| `Shared/Kazo.Os.App.Abstractions` | `Kazo.Os.App.Abstractions` | Définir une app (`AppDefinition`, capacités, manifeste). |
| `Shared/Kazo.Os.Simulation.Abstractions` | `Kazo.Os.Simulation.Abstractions.*` | Types de données du système simulé (chemins, passerelles, journaux, notifications, processus). |
| `Shared/Kazo.Os.AppSdk` | `Kazo.Os.AppSdk`, `.IO`, `.Settings`, `.Keyring`, `.Apps`, `.Net`, `.Clipboard`, `.Diagnostics`, `.ServiceProcess`, `.Shell`, `.Administration`, `.Threading` (SDK 1.2) | Façades `Kazo*`, contexte d'exécution, commandes et services. |
| `Shared/Kazo.Os.AppSdk.Blazor` | `Kazo.Os.AppSdk.Blazor`, `.Charts`, `.Interop`, `.Settings`, `.Files`, `.ContextMenus`, `.Components` | Apps fenêtrées, dialogues, graphiques, contrôle de fichiers et menu contextuel de l'OS (SDK 2.0). |
| `Shared/Kazo.Os.AppSdk.Terminal` | `Kazo.Os.AppSdk.Terminal` | Terminal xterm.js. |
| `Shared/Kazo.Os.AppSdk.Web` | `Kazo.Os.AppSdk.Web.Hosting`, `.Http`, `.Routing`, `.Mvc`, `.Views`, `.Antiforgery`, `.Sessions`, `.Diagnostics`, `.Data`, `.Logging` | Sites internes ASP.Kazo façon ASP.NET Core MVC ([guide ASP.Kazo](asp-kazo-developer-guide.md), [`asp-kazo-mvc.md`](asp-kazo-mvc.md)). |
| `Shared/Kazo.Os.AppSdk.Web.Blazor` | `Kazo.Os.AppSdk.Web.Blazor.Sites` | Sites Blazor interactifs dessinés dans le navigateur ([`blazor-sites.md`](blazor-sites.md)). |
| `Shared/Kazo.Os.AppSdk.Icons` | `Kazo.Os.AppSdk.Icons` | Icônes. |
| `Shared/Kazo.Os.AppSdk.VirtualKeyboard` | `Kazo.Os.AppSdk.VirtualKeyboard` | Clavier virtuel. |

## Définir une app (`Kazo.Os.App.Abstractions`)

| Type | Rôle |
|---|---|
| `[AppManifest]` | Marque la classe qui décrit l'app ; le build en génère `manifest.json`. `Execution` (`AppExecutionHost`) choisit où tourne un processus sans interface (ADR 0070). |
| `AppExecutionHost`, `AppExecutionHosts` | `Default`, `MainThread`, `Pool`, `Dedicated` ; `DefaultFor(kind)` (commande → `Pool`, service → `Dedicated`, fenêtre → `MainThread`), `Resolve`, `Of(manifest)`, `IsWorker`. Manifeste : `"execution": "mainThread" \| "pool" \| "dedicated"`. Voir [`worker-processes.md`](../dev/worker-processes.md), [`app-execution-hosts.md`](app-execution-hosts.md). |
| `AppDefinition` | Classe de base. Obligatoires : `Id`, `Name`, `Kind`, `EntryPoint`. Facultatifs : `Version`, `Description`, `PublisherId`, `Category`, `LaunchVisibility`, `Icons`, `Capabilities`, `FileHandlers`, `Terminal`, `Components`, `MobileEntryPoint`, `SingleInstancePerUser`, `AutoStart`, `SupportsBack`, `Window` (`WindowOptions`, ADR 0095, [`window-model.md`](window-model.md) ; `RememberGeometry`, SDK 1.1, ADR 0116, [`window-geometry.md`](../dev/window-geometry.md) ; `Style` (`WindowStyle`), `ShowIcon`, `MaximizeBox`, `MinimizeBox`, `ShowInTaskbar`, `Modal`, SDK 3.1, ADR 0234, [`window-model.md`](window-model.md) §6), `Resources`, `Settings`, `Dependencies`, `Intents`, `DeclaredTopicPermissions`, `Localizations`, `Assets`, `OomScoreAdjust` (SDK 1.2, ADR 0126 §4, [`oom-killer.md`](../dev/oom-killer.md)). |
| `AppKind` | `Window`, `Terminal`, `Service`. |
| `AppLaunchVisibility` | `Visible`, `Hidden` (absente du lanceur). |
| `FileHandler(MediaType, Extensions, Actions, Priority)` | Type de fichier que l'app ouvre. |
| `TerminalCommand(Name, Usage, Aliases)` | Nom de commande du shell. |
| `AppComponent(Contract, Type)` | Composant que d'autres apps peuvent embarquer (ADR 0063) ; voir [`embedded-components.md`](embedded-components.md). |
| `AppSettingsSchemaManifest(SchemaVersion, Fields)`, `AppSettingFieldManifest(Key, ValueType, DefaultValue, Scope, Sensitivity, AllowedValues, Group)` | Schéma des paramètres ; `AppSettingValueType` : `String`, `Integer`, `Boolean`, `Enum`. |
| `AppResourceProfileManifest` | Poids CPU, mémoire, disque et réseau simulés (`None` par défaut). |
| `AppDefinitionMapper.ToManifest(definition)` | Définition → `AppManifest` (utile dans les tests). |
| `KazoOsSdk` | `Version` (version du SDK chargé, par exemple `2.1.0`), `SemanticVersion`, `IsCompatible(range)` (ADR 0098 §3). `AppDefinition.SdkCompatibility` déclare une plage ; par défaut, la version contre laquelle l'app est compilée est le minimum. |
| `AppManifest` | Manifeste validé, lu par l'OS ; `AppContext.Manifest` à l'exécution. `OomScoreAdj` (champ `oomScoreAdj`, SDK 1.2, ADR 0126 §4) : valeur de `AppDefinition.OomScoreAdjust`. |
| `AppCapabilities` | Catalogue des capacités (voir ci-dessous) ; `Describe`, `IsDangerous`, `GetRisk`, `IsKnown`, `RequiresWindowKind`, `All`. |
| `VirtualPath` | Chemin absolu canonique (`Parse`, `Value`). |

Détails : [`app-model.md`](app-model.md).

### Capacités

| Constante | Identifiant | Classe | Donne accès à |
|---|---|---|---|
| `FileSystemPrivateRead` / `Write` | `filesystem.private.read` / `.write` | normale | `~/.config/<appId>/` ; lecture de `/etc/<appId>/` |
| `FileSystemUserSelectedRead` / `Write` | `filesystem.user-selected.read` / `.write` | normale | fichiers choisis par l'utilisateur |
| `FileSystemUserHomeRead` / `Write` | `filesystem.user-home.read` / `.write` | normale | dossier personnel |
| `FileSystemSystemRead` / `Write` | `filesystem.system.read` / `.write` | **dangereuse** | tout le reste (`/etc`, `/var`, `/proc`…) ; écrire `/etc`, `/bin`, `/var` exige aussi un administrateur |
| `AppsLaunch` | `apps.launch` | normale | `KazoApps.LaunchAsync`, `OpenFileAsync`, `EditFileAsync`, `RevealFileAsync`, `OpenUriAsync`, `SendIntentAsync` |
| `AppsManage` | `apps.manage` | **dangereuse** | `KazoAppManagement`, `KazoComponentProviders`, `KazoFileMenuEntries` |
| `DialogFileOpen`, `DialogFileSave`, `DialogFolderSelect` | `dialogs.file-open`, `.file-save`, `.folder-select` | normale | dialogues de fichiers (fenêtres seulement) |
| `FileAssociationsRead` | `file-associations.read` | normale | associations de fichiers ; avec `filesystem.user-home.write`, choix propre à l'utilisateur (ADR 0121) |
| `FileAssociationsWrite` | `file-associations.write` | **dangereuse** | changer les associations de la machine |
| `ProcessList` | `process.list` | normale | voir les processus des autres apps |
| `ProcessManage` | `process.manage` | **dangereuse** | arrêter les processus des autres apps |
| `ServicesManage` | `services.manage` | **dangereuse** | `KazoServiceController` sur les services des autres apps |
| `WindowsManage` | `windows.manage` | **dangereuse** | fenêtres des autres apps |
| `ScreensManage` | `screens.manage` | normale | `KazoScreens` (écrans secondaires, ADR 0184) |
| `NotificationsPost` | `notifications.post` | normale | `AppContext.Notifications` |
| `ClipboardRead` / `Write` | `clipboard.read` / `.write` | normale | `KazoClipboard` |
| `NetworkSimulatedRead` / `Write` | `network.simulated.read` / `.write` | normale | réseau simulé |
| `NetworkRealAccess` | `network.real.access` | **dangereuse** | vrai réseau par le serveur relais |
| `NetworkListen` | `network.listen` | normale | `KazoHttpListener` sur un préfixe hors de son manifeste |
| `NetworkListenPrivileged` | `network.listen.privileged` | **dangereuse** | écouter un port sous 1024 |
| `ShellControl` | `shell.control` | normale | agir sur le shell appelant (`IShellSession`) |
| `ShellContextMenu` | `shell.context-menu` | normale | ajouter des entrées aux menus des fichiers (`[RegisterFileContextMenu]`, ADR 0151) |
| `ShellHost` | `shell.host` | **dangereuse** | `KazoShell` |
| `KeyringReadAll` | `keyring.read-all` | **dangereuse** | `KazoKeyring` sur les entrées des autres apps |
| `UsersManage` | `users.manage` | **dangereuse** | `KazoUserAccounts.ResetPasswordAsync` / `RemovePasswordAsync` (ADR 0120), `CreateAsync` / `DeleteAsync` (ADR 0169) |
| `PermissionsManage` | `permissions.manage` | **dangereuse** | `KazoPermissionAdministration` |
| `ServerConnectionManage` | `server.connection.manage` | **dangereuse** | `KazoServerConnection` |
| `SystemPower` | `system.power` | **dangereuse** | `KazoSystem` (avec un administrateur) |
| `SystemStorageInspect` | `system.storage.inspect` | **dangereuse** | `KazoStorageDiagnostics` |
| `SystemBackup` | `system.backup` | **dangereuse** | `KazoSystemBackup` (avec un administrateur) |

Une capacité dangereuse d'une app non système n'est accordée qu'après
approbation d'un administrateur. Détails : [`capabilities.md`](capabilities.md),
[`dangerous-capability-approval.md`](../dev/dangerous-capability-approval.md).

## Contexte d'exécution (`Kazo.Os.AppSdk`)

### `IAppExecutionContext`

Construit par l'OS pour chaque instance (`AppContext` dans une fenêtre,
paramètre de `RunCoreAsync` pour un service, `context.App` pour une commande).

| Membre | Rôle |
|---|---|
| `Manifest`, `InstanceId`, `ProcessId`, `SessionId`, `CancellationToken` | Identité de l'instance ; le jeton est annulé à la fermeture, au kill, à la déconnexion. |
| `StopRequested`, `StopRequest` | Demande d'arrêt (`SIGTERM`, ADR 0069), levée une fois avant l'annulation du jeton : `KazoStopRequestedEventArgs` (`Reason`, `Deadline`, `Defer`). Préférer `KazoProcess.Current`. Après un kill, chaque passerelle lève `KazoProcessKilledException`. |
| `UserId`, `UserLoginName`, `UserDisplayName`, `HomeDirectory`, `UserAuthority`, `IsRootElevated` | Utilisateur effectif. |
| `GrantedCapabilities`, `Capabilities` | Capacités accordées à cette instance. |
| `FileSystem`, `Watch` | Système de fichiers brut (préférer `KazoFile`…) et surveillance ; `Watch` (et `KazoFileSystemWatcher`) marche aussi dans un worker depuis l'ADR 0124. |
| `Logging` | `Log(severity, message, properties)` → `/var/log/apps/<appId>.log`. |
| `Notifications` | `Post(severity, title, message, actions, expiresAfter)` ; `notifications.post`. |
| `Events` | Bus d'événements : sujets de l'app (`Publish(topic, payload)`, `PublishAsync` qui rend le vrai résultat même dans un worker, ADR 0145, `Subscribe`, `SubscribeChannel`, `RegisterSharedChannel`) et événements de la plateforme (`Subscribe<TEvent>`). Dans un worker, les charges d'un type de l'app sont déclarées par `[assembly: KazoWireContext(typeof(...))]` (`KazoWireContextAttribute`, SDK 1.2, ADR 0124) ; voir [`app-sdk.md`](app-sdk.md#processus-dans-des-web-workers-adr-0070-et-0071). |
| `Clock` | Temps simulé : `UtcNow`, `CurrentTick`, `DelayAsync`, `Schedule`. |
| `Processes` | Processus de l'app et des autres (`OwnProcess`, `ListProcesses`, `StopAsync`, `Kill`). Préférer `KazoProcess`. Les descendants du processus de l'app (ses tâches `KazoBackground`) comptent comme les siens, sans `process.list` ni `process.manage` (ADR 0140 §7). Dans un worker, `ListProcesses` et `Kill` d'un autre processus demandent une page isolée (ADR 0143 §5), sinon `NotSupportedException`. |
| `Background` | Passerelle des tâches en arrière-plan (`IAppBackgroundGateway.RunAsync`, SDK 1.2, ADR 0128) ; derrière `KazoBackground`. Par défaut (contexte que l'OS n'a pas construit), la tâche s'exécute en ligne avec la même sérialisation. |
| `Intents`, `Catalog`, `AppManagement` | Derrière `KazoApps` et `KazoAppManagement`. `IAppIntentGateway` : `LaunchAsync`, `OpenFileAsync`, `EditFileAsync`, `RevealFileAsync`, `OpenUriAsync`, `ShowAppSettingsAsync`, `SendIntentAsync` → `AppIntentResult` (`AppIntentOutcome`, ADR 0096). |
| `Intent`, `IntentReceived` | Intent qui a lancé l'instance (`AppIntentDelivery` : `IntentId`, `PayloadJson`, `SenderAppId`, `Arguments`) ; événement levé quand l'OS remet un intent ou une seconde activation à l'instance déjà ouverte (ADR 0096 §5). Voir [`app-intents.md`](app-intents.md). |
| `Environment` | Derrière `KazoEnvironment`. |
| `Culture`, `FormatCulture` | Langue des textes (`en` ou `fr`, depuis `LC_ALL`/`LC_MESSAGES`/`LANG` du processus) et culture des formats (`fr-CA`, depuis `LC_TIME`) ; membres par défaut relus à chaque appel (ADR 0104). `context.GetLocalizer<Strings>()` donne un `IStringLocalizer<Strings>` lié à cette langue ([`localization.md`](localization.md)). |
| `Network`, `Drives`, `Clipboard`, `ShellHost`, `Services` | Derrière les façades du même domaine. |
| `CrossOriginIsolation` | État de l'isolation cross-origin (`IAppCrossOriginIsolationGateway.GetStatusAsync`, ADR 0146, SDK 2.0) ; derrière `KazoCrossOriginIsolation`, aucune capacité. Membre par défaut : `NotSupportedException`. |
| `Console` | Derrière `KazoConsole` (ADR 0064). |
| `SystemSettings`, `PermissionAdministration`, `ServerConnection`, `SystemPower`, `StorageDiagnostics`, `SystemBackup` | Derrière les façades d'administration. |
| `WebDatabase` | Base IndexedDB `kazoweb.<appId>` d'une app `Web` (`IAppWebDatabaseGateway`, ADR 0088) ; derrière `KazoWebDb` du SDK web. Une autre app reçoit `UnauthorizedAccessException`. |
| `Accessibility` | Préférences d'accessibilité effectives de la session (`IAppAccessibilityGateway` : `GetPreferencesAsync`, `Changed`, ADR 0105 §3) ; derrière `KazoAccessibility`. Sans service (tests, Web Worker) : `DefaultAppAccessibilityGateway`, qui répond les valeurs par défaut. |
| `Keyring` | Trousseau de connexion de l'utilisateur (`IAppKeyringGateway`, ADR 0100 §2) ; derrière `KazoKeyring`. Toujours verrouillé hors d'une session avec mot de passe. |
| `PermissionErrors` | Événement `PermissionDenied` à chaque refus de permission. |
| `OneShotElevation` | Tickets d'élévation à usage unique (ADR 0042). |

### Façades générales

| API | Membres | Capacité | Document |
|---|---|---|---|
| `KazoExecutionContext` | `Current`, `CurrentOrNull`, `WorkingDirectory`, `Enter(context, workingDirectory)` ; `KazoExecutionContextMissingException` | — | [`kazo-file-api.md`](kazo-file-api.md) |
| `KazoEnvironment` | `CurrentDirectory`, `SetCurrentDirectoryAsync`, `GetEnvironmentVariable(s)`, `SetEnvironmentVariable`, `GetCommandLineArgs`, `GetFolderPath(KazoSpecialFolder)`, `UserName`, `MachineName`, `ProcessId`, `NewLine`, `Culture`, `FormatCulture` | — | [`process-environment.md`](../dev/process-environment.md) |
| `KazoLocale`, `KazoLocalization`, `KazoStringLocalizer`, `EnglishStringLocalizer<T>` (`Kazo.Os.AppSdk.Globalization`) | Règles de locale (`ResolveLanguage`, `ResolveFormatCulture`, `ParseLocaleConf`, `FormatLocaleConf`, `AvailableLocales`) ; `GetLocalizer<T>()` lié au processus ; localiseur toujours anglais pour les tests | — | [`localization.md`](localization.md) |
| `KazoTimeZone` (`Kazo.Os.AppSdk.Globalization`), `IAppExecutionContext.TimeZone` | Fuseau du processus (`TZ`, sinon celui de la session) : `Current`, `Now`, `ToLocal`, `TryFind`, `Resolve`, `GetId`, `FormatOffset`, `Available` | — | [`time-zone.md`](time-zone.md) |
| `KazoSpecialFolder` | `UserProfile`, `Desktop`, `MyDocuments`, `ApplicationData`, `LocalApplicationData` | — | [`app-sdk.md`](app-sdk.md#utilisateur-courant-adr-0056-1) |
| `KazoUser` | `Current`, `Describe(context)`, `Root` → `KazoUserInfo(LoginName, DisplayName, HomeDirectory, IsAdministrator)` | — | [`app-sdk.md`](app-sdk.md#utilisateur-courant-adr-0056-1) |
| `KazoConsole` (`IAppConsoleGateway`) | `Out`, `Error`, `In`, `IsAttached`, `Write`, `WriteLine[Async]`, `ReadLineAsync` : flux standard de l'instance ; ceux du shell qui l'a lancée (une app fenêtrée lancée par son nom écrit dans le terminal), `/dev/null` depuis le bureau (ADR 0064) | — | [`shell-interpreter.md`](../dev/shell-interpreter.md#apps-lancées-par-leur-nom) |

### Fichiers (`Kazo.Os.AppSdk.IO`)

| API | Membres | Document |
|---|---|---|
| `KazoFile` | `ReadAllText/Lines/BytesAsync`, `WriteAllText/Lines/BytesAsync`, `AppendAllText/LinesAsync`, `ExistsAsync`, `CopyAsync`, `MoveAsync`, `DeleteAsync`, `OpenRead/Write/AppendAsync`, `Get/SetUnixFileModeAsync`, `CreateSymbolicLinkAsync` (ADR 0064, `ln -s`) | [`kazo-file-api.md`](kazo-file-api.md) |
| `KazoDirectory` | `CreateDirectoryAsync`, `ExistsAsync`, `GetFiles/Directories/FileSystemEntriesAsync`, `EnumerateFileSystemEntriesAsync`, `MoveAsync`, `CopyAsync` (récursive), `DeleteAsync` | [`kazo-file-api.md`](kazo-file-api.md), [`file-explorer.md`](../dev/file-explorer.md) |
| `KazoPath` | `Combine`, `Join`, `GetFileName[WithoutExtension]`, `GetExtension`, `HasExtension`, `ChangeExtension`, `GetDirectoryName`, `IsPathRooted`, `GetFullPath` | [`kazo-file-api.md`](kazo-file-api.md) |
| `KazoFileInfo`, `KazoDirectoryInfo` (`KazoFileSystemInfo`) | `GetAsync`, `RefreshAsync`, `Exists`, `Length`, dates, `UnixFileMode`, `Owner`, `Group`, `Revision`, liens symboliques ; opérations d'instance | [`kazo-file-api.md`](kazo-file-api.md) |
| `KazoFileStream` | Flux de lecture, ou d'écriture publié en une écriture atomique à la fermeture ; le tampon d'écriture compte dans `VmData` quand la mémoire est mesurée | [`kazo-file-api.md`](kazo-file-api.md) |
| `KazoWriteOptions` | `ExpectedRevision` (concurrence optimiste), `CreateMode` | [`kazo-file-api.md`](kazo-file-api.md) |
| `KazoFileSystemWatcher` | `Created`, `Changed`, `Deleted`, `Renamed`, `Error`, `Filter`, `IncludeSubdirectories`, `StartAsync` | [`kazo-file-api.md`](kazo-file-api.md) |
| `KazoTrash` | `MoveToTrashAsync`, `GetItemsAsync`, `RestoreAsync`, `DeleteAsync`, `EmptyAsync`, `TrashDirectory` | [`trash.md`](../dev/trash.md), [`trash-api.md`](trash-api.md) |
| `KazoFileOperations` | `CopyAsync`, `MoveAsync(…, resolveConflict)`, `TransferAsync`, `ImportAsync` (`KazoHostFile` : fichiers de l'hôte), `MoveToTrashAsync`, `GetCopyName`, `GetName` → `KazoFileTransferResult` (`Completed`, `Skipped`, `Failures`, `Cancelled`, `DescribeFailures`) ; règles de Nautilus (`nom (copy).ext`, « Remplacer / Garder les deux / Ignorer » par `KazoFileConflictResolver`), un dossier jamais copié en lui-même, une erreur n'arrête pas les autres ; avec les droits de l'app ; SDK 2.0 (ADR 0161 §2). Une fenêtre pose la question de conflit par `Dialogs.AsFileConflictResolver(AppContext)` | [`file-explorer.md`](../dev/file-explorer.md), [`file-operations-api.md`](file-operations-api.md) |
| `KazoDesktopLayout`, `KazoDesktopCell`, `KazoDesktopOptions`, `KazoDesktopItems`, `KazoDesktopEntry`, `KazoDesktopSettings`, `KazoDesktopLink` (`.Files`) | Grille du bureau en fonctions pures (`Order`, `Arrange`, `Move`, `CellAtPoint`, `GridSize`), raccourcis `.desktop` (`TryParse`, `Format`, `CreateShortcutAsync` ; liens vers un fichier ou un dossier `KazoDesktopLink.CreateLinkAsync`, ADR 0185 §13), `~/.config/kazoos/desktop.config` (`ReadAsync`, `SaveOptionsAsync`, `SavePositionsAsync`) ; SDK 2.0 (ADR 0160) | [`desktop.md`](../dev/desktop.md), [`desktop-api.md`](desktop-api.md) |
| `KazoTextContent` | `TryDecode`, `TryReadAsync` (texte ou binaire, comme `file`) | [`text-editor.md`](../dev/text-editor.md) |
| `KazoRecoveryDraft` | `Open(name)`, `SaveAsync`, `TryReadAsync`, `TakeAsync`, `DeleteAsync` : brouillon de travail non enregistré dans `~/.local/state/<appId>/recovery/` (ADR 0069) | [`app-sdk.md`](app-sdk.md#arrêt-propre-et-points-darrêt-adr-0069) |
| `KazoDriveInfo` | `GetDrivesAsync` : taille et espace libre (comme `df`) | [`system-metrics.md`](../dev/system-metrics.md) |
| `KazoCrossOriginIsolation` (`.Diagnostics`) | `GetStatusAsync` → `KazoCrossOriginIsolationStatus` (`IsIsolated`, `Reason`, `Setting`, `EmbedderPolicy`, `SharedPipes`, `SynchronousWorkerCalls`, `BrowserMemoryMeasurement`, `TimerResolution`) ; aucune capacité ; SDK 2.0 (ADR 0146) | [`worker-processes.md`](../dev/worker-processes.md#isolation-cross-origin-adr-0131-adr-0143) |
| Exceptions | `KazoIOException`, `KazoFileNotFoundException`, `KazoDirectoryNotFoundException`, `KazoFileConflictException`, `KazoUnauthorizedAccessException` | [`kazo-file-api.md`](kazo-file-api.md) |

Capacités : selon le chemin (`filesystem.private.*`, `.user-home.*`,
`.system.*`), puis les bits de mode du fichier.

Limites de stockage (ADR 0094 §1, ADR 0099 §2) : `KazoIOException.ErrorCode`
porte le code du moteur, et `ShellErrorText.Describe` donne le message Linux.

| Code (`FileSystemErrorCode`) | Errno | Quand | Message du shell |
|---|---|---|---|
| `NoSpace` (1800) | `ENOSPC` | Le navigateur refuse l'écriture (quota atteint), ou il reste moins que la réserve de root et l'appelant n'est pas administrateur | `No space left on device` |
| `FileTooLarge` (1801) | `EFBIG` | Le contenu dépasse 16 Mio (taille maximale d'un fichier) | `File too large` |
| `TooManyOpenFiles` (1802, SDK 1.2) | `EMFILE` | Le processus a atteint sa limite `nofile` de descripteurs ouverts (ADR 0125, [`process-limits.md`](../dev/process-limits.md)) | `Too many open files` |

### Paramètres (`Kazo.Os.AppSdk.Settings`)

| API | Membres | Capacité | Document |
|---|---|---|---|
| `KazoSettings` | `OpenAsync([context,] documentId)`, `GetAsync<T>`, `SetAsync<T>`, `OpenSystemAsync(documentId)` | `filesystem.private.*` ; documents de l'OS : droits des fichiers | [`settings-files.md`](settings-files.md) |
| `KazoSettingsDocument` | `Get<T>`, `GetValue`, `IsMasked`, `GetSource`, `IsLocked`, `SetAsync`, `SetManyAsync`, `ResetAsync`, `GetSecretAsync`, `SetSecretAsync`, `RefreshAsync`, `Changed`, `Warnings` | — | [`settings-files.md`](settings-files.md), [`sensitive-settings.md`](../dev/sensitive-settings.md) |
| `KazoSettingValue` | `QualifiedKey`, `Value`, `Sensitivity`, `Source`, `IsLocked`, `IsMasked`, `DisplayText` (`••••••••`) | — | [`sensitive-settings.md`](../dev/sensitive-settings.md) |
| `KazoSettingLockedException` | Clé verrouillée dans `[locked]` | — | [`settings-files.md`](settings-files.md) |
| `KazoSettingMaskedException` | Clé non publique dont le fichier privé est illisible (`QualifiedKey`, `PrivatePath`) | — | [`sensitive-settings.md`](../dev/sensitive-settings.md) |

### Trousseau (`Kazo.Os.AppSdk.Keyring`)

| API | Membres | Capacité | Document |
|---|---|---|---|
| `KazoKeyring` | `IsUnlocked`, `GetAsync([context,] id)`, `GetAsync(context, appId, id)`, `SetAsync(id, secret, label)`, `DeleteAsync`, `ListAsync`, `ListAllAsync` | aucune pour ses propres entrées ; `keyring.read-all` (dangereuse) pour celles des autres apps | [`sensitive-settings.md`](../dev/sensitive-settings.md) |
| `KazoKeyringEntry` | `AppId`, `Id`, `Label`, `UpdatedAtUtc` (jamais le secret) | — | [`sensitive-settings.md`](../dev/sensitive-settings.md) |
| `KazoKeyringLockedException` | Trousseau verrouillé | — | [`sensitive-settings.md`](../dev/sensitive-settings.md) |

### Comptes et mots de passe (`Kazo.Os.AppSdk.Administration`)

| API | Membres | Capacité | Document |
|---|---|---|---|
| `KazoUserAccounts` | `ListAsync`, `ChangePasswordAsync([context,] courant, nouveau)`, `ResetPasswordAsync([context,] login, nouveau)`, `RemovePasswordAsync([context,] login)`, `CreateAsync([context,] KazoNewUserAccount)`, `DeleteAsync([context,] login, removeHome)`, `ListPasskeysAsync([context])`, `AddPasskeyAsync([context,] nom)`, `RemovePasskeyAsync([context,] id)`, `ValidateNewPassword`, `ValidateNewAccount`, `MinimumPasswordLength` (8), `MaximumPasswordLength` (256), `MaximumDisplayNameLength` (128) ; types `KazoNewUserAccount`, `KazoPasskey`, `KazoUserAccount.PasskeyCount` | aucune pour lister, changer son propre mot de passe et gérer ses clés d'accès ; `users.manage` (dangereuse) et un administrateur pour un autre compte, l'ajout et la suppression | [`system-administration.md`](system-administration.md), [`passkeys.md`](../dev/passkeys.md) |
| `KazoUserAccount` | `LoginName`, `DisplayName`, `Authority`, `IsEnabled`, `HasPassword`, `UpdatedAtUtc`, `IsCurrentUser`, `IsAdministrator` (jamais le hachage) | — | [`sensitive-settings.md`](../dev/sensitive-settings.md) |
| `IAppUserAccountsGateway` | passerelle derrière `KazoUserAccounts` (`IAppExecutionContext.UserAccounts`, membre par défaut non pris en charge) | — | [ADR 0120](../dev/adr/0120-passwd-and-administrator-password-reset.md) |

### Apps (`Kazo.Os.AppSdk.Apps`)

| API | Membres | Capacité | Document |
|---|---|---|---|
| `KazoApps` | `GetInstalledAsync`, `GetHandlersAsync(path)`, `LaunchAsync(appId, args)`, `OpenFileAsync(path)`, `EditFileAsync(path)`, `RevealFileAsync(path)`, `OpenUriAsync(uri, appId)`, `ShowSettingsAsync(appId)`, `SendIntentAsync(intentId, payloadJson, targetAppId, rememberChoice)`, `GetDefaultHandlersAsync()`, `SetDefaultHandlerAsync(target, appId, action)`, `ResetDefaultHandlerAsync(target, action)` (ADR 0121), `Changed` ; `KazoFileAssociation`, `KazoFileAssociationLayer`, `KazoDefaultHandlerStatus`, `KazoAppInfo` (avec `Capabilities`), `KazoOpenFileResult` (`FellBackToOpen`), `KazoIntentResult` (`KazoIntentStatus`), `KazoAppLaunchException` | lecture et `ShowSettingsAsync` : aucune ; défauts : `file-associations.read` (et `filesystem.user-home.write` pour écrire) ; le reste : `apps.launch` | [`app-sdk.md`](app-sdk.md#catalogue-des-apps-adr-0056-2), [`app-intents.md`](app-intents.md), [`file-associations-api.md`](file-associations-api.md), [`user-file-associations.md`](../dev/user-file-associations.md) |
| `KazoAppIcons` | `LucideName(appId, category)` : icône Lucide d'une app, celle du menu Démarrer et des raccourcis ; SDK 2.0 (ADR 0160) | aucune | [`desktop.md`](../dev/desktop.md) |
| `KazoBrowser` | `OpenAsync(url)`, `OpenDetachedAsync(url)` (fenêtre détachée, ADR 0078) ; `AppId`, `DetachedArgument` (`--detached`) | `apps.launch` | [`detached-display-windows.md`](../dev/detached-display-windows.md), [`display-surface-api.md`](display-surface-api.md) |
| `KazoAppManagement` | `GetAllAsync`, `UninstallAsync`, `ReinstallAsync` ; `KazoInstallScope`, `KazoAppInstallState` | `apps.manage` | [`app-installation.md`](../dev/app-installation.md) |
| `KazoAppPackages` | `GetSourcesAsync`, `GetInstalledAsync` (avec `PreviousVersion`), `GetInfoAsync`, `InstallAsync`, `RemoveAsync`, `RollbackAsync(appId, scope)` et `RepairAsync(system)` (ADR 0214), sources, liste noire, catalogues, mises à jour, `RestartToFinishAsync`, packs de thèmes | `apps.manage` | [`app-installation.md`](../dev/app-installation.md#ajout-sans-redémarrage-signature-et-transactions) |

### Processus et services

| API | Membres | Capacité | Document |
|---|---|---|---|
| `KazoProcess` (`.Diagnostics`) | `GetProcessesAsync`, `GetProcessByIdAsync`, `GetCurrentProcessAsync`, `RefreshAsync`, `GetEnvironmentVariablesAsync`, `GetChildren`, `KillAsync` (`SIGKILL`), `TerminateAsync` (`SIGTERM`, 5 s), `CloseMainWindowAsync` ; état, CPU, mémoire (`WorkingSet64` = `VmRSS`, `PrivateMemorySize64` = `RssAnon`, ADR 0068), E/S, arguments ; `UserProcessorTime`, `PrivilegedProcessorTime`, `TotalProcessorTime`, `Responding` (ADR 0067, [`process-resources-api.md`](process-resources-api.md), [`cpu-accounting.md`](../dev/cpu-accounting.md)) ; `GetResourceLimitsAsync` → `KazoResourceLimits` (`Cpu`, `Rss`, `NoFile`, `NProc`, `Enforcement` : `KazoResourceLimitEnforcement`, `IsCpuMeasured`, `IsMemoryMeasured`), `SetResourceLimitAsync(ResourceLimitKind, soft, hard)` (`getrlimit`/`prlimit`, ADR 0125, SDK 1.2, [`process-limits-api.md`](process-limits-api.md), [`process-limits.md`](../dev/process-limits.md)) ; raisons de fin `ProcessExitReason.CpuLimitExceeded`, `MemoryLimitExceeded` (SDK 1.2, ADR 0125) et `OutOfMemory` d'un processus tué par l'OOM killer (SDK 1.2, [`oom-killer.md`](../dev/oom-killer.md)) | `filesystem.system.read` pour `/proc`, `process.list`, `process.manage` | [`task-manager.md`](../dev/task-manager.md) |
| `KazoProcess.Current` → `KazoCurrentProcess` ; `KazoProcess.CheckpointAsync` (`.Diagnostics`) | `Id`, `StopToken`, `IsStopRequested`, `StopRequest`, `StopRequested` ; point d'arrêt : lève si l'arrêt est demandé, rend la main au-delà de 8 ms, sinon sans allocation | — | [`app-sdk.md`](app-sdk.md#arrêt-propre-et-points-darrêt-adr-0069) |
| `KazoStopRequestedEventArgs`, `KazoProcessKilledException` (`.Diagnostics`) | `Reason`, `RequestedAt`, `Deadline`, `GracePeriod`, `Defer(Task)` ; exception des passerelles d'un processus tué, avec sa `Reason` (`Killed` par défaut, SDK 1.2, ADR 0140) ; elle dérive de `OperationCanceledException` | — | [`app-sdk.md`](app-sdk.md#arrêt-propre-et-points-darrêt-adr-0069) |
| `KazoProcess.Nice`, `BasePriority`, `ParseStatNice` (`.Diagnostics`) ; `IAppProcessGateway.GetNice` / `SetNice`, `ProcessNice` (`Simulation.Abstractions`) ; `IShellSession.RunNicedAsync` | Priorité `nice` (-20..19) d'un processus, lue dans `/proc/<pid>/stat` ; la changer (monter : son processus ; baisser ou autre utilisateur : root) ; lancer une commande avec un ajustement (SDK 1.2, ADR 0127 §1) | `process.manage` hors de son processus et de ses enfants (ADR 0140), `shell.control` | [`app-responsiveness-api.md`](app-responsiveness-api.md), [`process-priority-and-unresponsive-windows.md`](../dev/process-priority-and-unresponsive-windows.md) |
| `ResourceLimitKind`, `ResourceLimit`, `ProcessResourceLimits`, `ResourceLimitChangeResult`, `ProcessLimitExceededException`, `IAppProcessGateway.SetResourceLimitAsync` (`Simulation.Abstractions`, SDK 1.2) | Contrat des limites (ADR 0125) : `ResourceLimit.Unlimited` vaut `long.MaxValue` ; `ProcessLimitExceededException` (`EAGAIN`, « Resource temporarily unavailable ») : refus par l'OS d'un lancement au-delà de `nproc`, vu par l'app comme un échec de lancement | `process.manage` pour changer la limite d'un autre processus | [`process-limits-api.md`](process-limits-api.md), [`process-limits.md`](../dev/process-limits.md) |
| `IAppProcessGateway.KillAsync(pid, reason)` (`Simulation.Abstractions`) | Tuer un processus en attendant l'accusé de l'OS ; utilisé par `KazoProcess.KillAsync`, y compris depuis un Web Worker (méthode par défaut qui retombe sur `Kill`) | `process.manage` | [`worker-processes.md`](../dev/worker-processes.md) |
| `KazoServiceController` (`.ServiceProcess`) | `GetServicesAsync`, `RefreshAsync`, `RefreshStatusAsync`, `StartAsync`, `StopAsync`, `ResetFailedAsync`, `SetStartTypeAsync`, `Status`, `StartType`, `ActiveState`, `Result`, `Health`, `StatusText`, `ActiveStateChangedAt`, `ExitCode` ; `KazoServiceStates.ToSystemdName` (ADR 0097) | `services.manage` sur les autres apps | [`task-manager.md`](../dev/task-manager.md) |

### Tâches en arrière-plan (`Kazo.Os.AppSdk.Threading`, SDK 1.2)

| API | Membres | Capacité | Document |
|---|---|---|---|
| `KazoBackground` | `RunAsync<TInput, TResult>(job, input, progress, options, cancellationToken)` pour une méthode statique (`Func<TInput, KazoBackgroundContext, Task<TResult>>` ou `TResult`), `RunAsync<TInput>` sans résultat (`Func<…, Task>` ou `Action<…>`), `RunAsync<TJob, TInput, TResult>(input, …)` pour une classe de tâche | — | [`background-jobs.md`](background-jobs.md) |
| `KazoBackgroundJob<TInput, TResult>` | Classe de tâche : `RunAsync(input, context)` ; créée par son constructeur sans paramètre dans le processus de la tâche | — | [`background-jobs.md`](background-jobs.md) |
| `KazoBackgroundContext` | `ProcessId`, `CancellationToken`, `Report(fraction, message)` (au plus 10 par seconde, la dernière l'emporte), `CheckpointAsync()` | — | [`background-jobs.md`](background-jobs.md) |
| `KazoBackgroundProgress`, `KazoBackgroundOptions` | `Fraction`, `Message`, `ToString()` (`42% message`) ; `Name` (affiché dans `ps`), `Nice` | — | [`background-jobs.md`](background-jobs.md), [`app-responsiveness-api.md`](app-responsiveness-api.md) |
| `KazoBackgroundJobException` | Exception de la tâche : `OriginalTypeName`, `Message`, `OriginalStackTrace` | — | [`background-jobs.md`](background-jobs.md) |
| `IAppBackgroundGateway`, `KazoBackgroundJobRequest`, `KazoBackgroundJobTarget` | Contrat entre le SDK et l'OS : cible nommée (assembly, type, méthode, type d'entrée), entrée JSON, nom, `nice` | — | [`background-jobs.md`](background-jobs.md) |
| Analyseur `KOSBG0001` à `KOSBG0004` | Livré dans le paquet `Kazo.Os.AppSdk` : groupe de méthode statique, types déclarés par `KazoWireContext`, assembly de l'app, pas de générique | — | [`background-jobs.md`](background-jobs.md#règles-vérifiées-au-build) |

### Réseau (`Kazo.Os.AppSdk.Net`)

| API | Membres | Document |
|---|---|---|
| `KazoDns` | `GetHostAddressesAsync`, `GetHostEntryAsync` | [`sdk-network.md`](sdk-network.md) |
| `KazoPing` | `SendAsync` → `KazoPingReply(Address, Status, RoundtripTime, Ttl)` | [`sdk-network.md`](sdk-network.md) |
| `KazoHttpClient` | `GetAsync`, `GetStringAsync`, `GetByteArrayAsync`, `GetFromJsonAsync`, `PostAsync` (formulaire ou contenu), `PostAsJsonAsync`, `PutAsync`, `PatchAsync`, `DeleteAsync`, `SendAsync`, `DefaultRequestHeaders`, `Cookies` (`KazoCookieContainer` : `Add`, `Remove`, `GetCookies`, `GetAllCookies`, `Clear`) ; `KazoHttpResponse` (`ContentType`, `MediaType`, `Content`, `ContentBytes`, `GetHeader`, `ReadFromJson`), `KazoHttpRequestException` | [`sdk-network.md`](sdk-network.md), [`http-bodies.md`](http-bodies.md) |
| `KazoHttpContent` | `KazoStringContent`, `KazoByteArrayContent`, `KazoFormUrlEncodedContent`, `KazoJsonContent` ; `ContentType`, `MediaType`, `CharSet`, `Length`, `ReadAsString`, `ReadAsByteArray` | [`http-bodies.md`](http-bodies.md) |
| `KazoTcpClient` | `ProbeAsync` (ports), `DetectOperatingSystemAsync`, `ConnectAsync` → `KazoNetworkStream` | [`sdk-network.md`](sdk-network.md) |
| `KazoNetworkStream` | `ReadAsync`, `WriteAsync`, `RemoteEndPoint` (flux TCP relayé) | [`server-network-relay.md`](../dev/server-network-relay.md) |
| `KazoUdpClient` | `ConnectAsync`, `SendAsync`, `ReceiveAsync` → `KazoUdpReceiveResult` | [`server-network-relay.md`](../dev/server-network-relay.md) |
| `KazoHttpListener` | `Prefixes`, `StartAsync`, `GetContextAsync`, `StopAsync`, `Close` ; `KazoHttpListenerContext` (`Request`, `Response`), `KazoHttpListenerRequest` (`HttpMethod`, `Url`, `Headers`, `QueryString`, `Cookies`, `Body`, `ReadForm`, `RemoteEndPoint`), `KazoHttpListenerResponse` (`StatusCode`, `ContentType`, `Headers`, `OutputStream`, `RedirectLocation`, `Close`) | [`listening-ports-and-dns.md`](listening-ports-and-dns.md) |
| `KazoIPGlobalProperties` | `GetActiveTcpListenersAsync` → `KazoTcpListenerInfo`, `GetActiveTcpListenerEndPointsAsync` | [`listening-ports-and-dns.md`](listening-ports-and-dns.md) |
| `KazoWebSite`, `KazoWebSiteFile`, `KazoWebSiteDeclaration`, `KazoWebSiteScope` (`.Net.Hosting`) | Fichiers `.site` des sites statiques : `SystemSitesDirectory`, `GetUserSitesDirectory`, `Parse`, `Validate`, `Serialize`, `TryParseBinding`, `ReadAllAsync` | [`static-sites.md`](../dev/static-sites.md), [`static-site-files.md`](static-site-files.md) |
| `WebServerAppBase` (`Kazo.Os.AppSdk`) | `Urls`, `ProcessRequestAsync`, `OnListeningAsync` : base d'une app web (`AppKind.Web`) | [`listening-ports-and-dns.md`](listening-ports-and-dns.md) |

Capacités : `network.simulated.*` ; le repli sur le vrai réseau est décidé par
l'OS et exige `network.real.access` ; écouter hors de ses `Urls` exige
`network.listen` (et `network.listen.privileged` sous 1024).

### Presse-papiers (`Kazo.Os.AppSdk.Clipboard`)

`KazoClipboard` : `GetTextAsync`, `SetTextAsync`, `ContainsTextAsync`,
`GetFileDropListAsync`, `SetFileDropListAsync` (copier ou couper des
fichiers), `ContainsFileDropListAsync`, `ClearAsync`, `Changed`. Capacités
`clipboard.read` et `clipboard.write`. Le texte est partagé avec la vraie
machine selon `/etc/kazoos/clipboard.config`. Voir [`file-explorer.md`](../dev/file-explorer.md)
et [`clipboard.md`](../dev/clipboard.md), [`clipboard-api.md`](clipboard-api.md).

### Accessibilité (`Kazo.Os.AppSdk.Accessibility`)

`KazoAccessibility` : `GetPreferencesAsync()` → `KazoAccessibilityPreferences(HighContrast,
TextScale, ReducedMotion)`, déjà résolues (`auto` suit l'hôte) ; `PreferencesChanged`. Aucune
capacité. `KazoAccessibilityPreferences.Scale(pixels)` agrandit une taille en pixels ;
`TextScales` liste `1.0`, `1.25`, `1.5`, `2.0`. Le shell applique déjà les préférences à tout ce
qui est dessiné en HTML avec les jetons ; une app qui dessine elle-même (canvas, terminal, éditeur)
les lit ici. Voir [`accessibility.md`](../dev/accessibility.md).

### Nouveautés (`Kazo.Os.AppSdk.WhatsNew`)

| API | Membres | Capacité |
|---|---|---|
| `KazoWhatsNew` | `IsSupported`, `IsSupportedFor(context)`, `GetAsync` → `KazoChangeList(Groups, NewCount)` (`KazoChangeGroup(Version, Since, Date, Entries)`, `KazoChangeEntry(Id, Category, Title, Description, PullRequestUrl, Links, IsNew)`, `KazoChangeCategory`), `MarkSeenAsync(ids)`, `WatchAsync` : liste des changements de l'OS dans la langue de l'app, et ce que l'utilisateur de la session a déjà vu (ADR 0186, [`change-list.md`](../dev/change-list.md)). Sans liste servie, la liste est vide. | aucune |

### Composants intégrables (`Kazo.Os.AppSdk.Components`)

| Type | Rôle | Document |
|---|---|---|
| `KazoComponentContracts` | Noms des contrats : `Terminal` (`kazoos.terminal`) ; réservés à l'OS (ADR 0148 ; l'ADR 0156 ouvre les deux premiers à une app tierce choisie par un administrateur) : `FileView` (`kazoos.file-view`), `FileDialogs` (`kazoos.file-dialogs`), `ContextMenu` (`kazoos.context-menu`). | [`embedded-components.md`](embedded-components.md) |
| `IKazoTerminal`, `KazoTerminalOptions`, `KazoTerminalCommandResult`, `KazoTerminalOutputEventArgs` | Piloter un terminal embarqué : `RunCommandAsync`, `SetWorkingDirectoryAsync`, `SendInputAsync`, `ReadOutput`, `ClearAsync`, `FocusAsync`, `WorkingDirectory`, `Title`, `IsRunning`, `IsBusy`, `OutputReceived`, `CommandCompleted`, `StateChanged`, `Exited`. | [`embedded-components.md`](embedded-components.md) |

### Shell (`Kazo.Os.AppSdk.Shell` et `Kazo.Os.AppSdk`)

| API | Rôle | Capacité | Document |
|---|---|---|---|
| `KazoShell.StartAsync(options)` → `IKazoShell` | Démarrer un `hsh` : `ExecuteAsync`, `CompleteAsync`, `WorkingDirectory`, `ExitStatus`, `Prompt`, `HasExited`, historique, `GetInstalledCommands`, `NeedsMoreInput` (ADR 0064, invite `>`) | `shell.host` | [`shell-host.md`](shell-host.md) |
| `KazoShellOptions`, `KazoShellExecution`, `KazoShellCompletion`, `KazoCredentialPrompt` | Options, flux d'une exécution, complétion, saisie des mots de passe | — | [`shell-host.md`](shell-host.md), [`terminal-console.md`](../dev/terminal-console.md) |
| `IShellSession` (`TerminalExecutionContext.Shell`) | Pour une commande : `WorkingDirectory`, `Environment`, `History`, `ChangeDirectoryAsync`, `SetVariable`, `UnsetVariable`, `GetCommands` (dans la langue du shell ; `GetCommands(culture)` dans une autre, ADR 0104), `Exit` ; ADR 0064 : `GetVariable`, `AssignVariable`, `RunAsync`, `Jobs` (`ShellJobInfo`), `KillJob` | `shell.control` | [`shell-commands.md`](../dev/shell-commands.md) |
| `IFullScreenTerminalSession` (`TerminalExecutionContext.FullScreen`) | Écran alternatif : `Viewport`, `EnterAlternateScreenAsync`, `RenderAsync`, `ReadKeyAsync`, `LeaveAlternateScreenAsync` | — | [`terminal-emulator.md`](../dev/terminal-emulator.md), [`terminal-api.md`](terminal-api.md) |
| `ShellErrorText` | Message court d'une erreur de fichier, pour une commande | — | [`app-sdk.md`](app-sdk.md) |

### Administration (`Kazo.Os.AppSdk.Administration`)

| API | Membres | Capacité |
|---|---|---|
| `KazoPermissionAdministration` | `GetPolicyAsync`, `SetPolicyAsync`, `GetOverviewAsync`, `SetMachineDecisionAsync`, `RemoveOwnDenialAsync` | `permissions.manage` |
| `KazoComponentProviders` | `GetOverviewAsync` (`KazoComponentContractInfo`, `KazoComponentProviderInfo`), `SetProviderAsync(contracts, appId)` : app qui fournit le contrôle de fichiers et ses dialogues pour toute la machine, `null` = explorateur ([`embedded-components.md`](embedded-components.md), ADR 0156) | `apps.manage` + administrateur (fenêtre d'approbation pour une app non système) |
| `KazoFileMenuEntries` | `GetEntriesAsync` (`KazoFileMenuEntryInfo`), `SetHiddenAsync(appId, name, hidden)` : entrées que les apps ajoutent aux menus des fichiers, masquées pour l'utilisateur de la session dans `~/.config/kazoos/context-menus.config` ([`context-menu.md`](context-menu.md), ADR 0159) | `apps.manage` |
| `KazoServerConnection` | `GetStateAsync`, `ConnectAsync`, `CreateAccountAsync`, `PairAsync`, `TestAsync` (`KazoServerProbe`, ADR 0191), `DisconnectAsync`, `SyncAsync`, `GetConflictsAsync`, `GetRelayPolicyAsync`, `ShowConflictsAsync`, `GetSyncStatusAsync` (`KazoSyncStatus`), `SetSyncPausedAsync`, `SyncNowAsync` ([`synchronization.md`](../dev/synchronization.md)) | `server.connection.manage` |
| `KazoSystem` | `RebootAsync`, `FactoryResetAsync` | `system.power` + administrateur |
| `KazoStorageDiagnostics` | `GetSnapshotAsync`, `GetWebSiteDatabasesAsync`, `DeleteWebSiteDatabaseAsync` (bases `kazoweb.*` des sites, ADR 0088 §4) | `system.storage.inspect` |
| `KazoScreens` | `IsSupported`, `IsSupportedFor(context)`, `GetState` (`KazoScreensState`, `KazoScreenInfo`), `WatchAsync`, `OpenScreenAsync` → `KazoScreenOpenStatus`, `RestoreScreensAsync`, `CloseScreenAsync(number)`, `IdentifyAsync`, `ToggleFullScreenAsync`, `RequestPermissionAsync` → `KazoScreenPermission` ; ouvrir, plein écran et permission seulement pendant un clic (ADR 0184, [`screens-settings.md`](../dev/screens-settings.md)) | `screens.manage` |
| `KazoSystemBackup` | `IsSupported`, `IsSupportedFor(context)`, `CreateAsync` → `KazoBackupInfo(FileName, SizeBytes, RecordCount, CreatedAtUtc)`, `RestoreAsync(KazoBackupRestoreMode.Merge \| Replace)` → `KazoBackupInfo?` (`null` : sélecteur fermé) ; `KazoBackupInvalidException` pour une archive refusée (ADR 0099 §1, [`system-backup-api.md`](system-backup-api.md) ; mise en œuvre : [`system-backup.md`](../dev/system-backup.md)) | `system.backup` + administrateur |

Détails : [`system-administration.md`](system-administration.md),
[`admin-authentication-agent.md`](../dev/admin-authentication-agent.md).

## Points d'entrée

| Classe | Projet | À surcharger | Document |
|---|---|---|---|
| `WindowAppBase` | `AppSdk.Blazor` | `OnAppInitialized[Async]`, `OnAppParametersSet[Async]`, `OnAppAfterRender[Async]`, `ShouldRenderApp`, `OnPermissionErrorRaised`, `OnAppStopRequestedAsync` (ADR 0069 : la fenêtre se ferme après la tâche renvoyée), crochets de fenêtre `OnWindowActivated[Async]`, `OnWindowDeactivated[Async]`, `OnWindowStateChanged[Async]`, `OnWindowResized[Async]`, état `IsWindowActive`, `WindowState`, `WindowSize`, `TrackDisplaySurface` (ADR 0095, [`window-model.md`](window-model.md)), titre `WindowTitle`, `SetWindowTitleAsync(title)`, `FormatWindowTitle(document, modified)` (SDK 1.1, ADR 0117/0123, [`window-model.md`](window-model.md) §4), fenêtre `WindowOptions`, `MinimizeWindowAsync`, `MaximizeWindowAsync`, `RestoreWindowAsync`, `MoveWindowAsync`, `ResizeWindowAsync`, `SetWindowStyleAsync`, `SetWindowOptionsAsync` (SDK 3.1, ADR 0234 §4, #384, [`window-model.md`](window-model.md) §6), fenêtres enfants `ShowWindowAsync<T>`, `ShowDialogAsync<T>` (SDK 3.1, ADR 0234 §3, [`window-model.md`](window-model.md) §7), `OnIntentReceivedAsync(AppIntentDelivery)` (ADR 0096 §5) ; fournit `AppContext`, `Manifest`, `LaunchArguments`, `LaunchIntent`, `LaunchFile`, `SendIntentAsync` (affiche le sélecteur si plusieurs apps répondent), `ChooseAppAsync`, `OpenFileAsync`, `SaveFileAsync`, `SelectFolderAsync`, `MessageBox`, `TextInput` | [guide §3](sdk-developer-guide.md#3-une-app-fenêtrée) |
| `KazoComponentBase` | `AppSdk.Blazor` | Composant enfant d'une fenêtre qui appelle `Kazo*` | [`kazo-file-api.md`](kazo-file-api.md) |
| `TerminalAppBase` | `AppSdk` | `ExecuteAsync(TerminalExecutionContext, CancellationToken)` → code de sortie ; le contexte donne `Arguments`, `StandardInput/Output/Error`, `WorkingDirectory`, `Environment`, `App`, `Shell`, `FullScreen`, et `ReadSecretLineAsync(invite)` qui lit une ligne écho coupé comme `getpass(3)` quand l'entrée est un terminal (`ITerminalInputMode.EchoEnabled`, ADR 0120) | [guide §4](sdk-developer-guide.md#4-une-commande-du-shell) |
| `WebAppDefinition` | `AppSdk.Web` | Classe `[AppManifest]` `partial` d'un site : `Urls`, `Configure(KazoWebApplicationBuilder)` (services, middlewares, `MapControllers`, `MapGet`...) ; le build génère l'`EntryPoint` (`<Nom>WebHost : KazoWebHost`) et la table de routes des contrôleurs (`KazoController`, `KazoApiController`) | [`asp-kazo-mvc.md`](asp-kazo-mvc.md) |
| `ServiceAppBase` | `AppSdk` | `OnStartAsync(context, token)` (le service est `activating` jusqu'à son retour, puis `active`, ADR 0107), `RunCoreAsync(context, sessionCancellationToken)`, `ReportHealth(KazoServiceHealth, status)` (santé et texte de `systemctl status`, ADR 0097 §2), `OnStoppingAsync(ServiceStopReason, token)` (appelé une fois, dès `StopRequested` ; `ServiceStopReason.Terminated` pour `kill`, « End task », `systemctl stop`), `OnIntentReceivedAsync(AppIntentDelivery, token)` (ADR 0096 §5) | [guide §5](sdk-developer-guide.md#5-un-service) |

Interfaces qu'un composant fenêtré peut implémenter :

| Interface | Rôle |
|---|---|
| `IWindowCloseGuard` | `ConfirmCloseAsync` : autoriser ou refuser la fermeture (modifications non enregistrées). |
| `IAppBackHandler` | `CanNavigateBack`, `CanNavigateBackChanged`, `NavigateBackAsync` → `AppBackResult`. Reçoit le bouton ◁ mobile, le bouton Retour de la barre de titre (avec `SupportsBack => true`), et sans rien déclarer le bouton Précédent de la souris, Alt+← et le Retour du téléphone ([ADR 0220](../dev/adr/0220-mouse-back-forward-buttons.md)). `AppBackRequest.Source` : `SystemNavigationBar`, `DesktopChromeButton`, `KeyboardShortcut`, `MouseButton`, `SystemBack`. |
| `IAppForwardHandler` | `CanNavigateForward`, `CanNavigateForwardChanged`, `NavigateForwardAsync(AppForwardRequest)` → `AppBackResult` : bouton Suivant de la souris, Alt+→, touche Browser Forward ([ADR 0220](../dev/adr/0220-mouse-back-forward-buttons.md)). |

## Sites web (ASP.Kazo)

Pour apprendre : [guide du développeur ASP.Kazo](asp-kazo-developer-guide.md),
qui suit l'exemple `Apps/Samples/Kazo.Os.Samples.Intranet`.

### Démarrage (`Kazo.Os.AppSdk.Web.Hosting`)

| Type | Membres | Document |
|---|---|---|
| `WebAppDefinition` | `Urls`, `BlazorRoot`, `Capabilities` (jamais `filesystem.*`, `KOSAPP0050`), `Configure(builder)` ; `Kind` `Web`, `Category` `internet`, `LaunchVisibility` `Hidden` | [`asp-kazo-mvc.md`](asp-kazo-mvc.md) |
| `KazoWebApplicationBuilder` | `Services`, `Json` (`SerializerOptions`, camelCase), `Mvc` (`Filters`), `Antiforgery`, `Logging` (`EnableDebugLog(level)`, `LogRequests`), `Build()` ; `ILogger<T>` injectable | [`asp-kazo-mvc.md`](asp-kazo-mvc.md), [`asp-kazo-site-data.md`](asp-kazo-site-data.md#journal-de-débogage) |
| `KazoWebApplication` | `Use`, `UseMiddleware<T>`, `UseExceptionPage`, `UseStatusCodePages`, `UseCookieSession`, `UseStaticFiles(configure)`, `Map`, `MapMethods`, `MapGet`/`MapPost`/`MapPut`/`MapPatch`/`MapDelete`, `MapFallback`, `MapControllers`, `MapControllerRoute`, `MapDefaultControllerRoute`, `ProcessAsync(KazoWebRequestData)`, `UnhandledException` | [`asp-kazo-mvc.md`](asp-kazo-mvc.md) |
| `KazoWebHost` | Classe d'entrée générée (`<Définition>WebHost`) ; `CreateApplication(definition, routes, urls, views)` construit le site sans OS (tests) | [guide §9](asp-kazo-developer-guide.md#9-tester-un-site) |
| `KazoStaticFileOptions` | `Assembly`, `ResourcePrefix`, `RequestPath`, `DefaultFileNames`, `CacheControl` du `wwwroot` embarqué | [`asp-kazo-razor-views.md`](asp-kazo-razor-views.md) |
| `KazoBlazorSiteEndpoints` | Page hôte, `blazor.kazo.js` et `blazor.boot.json` d'un site à `BlazorRoot` ; `IsHostPage(html)` | [`blazor-sites.md`](blazor-sites.md) |

### Requête et réponse (`.Http`, `.Sessions`)

| Type | Membres |
|---|---|
| `KazoHttpContext` | `Request`, `Response`, `RequestServices` (portée de la requête), `Session`, `HasSession`, `Items`, `Endpoint`, `RequestAborted`, `TraceIdentifier` |
| `KazoHttpRequest` | `Method`, `Path`, `PathAndQuery`, `Query`, `Form`, `Headers`, `Cookies`, `ContentType`, `RouteValues`, `ReadBodyAsString()` |
| `KazoHttpResponse` | `StatusCode`, `Headers`, `ContentType`, `Cookies`, `WriteAsync` |
| `KazoWebRequestData`, `KazoWebResponseData` | Échange indépendant du transport (`Create(method, url, ...)`, `ReadAsString()`) |
| `KazoSession` | `GetString`/`SetString`, `GetInt32`/`SetInt32`, `TryGetValue`, `Set`, `Remove`, `Clear`, `Keys`, `Id`, `IsNew` ; cookie `.KazoSession`, en mémoire du site |

### Contrôleurs (`.Mvc`)

| Type | Rôle |
|---|---|
| `KazoControllerBase` | `HttpContext`, `Request`, `Response`, `RouteValues`, `ModelState`, `Url` ; résultats `Ok`, `NoContent`, `StatusCode`, `Content`, `File`, `Redirect`, `RedirectPermanent`, `LocalRedirect`, `RedirectToAction`, `Created`, `CreatedAtAction`, `NotFound`, `BadRequest`, `Unauthorized`, `Forbid`, `Conflict`, `Problem`, `ValidationProblem` ; `TryValidateModel(model, prefix)` (ADR 0087 §3) |
| `KazoController` | Pages : `View`, `PartialView`, `View<TView>`, `ViewData`, `Json`, `OnActionExecuting`/`OnActionExecuted` |
| `KazoApiController` | API : corps JSON par défaut, `400` automatique, problèmes RFC 9457 ; route d'attribut obligatoire (`KOSWEB0007`) |
| Attributs | `[Route]`, `[HttpGet]`/`[HttpPost]`/`[HttpPut]`/`[HttpPatch]`/`[HttpDelete]` (`Name`, `Order`), `[ActionName]`, `[NonAction]`, `[NonController]`, `[FromRoute]`, `[FromQuery]`, `[FromForm]`, `[FromHeader]`, `[FromBody]`, `[FromServices]` |
| Filtres | `IKazoActionFilter`, `IKazoAsyncActionFilter`, `KazoActionFilterAttribute`, `KazoMvcOptions.Filters` |
| `KazoResults` | Résultats des routes minimales : `Ok`, `Text`, `Json` (avec `JsonTypeInfo`), `StatusCode`, `NoContent`, `NotFound`, `BadRequest`, `Created`, `Redirect`, `File`, `Problem` |
| `IKazoUrlHelper` | `Action`, `RouteUrl`, `Content("~/...")`, `IsLocalUrl` |

### Vues et anti-falsification (`.Views`, `.Antiforgery`)

| Type | Rôle | Document |
|---|---|---|
| `KazoView`, `KazoView<TModel>`, `KazoLayout` | Bases des vues `.razor` : `Model`, `ViewData` (`Title`), `ViewContext`, `HttpContext`, `Request`, `ModelState`, `Url`, `Layout` ; `Body` pour un layout | [`asp-kazo-razor-views.md`](asp-kazo-razor-views.md) |
| `KazoLink`, `KazoForm`, `KazoAntiforgeryToken`, `KazoValidationMessage`, `KazoValidationSummary`, `KazoSection`, `KazoRenderSection`, `KazoPartial`, `KazoRoute.Values` | Helpers des vues `.razor` | [`asp-kazo-razor-views.md`](asp-kazo-razor-views.md) |
| `KazoRazorPage<TModel>` | Base des vues `.cshtml` : `Model`, `ViewData`, `Context`, `Layout`, `RenderBody`, `RenderSection(Async)`, `IsSectionDefined` ; `Html` (`ActionLink`, `BeginForm`, `TextBoxFor`, `LabelFor`, `ValidationMessageFor`, `ValidationSummary`, `PartialAsync`, `Raw`...), `Json.Serialize`, `Component.InvokeAsync<T>`, `Url` | [`asp-kazo-cshtml-views.md`](asp-kazo-cshtml-views.md) |
| `[ValidateAntiForgeryToken]`, `[IgnoreAntiforgeryToken]`, `KazoAntiforgery` | Jeton de formulaire (`__RequestVerificationToken` ou en-tête `RequestVerificationToken`) et cookie `.KazoAntiforgery` | [`asp-kazo-razor-views.md`](asp-kazo-razor-views.md) |

Une vue est rendue en HTML statique dans l'iframe du navigateur : pas de CSS
scopé ni d'événement Blazor ; le CSS et le JavaScript viennent du `wwwroot`.

### Données et journal (`.Data`, `.Logging`)

| Type | Rôle |
|---|---|
| `AddKazoWebDb(services, schema)` | Enregistre `KazoWebDb`, la base `kazoweb.<appId>` du site, avec ses versions ; en mémoire (`kazoweb.in-memory`) hors de l'OS |
| `KazoWebDbSchemaBuilder`, `KazoWebDbVersionBuilder` | `Version(n, v => ...)` ; `CreateStore(name, keyPath, autoIncrement)`, `DeleteStore`, `CreateIndex(store, name, keyPath, unique, multiEntry)`, `DeleteIndex` |
| `KazoWebDb` | `Name`, `Version`, `Store<T>(name)`, `BeginTransaction(stores)`, `OpenAsync` |
| `KazoWebDbStore<T>`, `KazoWebDbIndex<T>` | `GetAsync`, `GetAllAsync`, `GetAllKeysAsync`, `CountAsync` ; magasin : `AddAsync`, `PutAsync`, `DeleteAsync`, `ClearAsync`, `Index(name)` |
| `KazoWebDbTransaction`, `KazoWebDbTransactionStore<T>` | Lot d'écritures (`Add`, `Put`, `Delete`, `Clear`) envoyé par `CommitAsync`, tout ou rien |
| `KazoWebDbKey`, `KazoWebDbKeyRange` | Clés (nombre, chaîne, `Array(...)`) et plages (`Only`, `LowerBound`, `UpperBound`, `Bound`) |
| `KazoWebDbException` (`Kazo.Os.AppSdk.WebData`) | `Name` d'une `DOMException` IndexedDB : `ConstraintError`, `NotFoundError`, `DataError`, `VersionError`… |
| `KazoWebDbInMemoryBackend` (`Kazo.Os.AppSdk.WebData`) | Passerelle en mémoire pour les tests |
| `KazoWebLoggingOptions`, `IKazoWebDebugLogSink`, `KazoWebDebugLogMemorySink` | Journal de débogage `/var/log/kazoweb/<appId>.log` ; récepteur de test |

Guide : [`asp-kazo-site-data.md`](asp-kazo-site-data.md).

## Composants et services Blazor

### Dialogues (`Kazo.Os.AppSdk.Blazor`)

| Type | Champs |
|---|---|
| `OpenFileDialogRequest` → `OpenFileDialogResult` | `Title`, `InitialDirectory`, `Filters` (`FileDialogFilter(DisplayName, Extensions, MediaTypes)`), `AllowMultiple`, `RequestedAccess` (`Read`, `ReadWrite`) ; résultat `Status`, `Resources` |
| `SaveFileDialogRequest` → `SaveFileDialogResult` | `Title`, `InitialDirectory`, `SuggestedFileName`, `DefaultExtension`, `Filters`, `ConfirmOverwrite` ; résultat `Status`, `Resource` |
| `SelectFolderDialogRequest` → `SelectFolderDialogResult` | `Title`, `InitialDirectory`, `AllowCreateFolder` (bouton « Nouveau dossier » dans la barre du dialogue, ADR 0175) |
| `MessageBoxDialogRequest` → `MessageBoxDialogResult` | `Title`, `Content`, `DialogType` (`MessageBoxType`), `Icon` (`MessageBoxIcon` : `None`, `Information`, `Warning`, `Error`, `Question` ; prochaine version du SDK) ; résultat `MessageBoxResult` |
| `TextInputDialogRequest` → `TextInputDialogResult` | `Title`, `Content`, `DefaultValue`, `Placeholder` ; résultat `Status` (`TextInputStatus`) et `Value` |
| `AppChooserDialogRequest` → `AppChooserDialogResult` | Sélecteur « Ouvrir avec » (ADR 0096) : `Title`, `Content`, `Candidates` (`AppChooserOption(AppId, Name, Description)`), `OfferAlwaysUse` ; résultat `Status` (`AppChooserStatus`), `AppId`, `AlwaysUse` |
| `OpenWithDialogRequest`, `FilePropertiesDialogRequest`, `FileConflictDialogRequest` (`.Files`) | Dialogues fournis par l'explorateur (`kazoos.file-dialogs`, ADR 0155 §5) : « Ouvrir avec » (`CandidateAppIds` ; résultat `Status`, `AppId`, `AlwaysUse`), propriétés (résultat `Closed`), conflit de noms (`OfferApplyToAll` ; résultat `Replace`/`KeepBoth`/`Skip` et `ApplyToAll`). Sans fournisseur : `Unavailable` (ou `Skip`). |
| `IDialogService`, `IFileDialogService` | Services injectés dans `WindowAppBase` (`Dialogs`, `FileDialogs`) ; `CredentialPromptAsync`, `ChooseAppAsync`, `OpenWithAsync`, `ShowFilePropertiesAsync` et `ResolveFileConflictAsync` en plus. Les dialogues Ouvrir / Enregistrer / Choisir un dossier montrent le contrôle de fichiers de l'OS (`kazoos.file-view`) avec les droits de l'app, ou la liste de la plateforme sans fournisseur (ADR 0157) |

Le dossier de départ (`InitialDirectory`) des dialogues Ouvrir, Enregistrer et Choisir un dossier : celui de l'app si l'app peut le lister, sinon le dossier personnel de l'utilisateur (`/home/<nom>`), sinon `/`. Un dossier absent ou illisible ne fait plus tomber l'OS (issue #385).

`FileDialogStatus.Selected` indique un choix ; tout autre statut est une
annulation.

### Autres composants de `Kazo.Os.AppSdk.Blazor`

| Type | Rôle | Document |
|---|---|---|
| `KazoLineChart` (`.Charts`) | Graphique SVG : `Series` (`KazoChartSeries(Name, Values)`), `Title`, `Capacity`, `Maximum`, `MinimumScale`, `ValueFormatter`, `Filled`, `ShowLegend`, `TimeSpanLabel`, `Size`, `Class` ; couleurs `--kos-chart-series-0..7` | [`task-manager.md`](../dev/task-manager.md) |
| `SettingOriginBadge` (`.Settings`) | Badge d'origine d'un paramètre (défaut, système, utilisateur, verrouillé) | [`settings-files.md`](settings-files.md) |
| `BrowserFileDownloader` (`.Interop`) | `DownloadFileFromStreamAsync(fileName, stream)` : télécharger vers le vrai disque | [`file-explorer.md`](../dev/file-explorer.md) |
| `BrowserFilePicker` (`.Interop`) | `PickAsync(accept)` → `BrowserPickedFile(Name, Content)` ou `null` : ouvrir un fichier du vrai disque ; seulement juste après un clic ou une touche (sinon `InvalidOperationException`) | [`system-backup.md`](../dev/system-backup.md) |
| `IDisplaySurfaceService`, `IDisplaySurface`, `DisplaySurfaceContent` (`.Display`) | Fenêtres détachées du navigateur hôte (ADR 0078) : `OpenAsync(DisplaySurfaceOptions)` → `DisplaySurfaceOpenResult` (`Opened`, `Blocked`, `Unavailable`), `Surfaces`, `SurfacesChanged`, `CloseAllAsync` ; surface `Id`, `Title`, `Width`, `Height`, `HasFocus`, `State`, `Changed`, `SetTitleAsync`, `FocusAsync`, `CloseAsync` ; `<DisplaySurfaceContent Surface="…">` y dessine son contenu (`Surface` facultatif : sans surface ouverte, le contenu reste en place) | [`detached-display-windows.md`](../dev/detached-display-windows.md), [`display-surface-api.md`](display-surface-api.md) |
| `IAppWindowGateway` | Fenêtre d'une app vue par le SDK (SDK 1.1, ADR 0123) : `Title`, `DefaultTitle`, `SetTitleAsync` ; depuis le SDK 3.1 (ADR 0234 §4) `Options`, `SetOptionsAsync`, `MinimizeAsync`, `MaximizeAsync`, `RestoreAsync`, `MoveAsync`, `ResizeAsync`, membres par défaut qui renvoient `false` pour une plateforme plus ancienne. Implémentée par la plateforme et donnée à `WindowAppBase` par valeur en cascade ; une app passe par `SetWindowTitleAsync` | [`window-model.md`](window-model.md) |
| `KazoWindowBase`, `KazoWindowRequest`, `KazoDialogResult`, `IKazoWindow`, `IAppChildWindowGateway` | Fenêtres enfants et dialogues d'une app (SDK 3.1, ADR 0234 §3) : composant de la fenêtre (`CloseAsync(result)`, méthodes de fenêtre), demande (titre, options, paramètres), résultat façon `DialogResult`, poignée (`Closed`, `CloseAsync`), passerelle de la plateforme | [`window-model.md`](window-model.md) §7 |
| `KazoContextMenu`, `KazoMenuItem`, `KazoMenuGroup`, `KazoMenus` (`Menus`), `KazoMenuBuilder`, `KazoMenuDefinition`, `KazoMenuOpeningEventArgs`, `IKazoContextMenu`, `IKazoMenuHost` (`.ContextMenus`) | Menu contextuel de l'OS (ADR 0150, ADR 0154) : déclaré dans le markup ou en C# (`Menus.Define`), attaché à n'importe quel élément (`@attributes="@(Menus.Attach(nom, objet))"`), ouvert depuis le code (`Menus.OpenAsync`) ; groupes, trois niveaux, `Opening` pour l'objet attaché ; dessiné par la plateforme. `Menus` existe sur `WindowAppBase` et `KazoComponentBase`. Icônes : `KazoIcons.Vector`/`FromImage` (`Kazo.Os.AppSdk.Icons`) | [`context-menu.md`](context-menu.md) |
| `RegisterFileContextMenuAttribute`, `RegisterFileContextMenuContext` (`.Files`), `MenuSelection`, `KazoFileMenuIntent` (`Kazo.Os.AppSdk.Files`), `KazoFileMenuContext`, `KazoMenuDefinition.FileMenu` | Entrées d'une app dans les menus des fichiers (ADR 0151, ADR 0159) : méthode statique `[RegisterFileContextMenu]` de la classe `[AppManifest]` qui appelle `Register(title, name, target, intent, icon, scope, types, selection, first, after, afterGroup, parent)`, exécutée au build ; capacité `shell.context-menu` ; la cible lit les chemins de l'intent par `KazoFileMenuIntent.ReadPaths`. Un fournisseur du contrôle de fichiers pose `FileMenu` pour que l'OS ajoute ces entrées | [`context-menu.md`](context-menu.md#entrées-des-apps-dans-les-menus-des-fichiers-chantier-4) |
| `KazoEmbeddedComponent<TController>` (`.Components`) | Affiche le composant d'une autre app pour un contrat : `Contract`, `Options`, `Parameters`, `ControllerChanged`, `Unavailable` ; `KazoEmbeddableComponent` est la base du composant fourni (`Options`, `ControllerReady`, `HostContext`, `UnmatchedParameters`) ; `KazoContractComponent<TController>` est la base des composants typés (`AddProviderParameters`, `Controller`, `DefaultUnavailable`, ADR 0153, ADR 0154) | [`embedded-components.md`](embedded-components.md) |

### Sites Blazor (`Kazo.Os.AppSdk.Web.Blazor.Sites`)

| Type | Rôle |
|---|---|
| `WebAppDefinition.BlazorRoot` (`AppSdk.Web`) | Composant racine du site ; produit `web.blazor` dans le manifeste, la page hôte et `blazor.boot.json`. |
| `KazoSiteRouter` | Routeur du site : `AppAssembly`, `DefaultLayout`, `NotFound` ; gabarits `@page` de Blazor, valeurs de route vers les `[Parameter]` de la page, `@layout` imbriqués. |
| `KazoSiteLayoutBase`, `KazoNavLink`, `KazoPageTitle` | Mise en page (`Body`), lien marqué `active` (`Match`), titre de l'onglet. |
| `KazoWebSite` (cascadé) | `Navigation` (`NavigationManager` de l'onglet : `NavigateTo`, `LocationChanged`, `IsWithinSite`), `Http` (`KazoHttpClient` avec `BaseAddress` et les cookies de l'onglet), `AppId`, `Name`, `Title`. |
| Liens | Un `<a href>` du site navigue sans requête ; un lien dont `target` n'est pas `_self` (`target="_top"`) charge un nouveau document : c'est ainsi qu'une page Blazor rejoint les pages MVC du même site (ADR 0087 §1). |
| `IKazoWebSiteHost`, `IKazoWebSiteProcess`, `KazoWebSiteView` | Côté navigateur : démarre le processus de rendu d'une page hôte et dessine le site dans la zone de page. |

Détails : [`blazor-sites.md`](blazor-sites.md).

### Contrôle de fichiers de l'OS (`Kazo.Os.AppSdk.Blazor.Files`, ADR 0148, ADR 0155)

| Type | Rôle |
|---|---|
| `KazoFileView` | Composant typé du contrat `kazoos.file-view`, fourni par l'explorateur. Voir [`file-control-api.md`](file-control-api.md). Paramètres : `InitialDirectory`, `Mode` (`KazoFileViewMode` : `Details`, `Icons`, `Tree`), `ModeChanged`, `Features` (`KazoFileViewFeatures`), `AllowMultiSelect`, `OpenOnTap` (un toucher ouvre, ADR 0171), `Filter`, `SelectedResource`, rappels `Opening` (`Handled`), `Navigated`, `SelectionChanged`, `Renamed`, `Deleted`, `ContextMenuOpening`, `ErrorOccurred`. |
| `IKazoFileView` | Contrôleur : `CurrentDirectory`, `Items`, `SelectedItems` (`KazoFileViewItem`), `CanPaste`, `IsBusy`, `StateChanged`, `NavigateAsync`, `RefreshAsync`, `RevealAsync`, `ClearSelection`, `RefreshFilter`, `CanExecute`/`ExecuteAsync(commande)`, `WhenIdleAsync`. |
| `KazoFileMenuOpeningEventArgs` | `Scope`, `Items`, `Directory`, `Menu` (`KazoMenuDefinition`), `Cancel`. |
| `KazoFileDialogs` (`.Files`) | Noms des dialogues du fournisseur `kazoos.file-dialogs` : `OpenWith` (« Ouvrir avec »), `Properties`, `Conflict` (types de requêtes : `OpenWithDialogRequest`, `FilePropertiesDialogRequest`, `FileConflictDialogRequest`). |
| `KazoFileOperationDialogs.AsFileConflictResolver(IDialogService, IAppExecutionContext, CancellationToken)` (`.Files`) | Résolveur de conflit de `KazoFileOperations.MoveAsync` et `TransferAsync` : le dialogue « Remplacer / Garder les deux / Ignorer » modal à la fenêtre de l'app ; fermer le dialogue ou un hôte sans fournisseur ignore l'élément. |
| `FileMenuScope`, `KazoFileMenuItems` (`Kazo.Os.AppSdk.Files`) | Portées (`File`, `Directory`, `Background`, `Desktop`) et identifiants des entrées (`open`, `open-with`, `open-with-other`, `open-with:<appId>`, `cut`, `copy`, `paste`, `paste-into`, `rename`, `delete`, `delete-permanently`, `new-folder`, `refresh`, `properties`…). |

Détails : [`embedded-components.md`](embedded-components.md#kazofileview-et-ikazofileview).

### Vue de fichiers (`Kazo.Os.AppSdk.FileView`) : retirée

Le paquet `Kazo.Os.AppSdk.FileView` a quitté le SDK à la 2.0 (ADR 0149,
ADR 0161) : son code est l'implémentation interne de l'explorateur de fichiers
(`Kazo.Os.Apps.FileExplorer.FileControl`), qui fournit `KazoFileView` aux
autres apps. `FileTransfer` est devenu `KazoFileOperations`. Le tableau de
migration d'une app 1.x est dans [`sdk-packages.md`](sdk-packages.md#sdk-20).

### Terminal (`Kazo.Os.AppSdk.Terminal`)

| Composant | Paramètres | Document |
|---|---|---|
| `TerminalPanel` | `CallerAppId`, `InitialUsername`, `InitialCwd`, `UseVirtualKeyboard`, `ShowBanner`, `FocusOnStart`, `OnExitRequested`, `OnTitleChanged` ; héberge un `hsh` (capacité `shell.host`) ; implémente `IKazoTerminal` (`RunCommandAsync`, `SetWorkingDirectoryAsync`, `SendInputAsync`, `ReadOutput`, `ClearAsync`, événements) | [`terminal-emulator.md`](../dev/terminal-emulator.md), [`terminal-api.md`](terminal-api.md), [`shell-host.md`](shell-host.md) |
| `TerminalTabs` | `CallerAppId`, `InitialCwd`, `OnLastTabClosed` ; un `hsh` par onglet | [`terminal-console.md`](../dev/terminal-console.md) |

### Icônes et clavier virtuel

| Composant | Paramètres |
|---|---|
| `KazoIcon` (`AppSdk.Icons`) | `Library` (`IconLibrary` : `Bootstrap`, `FontAwesome`, `Lucide`, `SimpleIcons`), `Name`, `Icon`, `Size`, `Title` ; voir [`old/icon-library.md`](../dev/old/icon-library.md). |
| `VirtualKeyboard`, `VirtualKeyboardHost` (`AppSdk.VirtualKeyboard`) | `Options` (`VirtualKeyboardOptions` : rangées modificateurs, symboles, flèches, libellés), `OnKeyPressed` (`VirtualKeyPressedEvent`), `IsKeyboardVisible`, `ChildContent`. |

## Thèmes (`Kazo.Os.Theming.Abstractions`)

Pour lire ou écrire un thème utilisateur (ADR 0103, détail dans
[`theme-editor.md`](../dev/theme-editor.md)) :

| Type | Membres |
|---|---|
| `ThemeTokenCatalog` | `All`, `InGroup(ThemeTokenGroup)`, `TryGet(name, out ThemeTokenDefinition)` : les 68 jetons `--kos-*` modifiables |
| `ThemeTokenDefinition` | `Name`, `Type` (`Color`, `Length`, `Duration`, `FontFamily`, `Number`), `Group`, `Label`, `Description` (anglais), `GetLabel(culture)`, `GetDescription(culture)`, `Example`, `RgbCompanion` |
| `ThemeTokenValidator` | `TryValidate(type ou nom, valeur, out normalisée, out erreur)`, `TryValidate(type, valeur, culture, out normalisée, out erreur)`, `TryGetRgbTriplet`, `ToHexColor` |
| `ThemeFile` | `Parse(texte, origine, chemin)` → `ThemeFileParseResult` (thème + diagnostics), `Serialize(UserTheme)`, `IsValidId`, `Extension` (`.theme`), `MediaType` ; format du fichier et jetons : [`theme-files.md`](theme-files.md) |
| `ThemeFileDiagnostic` | `Line`, `Message` (anglais), `IsError`, `Path`, `GetMessage(culture)`, `ToString(culture)` |
| `UserTheme` | `Id`, `Name`, `BaseThemeId`, `Platforms`, `Tokens`, `Origin`, `SourcePath`, `Supports`, `ToCssProperties` |
| `UserThemeLibrary` | `SystemDirectory`, `GetUserDirectory(home)`, `GetUserThemePath(home, id)`, `Build`, `LoadAsync(listFiles, readFile, home)` |
| `ResolvedTheme` | `Resolve(id, plateforme, thèmes)` → `BaseThemeId`, `UserTheme`, `IsFallback` |

`ThemeScope` et `ThemePreview` (`Kazo.Os.Theming.Blazor`) prennent un
paramètre `Tokens` appliqué par-dessus le thème de base.
`ThemeScope` prend aussi `HighContrast` et `TextScale` (ADR 0105) : attributs `data-contrast`
(`high` ou `standard`) et `data-text-scale` (`100`, `125`, `150`, `200`), lus par `themes.css`.

## Tâches de ce document

- [x] Lister les bibliothèques du SDK et leurs espaces de noms.
- [x] Types de définition d'une app et table complète des 37 capacités (`keyring.read-all` ajoutée par `v3/8`, `system.backup` par `v3/7`, `users.manage` par la suite de `v3/8`, ADR 0120).
- [x] Membres de `IAppExecutionContext`.
- [x] Chaque façade `Kazo*` avec ses membres, sa capacité et son document.
- [x] Classes de base des points d'entrée et interfaces des fenêtres.
- [x] Composants Blazor : dialogues, graphiques, vue de fichiers, terminal, icônes, clavier virtuel.
- [x] Hôte d'exécution (`AppExecutionHost`, ADR 0070) et `IAppProcessGateway.KillAsync`.
- [x] Sites web ASP.Kazo : démarrage, requête, contrôleurs, vues, anti-falsification, liens des sites Blazor (`web/11`).
- [x] Base `KazoWebDb`, journal de débogage d'un site, passerelle `WebDatabase`, bases des sites dans `KazoStorageDiagnostics` (`web/12`).
- [x] Réglages sensibles, `KazoKeyring` et `keyring.read-all` (`v3/8-settings-security`, ADR 0100).
- [x] `KazoUserAccounts`, `users.manage`, `ITerminalInputMode` et `ReadSecretLineAsync` (`passwd`, ADR 0120).
- [x] `KazoUserAccounts.CreateAsync`/`DeleteAsync` et les clés d'accès (ADR 0168, 0169).
- [x] Intents de l'ADR 0096 : `KazoApps.EditFileAsync`, `RevealFileAsync`, `OpenUriAsync`, `ShowSettingsAsync`, `SendIntentAsync` ; `Intent`/`IntentReceived` ; sélecteur d'apps ; `FileView.RevealAsync` (`v3/4`).
- [x] Défauts propres à l'utilisateur (ADR 0121, `v3/4b`) : `KazoApps.GetDefaultHandlersAsync`, `SetDefaultHandlerAsync`, `ResetDefaultHandlerAsync`.
- [x] Thèmes utilisateur : jetons, format `.theme`, bibliothèque et résolution (`v3/11`).
- [x] `KazoSystemBackup`, `BrowserFilePicker`, codes `NoSpace` et `FileTooLarge` (16 Mio) (`v3/7`).
- [x] Localisation : `Culture`/`FormatCulture`, `KazoLocale`, `GetLocalizer<T>`, `IShellSession.GetCommands(culture)` (`v3/12`, ADR 0104).
- [x] Titre de fenêtre : `WindowTitle`, `SetWindowTitleAsync`, `FormatWindowTitle`, `IAppWindowGateway` (`v3.1/3`, SDK 1.1, versé dans `PublicAPI.Shipped.txt` par `v3.1/4-sdk-1-1`).
- [x] Fenêtres enfants : `ShowWindowAsync`, `ShowDialogAsync`, `KazoWindowBase` (`os-3-1/5-child-windows`, SDK 3.1, ADR 0234 §3).
- [x] Styles de fenêtre façon WinForms : `WindowStyle`, booléens de `WindowOptions`, méthodes de fenêtre de `WindowAppBase` et `IAppWindowGateway` (`os-3-1/4-window-style-options`, SDK 3.1, ADR 0234, `PublicAPI.Unshipped.txt`).
- [x] Tâches en arrière-plan : `KazoBackground`, `KazoBackgroundContext`, `IAppExecutionContext.Background`, `KazoProcessKilledException.Reason`, analyseur `KOSBG` (`exec/5`, ADR 0128 et 0140).
- [x] SDK 1.2 (ADR 0132) : `KazoWireContextAttribute` (`exec/1`), limites de processus et `TooManyOpenFiles` (`exec/2`), `OomScoreAdjust`/`OomScoreAdj` et `OutOfMemory` (`exec/3`), `nice` (`exec/4`), versés dans `PublicAPI.Shipped.txt` par `exec/10-final`.
- [x] SDK 2.0 (`desk/6-sdk-2`, ADR 0149, ADR 0161) : `Kazo.Os.AppSdk.FileView` retiré, `KazoFileOperations`, contrôle de fichiers et menu contextuel de l'OS, versés dans `PublicAPI.Shipped.txt`.
- [x] Relecture finale de la migration v3 (`v3/16-final`) : renvoi vers les fichiers `PublicAPI.Shipped.txt`, liste exhaustive de la surface 1.0.
