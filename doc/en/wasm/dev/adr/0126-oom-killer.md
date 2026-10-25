# ADR 0126 : OOM killer désactivé par défaut et garde de la mémoire du vrai navigateur

## Statut

Accepté le 2026-10-18.

- **Plan :** [`execution-followups-plan.md`](../plans/execution-followups-plan.md),
  décisions 3 et 4 (réponses Q2 « simulée, estimation si possible » et Q3
  « désactivé ») ; mis en œuvre par `exec/3-oom-killer`, après
  `exec/2-process-limits`.
- **Remplace :** l'ADR 0068 §3 (« pas de limite, aucun OOM killer »).
- **Complète :** ADR 0068 (couches de mémoire), ADR 0069 (escalade),
  ADR 0125 (limite `rss` par processus).

## Contexte

L'ADR 0068 mesure la mémoire de chaque processus en couches (tas .NET, octets
des fichiers ouverts, worker) et affiche `MemTotal` du profil matériel simulé.
Dépasser `MemTotal` sature seulement les graphiques. Benoit a choisi de garder
`MemTotal` simulé, d'estimer l'occupation au plus juste, et de **ne pas**
activer l'OOM killer par défaut : la réponse par défaut à une app qui gêne est
la boîte « L'application ne répond pas » (ADR 0127), pas une mise à mort.

## Décision

### 1. Activation

- Document de l'OS `/etc/hackeros/memory.config` (ADR 0044), section
  `[oom-killer]` :

  ```text
  [oom-killer]
  enabled=false
  min-free-percent=5
  ```

- `enabled=true` ne suffit pas : il faut aussi que la mémoire soit mesurée
  (`MemSource` différent de `simulated`, ADR 0066). Sinon, l'OS l'écrit une fois
  dans `/var/log/syslog` (« oom-killer: disabled, memory is not measured ») et
  ne tue rien : on ne tue pas sur des valeurs inventées.
- Le fichier appartient à `system` : le modifier demande un administrateur.
  Paramètres l'affiche dans Système › Mémoire, avec la mention de la mesure.

### 2. Déclenchement et choix de la victime

- L'OS échantillonne chaque seconde. Quand `MemAvailable` passe sous
  `min-free-percent` de `MemTotal` **deux échantillons de suite**, il choisit
  une victime.
- `oom_score` d'un processus = `VmRSS × 1000 / MemTotal + oom_score_adj`,
  borné à `[0, 2000]` ; `oom_score_adj = -1000` exempte. Le plus haut score
  perd ; à égalité, le plus récent.
- La victime passe par `SIGKILL` (ADR 0069, étape 3 ; étape 4 en worker),
  raison `ProcessExitReason.OutOfMemory` (ajout du SDK 1.2, ADR 0132).
  Aucune nouvelle victime n'est choisie tant que la précédente n'est pas sortie.

### 3. Traces, comme le noyau

- Ligne de `/var/log/kern.log` et `/var/log/syslog` :
  `Out of memory: Killed process 1234 (hackpaint) total-vm:…kB, anon-rss:…kB, file-rss:…kB, shmem-rss:0kB, UID:1000 oom_score_adj:0`.
- Nouvelle commande **`dmesg`** : tampon du noyau de la session (lignes
  `kern.*` depuis le démarrage, horodatées en secondes depuis le boot) ;
  `dmesg -T` pour l'heure locale.
- Nouveau fichier `/proc/vmstat`, avec pour commencer le compteur `oom_kill`.
- Une app à fenêtre tuée laisse une notification à l'utilisateur :
  « « Hack Paint » a été fermée : mémoire insuffisante » (textes en anglais et
  en français, ADR 0104).

### 4. `oom_score` et `oom_score_adj`

- `/proc/<pid>/oom_score` (lecture) et `/proc/<pid>/oom_score_adj` (lecture et
  écriture, `-1000` à `1000`). Le propriétaire peut monter la valeur ; la
  baisser demande un administrateur, comme `CAP_SYS_RESOURCE`. La valeur est
  héritée par les enfants.
- Une app **système** peut déclarer une valeur négative dans sa définition
  (`AppDefinition.OomScoreAdjust`, ajout du SDK 1.2) ; une app non système qui
  le fait est refusée au build par le générateur de manifeste (nouveau code
  `HOSAPP` choisi par le chantier). Le bureau, le gestionnaire de fenêtres et
  les services de session reçoivent `-1000`, les services système `-500`.

### 5. Garde de la mémoire du vrai navigateur

- L'OS suit la mémoire WebAssembly du runtime principal et de chaque worker
  (taille de `WebAssembly.Memory`, plafond de 4 Gio en wasm32, ou
  `jsHeapSizeLimit` quand le navigateur le donne).
- À 80 % du plafond, il écrit un avertissement dans `kern.log` et une
  notification une seule fois par runtime : « La page approche de la limite de
  mémoire du navigateur ».
- Si l'OOM killer est activé, il choisit en plus une victime **parmi les
  processus de ce runtime** (le worker visé, ou le fil principal) même si
  `MemAvailable` simulé reste haut. Tuer un processus d'un worker rend la
  mémoire au navigateur ; sur le fil principal, l'OS lâche les références du
  processus et demande un ramassage.

### 6. Estimation

L'occupation reste celle de l'ADR 0068 ; quand l'isolation cross-origin est
active (ADR 0131), la couche d'un worker devient la mesure réelle de
`measureUserAgentSpecificMemory()`. Le champ `MemSource`
(ADR 0066) dit laquelle est utilisée.

## Conséquences

- Les apps qui gardent un état (éditeurs, CodeEditor, explorateur, Paramètres)
  gardent leur brouillon de récupération quand elles sont tuées, comme avec
  « Force kill ».
- `build/verify-hosts.sh` gagne un parcours avec `MemTotal` simulée réduite,
  OOM killer activé, `stress --vm` (option ajoutée par le chantier), ligne `Out of memory` vérifiée dans `dmesg`.
- Docs : [`memory-accounting.md`](../memory-accounting.md),
  [`virtual-proc-and-dev.md`](../virtual-proc-and-dev.md),
  [`linux-commands.md`](../linux-commands.md) (`dmesg`), nouveau
  `oom-killer.md`.

## Références

- ADR 0044, 0066, 0068, 0069, 0104, 0125, 0127, 0131, 0132.
- `proc(5)` (`oom_score`, `oom_score_adj`), `dmesg(1)`.
