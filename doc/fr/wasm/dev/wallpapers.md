> [English version](../../../en/wasm/dev/wallpapers.md)

# Fonds d'écran en image

Décisions : [ADR 0202](adr/0202-desktop-wallpaper-images.md) (bureau),
[ADR 0217](adr/0217-mobile-home-wallpaper.md) (écran d'accueil mobile) et
[ADR 0218](adr/0218-set-as-wallpaper-file-menu.md) (« Définir comme fond
d'écran » dans le menu des images). Issues
[#254](https://github.com/Kazo-ca/HackerOs/issues/254),
[#297](https://github.com/Kazo-ca/HackerOs/issues/297) et
[#298](https://github.com/Kazo-ca/HackerOs/issues/298).

## But

L'utilisateur choisit une image PNG, JPEG ou WebP comme fond du bureau, pour
tous les écrans ou pour un écran secondaire, dans Paramètres ▸ Apparence ▸
Fond d'écran. Sans image, le bureau garde le fond de son thème
(`--hos-desktop-background` et la grille).

L'écran d'accueil mobile a sa propre image et son propre ajustement (choix
« Mobile » du même panneau) ; sans image, il garde le fond de son thème mobile
(`--hos-mobile-surface-background`).

## Réglages

Dans `appearance.config` (`/etc/hackeros/` puis `~/.config/hackeros/`) :

```ini
wallpaper = /home/alice/Images/plage.webp
wallpaperFit = fill
wallpaperScreen2 = builtin:nebula
mobileWallpaper = builtin:aurora
mobileWallpaperFit = fill
```

| Clé | Valeur |
| --- | --- |
| `wallpaper` | vide (fond du thème), `builtin:<id>` ou chemin absolu ; écran 1 et écrans sans valeur propre |
| `wallpaperFit` | `fill` (défaut), `fit`, `stretch`, `center`, `tile` |
| `wallpaperScreen2` à `wallpaperScreen9` | valeur propre d'un écran ; vide = comme l'écran 1 |
| `mobileWallpaper` | même forme que `wallpaper`, pour l'écran d'accueil mobile ; vide = fond du thème mobile |
| `mobileWallpaperFit` | ajustement sur mobile, mêmes valeurs que `wallpaperFit` |

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
| `AppearanceSettingsDocuments` | `HackerOs.Platform.Core` | Déclare les clés `wallpaper`, `wallpaperFit`, `wallpaperScreen2..9`, `mobileWallpaper`, `mobileWallpaperFit`. |
| `WallpaperService`, `WallpaperPreferences`, `WallpaperImage` | `HackerOs.Platform.Core` | Suit `appearance.config`, lit un fichier en tant qu'utilisateur de la session, refuse ce qui n'est pas une image, garde une copie par fichier affiché. `GetImageAsync(écran)` pour le bureau, `GetMobileImageAsync()` pour le mobile. |
| `DesktopWallpaper` (`.razor`, `.razor.cs`, `.razor.css`, `.razor.js`) | `HackerOs.Platform.Blazor` | Dessine l'image d'un écran ; adresse `blob:` pour un fichier, adresse statique pour une image livrée ; classes `fit-*`. Avec `Mobile="true"`, l'image du mobile ; `MobileShell` la place derrière l'écran d'accueil, qui devient transparent et ombre ses textes. |
| `DesktopArea.WallpaperContent` | `HackerOs.Windowing.Blazor` | Couche entre le fond du thème et les icônes. `DesktopShell` la remplit pour l'écran 1 et chaque écran secondaire. |
| `WallpaperPanel` | `HackerOs.Apps.Settings` | Vignettes, « Parcourir… », « Ajustement », choix de l'écran ou « Mobile » ; SDK public seulement. |

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
  catalogue, schéma, service (image livrée, fichier, écran propre, mobile, repli).
- `Tests/HackerOs.Apps.Settings.Tests/WallpaperPanelTests.cs` : panneau
  (vignettes, choix par écran ou pour le mobile, « Parcourir… », ajustement, français).
- `Tests/HackerOs.Apps.Settings.Tests/SetWallpaperServiceTests.cs` : entrées
  de menu, écran de chaque entrée, clé écrite, fichier refusé, clé verrouillée,
  notification.
- `Tests/HackerOs.Platform.Blazor.Tests/ContextMenus/FileMenuEntriesTests.cs` :
  filtre par écran (entrée simple ou sous-menu des écrans ouverts).

## Hors de ce chantier

Le tiroir d'apps mobile reste opaque (ADR 0217 §3). « Définir comme fond
d'écran » n'écrit que les clés du bureau : l'image du mobile se choisit dans
Paramètres (le SDK ne dit pas à une app quelle interface est active).
