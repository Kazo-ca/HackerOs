> [English version](../../../../en/wasm/dev/plans/multi-monitor-plan.md)

# Multi-moniteur : plan de la série

**Statut :** série lancée par Benoit le 2026-10-28 (« On peut débuter la
fonctionnalité multi-moniteur »). Les ADR [0178](../adr/0178-extended-screens-in-display-surfaces.md)
et [0179](../adr/0179-per-screen-taskbars-and-other-screen-menus.md) sont écrits
avant le code (chantier 0). Aucun code n'est encore écrit.

## 1. Objet

Donner à HackerOS un **bureau étendu sur plusieurs écrans**. Chaque écran de
plus est une surface d'affichage (`window.open` de la même instance) qui contient
son propre bureau, sa barre des tâches et ses fenêtres. Une fenêtre passe d'un
écran à l'autre sans perdre son état.

## 2. Décisions de Benoit (2026-10-28)

| Sujet | Décision |
| --- | --- |
| Navigateurs | Chrome et Edge complets (placement automatique, plein écran par moniteur, permission `window-management`), avec un **repli** ailleurs : l'utilisateur place la fenêtre lui-même et l'OS retient sa position. |
| Barre des tâches | **Une barre par écran**, et un réglage : la barre de l'écran principal montre toutes les fenêtres ou seulement celles de son écran. Les autres barres montrent leurs propres fenêtres. |
| Changer une fenêtre d'écran | **Menu et raccourci d'abord** ; le glisser à la souris vient dans un chantier séparé. |
| Menus | Clic droit sur l'icône : menu actuel, séparateur, « Autre écran ▸ » (Écran 1, Écran 2, …, séparateur, Nouvel écran). Clic droit sur Agrandir/Restaurer : Ancrer à gauche, Ancrer à droite, Plein écran (touches Linux), séparateur, « Autre écran ▸ ». |
| Voile d'identification | Au survol d'« Écran N », voile noir à 33 % sur les autres écrans, carré noir et numéro blanc au centre. **Chaque écran montre son propre numéro**, l'écran survolé en surbrillance. Le voile disparaît si le pointeur le survole. |
| Fusions | Une branche et une PR par chantier vers `multi-monitor/base`. Depuis le 2026-10-28, Benoit autorise la fusion de ces PR sans sa validation, pour passer à l'étape suivante. À la fin, une PR vers `main`, que seul Benoit autorise à fusionner. |
| Fil des boutons | Quand « Corriger les boutons des fenêtres maximisées » (ADR 0177) est fusionné, ses changements sont intégrés dans **toutes** les branches de la série, sans conflit. |

## 3. Existant (main, 2026-10-28)

| Élément | État | Où |
| --- | --- | --- |
| Surfaces d'affichage | `IDisplaySurfaceService`, portail `DisplaySurfaceContent`, styles et thème recopiés, événements de taille et de focus, états de fin | ADR 0078, 0081, [`detached-display-windows.md`](../detached-display-windows.md) |
| Gestionnaire de fenêtres | Une seule `WorkArea` (`ChangeViewportCommand`), agrandir et ancrer s'y rapportent | `Platform/HackerOs.Windowing.Core/WindowRuntime.cs` |
| Bureau | Un seul `DesktopArea` dans `DesktopShell`, bureau (`org.hackeros.desktop`) en fond | `Platform/HackerOs.Platform.Blazor/Shell/` |
| Barre des tâches | Un exemplaire, toutes les fenêtres | `Platform/HackerOs.Taskbar.Blazor` |
| Menu de fenêtre | Liste à un niveau dans `WindowManagerOverlays` ; clic droit sur l'icône, `Alt+Space` | ADR 0095, 0164 |
| Menu contextuel de l'OS | Multi-niveaux, clavier | ADR 0150 |

## 4. Chantiers

Chaque chantier part de `multi-monitor/base` à jour, sur la branche indiquée, et
revient par une PR vers `multi-monitor/base`.

| N° | Branche | Contenu | Dépend de |
| --- | --- | --- | --- |
| 0 | `multi-monitor/0-adr` | ADR 0178 et 0179 (fr et en), ce plan, index. | — |
| 1 | `multi-monitor/1-window-runtime-displays` | `DisplayId`, zone de travail par écran, `AddDisplayCommand`, `RemoveDisplayCommand`, `MoveWindowToDisplayCommand` et leurs événements ; changement d'écran dans chaque état ; modale qui suit son propriétaire ; écran retiré. API publique en `Unshipped`. Tests unitaires. Aucun changement visible. | 0, ADR 0177 |
| 2 | `multi-monitor/2-window-portal` | Fenêtres rendues une fois et déplacées par portail dans la couche de fenêtres de leur écran ; `DesktopArea` filtré par écran. Tests navigateur (aucun composant recréé). | 1 |
| 3 | `multi-monitor/3-screen-service` | Service d'écrans : permission `window-management`, `getScreenDetails`, une surface par écran sur un moniteur libre, `screenschange`, repli avec position retenue, plein écran par bouton, « Restaurer les écrans », bandeau « onglet principal caché ». | 1 |
| 4 | `multi-monitor/4-secondary-desktop` | `DesktopArea` dans chaque écran, couche d'overlays par écran (menu de fenêtre, menu contextuel, sélecteur, déplacement au clavier), dialogues sécurisés renvoyés vers l'écran 1. Fond d'écran seulement sur les écrans secondaires ; les icônes restent sur l'écran 1 (Q2). | 2, 3 |
| 5 | `multi-monitor/5-taskbars` | Une barre des tâches par écran, filtre par écran, réglage `primary-taskbar`, menu Démarrer sur l'écran cliqué, nouvelles fenêtres sur le bon écran, notifications sur l'écran 1. | 4 |
| 6 | `multi-monitor/6-other-screen-menus` | Menu de fenêtre par le menu contextuel de l'OS, nouveau menu du bouton Agrandir/Restaurer, sous-menu « Autre écran », Nouvel écran, voile d'identification, raccourcis `MoveToPreviousScreen` / `MoveToNextScreen`. | 4, ADR 0177 |
| 7 | `multi-monitor/7-screen-settings` | `screens.config` (système et utilisateur), Paramètres ▸ Écrans. | 5, 6 |
| 8 | `multi-monitor/8-drag-between-screens` | Glisser une fenêtre d'un écran à l'autre (capture du pointeur, `screenX`/`screenY` convertis vers l'écran cible). | 4 |
| 9 | `multi-monitor/9-final` | Vérification finale de `multi-monitor/base`, mémoire du projet, puis PR vers `main` (fusion autorisée par Benoit seulement). | tous |

Après le chantier 1, les chantiers 2 et 3 peuvent avancer en parallèle, et le 8
peut avancer en parallèle des chantiers 5 à 7.

## 5. Règles de la série

- **Fusions** : les PR de chantier sont fusionnées dans `multi-monitor/base`
  sans attendre Benoit (autorisation du 2026-10-28). La PR finale vers `main` n'est fusionnée que sur
  son autorisation.
- **ADR 0177** : dès que le fil des boutons est fusionné, `main` est fusionné
  dans `multi-monitor/base`, puis dans chaque branche de chantier ouverte. Les
  conflits sont résolus dans la série, jamais en réécrivant l'historique.
- **Docs** : chaque chantier met à jour les docs en français et en anglais, et
  les index `README.md` des dossiers touchés.
- **Barre de fusion** : build et suite de tests verts en local. Pour un
  chantier qui touche une API publique : `PublicAPI.Unshipped.txt` et le test de
  compatibilité du SDK.
- **Questions** : posées directement à Benoit, avec des choix, le contexte et
  leurs conséquences ; un document ne suffit pas.

## 6. Questions posées à Benoit (2026-10-28)

| N° | Question | Choix recommandé |
| --- | --- | --- |
| Q1 | Une API du SDK pour les écrans (lister les écrans, écran de ma fenêtre) entre-t-elle dans cette série ? | **Répondu le 2026-10-28 : plus tard.** La série ne touche pas au SDK des apps. |
| Q2 | Que montre le bureau d'un écran secondaire ? | **Répondu le 2026-10-28 : le fond d'écran seulement.** Les icônes de `~/Desktop` restent sur l'écran 1. |

Les réponses seront reportées ici et dans les ADR.

## 7. Limites connues

- Placement automatique et plein écran par moniteur : Chrome et Edge seulement.
- L'onglet principal doit rester affiché, sinon le navigateur ralentit tout l'OS.
- Recharger l'OS ferme les écrans secondaires ; un clic les rouvre.
- Une iframe dans une fenêtre qui change d'écran se recharge.
- Pas de miroir, pas de multi-écran sur mobile.
