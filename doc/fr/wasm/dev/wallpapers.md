> [English version](../../../en/wasm/dev/wallpapers.md)

# Fonds d'écran en image

Décisions : [ADR 0202](adr/0202-desktop-wallpaper-images.md) (issue
[#254](https://github.com/Kazo-ca/HackerOs/issues/254)) et
[ADR 0218](adr/0218-set-as-wallpaper-file-menu.md), « Définir comme fond
d'écran » dans le menu des images (issue
[#298](https://github.com/Kazo-ca/HackerOs/issues/298)).

## But

L'utilisateur choisit une image PNG, JPEG ou WebP comme fond du bureau, pour
tous les écrans ou pour un écran secondaire, dans Paramètres ▸ Apparence ▸
Fond d'écran. Sans image, le bureau garde le fond de son thème
(`--hos-desktop-background` et la grille).

## Réglages

Dans `appearance.config` (`/etc/hackeros/` puis `~/.config/hackeros/`) :

```ini
wallpaper = /home/alice/Images/plage.webp
wallpaperFit = fill
wallpaperScreen2 = builtin:nebula
```

| Clé | Valeur |
| --- | --- |
| `wallpaper` | vide (fond du thème), `builtin:<id>` ou chemin absolu ; écran 1 et écrans sans valeur propre |
| `wallpaperFit` | `fill` (défaut), `fit`, `stretch`, `center`, `tile` |
| `wallpaperScreen2` à `wallpaperScreen9` | valeur propre d'un écran ; vide = comme l'écran 1 |

Un `nano ~/.config/hackeros/appearance.config` change le fond à l'enregistrement.

## Sources des images

| Source | Emplacement |
| --- | --- |
| Livrées avec l'OS | `builtin:hacker-grid`, `builtin:circuit`, `builtin:aurora`, `builtin:nebula` (fichiers statiques `_content/HackerOs.Theming.Blazor/wallpapers/*.webp`) |
| Système | `/usr/share/hackeros/wallpapers/` |
| Utilisateur | `~/.local/share/hackeros/wallpapers/` |
| Packs installés | `/usr/share/hackeros/theme-packs/<pack>/wallpapers/`, `~/.local/share/hackeros/theme-packs/<pack>/wallpapers/` (ADR 0190) |
| Tout autre fichier | choisi avec « Parcourir… », chemin gardé tel quel |

## Architecture

| Élément | Projet | Rôle |
| --- | --- | --- |
| `WallpaperCatalog`, `WallpaperFit`, `BuiltInWallpaper` | `HackerOs.Theming.Abstractions` | Images livrées, dossiers, extensions, type lu dans les octets (`TryDetectMediaType`), limite de 16 Mo, valeurs de `wallpaperFit`. |
| `AppearanceSettingsDocuments` | `HackerOs.Platform.Core` | Déclare les clés `wallpaper`, `wallpaperFit`, `wallpaperScreen2..9`. |
| `WallpaperService`, `WallpaperPreferences`, `WallpaperImage` | `HackerOs.Platform.Core` | Suit `appearance.config`, lit un fichier en tant qu'utilisateur de la session, refuse ce qui n'est pas une image, garde une copie par fichier affiché. |
| `DesktopWallpaper` (`.razor`, `.razor.cs`, `.razor.css`, `.razor.js`) | `HackerOs.Platform.Blazor` | Dessine l'image d'un écran ; adresse `blob:` pour un fichier, adresse statique pour une image livrée ; classes `fit-*`. |
| `DesktopArea.WallpaperContent` | `HackerOs.Windowing.Blazor` | Couche entre le fond du thème et les icônes. `DesktopShell` la remplit pour l'écran 1 et chaque écran secondaire. |
| `WallpaperPanel` | `HackerOs.Apps.Settings` | Vignettes, « Parcourir… », « Ajustement », choix de l'écran ; SDK public seulement. |

## « Définir comme fond d'écran » dans les menus des fichiers

Le menu d'une image PNG, JPEG ou WebP (explorateur, bureau, dialogues de
fichiers) propose « Définir comme fond d'écran » juste après « Ouvrir avec »
(ADR 0218) :

- avec un seul écran ouvert, une entrée simple : l'image va dans `wallpaper`
  (tous les écrans sans image propre) ;
- avec plusieurs écrans, un sous-menu « Écran 1 et les autres », puis
  « Écran 2 »… pour chaque écran secondaire ouvert (`wallpaperScreen<n>`).

Les entrées appartiennent à `org.hackeros.settings.wallpaper`
(`SetWallpaperApp`), un service caché déclaré par le projet Paramètres et
exécuté sur le fil principal. `SetWallpaperService` lit le fichier en tant
qu'utilisateur de la session, vérifie ses octets et sa taille comme le shell,
écrit la couche utilisateur d'`appearance.config`, publie une notification
« Fond d'écran changé » (ou l'erreur : clé verrouillée, fichier illisible, pas
une image), puis s'arrête. Aucune fenêtre ne s'ouvre ; le chemin est gardé tel
quel.

## Points d'attention

- Le shell ne fait jamais confiance au nom du fichier : un fichier qui n'est
  pas PNG, JPEG ou WebP, trop gros, absent ou illisible laisse le fond du
  thème et écrit un avertissement `wallpaper:` dans le journal.
- Les adresses d'images sont rendues absolues dans `DesktopWallpaper.razor.js`
  pour qu'un écran secondaire (autre document) les charge aussi.
- Le panneau lit au plus 60 images pour leurs vignettes.

## Tests

- `Tests/HackerOs.Platform.Core.Tests/Appearance/WallpaperTests.cs` : règles du
  catalogue, schéma, service (image livrée, fichier, écran propre, repli).
- `Tests/HackerOs.Apps.Settings.Tests/WallpaperPanelTests.cs` : panneau
  (vignettes, choix par écran, « Parcourir… », ajustement, français).
- `Tests/HackerOs.Apps.Settings.Tests/SetWallpaperServiceTests.cs` : entrées
  de menu, écran de chaque entrée, clé écrite, fichier refusé, clé verrouillée,
  notification.
- `Tests/HackerOs.Platform.Blazor.Tests/ContextMenus/FileMenuEntriesTests.cs` :
  filtre par écran (entrée simple ou sous-menu des écrans ouverts).

## Hors de ce chantier

Fond d'écran du mobile : issue `TODO` #297 (ADR 0202 §6). « Définir comme fond
d'écran » (#298) est fait par l'ADR 0218.
