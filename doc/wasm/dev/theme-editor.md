# Thèmes utilisateur et éditeur de thèmes

Chantier `v3/11-apps`, [ADR 0103](adr/0103-hack-paint-image-files-and-theme-editor.md)
§2 (complète l'ADR 0041). Porté depuis `src/apps/theme-editor.ts`, sans le CSS
libre (`customCss`) de l'ancienne app.

## Objet

L'utilisateur crée ses propres thèmes : un fichier `.theme` qui part d'un thème
intégré (`base`) et remplace quelques **jetons** CSS vérifiés (`--hos-*`). Le
fichier est un vrai fichier de l'OS : on le crée avec l'éditeur de thèmes, ou
avec `nano`, on le copie avec `cp`, on l'envoie, on l'ouvre par l'explorateur.
Paramètres le propose à côté des thèmes intégrés.

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

## Architecture

| Pièce | Projet | Rôle |
|---|---|---|
| `ThemeTokenCatalog`, `ThemeTokenValidator` | `Shared/HackerOs.Theming.Abstractions` | liste et vérification des jetons |
| `ThemeFile` | idem | `Parse` (tolérant) et `Serialize` du format `.theme` |
| `UserTheme`, `UserThemeLibrary` | idem | thème lu ; lecture des deux dossiers par délégués (`LoadAsync`), règles de non-masquage (`Build`) |
| `ResolvedTheme.Resolve` | idem | thème réellement dessiné pour un id et une forme (bureau ou mobile) |
| `ThemeScope`, `ThemePreview` (paramètre `Tokens`) | `Platform/HackerOs.Theming.Blazor` | appliquent les jetons par `style.setProperty` sur leur élément racine, via `wwwroot/themeTokens.js` (seules les propriétés `--hos-*` sont touchées, et seulement les différences) |
| `UserThemeCatalogService` | `Platform/HackerOs.Platform.Core/Appearance` | catalogue vivant du shell : lit `/etc` comme le système et `~` comme l'utilisateur, surveille les deux dossiers (`SettingsFilesWatcher.ForDirectories`), relit au changement de session, journalise chaque avertissement une fois |
| `App.razor` | `OS/HackerOs.Ecosystem` | `ThemeScope ThemeId=@ActiveTheme.BaseThemeId Tokens=@ActiveTheme.UserTheme?.Tokens` |
| Theme Editor | `Apps/System/HackerOs.Apps.ThemeEditor` | l'app |
| Paramètres | `Apps/System/HackerOs.Apps.Settings` | thèmes utilisateur dans les listes bureau et mobile, bouton « Open the Theme Editor » |

### Résolution d'un id

`appearance.config` garde `desktopThemeId` et `mobileThemeId` ; depuis v3/11
ce sont des chaînes libres (plus une énumération, `schemaVersion` reste 2).

1. Un thème intégré de la bonne forme : il s'applique.
2. Un thème utilisateur proposé sur cette forme : son thème de base s'applique,
   puis ses jetons par-dessus. Si sa base vise l'autre forme (un thème basé sur
   `hackeros` proposé aussi sur mobile), ses jetons s'appliquent sur le thème
   par défaut de la forme active.
3. Sinon (id inconnu, fichier supprimé ou refusé, mauvaise forme) : le thème par
   défaut de la forme, comme avant.

## L'éditeur de thèmes

App système `org.hackeros.theme-editor`, sur le SDK public seulement (ADR 0055).
Capacités : dossier personnel en lecture et écriture, dialogues ouvrir et
enregistrer, et `filesystem.system.read` (dangereuse, accordée aux apps
système) pour lister `/etc/hackeros/themes`. Fenêtre 1180 × 760, minimum
820 × 540 (ADR 0095).

- **Barre d'outils** : *New from* (thème de base), *Open…*, *Save* (vers
  `~/.config/hackeros/themes/<id>.theme`, dossier créé au besoin), *Export…*
  (n'importe où par le dialogue), *Use on desktop* / *Use on mobile* (écrit
  l'id dans la couche utilisateur de `appearance` par
  `HackSettings.OpenSystemAsync`).
- **Colonne de gauche** : nom, id (suit le nom tant qu'il n'est pas modifié à
  la main), thème de base, formes proposées, chemin ; liste des thèmes
  installés et avertissements de leurs fichiers.
- **Centre** : un onglet par groupe de jetons (MudTabs), un champ typé par
  jeton (sélecteur de couleur quand la valeur est convertible), erreur sous le
  champ ; onglet Documentation.
- **Droite** : aperçu en direct, bureau ou mobile, dans un `ThemeScope`
  imbriqué (fenêtre, barre des tâches, boutons, champs) et une carte
  `ThemePreview`.
- Une valeur invalide reste affichée pour être corrigée, mais n'entre ni dans
  l'aperçu ni dans le fichier, et bloque l'enregistrement avec la liste des
  problèmes.
- Fermer avec des changements non enregistrés demande confirmation
  (`IWindowCloseGuard`) ; revenir sur la fenêtre relit la liste des thèmes.
- Un `.theme` ouvert par l'explorateur (« Ouvrir avec ») ou passé en argument
  au lancement est chargé au démarrage.

### Langues (ADR 0104)

Barre d'outils, panneau d'identité, onglets, aperçu, champs de jeton,
documentation et messages viennent de `Resources/Strings.resx` /
`Strings.fr.resx`. `ThemeEditorDocument` reçoit le localiseur de la fenêtre
(nom par défaut « Mon … », problèmes qui empêchent l'enregistrement). Les
phrases de la documentation contiennent du `<code>` et sont rendues comme
balisage depuis les ressources de l'assembly (jamais depuis un fichier), d'où
le `::deep code` de `ThemeTokenDocumentation.razor.css`.

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

## Décisions clés

- Les fichiers sont lus et écrits par la passerelle de fichiers de l'app
  (`IAppFileSystemGateway`), comme l'éditeur de texte : un fichier choisi dans
  un dialogue hors du dossier personnel n'est accessible que par le jeton que
  le dialogue a remis, ce que `HackFile` ne sait pas porter. C'est du C# ; le
  JS ne touche jamais aux fichiers (ADR 0103 §1 et §2).
- La lecture des dossiers est partagée par délégués (`UserThemeLibrary.LoadAsync`)
  pour que le shell (passerelles de l'OS) et les apps (SDK) utilisent les mêmes
  règles sans nouvelle dépendance.
- Le service du shell lit `~` avec les droits de l'utilisateur : un fichier
  illisible (mode `0600` d'un autre) est simplement ignoré.

## Tâches

- [x] Liste des jetons, types et vérification (`ThemeTokenCatalog`, `ThemeTokenValidator`).
- [x] Format `.theme` : lecture tolérante, écriture, 64 Kio, diagnostics.
- [x] Bibliothèque : `/etc` et `~`, pas de masquage, résolution par forme.
- [x] `ThemeScope`/`ThemePreview` appliquent des jetons par `style.setProperty`.
- [x] `UserThemeCatalogService` : lecture, surveillance des dossiers, changement de session.
- [x] `appearance.config` : ids de thème en chaînes libres.
- [x] App Theme Editor : édition par groupes, aperçu, documentation, enregistrer, exporter, utiliser.
- [x] Paramètres : thèmes utilisateur listés, lien vers l'éditeur.
- [x] Tests : format, jetons refusés, non-masquage, résolution, thème appliqué
  après rechargement, modèle de l'éditeur (`Platform.Core.Tests/Appearance`,
  `HackerOs.Apps.ThemeEditor.Tests`).
- [ ] Scans axe de l'éditeur : chantier `v3/13`.
