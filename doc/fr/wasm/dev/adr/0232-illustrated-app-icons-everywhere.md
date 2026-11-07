> [English version](../../../../en/wasm/dev/adr/0232-illustrated-app-icons-everywhere.md)

# ADR 0232 : icônes d'apps illustrées, affichées partout

## Statut

Acceptée le 2026-11-07 ; Benoit a pris les quatre recommandations (fil
« Améliorer les icônes des applications », voir « Questions posées à Benoit »).
Série `icons/base`.

- **Remplace :** le dernier point de la décision de l'[ADR 0164](0164-app-icons-and-window-icon-context-menu.md)
  (« Le lanceur et les raccourcis gardent leur sélection Lucide existante ») et
  la règle des deux premières lignes du tableau §4 de l'[ADR 0203](0203-icon-packs.md)
  (menu Démarrer et raccourcis d'apps).
- **Ne change pas :** l'icône de fenêtre et de taskbar (ADR 0164), le menu de
  l'icône de fenêtre, le format des packs d'icônes et leur priorité pour une app
  nommée (`apps/<id>`), les icônes de fichiers et de dossiers.

## Contexte

- Les quinze apps système fenêtrées déclarent `icons/app.svg` (ADR 0164, puis
  l'app Kazo OS Store) :
  une tuile de couleur unie et un glyphe Lucide blanc. Ce dessin est correct,
  mais peu représentatif, et proche des petits symboles de l'interface.
- Cette icône ne se voit que dans la barre de titre, la taskbar et l'accueil
  mobile. Le menu Démarrer, les raccourcis du bureau et l'explorateur dessinent
  `KazoAppIcons.LucideName`, une icône Lucide choisie **par catégorie** : six
  apps de la catégorie `system` (Kazo OS Store, Éditeur de thème, Moniteur
  système, Diagnostic, Journal d'erreurs, Gestionnaire de serveur web) ont la
  même icône, et les apps sans catégorie (Calculatrice, Terminal, Éditeur de
  texte, Visionneuse d'icônes) ont la fenêtre générique `app-window`.
- Les apps tierces publiées (dépôts `Os-App-*`) ont déjà une icône illustrée :
  une tuile sombre en dégradé et un petit dessin en couleur. Une app installée
  depuis le Kazo OS Store ne montre pourtant pas son icône dans le menu
  Démarrer.
- Une planche de trois styles (néon, moderne en dégradé, illustré) a été
  montrée à Benoit sur quatre apps.

## Décision

### 1. Style « illustré » (Q1 : C)

Chaque app système fenêtrée reçoit un nouveau `icons/app.svg` :

- tuile `32×32`, coins `rx="7"`, dégradé sombre commun en diagonale
  (`#26303a` vers `#0a0e12`), le même que les apps tierces ;
- un dessin **en couleur** propre à l'app, qui montre ce qu'elle fait (pas un
  glyphe de trait), avec deux ou trois teintes et un léger dégradé ;
- lisible à 16 et 20 px : formes pleines, pas de détail sous 1 unité, contraste
  suffisant sur la tuile sombre ;
- SVG autonome : pas de police, pas d'image externe, pas de script ; les
  identifiants de dégradés restent locaux au fichier (l'icône est chargée par
  `<img>`).

| App | Dessin |
| --- | --- |
| Navigateur | globe bleu avec continents et anneau d'orbite |
| Calculatrice | calculatrice avec écran vert et touches de couleur |
| Éditeur de code | fenêtre de code avec chevrons `</>` en couleur |
| Diagnostic | puce électronique sous une loupe |
| Journal d'erreurs | page de journal avec triangle d'alerte rouge |
| Explorateur de fichiers | dossier jaune en deux tons avec une feuille |
| Visionneuse d'icônes | grille de quatre formes de couleur |
| Kazo Paint | palette de peintre avec pinceau |
| Paramètres | engrenage métal et petit engrenage orange |
| Kazo OS Store | sac de boutique turquoise avec étoile |
| Moniteur système | écran avec courbe d'activité verte |
| Terminal | console avec boutons de fenêtre et invite verte |
| Éditeur de texte | page avec lignes de texte et crayon |
| Éditeur de thème | éventail de nuanciers de couleur |
| Gestionnaire de serveur web | baie de serveurs avec voyants et globe |

### 2. L'icône de l'app partout (Q2 : A)

Le menu Démarrer, les raccourcis d'apps (bureau, dossiers, dialogues) et
l'accueil et le tiroir mobiles dessinent la même icône que la fenêtre, dans cet
ordre :

1. l'image du pack d'icônes pour l'app (`apps/<id>`) ;
2. sinon l'image déclarée par l'app (première de `presentation.iconAssetPaths`) ;
3. sinon l'image du pack pour sa catégorie (`categories/<catégorie>`) ;
4. sinon l'icône Lucide de sa catégorie (`KazoAppIcons.LucideName`).

C'est déjà la règle de la fenêtre, de la taskbar et de l'accueil mobile (ADR
0203 §4, troisième ligne), étendue aux autres surfaces. Un pack qui dessine une
app la remplace toujours ; un pack qui ne dessine que des catégories ne cache
plus l'icône propre d'une app.

### 3. Une URL d'icône dans le SDK

L'explorateur et le bureau n'utilisent que le SDK public. `KazoAppInfo` gagne
la propriété `IconUrl` (`string?`, `init`) : l'URL relative de l'icône déclarée
(`_content/<assembly>/<chemin>`, segments échappés), ou `null`. Le calcul est
celui de `StaticWebAssetAppAssetSource.RelativeUrl` (ADR 0164), déplacé dans le
SDK (`KazoAppAssets.RelativeUrl`) pour qu'il n'y ait qu'une formule. Une app
installée sert déjà ses fichiers à cette adresse (ADR 0206, 0221). L'ajout est
compatible : `Icon` (le chemin relatif au paquet) ne change pas.

### 4. Portée (Q3 : A)

Seules les apps système de ce dépôt changent de dessin. Les apps des dépôts
`Os-App-*` gardent leur icône ; elles profitent du point 2 sans changement de
leur côté. Les commandes et les services, qui n'ont pas d'icône, gardent l'icône
Lucide de leur catégorie.

### 5. Licence (Q4 : A)

Les dessins sont faits pour le projet, sans bibliothèque d'icônes tierce, et
suivent la licence du dépôt. Chaque fichier commence par un commentaire qui le
dit. Les glyphes Lucide (ISC) ne sont plus utilisés dans les icônes d'apps ;
ils restent ceux de l'interface et du repli du point 2.

## Questions posées à Benoit

Posées le 2026-11-06 avec une planche de comparaison ; réponse le 2026-11-07 :
« Je suis d'accord avec tes propositions ».

1. **Style** : A néon hacker ; B moderne en dégradé ; **C illustré**
   (recommandé, cohérent avec les apps tierces). Choix : C.
2. **Icône propre partout** : **A oui, partout, les packs gardent la priorité**
   (recommandé) ; B bureau et explorateur seulement ; C non. Choix : A.
3. **Apps concernées** : **A apps système seulement** (recommandé) ; B aussi
   une retouche de Webamp ; C aussi Chess. Choix : A.
4. **Licence** : **A dessins faits pour le projet** (recommandé) ; B jeu
   d'icônes MIT ou ISC existant. Choix : A.

## Conséquences

- Les apps se reconnaissent d'un coup d'œil dans le menu Démarrer et sur le
  bureau, et une app tierce y montre enfin son icône.
- Le menu Démarrer charge une petite image SVG par app au lieu d'un glyphe
  embarqué ; les fichiers font moins de 3 Ko et restent en cache du navigateur.
- Une app qui ne déclare pas d'icône garde l'icône Lucide de sa catégorie.

## Vérification

- Tests unitaires : `KazoAppAssets.RelativeUrl`, `KazoAppInfo.IconUrl`, ordre
  pack d'app, image déclarée, pack de catégorie, Lucide.
- Génération des manifestes : les quinze `icons/app.svg` restent des assets
  déclarés avec leur empreinte.
- Planche de rendu des icônes à 64, 32, 20 et 16 px jointe à la PR.

## Tâches

- [x] ADR (série `icons/base`, chantier `icons/1-adr`).
- [ ] Redessiner les `icons/app.svg` des apps système (chantier `icons/2-system-apps`).
- [ ] `KazoAppAssets.RelativeUrl` et `KazoAppInfo.IconUrl` dans le SDK.
- [ ] Menu Démarrer, accueil mobile et raccourcis : ordre du point 2.
- [ ] Tests, docs (`window-model.md`, `desktop-api.md`, `sdk-reference.md`),
      `features.md`, entrée de changement.
