# ADR 0111 : ajustements de la mise en œuvre de la localisation

## Statut

Accepté le 2026-10-17 (chantier `v3/12-localization`).

- **Complète :** [ADR 0104](0104-localization.md). Remplace un mot de son §4
  (« l'asset `localization/fr.json` ») et précise ses §2 et §3.
- Numéro : `0109` est peut-être pris par un chantier parallèle ; ce chantier
  prend `0111`, réservé pour lui.

## Contexte

La mise en œuvre de l'ADR 0104 a rencontré cinq points que l'ADR ne tranche
pas, ou qu'il décrit d'une façon qui ne marche pas telle quelle.

## Décision

1. **Chargement des satellites.** L'option de démarrage
   `loadAllSatelliteResources` télécharge les assemblies satellites mais
   `Assembly.GetSatelliteAssembly` ne les trouve pas pour une app chargée
   paresseusement (`FileNotFoundException`, vérifié dans Chromium). HackerOS
   appelle donc la fonction du runtime `INTERNAL.loadSatelliteAssemblies(["fr"])`
   par `[JSImport]` depuis C#, comme `WebAssemblyCultureProvider` de Blazor :
   au démarrage de l'hôte (`BrowserSatelliteAssemblies`, Infrastructure.Browser)
   et une fois dans chaque Web Worker (`BrowserWorkerAssemblyLoader`). Aucun
   fichier JavaScript n'est ajouté. Les hôtes déclarent aussi
   `SatelliteResourceLanguages=en;fr` et les données ICU `icudt_EFIGS.dat`
   (`Directory.Build.props`) pour les formats `fr-CA`, `fr-FR`, `en-GB`.
2. **Textes du manifeste embarqués, pas assets.** `localization/<culture>.json`
   est produit et référencé par `localizations`, comme le dit l'ADR 0104 §4,
   mais n'est **pas** déclaré dans `assets` du manifeste : l'agrégateur recopie
   ses textes dans la propriété facultative `localized` de
   `BuiltInApplications.json`, et le catalogue les lit de là
   (`AppCatalog.Localize`). Le lanceur n'a donc rien à télécharger, et le hash
   des assets (ADR 0098) n'est pas touché. Une app installée plus tard (plan
   d'installation au runtime) lira les mêmes fichiers depuis son paquet.
3. **Projets à plusieurs apps.** Dans un projet qui déclare plusieurs apps
   (les commandes), les clés de `AppManifest.<culture>.resx` sont préfixées de
   l'id (`org.hackeros.commands.ls.Description`) et les fichiers vont dans
   `localization/<appId>/<culture>.json`. Une clé absente garde le texte
   anglais ; une clé inconnue est l'erreur `HOSAPP0040`.
4. **Langue de la plateforme.** Les composants de la plateforme reçoivent
   `IStringLocalizer<T>` par injection, lié à `SessionLocaleService` (langue du
   système avant la connexion, de l'utilisateur après), et jamais à
   `CultureInfo.CurrentUICulture`. `AddHackerOsPlatformLocalization` remplace
   l'enregistrement de `IStringLocalizer<>` (même après un `AddLocalization()`
   d'une bibliothèque). MudBlazor garde sa propre localisation.
5. **`localectl set-locale`.** Le droit d'écrire `/etc/locale.conf` vient des
   permissions du fichier (`root`, `0644`) : `sudo localectl set-locale …`
   réussit, un utilisateur reçoit `Access denied (try sudo)`. Une locale
   inconnue (hors `localectl list-locales`) est refusée ; `LANGUAGE` n'est pas
   vérifiée, comme sous systemd.

## Conséquences

- [`localization.md`](../../framework/localization.md) décrit ces points ;
  [`app-model.md`](../../framework/app-model.md) liste `HOSAPP0040` et la propriété
  `localized`.
- Une app tierce traduit son nom par `Resources/AppManifest.fr.resx`, ses
  textes par `Resources/Strings*.resx` et `AppContext.GetLocalizer<T>()`.
