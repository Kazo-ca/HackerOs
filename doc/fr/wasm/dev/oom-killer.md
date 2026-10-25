> [English version](../../../en/wasm/dev/oom-killer.md)

# OOM killer et garde de la mémoire du navigateur

Ce document couvre la mise en œuvre de l'OOM killer et de la garde du navigateur. Ce qu'une app voit (fichiers `/proc` et `/dev/kmsg`, `AppDefinition.OomScoreAdjust`, raison de fin `OutOfMemory`) est dans [`../framework/process-resources-api.md`](../framework/process-resources-api.md).

Chantier `exec/3-oom-killer`. Décisions : [ADR 0126](adr/0126-oom-killer.md)
et ses ajustements [ADR 0142](adr/0142-oom-killer-implementation-adjustments.md).

## Rôle

Quand la mémoire disponible du système simulé devient trop basse, l'OS choisit
un processus par son `oom_score`, le tue (`SIGKILL`, sortie 137, « Killed ») et
l'écrit dans le journal du noyau, comme le `oom_killer` de Linux. Il surveille
aussi la vraie mémoire de la page (runtime .NET du fil principal et de chaque
Web Worker) et avertit avant que le navigateur ne fasse planter l'onglet.

L'OOM killer est **désactivé par défaut** et n'agit que si la mémoire est
**mesurée** (`MemSource` différent de `simulated`, ADR 0066) : on ne tue pas
sur des valeurs inventées. La garde du navigateur avertit toujours ; elle ne
tue que si l'OOM killer est activé.

## Architecture

| Pièce | Projet | Rôle |
| --- | --- | --- |
| `OomKiller` | Platform.Core | Échantillonne chaque seconde simulée ; deux échantillons de suite sous le seuil déclenchent le choix d'une victime. Garde du navigateur à 80 % du plafond. Compteur `KillCount`. |
| `OomKillerSettings`, `MemorySettingsDocuments` | Platform.Core | Document `/etc/hackeros/memory.config`, section `[oom-killer]`. |
| `SystemMemoryProbe` | Platform.Core | `MemTotal` (profil simulé), `MemAvailable` et `VmRSS` mesurés (ADR 0068), `oom_score`. |
| `InMemoryProcessOomScoreTable` | Platform.Core | `oom_score_adj` de chaque processus, avec son plancher (ADR 0142 §1-2). |
| `KernelLog` | Platform.Core | Tampon du noyau de la session (1 024 lignes), copié dans `/var/log/kern.log` et `syslog` ; lu par `/dev/kmsg`. |
| `BrowserRuntimeMemorySource` | Platform.Core | Tas géré de chaque runtime contre le plafond de 2 Gio (ADR 0142 §4). |
| `IWorkerMemoryReclaimer` (`WorkerPool`) | WorkerHost | Remplace le worker qui a hébergé une victime dès qu'il est vide (ADR 0142 §5). |
| `NotificationQueueOomNotifier` | Platform.Core | Notifications traduites : app à fenêtre fermée, limite du navigateur proche. |
| `MemoryPanel` | Apps.Settings | Onglet **Mémoire** de Paramètres. |
| `dmesg`, `stress --vm` | Commands | Lecture du journal du noyau ; charge mémoire de test. |

`EcosystemServiceCollectionExtensions` enregistre la table, le journal et
l'OOM killer ; `EcosystemBootCoordinator` l'attache aux réglages et le démarre
une fois le catalogue et les comptes prêts.

### Choix de la victime

- `oom_score = VmRSS × 1000 / MemTotal + oom_score_adj`, borné à `[0, 2000]`.
- `oom_score_adj = -1000` exempte. Le plus haut score perd ; à égalité, le PID
  le plus haut.
- Aucune nouvelle victime tant que la précédente n'est pas sortie.
- Garde du navigateur : la victime est choisie parmi les processus du runtime
  visé seulement (`CONSTRAINT_MEMCG`, `oom_memcg=/hackeros/<runtime>`).

### Traces

```text
[  812.402113] oom-kill:constraint=CONSTRAINT_NONE,nodemask=(null),cpuset=/,mems_allowed=0,global_oom,task_memcg=/,task=stress,pid=42,uid=1000
[  812.402150] Out of memory: Killed process 42 (stress) total-vm:503808kB, anon-rss:503808kB, file-rss:0kB, shmem-rss:0kB, UID:1000 oom_score_adj:0
```

## Utilisation

### Activer

```sh
sudo nano /etc/hackeros/memory.config
```

```ini
[oom-killer]
enabled=true
min-free-percent=5
```

Ou Paramètres › Mémoire (demande d'administrateur). `min-free-percent` va de
`0` à `50` ; une valeur hors bornes est ramenée et signalée dans `syslog`.

Les fichiers `/proc/<pid>/oom_score`, `oom_score_adj`, `/proc/vmstat` et `/dev/kmsg`, ainsi que la déclaration de `OomScoreAdjust` dans une app, sont décrits dans [`../framework/process-resources-api.md`](../framework/process-resources-api.md).

### Commandes

- `dmesg [-T] [-x] [-l NIVEAU[,NIVEAU…]] [-k] [-h]`.
- `stress --vm N [--vm-bytes B] [--vm-keep]` (`B` : `256M` par défaut,
  suffixes `K`, `M`, `G`).
- `echo 500 > /proc/<pid>/oom_score_adj`.

### Réglage d'hôte

`HackerOs:Hardware:MemoryMiB` (appsettings) fixe `MemTotal` du profil simulé.
`build/verify-hosts.sh` l'utilise dans le parcours `ecosystem-oom`
(`OOM_MEMORY_MIB`, 512 par défaut).

## Décisions

- Désactivé par défaut, actif seulement avec une mesure (réponse Q3 de Benoit).
- Plafond du navigateur à 2 Gio, comparé au tas géré (ADR 0142 §4).
- Worker d'une victime remplacé pour rendre vraiment la mémoire (ADR 0142 §5).
- Le bureau et le gestionnaire de fenêtres ne sont pas des processus, donc
  jamais candidats (ADR 0142 §3).
- Notification seulement pour une app à fenêtre (ADR 0142 §9).
- Le journal du noyau est celui de la session : recharger la page est un
  redémarrage.

## Liste des tâches

- [x] Réglage `/etc/hackeros/memory.config`, migration et schéma.
- [x] Mesure de `MemAvailable`, `oom_score`, choix de la victime, mise à mort `OutOfMemory`.
- [x] `oom_score_adj` en table, plancher, héritage, écriture dans `/proc`.
- [x] Journal du noyau, `/dev/kmsg`, `kern.log`, `dmesg`.
- [x] `/proc/vmstat` avec `oom_kill`.
- [x] Notification d'une app à fenêtre tuée (anglais, français).
- [x] Garde du vrai navigateur, remplacement du worker.
- [x] `AppDefinition.OomScoreAdjust`, `oomScoreAdj` du manifeste, `HOSAPP0024`, `-500` des services système.
- [x] Onglet Mémoire de Paramètres.
- [x] `stress --vm`.
- [x] Parcours `ecosystem-oom` de `build/verify-hosts.sh`.
