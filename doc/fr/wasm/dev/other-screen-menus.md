> [English version](../../../en/wasm/dev/other-screen-menus.md)

# Menus « Autre écran », voile d'identification et raccourcis d'écran

Chantier `multi-monitor/6-other-screen-menus` du [plan multi-moniteur](plans/multi-monitor-plan.md),
mise en œuvre de l'[ADR 0179 §3-6](adr/0179-per-screen-taskbars-and-other-screen-menus.md) (menus
et voile) et de l'[ADR 0178 §6](adr/0178-extended-screens-in-display-surfaces.md) (raccourcis). Il
s'appuie sur la couche d'overlays par écran ([`secondary-screen-desktop.md`](secondary-screen-desktop.md))
et sur le service d'écrans ([`screen-service.md`](screen-service.md)).

## 1. Les deux menus

| Ouverture | Menu (`data-context-menu`) | Entrées |
| --- | --- | --- |
| Clic droit sur l'icône de l'app, `Alt+Space`, `Alt+F3` | `window-menu` | Agrandir/Restaurer, Réduire, Déplacer, Redimensionner, Ancrer à gauche, Ancrer à droite, Plein écran, Fermer ; séparateur ; **Autre écran ▸** |
| Clic droit sur le bouton Agrandir/Restaurer | `window-maximize-menu` | Ancrer à gauche, Ancrer à droite, Plein écran ; séparateur ; **Autre écran ▸** |

**Autre écran ▸** : Écran 1, Écran 2, … (tous les écrans de `WindowRuntime.Displays`, l'écran de la
fenêtre désactivé) ; séparateur ; **Nouvel écran**. Le sous-menu existe aussi avec un seul écran.
Le clic gauche sur Agrandir/Restaurer ne change pas (ADR 0177). Le shell mobile n'a pas ces menus.

Les raccourcis affichés sont ceux qui sont actifs (touches Linux sous le verrouillage du clavier,
touches de remplacement sinon), comme avant.

## 2. Le menu de fenêtre passe par le menu contextuel de l'OS

`WindowManagerOverlays` ne dessine plus son propre menu (`nav.window-menu` a disparu). Il garde
`WindowKeyboardCommands.Menu` comme source de vérité et l'affiche dans `ContextMenuService` :

```
WindowChrome ─ clic droit (icône | Agrandir) ─► WindowMenuRequest { Kind }
DesktopShell.HandleWindowMenu ─► WindowKeyboardCommands.OpenMenu(id, x, y, kind)
WindowManagerOverlays (Commands.Changed)
  └─ ContextMenuService.OpenAsync(scope du shell, "window-menu" | "window-maximize-menu", …)
       └─ BuildMenu : GetMenuItems(id, kind) + GetScreenMenuItems(id) + Nouvel écran
ContextMenuOverlay ─ dessine, sous-menu, clavier, placement sur l'écran où l'on agit
```

- Une `ContextMenuScope` du shell (sans contexte d'app) enregistre les deux menus. Les entrées
  appellent `ExecuteForWindowAsync`, `MoveToScreen` ou `MoveToNewScreenAsync`.
- Ouvert au clavier (`Alt+Space`), le menu met la première entrée en surbrillance
  (`ContextMenuOrigin.Keyboard`).
- Synchronisation dans les deux sens : `Commands.Menu` remis à `null` ferme le menu contextuel ;
  le menu contextuel fermé (Échap, clic ailleurs, autre menu ouvert) remet `Commands.Menu` à
  `null`.
- Identifiants des entrées : `window.<clé de keybindings.config>` (`window.close`…),
  `window.other-screen`, `window.screen.<N>`, `window.new-screen`.

## 3. Changer d'écran

| API (`WindowKeyboardCommands`) | Rôle |
| --- | --- |
| `GetMenuItems(id, WindowMenuKind)` | Entrées d'un des deux menus, sans le sous-menu. |
| `GetScreenMenuItems(id)` | Écrans du sous-menu, celui de la fenêtre désactivé. |
| `MoveToScreen(id, écran)` | `MoveWindowToDisplayCommand`, puis focus si la fenêtre n'est pas réduite. |
| `OpenScreen` | Routine donnée par `DesktopShell` : `ScreenService.OpenScreenAsync()`, l'écran ouvert ou `null`. |
| `CanOpenScreen` | `OpenScreen` présent et un numéro libre (`ScreensSettingsDocuments.MaxScreenNumber`). |
| `MoveToNewScreenAsync(id)` | Ouvre l'écran pendant le clic (geste exigé par le navigateur), puis y déplace la fenêtre. Refus du navigateur : la fenêtre reste, la notification « fenêtre bloquée » des surfaces explique. |

**Raccourcis** (`keybindings.config`, ADR 0095) :

| Clé | `[linux]` | `[fallback]` |
| --- | --- | --- |
| `move-to-previous-screen` | `Super+Shift+Left` | `Ctrl+Alt+Shift+Left` |
| `move-to-next-screen` | `Super+Shift+Right` | `Ctrl+Alt+Shift+Right` |

Ils déplacent la fenêtre active vers l'écran d'avant ou d'après **par numéro**, en faisant le tour
(de l'écran 3 on revient à l'écran 1). Avec un seul écran, rien ne se passe.

## 4. Voile d'identification

`ScreenIdentifyVeil` (paramètre `Number`) est rendu une fois par écran : dans `DesktopShell` pour
l'écran 1, dans chaque écran de `ScreenLayer` pour les autres.

- `ContextMenuService` publie l'entrée en surbrillance (`Highlighted`, `HighlightChanged`,
  internes à l'OS). `ContextMenuOverlay` la met à jour au survol (même une entrée désactivée,
  pour que « Écran 1 » désactivé montre aussi son écran), au départ du pointeur de l'entrée, et
  à chaque touche (entrée en surbrillance du niveau le plus profond). La fermeture du menu la remet
  à `null`.
- Quand l'entrée est `window.screen.<N>`, chaque voile s'affiche : noir à 33 % sur tout l'écran,
  carré noir au centre avec **son propre** numéro en blanc ; sur l'écran N, le carré a la bordure
  de la couleur d'accent (`.is-target`).
- **L'écran qui montre le menu n'a pas de voile** : règle CSS
  `:root:has(.screen-overlays) .screen-identify-veil { display: none }`. Le conteneur des overlays
  est dans le document de l'écran où le menu est affiché (chantier 4).
- **Garde-fou** : le voile disparaît dès que le pointeur passe dessus (`pointerenter`,
  `pointermove`, `pointerdown`). Il revient seulement quand une autre entrée d'écran est
  survolée. Il ne fait rien d'autre d'un clic.

## 5. Tests

- `Tests/HackerOs.Platform.Blazor.Tests/OtherScreenMenuTests.cs` : entrées du menu d'Agrandir,
  type et origine du menu ouvert, écrans du sous-menu (écran courant désactivé, un seul écran,
  français), déplacement et focus, raccourcis qui font le tour, raccourcis sans effet avec un
  écran, valeurs par défaut des raccourcis, Nouvel écran accepté puis refusé, lecture du numéro
  par le voile.
- `Tests/HackerOs.Platform.Blazor.Tests/WindowMenuContextMenuTests.cs` : les deux menus ouverts
  dans `ContextMenuService` avec leur sous-menu, choix d'un écran, fermeture dans les deux sens,
  voile affiché sur chaque écran avec la cible marquée puis retiré à la fermeture.
- `Tests/HackerOs.UI.E2E.Tests/ScreenServiceTests.cs`, Chromium réel avec l'écran 2 restauré :
  clic droit sur Agrandir ; survol de « Écran 2 » → voile sur l'écran 2 seulement, retiré quand le
  pointeur passe dessus et quand il quitte l'entrée ; « Écran 2 » déplace la fenêtre ;
  `Ctrl+Alt+Shift+Left` la ramène ; « Nouvel écran » ouvre l'écran 3 avec la fenêtre.
- `Tests/HackerOs.UI.E2E.Tests/WindowAppIconTests.cs` : mêmes attentes qu'avant ; le menu se trouve
  maintenant par `[data-context-menu='window-menu']`.

## 6. Limites

- Le plein écran du menu porte sur l'onglet principal, comme le raccourci (chantier 4 §6).
- Pas de raccourci « aller à l'écran N » : seulement précédent et suivant (ADR 0178 §6).
