> [English version](../../../en/wasm/dev/taskbar-context-menus.md)

# Menus contextuels de la barre des tâches

Chantier 4 de la série barre des tâches (issue [#242](https://github.com/Kazo-ca/HackerOs/issues/242)),
mise en œuvre de l'[ADR 0194](adr/0194-taskbar-context-menus.md). Il s'appuie sur le menu
contextuel de l'OS ([ADR 0150](adr/0150-os-context-menu.md)), le menu de fenêtre et « Autre écran ▸ »
([`other-screen-menus.md`](other-screen-menus.md)) et les barres par écran
([`per-screen-taskbars.md`](per-screen-taskbars.md)).

## 1. Ce que voit l'utilisateur

| Où | Menu |
| --- | --- |
| Bouton d'une fenêtre | Nouvelle fenêtre · Agrandir/Restaurer, Minimiser, Déplacer, Taille, Ancrer à gauche, Ancrer à droite, Plein écran, Fermer · Autre écran ▸ |
| Reste de la barre (y compris le bouton Démarrer et l'horloge) | Gestionnaire des tâches · Afficher le bureau, Fenêtres en cascade, Fenêtres en mosaïque · Paramètres de la barre des tâches |

Le menu s'ouvre par un clic droit, un appui long, la touche Menu ou `Maj+F10`, sur la barre de
chaque écran. Le clic droit n'active pas la fenêtre.

- **Nouvelle fenêtre** lance l'app une nouvelle fois sur l'écran de la barre. Elle est désactivée
  pour une app `SingleInstancePerUser` ou absente du catalogue.
- **Gestionnaire des tâches** ouvre le moniteur système (`org.hackeros.system-monitor`) sur
  l'écran de la barre.
- **Afficher le bureau** minimise les fenêtres de l'écran de la barre.
- **Cascade** et **mosaïque** rangent les fenêtres de l'écran de la barre (section 3).
- **Paramètres de la barre des tâches** ouvre Paramètres sur la page « Écrans »
  (`--page=screens`), qui porte le réglage `primary-taskbar`.

Les entrées qui n'ont rien à faire sont désactivées.

## 2. Le code

| Élément | Rôle |
| --- | --- |
| `ITaskbarContextMenus` (paquet `HackerOs.Taskbar.Blazor`) | Donne les attributs HTML de la barre (`TaskbarAttributes`) et du bouton de chaque fenêtre (`WindowItemAttributes(id)`). Le paramètre `Taskbar.ContextMenus` les pose avec `@attributes`. Sans lui, rien ne change. |
| `TaskbarContextMenus` (`Platform.Blazor/Shell`) | Crée une portée du menu de l'OS et y enregistre `taskbar-window-menu` (objet attaché : la fenêtre et l'écran de la barre) et `taskbar-menu` (objet attaché : l'écran). Le menu est rempli à l'ouverture (`Opening`). `For(display)` donne l'objet de chaque barre. |
| `WindowMenuDefinitions` (`Platform.Blazor/Windows`) | Construit les entrées du menu de fenêtre et « Autre écran ▸ », pour la barre de titre (`WindowManagerOverlays`) et pour la barre des tâches. |
| `WindowArrangement` (`Platform.Blazor/Windows`) | `ShowDesktop`, `Cascade`, `Tile` et `ArrangeableWindows` sur un `WindowRuntime` et un écran. |
| `DesktopShell` | Crée `TaskbarContextMenus` et le passe à la barre de l'écran 1 et à celle de chaque écran secondaire ; lance les apps sur l'écran de la barre (`LaunchFromTaskbarAsync`). |

Les textes sont dans `Platform.Blazor/Resources/Strings*.resx` (clés `TaskbarMenu.*`).

## 3. Cascade et mosaïque

Fenêtres concernées (`ArrangeableWindows`) : celles de l'écran, pas minimisées, sans
propriétaire, non modales, et non bloquées par une boîte de dialogue. Une fenêtre fixe (non
déplaçable) reste en place.

- Une fenêtre agrandie est restaurée d'abord. Restaurer une fenêtre d'arrière-plan la met au
  premier plan : l'ordre d'origine est alors remis par des `FocusWindowCommand` de l'arrière vers
  l'avant.
- Chaque fenêtre reçoit un `MoveWindowCommand` (qui fait quitter l'ancrage), puis, si sa taille
  change et qu'elle est redimensionnable, un `ResizeWindowCommand` et un
  `CompleteWindowResizeCommand`. Le runtime borne la position et la taille comme pour un
  déplacement à la main, et `WindowGeometryRecorder` enregistre la nouvelle taille (ADR 0116).
- **Cascade** : fenêtre `i` en `(x + 28·(i mod 8), y + 28·(i mod 8))` de la zone de travail, sa
  taille gardée (`WindowLaunchCoordinator.CascadeStep`).
- **Mosaïque** : `ceil(√n)` colonnes, `ceil(n / colonnes)` lignes, cases de taille entière
  (`TileCell`), remplies ligne par ligne de l'arrière vers l'avant.

## 4. Tests

- `Tests/HackerOs.Taskbar.Blazor.Tests/TaskbarContextMenusTests.cs` : attributs posés sur la barre
  et les boutons, aucun sans hôte.
- `Tests/HackerOs.Platform.Blazor.Tests/Shell/TaskbarContextMenusTests.cs` : entrées et état des
  deux menus, lancements sur l'écran de la barre, entrées désactivées.
- `Tests/HackerOs.Platform.Blazor.Tests/WindowArrangementTests.cs` : afficher le bureau, cascade,
  mosaïque, fenêtres agrandies, ancrées, fixes, minimisées et boîtes de dialogue.
- `WindowMenuContextMenuTests` garde le menu de la barre de titre identique.
