> [English version](../../../en/wasm/framework/context-menu.md)

# Menu contextuel de l'OS (`hackeros.context-menu`)

## Objet

Un seul menu contextuel pour tout l'OS, multi-niveaux, dessiné par la
plateforme au thème de l'OS, qu'une app attache à n'importe quel élément de sa
fenêtre sans écrire de JavaScript. Clic droit, appui long au doigt, Touche Menu
et Maj+F10 l'ouvrent ; le clavier et les lecteurs d'écran s'y déplacent comme
dans le menu de Windows ou de GNOME. C'est la base des menus du contrôle de
fichiers (chantier 3) et des entrées inscrites par les apps (chantier 4) du plan
[`desktop-and-context-menu-plan.md`](../dev/plans/desktop-and-context-menu-plan.md).

Décisions : [ADR 0150](../dev/adr/0150-os-context-menu.md) ; ajustements de la mise en
œuvre : [ADR 0154](../dev/adr/0154-context-menu-implementation-adjustments.md).

Depuis le chantier 3 (ADR 0155), les menus du contrôle de fichiers
(`HackFileView`) sont des menus de l'OS : le fournisseur de l'explorateur
définit un menu par contrôle, le remplit à chaque ouverture (plan §7) et le
passe à `ContextMenuOpening` de l'hôte ; le `MudMenu` de `FileView` n'est plus
dessiné quand la vue est liée au menu de l'OS. Voir
[`embedded-components.md`](embedded-components.md#hackfileview-et-ihackfileview).

Depuis le chantier 4 (ADR 0159), l'OS ajoute à ces menus les entrées que les
apps inscrivent : voir [Entrées des apps](#entrées-des-apps-dans-les-menus-des-fichiers-chantier-4).

## Utiliser un menu dans une app

### Dans le markup

```razor
@using HackerOs.AppSdk.Blazor.ContextMenus
@using HackerOs.AppSdk.Icons

<HackContextMenu Name="rows" Opening="OnRowsMenuOpening">
    <HackMenuItem Id="open" Title="@L["Menu.Open"]" Icon="OpenIcon" OnClick="@(item => OpenAsync((Row)item!))" />
    <HackMenuGroup>
        <HackMenuItem Id="export" Title="@L["Menu.Export"]">
            <HackMenuItem Id="csv" Title="CSV" OnClick="@(item => ExportAsync((Row)item!, "csv"))" />
            <HackMenuItem Id="json" Title="JSON" OnClick="@(item => ExportAsync((Row)item!, "json"))" />
        </HackMenuItem>
    </HackMenuGroup>
    <HackMenuGroup>
        <HackMenuItem Id="delete" Title="@L["Menu.Delete"]" Shortcut="Del" OnClick="@(item => DeleteAsync((Row)item!))" />
    </HackMenuGroup>
</HackContextMenu>

@foreach (Row row in _rows)
{
    <tr tabindex="0" @attributes="@(Menus.Attach("rows", row))">…</tr>
}

@code {
    private static readonly HackIcon OpenIcon = HackIcons.Vector(IconLibrary.Lucide, "folder-open");

    // Avant chaque ouverture, pour l'objet attaché : masquer, désactiver, ajouter, annuler.
    private void OnRowsMenuOpening(HackMenuOpeningEventArgs args)
    {
        if (args.Item is Row { IsReadOnly: true })
        {
            args.Menu.Disable("delete");
        }
    }
}
```

- `HackContextMenu` ne dessine **rien** là où il est écrit : il décrit le menu
  à l'OS. Le nom (`Name`) est unique dans la fenêtre.
- `Menus` existe sur `WindowAppBase` et sur `HackComponentBase`.
  `Menus.Attach(nom, objet)` renvoie deux attributs (`data-hack-menu`,
  `data-hack-menu-token`) ; l'objet reste en C#, sous un jeton opaque. Le même
  objet attaché à nouveau garde son jeton (par référence pour un objet, par
  valeur pour une chaîne ou un type valeur).
- L'élément le plus proche qui porte un jeton gagne : une ligne dans une liste
  attachée à son tour ouvre le menu de la ligne, le vide de la liste celui de la
  liste.
- Un attribut dont la valeur contient des guillemets s'écrit `@(…)` ou entre
  apostrophes : `@attributes="@(Menus.Attach("rows", row))"`.
- L'action (`OnClick`) reçoit l'objet attaché et s'exécute dans le contexte de
  l'app, comme un `@onclick` du composant qui l'a déclarée (le composant est
  redessiné après).
- Pour qu'une ligne s'ouvre au clavier (Touche Menu, Maj+F10), elle doit
  pouvoir prendre le focus (`tabindex="0"`).
- Préférer `IsVisible="false"` à un `@if` autour d'une entrée : une entrée qui
  apparaît après le premier rendu va à la fin de son niveau.

### En C#

```csharp
protected override void OnAppInitialized()
{
    Menus.Define("rows-area", menu => menu
        .Item("new", L["Menu.New"], _ => CreateAsync(), icon: HackIcons.Vector(IconLibrary.Lucide, "file-plus"))
        .Group(group => group
            .Submenu("sort", L["Menu.SortBy"], sort => sort
                .Item("sort-name", L["Menu.Name"], _ => SortAsync("name"))
                .Item("sort-date", L["Menu.Date"], _ => SortAsync("date"), isDisabled: true))));
}
```

- Même modèle que le markup ; le constructeur est rappelé à chaque ouverture,
  donc les titres suivent la langue et l'état courants.
- `Define` renvoie un `IDisposable` ; la fermeture de la fenêtre retire aussi le
  menu. Un menu C# et un menu markup du même nom dans une fenêtre sont une
  erreur (`InvalidOperationException`).

### Depuis un bouton

```razor
<button @onclick="@(args => Menus.OpenAsync("rows-area", null, args.ClientX, args.ClientY))">⋯</button>
```

`HackContextMenu.Controller` (`IHackContextMenu`) donne aussi `OpenAsync(objet, x, y)`
et `Close()`.

L'exemple complet est dans l'app d'exemple `HackerOs.Samples.WindowApp`
(section « Context Menu »).

## Modèle

| Type | Rôle |
|---|---|
| `HackMenuDefinition` | Un menu : `Name`, `Label` (nom accessible), `Groups` ; `Find`, `Hide`, `Disable`, `AddGroup`, `Clone`, `Validate` ; `MaxDepth = 3` |
| `HackMenuGroupDefinition` | Un groupe d'entrées : un séparateur n'est dessiné qu'entre deux groupes visibles |
| `HackMenuItemDefinition` | Une entrée : `Id`, `Title` (déjà traduit), `Icon`, `IsDisabled`, `IsVisible`, `Shortcut` (texte seulement), `OnClick`, `Children` (sous-menu, groupes) |
| `HackMenuBuilder` | Construction en C# : `Item`, `Submenu`, `Group` |
| `HackMenuOpeningEventArgs` | `Menu` (copie de cette ouverture), `Item`, `Cancel` |
| `HackIcons.Vector(bibliothèque, nom)`, `HackIcons.FromImage(chemin)` | Icône décrite (`HackIcon`), dessinée par l'OS (`HackerOs.AppSdk.Icons`) |
| `HackMenus` | `Menus` d'un composant : `Attach`, `Define`, `OpenAsync`, `Close`, `IsAvailable` |
| `IHackMenuHost` | Côté OS des menus d'une fenêtre (mis en œuvre par la plateforme, donné en cascade) |
| `IHackContextMenu` | Contrôleur du contrat : `Name`, `OpenAsync`, `Close` |

Règles vérifiées à chaque ouverture (`Validate`) : identifiants présents et
uniques dans tout le menu, trois niveaux au plus, pas d'action sur une entrée
qui ouvre un sous-menu. Le constructeur C# et `HackMenuItem` refusent un
quatrième niveau dès la construction.

### Séparateurs

Après les filtres (`IsVisible`, ajustements de `Opening`) :

1. un groupe sans entrée visible disparaît ;
2. un sous-menu sans entrée visible masque son entrée parente ;
3. un séparateur n'est dessiné qu'entre deux groupes visibles : jamais en tête,
   en fin, ni deux à la suite. La règle vaut à chaque niveau.

## Architecture

```text
App (SDK)                                         Plateforme (Platform.Blazor)
<HackContextMenu Name="rows">  ── contrat ──►     PlatformContextMenu (HackPlatformComponent)
   HackMenuItem / HackMenuGroup                       │ inscrit le menu dans la fenêtre
<tr @attributes="Menus.Attach("rows", row)">          ▼
        │ data-hack-menu-token              ContextMenuScope (une par fenêtre, IHackMenuHost)
        ▼                                             │
ContextMenuOverlay.razor.js ── jeton, x, y ──► ContextMenuService (un menu ouvert dans tout l'OS)
  (clic droit, appui long, Touche Menu,               │ Build → copie → Opening → Validate → Arrange
   Maj+F10, fermeture, jetons libérés)                ▼
                                             ContextMenuOverlay (bureau : panneaux ; mobile : feuille)
```

- **Contrat.** `HackContextMenu` est un composant typé
  (`HackContractComponent<IHackContextMenu>`, ADR 0153) ; la plateforme fournit
  `hackeros.context-menu` par `HackPlatformComponent` (`PlatformContextMenu`),
  sans manifeste ni chargement d'app. `PlatformContextMenu` ne dessine rien : il
  inscrit le menu dans la fenêtre et rend le contrôleur.
- **Fenêtre.** `WindowAppRenderer` crée une `ContextMenuScope` par fenêtre
  (`ContextMenuService.CreateScope(AppContext)`) et la donne en cascade
  (`IHackMenuHost`) ; elle est détruite avec la fenêtre (menus, objets attachés,
  menu ouvert). Une zone de l'OS (bureau, barre des tâches) crée la sienne avec
  `CreateScope(null)`.
- **Service.** `ContextMenuService` (scoped, comme `WindowRuntime`) garde le
  seul menu ouvert (`Current`, `Changed`). À l'ouverture : copie fraîche du menu,
  `Opening` dans le contexte de l'app, vérification, mise en page
  (`ContextMenuLayout.Arrange`). Un menu qui échoue n'ouvre rien et l'erreur va
  dans `/var/log/syslog` (`ILogger`) ; l'OS continue. Une action qui lève est
  écrite au journal de la même façon.
- **Rendu.** `ContextMenuOverlay` est dans `DesktopShell` (panneaux flottants)
  et `MobileShell` (feuille du bas, `Presentation="Sheet"`). Composant natif avec
  CSS isolé, pas `MudMenu`. Le clavier passe par `ContextMenuNavigator`, testable
  sans navigateur.
- **Script.** `ContextMenuOverlay.razor.js`, module du composant importé par
  le C# (aucun hôte ne le référence) : écoute `contextmenu`, l'appui long
  (500 ms, 10 px), la Touche Menu et Maj+F10 sur l'élément qui a le focus ;
  annule le menu du navigateur sur un élément attaché ; place les panneaux
  dans la fenêtre du navigateur ; ferme le menu sur un appui dehors, un
  défilement, un redimensionnement ou la perte du focus ; rend le focus à
  l'élément d'origine ; signale les jetons des éléments retirés de la page.

### Ce que fait la souris, le doigt et le clavier

| Geste | Effet |
|---|---|
| Clic droit sur un élément attaché | Le menu s'ouvre au pointeur ; rien n'est en surbrillance |
| Appui long au doigt ou au stylet (500 ms sans bouger de plus de 10 px) | Le menu s'ouvre (feuille du bas sur mobile) ; le clic qui suit le lever du doigt est absorbé |
| Touche Menu, Maj+F10 | Le menu s'ouvre au coin de l'élément qui a le focus, première entrée en surbrillance |
| Haut, Bas | Entrée précédente ou suivante, en boucle, sans séparateur ni entrée désactivée |
| Début, Fin | Première ou dernière entrée |
| Droite, Entrée sur une entrée parente | Ouvre le sous-menu sur sa première entrée |
| Gauche | Ferme le sous-menu |
| Entrée, Espace | Choisit l'entrée |
| Échap | Ferme un niveau ; au premier niveau, ferme le menu |
| Tab | Ferme le menu, comme Windows |
| Une lettre | Va à l'entrée suivante qui commence par elle |
| Survol d'une entrée parente | Ouvre son sous-menu (sur bureau) |
| Clic ou appui hors du menu, défilement, redimensionnement, perte du focus | Ferme le menu ; le clic continue vers sa cible |

Un champ de texte dans un élément attaché garde le menu du navigateur
(copier, coller), sauf s'il est lui-même attaché. Un élément sans jeton dans une
zone `data-hack-menu-suppress` (une zone de l'OS qui a son propre menu) n'ouvre
pas non plus le menu du navigateur. Un `contextmenu` déjà annulé par l'app
(`@oncontextmenu:preventDefault`) est laissé à l'app.

### Accessibilité

`role="menu"` sur chaque panneau (nom accessible : `Label`, sinon « Menu
contextuel » ; le titre de l'entrée parente pour un sous-menu),
`role="menuitem"`, `role="separator"`, `aria-haspopup="menu"`,
`aria-expanded` et `aria-disabled` écrits en `"true"`/`"false"`
([`old/common-pitfalls.md`](../dev/old/common-pitfalls.md) §8), surbrillance annoncée
par `aria-activedescendant`. Le focus revient à l'élément d'origine à la
fermeture, avant l'action choisie (une boîte ou un renommage en place qu'elle
ouvre garde donc son focus). Mouvement réduit : pas d'animation.

### Limites connues

- Une fenêtre détachée dans une autre fenêtre du navigateur (ADR 0078) garde le
  menu du navigateur : le menu de l'OS n'est dessiné que dans le document du
  bureau et dans les écrans secondaires (ADR 0179 §2,
  `dev/secondary-screen-desktop.md`).
- L'ordre des entrées du markup est celui de leur premier rendu (voir
  `IsVisible` plus haut).

### Menus du bureau et du menu Démarrer (chantier 5)

Le fond du bureau a la portée `Desktop` ; l'app Bureau y ajoute
« Affichage ▸ », « Trier par ▸ », « Arrière-plan… » et « Paramètres du
bureau… ». Le menu Démarrer porte un menu de la coquille,
`hackeros.start-menu.app`, sur chaque app : Ouvrir, Épingler / Désépingler,
« Envoyer vers ▸ Bureau (créer un raccourci) ». Détails :
[`desktop.md`](../dev/desktop.md#menus).

## Entrées des apps dans les menus des fichiers (chantier 4)

Décisions : [ADR 0151](../dev/adr/0151-app-registered-file-menu-entries.md) ;
ajustements : [ADR 0159](../dev/adr/0159-app-menu-entries-implementation-adjustments.md).

### Objet

Une app ajoute ses propres entrées aux menus des fichiers (« Extraire ici »,
« Publier comme site web… ») sans que l'app qui affiche les fichiers la
connaisse. Les entrées paraissent partout où le contrôle de fichiers
(`HackFileView`) est affiché : explorateur, dialogues Ouvrir / Enregistrer,
toute app hôte et, au chantier 5, le bureau — quel que soit le fournisseur du
contrôle choisi par l'administrateur (ADR 0156).

### Déclarer des entrées

La classe `[AppManifest]` de l'app demande la capacité `shell.context-menu` et
déclare **une** méthode statique marquée `[RegisterFileContextMenu]` :

```csharp
using HackerOs.AppSdk.Blazor.Files;
using HackerOs.AppSdk.Files;
using HackerOs.AppSdk.Icons;

[AppManifest]
public sealed class ZipperApp : AppDefinition
{
    public const string ExtractIntent = "com.example.zipper.extract.v1";

    public override IReadOnlyList<string> Capabilities => [AppCapabilities.ShellContextMenu];
    public override IReadOnlyList<AppIntentDeclarationManifest> Intents => [new(ExtractIntent)];

    [RegisterFileContextMenu]
    public static void RegisterFileContextMenu(RegisterFileContextMenuContext context)
    {
        context.Register("Menu.Extract", "extract", intent: ExtractIntent,
            icon: HackIcons.Vector(IconLibrary.Lucide, "package-open"),
            types: [".zip", "application/zip"], after: HackFileMenuItems.Open);

        // Un sous-menu : le parent n'a pas d'intent, les enfants prennent sa portée.
        context.Register("Menu.Convert", "convert", scope: FileMenuScope.File | FileMenuScope.Directory);
        context.Register("Menu.ToPng", "to-png", intent: "com.example.zipper.to-png.v1",
            parent: "convert", selection: MenuSelection.Each);
    }
}
```

| Paramètre | Rôle | Défaut |
|---|---|---|
| `title` | Clé de ressource : anglais dans `Resources/AppManifest.resx`, traductions dans `Resources/AppManifest.<culture>.resx` | requis |
| `name` | Identifiant court, unique dans l'app (`extract-here`) | requis |
| `target` | `"self"` ou l'id d'une autre app qui déclare l'intent | `"self"` |
| `intent` | Intent remis à la cible ; absent sur un parent de sous-menu | — |
| `icon` | `HackIcons.Vector(…)` ou `HackIcons.FromImage("icons/x.svg")` (asset de l'app) | — |
| `scope` | `File`, `Directory`, `Background`, `Desktop`, combinables | `File` (celle du parent pour un enfant) |
| `types` | `.zip`, `application/zip`, `image/*`, `*`, `inode/directory` | tous |
| `selection` | `All` (un lancement, tous les chemins), `Each` (un par chemin), `Single` (masquée si plusieurs) | `All` |
| `first` / `after` / `afterGroup` | Groupe commun en tête ; juste après X dans son groupe ; nouveau groupe après celui de X | groupe commun avant « Propriétés » |
| `parent` | Sous-menu d'une entrée de la même app (3 niveaux au plus) | — |

`X` est une constante de `HackFileMenuItems` (`open`, `copy`, `properties`…)
ou le `name` d'une entrée de l'app. La méthode **n'enregistre que les appels** :
elle s'exécute au build, deux fois, et doit donner le même résultat. Le
générateur écrit les entrées dans `manifest.json` (`contextMenus`) et signale
une erreur par `HOSAPP0026` (règle, exception, titre anglais manquant),
`HOSAPP0027` (capacité absente) ou `HOSAPP0028` (cible absente de l'hôte ou
qui ne déclare pas l'intent).

### Recevoir le clic

La cible est lancée avec ses propres droits. Si elle déclare l'intent, elle le
reçoit dans `AppContext.LaunchIntent` (ou `OnIntentReceivedAsync` pour une
instance unique déjà ouverte), avec la charge utile
`{"paths":["/home/alice/a.zip"],"scope":"File"}`, que `HackFileMenuIntent` lit :

```csharp
if (LaunchIntent is { IntentId: ZipperApp.ExtractIntent } intent)
{
    foreach (string path in HackFileMenuIntent.ReadPaths(intent.PayloadJson))
    {
        await ExtractAsync(path);
    }
}
```

Sinon, elle reçoit les arguments `["<intent>", "<chemin>", …]`. Dans
l'espace vide (portées `Background` et `Desktop`), le chemin est le dossier
affiché.

### Comment l'OS les applique

1. Au démarrage et à chaque changement des apps installées,
   `FileContextMenuRegistry` (Platform Core) copie les entrées des manifestes
   dans `/etc/hackeros/context-menus.d/<appId>.json` (rang d'installation,
   empreinte, version de l'app), retire les fichiers des apps désinstallées
   pour la machine, puis relit le dossier. Un fichier invalide est ignoré et
   signalé dans `/var/log/syslog`.
2. Le fournisseur du contrôle de fichiers pose `HackMenuDefinition.FileMenu`
   (`HackFileMenuContext` : portée, éléments, dossier) sur le menu qu'il ouvre.
3. Après `Opening`, `ContextMenuService` appelle `IFileMenuEntrySource`
   (`FileMenuEntries`, Platform Blazor) : filtres (`FileContextMenuMatcher` :
   portée, types, sélection), apps installées pour l'utilisateur et cibles
   valides (`FileContextMenuLauncher.IsAvailable`), entrées masquées par
   l'utilisateur, puis placement (`FileMenuEntryPlacement`). Les entrées ont
   l'id `app:<appId>/<name>`.
4. Au clic, `FileContextMenuLauncher` revérifie la cible et lance l'app par
   `AppLifecycleOrchestrator`, l'OS (`org.hackeros.shell`) comme expéditeur.

### Masquer une entrée

Paramètres ▸ **Menus des fichiers** liste les entrées de toutes les apps
(sous-menus sous leur parent) avec une case par entrée. Le choix est écrit
dans `~/.config/hackeros/context-menus.config` :

```ini
[hidden]
org.hackeros.file-explorer.extract-here=true
```

Masquer un parent masque son sous-menu ; le fichier est relu à chaque
ouverture, donc une modification avec `nano` vaut pour le menu suivant. Une
app système le fait par l'API publique `HackFileMenuEntries`
(`GetEntriesAsync`, `SetHiddenAsync`, capacité `apps.manage`).

### Entrées de l'OS migrées

| App | Entrée | Portée et types | Intent |
|---|---|---|---|
| Explorateur | « Extraire ici » (`extract-here`), après « Ouvrir » | fichiers `.zip`, `application/zip` | `org.hackeros.file-explorer.extract-here.v1` |
| Web Server Manager | « Publier comme site web… » (`publish-site`) | dossiers, un seul | `org.hackeros.web-server-manager.publish-site.v1` |

### Tests

- `Tests/HackerOs.App.Abstractions.Tests/AppManifestContextMenusTests.cs` :
  règles du validateur, forme canonique, aller-retour JSON.
- `Tests/HackerOs.AppSdk.Blazor.Tests/RegisterFileContextMenuTests.cs` :
  `Register`, charge utile, identifiants par défaut identiques dans le SDK et le
  validateur.
- `Tests/HackerOs.Tools.ManifestGenerator.Tests/ContextMenuRegistrationTests.cs` :
  titres par culture, `HOSAPP0026` à `HOSAPP0028`, déterminisme.
- `Tests/HackerOs.Platform.Core.Tests/ContextMenus/FileContextMenuTests.cs` :
  dossier `context-menus.d` (rang, réécriture, retrait, fichiers invalides),
  filtres, préférences, lancements, `HackFileMenuEntries`.
- `Tests/HackerOs.Platform.Blazor.Tests/ContextMenus/FileMenuEntriesTests.cs` :
  placement, sous-menus vidés, clic, espace vide, service.
- `Tests/HackerOs.Apps.Settings.Tests/FileMenuEntriesPanelTests.cs` : ordre de la
  liste.
- `Tests/HackerOs.UI.E2E.Tests/FileMenuEntriesTests.cs` : « Extraire ici » après
  « Ouvrir » sur une archive téléversée, extraction, « Publier comme site
  web… » sur le dossier extrait.

### Tâches (chantier 4)

- [x] Contrat du manifeste `contextMenus`, schéma, sérialiseur, validateur, capacité `shell.context-menu`.
- [x] SDK : `[RegisterFileContextMenu]`, `RegisterFileContextMenuContext`, `MenuSelection`, `HackFileMenuIntent`, `HackMenuDefinition.FileMenu`.
- [x] Générateur : exécution de la méthode, titres EN/FR, `HOSAPP0026` à `HOSAPP0028`.
- [x] OS : `context-menus.d`, filtres, placement, lancement, entrées masquées.
- [x] Migration de « Extraire ici » et « Publier comme site web… ».
- [x] Paramètres ▸ Menus des fichiers et `HackFileMenuEntries`.
- [x] Tests, ADR 0159, documents et index.

## Tests

- `Tests/HackerOs.AppSdk.Blazor.Tests/HackContextMenuTests.cs` : groupes
  implicites, quatrième niveau refusé, validation, copie, `Menus` hors fenêtre,
  `Define`, description du menu du markup au fournisseur, action dans le
  contexte de l'app, rien dessiné sans fournisseur.
- `Tests/HackerOs.Platform.Blazor.Tests/ContextMenus/` : repli des séparateurs
  (exemple « Ouvrir avec » du plan), sous-menus vides, clavier et survol,
  jetons (même objet, valeurs, libération), menus en double, `Opening` sur une
  copie, menus annulés ou cassés, un seul menu ouvert dans l'OS, action dans le
  contexte de l'app après la fermeture, fournisseur de la plateforme de bout en
  bout.
- `Tests/HackerOs.Ecosystem.Tests` : le conteneur de services résout le contrat
  vers `PlatformContextMenu` sans charger d'app.

## Tâches (chantier 2)

- [x] Modèle du SDK (`HackMenuDefinition`, groupes, entrées, trois niveaux, validation, copie).
- [x] Construction en C# (`HackMenuBuilder`, `Menus.Define`).
- [x] Markup (`HackContextMenu`, `HackMenuItem`, `HackMenuGroup`) sur le contrat `hackeros.context-menu`.
- [x] `Menus.Attach` et jetons ; `Menus` sur `WindowAppBase` et `HackComponentBase`.
- [x] `HackIcons` (vectorielle ou image).
- [x] Fournisseur de la plateforme (`PlatformContextMenu`), une portée par fenêtre (`WindowAppRenderer`).
- [x] Service : un menu ouvert dans l'OS, `Opening`, repli des séparateurs, erreurs au journal.
- [x] Rendu natif multi-niveaux (bureau) et feuille du bas (mobile), thème de l'OS, ARIA.
- [x] Script : clic droit, appui long, Touche Menu, Maj+F10, placement à l'écran, fermeture, focus rendu, jetons libérés.
- [x] Clavier (`ContextMenuNavigator`).
- [x] Exemple dans `HackerOs.Samples.WindowApp`.
- [x] Tests, ADR 0154, index des documents.
