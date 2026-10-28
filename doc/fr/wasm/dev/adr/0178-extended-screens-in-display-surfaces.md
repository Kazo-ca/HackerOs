> [English version](../../../../en/wasm/dev/adr/0178-extended-screens-in-display-surfaces.md)

# ADR 0178 : écrans étendus dans des surfaces d'affichage

## Statut

Accepté le 2026-10-28 à la demande de Benoit (« On peut débuter la fonctionnalité
multi-moniteur »). Reprend l'évaluation du 2026-10-28 et les réponses de Benoit
(navigateurs visés, déplacement entre écrans). S'appuie sur
l'[ADR 0078](0078-detached-display-windows.md) et
l'[ADR 0081](0081-detached-display-windows-adjustments.md) (surfaces et portail),
et sur l'[ADR 0095](0095-window-events-sizes-and-keyboard.md) (commandes clavier
des fenêtres). Le fil « Corriger les boutons des fenêtres maximisées » écrit
l'ADR 0177 (mémoire de la taille d'avant l'ancrage) : cette série l'intègre dès
qu'il est fusionné. L'interface (barres des tâches, menus, voile, réglages) est
dans l'[ADR 0179](0179-per-screen-taskbars-and-other-screen-menus.md). Plan des
chantiers : [`multi-monitor-plan.md`](../plans/multi-monitor-plan.md).

## Contexte

- Une surface d'affichage (`IDisplaySurfaceService`) est une fenêtre
  `window.open` de la même instance : aucune seconde instance .NET. Le portail
  `DisplaySurfaceContent` y rend n'importe quel arbre Blazor en gardant son état,
  ses paramètres en cascade et son contexte d'app.
  [`detached-display-windows.md`](../detached-display-windows.md) annonçait déjà
  « un bureau dessiné dans une surface ».
- `WindowRuntime` ne connaît qu'un écran : une seule `WorkArea`, posée par
  `ChangeViewportCommand`, que `DesktopArea` envoie quand sa taille change.
  Toutes les bornes, l'agrandissement et l'ancrage s'y rapportent.
- `DesktopShell` rend un seul `DesktopArea`, une seule barre des tâches et une
  seule couche d'overlays (sélecteur, menu de fenêtre, menu contextuel, dialogues
  sécurisés), dans l'onglet principal.
- Le navigateur fixe les limites. `getScreenDetails()` (lister les moniteurs,
  placer une fenêtre sur un autre, plein écran par moniteur, événement
  `screenschange`) n'existe que dans Chrome et Edge sur ordinateur, avec la
  permission `window-management`. Une fenêtre ne s'ouvre qu'après un geste de
  l'utilisateur. Une iframe déplacée d'un document à l'autre se recharge
  (ADR 0081 §4). Un onglet principal caché ralentit tout l'OS.

## Décision

1. **Un écran = une surface.** L'écran 1 est l'onglet principal. Chaque écran
   de plus est une surface ouverte par le service d'écrans, qui y rend un
   `DesktopArea` par le portail, avec le fond d'écran seulement : les icônes de
   `~/Desktop` restent sur l'écran 1 (réponse de Benoit du 2026-10-28). Il n'y a qu'un mode : le **bureau étendu**. Le
   miroir est impossible (un élément DOM ne vit qu'à un endroit) et n'est pas
   proposé.
2. **Écrans dans le gestionnaire de fenêtres** (`HackerOs.Windowing.Abstractions`,
   API publique, `PublicAPI.Unshipped.txt`) :
   - `DisplayId` (valeur ; `DisplayId.Primary` est l'écran 1) et
     `WindowRuntimeState.DisplayId` (par défaut `Primary`).
   - Une zone de travail par écran. `ChangeViewportCommand` reçoit un
     `DisplayId` facultatif (`Primary` par défaut : le code actuel ne change
     pas). Nouvelles commandes `AddDisplayCommand(DisplayId, WindowBounds)`,
     `RemoveDisplayCommand(DisplayId)` et
     `MoveWindowToDisplayCommand(WindowId, DisplayId)` ; nouveaux événements
     `DisplayAddedEvent`, `DisplayRemovedEvent` et `WindowDisplayChangedEvent`.
   - Les bornes, l'agrandissement et l'ancrage se calculent dans la zone de
     l'écran de la fenêtre. Il y a un seul focus et un seul ordre Z pour tout
     l'OS.
   - **Changer d'écran garde l'état visuel.** Une fenêtre agrandie reste
     agrandie. Une fenêtre ancrée reste ancrée du même côté, avec la taille
     d'avant l'ancrage de l'ADR 0177. Une fenêtre normale garde sa position
     relative, ramenée dans la zone de l'écran cible. Une fenêtre réduite change
     d'écran et reste réduite.
   - Une fenêtre modale suit son propriétaire : déplacer l'une déplace l'autre.
   - **Écran retiré** (surface fermée par l'utilisateur, perdue, ou moniteur
     débranché) : ses fenêtres reviennent sur l'écran 1 en gardant leur état,
     comme le contenu d'une surface revient sous son ancre.
3. **Fenêtres rendues une fois, déplacées par portail.** Chaque `WindowHost` est
   rendu une seule fois, sous une même racine, puis son élément est déplacé dans
   la couche de fenêtres du `DesktopArea` de son écran. Changer d'écran ne
   recrée ni le composant ni l'app. Une iframe dans une fenêtre déplacée se
   recharge : le navigateur de l'OS réécrit déjà sa page ; les autres apps à
   iframe perdent l'état JS de l'iframe, et la doc le dit.
4. **Service d'écrans** (`HackerOs.Platform.Blazor`, interne à l'OS) :
   - Sous Chrome et Edge, il demande la permission `window-management` au
     premier usage (pendant un clic), liste les moniteurs avec
     `getScreenDetails()` et ouvre la surface d'un nouvel écran sur un moniteur
     libre (`left`/`top` du moniteur). Il suit `screenschange` : un moniteur
     retiré ferme son écran (point 2).
   - **Repli** dans les autres navigateurs, ou sans la permission : la surface
     s'ouvre comme une fenêtre détachée, l'utilisateur la place lui-même, et sa
     position et sa taille sont retenues pour la prochaine ouverture.
   - **Plein écran** d'un écran secondaire : un bouton dans l'écran (un geste par
     écran). Il n'est jamais imposé.
   - Une surface s'ouvre seulement pendant un geste. « Nouvel écran » (menus de
     l'ADR 0179, page Écrans des Paramètres) ouvre la surface **pendant le
     clic**, puis y déplace la fenêtre.
   - Après un rechargement de l'OS, les surfaces sont fermées. L'OS propose
     « Restaurer les écrans » : un clic rouvre ceux de la session précédente,
     dans le même ordre.
   - Un écran secondaire affiche un bandeau quand l'onglet principal est caché,
     parce que le navigateur ralentit alors tout l'OS.
5. **Numéros d'écran** : l'écran 1 est l'onglet principal ; un nouvel écran prend
   le plus petit numéro libre. Le numéro ne change pas tant que l'écran existe.
6. **Clavier** : nouvelles actions `MoveToPreviousScreen` et `MoveToNextScreen`
   dans `keybindings.config` (ADR 0095). Jeu Linux : `Super+Shift+Left` /
   `Super+Shift+Right`. Jeu de repli : `Ctrl+Alt+Shift+Left` /
   `Ctrl+Alt+Shift+Right`. Les menus sont décrits par l'ADR 0179.
7. **Hors périmètre** : l'interface mobile (`MobileShell`) n'a qu'un écran. Le
   glisser d'une fenêtre d'un écran à l'autre à la souris vient dans un chantier
   à part (décision de Benoit : menu et raccourci d'abord). Aucune API du SDK
   pour les écrans n'est ajoutée (réponse de Benoit du 2026-10-28 : plus tard).

## Conséquences

- Sans écran secondaire, le rendu et le comportement ne changent pas : un seul
  `DisplayId.Primary`, et les commandes existantes gardent leur forme.
- Les types publics ajoutés à `HackerOs.Windowing.Abstractions` passent dans
  `PublicAPI.Unshipped.txt` et dans la version mineure suivante.
- Le portail des fenêtres touche tout le rendu des fenêtres : il a son propre
  chantier et ses tests navigateur (aucun composant recréé, focus et menu
  contextuel gardés).
- Les overlays (menu de fenêtre, menu contextuel, sélecteur, dialogues) doivent
  s'afficher sur l'écran où l'utilisateur agit : chaque écran a sa couche
  d'overlays (ADR 0179).

## Vérification

- Tests unitaires de `WindowRuntime` : zones par écran, changement d'écran dans
  chaque état, modale qui suit son propriétaire, écran retiré.
- Tests navigateur (`HackerOs.BrowserHarness.Tests`) : fenêtre déplacée entre
  deux `DesktopArea` sans être recréée.
- UI.E2E : deuxième écran ouvert par « Nouvel écran », fenêtre déplacée, écran
  fermé et fenêtres revenues sur l'écran 1 ; repli sans `getScreenDetails`.

## Tâches

- [x] `DisplayId`, zones par écran et commandes dans `WindowRuntime` (chantier 1, [`window-runtime-displays.md`](../window-runtime-displays.md)).
- [x] Fenêtres rendues une fois et déplacées par portail (chantier 2, [`window-portal.md`](../window-portal.md)).
- [x] Service d'écrans, permission, repli, restauration (chantier 3, [ADR 0180](0180-screen-service-placement-and-memory.md), [`screen-service.md`](../screen-service.md)).
- [x] Bureau et overlays dans chaque écran (chantier 4, [`secondary-screen-desktop.md`](../secondary-screen-desktop.md)).
- [x] Raccourcis clavier (chantier 6, [`other-screen-menus.md`](../other-screen-menus.md)).
