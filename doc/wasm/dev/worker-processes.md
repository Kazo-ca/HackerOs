# Processus sans interface dans des Web Workers

Ce qu'une app déclare pour son hôte d'exécution, les types qui traversent la frontière d'un worker et l'état d'isolation de la page sont dans [`app-execution-hosts.md`](../framework/app-execution-hosts.md).

Mise en œuvre des [ADR 0070](adr/0070-worker-hosted-processes.md) et
[0071](adr/0071-always-ready-worker.md), avec les ajustements de
l'[ADR 0072](adr/0072-worker-processes-implementation-adjustments.md) ;
chantier `rebuild/5-worker-processes` de la phase 5
([`execution-isolation-plan.md`](plans/execution-isolation-plan.md)). L'isolation
cross-origin (tubes partagés, appels synchrones, mémoire mesurée) suit les
[ADR 0131](adr/0131-cross-origin-isolation.md) et
[0143](adr/0143-cross-origin-isolation-implementation-adjustments.md).

## Rôle

Blazor WebAssembly exécute tout le .NET sur un seul thread : une commande qui
boucle sans rendre la main gelait l'interface, la souris et `kill`. Dans le
navigateur, les commandes du shell et les services tournent maintenant dans des
**Web Workers**, chacun avec son propre runtime .NET :

- une boucle infinie (`stress`) ne gèle plus le bureau ; `echo` répond en
  ~300 ms pendant qu'elle tourne ;
- le gestionnaire de tâches, `ps` et `/proc/hackeros/workers` montrent
  « Not responding » **pendant** le blocage ;
- `kill -9` (et `kill` après le délai de grâce) termine le worker bloqué et le
  remplace aussitôt ;
- le SDK ne change pas : une commande ne sait pas où elle tourne.

Les apps à fenêtre, le shell (`hsh`) et ses commandes intégrées restent sur le
fil principal.

## Où tourne chaque processus

Une fenêtre reste sur le fil principal : l'essai de rendu dans un worker
(ADR 0129) a conclu « on s'arrête » ([ADR 0133](adr/0133-window-rendering-in-worker-verdict.md),
[`remote-window-rendering.md`](remote-window-rendering.md)). Son prototype,
coupé par défaut, est le seul à démarrer un processus `start` `k = 3`.

Les 74 commandes de `HackerOs.Commands` : 56 en `pool`, 18 en `mainThread`
avec la raison en commentaire dans leur définition (`cd`, `exit`, `export`,
`unset`, `jobs`, `read`, `help`, `which`, `kill` pilotent le shell ; `sudo`
demande un mot de passe à l'agent d'authentification ; `passwd` lit le terminal sans écho ; `hackbackup` ouvre
le sélecteur de fichiers du navigateur, qui exige le geste de l'utilisateur ; `nano`, `top`, `htop`,
`watch` utilisent l'écran alternatif du terminal ; `nice` lance sa commande
par le shell appelant (`RunNicedAsync`) et `renice` change une priorité, ce
qu'aucune passerelle d'un worker n'offre, ADR 0127) ; les 16 premières raisons ont été
relues par `exec/1-remote-gateways` et tiennent toujours (ADR 0138 §9). Le
service d'exemple et `hackwebd` tournent en worker dédié depuis que `HackEvents`
et `Watch` traversent la frontière (ADR 0124).

Les trois hôtes ont les workers : `test/test` exécute côté navigateur le
`Program.cs` de l'Ecosystem, et `HackerOs.Server` sert l'OS de l'Ecosystem en
WebAssembly depuis l'ADR 0130 (son ancien rendu Interactive Server, sans
worker, a été retiré). Un hôte composé sans `IWebWorkerRuntime` écrit dans
`/var/log/syslog` « workers: no Web Worker on this host: AppExecutionHost
(pool, dedicated) is ignored, every process runs in the host process ».

## Architecture

```
 fil principal (OS)                               Web Worker n
 ─────────────────────────────                    ─────────────────────────────
 AppLifecycleOrchestrator                         workerBootstrap.js (dotnetSidecar)
   └ IRemoteProcessHost ── WorkerRemoteProcessHost   └ BrowserWorkerEntryPoint [JSExport]
        └ WorkerPool ── WorkerConnection ◄─ JSON ─►      └ WorkerProcessHost
             │            └ RemoteProcessSession           ├ ProcessControlBlock / ProcessTaskScheduler
             │               (appels servis sous           ├ RemoteAppExecutionContext
             │                le contexte du processus)    │   └ passerelles distantes (WorkerRpcClient)
             └ IWebWorkerRuntime (BrowserWebWorkerRuntime  └ battement de cœur (250 ms)
               → workerHost.js, ou Loopback en test)
```

| Projet | Types |
|---|---|
| `Shared/HackerOs.App.Abstractions` | `AppExecutionHost`, `AppExecutionHosts` (`DefaultFor`, `Resolve`, `Of`, `IsWorker`), `AppManifestAttribute.Execution`, `AppManifest.Execution`. |
| `Shared/HackerOs.Simulation.Abstractions/Workers` | `IWebWorkerRuntime`, `IWebWorker`, `WebWorkerStartOptions` ; `IWorkerPoolStatus`, `WorkerSnapshot`, `WorkerRole`, `WorkerState`. |
| `Platform/HackerOs.Platform.Core` | `IRemoteProcessHost`, `RemoteProcessLaunch`, `RemoteProcessExit`, `IRemoteService`, `ExecutionPlacement` (groupe d'un pipeline), `SystemStartupSignal` ; `AppLifecycleOrchestrator` lance à distance ce que `ShouldRunRemotely` accepte ; `/proc/hackeros/workers`. |
| `Platform/HackerOs.Platform.WorkerHost` | **Protocole** : `WorkerMessages`, `WireCodec` (encodage explicite, sans réflexion), `RemoteErrors`, `RemoteOps`. **Côté worker** : `WorkerProcessHost`, `RemoteAppExecutionContext`, passerelles distantes (`RemoteGateways`, `RemoteFileSystemGateway`, `RemoteStandardStreams`), `WorkerCpuMeter`, `WorkerAssemblyLoader`, `BrowserWorkerEntryPoint`. **Côté OS** : `WorkerPool`, `WorkerConnection`, `RemoteProcessSession`, `WorkerRemoteProcessHost`, `WorkerAwareCpuTimeAccountant`, `WorkerAwareMemoryAccountant`, `AddHackerOsWorkerProcesses`. **Tests** : `LoopbackWebWorkerRuntime`. |
| `Infrastructure/HackerOs.Infrastructure.Browser` | `wwwroot/workerHost.js` (création, `postMessage`, `terminate`, `hardwareConcurrency`, `requestIdleCallback`), `wwwroot/workerBootstrap.js` (runtime .NET du worker), `Workers/BrowserWebWorkerRuntime`. Aucun hôte ne les référence : ils sont importés par le C# (`_content/HackerOs.Infrastructure.Browser/…`). |
| `OS/HackerOs.Ecosystem.Host` | `AddHackerOsBrowserHost` (appelé par le `Program.cs` de l'Ecosystem) enregistre `BrowserWebWorkerRuntime` et les workers **dans le navigateur seulement** (`OperatingSystem.IsBrowser()`), en préchargeant `HackerOs.Commands.dll` et avec `KnownAssemblies` = les assemblies paresseuses du catalogue (ADR 0124 §4) ; `EcosystemBootCoordinator.MarkSystemStarted` donne le signal de fin de boot. |
| `Apps/System/HackerOs.Apps.SystemMonitor` | `ProcWorkers`/`ParseWorkers`, section « Workers » de l'onglet Performance. |

### Démarrage d'un worker

`workerBootstrap.js` pose `globalThis.dotnetSidecar = true` avant d'importer
`dotnet.js` (sinon le runtime se prend pour un thread `pthread` et attend
indéfiniment), crée le runtime (~0,7 s), branche l'import
`hackeros-worker.post` et appelle `BrowserWorkerEntryPoint.Start`. Le worker
charge ensuite les assemblies demandées (`INTERNAL.loadLazyAssembly`), répond
`hello` et commence son battement de cœur.

### Protocole

Messages JSON avec un champ `t` :

| OS → worker | Rôle |
|---|---|
| `init` | Numéro du worker, période du battement, mesure du CPU. |
| `start` | Lancer un processus : identité, point d'entrée, flux, environnement. |
| `result` | Réponse à un `call` (valeur `v` ou erreur `e`). |
| `stop` | `SIGTERM` : `StopRequested`, puis annulation du jeton (ADR 0069). |
| `int` | Ctrl+C : annulation du jeton, sans demande d'arrêt. |
| `kill` | `SIGKILL` quand le worker répond encore (étape 3) : jeter le processus. |
| `sstop` | `OnStoppingAsync` d'un service (déconnexion, extinction). |
| `clock` | Réponse au battement : l'horloge simulée, pour que celle du worker suive. |
| `perm` | Erreur de permission levée par l'OS sur les passerelles du processus. |
| `ack` | Crédit de sortie : l'OS a écrit `n` caractères. |
| `epipe` | Le lecteur du flux `f` est parti (tube cassé). |
| `hreq` | Requête HTTP pour un socket ouvert par `net.listen` dans le worker (`web/3`). |
| `bgp` | Progression (`f`, `m`) de la tâche `j` qu'un processus du worker a lancée par `bg.run` (ADR 0128 §6, ADR 0140). |
| `uin` | Essai de l'ADR 0133 : entrée de l'hôte de fenêtre de la page (`d` : événement `e`, réponse de dialogue `dlgr`, `close`) pour la fenêtre `p`. |

| Worker → OS | Rôle |
|---|---|
| `hello` | Runtime et assemblies chargés. |
| `hb` | Battement (250 ms) : temps CPU par processus, mémoire du runtime, temps occupé, et depuis combien de temps le code de chaque processus a le thread (`rn`). |
| `call` | Appel de passerelle qui attend une réponse ; `"s": true` pour un appel synchrone, dont la réponse revient par la case partagée du worker (ADR 0143 §5). |
| `note` | Appel sans réponse (journal, environnement), gardé dans l'ordre. |
| `cancel` | Annuler un `call` en cours. |
| `out` | Sortie standard ou d'erreur d'un processus. |
| `exit` | Fin d'un processus : statut, code, message d'erreur ; pour une tâche `HackBackground`, `jr` (résultat JSON), `jf` (exception d'origine) et `jx` (tâche refusée). |
| `killed` | Accusé d'un `kill`. |
| `stopdone`, `sstopped` | Fin du traitement d'un `stop` / `sstop`. |
| `log` | Ligne de diagnostic du worker. |
| `hres` | Réponse à un `hreq` (`web/3`). |
| `ui` | Essai de l'ADR 0133 : sortie de la fenêtre `p` vers son hôte sur la page (`d` : lot d'opérations DOM `b`, demande de dialogue `dlg`). |

`start` porte `k` : 0 ou absent pour une commande, 1 pour un service, 2 pour
une tâche `HackBackground` (ADR 0140), 3 pour une fenêtre de l'essai de
l'ADR 0133 (champs `ra`/`rt` : le runner de la plateforme qui la rend).

### Événements et surveillance de fichiers (ADR 0124, ADR 0138)

Le bus reste dans l'OS. `RemoteEventGateway` et `RemoteWatchGateway`
(`Worker/RemoteEvents.cs`) envoient les opérations ci-dessous ;
`RemoteProcessSession` crée chaque abonnement **au nom du pid** et
`RemoteEventRouter` pousse ce qu'il reçoit par des messages `ev`.

| Opération | Forme | Rôle |
|---|---|---|
| `ev.sub` | `call` | S'abonner à un sujet (`t`) ou à un type d'événement noyau (`e`), avec la capacité d'un canal borné. |
| `ev.unsub` | `note` | `Dispose` de l'abonnement. La fin, le `kill` ou la mort du worker désabonnent côté OS sans attendre. |
| `ev.pub` | `note` | Publier sur un sujet (nom du type et JSON). Résultat provisoire `Delivered` dans le worker, refus au journal de l'app (ADR 0138 §3). |
| `ev.pubr` | `call` | `PublishAsync` (ADR 0145) : la même publication, avec en réponse le résultat de l'OS (`o`) et les fautes des abonnés (`f`) ; rien au journal. |
| `ev.chan` | `note` | `RegisterSharedChannel` (portée `VirtualPath` seulement). |
| `ev.ack` | `note` | Rendre le crédit : 256 messages en vol par abonnement. |
| `watch.open` | `call` | `WatchAsync` : l'OS ouvre la surveillance avec les droits du pid. |
| `ev` (OS → worker) | message | Une livraison : abonnement, JSON, sujet, app éditrice, date. Livrée dans le contexte ambiant du processus. |

Côté OS, les bus indexent ces abonnements par nom de type (`IWireTopicMessageBus`, `IWireEventBus`) : une
publication d'un worker n'est matérialisée en objet qu'une fois par type pour
les abonnés du fil principal.

Chaque `call` est servi sur le fil principal par `RemoteProcessSession` **sous
le contexte d'exécution du processus** (`HackExecutionContext.Enter` puis
`EnterKernel`) : les mêmes vérifications de capacités et la même comptabilité
qu'en local. La sortie a un crédit de 64 Kio en vol ; un fichier régulier de
16 Mio au plus est lu d'un coup, au-delà par blocs.

### Tâches `HackBackground` (ADR 0128, ADR 0140)

Une tâche lancée par `HackBackground.RunAsync` est un processus enfant de l'app
que l'OS crée sur le fil principal (`BackgroundJobService`), puis place dans un
worker du pool par `WorkerRemoteProcessHost.RunBackgroundJobAsync` : un message
`start` avec `k = 2`, la cible `jt` (`asm`, `ty`, `me`, `it`) et l'entrée `ji`.
Le worker charge l'assembly (`KnownAssemblies`), lie la méthode statique ou la
classe de tâche, exécute la tâche dans le contexte du processus et rend le
résultat dans `exit`. Sans worker, la tâche tourne sur le fil principal avec la
même sérialisation ([`background-jobs.md`](../framework/background-jobs.md)).

| Opération | Forme | Rôle |
|---|---|---|
| `bg.progress` | `note` | Une remontée de progression de la tâche (`f`, `m`), déjà limitée à 10 par seconde. |
| `bg.run` | `call` | Un processus déjà dans un worker lance une tâche (`jt`, `ji`, `n`, `ni`, numéro `j`) ; l'OS la place comme une autre et renvoie sa progression par des messages `bgp`, puis le résultat `{r}`. |

Erreurs distantes : `bgfault` (`ty`, `st`) porte une `HackBackgroundJobException`
avec le type et la pile d'origine ; `killed` porte la raison `rs`.

### Pool, worker prêt, débordement (ADR 0070 §2-3, ADR 0071)

| Rôle | Quand |
|---|---|
| `pool` | Un worker par cœur virtuel : `clamp(CpuCapacity, 1, capacité - 1)`, cœurs 0..n-1. |
| `ready` | Toujours un, une fois le système démarré ; remplacé aussitôt qu'il est pris. |
| `dedicated` | Prend le worker prêt pour un processus `dedicated` ; libéré à sa fin. |
| `overflow` | Le worker prêt pris par une commande quand aucun cœur n'est libre ; retiré après 30 s vide. |
| `web` | Un seul pour tous les sites (`AppKind.Web`, ADR 0073 §1) : le premier site prend le worker prêt, les suivants le partagent ; terminé quand son dernier site s'arrête. Requêtes HTTP par `hreq`/`hres` ([`listening-ports-and-dns.md`](../framework/listening-ports-and-dns.md)). |

Un worker `pool` ou `overflow` qui a hébergé une victime de l'OOM killer est
retiré dès qu'il n'héberge plus aucun processus, puis remplacé : sa mémoire
WebAssembly ne rétrécit jamais (ADR 0142 §5, [`oom-killer.md`](oom-killer.md)).

- **Capacité** : `max(2, navigator.hardwareConcurrency)` workers en tout. Au
  plafond, une demande attend qu'un worker se libère et `/var/log/syslog` le
  note une fois (« worker pool saturated »).
- **Démarrage** : au signal de fin de boot (bureau affiché), puis
  `requestIdleCallback` (2 s au plus) : le worker prêt d'abord, puis les cœurs.
  Avant ce signal, une demande démarre son propre worker et l'attend.
- **Libre** : un worker prêt sans processus, ou dont le dernier battement a
  moins de 750 ms **et** montre chaque processus hébergé en train d'exécuter
  son propre code depuis au moins une période (`rn`, ADR 0072 §6) : une
  commande qui boucle dès son lancement n'attire pas la suivante derrière
  elle. La preuve est ce que le worker rapporte, pas l'heure d'arrivée du
  battement : un battement envoyé pendant le chargement de l'assembly du
  nouveau processus (ce chargement rend la main) pouvait arriver une période
  après le placement sur une machine chargée, alors que le code n'avait pas
  encore tourné.
- **File d'attente** (ADR 0127 §1, `WorkerRunQueue`) : au plafond, les
  demandes attendent par `nice` croissant puis par ordre d'arrivée ; un
  processus de valeur `nice` positive ne prend pas le worker prêt pendant qu'un autre worker démarre. La
  priorité ne découpe pas le temps : rien n'est préempté.
- **Placement** : le cœur libre le moins chargé ; les étapes d'un pipeline
  (`ExecutionPlacement.BeginGroup` dans `ShellInterpreter`) vont sur le même
  worker.

### Battement de cœur et escalade (ADR 0070 §5)

- Toutes les 250 ms, le worker envoie les temps CPU de chaque processus
  (`utime`, `stime`, tranches, plus longue tranche, allocations) mesurés par
  `WorkerCpuMeter` sur le crochet de changement de contexte, la mémoire de son
  runtime et son temps occupé. Ils alimentent `/proc/<pid>/stat`, `status`,
  `/proc/stat` (le cœur du worker) et `/proc/meminfo` par
  `WorkerAwareCpuTimeAccountant` et `WorkerAwareMemoryAccountant`.
- Sans battement depuis 5 s, les processus du worker passent
  **Not responding** et syslog écrit « worker N: not responding (pids …) ».
  Le temps sans battement est compté comme occupé (ADR 0072 §6).
- **Étape 3** : `kill` au worker ; s'il confirme (`killed`) dans la seconde,
  il continue de servir.
- **Étape 4** : sinon (ou après le délai de grâce de `SIGTERM`), l'OS termine
  le worker. Ses autres processus sortent `Killed` avec « [pid] killed: worker
  N terminated to stop pid P » dans syslog, le shell écrit « Killed », et le
  worker prêt reprend le cœur. Les voisins sont marqués tués dans la table des
  processus **avant** la fin de leur session, pour que le shell voie la raison ;
  la sortie déjà reçue d'un processus tué est écrite avant qu'il se termine
  pour son appelant (1 s au plus pour une session abandonnée, 3 s au plus pour
  la commande tuée), comme les données d'un tube restent lisibles après un
  `SIGKILL`.

### `/proc/hackeros/workers`

Présent seulement quand l'hôte a des workers. Les premières lignes disent si
la page est isolée (ADR 0131 §5, ADR 0143 §8), puis vient le pool :

```
cross-origin-isolated: yes
isolation-reason: -
isolation-setting: auto
embedder-policy: credentialless
shared-pipes: yes
sync-calls: yes
memory-measure: browser
timer-resolution-us: 5
capacity 4
pool_size 1
worker role       state          core memory_kb heartbeat_ms busy_ms pids
2      pool       running        0    41216     112          380     57,58
1      ready      idle           -    38912     98           0       -
```

Rôles : `pool`, `overflow`, `dedicated`, `ready`, `web`. États : `starting`, `idle`,
`running`, `not-responding`, `terminated`. Les PID (séparés par des virgules)
sont filtrés par la visibilité de `/proc` du lecteur. Raisons d'une page non
isolée : `setting`, `missing-headers`, `insecure-context`, `browser`,
`not-browser` (hors navigateur, tests).

### Isolation cross-origin (ADR 0131, ADR 0143)

Quand la page est isolée (`crossOriginIsolated`), les workers gagnent :

- **des tubes partagés** : deux anneaux de 1 Mio par worker
  (`wwwroot/sharedRing.js`), un par sens, portent tous les messages texte du
  protocole ; le lecteur vide l'anneau avant tout message reçu par
  `postMessage`, un message trop gros part par `postMessage`, personne ne
  bloque ;
- **des appels synchrones** : `ListProcesses` (opération `proc.list`) et `Kill`
  d'un autre processus attendent la réponse de l'OS par `Atomics.wait` dans le
  worker (10 s au plus, réponse de 1 Mio au plus, `"s": true` dans le `call`) ;
  le fil principal n'attend jamais ;
- **la mémoire mesurée par le navigateur** : `IWebWorkerMemoryMeter`, toutes
  les 20 s (`WorkerPoolOptions.MemoryMeasurePeriod`), attribuée par
  `?hackeros-worker=N` dans l'URL du worker, valable trois périodes ;
- **des minuteries de 5 µs** (`performance.now()`), sans code à brancher.

Sans isolation, tout reste comme avant : messages seulement, membres
synchrones refusés avec le même message, mémoire du battement de cœur.

Qui pose les en-têtes : le service worker de l'Ecosystem (selon
`HackerOs:CrossOriginIsolation` de `appsettings.json`, repli `require-corp`
avec un rechargement unique), l'intergiciel de `test/test` et de
`HackerOs.Server`. L'Ecosystem servi sur `localhost` ou `127.0.0.1` n'a pas de
service worker, donc pas d'isolation (ADR 0143 §3) : on essaie sur
`127.0.0.2`.

#### Audit avant d'activer par défaut (ADR 0131 §3)

Chromium 140 sans écran, publication Release de l'Ecosystem servie sur
`127.0.0.2` (service worker actif) et `test/test` (intergiciel), le
2026-10-18. Résultat : **rien ne casse, `AuditPassed = true`**.

| Surface | Vérification | Résultat |
|---|---|---|
| Rendu HTML du navigateur simulé et sites ASP.Hack | suite UI E2E sous isolation : `BrowserHtmlRenderingTests`, `BrowserBlazorSiteTests`, `BrowserIntranetTests`, `BrowserHomePageTests`, `BrowserSiteDataTests` ; les iframes `srcdoc` héritent de la COEP de la page | passe |
| Fenêtres détachées (`window.open`) | `BrowserDetachedWindowTests` sous isolation : la fenêtre est de même origine, COOP `same-origin` garde le lien | passe |
| Repli sur le réseau réel, images externes | image et `fetch` `no-cors` d'une autre origine sans CORP : chargées avec `credentialless` | passe (bloquées seulement en `require-corp`, ADR 0143 question 3) |
| Monaco (CodeEditor), xterm.js (Terminal) | nouveau fichier, saisie dans Monaco ; commandes dans le terminal | passe, aucune erreur de console |
| Téléchargements (`download.js`), `.hosbak` | lien `blob:` avec `download` : le téléchargement part ; `FileExplorerDownloadTests` sous isolation | passe |
| Installation de la PWA, mise à jour du service worker | premier chargement sur `127.0.0.2` : installation, rechargement unique (`controllerchange`), page isolée ; `HostSmoke` `ecosystem-isolated` | passe |

Avec l'isolation, `seq 30000 | wc -l` donne `30000` par les anneaux,
`sleep 1 & ps` marche, et `sample-cmd --processes` répond.

## Usage

Côté hôte :

```csharp
if (OperatingSystem.IsBrowser())
{
    builder.Services.AddSingleton<IWebWorkerRuntime, BrowserWebWorkerRuntime>();
    builder.Services.AddHackerOsWorkerProcesses(o => o with { PreloadAssemblies = ["HackerOs.Commands.dll"] });
}
```

`WorkerPoolOptions` : `HeartbeatPeriod` (250 ms), `UnresponsiveThreshold`
(5 s), `BusyThreshold` (750 ms), `KillAcknowledgeTimeout` (1 s),
`OverflowIdleTimeout` (30 s), `IdleWait` (2 s), `VirtualCores` (du profil
matériel), `PreloadAssemblies`, `KnownAssemblies`, `MeterCpu`.

`KnownAssemblies` (ADR 0124 §4) : les assemblies d'apps connues au build
(l'Ecosystem y met `LazyAssemblyNames` du catalogue). Un worker charge à la
demande n'importe laquelle d'entre elles et la garde pour les demandes
suivantes ; une autre, absente de son runtime, est refusée : le processus écrit
l'erreur sur sa sortie d'erreur et sort avec le code 127. Une liste vide
n'impose rien.

Commande de démonstration : `stress [-c N] [-t SECONDES] [--yield]` fait
tourner N boucles de calcul sans rendre la main (`--yield` : avec points
d'arrêt).

### Limites de processus (ADR 0125, ADR 0139)

Les limites restent dans l'OS ([`process-limits.md`](process-limits.md)).

| Opération | Forme | Rôle |
|---|---|---|
| `fs.fdopen` | `call` | Prendre un descripteur pour un flux d'écriture (`OpenDescriptorAsync`) ; refus `EMFILE` à la limite. |
| `fs.fdclose` | `note` | Rendre ce descripteur ; la fin du processus rend les autres. |
| `proc.rlimit` | `call` | `SetResourceLimitAsync` (`setrlimit`/`prlimit`) au nom du pid. |

L'application est stricte en worker : le contrôleur envoie `SIGXCPU` par un
`stop`, puis `SIGKILL` termine le worker (étape 4 de l'ADR 0069) si le
processus ne rend pas la main. Une lecture de 16 Mio au plus arrive en une
seule réponse : son descripteur est rendu dès cette réponse (ADR 0139 §3).

## Décisions clés

- Le shell, ses scripts et ses tubes restent sur le fil principal ; seules les
  commandes externes migrent (ADR 0072 §1-2).
- Encodage explicite des messages (`WireCodec`) : pas de sérialisation par
  réflexion dans un runtime élagué.
- `LoopbackWebWorkerRuntime` exécute chaque « worker » sur un thread dédié avec
  son contexte de synchronisation : les tests couvrent le pool, le protocole et
  l'escalade sans navigateur.
- `IAppProcessGateway.KillAsync` (méthode par défaut) pour tuer depuis un worker
  (ADR 0072 §4).

## Tests

- `Tests/HackerOs.Platform.WorkerHost.Tests` (OS complet sans interface, workers
  en boucle locale) :
  - `CommandParityTests` : **les 74 commandes intégrées** (`nslookup`, `ausearch`, `xdg-open`, `xdg-mime`, `passwd`, `hackbackup`, `dmesg`, `nice`, `renice` et `prlimit` comprises) exécutées dans les
    deux modes (fil principal, worker) avec la même sortie, le même code et le
    même arbre de fichiers (`HACKEROS_PARITY_DUMP` pour inspecter) ;
  - `WorkerPoolTests` : démarrage avant / après la fin du boot, worker prêt
    pris et remplacé, débordement et retour à la taille, plafond et syslog,
    pipeline sur un seul worker, commande qui boucle dès son lancement,
    `/proc/hackeros/workers` ;
  - `EscalationTests` : `kill -9` d'une boucle (voisins tués, syslog, cœur
    remplacé), « Not responding » pendant le blocage, `SIGTERM` puis
    escalade, étape 3 sans terminer le worker, Ctrl+C, erreur dans un worker ;
  - `DedicatedServiceTests` : service dans un worker dédié, `systemctl stop` ;
  - `RemoteEventsTests` (ADR 0124) : parité fil principal / worker des sujets,
    canaux, événements noyau, `Watch` et refus ; charge non déclarée ;
    désabonnement au `kill` ; service d'exemple en worker dédié ;
    `sample-cmd` (projet d'exemple tiers) chargé à la demande ; refus d'une
    assembly inconnue.
- `Tests/HackerOs.App.Abstractions.Tests` : `Execution` dans le mappeur, le
  validateur, le sérialiseur et le schéma.
- `Tests/HackerOs.HostSmoke.Tests` (`build/verify-hosts.sh`) : sur les trois
  hôtes (le serveur sert le même OS depuis l'ADR 0130), pipeline,
  `/proc/hackeros/workers`, `echo` pendant `stress`, « not-responding »,
  `kill -9` et `kill`.

## Liste des tâches du chantier `rebuild/5-worker-processes`

- [x] `AppExecutionHost` dans `[AppManifest]`, le mappeur, le validateur, le schéma et `manifest.json` (défauts de l'ADR 0070 §1).
- [x] Contrats `IWebWorkerRuntime`, `IWorkerPoolStatus` ; `IRemoteProcessHost` dans l'orchestrateur.
- [x] `HackerOs.Platform.WorkerHost` : protocole, hôte de processus du worker, passerelles distantes, flux standard par messages.
- [x] Module JS dans `HackerOs.Infrastructure.Browser/wwwroot` et `BrowserWebWorkerRuntime`.
- [x] Pool par cœur virtuel, placement des pipelines, workers dédiés.
- [x] Worker toujours prêt après le boot, débordement, retour à la taille, plafond et syslog (ADR 0071).
- [x] Battement de cœur, mesures CPU et mémoire, « Not responding », étape 4 de l'escalade.
- [x] `/proc/hackeros/workers` et section « Workers » du gestionnaire de tâches.
- [x] Commandes : hôte déclaré ou défaut ; service d'exemple en `MainThread` (raison en commentaire) — en worker dédié depuis `exec/1-remote-gateways`.
- [x] Serveur : ligne d'information ; `test/test` comme l'Ecosystem.
- [x] Tests de parité des 63 commandes, tests du pool et de l'escalade.
- [x] `v3/4-intents` : les intents de l'ADR 0096 (`int.edit`, `int.reveal`, `int.uri`, `int.settings`, `int.send`) et la remise d'un intent à un processus en cours (message `intent`, `IntentReceived`) traversent la frontière du worker ; parité de `xdg-open`.
- [x] Vérification des trois hôtes (`build/verify-hosts.sh`).
- [x] Documentation et ADR 0072.

## Liste des tâches du chantier `exec/1-remote-gateways` (ADR 0124, ADR 0138)

- [x] Bus « wire » côté OS (`IWireTopicMessageBus`, `IWireEventBus`), mêmes règles d'autorisation.
- [x] Opérations `ev.*`, `watch.open` et message `ev` ; crédit de 256 messages.
- [x] Codecs intégrés et `HackWireContextAttribute` (SDK 1.2 ; versé dans `PublicAPI.Shipped.txt` par `exec/10-final`, ADR 0132 §2).
- [x] Désabonnement à la fin, au `kill` et à la mort du worker.
- [x] Service d'exemple et `hackwebd` en worker dédié ; revue des 16 commandes en `mainThread`.
- [x] `KnownAssemblies`, refus avec le code 127 ; `sample-cmd` dans l'Ecosystem et le test des hôtes.
- [x] Tests de parité (`RemoteEventsTests`, `WireTopicMessageBusTests`).
- [x] Documentation, ADR 0138, index.

## Liste des tâches du chantier `exec/2-process-limits` (ADR 0125, ADR 0139)

- [x] Opérations `fs.fdopen`, `fs.fdclose` et `proc.rlimit`.
- [x] Descripteurs du processus rendus à sa fin dans `RemoteProcessSession`.
- [x] `ProcessLimitsTests` sur le fil principal et en worker.

## Liste des tâches du chantier `exec/5-background-jobs` (ADR 0128, ADR 0140)

- [x] Démarrage `k = 2`, champs `jt`/`ji`, fin `jr`/`jf`/`jx`.
- [x] Note `bg.progress`, appel `bg.run` et messages `bgp` pour une tâche lancée depuis un worker.
- [x] Erreurs `bgfault` et raison `rs` de `killed`.
- [x] Tests de parité fil principal / worker (`BackgroundJobTests`).

## Liste des tâches du chantier `exec/9-cross-origin-isolation` (ADR 0131, ADR 0143)

- [x] Réglage `HackerOs:CrossOriginIsolation`, constante d'audit et copie dans le service worker.
- [x] En-têtes : service worker de l'Ecosystem (repli `require-corp`), intergiciel de `test/test` et `HackerOs.Server`.
- [x] Sonde de la page (`crossOriginIsolation.js`), raisons, ligne de `syslog`.
- [x] Anneaux partagés (`sharedRing.js`) dans `workerHost.js` et `workerBootstrap.js`.
- [x] Appels synchrones `ListProcesses`/`Kill`, opération `proc.list`, `sample-cmd --processes` et `--kill`.
- [x] Mémoire par worker mesurée par le navigateur (`IWebWorkerMemoryMeter`).
- [x] Lignes d'isolation de `/proc/hackeros/workers`, badge de Diagnostic.
- [x] Suite (ADR 0146, choix C de Benoit) : `HackCrossOriginIsolation` dans le SDK, sans capacité, opération `coi.get` ; Diagnostic ne déclare plus `filesystem.system.read` ; `sample-cmd --isolation`.
- [x] Audit dans Chromium, `AuditPassed = true`.
- [x] Tests : sonde et réglage, appels synchrones en bouclage, anneaux sous Node, `HostSmoke` et parcours `ecosystem-isolated`/`ecosystem-isolation-off`.

## Mesures de l'essai de fenêtre en worker (`exec/6`, ADR 0129 et 0133)

Publication Release trimmée, Chromium sans écran. Un worker prêt et vide
occupe **55,4 Mio** de mémoire linéaire WebAssembly (le runtime et les 87
assemblies du boot, ≈ 20,3 Mo dont 8,67 Mo pour `HackerOs.AppSdk.Icons`) et
5,7 Mio de tas managé. Avec la Calculatrice : 66,5 Mio. Ouverture +≈ 340 ms
(worker prêt), latence d'un clic p95 +3,3 ms. Détail et verdict dans
l'[ADR 0133](adr/0133-window-rendering-in-worker-verdict.md).

