> [English version](../../../../en/wasm/dev/adr/0217-mobile-home-wallpaper.md)

# ADR 0217 : fond d'écran en image sur l'écran d'accueil mobile

## Statut

Accepté le 2026-10-31 ; Benoit a choisi les recommandations (voir « Questions
posées à Benoit »). Issue
[#297](https://github.com/Kazo-ca/HackerOs/issues/297), série `todo/base`
(branche `todo/23-mobile-wallpaper`).

- **Complète :** [ADR 0202](0202-desktop-wallpaper-images.md) (fonds d'écran en
  image du bureau), dont le §6 laissait le mobile hors du chantier.
- **Ne change pas :** les règles « données seulement » de l'ADR 0202 §3 (PNG,
  JPEG, WebP lus dans les octets, 16 Mo, aucune adresse distante), les dossiers
  d'images de l'ADR 0202 §2 et les clés du bureau.

## Contexte

- L'ADR 0202 dessine une image derrière les icônes de chaque écran du bureau.
  L'interface mobile (`MobileShell`) garde le fond de son thème, le jeton
  `--hos-mobile-surface-background`.
- `appearance.config` garde déjà deux thèmes, `desktopThemeId` et
  `mobileThemeId`, pour que changer de forme d'appareil ne perde aucun des deux
  choix.
- Les thèmes mobiles (Android, iOS…) font de leur fond une partie de leur style
  (dégradés lumineux d'iOS, dégradé sombre d'Android).
- Un téléphone est en portrait : une image paysage choisie pour le bureau y est
  fortement rognée.

## Décision

### 1. Deux clés de plus dans `appearance.config` (Q1, Q2)

| Clé | Valeur | Défaut |
| --- | --- | --- |
| `mobileWallpaper` | vide (fond du thème mobile), `builtin:<id>` ou chemin absolu d'une image | vide |
| `mobileWallpaperFit` | `fill`, `fit`, `stretch`, `center` ou `tile` | `fill` |

Les valeurs ont la même forme et les mêmes règles que `wallpaper` et
`wallpaperFit` (ADR 0202 §1 et §3), dans les mêmes couches (utilisateur
par-dessus système, verrouillables). Vide garde le fond du **thème mobile** :
le choix du bureau ne s'impose pas au téléphone, comme les thèmes.

### 2. Affichage par le shell mobile

- `WallpaperPreferences` lit aussi les deux clés ; `WallpaperService` gagne
  `GetMobileImageAsync`, qui suit les mêmes règles que `GetImageAsync` : lecture
  **en tant qu'utilisateur de la session**, type lu dans les octets, limite de
  taille, retour au fond du thème avec un avertissement dans le journal. Un
  fichier choisi à la fois pour le bureau et le mobile n'est lu qu'une fois.
- `DesktopWallpaper` gagne le paramètre `Mobile` : il montre alors l'image de
  `mobileWallpaper`. `MobileShell` le place au fond de son espace de travail,
  sous l'écran d'accueil, le tiroir et les apps.
- Quand une image est affichée, l'écran d'accueil rend son fond transparent et
  ajoute une ombre aux noms des apps et au titre, pour qu'ils restent lisibles
  sur n'importe quelle image. Le dock garde son verre flouté.
- Les apps mobiles occupent tout l'écran (`SingleSurfaceArea`) : l'image n'y
  change rien, elle se voit sur l'écran d'accueil.

### 3. Le tiroir d'apps reste opaque (Q3)

Le tiroir garde le fond `--hos-bg-base` de son thème : l'image se voit sur
l'écran d'accueil et dans la bande au-dessus du tiroir, et la liste reste
lisible sur une image claire.

### 4. Les thèmes mobiles

Aucun thème ne change : leur fond reste celui qu'on voit tant que
`mobileWallpaper` est vide. Une image choisie le remplace, sur tous les thèmes
mobiles, y compris le contraste élevé, comme au bureau (ADR 0202 §4).

### 5. Paramètres ▸ Apparence ▸ Fond d'écran

- Le choix de l'écran devient « Écran 1 et les autres », « Écran N »… puis
  **« Mobile »**, montré toujours (même avec un seul écran).
- Avec « Mobile », la grille écrit `mobileWallpaper` (la première vignette est
  « Fond du thème mobile ») et la liste « Ajustement » écrit
  `mobileWallpaperFit`.
- Le panneau n'utilise toujours que le SDK public.

## Conséquences

- Le bureau et le mobile ont chacun leur image et leur ajustement ; une image
  paysage du bureau ne rogne pas l'écran du téléphone sans qu'on le demande.
- Le menu « Définir comme fond d'écran » de l'explorateur (#298, ADR 0218)
  pourra écrire `mobileWallpaper` quand l'interface mobile est active.
- Aucun changement du SDK des apps.

## Questions posées à Benoit

Posées le 2026-10-31 dans le fil « Afficher le fond d'écran sur mobile », avec
la recommandation appliquée en attendant sa réponse :

- **Q1** Réglage : A (recommandé) clé `mobileWallpaper`, vide = fond du thème
  mobile ; B clé `mobileWallpaper`, vide = image du bureau ; C même clé
  `wallpaper` que l'écran 1.
- **Q2** Ajustement : A (recommandé) clé `mobileWallpaperFit` ; B même
  `wallpaperFit` que le bureau.
- **Q3** Tiroir d'apps : A (recommandé) opaque comme aujourd'hui ; B verre
  flouté qui laisse voir l'image.

**Réponse (2026-10-31)** : Benoit choisit les trois recommandations (Q1 A,
Q2 A, Q3 A). Ce qui est construit ne change pas.

## Tâches

- [x] ADR (fr + en) et index.
- [x] Clés `mobileWallpaper` et `mobileWallpaperFit` dans `appearance.config`.
- [x] `WallpaperPreferences` et `WallpaperService.GetMobileImageAsync`.
- [x] `DesktopWallpaper Mobile="true"` dans `MobileShell`, accueil transparent et lisible.
- [x] Choix « Mobile » dans Paramètres ▸ Apparence ▸ Fond d'écran.
- [x] Tests : schéma, service, panneau.
