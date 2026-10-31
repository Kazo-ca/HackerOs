> [English version](../../../en/wasm/dev/icon-packs.md)

# Packs d'icônes

Décision : [ADR 0203](adr/0203-icon-packs.md). Issue : [#255](https://github.com/Kazo-ca/HackerOs/issues/255).

## Rôle

Un pack d'icônes remplace les icônes des applications, des dossiers et des
fichiers par des images PNG. On le choisit dans **Paramètres ▸ Apparence ▸ Pack
d'icônes** ; le menu Démarrer, le bureau, File Explorer, les fenêtres, la barre
des tâches et l'accueil mobile changent tout de suite.

## Créer un pack

1. Créez un dossier, par exemple `~/.local/share/icons/mon-pack/` (pour vous) ou
   `/usr/share/icons/mon-pack/` (pour tous, avec `sudo`).
2. Ajoutez `icon-pack.json` :

   ```json
   { "id": "mon-pack", "name": "Mon pack", "version": "1.0.0", "author": "Moi", "description": "Icônes rondes" }
   ```

3. Ajoutez des PNG (64 × 64 conseillé, 256 Kio au plus chacun, noms en
   minuscules) :

   | Fichier | Remplace |
   | --- | --- |
   | `apps/<appId>.png` | une app, par exemple `apps/org.hackeros.terminal.png` |
   | `categories/<catégorie>.png` | les apps d'une catégorie (`developer`, `graphics`, `internet`, `system`, `utilities`) |
   | `files/folder.png` | tous les dossiers |
   | `files/<extension>.png` | les fichiers d'une extension, par exemple `files/txt.png` |
   | `files/file.png` | les autres fichiers |

4. Ouvrez Paramètres ▸ Apparence et choisissez le pack.

Le pack d'exemple `/usr/share/icons/neon/` sert de modèle : `cp -r
/usr/share/icons/neon ~/.local/share/icons/mon-pack` puis changez l'`id`.

Règles : PNG seulement (pas de SVG, qui peut contenir du script), 8 Mio et
1 024 images au plus par pack, un `id` déjà pris par un autre pack est refusé.
Les refus vont dans le journal système (`icons: …`) et Paramètres en affiche le
nombre.

## Architecture

| Élément | Projet | Rôle |
| --- | --- | --- |
| `IconPackLibrary`, `IconPack`, `IconPackInfo`, `IconPackFileAccess` | `HackerOs.Theming.Abstractions` | Lecture des dossiers par l'accès fichier de l'appelant, validation, URL `data:`, règles de recherche |
| `IconPackSettingsDocuments` | `HackerOs.Platform.Core` | Document `icons` (`iconPackId`, `builtin` par défaut) déclaré dans `OsSettingsDocumentRegistry` |
| `IconPackService` | `HackerOs.Platform.Core` | Pack choisi pour le shell, relu quand `icons.config` ou la session change, événement `Changed` |
| `BuiltInIconPacks`, `FileSystemSeeder` | `HackerOs.Platform.Core` | Pack « Néon » embarqué, copié dans `/usr/share/icons/neon/` en arrière-plan après la connexion (`InstallIconPacksAsync`, lancé une fois par démarrage par `LocalSessionService`) |
| `ShellAppIcon` | `HackerOs.Platform.Blazor` | Icône d'app du menu Démarrer et de l'accueil mobile |
| `WindowLaunchCoordinator.IconFor`, `SetWindowIconCommand` | `HackerOs.Platform.Blazor`, `HackerOs.Windowing.*` | Icône des fenêtres et de la barre des tâches, changée en direct |
| `ExplorerFileView` (partie `IconPack`), `IconPackShellIconProvider` | File Explorer | Fichiers, dossiers et raccourcis, lus avec les droits de l'app hôte par le SDK public |
| `IconPackPanel` | Paramètres | Liste des packs, aperçu, écriture de la couche choisie |

## Décisions

- Un pack est un dossier, pas une archive : on le crée avec `mkdir`, `cp` et
  Hack Paint. Les packs de thèmes de l'ADR 0190 apportent leur dossier `icons/`.
- Document `icons` séparé d'`appearance.config`, pour ne pas mêler le thème et
  les icônes (et ne pas changer le schéma d'`appearance`).
- Les symboles de l'interface ne sont pas dans un pack (Q2).

## Liste des tâches

Voir les tâches de l'[ADR 0203](adr/0203-icon-packs.md#tâches).
