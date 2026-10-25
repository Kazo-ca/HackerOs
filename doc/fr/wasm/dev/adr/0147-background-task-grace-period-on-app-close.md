> [English version](../../../../en/wasm/dev/adr/0147-background-task-grace-period-on-app-close.md)

# ADR 0147 : délai de 5 s pour les tâches en arrière-plan quand leur app se ferme

## Statut

Accepté le 2026-10-19, sur la réponse de Benoit.

- **Fil :** « Exec 5 : tâches de fond ». Ce sont les deux cartes laissées
  ouvertes par l'ADR 0140.
- **Remplace :** l'ADR 0140 §6 (« sans délai de grâce »). Le reste de l'ADR 0140
  ne change pas.

## Contexte

L'ADR 0140 §6 arrêtait les tâches d'une app au moment même où l'app se
terminait. Le processus de la tâche sortait aussitôt avec la raison
`DependencyStop`, et son jeton était annulé.

Une tâche qui écrit un fichier, une archive par exemple, n'avait donc aucun
moment garanti pour finir proprement ou effacer ce qu'elle laissait à moitié
écrit.

Deux questions ont été posées à Benoit :

1. **Délai de grâce à la fermeture de l'app.**
   - Choix A : arrêt immédiat, comme jusqu'ici.
   - Choix B : 5 s, comme un `SIGTERM` suivi d'un `SIGKILL`.
   - **Réponse : B.**
2. **PID de la tâche dans `HackBackgroundProgress`.**
   - Choix A : non, la tâche le met dans son message, comme dans la fenêtre
     d'exemple.
   - Choix B : un champ ajouté à l'API et au protocole.
   - **Réponse : A.**

## Décision

### 1. Signal de mort du parent

`ProcessStartRequest` gagne la propriété `init` `ParentExitGracePeriod`
(`TimeSpan?`, dans `HackerOs.Simulation.Abstractions`).

Le modèle est `prctl(PR_SET_PDEATHSIG, SIGTERM)` sous Linux, suivi d'un
`SIGKILL`. Le comportement dépend de la façon dont le parent se termine.

**Le parent se termine sans `SIGKILL`.** C'est le cas d'une fenêtre fermée,
d'une déconnexion ou d'une app désactivée (`Kill` avec une raison qui n'est pas
un signal). Le processus enfant ne disparaît pas avec lui. Il reçoit
`StopAsync(grâce, DependencyStop)`, ce qui veut dire :

- il passe à l'état `Stopping` ;
- `StopRequested` est levé ;
- son jeton est annulé ;
- à la fin du délai, il est tué avec la raison `Timeout`, qui compte comme un
  `SIGKILL` (ADR 0069).

**Le parent est tué par un `SIGKILL`.** C'est le cas de `kill -9`, d'une limite
ou de l'OOM killer. L'enfant est alors tué tout de suite, comme avant.

**Ce que l'enfant a lui-même lancé.** Il en reste maître pendant le délai. Ce
qui tourne encore quand il se termine est tué.

**Une valeur `null` ou nulle** garde l'ancien comportement : l'enfant se termine
avec son parent.

Seules les tâches de `HackBackground` utilisent ce délai. Les processus lancés
par le shell et les autres enfants ne changent pas.

### 2. Les tâches

`BackgroundJobService.ParentExitGracePeriod` vaut **5 s** et sert au démarrage
de chaque tâche. Le délai est compté sur l'horloge de la simulation, comme celui
de l'ADR 0069.

Une tâche qui respecte son jeton (`HackBackgroundContext.CancellationToken`)
s'arrête pendant le délai et sort avec le code `130`. Une tâche qui l'ignore est
tuée au bout de 5 s ; sa raison de sortie est alors `Timeout`.

Le fonctionnement est le même sur le fil principal et dans un worker : la
demande d'arrêt du processus est transmise au worker (ADR 0069, ADR 0070).

### 3. Le PID dans la progression

Rien ne change. `HackBackgroundProgress` ne porte pas le PID. Une fenêtre qui
veut un bouton « Kill » obtient le PID de la tâche par le message de
progression, ou par `ListProcesses` sur le fil principal (ADR 0140 §7).

## Conséquences

- `ProcessStartRequest.ParentExitGracePeriod` et
  `BackgroundJobService.ParentExitGracePeriod` s'ajoutent à
  `PublicAPI.Unshipped.txt`. Ils seront publiés avec la prochaine mineure, après
  la 1.2.0. Le SDK de l'app (`HackerOs.AppSdk`) ne change pas.
- Pendant le délai, `ps` et le gestionnaire de tâches montrent la tâche à l'état
  `Stopping`. Son parent n'existe plus.
- Documentation : [`background-jobs.md`](../../framework/background-jobs.md).
- Tests :
  - `InMemoryProcessManagerTests` : demande d'arrêt puis kill, et `SIGKILL` du
    parent ;
  - `BackgroundJobTests`, dans les deux modes : une tâche qui respecte la
    demande sort avec `130`, une tâche qui l'ignore est tuée à 5 s.
