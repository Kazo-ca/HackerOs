> [English version](../../../../en/wasm/dev/adr/0057-proc-metrics-for-task-manager.md)

# ADR 0057 : Mesures système dans `/proc` et `HackDriveInfo`

## Statut

Accepté le 2026-10-10.

- **Phase du plan :** phase 4 — SDK complet et apps de base
  ([`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), section 8.2,
  gestionnaire de tâches : « Toutes les données viennent de `/proc` »).
- **Complète :** ADR 0046. Les fichiers décrits ici s'ajoutent au fournisseur
  `procfs` existant ; ses règles (lecture seule, calculé à la lecture, jamais
  persisté, taille `0`, visibilité des PID) s'appliquent sans changement.
- **S'appuie sur :** ADR 0012 (horloge et processus), ADR 0050
  (`/proc/<pid>/environ` et sa règle de visibilité), ADR 0056 (`HackProcess`
  lit `/proc`).

## Contexte

Le plan demande au gestionnaire de tâches un onglet performance (graphiques
CPU et mémoire), des onglets disque et réseau comme dans le legacy
(`src/apps/system-monitor.ts`), et un détail par processus, le tout lu dans
`/proc`. Aujourd'hui `/proc` offre `meminfo`, `cpuinfo`, `uptime`, `loadavg`
et, par processus, `status` (dont `VmRSS` et `Cpu`), `cmdline`, `environ` et
`cwd`. Il manque :

- un compteur de temps CPU cumulé, pour tracer l'usage par cœur et l'usage
  total dans le temps ;
- toute mesure de disque : ni débit, ni espace occupé ;
- toute mesure de réseau ;
- les entrées-sorties par processus.

Le simulateur de ressources (`IResourceSimulator`) produit des échantillons de
charge (`CpuUsage`, `MemoryUsageBytes`, `StorageIoBytes`, `NetworkIoBytes`)
tirés de l'activité déclarée des apps. Les octets réellement lus et écrits par
les apps ne sont comptés nulle part.

## Décision

### 1. Principe : deux sources

- **CPU et mémoire** restent simulés : ils viennent du simulateur, comme
  `status`, `meminfo` et `loadavg` aujourd'hui.
- **Disque et réseau** sont **comptés pour de vrai** : la passerelle fichier
  compte les octets que chaque processus lit et écrit, la persistance IndexedDB
  compte les opérations et les octets réellement envoyés au stockage, et la
  passerelle réseau (ADR 0056, section 6) compte les requêtes et les octets
  échangés. Un `cat` d'un gros fichier se voit donc dans le gestionnaire de
  tâches.

Les compteurs vivent en mémoire, partent de zéro au démarrage et ne sont
jamais persistés, comme sous Linux.

### 2. `/proc/stat`

Format Linux, temps en `USER_HZ` (100 par seconde simulée) depuis le
démarrage :

```text
cpu  4705 0 1210 93620 0 0 0 0 0 0
cpu0 2402 0 611 46790 0 0 0 0 0 0
cpu1 2303 0 599 46830 0 0 0 0 0 0
btime 1791640800
processes 312
procs_running 3
procs_blocked 0
```

- Une ligne `cpuN` par cœur (`ceil(CpuCapacity)`, comme `cpuinfo`), et la
  ligne `cpu` qui les additionne.
- Colonnes `user`, `nice`, `system`, `idle`, `iowait`, `irq`, `softirq`,
  `steal`, `guest`, `guest_nice`. Le simulateur ne distingue pas le noyau :
  le temps occupé va dans `user` pour les apps et dans `system` pour les
  processus de l'OS (`AppKind.Service` système et processus sans app) ;
  `nice`, `iowait`, `irq` et la suite restent à `0`.
- Les compteurs **cumulent** l'usage de chaque tick du simulateur : un lecteur
  calcule l'usage entre deux lectures par différence, comme `top`.
- `btime` : démarrage de l'OS en secondes Unix. `processes` : nombre de
  processus créés depuis le démarrage. `procs_running` : processus actifs.

### 3. `/proc/<pid>/io`

```text
rchar: 18342
wchar: 2210
syscr: 12
syscw: 3
read_bytes: 16384
write_bytes: 2048
cancelled_write_bytes: 0
```

- `rchar` / `wchar` : octets lus et écrits par le processus, fichiers et
  réseau confondus. `syscr` / `syscw` : nombre d'appels de lecture et
  d'écriture à la passerelle.
- `read_bytes` / `write_bytes` : la part qui touche le stockage persistant
  (le système de fichiers IndexedDB), hors `/proc`, `/dev` et réseau.
- `cancelled_write_bytes` : octets d'un fichier supprimé avant d'être relu,
  toujours `0` dans cette phase.
- Droits `0400`, propriétaire = utilisateur du processus, avec la même règle de
  visibilité qu'`environ` (ADR 0050) : le propriétaire, ou l'autorité
  `System`.

### 4. `/proc/diskstats`

Une ligne par disque, format Linux (14 premiers champs) :

```text
   8       0 hda 1204 0 9632 410 388 0 3104 220 0 630 630
```

- Un seul disque, `hda`, majeur 8 mineur 0 : la base IndexedDB de l'OS.
- Lectures et écritures **terminées**, secteurs de 512 octets lus et écrits,
  millisecondes passées, mesurées autour des transactions IndexedDB par la
  couche de persistance.
- Les fournisseurs virtuels (`/proc`, `/dev`) ne sont pas des disques et n'y
  apparaissent pas.

### 5. `/proc/net/dev`

Format Linux, deux interfaces :

```text
Inter-|   Receive                                                |  Transmit
 face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed
    lo:       0       0    0    0    0     0          0         0        0       0    0    0    0     0       0          0
  eth0:  482113     214    3    0    0     0          0         0    38211     214    0    0    0     0       0          0
```

- `eth0` : le réseau simulé et le repli réel (ADR 0056, section 6). Un
  « paquet » est une requête ou une réponse ; `errs` compte les hôtes
  introuvables et les échecs.
- `lo` : réservé, toujours à zéro dans cette phase.

### 6. `/proc/mounts`

Format de `/etc/fstab`, une ligne par montage du routeur
(`FileSystemMountRouter`) :

```text
/dev/hda / idbfs rw 0 0
proc /proc proc ro 0 0
devfs /dev devfs rw 0 0
```

Les types `idbfs` et `devfs` sont propres à HackerOS ; `proc` est celui de
Linux.

### 7. `HackDriveInfo`

L'espace disque n'est pas un fichier sous Linux (`statfs`). Le SDK l'expose
comme `System.IO.DriveInfo` :

```csharp
foreach (HackDriveInfo drive in await HackDriveInfo.GetDrivesAsync())
{
    // drive.Name = "/", drive.DriveFormat = "idbfs", drive.TotalSize, drive.AvailableFreeSpace
}
```

- `GetDrivesAsync` rend les montages de `/proc/mounts` qui ont une taille :
  aujourd'hui seulement `/`.
- **Taille totale :** une nouvelle propriété du profil matériel virtuel,
  `VirtualHardwareProfile.StorageCapacityBytes` (8 Gio par défaut), bornée par
  le quota du navigateur quand il est connu (`navigator.storage.estimate()`,
  appelé par `Infrastructure.Browser`, ADR 0055 : jamais par l'app).
- **Espace occupé :** octets des blocs stockés par la persistance
  dédupliquée. `AvailableFreeSpace` = taille totale − espace occupé, jamais
  négatif.
- **Capacité : aucune**, comme `df` sous Linux.

### 8. Ce que lit le gestionnaire de tâches

| Écran | Source |
|---|---|
| Processus (liste, CPU, mémoire) | `HackProcess` → `/proc/<pid>/status` |
| Détail d'un processus | `/proc/<pid>/status`, `cmdline`, `cwd`, `environ` (si lisible), `io` |
| Performance : CPU total et par cœur | `/proc/stat`, par différence |
| Performance : mémoire | `/proc/meminfo` |
| Disque : débit | `/proc/diskstats`, par différence |
| Disque : espace | `HackDriveInfo` |
| Réseau : débit par interface | `/proc/net/dev`, par différence |
| Services | `HackServiceController` (ADR 0056) |

## Conséquences

- `ps`, `top` (s'il est écrit un jour) et toute app tierce lisent les mêmes
  données que le gestionnaire de tâches, sans API dédiée.
- La passerelle fichier, la persistance et la passerelle réseau ajoutent un
  comptage par opération ; il reste un incrément d'entier, sans allocation.
- Le disque et le réseau montrent l'activité réelle des apps, alors que le CPU
  reste simulé. C'est voulu : le navigateur ne donne aucune mesure CPU
  fiable par app, mais il connaît exactement les octets échangés.

## Alternatives écartées

- **Disque et réseau tirés du simulateur** (`StorageIoBytes`,
  `NetworkIoBytes`) : cohérent avec le CPU, mais un fichier copié ne ferait
  pas bouger le graphique ; l'utilisateur verrait une activité sans rapport
  avec ce qu'il fait.
- **Une API `HackPerformanceCounter`** au lieu de fichiers : plus simple à
  consommer, mais contraire à « tout est fichier » (décision D20) et à la
  consigne du plan.
- **Espace disque dans un fichier `/proc`** : Linux ne le fait pas ;
  `DriveInfo` est l'équivalent .NET attendu par un développeur.

## Références

- `proc(5)`, `proc_pid_io(5)`, `proc_stat(5)` ; documentation du noyau Linux,
  `Documentation/admin-guide/iostats.rst`.
- ADR 0012, 0046, 0050, 0055, 0056 ;
  [`virtual-proc-and-dev.md`](../virtual-proc-and-dev.md).
- `Platform/HackerOs.Platform.Core/FileSystem/Virtual/ProcFileSystemProvider.cs`,
  `Shared/HackerOs.Simulation.Abstractions/Processes/ResourceSimulationContracts.cs`.
