# ADR 0043 : API fichier `Hack*` de type System.IO et contexte d'exécution ambiant

## Statut

Accepté le 2026-10-10.

- **Phase du plan :** phase 1 — Fichiers ([`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), section 5.1, décisions D2 à D5).
- **Remplace :** aucun ADR. **Complète :** ADR 0008 (le moteur reste la seule implémentation ; cette API est une façade au-dessus).

## Contexte

Écrire une chaîne dans un fichier exige aujourd'hui quatre étapes : `CreateAsync`
avec des bits de mode, `StatAsync` pour obtenir la révision, `WriteAsync` avec la
révision attendue, et une classe `IFileSystemContentSource` écrite à la main (voir
`Apps/System/HackerOs.Apps.TextEditor/TextEditorWindow.razor`). Chaque appel exige
aussi de transporter l'`IAppExecutionContext` de l'app jusqu'à la passerelle
`IAppFileSystemGateway`.

Le moteur (`IFileSystemService`, révisions optimistes, transactions, montages) est
solide et reste en place (D2). Il manque une façade familière à un développeur
.NET : la même forme que `System.IO`, statique, asynchrone, avec des exceptions.

## Décision

### 1. Emplacement et dépendances

- Projet : `Shared/HackerOs.AppSdk` (aucune dépendance Blazor, utilisable par les
  apps fenêtre, terminal et service).
- Dossier `Shared/HackerOs.AppSdk/IO/`, namespace public **`HackerOs.AppSdk.IO`**.
- Le contexte ambiant vit dans `Shared/HackerOs.AppSdk/HackExecutionContext.cs`,
  namespace **`HackerOs.AppSdk`**.
- La façade n'appelle **que** `IAppFileSystemGateway` et
  `IAppFileSystemWatchGateway` du contexte courant. Elle ne référence ni
  `HackerOs.Platform.Core` ni `IFileSystemService`. Les capacités, l'autorité,
  les bits de mode, les tickets d'élévation (ADR 0042) et la notification
  d'erreur de permission (`IAppPermissionErrorGateway`) restent donc appliqués
  exactement comme aujourd'hui.

### 2. Contexte d'exécution ambiant

```csharp
namespace HackerOs.AppSdk;

public static class HackExecutionContext
{
    // Contexte de l'app qui exécute le code courant. Lève
    // HackExecutionContextMissingException s'il n'y en a aucun.
    public static IAppExecutionContext Current { get; }

    // Même chose, mais null au lieu d'une exception.
    public static IAppExecutionContext? CurrentOrNull { get; }

    // Répertoire courant du code en cours (chemin canonique absolu).
    public static string WorkingDirectory { get; }

    // Positionne le contexte pour la durée du scope (Dispose restaure le précédent).
    public static IDisposable Enter(IAppExecutionContext context, string? workingDirectory = null);
}

public sealed class HackExecutionContextMissingException : InvalidOperationException;
```

- Stockage : un champ `static readonly AsyncLocal<HackAmbientFrame?>`, où
  `HackAmbientFrame` est un `record` interne `(IAppExecutionContext App, string
  WorkingDirectory)`. L'`AsyncLocal` suit les `await`, `Task.Run` et
  `System.Threading.Timer`, puisque ceux-ci capturent l'`ExecutionContext`.
- `workingDirectory` nul : on prend le dossier personnel `/home/{UserId}`.
- **Absence de contexte :** une erreur explicite
  (`HackExecutionContextMissingException`). On ne retombe **jamais** sur un
  contexte système ou anonyme.
- **Pourquoi `Enter` est public :** l'autorité vient des passerelles, que seule
  la fabrique de la plateforme (`AppExecutionContextFactory`) construit. Une app
  qui fabriquerait son propre `IAppExecutionContext` n'obtiendrait que ses
  propres fausses passerelles, sans aucun droit. Un `Enter` public reste donc
  sûr, et il rend la façade testable sans `InternalsVisibleTo`.

**Points d'entrée où l'OS positionne le contexte** (liste exhaustive à
implémenter) :

| Type d'app | Où | Répertoire courant |
|---|---|---|
| Terminal | `AppLifecycleOrchestrator`, autour de `app.ExecuteAsync(terminalContext, …)` | `TerminalExecutionContext.WorkingDirectory` |
| Service | `ServiceAppBase.RunAsync` et `ServiceAppBase.StopAsync` (scope ouvert dans la base, avec le contexte reçu) | dossier personnel |
| Fenêtre | `WindowAppBase` (`Shared/HackerOs.AppSdk.Blazor`) : `SetParametersAsync` (déjà scellé), et réimplémentation explicite de `IHandleEvent.HandleEventAsync` et `IHandleAfterRender.OnAfterRenderAsync`, chacun enveloppé dans `Enter(AppContext)` | dossier personnel |
| Composants enfants | nouvelle base `HackComponentBase : ComponentBase` dans `HackerOs.AppSdk.Blazor`, avec `[CascadingParameter] IAppExecutionContext`, qui enveloppe de la même façon ses événements et son cycle de vie. `WindowAppRenderer` (`Platform/HackerOs.Platform.Blazor/Windows/`) entoure l'app d'un `CascadingValue` (`IsFixed="true"`) portant son contexte. | celui du parent |
| Rappels des passerelles | `AppClockGateway.Schedule`, `AppEventGateway.Subscribe` (les deux voies), `AppFileSystemWatchGateway`, actions de `AppNotificationGateway` : chaque passerelle, liée à une seule instance, enveloppe l'appel du gestionnaire dans `Enter` avec son propre contexte | dossier personnel |

Les rappels JavaScript (`[JSInvokable]`) ne transportent pas l'`AsyncLocal`. Une
méthode `[JSInvokable]` d'une app doit appeler
`HackExecutionContext.Enter(AppContext)` elle-même. C'est documenté dans le SDK.

Le répertoire courant de phase 1 est celui du terminal ou le dossier personnel.
La phase 3 (D13) le remplacera par le répertoire courant du processus, sans
changer cette API.

### 3. Types publics

Tous asynchrones (D4) : aucune méthode synchrone d'E/S, puisqu'IndexedDB est
asynchrone et que le navigateur n'a qu'un thread. Les chemins sont des `string`,
comme dans `System.IO`. Les chemins relatifs et `~` sont résolus par
`HackPath.GetFullPath`.

| Type | Équivalent .NET | Membres |
|---|---|---|
| `static class HackFile` | `File` | `ReadAllTextAsync(path, Encoding? = null, ct)`, `ReadAllLinesAsync`, `ReadAllBytesAsync`, `WriteAllTextAsync(path, contents, Encoding? = null, ct)`, `WriteAllLinesAsync`, `WriteAllBytesAsync`, `AppendAllTextAsync`, `AppendAllLinesAsync`, `ExistsAsync`, `CopyAsync(src, dest, overwrite = false)`, `MoveAsync(src, dest, overwrite = false)`, `DeleteAsync`, `OpenReadAsync` → `HackFileStream`, `OpenWriteAsync` → `HackFileStream` (remplace), `OpenAppendAsync` → `HackFileStream`, `GetUnixFileModeAsync`, `SetUnixFileModeAsync(path, UnixFileMode)`. Chaque écriture accepte aussi une surcharge `HackWriteOptions`. |
| `static class HackDirectory` | `Directory` | `CreateDirectoryAsync` (crée les parents manquants, comme .NET), `ExistsAsync`, `GetFilesAsync(path, searchPattern = "*", SearchOption = TopDirectoryOnly)`, `GetDirectoriesAsync(…)`, `GetFileSystemEntriesAsync(…)`, `EnumerateFileSystemEntriesAsync(…)` → `IAsyncEnumerable<string>`, `MoveAsync`, `DeleteAsync(path, recursive = false)` |
| `static class HackPath` | `Path` | Synchrone et pur : `Combine`, `Join`, `GetFileName`, `GetFileNameWithoutExtension`, `GetExtension`, `ChangeExtension`, `GetDirectoryName`, `IsPathRooted`, `DirectorySeparatorChar = '/'`, `GetFullPath(path)` (lit le contexte ambiant pour `~` et le répertoire courant), `GetFullPath(path, basePath)` (pur) |
| `abstract class HackFileSystemInfo` | `FileSystemInfo` | `FullName`, `Name`, `Extension`, `Exists`, `CreationTimeUtc`, `LastWriteTimeUtc`, `UnixFileMode`, `Owner`, `Group`, `Revision` (révision optimiste du moteur), `IsSymbolicLink`, `LinkTarget`, `RefreshAsync()` |
| `sealed class HackFileInfo` | `FileInfo` | + `Length`, `Directory`, `DirectoryName`, `OpenReadAsync`, `OpenWriteAsync`, `CopyToAsync`, `MoveToAsync`, `DeleteAsync` ; `static ValueTask<HackFileInfo> GetAsync(path)` (instantané initial) |
| `sealed class HackDirectoryInfo` | `DirectoryInfo` | + `Parent`, `CreateAsync`, `GetFilesAsync`, `GetDirectoriesAsync`, `GetFileSystemInfosAsync`, `MoveToAsync`, `DeleteAsync(recursive)` ; `static ValueTask<HackDirectoryInfo> GetAsync(path)` |
| `sealed class HackFileStream : Stream` | `FileStream` | Lecture : enveloppe le flux de `FileSystemContentReadHandle`. Écriture : tampon en mémoire, publié par **un seul** `WriteAsync` atomique dans `FlushAsync()` ou `DisposeAsync()`. |
| `sealed class HackFileSystemWatcher : IAsyncDisposable` | `FileSystemWatcher` | `Path`, `Filter`, `IncludeSubdirectories`, `StartAsync()`, événements `Created`, `Changed`, `Deleted`, `Renamed` |
| `sealed record HackWriteOptions` | — | API avancée : `long? ExpectedRevision` (contrôle strict des conflits, pour l'éditeur de texte), `UnixFileMode? CreateMode` (bits du fichier créé) |

Les permissions utilisent l'énumération BCL `System.IO.UnixFileMode`. On
n'invente pas de type maison.

### 4. Création, umask et révisions

- **Umask fixe de phase 1 : `022`.** Fichier créé en `0644`, dossier en `0755`,
  sauf si `HackWriteOptions.CreateMode` dit autre chose. La phase 3 pourra rendre
  l'umask propre à chaque processus.
- **`WriteAllText`/`WriteAllBytes`/`OpenWrite` :** `StatAsync`. Si le fichier
  n'existe pas, `CreateAsync` puis `WriteAsync`. S'il existe, `WriteAsync` avec
  `expectedRevision` égale à la révision lue. En cas de `RevisionConflict`, on
  relit la révision et on réessaie **une seule fois**. Un second conflit lève
  `HackFileConflictException`.
- **`AppendAll*` :** lecture, concaténation, puis écriture avec la révision lue,
  avec la même règle d'un seul nouvel essai. Le moteur n'a pas d'ajout natif ;
  le stockage par blocs dédupliqués ne réécrit que le dernier bloc.
- **Avec `HackWriteOptions.ExpectedRevision` :** aucun nouvel essai. Un conflit
  lève immédiatement `HackFileConflictException`.
- **Encodage :** UTF-8 sans BOM par défaut. Le `FileSystemContentDescriptor`
  publié est du texte UTF-8 pour les API texte et binaire pour les API d'octets.
- **`CopyAsync`/`MoveAsync` entre deux montages** (`CrossMountOperation`) : la
  façade copie le flux puis supprime la source (pour un déplacement), comme
  `File.Move` entre volumes sous .NET.
- `HackFile.DeleteAsync` supprime **définitivement**. La corbeille arrive en
  phase 3 (D15) avec `HackTrash`. Elle ne changera pas cette méthode.

### 5. Exceptions (D5)

Hiérarchie, dans `HackerOs.AppSdk.IO` :

```text
System.IO.IOException
└── HackIOException                     (+ FileSystemErrorCode? ErrorCode, string? Path)
    ├── HackFileNotFoundException
    ├── HackDirectoryNotFoundException
    └── HackFileConflictException       (+ long? CurrentRevision)
System.UnauthorizedAccessException
└── HackUnauthorizedAccessException     (+ ErrorCode, Path, string? RequiredCapability)
```

Comme elles dérivent des types BCL, un `catch (IOException)` ordinaire fonctionne.

Traduction de `FileSystemErrorCode` (classe interne
`HackFileSystemErrorTranslator`) :

| Code | Exception |
|---|---|
| `NotFound` (opération sur fichier) | `HackFileNotFoundException` |
| `NotFound` (opération sur dossier, ou parent absent lors d'une création) | `HackDirectoryNotFoundException` |
| `AlreadyExists`, `DirectoryNotEmpty`, `NotDirectory`, `SymbolicLinkLoop`, `SymbolicLinkLimitExceeded`, `InvalidContent`, `ProviderUnavailable` | `HackIOException` |
| `NotFile` (lecture ou écriture d'un dossier, comme .NET) | `HackUnauthorizedAccessException` |
| `PermissionDenied`, `CapabilityDenied`, `AuthorityDenied`, `ProtectedEntry` | `HackUnauthorizedAccessException` |
| `RevisionConflict` | `HackFileConflictException` (après la règle du nouvel essai) |
| `InvalidPath`, `InvalidName`, `RootContainmentViolation` | `ArgumentException` (BCL, comme .NET) |
| `UnsupportedOperation` | `NotSupportedException` (BCL) |
| `Cancelled` | `OperationCanceledException` (BCL) |
| `CrossMountOperation` | aucune : géré par la façade (section 4) |

Un code inconnu ajouté plus tard donne `HackIOException`.

### 6. Tests de contrat

Dans `Tests/HackerOs.AppSdk.Tests/IO/`, avec un faux `IAppExecutionContext` et
le système de fichiers en mémoire de `HackerOs.Tests.Support` :

- chaque méthode publique et chaque ligne du tableau de traduction ;
- le nouvel essai unique sur conflit ;
- l'umask ;
- le contexte absent qui lève une exception ;
- la propagation de l'`AsyncLocal` à travers `await` ;
- la restauration du contexte précédent au `Dispose`.

## Conséquences

- Écrire un fichier tient en une ligne : `await HackFile.WriteAllTextAsync("~/notes.txt", text);`.
- Tout le code d'app doit s'exécuter sous un scope `Enter`. Un oubli dans un point
  d'entrée se voit tout de suite (`HackExecutionContextMissingException`) au lieu
  de s'exécuter avec de mauvais droits.
- Les apps existantes continuent de fonctionner avec `AppContext.FileSystem`. La
  migration des apps de base vers `Hack*` se fait en phase 4.
- La façade ajoute un `StatAsync` avant chaque écriture. C'est acceptable : la
  passerelle était déjà appelée plusieurs fois par écriture dans les apps
  actuelles.
- `HackFileStream` en écriture garde le contenu en mémoire jusqu'à la
  publication, car le navigateur n'a pas de fichier temporaire. Un très gros
  fichier écrit en flux coûte donc sa taille en mémoire.

## Références

- ADR 0008 : modèle du système de fichiers virtuel.
- ADR 0042 : `sudo` et invite d'approbation administrateur.
- [`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), sections 4 (D2 à D5) et 5.1.
- `Shared/HackerOs.Simulation.Abstractions/Gateways/AppGatewayContracts.cs` (`IAppFileSystemGateway`).
- `Shared/HackerOs.Simulation.Abstractions/FileSystem/FileSystemResults.cs` (`FileSystemErrorCode`).
