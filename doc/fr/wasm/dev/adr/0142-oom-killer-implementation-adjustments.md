# ADR 0142 : ajustements de la mise en œuvre de l'OOM killer

## Statut

Accepté le 2026-10-18, chantier `exec/3-oom-killer`.

- **Complète :** ADR 0126 (OOM killer et garde du vrai navigateur). Précise les
  points que le code a dû trancher et corrige deux hypothèses de l'ADR 0126 :
  le plafond de la mémoire WebAssembly (§4) et l'emplacement du réglage dans
  Paramètres (§8).
- **Doc :** [`oom-killer.md`](../oom-killer.md).

## Contexte

L'ADR 0126 fixe le réglage (`/etc/hackeros/memory.config`), le choix de la
victime par `oom_score`, les traces du noyau, `oom_score_adj` et la garde du
vrai navigateur. En l'écrivant, plusieurs détails sont apparus : où vit
`oom_score_adj`, qui a le droit de le baisser, ce qui reçoit `-1000` dans un OS
où le bureau n'est pas un processus, quel chiffre comparer au plafond du
navigateur et comment un worker rend vraiment sa mémoire.

## Décision

### 1. Une table à côté du processus

`oom_score_adj` vit dans une table tenue par le gestionnaire de processus
(`InMemoryProcessOomScoreTable`), comme les limites de l'ADR 0139 §1 :
l'entrée naît au `Start` et disparaît à la fin du processus. `ProcessRecord` ne
change pas. `ProcessStartRequest.OomScoreAdjust` (ajout du SDK 1.2) donne la
valeur de départ ; sans elle, l'enfant hérite de l'entrée de son parent, sinon
`0`.

### 2. Plancher : baisser demande un administrateur

Chaque entrée garde une **valeur** et un **plancher**, comme
`oom_score_adj_min` de Linux. Une écriture dans `/proc/<pid>/oom_score_adj`
par le propriétaire peut monter la valeur ou la ramener jusqu'au plancher ;
descendre sous le plancher demande root élevé (`sudo`) ou le compte système, et
déplace alors le plancher (comme `CAP_SYS_RESOURCE`). Une valeur déclarée au
lancement pose la valeur et le plancher. Un enfant hérite des deux.

### 3. Valeurs par défaut

- Une app **système** applique sa valeur déclarée telle quelle ; une app non
  système déclarée négative est ramenée à `0` au lancement (le build la refuse
  déjà, §6 ; le runtime se protège d'un manifeste installé à la main).
- Sans valeur déclarée, un **service système** reçoit `-500`
  (`SystemServiceOomScoreAdjust`) ; tout le reste hérite.
- Le bureau, le gestionnaire de fenêtres et les services de session ne sont
  **pas des processus** dans HackerOS : ils ne sont jamais candidats, ce qui
  vaut le `-1000` que l'ADR 0126 §4 leur donnait.

### 4. Plafond du navigateur : 2 Gio, et le tas géré

- Le plafond de chaque runtime est le maximum que le runtime .NET déclare pour
  sa mémoire WebAssembly : 32 768 pages de 64 Kio, **2 Gio**, pas les 4 Gio de
  wasm32 que l'ADR 0126 §5 supposait. `jsHeapSizeLimit` mesure le tas
  JavaScript, pas la mémoire WebAssembly : il n'est pas utilisé.
- Le chiffre comparé au plafond est le **tas géré** (`GC.GetGCMemoryInfo`, et
  `RuntimeHeapBytes` remonté par chaque worker), pas la taille de
  `WebAssembly.Memory` : cette mémoire ne rétrécit jamais, une fois grandie
  elle paraîtrait pleine pour toujours.

### 5. Un worker qui a hébergé une victime est remplacé

Tuer un processus d'un worker ne rend pas la mémoire au navigateur : la mémoire
WebAssembly du worker garde sa taille. Juste avant le `SIGKILL`, l'OOM killer
marque le worker de la victime (`IWorkerMemoryReclaimer.RecycleAfterOomKill`) ;
quand ce worker n'héberge plus aucun processus, le pool le retire et en démarre
un autre (rôles `pool` et `overflow` ; le worker « prêt » de l'ADR 0071 est
toujours neuf). Sur le fil principal, l'OS lâche les références et force un
ramassage, comme l'ADR 0126 §5 le disait.

### 6. Code `HOSAPP0024`

Le générateur de manifeste refuse une app non système dont la définition
déclare `OomScoreAdjust` négatif : erreur `HOSAPP0024` à l'agrégation de
`BuiltInApplications.json`, là où l'on sait quelles apps sont système. Le
schéma du manifeste borne `oomScoreAdj` à `-1000…1000`
(`manifest.oomScoreAdj.range`).

### 7. Journal du noyau, `/dev/kmsg`, `dmesg`, `/proc/vmstat`

- `KernelLog` est un tampon de 1 024 lignes de la **session** : recharger la
  page est un redémarrage, le tampon repart vide. Chaque ligne va aussi dans
  `/var/log/kern.log` et `/var/log/syslog`.
- `/dev/kmsg` (mode `0644`, format `prio,seq,usec,-;message`) n'existe que si
  l'hôte fournit ce journal. Y écrire demande root, comme sous Linux.
- `dmesg` lit `/dev/kmsg` ; options `-T` (heure locale, par `/proc/uptime`),
  `-x` (niveau en clair), `-l NIVEAU[,NIVEAU…]`, `-k` (accepté : tout est
  noyau) et `-h`.
- `/proc/vmstat` affiche `nr_free_pages`, puis `nr_anon_pages`, `nr_mapped`
  et `nr_slab_unreclaimable` quand la mémoire est mesurée, puis `oom_kill`.

### 8. Onglet Mémoire de premier niveau dans Paramètres

Paramètres n'a pas de groupe « Système » : la page est un onglet **Mémoire**
après Utilisateurs (et non « Système › Mémoire » de l'ADR 0126 §1). Elle montre
`MemTotal`, `MemAvailable`, la source de mesure et `oom_kill`, et écrit le
document `memory` dans la couche système, ce qui ouvre la demande
d'administrateur.

### 9. Notifications

Seule une app à **fenêtre** tuée laisse une notification (source
`org.hackeros.kernel`, nom traduit de l'app). Une commande tuée affiche déjà
« Killed » et le code 137 dans son terminal. L'avertissement de la garde du
navigateur va à l'utilisateur de la session, une fois par runtime.

### 10. `stress --vm`

`stress` gagne `-m N`/`--vm N`, `--vm-bytes B` (`256M` par défaut, suffixes
`K`, `M`, `G`) et `--vm-keep` : chaque travailleur alloue par blocs de 1 Mio,
touche un octet tous les 4 Kio, et passe un point d'arrêt entre deux blocs
(ADR 0069). Sans `--vm-keep`, il libère et recommence.

### 11. Vérification

- Réglage d'hôte `HackerOs:Hardware:MemoryMiB` : fixe `MemTotal` du profil
  matériel simulé.
- `build/verify-hosts.sh` gagne le parcours `ecosystem-oom` : une copie de la
  publication avec `MemoryMiB=512` (`OOM_MEMORY_MIB`). Le test active l'OOM
  killer par `printf … | sudo tee /etc/hackeros/memory.config`, lance
  `stress --vm 1 --vm-bytes` calculé depuis `/proc/meminfo`, attend la sortie
  137 et « Killed », puis vérifie `dmesg` et `oom_kill 1` dans `/proc/vmstat`.

## Conséquences

- `ProcessExitReason.OutOfMemory`, `ProcessStartRequest.OomScoreAdjust`,
  `AppDefinition.OomScoreAdjust` et `AppManifest.OomScoreAdj` entrent dans
  `PublicAPI.Unshipped.txt` et seront publiés avec le SDK 1.2 (ADR 0132).
- Une commande intégrée de plus, `dmesg` : 74 commandes et 92 apps avec `nice` et `renice` d'`exec/4`.
- Un worker remplacé coûte un démarrage de runtime ; cela n'arrive qu'après une
  mise à mort par l'OOM killer.

## Références

- ADR 0066, 0068, 0069, 0071, 0104, 0125, 0126, 0132, 0139.
- `proc(5)` (`oom_score`, `oom_score_adj`, `vmstat`), `dmesg(1)`, `stress(1)`.
