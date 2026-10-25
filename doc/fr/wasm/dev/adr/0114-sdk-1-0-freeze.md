# ADR 0114 : gel du SDK 1.0, périmètre de la promesse de compatibilité et app de compatibilité

## Statut

Accepté le 2026-10-17 (chantier `v3/15-sdk-freeze`). Deux questions sont
posées à Benoit dans le fil du chantier (§2 et §3) ; ce qui suit applique la
réponse recommandée en attendant.

- **Met en œuvre :** [ADR 0098](0098-sdk-versions-packages-and-manifest-checks.md)
  §8 (gel du SDK 1.0).
- **Précise :** [ADR 0019](0019-sdk-versioning-and-compatibility.md) (SemVer et
  dépréciation : quels paquets la promesse couvre, test de compatibilité
  binaire), ADR 0098 §1 (version unique).
- **Termine :** le retrait annoncé par l'ADR 0065 (« `BrowserClipboard` reste
  une version, `[Obsolete]`, puis sera retiré ») et par l'ADR 0056 §12
  (formes `Get/SetFileReferencesAsync`).
- **Document :** [`sdk-packages.md`](../../framework/sdk-packages.md),
  [`building-on-hackeros.md`](../../framework/building-on-hackeros.md).

## Contexte

- Depuis `v3/6`, les 39 bibliothèques publiées passent par l'analyseur d'API
  publique, avec toute leur surface en `PublicAPI.Unshipped.txt`, à la version
  `0.9.0-preview.1`.
- L'OS refuse une app dont le SDK minimum n'a pas la même majeure que le sien
  (ADR 0098 §3). Une app compilée en `0.9.0-preview` est donc refusée par un
  OS `1.0.0` : le passage à la 1.0 casse déjà toutes les apps preview, quoi
  qu'on retire.
- Toutes les bibliothèques portent un seul numéro (ADR 0098 §1). Une rupture
  dans n'importe laquelle force donc, sous SemVer strict, une nouvelle majeure
  de tout, SDK compris.
- Le SDK contenait encore trois API `[Obsolete]` : `BrowserClipboard`
  (`HackerOs.AppSdk.Blazor`, remplacé par `HackClipboard.SetTextAsync`) et
  `IAppClipboardGateway.GetFileReferencesAsync` / `SetFileReferencesAsync`
  (remplacées par `Get/SetFileDropListAsync`). Aucune app du dépôt ne les
  utilise (règle vérifiée par `app-sdk.md`).
- L'ADR 0019 §3 demandait un test automatique « une app compilée contre une
  version antérieure du SDK se charge et tourne », jamais écrit.

## Décision

### 1. Version 1.0.0, toute la surface en `Shipped`

- `build/Versions.props` : `HackerOsVersion` = `1.0.0`.
- Chaque `PublicAPI.Unshipped.txt` est versé dans son `PublicAPI.Shipped.txt`
  (lignes triées, sans doublon) et ne garde que `#nullable enable`.
- Après le gel, une API ajoutée va dans `Unshipped` (`RS0016`) ; une API
  retirée ou changée est refusée au build (`RS0017`) tant qu'elle n'est pas
  écrite `*REMOVED*` dans `Unshipped`. Toute rupture est donc visible dans la
  revue, quel que soit le paquet.
- Une publication déplace `Unshipped` vers `Shipped` et augmente la version :
  correctif (`1.0.x`) sans changement d'API, mineure (`1.x.0`) pour un ajout,
  majeure pour une rupture du SDK (§2).

### 2. La promesse couvre le SDK (question 2, défaut appliqué)

La promesse SemVer « une app compilée contre `1.x` tourne sur tout OS `1.y`,
y ≥ x » couvre ce qu'une app compile, c'est-à-dire les paquets que la règle de
frontière lui permet (`HOSAPP0030`), hors apps :

| Couvert (rupture = majeure) | Versionné avec, sans promesse (rupture possible en mineure) |
|---|---|
| `HackerOs.App.Abstractions`, `HackerOs.Simulation.Abstractions`, `HackerOs.Theming.Abstractions`, `HackerOs.AppSdk`, `.Blazor`, `.Terminal`, `.Web`, `.Web.Blazor`, `.FileView`, `.Icons`, `.VirtualKeyboard`, `HackerOs.Theming.Blazor` ; le contrat de build de `HackerOs.App.Build` (propriétés, éléments, codes `HOSAPP*`) ; le format du manifeste | `HackerOs.Platform.*`, `HackerOs.Windowing.*`, `HackerOs.Infrastructure.Browser`, `HackerOs.Taskbar.Blazor`, `HackerOs.MobileShell.Blazor`, `HackerOs.Server.Contracts`, `HackerOs.Ecosystem.Host`, les types publics des apps de base (`HackerOs.Apps.*`, `HackerOs.Commands`) |

- Une rupture hors du SDK reste écrite `*REMOVED*` et est décrite dans la
  section « Ruptures hors SDK » de [`sdk-packages.md`](../../framework/sdk-packages.md)
  avec la version qui l'apporte. Un hôte d'un autre dépôt peut devoir
  s'adapter à une mineure ; une app jamais.
- Ce qui est observable par une app reste couvert même quand le code est dans
  la plateforme : identifiants et intents des apps de base, chemins et formats
  des fichiers de l'OS documentés (`/etc`, `~/.config`, `/var/log`), commandes
  du shell et leurs options.
- **Pourquoi :** les chantiers `exec/*` vont retoucher la plateforme
  (exécution, serveur) ; figer aussi ses types imposerait une 2.0 du SDK pour
  un changement qu'aucune app ne voit.

### 3. Retraits faits avant le gel (question 1, défaut appliqué)

`BrowserClipboard` (et son module `wwwroot/clipboard.js`),
`IAppClipboardGateway.GetFileReferencesAsync` et `SetFileReferencesAsync` sont
supprimés. C'est le seul moment où ils ne coûtent rien : la 1.0 refuse déjà les
apps preview (contexte). La 1.0 ne contient aucune API `[Obsolete]`.

### 4. App de compatibilité gardée dans `Tests/fixtures`

- `Tests/fixtures/sdk-compat/src/Contoso.Sdk1Compat` : une app d'un « autre
  dépôt » (fenêtre `com.contoso.sdk1-window` et commande `sdk1-compat`) qui
  écrit et relit un fichier par `HackFile`, lit ses arguments et écrit sur la
  sortie standard. Ses `Directory.Build.*` vides coupent ceux du dépôt.
- `produce-fixture.sh` la construit contre les paquets d'un flux local, dans
  un dossier temporaire et un cache NuGet isolé, et écrit
  `packages/Contoso.Sdk1Compat.1.0.0.nupkg`, **committé et jamais
  reconstruit** (le `.gitignore` du dossier lève l'exclusion des `.nupkg`).
- `Tests/HackerOs.SdkCompatibility.Tests` (suite unitaire, sans navigateur) :
  le catalogue courant admet ses manifestes ; chaque type et membre HackerOS
  référencé par ses métadonnées existe encore ; chaque méthode est compilée par
  le JIT contre le SDK courant (`RuntimeHelpers.PrepareMethod`, qui lève
  `MissingMethodException` sur une signature changée) ; la commande, créée
  comme l'OS la crée, tourne. Vérifié : changer le type de retour de
  `HackFile.ReadAllTextAsync(string, CancellationToken)` fait échouer deux de
  ces tests.
- `HackerOs.Templates.E2E.Tests` : un hôte généré par `hackeros-host`, avec
  `PackageReference` vers ce paquet (source `hackeros-sdk-fixtures`), démarre
  dans Chromium ; la fenêtre et la commande de la 1.0 fonctionnent.
- À une 2.0 : `produce-fixture.sh` produit une app `2.0.0` ; celle de la 1.0
  devient un test de **refus** (majeure différente, ADR 0098 §3), puisque l'OS
  2.0 ne doit plus la charger.

### 5. Guide du dépôt tiers

[`building-on-hackeros.md`](../../framework/building-on-hackeros.md) décrit un dépôt qui
construit des apps et un hôte sur HackerOS : flux local, `nuget.config`,
version épinglée, modèles, apps et hôte, paquets d'apps, publication trimmée,
tests, montée de version et ce que la promesse couvre.

## Conséquences

- Les manifestes générés portent `sdkCompatibility.minimumVersion: 1.0.0`.
- Une app ou un hôte construit en `0.9.0-preview.*` doit être reconstruit
  contre les paquets `1.0.0` (le dépôt du jeu, ADR 0092).
- Chaque chantier qui touche une API publique met à jour `Unshipped` ; la
  publication suivante choisit la version d'après ce fichier (§1, §2).
- La suite unitaire `HackerOs.SdkCompatibility.Tests` tourne avec
  `dotnet test HackerOs.sln` ; le test E2E avec la suite des modèles.

## Options écartées

- **Promesse sur tous les paquets** (question 2) : sûr pour un hôte tiers,
  mais toute rupture dans la plateforme imposerait une 2.0 du SDK.
- **Garder les API obsolètes jusqu'à la 2.0** (question 1) : rien n'y gagne,
  les apps qui les utiliseraient sont déjà refusées par la 1.0.
- **Comparer les API par un outil (`Microsoft.DotNet.ApiCompat`)** contre les
  paquets 1.0.0 : utile plus tard pour toute la surface, mais ne prouve pas
  qu'une vraie app se charge et tourne ; l'analyseur d'API publique signale
  déjà les retraits.
