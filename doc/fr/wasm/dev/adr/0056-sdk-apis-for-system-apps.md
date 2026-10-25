> [English version](../../../../en/wasm/dev/adr/0056-sdk-apis-for-system-apps.md)

# ADR 0056 : API du SDK qui remplacent les services internes

## Statut

Accepté le 2026-10-10.

- **Phase du plan :** phase 4 — SDK complet et apps de base
  ([`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), section 8.1).
- **Applique :** ADR 0055 (une app n'obtient un service de l'OS que par le
  SDK ; agent d'authentification).
- **Complète :** ADR 0049 : cinq capacités dangereuses entrent dans le
  catalogue (section 11). ADR 0048 : `HackerOs.Apps.CodeEditor` rejoint la
  liste de confiance de l'hôte (section 7). ADR 0044 : `HackSettings` ouvre
  aussi les documents de l'OS (section 8).
- **S'appuie sur :** ADR 0043 (façades `Hack*`, exceptions), ADR 0046 et 0057
  (`/proc`), ADR 0051 (`IShellSession`, `shell.control`).

## Contexte

L'ADR 0055 interdit aux apps les services internes listés dans son contexte.
Pour chacun, il faut une API publique, protégée par une capacité, qui couvre
le besoin réel de l'app. Une partie existe déjà dans le SDK et n'est
simplement pas utilisée :

| Besoin | Déjà dans le SDK |
|---|---|
| Lancer une app, ouvrir un fichier | `IAppIntentGateway` (`apps.launch`) |
| Lister et tuer des processus | `IAppProcessGateway` (`process.list`, `process.manage`) |
| Démarrer, arrêter, configurer un service | `IAppServiceControlGateway` (`services.manage`) |
| Nom de l'utilisateur et de la machine, répertoire courant | `HackEnvironment` |
| Presse-papiers (texte et références de fichiers) | `IAppClipboardGateway` |
| Télécharger un fichier vers le navigateur | `BrowserFileDownloader` (`HackerOs.AppSdk.Blazor`) |
| Lire un paramètre, surveiller un fichier | `HackSettings`, `HackFileSystemWatcher` |

## Décision

Chaque section donne la façade statique (forme recommandée), la passerelle sur
`IAppExecutionContext` qui la porte, la capacité exigée et le service interne
qu'elle remplace. « Administrateur » signifie que l'opération passe en plus
par l'agent d'authentification (ADR 0055, section 5). Les noms suivent
`System.*` avec le préfixe `Hack` (décision D3). Toutes les méthodes sont
asynchrones (décision D4) sauf les accesseurs purs.

### 1. Utilisateur courant

Remplace `ISessionService.CurrentPrincipal` (FileExplorer, TextEditor,
CodeEditor, Settings).

```csharp
HackUserInfo me = HackUser.Current;          // LoginName, DisplayName, HomeDirectory, IsAdministrator
string docs = HackEnvironment.GetFolderPath(HackSpecialFolder.MyDocuments);  // /home/benoit/Documents
```

- `HackUser.Current` décrit l'utilisateur **effectif** du processus : `root`
  et `/root` pour un processus lancé par `sudo` (ADR 0042), l'utilisateur de
  la session sinon. `IsAdministrator` vaut `true` pour un administrateur ou
  pour root.
- `HackSpecialFolder` (calqué sur `Environment.SpecialFolder`) :
  `UserProfile` (`~`), `Desktop` (`~/Desktop`), `MyDocuments`
  (`~/Documents`), `ApplicationData` (`~/.config`), `LocalApplicationData`
  (`~/.local/share`).
- Passerelle : aucune nouvelle ; la fabrique remplit `IAppExecutionContext`
  (`UserId`, `HomeDirectory`, `IsRootElevated`, `UserAuthority`), complétée
  par `UserLoginName` et `UserDisplayName`.
- **Capacité : aucune.** Sous Linux, tout processus connaît son utilisateur
  (`getuid`, `getpwuid`).

### 2. Catalogue des apps

Remplace `AppCatalog` et `IAppEnablementRegistry` (FileExplorer, Settings,
SystemMonitor).

```csharp
IReadOnlyList<HackAppInfo> apps = await HackApps.GetInstalledAsync();
IReadOnlyList<HackAppInfo> editors = await HackApps.GetHandlersAsync("/home/benoit/notes.md");
await HackApps.LaunchAsync("org.hackeros.text-editor", ["/home/benoit/notes.md"]);
await HackApps.OpenFileAsync("/home/benoit/notes.md");
```

- `HackAppInfo` : `Id`, `Name`, `Description`, `Kind`, `Category`, `Icon`,
  `Version`, `IsSystemApp`, `FileHandlers`. Pas de point d'entrée ni de type
  .NET.
- `GetInstalledAsync` ne rend que les apps installées pour l'utilisateur
  courant (ADR 0048 : une app désinstallée est cachée partout).
  `GetHandlersAsync` applique les associations de fichiers et le même filtre
  (« Ouvrir avec »).
- `HackApps.Changed` signale une installation ou une désinstallation.
- `LaunchAsync` et `OpenFileAsync` sont des façades sur `IAppIntentGateway`.
- Passerelle : `IAppCatalogGateway Catalog`.
- **Capacités :** aucune pour la lecture (la liste des apps installées est
  publique, comme `/usr/share/applications`) ; `apps.launch` (normale) pour
  lancer.

### 3. Gestion des apps

Remplace `IAppInstallationService` et `AppCatalog` dans l'onglet « Installed
Apps » de Paramètres.

```csharp
IReadOnlyList<HackAppInstallInfo> all = await HackAppManagement.GetAllAsync();
await HackAppManagement.UninstallAsync("org.hackeros.calculator", HackInstallScope.User);
await HackAppManagement.ReinstallAsync("org.hackeros.calculator", HackInstallScope.Machine);
```

- `HackAppInstallInfo` : l'app (`HackAppInfo`), son état (`Installed`,
  `UninstalledForUser`, `UninstalledForMachine`) et la liste des apps qui
  partiraient avec elle (`GetUninstallClosure` actuel).
- Portée `User` : l'utilisateur pour lui-même. Portée `Machine` :
  **administrateur**.
- Une app système lève `InvalidOperationException` (ADR 0048 : jamais
  désinstallable).
- Passerelle : `IAppManagementGateway AppManagement`.
- **Capacité :** `apps.manage` (dangereuse, déjà au catalogue et réservée à
  cette API par l'ADR 0049).

### 4. Processus

Remplace `IProcessManager` et `AppLifecycleOrchestrator` (SystemMonitor).

```csharp
HackProcess[] all = await HackProcess.GetProcessesAsync();
HackProcess p = await HackProcess.GetProcessByIdAsync(42);
await p.RefreshAsync();
await p.KillAsync();
```

- Calqué sur `System.Diagnostics.Process` : `Id`, `ParentId`, `ProcessName`,
  `AppId`, `Kind`, `UserName`, `State`, `StartTime`, `WorkingSet64`,
  `CpuUsage`, `CommandLine`, `WorkingDirectory`, `ReadBytes`, `WriteBytes`.
- **Les données viennent de `/proc`** (plan, section 8.2) : la façade lit
  `/proc/<pid>/status`, `cmdline`, `cwd` et `io` (ADR 0046 et 0057) par la
  passerelle fichier. Elle voit donc exactement ce que `ps` voit : son propre
  processus, et les autres avec `process.list`.
- `KillAsync` et `CloseMainWindowAsync` (arrêt poli avec délai, qui remplace
  `AppLifecycleOrchestrator.StopAsync`) passent par `IAppProcessGateway` :
  tuer est un appel système, pas une écriture de fichier.
- **Capacités :** `process.list` (normale) pour voir les autres processus ;
  `process.manage` (dangereuse) pour arrêter un autre processus que le sien.
  C'est la règle actuelle de `AppProcessGateway`, inchangée.
- Pas d'abonnement aux événements bruts de l'OS : un gestionnaire de tâches
  relit `/proc` à intervalle régulier, comme `top`.

### 5. Services

Remplace `AppCatalog` (filtré sur `AppKind.Service`) et l'orchestrateur dans
l'onglet Services de SystemMonitor.

```csharp
HackServiceController[] services = await HackServiceController.GetServicesAsync();
HackServiceController svc = services[0];
await svc.StartAsync();
await svc.SetStartTypeAsync(ServiceStartMode.Automatic);
```

- Calqué sur `System.ServiceProcess.ServiceController` : `ServiceName` (id de
  l'app), `DisplayName`, `Status` (`Running`, `Stopped`), `StartType`.
- La liste vient de `HackApps.GetInstalledAsync` ; l'état, de
  `HackProcess`.
- `StartAsync`, `StopAsync`, `SetStartTypeAsync` : façades sur
  `IAppServiceControlGateway`.
- **Capacités :** aucune pour lister ; `services.manage` (dangereuse) pour
  agir.

### 6. Réseau

Remplace `ISimulatedNetworkService` (Browser, `ping`, `curl`, `cat`, `nmap`)
et, dans les commandes, `IServerConnectionService` et `IProxyClient`.

```csharp
IPAddress[] ips = await HackDns.GetHostAddressesAsync("hackmail.sim");
HackPingReply reply = await HackPing.SendAsync("10.0.0.5", TimeSpan.FromSeconds(1));
using HackHttpClient http = new(cookies);
HackHttpResponse page = await http.GetAsync("http://hackmail.sim/inbox");
IReadOnlyList<HackPortProbeResult> ports = await HackTcpClient.ProbeAsync("10.0.0.5", 1, 1024);
```

- `HackDns` : résolution et résolution inverse. `HackPing` : un écho.
  `HackHttpClient` : `GetAsync`, `PostAsync`, `SendAsync`, redirections
  suivies, cookies dans un `HackCookieContainer` que l'app garde (le
  navigateur en a un par onglet). La réponse porte le statut, les en-têtes, le
  corps et, pour un site simulé, la page structurée qu'utilise le navigateur.
  `HackTcpClient.ProbeAsync` : sondage de ports pour `nmap`.
- **Repli sur le réseau réel :** l'OS, et non plus l'app, décide. Il essaie
  d'abord le réseau simulé ; si l'hôte n'y existe pas, que l'app a
  `network.real.access` et que le serveur optionnel est connecté, il passe par
  le proxy du serveur (ADR 0023, 0035) en gérant lui-même le jeton d'accès.
  L'app ne voit ni le jeton ni l'état du serveur. Sans serveur, le résultat
  est « hôte introuvable » ; aucune exception ne signale l'absence du serveur.
- Les types de données du réseau simulé (`SimulatedPage`…) restent dans
  `Simulation.Abstractions` ; seules les façades et `IAppNetworkGateway
  Network` sont nouvelles.
- **Capacités :** `network.simulated.read` (normale) pour DNS, ping, GET et
  sondage ; `network.simulated.write` (normale) pour POST ;
  `network.real.access` (dangereuse) pour le repli réel. Classements
  inchangés.
- `HackerOs.Commands` perd ses références à `Platform.Core` et
  `Server.Contracts`. `ShellErrorText.Describe`, seul autre emprunt (`cd`),
  passe dans le SDK (`HackerOs.AppSdk`, à côté de `IShellSession`).

### 7. Hôte de shell

Remplace `ShellInterpreter`, `ISessionService` et `IElevationService` dans
`TerminalPanel` (bibliothèque `HackerOs.AppSdk.Terminal`, utilisée par
Terminal et CodeEditor).

```csharp
await using IHackShell shell = await HackShell.StartAsync(new HackShellOptions
{
    WorkingDirectory = HackEnvironment.GetFolderPath(HackSpecialFolder.UserProfile),
    CredentialPrompt = myTerminalPrompt,
});
int status = await shell.ExecuteAsync("ls ~ | grep notes", stdin, stdout, stderr, ct);
HackShellCompletion completion = await shell.CompleteAsync("cat ~/Doc", cursor: 9);
```

- `HackShell.StartAsync` crée un shell `hsh` interactif pour l'utilisateur
  courant : un **processus enfant** de l'appelant, avec son répertoire et son
  environnement (ADR 0050). Les commandes qu'il lance sont ses enfants, comme
  sous Linux (terminal → shell → commande).
- `IHackShell` : `ExecuteAsync` (une ligne, avec flux), `CompleteAsync`
  (section 4 de l'ADR 0058), `GetInstalledCommands`, `WorkingDirectory`,
  `ExitStatus` (`$?`), `DisposeAsync` (fin du processus shell).
- Les demandes d'identifiants (`sudo`, approbation des capacités dangereuses
  en mode texte, ADR 0049) passent par le `HackCredentialPrompt` fourni par
  l'appelant : le terminal lit la saisie masquée, l'OS vérifie. Le
  terminal ne vérifie plus rien lui-même.
- Distinct de `IShellSession` (ADR 0051) : `IShellSession` est ce qu'une
  **commande** voit du shell qui l'a lancée (`shell.control`) ; `IHackShell`
  est ce qu'un **émulateur de terminal** voit du shell qu'il héberge.
- Passerelle : `IAppShellHostGateway ShellHost`.
- **Capacité : `shell.host` (nouvelle, dangereuse).** Une commande lancée par
  le shell s'exécute avec **ses** capacités, pas celles de l'app hôte, et sans
  fenêtre visible : une app qui n'a que `notifications.post` pourrait sinon
  lire tout `~` par `cat`. Risque : « Exécuter des commandes en votre nom, avec
  les droits de ces commandes. »
- **Liste de confiance :** `HackerOs.Apps.CodeEditor` reçoit
  `IsSystemApp="true"` dans l'hôte (ADR 0048), comme Terminal. C'est une app
  livrée avec l'OS, et son panneau terminal ne doit pas déclencher une
  approbation administrateur au premier lancement.

### 8. Paramètres de l'OS

Remplace `ThemePreferenceService` dans Paramètres.

```csharp
await using HackSettingsDocument appearance = await HackSettings.OpenSystemAsync("appearance");
string theme = appearance.Get<string>("theme");
await appearance.SetAsync("theme", "gothic");                         // ~/.config/hackeros/appearance.config
await appearance.SetAsync("theme", "gothic", SettingLayer.System);    // /etc/hackeros/appearance.config
```

- `HackSettings.OpenSystemAsync(documentId)` ouvre un document de l'OS
  (`/etc/hackeros/<doc>.config` et `~/.config/hackeros/<doc>.config`), avec le
  schéma déclaré par l'OS. Mêmes couches, verrou `[locked]` et tolérance aux
  clés invalides que pour une app (ADR 0044).
- **Capacité : aucune.** Ce sont les droits du fichier qui décident
  (décision D16) : tout le monde lit, l'utilisateur écrit sa couche, la couche
  `/etc` appartient à root. Une écriture refusée lève
  `UnauthorizedAccessException` ; l'app peut proposer de relancer en `sudo`.
- `ThemePreferenceService` reste dans la plateforme : il surveille déjà ces
  fichiers et applique le thème quand ils changent.

### 9. Administration des permissions

Remplace `AppPermissionAdministration` (tiré de `IServiceProvider`) et
`ISessionService` dans l'onglet Permissions de Paramètres.

```csharp
DangerousCapabilityPolicy policy = await HackPermissionAdministration.GetPolicyAsync();
IReadOnlyList<HackAppPermissionInfo> rows = await HackPermissionAdministration.GetOverviewAsync();
await HackPermissionAdministration.SetMachineDecisionAsync(appId, capability, HackPermissionDecision.Allow);
await HackPermissionAdministration.RemoveOwnDenialAsync(appId, capability);
```

- `SetPolicyAsync` et `SetMachineDecisionAsync` : **administrateur**. Ils ne
  prennent plus de mot de passe en paramètre : l'agent le demande.
- `RemoveOwnDenialAsync` : l'utilisateur pour lui-même, sans administrateur.
- Passerelle : `IAppPermissionAdministrationGateway PermissionAdministration`.
- **Capacité : `permissions.manage` (nouvelle, dangereuse).** Risque :
  « Accorder ou retirer à d'autres apps des permissions dangereuses. »

### 10. Serveur optionnel et synchronisation

Remplace `IServerConnectionService`, `IFileSystemSyncService`,
`IGrantsSyncService` et `IFileAssociationsSyncService` dans Paramètres.

```csharp
HackServerConnectionState state = await HackServerConnection.GetStateAsync();
await HackServerConnection.ConnectAsync(serverUrl, login, password);
await HackServerConnection.SyncAsync(HackSyncDomain.FileSystem, HackSyncDirection.Push);
IReadOnlyList<HackSyncConflict> conflicts = await HackServerConnection.GetConflictsAsync();
```

- `GetStateAsync`, `ConnectAsync` (compte existant), `CreateAccountAsync`,
  `DisconnectAsync`, `SyncAsync(domaine, sens)` pour `FileSystem`, `Grants` et
  `FileAssociations`, `GetConflictsAsync`.
- Le mot de passe du **compte serveur** est saisi par l'app : c'est un
  identifiant du service distant, pas un mot de passe local (ADR 0055,
  section 5).
- Sans serveur configuré, l'état est « non connecté » ; rien ne lève
  d'exception à cause de l'absence du serveur (`AGENTS.md`).
- Passerelle : `IAppServerConnectionGateway ServerConnection`.
- **Capacité : `server.connection.manage` (nouvelle, dangereuse).** Risque :
  « Connecter cette machine à un serveur et y envoyer vos fichiers et
  permissions. »

### 11. Système : redémarrage, réinitialisation, inspection du stockage

Remplace les appels `IJSRuntime` de Paramètres (`location.reload`, suppression
de la base IndexedDB) et `Infrastructure.Browser` dans Diagnostic.

```csharp
await HackSystem.RebootAsync();
await HackSystem.FactoryResetAsync();
HackStorageSnapshot snapshot = await HackStorageDiagnostics.GetSnapshotAsync();
```

- `RebootAsync` : vide les écritures en attente (journaux, ADR 0045), termine
  les processus, puis recharge la page. **Administrateur**, comme `reboot`
  sous Linux.
- `FactoryResetAsync` : supprime la base IndexedDB de l'OS puis redémarre.
  **Administrateur.**
- `GetSnapshotAsync` : l'instantané que lit Diagnostic aujourd'hui (magasins,
  enregistrements, chemins résolus), avec des types de données déplacés dans
  le SDK. L'implémentation reste dans `Infrastructure.Browser`.
- Diagnostic copie dans le presse-papiers par `IAppClipboardGateway`
  (`clipboard.write`) au lieu de `navigator.clipboard`, et enregistre le JSON
  par `BrowserFileDownloader`.
- Passerelles : `IAppSystemGateway System`, `IAppStorageDiagnosticsGateway
  StorageDiagnostics`.
- **Capacités (nouvelles, dangereuses) :**
  - `system.power` (redémarrage et réinitialisation). Risque : « Redémarrer
    ou effacer entièrement cette machine. »
  - `system.storage.inspect` (instantané brut du stockage). Risque : « Lire
    le stockage brut de la machine, y compris les fichiers de tous les
    utilisateurs. »
- Diagnostic n'est pas une app système : son premier lancement demande
  l'approbation d'un administrateur. C'est voulu, l'instantané contourne les
  droits des fichiers.

### 12. Presse-papiers de fichiers

Complète `IAppClipboardGateway` pour couper et copier dans l'explorateur
(ADR 0058).

```csharp
await HackClipboard.SetFileDropListAsync(paths, HackClipboardFileOperation.Cut);
HackClipboardFileList? list = await HackClipboard.GetFileDropListAsync();   // Operation + Paths
```

- Calqué sur `Clipboard.SetFileDropList` ; le contenu suit le format
  `x-special/gnome-copied-files` de GNOME : première ligne `copy` ou `cut`,
  puis une URI `file://` par ligne. Une app qui lit le presse-papiers en texte
  y voit les chemins.
- `SetFileReferencesAsync` et `GetFileReferencesAsync` actuels deviennent des
  formes de « copier » et sont marqués `[Obsolete]` (ADR 0019).
- **Capacités :** `clipboard.read`, `clipboard.write` (normales, inchangées).

### 13. Récapitulatif des capacités

| Capacité | Classement | Nouvelle | API |
|---|---|---|---|
| `apps.launch` | normale | non | `HackApps.LaunchAsync`, `OpenFileAsync` |
| `apps.manage` | dangereuse | non | `HackAppManagement` |
| `process.list` | normale | non | `HackProcess` (autres processus) |
| `process.manage` | dangereuse | non | `HackProcess.KillAsync` (autre processus que le sien) |
| `services.manage` | dangereuse | non | `HackServiceController` (actions) |
| `network.simulated.read` / `.write` | normale | non | `HackDns`, `HackPing`, `HackHttpClient`, `HackTcpClient` |
| `network.real.access` | dangereuse | non | repli réel des mêmes API |
| `shell.host` | dangereuse | **oui** | `HackShell` |
| `permissions.manage` | dangereuse | **oui** | `HackPermissionAdministration` |
| `server.connection.manage` | dangereuse | **oui** | `HackServerConnection` |
| `system.power` | dangereuse | **oui** | `HackSystem` |
| `system.storage.inspect` | dangereuse | **oui** | `HackStorageDiagnostics` |
| `clipboard.read` / `.write` | normale | non | `HackClipboard` |
| — | — | — | `HackUser`, `HackEnvironment.GetFolderPath`, `HackApps` (lecture), `HackSettings.OpenSystemAsync` |

Chaque app met à jour sa définition `[AppManifest]` pour déclarer ce qu'elle
utilise. Une app système reçoit ces capacités d'office (ADR 0049).

## Conséquences

- Chaque app système se réécrit sur ces API ; les chantiers sont dans
  l'ADR 0058.
- La logique du repli réseau, du jeton du serveur, de la vérification
  d'administrateur et de l'effacement de la base passe dans Platform Core,
  derrière une passerelle testable.
- Le gestionnaire de tâches dépend de `/proc` : un champ qui manque à
  l'écran manque d'abord dans `/proc`, et l'ADR 0057 l'y ajoute.
- Cinq nouvelles capacités dangereuses : une app tierce qui en déclare une
  passe par la fenêtre d'approbation.

## Alternatives écartées

- **Une seule capacité « administration système »** pour permissions,
  serveur, alimentation et stockage : moins de lignes au catalogue, mais un
  administrateur ne saurait pas ce qu'il approuve.
- **`shell.host` normale :** elle rendrait la capacité inutile, puisque les
  commandes ont leurs propres droits (voir section 7).
- **Instantané du stockage en fichiers sous `/sys/kernel/debug`**, comme
  `debugfs` : séduisant pour « tout est fichier », mais l'instantané est un
  document JSON unique, pas une arborescence, et il faudrait un troisième
  fournisseur virtuel pour une seule app de développement.
- **Événements de processus poussés** au gestionnaire de tâches : plus
  réactif, mais il faudrait exposer le bus d'événements de l'OS ; la lecture
  périodique de `/proc` suffit et c'est ce que fait `top`.

## Références

- [`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), sections 4
  (D3, D4, D5, D16, D21), 8.1 et 8.2.
- ADR 0019, 0023, 0035, 0042, 0043, 0044, 0045, 0046, 0048, 0049, 0050, 0051,
  0055, 0057, 0058.
- `Shared/HackerOs.Simulation.Abstractions/Gateways/AppGatewayContracts.cs`,
  `Shared/HackerOs.AppSdk/Clipboard/IAppClipboardGateway.cs`,
  `Platform/HackerOs.Platform.Core/Policy/AppPermissionAdministration.cs`,
  `Platform/HackerOs.Platform.Core/Apps/AppInstallationContracts.cs`.
