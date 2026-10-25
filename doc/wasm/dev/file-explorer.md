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
