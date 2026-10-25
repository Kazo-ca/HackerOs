> [English version](../../../../en/wasm/dev/adr/0069-graceful-stop-and-checkpoints.md)

# ADR 0069 : arrêt demandé, points d'arrêt volontaires et escalade

## Statut

Accepté le 2026-10-16.

- **Phase du plan :** phase 5 — exécution des apps
  ([`execution-isolation-plan.md`](../plans/execution-isolation-plan.md)).
- **Complète :** ADR 0012 (cycle de vie des processus), ADR 0056 §4
  (`HackProcess`). Ne contredit pas l'ADR 0058 §2 : « End task » garde sa
  confirmation, seul son mode d'arrêt change (section 4).
- **Utilisé par :** ADR 0070 (processus dans des Web Workers).

## Contexte

L'OS sait déjà arrêter poliment un processus : `StopAsync` le passe à
`Stopping`, annule `IAppExecutionContext.CancellationToken`, attend un délai de
grâce, puis le force (`InMemoryProcessManager.StopAsync`). C'est `SIGTERM` puis
`SIGKILL`. `KillAsync` est `kill -9`.

Il manque trois choses :

1. Le code ne sait pas **pourquoi** ni **dans combien de temps** il sera forcé :
   il voit seulement un jeton annulé.
2. Un traitement long n'a aucun moyen simple de **rendre la main**. Dans un Web
   Worker, c'est grave : un worker n'a qu'un thread, et le message « arrête le
   pid 42 » n'est jamais traité tant qu'un processus du worker boucle sans
   rendre la main. Le jeton ne peut donc pas s'annuler.
3. « End task » du gestionnaire de tâches fait aujourd'hui un `kill -9`
   (`EndTaskRunner` appelle `HackProcess.KillAsync`) : l'app n'a aucune chance
   de se terminer proprement.

Benoit a proposé qu'un jeton prévienne le processus avant qu'il soit tué, pour
qu'il arrête ses tâches et évite de faire tomber son worker avec ses voisins.

## Décision

### 1. Arrêt demandé : raison et échéance

`IAppExecutionContext` reçoit :

```csharp
/// Levé une fois, quand l'OS demande l'arrêt (SIGTERM), avant l'annulation du jeton.
event EventHandler<HackStopRequestedEventArgs>? StopRequested;
```

`HackStopRequestedEventArgs` porte `Reason` (`ProcessExitReason` :
`CloseRequested`, `Terminated`, `Logout`, `Shutdown`) et `Deadline`
(l'instant où l'OS forcera, sur l'horloge simulée). Le SDK l'expose aussi par
`HackProcess.Current.StopRequested`, sur le modèle de
`IHostApplicationLifetime.ApplicationStopping`.

Ordre : passage à `Stopping`, levée de `StopRequested`, annulation de
`CancellationToken`, attente jusqu'à `Deadline`. La nouvelle valeur
`ProcessExitReason.Terminated` désigne un `SIGTERM` envoyé par un autre
processus (`kill`, « End task »).

### 2. Point d'arrêt volontaire

```csharp
for (...)
{
    await HackProcess.CheckpointAsync(cancellationToken);
}
```

- Si l'arrêt est demandé, lève `OperationCanceledException`.
- Si la tranche courante dépasse 8 ms (le budget d'une image à 60 fps), rend
  la main par `await Task.Yield()`, ce qui laisse passer les événements de
  l'interface et les messages de l'OS ; sinon, revient tout de suite, sans
  allocation.
- Rien n'oblige à l'appeler : ce n'est pas un quantum imposé, c'est un geste de
  bon citoyen. Les commandes intégrées longues (`find`, `grep -r`, `zip`, `du`)
  l'appellent dans leurs boucles.
- Il connaît la durée de la tranche par l'ADR 0067. Si la mesure du CPU est
  coupée (ADR 0066), il garde son propre horodatage de tranche.

### 3. Escalade

Quand l'OS arrête un processus :

1. **`SIGTERM`** : `StopRequested`, jeton annulé, délai de grâce (5 s pour
   `kill` et « End task », la valeur de `StopAsync` sinon).
2. **Le processus se termine** : fin, sans effet sur les autres.
3. **Délai passé, mais le thread qui l'exécute répond encore** : le processus
   ignore son jeton sans bloquer. L'OS le passe à `Exited (Killed)`, libère ses
   ressources, ferme ses fenêtres et **jette ses continuations** : l'ordonnanceur
   du processus (un `TaskScheduler` installé dans son contexte) refuse toute
   nouvelle tâche, et les passerelles de l'OS lèvent
   `HackProcessKilledException` à tout appel de ce pid. C'est un `SIGKILL`
   local, sans dégât collatéral.
4. **Le thread ne répond plus** : sur le thread principal, rien n'est possible
   (l'OS est gelé avec lui, voir ADR 0067 §4). Dans un Web Worker, l'OS termine
   le worker (ADR 0070).

### 4. Vocabulaire des commandes

| Action | Signal | Comportement |
|---|---|---|
| `kill <pid>`, `kill -15`, `kill -TERM` | `SIGTERM` | sections 1 et 3 |
| `kill -9`, `kill -KILL` | `SIGKILL` | immédiat, ne peut pas être intercepté, pas de `StopRequested` |
| « End task » du gestionnaire de tâches | `SIGTERM` | **change** : `EndTaskRunner` passe de `KillAsync` à `TerminateAsync` |
| « Force kill » du gestionnaire de tâches (nouveau, menu contextuel) | `SIGKILL` | `KillAsync` |
| fermeture de fenêtre | `SIGTERM`, raison `CloseRequested` | `CloseMainWindowAsync`, inchangé |

`HackProcess.TerminateAsync(TimeSpan? timeout)` est ajouté : c'est le
`SIGTERM` d'un autre processus, sous la capacité `process.manage` (ADR 0056).

## Conséquences

- Une app qui écoute `StopRequested` ou son jeton se termine proprement dans
  tous les cas.
- Le shell (`kill` sans option) garde son comportement ; « End task » devient
  poli.
- Le chantier ajoute `TerminateAsync`, `CheckpointAsync`, `StopRequested`,
  `ProcessExitReason.Terminated` et `HackProcessKilledException` au SDK, à
  documenter dans `app-sdk.md` et `sdk-reference.md`.

## Références

- [`execution-isolation-plan.md`](../plans/execution-isolation-plan.md)
- `Platform/HackerOs.Platform.Core/Processes/InMemoryProcessManager.cs`,
  `Apps/System/HackerOs.Apps.SystemMonitor/Processes/EndTaskRunner.cs`,
  `Apps/Commands/HackerOs.Commands/Kill/KillCommand.cs`.
- `signal(7)` : `SIGTERM`, `SIGKILL`.
- ADR 0012, 0056, 0058, 0066, 0067, 0070.
