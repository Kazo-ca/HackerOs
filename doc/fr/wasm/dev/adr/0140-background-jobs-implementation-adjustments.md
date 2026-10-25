> [English version](../../../../en/wasm/dev/adr/0140-background-jobs-implementation-adjustments.md)

# ADR 0140 : ajustements de la mise en œuvre de l'ADR 0128 (tâches en arrière-plan)

## Statut

Accepté le 2026-10-18.

- **Chantier :** `exec/5-background-jobs` (plan
  [`execution-followups-plan.md`](../plans/execution-followups-plan.md), décision 7,
  étape 1).
- **Complète :** l'ADR 0128, sans en changer la décision. Elle consigne ce que
  le code a dû préciser ou faire autrement, et pourquoi. Elle amende sur un
  point la règle de `IAppProcessGateway` (section 7).

## Contexte

L'ADR 0128 décrit `HackBackground.RunAsync` : une méthode statique de l'app,
des données sérialisables, un processus enfant placé dans le pool de workers, un
analyseur au build. Quatre contraintes ont pesé sur la mise en œuvre :

1. Une app peut tourner sans OS (ses tests unitaires) : `HackBackground` doit y
   marcher avec le même contrat.
2. Le protocole des workers (ADR 0070, 0124) ne lance que des points d'entrée
   d'apps ; une tâche n'en est pas un.
3. L'ordonnancement par `nice` appartient à `exec/4` (ADR 0127), qui tourne en
   parallèle.
4. Les traitements longs des apps de base ne sont pas tous du calcul : certains
   ont besoin de la fenêtre au milieu (une question à l'utilisateur).

## Décision

### 1. Passerelle `IAppExecutionContext.Background`

- Le SDK ajoute `IAppBackgroundGateway` (un seul membre, `RunAsync` d'une
  `HackBackgroundJobRequest` : cible nommée, entrée JSON, nom, `nice`) et le
  membre par défaut `IAppExecutionContext.Background`.
- **Par défaut** (contexte que l'OS n'a pas construit, test d'une app), la tâche
  s'exécute **en ligne** dans le processus appelant, avec la même sérialisation
  et les mêmes erreurs. Une app testée ainsi marche à l'identique sous l'OS.
- La fabrique de confiance (`AppExecutionContextFactory.BackgroundJobs`, une
  propriété `init` pour ne pas toucher au constructeur livré) branche
  `BackgroundJobService` de `HackerOs.Platform.Core`.

### 2. Processus d'une tâche

- Processus enfant de l'app (`ParentPid`), de genre `Terminal` (sans fenêtre),
  avec l'`AppId` de l'app, son utilisateur, sa session, son profil de
  ressources et ses capacités accordées. `argv[0]` est le nom de la tâche.
- Nom : `<commande de l'app>:<nom>`, où la commande est `Terminal.Name` du
  manifeste ou le dernier segment de l'ID (`code-editor:search`,
  `file-explorer:compress`). Le nom est l'option `Name`, sinon la méthode ou la
  classe ; 1 à 64 caractères, sans `:` ni caractère de contrôle.
- `/proc/<pid>/status` : `Name:` porte le nom de la tâche, tronqué à
  15 caractères comme `TASK_COMM_LEN` de Linux ; deux lignes s'ajoutent après
  `Session:`, `Job:` (le nom entier) et `Host: MainThread|Worker`. `ps` affiche
  le nom de la tâche dans sa colonne de commande.
- Codes de sortie : `0` succès, `1` exception de la tâche, `127` tâche refusée
  (rien de valide à exécuter, comme un `exec` qui échoue), `130` annulée,
  `137` tuée.

### 3. Concurrence et `nice`

- Au plus `MaxConcurrentJobsPerApp` tâches **par couple utilisateur et app**
  (nombre de cœurs virtuels, arrondi au-dessus) ; les suivantes attendent dans
  l'ordre d'arrivée.
- `nice` est borné à `[-20, 19]` ; une valeur négative est ramenée à `0` pour un
  utilisateur sans droits d'administrateur ni élévation root (`nice(2)`). Elle
  est enregistrée (`BackgroundJobInfo.Nice`) mais **n'ordonne pas encore** la
  file : l'ordonnancement par `nice` arrive avec `exec/4` (ADR 0127), qui le
  branchera aussi pour les tâches. La ligne `Nice` de `/proc/<pid>/status` est
  laissée à `exec/4` pour éviter deux formats.

### 4. Protocole des workers

- Démarrage d'une tâche : message `start` avec `k = 2`, la cible `jt`
  (`asm`, `ty`, `me` facultatif, `it`) et l'entrée `ji`. Le worker charge
  l'assembly (`KnownAssemblies`, ADR 0138), lie la méthode ou la classe et
  refuse (code 127) ce qu'il ne trouve pas.
- Fin : le message `exit` porte `jr` (résultat JSON), `jf` (`ty`, `m`, `st` :
  type, message et pile de l'exception d'origine) et `jx` (tâche refusée).
- Progression : note `bg.progress` `{f, m}` du worker vers l'OS.
- Tâche lancée depuis un worker (ADR 0128 §6) : appel `bg.run` (`jt`, `ji`, `n`,
  `ni`, numéro `j`) ; l'OS place la tâche et renvoie sa progression par des
  messages `bgp` (`p`, `j`, `f`, `m`), puis le résultat `{r}`.
- Erreurs distantes : `bgfault` (`ty`, `st`) pour `HackBackgroundJobException` ;
  l'erreur `killed` porte la raison `rs`.

### 5. Erreurs vues par l'appelant

- `HackProcessKilledException` reçoit la propriété `Reason` et un constructeur
  `(processId, reason)` : `Killed` par défaut, `OutOfMemory` ou
  `CpuLimitExceeded` quand l'OS le sait. Elle dérive de
  `OperationCanceledException` : un `catch` doit la placer avant celui de
  l'annulation.
- Tâche refusée (méthode introuvable, type non déclaré, autre assembly, entrée
  au-delà de 16 Mio) : `ArgumentException`, avant tout processus quand le SDK
  peut le voir.
- Progression : au plus une remontée par 100 ms, la dernière l'emporte et la
  dernière avant la fin est toujours livrée ; `ToString()` donne `42% message`.

### 6. Fermeture de l'app

- Quand l'app se termine, le gestionnaire de processus termine ses enfants avec
  la raison `DependencyStop` : le jeton de la tâche est annulé tout de suite,
  **sans délai de grâce** (pas d'attente de 5 s comme un `SIGTERM` suivi d'un
  `SIGKILL`). Avec `kill -9` de l'app, les tâches sont tuées aussi.
- La comptabilité de l'OS (fin du processus, place libérée) ne dépend pas du
  planificateur de l'appelant : il abandonne ses continuations une fois tué
  (ADR 0069), d'où des `ConfigureAwait(false)` dans `BackgroundJobService`.

### 7. Une app gère ses propres enfants (amende `IAppProcessGateway`)

`StopAsync`, `Kill`, `KillAsync` et `ListProcesses` exigeaient
`process.manage` / `process.list` pour tout autre processus que le sien. Un
**descendant** du processus de l'app (une de ses tâches) compte maintenant comme
le sien : une fenêtre peut afficher et tuer ses tâches sans droit
supplémentaire, comme un parent Unix signale les processus qu'il a lancés.
`ListProcesses` reste synchrone et donc indisponible dans un worker ; une tâche
peut communiquer son PID par sa progression (exemple de la fenêtre d'exemple).

### 8. Analyseur

- Projet `Tools/HackerOs.Tools.BackgroundAnalyzer` (netstandard2.0), livré dans
  le paquet `HackerOs.AppSdk` (`analyzers/dotnet/cs`) et branché sur les apps du
  dépôt par `build/HackerOs.Apps.targets`.
- Codes : `HOSBG0001` (pas un groupe de méthode statique : lambda, méthode
  anonyme, méthode d'instance, fonction locale non `static`, délégué passé par
  une variable), `HOSBG0002` (type d'entrée ou de résultat hors des contextes
  `HackWireContext`), `HOSBG0003` (méthode ou classe d'une autre assembly),
  `HOSBG0004` (méthode générique ou d'un type générique).
- Un type est accepté s'il est une racine `[JsonSerializable]` d'un contexte
  déclaré **ou** s'il est atteint depuis l'une d'elles (élément de tableau,
  argument générique, propriété publique), comme
  `JsonSerializerContext.GetTypeInfo` le trouve à l'exécution.
- Une fonction locale `static` est acceptée : le compilateur en fait une
  méthode statique que le worker retrouve par son nom.

### 9. Apps de base

| Traitement | Décision | Raison |
|---|---|---|
| Recherche dans le dossier du CodeEditor (Ctrl+Maj+F) | Tâche `code-editor:search` | La liste des fichiers est faite par la fenêtre ; lecture et recherche (expressions régulières) dans la tâche. Les onglets non enregistrés passent dans l'entrée. |
| Compresser, Extraire et « UnZip Here… » de l'explorateur | Tâches `file-explorer:compress` et `file-explorer:extract` | Compression et décompression sont du calcul ; la tâche lit et écrit par ses propres passerelles. Les erreurs reviennent comme clé de texte, traduites par la fenêtre. |
| Copier, déplacer, coller de l'explorateur | Restent dans la fenêtre | Chaque élément est un appel à l'OS attendu, avec un point d'arrêt entre deux (pas de long calcul) ; un conflit de nom pose une question dans la fenêtre entre deux éléments, ce qu'une tâche dans un autre processus ne peut pas faire. |
| Téléchargement d'une sélection en `.zip` | Reste dans la fenêtre | Les octets doivent finir dans la fenêtre pour le téléchargement du navigateur, et une grande sélection dépasserait les 16 Mio d'un résultat. |
| Filtres de Hack Paint | Sans objet | Hack Paint n'a pas de filtre. Rotation et recadrage restent : une passe de copies, sur un tampon qui peut faire 64 Mio (4096 × 4096 en RGBA), quatre fois la limite d'une entrée. Un futur filtre passera par une tâche et un fichier temporaire. |

L'exemple demandé est une section « Background Task » de
`Apps/Samples/HackerOs.Samples.WindowApp` (compte des nombres premiers avec
progression, annulation et bouton Kill), plutôt qu'une nouvelle app : la
fenêtre d'exemple est déjà celle qui montre le SDK d'une app à fenêtre.

## Conséquences

- Documentation : [`background-jobs.md`](../../framework/background-jobs.md) (nouveau),
  [`worker-processes.md`](../worker-processes.md),
  [`sdk-developer-guide.md`](../../framework/sdk-developer-guide.md),
  [`sdk-reference.md`](../../framework/sdk-reference.md), [`code-editor.md`](../code-editor.md),
  [`file-explorer.md`](../file-explorer.md), [`hack-paint.md`](../hack-paint.md).
- Ajouts publics dans `PublicAPI.Unshipped.txt` du SDK, de
  `HackerOs.Platform.Core` et de `HackerOs.Platform.WorkerHost` ; publiés avec
  le SDK 1.2 dans `exec/10` (ADR 0132).
- `exec/4` branche l'ordonnancement par `nice` des tâches et la ligne `Nice`.
- Deux questions à Benoit (délai de grâce à la fermeture de l'app, PID dans la
  progression) ; les défauts de cette ADR s'appliquent en attendant.
