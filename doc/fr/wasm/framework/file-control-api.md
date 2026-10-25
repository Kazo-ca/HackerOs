> [English version](../../../en/wasm/framework/file-control-api.md)

# Contrôle de fichiers de l'OS : usage depuis une app

Ce document décrit ce qu'une app obtient quand elle affiche le contrôle de fichiers (`HackFileView`) ou ouvre un dialogue de fichiers de l'OS, et ce qui change pour elle selon l'hôte. Le fournisseur de l'explorateur (`ExplorerFileView`, contrats et composition) est dans [`dev/file-explorer.md`](../dev/file-explorer.md).

## Affichage par `HackFileView`

Toute app l'affiche par `HackFileView` du SDK (`HackerOs.AppSdk.Blazor.Files`)
et l'explorateur aussi : sa fenêtre rend `<HackFileView>` et garde le chrome
(barres, Emplacements, Corbeille, barre d'état). Les boutons Renommer,
Supprimer, Couper, Copier, Coller, Propriétés et Ouvrir avec appellent
`IHackFileView.ExecuteAsync` avec l'identifiant de l'entrée de menu
correspondante, donc font la même chose que le menu. Nouveau dossier/fichier
par nom, envoi, téléchargement, compression, extraction et dépôt sur la
Corbeille restent dans la fenêtre ; ils passent eux aussi par `IHackFileView`
(dossier courant, sélection, `RefreshAsync`). Seuls le fil d'Ariane et les
dépôts sur Home et Trash utilisent l'implémentation interne (`FileView`) : si
un administrateur choisit un autre fournisseur (ADR 0156, code dans un chantier
suivant), la fenêtre affiche ce fournisseur et seuls ces deux extras
disparaissent.

Tout s'exécute **avec les droits de l'hôte** (`HostContext`) : l'éditeur de
code qui affiche le contrôle ne voit, ne copie et ne supprime que ce qu'il peut.

## Menus

Menus (plan §7) : `ExplorerFileMenu` remplit le menu à chaque ouverture ; un
élément hors de la sélection devient la sélection ; « Ouvrir avec ▸ » liste les
apps du type (défaut en tête, `open-with:<appId>`) puis « Choisir une autre
application… » ; plusieurs éléments : ni Renommer ni Ouvrir avec, Propriétés
désactivé. Un hôte retire des fonctions par `Features` et ajuste le menu par
`ContextMenuOpening`. Détails : [ADR 0155](../dev/adr/0155-file-control-implementation-adjustments.md).

## Dialogues de fichiers

Depuis `desk/3b` ([ADR 0157](../dev/adr/0157-file-dialogs-show-the-file-control.md)),
les dialogues Ouvrir, Enregistrer et Choisir un dossier de l'OS montrent aussi
ce contrôle (`FileDialogBrowser` de la plateforme, avec les droits de l'app
qui demande) : Ouvrir sans `Features`, Enregistrer et Choisir un dossier avec
Nouveau dossier et Renommer. Un fichier activé y est pris par le dialogue
(`Opening` mis à `Handled`), pas ouvert dans son app. Sans fournisseur, la
plateforme reprend sa liste `VirtualFolderBrowser`.

Sur la plateforme mobile ([ADR 0171](../dev/adr/0171-mobile-file-explorer-and-full-screen-dialogs.md)),
les dialogues s'ouvrent en plein écran et passent `OpenOnTap` au contrôle :
un toucher entre dans un dossier ou prend un fichier, sans double-clic. Une
app peut demander le même comportement avec `<HackFileView OpenOnTap="true">`.

## Langue

- Les menus et les dialogues du contrôle fourni (`ExplorerFileView`,
  `ExplorerFileDialogs`) parlent la langue de l'**app hôte**
  (`HostContext.GetLocalizer<Strings>()`) : dans l'éditeur de code en français,
  le menu de l'arbre est en français même si l'explorateur tourne en anglais.
  Clés `Menu.*`, `Dialog.Loading`, `Error.DialogsUnavailable` (desk/3).
