> [English version](../../../../en/wasm/dev/adr/0203-icon-packs.md)

# ADR 0203 : packs d'icônes

## Statut

Accepté le 2026-10-30. Chantier 9 du plan du Hack Store, issue
[#255](https://github.com/Kazo-ca/HackerOs/issues/255), série `todo/base`
(branche `todo/7-icon-packs`). Questions Q1 à Q4 posées à Benoit dans le fil
« Ajouter les packs d'icônes » ; les recommandations sont prises tant qu'il ne
répond pas autrement.

- **Réalise :** [ADR 0190](0190-theme-packs.md) §4, point 2 (packs d'icônes),
  avant que le Store accepte les icônes dans un pack de thèmes.
- **Complète :** [ADR 0026](0026-icon-library-support.md) (icônes vectorielles
  intégrées) et [ADR 0164](0164-app-icons-and-window-icon-context-menu.md)
  (icône déclarée d'une app, fenêtre et barre des tâches) ; leurs décisions ne
  changent pas : un pack ne fait que passer avant elles.

## Contexte

- Les icônes d'apps viennent de deux sources : l'image déclarée par l'app
  (`presentation.iconAssetPaths`, ADR 0164) pour la fenêtre, la barre des tâches
  et l'accueil mobile, et une icône Lucide choisie par `HackAppIcons` selon l'id
  ou la catégorie pour le menu Démarrer et les raccourcis du bureau.
- Les fichiers et dossiers sont dessinés par `IShellIconProvider`
  (`DefaultShellIconProvider`, icônes Lucide par extension) dans le contrôle de
  fichiers de File Explorer.
- L'ADR 0190 §3 impose des packs sans code : PNG, JPEG ou WebP, pas de SVG.

## Décision

### 1. Le format d'un pack

Un pack d'icônes est un **dossier** qui contient `icon-pack.json` et des PNG
nommés par convention, comme un thème d'icônes freedesktop :

```text
icon-pack.json                 {"id","name","version?","author?","description?"}
apps/<appId>.png               une app (apps/org.hackeros.terminal.png)
categories/<catégorie>.png     les apps d'une catégorie (categories/developer.png)
files/folder.png               tous les dossiers
files/file.png                 un fichier dont l'extension n'a pas d'image
files/<extension>.png          files/txt.png, files/zip.png…
```

- `id` : 1 à 64 caractères `a-z 0-9 . - _`, jamais `builtin` ; `name` : 64
  caractères au plus.
- **PNG seulement** (signature vérifiée), noms en minuscules. Un SVG, un faux
  PNG ou un fichier mal nommé est refusé avec un avertissement, le reste du pack
  s'applique.
- Limites : 256 Kio par image, 8 Mio et 1 024 images par pack, 16 Kio pour
  `icon-pack.json`.

### 2. Où vivent les packs (Q1 : A)

Comme sous Linux, dans cet ordre :

1. `/usr/share/icons/<dossier>/` (pour tous, appartient à root) ;
2. `/usr/share/hackeros/theme-packs/<pack>/icons/` (packs de thèmes de l'ADR 0190) ;
3. `~/.local/share/icons/<dossier>/` (pour soi) ;
4. `~/.local/share/hackeros/theme-packs/<pack>/icons/`.

Un dossier sans `icon-pack.json` est ignoré sans avertissement. Un id déjà pris
par un pack lu avant est refusé : un pack n'en cache jamais un autre (même règle
que les thèmes, ADR 0103). Les dossiers système sont lus en tant que système,
ceux de l'utilisateur avec ses droits.

### 3. Le choix du pack

- Nouveau document de réglages de l'OS **`icons`** : `/etc/hackeros/icons.config`
  (défaut de la machine) et `~/.config/hackeros/icons.config` (utilisateur), clé
  `iconPackId`, `builtin` par défaut (icônes intégrées). Il est séparé
  d'`appearance.config`, comme le thème d'icônes l'est du thème GTK.
- Un id qui ne nomme aucun pack installé garde les icônes intégrées.
- **Paramètres ▸ Apparence ▸ Pack d'icônes** liste les packs avec un aperçu de
  quatre images et écrit la couche choisie par le sélecteur de la page (« pour
  moi » ou « défaut du système », qui demande un administrateur).

### 4. Ce qu'un pack remplace (Q2 : A ; Q4 : A)

Partout, on demande d'abord au pack, puis on dessine l'icône intégrée :

| Surface | Règle |
| --- | --- |
| Menu Démarrer | `apps/<id>`, sinon `categories/<catégorie>`, sinon Lucide |
| Raccourcis d'apps du bureau et des dossiers | idem |
| Fenêtre, barre des tâches, accueil et tiroir mobiles | `apps/<id>`, sinon l'image déclarée (ADR 0164), sinon `categories/<catégorie>`, sinon le symbole générique |
| Fichiers et dossiers (File Explorer, bureau, boîtes de dialogue) | `files/folder` pour un dossier ; `files/<ext>`, sinon `files/file`, sinon l'icône intégrée |

- Les petits symboles de l'interface (horloge, alimentation, flèches) restent
  ceux du thème.
- Le changement s'applique tout de suite, sans redémarrer : le shell
  (`IconPackService`) et chaque contrôle de fichiers (`ExplorerFileView`, avec
  les droits de l'app hôte, par le SDK public) suivent `icons.config`. Les
  fenêtres ouvertes changent d'icône par la nouvelle commande
  `SetWindowIconCommand` du gestionnaire de fenêtres (événement
  `WindowIconChangedEvent`), comme un client X11 qui réécrit `_NET_WM_ICON`.
- Les images sont servies en URL `data:` : aucun fichier n'est exposé par le
  serveur, et l'OS reste autonome (aucun hôte requis).
- Modifier les images du pack en cours s'applique à la prochaine sélection ou
  connexion.

### 5. Le pack d'exemple « Néon » (Q3 : A)

Le noyau embarque un pack « Néon » (13 apps système, 5 catégories, 20 types de
fichiers ; tuiles sombres, glyphes Lucide, licence ISC, cyan pour les apps, vert
pour les catégories, magenta pour les fichiers). `FileSystemSeeder` le copie
dans `/usr/share/icons/neon/` (root, 0644) s'il manque, sans jamais écraser une
modification d'un administrateur. Il sert à essayer la fonction et de modèle.

## Conséquences

- Le Store pourra accepter le dossier `icons/` d'un pack de thèmes : le
  catalogue le lit déjà.
- Un pack bien fait remplace aussi les icônes d'apps tierces par leur id.
- Aucun changement du SDK public (`HackerOs.AppSdk`) : les nouveaux types sont
  dans `HackerOs.Theming.Abstractions` (`IconPackLibrary`, `IconPack`),
  `HackerOs.Platform.Core` (`IconPackService`, `IconPackSettingsDocuments`,
  `BuiltInIconPacks`), `HackerOs.Platform.Blazor` (`ShellAppIcon`),
  `HackerOs.Windowing.Abstractions` (`SetWindowIconCommand`) et File Explorer
  (`IconPackShellIconProvider`).

## Vérification

- `IconPackLibraryTests` : ordre des dossiers, pas de masquage, manifestes
  refusés, PNG seulement, URL `data:`, règles de recherche.
- `IconPackServiceTests` : pack Néon installé au démarrage, choix appliqué en
  direct, id inconnu, pack de l'utilisateur lu avec ses droits.
- `WindowIconTests` : la commande change l'icône sans toucher au reste.
- `IconPackShellIconProviderTests` : images du pack et icônes intégrées pour le
  reste.

## Tâches

- [x] Format, catalogue et limites (`IconPackLibrary`).
- [x] Document `icons` et `IconPackService`.
- [x] Menu Démarrer, accueil mobile, fenêtres et barre des tâches (`ShellAppIcon`,
  `SetWindowIconCommand`).
- [x] Fichiers, dossiers et raccourcis du contrôle de fichiers.
- [x] Paramètres ▸ Apparence ▸ Pack d'icônes.
- [x] Pack « Néon » installé dans `/usr/share/icons/neon/`.
- [x] Tests et documentation ([`icon-packs.md`](../icon-packs.md)).
