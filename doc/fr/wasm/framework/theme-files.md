> [English version](../../../en/wasm/framework/theme-files.md)

# Fichiers de thème `.theme`

Ce document décrit le format du fichier `.theme`, la liste des jetons et l'API de lecture des thèmes pour les auteurs de thèmes et les outils. L'éditeur de thèmes, la résolution d'un identifiant de thème et la mise en œuvre sont dans [`../dev/theme-editor.md`](../dev/theme-editor.md).

## Fichier `.theme`

Format des réglages (ADR 0044), deux sections :

```ini
# HackerOS theme (ADR 0103).
[theme]
id = neon-crypt
name = Neon Crypt
base = hackeros
platforms = desktop, mobile

[tokens]
--hos-bg-base = #0b0f0b
--hos-accent = #39ff14
--hos-font-mono = "JetBrains Mono", monospace
```

- `id` : 1 à 64 minuscules, chiffres et tirets ; c'est aussi le nom du fichier
  et la valeur écrite dans `appearance.config`.
- `name` : 1 à 80 caractères. `base` : un thème du catalogue (`ThemeCatalog`).
- `platforms` : `desktop`, `mobile` ou les deux ; absent, celui du thème de base.
- Taille maximale : 64 Kio.
- **Lecture tolérante** (ADR 0044 §5) : un jeton inconnu, une valeur invalide ou
  une ligne mal formée est ignoré avec un avertissement, le reste s'applique.
  Seule une identité invalide (`id`, `name`, `base`) refuse le thème entier.
- Média `text/x-hackeros-theme` : l'explorateur ouvre un `.theme` dans
  l'éditeur de thèmes.

### Emplacements

| Dossier | Lu comme | Pour |
|---|---|---|
| `/etc/hackeros/themes/` | le système | l'administrateur, pour toute la machine |
| `~/.config/hackeros/themes/` | l'utilisateur de la session (les bits de mode décident) | l'utilisateur |

Pas de masquage : un fichier dont l'`id` reprend un thème intégré, ou un `id`
déjà pris par un autre fichier (celui de `/etc` gagne), est refusé avec une
erreur dans le journal.

## Jetons

`ThemeTokenCatalog` (`HackerOs.Theming.Abstractions`) liste les 68 jetons
modifiables, chacun avec un type, un groupe, un libellé, une description et un
exemple. L'onglet **Documentation** de l'éditeur est généré depuis cette liste.

| Type | Valeurs acceptées (`ThemeTokenValidator`) |
|---|---|
| Couleur | `#rgb`, `#rgba`, `#rrggbb`, `#rrggbbaa`, `rgb()`/`rgba()`/`hsl()`/`hsla()` à nombres et pourcentages seulement, nom de couleur CSS |
| Longueur | nombre (éventuellement négatif) avec `px`, `rem`, `em`, `%`, `vh`, `vw`, ou `0` |
| Durée | `150ms`, `0.2s`, suivi au choix de `ease`, `linear`, `ease-in`, `ease-out`, `ease-in-out` |
| Famille de polices | noms séparés par des virgules (entre guillemets ou non) |
| Nombre | entier ou décimal |

Tout le reste est refusé, en particulier `url(`, `var(`, `calc(`,
`expression(`, `;`, `{`, `}` : un thème ne contient que des données (ADR 0103
§2.4). Une valeur fait au plus 200 caractères.

| Groupe | Jetons |
|---|---|
| Couleurs | `--hos-bg-base`, `-surface`, `-elevated`, `-overlay` ; `--hos-text-primary`, `-secondary`, `-muted`, `-inverse` ; `--hos-border-subtle`, `--hos-border`, `--hos-border-strong`, `--hos-focus-ring` ; `--hos-accent`, `-hover`, `-subtle`, `-glow` ; `--hos-success`, `--hos-warning`, `--hos-error`, `--hos-info` ; `--hos-desktop-background`, `--hos-desktop-grid-opacity` |
| Typographie | `--hos-font-ui`, `--hos-font-mono`, `--hos-titlebar-font-size`, `--hos-titlebar-font-weight`, `--hos-taskbar-font-size`, `--hos-btn-font-weight` |
| Espacements | `--hos-radius-sm`, `-md`, `-lg`, `--hos-btn-radius`, `--hos-input-radius`, `--hos-control-height` |
| Fenêtres | `--hos-window-background`, `-content-background`, `-text`, `-focused-border`, `-radius` ; `--hos-titlebar-height`, `-background`, `-focused-background`, `-text`, `-focused-text` ; `--hos-window-control-size`, `-radius`, `-background`, `-hover-background`, `-color` ; `--hos-window-close-background`, `-hover-background`, `-color` |
| Barre des tâches | `--hos-taskbar-height`, `-gap`, `-background`, `-button-background`, `-button-hover-background`, `-button-active-background`, `-button-color`, `-button-active-color`, `-button-radius`, `-active-indicator`, `-launcher-background`, `-launcher-color` ; `--hos-launcher-background`, `--hos-launcher-sidebar-background` |
| Animations | `--hos-transition-fast`, `--hos-transition-normal` (ignorés quand les animations sont coupées) |

Les couleurs `accent`, `text-primary`, `text-secondary`, `text-muted`,
`border`, `success`, `warning`, `error` et `info` reçoivent aussi leur
compagnon `--hos-…-rgb` (« r, g, b ») calculé, utilisé par les styles qui
écrivent `rgba(var(--hos-accent-rgb), .2)`.

## API de lecture

- `ThemeFile.Parse(texte, origine, chemin)` lit un fichier `.theme` de façon tolérante et rend `ThemeFileParseResult` (thème et diagnostics) ; `ThemeFile.Serialize(UserTheme)` l'écrit.
- `ThemeFile.IsValidId`, `ThemeFile.Extension` (`.theme`), `ThemeFile.MediaType` (`text/x-hackeros-theme`), `ThemeFile.MaxFileBytes` (64 Kio).
- `ThemeTokenCatalog` liste les jetons ; `ThemeTokenValidator.TryValidate(type, valeur, culture, out normalisée, out erreur)` vérifie une valeur.
- `UserThemeLibrary.LoadAsync` lit les dossiers de thèmes par délégués.

## Messages localisés du paquet Theming.Abstractions

`HackerOs.Theming.Abstractions` a ses propres `Resources/Strings.resx` /
`Strings.fr.resx`, lus par un simple `ResourceManager` (le paquet reste sans
dépendance HackerOS) avec la culture que l'appelant passe : une app donne
`AppContext.Culture` (l'éditeur la tire de son localiseur, `Strings.CultureOf(L)`),
la plateforme `SessionLocaleService.Culture`, et `null` donne l'anglais.
Les propriétés `Label`, `Description` et `Message` restent en anglais ; les
traductions passent par :

- `ThemeTokenDefinition.GetLabel(culture)` / `GetDescription(culture)` (clé
  `Token.BgBase.Label` pour `--hos-bg-base`) ;
- `ThemeTokenValidator.TryValidate(type, valeur, culture, out normalisée, out erreur)` ;
- `ThemeFileDiagnostic.GetMessage(culture)` et `ToString(culture)`
  (`chemin:ligne: avertissement: …`). Un diagnostic produit par `ThemeFile` ou
  `UserThemeLibrary` garde sa clé et ses arguments, il se lit donc dans la
  langue de celui qui l'affiche. `ToString()` reste en anglais : c'est la
  forme écrite dans les journaux par `UserThemeCatalogService`.

Ne sont jamais traduits : les noms des thèmes intégrés (`ThemeCatalog`,
des noms propres : HackerOS, Windows 98, macOS…), les noms de jetons, les ids
et le contenu d'un fichier `.theme` (clés, valeurs, `platforms = desktop, mobile`,
commentaires d'en-tête écrits par `Serialize`).
