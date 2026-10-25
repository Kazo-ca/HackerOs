# Bureau : icônes, raccourcis et `desktop.config`

Chantier `desk/5-desktop` du plan
[`desktop-and-context-menu-plan.md`](desktop-and-context-menu-plan.md) (§5).
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
| `HackDesktopLayout`, `HackDesktopCell`, `HackDesktopOptions`, `HackDesktopItems` | `AppSdk` (`Files/HackDesktop.cs`) | La grille en fonctions pures, partagée par tout fournisseur. |
| `HackDesktopEntry` | `AppSdk` (`Files/HackDesktopEntry.cs`) | Lecture et écriture des `.desktop`, `CreateShortcutAsync`. |
| `HackDesktopSettings` | `AppSdk` (`Files/HackDesktopSettings.cs`) | Lecture et écriture de `desktop.config`. |
| `HackAppIcons.LucideName` | `AppSdk` (`Apps/`) | Icône Lucide d'une app (celle du menu Démarrer), reprise par les raccourcis. |
| Menu Démarrer (`AppLauncher.Menu.cs`, `AppLauncher.razor.js`) | `Platform.Blazor` | Menu de l'OS sur chaque app (Ouvrir, Épingler, Envoyer vers ▸ Bureau) et glisser d'une app (`application/vnd.hackeros.app+json`). |
| `DesktopPanel` | `Apps.Settings` | L'onglet Paramètres ▸ Bureau. |

Le bureau étant un processus, il apparaît dans `ps` et le gestionnaire des
tâches ; `kill` le ferme (les icônes disparaissent jusqu'à la connexion
suivante), un plantage le relance une fois.

## Usage et API

### `desktop.config`

```ini
[view]
icon-size=medium      # small | medium | large
auto-arrange=false
align-to-grid=true
sort=name             # name | size | type | date
show-home=true
show-trash=true

[positions]
::home=0,0
::trash=0,1
notes.txt=1,0
```

- Fichier de l'utilisateur seulement ; pas de couche `/etc`.
- Une clé de `[positions]` est un nom de fichier (ou `::home`, `::trash`) où
  `%`, `=`, `\`, `#`, `;`, `[`, `]`, les caractères de contrôle et les espaces
  de bord sont écrits `%XX` ; la valeur est `colonne,ligne`, à partir de 0.
- Une ligne fausse est ignorée. Écrire `[view]` garde les commentaires et les
  clés inconnues ; `[positions]` est réécrit en entier.
- Le bureau suit le fichier : le modifier avec `nano` ou Paramètres redessine
  les icônes.

```csharp
HackDesktopSettingsDocument settings = await HackDesktopSettings.ReadAsync();
await HackDesktopSettings.SaveOptionsAsync(settings.Options with { IconSize = HackDesktopIconSize.Large });
```

### Grille (`HackDesktopLayout`)

| Membre | Rôle |
| --- | --- |
| `CellSize(size)` | Taille d'une case : petite 76 × 84, moyenne 92 × 100, grande 116 × 128 pixels CSS. |
| `GridSize(width, height, size)` | Colonnes et lignes qui tiennent (au moins une de chaque). |
| `Order(items, sort, showHome, showTrash)` | Ordre des éléments nouveaux : Dossier perso, Corbeille, puis dossiers et fichiers selon le tri. |
| `Arrange(keys, saved, columns, rows, autoArrange, alignToGrid)` | Case affichée de chaque élément, positions à enregistrer et `Changed`. Une position hors de l'écran est gardée (l'élément est montré ailleurs). |
| `Move(current, moved, anchor, target, columns, rows, autoArrange)` | Positions après un glisser : écarts gardés, cases occupées sautées ; en réorganisation automatique, insertion à la case visée. |
| `CellAtPoint(x, y, size, columns, rows)` | Case sous un point. |

Les cases vont de haut en bas, puis de gauche à droite.

### Raccourcis (`HackDesktopEntry`)

```ini
[Desktop Entry]
Type=Application
Name=Text Editor
Exec=org.hackeros.text-editor "/home/alice/My notes.txt"
Icon=file-text
```

`TryParse`, `Format`, `IsDesktopFile`, `IsAppId`, `SplitExec`, `JoinExec` et
`CreateShortcutAsync(dossier, appId, nom)` (écrit `Nom.desktop`, puis
`Nom (2).desktop`…). Les codes `%f`, `%U`… sont ignorés ; `%%` donne `%`. Un
fichier qui n'est pas un raccourci valide reste un fichier texte.

Dans tout contrôle de fichiers fourni par l'explorateur, un raccourci montre le
nom et l'icône de l'app, sans extension ; l'ouvrir lance l'app avec ses
arguments ; une app absente grise l'icône et l'ouvrir l'explique.

### Contrôle de fichiers en mode Bureau

```razor
<HackFileView InitialDirectory="@desktop"
              Mode="HackFileViewMode.Desktop"
              DesktopOptions="@_options"
              DesktopPositions="@_positions"
              DesktopPositionsChanged="SavePositionsAsync"
              ContextMenuOpening="OnContextMenuOpening" />
```

L'hôte garde le fichier ; le fournisseur place, déplace et signale les
positions à enregistrer (`DesktopPositionsChanged`) après un déplacement ou
quand un élément nouveau prend une case. Rien n'est enregistré avant que le
dossier soit lu, pour ne jamais effacer les positions.

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

Page après Apparence : taille des icônes, réorganisation automatique,
alignement, icônes système, tri, « Oublier les positions des icônes ».
Paramètres s'ouvre directement sur une page avec `--page=appearance` ou
`--page=desktop` (toutes les pages depuis l'ADR 0170, voir [settings-app.md](settings-app.md)).

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
