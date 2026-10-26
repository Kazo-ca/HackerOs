> [English version](../../../en/wasm/dev/code-editor.md)

# Éditeur de code

`Apps/System/HackerOs.Apps.CodeEditor` (`org.hackeros.code-editor`) : éditeur
de code façon VS Code, sur Monaco 0.55 (ADR 0020) et disposé en atelier
(ADR 0062), avec un dock en bas (ADR 0063). Il ouvre des fichiers et des
dossiers du système de fichiers virtuel, cherche dans tout un dossier, et
embarque les terminaux de l'app Terminal.

L'ancienne documentation (passage de CodeMirror à Monaco, preuves d'août 2026)
est archivée dans [`old/code-editor.md`](old/code-editor.md).

## Fonctions

| Domaine | Ce que l'utilisateur fait |
| --- | --- |
| Dossier | File › Open Folder… (ou le bouton de l'explorateur vide) ; le bouton « Nouveau dossier » du dialogue crée le dossier d'un nouveau projet (ADR 0175) ; l'arbre du dossier s'affiche dans la barre latérale ; File › Close Folder. |
| Explorateur | Un clic montre un fichier sans quitter l'arbre (flèches, Suppr, F2 restent à l'arbre) ; double-clic ou Entrée l'ouvre et donne le focus à l'éditeur. Boutons « New File… », « New Folder… », « Refresh » ; le nom se tape en place dans le dossier sélectionné. L'arbre est le contrôle de fichiers de l'OS (`HackFileView` en mode arbre, fourni par l'explorateur, ADR 0155) : son menu est celui de l'OS (Ouvrir, Ouvrir avec, Couper, Copier, Coller dans le dossier, Renommer, Mettre à la corbeille, Supprimer définitivement, Propriétés), auquel l'éditeur ajoute New File…, New Folder…, Find in Folder… et Open in Integrated Terminal par `ContextMenuOpening`. Glisser-déposer, presse-papiers, corbeille et erreurs (vers la barre d'état) sont ceux du contrôle, avec la poignée du dossier choisi (`SelectedResource`). |
| Onglets | Plusieurs fichiers ouverts, chacun avec son historique d'annulation, sa sélection et son défilement. Point « ● » sur un onglet modifié, croix au survol ; clic du milieu pour fermer ; nom du dossier ajouté quand deux onglets ont le même nom. Ctrl+Tab et Ctrl+Maj+Tab changent d'onglet. |
| Recherche | Ctrl+Maj+F ou l'icône loupe : recherche dans tous les fichiers du dossier, pendant la frappe. Options casse (Aa), mot entier (ab), expression régulière (.*), « files to include » et « files to exclude » (`*.cs`, `src/**`). Résultats groupés par fichier, repliables ; un clic ouvre le fichier et sélectionne l'occurrence. Le texte sélectionné dans l'éditeur sert de recherche initiale. |
| Ouverture rapide | Ctrl+P : liste les fichiers du dossier, filtrés en sous-suite floue (`edwin` trouve `EditorWindow.razor`), flèches et Entrée. Sans dossier ouvert, Ctrl+P ouvre la boîte « Open File ». |
| Édition | Tout Monaco : rechercher/remplacer dans le fichier (Ctrl+F, Ctrl+H), aller à la ligne (Ctrl+G), commenter (Ctrl+/), formater, palette de commandes de Monaco (F1), multicurseur, mini-carte. View › Word Wrap (Alt+Z), Show/Hide Minimap. |
| Enregistrement | Ctrl+S, Ctrl+Maj+S (Save As), File › Save All. Les fichiers du dossier ouvert passent par l'accès accordé au dossier. Le contenu non enregistré est sauvegardé pour récupération et restauré au prochain lancement. |
| Dock du bas | Ctrl+J l'affiche ou le cache (il est caché, pas détruit : les shells continuent). Onglets **Status** (dossier, fichier, langage, onglets ouverts et non enregistrés, terminaux, puis journal horodaté des messages, effaçable), **Terminal** et **Problems** (vide pour l'instant). |
| Terminal | Ctrl+` ou l'icône du bas de la barre d'activité : onglet Terminal du dock. C'est le terminal de l'app Terminal, embarqué par le contrat `hackeros.terminal` (ADR 0063) : un `hsh` par onglet (« + »), démarré dans le dossier ouvert, titre = commande en cours ou dossier, effacer, tuer. « Open in Integrated Terminal » fait un `cd` visible dans le terminal actif s'il est libre, sinon ouvre un nouveau terminal. `exit` ferme l'onglet ; le dernier cache le dock. Sans app Terminal installée, l'onglet dit pourquoi. |
| Disposition | Barre latérale (Ctrl+B) et dock redimensionnables par glissement ; la zone Monaco prend toujours tout l'espace restant ; barre d'état avec le dernier message (un clic ouvre Status), Ln/Col, sélection, encodage, fin de ligne et langage (modifiable). Les erreurs restent aussi au-dessus de l'éditeur jusqu'à ce qu'on les ferme. |

Hors périmètre (ADR 0062) : compilation, exécution, « aller à la définition »,
IntelliSense entre fichiers, contrôle de source, débogueur, extensions.

## Architecture

| Élément | Rôle |
| --- | --- |
| `CodeEditorWindow.razor` / `.razor.cs` / `.razor.css` | Atelier : menus, barre d'activité, barre latérale, onglets, fil d'Ariane, zone Monaco, filigrane, dock du bas (Status, Terminal par `HackEmbeddedComponent<IHackTerminal>`, Problems), barre d'état. Possède la session, l'espace de travail, la récupération et le garde de fermeture. |
| `CodeEditorWindow.razor.js` | Module collocé : `createEditor` (Monaco + nom accessible), `attachWorkbench` (raccourcis capturés avant le navigateur et Monaco, puis `OnShortcut(nom)` ; poignées de redimensionnement qui posent `--sidebar-width` et `--panel-height` sur la racine). |
| `CodeEditor.monaco.entry.js` → `wwwroot/monaco/monaco.bundle.js` | Thème `hackeros-dark` et `createCodeEditor` : un modèle par onglet (`showDocument`, `closeDocument`), `setLanguage`, `reveal`, `runAction`, `getSelectedText`, `setOptions`, `flush`, `markSaved`, `layout`, `focus`, `destroy` ; renvoie `OnDirtyChanged(id, modifié)` à chaque bascule, `OnEditorChanged(id, contenu)` après 400 ms sans frappe, et `OnCursorChanged(ligne, colonne, sélection)` après 60 ms. |
| `CodeEditorSession`, `CodeEditorDocument` | Onglets et contenu en C# ; `RelocatePaths` suit un renommage de fichier ou de dossier. |
| `CodeEditorWorkspace` | Dossier ouvert : liste des fichiers (exclusions `.git`, `.svn`, `.hg`, `node_modules`, `bin`, `obj` ; au plus 5 000), création de fichiers et de dossiers, chemins relatifs. |
| `CodeEditorSearch.cs` | `CodeEditorTextSearch` (ligne par ligne, 1 000 occurrences par fichier, aperçu coupé autour de l'occurrence), `CodeEditorGlob`, `CodeEditorWorkspaceSearch` (5 000 occurrences au total, fichiers de plus de 1 Mio ignorés), `CodeEditorQuickOpen` (classement flou). |
| `CodeEditorExplorer`, `CodeEditorSearchView`, `CodeEditorQuickOpenPalette`, `CodeEditorCursorStatus` | Vues de la barre latérale, boîte Ctrl+P et position du curseur, chacune avec son CSS collocé. Depuis `desk/3`, `CodeEditorExplorer` affiche `HackFileView` (plus de référence à `HackerOs.AppSdk.FileView`) : `Opening` ouvre un fichier dans un onglet (`Handled`), `SelectionChanged` le montre, `Renamed` fait suivre les onglets. |

Le contenu de chaque fichier appartient à `CodeEditorDocument` (C#). Monaco
garde un modèle par onglet pour l'historique et la position ; quand un onglet
devient actif, `showDocument` ne remplace le texte du modèle que s'il diffère
de celui du document (rechargement, récupération).

Une touche ne coûte rien côté .NET. Monaco garde le texte et compare la
version de son modèle à celle du dernier enregistrement
(`getAlternativeVersionId`) : l'état « modifié » n'est envoyé que quand il
bascule (`OnDirtyChanged`), et le texte entier n'est envoyé qu'après 400 ms
sans frappe (`OnEditorChanged`). Avant d'enregistrer, de fermer un onglet ou
de chercher dans le dossier, la fenêtre appelle `flush` pour recevoir le texte
en attente ; après un enregistrement, `markSaved` déplace la référence de
Monaco. La copie de récupération est écrite 1,5 s après la dernière
modification. Avant, chaque touche envoyait tout le fichier à .NET puis
relançait l'écriture de récupération : coller 1 000 lignes figeait la page
133 s dans Chromium sans tête, contre 40 s maintenant (le reste est Monaco
lui-même), et la frappe dans ce fichier coûte environ 23 % de script en moins.
La position du curseur est affichée par `CodeEditorCursorStatus`, qui se
redessine seul.

Chaque modification annule l'écriture de récupération en attente. Le système
de fichiers répond alors `Cancelled`, que `CodeEditorRecoveryStore` et
`CodeEditorFileService` traduisent en statut `Cancelled` sans texte d'erreur :
la fenêtre ne l'affiche jamais. Avant, ce code brut arrivait dans le bandeau
d'erreur (« Cancelled ») pendant la frappe.

Arrêt demandé par l'OS (ADR 0069 : déconnexion, `kill`, « End task ») :
`OnAppStopRequestedAsync` annule l'attente de 1,5 s et écrit tout de suite la
copie de récupération si un onglet est modifié ; l'OS attend cette écriture
avant de fermer la fenêtre. La recherche dans le dossier et le parcours du
dossier appellent `HackProcess.CheckpointAsync` entre deux fichiers ou
dossiers : la page reste fluide et la recherche s'arrête sur une demande
d'arrêt.

Depuis `exec/5` (ADR 0140), la vue Recherche lance la recherche comme **tâche
en arrière-plan** (`CodeEditorWorkspaceSearch.SearchInBackgroundAsync`,
[`background-jobs.md`](../framework/background-jobs.md)) : la fenêtre liste le dossier,
puis le processus enfant `code-editor:search` lit chaque fichier et applique le
motif, dans un Web Worker quand l'hôte en a. Les onglets non enregistrés
passent dans l'entrée de la tâche ; un motif invalide est signalé tout de suite,
sans processus. Une nouvelle frappe annule la tâche précédente (code 130).
`SearchAsync` reste la recherche en ligne, partagée avec la tâche
(`SearchFilesAsync`) et utilisée par les tests.

Entrée dans la boîte Ctrl+P et dans la zone de création de l'explorateur
agit au relâchement de la touche : l'éditeur reprend le focus quand la boîte
se ferme, et un traitement à l'appui laisserait la fin de la frappe insérer
un saut de ligne dans le fichier.

## Raccourcis

| Raccourci | Action |
| --- | --- |
| Ctrl+N / Ctrl+O | Nouveau fichier texte / ouvrir un fichier |
| Ctrl+S / Ctrl+Maj+S | Enregistrer / enregistrer sous |
| Ctrl+W | Fermer l'onglet |
| Ctrl+P | Aller au fichier (ouverture rapide) |
| Ctrl+Maj+F / Ctrl+Maj+E | Vue Recherche / vue Explorateur |
| Ctrl+B | Afficher ou masquer la barre latérale |
| Ctrl+` | Afficher ou masquer le terminal |
| Ctrl+J | Afficher ou masquer le dock du bas |
| Ctrl+Tab / Ctrl+Maj+Tab | Onglet suivant / précédent |
| Alt+Z | Retour à la ligne automatique |
| F1, Ctrl+F, Ctrl+H, Ctrl+G, Ctrl+/ | Monaco (palette, rechercher, remplacer, aller à la ligne, commenter) |

Dans le terminal, seuls Ctrl+` et Ctrl+J sont interceptés ; les autres touches
vont au shell. Chrome garde Ctrl+N, Ctrl+W et Ctrl+T pour lui tant que
HackerOS n'est pas installé en PWA ; chaque commande a aussi son entrée de
menu.

## Mettre à jour Monaco

Comme avant : modifier `package.json`, puis dans
`Apps/System/HackerOs.Apps.CodeEditor` lancer `npm ci && npm run build`, et
versionner `wwwroot/monaco/`. Le build .NET n'exécute pas npm.

## Tâches

- [x] Reproduire les défauts (zone d'édition réduite à une ligne, pas de
  création dans l'arbre, onglets qui perdent leur état).
- [x] Monaco : un modèle par onglet, curseur, `reveal`, actions, options,
  thème `hackeros-dark`.
- [x] `CodeEditorWorkspace` : liste des fichiers, création, chemins relatifs.
- [x] Recherche dans le dossier (casse, mot entier, regex, globs, contenu non
  enregistré) et ouverture rapide floue.
- [x] Recherche dans le dossier en tâche `HackBackground` (`exec/5`, ADR 0140).
- [x] Vues Explorateur (création en place, menu contextuel, corbeille) et
  Recherche ; boîte Ctrl+P.
- [x] Atelier : menus, barre d'activité, barre latérale et terminal
  redimensionnables, fil d'Ariane, filigrane, barre d'état Ln/Col.
- [x] Raccourcis de VS Code capturés par le module collocé.
- [x] Onglets qui suivent un renommage ; Save All ; Close All.
- [x] `FileView` : le clic droit sur un élément n'ouvre plus le menu du fond.
- [x] Tests : espace de travail, recherche, globs, ouverture rapide,
  déplacement des onglets.
- [x] Essai dans Chromium : dossier, création, recherche, Ctrl+P, Ctrl+S,
  suppression, menu contextuel, redimensionnement.

Lenteur et dock (ADR 0063) :

- [x] Mesurer la frappe et le collage dans un fichier de 1 000 lignes, avant
  et après.
- [x] Ne plus envoyer le texte à .NET à chaque touche (`OnDirtyChanged`,
  envoi après une pause, `flush`, `markSaved`).
- [x] Dock du bas : Status (faits et journal), Terminal, Problems ; Ctrl+J.
- [x] Terminal de l'app Terminal embarqué par contrat, « Open in Integrated
  Terminal ».
- [x] Essai dans Chromium : terminal embarqué, `cd`, titre, onglets, `exit`,
  Ctrl+J, Status, Problems.
- [x] Une écriture annulée (récupération remplacée par une frappe plus
  récente, fenêtre fermée) n'est plus affichée comme une erreur.

## Points revérifiés (v3/11, ADR 0103 §3)

Les deux points encore ouverts dans l'ancien suivi (P4-W3-002, P4-W3-007) ont
été revus dans le code le 2026-10-17 :

- **Ressources hors ligne.** Monaco, ses workers (`editor`, `json`, `css`,
  `html`, `ts`) et sa police `codicon.ttf` sont livrés dans
  `wwwroot/monaco/` ; aucun CDN. Les workers sont résolus par
  `new URL(…, import.meta.url)`, donc sous `_content/…` comme le module.
  **Corrigé :** le service worker publié ne mettait pas les polices en
  cache (`.ttf` absent de `offlineAssetsInclude`), ce qui faisait
  disparaître les icônes de Monaco hors ligne. `.ttf`, `.woff2`, `.svg` et
  `.webp` y sont ajoutés.
- **Gros fichiers.** Un document est limité à 1 MiB
  (`CodeEditorDocument.MaxDocumentBytes`) à l'ouverture, à l'écriture et
  dans la recherche (les fichiers plus gros sont ignorés) ; la récupération
  est limitée à 8 MiB et l'espace de travail à 5 000 fichiers. Le refus
  donne un message clair au lieu de figer l'onglet.
- **Nettoyage des workers.** `DisposeAsync` appelle `destroy` (abonnements,
  éditeur, tous les modèles, observateur ARIA) puis `dispose` de l'atelier
  (écouteurs clavier, séparateurs) et libère le module. Sans modèle ouvert,
  Monaco arrête lui-même ses workers de langage après sa période d'inactivité ;
  ils sont partagés entre fenêtres, donc on ne les termine pas de force.
- Reste vrai : la preuve de bout en bout dans une sortie publiée, hors ligne
  et chargée à la demande n'est pas automatisée (les tests E2E tournent sur
  l'hôte de développement).

Contrôle de fichiers de l'OS (`desk/3-file-control`, ADR 0155) :

- [x] Arbre de l'explorateur latéral sur `HackFileView` (mode arbre, sélection
  simple, poignée du dossier choisi par `SelectedResource`).
- [x] Entrées de l'éditeur ajoutées au menu de l'OS par `ContextMenuOpening`
  (fond : New File…/New Folder… à la place de « Nouveau dossier » ; dossier :
  les mêmes, Find in Folder…, Open in Integrated Terminal).
- [x] Suppression propre à l'éditeur retirée (celle du contrôle), textes
  `Delete.*` et `Dialog.Delete.*` retirés, erreurs du contrôle vers la barre
  d'état.
