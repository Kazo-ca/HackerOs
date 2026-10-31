> [English version](../../../../en/wasm/dev/adr/0222-installed-app-satellite-assemblies.md)

# ADR 0222 : traductions des apps installées, assemblages satellites dans le paquet et chargés par l'OS

## Statut

Proposé le 2026-10-31 (issue #337), construit avec les choix recommandés ;
questions posées à Benoit (voir « Questions »).

- **Complète :** [ADR 0182](0182-install-apps-from-github.md) §1 et §5
  (`hackpkg`, `HackerOsPack`), [ADR 0221](0221-pack-all-static-web-assets-of-an-app.md)
  (toutes les ressources statiques d'une app dans son paquet) et
  [ADR 0104](0104-localization.md) §3 (satellites dans le navigateur).

## Contexte

- Une app qui a des `.resx` par culture (`Resources/Strings.fr.resx`) compile
  un assemblage satellite `bin/<config>/<tfm>/fr/<Assemblage>.resources.dll`.
- Une app compilée dans l'OS a ses satellites chargés par Blazor et
  `BrowserSatelliteAssemblies` (ADR 0104 §3), pour toutes les langues de
  HackerOS : chaque processus a son propre `LANG`.
- Une app installée par `hackpkg` ou le Hack Store ne recevait pas les siens :
  `HackerOsPack` ne mettait dans le paquet que les assemblages déclarés,
  `AppPackage` n'acceptait que `lib/*.dll`, et l'OS, qui lit le code d'une app
  installée dans le système de fichiers virtuel (`LoadFromStream`), ne
  chargeait aucun satellite. Une app installée restait en anglais.

## Décision

### 1. Format du paquet

- Le satellite d'un assemblage déclaré par le manifeste (point d'entrée ou
  composant) est rangé à `lib/<culture>/<assemblage>.resources.dll`. Son
  SHA-256 est listé dans `hackeros-package.json`, comme chaque fichier du paquet
  (ADR 0182 §1) ; le manifeste ne change pas.
- `<culture>` est un nom BCP 47 fait de lettres, de chiffres et de tirets
  (`fr`, `fr-CA`, `zh-Hant`), d'au plus 20 caractères.
- `AppPackage` refuse (`apps.package.unexpected-file`) un satellite qui ne
  traduit aucun assemblage déclaré : ceux du SDK viennent avec l'OS, et un
  paquet ne livre pas les traductions d'un autre.
- `AppPackage.Assemblies` ne liste plus que `lib/*.dll` ;
  `AppPackage.SatelliteAssemblies` liste les satellites.

### 2. `HackerOsPack` ajoute les satellites de l'app

- L'outil `pack` cherche, dans le dossier de sortie (`--bin`), le satellite de
  chaque assemblage déclaré dans chaque sous-dossier de culture
  (`PackagedSatellites`). Les satellites du SDK et des bibliothèques
  référencées, copiés là par la compilation, restent dehors.

### 3. L'OS charge les satellites avant le premier rendu

- `InstalledAppAssemblyTransport` lit, avant de charger l'assemblage d'une app
  installée, `lib/<culture>/<assemblage>.resources.dll` pour chaque langue de
  HackerOS (`HackLocale.SupportedLanguages`, l'anglais neutre excepté), et le
  charge dans le contexte par défaut (`InstalledAppSatellites`). Une culture
  absente du paquet est sautée : l'app montre ses textes neutres.
- Un gestionnaire `AssemblyLoadContext.Default.Resolving` répond à la demande
  du runtime (`<assemblage>.resources, Culture=fr`) avec le satellite chargé,
  que le runtime retrouve ou non par son nom un assemblage chargé depuis un flux.
- Les Web Workers ne sont pas concernés : le code d'une app installée ne tourne
  que sur la page (les workers ne chargent que les assemblages connus du build).

## Conséquences

- Une app installée est traduite comme si elle était compilée dans l'OS, sans
  rien déclarer.
- **Compatibilité :** un OS 2.2.0 ou plus ancien refuse un paquet qui contient
  `lib/fr/...` (`apps.package.unexpected-file`). Ce paquet est compilé avec le
  SDK de la version qui apporte cette ADR, que son `sdkCompatibility` exige
  déjà (ADR 0098 §3) : il ne s'installerait pas non plus sur cet OS. Une app
  sans `.resx` par culture donne le même paquet qu'avant.
- La CI du catalogue (`PackageCheck` du dépôt HackerOs-AppMarketPlace) lit les
  paquets avec `HackerOs.Platform.Core` : elle doit passer à cette version pour
  accepter les paquets traduits.
- Chaque lancement de la première fenêtre d'une app installée lit un fichier de
  plus par langue (aujourd'hui `fr`), même absent ; le coût est une lecture
  IndexedDB.

## Questions

Posées à Benoit dans la conversation le 2026-10-31 ; réponses à reporter ici.

1. **Quelles langues charger ?** A (recommandé) : toutes les langues de
   HackerOS, comme les apps compilées, car chaque processus a son `LANG`.
   B : seulement la langue de la session au lancement ; une fenêtre ouverte
   ensuite dans une autre langue reste en anglais.
2. **Quels satellites mettre en paquet ?** A (recommandé) : tous ceux que la
   compilation écrit pour les assemblages de l'app, même d'une langue que
   HackerOS ne parle pas encore (prêts pour une langue ajoutée plus tard).
   B : seulement ceux des langues de HackerOS (`fr` aujourd'hui), pour un
   paquet plus petit.

## Tâches

- [x] ADR (fr/en), index, mémoire.
- [x] `AppPackage` : `lib/<culture>/<assemblage déclaré>.resources.dll` accepté et vérifié, `SatelliteAssemblies`, `TryParseSatellitePath`.
- [x] `AppPackageWriter.Write` : satellites ; outil `pack` : `PackagedSatellites`.
- [x] `InstalledApp.GetSatellitePath`, `InstalledAppSatellites`, `InstalledAppAssemblyTransport.Satellites`.
- [x] Tests unitaires (`AppPackageTests`, `AppPackageWriterTests`, `PackagedSatellitesTests`, `InstalledAppSatellitesTests`).
- [x] Paquet de test `Tests/fixtures/installed-app` avec `Resources/ProbeStrings.fr.resx` ; test de bout en bout dans `HackerOs.TrimmedPublish.E2E.Tests`.
- [x] Docs `app-installation.md`, `localization.md`, `app-repository.md`, `features.md`, entrée de changement.
- [ ] HackerOs-AppMarketPlace : passer `PackageCheck` à la version qui apporte cette ADR (après sa sortie).
