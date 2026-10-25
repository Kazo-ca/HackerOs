# Compiler, lancer et tester HackerOS

## Objet

Comment compiler la solution `wasm2/HackerOs`, lancer l'OS dans le navigateur
et exécuter les tests, y compris dans un conteneur Linux sans outils
préinstallés. Pour la structure du code, voir
[`project-overview.md`](project-overview.md).

Pour publier ensemble WASM à la racine et legacy sous `/legacy/`, voir
[le guide GitHub Pages](github-pages-deployment.md) (builds et caches indépendants).

## Prérequis

- **SDK .NET 10** (`global.json` : `10.0.100`, `rollForward: latestFeature`).
  Dans un conteneur sans .NET :

  ```bash
  curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir ~/.dotnet
  export PATH="$HOME/.dotnet:$PATH"
  ```

- **Node.js** seulement pour régénérer le bundle xterm.js
  ([`terminal-emulator.md`](terminal-emulator.md)) ou importer des icônes
  (`Tools/icon-import`). Le build .NET n'en a pas besoin.
  La publication combinée du legacy utilise séparément Node 20 et npm ; voir
  [GitHub Pages](github-pages-deployment.md).
- **Playwright et Chromium** seulement pour les suites E2E.

## Compiler

Toutes les commandes se lancent depuis `wasm2/HackerOs`.

```bash
dotnet build HackerOs.sln
```

Le build applique les règles du projet (avertissements en erreurs, actifs en
ligne interdits, manifestes régénérés, frontière du SDK) : voir
[`project-overview.md`](project-overview.md#règles-vérifiées-au-build). Un
`manifest.json` d'app modifié par le build est un changement à committer.

Le workflow de vérification installe explicitement `ripgrep` avant le scan
supplémentaire des sources Razor ; cet outil n'est pas garanti sur les runners
GitHub Ubuntu.
Ce scan exclut le document de démarrage de l'hôte et autorise uniquement les
trois lignes existantes de références de scripts ASP.Hack, à leurs chemins
exacts. Les autres lignes de ces vues et toutes les références de scripts
des composants ordinaires restent contrôlées.

Les constantes publiques des fixtures utilisent des échappements `\n` explicites :
les chaînes brutes multilignes héritent des fins de ligne du checkout et produisent
des valeurs différentes entre Windows et Linux, ce qui invalide les baselines
`PublicAPI.Unshipped.txt`.

### Mesure des ressources (ADR 0066)

Deux propriétés MSBuild choisissent le code de mesure livré par les hôtes
(`build/HackerOs.ResourceAccounting.targets`, importé par les trois hôtes) :

| Propriété | Défaut | Effet à `false` |
|---|---|---|
| `HackerOsCpuAccounting` | `true` | mesure du temps CPU (ADR 0067) coupée ; option d'exécution `HackerOs.ResourceAccounting.Cpu.IsSupported=false` |
| `HackerOsMemoryAccounting` | `true` | mesure de la mémoire (ADR 0068) coupée ; option `HackerOs.ResourceAccounting.Memory.IsSupported=false` |

Les deux à `false` : `HackerOs.Platform.ResourceAccounting` n'est plus
référencé, son code n'est pas dans le binaire.

```bash
dotnet build HackerOs.sln -p:HackerOsCpuAccounting=false -p:HackerOsMemoryAccounting=false
```

En ligne de commande (ou dans un `Directory.Build.props`), la propriété vaut
pour tout le graphe ; dans le seul `.csproj` de `test/test` ou du serveur, elle
ne coupe que l'option d'exécution de cet hôte. Au démarrage, la section
`HackerOs:ResourceAccounting` de `appsettings.json` coupe une mesure sans
recompiler : voir [`resource-accounting.md`](resource-accounting.md).

### Conteneur Linux : contournements connus

| Symptôme | Cause | Contournement |
|---|---|---|
| `NU1902` sur `Microsoft.Build.Tasks.Git` traité en erreur | avis de sécurité NuGet sur une dépendance de build | `export NuGetAudit=false` (ou `-p:NuGetAudit=false`) |
| `NU1301` sur la source `local-nupkg` | le dossier du feed local manque sur un checkout neuf | `mkdir -p artifacts/local-nupkg` pour le mode `ProjectReference` ; le workflow de vérification des packages remplit ce feed avant son restore. Le chemin du `nuget.config` utilise `/`, compatible Windows/Linux. |
| Page blanche de `test/test` ou de `HackerOs.Server` lancé avec `dotnet run --no-build --urls …` (journal : « Static Web Assets are not enabled ») | sans profil de lancement, l'hôte démarre en `Production` et ne sert pas les actifs des projets référencés hors d'une publication | `ASPNETCORE_ENVIRONMENT=Development` |
| `IOException … being used by another process` dans `obj/…/compressed/` d'Ecosystem | `test/test` et `HackerOs.Server` compilés en même temps : ils partagent Ecosystem | compiler la solution une fois, puis lancer les hôtes avec `--no-build` |

## Lancer l'OS

| Hôte | Commande | Usage |
|---|---|---|
| PWA autonome | `dotnet run --project OS/HackerOs.Ecosystem` | L'hôte livré, sans serveur. |
| Hôte de débogage | `dotnet run --project test/test` | Débogueur WebAssembly du navigateur ou de l'IDE ([`old/webassembly-debugging.md`](old/webassembly-debugging.md)). `dotnet run --project test/test -- --test-demo` affiche le bouton qui crée les fichiers de test E2E ([`old/e2e-test-demo-mode.md`](old/e2e-test-demo-mode.md)). |
| Serveur optionnel | `dotnet run --project Server/HackerOs.Server` | Synchronisation, comptes, relais réseau, et l'OS de l'Ecosystem servi tel quel en WebAssembly ([`server-hosting.md`](server-hosting.md)). |

Publier la PWA (Release trimmée, voir [Publier en Release trimmée](#publier-en-release-trimmée)) :

```bash
dotnet publish OS/HackerOs.Ecosystem -c Release -o artifacts/ecosystem
python3 -m http.server -d artifacts/ecosystem/wwwroot 8080   # n'importe quel serveur statique
```

Au premier démarrage, l'OS demande de créer le profil administrateur local,
puis affiche le bureau. Les données vivent dans l'IndexedDB du navigateur :
effacer les données du site remet la machine à neuf.

## Vérifier les trois hôtes

Une commande compile, lance et parcourt les trois hôtes
([`execution-isolation-plan.md`](plans/execution-isolation-plan.md), section 5.2) :

```bash
build/verify-hosts.sh                 # tout : build, publications, neuf parcours
build/verify-hosts.sh --skip-build    # réutilise les sorties déjà compilées
build/verify-hosts.sh --only server,test-host
```

Prérequis : SDK .NET 10 (`~/.dotnet` est ajouté au `PATH` s'il existe),
`python3` (serveur statique de la PWA publiée), `curl`, `setsid`, et un
Chromium que Playwright peut lancer (dans un conteneur cloud,
`/opt/pw-browsers/chromium` est pris automatiquement, voir
[Chromium pour Playwright](#chromium-pour-playwright)). Le script applique
lui-même les contournements Linux ci-dessus, compile une seule fois puis lance
les hôtes avec `--no-build`, et pose `ASPNETCORE_ENVIRONMENT=Development` pour
les hôtes lancés par `dotnet run` ([`old/common-pitfalls.md`](old/common-pitfalls.md) §4).

Déroulé :

1. `dotnet build` (Debug) d'`OS/HackerOs.Ecosystem`, `test/test`,
   `Server/HackerOs.Server` et `Tests/HackerOs.HostSmoke.Tests` ;
   `dotnet publish` (Release) d'Ecosystem, deux fois (réglages par défaut,
   puis mesures coupées au build).
2. Pour chaque parcours, l'hôte démarre sur un port libre de `127.0.0.1`
   (`127.0.0.2` pour les deux parcours de l'isolation) dans
   son propre groupe de processus : Ecosystem publié derrière un serveur
   statique Python, les deux autres par `dotnet run --no-build`. La base, les
   blobs et les sauvegardes du serveur vont dans un dossier jetable : le
   `Server/HackerOs.Server/hackeros.db` suivi par git n'est jamais modifié.
3. `Tests/HackerOs.HostSmoke.Tests` ouvre l'hôte dans Chromium (contexte
   neuf) : création du profil, rechargement et écran « Sign in », bureau
   (styles scopés appliqués à la barre des tâches, puis au cadre des fenêtres :
   `display: flex` calculé, sinon le bundle `{Projet}.styles.css` de l'hôte
   manque), Terminal avec `ps` puis `cat /proc/self/status` (valeurs de `CpuSource:` et
   `MemSource:` vérifiées ; chaque sortie est lue une fois l'invite suivante
   affichée, car une commande dans un Web Worker écrit en plusieurs messages), System Monitor (le processus du terminal est
   listé), fermeture de chaque fenêtre. Sur le parcours `server`, l'icône du
   serveur de la zone de notification doit passer seule à « Connected, not
   identified », puis à « Connected » après « Identify » et la création d'un
   compte depuis son panneau (ADR 0134) ; sur les autres hôtes, aucune icône
   de serveur. Toute erreur de la console ou de la page fait échouer le
   parcours, comme toute ligne `fail:` ou « Unhandled exception » du journal de
   l'hôte (erreurs d'une requête de l'API, invisibles côté navigateur).
4. L'hôte est arrêté (SIGTERM au groupe, SIGKILL après 15 s), puis le
   parcours suivant commence. Le script sort avec un code non nul au moindre
   échec et affiche une ligne par étape :

```text
== HackerOS host verification (9d26a77) ==
build HackerOs.Ecosystem         PASS
build test                       PASS
build HackerOs.Server            PASS
build HackerOs.HostSmoke.Tests   PASS
publish ecosystem                PASS
publish ecosystem-build-off      PASS
ecosystem                        PASS  23s
ecosystem-build-off              PASS  21s
ecosystem-settings-off           PASS  21s
test-host                        PASS  38s
server                           PASS  34s
RESULT: PASS
```

| Parcours | Hôte | Mesures (ADR 0066) | `CpuSource:` / `MemSource:` attendus |
|---|---|---|---|
| `ecosystem` | PWA publiée | build et réglage par défaut | `measured` |
| `ecosystem-build-off` | PWA publiée avec `-p:HackerOsCpuAccounting=false -p:HackerOsMemoryAccounting=false` | coupées au build ; aucun `HackerOs.Platform.ResourceAccounting*` ne doit être publié | `simulated` |
| `ecosystem-settings-off` | même publication que `ecosystem`, `wwwroot/appsettings.json` réécrit avec `Enabled: false` pour `Cpu` et `Memory` | coupées au démarrage | `simulated` |
| `ecosystem-limits-off` | même publication que `ecosystem`, `wwwroot/appsettings.json` réécrit avec `HackerOs:ResourceLimits:Enabled: false` (ADR 0125) | build et réglage par défaut | `measured` |
| `ecosystem-oom` | même publication que `ecosystem`, `wwwroot/appsettings.json` réécrit avec `HackerOs:Hardware:MemoryMiB` = `${OOM_MEMORY_MIB:-512}` (ADR 0142 §11) | build et réglage par défaut | `measured` |
| `ecosystem-isolated` | même publication que `ecosystem`, servie sur `127.0.0.2` : le service worker s'enregistre et pose COOP/COEP (ADR 0131, 0143) | build et réglage par défaut | `measured` |
| `ecosystem-isolation-off` | idem, `wwwroot/appsettings.json` réécrit avec `HackerOs:CrossOriginIsolation` = `off` | build et réglage par défaut | `measured` |
| `test-host` | `test/test` | réglage de l'hôte (activées) | `measured` |
| `server` | `HackerOs.Server` (sert l'OS de l'Ecosystem en WebAssembly, ADR 0130) | `appsettings.json` de l'Ecosystem (activées) | `measured` |

Sur un arbre sans les interrupteurs de l'ADR 0066
(`build/HackerOs.ResourceAccounting.targets`, section
`HackerOs:ResourceAccounting` d'`appsettings.json`), les deux parcours `-off`
sont marqués `SKIP` et les sources ne sont pas vérifiées.

Chaque parcours vérifie aussi les limites de processus (`exec/2`,
[`process-limits.md`](process-limits.md)) : la ligne `# enforcement:` de
`/proc/self/limits`, puis `sh -c 'ulimit -S -t 1; stress --yield'` qui doit
sortir avec 152 et « CPU time limit exceeded » là où le CPU est mesuré, et
normalement ailleurs. Dans `ecosystem-limits-off`, `ulimit -n 100` doit
répondre « cannot modify limit ».

Chaque parcours vérifie l'isolation cross-origin (`exec/9`,
[`worker-processes.md`](worker-processes.md#isolation-cross-origin-adr-0131-adr-0143)) :
`crossOriginIsolated` dans la page, la ligne `cross-origin-isolated:` de
`/proc/hackeros/workers`, `sample-cmd --processes` (réponse synchrone si la
page est isolée, refus sinon) et `seq 30000 | wc -l`. Isolés : `ecosystem-isolated`,
`test-host` et `server` (intergiciel). Non isolés : les parcours de l'Ecosystem
sur `127.0.0.1` (pas de service worker sur cette adresse) et
`ecosystem-isolation-off`. Un parcours qui réécrit `appsettings.json` recalcule
son empreinte dans `service-worker-assets.js`, sinon l'installation du service
worker échoue sur `127.0.0.2`.

Le parcours `ecosystem-oom` (`exec/3`, [`oom-killer.md`](oom-killer.md))
active l'OOM killer par `sudo tee /etc/hackeros/memory.config`, lance
`stress --vm 1 --vm-bytes` calculé depuis `/proc/meminfo`, attend la sortie
137 et « Killed », puis vérifie la ligne `Out of memory: Killed process` de
`dmesg` et `oom_kill 1` dans `/proc/vmstat`.

Journaux, publications et captures d'écran des échecs :
`artifacts/verify-hosts/` (ignoré par git). Sans `HACKEROS_SMOKE_URL`, que
pose le script, le test de démarrage est marqué « skipped » : un
`dotnet test HackerOs.sln` n'en dépend pas. Pour le lancer contre un hôte
démarré à la main :

```bash
HACKEROS_SMOKE_URL=http://127.0.0.1:5000 dotnet test Tests/HackerOs.HostSmoke.Tests \
  --logger "console;verbosity=detailed"
```

Variables facultatives : `HACKEROS_SMOKE_HOST` (nom affiché),
`HACKEROS_SMOKE_EXPECT_CPU_SOURCE` / `HACKEROS_SMOKE_EXPECT_MEM_SOURCE`
(valeur attendue dans `/proc/self/status`), `HACKEROS_SMOKE_ARTIFACTS`
(dossier des captures), `HACKEROS_SMOKE_EXPECT_ISOLATION` (`yes` ou `no`).

Chaque PR `rebuild/5-*` colle la sortie de `build/verify-hosts.sh`.

## Hébergement et en-têtes d'isolation

L'OS se sert de l'isolation cross-origin quand la page l'a (ADR 0131, 0143) et
marche sans. `HackerOs:CrossOriginIsolation` (`auto`, `on`, `off`) dans
`appsettings.json` de chaque hôte ; `auto` vaut `on` depuis l'audit d'`exec/9`.

- **Ecosystem sur un hébergement statique** : rien à configurer. Le service
  worker de la PWA ajoute `Cross-Origin-Opener-Policy: same-origin` et
  `Cross-Origin-Embedder-Policy: credentialless` (`require-corp` pour un
  navigateur qui ignore `credentialless`, avec un rechargement unique). Il
  faut un contexte sécurisé (HTTPS, ou une adresse de bouclage autre que
  `localhost`/`127.0.0.1`, sur lesquelles `index.html` n'enregistre pas le
  service worker) : en local, servir la publication sur `127.0.0.2`.
- Un hébergeur qui pose déjà ces en-têtes convient aussi ; un proxy qui les
  retire donne le repli exact (`/proc/hackeros/workers` dit pourquoi).
- **`test/test` et `HackerOs.Server`** posent les en-têtes eux-mêmes (intergiciel
  dans `Program.cs`).
- Après une modification de `wwwroot/appsettings.json` d'une publication,
  recalculer son empreinte dans `service-worker-assets.js` (voir
  `rehash_settings` dans `build/verify-hosts.sh`).

## Publier en Release trimmée

`dotnet publish` de l'Ecosystem en Release est élagué par ILLink
(`PublishTrimmed=true`, `TrimMode=partial`, ADR 0106 et 0113) : le framework est
élagué, les assemblies HackerOS et les apps restent entières. Une commande
publie, mesure et teste cette publication ([`trimmed-publish.md`](trimmed-publish.md)) :

```bash
build/publish-trimmed.sh             # publication, tailles, tests E2E de la publication
build/publish-trimmed.sh --compare   # publie aussi sans trimming et affiche les deux tailles
build/publish-trimmed.sh --skip-build
```

| Publication de l'Ecosystem (2026-10-17) | Fichiers `.wasm` | Taille | Brotli |
|---|---|---|---|
| avant `v3/14` (`PublishTrimmed=false`) | 273 | 50,9 Mio | 13,5 Mio |
| après `v3/14` | 134 | 26,5 Mio | 8,0 Mio |

Le script échoue sur toute alerte `IL…`, sert la publication sur `127.0.0.1`
et sur `127.0.0.2` (pour le service worker) et lance
`Tests/HackerOs.TrimmedPublish.E2E.Tests` (quatre tests, environ 3 min). C'est une porte de
fusion locale depuis `v3/14`. Sans `HACKEROS_TRIMMED_URL`, la suite est ignorée
par `dotnet test HackerOs.sln`.

Piège : la conversion WebCIL du SDK est incrémentale par dates de fichiers ; une
publication `-p:PublishTrimmed=false` faite après une publication trimmée peut
reprendre les assemblies élaguées. Supprimer d'abord
`OS/HackerOs.Ecosystem/obj/Release/net10.0/{linked,webcil,tmp-webcil}` (le script
le fait).

## Produire les paquets locaux

```bash
build/pack-local.sh            # ou build/pack-local.ps1 sous Windows
```

Empaquette en Release chaque projet qui importe `Platform/Packaging.props` (SDK,
plateforme, apps de base, `HackerOs.App.Build`, `HackerOs.Ecosystem.Host`,
`HackerOs.Templates`) dans `artifacts/local-nupkg`, à la version de
`build/Versions.props`, et retire cette version du cache NuGet global pour qu'un
autre dépôt ne reçoive pas d'anciens octets. Rien n'est publié sur nuget.org.
Détails : [`sdk-packages.md`](../framework/sdk-packages.md).

## Tester

La vérification automatique sur `main` lance les tests sans navigateur. Après
une compilation Release, le même sélecteur s'utilise localement :

```powershell
./build/run-test-suite.ps1 -Suite NonBrowser
./build/run-test-suite.ps1 -Suite Browser -ListOnly
./build/run-test-suite.ps1 -Suite Browser
```

Le script choisit les projets référençant `Microsoft.NET.Test.Sdk`, puis sépare
ceux dépendant de `Microsoft.Playwright` ou du support E2E partagé. Il exécute
les projets séquentiellement, conserve tous les résultats TRX et échoue si un
projet échoue. Il attend des assemblies déjà compilées (`--no-build`,
`--no-restore`). Actuellement, 41 projets sont sans navigateur et six utilisent
Playwright. Un nouveau projet Playwright est ainsi exclu automatiquement du
contrôle sans navigateur.

Dans GitHub Actions, les suites Playwright se lancent uniquement via
**Verify HackerOS browser tests (manual)** (`verify-browser-tests.yml`,
`workflow_dispatch`). Elles n'appartiennent pas au déploiement Pages.
Les scénarios conditionnels des hôtes déjà lancés ou de la publication élaguée
gardent leurs variables d'activation documentées ci-dessous.

Pour demander explicitement toutes les suites, y compris Playwright :

```bash
dotnet test HackerOs.sln
```

| Suites | Contenu |
|---|---|
| `Tests/*.Tests` (hors E2E) | Tests unitaires et de composants (xUnit, bUnit), un projet par projet de code. Rapides, sans navigateur. |
| `Tests/HackerOs.AppSdk.Web.Tests` | ASP.Hack : routeur, pipeline d'un site compilé avec le générateur de routes, générateur piloté sur des extraits (`HOSWEB*`), page hôte et `blazor.boot.json` d'un site Blazor. |
| `Tests/HackerOs.AppSdk.Web.Blazor.Tests` | Sites Blazor : routeur (`HackSiteRouter`), gestionnaire de navigation de l'onglet, `HackWebSite`. |
| `Tests/HackerOs.Samples.Intranet.Tests` | Exemple Intranet en mémoire (`HackWebHost.CreateApplication`) : vues `.cshtml` et `.razor`, `wwwroot`, session, anti-falsification, CRUD par formulaires, API REST, page hôte du tableau Blazor. Le parcours dans Chromium est `BrowserIntranetTests` (UI E2E). |
| `Tests/HackerOs.Tools.ManifestGenerator.Tests` | Builds MSBuild réels des projets `Tests/ManifestGenerator.Fixtures.*` : génération, agrégation, frontière du SDK. Plus lents. |
| `Tests/HackerOs.E2E.Tests`, `HackerOs.UI.E2E.Tests`, `HackerOs.Pwa.E2E.Tests` | Playwright contre l'OS lancé dans Chromium. Pièges connus : [`old/common-pitfalls.md`](old/common-pitfalls.md) §4 à §6. |
| `Tests/HackerOs.Templates.E2E.Tests` | Modèles `dotnet new` (ADR 0098 §6) : empaquette tout avec `build/pack-local.sh` (ou prend le flux de `HACKEROS_LOCAL_FEED`), génère et compile chaque modèle hors du dépôt avec un cache NuGet isolé, puis lance l'hôte généré dans Chromium avec une app fenêtrée et une commande générées, et un autre qui vérifie qu'un hôte référençant les apps compilées contre les SDK 1.0.0 et 1.1.0 ne se construit pas (`HOSAPP0021`), puis qu'avec l'app compilée contre le SDK 2.0.0 il démarre, montre `HackFileView` dans sa fenêtre et exécute sa commande (ADR 0114, ADR 0132 §1, ADR 0161 §3). |
| `Tests/HackerOs.SdkCompatibility.Tests` | Apps compilées contre les SDK 1.0.0, 1.1.0 et 2.0.0, gardées dans `Tests/fixtures/sdk-compat` (ADR 0114 §4, ADR 0132 §1, ADR 0161 §3) : le catalogue refuse les apps 1.x (`catalog.sdk.incompatible`) et admet l'app 2.0 ; pour celle-ci, références, compilation JIT de chaque méthode et exécution de la commande contre le SDK courant (2.0.3). Sans navigateur, lancé par `dotnet test HackerOs.sln`. |
| `Tests/HackerOs.HostSmoke.Tests` | Démarrage d'un hôte déjà lancé, piloté par `build/verify-hosts.sh` (voir plus haut) ; « skipped » sans `HACKEROS_SMOKE_URL`. |

Tester un seul projet ou un seul test :

```bash
dotnet test Tests/HackerOs.AppSdk.Tests
dotnet test Tests/HackerOs.Platform.Core.Tests --filter "FullyQualifiedName~ShellInterpreterTests"
```

### Chromium pour Playwright

Chaque version de `Microsoft.Playwright` attend une révision précise de
Chromium (ou Google Chrome pour `Channel = "chrome"`). Toutes les suites
lancent le navigateur par `ChromiumLauncher` (`Tests/HackerOs.E2E.Support`) :

1. `HACKEROS_CHROMIUM_EXECUTABLE`, s'il est posé, est utilisé tel quel ;
2. sinon, le lancement prévu par le test ;
3. s'il échoue, nouvel essai avec `$PLAYWRIGHT_BROWSERS_PATH/chromium` puis
   `/opt/pw-browsers/chromium` (le lien des conteneurs cloud), sans canal.

Dans un conteneur cloud, **ne jamais lancer `playwright install`** : la
révision fournie sous `/opt/pw-browsers` sert de repli.

Pour tester une app sans OS, `Tests/HackerOs.Tests.Support` fournit
`MinimalAppExecutionContext` et des passerelles factices (fichiers, horloge,
journaux, processus, intentions, environnement, shell). Exemple : voir le
[guide du SDK](../framework/sdk-developer-guide.md#8-tester-une-app).

### Fixtures de montée de version IndexedDB

ADR 0099 §4. `Tests/fixtures/indexeddb/` contient un vidage de la base
`hackeros` par version de schéma déjà publiée. `IndexedDbUpgradeFixtureTests`
(`HackerOs.UI.E2E.Tests`) recrée chaque base à son ancienne version avec
`indexedDB.open(nom, ancienneVersion)`, depuis une page de la même origine
qui ne démarre pas l'OS (servie par Playwright lui-même ; l'attente de l'hôte
se fait en HTTP, car ouvrir la page de l'OS créerait une base v4 neuve), puis
démarre l'OS courant et vérifie :

- l'écran « Sign in » (pas l'accueil « Create administrator ») et la connexion
  avec le mot de passe de l'administrateur du vidage : le compte a survécu ;
- `cat` du fichier du vidage dans le Terminal : entrées, liens et contenu ;
- `cat /etc/hackeros/appearance.config` contient `accent=cyan` : le réglage
  rangé dans l'ancien magasin `settings` a été repris par la migration vers les
  fichiers (ADR 0044) ;
- la base est en version 4 avec `serverConnection`, `syncCursors` et
  `syncRecordState`.

`IndexedDbUpgradeFixtureCatalogTests` (`HackerOs.Infrastructure.Browser.Tests`,
sans navigateur, donc dans chaque passe ordinaire) vérifie qu'un vidage existe
pour chaque version de 2 à `CurrentVersion - 1` et qu'il concorde avec le
schéma (nom de la base, version, magasins connus, chemin de clé,
auto-incrément, administrateur présent).

| Fichier | Version | Commit démarré | Production |
|---|---|---|---|
| — | 1 | aucun | **Pas de vidage.** La version 1 n'a existé avant aucun commit du dépôt : le commit racine `fdca68e` déclare déjà `CurrentVersion = 2`. Aucun build ne peut la produire et un vidage reconstitué serait inventé. L'étape 1 du plan reste testée par la création d'une base neuve (chaque démarrage à vide). |
| `hackeros-v2.json` | 2 | `e46e897` (dernier commit en v2, avant `7a20401`) | OS démarré pour de vrai |
| `hackeros-v3.json` | 3 | `7a20401` (seul commit en v3, avant `2481d67`) | OS démarré pour de vrai |

Chaque vidage contient `databaseName`, `version`, `producedFrom` (commit,
méthode, hôte, date), `expectations` (identifiants de l'administrateur,
chemin et contenu du fichier, réglage attendu) et `objectStores` : pour chaque
magasin, `keyPath`, `autoIncrement`, `indexes` et `records` (plus `keys` pour
un magasin sans chemin de clé ; aucun pour l'instant).

**Procédure** (utilisée le 2026-10-17 pour v2 et v3) :

1. Extraire l'ancien commit hors du dépôt :
   `git archive <commit> wasm2/HackerOs | tar -x -C <dossier>`.
2. Les commits d'avant `2fcccba` (2026-08-17) ne compilent pas tels quels :
   `Infrastructure/HackerOs.Infrastructure.Browser/Backup/` était exclu par la
   règle `Backup*/` du `.gitignore` de Visual Studio. Y remettre
   `IndexedDbBackupService.cs` tiré de `2fcccba`
   (`git show 2fcccba:wasm2/HackerOs/Infrastructure/HackerOs.Infrastructure.Browser/Backup/IndexedDbBackupService.cs`).
3. À cette époque, l'hôte Ecosystem autonome ne rendait rien
   (`RootComponents.Add<App>` en commentaire) : démarrer `test/test`.
   `dotnet build test/test/test.csproj -c Release`, puis
   `ASPNETCORE_ENVIRONMENT=Development dotnet run --no-build -c Release --project test/test/test.csproj --urls http://127.0.0.1:5301`.
   Le SDK .NET 10 courant suffit (même `global.json`).
4. `node Tests/fixtures/indexeddb/produce-fixture.js http://127.0.0.1:5301/ <version> <commit> Tests/fixtures/indexeddb/hackeros-v<version>.json <préfixe-captures>`
   (`PLAYWRIGHT_MODULE=/opt/node22/lib/node_modules/playwright` et
   `CHROMIUM=/opt/pw-browsers/chromium` dans un conteneur cloud). Le script,
   dans un contexte Chromium vierge, crée l'administrateur `admin` (mot de
   passe `Fixture-v<version>-pass`), téléverse
   `~/Documents/upgrade-note.txt` par l'Explorateur de fichiers (l'éditeur de
   texte de `7a20401` n'enregistre pas un nouveau fichier), choisit l'accent
   Cyan dans Paramètres, puis vide tous les magasins et refuse un vidage dont la
   version n'est pas celle attendue.
5. Lancer `IndexedDbUpgradeFixtureTests` et
   `IndexedDbUpgradeFixtureCatalogTests` ; ajouter la version au
   `[InlineData]` du premier.

**Règle :** tout chantier qui augmente `HackerOsIndexedDbSchema.CurrentVersion`
ajoute le vidage de la version précédente, produit par cette procédure à partir
du dernier commit de cette version, et l'ajoute au test E2E.
`IndexedDbUpgradeFixtureCatalogTests` échoue tant qu'il manque.

```bash
dotnet test Tests/HackerOs.UI.E2E.Tests -c Release --no-build --filter "FullyQualifiedName~IndexedDbUpgradeFixtureTests"
```

### Accessibilité (ADR 0105)

Deux classes de `HackerOs.UI.E2E.Tests` lancent axe-core (Deque.AxeCore.Playwright) ; une
violation sérieuse ou critique fait échouer, et chaque surface laisse une capture dans
`artifacts/playwright/accessibility/` :

- `AccessibilityAndVisualCoverageTests` : bureau, lanceur, explorateur, Paramètres, Code Editor,
  Hack Paint, et un écran mobile ;
- `AccessibilityPreferencesTests` (`v3/13`) : Terminal, navigateur, calculatrice, moniteur
  système, Theme Editor, Paramètres > Sauvegarde et > Accessibilité, chacun sans puis avec le
  contraste élevé ; un parcours au clavier seul (lanceur, `` Alt+` ``, `Alt+F7`, `Alt+Q`, focus
  visible) ; et la taille du texte à 200 % sans débordement. Voir
  [`accessibility.md`](accessibility.md).

```bash
dotnet test Tests/HackerOs.UI.E2E.Tests -c Release --no-build --filter "FullyQualifiedName~AccessibilityPreferencesTests"
```

### État connu

- **GitHub Actions** : sans exécuteur d'août au 23 octobre 2026, les workflows
  tournent de nouveau depuis le 2026-10-24 (`Verify HackerOS WASM`,
  `Deploy to GitHub Pages` ; Playwright dans le workflow manuel
  `verify-browser-tests.yml`). Un build et des tests locaux verts restent
  exigés avant de fusionner.
- Dans un conteneur cloud, les suites Playwright tournent sur le Chromium de
  `/opt/pw-browsers` ([repli](#chromium-pour-playwright)). État au
  2026-10-16 (après `dotnet build HackerOs.sln -c Release`, une suite à la
  fois) :
  - `HackerOs.E2E.Tests` : 16/16 ;
  - `HackerOs.UI.E2E.Tests` : 10/10 depuis `rebuild/5-ui-a11y-mkdir`, 15/15 sur
    `web/base` depuis son portage (`web/13`, 2026-10-17), 18/18 avec les deux
    tests de [montée de version IndexedDB](#fixtures-de-montée-de-version-indexeddb)
    (`v3/7`, 2026-10-17, environ 1 min 20 s chacun), 20/20 après le rebase de
    `v3/7` sur `v3/3` avec `LowStorageSpaceTests` (quota de Chromium abaissé,
    [`system-backup.md`](system-backup.md)), 24/24 avec les trois tests
    d'accessibilité (`v3/13`, 2026-10-17, environ 16 min 30 s)
    (2026-10-16 : contraste de `--hos-text-muted` du thème HackerOS, états
    ARIA écrits en `"true"`/`"false"`, voir
    [`old/common-pitfalls.md`](old/common-pitfalls.md) §8 ; le test du choix
    Mobile attend la bascule avant de recharger, car l'écriture du réglage
    dans `~/.config/hackeros` n'est pas instantanée : environ 2 s avant
    `web/15`, 235 ms depuis, voir
    [`filesystem-path-lookup.md`](filesystem-path-lookup.md)) ;
  - `HackerOs.Pwa.E2E.Tests` : la suite se lie à `[::1]` (le service worker
    refuse `127.0.0.1` et `localhost`) ; sans IPv6 elle se replie depuis
    `v3/7-storage-backup` sur `127.0.0.2`, adresse de bouclage et contexte
    sécurisé que le filtre d'`index.html` laisse passer (avant : 0/5,
    `SocketException : Address family not supported by protocol`). Elle
    compte 6 tests : le test qui vérifiait que des octets corrompus du cache
    étaient servis tels quels est remplacé par deux tests de récupération du
    cache (en ligne, hors ligne puis reconnexion), voir
    [`pwa-cache-recovery.md`](pwa-cache-recovery.md). État au 2026-10-17 :
    6/6 seule ; `Reload_fully_offline_boots_the_app_and_preserves_a_saved_file`
    a échoué une fois (bouton « Download » resté désactivé) pendant qu'une
    autre suite Playwright tournait en parallèle, puis est passé seul
    ([`old/common-pitfalls.md`](old/common-pitfalls.md) §6). Sur `v3/7`
    rebasé, sans autre suite en parallèle, trois lancements donnent 5/6 :
    tantôt ce test (bouton « Download »), tantôt
    `Update_to_a_new_release_purges_the_old_cache_and_preserves_compatible_data`
    (fichier enregistré pas encore listé par l'explorateur après 20 s) ; les
    deux passent ensemble ou seuls au lancement suivant. **Instables, à
    stabiliser** : l'attente porte sur l'explorateur de fichiers, pas sur le
    cache.
- Phase 5 : chaque PR `rebuild/5-*` colle la sortie de
  `build/verify-hosts.sh` ([Vérifier les trois hôtes](#vérifier-les-trois-hôtes)).
- Un test de `AppInstallationService` est instable (environ une fois sur
  douze), sur la base aussi.

## Tâches de ce document

- [x] Prérequis et installation de .NET dans un conteneur.
- [x] Propriétés `HackerOsCpuAccounting` et `HackerOsMemoryAccounting` (ADR 0066).
- [x] Build de la solution et contournements Linux connus.
- [x] Lancement des trois hôtes et publication de la PWA.
- [x] Suites de tests, filtrage, outils de test des apps, état connu.
- [x] `build/verify-hosts.sh` : les trois hôtes, les combinaisons des mesures, le test de démarrage (`rebuild/5-host-smoke`).
- [x] Repli des suites Playwright sur le Chromium du conteneur et leur état dans un conteneur.
- [x] Paquets locaux (`build/pack-local.sh`) et test des modèles (v3/6).
- [x] Publication Release trimmée, tailles avant et après, `build/publish-trimmed.sh` (`v3/14`, ADR 0106 et 0113).
- [x] Fixtures de montée de version IndexedDB (ADR 0099 §4, `v3/7`) :
  - [x] historique du schéma : v1 absente du dépôt, v2 jusqu'à `e46e897`, v3 en `7a20401` ;
  - [x] vidages v2 et v3 produits en démarrant l'OS de ces commits (`produce-fixture.js`) ;
  - [x] test E2E par vidage (compte, fichier, réglage, version 4) ;
  - [x] contrôle sans navigateur de la règle « un vidage par version précédente ».
