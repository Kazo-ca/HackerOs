> [English version](../../../../en/wasm/dev/adr/0213-theme-packs-build.md)

# ADR 0213 : packs de thèmes `.hacktheme` et onglet Thèmes du Hack Store, mise en œuvre

## Statut

Accepté le 2026-10-31, chantier `todo/18-theme-packs` (issue `TODO`
[#253](https://github.com/Kazo-ca/HackerOs/issues/253), Hack Store v2). Met en
œuvre l'[ADR 0190](0190-theme-packs.md) §1 à §3, sur le Store de
l'[ADR 0212](0212-hack-store-app-build.md) et les règles de confiance de
l'[ADR 0189](0189-app-sources-trust-and-per-user-install.md) et de
l'[ADR 0208](0208-hack-store-sources-trust-build.md). S'appuie sur les systèmes
des ADR [0202](0202-desktop-wallpaper-images.md) (fonds d'écran),
[0203](0203-icon-packs.md) (icônes), [0204](0204-os-event-sounds.md) (sons) et
[0205](0205-cursor-themes.md) (curseurs).

- **Précise :** ADR 0190 §1 (forme exacte des dossiers `icons/`, `sounds/` et
  `cursors/`, qui reprennent les formats des ADR 0203 à 0205).
- **Remplace :** ADR 0205 §6 (l'installation ne **copie pas** le dossier
  `cursors/` dans `/etc/hackeros/cursors/` ou `~/.config/hackeros/cursors/` :
  il est lu là où le pack est déballé, §3 ci-dessous).

Quatre questions ont été posées à Benoit (Q1 à Q4 ci-dessous) ; les
recommandations sont appliquées en attendant ses réponses.

## Contexte

L'ADR 0190 décrit le pack de thèmes et décide qu'il s'installe pour soi sans
administrateur ou pour le système avec la validation en deux étapes. Les
quatre systèmes qu'elle demandait existent depuis la série `todo/base` :
fonds d'écran (0202), packs d'icônes (0203), sons (0204) et curseurs (0205).
Les fonds d'écran et les icônes lisent déjà
`/usr/share/hackeros/theme-packs/<pack>/` et
`~/.local/share/hackeros/theme-packs/<pack>/`. Il restait à fixer la forme
exacte de l'archive, ses vérifications, la façon de l'écrire sur le disque,
la façon dont les thèmes, sons et curseurs la lisent, et l'onglet du Store.

## Décision

### 1. Le format `.hacktheme`

Une archive zip de 30 Mio au plus et de 2 000 fichiers au plus :

```text
hackeros-theme-pack.json      schemaVersion 1, id, version, name, author?, description?,
                              files { "chemin": "sha256" } (chaque fichier, sauf lui-même)
preview.png                   aperçu, PNG, 1 Mio au plus (facultatif)
themes/<id>.theme             thèmes de l'ADR 0103, 32 au plus, sans erreur de lecture
wallpapers/*.png|jpg|jpeg|webp  fonds de l'ADR 0202, 5 Mio chacun, 32 au plus
icons/icon-pack.json          pack d'icônes de l'ADR 0203
icons/{apps,categories,files}/*.png
sounds/index.theme            jeu de sons de l'ADR 0204 (Directories=)
sounds/<dossier>/<nom>.oga|ogg|mp3   1 Mio chacun
cursors/cursor.theme          thème de curseurs de l'ADR 0205
cursors/*.png                 images nommées par cursor.theme, point actif vérifié
```

- L'identifiant suit la règle des apps (`org.example.neon`) et celle des jeux
  de sons (le jeu de sons d'un pack porte l'identifiant du pack).
- Chaque fichier de l'archive est listé dans `files` avec son SHA-256, et chaque
  fichier listé existe et correspond. Un fichier en trop est refusé.
- Les noms sont en minuscules, sans `..` ni chemin absolu.
- Le **type** de chaque image et de chaque son est lu dans ses premiers octets,
  jamais dans son nom : un `.png` qui contient un SVG ou un script est refusé.
  Aucun SVG, aucun fichier d'une autre sorte (`.js`, `.css`, `.html`…).
- Les fichiers texte (`.theme`, `.json`, `index.theme`, `cursor.theme`) sont en
  UTF-8 et ne contiennent ni `://`, ni `url(`, ni `@import` : aucune adresse
  distante.
- Les erreurs ont un code `themes.pack.<raison>` (`too-large`,
  `no-description`, `unlisted-file`, `missing-file`, `hash-mismatch`, `empty`,
  `unexpected-file`, `bad-file`, `file-too-large`, `remote-address`,
  `unsupported`, `bad-description`) que le Store traduit.

Le code est `ThemePack.Read` dans `HackerOs.Platform.Core` (Apps/ThemePacks).
La CI du catalogue appliquera les mêmes règles (Q2).

### 2. Dans le catalogue

Une entrée de `catalog.json` porte `kind: "theme-pack"` (une entrée sans
`kind` reste une app). Ses versions pointent vers un `.hacktheme` de 30 Mio au
plus. Un identifiant de pack invalide ou une sorte inconnue est ignoré avec un
avertissement. `AppCatalogListing.Find(appId)` ne trouve que les apps ;
`Find(id, kind)` choisit la sorte. La source d'un pack installé est
`catalog:<nom>/<packId>@<version>`, comme une app (ADR 0212 §1).

### 3. Déballé tel quel, lu en place (Q1 A)

- Pour soi : `~/.local/share/hackeros/theme-packs/<packId>/`, écrit comme
  l'utilisateur, sans administrateur, depuis un catalogue de confiance.
- Pour le système : `/usr/share/hackeros/theme-packs/<packId>/`, après la
  validation en deux étapes (connexion fraîche d'un administrateur puis
  fenêtre de validation, ADR 0189 §4), écrit comme le système. Une source non
  fiable passe toujours par les deux étapes ; un pack bloqué par
  `[blocked-apps]` (ou sa catégorie) ne s'installe que pour le système
  (ADR 0208).
- L'écriture est atomique : le pack est écrit dans
  `<racine>.partial/<packId>`, l'ancienne copie est renommée
  `<packId>.old`, la nouvelle prend sa place, l'ancienne est effacée en
  dernier. Une mise à jour ne laisse donc aucun fichier de l'ancienne version.
- Chaque dossier contient un relevé `.hackeros-install` (`clé=valeur` : id,
  nom, version, source, parties, installé par) qui sert à lister les packs.
- Une installation pour le système enlève la copie personnelle du même pack
  (une copie à la fois, ADR 0189 §3).
- Les catalogues lisent les dossiers des packs **là où ils sont**, dans
  l'ordre système puis personnel :
  - thèmes : `/etc/hackeros/themes/`, `theme-packs/*/themes/` du système,
    `~/.config/hackeros/themes/`, `theme-packs/*/themes/` de l'utilisateur
    (`UserThemeLibrary.LoadAsync` reçoit une fonction qui liste les dossiers) ;
  - curseurs : `/etc/hackeros/cursors/`, `theme-packs/*/cursors/` du système,
    `~/.config/hackeros/cursors/`, puis ceux de l'utilisateur ;
  - sons : le jeu `<packId>` est cherché dans `/usr/share/sounds/<id>`,
    `/usr/share/hackeros/theme-packs/<id>/sounds`, `~/.local/share/sounds/<id>`
    puis `~/.local/share/hackeros/theme-packs/<id>/sounds` ;
  - fonds d'écran et icônes : déjà lus par les ADR 0202 et 0203.
- Un identifiant de thème ou de curseur qui reprend un identifiant existant
  est refusé, comme pour les fichiers de l'ADR 0103 (pas de masquage).
- Les paramètres d'apparence surveillent aussi les dossiers des packs : un pack
  installé apparaît dans Paramètres sans redémarrer.

### 4. SDK

`HackAppPackages` reçoit `GetInstalledThemePacksAsync`,
`InstallThemePackAsync(source, scope)` et `RemoveThemePackAsync(packId, scope)`,
avec les types `HackInstalledThemePack`, `HackThemePackInstallReport`,
`HackThemePackParts` et `HackCatalogItemKind`, et `HackCatalogApp.Kind`. Les
nouvelles méthodes de `IAppManagementGateway` ont une implémentation par
défaut qui lève `NotSupportedException` : un hôte plus ancien reste
compatible. Tout est dans `PublicAPI.Unshipped.txt` pour la prochaine mineure.
Un pack ne contient aucun code : `RestartRequired` vaut toujours `false`.

### 5. L'onglet Thèmes du Store

- Un onglet **Thèmes** à côté d'Accueil, Installées et Mises à jour
  (`--page=themes` l'ouvre directement). Les apps ne montrent plus les
  entrées `theme-pack`.
- Une carte par pack : aperçu 16:9 (ou une palette de couleurs sans aperçu),
  nom, confiance, version, taille, parties contenues (thèmes, fonds, icônes,
  sons, curseurs) et état.
- Boutons : « Installer pour moi », « Installer pour tous » (deux étapes),
  « Mettre à jour », « Ouvrir Apparence » et « Supprimer ». La recherche filtre
  les packs dans l'onglet.
- Quand plusieurs catalogues listent le même pack, le premier dans l'ordre de
  l'OS gagne, sauf si le pack a été installé depuis un autre.
- Un pack installé qu'aucun catalogue ne liste plus reste visible pour pouvoir
  le supprimer.

### 6. Après l'installation (Q3 A) et mises à jour (Q4 A)

Installer un pack n'applique rien : le Store affiche « Installé » et le bouton
« Ouvrir Apparence » ouvre Paramètres ▸ Apparence, où l'utilisateur choisit
le thème, le fond, les icônes, les sons ou les curseurs du pack. Les mises à
jour des packs se voient dans l'onglet Thèmes seulement, pas dans le badge des
apps (`AppUpdateMonitor`).

## Conséquences

- Un pack est installable dès qu'un catalogue le publie ; la CI du catalogue
  doit encore vérifier `kind: "theme-pack"` (Q2).
- `AppPackageInstaller` devient une classe partielle ;
  `AppPackageInstaller.ThemePacks.cs` porte l'installation des packs et
  `AppManagementGateway.ThemePacks.cs` sa passerelle.
- Un thème, un curseur ou un son d'un pack supprimé disparaît des listes ;
  le réglage qui le nommait revient au défaut, comme pour un fichier effacé.

## Questions posées à Benoit

1. **Q1, où vivent les fichiers d'un pack.** A (recommandé) : déballé tel quel
   dans `theme-packs/<id>/` et lu en place par chaque catalogue. B : copier
   chaque partie dans les dossiers existants (`/etc/hackeros/cursors/`…).
   A garde une suppression propre et une mise à jour atomique.
2. **Q2, CI du catalogue.** A (recommandé) : issue `TODO` sur
   `HackerOs-AppMarketPlace` pour vérifier `kind: "theme-pack"`, à faire quand
   `todo/base` est sur `main`. B : la faire dans ce chantier.
3. **Q3, après l'installation.** A (recommandé) : rien n'est appliqué, bouton
   « Ouvrir Apparence ». B : appliquer tout le pack d'un coup.
4. **Q4, mises à jour des packs dans le badge.** A (recommandé) : non, onglet
   Thèmes seulement. B : oui, comme les apps.

## Tâches

- [x] Format `.hacktheme` et vérifications à l'installation (`ThemePack`).
- [x] `kind: "theme-pack"` dans `catalog.json`.
- [x] Installation pour soi et pour le système, mise à jour atomique,
      suppression, relevé `.hackeros-install`.
- [x] Thèmes, curseurs et sons lus dans les dossiers des packs.
- [x] SDK `HackAppPackages` et passerelle.
- [x] Onglet Thèmes du Hack Store.
- [x] Tests : format, passerelle, dossiers des packs, onglet du Store.
- [ ] CI du catalogue pour `kind: "theme-pack"` (Q2, issue `TODO` sur
      `HackerOs-AppMarketPlace`).
