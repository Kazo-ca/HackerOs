# Modèle d'application : `AppDefinition` et manifestes générés

Phase 2 du [plan de refonte](../dev/plans/ecosystem-rebuild-plan.md) (sections 6.1 et 6.2),
décisions de l'[ADR 0047](../dev/adr/0047-app-definition-and-generated-manifests.md).

## Objectif

Le manifeste d'une app a une seule source : une classe C# marquée
`[AppManifest]`. Le build en tire le `manifest.json` de l'app, puis l'hôte
agrège tous les manifestes dans `BuiltInApplications.json`. Installer une app
dans l'hôte se résume à une `ProjectReference`.

## `AppDefinition` et `[AppManifest]`

Projet `Shared/HackerOs.App.Abstractions`, dossier `Definition/`, namespace
`HackerOs.App.Abstractions`.

| Type | Rôle |
| --- | --- |
| `AppManifestAttribute` | Marque la classe qui décrit une app. Usage unique, non hérité. Propriété nommée `Execution` (ADR 0070) : où l'OS exécute l'app (voir [Hôte d'exécution](#hôte-dexécution-adr-0070)). |
| `AppDefinition` | Classe de base abstraite : 4 membres obligatoires (`Id`, `Name`, `Kind`, `EntryPoint`) et des membres facultatifs avec valeur par défaut. |
| `FileHandler`, `TerminalCommand` | Formes courtes de `FileHandlerManifest` et `TerminalCommandManifest`. |
| `AppDefinitionMapper` | `ToManifest(definition)` convertit une définition en `AppManifest` (schéma version 1). |

Valeurs par défaut des membres facultatifs :

| Membre | Défaut |
| --- | --- |
| `Version` | `1.0.0` |
| `Description` | `Name` |
| `PublisherId` | `pub.local` |
| `Category` | `utilities` |
| `LaunchVisibility` | `Visible` |
| `Icons`, `Capabilities`, `FileHandlers`, `Dependencies`, `Intents`, `DeclaredTopicPermissions`, `Localizations`, `Assets` | liste vide |
| `Terminal`, `MobileEntryPoint`, `Settings` | `null` |
| `[RegisterFileContextMenu]` | aucune méthode ; `contextMenus` est alors omis du manifeste. Une méthode statique de la classe elle-même, exécutée deux fois au build, inscrit les entrées de l'app dans les menus des fichiers (ADR 0151, ADR 0159, [`context-menu.md`](context-menu.md#entrées-des-apps-dans-les-menus-des-fichiers-chantier-4)) ; titres dans `Resources/AppManifest.resx` |
| `Components` | liste vide ; `components` est alors omis du manifeste (ADR 0063, [`embedded-components.md`](embedded-components.md)) ; `hackeros.context-menu` est refusé (plateforme) ; `hackeros.file-view` et `hackeros.file-dialogs` sont ouverts à toute app fenêtrée, un administrateur choisit celle en service (ADR 0156, ADR 0158) |
| `SingleInstancePerUser`, `AutoStart`, `SupportsBack` | `false` |
| `ServiceStartup` | `null` (= `Login`) ; `serviceStartup` est alors omis du manifeste. `Boot` ne vaut que pour une app système de genre `Service` (ADR 0097 §4, ADR 0107) |
| `OomScoreAdjust` | `null` (hérite ; `-500` pour un service système) ; `oomScoreAdj` est alors omis du manifeste. Négatif seulement pour une app système (`HOSAPP0024`, ADR 0126 §4, ADR 0142, [`oom-killer.md`](../dev/oom-killer.md)) |
| `Resources` | `AppResourceProfileManifest.None` |
| `Window` | `null` : la fenêtre prend `WindowOptions.Default` (800×560, minimum 360×240) ; l'objet `window` est alors omis du manifeste (ADR 0095, [`window-model.md`](window-model.md)). Déclaré seulement par une app de genre `Window` (`manifest.window.*`). |

Depuis l'ADR 0165, les quatorze apps système fenêtrées déclarent
`MobileEntryPoint` avec le même composant que `EntryPoint`. Le générateur
produit donc la forme `platform` (`desktop`, `mobile`) pour ces apps ; le
tiroir mobile ne contourne jamais une déclaration de compatibilité absente.
Les apps tierces restent desktop seulement tant qu'elles ne déclarent pas
leur point d'entrée mobile.

Exemple :

```csharp
[AppManifest]
public sealed class TextEditorApp : AppDefinition
{
    public override string Id => "org.hackeros.text-editor";
    public override string Name => "Text Editor";
    public override AppKind Kind => AppKind.Window;
    public override Type EntryPoint => typeof(TextEditorWindow);
    public override Type? MobileEntryPoint => typeof(TextEditorWindow);
    public override string PublisherId => "pub.hackeros";
    public override IReadOnlyList<string> Capabilities =>
        [AppCapabilities.FileSystemUserHomeRead, AppCapabilities.FileSystemUserHomeWrite];
    public override IReadOnlyList<FileHandler> FileHandlers =>
        [new("text/plain", [".txt", ".md", ".log"], ["open", "edit"])];
}
```

### Règles de conversion

- `EntryPoint` devient `{ "assembly": "<assembly>.dll", "type": "<nom complet>" }`.
- `MobileEntryPoint` non nul produit la forme `platform` (`desktop` et `mobile`).
  Si c'est le même type que `EntryPoint`, une seule entrée couvre les deux
  plateformes ; sinon, une entrée par plateforme.
- `Icons` devient `presentation.iconAssetPaths`.
  Pour le chrome et la taskbar (ADR 0164), la première image est résolue sous
  `_content/<assembly>/…` ; le fichier doit être dans `wwwroot/` et déclaré dans
  `Assets`. Les apps système fenêtrées déclarent chacune `icons/app.svg`, avec
  une empreinte calculée au build. macOS masque seulement l'icône de la barre
  de titre ; son dock garde l'icône de l'app.
- `TerminalCommand.Aliases` nul devient une liste vide.
- `sdkCompatibility` (ADR 0098 §3) : par défaut, `minimumVersion` est la version
  du SDK contre laquelle l'app a été compilée. Le générateur la lit dans
  l'`AssemblyInformationalVersion` de la `HackerOs.App.Abstractions.dll`
  référencée par l'app (par exemple `2.0.0`, voir
  [`sdk-packages.md`](sdk-packages.md)) et la passe à
  `ToManifest(definition, sdkMinimumVersion)` ; `ToManifest(definition)` prend
  `HackerOsSdk.Version`, la version du SDK chargé. Une app qui veut une plage
  surcharge `AppDefinition.SdkCompatibility` (minimum et maximum facultatif).
  Au chargement, `AppCatalog` refuse une app dont la plage n'accepte pas le SDK
  de l'hôte (`catalog.sdk.incompatible`) : même version majeure que le minimum,
  minimum ≤ SDK ≤ maximum (`HackerOsSdk.IsCompatible`).
- `osCompatibility` et `update` ne sont pas exposés par `AppDefinition` : ils
  restent absents du manifeste généré.
- `[AppManifest(Execution = …)]` devient `execution`, toujours écrit, résolu
  (jamais `default`) : voir la section suivante.

### Intents et schémas d'URI (ADR 0096)

- `Intents` (`AppIntentDeclarationManifest(IntentId, PayloadSchemaAssetPath)`)
  déclare les intents que l'app **reçoit**. Un identifiant libre doit être bien
  formé (`domaine.inversé.nom.vN`, `AppIntentIds.IsWellFormed`) ; le
  répartiteur route vers les apps installées et activées qui le déclarent, au
  lieu de répondre `intent.unknown`. Déclarer
  `org.hackeros.intent.show-app-settings.v1` signifie « j'ai ma propre page de
  réglages » : l'app est lancée avec `--show-settings`.
- Un schéma d'URI est un type de média `x-scheme-handler/<schéma>` dans
  `FileHandlers`, comme sous freedesktop :
  `new("x-scheme-handler/https", [], ["open"])`. Le navigateur déclare `http` et
  `https`.
- Le schéma du manifeste ne change pas. Détails : [`app-intents.md`](app-intents.md).

### Hôte d'exécution (ADR 0070)

L'attribut `[AppManifest]` dit **où** l'OS exécute l'app, par sa propriété
nommée `Execution` (`AppExecutionHost`, `HackerOs.App.Abstractions`) :

| Valeur | Champ `execution` | Où | Défaut de |
| --- | --- | --- | --- |
| `MainThread` | `mainThread` | le thread principal du navigateur, avec le bureau | app à fenêtre (la seule valeur permise : elle a besoin du DOM) |
| `Pool` | `pool` | un worker du pool, un par cœur virtuel ; les étapes d'un même pipeline vont sur le même | commande |
| `Dedicated` | `dedicated` | un worker à lui, pris au worker prêt (ADR 0071) | service |
| `Web` | `web` | le worker `web`, partagé par tous les sites (ADR 0073 §1) | app web (`AppKind.Web`, la seule à pouvoir l'utiliser) |
| `Default` | — | laissé tel quel dans l'attribut : le générateur écrit le défaut du genre | — |

```csharp
// ADR 0070: stays on the main thread: it acts on the calling shell (its working directory),
// like the cd builtin of bash.
[AppManifest(Execution = AppExecutionHost.MainThread)]
public sealed class CdApp : AppDefinition { … }
```

- Le mappeur lit l'attribut de la classe de définition et écrit la valeur
  résolue (`AppExecutionHosts.Resolve`) ; une définition sans attribut (tests)
  reçoit le défaut de son genre.
- `AppManifest.Execution` est `null` pour un manifeste écrit avant l'ADR 0070 :
  `AppExecutionHosts.Of(manifest)` donne alors le défaut du genre.
- Le validateur refuse `pool` ou `dedicated` pour une app à fenêtre
  (`manifest.execution.window`) et la valeur `default`
  (`manifest.execution.invalid`) ; le schéma et la liste blanche de
  `AppManifestJsonSerializer.DeserializeStrict` connaissent `execution`.
- Une commande ou un service qui a besoin de ce qu'un worker n'offre pas déclare
  `MainThread`, **avec la raison en commentaire** au-dessus de l'attribut. Dans
  `HackerOs.Commands` : les built-ins du shell (`cd`, `exit`, `export`,
  `unset`, `jobs`, `read`, `help`, `which`, `kill`, `sudo`, `watch`), `passwd`,
  `hackbackup`, `nice` et `renice` (ADR 0127), et les commandes plein écran
  (`nano`, `top`, `htop`). Le service d'exemple et
  `hackwebd` sont en worker dédié depuis l'ADR 0124. Les autres commandes vont
  dans le pool (liste à jour dans [`worker-processes.md`](../dev/worker-processes.md)).
- Le validateur refuse `pool` et `dedicated` pour une app web
  (`manifest.execution.web`) et `web` pour toute autre app
  (`manifest.execution.web-only`). Une app web déclare ses URL par
  `AppDefinition.Urls` (champ `web.urls`) : voir
  [`listening-ports-and-dns.md`](listening-ports-and-dns.md).
- Un hôte sans Web Worker ignore `execution` (ADR 0070 §6). Il n'y en a plus
  dans ce dépôt : `HackerOs.Server` sert l'OS en WebAssembly depuis
  l'[ADR 0130](../dev/adr/0130-server-serves-webassembly-os-and-api.md).

Détails du mécanisme : [`worker-processes.md`](../dev/worker-processes.md).

### Écart avec le texte de l'ADR

L'ADR 0047 écrit `SettingsSchema? Settings`. `SettingsSchema` vit dans
`HackerOs.Simulation.Abstractions`, que `HackerOs.App.Abstractions` ne référence
pas. La propriété utilise donc l'enregistrement de manifeste existant
`AppSettingsSchemaManifest?`, conformément à la règle de l'ADR « les cas rares
réutilisent les enregistrements `*Manifest` existants ». Le contenu JSON est le
même.

### Règles imposées à la classe

Vérifiées par le générateur (voir plus bas) : classe `public`, non abstraite,
marquée `[AppManifest]`, constructeur public sans paramètre ; `EntryPoint` et
`MobileEntryPoint` dans la même assembly que la définition ; getters purs (le
générateur compare deux instanciations) ; `AppManifestValidator` et les règles
de point d'entrée de `AppEntryPointDiscovery` passent.

## Génération de `manifest.json` au build

### Outil

`Tools/HackerOs.Tools.ManifestGenerator` (exécutable .NET, framework partagé
`Microsoft.AspNetCore.App`) remplace l'ancien `HackerOs.Tools.ManifestValidator`.

| Commande | Rôle |
| --- | --- |
| `generate --assembly <dll> --project <csproj> --stamp <fichier> [--dependencies <liste>]` | Appelée par MSBuild après la compilation d'une app. |
| `aggregate --project <hôte.csproj> --apps <liste> --output <BuiltInApplications.json> [--trimmer-roots <racines.xml>]` | Appelée par l'hôte (voir plus bas). |
| `validate <manifest.json>` | Lecture stricte (schéma v1) et `AppManifestValidator` sur un fichier ; remplace l'ancien validateur. |

Fonctionnement de `generate` :

1. charge l'assembly compilée dans un `AssemblyLoadContext` isolé et collectable
   (`AppAssemblyLoadContext`). Les assemblies du framework partagé et les
   contrats HackerOS de l'outil viennent du contexte par défaut (une seule
   identité pour `AppDefinition`) ; les autres viennent de la liste
   `@(ReferenceCopyLocalPaths)` + `@(ReferencePath)` puis du dossier de sortie
   de l'app. Les assemblies de référence (`[ReferenceAssembly]`) sont ignorées ;
2. `AppDefinitionInspector` trouve les classes `[AppManifest]` (par nom
   d'attribut), applique les règles, instancie chaque classe **deux fois** et
   compare le JSON canonique ;
3. si **aucune** erreur, `ManifestWriter` écrit les fichiers (sinon rien n'est
   écrit) ; un fichier n'est réécrit que si ses octets changent ;
4. le fichier témoin `obj/<config>/<tfm>/hackeros-app-manifests.txt` liste les
   manifestes produits.

### Fichiers produits

- une classe `[AppManifest]` → `manifest.json` à la racine du projet ;
- plusieurs classes → `manifests/<appId>.json` ; passer d'une forme à l'autre
  supprime les fichiers de l'ancienne forme.

Le JSON est le JSON canonique de l'ADR 0010 (`AppManifestJsonSerializer.SerializeCanonical`),
schéma version 1, sans champ ajouté. Les fichiers sont **suivis par git** (un
changement de capacité se voit dans la PR) et ne sont **pas** du contenu MSBuild
(`Content Remove`) : ils ne sont jamais copiés ni publiés. Une modification à la
main est écrasée au build suivant.

### Intégration MSBuild

`build/HackerOs.Apps.targets`, importé par `Directory.Build.targets` :

| Élément | Rôle |
| --- | --- |
| `$(HackerOsAppProject)` | `true` par défaut sous `Apps/` ; un projet ailleurs la positionne lui-même. Il n'y a pas d'exception : un projet sous `Apps/` sans classe `[AppManifest]` échoue avec `HOSAPP0001`. |
| `ProjectReference` vers le générateur | Ajoutée automatiquement aux projets d'app pour l'ordre de build (`ReferenceOutputAssembly=false`, `PrivateAssets=all` : l'outil n'entre jamais dans le graphe d'un hôte WebAssembly). |
| Cible `GenerateHackerOsAppManifests` | Après `CopyFilesToOutputDirectory`. Incrémentale : entrées = assembly compilée, outil, liste des dépendances, manifestes existants ; sortie = fichier témoin. |
| Cible `GetHackerOsAppManifests` | Cible de requête pour l'hôte : les manifestes du projet, avec les métadonnées `AssemblyName`, `AssemblyFileName` et `AppProject`. Vide pour un projet qui n'est pas une app. |

### Codes d'erreur

Chaque erreur est une erreur MSBuild au format canonique, attribuée au `.csproj`,
avec le nom complet de la classe en cause : `App.csproj : error HOSAPP0005: [Ns.MyApp] ...`.

| Code | Règle |
| --- | --- |
| `HOSAPP0001` | Aucune classe `[AppManifest]` dans le projet d'app. |
| `HOSAPP0002` | Classe non publique. |
| `HOSAPP0003` | Classe abstraite. |
| `HOSAPP0004` | Ne dérive pas de `AppDefinition`. |
| `HOSAPP0005` | Pas de constructeur public sans paramètre. |
| `HOSAPP0006` | Le constructeur ou un getter lève une exception. |
| `HOSAPP0007` | `EntryPoint`/`MobileEntryPoint` nul ou dans une autre assembly. |
| `HOSAPP0008` | Getters non déterministes (deux instances, deux manifestes). |
| `HOSAPP0009` | `AppManifestValidator` refuse le manifeste (le code du validateur est rappelé). |
| `HOSAPP0010` | Règles de `AppEntryPointDiscovery` : type concret, classe de base selon le genre, `IAppBackHandler` si `SupportsBack` ; vérifiées pour chaque plateforme déclarée. |
| `HOSAPP0011` | Deux classes de la même assembly déclarent le même identifiant. |
| `HOSAPP0020` | Agrégation : fichier manifeste absent ou JSON non strict. |
| `HOSAPP0021` | Agrégation : catalogue invalide (identifiant en double, dépendance manquante ou cyclique) ou app que le SDK de l'hôte refuserait (`catalog.sdk.incompatible`, ADR 0098 §3). |
| `HOSAPP0022` | Agrégation : nom de commande ou alias déclaré par deux apps de l'hôte (comparaison sans casse), ou nom que le shell exécute lui-même (`ShellInterpreter.ReservedNames`) (ADR 0098 §4). |
| `HOSAPP0023` | Génération : ressource déclarée dans `Assets` absente de `wwwroot/` du projet ; son SHA-256 ne peut pas être calculé (ADR 0098 §5). |
| `HOSAPP0024` | Agrégation : app non système qui déclare `OomScoreAdjust` négatif (ADR 0126 §4, ADR 0142 §6, [`oom-killer.md`](../dev/oom-killer.md)). |
| `HOSAPP0025` | Retiré, jamais réutilisé : refusait à une app non système `hackeros.file-view` et `hackeros.file-dialogs` (ADR 0153 §4), que l'ADR 0156 ouvre à toute app fenêtrée (ADR 0158 §1). |
| `HOSAPP0030` | Frontière du SDK : `ProjectReference` interdite dans un projet d'app (ADR 0055 §2, numéro fixé par l'ADR 0059). Émis par la cible `ValidateHackerOsSdkBoundary`. Voir [`app-sdk.md`](app-sdk.md). |
| `HOSAPP0031` | Frontière du SDK : service injecté qui n'est ni un type du SDK, ni MudBlazor, ni `IJSRuntime` (ADR 0055 §3, ADR 0059). Voir [`app-sdk.md`](app-sdk.md). |
| `HOSAPP0040` | Traductions du manifeste (ADR 0104 §4) : `Resources/AppManifest.<culture>.resx` illisible, nom de culture invalide, clé autre que `Name`, `Description`, `Terminal.Usage` (préfixées de l'id dans un projet à plusieurs apps), `Terminal.Usage` pour une app qui n'est pas une commande, ou fichier `localization/` référencé illisible à l'agrégation. Voir [`localization.md`](localization.md). |
| `HOSAPP0090` | Arguments de l'outil invalides. |
| `HOSAPP0091` | Assembly de l'app impossible à charger. |
| `HOSAPP0099` | L'outil a échoué sans diagnostic (plantage). |

Les dépendances entre apps ne sont pas vérifiées à la génération (une seule app
est connue) : elles le sont à l'agrégation dans l'hôte.

### Migration des apps

Les 44 projets sous `Apps/` (12 apps, 28 commandes, 4 exemples) ont leur classe
`[AppManifest]` et leur `manifest.json` généré ; il ne reste aucun
`app.manifest.json` écrit à la main ni aucun champ `StaticManifest`.
`Apps/System/HackerOs.Apps.Calculator` (`CalculatorApp`) a servi d'exemple.

Convention de nommage de la classe : nom du point d'entrée sans le suffixe
`Window` ou `Command`, suivi de `App` (`CalculatorWindow` → `CalculatorApp`,
`RmCommand` → `RmApp`). Les exemples, dont le point d'entrée porte déjà ce genre
de nom, utilisent `<Projet>SampleApp` (`TerminalSampleApp`, `WindowSampleApp`,
`ServiceSampleApp`, `PlatformSampleApp`). La classe est dans le namespace de son
point d'entrée.

Les valeurs viennent de l'ancien `app.manifest.json`, qui faisait foi au runtime :
quand un `StaticManifest` C# divergeait (profil de ressources `None`, description
abrégée), c'est le JSON qui a été repris. Le code et les tests qui lisaient
`StaticManifest` utilisent `AppDefinitionMapper.ToManifest(new XxxApp())` ; c'est
aussi le cas des constructeurs sans paramètre de `SampleTerminalApp` et
`SampleTickerService`.

Vérification : un test de migration temporaire
(`LegacyManifestMigrationTests`, commit `feat(app-model): [AppManifest] classes
for the 43 remaining apps`) a comparé, pour les 43 apps, le `manifest.json`
généré et l'ancien fichier après lecture stricte, normalisation des collections
facultatives et sérialisation canonique : 43 égaux. Il a été supprimé avec les
anciens fichiers. Seul écart corrigé avant comparaison : le `app.manifest.json`
de l'exemple TerminalApp utilisait les clés hors schéma `commandName` et
`helpSummary` (refusées en lecture stricte) ; ce sont `name` et `usage`, déjà
utilisées par son `StaticManifest`.

### Commandes intégrées : un seul projet (ADR 0051)

Depuis le chantier `rebuild/3-commands-merge`, les commandes intégrées (28, puis
29 avec `trash` du chantier `rebuild/3-trash`, voir [`trash.md`](../dev/trash.md)) sont
dans un seul projet, `Apps/Commands/HackerOs.Commands/HackerOs.Commands.csproj` :

- un dossier par commande (`Cat/CatApp.cs`, `Cat/CatCommand.cs`), chacune garde
  son espace de noms (`HackerOs.Commands.Cat`) et sa classe `[AppManifest]` ;
- le générateur écrit `manifests/<appId>.json` (forme « plusieurs classes ») ;
  seul `entryPoint.assembly` change, il vaut `HackerOs.Commands.dll` ;
- **les identifiants d'app ne changent pas** (`org.hackeros.cmd.cat`,
  `org.hackeros.commands.curl`…) : `apps.config`, `app-permissions.config` et
  les alias existants restent valides ;
- l'hôte n'a qu'une `ProjectReference ... IsSystemApp="true"` pour toutes les
  commandes ; l'assembly est chargée à la demande en un seul bloc
  (`BuildKnownLazyAssemblies.Names` la contient une seule fois) ;
- les projets de tests (`HackerOs.Commands.Tests`, `HackerOs.Commands.Wave5.Tests`,
  `HackerOs.Commands.Nano.Tests`, `HackerOs.Network.Tests`) référencent ce projet.

`cd`, `pwd`, `clear`, `help` et `sudo` sont installées et résolues comme les
autres commandes : le terminal ne connaît aucun nom (chantier
`rebuild/3-shell-commands`, ADR 0051 sections 2 à 5, voir
[`shell-commands.md`](../dev/shell-commands.md)). Celles qui agissent sur le shell
déclarent la capacité `shell.control`. Une commande tierce reste un projet à part (modèle :
`Apps/Samples/HackerOs.Samples.TerminalApp`).

### Tests

`Tests/HackerOs.Tools.ManifestGenerator.Tests` :

- `AppDefinitionInspectorTests` : un test par code `HOSAPP0001` à `HOSAPP0011`
  sur des définitions de test ;
- `ManifestWriterTests` : formes un/plusieurs fichiers, pas de réécriture si
  identique, écrasement d'une modification à la main ;
- `MsBuildIntegrationTests` : build réel des projets hors solution
  `Tests/ManifestGenerator.Fixtures.SingleApp`, `.MultiApp` et `.NoDefinition`
  (`HOSAPP0001`).

### Traductions du manifeste (ADR 0104 §4)

Après l'inspection, `generate` lit `Resources/AppManifest.<culture>.resx`
(`ManifestLocalizationGenerator`) : clés `Name`, `Description`,
`Terminal.Usage`, préfixées de l'id de l'app dans un projet à plusieurs apps
(`org.hackeros.commands.ls.Description`). Une clé absente garde le texte
anglais. Il écrit `localization/<culture>.json` (une app) ou
`localization/<appId>/<culture>.json` (plusieurs apps), au format
`{"name","description","terminalUsage"?}`, ajoute l'entrée
`localizations: [{"culture":"fr","resourcePath":"localization/fr.json"}]` et
supprime les fichiers périmés. Ces `.resx` ne sont pas compilés dans
l'assembly ; les fichiers générés sont committés comme `manifest.json` et ne
sont pas déclarés comme assets. Erreur : `HOSAPP0040`. Voir
[`localization.md`](localization.md).

## Agrégation dans l'hôte : `BuiltInApplications.json`

### Déclarer un hôte

Un projet hôte déclare `<HackerOsAppHost>true</HackerOsAppHost>`. Dans ce dépôt,
seul `OS/HackerOs.Ecosystem` le fait. `test/test` réutilise le catalogue
compilé de l'Ecosystem (`BuildKnownLazyApps.Catalog`) et ne s'agrège pas
lui-même ; le serveur sert les fichiers publiés de l'Ecosystem tels quels, apps
comprises (ADR 0130). Hors du dépôt, un hôte référence le paquet
`HackerOs.App.Build` et déclare la même propriété (modèle `hackeros-host`, voir
[`sdk-packages.md`](sdk-packages.md)).

### Cible `CollectHackerOsBuiltInApplications`

Dans `build/HackerOs.Apps.targets`, après `ResolveProjectReferences` (donc avant
la préparation des ressources et avant les cibles Blazor) :

1. pour chaque `ProjectReference` directe (hors références à
   `ReferenceOutputAssembly=false`), appelle `GetHackerOsAppManifests` avec les
   mêmes propriétés globales que `ResolveProjectReferences` ; un projet qui
   n'est pas une app ne renvoie rien et est ignoré ;
2. associe à chaque manifeste la métadonnée `IsSystemApp` de sa `ProjectReference`
   (vide = `false`, sens fixé par l'ADR 0048) ;
3. appelle `ManifestGenerator aggregate`, qui relit chaque manifeste en mode strict,
   le valide, construit le catalogue avec `AppCatalog.Build` (identifiant en
   double, dépendance manquante ou cyclique = `HOSAPP0021`) et écrit
   `obj/<config>/<tfm>/BuiltInApplications.json` seulement si ses octets changent
   (pas de recompilation inutile) ; elle y mesure aussi l'**image** de chaque
   app (ADR 0068) à partir du chemin de son assembly et de sa liste de
   dépendances, que `GetHackerOsAppManifests` renvoie (`AssemblyPath`,
   `DependenciesFile`) ; enfin elle recopie les textes traduits que référence
   `localizations` dans la propriété facultative `localized` de chaque entrée
   (`{"fr":{"name":…}}`, ADR 0104), que `AppCatalog.Localize` applique ;
4. ajoute ce fichier en `EmbeddedResource` (`HackerOs.BuiltInApplications.json`) ;
5. ajoute un `BlazorWebAssemblyLazyLoad` pour l'assembly de chaque app (en
   retirant d'abord un éventuel doublon écrit à la main) ;
6. passe à ILLink le fichier `obj/<config>/<tfm>/HackerOs.TrimmerRoots.xml` que
   la même commande écrit (`--trimmer-roots`) : chaque assembly nommée par un
   manifeste y est gardée entière pour une publication trimmée (ADR 0106,
   ADR 0113, [`trimmed-publish.md`](../dev/trimmed-publish.md)).

Les apps installées en **paquet** (ADR 0098 §2) suivent le même chemin : chaque
paquet d'app contient ses manifestes sous `hackeros/manifests/` (leurs traductions
sous `hackeros/localization/`) et un fichier
`buildTransitive/<PackageId>.targets` qui les déclare en éléments
`HackerOsPackagedApp` (métadonnées `PackageId`, `AssemblyFileName`,
`AssemblyPath`). La cible `_HackerOsCollectPackagedAppManifests` les ajoute à
la liste ; `IsSystemApp` vient de la `PackageReference` de l'hôte sur ce paquet
(un paquet d'app arrivé par dépendance n'est jamais système). La cible
`_HackerOsPackageAppManifests` produit ces fichiers quand un projet d'app est
empaqueté.

L'ancrage après `ResolveProjectReferences` est vérifié par un
`dotnet publish -c Release` de l'Ecosystem : `HackerOs.Apps.Calculator` figure
dans la section `lazyAssembly` de la configuration de démarrage publiée, et le
générateur n'apparaît pas dans `_framework`.

### Format

```json
{"schemaVersion":1,"applications":[{"isSystemApp":false,"manifest":{"schemaVersion":1,"id":"org.hackeros.calculator", "...": "..."},"image":{"assemblies":[{"name":"HackerOs.Apps.Calculator.dll","bytes":17408,"shared":false},{"name":"HackerOs.AppSdk.dll","bytes":400896,"shared":true}]}}]}
```

`image` (facultatif, ADR 0068) liste les assemblies que l'app mappe, triées par
nom, avec leur taille : la sienne (`shared: false`, `VmExe`) et les
bibliothèques qu'elle référence, directement ou non (`shared: true`), hors
runtime et framework .NET (`System.*`, `Microsoft.*`). Le calcul est fait par
`AppImageInspector` (métadonnées seulement, aucun code chargé) ; voir
[`memory-accounting.md`](../dev/memory-accounting.md). Le `manifest.json` versionné
ne change pas : les tailles ne vivent que dans ce fichier produit à chaque
build.

`BuiltInApplicationsJsonSerializer` (`HackerOs.App.Abstractions`, à côté
d'`AppManifestJsonSerializer`) écrit la forme canonique (applications triées par
identifiant, manifestes canoniques) et relit en mode strict (propriété inconnue
ou en double, `schemaVersion` autre que 1, `isSystemApp` non booléen = erreur ;
chaque manifeste passe par `AppManifestJsonSerializer.DeserializeStrict`). Le
type `BuiltInApplication(IsSystemApp, Manifest, Image)` porte une entrée
(`Image` vaut `null` quand le champ est absent). Le fichier
n'est jamais écrit à la main ni versionné.

### Lecture au démarrage

`HostAppCatalog.Load(assembly)` (`HackerOs.Ecosystem.Host`) lit la ressource
`HackerOs.BuiltInApplications.json` de l'assembly de l'hôte et construit
`Catalog` avec `AppCatalog.BuildFromBuiltInApplications` : le catalogue porte
les drapeaux système (voir « Apps système » ci-dessous) ; `Images` donne l'image
de chaque app (lue par la mesure de la mémoire) et `LazyAssemblyNames` les seules
assemblies que le chargeur paresseux peut récupérer. Dans l'Ecosystem,
`BuildKnownLazyApps` (`Catalog`, `Images`, `Host`) et
`BuildKnownLazyAssemblies.Names` en sont de simples façades sur sa propre
assembly. Seul le
JSON est lu : les DLL d'apps restent chargées au premier lancement et les
classes `AppDefinition` ne sont jamais instanciées au runtime.

C'est la seule source : `HackerOs.Ecosystem.csproj` n'embarque plus de manifeste
à la main (`HackerOs.Ecosystem.Manifests.*`) et n'a plus de liste
`BlazorWebAssemblyLazyLoad`, et la cible de transition
`_HackerOsMarkLegacySystemManifests` a disparu avec elles : `IsSystemApp` vient
seulement de `BuiltInApplications.json`. Une ressource absente est une erreur au démarrage.

### Installer une app

Une fois l'app dotée de sa classe `[AppManifest]` : une `ProjectReference` dans
`HackerOs.Ecosystem.csproj`, avec `IsSystemApp="true"` si l'hôte la déclare
système. Rien d'autre.

### Tests

- `BuiltInApplicationsJsonSerializerTests` (`HackerOs.App.Abstractions.Tests`) :
  tri et aller-retour canonique, document vide, normalisation, rejets stricts.
- `BuiltInApplicationsAggregatorTests` : `IsSystemApp` reporté, identifiant en
  double refusé (`HOSAPP0021`), manifeste absent ou invalide (`HOSAPP0020`),
  analyse de la liste.
- `MsBuildIntegrationTests.Host_embeds_BuiltInApplications_...` : build réel de
  `Tests/ManifestGenerator.Fixtures.Host` (une app système, un projet à deux apps,
  un projet qui n'est pas une app, ignoré).
- `BuildKnownLazyAppsTests` (`HackerOs.Ecosystem.Tests`) : le catalogue contient
  toutes les apps de l'hôte (75 aujourd'hui : 12 fenêtrées et 63 commandes, `stress` depuis l'ADR 0070) ; toutes viennent de la ressource agrégée (aucune ressource
  `HackerOs.Ecosystem.Manifests.*`), et la liste de chargement différé est
  exactement celle de leurs assemblies.

## Version du SDK, commandes, intégrité (ADR 0098 §3-5)

- **Version du SDK** : voir « Règles de conversion ». `AppCatalog.Build` met de côté
  les apps refusées dans `AppCatalog.Rejected` (code `catalog.sdk.incompatible`, et
  `catalog.dependency.unavailable` pour une app qui en dépend obligatoirement) au
  lieu d'échouer : l'OS démarre sans elles, le démarrage l'écrit dans
  `/var/log/syslog` (catégorie `catalog`) et Paramètres les liste comme
  incompatibles avec la raison (`HackAppInstallState.Incompatible`, `Problem`).
- **Commandes en double** : refusées au build (`HOSAPP0022`). Au chargement, si deux
  apps déclarent quand même le même nom, une app système l'emporte, sinon la
  première activée ; l'autre perd ce nom (`AppCatalog.FindCommandOwner`,
  `CommandOwners`), un avertissement `catalog.command.duplicate` va dans
  `/var/log/syslog`, et `ShellCommandResolver` ne résout que le propriétaire.
- **Intégrité des ressources** : pour chaque entrée de `Assets`, le générateur calcule
  le SHA-256 du fichier `wwwroot/<chemin>` du projet et l'écrit dans le manifeste
  (`HOSAPP0023` s'il manque) ; le générateur relance quand un fichier de
  `wwwroot/` change. Au runtime, `AppAssetStore` (Platform.Core) lit une ressource
  par sa source (`IAppAssetSource` ; dans le navigateur
  `StaticWebAssetAppAssetSource`, `_content/<assembly>/<chemin>`) et vérifie son
  empreinte à sa **première** lecture de la session, comme `dpkg --verify`. Une
  ressource altérée est refusée (`AppAssetIntegrityException`), l'app est marquée
  endommagée (`HackAppInstallState.Damaged` dans Paramètres) et l'événement
  `catalog.asset.damaged` va dans `/var/log/syslog`. Les assemblies restent
  couvertes par l'intégrité du manifeste de démarrage de Blazor.

## Apps système (ADR 0048 §1)

### Source de confiance

Le statut système se déclare **uniquement** sur la `ProjectReference` de l'hôte
(`IsSystemApp="true"`), recopié par l'agrégation dans `isSystemApp`. Ni
`AppDefinition`, ni `[AppManifest]`, ni le schéma de `manifest.json` n'ont de champ
pour cela : un `manifest.json` qui contient `isSystemApp` est rejeté par le
désérialiseur strict. Une app installée au runtime par le futur magasin sera donc
toujours non système.

### API

`AppCatalog` (`Platform/HackerOs.Platform.Core/AppCatalog.cs`) :

| Membre | Rôle |
|---|---|
| `SystemAppIds` | ensemble (ordinal, figé) des identifiants système du catalogue |
| `IsSystemApp(appId)` | `true` si l'hôte a déclaré l'app système |
| `Build(manifests)` | catalogue sans app système (tests, catalogue vide) |
| `Build(manifests, systemAppIds)` | catalogue avec la liste système de l'hôte |
| `BuildFromBuiltInApplications(applications)` | raccourci depuis les entrées de `BuiltInApplications.json` |

Un identifiant système absent des manifestes fait échouer la construction
(`catalog.system-app.unknown`) : une faute de frappe ne peut pas laisser une app
sans protection. `BuildKnownLazyApps.Catalog` est le catalogue injecté par les
hôtes (`Program.cs`) ; les services de l'OS lisent donc
`AppCatalog.IsSystemApp` depuis l'injection de dépendances.

### Apps système de l'hôte Ecosystem

| Système | Non système (désinstallables) |
|---|---|
| Terminal, File Explorer, Settings, Text Editor, Code Editor (depuis `rebuild/4-sdk-shell-host` : son terminal héberge un shell, ADR 0056 §7), System Monitor, Error Log Viewer, Theme Editor (lit `/etc/hackeros/themes`, ADR 0103), et les 70 commandes (projet `HackerOs.Commands`, `xdg-open` comprise depuis `v3/4`, `xdg-mime` depuis `v3/4b`, `passwd` depuis `v3/8`) | Browser, Calculator, Diagnostic, HackPaint, Icon Viewer |

Effets du statut : une app système n'est pas désinstallable (voir
[`app-installation.md`](../dev/app-installation.md), ADR 0048 §2-6) ; ses capacités
dangereuses accordées d'office arrivent avec l'ADR 0049.

### Tests

- `AppCatalogTests` (`HackerOs.Platform.Core.Tests`) : pas d'app système par
  défaut, ensemble exposé et comparaison ordinale, identifiant inconnu refusé,
  drapeau pris de chaque entrée `BuiltInApplication`.
- `SystemAppTrustTests` (`HackerOs.App.Abstractions.Tests`) : aucun membre
  « SystemApp » sur `AppDefinition`, `AppManifest`, `[AppManifest]` ; un
  `manifest.json` qui revendique `isSystemApp` est rejeté.
- `BuildKnownLazyAppsTests.System_apps_follow_the_host_ProjectReference_metadata`
  (`HackerOs.Ecosystem.Tests`) : l'ensemble système du catalogue de l'hôte est
  exactement la table ci-dessus.

## Tâches

- [x] `rebuild/2-app-definition` : `AppDefinition`, `[AppManifest]`,
  `FileHandler`, `TerminalCommand`, `AppDefinitionMapper`, tests du mappeur
  (`Tests/HackerOs.App.Abstractions.Tests/Definition`).
- [x] `rebuild/2-manifest-generator` : `Tools/HackerOs.Tools.ManifestGenerator` (`generate`, `validate`), `build/HackerOs.Apps.targets`, cibles `GenerateHackerOsAppManifests` et `GetHackerOsAppManifests`, erreurs `HOSAPP*`, projets de test, Calculator migrée en exemple.
- [x] `rebuild/2-builtin-applications` : cible `CollectHackerOsBuiltInApplications`, `BuiltInApplications.json` embarqué, lazy load automatique, `BuildKnownLazyApps` lit le nouveau fichier, `IsSystemApp` transporté.
- [x] `rebuild/2-system-apps` (ADR 0048) : `AppCatalog.SystemAppIds` et `IsSystemApp`, `AppCatalog.Build(manifests, systemAppIds)` et `BuildFromBuiltInApplications`, `IsSystemApp="true"` posé sur les `ProjectReference` de l'Ecosystem, pont de transition `_HackerOsMarkLegacySystemManifests`, tests.
- [x] `rebuild/2-app-uninstall` (ADR 0048 §2-6) : désinstallation par `apps.config`, voir [`app-installation.md`](../dev/app-installation.md).
- [x] `rebuild/5-worker-processes` (ADR 0070) : `AppManifestAttribute.Execution`, `AppExecutionHost`, champ `execution` (schéma, liste blanche, validateur, fixture canonique), défauts par genre dans le mappeur, chaque commande et service déclare son hôte ou reçoit le défaut.
- [x] `v3/6-sdk-versioning` (ADR 0098) : version du SDK écrite par le générateur et vérifiée au chargement, `HOSAPP0022`/`HOSAPP0023`, propriétaire unique d'une commande, empreintes des ressources et `AppAssetStore`, agrégation des apps en paquet, `HostAppCatalog`.
- [x] `v3/4-intents` (ADR 0096) : intents déclarés reçus par l'app, schémas d'URI `x-scheme-handler/<schéma>`, commande `xdg-open`.
- [x] `v3/4b` (ADR 0121) : associations de fichiers et de schémas propres à chaque utilisateur, commande `xdg-mime`.
- [x] `v3/12-localization` (ADR 0104) : `AppManifest.<culture>.resx` → `localization/`, `HOSAPP0040`, propriété `localized` de `BuiltInApplications.json`, `AppCatalog.Localize`.
- [x] `exec/3-oom-killer` (ADR 0126, ADR 0142) : `AppDefinition.OomScoreAdjust` (SDK 1.2), champ `oomScoreAdj`, `HOSAPP0024`.
- [x] `rebuild/2-app-migration` : classes `[AppManifest]` pour les 43 autres apps, test d'égalité avec les anciens JSON, suppression des `app.manifest.json`, `StaticManifest`, des listes manuelles de l'hôte et de la cible `_HackerOsMarkLegacySystemManifests`, de la règle `Content Update` de `Directory.Build.targets` et des exceptions « transition ».
