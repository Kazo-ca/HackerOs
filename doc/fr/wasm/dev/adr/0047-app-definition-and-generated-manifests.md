> [English version](../../../../en/wasm/dev/adr/0047-app-definition-and-generated-manifests.md)

# ADR 0047 : Manifeste en classe C# (`AppDefinition`) et manifestes JSON générés

## Statut

Accepté le 2026-10-10.

- **Phase du plan :** phase 2 — Modèle d'app ([`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), sections 6.1 et 6.2, décisions D6 à D8).
- **Remplace :** ADR 0010 en partie. Sont remplacés : le fichier `app.manifest.json`
  écrit à la main comme source canonique, et la règle « le build échoue si le
  fichier canonique est absent ». Sont **conservés** : les conventions JSON
  (camelCase, rejet des propriétés inconnues et en double, `schemaVersion` = 1),
  l'ordre de validation, la sérialisation canonique et le principe « aucune
  autorité dans le manifeste ».
- **Complète :** ADR 0044 (le schéma de paramètres vient désormais de la classe
  manifeste, sans changement de l'API `HackSettings`).

## Contexte

Le manifeste d'une app existe aujourd'hui en deux exemplaires : un
`app.manifest.json` écrit à la main et, pour une partie des apps, une copie C#
`StaticManifest` (commandes, exemples). Installer une app dans l'hôte demande
trois lignes dans `OS/HackerOs.Ecosystem/HackerOs.Ecosystem.csproj` : une
`ProjectReference`, un `BlazorWebAssemblyLazyLoad` et un `EmbeddedResource` qui
renomme le JSON. `BuildKnownLazyApps.cs` relit ensuite ces ressources par préfixe
de nom. Oublier une des trois lignes donne une app invisible ou impossible à
charger, sans erreur de build.

Le plan veut une seule source, en C#, et une seule ligne dans l'hôte (D6 à D8).

## Décision

### 1. `AppDefinition` et `[AppManifest]`

Projet `Shared/HackerOs.App.Abstractions` (déjà référencé par toutes les apps, sans
dépendance Blazor), dossier `Definition/`, namespace **`HackerOs.App.Abstractions`**.

```csharp
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class AppManifestAttribute : Attribute;

public abstract class AppDefinition
{
    // Obligatoires (D6)
    public abstract string Id { get; }            // org.hackeros.text-editor
    public abstract string Name { get; }
    public abstract AppKind Kind { get; }
    public abstract Type EntryPoint { get; }      // point d'entrée bureau

    // Facultatifs, avec valeur par défaut
    public virtual string Version => "1.0.0";
    public virtual string Description => Name;
    public virtual string PublisherId => "pub.local";
    public virtual string Category => "utilities";
    public virtual AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Visible;
    public virtual IReadOnlyList<string> Icons => [];
    public virtual IReadOnlyList<string> Capabilities => [];
    public virtual IReadOnlyList<FileHandler> FileHandlers => [];
    public virtual TerminalCommand? Terminal => null;
    public virtual Type? MobileEntryPoint => null;
    public virtual bool SingleInstancePerUser => false;
    public virtual bool AutoStart => false;
    public virtual bool SupportsBack => false;
    public virtual AppResourceProfileManifest Resources => AppResourceProfileManifest.None;
    public virtual SettingsSchema? Settings => null;
    public virtual IReadOnlyList<AppDependencyManifest> Dependencies => [];
    public virtual IReadOnlyList<AppIntentDeclarationManifest> Intents => [];
    public virtual IReadOnlyList<TopicPermissionDeclarationManifest> DeclaredTopicPermissions => [];
    public virtual IReadOnlyList<LocalizationManifest> Localizations => [];
    public virtual IReadOnlyList<AssetManifest> Assets => [];
}

public sealed record FileHandler(
    string? MediaType, IReadOnlyList<string> Extensions,
    IReadOnlyList<string> Actions, int Priority = 0);

public sealed record TerminalCommand(
    string Name, string Usage, IReadOnlyList<string>? Aliases = null);
```

- Les types simples d'usage courant (`FileHandler`, `TerminalCommand`) ont une
  forme courte. Les cas rares réutilisent les enregistrements `*Manifest`
  existants, pour ne pas dupliquer le modèle.
- **Aucune propriété ne parle d'autorité ou de statut système.** Une app ne peut
  pas se déclarer système (D9, ADR 0048) ni s'accorder une capacité (ADR 0002).
- `MobileEntryPoint` non nul produit la forme multi-plateforme `platform` du
  manifeste ; sinon, la forme `entryPoint` (bureau seulement), comme aujourd'hui.
- `sdkCompatibility.minimumVersion` n'est pas une propriété : le générateur
  l'écrit à partir de la version de `HackerOs.App.Abstractions` utilisée à la
  compilation (`major.minor.0`).
- `AppDefinitionMapper.ToManifest(AppDefinition)` (même dossier) convertit une
  définition en `AppManifest`. `EntryPoint` devient
  `{ "assembly": "<nom de l'assembly>.dll", "type": "<nom complet du type>" }`.

**Règles imposées à la classe**, vérifiées par le générateur :

- `public`, non abstraite, marquée `[AppManifest]`, constructeur public sans
  paramètre ;
- `EntryPoint` (et `MobileEntryPoint`) dans **la même assembly** que la
  définition : le chargement différé ne charge qu'une DLL par app ;
- getters **purs** : pas de service, d'horloge, de hasard ni d'entrée-sortie. Le
  générateur instancie la classe deux fois et compare les octets canoniques ;
  une différence fait échouer le build ;
- `AppManifestValidator` passe sans erreur, ainsi que les règles de type de
  point d'entrée de `AppEntryPointDiscovery` (une app `Window` pointe vers un
  composant, une app `Terminal` vers une commande, etc.).

Une assembly peut contenir plusieurs classes `[AppManifest]` : c'est le cas du
projet unique de commandes prévu en phase 3.

Exemple :

```csharp
[AppManifest]
public sealed class TextEditorApp : AppDefinition
{
    public override string Id => "org.hackeros.text-editor";
    public override string Name => "Text Editor";
    public override AppKind Kind => AppKind.Window;
    public override Type EntryPoint => typeof(TextEditorWindow);
    public override string PublisherId => "pub.hackeros";
    public override IReadOnlyList<string> Capabilities =>
        [AppCapabilities.FileSystemUserHomeRead, AppCapabilities.FileSystemUserHomeWrite];
    public override IReadOnlyList<FileHandler> FileHandlers =>
        [new("text/plain", [".txt", ".md", ".log"], ["open", "edit"])];
}
```

### 2. Génération de `manifest.json` dans chaque projet d'app (D7)

**Outil :** `Tools/HackerOs.Tools.ManifestGenerator`, qui remplace et absorbe
`Tools/HackerOs.Tools.ManifestValidator`. C'est un exécutable .NET qui dépend du
framework partagé `Microsoft.AspNetCore.App`, afin de pouvoir charger les types
Blazor des points d'entrée.

Pourquoi un exécutable et pas une tâche MSBuild en code ni un générateur Roslyn :
un générateur Roslyn ne peut ni écrire dans le dossier source ni exécuter les
getters ; une tâche MSBuild en code tourne sous .NET Framework dans Visual Studio
et ne peut pas charger une assembly `net10.0`. Un `Exec` fonctionne pareil en
ligne de commande et dans l'IDE.

**Cible MSBuild :** `GenerateHackerOsAppManifests`, dans
`build/HackerOs.Apps.targets`, importé par `Directory.Build.targets`.

- Elle s'exécute après `CopyFilesToOutputDirectory` quand
  `$(HackerOsAppProject)` vaut `true`. Cette propriété vaut `true` par défaut pour
  tout projet situé sous `Apps/` ; un projet ailleurs la positionne lui-même.
- Elle appelle `ManifestGenerator generate` avec l'assembly compilée et la liste
  `@(ReferencePath)` + `@(ReferenceCopyLocalPaths)`. L'outil charge l'app dans un
  `AssemblyLoadContext` isolé et collectable, qui résout les dépendances par nom
  depuis cette liste, puis depuis le framework partagé.
- Elle est incrémentale (entrée : l'assembly compilée ; sortie : un fichier témoin
  dans `obj/`).

**Fichiers produits**, en JSON canonique (ADR 0010) :

- une seule classe `[AppManifest]` dans le projet → `manifest.json` à la racine du
  projet ;
- plusieurs classes → `manifests/<appId>.json`, un par app.

Le JSON garde exactement le schéma version 1 : aucun champ « généré » n'est
ajouté, puisque les lecteurs stricts le refuseraient. Le fichier est **réécrit à
chaque build** si ses octets diffèrent. Une
modification à la main disparaît donc au build suivant.

Les fichiers générés sont **suivis par git**. Un changement de capacité ou
d'association de fichiers apparaît ainsi dans le diff de la PR, ce qui aide la
relecture de sécurité. Le build n'écrit pas le fichier si son contenu est
identique, pour ne pas toucher sa date.

**Erreurs :** chaque règle de la section 1 produit une erreur MSBuild avec un code
stable `HOSAPP0001` à `HOSAPP0099` et le nom complet de la classe en cause. Le
build échoue ; aucun manifeste partiel n'est écrit. Un projet d'app sans aucune
classe `[AppManifest]` est une erreur `HOSAPP0001`.

**Cible de requête :** `GetHackerOsAppManifests` renvoie, pour un projet d'app,
la liste des manifestes produits et le nom de son assembly. Elle sert à l'hôte.

### 3. Agrégation dans l'hôte : `BuiltInApplications.json` (D7, D8)

Un projet hôte se déclare avec `<HackerOsAppHost>true</HackerOsAppHost>`. Seul
`OS/HackerOs.Ecosystem` le fait aujourd'hui. `test/test` réutilise le catalogue
compilé de l'Ecosystem (`BuildKnownLazyApps.Catalog`) et ne s'agrège pas lui-même.

Cible `CollectHackerOsBuiltInApplications` (même fichier `.targets`), après
`ResolveProjectReferences` :

1. appelle `GetHackerOsAppManifests` sur chaque `ProjectReference` directe ; un
   projet qui n'est pas une app est ignoré ;
2. lit sur chaque `ProjectReference` la métadonnée `IsSystemApp` (défaut `false`,
   sens fixé par l'ADR 0048) ;
3. appelle `ManifestGenerator aggregate`, qui relit et valide chaque manifeste,
   construit le catalogue avec `AppCatalog.Build` (identifiants en double,
   dépendances manquantes ou cycliques = erreur de build) et écrit
   `$(IntermediateOutputPath)BuiltInApplications.json` ;
4. ajoute ce fichier en `EmbeddedResource` avec le nom logique
   `HackerOs.BuiltInApplications.json` ;
5. ajoute un `BlazorWebAssemblyLazyLoad` pour l'assembly de chaque app, avant les
   cibles Blazor qui consomment cet élément. La bonne cible d'ancrage est vérifiée
   par un `dotnet publish` de l'Ecosystem dans le chantier qui l'implémente.

**Format**, canonique, applications triées par identifiant :

```json
{"schemaVersion":1,"applications":[{"isSystemApp":true,"manifest":{"schemaVersion":1,"id":"org.hackeros.text-editor", "...": "..."}}]}
```

`BuiltInApplications.json` n'est jamais écrit à la main ni versionné : il est
produit dans `obj/` à chaque build de l'hôte. Le format servira aussi au futur
magasin d'apps (une app installée au runtime aura toujours `isSystemApp: false`).

### 4. Une seule ligne dans l'hôte (D8)

Installer une app = une `ProjectReference` dans `HackerOs.Ecosystem.csproj`. Les
listes `BlazorWebAssemblyLazyLoad` et `EmbeddedResource` du `.csproj` sont
supprimées, ainsi que la règle `Content Update="app.manifest.json"` de
`Directory.Build.targets`.

### 5. Lecture au démarrage

`BuildKnownLazyApps` lit la ressource `HackerOs.BuiltInApplications.json` avec un
désérialiseur strict (`BuiltInApplicationsJsonSerializer`, à côté
d'`AppManifestJsonSerializer`) au lieu des ressources `HackerOs.Ecosystem.Manifests.*`.
Il expose le catalogue comme aujourd'hui, plus l'information `IsSystemApp` par
app (ADR 0048). Les DLL d'apps ne sont toujours chargées qu'au premier lancement :
les classes `AppDefinition` ne sont **jamais** instanciées au runtime, seul le JSON
est lu.

### 6. Migration

- Chaque app sous `Apps/` reçoit une classe `[AppManifest]`, avec des valeurs
  identiques à son `app.manifest.json` actuel. Le `manifest.json` généré doit
  être sémantiquement égal à l'ancien fichier (même identifiant, capacités,
  associations, profil de ressources…). Un test de migration compare les deux
  avant la suppression de l'ancien.
- Puis `app.manifest.json` et les champs `StaticManifest` sont supprimés. Les
  commandes et exemples qui construisaient leur manifeste en C# l'obtiennent par
  `AppDefinitionMapper.ToManifest(new XxxApp())`, y compris dans les tests.
- Les projets `HackerOs.Commands.Cd/Pwd/Clear/Help` reçoivent aussi leur classe,
  mais restent non référencés par l'hôte jusqu'à la phase 3 (ADR 0034).

### 7. Tests de contrat

- `AppDefinitionMapper` : chaque propriété facultative a sa valeur par défaut ;
  `EntryPoint` et `MobileEntryPoint` produisent la bonne forme.
- Générateur : un projet de test par erreur (`HOSAPP*`) ; getters non déterministes
  détectés ; point d'entrée dans une autre assembly refusé ; deux classes dans un
  projet donnent deux fichiers.
- Agrégation : `IsSystemApp` reporté ; identifiant en double refusé ; projet non-app
  ignoré.
- Ecosystem : `BuildKnownLazyApps.Catalog` contient exactement les apps
  référencées, et le chargement différé de chaque app fonctionne.

## Conséquences

- Ajouter une app : un projet avec une classe `[AppManifest]`, puis une
  `ProjectReference` dans l'hôte. Plus de JSON à écrire, plus de liste à tenir.
- Les erreurs de manifeste sont des erreurs de compilation, avec la classe en
  cause, au lieu d'une app absente à l'exécution.
- Le build d'une app exécute son code (les getters de la définition). C'est
  acceptable pour du code du dépôt ; pour le futur magasin, le JSON publié suffit
  et l'hôte n'exécute rien.
- Le schéma JSON version 1 et le format runtime `AppManifest` ne changent pas :
  le moteur, le catalogue et les tests qui consomment `AppManifest` restent
  valables.
- Le `manifest.json` suivi par git peut être en retard sur le code entre deux
  builds ; il est toujours remis à jour par le build suivant, et c'est le build
  qui fait foi.

## Chantiers d'implémentation

| Branche | Contenu | Dépend de |
|---|---|---|
| `rebuild/2-app-definition` | `AppDefinition`, `[AppManifest]`, `FileHandler`, `TerminalCommand`, `AppDefinitionMapper`, tests du mappeur. | — |
| `rebuild/2-manifest-generator` | `Tools/HackerOs.Tools.ManifestGenerator` (`generate`, `aggregate`), `build/HackerOs.Apps.targets`, cibles `GenerateHackerOsAppManifests` et `GetHackerOsAppManifests`, erreurs `HOSAPP*`, projets de test. | `2-app-definition` |
| `rebuild/2-builtin-applications` | Cible `CollectHackerOsBuiltInApplications`, `BuiltInApplications.json`, lazy load automatique, `BuildKnownLazyApps` lit le nouveau fichier, métadonnée `IsSystemApp` transportée. | `2-manifest-generator` |
| `rebuild/2-app-migration` | Classes `[AppManifest]` pour les 44 projets d'app (12 apps, 28 commandes, 4 exemples), test d'égalité avec les anciens JSON, suppression des `app.manifest.json`, `StaticManifest` et listes manuelles de l'hôte. | `2-builtin-applications` |

## Références

- ADR 0002 (autorité), ADR 0010 (remplacé en partie), ADR 0034 (commandes non
  câblées), ADR 0044 (schéma de paramètres), ADR 0048 (apps système).
- [`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), sections 6.1 et 6.2.
- `OS/HackerOs.Ecosystem/BuildKnownLazyApps.cs`, `HackerOs.Ecosystem.csproj`.
