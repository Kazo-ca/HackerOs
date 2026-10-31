> [English version](../../../en/wasm/dev/wallpapers.md)

# Fonds d'écran en image

Décisions : [ADR 0202](adr/0202-desktop-wallpaper-images.md) (bureau),
[ADR 0217](adr/0217-mobile-home-wallpaper.md) (écran d'accueil mobile). Issues
[#254](https://github.com/Kazo-ca/HackerOs/issues/254) et
[#297](https://github.com/Kazo-ca/HackerOs/issues/297).

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

## Hors de ce chantier

« Définir comme fond d'écran » dans l'explorateur : issue `TODO` #298
(ADR 0202 §6). Le tiroir d'apps mobile reste opaque (ADR 0217 §3).
