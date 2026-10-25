# ADR 0068 : mémoire mesurée par processus, en couches

## Statut

Accepté le 2026-10-16.

- **Phase du plan :** phase 5 — exécution des apps
  ([`execution-isolation-plan.md`](../plans/execution-isolation-plan.md)).
- **Optionnel :** interrupteur `HackerOsMemoryAccounting` /
  `HackerOs:ResourceAccounting:Memory:Enabled` (ADR 0066).
- **S'appuie sur :** ADR 0067 (frames ambiants et changements de contexte).

## Contexte

Toutes les apps partagent le même tas .NET. Le runtime ne dit pas quelle part
du tas appartient à quelle app, et le navigateur ne donne qu'une mesure
globale, et seulement avec l'isolation cross-origin, que nous écartons.

Compter le *marshaling* (les données échangées entre C# et JavaScript) a été
envisagé et écarté : il mesure le trafic d'interop, pas la mémoire. Une app en
C# pur qui garde 200 Mo afficherait zéro.

Benoit veut **mesurer seulement** : pas de limite par processus, pas d'OOM
killer.

## Décision

### 1. Quatre couches

| Couche | Source | Précision | Champ `/proc/<pid>/status` |
|---|---|---|---|
| Image | taille des assemblies de l'app, connue par son `manifest.json` et le catalogue de l'hôte ; une assembly partagée est comptée une fois, dans `RssFile` partagé | exacte | `VmExe`, `RssFile` |
| Ressources de l'OS | octets tenus par l'OS pour le processus : tampons de `HackFileStream`, entrées du presse-papiers, tubes, sockets, fenêtres ouvertes (coût fixe par fenêtre) | exacte | `VmData` (part noyau) |
| Allocations | `GC.GetAllocatedBytesForCurrentThread()` lu à chaque changement de contexte (ADR 0067, section 1) ; un seul thread exécute le code, donc la différence revient au processus courant | exacte en octets alloués | `HackAllocBytes` |
| Mémoire résidente | après chaque GC (`GC.RegisterForFullGCNotification` n'existe pas en WASM : on compare `GC.CollectionCount(0)` à chaque échantillon), le tas réel (`GC.GetGCMemoryInfo().HeapSizeBytes`) est réparti entre les processus au prorata de leurs allocations récentes, avec une décroissance exponentielle | estimation | `VmRSS`, `RssAnon` |

`VmRSS = RssAnon + RssFile + part noyau`. Les règles de calcul (fenêtre de la
décroissance, coût d'une fenêtre) sont des constantes documentées dans
`system-metrics.md` et couvertes par des tests.

### 2. Lecture

- `/proc/<pid>/status` donne les champs ci-dessus, et `MemSource: measured`
  (ADR 0066) ;
- `/proc/meminfo` additionne les processus et garde `MemTotal` du
  `VirtualHardwareProfile` ;
- `HackProcess.WorkingSet64` (existe déjà, lu dans `VmRSS`) et le nouveau
  `PrivateMemorySize64` (lu dans `RssAnon`), calqués sur
  `System.Diagnostics.Process`.

### 3. Pas de limite

Aucune limite, aucun OOM killer : dépasser `MemTotal` ne fait que saturer les
graphiques. Si une limite devient utile, un nouvel ADR la décidera.

### 4. Dans un Web Worker

Un processus seul dans son worker (ADR 0070) a son propre runtime : son tas
est **exactement** le sien, et le worker l'envoie avec son battement de cœur.
Dans un worker du pool, la répartition de la section 1 s'applique à l'intérieur
du worker.

## Conséquences

- La mémoire affichée devient crédible pour toutes les apps, sans changement
  de leur code.
- La couche résidente reste une estimation, ce que `system-metrics.md` dit
  clairement.
- Lire le compteur d'allocations à chaque changement de contexte se fait dans
  le même rappel que l'ADR 0067. Si seule la mémoire est active, le rappel est
  installé pour elle seule.

## Références

- [`execution-isolation-plan.md`](../plans/execution-isolation-plan.md)
- `proc(5)` : `/proc/<pid>/status`, `/proc/meminfo`.
- ADR 0012, 0057, 0066, 0067, 0070.
