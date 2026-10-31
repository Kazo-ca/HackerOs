> [English version](../../../en/wasm/framework/cursor-themes.md)

# Thèmes de curseurs

Décision : [ADR 0205](../dev/adr/0205-cursor-themes.md).

## À quoi ça sert

Un thème de curseurs remplace le pointeur de toute la surface de HackerOS
(bureau, barre des tâches, fenêtres et apps) par des images PNG. On le choisit
dans **Paramètres ▸ Apparence ▸ Curseurs**. Trois thèmes sont intégrés :
**Système** (les curseurs de l'appareil, par défaut), **HackerOS Néon** et
**Gothique Pourpre** ; chacun peut ajouter les siens.

## Créer un thème

1. Créez un dossier dans `~/.config/hackeros/cursors/` (pour vous) ou, en
   administrateur, dans `/etc/hackeros/cursors/` (pour tous) :

   ```sh
   mkdir -p ~/.config/hackeros/cursors/neon-crypt
   ```

2. Copiez-y vos images PNG (au plus 128 × 128 pixels et 64 Kio chacune ;
   32 × 32 est la taille habituelle).
3. Écrivez l'index `cursor.theme` du dossier :

   ```ini
   [cursor-theme]
   id = neon-crypt
   name = Neon Crypt

   [cursors]
   # nom = fichier.png  colonne  ligne du point actif
   default = arrow.png 2 1
   pointer = hand.png 11 1
   text = ibeam.png 16 16
   wait = hourglass.png 16 16
   ns-resize = resize-ns.png 16 16
   ew-resize = resize-ew.png 16 16
   ```

4. Ouvrez Paramètres ▸ Apparence : le thème apparaît sous **Curseurs** ;
   choisissez-le. Le choix est écrit dans `~/.config/hackeros/cursors.config`
   (`theme=neon-crypt`) et s'applique tout de suite.

Règles :

- `id` : 1 à 64 lettres minuscules, chiffres et tirets, différent des thèmes
  intégrés et des thèmes de `/etc`.
- Le **point actif** est le pixel qui pointe, compté depuis le coin en haut à
  gauche de l'image ; il doit être dans l'image.
- Un fichier est un simple nom du dossier, en `.png` : ni chemin, ni `..`, ni
  adresse web, ni SVG.
- Une ligne fausse ou une image refusée est ignorée et listée sous les choix de
  Paramètres ; le reste du thème s'applique.

## Noms de curseurs

Les noms sont les mots-clés CSS de `cursor`. Un nom absent du thème prend son
repli ; sans repli, le curseur de l'appareil reste.

| Nom | Repli |
| --- | --- |
| `default`, `pointer`, `text`, `wait`, `help`, `crosshair`, `move`, `not-allowed`, `grab`, `ns-resize`, `ew-resize`, `nesw-resize`, `nwse-resize` | aucun |
| `progress` | `wait` |
| `grabbing` | `grab` |
| `n-resize`, `s-resize`, `row-resize` | `ns-resize` |
| `e-resize`, `w-resize`, `col-resize` | `ew-resize` |
| `ne-resize`, `sw-resize` | `nesw-resize` |
| `nw-resize`, `se-resize` | `nwse-resize` |
| `all-scroll` | `move` |
| `no-drop` | `not-allowed` |
| `vertical-text` | `text` |
| `cell` | `crosshair` |
| `copy`, `alias`, `context-menu`, `zoom-in`, `zoom-out` | aucun |

## Pour les développeurs d'apps

Rien à faire : écrivez `cursor: pointer`, `cursor: ew-resize`… dans le
`.razor.css` de vos composants ; l'OS remplace l'image selon le thème choisi.
Ne posez pas de curseur par un attribut `style` ni par JavaScript
(`element.style.cursor`) : ils gardent le curseur de l'appareil.

## API

Paquet `HackerOs.Theming.Abstractions` :

- `CursorNames` : la liste des noms (`All`), l'aperçu de Paramètres
  (`Preview`) et les replis (`GetFallback`).
- `CursorThemeFile` : `Parse` (index `cursor.theme`), `TryValidateImage`
  (vérifie un PNG sans le décoder), `ToDataUrl`, limites `MaxImageBytes` et
  `MaxImageSize`.
- `CursorThemeLibrary.LoadAsync` : lit les dossiers de `/etc` et `~` avec vos
  propres fonctions de lecture (`HackDirectory`/`HackFile` dans une app).
- `CursorThemeCatalog` : les thèmes intégrés ; `CursorTheme.Resolve` et
  `ResolveAll` appliquent les replis.

Paquet `HackerOs.Theming.Blazor` : `CursorThemeSurface`, composant sans rendu
qu'un hôte place une fois dans sa racine (`Theme="…"`).

Réglage : document `cursors` (`HackSettings.OpenSystemAsync(context, "cursors")`),
clé `theme`.

## Thèmes intégrés

Les images de HackerOS Néon et Gothique Pourpre sont dans
`wasm2/HackerOs/Platform/HackerOs.Theming.Blazor/wwwroot/cursors/<id>/` et sont
dessinées par `wasm2/HackerOs/build/tools/generate-cursor-themes.py` (Pillow).
Changer un dessin ou un point actif demande de modifier ensemble le script et
`CursorThemeCatalog` ; le test `Every_built_in_cursor_is_a_shipped_png_whose_hot_spot_is_inside`
vérifie qu'ils s'accordent.

## Limites

- Les feuilles de style d'une autre origine, les attributs `style` en ligne et
  les `<iframe>` gardent les curseurs de l'appareil.
- Un thème copié dans un dossier apparaît à la prochaine ouverture de la page
  de Paramètres ; modifier les images du thème en cours demande de le choisir
  à nouveau ou de rouvrir la session.
- Le texte ordinaire montre la flèche du thème (et non l'I) ; les champs de
  saisie montrent l'I du thème.
