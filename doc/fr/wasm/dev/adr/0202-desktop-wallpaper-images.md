> [English version](../../../../en/wasm/dev/adr/0202-desktop-wallpaper-images.md)

# ADR 0202 : fonds d'écran en image

## Statut

Accepté le 2026-10-30. Chantier 9 du plan du Hack Store, système de l'OS
demandé par l'[ADR 0190](0190-theme-packs.md) §4 avant que le Store accepte des
fonds d'écran dans un pack. Issue [#254](https://github.com/Kazo-ca/HackerOs/issues/254),
série `todo/base` (branche `todo/6-wallpaper-images`).

- **Complète :** [ADR 0044](0044-settings-as-files.md) §8 (`appearance.config`
  en couches), [ADR 0103](0103-hack-paint-image-files-and-theme-editor.md)
  (thèmes « données seulement »), [ADR 0178](0178-extended-screens-in-display-surfaces.md)
  (bureau de chaque écran), [ADR 0190](0190-theme-packs.md) §1 à §3 (dossier
  `wallpapers/` des packs et règles « données seulement »).
- **Ne change pas :** les thèmes et leur jeton `--hos-desktop-background`, qui
  restent le fond quand aucune image n'est choisie.

## Contexte

- Le fond du bureau vient seulement du thème : le jeton
  `--hos-desktop-background` (couleur ou dégradé) et une grille décorative.
- L'ADR 0190 prévoit des packs avec un dossier `wallpapers/*.png|*.jpg|*.webp`,
  sans SVG, avec une taille maximale par image.
- Le bureau étendu (ADR 0178) donne à chaque écran son propre `DesktopArea`.

## Décision

### 1. Les réglages, dans `appearance.config`

Trois clés de plus dans le document `appearance` (utilisateur par-dessus
système, verrouillables comme les autres) :

| Clé | Valeur | Défaut |
| --- | --- | --- |
| `wallpaper` | vide (fond du thème), `builtin:<id>` ou chemin absolu d'une image | vide |
| `wallpaperFit` | `fill`, `fit`, `stretch`, `center` ou `tile` | `fill` |
| `wallpaperScreen2` à `wallpaperScreen9` | même forme que `wallpaper` ; vide = comme l'écran 1 | vide |

L'écran 1 et tous les écrans sans valeur propre utilisent `wallpaper`. La
limite de 9 écrans est celle de `screens.config` (ADR 0180).

### 2. D'où viennent les images

- **Livrées avec l'OS** : 4 images WebP (`hacker-grid`, `circuit`, `aurora`,
  `nebula`) dans `HackerOs.Theming.Blazor/wwwroot/wallpapers/`, servies comme
  fichiers statiques (`_content/HackerOs.Theming.Blazor/wallpapers/`). Elles ne
  prennent aucune place dans le stockage de l'utilisateur, comme les thèmes
  intégrés vivent dans le code.
- **Système** : `/usr/share/hackeros/wallpapers/`, déposées par un
  administrateur, proposées à tous.
- **Utilisateur** : `~/.local/share/hackeros/wallpapers/`.
- **Packs installés** : `wallpapers/` de chaque pack de
  `/usr/share/hackeros/theme-packs/` et `~/.local/share/hackeros/theme-packs/`
  (ADR 0190 §2). Le dossier est lu dès qu'il existe ; l'installation des packs
  reste l'issue #253.
- **N'importe quel fichier** choisi avec « Parcourir… » : son chemin est gardé
  tel quel (Q2). S'il est déplacé ou supprimé, le bureau revient au fond du
  thème.

### 3. Règles « données seulement »

- PNG, JPEG ou WebP seulement. Le type est lu dans les **premiers octets** du
  fichier, jamais dans son nom : un `.png` qui contient un SVG est refusé.
- 16 Mo au plus par image (`WallpaperCatalog.MaxImageBytes`). La limite de 5 Mo
  par fond d'un pack reste celle de la CI du catalogue (ADR 0190 §3).
- Aucune adresse distante : une valeur est vide, `builtin:<id>` ou un chemin du
  système de fichiers virtuel.
- `wallpaperFit` : `fill` couvre l'écran (rognage), `fit` montre toute l'image
  (le fond du thème autour), `stretch` déforme, `center` garde la taille réelle
  au centre, `tile` répète l'image.

### 4. Affichage par le shell

- `WallpaperService` (Platform Core) suit `appearance.config` comme
  `ThemePreferenceService`, et relit tout quand la session change.
- Il lit un fichier **en tant qu'utilisateur de la session** : les droits du
  fichier décident, on ne peut pas afficher un fichier qu'on ne peut pas ouvrir.
  Un fichier absent, illisible, trop gros ou qui n'est pas une image laisse le
  fond du thème, avec un avertissement dans le journal (une fois par fichier).
- `DesktopWallpaper` (Platform Blazor) dessine l'image d'un écran dans le
  nouveau paramètre `DesktopArea.WallpaperContent`, par-dessus le fond et la
  grille du thème, sous les icônes et les fenêtres. Les octets d'un fichier
  deviennent une adresse `blob:` (`DesktopWallpaper.razor.js`), libérée quand
  l'image change. L'écran 1 et chaque écran secondaire ont le leur.

### 5. Paramètres ▸ Apparence ▸ Fond d'écran

- Une grille de vignettes : « Fond du thème », les images livrées, puis les
  images des dossiers système, utilisateur et des packs, et les fichiers déjà
  choisis ailleurs (60 au plus).
- « Parcourir… » ouvre le dialogue d'ouverture de fichier (PNG, JPEG, WebP).
- Une liste « Ajustement » écrit `wallpaperFit`.
- Avec plusieurs écrans, un choix « Écran 1 et les autres » / « Écran N » ;
  pour un écran secondaire, « Comme l'écran 1 » efface sa valeur propre.
- Le choix « Pour moi / Par défaut pour tous » de la page s'applique aussi au
  fond d'écran (la couche système demande un administrateur, ADR 0042).
- Le panneau n'utilise que le SDK public (`HackSettings`, `HackDirectory`,
  `HackFile`, `HackScreens`), comme le reste de Paramètres.

### 6. Hors de ce chantier

- **Mobile** (Q1) : l'écran d'accueil mobile garde le fond de son thème.
  Issue `TODO` [#297](https://github.com/Kazo-ca/HackerOs/issues/297).
- **« Définir comme fond d'écran »** dans le menu contextuel des images de
  l'explorateur (Q3) : issue `TODO` [#298](https://github.com/Kazo-ca/HackerOs/issues/298).

## Conséquences

- Le Store peut accepter la partie `wallpapers/` d'un pack dès que les packs
  s'installent (#253) : les dossiers sont déjà lus.
- `HackerOs.Theming.Abstractions` gagne `WallpaperCatalog`, `WallpaperFit` et
  `BuiltInWallpaper` ; `HackerOs.Windowing.Blazor` gagne
  `DesktopArea.WallpaperContent`. Aucun changement du SDK des apps.
- Une image de 16 Mo reste en mémoire tant qu'un écran l'affiche (une copie par
  fichier, partagée entre les écrans).

## Questions posées à Benoit

Posées le 2026-10-30 dans le fil « Ajouter des fonds d'écran en image », avec
la recommandation appliquée en attendant sa réponse :

- **Q1** Mobile : A (recommandé) bureau seulement, issue `TODO` pour le mobile ;
  B aussi l'écran d'accueil mobile.
- **Q2** Image choisie hors bibliothèque : A (recommandé) garder son chemin ;
  B la copier dans `~/.local/share/hackeros/wallpapers/`.
- **Q3** « Définir comme fond d'écran » dans l'explorateur : A dans ce
  chantier ; B (recommandé) issue `TODO` à part.
- **Q4** Fonds livrés avec l'OS : A (recommandé) 4 images WebP ; B aucune.

## Tâches

- [x] `WallpaperCatalog`, `WallpaperFit`, `BuiltInWallpaper` (Theming Abstractions).
- [x] Clés `wallpaper`, `wallpaperFit`, `wallpaperScreen2..9` dans `appearance.config`.
- [x] `WallpaperService` : lecture en tant qu'utilisateur, type lu dans les octets, limite de taille.
- [x] `DesktopWallpaper` et `DesktopArea.WallpaperContent`, pour chaque écran.
- [x] Panneau Fond d'écran dans Paramètres ▸ Apparence.
- [x] 4 fonds livrés (WebP).
- [x] Tests : catalogue, schéma, service, panneau.
- [ ] Mobile (#297).
- [ ] « Définir comme fond d'écran » dans l'explorateur (#298).
