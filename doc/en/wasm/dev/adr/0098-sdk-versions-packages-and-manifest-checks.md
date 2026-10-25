# ADR 0098 : versions et paquets NuGet du SDK et de la plateforme, vérifications du manifeste au chargement, gabarits et gel du SDK 1.0

## Statut

Accepté le 2026-10-17 (chantier `v3/1-adr`, pour les chantiers
`v3/6-sdk-versioning` et `v3/15-sdk-freeze`).

- **Complète :** [ADR 0019](0019-sdk-versioning-and-compatibility.md) (SemVer,
  frontière du SDK, vérification de `sdkCompatibility` « au chargement », jamais
  faite), ADR 0047 (manifeste généré, `BuiltInApplications.json`), ADR 0048
  (`IsSystemApp` sur la référence de l'hôte), ADR 0055 et 0059 (frontière du
  SDK, `HOSAPP0030`/`HOSAPP0031`).
- **Remplace :** le profil de build du développeur (`BuildProfileManifest`) de
  l'analyse de migration §14.1, déjà rendu inutile par les ADR 0047 et 0048.
- **Plan :** [`wasm-v3-completion-plan.md`](../plans/wasm-v3-completion-plan.md),
  lignes `v3/6` et `v3/15` ; manques R3, R4, R5, R20, R21 ; décision différée
  n° 11 ; réponse 2 de Benoit (paquets NuGet).

## Contexte

- Les bibliothèques du SDK (`HackerOs.App.Abstractions`, `HackerOs.AppSdk`,
  `HackerOs.AppSdk.Blazor`, `.Terminal`, `.Web`, `.Web.Blazor`, `.FileView`,
  `HackerOs.Simulation.Abstractions`) n'ont ni version ni `PackageId`. Seuls
  trois projets `Shared` et six projets de fenêtrage importent
  `Platform/Packaging.props` (version unique `0.1.0-local`, analyseur d'API
  publique). Une app ne se construit qu'à l'intérieur de ce dépôt
  (`ProjectReference` vers `Shared/*`, `HOSAPP0030` sinon).
- `sdkCompatibility` n'est vérifié que dans sa forme
  (`AppManifestValidator.cs:34-38`) ; aucune version courante du SDK n'existe à
  l'exécution, et le générateur écrit `1.0.0` en dur comme minimum
  (`AppDefinitionMapper.cs:51`).
- Deux apps peuvent déclarer le même nom de commande : `ShellCommandResolver`
  prend la première trouvée (`ShellCommandResolver.cs:43-56`).
- Le hash SHA-256 d'un asset n'est vérifié que dans sa forme
  (`AppManifestValidator.cs:393-396`).
- `BuildProfileManifest`, son validateur et son sérialiseur ne servent qu'à
  leurs tests.
- Le dépôt du jeu (ADR 0092) doit pouvoir construire des apps **et** un hôte
  d'OS complet avec ses propres apps, sans copier ce dépôt.

## Décision

### 1. Une seule version pour tout ce qui est publié

- Toutes les bibliothèques publiées partagent un numéro, défini une fois dans
  `build/Versions.props` (`HackerOsVersion`), importé par
  `Directory.Build.props`. `Platform/Packaging.props` en hérite au lieu de
  porter `0.1.0-local`.
- Jusqu'au gel : `0.9.0-preview.<n>`, `n` augmenté par chaque chantier qui
  change une API publiée. Au gel (`v3/15`) : `1.0.0`. Ensuite SemVer
  (ADR 0019) : mineure pour un ajout, majeure pour une rupture.

### 2. Paquets

| Paquet | Contenu | Pour |
|---|---|---|
| `HackerOs.App.Abstractions`, `HackerOs.AppSdk`, `HackerOs.AppSdk.Blazor`, `HackerOs.AppSdk.Terminal`, `HackerOs.AppSdk.Web`, `HackerOs.AppSdk.Web.Blazor`, `HackerOs.AppSdk.FileView`, `HackerOs.AppSdk.Icons`, `HackerOs.AppSdk.VirtualKeyboard`, `HackerOs.Theming.Abstractions` | le SDK public | écrire une app |
| `HackerOs.App.Build` | `build/HackerOs.Apps.targets` en `buildTransitive`, le générateur de manifeste et les règles de frontière du SDK | qu'une app hors du dépôt produise son `manifest.json` comme ici |
| `HackerOs.Platform.*`, `HackerOs.Infrastructure.Browser`, `HackerOs.Windowing.*`, `HackerOs.Theming.Blazor`, `HackerOs.MobileShell.Blazor` | la plateforme | composer un hôte d'OS |
| `HackerOs.Ecosystem.Host` | la composition de l'hôte (aujourd'hui dans l'exe `HackerOs.Ecosystem` : `EcosystemServiceCollectionExtensions`, composants racine), sortie dans une bibliothèque ; l'exe devient une coquille qui l'appelle | qu'un autre dépôt fasse son propre hôte PWA avec ses apps en plus des apps de base |
| `HackerOs.Apps.*`, `HackerOs.Commands` | les apps et commandes de base | les inclure dans un autre hôte |
| `HackerOs.Templates` | les gabarits `dotnet new` (point 6) | démarrer un projet |

- `Simulation.Abstractions` est publié parce que le SDK l'expose, mais reste
  hors des références autorisées d'une app (ADR 0055).
- `build/pack-local.sh` (et `.ps1`) fait `dotnet pack` de tout ce qui est
  publiable vers `artifacts/local-nupkg` (non versionné dans git). Un `nuget.config`
  d'exemple montre comment un autre dépôt pointe sur ce dossier. GitHub
  Packages viendra quand la CI remarchera ; rien n'est poussé sur nuget.org.
- Les cibles de l'hôte acceptent une **`PackageReference`** d'app avec les mêmes
  métadonnées qu'une `ProjectReference` (`IsSystemApp="true"`, chargement
  paresseux). Les règles `HOSAPP0030`/`HOSAPP0031` acceptent les paquets du SDK
  comme les projets du SDK.

### 3. Compatibilité du SDK vérifiée au chargement

- `HackerOs.App.Abstractions` expose `HackerOsSdk.Version`, tiré de sa version
  d'assembly au build.
- Le générateur écrit comme minimum **la version du SDK contre laquelle l'app a
  été compilée** (au lieu de `1.0.0` en dur), sauf si l'app déclare autre chose.
- `AppCatalog` refuse une app dont la plage ne contient pas `HackerOsSdk.Version`
  (précédence SemVer, préversions comprises) : code `catalog.sdk.incompatible`,
  app listée « incompatible » dans Paramètres avec la plage demandée, jamais
  lancée.
- Politique de support (décision différée n° 11) : l'OS charge toute app
  compilée contre une version **de la même majeure** inférieure ou égale à la
  sienne ; une API retirée l'est seulement à la majeure suivante, après au
  moins une mineure marquée `[Obsolete]` (ADR 0019).

### 4. Noms de commandes en double

- **Au build** : l'agrégation de `BuiltInApplications.json` refuse deux apps
  qui déclarent le même nom ou alias de commande, ou un nom de built-in du
  shell (ADR 0064), avec le nouveau code **`HOSAPP0022`**.
- **Au chargement** (apps venues d'ailleurs plus tard) : une app système garde
  le nom ; sinon le premier dans l'ordre d'activation le garde. La commande de
  l'autre app n'est pas enregistrée, l'app reste lançable, et l'avertissement
  `catalog.command.duplicate` est écrit dans `/var/log/syslog`.

### 5. Hash des assets vérifié

- Le générateur calcule le SHA-256 de chaque asset déclaré au build.
- La plateforme vérifie le hash d'un asset à sa **première lecture** dans la
  session (icône, schéma de charge utile, fichier embarqué) et garde le résultat
  en mémoire. Un écart : l'asset est refusé, l'app est marquée « endommagée »
  dans Paramètres, et l'événement va dans `/var/log/syslog`. Les assemblies
  chargées paresseusement restent couvertes par l'intégrité du manifeste de
  boot de Blazor.

### 6. Gabarits `dotnet new`

`HackerOs.Templates` fournit `hackeros-window` (app à fenêtre),
`hackeros-command` (commande), `hackeros-service` (service),
`hackeros-site` (site ASP.Hack) et `hackeros-host` (hôte PWA qui inclut les apps
de base et les apps du dépôt). Chaque gabarit est construit et testé par un test
qui fait `dotnet new` puis `dotnet build` contre le flux local.

### 7. Profil de build retiré

`BuildProfileManifest`, `BuildProfileValidator`, `BuildProfileJsonSerializer`
et leurs tests sont supprimés. La sélection des apps livrées est la liste des
références de l'hôte (ADR 0047, 0048). La valeur
`CapabilityGrantSource.BuildProfile` reste si des accords enregistrés
l'utilisent déjà (valeur stockée), sinon elle part aussi.

### 8. Gel du SDK 1.0 (`v3/15`)

- Dès `v3/6`, toute bibliothèque publiée a l'analyseur d'API publique, avec
  ses API en `PublicAPI.Unshipped.txt`.
- `v3/15` passe tout en `PublicAPI.Shipped.txt`, publie `1.0.0`, et ajoute un
  test qui charge dans l'OS courant une app compilée contre le paquet `1.0.0`
  gardé dans `Tests/fixtures`.

## Conséquences

- `v3/6` et `v3/3` touchent tous deux `AppDefinition` : le second fusionné
  reprend l'autre (plan, §4).
- `sdk-developer-guide.md` gagne « construire une app depuis un autre dépôt »
  (`v3/6`) puis le guide complet du dépôt tiers (`v3/15`) ;
  `building-and-testing.md`, `project-overview.md`, `sdk-reference.md` et
  `app-installation.md` sont mis à jour.
- La sortie de la composition de l'hôte dans `HackerOs.Ecosystem.Host` ne change
  rien au comportement ; `build/verify-hosts.sh` le vérifie.
