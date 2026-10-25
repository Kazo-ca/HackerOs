> [English version](../../../en/wasm/framework/desktop-api.md)

# Bureau : API pour les apps

Ce document décrit ce qu'une app utilise pour le bureau : les types `HackDesktop*` du SDK, le fichier `desktop.config`, les raccourcis `.desktop`, le mode Bureau du contrôle de fichiers et l'intent de création de raccourci. L'app Bureau, la coquille, les menus et les surfaces de l'OS sont décrits dans [`dev/desktop.md`](../dev/desktop.md).

## Types du SDK

| Élément | Projet | Rôle |
| --- | --- | --- |
| `HackDesktopLayout`, `HackDesktopCell`, `HackDesktopOptions`, `HackDesktopItems` | `AppSdk` (`Files/HackDesktop.cs`) | La grille en fonctions pures, partagée par tout fournisseur. |
| `HackDesktopEntry` | `AppSdk` (`Files/HackDesktopEntry.cs`) | Lecture et écriture des `.desktop`, `CreateShortcutAsync`. |
| `HackDesktopSettings` | `AppSdk` (`Files/HackDesktopSettings.cs`) | Lecture et écriture de `desktop.config`. |
| `HackAppIcons.LucideName` | `AppSdk` (`Apps/`) | Icône Lucide d'une app (celle du menu Démarrer), reprise par les raccourcis. |

## Fichier `desktop.config`

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

## Grille (`HackDesktopLayout`)


| Membre | Rôle |
| --- | --- |
| `CellSize(size)` | Taille d'une case : petite 76 × 84, moyenne 92 × 100, grande 116 × 128 pixels CSS. |
| `GridSize(width, height, size)` | Colonnes et lignes qui tiennent (au moins une de chaque). |
| `Order(items, sort, showHome, showTrash)` | Ordre des éléments nouveaux : Dossier perso, Corbeille, puis dossiers et fichiers selon le tri. |
| `Arrange(keys, saved, columns, rows, autoArrange, alignToGrid)` | Case affichée de chaque élément, positions à enregistrer et `Changed`. Une position hors de l'écran est gardée (l'élément est montré ailleurs). |
| `Move(current, moved, anchor, target, columns, rows, autoArrange)` | Positions après un glisser : écarts gardés, cases occupées sautées ; en réorganisation automatique, insertion à la case visée. |
| `CellAtPoint(x, y, size, columns, rows)` | Case sous un point. |

Les cases vont de haut en bas, puis de gauche à droite.

## Raccourcis (`HackDesktopEntry`)

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

## Contrôle de fichiers en mode Bureau

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

## Lancement et intents

Paramètres s'ouvre directement sur un onglet avec `--page=appearance` ou
`--page=desktop`.


« Envoyer vers ▸ Bureau (créer un raccourci) » passe par l'intent `org.hackeros.desktop.create-shortcut.v1`, et le fichier est écrit avec les droits de l'app Bureau.

Les entrées `background` et `desktop` des apps (ADR 0159) s'ajoutent au menu du fond ; voir [`context-menu.md`](context-menu.md).
