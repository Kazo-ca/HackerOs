> [English version](../../../en/wasm/framework/background-jobs.md)

# Tâches en arrière-plan (`HackBackground`)

Chantier `exec/5-background-jobs`, [ADR 0128](../dev/adr/0128-background-jobs-hack-background.md)
[ADR 0140](../dev/adr/0140-background-jobs-implementation-adjustments.md) et
[ADR 0147](../dev/adr/0147-background-task-grace-period-on-app-close.md). Ajout du
SDK 1.2, publié à la fin de la série `exec` (ADR 0132).

## Objet

Une app à fenêtre tourne sur le fil principal du navigateur, celui qui dessine
aussi le bureau. Un filtre d'image, une recherche dans un projet ou une
compression y gèlent tout. `HackBackground.RunAsync` fait l'inverse de
`Control.Invoke` de WinForms : la fenêtre confie la charge lourde à un autre
« thread », ici un **processus enfant** de l'app, placé dans un Web Worker du
pool quand l'hôte en a, et attend son résultat sans bloquer.

Comme sous Linux, la tâche est un vrai processus :

- elle apparaît dans `ps`, `top` et le gestionnaire de tâches sous l'app, avec
  le nom `<commande>:<tâche>` (`code-editor:search`, `file-explorer:compress`) ;
- `kill -9 <pid>` ou « Terminer » la tue **sans fermer la fenêtre** ;
- quand l'app se ferme, elle reçoit une demande d'arrêt (son jeton est annulé)
  et a **5 s** pour finir, puis elle est tuée, comme `SIGTERM` puis `SIGKILL`
  (ADR 0147) ; un `kill -9` de l'app la tue tout de suite ;
- elle est soumise aux limites des processus (ADR 0125) comme les autres.

## Usage

### Une méthode statique

```csharp
using System.Text.Json.Serialization;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Threading;

[assembly: HackWireContext(typeof(PaintJsonContext))]

public sealed record BlurInput(byte[] Pixels, int Width, int Height, int Radius);

[JsonSerializable(typeof(BlurInput))]
[JsonSerializable(typeof(byte[]))]
internal sealed partial class PaintJsonContext : JsonSerializerContext;

public static class ImageJobs
{
    public static async Task<byte[]> Blur(BlurInput input, HackBackgroundContext context)
    {
        for (int row = 0; row < input.Height; row++)
        {
            // ... une ligne ...
            context.Report((row + 1d) / input.Height, "Blur");
            await context.CheckpointAsync();
        }

        return input.Pixels;
    }
}

// Dans la fenêtre :
byte[] blurred = await HackBackground.RunAsync<BlurInput, byte[]>(
    ImageJobs.Blur, new BlurInput(pixels, width, height, 3),
    new Progress<HackBackgroundProgress>(report => { _progress = report; StateHasChanged(); }),
    new HackBackgroundOptions { Name = "blur" },
    cancellationToken);
```

Formes acceptées (`HackerOs.AppSdk.Threading.HackBackground`) :

| Forme | Tâche |
|---|---|
| `RunAsync<TInput, TResult>(job, input, …)` | `Func<TInput, HackBackgroundContext, Task<TResult>>` ou `Func<…, TResult>` |
| `RunAsync<TInput>(job, input, …)` | `Func<TInput, HackBackgroundContext, Task>` ou `Action<TInput, HackBackgroundContext>` |
| `RunAsync<TJob, TInput, TResult>(input, …)` | une classe `TJob : HackBackgroundJob<TInput, TResult>, new()` |

Chaque forme prend `IProgress<HackBackgroundProgress>? progress`,
`HackBackgroundOptions? options` (`Name`, `Nice`) et `CancellationToken`.

### Le contexte de la tâche

`HackBackgroundContext` :

| Membre | Rôle |
|---|---|
| `ProcessId` | PID du processus de la tâche. |
| `CancellationToken` | Annulé par le jeton de l'appelant, par `kill` sans `-9`, à la fermeture de l'app. |
| `Report(fraction, message)` | Progression de `0` à `1` (bornée) ; au plus 10 remontées par seconde, la dernière l'emporte, celle d'avant la fin est toujours livrée. `NaN` lève `ArgumentOutOfRangeException`. |
| `CheckpointAsync()` | Point d'arrêt volontaire (ADR 0069) : lève si la tâche est annulée et rend le fil au-delà de 8 ms. Indispensable sur le fil principal, utile en worker. |

Dans la tâche, le reste du SDK (`HackFile`, `HackDirectory`, `HackProcess`,
`HackEnvironment`…) marche par le contexte ambiant du processus de la tâche :
même utilisateur, même session, mêmes capacités accordées que l'app.

### Résultat et erreurs

| Cas | Ce que voit l'appelant | Code de sortie |
|---|---|---|
| Succès | Le résultat désérialisé | `0` |
| Exception de la tâche | `HackBackgroundJobException` (`OriginalTypeName`, `Message`, `OriginalStackTrace`) : l'objet ne traverse pas | `1` |
| Annulation | `OperationCanceledException` | `130` |
| Tuée (`kill -9`, gestionnaire de tâches, OOM) | `HackProcessKilledException` avec `Reason` | `137` |
| Refusée (méthode introuvable, type non déclaré, autre assembly, plus de 16 Mio) | `ArgumentException` | `127` |

`HackProcessKilledException` dérive de `OperationCanceledException` : placer
son `catch` **avant** celui de l'annulation.

### Montrer et tuer une tâche depuis la fenêtre

Une app voit et tue ses propres descendants sans `process.list` ni
`process.manage` (ADR 0140 §7) :

```csharp
await AppContext.Processes.KillAsync(ProcessId.FromInt64(pid));
```

La tâche peut donner son PID dans sa progression (`$"pid {context.ProcessId} · …"`),
comme le fait la fenêtre d'exemple.

### Tester une app qui utilise `HackBackground`

Sans OS (contexte de test comme `MinimalAppExecutionContext`), la passerelle
par défaut exécute la tâche **en ligne**, dans le processus du test, avec la
même sérialisation de l'entrée et du résultat et les mêmes erreurs. Un test qui
passe ainsi passe à l'identique en worker.

```csharp
using IDisposable scope = HackExecutionContext.Enter(new MinimalAppExecutionContext(manifest));
PrimeCountResult result = await HackBackground.RunAsync<PrimeCountInput, PrimeCountResult>(
    SampleJobs.CountPrimes, new PrimeCountInput(100_000));
```

## Règles vérifiées au build

L'analyseur `HackerOs.Tools.BackgroundAnalyzer` est livré dans le paquet
`HackerOs.AppSdk` (`analyzers/dotnet/cs`) ; les apps du dépôt le reçoivent par
`build/HackerOs.Apps.targets`. Toutes ses règles sont des erreurs.

| Code | Refus |
|---|---|
| `HOSBG0001` | La tâche n'est pas un groupe de méthode statique : lambda (même `static`), méthode anonyme, méthode d'instance, fonction locale non `static`, délégué passé par une variable. |
| `HOSBG0002` | Le type d'entrée ou de résultat n'est pas atteint depuis un `[JsonSerializable]` d'un contexte déclaré par `[assembly: HackWireContext(...)]`. Un type atteint par un tableau, un argument générique ou une propriété publique d'une racine est accepté, comme `GetTypeInfo` le trouve. |
| `HOSBG0003` | La méthode ou la classe de tâche est dans une autre assembly que l'app. |
| `HOSBG0004` | La méthode est générique, ou appartient à un type générique. |

L'OS refait les vérifications à l'exécution (un appel par réflexion ne passe
pas l'analyseur) et lève `ArgumentException`.

## Architecture

```
Fenêtre (fil principal)                       OS (fil principal)                         Web Worker du pool
HackBackground.RunAsync ──► IAppExecutionContext.Background
   sérialise l'entrée          AppBackgroundGateway ──► BackgroundJobService
   (HackWireContext)                                    vérifie, crée le processus enfant,
                                                        attend une place (par utilisateur et app)
                                                        ├─ pas de worker : la tâche sur le fil principal,
                                                        │  dans son processus (son planificateur)
                                                        └─ WorkerRemoteProcessHost ── start k=2 ──► WorkerProcessHost
                                                                                       ◄── bg.progress ── tâche
                                                                                       ◄── exit jr/jf ───
   ◄── résultat désérialisé, progression postée dans le contexte de la fenêtre
```

- **SDK** (`Shared/HackerOs.AppSdk/Threading`) : `HackBackground`,
  `HackBackgroundContext`, `HackBackgroundJob<,>`, `IAppBackgroundGateway`,
  `HackBackgroundWire` (contextes `HackWireContext`, 16 Mio au plus),
  `HackBackgroundJobRunner` (liaison d'une cible nommée), passerelle en ligne
  par défaut.
- **OS** (`Platform/HackerOs.Platform.Core/Execution/BackgroundJobService.cs`) :
  processus enfant (genre `Terminal`, `argv[0]` = nom), contexte de confiance
  de l'enfant par la fabrique, file par couple utilisateur et app
  (`MaxConcurrentJobsPerApp`, nombre de cœurs virtuels), `GetJobs` et
  `TryGetJob` pour `/proc` et le gestionnaire de tâches.
- **Workers** (`Platform/HackerOs.Platform.WorkerHost`) : `start` avec `k = 2`,
  `bg.progress`, `bg.run` et `bgp` pour une tâche lancée depuis un worker
  ([`worker-processes.md`](../dev/worker-processes.md#tâches-hackbackground-adr-0128-adr-0140)).
- **`/proc/<pid>/status`** : `Name:` porte le nom de la tâche (15 caractères),
  puis `Job:` et `Host: MainThread|Worker`.

## Décisions clés

- La tâche est nommée (méthode statique ou classe) et ses données passent en
  JSON : un worker est un autre runtime .NET, sans les objets de la fenêtre.
- Sans worker, même contrat sur le fil principal, avec la même sérialisation :
  une app qui marche dans ses tests marche en worker.
- Un processus par tâche, enfant de l'app : visible, tuable seul. À la
  fermeture de l'app, demande d'arrêt `DependencyStop` puis mise à mort après
  5 s sur l'horloge de la simulation (`ProcessStartRequest.ParentExitGracePeriod`,
  ADR 0147, qui remplace l'ADR 0140 §6) ; `kill -9` de l'app, une limite ou
  l'OOM killer la tuent tout de suite.
- Le PID de la tâche ne fait pas partie de la progression (réponse de Benoit,
  ADR 0147 §3) : la tâche le met dans son message si la fenêtre en a besoin.
- `nice` est borné et devient la valeur `nice` du processus de la tâche
  (`ps -o ni`, `/proc/<pid>/stat`) ; il fixe sa place dans la file des workers
  (ADR 0127, [`process-priority-and-unresponsive-windows.md`](../dev/process-priority-and-unresponsive-windows.md)).

## Apps de base

| Traitement | Décision |
|---|---|
| Recherche dans le dossier du CodeEditor | Tâche `code-editor:search` ([`code-editor.md`](../dev/code-editor.md)). |
| Compresser, Extraire, « UnZip Here… » de l'explorateur | Tâches `file-explorer:compress` et `file-explorer:extract` ([`file-explorer.md`](../dev/file-explorer.md)). |
| Copier, déplacer, coller ; téléchargement en `.zip` | Restent dans la fenêtre : appels à l'OS attendus avec des points d'arrêt, question de conflit dans la fenêtre ; octets du téléchargement destinés à la fenêtre (ADR 0140 §9). |
| Hack Paint | Pas de filtre ; rotation et recadrage restent (tampon jusqu'à 64 Mio, au-delà de la limite de 16 Mio) ([`hack-paint.md`](../dev/hack-paint.md)). |

## Exemple

`Apps/Samples/HackerOs.Samples.WindowApp`, section « Background Task » :
compte des nombres premiers (`SampleJobs.CountPrimes`) avec barre de
progression, « Cancel » (annulation) et « Kill » (tue le processus de la
tâche ; la fenêtre reste ouverte et affiche `killed`).

## Tests

- `Tests/HackerOs.AppSdk.Tests/Threading/HackBackgroundTests.cs` : passerelle en
  ligne, lambda refusée, type non déclaré, faute, annulation, limitation de la
  progression.
- `Tests/HackerOs.AppSdk.Tests/Threading/HackBackgroundAnalyzerTests.cs` : les
  quatre règles `HOSBG`.
- `Tests/HackerOs.Platform.WorkerHost.Tests/BackgroundJobTests.cs` : chaque cas
  sur le fil principal **et** en worker (résultat, processus enfant,
  progression, classe de tâche, faute, annulation, `kill -9` sans fermer l'app,
  l'app qui tue sa propre tâche, `kill -9` de l'app, fermeture de l'app avec
  sortie en `130` ou mise à mort à 5 s, limite de concurrence,
  `/proc/<pid>/status`, `ps`, entrée refusée, autre assembly).
- Tests des apps : recherche en tâche (CodeEditor), compression et extraction
  (explorateur), tâche de l'exemple.

## Liste des tâches du chantier `exec/5-background-jobs`

- [x] API du SDK (`HackBackground`, contexte, classe de tâche, options, exception), passerelle en ligne par défaut.
- [x] `BackgroundJobService` : processus enfant, contexte de confiance, file par utilisateur et app, codes de sortie.
- [x] Workers : `start` `k = 2`, `bg.progress`, `bg.run`, `bgp`, erreurs `bgfault` et raison `rs`.
- [x] `/proc/<pid>/status` (`Name`, `Job`, `Host`) et `ps`.
- [x] Une app gère ses descendants sans `process.manage` (ADR 0140 §7).
- [x] Analyseur `HOSBG0001` à `HOSBG0004`, livré dans le paquet du SDK et branché sur les apps du dépôt.
- [x] Recherche du CodeEditor, compression et extraction de l'explorateur en tâches ; raison écrite pour les copies, le téléchargement et Hack Paint.
- [x] Exemple dans la fenêtre d'exemple.
- [x] Tests SDK, analyseur, OS et worker, apps.
- [x] Documentation, ADR 0140, index.
- [x] Délai de 5 s à la fermeture de l'app, réponses de Benoit aux deux cartes (ADR 0147).
