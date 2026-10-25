# ADR 0055 : Apps de base sur le SDK public uniquement

## Statut

Accepté le 2026-10-10.

- **Phase du plan :** phase 4 — SDK complet et apps de base
  ([`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), section 8.1,
  décision D21, principe directeur 3).
- **Complète :** ADR 0019. Ses règles 1 et 2 (une app ne consomme que le SDK
  public, Platform Core et l'infrastructure navigateur lui restent cachés)
  n'ont jamais été vérifiées : dix projets sous `Apps/` référencent
  `HackerOs.Platform.Core`. Cet ADR les rend vérifiables au build, élargit la
  liste des références permises aux bibliothèques SDK apparues depuis et
  ajoute la règle sur l'injection. Le reste de l'ADR 0019 (versionnement,
  dépréciation) ne change pas.
- **S'appuie sur :** ADR 0016 (MudBlazor permis dans les apps), ADR 0043
  (contexte ambiant et façades statiques `Hack*`), ADR 0048 (liste de
  confiance des apps système), ADR 0049 (capacités normales et dangereuses,
  fenêtre d'approbation, `auth.log`).
- Les API ajoutées au SDK pour remplacer chaque service interne sont décrites
  dans l'ADR 0056.

## Contexte

Le principe 3 du plan veut qu'une app système n'ait rien de plus qu'une app
tierce. Aujourd'hui, c'est faux à trois niveaux.

**Références de projet.** État de `rebuild/base` au 2026-10-10 (dfd9c47) :

| Projet | Référence interdite | Utilisée pour |
|---|---|---|
| `HackerOs.Apps.Settings` | `Platform.Core`, `Infrastructure.Browser` | catalogue, désinstallation, permissions, thème, serveur et synchronisation, effacement d'IndexedDB |
| `HackerOs.Apps.SystemMonitor` | `Platform.Core` | `AppCatalog`, `AppLifecycleOrchestrator` |
| `HackerOs.Apps.FileExplorer` | `Platform.Core` | `AppCatalog`, `IAppEnablementRegistry` |
| `HackerOs.Apps.Diagnostic` | `Platform.Core`, `Infrastructure.Browser` | inspecteur IndexedDB |
| `HackerOs.Apps.Browser` | `Platform.Core` | aucune (seul un `using` inutile) |
| `HackerOs.Apps.Calculator`, `HackPaint`, `IconViewer`, `TextEditor` | `Platform.Core` | aucune |
| `HackerOs.Commands` | `Platform.Core`, `Server.Contracts` | `ShellErrorText` (`cd`), `IServerConnectionService` et `IProxyClient` (`ping`, `curl`, `cat`, `nmap`) |
| `HackerOs.AppSdk.Terminal` (bibliothèque SDK) | `Platform.Core` | `ShellInterpreter`, `IElevationService` |

**Injection.** Même sans référence interdite, une app peut injecter un service
brut de l'OS, car leurs interfaces vivent dans des projets partagés
(`HackerOs.Simulation.Abstractions`, `HackerOs.App.Abstractions`). Ces
services ne vérifient aucune capacité :

| Service injecté | Projet de l'interface | Apps |
|---|---|---|
| `ISessionService` | Simulation.Abstractions | FileExplorer, TextEditor, CodeEditor, Settings, `TerminalPanel` |
| `IProcessManager`, `IEventBus` | Simulation.Abstractions | SystemMonitor |
| `ISimulatedNetworkService` | Simulation.Abstractions | Browser, `ping`, `curl`, `cat`, `nmap` |
| `IElevationService` | App.Abstractions | `TerminalPanel` |
| `IServiceProvider` | .NET | Settings (`AppPermissionsPanel` en tire `AppPermissionAdministration`) |
| `IJSRuntime` pour autre chose qu'importer son module | .NET | Diagnostic (`navigator.clipboard`), Settings (`location.reload`, suppression de la base IndexedDB) |

**Vérification.** Rien n'empêche aujourd'hui d'ajouter une telle dépendance :
aucun test ni aucune étape du build ne contrôle les références ou les
injections d'un projet d'app.

## Décision

### 1. Périmètre

Les règles s'appliquent :

- à tout projet d'app, c'est-à-dire tout projet dont `HackerOsAppProject` vaut
  `true` (projets sous `Apps/`, ADR 0047) ;
- aux bibliothèques du SDK : `Shared/HackerOs.AppSdk` et
  `Shared/HackerOs.AppSdk.*`. Une app ne doit pas pouvoir atteindre l'OS par
  une bibliothèque SDK qui le ferait à sa place.

Les hôtes (`OS/`, `test/`, `Server/`), `Platform/`, `Infrastructure/` et les
projets de tests ne sont pas concernés.

### 2. Références de projet permises

Un projet d'app ne peut référencer que :

| Projet | Rôle |
|---|---|
| `Shared/HackerOs.App.Abstractions` | manifeste, `AppDefinition`, capacités, `VirtualPath` |
| `Shared/HackerOs.Simulation.Abstractions` | types de données (processus, chemins, réseau simulé…) |
| `Shared/HackerOs.Theming.Abstractions` | jetons et définitions de thème |
| `Shared/HackerOs.Game.Abstractions` | contenu de jeu |
| `Shared/HackerOs.AppSdk` et `Shared/HackerOs.AppSdk.*` | le SDK |
| `Platform/HackerOs.Theming.Blazor` | composants de thème (catalogue, aperçu) ; il ne dépend que de `Theming.Abstractions` |
| un autre projet sous `Apps/` | partage de code entre apps |

Toute autre `ProjectReference` fait échouer le build avec **`HOSAPP0002`**, qui
nomme le projet et la référence. C'est le cas notamment de
`Platform.Core`, `Platform.Blazor`, `Windowing.*`, `Taskbar.Blazor`,
`MobileShell.Blazor`, `Infrastructure.*`, `Server.*` (dont
`Server.Contracts`) et des hôtes. Les `PackageReference` restent libres :
un paquet NuGet ne donne pas accès aux services de l'OS.

Une bibliothèque SDK ne peut référencer que les quatre projets
`*.Abstractions` ci-dessus et d'autres bibliothèques SDK. Toute autre référence
échoue avec **`HOSSDK0001`**.

La vérification est une cible MSBuild de `build/HackerOs.Apps.targets`,
exécutée avant la compilation, à côté de la génération du manifeste.

### 3. Injection permise

Dans un projet d'app, un service ne peut être obtenu par injection
(`@inject`, propriété `[Inject]`, paramètre du constructeur d'une classe
d'entrée construite par `ActivatorUtilities`, ADR 0034 section 2) que si son
type est :

1. défini dans une bibliothèque SDK (`HackerOs.AppSdk`, `HackerOs.AppSdk.*`) ;
2. un service MudBlazor (ADR 0016), par exemple `MudBlazor.IDialogService` ;
3. `Microsoft.JSInterop.IJSRuntime`, **uniquement** pour importer un module
   JavaScript de l'app elle-même
   (`import`, `./_content/<projet de l'app>/…`), comme le prévoit
   `AGENTS.md`. Tout autre appel (`navigator.*`, `location.*`, `eval`, module
   d'un autre projet) est interdit.

Tout le reste est interdit, en particulier : les types de
`HackerOs.Simulation.Abstractions` et `HackerOs.App.Abstractions`
(`ISessionService`, `IProcessManager`, `IEventBus`,
`ISimulatedNetworkService`, `IElevationService`, `IAppCatalogRepository`…),
`IServiceProvider`, `ILogger<T>` (le journal passe par
`IAppLoggingGateway`, ADR 0045) et `NavigationManager`.

Ces types restent utilisables comme **données** : une app peut manipuler un
`VirtualPath`, un `ProcessRecord` ou un `AppManifest`. Seule l'obtention d'un
service de l'OS hors du SDK est interdite.

Vérifications :

- **`HOSAPP0003`**, au build : le générateur de manifeste charge déjà
  l'assembly de l'app (ADR 0047). Il parcourt en plus tous ses types et signale
  chaque propriété `[Inject]` et chaque paramètre de constructeur d'une classe
  d'entrée dont le type n'est pas permis. Le build échoue.
- **Test de sources** (`HackerOs.Ecosystem.Tests`) : pour chaque fichier
  `.razor` et `.cs` sous `Apps/`, chaque appel `Invoke*Async` sur un
  `IJSRuntime` doit avoir pour identifiant `"import"` et pour chemin
  `./_content/<nom du projet>/`. Un appel sur le `IJSObjectReference` du module
  importé est libre : c'est le code de l'app.
- Les bibliothèques SDK ne sont pas soumises à `HOSAPP0003`, mais
  `HOSSDK0001` leur interdit de référencer l'implémentation : elles ne peuvent
  donc injecter que des contrats du SDK ou des abstractions. Une revue refuse
  qu'une bibliothèque SDK injecte un service brut de
  `Simulation.Abstractions` pour le rendre à une app.

### 4. Comment une app obtient un service de l'OS

Uniquement par le SDK :

- une **passerelle** sur `IAppExecutionContext`, propre à l'instance, qui
  vérifie la capacité requise à chaque appel ;
- une **façade statique** `Hack*` qui lit le contexte ambiant (ADR 0043) et
  appelle la passerelle. C'est la forme recommandée aux développeurs.

Chaque nouvelle passerelle suit le modèle des passerelles existantes
(`Intents`, `Services`, `Watch`) : membre de l'interface avec une
implémentation par défaut « non prise en charge », pour que les implémentations
existantes de `IAppExecutionContext` compilent encore (ADR 0019). La fabrique de
confiance de Platform Core fournit la vraie implémentation.

Une capacité manquante lève `HackUnauthorizedAccessException` (décision D5) et
passe par `IAppPermissionErrorGateway`, comme l'API fichier. La capacité exigée
par chaque API, et son classement, sont fixés par l'ADR 0056. Une capacité
nouvelle entre dans `AppCapabilities` avec son classement et sa phrase de
risque, comme l'exige l'ADR 0049.

### 5. Agent d'authentification de l'OS

Certaines opérations exigent un administrateur en plus de la capacité de l'app
(désinstaller pour toute la machine, changer la politique de sécurité,
réinitialiser la machine…). Comme `polkit` sous Linux, **l'app ne voit jamais
le mot de passe** :

1. l'app appelle l'API du SDK, sans identifiants ;
2. la passerelle vérifie la capacité, puis demande à l'agent
   d'authentification de l'OS de confirmer un administrateur ;
3. l'agent affiche sa propre fenêtre, dessinée par la plateforme et non par
   l'app, de la même famille visuelle que la fenêtre d'approbation de
   l'ADR 0049 : nom de l'app, opération demandée en clair, identifiant et mot
   de passe d'un administrateur. Dans un terminal, la demande se fait en mode
   texte, comme `sudo` ;
4. la vérification réutilise le vérificateur de `sudo` (ADR 0042) ; le
   résultat est écrit dans `/var/log/auth.log` (app, opération,
   administrateur, issue) ;
5. refus ou annulation : `HackUnauthorizedAccessException`.

Un processus lancé par `sudo` (`IsRootElevated`, ADR 0042) agit déjà en root :
l'agent ne redemande rien. Les API qui passent par l'agent sont marquées
« administrateur » dans l'ADR 0056.

Les mots de passe que l'app doit légitimement saisir pour un service tiers (le
compte du serveur optionnel, par exemple) ne sont pas concernés.

### 6. Transition

Les violations listées dans le contexte ne disparaissent pas en un seul
changement. La cible MSBuild lit donc une liste d'exemptions temporaire,
`HackerOsSdkBoundaryExemption`, déclarée dans `build/HackerOs.Apps.targets` :
un élément par projet encore en défaut, avec l'ADR et le chantier qui le
corrigeront. Un projet exempté produit un avertissement au lieu d'une erreur.

- Chaque chantier qui corrige un projet retire son exemption dans le même
  changement.
- Le dernier chantier de la phase (ADR 0058, `4-sdk-boundary-final`) supprime
  le mécanisme d'exemption.
- Aucun nouveau projet ne peut être ajouté à la liste.

### 7. Apps système et liste de confiance

La liste de confiance de l'hôte (ADR 0048) ne change pas de forme. Une app
système reçoit d'office les capacités dangereuses qu'elle déclare : elle
utilise le même SDK qu'une autre app, mais sans fenêtre d'approbation. Une app
non système qui déclare une capacité dangereuse passe par la fenêtre
d'approbation de l'ADR 0049 à son lancement. Les changements de la liste
qu'entraîne cette phase sont décidés par l'ADR 0056.

## Conséquences

- Une app tierce peut faire tout ce que fait une app système, avec les mêmes
  API et les mêmes capacités ; seule l'approbation diffère.
- Le build attrape une dépendance interdite dès qu'elle est ajoutée, au lieu
  d'une revue.
- Les commandes du réseau, le terminal et l'app Paramètres perdent leurs accès
  directs : la logique de repli sur le serveur, de jeton d'accès et de
  vérification d'administrateur passe dans Platform Core, derrière le SDK.
- L'architecture est un peu plus lourde côté plateforme : chaque besoin d'une
  app système devient une passerelle à écrire et à tester.

## Alternatives écartées

- **Test d'architecture seulement sur `Platform.Core` et `Platform.Blazor`**
  (texte du plan, section 8.1) : il laisse passer `Infrastructure.Browser`,
  `Server.Contracts` et l'injection des services de
  `Simulation.Abstractions`, qui sont la majorité des violations réelles.
- **Déplacer les interfaces de services hors de `Simulation.Abstractions`**
  pour que les apps ne puissent plus les nommer : correct à terme, mais ce
  projet est aussi consommé par le serveur et par Platform Core ; le
  déplacement toucherait de nombreux fichiers sans rien ajouter à la
  vérification par type de `HOSAPP0003`.
- **Analyseur Roslyn** au lieu de la cible MSBuild et du générateur : plus
  précis pour l'appel `IJSRuntime`, mais un projet de plus à maintenir ; le
  générateur charge déjà l'assembly.

## Références

- [`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), sections 3 et 8.1.
- ADR 0016, 0019, 0034, 0042, 0043, 0047, 0048, 0049 ; ADR 0056 (API),
  ADR 0058 (chantiers).
- `build/HackerOs.Apps.targets`, `Tools/HackerOs.Tools.ManifestGenerator`,
  `Shared/HackerOs.AppSdk/IAppExecutionContext.cs`.
