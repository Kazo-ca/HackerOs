> [English version](../../../en/wasm/framework/hack-file-api.md)

# API fichier `Hack*` et contexte d'exécution ambiant

Implémentation de la section 5.1 du [plan de refonte](../dev/plans/ecosystem-rebuild-plan.md),
selon l'[ADR 0043](../dev/adr/0043-hack-file-api-and-ambient-context.md).

## Objectif

Donner aux apps une API de fichiers familière à un développeur .NET, calquée sur
`System.IO`, sans passer de contexte ni gérer les révisions à la main :

```csharp
using HackerOs.AppSdk.IO;

await HackFile.WriteAllTextAsync("~/notes.txt", "Bonjour");
string texte = await HackFile.ReadAllTextAsync("~/notes.txt");
string[] logs = await HackDirectory.GetFilesAsync("/var/log", "*.log");
```

L'app appelante est identifiée par le contexte ambiant que l'OS positionne. Tous
les appels passent par la passerelle `IAppFileSystemGateway` de l'app : capacités,
autorité, bits de mode, tickets d'élévation (ADR 0042) et notification d'erreur
de permission restent appliqués exactement comme avant.

## Architecture

| Élément | Emplacement | Rôle |
|---|---|---|
| `HackExecutionContext` | `Shared/HackerOs.AppSdk/HackExecutionContext.cs` | Contexte ambiant (`AsyncLocal`) : `Current`, `CurrentOrNull`, `WorkingDirectory`, `Enter(context, workingDirectory)`. Depuis la phase 3, `WorkingDirectory` est le répertoire du processus (`HackEnvironment`, [`process-environment.md`](../dev/process-environment.md)), sauf répertoire fixé par `Enter`. Depuis la phase 5, son `AsyncLocal` peut porter le crochet de changement de contexte des mesures de ressources, installé à la composition par `HackerOs.Platform.ResourceAccounting` ([`resource-accounting.md`](../dev/resource-accounting.md)) ; une portée ouverte empêche de l'installer. |
| `IAppExecutionContext.HomeDirectory` | `Shared/HackerOs.AppSdk/IAppExecutionContext.cs` | Dossier personnel `/home/<login>` (membre par défaut `/home/{UserId}`, remplacé par la plateforme). Sert à `~` et au répertoire courant par défaut. |
| `IAppExecutionContext.IsRootElevated` | `Shared/HackerOs.AppSdk/IAppExecutionContext.cs` | Lancement en root complet par `sudo` (ADR 0042), membre par défaut `false`. Ne change pas `HomeDirectory` ; sert à la corbeille de l'utilisateur effectif (`/root/.local/share/Trash`, [`trash.md`](../dev/trash.md)). |
| `HackFile`, `HackDirectory`, `HackPath` | `Shared/HackerOs.AppSdk/IO/` | Façades statiques. `HackPath` est pur, sauf `GetFullPath(path)`. |
| `HackFileSystemInfo`, `HackFileInfo`, `HackDirectoryInfo` | `IO/HackFileSystemInfo.cs` | Instantanés de métadonnées (`GetAsync`, `RefreshAsync`). |
| `HackFileStream` | `IO/HackFileStream.cs` | Flux de lecture (enveloppe la lecture du moteur) ou d'écriture (tampon mémoire publié en une écriture atomique). |
| `HackFileSystemWatcher` | `IO/HackFileSystemWatcher.cs` | Événements `Created`, `Changed`, `Deleted`, `Renamed` au-dessus de `IAppFileSystemWatchGateway`. |
| `HackWriteOptions` | `IO/HackWriteOptions.cs` | API avancée : `ExpectedRevision`, `CreateMode`. |
| Exceptions `Hack*` | `IO/HackExceptions.cs` | `HackIOException` et ses dérivées, `HackUnauthorizedAccessException`. |
| `HackFileSystemErrorTranslator` (interne) | `IO/HackFileSystemErrorTranslator.cs` | Table de traduction `FileSystemErrorCode` → exception. |
| `HackFileSystemCore` (interne) | `IO/HackFileSystemCore.cs` | Plomberie commune : stat, lecture, publication avec nouvel essai, copie entre montages, énumération. |

### Points d'entrée où l'OS ouvre le contexte

| Type de code | Où | Répertoire courant |
|---|---|---|
| Commande terminal | `AppLifecycleOrchestrator`, autour de `ExecuteAsync` | celui du processus de la commande (copié du shell à la création) |
| Service | `ServiceAppBase.RunAsync` et `StopAsync` | dossier personnel |
| Fenêtre | `WindowAppBase` : `SetParametersAsync`, `IHandleEvent.HandleEventAsync`, `IHandleAfterRender.OnAfterRenderAsync` | dossier personnel |
| Composant enfant | `HackComponentBase` (nouveau, `HackerOs.AppSdk.Blazor`) reçoit le contexte par `CascadingValue` posé par `WindowAppRenderer` | celui de la fenêtre |
| Rappels de passerelles | `AppClockGateway.Schedule` et `AppEventGateway.Subscribe` (voie noyau et voie topic) rentrent dans le contexte de leur app (`AppAmbientContextBinding`) | dossier personnel |

Les autres passerelles de l'ADR n'ont pas de rappel à envelopper :
`IAppFileSystemWatchGateway` renvoie un canal lu par le code de l'app (le
`HackFileSystemWatcher` le lit dans une boucle qui hérite du contexte de
`StartAsync`), et les actions de notification reviennent à l'app par le bus
d'événements, déjà couvert.

Une méthode `[JSInvokable]` ne reçoit pas l'`AsyncLocal` : elle appelle
`HackExecutionContext.Enter(AppContext)` elle-même. Sans contexte, l'API lève
`HackExecutionContextMissingException` ; elle ne retombe jamais sur une identité
système.

## Utilisation

### Chemins

- Absolus (`/etc/hosts`), relatifs au répertoire courant (`notes.txt`, `../x`),
  ou dans le dossier personnel (`~`, `~/docs`).
- `HackPath.GetFullPath(path, basePath)` résout sans lire le contexte.
- Les listes renvoient des chemins absolus canoniques, triés par nom (ordinal),
  en profondeur pour `SearchOption.AllDirectories`, sans suivre les liens
  symboliques vers des dossiers. Les motifs (`*`, `?`) respectent la casse.

### Écriture, umask et révisions

- Umask fixe de phase 1 : fichier créé en `0644`, dossier en `0755`
  (`HackWriteOptions.CreateMode` ou `CreateDirectoryAsync(path, mode)` pour autre
  chose). Seuls les bits `0777` sont acceptés.
- Une écriture lit la révision courante, crée le fichier s'il manque, écrit, et
  réessaie **une seule fois** en cas de conflit. Un second conflit lève
  `HackFileConflictException`.
- L'ajout (`AppendAll*`, `OpenAppendAsync`) relit le contenu à chaque essai : une
  modification concurrente n'est jamais écrasée.
- Contrôle strict (éditeur de texte) : `ExpectedRevision` désactive le nouvel
  essai. Les surcharges avec `HackWriteOptions` renvoient le `HackFileInfo` validé,
  dont la `Revision` sert à l'écriture suivante.

```csharp
HackFileInfo fichier = await HackFileInfo.GetAsync(chemin);
// ... l'utilisateur modifie ...
try
{
    fichier = await HackFile.WriteAllTextAsync(chemin, texte,
        new HackWriteOptions { ExpectedRevision = fichier.Revision });
}
catch (HackFileConflictException conflit)
{
    // Le fichier a changé ailleurs : conflit.CurrentRevision
}
```

- Texte : UTF-8 sans BOM par défaut ; un encodage passé explicitement écrit son
  préambule (comme .NET). Les lignes se terminent par `\n`. Les fichiers texte
  sont publiés avec le type `text/plain`.
- `CopyAsync`/`MoveAsync` entre deux montages : la façade copie le flux (en gardant
  les bits du fichier source) puis supprime la source.
- `HackFile.DeleteAsync` supprime définitivement, comme `System.IO`. Pour une
  suppression réversible, `HackTrash.MoveToTrashAsync` (phase 3, voir
  [`trash.md`](../dev/trash.md)).

### Flux

`OpenWriteAsync` **remplace** le contenu (contrairement à `File.OpenWrite`, voir
l'ADR). Le contenu est publié par une seule écriture atomique sur `FlushAsync()`
ou `DisposeAsync()`. Toujours utiliser `await using` :

```csharp
await using HackFileStream flux = await HackFile.OpenWriteAsync("~/rapport.txt");
await using StreamWriter writer = new(flux);
await writer.WriteLineAsync("ligne");
```

Quand l'OS mesure la mémoire (ADR 0068), le tampon d'un flux d'écriture est
compté dans la part noyau du processus (`VmData` de `/proc/<pid>/status`)
jusqu'à la fermeture du flux, par `IAppFileSystemGateway.ChargeStreamBuffer()`
(méthode par défaut qui ne charge rien : une passerelle de test n'a rien à
faire). Voir [`memory-accounting.md`](../dev/memory-accounting.md).

Le `Flush()` synchrone ne fait rien, et un `Dispose()` synchrone d'un flux qui
contient des octets non publiés lève `InvalidOperationException` au lieu de les
perdre. Le flux garde la passerelle de l'app qui l'a ouvert.

### Taille totale d'un dossier (ADR 0185 §5)

`HackDirectory.GetTotalSizeAsync(path)` (ou `HackDirectoryInfo.GetTotalSizeAsync()`) rend un
`HackDirectorySize` : somme des longueurs des fichiers, nombre de fichiers et de sous-dossiers, comme
`du -sb`, sans lire aucun contenu. Les liens symboliques ne sont ni suivis ni comptés. Un sous-dossier
illisible est sauté et `IsComplete` vaut alors `false`. Sous le SDK, c'est la méthode
`IAppFileSystemGateway.MeasureAsync` du service de fichiers (membre par défaut qui parcourt avec
`StatAsync` et `EnumerateAsync`, donc avec les droits de l'appelant ; algorithme partagé
`FileSystemTreeMeasure`). Le dialogue Propriétés d'un dossier l'affiche.

### Surveillance

```csharp
await using HackFileSystemWatcher watcher = new("~/inbox", "*.txt");
watcher.Created += (_, e) => Console.WriteLine(e.FullPath);
await watcher.StartAsync();
```

Le moteur ne surveille que les enfants directs : `IncludeSubdirectories = true`
lève `NotSupportedException`. Un déplacement dans le dossier donne `Renamed`, une
sortie `Deleted`, une entrée `Created`.

### Exceptions

| Code moteur | Exception |
|---|---|
| `NotFound` (fichier) | `HackFileNotFoundException` |
| `NotFound` (dossier, ou parent absent à la création) | `HackDirectoryNotFoundException` |
| `NotFile`, `PermissionDenied`, `CapabilityDenied`, `AuthorityDenied`, `ProtectedEntry` | `HackUnauthorizedAccessException` (avec `RequiredCapability`) |
| `RevisionConflict` | `HackFileConflictException` (après la règle du nouvel essai) |
| `InvalidPath`, `InvalidName`, `RootContainmentViolation` | `ArgumentException` |
| `UnsupportedOperation` | `NotSupportedException` |
| `Cancelled` | `OperationCanceledException` |
| `CrossMountOperation` | aucune : copie puis suppression par la façade |
| Autres codes, et tout code futur | `HackIOException` |

Les exceptions dérivent de `IOException` et `UnauthorizedAccessException` : un
`catch` ordinaire fonctionne.

## Décisions clés

- **`HomeDirectory` sur `IAppExecutionContext`.** L'ADR parle de `/home/{UserId}`,
  mais `UserId` est un identifiant opaque alors que le dossier personnel est
  `/home/<login>`. Le contexte expose donc `HomeDirectory`, rempli par la fabrique
  avec `AuthenticatedPrincipal.HomePath`. Le fil `rebuild/1-settings-files` ajoute
  le même membre, avec la même signature.
- **Infos sans lecture paresseuse.** `HackFileInfo`/`HackDirectoryInfo` ne lisent
  rien implicitement : `GetAsync` ou `RefreshAsync`. Un fichier absent rapporte les
  valeurs par défaut de .NET (date 1601-01-01 UTC, `UnixFileMode` à `-1`).
- **Liens symboliques.** `IsSymbolicLink` et `LinkTarget` décrivent le lien ; les
  autres propriétés décrivent sa cible, comme `stat`.
- **`File.Move` d'un dossier** lève `HackFileNotFoundException`, comme .NET ;
  `HackDirectory.MoveAsync` déplace un dossier ou un fichier.
- **`HackDirectory.CopyAsync`** (phase 4, `rebuild/4-file-explorer`) : copie
  récursive d'un dossier vers une destination absente, ajout de HackerOS (.NET n'a
  pas de `Directory.Copy`) ; le moteur copie l'arbre en une transaction, la façade
  le recrée entrée par entrée entre deux montages. Voir
  [`file-explorer.md`](../dev/file-explorer.md).
- **Double faux de tests.** `FakeAppFileSystemGateway` (`HackerOs.Tests.Support`)
  traite maintenant une révision attendue nulle comme une opération
  inconditionnelle et renvoie `NotFile` pour la lecture ou l'écriture d'un dossier,
  comme le vrai moteur. `MinimalAppExecutionContext` accepte une passerelle de
  surveillance et un dossier personnel.

## Tests

- `Tests/HackerOs.AppSdk.Tests/IO/` : contexte (absence, restauration, imbrication,
  propagation par `await`, `Task.Run` et `Timer`, services), `HackPath`, `HackFile`
  (umask, encodage, nouvel essai unique, révision stricte, flux, copie et
  déplacement entre montages), `HackDirectory`, infos, chaque ligne de la table de
  traduction, `HackFileSystemWatcher`.
- `Tests/HackerOs.AppSdk.Blazor.Tests/AmbientContextComponentTests.cs` : cycle de
  vie, événements et après-rendu d'une fenêtre et d'un `HackComponentBase` enfant.
- `Tests/HackerOs.Platform.Core.Tests` : `HomeDirectory` de la fabrique, rappels de
  l'horloge et du bus d'événements, contexte d'une commande terminal.

## Liste des tâches

- [x] `HackExecutionContext` et `HackExecutionContextMissingException`.
- [x] `IAppExecutionContext.HomeDirectory`, rempli par la fabrique.
- [x] `HackPath`.
- [x] Exceptions `Hack*` et traduction depuis `FileSystemErrorCode`.
- [x] `HackFile` (lecture, écriture, ajout, copie, déplacement, suppression, flux, permissions).
- [x] `HackFile.CreateSymbolicLinkAsync(chemin, cible)` (ADR 0064, `ln -s`) : comme `File.CreateSymbolicLink`, cible gardée telle quelle, lien pendant permis.
- [x] `HackDirectory`.
- [x] `HackFileSystemInfo`, `HackFileInfo`, `HackDirectoryInfo`.
- [x] `HackFileStream`.
- [x] `HackFileSystemWatcher`.
- [x] Points d'entrée : terminal, service, fenêtre, `HackComponentBase` + `CascadingValue`, horloge, bus d'événements.
- [x] Tests de contrat.
- [ ] Phase 3 : répertoire courant par processus (D13) et umask par processus.
- [ ] Phase 4 : migrer les apps de base de `AppContext.FileSystem` vers `Hack*`.
