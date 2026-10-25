> [English version](../../../../en/wasm/dev/adr/0104-localization.md)

# ADR 0104 : localisation (anglais et français, `LANG` par processus, `locale.conf`, ressources `.resx`)

## Statut

Accepté le 2026-10-17 (chantier `v3/1-adr`, pour le chantier `v3/12-localization`).

- **Complète :** [ADR 0050](0050-shell-grammar-and-process-environment.md)
  (environnement de processus, `LANG=en_US.UTF-8` par défaut), ADR 0044
  (réglages en fichiers), ADR 0047 (champ `localizations` du manifeste).
- **Remplace :** la conception jamais réalisée de `docs/old/localization.md`
  (cinq cultures, `/etc/hackeros/locale.json`).
- **Plan :** [`wasm-v3-completion-plan.md`](../plans/wasm-v3-completion-plan.md),
  ligne `v3/12-localization` ; manque R10 ; réponse 3 de Benoit (anglais et
  français, anglais par défaut).

## Contexte

- Aucun `.resx`, aucun `IStringLocalizer` : les chaînes sont écrites en dur, en
  anglais et en français mélangés.
- `LoginEnvironment` (`Platform/HackerOs.Platform.Core/Processes/LoginEnvironment.cs`)
  pose `LANG=en_US.UTF-8`, puis applique `/etc/environment`. Rien ne lit `LANG`.
- `AppManifest.Localizations` (`LocalizationManifest(Culture, ResourcePath)`)
  existe et reste vide partout.
- Sous Linux, la langue suit `LANG` et `LC_*` de chaque processus, initialisés
  depuis `/etc/locale.conf` (système, `localectl`) puis `~/.config/locale.conf`
  (utilisateur) ; un changement de langue du bureau prend effet à la connexion
  suivante.

## Décision

### 1. Langues

- Deux langues : **anglais** (`en`, langue neutre, utilisée par défaut et en
  repli) et **français** (`fr`). Toute culture `en_*` donne l'anglais, toute
  culture `fr_*` le français (`fr_CA`, `fr_FR`…), toute autre l'anglais.
- La culture complète (par exemple `fr_CA`) sert quand même au **format** des
  dates, nombres et monnaies.

### 2. La langue vient de l'environnement du processus

- Ordre à la connexion : défaut `en_US.UTF-8` → `/etc/locale.conf` →
  `/etc/environment` → `~/.config/locale.conf`. Les fichiers `locale.conf`
  ont le format de systemd (`LANG=fr_CA.UTF-8`, `LC_TIME=…`).
- Variables lues : `LC_ALL`, puis `LC_MESSAGES` (textes) ou `LC_TIME` /
  `LC_NUMERIC` (formats), puis `LANG`, comme la glibc.
- Chaque processus a sa culture, tirée de **son** environnement :
  `LANG=en_US.UTF-8 ls` affiche ses messages en anglais même si la session est
  en français.
- Le SDK l'expose par `IAppExecutionContext.Culture` (textes) et
  `.FormatCulture` (formats), et par `HackEnvironment`. C'est la « passerelle de
  langue » du plan.
- Le bureau, la barre des tâches, le lanceur et les dialogues de la plateforme
  prennent la langue de l'utilisateur **à la connexion**. Changer de langue dans
  Paramètres écrit `~/.config/locale.conf` et indique que le changement prend
  effet à la prochaine connexion, comme sous GNOME. Les apps lancées ensuite
  dans la même session gardent l'environnement de la session.
- Nouvelle commande `localectl` : `status`, `list-locales`
  (`en_US.UTF-8`, `en_CA.UTF-8`, `fr_FR.UTF-8`, `fr_CA.UTF-8`…), et
  `set-locale LANG=…` (administrateur, écrit `/etc/locale.conf`).

### 3. Ressources

- Chaque projet a ses ressources `.resx` (`Resources/<Type>.resx` en anglais,
  `<Type>.fr.resx` en français) et utilise `IStringLocalizer<T>`
  (`Microsoft.Extensions.Localization`).
- Le localiseur remis à une app est **lié à la culture de son processus** : il
  lit `ResourceManager.GetString(clé, culture du processus)`, sans dépendre de
  `CultureInfo.CurrentUICulture`, qui est commun à tout l'onglet.
- Clé absente en français : texte anglais, et un avertissement de débogage une
  fois par clé.
- Les hôtes déclarent `SatelliteResourceLanguages=en;fr`. Les assemblies
  satellites des apps chargées paresseusement sont chargées avec elles ; si le
  chargeur paresseux de Blazor ne le fait pas, `v3/12` charge le satellite `fr`
  explicitement avec l'assembly de l'app (c'est le premier point à prouver).

### 4. Nom et description des apps

- Le générateur de manifeste lit `Resources/AppManifest.fr.resx` (clés `Name`,
  `Description`, `Terminal.Usage`) et produit l'asset `localization/fr.json`
  avec l'entrée `localizations: [{ "culture": "fr", "resourcePath": … }]`.
- Le lanceur, la barre des tâches, Paramètres et `help` affichent le nom dans la
  langue de l'utilisateur.

### 5. Ce qui est traduit

- La plateforme (bureau, barre des tâches, lanceur, dialogues, notifications,
  écrans de connexion et d'erreur), les apps système et les messages des
  commandes (aide, usage, erreurs).
- **Pas traduit** : les sorties que des scripts lisent (colonnes de `ls -l`,
  `ps`, `df`, `top`, `systemctl list-units`), les noms de fichiers et de
  dossiers du système (`/home`, `~/.config`, `Trash`), les journaux de
  `/var/log` (en anglais, pour les outils), les codes d'erreur.
- Les dossiers de l'utilisateur gardent leurs noms anglais : pas de traduction
  à la `xdg-user-dirs` (`~/Downloads` ne devient pas `~/Téléchargements`).

### 6. Tests

- Un test vérifie que chaque `.fr.resx` a toutes les clés de son `.resx`, et
  inversement.
- Un test E2E se connecte avec `~/.config/locale.conf` en français et vérifie le
  lanceur, une app et une commande.

## Conséquences

- Chantier le plus long du plan : il peut être découpé en un fil
  d'infrastructure (§2 à §4) puis un fil par groupe d'apps (plan, §6).
- `docs/old/localization.md` reçoit en tête un renvoi vers cet ADR (archive, le
  contenu n'est pas réécrit) ; `process-environment.md`, `sdk-reference.md`,
  `sdk-developer-guide.md` (« traduire son app ») et `linux-commands.md` sont mis
  à jour.
