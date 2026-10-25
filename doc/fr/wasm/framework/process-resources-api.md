> [English version](../../../en/wasm/framework/process-resources-api.md)

# Ressources d'un processus côté apps (CPU, mémoire, OOM killer)

Ce document décrit ce qu'une app voit et peut faire avec le temps CPU, la mémoire et l'OOM killer d'un processus : `HackProcess`, `/proc/<pid>/stat`, `/proc/<pid>/status`, `/proc/meminfo` et `/proc/<pid>/oom_score_adj`. La mise en œuvre est dans [`../dev/cpu-accounting.md`](../dev/cpu-accounting.md), [`../dev/memory-accounting.md`](../dev/memory-accounting.md) et [`../dev/oom-killer.md`](../dev/oom-killer.md).

## Ce que la mesure change pour une app

Aucune app ne change une ligne : tout passe par le frame ambiant que l'OS ouvre
déjà autour de leur code (ADR 0043).

## Temps CPU

### Lire

```bash
cat /proc/42/stat
# 42 (code-editor) R 1 42 42 0 -1 0 0 0 0 0 153 27 0 0 20 0 1 0 1234 4194304 1024 18446744073709551615 0 0 ... 0
grep -E 'Cpu|Responding|LongTasks|ctxt' /proc/42/status
cat /proc/stat
```

```csharp
HackProcess editor = await HackProcess.GetProcessByIdAsync(42);
TimeSpan user = editor.UserProcessorTime;          // utime
TimeSpan system = editor.PrivilegedProcessorTime;  // stime
TimeSpan total = editor.TotalProcessorTime;
bool responding = editor.Responding;               // false : « Not responding »
```

| Fichier | Mesure active | Mesure coupée |
|---|---|---|
| `/proc/<pid>/stat` | `utime`, `stime` mesurés (champs 14 et 15, `USER_HZ`) | `utime` reconstruit depuis l'historique borné du simulateur (un échantillon par seconde), `stime` à `0` |
| `/proc/<pid>/status` | `Cpu` = usage de la dernière seconde, `CpuSource: measured`, puis `Responding`, `LongTasks`, `voluntary_ctxt_switches`, `nonvoluntary_ctxt_switches` | exactement le fichier d'avant : `Cpu` simulé, `CpuSource: simulated`, sans les quatre champs |
| `/proc/stat` | `cpu0` = le thread principal (`user`, `system` = `stime` + compositeur, `idle`) ; le cœur `k` ajoute le temps occupé du worker du pool qui le porte (ADR 0070, [`worker-processes.md`](../dev/worker-processes.md)) ; sans worker, les autres cœurs restent `idle` | temps simulé (ADR 0057) |

Un processus qui n'a encore rien exécuté (ou un processus simulé,
qui n'exécute pas de code) n'est pas mesuré : il garde ses valeurs simulées.

## Comportement sous charge

- **Attention (ADR 0127 §4)** : une app fenêtrée tourne sur le fil principal.
  Une boucle longue sans `await` y gèle tout le bureau, souris comprise, et
  aucune boîte ne peut s'afficher pendant le gel. Un calcul lourd va dans
  `HackBackground.RunAsync` (ADR 0128) ; une boucle longue qui doit rester sur
  le fil appelle `CheckpointAsync` (ADR 0069) assez souvent pour rendre la main.

Une fenêtre sans réponse est décrite dans [`app-responsiveness-api.md`](app-responsiveness-api.md).

## Sous-frame noyau

- **Sous-frame noyau réservé à l'OS.** `EnterKernel` est interne au SDK et
  visible de Platform Core seulement : une app ne peut pas déclarer son propre
  code en temps système.

## Mémoire

### Mesuré ou simulé

Un processus est mesuré si le catalogue de l'hôte connaît son image (toutes
les apps intégrées) ou s'il a exécuté du code sous son frame (le shell, une
app installée au runtime). Les autres (processus simulés sans code) restent
simulés, comme le veut l'ADR 0066 §4.
### Lecture

```text
$ cat /proc/42/status
...
VmRSS:	4712 kB
RssAnon:	3000 kB
RssFile:	1200 kB
VmData:	512 kB
VmExe:	40 kB
HackAllocBytes:	123456789
Cpu:	0.0
CpuSource:	simulated
MemSource:	measured
```

```text
$ cat /proc/meminfo
MemTotal:        4194304 kB
MemFree:         4063232 kB
MemAvailable:    4063232 kB
AnonPages:        102400 kB
Mapped:            20480 kB
Slab:               4096 kB
```

- `MemTotal` reste celui du `VirtualHardwareProfile`.
- Mémoire utilisée = `AnonPages + Mapped + Slab` + mémoire simulée des
  processus restés simulés.
- `HackProcess.WorkingSet64` lit `VmRSS` ; `HackProcess.PrivateMemorySize64`
  lit `RssAnon` (et vaut `WorkingSet64` quand la mémoire est simulée), comme
  `System.Diagnostics.Process`.

## Mémoire et OOM killer

L'OOM killer est désactivé par défaut et n'agit que si la mémoire est mesurée (`MemSource` différent de `simulated`). Une app tuée par lui se termine avec la raison `ProcessExitReason.OutOfMemory` (SDK 1.2) ; une app à fenêtre voit sa fenêtre se fermer et l'utilisateur reçoit une notification (ADR 0142 §9).

### Fichiers

| Chemin | Accès | Contenu |
| --- | --- | --- |
| `/proc/<pid>/oom_score` | lecture | Score actuel. |
| `/proc/<pid>/oom_score_adj` | lecture, écriture | `-1000` à `1000`. Le propriétaire monte la valeur ; descendre sous le plancher demande `sudo`. |
| `/proc/vmstat` | lecture | `nr_free_pages`, puis `nr_anon_pages`, `nr_mapped`, `nr_slab_unreclaimable` si mesuré, puis `oom_kill`. |
| `/dev/kmsg` | lecture ; écriture root | `prio,seq,usec,-;message`. |
| `/var/log/kern.log` | lecture | Lignes du noyau (rotation 1 Mio × 5, ADR 0045). |
### Déclarer une valeur dans une app

```csharp
public sealed class MyServiceDefinition : AppDefinition
{
    // Seule une app système peut déclarer une valeur négative (HOSAPP0024).
    public override int? OomScoreAdjust => -200;
}
```

Le manifeste généré porte `"oomScoreAdj": -200`. Un service système sans
valeur reçoit `-500` ; les autres processus héritent de leur parent.
