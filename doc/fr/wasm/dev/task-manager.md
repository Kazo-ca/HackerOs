> [English version](../../../en/wasm/dev/task-manager.md)

# Gestionnaire de tâches

Mise en œuvre du chantier `rebuild/4-task-manager` de la phase 4 : ADR 0056
sections 4 et 5 (`HackProcess`, `HackServiceController`) et ADR 0058 section 2
(réécriture de `HackerOs.Apps.SystemMonitor`). Les mesures lues viennent du
chantier `4-proc-metrics` ([`system-metrics.md`](system-metrics.md),
ADR 0057).

## Rôle

L'app « System Monitor » (`org.hackeros.system-monitor`) est le gestionnaire de
tâches de HackerOS. Comme `top`, elle relit `/proc` toutes les secondes ; tout
ce qu'elle affiche, une app tierce ou `ps` peut le lire aussi.

| Onglet | Contenu | Source |
| --- | --- | --- |
| Processes | PID, nom, utilisateur, état, CPU, mémoire, lecture et écriture disque par seconde ; tri, filtre, sélection multiple, « End task » (`SIGTERM`), « Force kill » (`SIGKILL`, menu contextuel), détail | `HackProcess` → `/proc/<pid>/status`, `stat`, `cmdline`, `cwd`, `io` |
| Performance | résumé (ancien onglet Overview), graphique du CPU total ou par cœur, graphique de la mémoire, section matériel (ancien onglet « Hardware & System Info ») | `/proc/stat`, `/proc/meminfo`, `/proc/uptime`, `/proc/loadavg`, `/proc/cpuinfo` |
| Disk | espace occupé et libre de chaque disque ; débits de lecture et d'écriture, temps d'activité, compteurs | `HackDriveInfo`, `/proc/diskstats` |
| Network | débits reçus et émis par interface, paquets, erreurs, pertes | `/proc/net/dev` |
| Services | services installés, état, PID, type de démarrage, démarrer et arrêter | `HackServiceController` |

L'app ne référence plus que le SDK : `IProcessManager`, `AppCatalog`,
`AppLifecycleOrchestrator` et `IEventBus` ont disparu, et son exemption de
l'ADR 0055 est retirée de `build/HackerOs.Apps.targets`.

## Architecture

### SDK

| Type | Projet | Rôle |
| --- | --- | --- |
| `HackProcess`, `HackProcessState`, `HackProcessIoCounters` | `HackerOs.AppSdk` (`Diagnostics/`) | un processus, sur le modèle de `System.Diagnostics.Process`, lu dans `/proc` par la passerelle fichier de l'app |
| `HackServiceController`, `HackServiceControllerStatus` | `HackerOs.AppSdk` (`ServiceProcess/`) | un service, sur le modèle de `System.ServiceProcess.ServiceController` |
| `HackLineChart`, `HackChartSeries`, `HackChartGeometry`, `HackChartSize` | `HackerOs.AppSdk.Blazor` (`Charts/`) | graphique en courbes SVG natif, sans bibliothèque ni JavaScript |

### App (`Apps/System/HackerOs.Apps.SystemMonitor`)

| Fichier | Rôle |
| --- | --- |
| `SystemMonitorWindow.razor` (+ `.cs`, `.css`, `.js`) | fenêtre : boucle de rafraîchissement, onglets `MudTabs`, « End task » et « Force kill » avec confirmation |
| `Metrics/ProcFiles.cs` | parseurs de `stat`, `meminfo`, `cpuinfo`, `uptime`, `loadavg`, `diskstats`, `net/dev` (formats Linux) |
| `Metrics/ProcReader.cs` | une lecture de `/proc` par le SDK : processus, fichiers système, disques |
| `Metrics/SystemMetrics.cs` | différences entre deux lectures et historique de 60 valeurs (`MetricHistory`) |
| `Metrics/Units.cs` | tailles, débits, pourcentages et durées lisibles, indépendants de la culture |
| `Processes/ProcessTable.cs` | tri, filtre, sélection multiple, processus du panneau de détail |
| `Processes/EndTaskRunner.cs` | arrêt d'une sélection, refus signalés processus par processus |
| `Tabs/*.razor` | un composant par onglet et le panneau de détail, chacun avec son CSS isolé |

Tous les composants des onglets héritent de `HackComponentBase` : leurs
gestionnaires d'événements s'exécutent dans le contexte ambiant de l'app
(ADR 0043). `PerformanceTab`, `DiskTab` et `NetworkTab`, qui n'appellent pas le
SDK, y sont passés en phase 5 pour que leur temps soit compté à l'app et non au
compositeur (ADR 0067, [`resource-accounting.md`](resource-accounting.md#apps-existantes)).

## Fonctionnement

### Rafraîchissement

- Un `PeriodicTimer` d'une seconde (`SystemMonitorWindow.RefreshInterval`)
  appelle `ProcReader.ReadAsync`, puis `SystemMetrics.Apply` et
  `ProcessTable.SetRows`. Une lecture en cours n'est jamais doublée.
- **Pause** : quand la fenêtre est réduite (le bureau la masque) ou que
  l'onglet du navigateur est caché, le module `SystemMonitorWindow.razor.js`
  le signale (`IntersectionObserver`, `visibilitychange`) et la boucle saute
  ses lectures. À la restauration, une lecture est faite tout de suite. Un
  bouton « Pause » suspend aussi le rafraîchissement à la main.
- **Différences** : la première lecture sert de base. Le CPU est la part de
  temps occupé entre deux lectures de `/proc/stat`, comme `top` (aucune
  horloge nécessaire, le temps occupé et le temps libre sont sur la même
  horloge simulée). Les débits disque, réseau et par processus sont des
  octets réels divisés par le temps réel écoulé. Un compteur qui recule (OS
  redémarré) donne `0`.

### Processus

- Mention « estimated » (ADR 0066) : l'en-tête des colonnes CPU et Memory porte
  une petite étiquette en pointillés tant qu'une ligne au moins vient du
  simulateur (`CpuSource:` / `MemSource:` de `/proc/<pid>/status` différent de
  `measured`, lus dans `HackProcess.Status`). Son infobulle le dit. Voir
  [`resource-accounting.md`](resource-accounting.md). Avec la mémoire mesurée
  (ADR 0068, [`memory-accounting.md`](memory-accounting.md)), la colonne
  Memory montre `VmRSS` mesuré pour toutes les apps intégrées et l'étiquette
  disparaît de cette colonne dès que toutes les lignes sont mesurées ; le
  détail d'un processus montre les couches (`RssAnon`, `RssFile`, `VmData`,
  `VmExe`, `HackAllocBytes`) dans son `status` complet. L'app n'a pas changé :
  elle lit toujours `HackProcess`.
- « Not responding » (ADR 0067) : quand `/proc/<pid>/status` porte
  `Responding: 0` (`HackProcess.Responding` faux), la colonne d'état affiche
  « Not responding » en rouge à la place de l'état, avec une infobulle ; le tri
  par état en tient compte. Affichage seulement : le gestionnaire n'arrête
  rien de lui-même, « End task » reste son seul moyen. Voir
  [`cpu-accounting.md`](cpu-accounting.md). Pour une app à fenêtre, la fenêtre
  elle-même affiche aussi « (Not responding) » et la boîte « Attendre |
  Terminer l'application » (ADR 0127,
  [`process-priority-and-unresponsive-windows.md`](process-priority-and-unresponsive-windows.md)).
  Pour un processus dans un Web Worker (ADR 0070), l'état apparaît **pendant**
  le blocage : son worker ne bat plus depuis 5 s, et le reste de l'interface
  continue de répondre ; « End task » puis « Force kill » terminent le worker
  ([`worker-processes.md`](worker-processes.md)).
- Priorité (ADR 0127 §1) : colonne « Priority » (`ProcessColumn.Priority`,
  `ProcessRow.Nice`) affichée par un bouton bascule (`ProcessTable.ShowPriority`,
  masquée au départ) ; elle montre la valeur `nice` lue dans
  `/proc/<pid>/stat` et se trie. Le gestionnaire l'affiche seulement : on la
  change avec `renice`. Voir [`process-priority-and-unresponsive-windows.md`](process-priority-and-unresponsive-windows.md).
- Tâches de fond : une tâche `HackBackground.RunAsync` est un processus enfant
  de son app (ADR 0128, 0140) ; elle apparaît comme une ligne à part, nommée
  d'après la tâche (`Name:` de `status`, lignes `Job:` et `Host:`), et
  « End task » l'arrête comme les autres ([`background-jobs.md`](../framework/background-jobs.md)).
- Un processus tué par l'OOM killer (ADR 0126) ou par sa limite `rss`/`cpu`
  (ADR 0125) disparaît simplement de la table à la lecture suivante ; une
  victime de l'OOM killer laisse sa trace dans `dmesg`
  ([`oom-killer.md`](oom-killer.md), [`process-limits.md`](process-limits.md)).
- Tri par colonne (clic sur l'en-tête ; même colonne : sens inverse ; colonne
  de mesure : plus grand d'abord). À valeur égale, ordre par PID, pour que les
  lignes ne sautent pas à chaque seconde.
- Filtre texte sur le nom, l'id d'app et le PID, insensible à la casse. Une
  ligne sélectionnée que le filtre cache est désélectionnée : « End task »
  n'atteint jamais une ligne invisible.
- Sélection : clic, Ctrl+clic (bascule), Maj+clic (plage depuis le dernier
  clic, ajoutée avec Ctrl+Maj), Ctrl+A, Échap. La sélection est gardée par PID
  d'une lecture à l'autre.
- Clavier dans la table : Suppr pour « End task », Maj+Suppr pour « Force
  kill », Entrée pour le détail. Clic droit sur une ligne : menu Details, End
  task, Force kill (la ligne est sélectionnée seule si elle ne l'était pas).
- **End task** (ADR 0069 §4, `SIGTERM`) : une fenêtre de confirmation liste les
  processus, puis chacun reçoit `HackProcess.TerminateAsync` : il est averti
  (`StopRequested`, raison `Terminated`), son jeton est annulé, et il a 5
  secondes pour se terminer avant que l'OS le tue. Une app à fenêtre garde un
  brouillon de ses modifications non enregistrées puis sa fenêtre se ferme.
  Les processus sélectionnés sont signalés ensemble, pas l'un après l'autre :
  trois processus bloqués ne coûtent pas 15 secondes.
- **Force kill** (menu contextuel, `SIGKILL`) : confirmation distincte (« it
  cannot react »), puis `HackProcess.KillAsync` : immédiat, sans
  `StopRequested`, descendants compris.
- Dans les deux cas, un refus (`process.manage` absent) est noté pour ce
  processus et n'arrête pas les autres ; un processus déjà terminé compte comme
  arrêté. Le résultat s'affiche au-dessus de la table, dans l'ordre de la
  sélection.
- **Détail** (double clic, Entrée ou « Details ») : vue d'ensemble, ligne de
  commande, répertoire courant, parent et enfants cliquables, compteurs `io`,
  `status` complet, environnement (`environ`) s'il est lisible. `io` et
  `environ` sont en `0400` : pour le processus d'un autre utilisateur, le
  panneau l'indique au lieu d'une erreur.

### Services

Colonnes État (`active`, `activating`, `deactivating`, `inactive`, `failed`
avec sa raison, ADR 0097) et Santé (santé et texte rapportés par le service).
Une ligne `failed` est mise en évidence et offre « Reset » (`reset-failed`).
L'état vient du gestionnaire de services (`RefreshStatusAsync` à chaque
rafraîchissement) ; seul un OS qui ne le fournit pas retombe sur `/proc`.
La liste et les types de démarrage sont lus à l'ouverture de l'onglet et après
chaque action ; l'état et le PID suivent les processus lus chaque seconde par
la fenêtre, donc un service qui s'arrête apparaît arrêté sans recharger la
liste. Un type de démarrage illisible (service d'une autre app sans
`services.manage`) s'affiche « unknown ».

## API du SDK

### `HackProcess` (ADR 0056 §4)

```csharp
HackProcess[] all = await HackProcess.GetProcessesAsync();     // par PID croissant
HackProcess me = await HackProcess.GetCurrentProcessAsync();
HackProcess p = await HackProcess.GetProcessByIdAsync(42);     // ArgumentException s'il n'existe pas
await p.RefreshAsync();                                        // HasExited si /proc/42 a disparu
IReadOnlyDictionary<string, string>? env = await p.GetEnvironmentVariablesAsync();
bool closedByItself = await p.CloseMainWindowAsync(TimeSpan.FromSeconds(5));
bool endedByItself = await p.TerminateAsync();                 // SIGTERM, 5 s (ADR 0069)
await p.KillAsync();                                           // SIGKILL
```

- Propriétés : `Id`, `ParentId`, `ProcessName`, `AppId`, `Kind`, `UserId`,
  `UserName`, `SessionId`, `State`, `StateCode`, `StartTime`, `WorkingSet64`
  (`VmRSS`), `PrivateMemorySize64` (`RssAnon` quand la mémoire est mesurée,
  sinon égal à `WorkingSet64`, ADR 0068), `CpuUsage`, `Arguments`, `CommandLine`, `WorkingDirectory`, `IoCounters`,
  `ReadBytes`, `WriteBytes`, `Status`, `HasExited` ; `GetChildren(liste)`.
- Temps CPU (ADR 0067, comme `System.Diagnostics.Process`) :
  `UserProcessorTime` (`utime`), `PrivilegedProcessorTime` (`stime`),
  `TotalProcessorTime`, lus dans `/proc/<pid>/stat` (résolution 10 ms) ;
  `Responding` (`Responding:` de `status`, toujours vrai quand le CPU n'est pas
  mesuré). `ParseStatTimes(texte)` analyse une ligne `stat`. Voir
  [`cpu-accounting.md`](cpu-accounting.md).
- Priorité (ADR 0127 §1) : `Nice` (champ 19 de `stat`, de -20 à 19) et
  `BasePriority` (`20 + Nice`, le `PR` de `top`) ; `ParseStatNice(texte)`.
  Changer la priorité passe par `IAppProcessGateway.SetNice` (commande `renice`).
- Limites (ADR 0125, [`process-limits.md`](process-limits.md)) :
  `GetResourceLimitsAsync()` lit `/proc/<pid>/limits` (`HackResourceLimits` :
  `Cpu`, `Rss`, `NoFile`, `NProc`, `Enforcement`, `IsCpuMeasured`,
  `IsMemoryMeasured`) ; `SetResourceLimitAsync(kind, souple, dure)` en change
  une comme `prlimit` (`process.manage` pour un autre processus, `sudo` pour
  monter une limite dure ou toucher le processus d'un autre utilisateur).
- Lecture : `/proc` (liste et date de création de `/proc/<pid>` comme
  `StartTime`), `status`, `stat`, `cmdline`, `cwd` (cible du lien), `io`, `environ`.
  Visibilité de `/proc` : son propre processus, les autres avec
  `process.list`. Lire `/proc` demande `filesystem.system.read`. Par la
  passerelle (`IAppProcessGateway`), une app voit, arrête et tue aussi ses
  propres descendants (ses tâches `HackBackground`) sans `process.list` ni
  `process.manage` (ADR 0140 §7).
- Action : `KillAsync` (`SIGKILL` : immédiat, descendants compris, pas de
  `StopRequested`), `TerminateAsync` (`SIGTERM` d'un autre processus, raison
  `Terminated`, `DefaultTerminateTimeout` = 5 s) et `CloseMainWindowAsync`
  (même arrêt poli avec la raison `CloseRequested`, `DefaultCloseTimeout`)
  passent par `IAppProcessGateway`. Les deux arrêts polis renvoient `false`
  quand l'OS a dû tuer le processus au délai. Voir ADR 0069 et
  [`app-sdk.md`](../framework/app-sdk.md#arrêt-propre-et-points-darrêt-adr-0069). Exceptions :
  `HackUnauthorizedAccessException` (capacité `process.manage` absente pour un
  autre processus), `InvalidOperationException` (processus déjà terminé).

### `HackServiceController` (ADR 0056 §5)

```csharp
HackServiceController[] services = await HackServiceController.GetServicesAsync();
HackServiceController sync = services[0];
await sync.StartAsync();
await sync.SetStartTypeAsync(ServiceStartMode.Automatic);
```

- Propriétés : `ServiceName` (id d'app), `DisplayName`, `Description`,
  `Status` (`Stopped`, `StartPending`, `StopPending`, `Running`, valeurs de
  `ServiceControllerStatus`, déduit de l'état actif : `failed` donne
  `Stopped`), `StartType` (`ServiceStartMode?`), `ProcessId`.
- États façon systemd (ADR 0097) : `ActiveState` (`HackServiceActiveState` :
  `Inactive`, `Activating`, `Active`, `Deactivating`, `Failed`), `Result`
  (`HackServiceResult` : `Success`, `ExitCode`, `Exception`, `Killed`,
  `Timeout`), `Health` (`HackServiceHealth`), `StatusText`,
  `ActiveStateChangedAt`, `ExitCode`. `ResetFailedAsync()` efface `failed` ;
  `RefreshStatusAsync()` relit l'état seul. `HackServiceStates.ToSystemdName`
  donne les mots de systemd.
- Liste : `HackApps.GetInstalledAsync` filtrée sur `AppKind.Service`, triée
  par nom. État : `HackProcess`. Type de démarrage et actions :
  `IAppServiceControlGateway`.
- Capacités : aucune pour lister ; `services.manage` pour agir sur le service
  d'une autre app et pour lire son type de démarrage. Échecs :
  `HackUnauthorizedAccessException` (refus), `InvalidOperationException`
  (service désinstallé, désactivé ou en échec).

### `HackLineChart` (`HackerOs.AppSdk.Blazor.Charts`)

```razor
<HackLineChart Title="Total CPU usage"
               Series="@([new HackChartSeries("Total", cpuHistory)])"
               Maximum="100" Filled="true" Size="HackChartSize.Large"
               ValueFormatter="FormatPercent" />
```

- Paramètres : `Series`, `Title`, `Capacity` (60 par défaut), `Maximum`
  (`null` : échelle automatique arrondie à 1, 2 ou 5 × 10ⁿ, jamais sous
  `MinimumScale`), `ValueFormatter`, `Filled`, `ShowLegend`, `TimeSpanLabel`,
  `Size` (`Small`, `Medium`, `Large`), `Class`.
- La valeur la plus récente est au bord droit ; les coordonnées SVG sont
  écrites en culture invariante.
- Couleurs : `--hos-chart-series-0` à `--hos-chart-series-7`, avec repli sur
  l'accent et les couleurs d'état du thème.

## Décisions clés et précisions par rapport aux ADR 0056 et 0058

- **`UserName`** : `/proc/<pid>/status` donne un `Uid` opaque et HackerOS n'a
  pas de `/etc/passwd`. Le SDK nomme l'utilisateur du processus appelant ;
  pour un autre, `UserName` vaut l'id, comme `ps` avec un uid inconnu.
  L'onglet raccourcit un id de 32 caractères en `uid:xxxxxxxx`.
- **`StartType` nullable** : lire le type de démarrage du service d'une autre
  app passe par la même règle que le modifier (`services.manage`). Sans elle,
  la liste reste lisible, le type est `null`.
- **Ajouts à la liste de l'ADR 0056** : `SessionId`, `StateCode`,
  `Arguments`, `IoCounters`, `Status`, `GetEnvironmentVariablesAsync`,
  `GetChildren`, `GetCurrentProcessAsync`, `HackServiceController.ProcessId`
  et `Description` : tout ce que le détail d'un processus et l'onglet Services
  affichent, sans autre source que `/proc` et le catalogue.
  `ParseStatus` et `ParseIo` sont publics pour les apps qui lisent `/proc`
  elles-mêmes.
- **Graphique dans le SDK** : l'ADR 0058 demande des composants SVG natifs ;
  `HackLineChart` vit dans `HackerOs.AppSdk.Blazor` pour qu'une autre app
  (Diagnostic, une app tierce) le réutilise, comme le demande `AGENTS.md`.
- **Fenêtre réduite détectée en JavaScript** : le SDK n'expose pas l'état de la
  fenêtre. Le module de la fenêtre observe sa propre visibilité, seul point que
  C# ne voit pas ; il est importé depuis `./_content/HackerOs.Apps.SystemMonitor/`
  (ADR 0055 §3). Le même module empêche Ctrl+A de sélectionner toute la page
  quand la table a le focus.
- **Onglets non conservés** (`KeepPanelsAlive="false"`) : seul l'onglet visible
  se rend chaque seconde. L'état qui doit survivre à un changement d'onglet
  (tri, filtre, sélection, détail ouvert) vit dans `ProcessTable`, détenu par
  la fenêtre.
- **Manifeste** : `filesystem.system.read` s'ajoute à `process.list`,
  `process.manage` et `services.manage`, comme pour `ps`.
- **Profil de ressources au lancement** (correction dans Platform Core) :
  `AppLifecycleOrchestrator` lançait chaque processus avec
  `ResourceProfile.None`, si bien que le simulateur rendait 0 % de CPU et
  0 octet de mémoire pour tous, et que les graphiques restaient plats. Il passe
  maintenant les poids `Resources` du manifeste, comme le prévoit l'ADR 0012 ;
  le validateur de manifeste impose déjà les mêmes bornes que `ResourceProfile`.
- **Pas d'effet d'observateur sur le disque.** Le gestionnaire ne doit pas
  devenir le premier lecteur du disque qu'il surveille :
  - `HackProcess` lit le lien `cwd` comme `readlink`, sans résoudre sa cible.
    Suivre le lien faisait résoudre le répertoire courant de chaque processus
    dans IndexedDB, un segment par transaction, à chaque seconde (environ
    40 lectures par seconde mesurées sur `hda`, contre 4 au repos) ;
  - l'espace des disques (`HackDriveInfo`, qui parcourt les entrées
    IndexedDB) n'est relu que toutes les 10 lectures
    (`SystemMonitorWindow.DriveRefreshEvery`), comme on ne lance pas `df` en
    boucle.

## Tests

- `Tests/HackerOs.Platform.Core.Tests/Execution/HackProcessTests.cs` :
  `HackProcess` et `HackServiceController` sur le vrai fournisseur `/proc` et
  `AppFileSystemGateway` ; visibilité sans `process.list`, `io` et `environ`
  privés, processus terminé, refus et sortie de `KillAsync`, délai de
  `CloseMainWindowAsync`, raison et délai de `TerminateAsync`, services (état,
  type illisible, désactivé, refus).
- `Tests/HackerOs.Platform.Core.Tests/Lifecycle/AppLifecycleOrchestratorTests.cs` :
  le processus reçoit les poids `Resources` de son manifeste.
- `Tests/HackerOs.AppSdk.Blazor.Tests/HackChartGeometryTests.cs` : échelle
  automatique, position des points, aire, culture invariante.
- `Tests/HackerOs.Apps.SystemMonitor.Tests/` : parseurs `/proc`
  (`ProcFilesTests`), différences et historique (`SystemMetricsTests`), tri,
  filtre, sélection et « End task » (`ProcessTableTests`), « End task » en
  `SIGTERM` 5 s et « Force kill » en `SIGKILL`, sélection signalée ensemble
  (`EndTaskTests`), unités (`UnitsTests`), manifeste (`SystemMonitorWindowTests`),
  textes anglais et français et unités françaises (`SystemMonitorLocalizationTests`).

## Langues (ADR 0104)

Les textes de l'app sont dans `Resources/Strings.resx` (anglais) et
`Strings.fr.resx` (français), lus avec `AppContext.GetLocalizer<Strings>()`.
Les nombres suivent `AppContext.FormatCulture` : `Units` a des surcharges qui
prennent une culture (`1,5 Mio`, `12,5 %`, `2 j 03:14:15` en français) ; les
surcharges sans culture restent invariantes. Les noms et valeurs lus dans
`/proc` (champs de `status`, états systemd) ne sont pas traduits.

## Liste des tâches du chantier `rebuild/4-task-manager`

- [x] `HackProcess` : lecture de `/proc/<pid>/status`, `cmdline`, `cwd`, `io`, `environ` ; `KillAsync`, `CloseMainWindowAsync` par `IAppProcessGateway`.
- [x] `HackServiceController` : liste par `HackApps`, état par `HackProcess`, actions et type de démarrage par `IAppServiceControlGateway`.
- [x] `HackLineChart` : graphique SVG natif dans `HackerOs.AppSdk.Blazor`.
- [x] Parseurs `/proc` et calcul des débits par différence, historique de 60 s.
- [x] Modèle de la table : tri, filtre, sélection multiple (Ctrl, Maj, Ctrl+A), détail.
- [x] Réécriture de la fenêtre : onglets Processes, Performance, Disk, Network, Services ; détail ; « End task » confirmé, refus par processus ; rafraîchissement d'une seconde suspendu quand la fenêtre est réduite.
- [x] Retrait de `Platform.Core` et de l'exemption ADR 0055 ; manifeste (`filesystem.system.read`).
- [x] Correction : profil de ressources du manifeste passé au processus par `AppLifecycleOrchestrator` (CPU et mémoire enfin non nuls).
- [x] Vérification dans le navigateur (hôte `test/test`) : onglets, tri, Maj+clic, détail, filtre, « End task », pause fenêtre réduite ; correction de l'effet d'observateur sur `hda`.
- [x] Tests (SDK, graphique, app).
- [x] Documentation : ce document, `app-sdk.md`, `system-metrics.md`, `virtual-proc-and-dev.md`, index.

## Chantier `rebuild/5-graceful-stop` (ADR 0069)

- [x] « End task » passe de `KillAsync` à `TerminateAsync` (`SIGTERM`, 5 s), avec sa confirmation (ADR 0058 §2).
- [x] « Force kill » (`SIGKILL`) dans le menu contextuel des lignes et sur Maj+Suppr, avec sa propre confirmation.
- [x] Sélection signalée en parallèle, rapport dans l'ordre de la sélection (`EndTaskRunner`, `EndTaskMode`).
- [x] Tests : `EndTaskTests`, `HackProcessTests.TerminateAsync_*`.

## Chantier `rebuild/5-worker-processes` (ADR 0070, 0071)

- [x] `Metrics/ProcFiles.cs` lit `/proc/hackeros/workers` (`ProcWorkers`, `ProcWorker`, `ParseWorkers`) ; `ProcReader` le lit s'il existe.
- [x] Onglet Performance : section « Workers » (capacité, taille du pool, une carte par worker avec rôle, état, cœur, mémoire, processus) ; « Not responding » en rouge.
  Depuis `exec/9` (ADR 0131, 0143), quand la page est isolée, la mémoire d'une carte est celle que le navigateur mesure pour ce worker (`memory-measure: browser` dans `/proc/hackeros/workers`) ; l'app n'a pas changé.
- [x] « Not responding » visible pendant le blocage d'une commande dans un worker ; « Force kill » termine le worker.
- [x] Tests : `ProcFilesTests` (`ParseWorkers`).
