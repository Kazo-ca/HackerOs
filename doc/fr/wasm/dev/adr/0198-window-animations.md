> [English version](../../../../en/wasm/dev/adr/0198-window-animations.md)

# ADR 0198 : animations des fenêtres

## Statut

Accepté le 2026-10-30, chantier 3 de l'issue TODO
[#242](https://github.com/Kazo-ca/HackerOs/issues/242) (points 5, 6 et 7 des décisions
prises avec Benoit le 2026-10-29), série `todo/`. Mise en œuvre :
[`window-animations.md`](../window-animations.md).

## Contexte

- Les fenêtres apparaissent, disparaissent et changent de taille d'un coup. Une fenêtre
  minimisée reçoit tout de suite l'attribut `hidden` (`WindowHost.razor`).
- La géométrie des fenêtres est écrite par `WindowHost.razor.js` dans des variables CSS
  (`--window-x`…). Le déplacement à la souris est un aperçu `translate` du navigateur,
  validé par C# au relâchement ([ADR 0163](0163-window-pointer-preview-and-invariant-coordinates.md)).
- Chaque écran a sa barre des tâches et ses boutons de fenêtre
  ([ADR 0179](0179-per-screen-taskbars-and-other-screen-menus.md)). Les fenêtres sont
  déplacées dans la couche de leur écran par un portail (ADR 0178 §3).
- Le réglage « Réduire les animations » existe ([ADR 0105](0105-accessibility-preferences.md)) :
  il pose `data-motion="reduced"`, met les jetons de transition à `0ms` et réduit à
  0,01 ms les animations CSS.

## Décision

1. **Ce qui est animé.** Ouvrir, fermer, minimiser, restaurer depuis la barre,
   agrandir et restaurer la taille, et le snap (ancrage à gauche ou à droite, au clavier
   ou en glissant). Le déplacement et le redimensionnement à la souris ne sont pas
   animés : ils suivent déjà le pointeur.
2. **Minimiser vers le bouton de son écran.** La fenêtre rétrécit et s'efface vers son
   bouton dans la barre de **son** écran (le bouton porte `data-taskbar-window-id`).
   Sans bouton visible, elle part vers le bas de son écran. L'attribut `hidden` n'est
   posé qu'à la fin de l'animation ; une restauration pendant l'animation l'annule.
   Restaurer joue la même animation à l'envers, depuis le bouton.
3. **Agrandir, restaurer et snap** : la fenêtre passe de son ancien cadre au nouveau
   (technique « FLIP » : le nouveau cadre est posé tout de suite, une transformation le
   fait partir de l'ancien). Quand un glissement finit en snap, l'animation part de là
   où la fenêtre était dessinée pendant le glissement.
4. **Fermer** : comme un compositeur qui garde la dernière image d'une fenêtre détruite,
   l'OS fait s'effacer une **copie figée** de la fenêtre, pendant que Blazor retire la
   vraie tout de suite. La copie est cachée des technologies d'assistance (`aria-hidden`,
   `inert`), n'a ni `id` ni `data-window-*`, et ne reçoit pas le pointeur. Ses `iframe`,
   vidéos et sons sont remplacés par des blocs vides (rien ne se recharge ni ne joue) ;
   ses `canvas` gardent leur dernière image.
5. **Style** : rapide et sobre, un fondu et une légère échelle, **180 ms** par défaut,
   courbe `cubic-bezier(0.2, 0, 0, 1)`.
6. **Thèmes** : trois variables CSS qu'un thème ou un pack de thème peut redéfinir :
   `--hos-window-anim-duration` (`180ms`), `--hos-window-anim-easing` et
   `--hos-window-anim-scale` (`0.95`). La durée est un jeton du catalogue, modifiable dans
   l'éditeur de thèmes (groupe Animations) ; la courbe et l'échelle sont réservées aux
   feuilles de thème, car le validateur du catalogue n'accepte que les courbes
   nommées (`ease`, `ease-out`…).
7. **Réduire les animations** coupe tout, sans nouveau réglage : `data-motion="reduced"`
   et `prefers-reduced-motion: reduce` mettent `--hos-window-anim-duration` à `0ms`, et
   une durée nulle veut dire aucune animation (la fenêtre se comporte comme avant).
8. **Mobile** : un hôte sans cadre de fenêtre (une seule surface épinglée, `ShowChrome`
   à `false`) n'anime rien.
9. **Code** : le paquet `HackerOs.Windowing.Blazor` seul. `WindowHost` choisit
   l'animation de chaque rendu à partir du changement d'état de la fenêtre et la passe à
   `projectWindowGeometry` ; le script joue l'animation avec l'API Web Animations du
   navigateur (les cadres à mesurer ne sont pas accessibles en C#). Rien ne change dans
   le SDK des apps ; l'API publique du paquet gagne seulement l'override
   `WindowHost.OnParametersSet` (`PublicAPI.Unshipped.txt`). La barre des tâches ajoute seulement l'attribut
   `data-taskbar-window-id` à ses boutons.

## Conséquences

- Les tests qui minimisent une fenêtre doivent attendre l'attribut `hidden` (Playwright
  attend déjà) : il arrive ~180 ms plus tard.
- Une fenêtre fermée reste visible ~180 ms sous forme de copie inerte, absente de
  l'arbre d'accessibilité et des requêtes `[data-window-id]`.
- Le premier rendu d'une fenêtre est transparent (`is-opening`) jusqu'au début de son
  animation, pour ne jamais la montrer une image avant.
