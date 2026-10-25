# Bureau : icônes, raccourcis et `desktop.config`

Les types `HackDesktop*`, le fichier `desktop.config`, les raccourcis `.desktop` et l'intent de création de raccourci destinés aux apps sont dans [`desktop-api.md`](../framework/desktop-api.md).

Chantier `desk/5-desktop` du plan
[`desktop-and-context-menu-plan.md`](plans/desktop-and-context-menu-plan.md) (§5).
Les décisions sont dans l'[ADR 0152](adr/0152-desktop-icons.md) et
l'[ADR 0160](adr/0160-desktop-implementation-adjustments.md) ; ce document
décrit le code livré.

Depuis l'ADR 0166, le bureau masque les noms commençant par un point ;
`~/Desktop/.mobile/layout.conf` contient la disposition mobile et ne crée
aucune icône sur le bureau. L'explorateur peut révéler ce dossier caché.

## Objet

Le fond du bureau montre les fichiers de `~/Desktop` en grille d'icônes, avec
les icônes système **Dossier perso** et **Corbeille**, comme le bureau de
Windows ou de Xfce :

- chaque icône garde sa case, mémorisée dans
  `~/.config/hackeros/desktop.config` ; un fichier nouveau prend la première
  case libre ; « Réorganiser automatiquement » range tout sans trou ;
- glisser pour déplacer (aimanté à la grille), sélection au lasso, Ctrl et Maj
  pour étendre, flèches, Entrée, F2 et Suppr ;
- glisser-déposer avec l'explorateur et entre fenêtres, dépôt sur un dossier,
  sur Dossier perso (copie ou déplacement vers `~`) ou sur la Corbeille ;
- des **raccourcis d'apps** : fichiers `.desktop` au format freedesktop, créés
  en glissant une app du menu Démarrer sur le bureau (ou dans un dossier de
  l'explorateur) ou par « Envoyer vers ▸ Bureau (créer un raccourci) » ;
- le menu contextuel de l'OS, portée `Desktop`, avec « Affichage ▸ »,
  « Trier par ▸ », « Arrière-plan… » et « Paramètres du bureau… » ;
- un onglet **Paramètres ▸ Bureau**.

Sur mobile, il n'y a pas d'icônes de bureau (ADR 0152 §7).

## Architecture

```text
DesktopShell ──► DesktopArea.BackgroundContent (BackgroundInteractive)
                   └─ DesktopSurfaceHost ── lance org.hackeros.desktop (processus réel)
                        └─ DesktopSurface (app Bureau, droits de son manifeste)
                             └─ HackFileView Mode=Desktop ──► registre ──► ExplorerFileView
                                                                └─ FileView ─► FileViewDesktop (grille)
```

| Élément | Projet | Rôle |
| --- | --- | --- |
| `DesktopSurfaceHost` | `Platform.Blazor` (`Shell/`) | Réserve l'app comme surface de la coquille (`WindowLaunchCoordinator.ReserveShellSurface`), la lance pour l'utilisateur de la session, rend son composant d'entrée hors fenêtre avec le contexte d'app et une portée de menus, la relance une fois si elle plante. `CreateShortcutAsync(appId)` envoie l'intent de raccourci. |
| `DesktopArea.BackgroundInteractive` | `Windowing.Blazor` | Rend la couche de fond cliquable et lisible ; la couche des fenêtres laisse passer le pointeur hors des fenêtres. |
| App **Bureau** (`org.hackeros.desktop`) | `Apps/System/HackerOs.Apps.Desktop` | App système cachée, à instance unique, SDK public seulement. Crée `~/Desktop` s'il manque, lit et écrit `desktop.config`, suit `~/.config/hackeros`, complète le menu du fond, reçoit l'intent de raccourci. |
| `FileViewDesktop` | explorateur, `FileControl/` (`AppSdk.FileView` avant le SDK 2.0) | Le mode Bureau du contrôle interne : grille mesurée (`ResizeObserver`), sélection, lasso, clavier, glisser-déposer, icônes système. Script collocalisé `FileViewDesktop.razor.js`. |
| `ExplorerFileView` (`ExplorerFileView.Desktop.cs`) | `Apps.FileExplorer` | Le fournisseur : menus et actions de Dossier perso et Corbeille, état plein ou vide de la Corbeille, décor des raccourcis (nom et icône de l'app, grisé si l'app manque), lancement d'un raccourci, création de raccourcis à un dépôt d'app. |
| Menu Démarrer (`AppLauncher.Menu.cs`, `AppLauncher.razor.js`) | `Platform.Blazor` | Menu de l'OS sur chaque app (Ouvrir, Épingler, Envoyer vers ▸ Bureau) et glisser d'une app (`application/vnd.hackeros.app+json`). |
| `DesktopPanel` | `Apps.Settings` | L'onglet Paramètres ▸ Bureau. |

Le bureau étant un processus, il apparaît dans `ps` et le gestionnaire des
tâches ; `kill` le ferme (les icônes disparaissent jusqu'à la connexion
suivante), un plantage le relance une fois.

## Usage et API

### Menus

- **Fond** (portée `Desktop`) : Nouveau dossier, Coller, Actualiser,
  Propriétés du fournisseur, plus, en tête, « Affichage ▸ » (tailles,
  Réorganiser automatiquement, Aligner sur la grille, Afficher Dossier perso,
  Afficher Corbeille, cochés par l'icône `check`) et « Trier par ▸ » (efface
  les positions), et à la fin « Arrière-plan… » (`--page=appearance`) et
  « Paramètres du bureau… » (`--page=desktop`). Les entrées `background` et
  `desktop` des apps (ADR 0159) s'y ajoutent.
- **Dossier perso** : Ouvrir, Propriétés. **Corbeille** : Ouvrir, Vider la
  corbeille (avec confirmation), Propriétés. Ouvrir la Corbeille lance
  l'explorateur avec `trash:///`.
- **Menu Démarrer**, sur chaque app : Ouvrir, Épingler / Désépingler,
  « Envoyer vers ▸ Bureau (créer un raccourci) », grisé quand le bureau ne
  tourne pas.

### Paramètres ▸ Bureau

Onglet après Apparence : taille des icônes, réorganisation automatique,
alignement, icônes système, tri, « Oublier les positions des icônes ».

## Décisions clés

- **Le bureau est une app** (ADR 0160 §1) : le contrôle de fichiers s'exécute
  avec les droits d'une app, et un vrai processus se voit et se tue comme
  `explorer.exe` ou `xfdesktop`.
- **Pas de fenêtre pour la surface** : la réservation par id d'app dans
  `WindowLaunchCoordinator` empêche toute fenêtre, même si quelqu'un lance
  l'app à la main.
- **Fichier de l'utilisateur, codec dans le SDK** (ADR 0160 §4) : pas de
  document de l'OS ni de schéma pour `[positions]` ; tout fournisseur et
  Paramètres utilisent le même code.
- **Cases, pas pixels** : une case reste juste quand la taille des icônes
  change.
- **Raccourci écrit par le bureau** : « Envoyer vers ▸ Bureau » passe par
  l'intent `org.hackeros.desktop.create-shortcut.v1`, et le fichier est écrit
  avec les droits de l'app Bureau, pas de la coquille.

## Tests

- `HackerOs.AppSdk.Tests/Files/HackDesktopTests.cs` : ordre, placement,
  collisions, hors écran, réorganisation automatique, déplacements, raccourcis
  (lecture, écriture, rejet, noms numérotés), `desktop.config` (lecture,
  écriture, clés encodées, création du dossier).
- `HackerOs.Apps.FileExplorer.FileControl.Tests/FileViewDesktopTests.cs` : charge d'un glisser
  d'app.
- `HackerOs.Platform.Blazor.Tests/WindowLaunchCoordinatorTests.cs` :
  réservation de surface.
- `HackerOs.Apps.Settings.Tests/SettingsWindowTests.cs` : `--page=`.
- `HackerOs.Ecosystem.Tests/BuildKnownLazyAppsTests.cs` : l'app Bureau est
  une app système chargée à la demande.

## Tâches (chantier `desk/5-desktop`)

- [x] ADR 0160 (ajustements de l'ADR 0152).
- [x] Contrat SDK : `HackDesktop*`, `HackDesktopEntry`, `HackDesktopSettings`,
  `HackAppIcons`, `HackFileMenuItems.EmptyTrash`.
- [x] `HackFileView` : `HackFileViewMode.Desktop`, `DesktopOptions`,
  `DesktopPositions`, `DesktopPositionsChanged`.
- [x] `FileViewDesktop` : grille, sélection, lasso, clavier, renommage,
  glisser-déposer, icônes système.
- [x] Fournisseur explorateur : menus système, Corbeille pleine ou vide,
  raccourcis décorés et lancés, dépôt d'apps, `trash:///`.
- [x] App Bureau et `DesktopSurfaceHost`, couche de fond interactive,
  réservation de surface.
- [x] Menu Démarrer : menu de l'OS, glisser d'une app, « Envoyer vers ▸
  Bureau ».
- [x] Paramètres ▸ Bureau et `--page=`.
- [x] Tests et documentation (ce document, ADR, index, documents liés).
