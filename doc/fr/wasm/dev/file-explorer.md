> [English version](../../../en/wasm/dev/file-explorer.md)

# Explorateur de fichiers : couper, copier, coller et glisser-déposer

Les API destinées aux apps sont dans [`file-operations-api.md`](../framework/file-operations-api.md) (`HackFileOperations`), [`file-control-api.md`](../framework/file-control-api.md) (`HackFileView` et dialogues) et [`clipboard-api.md`](../framework/clipboard-api.md) (presse-papiers).

## Objet

Chantier `rebuild/4-file-explorer` de la phase 4 (ADR 0056 §12, ADR 0058 §1).
L'explorateur (`Apps/System/HackerOs.Apps.FileExplorer`) atteint la parité avec
le legacy (`src/apps/file-explorer.ts`) et la dépasse :

- **couper, copier, coller** par Ctrl+X, Ctrl+C, Ctrl+V, la barre d'outils et le
  menu contextuel (« Cut », « Copy », « Paste », « Paste Into Folder »), au
  travers du **presse-papiers de la session** : une autre fenêtre de
  l'explorateur, ou une autre app, colle ce qu'une fenêtre a coupé ;
- **conflits de noms à la Nautilus** : une copie dont le nom est pris devient
  `rapport (copy).txt`, puis `rapport (copy 2).txt`… ; un déplacement demande
  « Replace », « Keep both » ou « Skip », avec « Apply to all » ;
- **glisser-déposer** sur un dossier, sur le fond de la vue, sur un nœud de
  l'arbre, sur un segment du fil d'Ariane, sur les entrées « Home » et « Trash »
  du panneau Places, sur la vue Corbeille, entre deux fenêtres, et depuis le
  bureau réel de l'utilisateur (fichiers de l'hôte) ;
- un **résumé des échecs** à la fin de chaque opération (bandeau et
  notification), avec la raison par élément ; une erreur n'arrête pas les autres.
- depuis `web/5-static-sites`, **« Publish as a web site… »** dans le menu d'un
  dossier : ouvre Web Server Manager avec ce dossier comme racine d'un nouveau
  site ([`static-sites.md`](static-sites.md)). Depuis `desk/4` (ADR 0159),
  c'est une entrée **inscrite par Web Server Manager**, et « Extract here »
  une entrée inscrite par l'explorateur (`[RegisterFileContextMenu]` de
  `FileExplorerApp`, intent `org.hackeros.file-explorer.extract-here.v1` :
  l'explorateur s'ouvre sur le dossier de l'archive et l'extrait) ; la fenêtre
  n'ajoute plus rien par `ContextMenuOpening`. Voir
  [`context-menu.md`](../framework/context-menu.md#entrées-des-apps-dans-les-menus-des-fichiers-chantier-4).
- depuis `desk/3-file-control` (ADR 0155), **l'explorateur fournit le contrôle
  de fichiers de l'OS** (`hackeros.file-view`) et ses dialogues
  (`hackeros.file-dialogs`), et s'en sert lui-même : voir la section suivante.

## Fournisseur du contrôle de fichiers (ADR 0148, ADR 0155)

La fenêtre masque par défaut les noms commençant par un point, notamment la
configuration de l'accueil mobile `~/Desktop/.mobile/layout.conf`
(ADR 0166). Le bouton œil **Afficher les fichiers et dossiers cachés** de la
barre de vue active/désactive leur affichage pour cette fenêtre. Le prédicat
est combiné à la recherche via `Filter`, sans modifier les fichiers ni les
droits ; les descendants de l'arbre et sa navigation clavier suivent ce filtre.

Le manifeste (`FileExplorerApp.Components`) inscrit deux composants :

| Contrat | Composant | Rôle |
|---|---|---|
| `hackeros.file-view` | `ExplorerFileView` | `FileView` (implémentation interne, ADR 0155 §1) liée au menu de l'OS, avec la corbeille, le presse-papiers, les transferts et leurs conflits, l'ouverture et « Ouvrir avec ». Contrôleur `IHackFileView`. |
| `hackeros.file-dialogs` | `ExplorerFileDialogs` | « Ouvrir avec » (« Toujours utiliser »), « Propriétés », conflit de noms, en markup natif. |

```text
App hôte ──► HackFileView (SDK) ──► registre ──► ExplorerFileView (explorateur)
                                                   ├─ FileView + FileViewMenuBinding ──► Menus.Attach (menu de l'OS)
                                                   ├─ ExplorerFileMenu (menus du plan §7) ──► ContextMenuOpening de l'hôte
                                                   └─ IDialogService ──► DialogCoordinator ──► ProvidedFileDialog ──► ExplorerFileDialogs
```

### Mode Bureau et raccourcis (`desk/5`, ADR 0160)

`ExplorerFileView` fournit aussi le mode `Desktop` du contrôle, que l'app
Bureau affiche sur `~/Desktop` (voir [`desktop.md`](desktop.md)) : la grille
`FileViewDesktop`, les icônes Dossier perso et Corbeille avec leurs menus
(Ouvrir, Vider la corbeille, Propriétés) et leurs dépôts, et la Corbeille
pleine ou vide selon `~/.local/share/Trash/files`. Ouvrir un dossier du bureau
ouvre une fenêtre de l'explorateur ; l'argument `trash:///` ouvre la fenêtre
sur la vue Corbeille.

Dans tous les modes, un fichier `.desktop` valide s'affiche avec le nom et
l'icône de son app (grisé si l'app manque) et l'ouvrir lance l'app ; déposer
une app glissée du menu Démarrer crée un raccourci dans le dossier visé.

## Explorateur mobile (ADR 0171)

Sur la plateforme mobile, l'OS ouvre `FileExplorerMobileWindow`, le point
d'entrée `MobileEntryPoint` de l'app ([ADR 0171](adr/0171-mobile-file-explorer-and-full-screen-dialogs.md)).
La fenêtre hérite de `FileExplorerWindow` : même état, mêmes commandes, même
`HackFileView`. Seule la mise en page change :

- en haut : Retour, nom du dossier, Rechercher et le menu « ⋮ » (Sélectionner,
  Liste ou Grille, fichiers cachés, Actualiser) ; puis les puces Dossier perso
  et Corbeille et le fil d'Ariane ;
- en bas : les actions sur la sélection (Copier, Couper, Renommer, Supprimer,
  Plus), sinon « Coller ici » quand le presse-papiers a des fichiers, sinon le
  bouton « + » (Nouveau dossier, Nouveau fichier, Téléverser) ;
- un toucher ouvre l'élément (`FileView.TapOpens`) ; en mode sélection, il
  l'ajoute ou le retire (`FileView.TapTogglesSelection`) ; l'appui long ouvre le
  menu de fichiers de l'OS ;
- le bouton Retour du système (`IAppBackHandler`) ferme la feuille, la
  recherche, la sélection, quitte la Corbeille, puis remonte l'historique.

Les dialogues que l'explorateur ouvre (nom d'un dossier, confirmation,
propriétés) s'affichent en plein écran (voir
[`mobile-home-screen.md`](mobile-home-screen.md#dialogues-en-plein-écran-adr-0171)).

## Architecture

Depuis `desk/3`, ce schéma est celui du fournisseur `ExplorerFileView`, que la
fenêtre affiche par `HackFileView` (section précédente).

```text
ExplorerFileView ──► FileView (interne)    ── Ctrl+X/C/V, menus ──► ClipboardRequested
        │                 │  FileView.razor.js  ── dépôts ─────────► DropRequested
        │                 ├─ FileViewBreadcrumb (fil d'Ariane, cibles de dépôt)
        │                 └─ FileViewDropZone   (Home, Trash, vue Corbeille)
        ├──► HackClipboard ──► IAppClipboardGateway ──► AppClipboardGateway ──► SystemClipboard (une sélection par session)
        └──► HackFileOperations (SDK) ──► HackFile / HackDirectory / HackTrash (contexte ambiant de l'app)
```

| Pièce | Où | Rôle |
|---|---|---|
| `HackClipboard`, `HackClipboardFileList`, `HackClipboardFileOperation` | `Shared/HackerOs.AppSdk/Clipboard/` | Façade publique : texte et liste de fichiers (format GNOME `x-special/gnome-copied-files`). |
| `IAppClipboardGateway` | idem | Passerelle `IAppExecutionContext.Clipboard` : `clipboard.read` / `clipboard.write`, événement `Changed`. |
| `SystemClipboard` | `Platform/HackerOs.Platform.Core/Clipboard/` | Presse-papiers de l'OS : une sélection par session, cibles MIME (`text/plain`, `x-special/gnome-copied-files`), vidée à la déconnexion. En mémoire, comme un vrai presse-papiers. Numéro de séquence, origine et partage du texte avec la vraie machine : [`clipboard.md`](clipboard.md) (ADR 0065). |
| `AppClipboardGateway` | `Platform/HackerOs.Platform.Core/Execution/` | Passerelle de confiance par instance : vérifie la capacité à chaque appel, ne voit que sa session, rejoue `Changed` dans le contexte ambiant de l'app abonnée. |
| `HackFileOperations` et contrats (ex-`FileTransfer`) | `Shared/HackerOs.AppSdk/IO/HackFileOperations*.cs` | Copie, déplacement, import de fichiers de l'hôte, envoi à la corbeille ; noms Nautilus, conflits, refus d'un dossier dans lui-même, résumé des échecs. Tourne sur l'API `Hack*`, donc avec les droits de l'app. Dans le SDK depuis la 2.0 (ADR 0161 §2) ; `IDialogService.AsFileConflictResolver` (`HackerOs.AppSdk.Blazor.Files`) pose la question de conflit par `hackeros.file-dialogs`. |
| `FileView` (ajouts) | `Apps/System/HackerOs.Apps.FileExplorer/FileControl/` (dans `Shared/HackerOs.AppSdk.FileView` avant le SDK 2.0, ADR 0161 §1) | Dépôt sur le fond, `DropRequested`, `ClipboardRequested`, `CanPaste`, `CutPaths` (éléments atténués), Ctrl+A. |
| `FileViewBreadcrumb`, `FileViewDropZone` | idem | Fil d'Ariane cliquable et déposable ; zone de dépôt générique pour le chrome de l'hôte. |
| `FileView.razor.js` | idem | Écouteurs natifs `dragover` / `drop` : curseur déplacer/copier, surbrillance de la cible, capture du contenu et des `File` de l'hôte. Depuis l'ADR 0065, Ctrl+V suit l'événement `paste` (`attachPasteTarget`) : des fichiers copiés sur la vraie machine et collés dans la vue sont importés comme un dépôt. |
| `ExplorerFileDialogs` | `Apps/System/HackerOs.Apps.FileExplorer/` | Fenêtre « Replace / Keep both / Skip » (et « Ouvrir avec », « Propriétés ») du contrat `hackeros.file-dialogs`, en markup natif depuis `desk/3` ; l'ancien `FileConflictDialog` MudBlazor est retiré. |

## Usage et API

### Archives en arrière-plan (ADR 0140)

Compresser, Extraire et « Extract here » passent par des tâches
`HackBackground` ([`background-jobs.md`](../framework/background-jobs.md)) :
`FileExplorerZipService.CompressInBackgroundAsync` et `ExtractInBackgroundAsync`
lancent les processus enfants `file-explorer:compress` et
`file-explorer:extract`, qui lisent, compressent ou décompressent et écrivent
par leurs propres passerelles (mêmes droits que l'explorateur), dans un Web
Worker quand l'hôte en a. Une erreur revient comme clé de texte
(`ZipFailure`) et la fenêtre la traduit. Ces tâches sont suivies comme les
transferts : une demande d'arrêt attend leur fin.

Restent dans la fenêtre, avec leur raison écrite dans le code : les copies et
déplacements (`HackFileOperations`, appels à l'OS attendus avec un point d'arrêt
entre deux éléments, et question de conflit dans la fenêtre) et le
téléchargement d'une sélection en `.zip` (les octets vont au navigateur depuis
la fenêtre et dépasseraient vite les 16 Mio d'un résultat).

### `FileView`

| Paramètre | Rôle |
|---|---|
| `DropRequested` | Reçoit chaque dépôt (`FileViewDropRequestedEventArgs` : `Destination`, `Entries`, `HostFiles`, `Copy`). Sans lui, la vue déplace (Ctrl : copie) elle-même et ignore les fichiers de l'hôte. |
| `ClipboardRequested` | Reçoit Ctrl+X/C/V et les entrées de menu (`FileViewClipboardRequestedEventArgs` : `Action`, `Items`, `Destination`). Sans lui, ces entrées n'existent pas. |
| `CanPaste` | Grise « Paste » et « Paste Into Folder ». |
| `CutPaths` | Chemins coupés : leurs éléments sont atténués (`FileViewItem.IsCut`). |

`FileViewMenuItem` gagne `IsDisabled`. Identifiants des nouvelles entrées :
`cut`, `copy`, `paste-into` (dossier), `paste` (fond).

```razor
<FileViewBreadcrumb Owner="_fileView" RootLabel="Computer" />
<FileViewDropZone Owner="_fileView" Destination="_home">…</FileViewDropZone>
<FileViewDropZone Owner="_fileView" Dropped="OnTrashDropAsync">…</FileViewDropZone>
```

Le curseur indique l'opération pendant le survol : déplacer, copier avec Ctrl ou
pour des fichiers de l'hôte. La cible survolée est encadrée en pointillés.

### Ligne de commande et « Afficher dans le dossier » (ADR 0039, ADR 0096 §1)

| Arguments | Effet |
|---|---|
| aucun | Ouvre le dossier personnel. |
| `<dossier>` | Ouvre ce dossier (ouverture d'un `inode/directory`, `xdg-open ~/Documents`). |
| `<parent> --select=<chemin>` | Ouvre `<parent>`, sélectionne l'élément et le fait défiler jusqu'à lui : c'est ce qu'envoie `RevealFileIntent` (`HackApps.RevealFileAsync`), l'équivalent de `org.freedesktop.FileManager1.ShowItems`. |
| `--select=<chemin>` seul | Ouvre le dossier parent de `<chemin>` et le sélectionne. |

Un argument relatif ou mal formé est ignoré. L'explorateur n'est pas à
instance unique : chaque demande ouvre une fenêtre, comme Nautilus lancé avec un
chemin. `FileView.RevealAsync(path)` fait le
travail : navigation vers le parent, sélection, puis `revealItem` de
`FileView.razor.js` (`scrollIntoView` puis focus).

## Langues (ADR 0104)

Tous les textes de l'explorateur (barres d'outils, Emplacements, Corbeille,
dialogues Ouvrir avec / Propriétés / conflit de noms, menus contextuels,
messages d'erreur, résumés de transfert) viennent de `Resources/Strings.resx`
(anglais) et `Resources/Strings.fr.resx` (français), lus par
`AppContext.GetLocalizer<Strings>()` dans la langue du processus. Le nom de
l'app est traduit par `Resources/AppManifest.fr.resx`.

- Le résumé d'échec d'un transfert est construit par la fenêtre
  (`Transfer.<Copy|Move|Trash|Upload>.One/Many`) à partir de
  `FileTransferResult.Failures` ; la raison de chaque échec vient du SDK
  `FileView`.
- Dates (colonne « Supprimé le » de la Corbeille, « Modifié » des Propriétés)
  et tailles suivent `AppContext.FormatCulture` (`LC_TIME`, `LANG`) : date et
  heure courtes de la locale, unités `o/Ko/Mo/Go` en français.
- Les noms de fichiers et de dossiers ne sont jamais traduits (`NewFile.txt`,
  `Archive.zip`, `~/.local/share/Trash`) ; seul le nom proposé pour un nouveau
  dossier l'est, comme « Dossier sans titre » sous GNOME.

## Décisions clés

- **Un presse-papiers par session, dans Platform Core.** Comme la sélection
  `CLIPBOARD` de X11 tenue par le gestionnaire de presse-papiers du bureau : toutes
  les apps de la session le partagent, une autre session ne le voit pas, il est
  vidé à la déconnexion et ne survit pas à un redémarrage. L'ancienne classe en
  mémoire `AppClipboardGateway` du SDK, non branchée, est supprimée.
- **Format GNOME.** La liste est stockée en `x-special/gnome-copied-files` et en
  `text/plain` : une app qui lit du texte voit les chemins (ADR 0056 §12).
- **`HackFileOperations` dans le SDK** (ADR 0161 §2, ex-`FileTransfer` de
  `HackerOs.AppSdk.FileView`). Toute app, tout fournisseur du contrôle de
  fichiers et toute commande reprennent les mêmes règles ; le code n'utilise que
  l'API `Hack*`, donc les capacités et les droits de l'app.
- **`HackDirectory.CopyAsync`.** .NET n'a pas de `Directory.Copy` ; l'ADR 0058
  demande une copie récursive. Le moteur copie l'arbre en une transaction ; entre
  deux montages, la façade le recrée entrée par entrée (même repli que
  `MoveAsync`).
- **Dépôts lus par un écouteur natif.** Le gestionnaire C# d'un `drop` ne peut pas
  compter sur `window.event` ; l'écouteur `drop` en phase de capture de
  `FileView.razor.js` garde le contenu et les `File` de l'hôte, que le C# lit
  ensuite. Le contenu d'un fichier de l'hôte n'est lu qu'au moment de l'import
  (`IJSStreamReference`). Le document archivé
  `old/Global-FileView-And-MessagingSystem/FileViewControl.md` disait le dépôt de
  fichiers de l'hôte hors de portée : ce n'est plus le cas.
- **Remplacer un dossier le supprime définitivement**, comme Nautilus en mode
  « Replace » ; la fenêtre le dit.
- **Envoi depuis la barre d'outils** : passe par `HackFileOperations.ImportAsync` et ne
  remplace plus un fichier existant (nom de copie), comme un dépôt.
- **Capacités de l'explorateur** : `clipboard.read`, `clipboard.write` et
  `notifications.post` (résumé des échecs), toutes normales.

## Tests

- `Tests/HackerOs.Platform.Core.Tests/Execution/AppClipboardGatewayTests.cs` :
  partage entre apps d'une session, isolement des sessions, capacités,
  `Changed`, vidage à la déconnexion, façade `HackClipboard`, format GNOME.
- `Tests/HackerOs.AppSdk.Tests/IO/HackFileOperationsTests.cs` : noms de copie,
  copie récursive, dossier dans lui-même, déplacement sur place, choix de
  conflit, « Apply to all », échec isolé, import, corbeille.
- `Tests/HackerOs.AppSdk.Blazor.Tests/HackFileOperationDialogsTests.cs` : question
  de conflit posée par le dialogue du fournisseur.
- `Tests/HackerOs.Apps.FileExplorer.FileControl.Tests/FileViewDropAndClipboardTests.cs` : dépôt
  sur le fond, remise à `DropRequested`, fichiers de l'hôte, raccourcis,
  éléments coupés, fil d'Ariane.
- `Tests/HackerOs.AppSdk.Tests/IO/HackDirectoryTests.cs` : `HackDirectory.CopyAsync`.
- `Tests/HackerOs.Apps.FileExplorer.Tests/FileExplorerLaunchArgumentsTests.cs` : dossier, `--select`, parent déduit, arguments ignorés (`v3/4`).

## Liste des tâches du chantier `rebuild/4-file-explorer`

- [x] `IAppClipboardGateway` complet, `HackClipboard`, `HackClipboardFileList` (format GNOME), anciens membres `[Obsolete]`.
- [x] `SystemClipboard` et `AppClipboardGateway` dans Platform Core, branchés par la fabrique et l'hôte.
- [x] `HackDirectory.CopyAsync` (copie récursive).
- [x] `FileTransfer` : copie, déplacement avec conflits, import de fichiers de l'hôte, corbeille, résumé des échecs.
- [x] `FileView` : dépôt sur le fond et les nœuds de l'arbre, `DropRequested`, `ClipboardRequested`, `CanPaste`, `CutPaths`, Ctrl+A, entrées de menu.
- [x] `FileViewBreadcrumb` et `FileViewDropZone` ; écouteurs natifs et fichiers de l'hôte dans `FileView.razor.js`.
- [x] Explorateur : barre d'outils Cut/Copy/Paste, fil d'Ariane, dépôts sur Home, Trash et la vue Corbeille, fenêtre de conflit, notification des échecs, envoi par `FileTransfer`.
- [x] Tests, ce document, `app-sdk.md`, `hack-file-api.md`, `trash.md`, plan et index.

## Tâches du chantier `v3/4-intents`

- [x] L'explorateur ouvre le dossier passé en argument (il ouvrait toujours le dossier personnel).
- [x] `--select=<chemin>` : sélection et défilement (`FileView.RevealAsync`, `revealItem`).
- [x] Tests et ce document.

## Tâches du chantier `v3/4b` (ADR 0121)

- [x] « Ouvrir avec » propose « Toujours utiliser cette app pour les fichiers
  .ext » quand l'app choisie déclare l'extension ; le choix est écrit dans
  `~/.config/hackeros/file-associations.json` par
  `AppContext.Catalog.SetUserDefaultHandlerAsync`
  ([`user-file-associations.md`](user-file-associations.md)).
- [x] L'explorateur déclare `file-associations.read`.

## Tâches du chantier `exec/5-background-jobs` (ADR 0140)

- [x] Compresser, Extraire et « UnZip Here… » en tâches `file-explorer:compress` et `file-explorer:extract`.
- [x] Copies et téléchargement gardés dans la fenêtre, raison écrite.
- [x] Test de l'aller-retour compression puis extraction (`FileExplorerZipJobsTests`).

## Tâches du chantier `desk/3-file-control` (ADR 0155)

- [x] Contrats du SDK : `HackFileView`, `IHackFileView`, `HackFileViewItem`, `HackFileViewFeatures`, `HackFileMenuOpeningEventArgs`, `FileMenuScope`, `HackFileMenuItems`.
- [x] `ExplorerFileView` : `FileView` liée au menu de l'OS (`FileViewMenuBinding`), commandes, corbeille, presse-papiers, transferts, ouverture.
- [x] Menus du plan §7 (`ExplorerFileMenu`), « Ouvrir avec » en sous-menu.
- [x] Dialogues fournis (`ExplorerFileDialogs`) par `IDialogService` et le proxy de la plateforme ; dialogues MudBlazor retirés.
- [x] Fenêtre de l'explorateur sur `HackFileView` ; « UnZip Here… » et « Publish as a web site… » par `ContextMenuOpening` (remplacés au chantier 4 par des entrées inscrites, ADR 0159).
- [x] Éditeur de code sur `HackFileView` ([`code-editor.md`](code-editor.md)).
- [x] Vue des dialogues Ouvrir / Enregistrer : chantier `desk/3b` ([ADR 0157](adr/0157-file-dialogs-show-the-file-control.md)).

## Série `explorer/base` (ADR 0185)

Améliorations décidées dans l'[ADR 0185](adr/0185-file-explorer-improvements.md) (issue #241). Les points
« archive ouverte comme un dossier » (§6 à §9) ne font pas partie de cette série : ils sont faits
ensuite, voir la section suivante.

- [x] `explorer/1-empty-state` (§2) : le message de dossier vide est dans la liste du contrôle partagé
  (`FileView.EmptyText`) : une ligne sous les en-têtes en mode Détails, centré en mode Icônes. Une
  recherche qui cache tout affiche « Aucun résultat pour « x » » (`FileView.NoMatchText`, donné par la
  fenêtre de l'explorateur) ; un autre filtre affiche « Aucun élément à afficher ici. ». Rien sur le
  bureau ni dans l'arbre. Les dialogues Ouvrir et Enregistrer en profitent. Tests :
  `FileViewEmptyStateTests`.
- [x] `explorer/2-stray-drop` (§3) : un dépôt qui tombe hors de toute zone de dépôt ne fait rien, dans
  toutes les fenêtres (fenêtres détachées comprises) : le navigateur n'ouvre plus le fichier. Garde
  installée par l'initialiseur JS de `HackerOs.Platform.Blazor` (`installStrayDropGuard`) : écouteurs
  `dragover` et `drop` sur `window`, en phase de bouillonnement, qui n'agissent que si aucune zone n'a
  annulé l'évènement (curseur « interdit », action par défaut annulée). Les champs de texte gardent le
  dépôt de texte du navigateur.
- [x] `explorer/3-resizable-sidebar` (§1) : une poignée sur le bord droit du panneau Places
  (`ExplorerSidebarResizer`, rôle `separator`) : glisser la redimensionne entre 140 et 400 px, un
  double-clic remet la largeur par défaut (150 px), les flèches gauche et droite la changent de 16 px,
  Début et Fin vont aux bornes. La largeur est retenue par utilisateur dans
  `~/.config/hackeros/file-explorer.json` (`sidebarWidth`, `FileExplorerSettings` garde les autres clés).
  Bureau seulement : la fenêtre mobile n'a pas de panneau. Tests : `FileExplorerSettingsTests`.
- [x] `explorer/4-hover-tooltips` (§16) : une bulle au style de l'OS apparaît après 600 ms de souris
  immobile sur un élément : nom complet, type, taille (fichier), date de modification et nombre
  d'éléments (dossier). Elle vit dans le contrôle partagé (`attachTooltips` de `FileView.razor.js`,
  lignes données par `FileView.GetItemTooltipAsync` et `FileViewTooltip`), donc le bureau et les
  dialogues l'ont aussi. Souris seulement : jamais au toucher (sur mobile, l'appui long ouvre le menu).
  Un clic, une touche, la molette, un glisser ou la sortie de l'élément la cachent. Tests :
  `FileViewTooltipTests`.
- [x] `explorer/5-recursive-size` (§5) : taille totale récursive dans le service de fichiers
  (`IAppFileSystemGateway.MeasureAsync`, `FileSystemTreeSize`, `FileSystemTreeMeasure`) et le SDK
  (`HackDirectory.GetTotalSizeAsync`, `HackDirectoryInfo.GetTotalSizeAsync`, `HackDirectorySize`, voir
  [`hack-file-api.md`](../framework/hack-file-api.md)), ajoutés aux `PublicAPI.Unshipped.txt`. Le
  dialogue Propriétés d'un dossier affiche « 1 234 octets (5 fichiers, 2 dossiers) ». Tests :
  `HackDirectorySizeTests`.
- [x] `explorer/6-file-menu-actions` (§10 à §13) : la fenêtre de l'explorateur (bureau et mobile)
  ajoute au menu d'un ou plusieurs éléments, juste avant Propriétés : « Compresser en .zip… » (nom
  demandé, prérempli), « Télécharger » (un fichier tel quel, sinon un `.zip`), « Copier le chemin »
  (chemins absolus, un par ligne, dans le presse-papiers texte) et « Envoyer vers ▸ » : Bureau (un
  raccourci vers chaque élément, pas une copie), Dossier compressé (`.zip` à côté, nom par défaut
  numéroté s'il est pris), Mon ordinateur (téléchargement) et chaque lecteur réseau prêt (copie à sa
  racine). Code dans `FileExplorerWindow.Menu.cs`, par l'évènement `ContextMenuOpening` du contrôle :
  le bureau et les dialogues gardent le menu du contrôle. Le raccourci vers un fichier ou un dossier
  est le nouveau `HackDesktopLink` du SDK (`Type=Link`, voir
  [`desktop-api.md`](../framework/desktop-api.md)), que le contrôle affiche et ouvre. Tests :
  `HackDesktopLinkTests`, `FileExplorerMenuActionsTests`.
- [x] `explorer/7-folder-previews` (§14) : en mode Icônes, l'icône d'un dossier devient un dossier
  ouvert avec les mini-icônes de ses 4 premiers éléments (dossiers d'abord, puis par nom, éléments
  cachés exclus) ; un dossier vide garde l'icône fermée. Le contenu n'est lu que pour les tuiles
  visibles (`attachFolderPreviews` de `FileView.razor.js`, un `IntersectionObserver` qui appelle
  `FileView.LoadFolderPreviewsAsync`), puis gardé en cache par révision du dossier
  (`FileView.Previews.cs`). Chaque dossier prévisualisé est surveillé (ses enfants directs, au plus
  64 dossiers) : un changement dedans relit l'aperçu en gardant l'ancien affiché ; ces surveillances
  s'arrêtent quand la vue quitte le dossier. Les modes Détails, arbre
  et bureau ne changent pas ; une icône remplacée (raccourci) n'a pas d'aperçu. Tests :
  `FileViewFolderPreviewTests`.
- [x] `explorer/8-network-drive-refresh` (§15) : un dossier de lecteur réseau affiché (ou surveillé
  par n'importe quelle app) suit les changements faits sur l'ordinateur hôte. Quand une app surveille
  un dossier d'un lecteur réseau (`AppFileSystemWatchGateway`), le service de fichiers
  (`IExternalFileSystemChanges.TrackExternalChanges`) relit ce dossier toutes les 5 secondes pendant
  que la page est visible, tout de suite quand elle revient (onglet réaffiché ou focus,
  `IHostActivitySignal` / `BrowserHostActivitySignal`), et tout de suite quand le `FileSystemObserver`
  du navigateur signale un changement (`IObservableHostFolder`, Chrome récent). Chaque lecture est
  comparée à la précédente (`HostFolderChangeTracker`) : les éléments créés, supprimés ou modifiés
  sont publiés par le même chemin que les écritures de l'OS, et la révision du dossier monte. Une
  seule boucle par dossier, arrêtée quand la dernière surveillance se termine. Tests :
  `HostFolderChangeTrackerTests`. Non vérifié dans un vrai navigateur : il faut un vrai dossier hôte
  choisi par l'utilisateur.
- [x] `explorer/9-drag-out-download` (§4, §5) : glisser des éléments hors du navigateur les télécharge
  (Chrome et Edge, entrée `DownloadURL`) : un fichier seul tel quel, un dossier ou plusieurs éléments en
  `.zip` (`Dossier.zip`, ou `Archive.zip` pour plusieurs). Le navigateur ne prend le contenu que pendant
  le `dragstart`, donc il est préparé dès l'appui du bouton gauche de la souris sur un élément
  (`attachExternalDrag` de `FileView.razor.js` appelle `FileView.PrepareExternalDragAsync` avec les
  éléments sélectionnés) : la taille donnée par l'OS décide d'abord (`FileMetadata.Length`, ou
  `MeasureAsync` pour un dossier), puis le contenu est lu ou compressé dans un blob de la page
  (`prepareDownload`, un seul à la fois). Au-delà d'environ 50 Mo rien n'est préparé et le glisser
  affiche une bulle « Trop gros pour glisser hors du navigateur (taille) : utilisez Télécharger » ; le
  glisser vers un dossier de HackerOS reste possible. Valable pour toutes les vues du contrôle partagé,
  bureau compris ; pas au toucher. Si le glisser commence avant la fin de la préparation (gros dossier),
  il reste interne. Code dans `FileView.ExternalDrag.cs`. Tests : `FileViewExternalDragTests`.
- [x] `explorer/network-drives-sidebar-alignment` (après la série, demande de Benoit du 2026-10-29) :
  la section « Network drives » du panneau de gauche est alignée à gauche comme « Places » (le style
  global des boutons centrait leur contenu ; `.sidebar-entry` force `justify-content: flex-start`). Un
  nom trop long ne passe plus à la ligne et n'est plus coupé : il reste sur une ligne, et le panneau
  affiche une barre de défilement horizontale (`overflow-x: auto` sur `.explorer-sidebar`).
- [x] Erreurs en boîte de message (demande de Benoit du 2026-10-29) : les erreurs de l'explorateur ne
  s'affichent plus en texte rouge au-dessus de la liste (ni dans le bandeau du mobile). Elles ouvrent la
  boîte de message du SDK de type Erreur (`MessageBoxIcon.Error`, bouton OK, titre « Explorateur de
  fichiers ») ; la même erreur signalée deux fois n'ouvre qu'une boîte. Tout passe par `ReportError`
  de `FileExplorerWindow.razor`.

## Archive `.zip` ouverte comme un dossier (ADR 0185 §6 à §9)

Chantier `todo/10-zip-as-folder` de la série `todo/base` (issue #241).

**But.** Un double-clic sur un `.zip` dans une fenêtre de l'explorateur l'ouvre comme un dossier en
lecture seule. Le fil d'Ariane et la barre d'adresse montrent `Documents › photos.zip › 2024`
(chemin `/home/u/Documents/photos.zip/2024`). Un double-clic sur un `.zip` du bureau lance
l'explorateur sur l'archive (le manifeste déclare `application/zip`, `.zip` dans ses `fileHandlers`).

**Architecture.**

- `Archives/ZipArchiveIndex.cs` : la table des matières d'un `.zip`, lue une fois depuis ses octets
  (`System.IO.Compression`). Les dossiers que le zip sous-entend (`a/b/c.txt` sans entrée `a/`) sont
  ajoutés. Les noms sont nettoyés comme à l'extraction ; une entrée qui remonte (`..`, « zip slip ») ou
  dont le nom n'est pas un chemin HackerOS valide est ignorée.
- `Archives/ZipArchiveBrowser.cs` : trouve l'archive d'un chemin (le premier segment `*.zip` qui est un
  fichier ; un dossier nommé `x.zip` reste un dossier ; un chemin sans segment `.zip` ne coûte aucune
  lecture), garde en cache les 4 dernières archives tant que leur révision ne change pas, et extrait
  une entrée (fichier ou dossier entier) vers un chemin du système de fichiers.
- `Archives/ZipArchiveFileSystemGateway.cs` : le système de fichiers vu par la vue de l'explorateur.
  Un chemin qui ne passe pas par une archive va tel quel à la passerelle de l'explorateur, avec ses
  droits. Dans une archive : `EnumerateAsync`, `StatAsync`, `ReadAsync` et `MeasureAsync` lisent
  l'archive (fichiers `r--r--r--`, dossiers `r-xr-xr-x`, propriétaire et révision de l'archive,
  identifiants stables) ; toute écriture est refusée avec `ProtectedEntry`. L'archive elle-même reste
  un fichier pour `StatAsync`. `ZipArchiveWatchGateway` surveille un dossier d'archive par le dossier
  qui contient le `.zip` : la liste est relue quand l'archive change.
- `ExplorerFileView.Archives.cs` : seules les fenêtres de l'explorateur (bureau et mobile) activent
  ces règles, par la valeur en cascade `ExplorerFileView.ArchiveBrowsingName`. Le même contrôle montré
  par une autre app, un dialogue de fichiers ou le bureau ne voit jamais l'intérieur d'une archive
  (§7) : rien n'est monté, le terminal et les autres apps ne voient que le fichier `.zip`.

**Comportement.**

- Ouvrir un fichier de l'archive (double-clic, Ouvrir, Ouvrir avec) l'extrait d'abord dans un nouveau
  dossier `/tmp/<archive>-<8 caractères>/` (droits `700`), puis l'ouvre avec son app. Une
  modification de cette copie ne revient pas dans l'archive. Un `.zip` dans l'archive s'ouvre comme
  dossier depuis sa copie.
- Glisser ou coller (après Copier) des éléments de l'archive vers un dossier les extrait, avec le
  dialogue de conflit habituel (Remplacer, Garder les deux, Ignorer). Même un déplacement ne retire
  rien de l'archive. Le glisser hors du navigateur et Télécharger lisent aussi l'archive.
- Refusé avec le message « Cette archive est en lecture seule : extrayez-la pour modifier son
  contenu. » : renommer, supprimer, couper, coller ou déposer dans l'archive, nouveau dossier, nouveau
  fichier, téléverser, compresser, extraire, envoyer à la corbeille. Les entrées Renommer, Supprimer,
  Couper, Coller, Nouveau dossier et Propriétés sont cachées du menu dans une archive ; le menu de
  l'explorateur n'y garde que Télécharger et Copier le chemin.
- Une archive illisible donne « « x.zip » n'est pas une archive .zip lisible. ».
- `.zip` seulement (§8).

**Tâches.**

- [x] Index du `.zip`, lecteur d'archives avec cache, passerelle en lecture seule et surveillance.
- [x] Fenêtres de l'explorateur : double-clic, ouverture par `/tmp`, glisser et coller hors de
  l'archive, refus des changements, menus.
- [x] `fileHandlers` du manifeste pour `.zip`.
- [x] Tests : `ZipArchiveBrowsingTests` (unitaires), `ZipArchiveFolderTests` (UI.E2E).
