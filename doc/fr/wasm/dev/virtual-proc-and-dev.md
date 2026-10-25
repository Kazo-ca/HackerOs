> [English version](../../../en/wasm/dev/virtual-proc-and-dev.md)

# `/proc` et `/dev` virtuels

Mise en œuvre de la section 5.4 du [plan de refonte](plans/ecosystem-rebuild-plan.md)
(décision D20) selon l'[ADR 0046](adr/0046-virtual-proc-and-dev-filesystems.md).

## Rôle

HackerOS expose l'état du système comme Linux : `/proc` décrit les processus,
la mémoire, le CPU et la charge ; `/dev` offre les périphériques `null`, `zero`,
`random` et `urandom`, plus `kmsg` quand l'hôte fournit le journal du noyau
(ADR 0126). Les deux montages sont **calculés à chaque lecture** et
**jamais écrits dans IndexedDB**.

## Architecture

Code : `Platform/HackerOs.Platform.Core/FileSystem/Virtual/`.

| Type | Rôle |
| --- | --- |
| `IVirtualFileSystemProvider` | Marqueur d'un fournisseur calculé, jamais persisté. `FileSystemService` n'émet aucun `FileSystemChangeEvent` pour ses chemins. |
| `VirtualFileSystemProvider` | Base commune : identité stable (SHA-256 de `"<providerId>:<chemin>"`), révision = `CurrentTick + 1`, refus `PermissionDenied` de toute création, suppression, déplacement, copie et `chmod`. |
| `ProcFileSystemProvider` (`procfs`) | Monté sur `/proc`. Lit `IProcessManager`, `IResourceSimulator`, `VirtualHardwareProfile` et `ISimulationClock`, et les compteurs de `ProcMetricsSources` (ADR 0057, [`system-metrics.md`](system-metrics.md)). |
| `DevFileSystemProvider` (`devfs`) | Monté sur `/dev`. Quatre entrées `CharacterDevice` en `0666`, plus `kmsg` en `0644` quand l'hôte fournit le journal du noyau (`KernelLog`, ADR 0126). |

Branchement : `EcosystemServiceCollectionExtensions` passe les deux montages au
`FileSystemMountRouter`, pour les deux hôtes WASM. Le gestionnaire de processus
est résolu **à la demande** : il dépend du service de session, qui dépend du
`FileSystemSeeder`, qui atteint `/proc` par le routeur. Une résolution immédiate
fermerait un cycle.

Points de montage : le routeur ne fusionne pas les montages dans le listage du
parent. `FileSystemSeeder` reçoit donc le routeur et crée `/proc` (`0555`) et
`/dev` (`0755`), vides, **directement dans le fournisseur racine**. `ls /` les
montre, puis le fournisseur virtuel les masque.

Contrats touchés :

- `FileSystemEntryKind.CharacterDevice` et `CharacterDeviceMetadata`
  (`Shared/HackerOs.Simulation.Abstractions/FileSystem/FileSystemEntryMetadata.cs`).
  Les fournisseurs IndexedDB et mémoire refusent de créer ce type
  (`UnsupportedOperation`). `ls -l` l'affiche avec `c` (et les liens avec `l`).
- `AppOperationContext.ProcessId` (`long?`), renseigné par
  `AppExecutionContextFactory`. Il sert à `/proc/self` et à la visibilité.

## Contenu

### `/proc`

| Chemin | Type et mode | Contenu |
| --- | --- | --- |
| `/proc/<pid>/` | dossier `0555`, propriétaire = utilisateur du processus | — |
| `/proc/<pid>/status` | fichier `0444` | voir ci-dessous |
| `/proc/<pid>/cmdline` | fichier `0444` | arguments séparés et terminés par NUL ([`process-environment.md`](process-environment.md)) ; `appId\0` sans entrée d'environnement |
| `/proc/<pid>/environ` | fichier `0400` | paires `NOM=valeur` terminées par NUL (phase 3) ; vide sans entrée |
| `/proc/<pid>/cwd` | lien `0777` | répertoire courant du processus (phase 3) ; `/home/<login>` sans entrée |
| `/proc/<pid>/stat` | fichier `0444` | une ligne au format `proc(5)`, 52 champs ; `utime` et `stime` (14 et 15) en `USER_HZ`, mesurés quand la mesure CPU est active, sinon `utime` reconstruit depuis le simulateur (phase 5, ADR 0067, [`cpu-accounting.md`](cpu-accounting.md)) ; `priority` et `nice` (18 et 19) depuis la valeur `nice` du processus (ADR 0127, [`process-priority-and-unresponsive-windows.md`](process-priority-and-unresponsive-windows.md)) |
| `/proc/<pid>/limits` | fichier `0444` | tableau de `proc(5)` limité aux quatre limites appliquées (`Max cpu time`, `Max resident set`, `Max processes`, `Max open files`), puis `# enforcement: strict (worker)`, `best-effort (main thread)` ou `off (HackerOs:ResourceLimits:Enabled=false)` et, sans mesure, `# cpu, rss: inactive (not measured)` (ADR 0125 et 0139, [`process-limits.md`](process-limits.md)) |
| `/proc/<pid>/fd/` | dossier `0500` | un lien par descripteur ouvert : `0`, `1`, `2` vers `/dev/tty` (terminal) ou `/dev/null`, puis le fichier lu ou écrit (`exec/2`) |
| `/proc/<pid>/oom_score` | fichier `0444` | `VmRSS × 1000 / MemTotal + oom_score_adj`, borné à `[0, 2000]` ; `0` pour un processus exempté (`oom_score_adj` = `-1000`) (ADR 0126, [`oom-killer.md`](oom-killer.md)) |
| `/proc/<pid>/oom_score_adj` | fichier `0644` | `-1000` à `1000` ; le propriétaire monte la valeur, descendre sous le plancher demande `sudo` (ADR 0142 §2) |
| `/proc/<pid>/io` | fichier `0400` | `rchar`, `wchar`, `syscr`, `syscw`, `read_bytes`, `write_bytes`, `cancelled_write_bytes` (phase 4, ADR 0057) |
| `/proc/self` | lien `0777` | `/proc/<pid de l'appelant>`, absent sans processus |
| `/proc/meminfo` | fichier `0444` | `MemTotal`, `MemFree`, `MemAvailable` en kB ; avec la mémoire mesurée (ADR 0068), aussi `AnonPages`, `Mapped`, `Slab` |
| `/proc/vmstat` | fichier `0444` | `nr_free_pages`, puis `nr_anon_pages`, `nr_mapped`, `nr_slab_unreclaimable` avec la mémoire mesurée, puis `oom_kill` (`exec/3`, ADR 0142 §7) |
| `/proc/cpuinfo` | fichier `0444` | un bloc par cœur (`ceil(CpuCapacity)`) |
| `/proc/uptime` | fichier `0444` | `<secondes depuis le démarrage> <secondes d'inactivité>` |
| `/proc/loadavg` | fichier `0444` | `<1 min> <5 min> <15 min> <actifs>/<total> <dernier pid>` |
| `/proc/stat` | fichier `0444` | lignes `cpu` et `cpuN` en `USER_HZ`, `btime`, `processes`, `procs_running`, `procs_blocked` (phase 4) ; temps réels du thread principal sur `cpu0` quand la mesure CPU est active (ADR 0067) |
| `/proc/diskstats` | fichier `0444` | une ligne pour `hda` (majeur 8, mineur 0), 14 champs (phase 4) |
| `/proc/net/` | dossier `0555` | contient `dev` et `tcp` |
| `/proc/net/dev` | fichier `0444` | en-tête du noyau, une ligne pour `lo` et une pour `eth0` (phase 4) ; le trafic local est compté sur `lo` depuis `web/3` |
| `/proc/net/tcp` | fichier `0444` | sockets TCP à l'écoute au format `proc(5)` (adresse hexadécimale petit-boutiste, état `0A`, `uid`, inode), lus dans `ListeningPortTable` ; démons de `localhost` compris (`web/3`, [`listening-ports-and-dns.md`](../framework/listening-ports-and-dns.md)) |
| `/proc/mounts` | fichier `0444` | une ligne par montage, format `fstab` (phase 4) |

Exemple de `/proc/42/status` :

```text
Name:	code-editor-del
State:	R (running)
Pid:	42
PPid:	7
Uid:	aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
AppId:	org.hackeros.apps.code-editor-deluxe
Kind:	window
Session:	bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
VmRSS:	3072 kB
Cpu:	12.5
CpuSource:	simulated
MemSource:	simulated
```

`Name` est le dernier segment de l'`appId`, tronqué à 15 caractères. Pour une
tâche `HackBackground` (ADR 0128, ADR 0140, [`background-jobs.md`](../framework/background-jobs.md)),
`Name` est le nom de la tâche (`code-editor:sea`, tronqué de même) et deux
lignes suivent `Session` : `Job:` (le nom entier, `code-editor:search`) et
`Host:` (`MainThread` ou `Worker`). `Cpu` est
un pourcentage d'un cœur (`CpuUsage × 100`), sans le signe `%`, pour rester
facile à analyser. `CpuSource` et `MemSource` (ADR 0066) disent d'où viennent
`Cpu` et `VmRSS` : `measured` quand la mesure de la phase 5 répond pour ce
processus, `simulated` sinon (mesure coupée, pas encore implémentée, ou
processus simulé sans code) ; voir
[`resource-accounting.md`](resource-accounting.md).

Quand la mémoire d'un processus est mesurée (ADR 0068,
[`memory-accounting.md`](memory-accounting.md)), cinq lignes suivent `VmRSS`,
dans l'ordre de Linux : `RssAnon`, `RssFile`, `VmData` (part noyau), `VmExe`
en kB, puis `HackAllocBytes` (octets alloués depuis le démarrage du processus,
sans unité). `VmRSS = RssAnon + RssFile + VmData`. Un processus simulé n'a
aucune de ces lignes : le format est exactement celui d'avant la phase 5.

Quand le CPU est mesuré (ADR 0067), `Cpu` est l'usage de la dernière seconde
et quatre lignes suivent `MemSource` :

```text
Responding:	1
LongTasks:	3
voluntary_ctxt_switches:	1520
nonvoluntary_ctxt_switches:	0
```

`Responding: 0` signale un processus « Not responding » ; `LongTasks` compte
ses tranches de plus de 50 ms. Mesure coupée, ces lignes n'apparaissent pas :
le fichier est exactement celui d'avant la phase 5. Détails :
[`cpu-accounting.md`](cpu-accounting.md).

Choix de calcul :

- `MemAvailable` vaut `MemFree` : la simulation n'a pas de cache à libérer.
- L'inactivité d'`uptime` est cumulée sur tous les cœurs, comme sous Linux :
  `uptime × (cœurs − usage moyen)`, l'usage moyen venant de l'historique système
  borné.
- `loadavg` fait la moyenne de `TotalCpuUsage` sur les échantillons de
  l'historique compris dans chaque fenêtre. L'historique étant borné, les
  fenêtres de 5 et 15 minutes se réduisent aux échantillons disponibles.
- Horodatages : démarrage du processus pour `/proc/<pid>`, démarrage de l'OS
  (`maintenant − tick × durée du tick`) sinon ; le contenu des fichiers est daté
  de maintenant. La taille des fichiers est toujours `0`.

Fichiers de mesure (phase 4, [ADR 0057](adr/0057-proc-metrics-for-task-manager.md)).
Leurs compteurs et la façon dont ils sont pris sont décrits dans
[`system-metrics.md`](system-metrics.md). Exemples :

```text
$ cat /proc/stat
cpu  4705 0 1210 93620 0 0 0 0 0 0
cpu0 2402 0 611 46790 0 0 0 0 0 0
cpu1 2303 0 599 46830 0 0 0 0 0 0
btime 1791640800
processes 312
procs_running 3
procs_blocked 0

$ cat /proc/42/io
rchar: 18342
wchar: 2210
syscr: 12
syscw: 3
read_bytes: 16384
write_bytes: 2048
cancelled_write_bytes: 0

$ cat /proc/diskstats
   8       0 hda 1204 0 9632 410 388 0 3104 220 0 630 630

$ cat /proc/net/dev
Inter-|   Receive                                                |  Transmit
 face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed
    lo:       0       0    0    0    0     0          0         0        0       0    0    0    0     0       0          0
  eth0:  482113     214    3    0    0     0          0         0    38211     214    0    0    0     0       0          0

$ cat /proc/mounts
/dev/hda / idbfs rw 0 0
devfs /dev devfs rw 0 0
proc /proc proc ro 0 0
```

- `stat` cumule le temps CPU simulé depuis le démarrage : un lecteur calcule
  l'usage par différence entre deux lectures, comme `top`. `processes` est le
  dernier PID attribué (les PID sont attribués dans l'ordre depuis 1).
- `io` suit la règle de visibilité d'`environ` : le propriétaire, ou l'autorité
  `System`.
- Depuis la phase 4, `ResourceSamplingService` fait avancer le simulateur chaque
  seconde simulée : `status`, `meminfo`, `uptime` et `loadavg` ne restent plus à
  zéro dans un hôte réel.

- `/proc/hackeros/workers` (ADR 0071 §5) n'existe que si l'hôte a des Web
  Workers : d'abord l'isolation cross-origin de la page
  (`cross-origin-isolated: yes|no`, raison, réglage, COEP, tubes partagés,
  appels synchrones, mesure de la mémoire, résolution des minuteries ; ADR 0131
  §5, ADR 0143 §8), puis capacité, taille du pool, puis une ligne par worker (rôle, état,
  cœur, mémoire, âge du battement, temps occupé, PID visibles). Format :
  [`worker-processes.md`](worker-processes.md#prochackerosworkers).

Visibilité : seuls les processus actifs apparaissent. L'appelant voit toujours
son propre processus, et les autres avec `process.list` ou l'autorité `System`.
Un PID invisible répond `NotFound`.

### `/dev`

| Chemin | Lecture | Écriture |
| --- | --- | --- |
| `/dev/null` | fin de flux immédiate | absorbée |
| `/dev/zero` | flux infini de `0x00` | absorbée |
| `/dev/random`, `/dev/urandom` | flux infini de `RandomNumberGenerator` | absorbée |
| `/dev/kmsg` (`0644`, seulement si l'hôte fournit le journal du noyau) | lignes du noyau de la session, `prio,seq,usec,-;message` ; lu par `dmesg` | root seulement : ligne ajoutée au journal (`exec/3`, ADR 0142 §7) |

Les flux infinis rendent au plus 4 Kio par lecture et respectent l'annulation :
un lecteur borne lui-même sa lecture. En lecture asynchrone, ils cèdent la main
au navigateur tous les 16 blocs, pour qu'un `cat /dev/zero` laisse l'interface
répondre et puisse être interrompu. Une écriture vide le flux reçu, réussit
sans changer de révision et n'émet aucun événement.

## Utilisation

Tout passe par l'API fichier ordinaire (`IFileSystemService`, puis
`IAppFileSystemGateway` côté app). Lire `/proc` ou `/dev` demande
`filesystem.system.read` ; écrire dans `/dev` demande `filesystem.system.write`.

`ps` lit maintenant `/proc` : il liste `/proc`, lit chaque `/proc/<pid>/status`
et prend l'heure de création de `/proc/<pid>` comme heure de démarrage. Son
manifeste déclare `filesystem.system.read` en plus de `process.list`. La colonne
`STAT` affiche la lettre Linux (`R`, `S`…). `kill` garde `IAppProcessGateway`
pour agir.

## Décisions clés

- **Migration de `ps` avancée.** L'ADR 0046 (section 5) plaçait cette migration
  en phase 3. Elle est faite dès la phase 1, comme le demande la section 5.4 du
  plan, parce qu'elle ne dépend que de `/proc`. La refonte des commandes de la
  phase 3 la conserve. Le gestionnaire de tâches est passé à `/proc` en
  phase 4 ([`task-manager.md`](task-manager.md), `HackProcess`).
- **Pas d'événements de changement** : le marqueur `IVirtualFileSystemProvider`
  est vérifié au seul endroit qui publie, `FileSystemService`.
- **Mutations refusées par le fournisseur lui-même**, en plus des bits de mode :
  même l'autorité `System`, qui ignore les modes, reçoit `PermissionDenied`.
  Deux écritures font exception depuis la série `exec` : `/proc/<pid>/oom_score_adj`
  (seul fichier inscriptible de `/proc`, ADR 0126 §4 et 0142 §2) et une ligne
  écrite dans `/dev/kmsg` par root (ADR 0142 §7) ; ni l'une ni l'autre n'est
  persistée.

## Tests

- `Tests/HackerOs.Platform.Core.Tests/FileSystem/Virtual/` : listage de `/`,
  formats exacts de `status`, `meminfo`, `cpuinfo`, `uptime` et `loadavg`,
  visibilité selon `process.list`, `/proc/self`, refus des mutations, `/dev/null`,
  lecture bornée de `/dev/zero`, annulation, refus de `CharacterDevice` par le
  stockage, et aucune écriture dans le dépôt racine.
- `Tests/HackerOs.Commands.Wave5.Tests/PsCommandTests.cs` : `ps` contre le vrai
  fournisseur `/proc`.
- `Tests/HackerOs.Platform.Core.Tests/FileSystem/Virtual/ProcMetricsTests.cs` :
  fichiers de mesure de la phase 4 (voir [`system-metrics.md`](system-metrics.md)).

## Liste des tâches

- [x] Ajouter `FileSystemEntryKind.CharacterDevice` et `CharacterDeviceMetadata`.
- [x] Faire refuser `CharacterDevice` par les fournisseurs IndexedDB et mémoire.
- [x] Ajouter `AppOperationContext.ProcessId` et le renseigner.
- [x] Écrire la base `VirtualFileSystemProvider` et le marqueur `IVirtualFileSystemProvider`.
- [x] Écrire `ProcFileSystemProvider`.
- [x] Écrire `DevFileSystemProvider`.
- [x] Couper les événements de changement pour les montages virtuels.
- [x] Créer les points de montage `/proc` et `/dev` dans `FileSystemSeeder`.
- [x] Monter les deux fournisseurs dans `EcosystemServiceCollectionExtensions`.
- [x] Afficher `c` et `l` dans `ls -l`.
- [x] Faire lire `/proc` par `ps`.
- [x] Écrire les tests de contrat.
- [x] `/proc/<pid>/limits` et `/proc/<pid>/fd/` (`exec/2-process-limits`).
- [x] `/proc/<pid>/oom_score`, `oom_score_adj`, `oom_kill` de `/proc/vmstat` et `/dev/kmsg` (`exec/3-oom-killer`).
