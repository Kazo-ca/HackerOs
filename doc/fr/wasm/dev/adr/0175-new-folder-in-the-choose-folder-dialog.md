> [English version](../../../../en/wasm/dev/adr/0175-new-folder-in-the-choose-folder-dialog.md)

# ADR 0175 : « Nouveau dossier » dans le dialogue Choisir un dossier

## Statut

Accepté le 2026-10-26 à la demande de Benoit (« dans la boîte de dialogue
`Open Folder` on doit ajouter l'option d'ajouter un nouveau dossier »). Complète
l'[ADR 0157](0157-file-dialogs-show-the-file-control.md) (les dialogues montrent
le contrôle de fichiers) et l'[ADR 0171](0171-mobile-file-explorer-and-full-screen-dialogs.md)
(dialogues en plein écran sur mobile).

## Contexte

- Le dialogue « Open Folder » de Code Editor est le dialogue Choisir un dossier de
  la plateforme (`FolderSelectDialog`). Il sait créer un dossier depuis l'ADR 0157,
  mais seulement si l'app le demande (`SelectFolderDialogRequest.AllowCreateFolder`).
  Code Editor ne le demandait pas : aucun moyen de créer un dossier.
- Quand l'app le demande, la création passait par une zone de nom et un bouton sous
  la liste. Le contrôle de fichiers de l'OS a déjà sa propre commande « Nouveau
  dossier » (`HackFileMenuItems.NewFolder`) : elle crée « Nouveau dossier » (ou
  « Nouveau dossier (2) »), le sélectionne et le met en renommage, comme le menu
  contextuel de l'explorateur. Elle n'était accessible que par ce menu (clic droit,
  appui long sur mobile), que l'utilisateur ne voit pas.

## Décision

1. **Bouton dans la barre du dialogue.** `FileDialogBrowser` affiche un bouton
   « Nouveau dossier » (icône et libellé ; icône seule sur un dialogue étroit) dans
   sa barre, entre le fil d'Ariane et les modes d'affichage, quand le dialogue
   autorise `HackFileViewFeatures.CreateFolder` et que le contrôle de fichiers est
   affiché. Il lance la commande du contrôle
   (`IHackFileView.ExecuteAsync(HackFileMenuItems.NewFolder)`), donc le dossier est
   créé avec les droits de l'app, puis mis en renommage. Le bouton est désactivé
   quand `CanExecute` le refuse. Il marche de la même façon sur bureau et sur
   mobile (le dialogue plein écran de l'ADR 0171 garde la barre).
2. **Une seule façon de créer.** `FolderSelectDialog` ne montre sa zone de nom que
   lorsque la liste de la plateforme remplace le contrôle (aucun fournisseur de
   `hackeros.file-view`) : cette liste ne sait que parcourir. `FileDialogBrowser`
   le signale par un nouveau paramètre `FileControlChanged` (`EventCallback<bool>`,
   API publique de `HackerOs.Platform.Blazor`, dans `PublicAPI.Unshipped.txt`).
3. **Le dialogue Enregistrer** autorisait déjà `CreateFolder` : il reçoit le même
   bouton.
4. **Code Editor** passe `AllowCreateFolder = true` pour « Open Folder » : on peut
   y créer le dossier d'un nouveau projet.
5. **Deux corrections du contrôle de fichiers**, trouvées par le test navigateur :
   - `CanExecute(NewFolder)` attend la première lecture du dossier
     (`FileView.HasListing`) : un clic pendant le chargement créait le dossier à la
     racine (`/New Folder`, refusé faute de droit).
   - Un rechargement du même dossier garde l'élément en cours de renommage
     (même chemin, même révision) : la surveillance du dossier recharge la liste
     juste après la création, ce qui effaçait le renommage.
6. Le SDK ne change pas : `AllowCreateFolder` garde sa valeur par défaut `false`.
   Chaque app choisit ; WebServer et Code Editor l'activent.

## Conséquences

- Le dialogue n'ouvre pas de second dialogue (une saisie de texte attendrait
  derrière le dialogue actif) : le nom se tape dans la liste, comme dans
  l'explorateur.
- Tests : `FileViewTests` (pas de création avant la première lecture, le
  renommage survit à un rechargement), `FileDialogBrowserTests` (le bouton est là quand le dialogue le permet,
  absent d'un dialogue Ouvrir, `FileControlChanged`) et UI.E2E
  `CodeEditorOpenFolderTests` (bureau et mobile : Nouveau dossier, renommage,
  Sélectionner).
