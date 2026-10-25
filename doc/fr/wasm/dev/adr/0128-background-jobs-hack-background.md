> [English version](../../../../en/wasm/dev/adr/0128-background-jobs-hack-background.md)

# ADR 0128 : tâches lourdes en arrière-plan (`HackBackground.RunAsync`)

## Statut

Accepté le 2026-10-18.

- **Plan :** [`execution-followups-plan.md`](../plans/execution-followups-plan.md),
  décision 7, étape 1 (réponse Q5 de Benoit : « l'équivalent de `Invoke` dans
  WinForms, mais à l'inverse ») ; mis en œuvre par `exec/5-background-jobs`,
  après `exec/1-remote-gateways`.
- **Complète :** l'ADR 0070 §1 (les apps à fenêtre restent sur le fil
  principal, mais leurs calculs peuvent en sortir), l'ADR 0124 (chargement des
  assemblies et charges sérialisables).

## Contexte

Une app à fenêtre tourne sur le fil principal, parce qu'un worker n'a pas de
DOM. Une boucle lourde (filtre d'image, recherche dans un projet, compression)
y gèle donc tout le bureau. WinForms règle le cas inverse avec
`Control.Invoke` : un thread de travail demande au thread de l'interface
d'exécuter un délégué. Benoit veut le contraire : l'interface demande à un
« autre thread » d'exécuter la charge lourde.

Un worker est un autre runtime .NET avec sa propre mémoire : une lambda qui
capture des objets de la fenêtre ne peut pas y être envoyée. La tâche doit donc
être nommable (une méthode statique ou une classe) et ses données
sérialisables.

## Décision

### 1. API (ajout du SDK 1.2, ADR 0132)

```csharp
namespace HackerOs.AppSdk.Threading;

public static class HackBackground
{
    // Méthode statique : Compress(input, context) dans l'assembly de l'app.
    public static Task<TResult> RunAsync<TInput, TResult>(
        Func<TInput, HackBackgroundContext, Task<TResult>> job,
        TInput input,
        IProgress<HackBackgroundProgress>? progress = null,
        HackBackgroundOptions? options = null,
        CancellationToken cancellationToken = default);

    // Classe de tâche : une instance est créée dans le worker.
    public static Task<TResult> RunAsync<TJob, TInput, TResult>(
        TInput input, /* mêmes paramètres */)
        where TJob : HackBackgroundJob<TInput, TResult>, new();
}
```

- Variantes sans résultat et synchrones (`Func<TInput, HackBackgroundContext,
  TResult>`), même forme.
- `HackBackgroundContext` : `CancellationToken`, `Report(fraction, message)`,
  `CheckpointAsync()`, `ProcessId`. Le reste du SDK (`HackFile`, `HackProcess`,
  `HackEnvironment`…) marche dans la tâche par les passerelles distantes.
- `HackBackgroundOptions` : `Name` (affiché dans `ps`), `Nice` (ADR 0127).
- Usage :

  ```csharp
  byte[] blurred = await HackBackground.RunAsync(ImageJobs.Blur, new BlurInput(pixels, radius),
      progress, cancellationToken: ct);
  ```

### 2. Règles vérifiées au build

Un analyseur Roslyn livré avec `HackerOs.AppSdk` (codes `HOSBG0001` et
suivants, choisis par le chantier) refuse :

- un délégué qui n'est pas un **groupe de méthode statique** (lambda, méthode
  locale qui capture, méthode d'instance) ;
- un `TInput` ou un `TResult` absent du `JsonSerializerContext` déclaré par
  `[assembly: HackWireContext(…)]` (ADR 0124 §2) ;
- une méthode qui n'est pas dans l'assembly de l'app appelante.

À l'exécution, l'OS refait la vérification (un appel par réflexion ne passe
pas l'analyseur) et lève `ArgumentException` en cas d'écart.

### 3. Modèle de processus

- Chaque appel crée un **processus enfant** de l'app appelante : même
  utilisateur, même session, mêmes capacités accordées, environnement copié,
  nom `<commande de l'app>:<Name ou méthode>`. Il apparaît dans `ps`, `top` et
  le gestionnaire de tâches sous l'app.
- Il tourne dans le **pool** (ADR 0071) ; la file suit `nice` (ADR 0127). Au
  plus `MaxConcurrentJobsPerApp` (nombre de cœurs virtuels par défaut) tâches
  d'une même app en parallèle ; les suivantes attendent.
- Il suit l'arrêt de l'ADR 0069 : annulation du jeton par `cancellationToken`,
  `SIGTERM` (raison `DependencyStop`) quand l'app se ferme, `SIGKILL` par
  `kill` ou le gestionnaire de tâches **sans fermer la fenêtre**.
- Il est soumis aux limites (ADR 0125) et à l'OOM killer (ADR 0126) comme
  tout processus ; dans un worker, ces limites sont strictes.

### 4. Résultat et erreurs

- La tâche rend le résultat désérialisé ; la progression est livrée dans le
  contexte de synchronisation de l'appelant, au plus 10 fois par seconde.
- Annulation : `OperationCanceledException`. Tâche tuée :
  `HackProcessKilledException` (raison `Killed`, `OutOfMemory`,
  `CpuLimitExceeded`…). Exception de la tâche : `HackBackgroundJobException`
  avec le type, le message et la pile d'origine (l'objet ne traverse pas).
- Entrée ou résultat de plus de 16 Mio (la taille maximale d'un fichier,
  ADR 0094) : refusé. Au-delà de 1 Mio, le guide recommande de passer un
  chemin et de lire le fichier dans la tâche.

### 5. Sans worker : même contrat sur le fil principal

Quand il n'y a pas de worker (tests sans boucle locale, navigateur sans
Web Worker, hôte qui les coupe), la tâche tourne sur le fil principal, comme
processus enfant, **avec la même sérialisation** de l'entrée et du résultat :
une app qui marche dans les tests marche en worker. `/proc/<pid>/status`
indique `Host: MainThread`. Appeler `context.CheckpointAsync()` y garde le
bureau vivant.

### 6. Depuis un processus déjà en worker

Une commande ou un service en worker peut aussi appeler `RunAsync` : la
demande passe par l'opération `bg.run` à l'OS, qui place la tâche comme
ci-dessus. C'est le seul lancement d'enfant permis depuis un worker
(`StartChild` reste refusé, ADR 0124 §5).

## Conséquences

- Nouvelle bibliothèque d'analyseur dans le paquet `HackerOs.AppSdk`,
  testée comme le générateur de routes d'ASP.Hack (ADR 0082).
- Exemple compilé dans `Apps/Samples` (une fenêtre qui lance un calcul long
  avec progression, annulation et `kill`).
- Migration des apps de base : recherche du CodeEditor, compression et copies
  de l'explorateur, filtres de Hack Paint ; chacune passe par
  `HackBackground.RunAsync` ou garde sa place avec la raison écrite.
- Docs : nouveau `background-jobs.md`, [`sdk-developer-guide.md`](../../framework/sdk-developer-guide.md),
  [`sdk-reference.md`](../../framework/sdk-reference.md),
  [`worker-processes.md`](../worker-processes.md).

## Références

- ADR 0069, 0070, 0071, 0082, 0094, 0124, 0125, 0126, 0127, 0132.
- `Control.Invoke` (WinForms), `Worker` (HTML), `Task.Run`.
