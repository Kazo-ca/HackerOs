> [English version](../../../en/wasm/dev/window-animations.md)

# Animations des fenêtres

Chantier 3 de l'issue [#242](https://github.com/Kazo-ca/HackerOs/issues/242), mise en œuvre
de l'[ADR 0198](adr/0198-window-animations.md).

## 1. Ce que voit l'utilisateur

| Action | Animation |
| --- | --- |
| Ouvrir une fenêtre | fondu et légère échelle (0,95 → 1) |
| Fermer | la fenêtre s'efface et rétrécit un peu |
| Minimiser | la fenêtre rétrécit et s'efface vers son bouton dans la barre de son écran |
| Restaurer depuis la barre | l'inverse, depuis le bouton |
| Agrandir, restaurer la taille, snap | la fenêtre glisse de son ancien cadre vers le nouveau |

Durée : 180 ms par défaut. « Réduire les animations » (Paramètres ▸ Accessibilité) ou la
préférence du système coupent tout. Rien n'est animé sur mobile (surface unique, sans cadre).

## 2. Architecture

Tout est dans `Platform/HackerOs.Windowing.Blazor` :

- **`WindowHost.razor`** compare, à chaque `OnParametersSet`, l'instantané précédent de la
  fenêtre au nouveau et choisit l'animation du rendu : `open` (premier rendu), `minimize`,
  `restore`, `geometry` (changement Normal ↔ Agrandi, ou de `IsSnapped`) ou `none`.
  - Pendant `minimize`, l'attribut `hidden` attend la fin de l'animation : un compteur
    (`_minimizeGeneration`) rend caduque une animation qu'une restauration a interrompue.
  - Le premier rendu porte la classe `is-opening` (opacité 0) que le script retire quand
    l'animation d'ouverture commence : l'import du module ne montre jamais la fenêtre une
    image trop tôt.
  - À la destruction (`DisposeAsync`), il appelle `playClose`.
- **`WindowHost.razor.js`** :
  - `projectWindowGeometry(element, x, y, w, h, z, transition, windowId)` écrit la
    géométrie, joue l'animation demandée et retient le cadre de la fenêtre (sans
    transformation) pour l'animation suivante. Pour `minimize`, la promesse se termine à
    la fin de l'animation (ou un peu après sa durée, si l'onglet est en arrière-plan).
  - `playClose(element)` insère dans la couche de l'écran une copie figée de la fenêtre
    (`is-closing-ghost`) et l'efface.
  - Les animations utilisent `element.animate` (Web Animations). Chaque nouvelle animation
    annule la précédente : une fenêtre ne reste jamais transparente.
  - La cible de la minimisation est le bouton `[data-taskbar-window-id="<id>"]` le plus
    proche de la couche de l'écran de la fenêtre, sinon le bas de l'écran.
- **`WindowChrome.razor.js`** retient, pendant un glissement, le cadre dessiné
  (`__hosPreviewBox`) : un glissement qui finit en snap anime depuis là.
- **`HackerOs.Taskbar.Blazor`** : chaque bouton de fenêtre porte `data-taskbar-window-id`.

## 3. Variables de thème

| Variable | Défaut | Où la changer |
| --- | --- | --- |
| `--hos-window-anim-duration` | `180ms` | éditeur de thèmes (Animations), thème, pack |
| `--hos-window-anim-easing` | `cubic-bezier(0.2, 0, 0, 1)` | feuille de thème ou pack |
| `--hos-window-anim-scale` | `0.95` | feuille de thème ou pack |

`themes.css` met la durée à `0ms` sous `data-motion="reduced"` et
`prefers-reduced-motion: reduce`. Une durée nulle ou une variable absente avec
`prefers-reduced-motion: reduce` : aucune animation. Une courbe que le navigateur ne
comprend pas est remplacée par la courbe par défaut.

## 4. Décisions clés

- Copie figée pour la fermeture plutôt que retarder la suppression dans le gestionnaire
  de fenêtres : l'état C# reste exact (la fenêtre est fermée) et rien ne change pour les
  apps ni pour les tests qui comptent les fenêtres.
- FLIP plutôt qu'une transition CSS sur `top/left/width/height` : le glissement et le
  redimensionnement à la souris restent instantanés.
- Rien de nouveau dans le SDK des apps ; seul l'override `WindowHost.OnParametersSet` entre dans `PublicAPI.Unshipped.txt` du paquet.

## 5. Tests

- `Tests/HackerOs.Platform.Blazor.Tests/WindowAnimationTests.cs` : rendu de
  `WindowHost` (classe `is-opening`, `hidden` après la fin de la minimisation, animation
  passée au script).
- `Tests/HackerOs.Apps.ThemeEditor.Tests` : le jeton `--hos-window-anim-duration` est traduit.

## 6. Liste des tâches

- [x] Choisir l'animation de chaque rendu dans `WindowHost` et retarder `hidden`.
- [x] Jouer ouverture, minimisation, restauration, géométrie et fermeture dans le script.
- [x] Variables de thème et jeton du catalogue (fr + en), coupure par « Réduire les animations ».
- [x] Attribut `data-taskbar-window-id` sur les boutons de la barre.
- [x] Tests, ADR 0198, docs, `features.md`, entrée de changement.
