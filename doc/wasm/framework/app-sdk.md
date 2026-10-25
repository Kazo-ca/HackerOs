# SDK des apps et frontière du SDK

## Objet

Une app HackerOS, système ou tierce, n'obtient les services de l'OS que par le
SDK public (principe 3 du plan, ADR 0055). Ce document décrit :

- ce qui forme le SDK et ce qu'une app a le droit de référencer et d'injecter ;
- comment le build vérifie ces règles (codes `HOSAPP0030`, `HOSAPP0031`,
  `HOSSDK0001`, ADR 0059) ;
- la fin de la liste d'exemptions temporaire de la phase 4 ;
- les API du SDK, au fur et à mesure que les chantiers `rebuild/4-*` les
  ajoutent (ADR 0056).

Chaque chantier de la phase 4 qui ajoute une API la documente ici, dans la
section [API du SDK](#api-du-sdk).

Pour écrire une app pas à pas, voir le
[guide du développeur](sdk-developer-guide.md) ; pour la liste complète des
API publiques, la [référence du SDK](sdk-reference.md).

## Ce qui forme le SDK

| Projet | Contenu |
|---|---|
| `Shared/HackerOs.AppSdk` | `IAppExecutionContext` et ses passerelles, façades statiques `Hack*`, classes de base des points d'entrée (`TerminalAppBase`…). |
| `Shared/HackerOs.AppSdk.Blazor` | Composants et services Blazor des apps fenêtrées (fenêtre, téléchargement, graphiques, composants intégrables `HackEmbeddedComponent<T>`…). |
| `Shared/HackerOs.AppSdk.Icons` | Catalogue d'icônes. |
| `Shared/HackerOs.AppSdk.Terminal` | Terminal xterm.js (`TerminalPanel`, ADR 0061), qui héberge un shell par `HackShell`, et console à onglets (`TerminalTabs`). |
| `Shared/HackerOs.AppSdk.VirtualKeyboard` | Clavier virtuel. |

Toute bibliothèque `Shared/HackerOs.AppSdk` ou `Shared/HackerOs.AppSdk.*` est
une **bibliothèque SDK** (propriété MSBuild `HackerOsSdkLibrary`, calculée par
`build/HackerOs.Apps.targets`). Un projet de tests nommé de la même façon n'en
est pas une : seul l'emplacement sous `Shared/` compte.

## Règles

### Références de projet (ADR 0055 §2)

| Projet | Peut référencer | Sinon |
|---|---|---|
| Projet d'app (`HackerOsAppProject=true`, tout projet sous `Apps/`) | `Shared/HackerOs.{App,Simulation,Theming}.Abstractions`, les bibliothèques SDK, `Platform/HackerOs.Theming.Blazor`, un autre projet sous `Apps/` | `HOSAPP0030` |
| Bibliothèque SDK | les trois `*.Abstractions` ci-dessus, une autre bibliothèque SDK | `HOSSDK0001` |

Seules les références **directes** du projet comptent : une référence
transitive est vérifiée dans le projet qui la déclare. Les `PackageReference`
restent libres. La référence d'ordre de build vers le générateur de manifeste,
ajoutée par `HackerOs.Apps.targets`, est ignorée.

### Injection (ADR 0055 §3)

Une app ne peut obtenir par injection qu'un service dont le type est :

1. défini dans une bibliothèque SDK ;
2. un type de MudBlazor (par exemple `MudBlazor.IDialogService`) ;
3. `Microsoft.JSInterop.IJSRuntime`, seulement pour importer son propre module
   JavaScript.

Deux points d'injection sont contrôlés, ce sont les seuls que la plateforme
honore pour une app :

- chaque propriété `[Inject]` de chaque type de l'assembly (une directive
  `@inject` compile en propriété `[Inject]`) ;
- chaque paramètre des constructeurs publics des points d'entrée
  (`EntryPoint`, `MobileEntryPoint`), que l'hôte construit avec
  `ActivatorUtilities` (ADR 0034 §2).

Pour un constructeur de point d'entrée, restent permis : le `AppManifest` que
l'hôte passe explicitement, les types valeur et `string` (jamais enregistrés
comme services : un tel constructeur ne sert qu'aux tests, par exemple un
intervalle de minuterie).

Les types de `Simulation.Abstractions` et `App.Abstractions` restent permis
comme **données** (une propriété non `[Inject]`, un paramètre de méthode).

### Appels JavaScript (ADR 0055 §3)

Un appel `Invoke*Async` sur un `IJSRuntime` doit avoir la forme :

```csharp
_module = await JS.InvokeAsync<IJSObjectReference>(
    "import", "./_content/<projet de l'app>/<fichier>.js");
```

Les appels sur le `IJSObjectReference` obtenu sont libres : c'est le code de
l'app. Passer l'`IJSRuntime` à un composant ou à un service d'une bibliothèque
SDK (par exemple `BrowserFileDownloader` de `HackerOs.AppSdk.Blazor`) est
permis : c'est le code du SDK qui fait l'appel.

## Vérification

| Règle | Où | Quand | Code |
|---|---|---|---|
| Références d'une app | cible `ValidateHackerOsSdkBoundary` de `build/HackerOs.Apps.targets` | avant `ResolveProjectReferences`, donc avant la compilation | `HOSAPP0030` |
| Références d'une bibliothèque SDK | même cible | idem | `HOSSDK0001` |
| Paquets `HackerOs.*` d'une app ou d'une bibliothèque SDK (ADR 0098 §2), directs ou transitifs | cible `_ValidateHackerOsPackageBoundary` | après `ResolveAssemblyReferences` : chaque référence de compilation venue d'un paquet `HackerOs.*` doit être permise (app : `*.Abstractions`, `HackerOs.AppSdk*`, `HackerOs.Theming.Blazor`, `HackerOs.App.Build`, `HackerOs.Apps.*`, `HackerOs.Commands` ; bibliothèque SDK : `*.Abstractions` et SDK) | `HOSAPP0030` / `HOSSDK0001` |
| Injection | `SdkBoundaryInspector` du générateur de manifeste (`Tools/HackerOs.Tools.ManifestGenerator`) | après la compilation, avec la génération de `manifest.json` ; en cas d'erreur, rien n'est écrit | `HOSAPP0031` |
| `IJSRuntime` | test de sources `SdkBoundarySourceTests` (`Tests/HackerOs.Ecosystem.Tests`) | `dotnet test` | échec du test |

Les messages nomment le projet, la référence ou le type en cause, et la
règle de l'ADR :

```text
App.csproj : error HOSAPP0030: [My.App] Forbidden project reference 'Platform/HackerOs.Platform.Core/HackerOs.Platform.Core.csproj'. …
App.csproj : error HOSAPP0031: [My.App.MyWindow] [Inject] property 'Sessions' injects 'HackerOs.Simulation.Abstractions.Sessions.ISessionService' …
```

Le générateur relance l'inspection quand `HackerOs.Apps.targets` change.

Hors du dépôt (paquet `HackerOs.App.Build`, voir [`sdk-packages.md`](sdk-packages.md)),
la règle des `ProjectReference` ne s'applique pas (elle est écrite en chemins du
dépôt) ; c'est la règle des paquets qui protège la frontière, et l'inspection de
l'injection (`HOSAPP0031`) tourne de la même façon.

Ces trois vérifications sont toujours des **erreurs** : aucun projet ne peut les
ramener à des avertissements. Le rôle d'un projet vient de son emplacement
(`Apps/` pour une app, `Shared/HackerOs.AppSdk*` pour une bibliothèque SDK) ; un
`.csproj` sous ces dossiers qui fixe lui-même `HackerOsAppProject` ou
`HackerOsSdkLibrary` fait échouer
`SdkBoundarySourceTests.No_app_or_sdk_project_overrides_its_boundary_role`, et
`Build_targets_have_no_exemption_mechanism` échoue si une exemption ou un
`<Warning>` réapparaît dans `HackerOs.Apps.targets`.

## Fin des exemptions temporaires (ADR 0055 §6)

`4-sdk-boundary` avait introduit une liste `HackerOsSdkBoundaryExemption` dans
`build/HackerOs.Apps.targets` : un projet listé recevait des avertissements au
lieu d'erreurs, et le test `IJSRuntime` l'ignorait. Neuf projets y figuraient ;
chaque chantier de la phase 4 a retiré les siens :

| Projet | Sorti par |
|---|---|
| `HackerOs.Apps.Browser`, `HackerOs.Commands` | `4-sdk-network` ([`sdk-network.md`](sdk-network.md)) |
| `HackerOs.AppSdk.Terminal` | `4-sdk-shell-host` ([`shell-host.md`](shell-host.md)) |
| `HackerOs.Apps.FileExplorer`, `HackerOs.Apps.TextEditor`, `HackerOs.Apps.CodeEditor` | `4-sdk-user-and-apps` |
| `HackerOs.Apps.SystemMonitor` | `4-task-manager` ([`task-manager.md`](../dev/task-manager.md)) |
| `HackerOs.Apps.Settings`, `HackerOs.Apps.Diagnostic` | `4-sdk-system-admin` ([`system-administration.md`](system-administration.md)) |

`4-sdk-boundary-final` (2026-10-11) a supprimé le mécanisme : l'élément
`HackerOsSdkBoundaryExemption`, la cible `_HackerOsResolveSdkBoundaryExemption`
et sa propriété `_HackerOsSdkBoundaryExempt`, l'avertissement `HOSAPP0030` /
`HOSSDK0001` de la cible `ValidateHackerOsSdkBoundary`, l'option
`--boundary-exempt` du générateur et le paramètre `severity` de
`GeneratorDiagnostic.ToMsBuildLine`. Toutes les apps de `Apps/` et toutes les
bibliothèques `HackerOs.AppSdk*` compilent sans aucun diagnostic de frontière.

Les références `Platform.Core` inutilisées de Calculator, HackPaint,
IconViewer, TextEditor et Browser ont été retirées par `4-sdk-boundary` :
Calculator, HackPaint et IconViewer passaient déjà sans exemption.

## Comment une app obtient un service de l'OS (ADR 0055 §4)

Uniquement par le SDK :

- une **passerelle** sur `IAppExecutionContext`, propre à l'instance, qui
  vérifie la capacité requise à chaque appel. Une nouvelle passerelle est un
  membre de l'interface avec une implémentation par défaut « non prise en
  charge », pour que les implémentations existantes compilent encore ; la
  fabrique de confiance de Platform Core fournit la vraie ;
- une **façade statique** `Hack*` qui lit le contexte ambiant (ADR 0043) et
  appelle la passerelle. C'est la forme recommandée.

Une capacité manquante lève `HackUnauthorizedAccessException` et passe par
`IAppPermissionErrorGateway`.

## API du SDK

Façades statiques existantes au début de la phase 4 :

| Façade | Rôle | Document |
|---|---|---|
| `HackFile`, `HackDirectory`, `HackPath` | fichiers | [`hack-file-api.md`](hack-file-api.md) |
| `HackExecutionContext` | contexte ambiant | [`hack-file-api.md`](hack-file-api.md) |
| `HackEnvironment` | arguments, répertoire courant, variables | [`process-environment.md`](../dev/process-environment.md) |
| `HackSettings` | paramètres en fichiers | [`settings-files.md`](settings-files.md) |
| `HackTrash` | corbeille | [`trash.md`](../dev/trash.md), [`trash-api.md`](trash-api.md) |
| `HackDns`, `HackPing`, `HackHttpClient`, `HackTcpClient`, `HackUdpClient` | réseau, repli réel décidé par l'OS (`4-sdk-network`), sockets relayés (`v3/10`) | [`sdk-network.md`](sdk-network.md) |
| `HackDriveInfo` | taille et espace libre des disques (`4-proc-metrics`) | [`system-metrics.md`](../dev/system-metrics.md) |
| `HackCrossOriginIsolation` | page isolée ou non, et ce que ça active (ADR 0146, SDK 2.0) | [`worker-processes.md`](../dev/worker-processes.md#isolation-cross-origin-adr-0131-adr-0143) |
| `HackClipboard` | presse-papiers de session, texte et liste de fichiers (`4-file-explorer`) | [`file-explorer.md`](../dev/file-explorer.md), [`clipboard-api.md`](clipboard-api.md) |
| `HackTextContent` | texte ou binaire décidé sur le contenu, comme `file` (`4-file-explorer`) | [`text-editor.md`](../dev/text-editor.md) |

Passerelles de `IAppExecutionContext` : `FileSystem`, `Events`,
`Notifications`, `Logging`, `Clock`, `Processes`, `Intents`, `Watch`,
`Services`, `PermissionErrors`, `Environment`, `Catalog` et `AppManagement`
(ADR 0056, `4-sdk-user-and-apps`), `Network` (`4-sdk-network`), `ShellHost`
(`4-sdk-shell-host`), `Drives` (ADR 0057, `4-proc-metrics`),
`Clipboard` (ADR 0056 §12, `4-file-explorer`), `SystemSettings`,
`PermissionAdministration`, `ServerConnection`, `SystemPower` et
`StorageDiagnostics` (`4-sdk-system-admin`), `Console` (ADR 0064).

`ShellErrorText` (texte court d'une erreur de fichier, pour les commandes comme
`cd`) est aussi dans le SDK, à côté de `IShellSession` (`4-sdk-network`).
`ShellErrorText.Describe(exception)` reste en anglais (texte identique à
`strerror` en locale C) ; `Describe(exception, context)` le donne dans la
langue du processus de l'app (ADR 0104). De même,
`HackWebSiteFile.Validate(site, scope, context)` traduit les messages de
validation d'un `.site` affichés par une app.

### Utilisateur courant (ADR 0056 §1)

```csharp
HackUserInfo me = HackUser.Current;     // LoginName, DisplayName, HomeDirectory, IsAdministrator
string docs = HackEnvironment.GetFolderPath(HackSpecialFolder.MyDocuments);   // /home/benoit/Documents
HackUserInfo cached = HackUser.Describe(AppContext);   // sans contexte ambiant (rendu d'un composant)
```

- `HackUser.Current` décrit l'utilisateur **effectif** : `HackUser.Root`
  (`root`, `/root`, administrateur) pour un processus lancé par `sudo`.
- `HackSpecialFolder` : `UserProfile` (`~`), `Desktop`, `MyDocuments`
  (`~/Documents`), `ApplicationData` (`~/.config`), `LocalApplicationData`
  (`~/.local/share`). Le chemin est calculé, pas vérifié.
- Aucune capacité. `IAppExecutionContext` gagne `UserLoginName` et
  `UserDisplayName` (implémentations par défaut ; la fabrique les remplit
  depuis le principal).
- Remplace `ISessionService.CurrentPrincipal` dans FileExplorer, TextEditor et
  CodeEditor. Comme `BuildRenderTree` ne tourne pas dans le contexte ambiant,
  un composant qui affiche le dossier personnel utilise
  `HackUser.Describe(AppContext)`.

### Catalogue des apps (ADR 0056 §2)

```csharp
IReadOnlyList<HackAppInfo> apps = await HackApps.GetInstalledAsync();
IReadOnlyList<HackAppInfo> editors = await HackApps.GetHandlersAsync("~/notes.md");
await HackApps.LaunchAsync("org.hackeros.text-editor", ["/home/benoit/notes.md"]);
HackOpenFileResult opened = await HackApps.OpenFileAsync("~/notes.md");   // Opened, ChooserRequired (+ CandidateAppIds), NoHandler
```

- `HackAppInfo` : `Id`, `Name`, `Description`, `Kind`, `Category`, `Icon`,
  `Version`, `PublisherId`, `IsSystemApp`, `IsHidden`, `FileHandlers`, `Urls`
  (adresses `web.urls` d'une app web, vide sinon ; liste des sites externes du
  Web Server Manager).
- `GetInstalledAsync` : apps installées pour l'utilisateur, triées par nom ;
  une app désinstallée (ADR 0048) n'y figure pas. **Aucune capacité.**
- `GetHandlersAsync` : apps fenêtrées installées qui déclarent l'extension ou
  le type MIME ; le défaut en tête : celui de l'utilisateur
  (`~/.config/hackeros/file-associations.json`), sinon celui de
  `/etc/hackeros/file-associations.json` (ADR 0121).
- `GetDefaultHandlersAsync`, `SetDefaultHandlerAsync(target, appId, action)` et
  `ResetDefaultHandlerAsync(target, action)` (ADR 0121, chantier `v3/4b`) :
  défauts de l'utilisateur et de la machine, choix propre à l'utilisateur
  (`file-associations.read`, plus `filesystem.user-home.write` pour écrire) ;
  voir [`user-file-associations.md`](../dev/user-file-associations.md).
- `LaunchAsync` et `OpenFileAsync` passent par `IAppIntentGateway`
  (`apps.launch`). Échec : `HackAppLaunchException` (app inconnue,
  désinstallée, ou erreur au démarrage).
- `EditFileAsync`, `RevealFileAsync`, `OpenUriAsync`, `ShowSettingsAsync` et
  `SendIntentAsync` (ADR 0096, chantier `v3/4`) passent aussi par
  `IAppIntentGateway` ; voir [`app-intents.md`](app-intents.md).
- `HackApps.Changed` (ou `AppContext.Catalog.Changed`, à préférer quand on se
  désabonne dans `Dispose`, hors contexte ambiant) signale une installation ou
  une désinstallation ; les gestionnaires s'exécutent dans le contexte de
  l'app abonnée.
- Passerelle : `IAppCatalogGateway Catalog` (`AppCatalogGateway`, Platform Core).

### Gestion des apps (ADR 0056 §3)

```csharp
IReadOnlyList<HackAppInstallInfo> all = await HackAppManagement.GetAllAsync();   // App, State, UninstallClosure
IReadOnlyList<string> removed = await HackAppManagement.UninstallAsync("org.hackeros.calculator", HackInstallScope.User);
await HackAppManagement.ReinstallAsync("org.hackeros.calculator", HackInstallScope.Machine);
```

- **Capacité `apps.manage`** (dangereuse) pour tous les membres. Paramètres la
  déclare désormais.
- `HackAppInstallState` : `Installed` (y compris une app système, signalée par
  `App.IsSystemApp`), `UninstalledForUser`, `UninstalledForMachine`,
  `Incompatible` (refusée au chargement : version du SDK, ADR 0098 §3) et
  `Damaged` (ressource altérée, ADR 0098 §5) ; `HackAppInstallInfo.Problem`
  donne alors la raison, affichée par Paramètres.
- Portée `Machine` : l'agent d'authentification de l'OS demande un
  administrateur ([`admin-authentication-agent.md`](../dev/admin-authentication-agent.md)),
  puis l'OS écrit `/etc/hackeros/apps.config` lui-même. Refus ou annulation :
  `HackUnauthorizedAccessException`.
- App système, ou fermeture qui en contient une : `InvalidOperationException`,
  avant toute demande de mot de passe. Réinstaller pour soi une app
  désinstallée par l'administrateur : `InvalidOperationException`. App
  inconnue : `ArgumentException`.
- Passerelle : `IAppManagementGateway AppManagement` (`AppManagementGateway`,
  sur `IAppInstallationService`).

### Réseau (ADR 0056 §6)

`HackDns`, `HackPing`, `HackHttpClient` et `HackTcpClient`, passerelle
`IAppNetworkGateway Network`, repli réel décidé par l'OS : voir
[`sdk-network.md`](sdk-network.md).

### Disques (ADR 0057 §7)

`HackDriveInfo.GetDrivesAsync()` rend les montages qui ont une taille
(aujourd'hui `/`), sur le modèle de `System.IO.DriveInfo` : `Name`,
`DriveFormat` (`idbfs`), `DriveType` (`Fixed`), `VolumeLabel` (`hda`),
`TotalSize`, `AvailableFreeSpace`, `TotalFreeSpace`, `RootDirectory`. Aucune
capacité, comme `df`. Passerelle `IAppDriveGateway Drives`, implémentée par
`AppDriveGateway` dans Platform Core. Les valeurs sont lues une fois, à l'appel.
Les mesures CPU, disque et réseau, elles, sont des fichiers de `/proc` : voir
[`system-metrics.md`](../dev/system-metrics.md).

### Isolation cross-origin (ADR 0146)

`HackCrossOriginIsolation.GetStatusAsync()` (espace `Diagnostics`) rend
l'isolation que l'OS a décidée au démarrage : `IsIsolated`, `Reason`
(`None`, `Setting`, `MissingHeaders`, `InsecureContext`, `Browser`,
`NotBrowser`), `Setting` (`Auto`, `On`, `Off`), `EmbedderPolicy`,
`SharedPipes`, `SynchronousWorkerCalls`, `BrowserMemoryMeasurement`,
`TimerResolution`. Aucune capacité, comme `uname`. Passerelle
`IAppCrossOriginIsolationGateway CrossOriginIsolation`, implémentée par
`AppCrossOriginIsolationGateway` dans Platform Core et par l'opération
`coi.get` dans un worker. Publié avec le SDK 2.0 (aucune 1.3 n'a été publiée).

### Processus et services (ADR 0056 §4 et §5)

| API | Passerelle | Capacité | Remplace | Document |
|---|---|---|---|---|
| `HackProcess` (`GetProcessesAsync`, `GetProcessByIdAsync`, `GetCurrentProcessAsync`, `RefreshAsync`, `GetEnvironmentVariablesAsync`, `KillAsync`, `TerminateAsync`, `CloseMainWindowAsync`, `Current`, `CheckpointAsync` ; `WorkingSet64` lit `VmRSS`, `PrivateMemorySize64` lit `RssAnon`, ADR 0068 ; `UserProcessorTime`, `PrivilegedProcessorTime`, `TotalProcessorTime`, `Responding` lus dans `/proc/<pid>/stat` et `status`, ADR 0067), `HackCurrentProcess`, `HackStopRequestedEventArgs`, `HackProcessKilledException`, `HackProcessState`, `HackProcessIoCounters` | lecture : `IAppExecutionContext.FileSystem` sur `/proc` ; action : `IAppExecutionContext.Processes` (`IAppProcessGateway`) | `filesystem.system.read` pour lire `/proc` ; `process.list` (normale) pour voir les autres processus ; `process.manage` (dangereuse) pour arrêter un autre processus | `IProcessManager`, `AppLifecycleOrchestrator`, `IEventBus` | [`task-manager.md`](../dev/task-manager.md) |
| `HackServiceController` (`GetServicesAsync`, `RefreshAsync`, `StartAsync`, `StopAsync`, `SetStartTypeAsync`), `HackServiceControllerStatus` | `Catalog` (liste), `/proc` (état), `Services` (`IAppServiceControlGateway`) | aucune pour lister ; `services.manage` (dangereuse) pour agir sur le service d'une autre app ou lire son type de démarrage | `AppCatalog` filtré sur `AppKind.Service`, orchestrateur | [`task-manager.md`](../dev/task-manager.md) |

Les données d'un processus viennent de `/proc`, exactement ce que `ps` lit ; il
n'y a pas d'abonnement aux événements de processus : un gestionnaire de tâches
relit `/proc` chaque seconde.

### Arrêt propre et points d'arrêt (ADR 0069)

Quand l'OS arrête un processus poliment (`kill`, « End task »,
`CloseMainWindowAsync`, déconnexion, extinction), il le passe à `Stopping`,
lève **une fois** `StopRequested`, annule son jeton, puis attend jusqu'à
l'échéance (horloge simulée) avant de le tuer. `kill -9` et « Force kill » ne
préviennent pas.

| API | Rôle |
|---|---|
| `HackProcess.Current` (`HackCurrentProcess` : `Id`, `StopToken`, `IsStopRequested`, `StopRequest`, `StopRequested`) | Le processus courant vu de l'intérieur, sur le modèle de `IHostApplicationLifetime.ApplicationStopping`. Aussi `IAppExecutionContext.StopRequested` / `StopRequest`. |
| `HackStopRequestedEventArgs` (`Reason`, `RequestedAt`, `Deadline`, `GracePeriod`, `Defer(Task)`) | Raison (`CloseRequested`, `Terminated`, `Logout`, `Shutdown`…) et échéance ; `Defer` demande à l'OS d'attendre un travail (brouillon, étape en cours) avant son action par défaut, jamais au-delà de l'échéance. |
| `HackProcess.CheckpointAsync(token)` | Point d'arrêt volontaire : lève `OperationCanceledException` si l'arrêt est demandé ; rend la main (`Task.Yield`) quand la tranche courante dépasse 8 ms ; sinon retourne tout de suite, sans allocation. |
| `HackProcess.TerminateAsync(timeout)` | `SIGTERM` d'un autre processus (raison `Terminated`, 5 s par défaut), sous `process.manage` ; `true` si le processus s'est terminé seul. |
| `HackProcessKilledException` | Levée par toute passerelle appelée pour un processus tué (dérive de `OperationCanceledException`). |
| `WindowAppBase.OnAppStopRequestedAsync(e)` | Crochet d'une app à fenêtre ; la fenêtre se ferme quand la tâche renvoyée se termine (action par défaut de `SIGTERM`). Pas appelé quand l'utilisateur ferme la fenêtre : `IWindowCloseGuard` s'en charge. |
| `ServiceAppBase.OnStoppingAsync` | Appelé une seule fois, dès `StopRequested` (`ServiceStopReason.Terminated` pour `kill`/« End task »/`systemctl stop`), et au plus tard à l'arrêt par l'OS. |
| `HackRecoveryDraft` (`Open`, `SaveAsync`, `TryReadAsync`, `TakeAsync`, `DeleteAsync`) | Brouillon de récupération dans `~/.local/state/<appId>/recovery/` (dossier `0700`), à écrire sur `StopRequested` et à proposer au démarrage suivant. |

Escalade (ADR 0069 §3) : délai passé, le processus est tué (`Timeout`) ;
l'ordonnanceur de son processus (`ProcessTaskScheduler`, installé pour les
commandes et les services) jette ses continuations, ses abonnements et
minuteries ne sont plus rappelés, et ses passerelles lèvent
`HackProcessKilledException`. Les apps à fenêtre restent sur le dispatcher du
rendu : leurs fenêtres sont fermées.

Ce que font les apps existantes (section 5.1 du
[plan de la phase 5](../dev/plans/execution-isolation-plan.md)) :

| App | Sur `StopRequested` | Boucles longues avec `CheckpointAsync` |
|---|---|---|
| TextEditor | brouillon `HackRecoveryDraft` si le document est modifié ; restauré au démarrage suivant comme nouveau tampon non enregistré | — |
| CodeEditor | instantané de récupération écrit tout de suite (au lieu d'attendre l'anti-rebond de 1,5 s) | recherche dans le dossier, parcours du dossier |
| FileExplorer | attend l'élément en cours ; `HackFileOperations` s'arrête entre deux éléments (atomiques) et rapporte les autres « stopped before this item » (`HackFileTransferResult.Cancelled`) | copie, déplacement, import, corbeille, zip, extraction, restauration de la corbeille |
| Settings | attend l'écriture de réglage en cours, qui n'utilise plus le jeton du processus | — |
| Services (`SampleTickerService`) | `OnStoppingAsync` (une fois) | — |
| `HackerOs.Commands` | jeton annulé : sortie 130 ; tuée : 137 | `find`, `grep` (dont `-r`), `du`, `zip`, `unzip`, `locate` |

HackPaint n'a pas d'enregistrement : rien à garder. Les autres apps n'ont ni
état non enregistré ni boucle longue.

### Processus dans des Web Workers (ADR 0070 et 0071)

Une commande ou un service peut tourner dans un Web Worker au lieu du fil
principal. L'app le déclare dans son manifeste — rien d'autre ne change dans
son code :

```csharp
[AppManifest(Execution = AppExecutionHost.Dedicated)] // ou Pool, MainThread
public sealed class MyServiceApp : AppDefinition { ... }
```

| Valeur | Effet |
|---|---|
| `Default` (omise) | Commande → `Pool`, service → `Dedicated`, fenêtre → `MainThread` (ADR 0070 §1). |
| `Pool` | Un worker par cœur virtuel ; les étapes d'un pipeline partagent le même worker. |
| `Dedicated` | Un worker à soi (le worker prêt, remplacé aussitôt). |
| `MainThread` | Reste sur le fil principal ; à commenter (raison) dans la définition. |

Dans un worker, `IAppExecutionContext` est servi par des passerelles distantes
qui envoient chaque appel à l'OS, où **les mêmes vérifications de capacités**
s'appliquent ; `stdin`/`stdout`/`stderr` passent par messages. `StopRequested`,
`CheckpointAsync`, les codes 130/137 et `OnStoppingAsync` se comportent comme
sur le fil principal. Ce qui n'est pas disponible dans un worker (hôte de
shell, administration, page HTTP du navigateur simulé, `StartChild`, formes
synchrones de `Kill` et `ListProcesses`, `WithSelectedHandle`, `SetNice`,
`RegisterSharedChannel` hors d'une portée `VirtualPath`) lève
`NotSupportedException` : une app qui en a besoin déclare `MainThread`.
Exception : quand la page est isolée (`crossOriginIsolated`, ADR 0131 et 0143),
les formes synchrones de `Kill` et `ListProcesses` marchent aussi dans un
worker, qui attend la réponse de l'OS comme un appel système bloquant.
Détails : [`worker-processes.md`](../dev/worker-processes.md), [`app-execution-hosts.md`](app-execution-hosts.md).

**Événements et surveillance de fichiers dans un worker (ADR 0124, ADR
0138).** `HackEvents` et `HackFileSystemWatcher` marchent dans un worker : l'OS
tient l'abonnement au nom du processus et lui envoie chaque message. La charge
traverse en JSON ; les événements de l'OS sont déjà pris en charge, un type de
l'app doit être déclaré par un `JsonSerializerContext` de son assembly :

```csharp
[assembly: HackWireContext(typeof(MyEventsJsonContext))]

[JsonSerializable(typeof(MyTickEvent))]
internal sealed partial class MyEventsJsonContext : JsonSerializerContext;
```

Sans déclaration, l'abonnement ou la publication lève dans le worker une
`NotSupportedException` qui nomme le type et l'attribut ; sur le fil principal,
rien ne change. Dans un worker, `Publish` renvoie un résultat provisoire
(`Delivered`) : un refus de l'OS (sujet d'une autre app, canal verrouillé) est
écrit dans le journal de l'app ([`app-log-files.md`](app-log-files.md)). `PublishAsync` (ADR 0145, SDK 2.0)
renvoie le vrai résultat de l'OS, fautes des abonnés comprises, sur le fil
principal comme dans un worker : c'est la forme à préférer dans le code qui
peut tourner en worker.

```csharp
TopicPublishResult result = await context.Events.PublishAsync(topic, new MyTickEvent(1), cancellationToken);
if (result.Outcome != TopicPublishOutcome.Delivered) { /* refusé par l'OS */ }
```

Un abonnement par canal refusé se termine avec l'exception. Les livraisons
sont toujours asynchrones dans un worker.

### Graphiques (ADR 0058 §2)

`HackLineChart` (`HackerOs.AppSdk.Blazor.Charts`) trace en SVG natif une ou
plusieurs `HackChartSeries`, la valeur la plus récente à droite, avec une
légende et une échelle automatique ; ni bibliothèque ni JavaScript. Les
couleurs viennent du thème (`--hos-chart-series-0` à `7`). Voir
[`task-manager.md`](../dev/task-manager.md).

### Presse-papiers (ADR 0056 §12)

```csharp
await HackClipboard.SetFileDropListAsync(["~/rapport.txt"], HackClipboardFileOperation.Cut);
HackClipboardFileList? files = await HackClipboard.GetFileDropListAsync();   // Operation + Paths
string? text = await HackClipboard.GetTextAsync();
```

- Un presse-papiers par **session**, partagé par toutes ses apps (`SystemClipboard`,
  Platform Core), vidé à la déconnexion ; liste de fichiers au format GNOME
  `x-special/gnome-copied-files`, lisible aussi en texte (les chemins).
- **Capacités** `clipboard.read` (lecture) et `clipboard.write` (écriture,
  vidage), normales. `Changed` ne demande aucune capacité.
- `GetFileReferencesAsync` / `SetFileReferencesAsync`, anciennes formes de
  « copier », sont retirées au gel du SDK 1.0 (ADR 0114 §3). L'ancienne classe en mémoire `AppClipboardGateway` du SDK est
  supprimée (la vraie est dans Platform Core).
- Passerelle : `IAppClipboardGateway Clipboard` (`AppClipboardGateway`, Platform Core).
- **Partage avec la vraie machine** (ADR 0065) : le texte copié par une app part
  aussi vers le presse-papiers de l'hôte, et un collage de l'hôte devient
  l'entrée courante, selon la clé `sharing` de `/etc/hackeros/clipboard.config`.
  Une app ne parle jamais à `navigator.clipboard` ; `BrowserClipboard` a été
  retiré au gel du SDK 1.0 (ADR 0114 §3). Voir [`clipboard.md`](../dev/clipboard.md), [`clipboard-api.md`](clipboard-api.md).
- `HackDirectory.CopyAsync` (copie récursive) et `HackFileOperations`
  (`HackerOs.AppSdk.IO`, ex-`FileTransfer`, transferts de l'explorateur et de
  toute app depuis le SDK 2.0) sont décrits dans
  [`file-explorer.md`](../dev/file-explorer.md).

### Administration du système (ADR 0056 §8 à §11, ADR 0060)

```csharp
await using HackSettingsDocument appearance = await HackSettings.OpenSystemAsync("appearance");
await HackPermissionAdministration.SetMachineDecisionAsync(appId, capability, HackPermissionDecision.Deny);
HackServerConnectionState server = await HackServerConnection.GetStateAsync();
await HackSystem.RebootAsync();
HackStorageSnapshot snapshot = await HackStorageDiagnostics.GetSnapshotAsync();
```

- `HackSettings.OpenSystemAsync` : documents de l'OS (`appearance`,
  `ui-platform-preference`, `security`, `policy`), sans capacité ; les droits
  des fichiers décident. `HackSettingsDocument.SetManyAsync` écrit plusieurs
  clés en une seule modification (une seule approbation pour `/etc`).
- `HackComponentProviders` (`apps.manage`, ADR 0156) : l'app qui fournit le
  contrôle de fichiers et ses dialogues pour toute la machine ; une app non
  système passe par la fenêtre d'approbation de l'OS.
- `HackPermissionAdministration` (`permissions.manage`), `HackServerConnection`
  (`server.connection.manage`), `HackSystem` (`system.power`),
  `HackStorageDiagnostics` (`system.storage.inspect`) : capacités dangereuses.
  Les changements machine et l'alimentation passent par l'agent
  d'authentification de l'OS.
- Passerelles : `SystemSettings`, `PermissionAdministration`,
  `ServerConnection`, `SystemPower` (pas `System`, pour ne pas masquer
  l'espace de noms) et `StorageDiagnostics`.
- Détails : [`system-administration.md`](system-administration.md).

### Hôte de shell (ADR 0056 §7)

| API | Passerelle | Capacité | Remplace | Document |
|---|---|---|---|---|
| `HackShell.StartAsync`, `IHackShell` (`ExecuteAsync`, `CompleteAsync`, `GetInstalledCommands`, `WorkingDirectory`, `ExitStatus`, `Prompt`, historique), `HackShellOptions`, `HackShellCompletion`, `HackCredentialPrompt` | `IAppExecutionContext.ShellHost` (`IAppShellHostGateway`) | `shell.host` (dangereuse) | `ShellInterpreter`, `ISessionService`, `IElevationService` dans `TerminalPanel` | [`shell-host.md`](shell-host.md) |

`4-terminal` ajoute la complétion des chemins, `HackShellCompletion.DisplayCandidates`,
`IShellSession.Exit` (commande `exit`) et le composant d'onglets `TerminalTabs`
de `HackerOs.AppSdk.Terminal` ([`terminal-console.md`](../dev/terminal-console.md)).

Le shell est un processus `hsh` enfant de l'app ; ses commandes sont ses
enfants et gardent leurs propres capacités. Le terminal lit les identifiants
(`HackCredentialPrompt`), l'OS les vérifie.

### Arguments de lancement d'une fenêtre

`WindowAppBase.LaunchArguments` rend les arguments du processus sans `argv[0]`
(comme `Main(string[] args)`), et `WindowAppBase.LaunchFile` le premier s'il est
un chemin absolu. Ouvrir un fichier depuis l'explorateur ou avec `open` lance
l'app associée avec ce chemin : une app de document lit son fichier initial là
(voir [`text-editor.md`](../dev/text-editor.md)).

### Événements et tailles de fenêtre (ADR 0095)

`WindowAppBase` reçoit les crochets `OnWindowActivated`, `OnWindowDeactivated`,
`OnWindowStateChanged` et `OnWindowResized` (avec leurs versions `Async`) et
l'état `IsWindowActive`, `WindowState`, `WindowSize`. Les types `HackWindowState`,
`HackWindowSize` et `IWindowAppEventTarget` sont dans `HackerOs.AppSdk.Blazor` ;
`WindowOptions` (tailles déclarées par `AppDefinition.Window`, et depuis le SDK 1.1
`RememberGeometry`, ADR 0116) est dans `HackerOs.App.Abstractions`. Aucune app n'injecte de service pour les recevoir :
l'OS les appelle. Voir [`window-model.md`](window-model.md).

### Titre de fenêtre (ADR 0117, ADR 0123, SDK 1.1)

`WindowAppBase.SetWindowTitleAsync(title)` remplace le titre de la fenêtre de
l'instance (aucune capacité), `WindowTitle` le lit et
`FormatWindowTitle(document, modified)` compose `document — App` avec le nom
traduit de l'app. La plateforme donne à chaque fenêtre une `IAppWindowGateway`
par valeur en cascade fixe, jamais par injection : une app n'atteint que sa
propre fenêtre et le SDK ne référence aucun type `HackerOs.Windowing.*`
(ADR 0055). Voir [`window-model.md`](window-model.md) §4.

### API du SDK 1.2 (série `exec`, ADR 0124 à 0128, ADR 0132)

Ajouts seulement : une app 1.0 ou 1.1 tourne sans changement. Liste complète
dans la [référence du SDK](sdk-reference.md).

| API | Rôle | Capacité | Document |
|---|---|---|---|
| `[assembly: HackWireContext(typeof(…))]` (`HackWireContextAttribute`) | Types d'une app qui traversent la frontière d'un worker : charges de `HackEvents`, entrées et résultats de `HackBackground` | — | [ci-dessus](#processus-dans-des-web-workers-adr-0070-et-0071), [ADR 0124](../dev/adr/0124-remote-event-and-watch-gateways.md) |
| `HackProcess.GetResourceLimitsAsync` → `HackResourceLimits`, `SetResourceLimitAsync` ; `ProcessLimitExceededException` (`EAGAIN`), `FileSystemErrorCode.TooManyOpenFiles` (`EMFILE`), `ProcessExitReason.CpuLimitExceeded`, `MemoryLimitExceeded` | Limites façon `setrlimit` d'un processus | `process.manage` pour un autre processus | [`process-limits.md`](../dev/process-limits.md) |
| `AppDefinition.OomScoreAdjust` (manifeste `oomScoreAdj`), `ProcessExitReason.OutOfMemory` | `oom_score_adj` de départ, négatif pour une app système seulement (`HOSAPP0024`) ; raison de fin d'une victime de l'OOM killer | — | [`oom-killer.md`](../dev/oom-killer.md) |
| `HackProcess.Nice`, `BasePriority`, `IShellSession.RunNicedAsync`, `ProcessNice` | Priorité `nice` d'un processus ; lancer une commande avec un ajustement | `process.manage` hors de son processus et de ses enfants ; `shell.control` | [`process-priority-and-unresponsive-windows.md`](../dev/process-priority-and-unresponsive-windows.md) |
| `HackBackground.RunAsync`, `HackBackgroundJob<TInput, TResult>`, `HackBackgroundContext`, `IAppExecutionContext.Background` ; analyseur `HOSBG0001` à `HOSBG0004` | Calcul long dans un processus enfant de l'app, placé dans un worker ; méthode statique vérifiée au build | — | [`background-jobs.md`](background-jobs.md) |

### API du SDK 2.0 (série du bureau, ADR 0148 à 0161)

Une majeure : une app 1.x est refusée et doit être recompilée. Le seul retrait
est le paquet `HackerOs.AppSdk.FileView` ; le tableau de migration est dans
[`sdk-packages.md`](sdk-packages.md#migration-dune-app-1x).

| API | Rôle | Capacité | Document |
|---|---|---|---|
| `HackFileView`, `IHackFileView` (`HackerOs.AppSdk.Blazor.Files`) | Contrôle de fichiers fourni par l'OS (l'explorateur par défaut), avec les droits de l'app qui l'affiche | celles des fichiers montrés | [`embedded-components.md`](embedded-components.md), [`file-explorer.md`](../dev/file-explorer.md), [`file-control-api.md`](file-control-api.md) |
| `Menus.Attach`, `Menus.Define`, `HackContextMenu`, `HackMenuDefinition` | Menu contextuel de l'OS, multi-niveaux, clavier et appui long | — | [`context-menu.md`](context-menu.md) |
| `[RegisterFileContextMenu]`, `RegisterFileContextMenuContext`, `HackFileMenuItems` | Entrées d'une app dans les menus des fichiers, inscrites au build | `shell.context-menu` | [`context-menu.md`](context-menu.md#entrées-des-apps-dans-les-menus-des-fichiers-chantier-4) |
| `HackFileOperations`, `IDialogService.AsFileConflictResolver` | Copie, déplacement, import, corbeille, conflits à la Nautilus (ex-`FileTransfer`) | celles des fichiers | [`file-explorer.md`](../dev/file-explorer.md), [`file-operations-api.md`](file-operations-api.md) |
| `HackDesktopSettings`, `HackDesktopEntry`, `HackDesktopLayout` | Réglages, raccourcis `.desktop` et grille du bureau | celles du dossier perso | [`desktop.md`](../dev/desktop.md), [`desktop-api.md`](desktop-api.md) |
| `HackComponentProviders`, `HackFileMenuEntries` | Choisir le fournisseur d'un contrat, masquer une entrée de menu | `apps.manage` | [`embedded-components.md`](embedded-components.md) |
| `IAppEventGateway.PublishAsync`, `HackCrossOriginIsolation` | Prévus pour une 1.3 jamais publiée (ADR 0145, 0146) | — | ci-dessus |

### API ajoutées après la phase 4 (ADR 0061 à 0065)

Ces API suivent les mêmes règles (façade ou passerelle, capacité déclarée dans
le `[AppManifest]`) ; leur détail est dans la
[référence du SDK](sdk-reference.md) et dans le document de chaque chantier.

| API | Rôle | Capacité | Document |
|---|---|---|---|
| `HackConsole`, passerelle `IAppExecutionContext.Console` (`IAppConsoleGateway`) | Flux standard d'une instance : ceux du shell qui l'a lancée par son nom, `/dev/null` depuis le bureau | — | [`shell-interpreter.md`](../dev/shell-interpreter.md#apps-lancées-par-leur-nom) |
| `IShellSession` : `GetVariable`, `AssignVariable`, `RunAsync`, `Jobs`, `KillJob` ; `IHackShell.NeedsMoreInput` | Built-ins `read`, `watch`, `jobs`, `kill %n` écrits comme des commandes ordinaires ; invite de continuation | `shell.control` ; `shell.host` | [`shell-commands.md`](../dev/shell-commands.md), [`shell-host.md`](shell-host.md) |
| `IFullScreenTerminalSession` (`TerminalExecutionContext.FullScreen`), `TerminalInputReader` | Écran alternatif (`nano`, `top`, `htop`) et entrée standard du terminal | — | [`terminal-emulator.md`](../dev/terminal-emulator.md), [`terminal-api.md`](terminal-api.md) |
| `HackFile.CreateSymbolicLinkAsync` | `ln -s` | droits du chemin | [`hack-file-api.md`](hack-file-api.md) |
| `AppDefinition.Components` (`AppComponent`), `HackEmbeddedComponent<T>`, `HackContractComponent<T>`, `HackEmbeddableComponent`, `HackComponentContracts`, `IHackTerminal` | Composant d'une app affiché par une autre, par contrat (`hackeros.terminal`) | celles de l'app qui affiche le composant (CodeEditor déclare `shell.host` pour le terminal embarqué) | [`embedded-components.md`](embedded-components.md) |
| `HackClipboard` partagé avec l'hôte (`BrowserClipboard` retiré en 1.0) | Le texte copié part vers la vraie machine selon `clipboard.config` | `clipboard.read`, `clipboard.write` | [`clipboard.md`](../dev/clipboard.md) |

Aucune de ces API ne donne accès à un service interne : les apps qui les
utilisent (Terminal, CodeEditor, Diagnostic, FileExplorer, `HackerOs.Commands`)
passent la vérification de la frontière sans exception (voir
[Vérification finale](#vérification-finale-de-la-phase-4-2026-10-16)).

## Décisions

- Codes `HOSAPP0030` et `HOSAPP0031` au lieu de `HOSAPP0002` et `HOSAPP0003`,
  déjà pris par l'ADR 0047 (ADR 0059).
- Exemption par projet, pas par référence : plus simple à retirer, et un
  chantier corrige un projet entier (historique : le mécanisme a été supprimé
  par `4-sdk-boundary-final`).
- Les avertissements d'un projet exempté ne sont pas transformés en erreurs :
  `TreatWarningsAsErrors` ne s'applique qu'au compilateur C#, pas aux
  avertissements MSBuild.
- Le contrôle d'injection compare les types par nom (assembly, nom complet) :
  l'assembly de l'app est chargée dans un contexte isolé.

- `HackAppInfo` ajoute `PublisherId` et `IsHidden` à la liste de l'ADR 0056 :
  l'onglet « Installed Apps » affiche l'éditeur, et un lanceur doit savoir
  qu'une app est cachée. `HackApps.OpenFileAsync` rend un résultat plutôt que
  rien, parce que l'explorateur doit proposer un choix quand plusieurs apps
  conviennent. Les méthodes de `HackAppManagement` rendent les ids écrits.
- Un composant enfant qui n'hérite pas de `HackComponentBase` appelle les
  passerelles de `AppContext` directement (`AppContext.Catalog`,
  `AppContext.AppManagement`) : ses gestionnaires d'événements ne sont pas dans
  le contexte ambiant. *Phase 5 :* les panneaux de Settings (`InstalledAppsPanel`,
  `AppPermissionsPanel`) héritent désormais de `HackComponentBase` et reçoivent
  le contexte en cascade, comme tous les composants de `Apps/`
  ([`resource-accounting.md`](../dev/resource-accounting.md#apps-existantes)).

## Vérification finale de la phase 4 (2026-10-16)

Repasse de `rebuild/4-sdk-boundary-final` sur `rebuild/base` après les PR #115
à #122 (xterm.js, éditeur de code, composants intégrables, shell POSIX et
commandes Linux, presse-papiers partagé avec l'hôte) :

- `dotnet build HackerOs.sln` : 0 avertissement, 0 erreur, aucun diagnostic
  `HOSAPP0002`, `HOSAPP0003`, `HOSAPP0030`, `HOSAPP0031` ni `HOSSDK0001`.
- Aucun projet de `Apps/` ni bibliothèque `HackerOs.AppSdk*` ne référence
  `Platform.Core`, `Platform.Blazor`, `Infrastructure.*` ou `Server.*`. Aucun
  reste du mécanisme d'exemption hors de l'historique des documents.
- Injections dans `Apps/` : uniquement `IJSRuntime` (import du module de
  l'app), `MudBlazor.IDialogService`, et des types du SDK.
  `SdkBoundarySourceTests` parcourt tous les `.csproj` de `Apps/`, donc aussi
  `HackerOs.Apps.Terminal` et les nouvelles commandes.
- Capacités : chaque app et commande ajoutée ou modifiée déclare celles des API
  qu'elle appelle (par exemple `systemctl` : `services.manage` ; `htop` :
  `process.manage` ; `jobs`, `read`, `watch` : `shell.control` ; Terminal et
  CodeEditor : `shell.host`).
- Aucun appel d'API `[Obsolete]` dans `Apps/` ; depuis le gel du SDK 1.0, le
  SDK n'en contient plus (`BrowserClipboard`, `Get/SetFileReferencesAsync`
  retirés, ADR 0114 §3).
- Corrigé : la copie du terminal (Ctrl+C sur une sélection, Ctrl+Shift+C)
  écrivait `navigator.clipboard` directement, hors du presse-papiers de l'OS et
  sans respecter `clipboard.config` (ADR 0065). Elle lève désormais un vrai
  événement `copy` que le pont reçoit comme une copie native
  ([`terminal-emulator.md`](../dev/terminal-emulator.md)).

## Liste des tâches du chantier `rebuild/4-sdk-boundary`

- [x] Cible MSBuild `ValidateHackerOsSdkBoundary` (`HOSAPP0030`, `HOSSDK0001`), références directes seulement.
- [x] Propriété `HackerOsSdkLibrary` pour les bibliothèques SDK.
- [x] `SdkBoundaryInspector` dans le générateur (`HOSAPP0031`), option `--boundary-exempt`.
- [x] Liste `HackerOsSdkBoundaryExemption` (9 projets) et test qui l'empêche de grandir.
- [x] Test de sources `IJSRuntime` dans `HackerOs.Ecosystem.Tests`.
- [x] Retrait des références `Platform.Core` inutilisées (Calculator, HackPaint, IconViewer, TextEditor, Browser).
- [x] Projets de test `ManifestGenerator.Fixtures.ForbiddenReference` et `ForbiddenInjection`, tests unitaires de l'inspecteur.
- [x] ADR 0059 (numéros des codes), ce document, `app-model.md`, plan et index à jour.

## Liste des tâches du chantier `rebuild/4-sdk-user-and-apps`

- [x] `HackUser`, `HackUserInfo`, `HackEnvironment.GetFolderPath`, `HackSpecialFolder`.
- [x] `IAppExecutionContext` : `UserLoginName`, `UserDisplayName`, passerelles `Catalog` et `AppManagement`.
- [x] `HackApps`, `HackAppInfo`, `IAppCatalogGateway` et `AppCatalogGateway`.
- [x] `HackAppManagement`, `IAppManagementGateway` et `AppManagementGateway` (`apps.manage`).
- [x] Agent d'authentification de l'OS, courtier et fenêtre ([`admin-authentication-agent.md`](../dev/admin-authentication-agent.md)).
- [x] Migration de FileExplorer (« Open with », dossier personnel), TextEditor et CodeEditor ; FileExplorer perd sa référence à `Platform.Core`.
- [x] Onglet « Installed Apps » de Paramètres sur le SDK ; `apps.manage` déclarée par Paramètres.
- [x] Exemptions retirées : FileExplorer, TextEditor, CodeEditor ; Paramètres reste pour `4-sdk-system-admin`.
- [x] Tests : agent, passerelles, `HackUser`, fenêtre.

## Liste des tâches du chantier `rebuild/4-file-explorer`

- [x] `HackClipboard`, `HackClipboardFileList`, passerelle `IAppExecutionContext.Clipboard` ; `SystemClipboard` et `AppClipboardGateway` dans Platform Core.
- [x] `HackDirectory.CopyAsync`, `FileTransfer` et les ajouts de `FileView` ([`file-explorer.md`](../dev/file-explorer.md)).
- [x] Explorateur sur ces API ; capacités `clipboard.read`, `clipboard.write`, `notifications.post` déclarées. Aucune exemption à retirer (FileExplorer était déjà sorti de la liste).

## Liste des tâches du chantier `rebuild/4-sdk-system-admin`

- [x] Capacités `permissions.manage`, `server.connection.manage`, `system.power`, `system.storage.inspect` (dangereuses).
- [x] `HackSettings.OpenSystemAsync`, `HackSettingsDocument.SetManyAsync`, `IAppSystemSettingsGateway`.
- [x] `HackPermissionAdministration`, `HackServerConnection`, `HackSystem`, `HackStorageDiagnostics` et leurs passerelles Platform Core, avec l'agent d'authentification.
- [x] `SystemPowerService` (Platform Core) et appareil navigateur (`BrowserSystemPowerDevice`, `IndexedDbStorageInspector` dans l'hôte Ecosystem).
- [x] Paramètres et Diagnostic sur le SDK public ; exemptions retirées (liste vide).
- [x] Tests `SystemAdministrationGatewayTests` ; ADR 0060 ; doc [`system-administration.md`](system-administration.md).

## Liste des tâches du chantier `rebuild/4-sdk-boundary-final`

- [x] Vérifier que la liste d'exemptions est vide et que la solution compile sans diagnostic `HOSAPP0030`, `HOSAPP0031` ni `HOSSDK0001`.
- [x] Supprimer le mécanisme de `build/HackerOs.Apps.targets` (élément, cible `_HackerOsResolveSdkBoundaryExemption`, propriété, avertissement).
- [x] Supprimer l'option `--boundary-exempt` du générateur et la sévérité `warning` de `GeneratorDiagnostic`.
- [x] Remplacer les tests de la liste par `Build_targets_have_no_exemption_mechanism` et `No_app_or_sdk_project_overrides_its_boundary_role` ; le test `IJSRuntime` couvre toutes les apps.
- [x] Ce document, le plan (phase 4 cochée) et l'index à jour.

### Vérification finale (2026-10-16)

- [x] Build de la solution sans diagnostic de frontière ni avertissement ; références de `Apps/` et du SDK vérifiées.
- [x] Aucun reste du mécanisme d'exemption ; `SdkBoundarySourceTests` couvre les apps ajoutées depuis #113.
- [x] Injections et capacités des apps et commandes ajoutées par les PR #115 à #122.
- [x] Usages d'API `[Obsolete]` dans `Apps/` : aucun.
- [x] Copie du terminal passée par le pont du presse-papiers (ADR 0065), bundle xterm régénéré.
- [x] Suites `Tests/*.Tests` hors E2E vertes ; liens de `docs/` vérifiés par script ; index et plan à jour.
