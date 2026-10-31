> [English version](../../../en/wasm/framework/sdk-packages.md)

# Paquets NuGet, version du SDK et modèles

## Objet

Permettre à un autre dépôt de construire des apps HackerOS
**et** un hôte d'OS complet avec ses propres apps, sans copier ce dépôt
([ADR 0098](../dev/adr/0098-sdk-versions-packages-and-manifest-checks.md) §1, §2, §6).
Tout ce qui est publié porte une seule version, sort en paquets NuGet dans un
flux **local** (`artifacts/local-nupkg`), et cinq modèles `dotnet new` créent
les projets. Depuis l'[ADR 0173](../dev/adr/0173-automatic-releases-from-the-version-file.md),
chaque version fusionnée dans `main` est aussi publiée sur nuget.org et dans les
Releases GitHub ([`releases.md`](../dev/releases.md)).

Le guide pas à pas d'un dépôt tiers (disposition, hôte, tests, montée de
version) est [`building-on-hackeros.md`](building-on-hackeros.md).

Les vérifications faites au chargement (version du SDK, commandes en double,
empreintes des ressources, ADR 0098 §3 à §5) sont décrites dans
[`app-model.md`](app-model.md#version-du-sdk-commandes-intégrité-adr-0098-3-5).

## Version unique

- `build/Versions.props` définit `HackerOsVersion` (aujourd'hui `2.2.0` ; `2.1.0` a été la première publication automatique de l'ADR 0173),
  importé par `Directory.Build.props` : chaque projet de la solution porte ce
  `Version`.
- Avant le gel : `0.9.0-preview.<n>`. Depuis le gel du SDK 1.0 (chantier
  `v3/15`, [ADR 0114](../dev/adr/0114-sdk-1-0-freeze.md)) : SemVer (ADR 0019),
  correctif sans changement d'API, mineure pour un ajout, majeure pour une
  rupture du SDK (voir « Gel du SDK 1.0 » et « SDK 2.0 »).
- `HackerOsSdk.Version` (`HackerOs.App.Abstractions`) est cette version à
  l'exécution (version informative de l'assembly, sans métadonnées `+sha`).
  Le générateur l'écrit comme `sdkCompatibility.minimumVersion` de chaque app ;
  `AppCatalog` refuse une app dont la plage n'accepte pas le SDK de l'hôte (même
  majeure que le minimum, minimum ≤ SDK ≤ maximum).

## Paquets

Un projet est publié quand il importe `Platform/Packaging.props` ;
`Directory.Build.props` rend tous les autres non empaquetables. `Packaging.props`
apporte les métadonnées communes (auteur, licence, dépôt, SourceLink, symboles
`.snupkg`, documentation XML) et l'**analyseur d'API publique** : chaque
bibliothèque liste sa surface dans `PublicAPI.Shipped.txt` (la 1.0, gelée) et
`PublicAPI.Unshipped.txt` (ce qui a changé depuis, voir « Gel du SDK 1.0 »). Les avertissements de documentation XML sont des erreurs
(`TreatWarningsAsErrors`), donc chaque membre public est documenté.

| Paquets | Contenu |
|---|---|
| `HackerOs.App.Abstractions`, `HackerOs.Simulation.Abstractions`, `HackerOs.Theming.Abstractions`, `HackerOs.AppSdk`, `.Blazor`, `.Terminal`, `.Web`, `.Web.Blazor`, `.Icons`, `.VirtualKeyboard` | le SDK public, ce qu'une app référence (`.FileView` n'en fait plus partie depuis la 2.0 : son code est dans l'explorateur de fichiers) ; `HackerOs.AppSdk` porte aussi l'analyseur `HOSBG` de `HackBackground` dans `analyzers/dotnet/cs/` (ADR 0140, [`background-jobs.md`](background-jobs.md)) |
| `HackerOs.App.Build` | l'intégration de build (voir plus bas) |
| `HackerOs.Platform.Core`, `.Blazor`, `.ResourceAccounting`, `.WorkerHost`, `HackerOs.Infrastructure.Browser`, `HackerOs.Windowing.Abstractions`, `.Core`, `.Blazor`, `HackerOs.Taskbar.Blazor`, `HackerOs.Theming.Blazor`, `HackerOs.MobileShell.Blazor`, `HackerOs.Server.Contracts` | la plateforme, ce qu'un hôte compose (`Server.Contracts` est une dépendance de `Platform.Core`) |
| `HackerOs.Ecosystem.Host` | l'OS lui-même : bureau, démarrage, services, adaptateurs du navigateur, jetons de design |
| `HackerOs.Apps.*` (13 apps), `HackerOs.Commands` | les apps de base, chacune avec ses manifestes |
| `HackerOs.Templates` | les modèles `dotnet new` |

### Produire et consommer le flux local

```bash
build/pack-local.sh                 # ou build/pack-local.ps1 ; --output <dossier> pour un autre flux
```

Le script construit un filtre de solution (`artifacts/packages.slnf`) des
projets qui importent `Packaging.props`, lance `dotnet pack -c Release` vers
`artifacts/local-nupkg`, puis retire cette version de chaque paquet `HackerOs.*`
du cache NuGet global : sans cela, un nouveau paquet de même version serait
masqué par l'ancien.

Dans l'autre dépôt, `build/nuget.config.sample` sert de modèle : une source
`hackeros-local` vers ce dossier, nuget.org pour le reste, et un
`packageSourceMapping` qui ne prend les `HackerOs.*` que du flux local.

### `HackerOs.App.Build`

Paquet sans assembly (`Tools/HackerOs.App.Build`), à référencer avec
`PrivateAssets="all"` pour qu'il ne se propage pas aux consommateurs du projet :

| Chemin dans le paquet | Rôle |
|---|---|
| `buildTransitive/HackerOs.App.Build.targets` | pose `HackerOsBuildFromPackage=true` et importe le fichier suivant |
| `hackeros-build/HackerOs.Apps.targets` | **le même fichier** que `build/HackerOs.Apps.targets` du dépôt (hors de `build/`, où NuGet n'attend que `HackerOs.App.Build.targets`) |
| `hackeros-build/asp-hack/` | `_ViewImports.cshtml` et types de compatibilité des vues `.cshtml` ASP.Hack (ADR 0086) |
| `tools/generator/` | le générateur de manifestes, lancé par `dotnet <dll>` (sans l'exécutable natif) |
| `analyzers/dotnet/cs/` | le générateur de routes ASP.Hack (ADR 0082) |

En **mode paquet**, un projet qui référence `HackerOs.App.Build` est une app,
sauf s'il déclare `HackerOsAppHost=true` (un hôte). Différences avec le dépôt :

- le générateur vient du paquet au lieu d'une `ProjectReference` ;
- la règle de frontière sur les `ProjectReference` (écrite en chemins du dépôt)
  ne s'applique pas ; celle sur les paquets, si
  (`_ValidateHackerOsPackageBoundary`, `HOSAPP0030`, voir
  [`app-sdk.md`](app-sdk.md#vérification)) : toute référence de compilation
  issue d'un paquet `HackerOs.*`, directe ou transitive, doit être permise ;
- l'inspection de l'injection (`HOSAPP0031`), les codes `HOSAPP*` et les
  empreintes des ressources sont identiques.

### Paquets d'apps

Quand un projet d'app est empaqueté, la cible `_HackerOsPackageAppManifests`
(branchée sur `TargetsForTfmSpecificContentInPackage`) ajoute :

- ses manifestes générés sous `hackeros/manifests/` et leurs traductions
  (`localization/<culture>.json`, ADR 0104) sous `hackeros/localization/` (ajouté
  par `v3/14` : sans elles, un hôte en paquets échouait en `HOSAPP0040`) ;
- `buildTransitive/<PackageId>.targets`, qui les déclare en éléments
  `HackerOsPackagedApp` :

```xml
<HackerOsPackagedApp Include="$(MSBuildThisFileDirectory)../hackeros/manifests/manifest.json"
                     PackageId="HackerOs.Apps.Calculator"
                     AssemblyFileName="HackerOs.Apps.Calculator.dll"
                     AssemblyPath="$(MSBuildThisFileDirectory)../lib/net10.0/HackerOs.Apps.Calculator.dll" />
```

Les fichiers de `wwwroot/` d'une app restent des ressources web statiques du
paquet (`_content/<PackageId>/…`), là où `StaticWebAssetAppAssetSource` les lit.

Un hôte agrège ces éléments avec ses `ProjectReference`
(`_HackerOsCollectPackagedAppManifests`) : `IsSystemApp` vient de sa
`PackageReference` sur le paquet ; un paquet d'app arrivé par dépendance est
installé aussi (comme un gestionnaire de paquets installe une dépendance), mais
jamais en app système. Les assemblies sont chargées paresseusement comme les
autres.

## `HackerOs.Ecosystem.Host`

L'exécutable `OS/HackerOs.Ecosystem` contenait tout l'OS. Il est coupé en deux :

| Projet | Contenu |
|---|---|
| `OS/HackerOs.Ecosystem.Host` (bibliothèque Razor, publiée) | `App.razor`, `LoginProgressScreen`, `EcosystemBootCoordinator`, `EcosystemHostState`, `AddHackerOsEcosystem`, `AddHackerOsResourceAccounting`, `AddHackerOsBrowserHost`, `HostAppCatalog`, adaptateurs WebAssembly (transport paresseux, environnement), `Administration/`, `WebData/`, `wwwroot/css/app.css` |
| `OS/HackerOs.Ecosystem` (PWA de ce dépôt) | `wwwroot/` (page, service worker, manifeste web, icônes), les références d'apps, `BuildKnownLazyApps` et `BuildKnownLazyAssemblies` (façades sur son `HostAppCatalog`), un `Program.cs` de trois lignes |

L'espace de noms reste `HackerOs.Ecosystem` : `HackerOs.Ecosystem.App` vit
désormais dans l'assembly `HackerOs.Ecosystem.Host`. `test/test` et le serveur
référencent toujours l'exe (pour son catalogue agrégé) et reçoivent la
bibliothèque par transitivité. Les jetons de design sont servis à
`_content/HackerOs.Ecosystem.Host/css/app.css` par les trois hôtes.

```csharp
// Program.cs d'un hôte WebAssembly
var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.AddHackerOsBrowserHost(HostAppCatalog.Load(typeof(Program).Assembly));
await builder.Build().RunAsync();
```

`AddHackerOsBrowserHost(apps, options)` enregistre le composant racine (seulement
si l'élément `#app`, `RootSelector`, existe : un îlot WebAssembly d'une Blazor
Web App n'en a pas), le chargeur paresseux limité aux assemblies du catalogue, la
source des ressources d'apps (`StaticWebAssetAppAssetSource` sur
`_content/`), la mesure des ressources avec les images du catalogue, tous les
services de l'OS et le pool de Web Workers (`WorkerPreloadAssemblies`, par défaut
`HackerOs.Commands.dll` quand l'hôte l'a).

Le paquet est produit avec les deux mesures de ressources compilées
(ADR 0066) ; un hôte les coupe à l'exécution par `appsettings.json`.

## Modèles `dotnet new`

`Templates/HackerOs.Templates` (paquet `HackerOs.Templates`) :

| Modèle | Crée | Paquets |
|---|---|---|
| `hackeros-window` | app fenêtrée (`WindowAppBase`, CSS isolé, `_Imports.razor`) | `HackerOs.AppSdk`, `.Blazor`, `HackerOs.App.Build` |
| `hackeros-command` | commande du shell `hello` (`TerminalAppBase`) | `HackerOs.AppSdk`, `HackerOs.App.Build` |
| `hackeros-service` | service démarré à la connexion (`ServiceAppBase`, horloge simulée) | idem |
| `hackeros-site` | site ASP.Hack (`WebAppDefinition`, `wwwroot/` embarqué) | `HackerOs.AppSdk.Web`, `HackerOs.App.Build` |
| `hackeros-host` | hôte Blazor WebAssembly : `Program.cs`, `index.html`, `appsettings.json`, les apps de base en paquets (système comme dans l'Ecosystem) | `HackerOs.Ecosystem.Host`, `HackerOs.App.Build`, `HackerOs.Apps.*`, `HackerOs.Commands` |
| `hackeros-solution` | dépôt d'app entier : app, hôte, tests, sous-module HackerOS (sources en `Debug`, paquets en `Release`), CI, publication dans le Hack Store ([`app-repository.md`](app-repository.md), ADR 0207) | ceux de l'app et de l'hôte, à la version du sous-module |

Les modèles d'app partent avec `IsTrimmable=true` ; `hackeros-host` publie en
Release trimmée (`PublishTrimmed=true`, `TrimMode=partial`) et
`HackerOs.App.Build` garde entiers les paquets `HackerOs.*` et chaque app
installée, active l'analyse de trimming dans un projet d'app qui ne la règle
pas (ADR 0106 §4, ADR 0113, [`trimmed-publish.md`](../dev/trimmed-publish.md)).

Paramètres des modèles d'app : `--appId` (défaut `com.example.my-app`) et
`--publisherId` (défaut `pub.example`) ; le nom du projet (`-n`) devient
l'espace de noms et le nom affiché. `hackeros-solution` prend aussi `--repository` (`propriétaire/nom`).
Chaque modèle a un `.template.config/ide.host.json` (icône, libellés des
paramètres) pour Visual Studio et Rider (ADR 0176 §7), et les modèles d'app
déclarent `HackerOsAppProject=true` (ADR 0207 §4).

Chaque projet généré déclare `<HackerOsVersion>` une fois et l'utilise pour
toutes ses références `HackerOs.*`. Dans les sources du modèle, la valeur est
`0.0.0-dev` ; l'empaquetage copie `content/` dans `obj/` et y écrit la version du
dépôt (`XmlPoke`), si bien qu'un projet généré référence exactement les paquets
empaquetés avec lui.

Les points d'entrée des modèles de commande et de service ont le constructeur
`(AppManifest manifest)` par lequel l'OS démarre une app, plus un constructeur
sans paramètre pour les tests unitaires.

## Gel du SDK 1.0

[ADR 0098](../dev/adr/0098-sdk-versions-packages-and-manifest-checks.md) §8 et
[ADR 0114](../dev/adr/0114-sdk-1-0-freeze.md) :

- Toute la surface des 39 bibliothèques (38 depuis la 2.0) est dans `PublicAPI.Shipped.txt` ;
  chaque `PublicAPI.Unshipped.txt` ne contient que `#nullable enable`.
- Après le gel, un ajout va dans `Unshipped` (`RS0016`, la correction
  `dotnet format analyzers <csproj> --diagnostics RS0016 --severity info`
  l'écrit) ; un retrait ou un changement est refusé (`RS0017`) tant qu'il n'est
  pas écrit `*REMOVED*<ligne>` dans `Unshipped`. Une publication verse
  `Unshipped` dans `Shipped` et choisit la version d'après son contenu.
- **Promesse** : une app compilée contre `1.x` tourne sur tout OS `1.y`
  (y ≥ x). Elle couvre ce qu'une app compile : les trois `*.Abstractions`
  publiés, `HackerOs.AppSdk*`, `HackerOs.Theming.Blazor`, le contrat de build de
  `HackerOs.App.Build` et le format du manifeste. La plateforme, l'hôte et les
  types publics des apps de base suivent la même version **sans** cette
  promesse : une mineure peut y casser un hôte (jamais une app), et chaque
  rupture est listée ci-dessous.
- Les trois API `[Obsolete]` (`BrowserClipboard`,
  `IAppClipboardGateway.Get/SetFileReferencesAsync`) ont été retirées avant le
  gel : la 1.0 n'en contient aucune.
- Une app compilée en `0.9.0-preview.*` est refusée par l'OS 1.0 (autre
  majeure) : elle doit être reconstruite.

### Ruptures hors SDK

Ruptures d'API publique de la plateforme, de l'hôte ou des apps de base, par
version (lignes `*REMOVED*` versées dans `Shipped` à la publication). Aucune
app n'est touchée : ces types ne font pas partie du SDK.

| Version | Paquet | Rupture | Que faire |
|---|---|---|---|
| 1.1.0 | `HackerOs.Platform.Blazor` | `WindowGeometryPersistence.RestoreAsync` rend `SavedWindowGeometry?` au lieu de `WindowBounds?` ; `SaveAsync` prend `SavedWindowGeometry` au lieu de `(WindowBounds, isEligible)` (ADR 0116, 0122 : seules la taille et l'état agrandi sont retenus) | passer par `SavedWindowGeometry`, ou par `WindowGeometryRecorder` |
| 1.1.0 | `HackerOs.Platform.Blazor` | `WindowGeometrySettings.Parse` rend `SavedWindowGeometry?` ; `Serialize(WindowBounds)` devient `Serialize(appId, SavedWindowGeometry)` | adapter l'appel |
| 1.1.0 | `HackerOs.Platform.Blazor` | `WindowLaunchCoordinator.Present(...)` (deux surcharges) remplacé par `PresentAsync(...)` ; le constructeur prend maintenant l'enregistreur de géométrie | appeler `PresentAsync` ; laisser l'injection de dépendances construire le coordinateur (`AddHackerOsBrowserHost`) |
| 1.2.0 | `HackerOs.Ecosystem.Host` | le constructeur d'`EcosystemBootCoordinator` prend trois paramètres optionnels de plus (`OomKiller? oomKiller`, `IOsSettingsService? settings`, `CrossOriginIsolationProbe? isolation`, ADR 0126, 0131) : rupture binaire, le code source compile toujours | recompiler l'hôte ; laisser l'injection de dépendances construire le coordinateur (`AddHackerOsEcosystem`) |
| 2.0.0 | `HackerOs.Ecosystem.Host` | n'enregistre plus `IShellIconProvider` (ADR 0161 §1) | rien : le contrôle de fichiers garde ses icônes par défaut |
| 2.0.0 | `HackerOs.Apps.FileExplorer` | les types de l'ancienne `FileView` sont dans `HackerOs.Apps.FileExplorer.FileControl` et dérivent de `HackComponentBase` ; `FileConflictDialog`, `FilePropertiesDialog`, `OpenWithDialog`, `FileExplorerDialogBase`, `PublishSiteContextMenuProvider`, `ZipFileContextMenuProvider` et `FileExplorerState.ViewMode` sont retirés (desk/3 à desk/5) | passer par les contrats `hackeros.file-view` et `hackeros.file-dialogs` |
| 2.0.0 | `HackerOs.Apps.CodeEditor` | `CodeEditorExplorer` perd `OnDeleteRequested`, `OwnerAppId`, `Watch` et `Dispose` ; le constructeur public de `CodeEditorWorkspace` est retiré (desk/3) | utiliser `HackFileView` |

## SDK 1.1

Publiée par le chantier `v3.1/4-sdk-1-1` ([ADR 0117](../dev/adr/0117-sdk-window-title-and-sdk-1-1.md) §5) :
chaque `PublicAPI.Unshipped.txt` est versé dans son `PublicAPI.Shipped.txt`
(lignes triées, `*REMOVED*` appliqués) et `HackerOsVersion` passe à `1.1.0`.
Ajouts du SDK, tous compatibles avec une app 1.0 (les nouveaux membres
d'interfaces ont une implémentation par défaut) :

| Bibliothèque | Ajout | Décision |
|---|---|---|
| `HackerOs.AppSdk.Blazor` | `WindowAppBase.SetWindowTitleAsync`, `WindowTitle`, `FormatWindowTitle`, `IAppWindowGateway` | ADR 0117, 0123 |
| `HackerOs.App.Abstractions` | `WindowOptions.RememberGeometry` ; capacité `users.manage` | ADR 0116, 0122 ; ADR 0120 |
| `HackerOs.AppSdk` | `HackUserAccounts`, `HackUserAccount`, `IAppUserAccountsGateway`, `IAppExecutionContext.UserAccounts`, `ITerminalInputMode`, `TerminalExecutionContext.ReadSecretLineAsync` | ADR 0120 |
| `HackerOs.AppSdk` | `HackApps.GetDefaultHandlersAsync`, `SetDefaultHandlerAsync`, `ResetDefaultHandlerAsync`, `HackFileAssociation`, `HackFileAssociationLayer`, `HackDefaultHandlerStatus` | ADR 0121 |
| `HackerOs.AppSdk.Terminal` | `TerminalInputReader.EchoEnabled` | ADR 0120 |
| `HackerOs.Simulation.Abstractions` | `ILocalUserRepository.SetCredentialAsync` | ADR 0120 |

Une app reconstruite contre 1.1.0 porte `sdkCompatibility.minimumVersion:
1.1.0` et n'est plus admise par un OS 1.0. L'app de compatibilité compilée en
1.0.0 se charge et tourne toujours sur l'OS 1.1 (voir ci-dessous).

## SDK 1.2

Publiée une seule fois, à la fin de la série `exec`, par le chantier
`exec/10-final` ([ADR 0132](../dev/adr/0132-exec-series-sdk-1-2-and-merge-rules.md) §2,
réponse E4 A de Benoit) : `build/tools/ship-public-api.sh` verse chaque
`PublicAPI.Unshipped.txt` dans son `PublicAPI.Shipped.txt` (795 lignes, triées
en ordre ordinal, `*REMOVED*` appliqués) et `HackerOsVersion` passe à `1.2.0`.
Ajouts du SDK, tous compatibles avec une app 1.0 ou 1.1 (chaque nouveau membre
d'interface a une implémentation par défaut, chaque nouvelle valeur
d'énumération prend un nombre à la fin) :

| Bibliothèque | Ajout | Décision |
|---|---|---|
| `HackerOs.AppSdk` | `HackWireContextAttribute` (`[assembly: HackWireContext(typeof(...))]`), pour les passerelles d'événements et de surveillance appelées depuis un worker | ADR 0124, 0138 |
| `HackerOs.AppSdk` | `HackProcess.GetResourceLimitsAsync`, `SetResourceLimitAsync`, `HackResourceLimits`, `HackResourceLimitEnforcement`, `HackProcessKilledException.Reason` et son constructeur à raison | ADR 0125, 0139 |
| `HackerOs.AppSdk` | `HackProcess.Nice`, `BasePriority`, `ParseStatNice` ; `IShellSession.RunNicedAsync` | ADR 0127 |
| `HackerOs.AppSdk` | `HackBackground.RunAsync` (cinq surcharges), `HackBackgroundJob<TInput, TResult>`, `HackBackgroundContext`, `HackBackgroundOptions`, `HackBackgroundProgress`, `HackBackgroundJobException`, `IAppBackgroundGateway`, `IAppExecutionContext.Background` ; analyseur `HOSBG0001` à `HOSBG0004` dans le paquet | ADR 0128, 0140 |
| `HackerOs.App.Abstractions` | `AppDefinition.OomScoreAdjust`, `AppManifest.OomScoreAdj` (`oomScoreAdj` du manifeste) | ADR 0126, 0142 |
| `HackerOs.Simulation.Abstractions` | `ResourceLimit`, `ResourceLimitKind`, `ProcessResourceLimits`, `ResourceLimitChangeResult`, `ProcessLimitExceededException`, `IProcessResourceLimitTable`, `IProcessFileDescriptorTable`, `ProcessFileDescriptor`, `NoDescriptor`, `IAppFileSystemGateway.OpenDescriptorAsync`, `IAppProcessGateway.SetResourceLimitAsync`, `ProcessStartRequest.ResourceLimits` et `ResourceLimitExempt`, `FileSystemErrorCode.TooManyOpenFiles`, `ProcessExitReason.CpuLimitExceeded`, `MemoryLimitExceeded`, `OutOfMemory` | ADR 0125, 0126, 0139 |
| `HackerOs.Simulation.Abstractions` | `ProcessNice`, `IProcessManager.GetNice`/`SetNice`, `IAppProcessGateway.GetNice`/`SetNice`, `ProcessStartRequest.Nice` et `OomScoreAdjust` | ADR 0126, 0127 |
| `HackerOs.Simulation.Abstractions` | `WorkerSnapshot.RuntimeHeapBytes`, `IWebWorkerMemoryMeter`, `ISynchronousWebWorker`, `SynchronousAnswerOutcome` | ADR 0131, 0142, 0143 |

Les autres bibliothèques publiées (plateforme, hôte, serveur, apps de base)
reçoivent les ajouts de la série sans la promesse du SDK ; la seule rupture est
listée dans « Ruptures hors SDK ». Une app reconstruite contre 1.2.0 porte
`sdkCompatibility.minimumVersion: 1.2.0` et n'est plus admise par un OS 1.0 ou
1.1. Les apps de compatibilité compilées en 1.0.0 et en 1.1.0 se chargent et
tournent sur l'OS 1.2 (voir ci-dessous).

```bash
build/tools/ship-public-api.sh --check   # ce qu'une publication verserait, sans rien changer
build/tools/ship-public-api.sh           # à la publication, puis HackerOsVersion dans build/Versions.props
```

Aucune 1.3 n'a été publiée : les ajouts qui l'attendaient (`PublishAsync`,
ADR 0145 ; `HackCrossOriginIsolation`, ADR 0146) sont partis dans la 2.0.

## SDK 2.0

Publiée par le chantier `desk/6-sdk-2`, à la fin de la série du bureau et du
menu contextuel ([ADR 0149](../dev/adr/0149-sdk-2-0-file-view-removal.md),
[ADR 0161](../dev/adr/0161-sdk-2-0-implementation-adjustments.md) ; question Q5 du
[plan](../dev/plans/desktop-and-context-menu-plan.md), choix de Benoit « SDK 2.0
maintenant ») : `build/tools/ship-public-api.sh` verse 1561 lignes de
`PublicAPI.Unshipped.txt` dans `PublicAPI.Shipped.txt` et `HackerOsVersion`
passe à `2.0.0`.

### Rupture : `HackerOs.AppSdk.FileView` quitte le SDK

Le paquet `HackerOs.AppSdk.FileView` n'existe plus. Son code (vue, arbre,
détails, icônes, fil d'Ariane, zone de dépôt, menus `IFileViewContextMenuProvider`,
`ShellIcon`, `IShellIconProvider`) est l'implémentation interne de l'explorateur
de fichiers (`Apps/System/HackerOs.Apps.FileExplorer/FileControl/`), qui fournit
le contrôle aux autres apps par le contrat `hackeros.file-view` (ADR 0148). Les
autres bibliothèques du SDK ne perdent aucun membre : la majeure vient de ce
seul retrait.

Une app compilée contre 1.x porte `sdkCompatibility.minimumVersion: 1.x`. Un
hôte qui la référence ne se construit plus (`HOSAPP0021`, la même règle que le
catalogue appliquée au build) ; un OS 2.0 qui la trouve à son catalogue la refuse
au démarrage (`catalog.sdk.incompatible`), ne la charge pas, et Paramètres ▸
Applications installées l'affiche « Incompatible ». Elle doit être recompilée
contre les paquets 2.x (2.2.0 aujourd'hui).

### Migration d'une app 1.x

| En 1.x | En 2.0 |
|---|---|
| `<FileView …/>` (`HackerOs.AppSdk.FileView`) | `<HackFileView InitialDirectory="…" …/>` (`HackerOs.AppSdk.Blazor.Files`) et son contrôleur `IHackFileView` ([`file-explorer.md`](../dev/file-explorer.md)) |
| `IFileViewContextMenuProvider`, `FileViewMenuItemCollection` | `ContextMenuOpening` de `HackFileView`, ou une entrée inscrite par `[RegisterFileContextMenu]` ([`context-menu.md`](context-menu.md)) |
| `FileTransfer.CopyAsync` / `MoveAsync` / `TransferAsync` / `ImportAsync` / `MoveToTrashAsync` / `GetCopyName` | les mêmes méthodes de `HackFileOperations` (`HackerOs.AppSdk.IO`) |
| `FileTransferResult`, `FileTransferMode`, `FileConflictChoice`, `FileConflictResolution`, `FileTransferConflict`, `FileConflictResolver`, `FileTransferFailure`, `FileTransferItemResult`, `FileTransferHostFile` | `HackFileTransferResult`, `HackFileTransferMode`, `HackFileConflictChoice`, `HackFileConflictResolution`, `HackFileTransferConflict`, `HackFileConflictResolver`, `HackFileTransferFailure`, `HackFileTransferItemResult`, `HackHostFile` |
| un résolveur de conflit écrit par l'app | `Dialogs.AsFileConflictResolver(AppContext)` : le dialogue « Remplacer / Garder les deux / Ignorer » du fournisseur de `hackeros.file-dialogs` |
| `ShellIcon`, `ShellIconDescriptor` | `HackIcons.Vector(…)` / `HackIcons.FromImage(…)` (`HackerOs.AppSdk.Icons`) pour décrire une icône ; le rendu appartient à l'OS |

Puis passer `HackerOsVersion` à `2.2.0` (ou la dernière 2.x publiée) et recompiler.

### Ajouts

Tous les autres changements du SDK sont des ajouts :

| Bibliothèque | Ajout | Décision |
|---|---|---|
| `HackerOs.AppSdk.Blazor` | `HackContractComponent<TController>`, `HackComponentResolution` : hôte d'un contrat fourni par une app | ADR 0148, 0153 |
| `HackerOs.AppSdk.Blazor` | `HackContextMenu`, `IHackContextMenu`, `HackMenus` (`Menus.Attach`, `Menus.Define`, `OpenAsync`), `HackMenuBuilder`, `HackMenuDefinition`, `HackMenuItem`, `HackMenuGroup`, `HackMenuOpeningEventArgs`, `IHackMenuHost` | ADR 0150, 0154 |
| `HackerOs.AppSdk.Blazor` | `HackFileView`, `IHackFileView`, `HackFileViewItem`, `HackFileViewMode`, `HackFileViewFeatures`, leurs événements, `HackFileMenuOpeningEventArgs`, `HackFileMenuContext` | ADR 0148, 0155, 0160 |
| `HackerOs.AppSdk.Blazor` | `IDialogService.OpenWithAsync`, `ShowFilePropertiesAsync`, `ResolveFileConflictAsync` et leurs types, `HackFileDialogs`, `HackFileOperationDialogs.AsFileConflictResolver` | ADR 0155, 0157, 0161 |
| `HackerOs.AppSdk.Blazor` | `[RegisterFileContextMenu]`, `RegisterFileContextMenuContext` | ADR 0151, 0159 |
| `HackerOs.AppSdk` | `HackFileOperations` et ses types (`HackFileTransferResult`…, `HackHostFile`) | ADR 0161 §2 |
| `HackerOs.AppSdk` | `HackFileMenuItems`, `FileMenuScope`, `MenuSelection`, `HackFileMenuIntent`, `HackFileMenuEntries`, `HackFileMenuEntryInfo` | ADR 0151, 0159 |
| `HackerOs.AppSdk` | `HackComponentProviders`, `HackComponentContractInfo`, `HackComponentProviderInfo` (choix du fournisseur par l'administrateur) | ADR 0156, 0158 |
| `HackerOs.AppSdk` | `HackDesktopSettings`, `HackDesktopSettingsDocument`, `HackDesktopOptions`, `HackDesktopLayout`, `HackDesktopCell`, `HackDesktopEntry`, `HackDesktopItems`, `HackDesktopSort`, `HackDesktopIconSize`, `HackDesktopArrangement`, `HackAppIcons` | ADR 0152, 0160 |
| `HackerOs.AppSdk` | `HackCrossOriginIsolation` et ses types, `IAppCrossOriginIsolationGateway` | ADR 0146 |
| `HackerOs.AppSdk.Icons` | `HackIcons` (`Vector`, `FromImage`), `HackIcon`, `HackIconKind` | ADR 0150, 0151 |
| `HackerOs.App.Abstractions` | `AppManifest.ContextMenus`, `AppContextMenuEntryManifest` et ses titres et icônes, `AppContextMenus`, `AppComponentContracts`, `AppComponentProviderScope`, capacité `shell.context-menu` | ADR 0151, 0153, 0156 |
| `HackerOs.Simulation.Abstractions` | `IAppEventGateway.PublishAsync` | ADR 0145 |

### SDK 2.1

Publiée le 2026-10-26, première version sortie par le workflow `Release HackerOS`
([ADR 0173](../dev/adr/0173-automatic-releases-from-the-version-file.md),
[`releases.md`](../dev/releases.md)) : tag `v2.1.0`, GitHub Release et nuget.org.
`build/tools/ship-public-api.sh` a versé 5880 lignes de `PublicAPI.Unshipped.txt`
dans `PublicAPI.Shipped.txt` : les ajouts arrivés après la coupe de la 2.0
(clés d'accès et administration des comptes, ADR 0168 et 0169 ;
`HackFileView.OpenOnTap`, ADR 0171 ; barre latérale des Paramètres, ADR 0170 ;
le tableau ci-dessous) et les membres de records que l'analyseur d'API publique
5.x suit désormais (`<Clone>$`, `Deconstruct`, `Equals`). Aucune ligne
`*REMOVED*` : mineure d'après SemVer, une app 2.0 tourne sans recompilation.

| Bibliothèque | Ajout | Décision |
|---|---|---|
| `HackerOs.Simulation.Abstractions` | `ProcessStartRequest.ParentExitGracePeriod` : délai d'un enfant après la fin de son parent (demande d'arrêt, puis mise à mort) | ADR 0147 |
| `HackerOs.Platform.Core` | `BackgroundJobService.ParentExitGracePeriod` (5 s) | ADR 0147 |

### SDK 2.2

Préparée le 2026-10-31 (branche `release/2.2.0`), publiée par le workflow
`Release HackerOS` quand sa PR arrive dans `main` : tag `v2.2.0`, GitHub
Release et nuget.org. `build/tools/ship-public-api.sh` a versé 3020 lignes de
`PublicAPI.Unshipped.txt` dans `PublicAPI.Shipped.txt` : les ajouts de la
série `todo/base` et ceux arrivés sur `main` depuis la 2.1.0. Les seules lignes
`*REMOVED*` touchent `HackerOs.Apps.Browser` (le constructeur de
`BrowserResponse` reçoit la source de la réponse, ADR 0210), une app de base
et non une bibliothèque du SDK : mineure d'après SemVer, une app 2.x tourne
sans recompilation. Le modèle `hackeros-solution` a besoin de cette version
pour son profil `Release` (ADR 0207 §11).

| Bibliothèque | Ajout | Décision |
|---|---|---|
| `HackerOs.AppSdk` | `HackAppPackages`, `HackPackageSources`, `HackPackagePolicy`, `HackPackageTrust`, `HackPackageScope`, `HackInstalledPackage`, `HackPackageInstallReport`, `HackPackageException` : installer une app pour soi ou pour tous, sources de confiance, liste noire | ADR 0182, 0208 |
| `HackerOs.AppSdk` | `HackAppCatalog`, `HackCatalogApp`, `HackCatalogAppVersion`, `HackCatalogCategory`, `HackCatalogItemKind`, `HackPackageCatalog`, `HackPackageUpdate` : catalogues du Hack Store et mises à jour | ADR 0189, 0212 |
| `HackerOs.AppSdk` | `HackInstalledThemePack`, `HackThemePackInstallReport`, `HackThemePackParts` : packs de thèmes | ADR 0213 |
| `HackerOs.AppSdk` | `HackWhatsNew`, `HackChangeList`, `HackChangeGroup`, `HackChangeEntry`, `HackChangeCategory` : liste des nouveautés | ADR 0186 |
| `HackerOs.AppSdk` | `HackScreens`, `HackScreenInfo` et leurs types : écrans du bureau étendu | ADR 0184 |
| `HackerOs.AppSdk` | `HackTimeZone`, `IAppExecutionContext.TimeZone` ; `HackServerProbe`, `HackServerConnection.PairAsync` et `TestAsync` ; `HackHttpResponseSource` ; `HackDirectorySize` ; `HackDesktopLink` | ADR 0193, 0191, 0210, 0185 |
| `HackerOs.Simulation.Abstractions` | `IHostFolder`, `IHostFolderAccess` et leurs types (dossier de l'ordinateur monté) ; `OsSoundEvent`, `SoundThemeIndex` (sons du système) ; `FileSystemTreeSize` | ADR 0181, 0204, 0185 |
| `HackerOs.Theming.Abstractions` | `WallpaperCatalog`, `BuiltInWallpaper`, `WallpaperFit` (fonds en image) ; `IconPacks.IconPack`, `IconPackLibrary` et leurs types (packs d'icônes) ; `CursorTheme`, `CursorThemeLibrary` et leurs types (thèmes de curseurs) ; `ThemePackLayout` (packs de thèmes) | ADR 0202, 0203, 0205, 0213 |
| `HackerOs.AppSdk.Blazor` | `MessageBoxIcon` | ADR 0185 |

### App de compatibilité

`Tests/fixtures/sdk-compat/packages` garde une app par version publiée qui en
a reçu une, chacune compilée **une fois** par `produce-fixture.sh`, hors du
dépôt, contre les paquets de sa version, et **jamais reconstruite** :

| Paquet | Compilé contre | Contenu |
|---|---|---|
| `Contoso.Sdk1Compat.1.0.0.nupkg` | 1.0.0 (`v3/15`) | fenêtre `com.contoso.sdk1-window` (enregistre et relit un fichier), commande `sdk1-compat` |
| `Contoso.Sdk11Compat.1.1.0.nupkg` | 1.1.0, paquets du sommet de `v3.1/base` (`exec/10-final`, ADR 0132 §1) | fenêtre `com.contoso.sdk11-window` qui se nomme avec `SetWindowTitleAsync` et `FormatWindowTitle` (`note.txt — SDK 1.1 Window`), `rememberGeometry: false` ; commande `sdk11-compat` qui appelle aussi `HackUserAccounts.ValidateNewPassword` |

| `Contoso.Sdk2Compat.2.0.0.nupkg` | 2.0.0, paquets du sommet de `desk/6-sdk-2` (ADR 0161 §3) | fenêtre `com.contoso.sdk2-window` qui affiche le contrôle de fichiers de l'OS (`HackFileView`) et inscrit l'entrée « Inspect with SDK 2.0 » dans le menu des fichiers (`[RegisterFileContextMenu]`, intent `com.contoso.sdk2.inspect.v1`) ; commande `sdk2-compat` qui copie un fichier par `HackFileOperations.CopyAsync` (`sdk2-compat (copy).txt`) |

Les tests chargent l'app de la majeure courante (2.0.0) dans l'OS ; celles de la
1.x restent comme preuve du **refus** d'une app d'une autre majeure
(ADR 0149 §5).

```bash
build/pack-local.sh
Tests/fixtures/sdk-compat/produce-fixture.sh --project Contoso.Sdk2Compat   # contre le flux 2.0.0
```

## Tests

| Où | Quoi |
|---|---|
| `Tests/HackerOs.Templates.E2E.Tests` | empaquette tout (`pack-local`, ou le flux de `HACKEROS_LOCAL_FEED`), installe les modèles dans un `DOTNET_CLI_HOME` isolé, génère et compile chaque modèle d'app hors du dépôt avec un cache NuGet isolé (manifeste, genre, version minimale du SDK vérifiés) ; puis génère un hôte, y ajoute une app fenêtrée et une commande générées, le compile (catalogue agrégé vérifié), le lance, crée le profil dans Chromium, ouvre l'app (`Clicked 0 times` puis `1`) et exécute `hello Morpheus` dans le terminal (`Hello, Morpheus!`). |
| `Tests/HackerOs.Templates.E2E.Tests` (`A_host_runs_SDK_2_app_packages_and_refuses_SDK_1_x_packages`) | un hôte généré avec `PackageReference` vers les apps de compatibilité 1.0.0 et 1.1.0 (source `hackeros-sdk-fixtures`) ne se construit pas (`HOSAPP0021` pour chacune des quatre apps) ; avec l'app 2.0.0 à la place, il démarre dans Chromium, la fenêtre 2.0 affiche le contrôle de fichiers de l'OS et `sdk2-compat still binds` s'exécute dans le terminal (ADR 0149 §5, ADR 0161 §3) |
| `Tests/HackerOs.SdkCompatibility.Tests` | l'app de compatibilité 2.0.0 : admise par le catalogue courant, entrée de menu inscrite et `HackFileView` référencé, chaque type et membre HackerOS qu'elle référence existe, chaque méthode compile contre le SDK courant (JIT), la commande tourne ; les apps 1.0.0 et 1.1.0 : refusées (`catalog.sdk.incompatible`) (ADR 0149 §5, ADR 0161 §3) |
| `Tests/HackerOs.App.Abstractions.Tests` (`HackerOsSdkTests`) | lecture de la version, compatibilité des plages |
| `Tests/HackerOs.Tools.ManifestGenerator.Tests` | version du SDK lue dans la référence, empreintes et `HOSAPP0023`, `HOSAPP0022`, app plus récente que le SDK de l'hôte (`HOSAPP0021`) |
| `Tests/HackerOs.Platform.Core.Tests` | `AppCatalog` (refus, cascade des dépendances, propriétaire des commandes), `AppAssetStore`, `ShellCommandResolver` |
| `build/verify-hosts.sh` | les trois hôtes démarrent après l'extraction de `HackerOs.Ecosystem.Host` |

Vérifié à la main aussi : une app qui ajoute la référence
`HackerOs.Platform.Core` échoue avec `HOSAPP0030 Forbidden package`.

## Décisions

- **Une seule version** pour tout, plutôt qu'une version par paquet : le SDK, la
  plateforme et les apps évoluent ensemble ; depuis le gel, la promesse SemVer
  ne couvre que le SDK (ADR 0114 §2).
- **Le même `HackerOs.Apps.targets`** dans le dépôt et dans le paquet, avec un
  interrupteur `HackerOsBuildFromPackage`, pour qu'aucune règle ne diverge.
- **Frontière des paquets vérifiée sur les références résolues** (directes et
  transitives), pas seulement sur les `PackageReference` du projet : une
  bibliothèque intermédiaire ne peut pas faire entrer la plateforme dans une app.
- **Les apps en paquet portent leurs manifestes** : un hôte n'a jamais besoin du
  code source d'une app pour la cataloguer, comme `dpkg` lit le contrôle d'un
  `.deb`.
- **Cache NuGet purgé par `pack-local`** et cache isolé dans le test : une
  version `preview` ré-empaquetée n'est jamais servie périmée.
- **`HostAppCatalog` dans la bibliothèque**, le catalogue agrégé dans l'assembly
  de l'hôte : la bibliothèque ne connaît pas les apps d'un hôte.

## Tâches

- [x] `build/Versions.props`, version portée par chaque projet, `Packaging.props` sans version fixe.
- [x] 37 bibliothèques publiées avec métadonnées, analyseur d'API publique et listes `PublicAPI.*.txt` générées.
- [x] Documentation XML de chaque membre public (≈ 300 avertissements corrigés, aucune suppression).
- [x] `HackerOsSdk`, minimum écrit par le générateur, refus au chargement, état « incompatible » dans Paramètres.
- [x] `HOSAPP0022` et propriétaire unique d'une commande au chargement.
- [x] Empreintes des ressources (`HOSAPP0023`, `AppAssetStore`, état « endommagée »).
- [x] Retrait de `BuildProfileManifest`, de son validateur et de son sérialiseur.
- [x] Paquet `HackerOs.App.Build` et mode paquet de `HackerOs.Apps.targets` (frontière des paquets, agrégation des apps en paquet, manifestes dans les paquets d'apps).
- [x] `HackerOs.Ecosystem.Host` extrait, `AddHackerOsBrowserHost`, `HostAppCatalog`, jetons de design déplacés, trois hôtes mis à jour.
- [x] `build/pack-local.sh` / `.ps1`, `build/nuget.config.sample`.
- [x] Cinq modèles et leur test de bout en bout.
- [x] Gel du SDK 1.0 (`v3/15`, ADR 0114) : tout en `Shipped`, version `1.0.0`, API obsolètes retirées, app de compatibilité et ses tests, guide [`building-on-hackeros.md`](building-on-hackeros.md).
- [x] SDK 1.1 (`v3.1/4-sdk-1-1`, ADR 0117 §5) : `Unshipped` versé dans `Shipped`, version `1.1.0`, ruptures hors SDK listées, app de compatibilité 1.0 toujours chargée.
- [x] SDK 1.2 (`exec/10-final`, ADR 0132 §2) : `build/tools/ship-public-api.sh`, `Unshipped` versé dans `Shipped`, version `1.2.0`, rupture hors SDK listée, app de compatibilité 1.1.0 ajoutée, 1.0.0 et 1.1.0 chargées.
- [x] SDK 2.0 (`desk/6-sdk-2`, ADR 0149, ADR 0161) : `HackerOs.AppSdk.FileView` retiré du SDK (code dans l'explorateur), `HackFileOperations`, `Unshipped` versé dans `Shipped`, version `2.0.0`, ruptures hors SDK listées, app de compatibilité 2.0.0 ajoutée, apps 1.x refusées.
- [x] Documentation (ce fichier, `app-model`, `app-sdk`, `sdk-reference`, `sdk-developer-guide`, `building-and-testing`, `project-overview`, mesure des ressources, notes des documents archivés) et index.
