> [English version](../../../en/wasm/framework/localization.md)

# Localisation (anglais et français)

Décisions : [ADR 0104](../dev/adr/0104-localization.md), ajustée par l'[ADR 0111](../dev/adr/0111-localization-implementation-adjustments.md). Chantier : `v3/12-localization`
du [plan wasm v3](../dev/plans/wasm-v3-completion-plan.md).

## But

HackerOS parle **anglais** (langue par défaut et de repli) et **français**. La
langue suit la locale, comme sous Linux :

- chaque **processus** a sa langue, tirée de **son** environnement (`LANG`,
  `LC_MESSAGES`, `LC_ALL`) : `LANG=en_US.UTF-8 ls` répond en anglais même dans
  une session française ;
- la **plateforme** (bureau, barre des tâches, lanceur, dialogues, écrans de
  connexion) prend la langue de l'utilisateur **à la connexion** et la garde
  jusqu'à la déconnexion ; avant la connexion, elle parle la langue du système.

## Architecture

### Où la langue est écrite

| Fichier | Rôle | Écrit par |
|---|---|---|
| `/etc/locale.conf` | Locale du système (écran de connexion, utilisateurs sans réglage) | `sudo localectl set-locale LANG=fr_CA.UTF-8`, créé par le seeder avec `LANG=en_US.UTF-8` |
| `/etc/environment` | Variables globales (peut aussi fixer `LANG`) | l'administrateur |
| `~/.config/locale.conf` | Locale de l'utilisateur | Paramètres → Langue, ou à la main |

Format systemd : une ligne `NOM=valeur` par variable (`LANG`, `LANGUAGE`,
`LC_MESSAGES`, `LC_TIME`, `LC_NUMERIC`, `LC_MONETARY`…), `#` pour les
commentaires. `HackLocale.ParseLocaleConf` / `FormatLocaleConf` lisent et
écrivent ce format.

À la connexion, `LoginEnvironment.Build` applique dans l'ordre : défaut
`LANG=en_US.UTF-8` → `/etc/locale.conf` → `/etc/environment` →
`~/.config/locale.conf`. L'environnement obtenu est copié dans chaque processus
(ADR 0050).

### Comment la langue est lue

- `HackLocale.ResolveLanguage(getVar)` : `LC_ALL`, puis `LC_MESSAGES`, puis
  `LANG` ; `fr_*` → `fr`, tout le reste → `en`.
- `HackLocale.ResolveFormatCulture(getVar, LC_TIME)` : la culture complète
  (`fr-CA`) pour les dates, nombres et monnaies ; `C`/`POSIX` → culture
  invariante.
- Apps : `IAppExecutionContext.Culture` et `.FormatCulture` (membres par défaut,
  lus dans l'environnement du processus à chaque appel), et
  `HackEnvironment.Culture` / `.FormatCulture` pour le contexte ambiant.
- Plateforme : `SessionLocaleService` (Platform.Core), avec `Culture`,
  `FormatCulture`, `LocaleName` et l'événement `Changed`. `App.razor` appelle
  `UseSystemLocaleAsync()` au démarrage et `UseSessionLocaleAsync(principal)`
  après chaque connexion.

`CultureInfo.CurrentUICulture` n'est **jamais** utilisé pour choisir la langue :
il est commun à tout l'onglet, alors que deux processus peuvent avoir deux
langues.

### Localiseurs

- `HackStringLocalizer` (AppSdk) : un `IStringLocalizer` sur un
  `ResourceManager`, avec une fonction qui donne la culture à chaque lecture.
  Les arguments (`L["Clé", n]`) sont formatés avec la culture du texte. Une clé
  absente en français donne le texte anglais et un avertissement de débogage,
  une fois par clé.
- `AppContext.GetLocalizer<Strings>()` : localiseur lié au **processus** de
  l'app. `HackLocalization.GetLocalizer<Strings>()` sans argument lit le
  contexte ambiant.
- `SessionStringLocalizer<T>` (Platform.Core) : ce que reçoit
  `@inject IStringLocalizer<T>` dans un composant de la plateforme, lié à
  `SessionLocaleService.Culture`. `AddHackerOsPlatformLocalization()` le
  déclare (appelé par `AddHackerOsEcosystem`, donc pour les trois hôtes).
- `EnglishStringLocalizer<T>` (AppSdk) : toujours anglais, pour les tests de
  composants : `services.AddSingleton(typeof(IStringLocalizer<>), typeof(EnglishStringLocalizer<>))`.

### Satellites dans le navigateur

Les hôtes déclarent `SatelliteResourceLanguages=en;fr` et les données ICU
`icudt_EFIGS.dat` (`Directory.Build.props`). L'option de démarrage
`loadAllSatelliteResources` ne suffit pas pour les apps chargées paresseusement
(`GetSatelliteAssembly` lève `FileNotFoundException`). HackerOS appelle donc la
fonction du runtime `INTERNAL.loadSatelliteAssemblies(["fr"])`, comme le fait
Blazor pour changer de culture :

- sur le fil principal, `BrowserSatelliteAssemblies.LoadAsync` au démarrage de
  l'hôte (Infrastructure.Browser) ;
- dans les Web Workers, `BrowserWorkerAssemblyLoader.LoadLazyAsync`, une fois.

### Nom et description des apps

Le générateur de manifestes lit `Resources/AppManifest.<culture>.resx` :

| Clé | Remplace |
|---|---|
| `Name` | `AppManifest.Name` |
| `Description` | `AppManifest.Description` |
| `Terminal.Usage` | `TerminalCommandManifest.Usage` |

Dans un projet à plusieurs apps (les commandes), chaque clé est préfixée de
l'id : `org.hackeros.commands.ls.Description`.

Les titres des entrées de menu de fichiers (ADR 0159 §3) vivent aussi dans ces
fichiers, mais à part : l'anglais dans le fichier **neutre**
`Resources/AppManifest.resx` (obligatoire, `HOSAPP0026` sinon), les traductions
dans `AppManifest.<culture>.resx`. Le générateur les copie dans `contextMenus`
du manifeste (`titles`) et non dans `localization/<culture>.json`.

Il écrit `localization/<culture>.json` (une app) ou
`localization/<appId>/<culture>.json` (plusieurs apps), au format strict
`{"name","description","terminalUsage"?}`, ajoute l'entrée
`localizations: [{"culture":"fr","resourcePath":"localization/fr.json"}]` et
supprime les fichiers périmés. Erreur : `HOSAPP0040`. L'agrégateur recopie ces
textes dans `BuiltInApplications.json` (propriété facultative `localized`), et
`AppCatalog.Localize(manifest, culture)` les applique. Le lanceur, le titre des
fenêtres, `help` et `IAppCatalogGateway` affichent ainsi le nom traduit.
L'id, le nom de commande et les alias ne sont jamais traduits.

Les fichiers `localization/**` ne sont pas déclarés comme assets du manifeste :
leurs textes voyagent dans `BuiltInApplications.json`.

## Traduire son code (guide)

1. **Classe marqueur.** Une classe `internal sealed class Strings` (constructeur
   privé) dans l'espace de noms racine du projet. Les ressources sont
   `Resources/Strings.resx` (anglais) et `Resources/Strings.fr.resx`
   (français). Un sous-dossier donne un sous-espace de noms :
   `HackerOs.Commands.Ls.Strings` ↔ `Resources/Ls/Strings.resx`. Le nom de
   ressource suit la règle `<assembly>.Resources.<nom du type sans le préfixe de l'assembly>`.
2. **Écrire les `.resx`.** `build/tools/resx.py sortie.resx table.json` écrit
   un `.resx` trié depuis une table JSON `{"Clé": "Texte"}`. Les deux fichiers
   ont **exactement** les mêmes clés (un test le vérifie).
3. **Clés.** PascalCase, groupées par zone avec un point :
   `Toolbar.NewFolder`, `Dialog.Delete.Title`, `Error.NotFound`. Les arguments
   sont `{0}`, `{1}` ; une accolade littérale s'écrit `{{`.
4. **Lire les textes.**
   - App à fenêtre : `private IStringLocalizer<Strings> L => _l ??= AppContext.GetLocalizer<Strings>();`
     puis `@L["Clé"]`.
   - Commande : `IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();`.
     Les raisons d'erreur de fichier à la glibc (« Aucun fichier ou dossier de ce
     nom ») viennent de `CommandText.Reason`/`FileError`/`Describe`
     (`HackerOs.Commands.Common`), et `ShellErrorText.Describe(exception, context)`
     les donne en français pour une app tierce.
   - Composant de la plateforme : `@inject IStringLocalizer<Strings> L`.
   - Bibliothèque du SDK (`AppSdk.Blazor`, `FileView`, `Terminal`) : le
     contexte d'app reçu en cascade, `AppContext.GetLocalizer<Strings>()`, ou
     `HackLocalization.GetLocalizer<Strings>()` hors composant.
   - Code de la plateforme hors composant (Platform.Core) :
     `Strings.For(culture)[...]`.
5. **Dates et nombres** : `FormatCulture` (app) ou
   `SessionLocaleService.FormatCulture` (plateforme), jamais la culture du thread.
6. **Typographie française** : espace avant `:`, `?`, `!` et `;`, guillemets
   « … », majuscule seulement au premier mot (« Nouveau dossier »).
7. **Ce qui reste en anglais** (ADR 0104 §5) : les sorties lues par des
   scripts (colonnes de `ls -l`, `ps`, `df`, `top`, `systemctl list-units`),
   les noms de fichiers et dossiers (`~/Downloads` reste tel quel), les
   journaux de `/var/log`, les codes d'erreur, les noms de commandes et
   d'options, les identifiants (capacités, ids d'apps, clés de réglages).
8. **Nom de l'app** : `Resources/AppManifest.fr.resx` (clés du tableau
   ci-dessus), puis un build régénère `manifest.json` et `localization/`. Ces
   fichiers générés sont commités.

## Changer de langue

- **Utilisateur** : Paramètres → Langue écrit `~/.config/locale.conf`
  (`LANG=fr_CA.UTF-8`), ou « Suivre la langue du système » le supprime. Le
  changement prend effet à la prochaine connexion, comme sous GNOME.
- **Système** : `sudo localectl set-locale LANG=fr_FR.UTF-8` ; `localectl`
  affiche la locale du système, `localectl list-locales` les 8 locales connues.
- **Un seul processus** : `LANG=fr_FR.UTF-8 help`.

## Décisions clés

- Langue par processus, jamais par onglet (`CurrentUICulture` ignoré).
- Plateforme figée pendant la session : un changement de `~/.config/locale.conf`
  attend la prochaine connexion.
- Satellites chargés par `INTERNAL.loadSatelliteAssemblies`, pas par l'option
  de démarrage.
- Textes traduits du manifeste embarqués dans `BuiltInApplications.json`
  plutôt que lus comme assets à l'exécution.

## Liste des tâches

- [x] `HackLocale` : règles glibc, `locale.conf`, locales connues.
- [x] `LoginEnvironment` : `/etc/locale.conf` et `~/.config/locale.conf` ; seeder.
- [x] `IAppExecutionContext.Culture` / `.FormatCulture`, `HackEnvironment`.
- [x] `HackStringLocalizer`, `GetLocalizer<T>`, `EnglishStringLocalizer<T>`.
- [x] `SessionLocaleService`, `SessionStringLocalizer<T>`, `AddHackerOsPlatformLocalization`.
- [x] Satellites `fr` chargés dans le navigateur (fil principal et workers).
- [x] Générateur : `AppManifest.<culture>.resx` → `localization/`, `HOSAPP0040` ; agrégateur `localized`.
- [x] Catalogue, lanceur, titre des fenêtres, `IAppCatalogGateway` localisés.
- [x] Shell : builtins et `help` dans la langue du processus.
- [x] Commande `localectl`.
- [x] Panneau Langue de Paramètres.
- [x] Traduction de la plateforme (bureau, barre des tâches, lanceur, dialogues, connexion).
- [x] Traduction des apps système et des commandes.
- [x] Tests : parité des `.resx`, `HackLocale`, générateur, E2E en français.
