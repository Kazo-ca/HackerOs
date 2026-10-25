> [English version](../../../en/wasm/dev/system-metrics.md)

# Mesures système : CPU, disque, réseau et espace disque

Mise en œuvre de l'[ADR 0057](adr/0057-proc-metrics-for-task-manager.md),
chantier `rebuild/4-proc-metrics` de la phase 4 (tableau des chantiers de
l'[ADR 0058](adr/0058-base-app-features.md)). Les fichiers produits sont décrits
dans [`virtual-proc-and-dev.md`](virtual-proc-and-dev.md) ; ce document décrit
les compteurs qui les alimentent.

## Rôle

Le gestionnaire de tâches ([`task-manager.md`](task-manager.md), chantier
`4-task-manager`), `ps` et toute app tierce lisent les mesures du système dans
`/proc`, comme sous Linux :

| Fichier ou API | Mesure | Source |
| --- | --- | --- |
| `/proc/stat` | temps CPU cumulé par cœur, en `USER_HZ` | **mesuré** quand la mesure CPU est active (ADR 0067, [`cpu-accounting.md`](cpu-accounting.md)), **simulé** sinon : simulateur de ressources |
| `/proc/<pid>/stat` | `utime` et `stime` d'un processus ; `priority` et `nice` | temps : **mesuré** au changement de contexte (ADR 0067), sinon reconstruit depuis le simulateur ; priorité : valeur `nice` du gestionnaire de processus (`nice`, `renice`, ADR 0127, [`process-priority-and-unresponsive-windows.md`](process-priority-and-unresponsive-windows.md)) |
| `/proc/<pid>/io` | octets lus et écrits par un processus | **compté** : service de fichiers et passerelle réseau |
| `/proc/<pid>/limits`, `/proc/<pid>/fd/` | limites `cpu`, `rss`, `nofile`, `nproc` et descripteurs ouverts (ADR 0125, 0139) | table des limites et table des descripteurs du gestionnaire de processus ([`process-limits.md`](process-limits.md)) |
| `/proc/<pid>/oom_score`, `/proc/vmstat` (`oom_kill`) | score de l'OOM killer ; nombre de processus tués depuis le démarrage (ADR 0126, 0142) | `VmRSS` mesuré, table `oom_score_adj`, compteur de l'OOM killer ([`oom-killer.md`](oom-killer.md)) |
| `/proc/diskstats` | requêtes, secteurs et temps du disque `hda` | **compté** : transactions IndexedDB du système de fichiers |
| `/proc/net/dev` | octets et paquets de `lo` et `eth0` | **compté** : passerelle réseau des apps |
| `/proc/mounts` | table des montages | routeur de montages |
| `HackDriveInfo` | taille et espace libre de `/` | profil matériel, contenu stocké, quota du navigateur |
| `/proc/hackeros/workers` | isolation cross-origin de la page (ADR 0131, 0143), puis Web Workers de l'OS : rôle, état, cœur, mémoire du runtime, âge du battement de cœur, temps occupé, processus (ADR 0070) | **mesuré** : battement de cœur de chaque worker, toutes les 250 ms ; page isolée, mémoire de chaque worker mesurée par le navigateur (`performance.measureUserAgentSpecificMemory()`, toutes les 20 s, `memory-measure: browser`) ([`worker-processes.md`](worker-processes.md)) ; absent quand l'hôte n'a pas de workers |

Un `cat` d'un gros fichier fait monter `rchar` du processus et les secteurs lus
de `hda` ; une page chargée par le navigateur fait monter `eth0`. Le navigateur
ne mesure pas le CPU par app : depuis la phase 5, l'OS le chronomètre lui-même
à chaque changement de contexte (ADR 0067), quand la mesure est active ; la
mémoire est mesurée de même (ADR 0068, section « Mémoire mesurée » ci-dessous).

### Deux sources pour le CPU et la mémoire d'un processus (ADR 0066)

| Source | Quand | Indicateur |
| --- | --- | --- |
| **simulée** : `IResourceSimulator`, profil `ResourceProfile` de l'app | mesure coupée au build (`HackerOsCpuAccounting`, `HackerOsMemoryAccounting`) ou dans `appsettings.json` (`HackerOs:ResourceAccounting`), mesure pas encore implémentée, processus simulé sans code | `CpuSource: simulated`, `MemSource: simulated` ; mention « estimated » dans le gestionnaire de tâches |
| **mesurée** : `ICpuTimeAccountant`, `IMemoryAccountant` alimentés par le crochet de changement de contexte | mesure livrée et activée, et le comptable répond pour ce processus | `CpuSource: measured`, `MemSource: measured` |

Le fournisseur `/proc` reçoit les deux comptables par `ProcMetricsSources`
(`CpuAccountant`, `MemoryAccountant`) et retombe sur le simulateur dès qu'un
comptable ne répond pas. Depuis la série `exec`, `ProcMetricsSources` porte
aussi les tables des limites (`ResourceLimits`) et des descripteurs
(`FileDescriptors`), les tâches `HackBackground` (`BackgroundJobs`), la table
`oom_score_adj` (`OomScores`), le compteur `OomKills` et l'état de l'isolation
(`CrossOriginIsolation`). Le chantier `5-accounting-switches` livre les
interrupteurs, le crochet et les comptables nuls ; le chantier
`5-memory-accounting` mesure la mémoire (section suivante) ; le chantier
`5-cpu-accounting` mesure le CPU avec `MeasuredCpuTimeAccountant`
([`cpu-accounting.md`](cpu-accounting.md)). Détails :
[`resource-accounting.md`](resource-accounting.md).

### Mémoire mesurée (ADR 0068)

Quand la mesure de la mémoire est livrée et activée, la mémoire d'un processus
est reconstituée en quatre couches ([`memory-accounting.md`](memory-accounting.md)) :

| Couche | Champ de `/proc/<pid>/status` | Source | Précision |
| --- | --- | --- | --- |
| Image | `VmExe`, `RssFile` | taille des assemblies, mesurée par le build de l'hôte (`BuiltInApplications.json`) | exacte |
| Ressources de l'OS | `VmData` | tampons de flux, presse-papiers, tubes, sockets en vol, coût fixe par fenêtre | exacte |
| Allocations | `HackAllocBytes` | `GC.GetAllocatedBytesForCurrentThread()` à chaque changement de contexte | exacte |
| Résidente | `RssAnon` | tas réel réparti après chaque GC au prorata des allocations récentes | **estimation** |

`VmRSS = RssAnon + RssFile + VmData`. `/proc/meminfo` garde `MemTotal` du
profil matériel et ajoute `AnonPages`, `Mapped` (chaque assembly comptée une
fois) et `Slab` (part noyau des processus et part du tas allouée par l'OS
lui-même). Ces chiffres mesurés sont aussi ceux qu'appliquent la limite `rss`
d'un processus (`ulimit -m`, ADR 0125, [`process-limits.md`](process-limits.md))
et l'OOM killer, **désactivé par défaut** et activé dans
`/etc/hackeros/memory.config` (ADR 0126, 0142, [`oom-killer.md`](oom-killer.md)) :
ni l'un ni l'autre n'agit sur une mémoire simulée.

Dans une page isolée (ADR 0131, ADR 0143 §6), la mémoire du runtime de chaque
Web Worker vient du navigateur (`performance.measureUserAgentSpecificMemory()`,
toutes les 20 s, valable trois périodes) au lieu du battement de cœur ; elle
est répartie entre les processus du worker comme avant, et `MemSource` reste
`measured` ([`memory-accounting.md`](memory-accounting.md)).

Règles de calcul (constantes de `MemoryAccountingConstants` et
`AppNetworkGateway`, couvertes par des tests) :

| Règle | Valeur |
| --- | --- |
| Demi-vie du poids d'un processus dans la répartition du tas (`ResidentHalfLife`) | 10 s |
| Coût fixe d'une fenêtre ouverte (`WindowBytes`) | 512 Kio |
| Tampons d'une opération réseau en vol (`SocketBufferBytes`) | 144 Kio |
| Presse-papiers, tubes | 2 octets par caractère tenu |
| Tampon d'un flux d'écriture | capacité du tampon |

La couche résidente reste une estimation : le runtime ne dit pas à qui
appartient un objet du tas partagé. Un processus lancé depuis le dernier GC
n'a pas encore de `RssAnon`.

Les compteurs vivent en mémoire, partent de zéro au démarrage et ne sont jamais
persistés.

## Architecture

### Contrats

`Shared/HackerOs.Simulation.Abstractions/Metrics/SystemMetricsContracts.cs` :

| Contrat | Rôle |
| --- | --- |
| `IProcessIoAccounting`, `ProcessIoCounters` | compteurs de `/proc/<pid>/io` ; `Retain` oublie les processus terminés |
| `IBlockDeviceStatistics`, `BlockDeviceCounters`, `BlockIoDirection` | statistiques de `hda` (majeur 8, mineur 0) ; `Begin` puis `Complete` par requête |
| `INetworkInterfaceStatistics`, `NetworkInterfaceCounters`, `NetworkInterfaceNames` | compteurs de `lo` et `eth0` |
| `ICpuTimeAccounting`, `CpuTimes` | temps `user`, `nice`, `system`, `idle` par cœur |
| `IStorageUsageProvider`, `StorageUsage` | espace occupé et quota du navigateur |

`VirtualHardwareProfile.StorageCapacityBytes` (8 Gio par défaut,
`DefaultStorageCapacityBytes`) donne la taille du disque.

### Implémentations

| Type | Projet | Rôle |
| --- | --- | --- |
| `ProcessIoAccounting`, `BlockDeviceStatistics`, `NetworkInterfaceStatistics`, `CpuTimeAccounting` | `Platform.Core/Metrics` | compteurs en mémoire, sous verrou |
| `ResourceSamplingService` | `Platform.Core/Processes` | le « tick » de l'ordonnanceur : une fois par seconde simulée, échantillonne chaque processus actif et cumule le temps CPU |
| `FileSystemService` | `Platform.Core/FileSystem` | compte les lectures et écritures de chaque processus |
| `AppNetworkGateway` + `NetworkTrafficMeter` | `Platform.Core/Network` | compte le trafic de chaque app |
| `ProcFileSystemProvider` + `ProcMetricsSources` | `Platform.Core/FileSystem/Virtual` | rend les fichiers |
| `AppDriveGateway` | `Platform.Core/Execution` | `statfs` de l'OS derrière `HackDriveInfo` |
| `IndexedDbInteropAdapter` | `Infrastructure.Browser/Interop` | chronomètre chaque transaction et la signale à `hda` |
| `IndexedDbStorageUsageProvider` | `Infrastructure.Browser/FileSystem` | espace occupé et quota réels du navigateur (`navigator.storage.estimate()`, ADR 0099 §2) ; sans quota connu, contenu dédupliqué |

Branchement : `EcosystemServiceCollectionExtensions` enregistre les compteurs en
`Scoped` (une instance par machine, comme le gestionnaire de processus), les
passe au fournisseur `/proc` (`ProcMetricsSources`), au service de fichiers, au
fournisseur IndexedDB et à `AppExecutionContextFactory`
(`SystemMetricsServices`). `EcosystemBootCoordinator` démarre
`ResourceSamplingService` juste après le moteur d'horloge.

## Comment chaque mesure est prise

### CPU (`/proc/stat`)

Mesure CPU active (ADR 0067) : `CpuTimeAccounting` ignore les échantillons
simulés et rend sur `cpu0` les vrais temps du thread principal (`user` = somme
des `utime`, `system` = somme des `stime` plus le compositeur, `idle` = le
reste) ; les autres cœurs du profil matériel restent `idle`. Avec les Web
Workers (ADR 0070), le cœur `k` reçoit en plus le temps occupé du worker du
pool qui porte ce cœur (`cpu0` est donc partagé entre le fil principal et le
worker du cœur 0), et le `utime` d'un processus dans un worker vient du
battement de cœur de son worker, plus le temps noyau passé sur le fil principal
à servir ses appels ; un worker qui ne bat plus compte ce temps comme occupé
par ses processus (voir [`worker-processes.md`](worker-processes.md)). Voir
[`cpu-accounting.md`](cpu-accounting.md). La colonne `nice` de `/proc/stat`
reste à `0` (`CpuTimes.Nice`) : le temps d'un processus dont `nice`/`renice`
ont changé la priorité (ADR 0127) reste compté dans `user`. Ce qui suit décrit le mode simulé,
toujours utilisé quand la mesure est coupée.

Avant ce chantier, rien n'appelait `IResourceSimulator.Tick` dans un hôte réel :
`status`, `meminfo` et `loadavg` restaient à zéro. `ResourceSamplingService`
s'inscrit sur l'horloge simulée et, chaque seconde simulée :

1. lit les processus actifs ;
2. demande un échantillon au simulateur (`Tick`) ;
3. ajoute l'intervalle à `CpuTimeAccounting` ;
4. oublie les compteurs d'E/S des processus disparus.

`CpuTimeAccounting` donne à chaque cœur `100 × secondes` unités par intervalle.
Un processus commence sur le cœur `pid mod cœurs` et déborde sur les suivants
quand il est plein, comme un ordonnanceur avec affinité : deux processus
occupés tombent sur des cœurs différents. Le temps va dans `system` pour les
processus de l'OS (services déclarés système par l'hôte et processus inconnus
du catalogue), dans `user` sinon ; le reste du cœur va dans `idle`.

Le simulateur tire l'usage de chaque processus de son `ResourceProfile`, c'est-à-dire
des poids `Resources` déclarés par le manifeste (ADR 0012). Jusqu'au chantier
`4-task-manager`, `AppLifecycleOrchestrator` lançait chaque processus avec
`ResourceProfile.None` : le CPU et la mémoire restaient à zéro malgré
l'échantillonnage. L'orchestrateur passe maintenant le profil du manifeste
(voir [`task-manager.md`](task-manager.md)).

Une seconde d'échantillonnage garde l'historique borné du simulateur
(120 échantillons) sur deux minutes, assez pour les graphiques.

### Entrées-sorties par processus (`/proc/<pid>/io`)

`FileSystemService` est le seul chemin vers les fichiers, pour les passerelles
des apps comme pour les commandes, et le seul endroit qui connaît le montage
d'un chemin. Quand l'appelant a un PID (`AppOperationContext.ProcessId`) :

- **Lecture** : le flux rendu est enveloppé ; chaque appel de lecture compte un
  `syscr` et les octets rendus dans `rchar`. Un `head -c 10` d'un gros fichier
  coûte dix octets.
- **Écriture** : la source de contenu est enveloppée ; les octets que le
  fournisseur en tire vont dans `wchar`, avec un `syscw` par écriture.
- **Stockage** : `read_bytes` et `write_bytes` reçoivent les mêmes octets
  quand le montage n'est pas virtuel (`/proc` et `/dev` en sont exclus).

`AppNetworkGateway` ajoute le trafic réseau à `rchar` et `wchar`, jamais à
`read_bytes` ni à `write_bytes`. Une opération système sans processus n'est
comptée nulle part. `cancelled_write_bytes` reste à `0`.

### Disque (`/proc/diskstats`)

Le fournisseur IndexedDB passe `IBlockDeviceStatistics` à ses trois
adaptateurs (métadonnées en lecture, métadonnées en écriture, contenu).
`IndexedDbInteropAdapter.ExecuteAsync` marque la requête en vol, la chronomètre
et la termine :

- transaction en lecture seule : une lecture ; en lecture-écriture : une
  écriture ;
- taille : les octets du fichier pour le contenu, un bloc de 4 Kio pour une
  transaction de métadonnées (la taille d'une mise à jour d'inode sous Linux) ;
- une transaction en échec compte dans le temps occupé, pas dans les requêtes
  terminées ;
- la recherche d'un chemin est une seule transaction quelle que soit sa
  profondeur (opération `resolvePath`, ADR 0090), donc une seule lecture, comme
  un noyau dont le cache des entrées de répertoire évite de relire chaque
  préfixe.

`BlockDeviceStatistics` tient `io_ticks` (temps avec au moins une requête en
vol) et `time_in_queue` (temps pondéré par la profondeur de file) comme le
noyau : à chaque changement de file, il ajoute le temps écoulé depuis le
précédent. Les champs « fusionnées » restent à `0` : il n'y a pas de file à
fusionner.

### Réseau (`/proc/net/dev`)

`AppNetworkGateway` reste le point de passage unique ([`sdk-network.md`](../framework/sdk-network.md)).
Chaque méthode publique appelle son implémentation, puis `NetworkTrafficMeter`
compte l'échange sur `eth0` :

| Opération | Envoyé | Reçu |
| --- | --- | --- |
| DNS (`Resolve`, `ReverseLookup`) | IP + UDP + question RFC 1035 | question + 16 octets par adresse |
| Ping | 84 octets (IP + ICMP + 56) | 84 octets si l'hôte répond |
| HTTP | IP + TCP + ligne de requête, en-têtes, cookies, formulaire | IP + TCP + ligne d'état, en-têtes, corps ; un aller-retour de plus par redirection |
| Sondage TCP | un SYN de 40 octets par port | un SYN-ACK ou RST par port non filtré |
| Détection d'OS | 16 sondes | 16 réponses si l'hôte est connu |

Un paquet est une requête ou une réponse (ADR 0057). Un hôte introuvable,
injoignable, un délai dépassé ou une connexion refusée comptent la requête et
une erreur de réception (`errs`). Un appel refusé faute de capacité ne sort
pas : rien n'est compté. Depuis `web/3-listener-dns`, un échange avec un
port local (cible résolue en loopback par `hackdns`) est compté sur `lo` et
non sur `eth0` ([`listening-ports-and-dns.md`](../framework/listening-ports-and-dns.md)).

### Montages (`/proc/mounts`)

`MountDescription.From` décrit chaque montage du routeur : `procfs` →
`proc /proc proc ro`, `devfs` → `devfs /dev devfs rw`, tout autre fournisseur →
`/dev/hda <point> idbfs rw`. Le fournisseur `/proc` lit le routeur à la demande,
parce que le routeur le contient.

### Espace disque (`HackDriveInfo`)

`AppDriveGateway` rend un `HackDriveSnapshot` par montage non virtuel
(aujourd'hui `/`) :

- **taille** : `StorageCapacityBytes`, ramenée au quota du navigateur quand il
  est plus petit ;
- **occupé** : depuis l'ADR 0099 §2, tout ce que l'origine stocke d'après
  `navigator.storage.estimate()` (fichiers, comptes, réglages, bases des
  sites). Quand le navigateur ne donne pas de quota, c'est encore la somme des
  longueurs des contenus distincts référencés par une entrée de fichier (deux
  fichiers identiques comptent une fois, comme leurs blocs dédupliqués) ;
- **libre** : taille − occupé, jamais négatif.

Aucune capacité n'est demandée, comme `df`.

## Utilisation

```csharp
// Usage CPU entre deux lectures, comme top.
string before = await HackFile.ReadAllTextAsync("/proc/stat");
await Task.Delay(1000);
string after = await HackFile.ReadAllTextAsync("/proc/stat");

foreach (HackDriveInfo drive in await HackDriveInfo.GetDrivesAsync())
{
    Console.WriteLine($"{drive.Name} {drive.DriveFormat} {drive.AvailableFreeSpace}/{drive.TotalSize}");
}
```

Lire `/proc` demande `filesystem.system.read`. `/proc/<pid>/io` est en `0400` :
seuls le propriétaire et l'autorité `System` le lisent.

## Décisions clés et précisions par rapport à l'ADR 0057

- **Comptage des fichiers dans `FileSystemService`.** L'ADR parle de « la
  passerelle fichier » : c'est le service derrière toutes les passerelles
  (`AppFileSystemGateway`, commandes, `HackFile`). Lui seul sait si un chemin
  touche le stockage ou un montage virtuel.
- **`ResourceSamplingService` est nouveau.** L'ADR suppose que le simulateur
  avance à chaque tick ; aucun code ne l'appelait. L'échantillonnage se fait
  chaque seconde simulée, pas chaque tick de 100 ms, pour garder deux minutes
  d'historique.
- **`hda` ne compte que le système de fichiers.** Les autres magasins IndexedDB
  (utilisateurs, groupes, autorisations, synchronisation) ne sont pas des
  fichiers ; ils ne passent pas par cet adaptateur instrumenté.
- **Tailles des métadonnées et des paquets estimées.** IndexedDB ne donne pas la
  taille de ses écritures de métadonnées, ni le réseau simulé celle de ses
  paquets : un bloc de 4 Kio par transaction de métadonnées, et des en-têtes IP,
  TCP, UDP et ICMP standard autour des données réellement échangées.
- **Temps du disque en temps réel.** Les millisecondes de `diskstats` sont
  mesurées sur l'horloge réelle : c'est le vrai temps passé par IndexedDB.
- **`HackDriveInfo` lit ses valeurs une fois.** Le navigateur ne répond qu'en
  asynchrone ; `GetDrivesAsync` rend des instances figées, à redemander pour des
  valeurs fraîches.
- **Chiffres réels du navigateur (ADR 0099 §2).** `df` montre l'utilisé et
  le quota de `navigator.storage.estimate()` ; la réserve de root et l'alerte
  d'espace faible utilisent les mêmes chiffres
  ([`system-backup.md`](system-backup.md#espace-de-stockage-adr-0099-2)).
- **Espace occupé sans les blocs orphelins.** Les blocs en attente de nettoyage
  ne sont plus référencés par aucun fichier et ne sont pas comptés.

## Tests

- `Tests/HackerOs.Platform.Core.Tests/FileSystem/Virtual/ProcMetricsTests.cs` :
  format exact de `stat`, `diskstats`, `net/dev`, `mounts` ; débordement d'un
  cœur à l'autre ; échantillonnage par seconde simulée ; `io` compté par le
  service de fichiers, sans octets de stockage pour `/proc` ; `io` privé ;
  temps occupé pondéré par la file.
- `Tests/HackerOs.Platform.Core.Tests/Execution/AppDriveGatewayTests.cs` :
  taille, quota, espace libre jamais négatif, `HackDriveInfo` dans le contexte
  ambiant.
- `Tests/HackerOs.Network.Tests/Wave4NetworkTests.ProcMetrics.cs` : DNS, ping,
  HTTP et refus de capacité comptés (ou non) sur `eth0` et dans `rchar`/`wchar`.
- `Tests/HackerOs.Infrastructure.Browser.Tests/IndexedDbFileSystemReaderTests.cs` :
  espace occupé dédupliqué, transactions signalées à `hda`.

## Liste des tâches du chantier `rebuild/4-proc-metrics`

- [x] Contrats `Metrics` dans Simulation Abstractions ; `VirtualHardwareProfile.StorageCapacityBytes`.
- [x] Compteurs en mémoire dans `Platform.Core/Metrics`.
- [x] `ResourceSamplingService` démarré au boot : le simulateur avance enfin.
- [x] Comptage des fichiers par processus dans `FileSystemService`.
- [x] Comptage du trafic dans `AppNetworkGateway` (`NetworkTrafficMeter`).
- [x] Comptage des transactions IndexedDB du système de fichiers pour `hda`.
- [x] `/proc/stat`, `/proc/<pid>/io`, `/proc/diskstats`, `/proc/net/dev`, `/proc/mounts`.
- [x] `HackDriveInfo`, `IAppDriveGateway`, `IAppExecutionContext.Drives`, `AppDriveGateway`, `IndexedDbStorageUsageProvider`.
- [x] Câblage dans l'hôte Ecosystem.
- [x] Tests.
- [x] Documentation : ce document, `virtual-proc-and-dev.md`, `app-sdk.md`, `sdk-network.md`, index.
