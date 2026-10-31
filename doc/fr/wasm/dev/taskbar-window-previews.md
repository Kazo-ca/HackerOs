> [English version](../../../en/wasm/dev/taskbar-window-previews.md)

# Aperçus des fenêtres de la barre des tâches et « peek »

Chantier 5 de la série barre des tâches (issue [#242](https://github.com/Kazo-ca/HackerOs/issues/242)),
mise en œuvre de l'[ADR 0199](adr/0199-taskbar-window-previews.md). Il s'appuie sur les barres
par écran ([`per-screen-taskbars.md`](per-screen-taskbars.md)) et les menus de la barre
([`taskbar-context-menus.md`](taskbar-context-menus.md)).

## 1. Ce que voit l'utilisateur

- Survoler le bouton d'une fenêtre avec la souris pendant 400 ms ouvre une **carte d'aperçu**
  au-dessus du bouton : icône, titre, bouton « × » et une **copie réduite en direct** de la
  fenêtre, rafraîchie deux fois par seconde.
- Quand une carte est ouverte, survoler un autre bouton y passe tout de suite.
- **Clic sur la vignette** : la fenêtre passe au premier plan (restaurée si elle était
  minimisée). **« × »** : la fenêtre se ferme, comme par sa barre de titre. **Clic droit** : le
  menu du bouton de la fenêtre.
- **Peek** : laisser le pointeur 300 ms sur la carte montre la fenêtre seule à sa place (même
  minimisée) ; les autres fenêtres de l'écran ne gardent que leur contour. Quitter la carte
  remet tout comme avant.
- Pas d'aperçu au doigt (mobile, écran tactile) ni au clavier.
- Une fenêtre qui affiche un `iframe`, une vidéo ou un plugin montre son icône et son titre au
  lieu de la copie.

## 2. Architecture

| Pièce | Projet | Rôle |
| --- | --- | --- |
| `ITaskbarWindowPreviews` | `HackerOs.Taskbar.Blazor` | Contrat donné par l'hôte : `LiveCopySelector(id)` et `Peek(id?)`. |
| `Taskbar.Previews` | `HackerOs.Taskbar.Blazor` | Paramètre ; sans lui, aucun aperçu. |
| `TaskbarPreviewController` | `HackerOs.Taskbar.Blazor` (interne) | Minutage : ouverture (400 ms), passage d'un bouton à l'autre, fermeture (300 ms), peek (300 ms), toucher ignoré. |
| `Taskbar.razor.js` | `HackerOs.Taskbar.Blazor` | Place la carte au-dessus du bouton, clone la fenêtre, retire identifiants et attributs actifs, copie les canevas et les défilements, recommence toutes les 500 ms. |
| `TaskbarWindowInteraction.Show` | `HackerOs.Taskbar.Blazor` (interne) | Clic sur la vignette : restaurer si minimisée, puis activer. |
| `TaskbarWindowPreviews` | `HackerOs.Platform.Blazor` (`Shell/`) | Implémentation du bureau : sélecteur `[data-window-id="…"]`, fenêtre du peek et événement `Changed`. Une instance pour toutes les barres. |
| `DesktopArea.PeekWindowId`, `WindowHost.PeekState`, `WindowPeekState` | `HackerOs.Windowing.Blazor` | Le peek : `is-peek-shown` (la fenêtre et ses boîtes de dialogue, au-dessus, même minimisée) et `is-peek-see-through` (les autres fenêtres du même écran, contour seul, sans pointeur). |

Le flux : survol → `TaskbarPreviewController` → rendu de la carte → `OnAfterRenderAsync`
appelle `showPreview` du module. Repos sur la carte → `Peek(id)` → `TaskbarWindowPreviews.Changed`
→ `DesktopShell` se redessine → `DesktopArea` donne un `WindowPeekState` à chaque fenêtre.

## 3. Copie en direct : règles

- Retirés de la copie : `id`, `name`, `tabindex`, `autofocus`, `accesskey`, `contenteditable`,
  `popover`, `for`, `form`, `list`, les `aria-*` qui désignent un identifiant et les
  `data-window-*` (la copie n'est jamais prise pour une fenêtre).
- La couche de copie est `inert` et `aria-hidden="true"` : rien ne prend le focus ni le
  pointeur, et les lecteurs d'écran ne la lisent pas.
- La taille est celle de la fenêtre à l'écran ; minimisée, celle de sa géométrie
  (`--window-width`, `--window-height`).

## 4. Thèmes

Jetons ajoutés : `--hos-taskbar-preview-width`, `--hos-taskbar-preview-height`,
`--hos-taskbar-preview-background`, `--hos-taskbar-preview-border`,
`--hos-taskbar-preview-radius`, `--hos-taskbar-preview-shadow`,
`--hos-taskbar-preview-duration`, `--hos-taskbar-preview-close-hover-border`,
`--hos-taskbar-preview-close-hover-background`, `--hos-window-peek-outline`, `--hos-window-peek-glass` et
`--hos-window-peek-z`. L'apparition est coupée par « Réduire les animations »
(`data-motion`) et `prefers-reduced-motion`.

## 5. Tests

- `Tests/HackerOs.Taskbar.Blazor.Tests/TaskbarPreviewControllerTests.cs` : minutage, toucher,
  passage d'un bouton à l'autre, peek, fermeture, clic sur la vignette, attributs du rendu.
- `Tests/HackerOs.Platform.Blazor.Tests/Shell/TaskbarWindowPreviewsTests.cs` : sélecteur, peek
  annoncé une fois, classes de peek par fenêtre et par écran.
- `Tests/HackerOs.UI.E2E.Tests/TaskbarWindowPreviewTests.cs` : dans Chromium, survol, copie sans
  identifiant, peek, « × ».

## 6. Liste des tâches

- [x] ADR 0199.
- [x] Contrat `ITaskbarWindowPreviews` et paramètre `Taskbar.Previews`.
- [x] Minutage en C# (`TaskbarPreviewController`) et tests.
- [x] Carte d'aperçu (icône, titre, « × », vignette, menu contextuel) et textes fr/en.
- [x] Copie en direct (module JavaScript du paquet).
- [x] Peek dans `DesktopArea`/`WindowHost` et implémentation du bureau.
- [x] Test E2E, docs, `features.md`, entrée de changement.
