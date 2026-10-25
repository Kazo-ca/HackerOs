# ADR 0171 : explorateur de fichiers mobile et dialogues en plein écran

## Statut

Accepté le 2026-10-25 à la demande de Benoit (« j'aimerais que nous ayons une
version différente de cette application pour le mobile ; ça doit aussi être le
cas pour les dialogues FileOpen, FolderOpen, etc. Ils doivent s'ouvrir en
plein écran »). Complète l'[ADR 0165](0165-mobile-home-drawer-and-mouse-controls.md)
(point d'entrée mobile des apps système) et l'[ADR 0157](0157-file-dialogs-show-the-file-control.md)
(dialogues de fichiers). Corrige aussi l'alignement de « File Explorer » dans le
menu Démarrer.

## Contexte

- `AppDefinition.MobileEntryPoint` existe et le manifeste généré déclare un point
  d'entrée par plateforme (`platform.entryPoints`), mais l'OS ne s'en servait pas :
  le chargeur paresseux résolvait toujours le point d'entrée Desktop. Une app ne
  pouvait donc pas avoir de fenêtre propre au téléphone.
- Sur mobile, les dialogues de la plateforme (Ouvrir, Enregistrer, Choisir un
  dossier, saisie de texte, message, Ouvrir avec, Propriétés, conflit) ne
  s'affichaient pas du tout : `FileDialogWindowAdapter`, qui en fait des fenêtres,
  n'était créé que par la coquille Desktop. Et même créé, un dialogue naît sans le
  focus : la surface unique du mobile montrait l'app et réduisait le dialogue.
- Dans le menu Démarrer, un `<button>` centre son contenu. Les lignes dont la
  description est assez longue remplissent la ligne ; celle de « File Explorer »
  est la plus courte, son icône et son texte étaient donc décalés vers la droite.

## Décision

### 1. Point d'entrée par plateforme

- La découverte garde le point d'entrée principal (Desktop) et résout aussi, dans
  les assemblies de l'hôte, ceux que le manifeste déclare pour les autres
  plateformes : `AppDescriptor.PlatformEntryPointTypes` et
  `AppDescriptor.EntryPointTypeFor(platform)`. Un type introuvable, abstrait ou de
  la mauvaise base est ignoré : l'app retombe sur son point d'entrée principal,
  une variante ne peut jamais l'empêcher de démarrer.
- `WindowAppRenderer` crée le composant de la plateforme active au premier rendu
  de la fenêtre et le garde pour la vie de la fenêtre. Un changement de plateforme
  redémarre déjà les fenêtres dont le point d'entrée diffère
  (`PlatformShellSwitchCoordinator`) ; rien n'est échangé à chaud.

### 2. Explorateur de fichiers mobile

- `FileExplorerApp.MobileEntryPoint` est `FileExplorerMobileWindow`, qui hérite de
  `FileExplorerWindow` : même état, mêmes commandes, même contrôle de fichiers
  (`HackFileView`), seule la mise en page change. Les membres partagés de la
  fenêtre Desktop passent de `private` à `private protected` (aucune API publique
  ajoutée à la fenêtre Desktop).
- Mise en page d'un gestionnaire de fichiers de téléphone : barre du haut (retour,
  nom du dossier, recherche, menu « ⋮ »), puces Dossier perso et Corbeille, fil
  d'Ariane, liste ou grille. En bas : les actions sur la sélection (Copier, Couper,
  Renommer, Supprimer, Plus), sinon « Coller ici » quand le presse-papiers a des
  fichiers, sinon le bouton flottant « + » (Nouveau dossier, Nouveau fichier,
  Téléverser). Les autres commandes sont dans des feuilles en bas de l'écran.
- Un toucher ouvre l'élément (`HackFileView.OpenOnTap`, voir §3). Le
  mode sélection (menu « ⋮ » ▸ Sélectionner) fait d'un toucher un ajout ou un
  retrait (`FileView.TapTogglesSelection`) et se termine avec le dernier élément
  désélectionné. Le double-clic n'ouvre pas une seconde fois. L'appui long ouvre
  toujours le menu de fichiers de l'OS en feuille (ADR 0150 §6).
- La fenêtre implémente `IAppBackHandler` : le bouton Retour du système ferme la
  feuille, puis la recherche, puis la sélection, quitte la Corbeille, puis revient
  au dossier précédent ; ensuite l'OS reprend la main. Le manifeste ne déclare pas
  `supportsBack` (pas de bouton Retour dans le chrome Desktop).
- La colonne Type est masquée dans la liste mobile ; l'icône suffit.

### 3. Dialogues en plein écran sur mobile

- `MobileShell` crée aussi `FileDialogWindowAdapter` : les dialogues de la session
  deviennent des fenêtres sur mobile comme sur Desktop.
- `SingleSurfacePresentationPolicy.SelectSurfaceStack` donne la pile affichée :
  la fenêtre de l'app, puis ses dialogues modaux (un dialogue ouvert depuis un
  dialogue en dernier). `SelectPrimary` renvoie le haut de la pile, même avant que
  le dialogue ait le focus. Le Retour du système ferme donc le dialogue (étape 1
  de `MobileNavigationCommandsAdapter`).
- `SingleSurfaceArea` dessine toute la pile, agrandie à l'écran
  (`WindowsToMaximize`) ; seule la dernière fenêtre reçoit les entrées, les autres
  sont `inert`. Le propriétaire n'est plus réduit (`WindowsToMinimize`) et reste
  monté sous son dialogue, pour que l'app qui attend la réponse garde son état.
  Chaque hôte de fenêtre est son propre contexte d'empilement : rien d'une app
  (un bouton flottant) ne passe au-dessus de son dialogue.
- Un toucher entre dans un dossier ou prend un fichier, sans double-clic (choix
  de Benoit, « pour mobile seulement ») : le contrat `HackFileView` gagne le
  paramètre `OpenOnTap` (désactivé par défaut), que `FileDialogBrowser` active
  sur la plateforme mobile (`FileDialogBrowser.OpenOnTap`, `null` suit la
  plateforme active). L'explorateur le transmet à `FileView.TapOpens`.
- Les dialogues mobiles n'ont pas de barre de titre (choix de Benoit) : ils ont
  leurs boutons Annuler et le Retour du système.
- Le Desktop ne change pas : les dialogues y gardent leur taille et le
  double-clic.

### 4. Menu Démarrer

- `.app-launch-button` aligne son contenu à gauche (`justify-content: flex-start`)
  et le texte prend la place restante. Toutes les lignes, épinglées ou non, ont
  l'icône à la même position.

## Conséquences

- Une app tierce peut déclarer une fenêtre propre au téléphone avec
  `MobileEntryPoint` ; c'est désormais celle que voit l'utilisateur.
- API publique ajoutée (non livrée, prochaine mineure) : `AppDescriptor.PlatformEntryPointTypes`,
  `AppDescriptor.EntryPointTypeFor`, `SingleSurfacePresentationPolicy.SelectSurfaceStack`
  et `WindowsToMaximize`, `FileDialogBrowser.OpenOnTap`, la classe
  `FileExplorerMobileWindow` et `ExplorerFileView.OpenOnTap`. Dans le SDK (ajout
  compatible, prochaine mineure 2.1) : `HackFileView.OpenOnTap`. Un autre
  fournisseur du contrôle (ADR 0156) qui ignore ce paramètre garde le double-clic.
- Dans un dialogue qui accepte plusieurs fichiers, un toucher sur un fichier le
  prend seul ; Ctrl+clic sélectionne encore.

## Vérification

- Tests : `SingleSurfacePresentationPolicyTests` (pile, dialogue sans focus, dialogue
  réduit, propriétaire gardé, dialogue imbriqué), `AppEntryPointDiscoveryTests`
  (type mobile gardé, repli, point d'entrée partagé), `FileViewIconsTests` (toucher
  qui ouvre, double-clic ignoré, mode sélection), `FileExplorerMobileWindowTests`,
  `FileDialogBrowserTests` (`OpenOnTap` transmis au contrôle).
- Navigateur à 390 × 844 en mode mobile : explorateur mobile, dossier ouvert d'un
  toucher, « + » ▸ Nouveau dossier en plein écran, dialogue Ouvrir de l'éditeur de
  texte en plein écran, un seul toucher sur « Documents » qui y entre, Retour qui
  ferme le dialogue. Menu Démarrer Desktop : icône
  de « File Explorer » à la même position que les autres.
- Doc : [file-explorer.md](../file-explorer.md#explorateur-mobile-adr-0171),
  [mobile-home-screen.md](../mobile-home-screen.md#dialogues-en-plein-écran-adr-0171).
