> [English version](../../../../en/wasm/dev/adr/0155-file-control-implementation-adjustments.md)

# ADR 0155 : ajustements du contrôle de fichiers fourni par l'explorateur

## Statut

Accepté le 2026-10-20.

- **Chantier :** `desk/3-file-control` (chantier 3 du plan
  [`desktop-and-context-menu-plan.md`](../plans/desktop-and-context-menu-plan.md) §11).
- **Ajuste :** l'ADR 0148 §1, §2 et §6, l'ADR 0149 §3 et le plan §7 sur des
  points de mise en œuvre ; leurs décisions restent vraies.

## Contexte

L'ADR 0148 décide que le SDK ne porte que le contrat `hackeros.file-view` et
que l'explorateur de fichiers le fournit à l'exécution, avec ses dialogues
(`hackeros.file-dialogs`) et les menus du plan §7 sur le menu de l'OS
(ADR 0150). Le chantier 1 a écrit l'hôte de contrats (ADR 0153), le chantier 2
le menu de l'OS (ADR 0154). Ce chantier fait de l'explorateur le fournisseur du
contrôle et de ses dialogues, et le fait l'utiliser pour lui-même et pour
l'éditeur de code. Plusieurs détails n'étaient pas fixés.

## Décision

### 1. `FileView` devient l'implémentation de l'explorateur, sans changer de place

Le code de `HackerOs.AppSdk.FileView` reste dans sa bibliothèque jusqu'au
SDK 2.0 (chantier 6, ADR 0149), qui la retire du SDK. D'ici là :

- l'explorateur l'utilise comme implémentation interne
  (`InternalsVisibleTo("HackerOs.Apps.FileExplorer")`) ;
- le fournisseur `ExplorerFileView` donne en cascade une liaison interne,
  `FileViewMenuBinding`, à la vue. Avec elle, la vue ne dessine plus son
  `MudMenu` : chaque ligne, tuile, nœud de l'arbre et le fond portent les
  attributs de `Menus.Attach` (ADR 0154 §3), et le menu de l'OS s'ouvre dessus ;
- sans liaison (une app 1.x qui affiche encore `FileView` directement), la vue
  garde son ancien menu. L'API publique de `FileView` ne change pas.

Le `MudMenu` de la vue disparaît donc de l'explorateur et de tout hôte du
contrat, comme l'ADR 0154 l'annonçait.

### 2. L'API du contrat

Dans `HackerOs.AppSdk.Blazor.Files` :

- **`HackFileView`** (`HackContractComponent<IHackFileView>`) prend des
  **paramètres ordinaires** plutôt qu'un enregistrement d'options
  (`HackFileViewOptions` de l'ADR 0149 §3 n'existe pas) : `InitialDirectory`,
  `Mode`/`ModeChanged`, `Features`, `AllowMultiSelect`, `Filter`,
  `SelectedResource`, et les rappels `Opening`, `Navigated`,
  `SelectionChanged`, `Renamed`, `Deleted`, `ContextMenuOpening`,
  `ErrorOccurred`. Tous sont transmis par leur nom au fournisseur ; un test
  vérifie qu'aucun n'est oublié.
- **`IHackFileView`** : dossier courant, éléments, sélection, `CanPaste`,
  `IsBusy`, `StateChanged`, `NavigateAsync`, `RefreshAsync`, `RevealAsync`,
  `ClearSelection`, `RefreshFilter`, `WhenIdleAsync` (une fenêtre qui se ferme
  attend les copies en cours, ADR 0069), et **`CanExecute`/`ExecuteAsync`**
  dont les commandes sont les identifiants des entrées de menu. Une barre
  d'outils et le menu font donc exactement la même chose.
- **`HackFileViewItem`** : instantané d'un élément (chemin, nom, dossier,
  taille, type MIME, date). Le même élément de la vue donne toujours le même
  instantané, pour que le jeton de `Menus.Attach` reste stable.
- **`HackFileMenuOpeningEventArgs`** : portée, cibles, dossier, menu à
  modifier, `Cancel`.
- Dans `HackerOs.AppSdk.Files` (sans Blazor, pour le chantier 4) :
  **`FileMenuScope`** (`File`, `Directory`, `Background`, `Desktop`) et
  **`HackFileMenuItems`**, avec deux identifiants de plus que le plan :
  `open-with-other` (« Choisir une autre application… ») et le préfixe
  `open-with:<appId>` des apps du sous-menu.

`SelectedResource` est un ajout : l'éditeur de code ouvre un dossier choisi
dans le dialogue de l'OS, avec la poignée de ce choix
(`WithSelectedHandle`). Le contrôle liste et renomme dans ce dossier avec la
même poignée, comme l'ADR 0148 §4 le prévoit pour les dialogues.

### 3. Le contrôle fait lui-même ce que l'explorateur faisait autour

`ExplorerFileView` fait, **avec les droits de l'hôte** (ADR 0148 §4 : ses
passerelles sont celles de `HostContext`, ses rappels s'exécutent dans le
contexte ambiant de l'hôte) : la corbeille et la suppression définitive après
confirmation, le presse-papiers de session, les copies et déplacements avec le
conflit de noms, l'import de fichiers de l'hôte, l'ouverture par l'app par
défaut et « Ouvrir avec », le choix « Toujours utiliser » (ADR 0121 §4).

« N'apparaît que si l'hôte les gère » (plan §7) devient le paramètre
**`Features`** (`Clipboard`, `Delete`, `Rename`, `CreateFolder`, `DragDrop`,
`OpenWith`, `Properties`, tout par défaut) : un hôte retire ce qu'il ne veut
pas, l'entrée disparaît du menu et la touche ne fait rien.

`Opening` laisse l'hôte ouvrir lui-même un élément (`Handled = true`) : l'éditeur
de code ouvre un fichier dans un onglet plutôt que dans son app par défaut ; un
dossier non pris en main est ouvert dans la vue. Les erreurs vont à
`ErrorOccurred`, ou en notification si l'hôte n'en veut pas.

### 4. Les menus

- Un menu vide par contrôle (`Menus.Define`), **rempli à chaque ouverture**
  par `ExplorerFileMenu` (fonction pure, testée) puis passé à
  `ContextMenuOpening` de l'hôte.
- **Cibles** : un élément hors de la sélection devient la sélection ; un
  élément de la sélection agit sur toute la sélection. Les entrées agissent sur
  les cibles prises à l'ouverture, pas sur la sélection au moment du clic.
- **Portée** : `Directory` si toutes les cibles sont des dossiers, `File`
  sinon, `Background` pour le vide.
- **Plusieurs éléments** : « Renommer » et « Ouvrir avec » absents,
  « Propriétés » désactivé ; « Ouvrir » désactivé pour plusieurs dossiers.
- **« Ouvrir avec ▸ »** sur un fichier : les apps de
  `IAppCatalogGateway.GetHandlersAsync` (défaut de l'utilisateur en tête), puis
  un groupe « Choisir une autre application… ». Pas d'« Ouvrir avec » sur un
  dossier, comme dans le plan.
- Un groupe vide est gardé entre l'édition et « Propriétés » : les entrées
  inscrites par les apps y viendront (chantier 4) ; l'OS replie les
  séparateurs autour d'un groupe vide.
- Raccourcis affichés : Ctrl+X, Ctrl+C, Ctrl+V, F2, Suppr, Maj+Suppr, F5.

### 5. Les dialogues fournis passent par `IDialogService`

`IDialogService` gagne trois méthodes à implémentation par défaut (un hôte
écrit avant répond `Unavailable`) : `OpenWithAsync`,
`ShowFilePropertiesAsync`, `ResolveFileConflictAsync`, avec leurs
enregistrements de requête et de résultat (`HackFileDialogs`). La plateforme
(`DialogCoordinator`) n'est que le proxy : elle met la requête en file avec le
contexte de l'app appelante, la montre dans une fenêtre modale de cette app
(`ProvidedFileDialog`, qui donne ce contexte en cascade puis rend
`hackeros.file-dialogs`), et rend le résultat que le fournisseur donne par
`Completed`. Une fermeture ou un résultat d'un autre type vaut « rien »
(`Cancelled`, `Closed`, `Skip`).

Le fournisseur `ExplorerFileDialogs` est en markup natif (dans le style du
sélecteur d'apps de l'OS), plus en MudBlazor : `OpenWithDialog`,
`FilePropertiesDialog`, `FileConflictDialog`, `OpenWithChoice` et
`FileExplorerDialogBase` sont retirés de l'explorateur.

### 6. La vue des dialogues Ouvrir / Enregistrer

L'ADR 0148 §2 place aussi dans `hackeros.file-dialogs` la vue de fichiers des
dialogues Ouvrir / Enregistrer / Choisir un dossier, aujourd'hui
`VirtualFolderBrowser` dans la plateforme. Ce remplacement touche le pipeline
des dialogues de la plateforme et les tests UI qui les pilotent ; il n'est
**pas** dans ce chantier : Benoit a choisi (2026-10-20) un chantier
`desk/3b` séparé, après celui-ci.

### 7. Ce qui reste pour les chantiers suivants

- `IHackFileOperations` (ADR 0149 §3) : chantier 6. Jusque-là, une app qui
  copie hors du contrôle utilise encore `FileTransfer`.
- « UnZip Here… » et « Publish as a web site… » deviennent des entrées
  inscrites au chantier 4 ; d'ici là, la fenêtre de l'explorateur les ajoute
  par son propre `ContextMenuOpening` (`FileExplorerMenuEntries`), donc
  seulement dans sa fenêtre.
- La portée `Desktop` et son menu : chantier 5.
- La vue des dialogues Ouvrir / Enregistrer : chantier `desk/3b` (choix de
  Benoit, 2026-10-20).

### 8. Rien ne suppose que l'explorateur est le seul fournisseur

L'ADR 0156, acceptée pendant ce chantier, laisse un administrateur choisir une
app tierce comme fournisseur des deux contrats ; son code vient dans un
chantier suivant. Ce chantier la prépare :

- les hôtes (éditeur de code, fenêtre de l'explorateur) ne parlent qu'au
  contrat : paramètres de `HackFileView`, `IHackFileView`, identifiants de
  `HackFileMenuItems`, requêtes de `IDialogService` ;
- la fenêtre de l'explorateur ne touche l'implémentation interne (`FileView`)
  que pour le fil d'Ariane et les dépôts sur Home et Trash ; avec un autre
  fournisseur, seuls ces deux extras disparaissent ;
- `ExplorerFileMenu` et `FileViewMenuBinding` sont internes à l'explorateur :
  un autre fournisseur construit ses menus lui-même sur le menu de l'OS, avec
  les mêmes identifiants (ADR 0156 §5) ;
- le proxy des dialogues (`DialogCoordinator`, `ProvidedFileDialog`) rend le
  contrat par le registre, pas l'explorateur par son nom.

## Conséquences

- Une correction du contrôle, de ses menus ou de ses dialogues atteint
  l'explorateur, l'éditeur de code et toute app qui affiche `HackFileView`
  sans les recompiler.
- L'éditeur de code ne référence plus `HackerOs.AppSdk.FileView` ; son
  explorateur latéral affiche `HackFileView` en arbre et ajoute ses entrées
  (« Nouveau fichier », « Nouveau dossier », « Rechercher dans le dossier »,
  « Ouvrir dans le terminal ») par `ContextMenuOpening`. Sa propre suppression
  vers la corbeille est retirée : c'est celle du contrôle.
- Les ajouts sont dans `PublicAPI.Unshipped.txt` (SDK, plateforme, apps) et
  publiés avec la 2.0 (ADR 0149 §1).

## Références

- [ADR 0148](0148-sdk-contracts-and-os-provided-controls.md),
  [ADR 0149](0149-sdk-2-0-file-view-removal.md),
  [ADR 0150](0150-os-context-menu.md),
  [ADR 0153](0153-contract-host-implementation-adjustments.md),
  [ADR 0154](0154-context-menu-implementation-adjustments.md),
  [ADR 0156](0156-replaceable-file-control-provider.md),
  [ADR 0121](0121-user-file-associations.md).
- [`file-explorer.md`](../file-explorer.md),
  [`embedded-components.md`](../../framework/embedded-components.md),
  [`context-menu.md`](../../framework/context-menu.md),
  [`code-editor.md`](../code-editor.md).
