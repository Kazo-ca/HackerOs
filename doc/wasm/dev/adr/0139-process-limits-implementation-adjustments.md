# ADR 0139 : ajustements de la mise en œuvre des limites de processus

## Statut

Accepté le 2026-10-18, chantier `exec/2-process-limits`.

- **Complète :** ADR 0125 (limites par processus). Ne remplace aucune de ses
  décisions. Il précise les points que le code a dû trancher et corrige le
  parcours de vérification de l'ADR 0125, section « Conséquences ».
- **Doc :** [`process-limits.md`](../process-limits.md).

## Contexte

L'ADR 0125 fixe les quatre limites (`cpu`, `rss`, `nofile`, `nproc`), leurs
sources (`limits.conf`, héritage), leurs surfaces (`ulimit`, `prlimit`,
`/proc/<pid>/limits`, SDK) et leur application (stricte en worker, au mieux sur
le fil principal). En l'écrivant, plusieurs détails sont apparus : où vivent
les limites, quand `SIGXCPU` part, ce qu'est un descripteur dans un OS sans
appel `open(2)`, et les noms exacts du SDK.

## Décision

### 1. Tables à côté du processus, pas dans `ProcessRecord`

Les limites et les descripteurs vivent dans deux tables tenues par le
gestionnaire de processus, comme la table d'environnement de l'ADR 0050 :
`IProcessResourceLimitTable` et `IProcessFileDescriptorTable` (Simulation
Abstractions). L'entrée naît au `Start` (limites du parent, sinon celles de la
session, sinon celles du noyau) et disparaît à la fin du processus.
`ProcessRecord` ne change pas : c'est un instantané figé et ses champs font
partie de l'API 1.1 livrée.

### 2. `SIGXCPU` une fois, puis le délai de grâce

Un contrôleur (`ResourceLimitEnforcer`) compare la mesure aux limites tous les
quarts de seconde simulée. L'ordre est celui de Linux :

1. limite **dure** du CPU atteinte : `SIGKILL`, raison `CpuLimitExceeded`,
   sortie 137, « Killed » ;
2. limite **souple** atteinte : `SIGXCPU` une seule fois, sous la forme de la
   demande d'arrêt de l'ADR 0069 (raison `CpuLimitExceeded`, délai de 5 s). Si
   le processus finit sur cette demande, sa sortie vaut 152 et le shell écrit
   « CPU time limit exceeded ». Sinon le délai de grâce mène à `SIGKILL` ;
3. mémoire au-delà de la limite : `SIGKILL` tout de suite, raison
   `MemoryLimitExceeded`.

L'ADR 0125 parlait d'un `SIGXCPU` « puis chaque seconde ». Une demande d'arrêt
déjà en cours n'est pas renvoyée (ADR 0069), donc il n'y en a qu'une.

Conséquence pour la vérification : avec `ulimit -t 1`, la souple et la dure
valent 1, et la dure passe en premier. Le processus reçoit donc `SIGKILL` (137),
comme sous Linux. Le parcours de `build/verify-hosts.sh` utilise
`sh -c 'ulimit -S -t 1; stress --yield'` : seule la souple est posée, et
`stress --yield` s'arrête sur la demande, ce qui donne la sortie 152. Un
`stress` sans `--yield` ne rend jamais la main. Il finit donc par `SIGKILL`
après le délai de grâce : le worker est terminé (étape 4 de l'ADR 0069).

### 3. Ce qui compte comme descripteur

HackerOS n'a pas d'appel `open(2)`. Un descripteur est pris :

- par chaque lecture (`IAppFileSystemGateway.ReadAsync`), jusqu'à ce que son
  flux soit libéré ;
- par chaque flux d'écriture (`HackFileStream` en écriture), au moyen de
  `IAppFileSystemGateway.OpenDescriptorAsync`, jusqu'à sa fermeture.

Les numéros 0, 1 et 2 sont les flux standard (`/dev/tty` pour un terminal,
`/dev/null` sinon). Un nouveau descripteur prend le plus petit numéro libre à
partir de 3. Il est refusé (`EMFILE`) quand son numéro atteint la limite
souple : avec `ulimit -n 3`, aucun fichier ne s'ouvre, comme sous Linux.
`/proc/<pid>/fd/` liste les descripteurs ouverts.

Ne sont pas comptés : les écritures d'un bloc (`WriteAllText` et le reste de
`WriteAsync`, qui n'ont pas de flux ouvert), les listes de dossiers et les
sockets. Dans un worker, un fichier de 16 Mio au plus est lu en une seule
réponse (ADR 0070). Son descripteur est donc fermé dès cette réponse, même si
l'app garde le flux plus longtemps.

### 4. `NPROC` vérifié au lancement, par le gestionnaire

`InMemoryProcessManager.Start` compte les processus vivants de l'utilisateur.
Il lève `ProcessLimitExceededException` (`EAGAIN`) quand ce compte atteint la
limite souple de l'appelant. En sont exemptés :

- un lancement marqué `ResourceLimitExempt` (root élevé) ;
- un lancement de portée système (`hackeros-svc`).

L'orchestrateur traduit cette exception en un échec de lancement de code
`lifecycle.process.eagain`. Le shell écrit alors
« hsh: fork: Resource temporarily unavailable » et sort avec 1. Un script
(`sh script`, `sh -c`) donne le même message.

### 5. `pam_limits` au premier processus racine de la session

Les fichiers sont lus au premier lancement d'un processus sans parent de la
session, puis gardés en mémoire jusqu'à la session suivante. Un changement de
`limits.conf` vaut donc pour la prochaine connexion. Chaque ligne invalide est
écrite une fois dans `/var/log/auth.log` (acteur `pam_limits`). Les éléments de
Linux que HackerOS n'applique pas (`core`, `stack`, `as`…) sont acceptés sans
message.

### 6. Noms du SDK (ajouts du SDK 1.2, `PublicAPI.Unshipped.txt`)

- `HackProcess.GetResourceLimitsAsync()` (méthode d'instance asynchrone :
  elle lit `/proc/<pid>/limits`) et
  `HackProcess.SetResourceLimitAsync(ResourceLimitKind, long soft, long hard)`.
  Les erreurs sont `ArgumentException` (`EINVAL`),
  `HackUnauthorizedAccessException` (`EPERM`) et `InvalidOperationException`
  (`ESRCH`).
- `HackResourceLimits` (avec `Parse`) et `HackResourceLimitEnforcement`
  (`Off`, `BestEffort`, `Strict`).
- Simulation Abstractions :
  - `ResourceLimitKind`, `ResourceLimit`, `ProcessResourceLimits`,
    `ResourceLimitChangeResult`, `ProcessLimitExceededException` ;
  - les deux tables du §1 ;
  - `ProcessStartRequest.ResourceLimits` et `ResourceLimitExempt` ;
  - `IAppFileSystemGateway.OpenDescriptorAsync` et
    `IAppProcessGateway.SetResourceLimitAsync`, avec une implémentation par
    défaut pour que les passerelles existantes compilent ;
  - `NoDescriptor` ;
  - `ProcessExitReason.CpuLimitExceeded` et `MemoryLimitExceeded` ;
  - `FileSystemErrorCode.TooManyOpenFiles` (1802).

### 7. Surfaces

- `ulimit` accepte aussi `-f`. `ulimit unlimited` (ou `-f unlimited`) réussit
  sans rien changer. Un nombre répond « cannot modify limit: Operation not
  permitted », comme le dit l'ADR 0125.
- `prlimit` sans `--pid` vise son propre processus, comme dans util-linux. Il
  demande `process.list` et `filesystem.system.read` pour lire `/proc`, comme
  `ps`, et `process.manage` pour changer un autre processus.
- `/proc/<pid>/limits` gagne la ligne
  `# enforcement: off (HackerOs:ResourceLimits:Enabled=false)` quand l'hôte a
  coupé les limites.
- Chaque hôte qui compose l'OS (l'Ecosystem et `test/test` ; `HackerOs.Server`
  sert l'Ecosystem depuis l'ADR 0130) lit le réglage par
  `AddHackerOsResourceLimits(configuration)`, avant `AddHackerOsEcosystem`.

## Conséquences

- `build/verify-hosts.sh` gagne une étape et un passage. L'étape lit
  `/proc/self/limits` et lance le parcours du §2 sur chaque hôte : 152 là où le
  CPU est mesuré, sortie normale là où il ne l'est pas. Le passage
  `ecosystem-limits-off` publie avec les limites coupées.
- `cat`, `grep` et `wc` disent « Too many open files » au lieu de « No such
  file or directory » quand l'ouverture échoue sur `EMFILE`, par le même
  traducteur (`CommandIO.ReadErrno`) que les autres commandes de lecture.

## Références

- ADR 0050, 0066, 0067, 0068, 0069, 0070, 0107, 0125, 0132.
