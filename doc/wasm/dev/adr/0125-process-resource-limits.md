# ADR 0125 : limites de ressources par processus (`setrlimit`, `ulimit`, `limits.conf`)

## Statut

Accepté le 2026-10-18. Une question est posée à Benoit (§6, valeurs par
défaut) ; ce qui suit applique la réponse recommandée en attendant.

- **Plan :** [`execution-followups-plan.md`](../plans/execution-followups-plan.md),
  décision 2 (réponse Q1 : « au mieux » sur le fil principal) ; mis en œuvre
  par `exec/2-process-limits`.
- **Remplace :** le principe « mesurer, pas contraindre » de la phase 5
  ([`execution-isolation-plan.md`](../plans/execution-isolation-plan.md) §3) pour les
  limites de cet ADR.
- **Complète :** ADR 0067 (temps CPU), 0068 (mémoire), 0069 (escalade).

## Contexte

La phase 5 mesure le CPU et la mémoire de chaque processus (ADR 0066 à 0068)
mais n'en limite aucun. Un processus peut aussi ouvrir des descripteurs ou
lancer des enfants sans borne. Linux règle cela par `setrlimit` : une limite
**souple** (celle qui s'applique) et une limite **dure** (le plafond de la
souple), héritées au `fork`, posées à la connexion par `pam_limits` depuis
`/etc/security/limits.conf`.

## Décision

### 1. Limites retenues

| Ressource | Nom Linux | `ulimit` | Mesure | Dépassement |
|---|---|---|---|---|
| Temps CPU | `RLIMIT_CPU` | `-t` (secondes) | `utime + stime` (ADR 0067) | souple : `SIGXCPU`, puis chaque seconde ; dure : `SIGKILL` |
| Mémoire résidente | `RLIMIT_RSS` | `-m` (Kio) | `VmRSS` mesuré (ADR 0068) | `SIGKILL`, raison `MemoryLimitExceeded` |
| Descripteurs ouverts | `RLIMIT_NOFILE` | `-n` | table des descripteurs (`/proc/<pid>/fd`) | ouverture refusée : `EMFILE`, « Too many open files » |
| Processus de l'utilisateur | `RLIMIT_NPROC` | `-u` | processus vivants de l'UID | lancement refusé : `EAGAIN`, « Resource temporarily unavailable » |

- Linux ignore `RLIMIT_RSS` depuis 2.6 ; HackerOS l'applique, parce que c'est la
  seule mémoire qu'il mesure. La documentation le dit.
- `RLIM_INFINITY` s'écrit `unlimited`.
- Une limite fondée sur une mesure (CPU, mémoire) est **inactive** quand cette
  mesure est coupée (`CpuSource`/`MemSource: simulated`, ADR 0066) ; `ulimit`
  l'affiche quand même, et `/proc/<pid>/limits` le note (§4).
- `NOFILE` et `NPROC` sont comptés par l'OS lui-même : ils s'appliquent
  partout, au fil principal comme en worker.
- Un réglage d'hôte `HackerOs:ResourceLimits:Enabled` (`appsettings.json`,
  `true` par défaut) coupe toutes les limites : l'OS se comporte alors comme
  aujourd'hui et tout est `unlimited`.

### 2. Application stricte en worker, au mieux sur le fil principal

- **Dans un worker**, `SIGXCPU` lève `StopRequested` avec la nouvelle raison
  `ProcessExitReason.CpuLimitExceeded` ; sans fin du processus, la limite dure
  mène à `SIGKILL` par l'escalade de l'ADR 0069, jusqu'à l'étape 4 (worker
  terminé). Le dépassement de la mémoire tue tout de suite (étape 3 puis 4) :
  rien ne permet de refuser une allocation .NET.
- **Sur le fil principal**, l'OS ne voit la mesure qu'entre deux tranches
  (ADR 0067 §4) : `SIGXCPU` et `SIGKILL` partent à la fin de la tranche qui
  franchit la limite, et l'escalade s'arrête à l'étape 3. Une boucle qui ne rend
  jamais la main n'est pas arrêtée.
- Codes de sortie : 152 (128 + `SIGXCPU`, 24) si le processus se termine sur
  `SIGXCPU`, 137 après `SIGKILL`. Le shell écrit « CPU time limit exceeded » ou
  « Killed », comme bash.

### 3. Sources et héritage

- **Valeurs du noyau** (avant toute configuration) : celles de §6.
- **`/etc/security/limits.conf`** et **`/etc/security/limits.d/*.conf`**
  (ordre alphabétique), format de `pam_limits` :
  `<domaine> <type> <élément> <valeur>`, domaine `utilisateur`, `@groupe` ou
  `*`, type `soft`, `hard` ou `-`, éléments `cpu`, `rss`, `nofile`, `nproc`.
  Lus à l'ouverture de session (premier processus de la session) ; une ligne
  invalide est ignorée et écrite dans `/var/log/auth.log`, comme `pam_limits`.
  Fichiers `system:system 0644` ; l'OS livre un `limits.conf` entièrement
  commenté, comme les distributions.
- **Héritage** : un enfant reçoit les limites de son parent au lancement.
- **Changement** : un processus baisse ses limites librement, monte la souple
  jusqu'à la dure ; seul un processus élevé (`sudo`, ADR 0042) monte la dure.

### 4. Surfaces

- **`ulimit`** intégré à `hsh` (il agit sur le shell et ses futurs enfants) :
  `-a`, `-S`, `-H`, `-t`, `-m`, `-n`, `-u`. Sans lettre, bash vise `-f` (taille
  de fichier) ; HackerOS n'a pas cette limite, donc `ulimit` seul affiche
  `unlimited` et `ulimit <n>` répond « ulimit: file size: cannot modify limit:
  Operation not permitted ».
- **`prlimit`** : `prlimit --pid <pid> [--cpu=soft:hard] [--rss=…] [--nofile=…]
  [--nproc=…]`, sur un processus du même utilisateur, ou sur tout processus
  avec `sudo`.
- **`/proc/<pid>/limits`** : le tableau de Linux (`Limit`, `Soft Limit`,
  `Hard Limit`, `Units`) pour les quatre lignes, suivi d'une ligne de
  commentaire propre à HackerOS : `# enforcement: strict (worker)`,
  `# enforcement: best-effort (main thread)` ou `# cpu, rss: inactive (not measured)`.
- **SDK** (ajouts du SDK 1.2, ADR 0132) : `HackProcess.Current.GetResourceLimits()`
  (enregistrement `HackResourceLimits`), `HackProcess.SetResourceLimitAsync(kind,
  soft, hard)`, `ProcessExitReason.CpuLimitExceeded` et `MemoryLimitExceeded`,
  `FileSystemErrorCode.TooManyOpenFiles` (`EMFILE`) traduit en `HackIOException`,
  et un échec de lancement qui porte `EAGAIN`. Les noms exacts sont fixés par
  le chantier dans `PublicAPI.Unshipped.txt`, dans l'esprit de cette liste.

### 5. Exemptions

`root` élevé n'est pas soumis à `NPROC` (comme `CAP_SYS_RESOURCE`). Les
processus système du compte `hackeros-svc` (ADR 0107) reçoivent les limites du
noyau, pas celles de `limits.conf` (ils ne passent pas par une connexion).

### 6. Valeurs par défaut (question posée à Benoit)

Appliqué en attendant sa réponse (recommandé) : valeurs d'une distribution
Linux courante pour les compteurs, illimité pour ce qui dépend d'une mesure.

| Élément | Souple | Dure |
|---|---|---|
| `cpu` | `unlimited` | `unlimited` |
| `rss` | `unlimited` | `unlimited` |
| `nofile` | 1024 | 524288 |
| `nproc` | 4096 | 4096 |

L'autre choix proposé : tout `unlimited` par défaut, rien ne change pour une
app tant qu'un administrateur n'écrit pas `limits.conf`. Si Benoit le choisit,
un ADR court remplace ce paragraphe.

## Conséquences

- Contrat de limites dans Simulation Abstractions, table des limites dans
  `ProcessRecord`, héritage au lancement, contrôle dans la table des
  descripteurs et au lancement d'un processus.
- Chaque commande traite `EMFILE` et `EAGAIN` comme une erreur d'E/S ordinaire
  (message, code de sortie 1).
- `build/verify-hosts.sh` gagne le parcours `ulimit -t 1` puis `stress`, sortie
  152, dans un worker.
- Docs : [`linux-commands.md`](../linux-commands.md) (`ulimit`, `prlimit`),
  [`virtual-proc-and-dev.md`](../virtual-proc-and-dev.md) (`limits`),
  [`worker-processes.md`](../worker-processes.md), nouveau
  `process-limits.md`.

## Références

- ADR 0042, 0066, 0067, 0068, 0069, 0070, 0107, 0132.
- `setrlimit(2)`, `limits.conf(5)`, `prlimit(1)`, `proc(5)`.
