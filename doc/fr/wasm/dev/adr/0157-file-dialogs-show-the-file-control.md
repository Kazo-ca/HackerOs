> [English version](../../../../en/wasm/dev/adr/0157-file-dialogs-show-the-file-control.md)

# ADR 0157 : les dialogues Ouvrir / Enregistrer / Choisir un dossier montrent le contrôle de fichiers

## Statut

Accepté le 2026-10-20.

- **Chantier :** `desk/3b-file-dialogs`, chantier séparé du chantier 3 choisi
  par Benoit (option « Chantier 3b séparé » de la carte du fil Desk 3,
  2026-10-20, ADR 0155 §6).
- **Ajuste :** l'ADR 0148 §2 (ligne `hackeros.file-dialogs` de la table) sur
  le contrat qui porte la vue de fichiers de ces trois dialogues ; ses autres
  décisions restent vraies.

## Contexte

Les dialogues Ouvrir, Enregistrer et Choisir un dossier
(`IFileDialogService`) sont dessinés par la plateforme : `OpenFileDialog`,
`SaveFileDialog` et `FolderSelectDialog`, avec une liste maison,
`VirtualFolderBrowser`. Le chantier 3 a fait du contrôle de fichiers de
l'explorateur (`hackeros.file-view`) la source de vérité de l'affichage des
fichiers (ADR 0155). L'ADR 0148 §2 range « la vue de fichiers des dialogues
Ouvrir / Enregistrer / Choisir un dossier » sous `hackeros.file-dialogs`, et
laisse `IFileDialogService` comme point d'entrée.

## Décision

### 1. La vue de ces dialogues est le contrat `hackeros.file-view`

La plateforme garde le dialogue : titre, type de fichier, nom du fichier,
confirmation de remplacement, Nouveau dossier, boutons, et la délivrance des
poignées (`FileDialogCoordinator`, inchangé). Seule la liste change : un
composant commun de la plateforme, `FileDialogBrowser`, affiche
**`HackFileView`**, donc le contrôle de l'app qui fournit
`hackeros.file-view`, plutôt que `VirtualFolderBrowser`.

On n'ajoute pas un troisième dialogue à `hackeros.file-dialogs` : le
fournisseur devrait redessiner tout le dialogue et la plateforme perdrait la
délivrance des poignées. Le contrôle suffit, et le fournisseur choisi par un
administrateur (ADR 0156) atteint ces dialogues sans rien de plus.
`hackeros.file-dialogs` garde « Ouvrir avec », « Propriétés » et le conflit de
noms.

### 2. Avec les droits de l'app qui demande

Le dialogue garde le contexte de l'app appelante
(`FileDialogPresentation.Context`, posé par le coordinateur) et le donne en
cascade au contrôle, comme `ProvidedFileDialog` (ADR 0155 §5) : le contrôle
liste, crée et renomme avec les droits de cette app (ADR 0148 §4), comme
`VirtualFolderBrowser` le faisait avec sa passerelle. La fenêtre du dialogue
n'étant pas une fenêtre d'app, `FileDialogBrowser` crée aussi la portée de
menus de cette app (`ContextMenuService.CreateScope`) : le menu de fichiers
s'ouvre dans le dialogue comme dans une fenêtre.

### 3. Ce que le contrôle fait dans chaque dialogue

| Dialogue | `Features` | Sélection gardée | Fichier activé |
|---|---|---|---|
| Ouvrir | `None` (parcourir) | fichiers ; plusieurs si `AllowMultiple` | ouvre le dialogue avec ce fichier |
| Enregistrer | `CreateFolder`, `Rename` | aucune | prend son nom |
| Choisir un dossier | `CreateFolder`, `Rename` si `AllowCreateFolder`, sinon `None` | dossiers | — |

- Un fichier activé (double clic, Entrée, « Ouvrir » du menu) passe par
  `Opening` avec `Handled = true` : le dialogue le prend, aucune app ne
  s'ouvre. Un dossier n'est pas pris en main : le contrôle y entre.
- Le filtre du type de fichier devient `Filter` (nom et type MIME) ; les
  dossiers restent visibles. Changer de type appelle `RefreshFilter`.
- Le dialogue dessine au-dessus du contrôle sa barre : dossier parent, fil
  d'Ariane (le contrôle n'en a pas, ADR 0155 §8) et le choix Détails / Icônes.
  Les erreurs du contrôle (`ErrorOccurred`) s'affichent dans le dialogue
  jusqu'au dossier suivant.
- Choisir un dossier crée son dossier sans révision du parent (le contrat
  n'en donne pas) ; un nom pris échoue toujours (`AlreadyExists`). Le dossier
  créé est sélectionné (`RevealAsync`).

### 4. Repli sur la liste de la plateforme

Sans fournisseur de `hackeros.file-view` (aucune app installée ou activée qui
le fournit, un hôte de test), ou sans contexte (une présentation construite à
la main), `FileDialogBrowser` affiche `VirtualFolderBrowser` dans le
`Unavailable` de `HackFileView`. Les dialogues marchent donc toujours, comme
ils marchaient avant ; ce repli vient après celui de l'ADR 0156 (retour à
l'explorateur), quand même l'explorateur manque.

## Conséquences

- Les dialogues ont la même liste que l'explorateur (colonnes, tri, icônes,
  menu de l'OS) et suivent ses corrections sans recompiler les apps.
- `FileDialogPresentation.Context` et `FileDialogBrowser` sont ajoutés à
  l'API de la plateforme (`PublicAPI.Unshipped.txt`). L'API du SDK ne change
  pas.
- Correction trouvée en vérifiant les dialogues : le contenu d'une fenêtre
  (`.window-content` de `WindowHost`) est placé explicitement dans la seconde
  rangée de sa grille. Placé automatiquement, il glissait dans une troisième
  rangée dès que la couche de la fenêtre (`.window-overlay`, ADR 0127 §3)
  était rendue, et laissait une bande vide sous la barre de titre.
- Les tests de bout en bout qui pilotent les dialogues de l'OS lisent des
  lignes (`role=row`) plutôt que des options ; ceux du banc d'essai sans
  explorateur (`HackerOs.E2E.Tests`) couvrent le repli.

## Références

- [ADR 0148](0148-sdk-contracts-and-os-provided-controls.md),
  [ADR 0155](0155-file-control-implementation-adjustments.md),
  [ADR 0156](0156-replaceable-file-control-provider.md).
- [`file-explorer.md`](../file-explorer.md),
  [`desktop-and-context-menu-plan.md`](../plans/desktop-and-context-menu-plan.md),
  [`sdk-reference.md`](../../framework/sdk-reference.md).
