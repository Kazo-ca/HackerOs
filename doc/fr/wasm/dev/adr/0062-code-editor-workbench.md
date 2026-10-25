# ADR 0062 : l'éditeur de code en atelier à la VS Code

## Statut

Accepté le 2026-10-11.

- **Contexte du plan :** après la phase 4 (refonte terminée), demande de
  Benoit du 2026-10-11 : l'éditeur ne prend pas toute la place, on ne peut ni
  ouvrir un dossier correctement, ni avoir plusieurs onglets utiles, ni créer
  de fichiers et de dossiers dans la liste ; il veut les fonctions de VS Code,
  sans compilation ni « aller à la définition », mais avec la recherche dans
  tout le dossier ouvert.
- **Remplace** la dernière puce de la section 4 de l'ADR 0058 (« Le panneau
  terminal de CodeEditor reste à un seul shell ») : le panneau terminal de
  l'éditeur a plusieurs onglets, un `hsh` par onglet, comme la console.
- **Complète** l'ADR 0020 (Monaco, frontière JavaScript collocée, tout le
  reste en C#) sans en changer les décisions.

## Contexte

L'éditeur affichait une barre d'outils, une bande d'onglets, l'arbre
`FileView` d'un dossier et un seul Monaco dont le contenu était remplacé à
chaque changement d'onglet (historique d'annulation et défilement perdus).
La hauteur de Monaco dépendait d'un `flex` sans position absolue : avec le
panneau terminal ouvert dans la fenêtre par défaut (800 × 560), il ne restait
qu'une ou deux lignes visibles. Rien ne permettait de créer un fichier dans
le dossier, de chercher dans ses fichiers ou d'en ouvrir un par son nom.

## Décision

### 1. Disposition de VS Code

La fenêtre suit l'atelier de VS Code : barre de menus (File, Edit, View, Go
en `MudMenu`, ADR 0016), barre d'activité (Explorer, Search, terminal), barre
latérale redimensionnable, groupe d'éditeurs (onglets, fil d'Ariane, Monaco),
panneau terminal redimensionnable, barre d'état (dossier, fichiers non
enregistrés, Ln/Col, encodage, fin de ligne, langage). Monaco occupe une zone
en `position: absolute; inset: 0` : il prend toujours tout l'espace restant.
Sans onglet ouvert, un filigrane rappelle les raccourcis.

### 2. Un modèle Monaco par onglet

Chaque onglet a son modèle Monaco (`showDocument`, `closeDocument`), créé à la
première ouverture : l'historique d'annulation, la sélection et le défilement
de chaque fichier sont gardés. Le contenu reste possédé par
`CodeEditorDocument` en C# ; Monaco renvoie `(id, contenu)` à chaque
modification, et la fenêtre ne se redessine que si l'état « modifié »
change. Le curseur est remonté à part (`OnCursorChanged`) vers un petit
composant de la barre d'état.

### 3. Le dossier ouvert est un espace de travail C#

`CodeEditorWorkspace` représente le dossier choisi dans la boîte « Open
Folder » et passe par la passerelle de fichiers liée à ce choix
(`WithSelectedHandle`). Il liste les fichiers (en sautant `.git`,
`node_modules`, `bin`, `obj`…, au plus 5 000), crée fichiers et dossiers, et
sert l'explorateur, la recherche et l'ouverture rapide. Les fichiers du
dossier sont lus et enregistrés par cette même passerelle.

### 4. Recherche et ouverture rapide en C#

La recherche dans le dossier (Ctrl+Maj+F) et l'ouverture rapide (Ctrl+P) sont
écrites en C# (`CodeEditorWorkspaceSearch`, `CodeEditorQuickOpen`), pas
dans Monaco : elles lisent des fichiers que Monaco n'a jamais ouverts, et la
règle de l'ADR 0020 garde la logique en C#. La recherche gère la casse, le mot
entier, les expressions régulières (.NET, délai de 250 ms par ligne), les
globs « files to include/exclude », et lit le contenu non enregistré des
onglets ouverts, comme VS Code. Rechercher et remplacer dans un fichier reste
celui de Monaco (Ctrl+F, Ctrl+H).

### 5. Supprimer passe par la corbeille

La touche Suppr et le menu de l'explorateur mettent les éléments à la
corbeille (`HackTrash`, ADR 0052) ; « Delete permanently » demande une
confirmation, comme dans l'explorateur de fichiers. Les onglets des fichiers
supprimés restent ouverts avec leur contenu : enregistrer recrée le fichier.
Renommer un fichier ou un dossier dans l'arbre déplace les onglets concernés
(`CodeEditorSession.RelocatePaths`).

### 6. Menu contextuel des éléments de `FileView`

Le clic droit sur un élément de `FileView` (arbre, détails, icônes) arrête la
propagation de l'évènement : jusque-là, le gestionnaire du fond recevait aussi
le clic et remplaçait le menu de l'élément par celui du fond. Toutes les apps
qui affichent `FileView` en profitent.

## Hors périmètre

Compilation et exécution, « aller à la définition », IntelliSense entre
fichiers, contrôle de source, débogueur, extensions, dossiers multiples :
Benoit les a exclus. La palette de commandes est celle de Monaco (F1).

## Conséquences

- L'éditeur reste une app sur le SDK public (ADR 0055) : `IJSRuntime` ne sert
  qu'à importer son propre module.
- Le bundle Monaco expose une API un peu plus large (`showDocument`,
  `closeDocument`, `reveal`, `runAction`, `getSelectedText`, `setOptions`) ;
  il est reconstruit par `npm run build` comme avant.
- La taille par défaut des fenêtres (800 × 560, `WindowLaunchCoordinator`)
  n'est pas changée : l'éditeur s'agrandit comme les autres apps.

## Alternatives écartées

- **Recherche par les modèles Monaco** (`model.findMatches`) : il faudrait
  charger chaque fichier du dossier dans Monaco, et la logique passerait en
  JavaScript.
- **Un seul modèle Monaco rechargé** (l'existant) : l'annulation et la
  position sont perdues à chaque changement d'onglet.
