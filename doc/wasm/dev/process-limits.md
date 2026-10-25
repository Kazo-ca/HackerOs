# Limites de processus (`ulimit`, `prlimit`, `limits.conf`)

Chantier `exec/2-process-limits` (2026-10-18). Décisions : ADR 0125 (limites
par processus) et ADR 0139 (ajustements de la mise en œuvre).

## Rôle

Chaque processus porte quatre limites façon `setrlimit(2)`, avec une valeur
souple (celle qui s'applique) et une valeur dure (son plafond) :

| Limite | `ulimit` | `/proc/<pid>/limits` | Défaut (souple / dure) | Effet quand elle est atteinte |
|---|---|---|---|---|
| `cpu` | `-t` (secondes) | `Max cpu time` | illimité / illimité | souple : `SIGXCPU` (sortie 152, « CPU time limit exceeded ») ; dure : `SIGKILL` (137, « Killed ») |
| `rss` | `-m` (Kio) | `Max resident set` | illimité / illimité | `SIGKILL` (137) |
| `nofile` | `-n` | `Max open files` | 1024 / 524288 | ouverture refusée : `EMFILE`, « Too many open files » |
| `nproc` | `-u` | `Max processes` | 4096 / 4096 | lancement refusé : `EAGAIN`, « hsh: fork: Resource temporarily unavailable » |

Ce sont les défauts du noyau Linux. Un processus hérite des limites de son
parent ; un processus sans parent prend celles de la session, lues dans
`/etc/security/limits.conf` à la connexion.

L'application est **stricte dans un Web Worker** (l'OS termine le worker) et
**au mieux sur le fil principal** (vérifiée entre deux tranches de travail).
Les limites `cpu` et `rss` ne s'appliquent que là où le CPU et la mémoire sont
mesurés ([`resource-accounting.md`](resource-accounting.md)) ; sinon
`/proc/<pid>/limits` les dit inactives.

## Architecture

| Élément | Projet | Rôle |
|---|---|---|
| `ResourceLimitKind`, `ResourceLimit`, `ProcessResourceLimits`, `ResourceLimitChangeResult`, `ProcessLimitExceededException` | Simulation Abstractions | Contrat. `ResourceLimit.Unlimited` vaut `long.MaxValue`. |
| `IProcessResourceLimitTable` / `InMemoryProcessResourceLimitTable` | Abstractions / Platform.Core | Limites de chaque processus vivant, à côté de `ProcessRecord` (ADR 0139 §1). Règles de `setrlimit` : baisser toujours, monter la souple jusqu'à la dure, monter la dure en root élevé seulement. Désactivée par le réglage de l'hôte. |
| `IProcessFileDescriptorTable` / `InMemoryProcessFileDescriptorTable` | Abstractions / Platform.Core | Descripteurs ouverts : 0, 1, 2 réservés, plus petit numéro libre à partir de 3, `EMFILE` à la limite souple. |
| `InMemoryProcessManager` | Platform.Core | Crée l'entrée au `Start` (parent, puis session, puis noyau), vérifie `NPROC` (`EAGAIN`), retire les entrées à la fin. Exemptés : `ResourceLimitExempt` (root élevé) et la portée système (`hackeros-svc`). |
| `ResourceLimitEnforcer` | Platform.Core | Tous les quarts de seconde simulée : dure du CPU → `SIGKILL`, souple → `SIGXCPU` (demande d'arrêt de l'ADR 0069, raison `CpuLimitExceeded`), mémoire → `SIGKILL` (`MemoryLimitExceeded`). |
| `ResourceLimitsConfiguration` / `LoginResourceLimitsProvider` | Platform.Core | Lecture façon `pam_limits` de `limits.conf` et `limits.d/*.conf`, au premier processus racine de la session. Lignes invalides dans `/var/log/auth.log`. |
| `AppFileSystemGateway` (`OpenDescriptorAsync`, lectures) et `AppProcessGateway.SetResourceLimitAsync` | Platform.Core | Prennent un descripteur par lecture ou flux d'écriture ; changent une limite (`process.manage` pour un autre processus). |
| `ProcFileSystemProvider` | Platform.Core | `/proc/<pid>/limits` et `/proc/<pid>/fd/`. |
| Opérations `fs.fdopen`, `fs.fdclose`, `proc.rlimit` | WorkerHost | Les mêmes appels depuis un worker ([`worker-processes.md`](worker-processes.md)). |
| `ulimit` (built-in) | Platform.Core, `ShellInterpreter.Ulimit.cs` | Lit et change les limites du processus du shell. |
| `prlimit` | Commands | Lit et change les limites d'un processus. |
| `HackProcess.GetResourceLimitsAsync` / `SetResourceLimitAsync`, `HackResourceLimits` | AppSdk | API publique (SDK 1.2, `PublicAPI.Unshipped.txt`). |
| `AddHackerOsResourceLimits(configuration)` | Ecosystem.Host | Lit `HackerOs:ResourceLimits:Enabled` pour l'hôte. |

## Usage

### Shell

```text
alice@hackeros:~$ ulimit -a
file size                   (blocks, -f) unlimited
max memory size             (kbytes, -m) unlimited
open files                          (-n) 1024
cpu time                   (seconds, -t) unlimited
max user processes                  (-u) 4096
alice@hackeros:~$ ulimit -S -n 512      # souple seulement
alice@hackeros:~$ ulimit -Hn 600000
hsh: ulimit: open files: cannot modify limit: Operation not permitted
alice@hackeros:~$ sh -c 'ulimit -S -t 1; stress --yield'; echo "exit=$?"
CPU time limit exceeded
exit=152
```

- Sans `-S` ni `-H`, `ulimit` affiche la souple et change les deux.
- `ulimit` seul affiche la taille de fichier : `unlimited`. `ulimit 100`
  répond « file size: cannot modify limit: Operation not permitted ».
- Les commandes lancées ensuite héritent des nouvelles limites ; un `sh -c`
  garde les siennes pour lui.

### `prlimit`

```text
alice@hackeros:~$ prlimit --pid 7 --nofile
RESOURCE DESCRIPTION              SOFT   HARD UNITS
NOFILE   max number of open files 1024 524288 files
alice@hackeros:~$ prlimit --pid 7 --nofile=100:200 --nproc=50
```

Options : `--pid`/`-p`, `--cpu`/`-t`, `--rss`/`-m`, `--nofile`/`-n`,
`--nproc`/`-u` (avec `=souple:dure`, `=souple:` ou `=:dure`), `--noheadings`.
Sans `--pid`, `prlimit` vise son propre processus.

### `/etc/security/limits.conf`

Format de `pam_limits` : `<domaine> <type> <élément> <valeur>`.

```text
# domaine  type  élément  valeur
@devs      soft  nofile   4096
alice      hard  cpu      60
*          -     nproc    500
```

- Domaine : utilisateur, `@groupe` ou `*`. Type : `soft`, `hard` ou `-`
  (les deux). Éléments appliqués : `cpu` (minutes, comme Linux), `rss` (Kio),
  `nofile`, `nproc`. Les autres éléments de Linux sont acceptés et ignorés.
- Les fichiers `limits.d/*.conf` sont lus après, par ordre de nom.
- Le fichier livré est entièrement commenté (`system:system`, `0644`) :
  il ne change rien aux défauts. Un changement vaut à la prochaine connexion.

### `/proc/<pid>/limits`

```text
Limit                     Soft Limit           Hard Limit           Units     
Max cpu time              unlimited            unlimited            seconds   
…
Max open files            1024                 524288               files     
# enforcement: strict (worker)
# cpu, rss: inactive (not measured)
```

La ligne `# enforcement:` vaut `strict (worker)`, `best-effort (main thread)`
ou `off (HackerOs:ResourceLimits:Enabled=false)`. `/proc/<pid>/fd/` (dossier
`0500`) liste les descripteurs ouverts sous forme de liens.

### SDK

```csharp
HackProcess self = await HackProcess.GetCurrentProcessAsync();
HackResourceLimits limits = await self.GetResourceLimitsAsync();
await self.SetResourceLimitAsync(ResourceLimitKind.NoFile, 256, limits.NoFile.Hard);
```

Erreurs : `ArgumentException` (`EINVAL`), `HackUnauthorizedAccessException`
(`EPERM`), `InvalidOperationException` (`ESRCH`).

### Hôte

```json
{ "HackerOs": { "ResourceLimits": { "Enabled": true } } }
```

`false` coupe toutes les limites : `ulimit` ne change plus rien
(« cannot modify limit »), aucune vérification n'a lieu.

## Décisions clés

- Tables à côté de `ProcessRecord`, qui reste figé (API 1.1 livrée).
- Un seul `SIGXCPU`, puis le délai de grâce de l'ADR 0069 ; la dure est
  vérifiée en premier, donc `ulimit -t 1` donne `SIGKILL` comme sous Linux.
- Un descripteur = une lecture ou un flux d'écriture ouvert ; les écritures
  d'un bloc, les listes de dossiers et les sockets ne comptent pas.
- `NPROC` vérifié par le gestionnaire au lancement ; root élevé et
  `hackeros-svc` exemptés.
- `limits.conf` lu une fois par session.

Détails : ADR 0139.

## Tests

- `Tests/HackerOs.Platform.Core.Tests/Processes/InMemoryProcessManagerTests.Limits.cs`
  et `ResourceLimitsConfigurationTests.cs` : tables, règles de `setrlimit`,
  limites coupées, exemptions, descripteurs, `pam_limits`, contrôleur.
- `Tests/HackerOs.Platform.WorkerHost.Tests/ProcessLimitsTests.cs` : de bout en
  bout, sur le fil principal et en worker (`ulimit`, `prlimit`,
  `/proc/self/limits`, `EMFILE`, `EAGAIN`, sorties 152 et 137).
- `Tests/HackerOs.AppSdk.Tests/HackResourceLimitsTests.cs` : lecture de
  `/proc/<pid>/limits`.
- `CommandParityTests` : `prlimit` en worker et sur le fil principal.
- `build/verify-hosts.sh` : étape des limites sur les trois hôtes et passage
  `ecosystem-limits-off`.

## Liste des tâches

- [x] Contrat dans Simulation Abstractions, tables des limites et des descripteurs.
- [x] Héritage au lancement, limites de session, `NPROC` et `EAGAIN`.
- [x] Lecture de `limits.conf` et `limits.d/`, journal `auth.log`, fichier livré commenté.
- [x] Contrôleur CPU et mémoire, `SIGXCPU` (152), `SIGKILL` (137).
- [x] Descripteurs des lectures et des flux d'écriture, `EMFILE`.
- [x] `/proc/<pid>/limits` et `/proc/<pid>/fd/`.
- [x] Built-in `ulimit`, commande `prlimit`, messages en anglais et en français.
- [x] Opérations du worker (`fs.fdopen`, `fs.fdclose`, `proc.rlimit`).
- [x] API du SDK (`PublicAPI.Unshipped.txt`).
- [x] Réglage `HackerOs:ResourceLimits:Enabled` sur les trois hôtes.
- [x] Tests, `verify-hosts.sh`, ADR 0139, documentation et index.
