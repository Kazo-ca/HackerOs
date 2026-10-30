> [English version](../../../../en/wasm/dev/adr/0205-cursor-themes.md)

# ADR 0205 : thèmes de curseurs

## Statut

Accepté le 2026-10-30 (série `todo/base`, chantier `todo/9-cursor-themes`,
issue [#257](https://github.com/Kazo-ca/HackerOs/issues/257)). C'est le
« nouveau système de l'OS, avec sa propre ADR » que demande le §4 de
l'[ADR 0190](0190-theme-packs.md) avant que le Hack Store accepte le dossier
`cursors/` d'un pack.

- **Complète :** [ADR 0190](0190-theme-packs.md) (packs de thèmes, règles
  « données seulement ») ; [ADR 0103](0103-hack-paint-image-files-and-theme-editor.md)
  (thèmes en fichiers dans `/etc` et `~`, pas de masquage) ;
  [ADR 0044](0044-settings-as-files.md) (réglages en fichiers à couches).
- **Questions posées à Benoit** (fin du document) : le thème d'une nouvelle
  installation et la portée du thème dans les apps. Le travail suit les
  recommandations en attendant ses réponses.

## Contexte

- Le pointeur de l'OS est celui du navigateur. Chaque surface demande un
  curseur par son nom CSS (`cursor: pointer`, `cursor: ew-resize`…) dans ses
  fichiers `.razor.css`, comme une app d'un vrai OS demande un curseur par son
  nom (`left_ptr`, `hand2`…) et laisse le thème de curseurs choisir l'image.
- Rien ne permettait de choisir des curseurs ; l'issue #257 demande des
  curseurs PNG avec leur point actif, appliqués à la surface de l'OS, choisis
  dans Paramètres ▸ Apparence.

## Décision

### 1. Un thème de curseurs est un dossier

Comme un thème de curseurs X11 (`~/.icons/<thème>/cursors/`), un thème est un
**dossier** qui contient un index `cursor.theme` et ses images PNG :

```ini
[cursor-theme]
id = neon-crypt
name = Neon Crypt

[cursors]
default = arrow.png 2 1
pointer = hand.png 11 1
text = ibeam.png 16 16
```

- Une ligne de `[cursors]` donne un **nom de curseur** (les mots-clés CSS de
  `cursor`, liste dans `CursorNames.All`), le **fichier PNG** du dossier et le
  **point actif** (colonne et ligne du pixel qui pointe).
- Un nom absent suit un **repli**, comme les liens symboliques d'un thème X11 :
  `n-resize`, `s-resize` et `row-resize` prennent `ns-resize`, `col-resize`
  prend `ew-resize`, `grabbing` prend `grab`, `progress` prend `wait`,
  `all-scroll` prend `move`, etc. Sans image ni repli (par exemple `copy`), le
  curseur de l'appareil reste : une flèche ferait perdre le sens.
- Lecture tolérante (ADR 0044 §5) : une ligne mauvaise ou une image refusée est
  ignorée avec un avertissement ; seule une identité absente ou invalide
  (`id`, `name`) refuse le thème.
- Règles « données seulement » (ADR 0190 §3) : PNG seulement, vérifié par sa
  signature et son en-tête `IHDR` sans être décodé ; au plus **64 Kio** et
  **128 × 128 pixels** (les navigateurs ignorent un curseur plus grand) ; point
  actif dans l'image ; nom de fichier simple du dossier (ni chemin, ni `..`, ni
  adresse distante, ni SVG). Un PNG lu dans les fichiers devient une URL
  `data:image/png;base64,`.
- Emplacements : `/etc/hackeros/cursors/<dossier>/` (administrateur, pour
  tous) et `~/.config/hackeros/cursors/<dossier>/` (l'utilisateur). Un `id` qui
  reprend un thème intégré ou un thème de `/etc` est refusé (pas de masquage,
  comme l'ADR 0103).

### 2. Thèmes intégrés

- **Système** (`system`) : aucune image, les curseurs de l'appareil. C'est le
  défaut (question 1).
- **HackerOS Néon** (`hackeros-neon`) et **Gothique Pourpre**
  (`gothic-crimson`) : 15 curseurs PNG de 32 × 32 chacun (flèche, main, I,
  sablier, flèche occupée, aide, croix, déplacer, interdit, main ouverte et
  fermée, quatre doubles flèches), livrés par `HackerOs.Theming.Blazor`
  (`wwwroot/cursors/<id>/`) et dessinés par
  `build/tools/generate-cursor-themes.py`. Leurs points actifs sont déclarés
  dans `CursorThemeCatalog`, un test vérifie chaque fichier.

### 3. Le choix est un réglage en fichier

Nouveau document à couches **`cursors.config`**, clé `theme` (chaîne ouverte,
défaut `system`) : `/etc/hackeros/cursors.config` est le choix de la machine
(aussi utilisé par l'écran de connexion) et `~/.config/hackeros/cursors.config`
celui de l'utilisateur, comme `XCURSOR_THEME`. Un `id` qui ne nomme aucun thème
(dossier supprimé) donne les curseurs de l'appareil. Le document est ouvert aux
apps par `HackSettings.OpenSystemAsync` (registre de l'ADR 0056 §8) et livré
commenté dans une nouvelle installation.

### 4. Application à toute la surface

- `CursorThemeService` (Platform Core) lit `cursors.config`, résout l'`id`
  contre les thèmes intégrés et les dossiers (lus avec les droits du système
  pour `/etc` et de l'utilisateur pour `~`), et relit quand le fichier ou la
  session change.
- `CursorThemeSurface` (`HackerOs.Theming.Blazor`), rendu une fois par
  `App.razor`, passe les images au module `cursorTheme.js` de la bibliothèque,
  qui :
  1. réécrit chaque règle CSS de même origine dont `cursor` est un nom du
     thème (`cursor: pointer` devient `cursor: url(…) 11 1, pointer`), y compris dans les feuilles importées (`@import`), `@media` et `@layer`, en gardant `!important`, et la remet
     comme avant quand le thème change ;
  2. ajoute un petit élément `<style>` pour ce qu'aucune règle ne nomme : la
     flèche du document, les liens et les champs de texte ;
  3. surveille les feuilles ajoutées plus tard (apps chargées à la demande).
- Les apps n'ont rien à changer (question 2) : elles demandent un nom de
  curseur, l'OS fournit l'image. Restent avec les curseurs de l'appareil : les
  feuilles d'une autre origine, les attributs `style` en ligne et les
  `<iframe>` (sites du navigateur intégré).
- Le nom CSS reste en repli dans chaque valeur : si une image ne se charge
  pas, le navigateur montre le curseur de l'appareil.
- Sur mobile, il n'y a pas de pointeur : le réglage ne change rien.

### 5. Paramètres ▸ Apparence ▸ Curseurs

Une rangée de choix (`role="radiogroup"`) montre chaque thème avec une bande
d'aperçu (flèche, main, I, sablier, déplacer, redimensionner) sur un fond en
damier ; les thèmes des dossiers portent leur origine (`/etc` ou `~`) et les
avertissements de lecture sont listés sous les choix. Le choix est écrit dans
la couche utilisateur ; un verrou de l'administrateur (`[locked]` dans
`/etc/hackeros/cursors.config`) désactive la rangée. Le panneau lit les
dossiers avec le SDK public (`HackDirectory`, `HackFile`), comme le reste de
Paramètres (ADR 0055).

### 6. Packs du Hack Store

Le dossier `cursors/` d'un pack `.hacktheme` (ADR 0190 §1) aura la forme du §1 :
un dossier de thème avec son `cursor.theme`. Son installation (chantier #253)
copie le dossier dans `/etc/hackeros/cursors/` ou `~/.config/hackeros/cursors/`
et reprend les vérifications de `CursorThemeFile`.

## Conséquences

- Nouveaux types publics dans `HackerOs.Theming.Abstractions` (`CursorNames`,
  `CursorImage`, `CursorTheme`, `CursorThemeCatalog`, `CursorThemeFile`,
  `CursorThemeLibrary`) et `HackerOs.Theming.Blazor` (`CursorThemeSurface`),
  dans `PublicAPI.Unshipped.txt` pour la prochaine version mineure.
- Un thème copié dans un dossier apparaît dans Paramètres à la prochaine
  ouverture de la page et s'applique quand on le choisit ; modifier les images
  du thème en cours demande de le choisir à nouveau ou de rouvrir la session.
- Le texte ordinaire (paragraphes) montre la flèche du thème au lieu de l'I de
  l'appareil, puisque la flèche est posée sur le document ; les champs de
  saisie gardent l'I du thème.

## Questions posées à Benoit

1. **Thème d'une nouvelle installation** : A) Système (recommandé, garde la
   taille et l'accessibilité réglées sur l'appareil) ; B) HackerOS Néon ;
   C) Gothique Pourpre.
2. **Portée** : A) partout, apps comprises (recommandé, comme un vrai OS, aucune
   app à modifier) ; B) seulement l'interface de l'OS (bureau, barre des
   tâches, cadres des fenêtres), en marquant chaque surface.

## Tâches

- [x] Format `cursor.theme`, vérification des PNG, bibliothèque `/etc` et `~`.
- [x] Deux thèmes intégrés dessinés et leur script.
- [x] `cursors.config`, `CursorThemeService`, application par
      `CursorThemeSurface` et `cursorTheme.js`.
- [x] Paramètres ▸ Apparence ▸ Curseurs.
- [x] Tests : format, service, panneau, et test de bout en bout dans Chromium.
- [x] Docs (ce document, [`cursor-themes.md`](../../framework/cursor-themes.md)),
      `features.md`, entrée de la liste des changements.
