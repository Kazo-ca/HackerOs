> [English version](../../../en/wasm/framework/building-on-hackeros.md)

# Construire sur HackerOS depuis un autre dépôt

## Objet

Le guide d'un dépôt qui **utilise** HackerOS sans en copier le code : tout
projet qui veut ses propres apps et son propre hôte d'OS. **Aucun jeu sur ce repository** ([ADR 0135](../dev/adr/0135-no-game-in-this-repository.md)). Il part du SDK 1.0, gelé par le chantier `v3/15`
([ADR 0114](../dev/adr/0114-sdk-1-0-freeze.md)) ; la version courante est la **2.1.0**
(série du bureau et du menu contextuel, [ADR 0149](../dev/adr/0149-sdk-2-0-file-view-removal.md)
et [ADR 0161](../dev/adr/0161-sdk-2-0-implementation-adjustments.md), voir « Passer à la 2.0 » ;
la 1.2 vient de la série `exec`, [ADR 0132](../dev/adr/0132-exec-series-sdk-1-2-and-merge-rules.md) §2,
la 1.1 de la série v3.1, [ADR 0117](../dev/adr/0117-sdk-window-title-and-sdk-1-1.md) §5).

Détails des paquets et du mode paquet de `HackerOs.App.Build` :
[`sdk-packages.md`](sdk-packages.md). Écrire une app (API, exemples) :
[`sdk-developer-guide.md`](sdk-developer-guide.md) et
[`sdk-reference.md`](sdk-reference.md).

## Le plus court : le modèle `hackeros-solution`

Pour une app dans son propre dépôt, partez du modèle de solution
([ADR 0176](../dev/adr/0176-app-repository-template.md),
[ADR 0207](../dev/adr/0207-solution-template-implementation.md)) : app, hôte,
tests, sous-module HackerOS qui fixe la version, sources de l'OS en `Debug`,
paquets en `Release`, CI et publication dans le Hack Store par un tag. Tout est
dans [`app-repository.md`](app-repository.md). La suite de ce guide décrit
l'assemblage à la main, utile pour un dépôt qui a plusieurs apps ou son propre
hôte.

## 1. Ce que la version 1.0 promet

| Votre projet dépend de… | Garantie |
|---|---|
| le SDK : `HackerOs.App.Abstractions`, `HackerOs.Simulation.Abstractions`, `HackerOs.Theming.Abstractions`, `HackerOs.AppSdk*`, `HackerOs.Theming.Blazor`, le contrat de build de `HackerOs.App.Build`, le format du manifeste | une app compilée contre `1.x` tourne sur tout OS `1.y` (y ≥ x) ; une rupture n'arrive qu'avec une 2.0 |
| la plateforme : `HackerOs.Ecosystem.Host`, `HackerOs.Platform.*`, `HackerOs.Windowing.*`, `HackerOs.Infrastructure.Browser`, les types publics des apps de base | versionnée avec le SDK ; une mineure peut casser un **hôte**, jamais une app. Chaque rupture est listée dans [`sdk-packages.md`](sdk-packages.md#ruptures-hors-sdk) |

L'OS refuse une app compilée contre un SDK plus récent que le sien, ou d'une
autre majeure (état « incompatible » dans Paramètres, ADR 0098 §3). Une app
construite en `0.9.0-preview.*` doit être reconstruite contre la 1.0.

## 2. Récupérer les paquets

Depuis la 2.1.0, chaque release publie tous les paquets HackerOS (SDK,
plateforme, apps de base, `HackerOs.App.Build`, `HackerOs.Ecosystem.Host`,
`HackerOs.Templates`) sur **nuget.org**, avec leurs symboles et Source Link
([ADR 0173](../dev/adr/0173-automatic-releases-from-the-version-file.md)) :
une `PackageReference` à la version voulue suffit, sans `nuget.config`.

Le flux **local** sert à essayer une version pas encore publiée. Dans un clone
de HackerOS, sur la branche voulue :

```bash
git -C ../HackerSimulator checkout <tag ou branche>
../HackerSimulator/wasm2/HackerOs/build/pack-local.sh      # ou pack-local.ps1
# -> ../HackerSimulator/wasm2/HackerOs/artifacts/local-nupkg/*.2.2.0.nupkg
```

`--output <dossier>` écrit ailleurs (par exemple un dossier partagé de votre
dépôt). Le script purge cette version du cache NuGet global : un paquet
ré-empaqueté n'est jamais servi périmé.

## 3. Disposition du dépôt

```text
mon-depot/
├─ nuget.config                 # flux local seulement (build/nuget.config.sample)
├─ Directory.Build.props        # HackerOsVersion épinglée une fois
├─ MonDepot.sln
├─ src/
│  ├─ Contoso.Os/               # l'hôte (dotnet new hackeros-host)
│  ├─ Contoso.Notes/            # une app fenêtrée (hackeros-window)
│  └─ Contoso.Hello/            # une commande (hackeros-command)
└─ tests/
   └─ Contoso.Hello.Tests/
```

`nuget.config` (seulement avec le flux local) : la source `hackeros-local`
pointe vers le flux, et le `packageSourceMapping` ne prend les `HackerOs.*` que
de là :

```xml
<configuration>
  <packageSources>
    <add key="hackeros-local" value="../HackerSimulator/wasm2/HackerOs/artifacts/local-nupkg" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="hackeros-local"><package pattern="HackerOs.*" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
```

`Directory.Build.props` : une seule version pour tous les paquets HackerOS
(ils sont publiés ensemble, ADR 0098 §1). Les modèles écrivent
`<HackerOsVersion>` dans chaque projet ; retirez-la des projets pour ne garder
que celle-ci :

```xml
<Project>
  <PropertyGroup>
    <HackerOsVersion>2.2.0</HackerOsVersion>
  </PropertyGroup>
</Project>
```

## 4. Créer les projets

```bash
dotnet new install HackerOs.Templates::2.2.0
# ou, depuis le flux local : dotnet new install <flux>/HackerOs.Templates.<version>.nupkg
dotnet new hackeros-window  -n Contoso.Notes -o src/Contoso.Notes --appId com.contoso.notes --publisherId pub.contoso
dotnet new hackeros-command -n Contoso.Hello -o src/Contoso.Hello --appId com.contoso.hello --publisherId pub.contoso
dotnet new hackeros-service -n Contoso.Sync  -o src/Contoso.Sync  --appId com.contoso.sync  --publisherId pub.contoso
dotnet new hackeros-site    -n Contoso.Site  -o src/Contoso.Site  --appId com.contoso.site  --publisherId pub.contoso
dotnet new hackeros-host    -n Contoso.Os    -o src/Contoso.Os
dotnet sln add src/*/*.csproj
```

Chaque projet d'app référence `HackerOs.AppSdk*` et `HackerOs.App.Build`
(`PrivateAssets="all"`). Au build, `HackerOs.App.Build` écrit `manifest.json`
depuis la classe `[AppManifest]`, avec la version du SDK comme minimum, et
refuse une référence hors du SDK (`HOSAPP0030`) ou une injection d'un service
de la plateforme (`HOSAPP0031`).

## 5. Composer l'hôte

L'hôte `hackeros-host` est une PWA Blazor WebAssembly complète : tout l'OS vient
de `HackerOs.Ecosystem.Host`, les apps sont ses références.

```xml
<!-- src/Contoso.Os/Contoso.Os.csproj -->
<ItemGroup>
  <!-- Vos apps, en projets… -->
  <ProjectReference Include="../Contoso.Notes/Contoso.Notes.csproj" />
  <ProjectReference Include="../Contoso.Hello/Contoso.Hello.csproj" IsSystemApp="true" />
  <!-- …ou en paquets d'apps. -->
  <PackageReference Include="Contoso.Tools" Version="2.3.0" />
  <!-- Les apps de base que vous gardez (retirez les autres). -->
  <PackageReference Include="HackerOs.Apps.Terminal" Version="$(HackerOsVersion)" IsSystemApp="true" />
</ItemGroup>
```

- `IsSystemApp="true"` : app système (non désinstallable, capacités
  dangereuses accordées, ADR 0048, ADR 0049).
- Le build agrège les manifestes de toutes les apps dans
  `BuiltInApplications.json`, embarqué dans l'assembly de l'hôte ;
  `HostAppCatalog.Load` le relit et les assemblies des apps sont chargées
  paresseusement.
- `dotnet run` sert l'hôte ; le premier démarrage demande de créer
  l'administrateur.

## 6. Distribuer une app en paquet

`dotnet pack` d'un projet d'app produit un paquet qui embarque ses manifestes
(`hackeros/manifests/`), leurs traductions et un `buildTransitive` qui les
déclare à l'hôte : un hôte n'a jamais besoin de son code source. Donnez au
paquet un `PackageId` hors de l'espace `HackerOs.*`, réservé au flux HackerOS
par le `packageSourceMapping`.

## 7. Tester

- Une commande se teste sans navigateur : créer la commande avec son manifeste
  (`AppDefinitionMapper.ToManifest(new MonApp())`), une
  `TerminalExecutionContext` sur des passerelles factices, et
  `HackExecutionContext.Enter(app)` autour de `ExecuteAsync`
  ([`sdk-developer-guide.md`](sdk-developer-guide.md), test d'une commande).
- Un hôte se teste comme `HackerOs.Templates.E2E.Tests` : `dotnet run` de
  l'hôte, puis Playwright (création de l'administrateur, menu Applications,
  Terminal).
- Pour vérifier qu'une montée de version ne casse pas vos apps déjà publiées,
  gardez comme HackerOS un paquet d'app compilé contre l'ancienne version et
  chargez-le dans l'hôte courant (`Tests/fixtures/sdk-compat`, ADR 0114 §4).

## 8. Publier l'hôte

```bash
dotnet publish src/Contoso.Os -c Release
```

Le modèle d'hôte active `PublishTrimmed` en mode `partial` : le framework est
élagué, les assemblies HackerOS et toutes les apps restent entières (racines
ajoutées par `HackerOs.App.Build`, ADR 0113). La sortie `wwwroot` est un site
statique ; le service worker ne s'enregistre que hors de `localhost`. Voir
[`trimmed-publish.md`](../dev/trimmed-publish.md).

## 9. Monter de version

1. Produire le flux de la nouvelle version (§2).
2. Changer `HackerOsVersion` dans `Directory.Build.props`.
3. Mineure : reconstruire ; lire « Ruptures hors SDK » de
   [`sdk-packages.md`](sdk-packages.md) si vous avez un hôte. Majeure :
   reconstruire **toutes** les apps, l'OS refuse celles de l'ancienne majeure.
4. Les manifestes régénérés portent le nouveau minimum : une app reconstruite
   ne se charge plus sur un OS plus ancien.

### Passer à la 2.0

Une majeure : l'OS 2.0 refuse une app compilée contre 1.x, et un hôte qui en
référence une ne se construit plus (`HOSAPP0021`). Toute app doit être
recompilée contre les paquets 2.x (2.2.0 aujourd'hui ; vérifié par `Tests/fixtures/sdk-compat`,
où les apps 1.0.0 et 1.1.0 sont refusées et l'app 2.0.0 tourne).

- **Seule rupture** : le paquet `HackerOs.AppSdk.FileView` n'existe plus. Une
  app qui affichait `FileView` affiche `HackFileView` (contrôle de fichiers
  fourni par l'OS, `HackerOs.AppSdk.Blazor.Files`) ; `FileTransfer` devient
  `HackFileOperations` (`HackerOs.AppSdk.IO`). Le tableau complet est dans
  [`sdk-packages.md`](sdk-packages.md#migration-dune-app-1x).
- **Menu contextuel de l'OS** : `Menus.Attach(nom, élément)` sur n'importe quel
  élément, `Menus.Define` en C#, sous-menus, clavier et appui long
  (ADR 0150, [`context-menu.md`](context-menu.md)).
- **Entrées dans les menus des fichiers** : `[RegisterFileContextMenu]` dans la
  classe `[AppManifest]`, capacité `shell.context-menu` ; l'entrée lance l'app
  avec un intent et les chemins (ADR 0151, 0159).
- **Bureau** : `HackDesktopSettings`, raccourcis `.desktop` (`HackDesktopEntry`,
  ADR 0152, 0160, [`desktop.md`](../dev/desktop.md)).
- **Fournir un contrôle de l'OS** : une app peut fournir `hackeros.file-view`
  et `hackeros.file-dialogs` ; l'administrateur choisit le fournisseur
  (ADR 0156, 0158, [`embedded-components.md`](embedded-components.md)).
- **Aussi** : `IAppEventGateway.PublishAsync` (ADR 0145) et
  `HackCrossOriginIsolation` (ADR 0146), prévus pour une 1.3 jamais publiée.

Les ruptures hors SDK d'un hôte (explorateur de fichiers, éditeur de code,
`IShellIconProvider` de l'hôte) sont dans
[`sdk-packages.md`](sdk-packages.md#ruptures-hors-sdk).

### Ce que la 1.2 ajoutait

Une mineure : une app compilée contre 1.0.0 ou 1.1.0 tournait sans changement
sur l'OS 1.2. Ses ajouts font partie de la 2.0.

- **Tâches lourdes hors de la fenêtre** : `HackBackground.RunAsync` avec une
  méthode statique et des arguments sérialisables, ou une classe
  `HackBackgroundJob<TInput, TResult>` ; progression, annulation et
  `CheckpointAsync` par `HackBackgroundContext`. L'analyseur `HOSBG0001` à
  `HOSBG0004`, livré dans `HackerOs.AppSdk`, refuse une lambda qui capture
  (ADR 0128, 0140, [`background-jobs.md`](background-jobs.md)).
- **Limites par processus** : `HackProcess.GetResourceLimitsAsync`,
  `SetResourceLimitAsync`, `HackResourceLimits` ; `EMFILE`
  (`FileSystemErrorCode.TooManyOpenFiles`) et `EAGAIN`
  (`ProcessLimitExceededException`) à traiter comme des erreurs d'E/S
  ordinaires ; nouvelles raisons de sortie `CpuLimitExceeded`,
  `MemoryLimitExceeded`, `OutOfMemory` (ADR 0125, 0139,
  [`process-limits.md`](../dev/process-limits.md)).
- **OOM killer** : `AppDefinition.OomScoreAdjust` pour qu'une app soit tuée
  plus tard (ou plus tôt) quand la mémoire manque ; l'OOM killer est coupé par
  défaut (ADR 0126, 0142, [`oom-killer.md`](../dev/oom-killer.md)).
- **Priorité** : `HackProcess.Nice`, `BasePriority` (ADR 0127,
  [`process-priority-and-unresponsive-windows.md`](../dev/process-priority-and-unresponsive-windows.md)).
- **Commandes en worker qui écoutent des événements** :
  `[assembly: HackWireContext(typeof(MonContexteJson))]` pour que
  `HackEvents` et `Watch` passent par la passerelle distante (ADR 0124, 0138,
  [`worker-processes.md`](../dev/worker-processes.md)).

La liste exacte et la rupture hors SDK d'un hôte (constructeur
d'`EcosystemBootCoordinator`) sont dans
[`sdk-packages.md`](sdk-packages.md#sdk-12).

### Ce que la 1.1 ajoute

Une mineure : une app compilée contre 1.0.0 tourne sans changement sur l'OS
1.1 (vérifié par l'app figée de `Tests/fixtures/sdk-compat`). Pour utiliser les
ajouts, passer `HackerOsVersion` à `1.1.0` et reconstruire ; l'app exige alors
un OS 1.1 ou plus récent.

- **Titre de fenêtre** : `WindowAppBase.SetWindowTitleAsync`, `WindowTitle`,
  `FormatWindowTitle("rapport.txt", modified: true)` → `*rapport.txt — Mon app`
  (ADR 0117, 0123, [`window-model.md`](window-model.md) §4).
- **Taille des fenêtres retenue** : `WindowOptions.RememberGeometry = false`
  pour qu'une fenêtre ne reprenne pas sa dernière taille (ADR 0116, 0122,
  [`window-geometry.md`](../dev/window-geometry.md)).
- **Comptes et mots de passe** : `HackUserAccounts` (`ListAsync`,
  `ChangePasswordAsync`, `ResetPasswordAsync` avec la capacité `users.manage`),
  `TerminalExecutionContext.ReadSecretLineAsync` pour une saisie sans écho
  (ADR 0120, [`sensitive-settings.md`](../dev/sensitive-settings.md)).
- **Applications par défaut de l'utilisateur** : `HackApps.SetDefaultHandlerAsync`,
  `ResetDefaultHandlerAsync`, `GetDefaultHandlersAsync` (ADR 0121,
  [`user-file-associations.md`](../dev/user-file-associations.md)).

La liste exacte et les ruptures hors SDK d'un hôte (trois changements de
`HackerOs.Platform.Blazor` autour de la géométrie des fenêtres) sont dans
[`sdk-packages.md`](sdk-packages.md#sdk-11).

## Décisions

- **nuget.org depuis la 2.1.0** (ADR 0173) ; le flux local de l'ADR 0098 §2
  reste pour une version pas encore publiée.
- **Le modèle `hackeros-solution`** (ADR 0176, ADR 0207) est le départ
  conseillé pour une app dans son dépôt.
- **Version épinglée une fois** dans le dépôt tiers : les paquets HackerOS
  vont ensemble, mélanger deux versions n'est pas pris en charge.

## Tâches

- [x] Promesse de compatibilité et refus des autres majeures.
- [x] Flux local, `nuget.config`, version épinglée.
- [x] Modèles, hôte, apps en projets et en paquets.
- [x] Tests, publication trimmée, montée de version.
- [x] SDK 1.1 : ajouts et lien vers les ruptures hors SDK (`v3.1/4-sdk-1-1`).
- [x] SDK 1.2 : ajouts et lien vers la rupture hors SDK (`exec/10-final`).
- [x] SDK 2.0 : version courante, rupture, migration et ajouts (`desk/6-sdk-2`).
- [x] Paquets sur nuget.org et renvoi vers le modèle `hackeros-solution` (`todo/1-hackstore-solution-template`).
