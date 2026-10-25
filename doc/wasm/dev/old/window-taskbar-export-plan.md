# Plan d’extraction du système de fenêtres et de la barre des tâches

**Statut :** proposition d’architecture et plan de reprise  
**Cible immédiate :** réutilisation par plusieurs hôtes de `HackerOs.sln`  
**Cible future :** publication sous forme de packages NuGet versionnés  
**Code concerné :** `wasm2/HackerOs/Platform/HackerOs.Platform.Blazor/`

## 1. Objectif

Extraire le moteur de fenêtres, ses composants Blazor et la barre des tâches de
leur projet actuel afin qu’ils puissent être consommés par une autre application
de la solution sans dépendre du projet hôte `HackerOs.Ecosystem`.

L’extraction doit produire des projets utilisables immédiatement par références
de projets. Leurs API publiques, dépendances et ressources statiques doivent
toutefois être conçues dès maintenant pour permettre une publication NuGet
ultérieure sans réécriture majeure.

Le résultat ne doit pas exporter l’ensemble de HackerOS. Une application
consommatrice doit pouvoir utiliser le système de fenêtres et la barre des tâches
en fournissant ses propres sources d’applications, commandes de cycle de vie,
icônes, horloge, notifications et stratégie de thème.

## 2. État actuel et couplages à traiter

Le code actuel est principalement regroupé dans
`Platform/HackerOs.Platform.Blazor` :

- `Windows/WindowRuntime.cs` possède l’état et les transitions des fenêtres ;
- `Windows/WindowRuntimeState.cs` contient l’identité, la géométrie, le focus,
  l’ordre Z, la modalité et les contraintes ;
- `Windows/DesktopArea.razor`, `WindowHost.razor` et `WindowChrome.razor`
  effectuent le rendu Blazor ;
- `Shell/Taskbar.razor` injecte directement `WindowRuntime` et d’autres services
  HackerOS ;
- `Shell/DesktopShell.razor` compose fenêtres, taskbar, lanceur, notifications et
  session ;
- `WindowCloseCoordinator` dépend du cycle de vie applicatif HackerOS ;
- `WindowAppRenderer` dépend de `WindowAppBase`, `AppDescriptor` et du contexte
  applicatif HackerOS ;
- les identifiants de fenêtres référencent actuellement les identités de processus
  et d’instances de la simulation.

Le moteur headless est déjà bien séparé du DOM, mais son modèle d’identité et les
composants de shell restent liés au domaine HackerOS. La barre des tâches est plus
fortement couplée que le moteur de fenêtres et doit passer par des contrats
d’adaptation.

### 2.1 Changement récent : contenu générique et dialogues (2026-08)

Un couplage supplémentaire, absent de la rédaction initiale de ce document, existe
dans `Platform/HackerOs.Platform.Blazor/Dialogs/` : `FileDialogCoordinator`,
`DialogCoordinator` et `FileDialogWindowAdapter` créent des fenêtres owner-modal via
`WindowRuntime.Apply(new CreateWindowCommand(...))`, exactement comme
`WindowLaunchCoordinator` le fait pour les applications. Ces dialogues (sélection de
fichier, boîtes de message, saisie de texte) restent spécifiques à HackerOS
(système de fichiers virtuel, capacités) et ne font pas partie du périmètre exporté.

Ce couplage a toutefois motivé une extension générique déjà en place :
`WindowRuntimeState` porte désormais `Content` (`RenderFragment?`) et
`OnRequestClose` (`Func<Task>?`). `DesktopShell.razor` ne connaît plus les types de
dialogues concrets ; il rend `window.Content` lorsqu’il est fourni, et route la
fermeture demandée par l’utilisateur vers `window.OnRequestClose` quand il existe. Ce
mécanisme générique — pas les dialogues eux-mêmes — fait partie du moteur exporté (voir
3.3) : n’importe quel hôte peut désormais créer une fenêtre dont le contenu et la
logique de fermeture sont fournis par l’appelant, sans que le moteur ait besoin d’un
cas spécial par type de fenêtre.

## 3. Architecture cible

### 3.1 Projets proposés

Créer les projets suivants sous `wasm2/HackerOs/` :

```text
Shared/
  HackerOs.Windowing.Abstractions/
Platform/
  HackerOs.Windowing.Core/
  HackerOs.Windowing.Blazor/
  HackerOs.Taskbar.Blazor/
  HackerOs.Platform.Blazor/          # composition/adaptateurs HackerOS existants
Tests/
  HackerOs.Windowing.Core.Tests/
  HackerOs.Windowing.Blazor.Tests/
  HackerOs.Taskbar.Blazor.Tests/
Samples/
  HackerOs.Windowing.SampleHost/     # consommateur sans HackerOs.Ecosystem
```

Les noms pourront être ajustés avant création, mais les frontières de dépendances
doivent être conservées.

`HackerOs.sln` est un fichier `.sln` classique (pas au format XML `slnx`) : chaque
nouveau projet doit être ajouté explicitement via `dotnet sln add`, avec une entrée
`GlobalSection(ProjectConfigurationPlatforms)` et, si on le range dans un dossier de
solution existant (`Shared`, `Platform`, `Tests`, `Samples`), une entrée
`GlobalSection(NestedProjects)`. Aucune découverte automatique n’a lieu.

### 3.2 `HackerOs.Windowing.Abstractions`

Bibliothèque .NET sans dépendance Blazor, MudBlazor, navigateur ou hôte HackerOS.

Elle doit contenir :

- `WindowId` et les identifiants d’owner génériques ;
- `WindowBounds`, `WindowConstraints` et `WindowVisualState` ;
- les descriptions immuables de titre, icône et contenu logique — le contenu logique
  est un `RenderFragment` fourni par l’appelant (voir 2.1 et 3.3), pas un contrat
  opaque : la librairie exportée est de toute façon toujours consommée depuis Blazor,
  donc ce type n’ajoute aucun couplage réel à un hôte non-Blazor ;
- les commandes et événements de fenêtres ;
- les contrats de fermeture, confirmation et activation ;
- les contrats de source d’éléments pour la barre des tâches ;
- les contrats de commandes de barre : activer, réduire, restaurer, fermer,
  afficher l’accueil ou ouvrir une surface fournie par l’hôte ;
- aucun modèle de thème hôte. Depuis la mise à jour du 2026-08-19, le catalogue
  framework-neutral vit dans `HackerOs.Theming.Abstractions` et le vocabulaire
  visuel/static asset dans `HackerOs.Theming.Blazor`; les packages de chrome
  consomment uniquement les tokens `--hos-*`, jamais du CSS arbitraire.

Le projet `Shared/HackerOs.AppSdk.Icons` existe déjà et fournit un modèle d’identité
d’icône indépendant du rendu ; les contrats d’icônes de fenêtre et de barre des tâches
doivent le réutiliser plutôt que d’en recréer un.

Les identités propres à HackerOS (`ProcessId`, `AppInstanceId`, `AppId`) ne doivent
pas être obligatoires dans le moteur exportable. Utiliser un identifiant de
propriétaire opaque validé, puis fournir un adaptateur HackerOS qui conserve les
relations processus/application existantes.

### 3.3 `HackerOs.Windowing.Core`

Bibliothèque headless contenant :

- `WindowRuntime` ;
- l’application atomique des commandes ;
- le focus et l’ordre Z ;
- déplacement, redimensionnement et contraintes ;
- minimisation, maximisation et restauration ;
- modalité et relation owner/enfant ;
- adaptation au changement de work area ;
- état sérialisable facultatif de géométrie ;
- événements déterministes observables par l’hôte.

Elle dépend de `HackerOs.Windowing.Abstractions` et peut référencer
`Microsoft.AspNetCore.Components` (uniquement pour le type délégué `RenderFragment`
porté par `WindowRuntimeState.Content`, cf. 2.1 et 3.2). Elle ne doit contenir aucun
composant `.razor`, aucun `RenderFragment` produit par le moteur lui-même, aucun
MudBlazor et aucune interop JavaScript : le moteur reste headless, il consomme
seulement le contenu que l’appelant lui fournit sans jamais le construire.

### 3.4 `HackerOs.Windowing.Blazor`

Razor Class Library contenant :

- `DesktopArea.razor` ;
- `WindowHost.razor` ;
- `WindowChrome.razor` ;
- les fichiers `.razor.css` associés ;
- les modules `.razor.js` strictement nécessaires aux Pointer Events, à la
  projection de géométrie et à la gestion du focus ;
- un composant racine paramétrable, par exemple `WindowSurface`, qui reçoit un
  runtime et un fragment de rendu de contenu ; **implémenté**, voir la note
  sous `EXT-WIN-005` en section 10 ;
- des abstractions d’icônes et de chrome permettant au consommateur de choisir son
  rendu sans imposer MudBlazor.

Le package doit embarquer ses ressources via les mécanismes standards de Razor
Class Library (`_content/{PackageId}/...`). Aucun chemin ne doit dépendre du nom du
projet hôte.

### 3.5 `HackerOs.Taskbar.Blazor`

Razor Class Library séparée afin qu’un consommateur puisse utiliser le moteur de
fenêtres sans adopter la barre des tâches HackerOS.

La taskbar doit recevoir des interfaces plutôt que d’injecter directement :

- `WindowRuntime` concret ;
- `AppCatalog` ;
- `ISimulationClock` ;
- `INotificationQueue` ;
- `ISessionService` ;
- `AppIntentDispatcher`.

Contrats proposés :

- `ITaskbarWindowSource` : instantané ordonné des fenêtres affichables ;
- `ITaskbarCommandDispatcher` : activation, réduction, restauration et fermeture ;
- `ITaskbarLauncher` : ouverture du lanceur fourni par l’hôte ;
- `ITaskbarStatusSource` : heure, réseau, batterie simulée ou états personnalisés ;
- `ITaskbarNotificationSource` : compteur et ouverture du centre de notifications ;
- `ITaskbarSessionCommands` : verrouillage, déconnexion ou arrêt facultatifs ;
- `TaskbarOptions` : visibilité des zones, ordre, densité et labels.

Chaque fonctionnalité optionnelle doit disparaître proprement lorsque son contrat
n’est pas fourni. La taskbar ne doit pas créer un faux service global pour
remplacer une intégration absente.

**Implémenté** avec `RequestLogout()` seul sur `ITaskbarSessionCommands`
(verrouillage/arrêt non exposés faute de consommateur réel — à ajouter quand un
hôte en a besoin, pas avant) et `TaskbarOptions` limité aux libellés du lanceur
(`LauncherMark`/`LauncherLabel`, seul texte HackerOS actuellement codé en dur) ;
la visibilité de zone est déjà couverte par l’absence/présence de chaque contrat,
donc pas dupliquée dans les options.

**Piège de compilation Razor à retenir** pour toute future Razor Class Library
exportée (Phase D comprise) : un projet `Microsoft.NET.Sdk.Razor` qui référence
le paquet `Microsoft.AspNetCore.Components.Web` mais dont le `_Imports.razor`
n’a **pas** `@using Microsoft.AspNetCore.Components.Web` compile sans aucune
erreur ni avertissement, mais Razor ne reconnaît alors aucun `@onclick` (ni les
autres événements DOM) comme liaison d’événement : il les rend comme attribut
HTML littéral (`@onclick="..."` visible tel quel dans le DOM), et les clics ne
font donc rien, silencieusement. `HackerOs.Windowing.Blazor` (3.4) avait ce
`@using` ; `HackerOs.Taskbar.Blazor` ne l’avait pas à la création et est resté
casse pendant tout le développement du composant jusqu’à ce que ce soit
diagnostiqué au navigateur. Vérifier systématiquement ce `@using` dans le
`_Imports.razor` de toute nouvelle RCL, et confirmer par un test au navigateur
qu’un clic produit un effet observable, pas seulement que le build réussit.

### 3.6 `HackerOs.Platform.Blazor`

Le projet existant devient la couche de composition HackerOS :

- implémentation des adaptateurs entre les contrats exportables et
  `AppCatalog`/processus/sessions/notifications ;
- `WindowAppRenderer` et validation de `WindowAppBase` ;
- `WindowCloseCoordinator` relié à `AppLifecycleOrchestrator` ;
- shell Desktop et Mobile propres à HackerOS ;
- lanceur d’applications, dialogues, centre de notifications et UX de session.

Ainsi, les packages exportables ne connaissent pas le modèle complet de l’OS.

Vérifié dans le code actuel : `WindowCloseCoordinator` dépend de
`AppLifecycleOrchestrator` (`HackerOs.Platform.Core.Lifecycle`) et
`WindowAppRenderer` dépend de `AppDescriptor`
(`HackerOs.Platform.Core.Discovery`) ainsi que de `WindowAppBase`/
`IWindowCloseGuard` (`HackerOs.AppSdk.Blazor`). Les deux confirment donc bien la
frontière ci-dessus et restent dans `HackerOs.Platform.Blazor` sans modification
de signature publique lors de l’extraction. Le système de dialogues (2.1) suit la
même règle : `FileDialogWindowAdapter` reste dans `HackerOs.Platform.Blazor` et
n’utilise que le mécanisme générique `Content`/`OnRequestClose` du moteur exporté.

## 4. API de consommation attendue

Le consommateur interne doit pouvoir enregistrer les services sans connaître
l’implémentation :

```csharp
builder.Services.AddHackerOsWindowing(options =>
{
    options.InitialWorkArea = new WindowBounds(0, 0, 1280, 720);
});

builder.Services.AddHackerOsTaskbar();
```

La composition Razor doit rester simple :

```razor
<WindowSurface Runtime="WindowRuntime"
               WindowContent="RenderWindowContent" />

<HackerTaskbar WindowSource="TaskbarWindowSource"
               Commands="TaskbarCommands"
               Options="TaskbarOptions" />
```

Ces exemples représentent l’ergonomie recherchée. Les noms définitifs doivent
être choisis après la revue de l’API publique.

## 5. Stratégie de migration sans rupture

1. Copier d’abord les contrats génériques dans les nouveaux projets sans supprimer
   les types existants.
2. Écrire les tests de contrat du moteur extrait.
3. Faire utiliser le nouveau moteur par des adaptateurs conservant les signatures
   HackerOS actuelles.
4. Déplacer les composants Razor et leurs assets collocatés.
5. Remplacer les chemins d’import JS par les chemins `_content` du nouveau projet.
6. Extraire la taskbar après stabilisation du moteur et introduire ses sources de
   données abstraites.
7. Migrer `DesktopShell` vers les nouveaux composants.
8. Ajouter un hôte exemple qui ne référence ni `HackerOs.Ecosystem` ni les projets
   d’applications HackerOS.
9. Supprimer les anciens types seulement après réussite de tous les tests et
   consommateurs.
10. Conserver, si nécessaire, une façade obsolète pendant une version afin de
    faciliter les migrations internes.

## 6. Préparation à NuGet

Chaque projet destiné à être publié doit définir :

- `PackageId`, `Version`, `Authors`, `Description`, `PackageTags` et licence ;
- génération du package et des symboles ;
- documentation XML publique sans avertissement ;
- `README` inclus dans le package ;
- dépendances minimales avec versions compatibles explicites ;
- déterminisme de build et Source Link ;
- ressources statiques Razor correctement empaquetées ;
- compatibilité de trimming documentée et testée ;
- politique SemVer et baseline de compatibilité API.

Ne pas publier immédiatement. Produire d’abord les `.nupkg` localement et les
consommer depuis une source NuGet temporaire dans le sample host. Cela détectera
les dépendances transitives ou assets qui fonctionnent par référence de projet
mais manquent dans un package.

## 7. Thème et personnalisation

- Les composants exportables fournissent des valeurs de repli accessibles.
- Les couleurs, espacements, tailles, rayons et animations utilisent un ensemble
  documenté de variables CSS préfixées.
- Le consommateur surcharge les tokens dans sa feuille globale ; il ne fournit pas
  de texte CSS exécutable au composant.
- Le chrome et la taskbar exposent des fragments ou contrats d’icônes bornés.
- Les fonctionnalités de base restent utilisables sans MudBlazor.
- Si une variante MudBlazor est conservée, elle appartient à un package adaptateur
  facultatif, pas au moteur ou aux abstractions.

## 8. Tests requis

### Tests headless

- création et suppression ;
- focus unique et ordre Z ;
- déplacement et huit directions de redimensionnement ;
- contraintes min/max et changement de viewport ;
- minimisation, maximisation et restauration ;
- owner/modal, fermeture et restauration du focus ;
- ordre déterministe des événements ;
- séparation entre identité générique et métadonnées HackerOS.

### Tests de composants

- rendu du contenu fourni par un consommateur ;
- navigation clavier complète ;
- labels accessibles et réduction des animations ;
- taskbar avec toutes les sources facultatives présentes ou absentes ;
- adaptation du thème par tokens ;
- absence de CSS/JS inline dans Razor.

### Tests navigateur et packaging

- drag/resize mouse, touch et pen ;
- focus et ordre Z sans course de rendu ;
- import des modules depuis `_content` ;
- console et réseau sans erreur ;
- application exemple consommant les projets ;
- application exemple consommant les `.nupkg` locaux ;
- publication Release et analyse de trimming.

## 9. Risques à contrôler

- **Fuite du domaine HackerOS :** éviter que les types de processus, catalogue ou
  session deviennent des dépendances obligatoires du package.
- **API trop générique :** conserver des contrats orientés fenêtres et taskbar,
  sans construire un framework UI universel.
- **Assets manquants dans NuGet :** tester les packages produits, pas seulement les
  références de projets.
- **Interop fragile :** rendre l’initialisation framework incontournable et la
  destruction déterministe.
- **Double état :** `WindowRuntime` reste l’unique source de vérité.
- **MudBlazor transitif :** isoler ou supprimer cette dépendance dans les packages
  génériques.
- **Rupture mobile :** ne pas figer dans le moteur des hypothèses propres aux
  fenêtres flottantes Desktop. Le plan mobile est défini dans
  `docs/mobile-interface-platform-plan.md`.

## 10. Plan d’implémentation

- [x] `EXT-WIN-001` Créer `HackerOs.Windowing.Abstractions` et documenter son API
  publique. **Complété 2026-08-17** (suivi tardif, après la Phase D) : le
  report initial ("les contrats génériques vivent dans `HackerOs.Windowing.Core`
  ... le split proprement dit reste à faire avant la Phase B") n'avait jamais
  été refermé alors que les Phases B à D s'étaient déjà terminées sans lui.
  `WindowId`, `WindowOwnerId`, `WindowBounds`, `WindowConstraints`,
  `WindowVisualState`, `WindowModality`, `WindowRuntimeState`, et toutes les
  commandes/événements (`WindowCommand`/`WindowEvent` et leurs types dérivés)
  sont déplacés dans le nouveau projet `Platform/HackerOs.Windowing.Abstractions/`
  (mêmes noms de types, namespace `HackerOs.Windowing.Abstractions` au lieu de
  `HackerOs.Windowing.Core` — changement non rétrocompatible mais sans risque
  réel puisque `PublicAPI.Shipped.txt` était vide pour les deux projets, donc
  rien n'avait encore été publié sous SemVer). `HackerOs.Windowing.Core` ne
  contient plus que `WindowRuntime` (le moteur) et référence Abstractions par
  `ProjectReference`. Les 30 fichiers consommateurs (`HackerOs.Windowing.Blazor`,
  `HackerOs.Taskbar.Blazor`, `HackerOs.Platform.Blazor`, le sample host, et
  tous les projets de tests concernés) ont reçu le `using`/`@using` supplémentaire
  nécessaire. `dotnet build HackerOs.sln` en Debug et en Release passe à 0
  avertissement/0 erreur (y compris les baselines `PublicApiAnalyzers`
  régénérées via `dotnet format analyzers --diagnostics RS0016`), et
  `dotnet test` sur `HackerOs.Windowing.Core.Tests`, `HackerOs.Taskbar.Blazor.Tests`,
  `HackerOs.Platform.Blazor.Tests` et `HackerOs.Ecosystem.Tests` passe
  intégralement. Vérifié au navigateur : le sample host et les scénarios
  `window`/`taskbar` du harness fonctionnent identiquement, sans erreur
  console.
- [x] `EXT-WIN-002` Découpler les identités génériques des identités de processus
  HackerOS. `WindowRuntimeState` porte désormais `WindowOwnerId` (opaque,
  dérivé de l'instance d'app) au lieu de `ProcessId`/`AppInstanceId`.
- [x] `EXT-WIN-003` Déplacer le moteur headless dans `HackerOs.Windowing.Core`.
  Le projet ne référence plus que `Microsoft.AspNetCore.Components`, aucune
  dépendance HackerOS.
- [x] `EXT-WIN-004` Porter les tests du runtime et ajouter une suite de contrats
  indépendante de HackerOS. `HackerOs.Windowing.Core.Tests` (11 tests) ne
  référence que `HackerOs.Windowing.Core`.
- [x] `EXT-WIN-005` Créer la Razor Class Library `HackerOs.Windowing.Blazor` avec
  assets collocatés. `DesktopArea.razor`, `WindowHost.razor` et
  `WindowChrome.razor` (+ `.razor.css`/`.razor.js`) déplacés depuis
  `Platform.Blazor`, servis depuis `_content/HackerOs.Windowing.Blazor/...`.
  `WindowSurface` (voir 3.4) est **implémenté 2026-08-17** : composant
  paramétrable qui reçoit `Runtime` (un `WindowRuntime`) et `WindowContent`,
  s'abonne lui-même à `Runtime.StateChanged`, et traduit les callbacks
  `OnFocus`/`OnMinimize`/`OnToggleMaximize`/`OnClose`/`OnGesture` de
  `DesktopArea` en appels `Runtime.Apply(...)` — exactement le boilerplate que
  `HackerOs.Windowing.SampleHost/App.razor` réimplémentait à la main jusque-là.
  Le sample host est migré vers `<WindowSurface Runtime="WindowRuntime">` pour
  prouver une consommation réelle, pas seulement la compilation ; vérifié au
  navigateur : ouverture par le launcher, focus, interaction (incrément),
  maximize/restore, minimize/restore depuis la taskbar, et close fonctionnent
  tous sans erreur console. `DesktopShell` (l'hôte HackerOS complet) n'est
  délibérément pas migré vers `WindowSurface` — il gère aussi les dialogues
  (`Content`/`OnRequestClose`) et la fermeture avec garde/confirmation via
  `WindowCloseCoordinator`, deux préoccupations hors du périmètre minimal que
  `WindowSurface` couvre volontairement ; le forcer y introduirait une rupture
  sans bénéfice réel, contrairement à l'esprit "migration sans rupture" de la
  section 5.
- [x] `EXT-WIN-006` Éliminer les dépendances MudBlazor obligatoires du chrome
  exportable. `WindowChrome.razor` n'utilise plus `MudIconButton` ; boutons
  natifs + icônes SVG scoped-CSS. Vérifié au navigateur : minimiser,
  maximiser/restaurer, fermer et le geste de déplacement pointeur
  fonctionnent identiquement.
- [x] `EXT-WIN-007` Définir les contrats de source et de commandes de taskbar.
  `ITaskbarWindowSource`, `ITaskbarCommandDispatcher`, `ITaskbarLauncher`,
  `ITaskbarStatusSource`, `ITaskbarNotificationSource`,
  `ITaskbarSessionCommands`, `TaskbarOptions` dans `HackerOs.Taskbar.Blazor`.
- [x] `EXT-WIN-008` Créer `HackerOs.Taskbar.Blazor` et ses tests de composants.
  `Taskbar.razor` ne reçoit plus que ces contrats (tous nullables) au lieu
  d'injecter `WindowRuntime`/`ISimulationClock` ; chaque zone optionnelle
  disparaît proprement quand son contrat n'est pas fourni (vérifié au
  navigateur pour les cinq). `HackerOs.Taskbar.Blazor.Tests` couvre la
  logique d'interaction pure (`TaskbarWindowInteraction`, 4 tests). Piège
  rencontré, voir note dans la section 3.5 ci-dessous.
- [x] `EXT-WIN-009` Implémenter les adaptateurs HackerOS dans
  `HackerOs.Platform.Blazor`. Six classes dans `Shell/TaskbarAdapters.cs` :
  `TaskbarWindowSourceAdapter`, `TaskbarCommandDispatcherAdapter` (route
  `Close` par le même mécanisme de close-guard que `DesktopShell`),
  `TaskbarLauncherAdapter`/`TaskbarNotificationSourceAdapter` (possèdent
  eux-mêmes leur état ouvert/fermé pour que le panneau du shell et le
  déclencheur de la taskbar restent synchronisés quel que soit lequel des
  deux a changé l'état), `TaskbarStatusSourceAdapter` (tick chaque seconde
  simulée), `TaskbarSessionCommandsAdapter`.
- [x] `EXT-WIN-010` Migrer `DesktopShell` sans modifier son comportement public.
  L'ancien `Platform.Blazor/Shell/Taskbar.razor` est supprimé (plus aucune
  référence). Vérifié entièrement au navigateur contre l'hôte HackerOS
  complet : lanceur, sélection d'appli, minimiser/restaurer par la taskbar,
  notifications, touche Échap, fermeture de fenêtre, déconnexion, geste de
  déplacement pointeur. Un vrai bug trouvé par cette vérification (pas par
  la compilation) : `OpenLogout()` invoqué via un appel de méthode brut sur
  l'adaptateur au lieu d'un `EventCallback` Blazor ne déclenchait plus le
  nouveau rendu automatique du parent ; corrigé par un appel explicite.
- [x] `EXT-WIN-011` Créer un sample host interne sans dépendance à
  `HackerOs.Ecosystem`. `Samples/HackerOs.Windowing.SampleHost` ne référence
  que la fermeture desktop exportable : `HackerOs.Theming.Abstractions`,
  `HackerOs.Theming.Blazor`, `HackerOs.Windowing.Abstractions`,
  `HackerOs.Windowing.Core`, `HackerOs.Windowing.Blazor`,
  `HackerOs.AppSdk.Icons` et `HackerOs.Taskbar.Blazor` ; sa propre source
  d'applications (deux
  composants Blazor triviaux), ses propres adaptateurs de taskbar, et
  n'active ni notifications ni commandes de session (zones absentes
  proprement, vérifié au navigateur). Confirme que le moteur/chrome/taskbar
  exportés sont utilisables sans aucun type HackerOS.
- [x] `EXT-WIN-012` Ajouter les métadonnées de packaging et produire des packages
  NuGet locaux. `Platform/Packaging.props` est importé par les sept projets de
  la fermeture desktop : les quatre packages Windowing/Taskbar historiques,
  `HackerOs.Theming.Abstractions`, `HackerOs.Theming.Blazor`, ainsi que
  `HackerOs.AppSdk.Icons` (dépendance publique de Taskbar). Il fixe
  `Version=0.1.0-local`, `Authors`, `RepositoryUrl`/`RepositoryType`,
  `PackageTags`, `PackageLicenseFile`/`PackageReadmeFile`,
  `GenerateDocumentationFile=true`, symboles `.snupkg`, et SourceLink GitHub
  (`EmbedUntrackedSources`). Chaque projet ajoute son `PackageId` et sa
  `Description`. Un `README.md` existe dans chacun des sept dossiers de
  projet. `dotnet pack -c Release -o artifacts/local-nupkg` produit
  les sept packages dans l'ordre topologique Theme contracts, Theme Blazor,
  Windowing abstractions, Windowing core, Windowing Blazor, Icons, Taskbar
  (`.nupkg` + `.snupkg`) sans avertissement, y
  compris sous `TreatWarningsAsErrors=true` avec doc XML générée. L'ajout
  d'Icons à la fermeture a aussi complété les six commentaires XML publics
  manquants de `IconCatalog`, sans changement comportemental.
  `artifacts/` et `*.nupkg` sont déjà couverts par le `.gitignore` du dépôt.
- [x] `EXT-WIN-013` Tester un sample consommant uniquement les packages locaux.
  `Samples/HackerOs.Windowing.SampleHost/nuget.config` déclare une source
  locale (`<clear/>` + `nuget.org` + `../../artifacts/local-nupkg`). Le csproj
  conserve les `ProjectReference` par défaut pour le développement, mais
  `-p:UseLocalHackerOsPackages=true` les remplace par sept
  `PackageReference Version="0.1.0-local"`. Le workflow dédié
  `.github/workflows/window-taskbar-packages.yml` packe la fermeture dans
  l'ordre topologique, restaure le sample dans un cache NuGet isolé, vérifie
  que `project.assets.json` ne contient aucune dépendance de type `project`,
  puis build et publie le sample en Release.
  Vérifié au navigateur (harness `hackeros-windowing-sample-host`, port 5254) :
  aucune erreur console, rendu identique (launcher + horloge, sans
  notifications/session comme prévu), et surtout les assets statiques
  packagés de la RCL (`DesktopArea.razor.js`, `WindowHost.razor.js`,
  `WindowChrome.razor.js`) se chargent en `200 OK` depuis
  `_content/HackerOs.Windowing.Blazor/...` — preuve historique que le contenu
  `staticwebassets/` du `.nupkg` fonctionne à l'exécution, pas seulement par
  coïncidence de référence de projet. Le workflow vérifie en plus que
  `_content/HackerOs.Theming.Blazor/themes.css` est physiquement présent dans
  la publication package-only. Le geste de déplacement de fenêtre par
  pointeur (`pointerdown`/`pointermove`/`pointerup` synthétiques) a aussi été
  rejoué avec succès contre les assets packagés, avec un delta exact
  (`+60,+30`). Le mode par défaut reste `ProjectReference` (ergonomie de
  développement normale dans le dépôt) ; `nuget.config` et le mode
  conditionnel permettent de rejouer la vérification sans éditer le csproj.
  `HackerOs.MobileShell.Blazor` est package-ready et partage le thème, mais
  reste volontairement hors de cette fermeture/test **desktop** EXT-WIN-013.
- [x] `EXT-WIN-014` Ajouter baseline API, tests Release/trimming et documentation
  de versionnement. `Microsoft.CodeAnalysis.PublicApiAnalyzers` (3.3.4) est
  référencé par les sept projets de la fermeture via `Packaging.props`. Chaque projet a
  ses `PublicAPI.Shipped.txt` (vide — rien n'a encore été publié) et
  `PublicAPI.Unshipped.txt` (surface publique actuelle, générée avec
  `dotnet format analyzers --diagnostics RS0016 --include-generated`, seul
  moyen de faire remonter aussi les membres générés par le compilateur Razor
  comme `OnAfterRenderAsync`/`DisposeAsync`/les accesseurs de `[Parameter]` —
  sans ce drapeau l'outil ignore silencieusement le code généré). RS0041
  (« type référence inconscient ») est désactivé via `NoWarn` dans
  `Packaging.props` uniquement pour les surcharges `BuildRenderTree`
  générées par Razor, qui n'ont pas de contexte `#nullable enable` et ne
  peuvent pas être corrigées à la main sans casser à chaque montée de
  version du SDK Razor. Toute évolution future de la surface publique de ces
  sept projets fera échouer le build (RS0016/RS0017) tant que
  `PublicAPI.Unshipped.txt` n'est pas mis à jour — comportement voulu.
  Preuve Release/trimming : `dotnet publish
  Samples/HackerOs.Windowing.SampleHost -c Release` termine sans le moindre
  avertissement (aucun `warn`/`IL####` dans la sortie complète), l'IL Linker
  tourne (« Optimisation des assemblages pour la taille ») et les sept
  assemblages de la fermeture exportée apparaissent
  bien recadrés dans `wwwroot/_framework/` aux côtés du sample lui-même.
  **Revérifié 2026-08-17** après l'ajout d'`HackerOs.Windowing.Abstractions`,
  puis étendu le 2026-08-19 : le workflow produit maintenant les sept
  `.nupkg`/`.snupkg` de la fermeture desktop sans avertissement.
- [x] `EXT-WIN-015` Mettre à jour la solution, les documents d’architecture et la
  liste d’intégration. `HackerOs.sln` contient déjà les 6 projets exportés/tests/
  sample depuis les phases précédentes (aucun ajout requis ici). Mis à jour :
  `docs/window-runtime.md` (retire l'affirmation obsolète « pas de RenderFragment »
  et « couplé à ProcessId/AppInstanceId », documente `HackerOs.Windowing.Core`,
  `WindowOwnerId`, et le `RenderFragment? Content` approuvé) ; `docs/desktop-shell.md`
  (remplace la description de l'ancien `Shell/Taskbar.razor` par
  `HackerOs.Taskbar.Blazor.Taskbar` + `Shell/TaskbarAdapters.cs`) ;
  `docs/integration-task-list.md` sections 11 et 14 (note de relocalisation
  pointant vers ce document, historique `P2-WIN-*`/`P2-SHELL-002` conservé tel
  quel conformément à la règle « ne jamais supprimer une tâche »).
  `dotnet build HackerOs.sln -c Debug` termine à 0 avertissement/0 erreur après
  chacune des sections `EXT-WIN-012` à `014` (métadonnées de packaging,
  `Microsoft.CodeAnalysis.PublicApiAnalyzers`, RS0041 en `NoWarn`).
  **Mis à jour 2026-08-17** (`EXT-WIN-001`) : `HackerOs.sln` contient
  désormais un 7e projet exporté, `HackerOs.Windowing.Abstractions`, ajouté
  via `dotnet sln add ... -s Platform` (donc avec les entrées
  `ProjectConfigurationPlatforms` et `NestedProjects` correctement générées,
  pas éditées à la main) ; `dotnet build HackerOs.sln -c Debug` et
  `-c Release` terminent tous deux à 0 avertissement/0 erreur.

## 11. Définition de complétion

L’extraction est terminée lorsque :

- [x] une application de la solution utilise fenêtres et taskbar sans référencer
  `HackerOs.Ecosystem` (`Samples/HackerOs.Windowing.SampleHost`, `EXT-WIN-011`) ;
- [x] le moteur headless ne dépend ni de Blazor ni des services HackerOS
  (`HackerOs.Windowing.Core` : zéro référence de projet, seul
  `Microsoft.AspNetCore.Components` pour le type délégué `RenderFragment`) ;
- [x] la taskbar consomme uniquement ses contrats publics
  (`HackerOs.Taskbar.Blazor`, `EXT-WIN-007`/`008`) ;
- [x] HackerOS conserve son comportement via des adaptateurs explicites
  (`Shell/TaskbarAdapters.cs`, vérifié au navigateur sans régression, `EXT-WIN-009`/`010`) ;
- [x] les tests headless, composants et navigateur passent (`dotnet test HackerOs.sln`
  et vérifications `HackerOs.BrowserHarness.Tests` à chaque phase). Un test E2E
  Playwright permanent manquait pour le scénario `taskbar` (vérifié seulement à
  la main via le navigateur pendant la Phase C) : ajouté
  `Taskbar_reacts_to_contracts_and_hides_optional_zones_cleanly` dans
  `Tests/HackerOs.E2E.Tests/IndexedDbBrowserContractTests.cs`, qui exerce le
  cycle background→focused→minimized→focused par clic, l'ajout dynamique de
  fenêtre, notification et horloge poussées sans commande du taskbar, le
  launcher, la déconnexion, et la disparition/réapparition propre de chacune
  des 5 zones optionnelles quand son contrat est retiré ; `taskbar` a aussi été
  ajouté à la liste de scénarios du test d'accessibilité axe-core. Les deux
  suites `HackerOs.E2E.Tests` (15/15) et `HackerOs.UI.E2E.Tests` (4/4) passent
  intégralement (nécessite un build Release préalable de
  `Tests/HackerOs.BrowserHarness.Tests` et `test/test`, que ces suites lancent
  elles-mêmes via `dotnet run --no-build -c Release`) ;
- [x] les ressources statiques fonctionnent depuis une référence de projet et depuis
  un package NuGet local (`EXT-WIN-011` puis `EXT-WIN-013`) ;
- [x] la publication Release ne produit aucun diagnostic inexpliqué
  (`EXT-WIN-014` : `dotnet publish -c Release` du sample, 0 avertissement) ;
- [x] la documentation publique permet à un autre développeur d’intégrer les packages
  sans lire leur code source (`README.md` dans chacun des sept projets de la fermeture,
  doc XML publique complète sous `GenerateDocumentationFile=true`).
- [x] la même référence de projet peut appliquer les neuf thèmes au chrome et à
  la taskbar via un unique `ThemeScope`, sans copie d'asset dans l'hôte ; voir
  [`theming.md`](theming.md) et l'ADR 0041. Cette amélioration du 2026-08-19 ne
  modifie pas les contrats du moteur headless.

**État : extraction terminée le 2026-08-13, close pour de bon le 2026-08-17.**
`EXT-WIN-001` à `015` sont tous complets ; voir la section 10 pour le détail
phase par phase. Le 2026-08-13, `EXT-WIN-001` restait explicitement reporté
(`[~]`) et le composant `WindowSurface` de la section 3.4 n'existait pas —
la ligne d'état de cette date affirmait à tort que tout était complet. Ces
deux points sont fermés le 2026-08-17 : voir `EXT-WIN-001` (section 10) et
la note `WindowSurface` sous `EXT-WIN-005` pour les preuves.

