> [English version](../../../../en/wasm/dev/adr/0183-drag-a-window-between-screens.md)

# ADR 0183 : glisser une fenêtre d'un écran à l'autre

## Statut

Accepté le 2026-10-28, chantier `multi-monitor/8-drag-between-screens` du
[plan multi-moniteur](../plans/multi-monitor-plan.md). Complète
l'[ADR 0178 §7](0178-extended-screens-in-display-surfaces.md), qui laissait le glisser à
la souris à un chantier à part (décision de Benoit : menu et raccourci d'abord). S'appuie
sur l'[ADR 0163](0163-window-pointer-preview-and-invariant-coordinates.md) (aperçu du
déplacement) et sur le portail des fenêtres ([`window-portal.md`](../window-portal.md)).
Mise en œuvre : [`drag-between-screens.md`](../drag-between-screens.md).

## Contexte

- Une fenêtre se déplace par sa barre de titre. `WindowChrome.razor.js` capture le
  pointeur (`setPointerCapture`), dessine un aperçu par une translation CSS et envoie
  une seule `MoveWindowCommand` au relâchement (ADR 0163). La position est bornée à la
  zone de travail de l'écran de la fenêtre.
- Chaque écran secondaire est une autre fenêtre du navigateur (une surface). Pendant
  la capture, le navigateur envoie tous les événements du pointeur à la fenêtre où le
  geste a commencé, même quand le pointeur passe au-dessus d'une autre fenêtre : la
  fenêtre cible ne reçoit rien.
- Chaque événement du pointeur donne deux positions : dans la page (`clientX`/`clientY`)
  et sur le bureau physique (`screenX`/`screenY`). Aucune API ne donne directement la
  position de la page d'une autre fenêtre sur le bureau.
- Les écrans sont déjà marqués dans le DOM : la couche de fenêtres de chaque
  `DesktopArea` porte `data-display` (chantier 2), et le registre des surfaces
  (`watch`) annonce chaque document d'écran (chantier 4).

## Décision

1. **Écran sous le pointeur.** Pendant un déplacement, `WindowChrome.razor.js` cherche
   l'écran sous le pointeur à chaque mouvement :
   - si le pointeur est dans la fenêtre du navigateur où le geste a commencé, ce sont
     les couches de cette fenêtre qui décident (position dans la page) ;
   - sinon, les autres fenêtres d'écran (onglet principal et surfaces), par la
     position sur le bureau physique. La page de chaque fenêtre sait où elle commence
     sur le bureau grâce au dernier événement du pointeur qu'elle a vu
     (`screenX - clientX`), corrigé si la fenêtre a bougé depuis ; avant tout
     événement, par sa position et ses bordures (`screenX`, `outerWidth - innerWidth`).
   - Entre deux écrans (aucune couche sous le pointeur), il n'y a pas de cible.
2. **Changement d'écran au relâchement seulement.** L'élément de la fenêtre ne change
   pas de document pendant le geste : le déplacer ferait perdre la capture du pointeur.
   Pendant le geste, la fenêtre suit le pointeur dans son écran (aperçu de l'ADR 0163),
   et l'écran cible montre un **contour** de la taille de la fenêtre là où elle va
   arriver (seulement si l'écran cible est une autre fenêtre du navigateur).
3. **Dépôt.** Relâchée au-dessus d'un autre écran, la fenêtre arrive à la position du
   pointeur moins le point où la barre de titre a été saisie, le haut ramené à 0 au
   moins. `WindowChrome.ReportScreenDropAsync(écran, x, y)` (public,
   `PublicAPI.Unshipped.txt`) envoie `MoveWindowToDisplayCommand` puis
   `MoveWindowCommand` : le portail déplace la fenêtre sans la recréer, et la position
   est bornée à la zone de l'écran cible. Une fenêtre modale suit toujours son
   propriétaire (ADR 0178 §2).
4. **Sans cible**, ou au-dessus de son propre écran, le déplacement reste celui
   d'aujourd'hui. `DesktopShell` ignore un dépôt sur un écran fermé pendant le geste.
5. **Périmètre.** Seule une fenêtre normale et déplaçable se glisse (comme
   aujourd'hui). Le glisser d'une fenêtre agrandie ou ancrée, et l'ancrage sur un bord
   de l'écran cible, ne font pas partie de ce chantier. Pas de changement sur mobile.

## Conséquences

- Aucun changement sans écran secondaire : le module ne trouve qu'une couche, celle de
  la fenêtre.
- Le zoom du navigateur fausse la conversion (`screenX` et `clientX` n'ont plus la même
  échelle) ; à 100 %, la position est exacte.
- Deux fenêtres d'écran qui se chevauchent sur le même moniteur : l'onglet où le geste a
  commencé gagne, puis l'onglet principal, puis les surfaces dans l'ordre d'ouverture.
- Tests : scénario `screen-portal` du harnais (deux écrans dans une page) et test
  Chromium réel où la fenêtre quitte l'onglet principal pour l'écran 2.
