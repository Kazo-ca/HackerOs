# Icônes du bureau, contrôle de fichiers fourni par l'OS et menu contextuel central

**Statut :** plan **validé par Benoit le 2026-10-19** (récapitulatif accepté,
réponses aux questions Q1 à Q22 de la section 3). Aucun code n'est écrit. Les
ADR de la section 10 sont écrits avant le code de chaque chantier.

## 1. Objet

- Afficher des **icônes sur le bureau** : le contenu de `~/Desktop`, plus
  Dossier perso et Corbeille, sur une grille où chaque icône garde sa place.
- Faire de l'**explorateur de fichiers la source de vérité** du contrôle de
  fichiers : il le fournit à l'OS, s'en sert lui-même, et le bureau comme
  toute app l'affichent sans en embarquer le code.
- Réduire le SDK à des **contrats** : interfaces, options, événements, et un
  composant hôte qui reçoit à l'exécution le rendu fourni par l'OS. Une
  amélioration de l'OS profite à toutes les apps sans recompilation, et 200
  apps n'embarquent pas 200 fois le même code.
- Ajouter un **menu contextuel central**, multi-niveaux, fourni par l'OS,
  utilisable dans n'importe quelle app, et **extensible par les apps** pour
  les menus de fichiers, comme les verbes du registre de Windows.

## 2. Existant (main, 2026-10-19)

| Élément | État | Où |
|---|---|---|
| `FileView` | Contrôle complet (arbre, détails, icônes, renommage en place, glisser-déposer, presse-papiers, transferts), **bibliothèque du SDK compilée dans chaque app**, publique depuis le gel 1.0 | `Shared/HackerOs.AppSdk.FileView`, ADR 0037, [`file-explorer.md`](../file-explorer.md) |
| Menu de `FileView` | Portées fond / dossier / fichier / type, identifiants stables (`open`, `cut`, `copy`, `paste`, `paste-into`, `rename`, `delete`, `delete-permanently`, `new-folder`, `refresh`), `InsertAfter`/`InsertBefore`, **un seul niveau**, fournisseurs **propres à l'instance** (`MudMenu`) | `ContextMenu/FileViewContextMenuContracts.cs` |
| Entrées ajoutées | « UnZip Here » et « Publish as a web site… », codées dans l'explorateur | `ZipFileContextMenuProvider`, `PublishSiteContextMenuProvider` |
| « Ouvrir avec » | Boîte de l'explorateur (barre d'outils), « Toujours utiliser » | `OpenWithDialog`, ADR 0121 |
| Composants intégrables | Contrat `hackeros.terminal` fourni par l'app Terminal, affiché par `HackEmbeddedComponent<T>` | ADR 0063, [`embedded-components.md`](../../framework/embedded-components.md) |
| Dialogues de l'OS | Contrats `IDialogService`/`IFileDialogService` dans le SDK, mis en œuvre par la plateforme (`VirtualFolderBrowser` pour Ouvrir/Enregistrer ; depuis `desk/3b`, le contrôle `hackeros.file-view`, ADR 0157) | `HackerOs.AppSdk.Blazor`, `Platform.Blazor/Dialogs` |
| Bureau | `~/Desktop` créé pour chaque utilisateur ; emplacement `DesktopArea.BackgroundContent` prévu mais vide | `FileSystemSeeder`, `DesktopArea.razor` |
| Intents | Intents définis par les apps, `IntentReceived` pour une instance ouverte | ADR 0096, [`app-intents.md`](../../framework/app-intents.md) |

## 3. Décisions de Benoit (2026-10-19)

| # | Question | Réponse |
|---|---|---|
| Q1 | Contenu du bureau | `~/Desktop` **plus** Dossier perso et Corbeille, affichables au choix de l'utilisateur |
| Q2 | Rangement | **Grille + position libre** : positions aimantées et mémorisées, « Réorganiser automatiquement », « Aligner sur la grille » |
| Q3 | Livraison du contrôle | Le SDK fournit **l'interface, pas l'implémentation** ; un composant du SDK reçoit un `RenderFragment` de l'OS à l'exécution. L'explorateur **s'inscrit au démarrage** comme fournisseur du contrôle **et de ses boîtes de dialogue** ; les autres apps n'y font jamais référence ; l'OS sert de proxy, le SDK de contrat |
| Q4, Q4 bis | Lecture des entrées ajoutées | Comme Windows : la méthode d'inscription s'exécute **à l'installation** (au build pour les apps intégrées) ; le résultat est un fichier lu au démarrage |
| Q5 | `FileView` public | **SDK 2.0 maintenant** : `FileView` est retiré ; les apps 1.x sont refusées et doivent être recompilées |
| Q6 | Fournisseur du menu générique | **L'OS** (la plateforme) |
| Q7 | Filtre par type | **Filtre déclaré** (`types`) et nombre d'éléments sélectionnés |
| Q8 | Groupes et séparateurs | `after: "X"` = dans le groupe de X ; `afterGroup: "X"` = nouveau groupe après celui de X |
| Q9 | Ordre entre apps | **Ordre d'installation** |
| Q10 | Sous-menus des apps | **Oui**, par `parent` (entrée de la même app) |
| Q11 | Sélection multiple | **Déclarée par entrée** : `All` (défaut), `Each`, `Single` |
| Q12 | Entrées sans position | **Un groupe commun** en fin de menu |
| Q13 | `target` | `"self"` **ou l'identifiant d'une autre app** qui déclare l'intent |
| Q14 | Menu dans le markup d'une app | **Attributs + aide** : `@attributes="Menus.Attach("files", item)"` sur n'importe quel élément |
| Q15 | Contrôle des entrées ajoutées | **Capacité + réglage** : capacité déclarée, et Paramètres permet de masquer les entrées d'une app |
| Q16 | Identifiants des entrées par défaut | **Courts** (`open`, `copy`…) avec constantes C# dans le SDK |
| Q17 | « Ouvrir avec » | Toujours un **sous-menu**, terminé par « Choisir une autre application… » |
| Q18 | Menu du fond du bureau | **4e portée `Desktop`** |
| Q19 | Titre traduit | **Clé de ressource**, textes EN et FR copiés à l'installation |
| Q20 | Mobile | **Menu oui** (appui long), **icônes non** |
| Q21 | Apps sur le bureau | Fichiers **`.desktop`** |
| Q22 | Ce document | Dans `docs/`, par une PR vers `main` |
| Q23 | Fournisseur du contrôle de fichiers (2026-10-20) | **Remplaçable** par une app tierce ; choix d'un administrateur dans Paramètres, l'explorateur par défaut ([ADR 0156](../adr/0156-replaceable-file-control-provider.md)) |

Détails retenus sans question (« défauts » du récapitulatif validé) : repli
automatique des séparateurs (§6.2), contenu des menus par défaut (§7),
emplacements des fichiers (§5, §8), Touche Menu et Maj+F10 (§6.3).

## 4. Architecture : le SDK contrat, l'OS proxy

```text
App (SDK 2.0 : contrats seulement)
  <HackFileView Options=… OnOpened=… />   <button @attributes="Menus.Attach("row", item)">
        │ contrat hackeros.file-view                │ contrat hackeros.context-menu
        ▼                                           ▼
OS (Platform.Blazor) ── registre des fournisseurs ── HackComponentHost
        │ lu au démarrage depuis les manifestes      │
        ▼                                           ▼
Explorateur de fichiers (app système)          Plateforme (shell)
  FileViewProvider, dialogues de fichiers        ContextMenuProvider, ContextMenuRegistry
  (chargé au premier usage)                      (/etc/hackeros/context-menus.d/*.json)
```

- **Composant hôte générique.** `HackEmbeddedComponent<T>` (ADR 0063)
  devient la base : le SDK expose pour chaque contrat un composant typé
  (`HackFileView`, `HackContextMenu`) avec des paramètres et des
  `EventCallback` ordinaires ; à l'exécution, l'OS lui donne le
  `RenderFragment` du fournisseur inscrit et le contrôleur (`IHackFileView`).
- **Inscription.** Un fournisseur se déclare dans son `[AppManifest]`
  (`Components`, comme le terminal). L'OS lit les manifestes au démarrage et
  connaît donc tous les fournisseurs ; le code d'une app n'est chargé qu'au
  premier usage du contrat (`AppLifecycleOrchestrator.EnsureDescriptorAsync`).
- **Fournisseurs.**

| Contrat | Contrôleur et composant du SDK | Fournisseur |
|---|---|---|
| `hackeros.file-view` | `IHackFileView`, `HackFileView` | Explorateur |
| `hackeros.file-dialogs` | dialogues Ouvrir avec, Propriétés, conflit de noms, et la vue de fichiers des dialogues Ouvrir / Enregistrer / Choisir un dossier (finalement le contrôle `hackeros.file-view` dans le dialogue de la plateforme, [ADR 0157](../adr/0157-file-dialogs-show-the-file-control.md)) | Explorateur |
| `hackeros.context-menu` | `IHackContextMenu`, `HackContextMenu`, `Menus.Attach` | Plateforme |

- **Fournisseur remplaçable.** Une app tierce peut fournir `hackeros.file-view`
  et `hackeros.file-dialogs` à la place de l'explorateur, si un administrateur
  la choisit dans Paramètres ([ADR 0156](../adr/0156-replaceable-file-control-provider.md)).
- **Droits.** Le rendu fourni tourne avec **les droits de l'app hôte** :
  le fournisseur reçoit les passerelles de l'hôte (`HostContext`), jamais les
  siennes, comme `FileView` aujourd'hui (ADR 0037 §1). Une app ne voit donc
  pas plus de fichiers en affichant le contrôle de l'explorateur.
- **Indisponible.** Si le fournisseur manque (app désactivée, chargement en
  échec), le composant affiche son contenu `Unavailable` ; l'explorateur et
  la plateforme étant système, ce cas reste une erreur.

## 5. Le bureau

> Mis en œuvre par le chantier 5 : voir [`desktop.md`](../desktop.md) et
> l'[ADR 0160](../adr/0160-desktop-implementation-adjustments.md) (le bureau est
> l'app système `org.hackeros.desktop`, `desktop.config` est un fichier de
> l'utilisateur seulement).

- `DesktopShell` remplit `DesktopArea.BackgroundContent` avec `HackFileView`
  en **mode Bureau** sur `~/Desktop`, avec les droits de la session.
- **Icônes système** : Dossier perso et Corbeille, entrées virtuelles
  ajoutées par le bureau (pas des fichiers), cochées dans Paramètres ▸
  Bureau. Menu de la Corbeille : Ouvrir, Vider la corbeille ; dépôt = mise à
  la corbeille.
- **Mode Bureau** du contrôle : grille de cellules, icône à la case choisie,
  glisser pour déplacer (aimanté), sélection au lasso, renommage en place,
  glisser-déposer avec l'explorateur et entre fenêtres (ADR 0037 §4).
- **Positions** dans `~/.config/hackeros/desktop.config`
  (`[positions]` `nom=colonne,ligne`, `[view]` `auto-arrange`,
  `icon-size`, `show-home`, `show-trash`) ; un fichier nouveau prend la
  première case libre ; un nom absent est oublié au prochain
  enregistrement. Réorganiser automatiquement trie par nom, puis garde
  l'ordre.
- **Raccourcis d'apps** : `~/Desktop/<nom>.desktop` au format freedesktop
  (`[Desktop Entry]`, `Type=Application`, `Name`, `Exec=<appId> [args]`,
  `Icon`). Créés en glissant une app du menu Démarrer sur le bureau ou par
  « Envoyer vers ▸ Bureau ». Partout où le contrôle les montre : icône et nom
  de l'app, double-clic = lancement (`LaunchApp`). Un `.desktop` d'une app
  désinstallée s'affiche grisé.
- **Mobile** : pas d'icônes ; le lanceur mobile reste tel quel.

## 6. Menu contextuel générique (`hackeros.context-menu`)

### 6.1 Modèle

Une entrée : `Id`, `Title`, `Icon` (bibliothèque vectorielle ou image
d'asset), `IsDisabled`, `IsVisible`, `Children` (sous-menu, profondeur 3 au
plus), action (callback C# dans l'app, ou intent pour une entrée inscrite).
Les entrées sont rangées en **groupes** ; un séparateur sépare deux groupes.

### 6.2 Séparateurs

Le menu ne contient jamais deux séparateurs à la suite, ni en tête, ni en
fin, ni autour d'un groupe vide ou entièrement masqué. Exemple : « Ouvrir
avec » est seul dans son groupe ; s'il est masqué, son séparateur disparaît.

### 6.3 Dans une app

```razor
<HackContextMenu Name="files">
    <HackMenuItem Id="preview" Title="@L["Menu.Preview"]" OnClick="item => PreviewAsync((Row)item)" />
    <HackMenuGroup>
        <HackMenuItem Id="export" Title="@L["Menu.Export"]">
            <HackMenuItem Id="csv" Title="CSV" OnClick="item => ExportAsync((Row)item, "csv")" />
        </HackMenuItem>
    </HackMenuGroup>
</HackContextMenu>

@foreach (Row row in _rows)
{
    <tr @attributes="Menus.Attach("files", row)">…</tr>
}
```

- Le même menu se construit aussi en C# (`Menus.Define("files", builder =>
  …)`).
- `Menus.Attach(name, objet)` renvoie les attributs à poser
  (`data-hack-menu`, jeton) ; l'objet reste côté C# dans une table de
  l'instance, vidée quand l'élément quitte le rendu. Un petit script de la
  plateforme capte `contextmenu`, l'appui long au doigt, la Touche Menu et
  Maj+F10, et rend la main au C# avec le jeton.
- Les callbacks ne servent qu'au menu de l'instance qui les déclare.
- Rendu : composant natif de la plateforme (pas `MudMenu`), thème de l'OS,
  clavier (flèches, Entrée, Échap, flèche droite pour ouvrir un sous-menu),
  ARIA `menu`/`menuitem`, repositionné pour rester à l'écran.

## 7. Menus du contrôle de fichiers

Quatre portées : `File`, `Directory`, `Background` (le vide) et `Desktop`
(= `Background` + entrées du bureau). Identifiants en constantes
(`HackFileMenuItems.Open`…).

```text
Fichier                                  Dossier
Ouvrir                      open         Ouvrir                      open
-                                        -
Ouvrir avec ▸               open-with    Ouvrir avec ▸               open-with
   <apps du type, défaut en tête>        -
   -                                     Couper / Copier             cut, copy
   Choisir une autre application…        Coller dans le dossier      paste-into
-                                        Renommer                    rename
Couper / Copier             cut, copy    Supprimer                   delete
Renommer                    rename       Supprimer définitivement    delete-permanently
Supprimer                   delete       -
Supprimer définitivement    delete-perm… <entrées des apps sans position>
-                                        -
<entrées des apps sans position>         Propriétés                  properties
-
Propriétés                  properties

Vide                                     Bureau (en plus du vide)
Nouveau dossier             new-folder   Affichage ▸                 view
Coller                      paste           Taille des icônes, Réorganiser
Actualiser                  refresh         automatiquement, Aligner sur la grille,
-                                           Afficher Dossier perso, Afficher Corbeille
<entrées des apps sans position>         Trier par ▸                 sort
-                                        -
Propriétés                  properties   Arrière-plan…               background (ouvre Paramètres)
```

- « Ouvrir avec » liste les apps qui déclarent le type (associations de
  l'ADR 0121, défaut de l'utilisateur en tête), puis « Choisir une autre
  application… », qui ouvre la boîte avec « Toujours utiliser cette app ».
- « Supprimer définitivement », « Couper / Copier / Coller » n'apparaissent
  que si l'hôte les gère (comme aujourd'hui).
- Les entrées « UnZip Here » et « Publish as a web site… » deviennent des
  **entrées inscrites** : l'explorateur inscrit « Extraire ici » (`types:
  [".zip"]`, `target: "self"`), Web Server Manager inscrit « Publier comme
  site web » (portée `Directory`, `target: "self"`). Elles apparaissent
  donc aussi sur le bureau.
- Un hôte peut encore ajuster le menu de son instance (événement
  `ContextMenuOpening` du contrôleur) avec des callbacks, comme en §6.3.

## 8. Extension par les apps

### 8.1 Déclaration

```csharp
[AppManifest]
public sealed class SuperApp : AppDefinition
{
    // …
    [RegisterFileContextMenu]
    public static void RegisterFileContextMenu(RegisterFileContextMenuContext context)
    {
        context.Register(title: "Menu.Super", name: "super", target: "self",
            icon: HackIcons.FromImage("/assets/super-menu-icon.png"),
            intent: "com.example.super.v1", first: true);
        context.Register(title: "Menu.Archive", name: "archive", target: "self",
            icon: HackIcons.Vector(IconLibrary.Lucide, "archive"),
            scope: FileMenuScope.File | FileMenuScope.Directory);
        context.Register(title: "Menu.Compress", name: "compress", parent: "archive",
            intent: "com.example.compress.v1", selection: MenuSelection.All);
        context.Register(title: "Menu.Extract", name: "extract", parent: "archive",
            intent: "com.example.extract.v1", types: [".zip", "application/zip"],
            selection: MenuSelection.Each, afterGroup: "copy");
    }
}
```

| Paramètre | Rôle |
|---|---|
| `title` | Clé de ressource de l'app ; textes EN et FR copiés à l'inscription |
| `name` | Identifiant unique dans l'app (référencé par `parent`) |
| `target` | `"self"` ou l'identifiant d'une autre app qui déclare l'intent ; vérifié à l'inscription et à l'exécution |
| `intent` | Intent défini par l'app (ADR 0096) ; absent sur une entrée parent |
| `icon` | `HackIcons.FromImage(asset)` ou `HackIcons.Vector(bibliothèque, nom)` |
| `scope` | `File`, `Directory`, `Background`, `Desktop` (combinables) ; `File` par défaut |
| `types` | Extensions, types de média, `image/*`, `*` ; absent = tous |
| `selection` | `All` (défaut), `Each`, `Single` |
| `first` / `after` / `afterGroup` / `parent` | Position (§8.3) |

### 8.2 Inscription à l'installation

- Le générateur de manifeste (`HackerOs.Tools.ManifestGenerator`) appelle la
  méthode `[RegisterFileContextMenu]` **au build** des apps intégrées et
  écrit le résultat dans `manifest.json` (`contextMenus`) et
  `BuiltInApplications.json`. Pour une app installée plus tard, l'OS exécute
  la méthode **une fois à l'installation**, dans un contexte qui ne fait
  qu'enregistrer les appels.
- L'OS écrit `/etc/hackeros/context-menus.d/<appId>.json` (`root 0644`),
  avec le rang d'installation ; les apps intégrées y sont écrites au premier
  démarrage et à chaque mise à jour de l'OS. Le démarrage lit ce dossier :
  aucun code d'app ne tourne pour construire un menu.
- Désinstaller l'app retire son fichier ; une app désactivée (ADR 0048) voit
  ses entrées masquées.
- **Capacité** `shell.context-menu` (normale) exigée pour inscrire des
  entrées ; sans elle, le générateur produit un diagnostic et l'OS ignore les
  entrées. Paramètres ▸ Applications liste les entrées de chaque app et
  permet de les masquer, dans `~/.config/hackeros/context-menus.config`
  (`[hidden]` `<appId>.<name>=true`).
  *Mis en œuvre (ADR 0159) : la méthode tourne au build pour toutes les apps
  (il n'y a pas d'installation de paquet à l'exécution), les titres viennent
  de `Resources/AppManifest.resx`, et le réglage est l'onglet Paramètres ▸
  Menus des fichiers.*
- Le générateur vérifie : `name` unique, `parent` existant dans la même app
  et sans intent, profondeur ≤ 3, `after`/`afterGroup` qui visent un
  identifiant connu de l'OS ou de la même app, `target` valide.

### 8.3 Placement

1. Les entrées par défaut forment des groupes (§7).
2. `first: true` : groupe tout en haut du menu, suivi d'un séparateur ;
   plusieurs apps y partagent ce groupe.
3. `after: "X"` : juste après X, **dans le groupe de X**, sans séparateur.
4. `afterGroup: "X"` : **nouveau groupe** après le groupe de X.
5. Sans position : le **groupe commun** des apps, avant « Propriétés ».
6. Plusieurs apps sur la même place : **ordre d'installation**, puis ordre
   de déclaration dans l'app.
7. Une ancre absente du menu courant (par exemple `paste-into` sur un
   fichier) : l'entrée va dans le groupe commun.
8. Les filtres (`scope`, `types`, `selection`) et les entrées masquées
   s'appliquent avant le placement ; les séparateurs se replient ensuite
   (§6.2).

### 8.4 Exécution

- `selection: All` : un lancement avec tous les chemins ; `Each` : un
  lancement par élément ; `Single` : entrée masquée si plusieurs éléments.
- L'app cible reçoit l'intent (`AppContext.Intent`, `IntentId` = l'intent
  déclaré, charge utile `{"paths":[…],"scope":"File"}`) ; sans intent
  déclaré, les arguments de lancement `["{intent}", "{chemin}", …]`. Une
  instance unique déjà ouverte reçoit `IntentReceived`.
- L'app est lancée **avec ses propres droits** : l'entrée de menu ne lui
  donne aucun accès aux fichiers qu'elle n'aurait pas.

## 9. SDK 2.0

- `HackerOsVersion` passe à `2.0.0`. `AppCatalog` refuse déjà une app
  d'une autre majeure : toute app 1.x doit être recompilée.
- **Retiré** : la bibliothèque `HackerOs.AppSdk.FileView` (`FileView`,
  `FileViewTree`, `FileViewDetails`, `FileViewIcons`, menus
  `IFileViewContextMenuProvider`, `FileViewBreadcrumb`, `FileViewDropZone`,
  `ShellIcon`…) ; ses types publics passent en `*REMOVED*`. Son code
  devient l'implémentation interne de l'explorateur.
- `FileTransfer` (copie, déplacement, conflits Nautilus) reste utile aux
  apps : il passe dans le SDK comme contrat (`IHackFileOperations`) mis en
  œuvre par l'explorateur, pour que ses règles ne vivent qu'à un endroit.
- **Ajouté** : contrats `IHackFileView`, `HackFileView`, `HackContextMenu`,
  `Menus.Attach`, `[RegisterFileContextMenu]`,
  `RegisterFileContextMenuContext`, `HackFileMenuItems`, `HackIcons`.
- Les ajouts en attente pour la 1.3 (`PublishAsync`, ADR 0145 ;
  `HackCrossOriginIsolation`, ADR 0146) partent dans la 2.0.
- La fixture `Tests/fixtures/sdk-compat` (app 1.0.0) devient la preuve du
  **refus** d'une app 1.x ; une nouvelle fixture 2.0.0 est ajoutée.
- `CodeEditor` (explorateur latéral) passe à `HackFileView`.
- [`sdk-packages.md`](../../framework/sdk-packages.md) gagne une section « SDK 2.0 » avec la
  liste des ruptures et la migration.

## 10. ADR à écrire

Écrits par le chantier 0 (branche `desk/0-adrs`, vers `desk/base`).

| ADR | Sujet |
|---|---|
| 0148 | Le SDK ne porte que des contrats ; les contrôles et dialogues partagés sont fournis par l'OS à l'exécution (complète l'ADR 0063, remplace l'ADR 0037 §1 et §8) |
| 0149 | SDK 2.0 : retrait de `HackerOs.AppSdk.FileView`, contrats de fichiers, migration (amende l'ADR 0114) |
| 0150 | Menu contextuel central de l'OS (modèle, groupes, séparateurs, `Menus.Attach`, clavier et appui long) |
| 0151 | Entrées de menu inscrites par les apps (déclaration, inscription à l'installation, placement, exécution, capacité `shell.context-menu`) |
| 0152 | Icônes du bureau (mode Bureau, positions, icônes système, fichiers `.desktop`) |

## 11. Chantiers

Les chantiers s'enchaînent dans cet ordre ; chacun met à jour la
documentation et l'index [`README.md`](../../README.md).

| # | Chantier | Contenu | Dépend de |
|---|---|---|---|
| 0 | ADR | ADR 0148 à 0152 | — |
| 1 | Hôte de contrats générique | Composant hôte typé, registre des fournisseurs lu au démarrage, passage des droits de l'hôte | 0 |
| 2 | Menu contextuel de l'OS | `hackeros.context-menu`, rendu natif multi-niveaux, `Menus.Attach`, script de la plateforme, clavier, appui long | 1 |
| 3 | Contrôle de fichiers fourni par l'explorateur | `hackeros.file-view`, `IHackFileView`, menus par défaut (§7) sur le menu de l'OS, « Ouvrir avec » en sous-menu, dialogues fournis, explorateur et éditeur de code migrés | 1, 2 |
| 4 | Entrées inscrites par les apps | Générateur, `context-menus.d`, placement, capacité, Paramètres, « Extraire ici » et « Publier comme site web » inscrits | 2, 3 |
| 5 | Bureau | Mode Bureau, positions, icônes système, `.desktop`, menu `Desktop`, Paramètres ▸ Bureau | 3, 4 |
| 6 | SDK 2.0 | Retrait de `FileView`, `PublicAPI`, fixtures, `sdk-packages.md`, publication 2.0.0, vérification finale | 3, 4, 5 |

## 12. Tâches

- [x] Débattre de l'idée avec Benoit (Q1 à Q22).
- [x] Valider le récapitulatif.
- [x] Écrire ce plan.
- [x] Chantier 0 : ADR 0148 à 0152 ([0148](../adr/0148-sdk-contracts-and-os-provided-controls.md), [0149](../adr/0149-sdk-2-0-file-view-removal.md), [0150](../adr/0150-os-context-menu.md), [0151](../adr/0151-app-registered-file-menu-entries.md), [0152](../adr/0152-desktop-icons.md)).
- [x] Chantier 1 : hôte de contrats générique (`HackContractComponent<T>`, registre dans `AppCatalog`, contrats réservés et `HOSAPP0025`, fournisseurs de la plateforme, erreur au journal ; [ADR 0153](../adr/0153-contract-host-implementation-adjustments.md), [`embedded-components.md`](../../framework/embedded-components.md)).
- [x] Chantier 2 : menu contextuel de l'OS (`HackContextMenu`, `Menus.Attach`/`Define`/`OpenAsync`, fournisseur de la plateforme, rendu natif multi-niveaux et feuille mobile, script du clic droit, de l'appui long, de la Touche Menu et de Maj+F10, clavier et ARIA ; [ADR 0154](../adr/0154-context-menu-implementation-adjustments.md), [`context-menu.md`](../../framework/context-menu.md)).
- [x] Chantier 3 : contrôle de fichiers fourni par l'explorateur (`HackFileView`, `IHackFileView`, `ExplorerFileView` lié au menu de l'OS, menus du §7 avec « Ouvrir avec » en sous-menu, dialogues fournis par `IDialogService`, explorateur et éditeur de code migrés ; [ADR 0155](../adr/0155-file-control-implementation-adjustments.md), [`file-explorer.md`](../file-explorer.md)). La vue des dialogues Ouvrir / Enregistrer est le chantier `desk/3b`, séparé (choix de Benoit, ADR 0155 §6).
- [x] Chantier 3b : les dialogues Ouvrir / Enregistrer / Choisir un dossier montrent le contrôle `hackeros.file-view` avec les droits de l'app qui demande (`FileDialogBrowser`, repli sur `VirtualFolderBrowser` sans fournisseur ; [ADR 0157](../adr/0157-file-dialogs-show-the-file-control.md)).
- [x] Chantier 3c : fournisseur du contrôle de fichiers remplaçable (contrats ouverts à toute app fenêtrée, `HOSAPP0025` retiré, `/etc/hackeros/components.config`, choix de l'administrateur après approbation, repli `component.fallback`, onglet « Composants de l'OS » ; [ADR 0156](../adr/0156-replaceable-file-control-provider.md), [ADR 0158](../adr/0158-replaceable-file-provider-implementation-adjustments.md)).
- [x] Chantier 4 : entrées inscrites par les apps (`[RegisterFileContextMenu]` exécutée au build pour toutes les apps, `contextMenus` du manifeste, `/etc/hackeros/context-menus.d` aligné au démarrage, filtres, placement, lancement par intent ou arguments, capacité `shell.context-menu`, `HOSAPP0026` à `HOSAPP0028`, Paramètres ▸ Menus des fichiers et `HackFileMenuEntries`, « Extraire ici » et « Publier comme site web… » inscrits ; [ADR 0159](../adr/0159-app-menu-entries-implementation-adjustments.md), [`context-menu.md`](../../framework/context-menu.md#entrées-des-apps-dans-les-menus-des-fichiers-chantier-4)).
- [x] Chantier 5 : bureau (app système `org.hackeros.desktop` rendue par la coquille sous les fenêtres, mode Bureau du contrôle de fichiers, grille `HackDesktopLayout`, `~/.config/hackeros/desktop.config` par `HackDesktopSettings`, Dossier perso et Corbeille, raccourcis `.desktop` par glisser depuis le menu Démarrer et « Envoyer vers ▸ Bureau », menus Affichage et Trier par, Paramètres ▸ Bureau ; [ADR 0160](../adr/0160-desktop-implementation-adjustments.md), [`desktop.md`](../desktop.md)).
- [x] Chantier 6 : SDK 2.0 (`HackerOs.AppSdk.FileView` retiré, son code dans l'explorateur sous `FileControl/`, règles de copie `HackFileOperations` dans le SDK, `PublicAPI` publié, version `2.0.0`, apps 1.x refusées au catalogue et au build de l'hôte, fixture `Contoso.Sdk2Compat.2.0.0`, migration dans [`sdk-packages.md`](../../framework/sdk-packages.md#sdk-20) ; [ADR 0161](../adr/0161-sdk-2-0-implementation-adjustments.md)) et vérification finale (§13).

## 13. Vérification finale

Faite le 2026-10-20 sur la branche `desk/6-sdk-2` (série complète : `desk/base`
avec les chantiers 0 à 5 et le 6), dans un conteneur Linux, .NET 10.

| Vérification | Résultat |
|---|---|
| `dotnet build HackerOs.sln` (Debug) | réussi, 0 avertissement, 0 erreur |
| Tests unitaires et d'intégration (41 projets `Tests/*.Tests` hors E2E) | 3 968 réussis, 0 échec |
| `build/publish-trimmed.sh` et `HackerOs.TrimmedPublish.E2E.Tests` | `RESULT: PASS` ; 136 fichiers `.wasm`, 27,7 Mio, 8,3 Mio en Brotli |
| `build/verify-hosts.sh` (Ecosystem et ses variantes, hôte de test, serveur) | `RESULT: PASS`, les 13 lignes |
| `HackerOs.E2E.Tests` | 18 réussis |
| `HackerOs.UI.E2E.Tests` | 28 réussis |
| `HackerOs.Templates.E2E.Tests` (dont un hôte qui refuse les apps 1.x par `HOSAPP0021` et lance l'app 2.0) | 6 réussis |
| `HackerOs.Pwa.E2E.Tests` | 6 réussis |

Aucun test n'a été désactivé ni ignoré. Les échecs connus d'avant la série
(PWA sans IPv6, tests instables listés dans
[`building-and-testing.md`](../building-and-testing.md)) ne se sont pas montrés
pendant ce passage.

Ce que la série livre :

- un SDK fait de contrats : `HackFileView`, les dialogues de fichiers et le menu
  contextuel sont rendus par l'OS à l'exécution (ADR 0148, 0153) ;
- le menu contextuel de l'OS (`Menus.Attach`, multi-niveaux, clavier, appui
  long ; ADR 0150, 0154) ;
- le contrôle de fichiers fourni par l'explorateur, montré aussi dans les
  dialogues Ouvrir / Enregistrer, et remplaçable par l'administrateur
  (ADR 0155, 0156, 0157, 0158) ;
- les entrées de menu inscrites par les apps (`[RegisterFileContextMenu]`,
  ADR 0151, 0159) ;
- le bureau sur `~/Desktop` (ADR 0152, 0160) ;
- le SDK 2.0.0, qui retire la `FileView` publique et refuse les apps 1.x
  (ADR 0149, 0161).

