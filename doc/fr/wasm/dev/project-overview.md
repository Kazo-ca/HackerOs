# Vue d'ensemble du projet HackerOS

## Objet

HackerOS simule un système d'exploitation de type Linux dans le navigateur,
pour servir d'environnement où développer des applications. **Aucun jeu sur ce repository** ([ADR 0135](adr/0135-no-game-in-this-repository.md)). C'est
le portage en C# et Blazor WebAssembly du simulateur JavaScript d'origine
(`src/`, gardé en lecture seule comme référence de comportement).

Ce document est le point d'entrée pour comprendre le code de `wasm2/HackerOs` :
les projets de la solution, leurs couches, les hôtes, ce qui se passe au
démarrage et les règles qui s'appliquent au build. Pour compiler et lancer le
projet, voir [`building-and-testing.md`](building-and-testing.md). Pour écrire
une app, voir [`sdk-developer-guide.md`](../framework/sdk-developer-guide.md).

Deux principes guident tout le reste
([`ecosystem-rebuild-plan.md`](plans/ecosystem-rebuild-plan.md), section 1) :

1. **Pour le développeur d'app**, ajouter une app est simple : l'API fichier
   ressemble à `System.IO` (`HackFile`, `HackDirectory`…), le manifeste est une
   classe C#, et une seule `ProjectReference` installe l'app.
2. **Pour l'utilisateur**, l'OS se comporte comme un vrai Linux : tout est
   fichier (paramètres dans `/etc` et `~/.config`, journaux dans `/var/log`,
   processus dans `/proc`), le shell est complet, les apps de base sont riches.

## Couches

Les dépendances vont toujours de haut en bas. Une app ne voit que le SDK.

```text
 Hôtes            OS/HackerOs.Ecosystem (PWA)   test/test (débogage)   Server/HackerOs.Server
                         │
 Apps             Apps/System/*   Apps/Commands/HackerOs.Commands   Apps/Samples/*
                         │   (SDK public uniquement, vérifié au build : ADR 0055)
 SDK              Shared/HackerOs.AppSdk*  (Hack*, IAppExecutionContext, composants)
                         │
 Contrats         Shared/HackerOs.App.Abstractions   Shared/HackerOs.Simulation.Abstractions
                  Shared/HackerOs.Theming.Abstractions
                         │
 Plateforme       Platform/HackerOs.Platform.Core (services de l'OS, sans interface)
                  Platform/HackerOs.Platform.Blazor (bureau, fenêtres, dialogues)
                  Platform/HackerOs.Windowing.*, Taskbar.Blazor, MobileShell.Blazor, Theming.Blazor
                         │
 Infrastructure   Infrastructure/HackerOs.Infrastructure.Browser (IndexedDB, interop navigateur)
```

La plateforme implémente les contrats ; le SDK n'en connaît que les
interfaces. Les apps reçoivent un `IAppExecutionContext` construit par la
plateforme et ne référencent jamais `Platform.*` ni `Infrastructure.*`
(erreurs `HOSAPP0030`, `HOSAPP0031`, `HOSSDK0001`, voir
[`app-sdk.md`](../framework/app-sdk.md)).

## Projets de la solution

`HackerOs.sln` regroupe tous les projets ci-dessous (.NET 10, `global.json`).

### Contrats (`Shared/`)

| Projet | Contenu |
|---|---|
| `HackerOs.App.Abstractions` | Modèle d'app : `AppDefinition` et `[AppManifest]`, `AppManifest` et son validateur, `AppKind`, catalogue des capacités `AppCapabilities`, intentions, plateformes, sérialisation canonique des manifestes. |
| `HackerOs.Simulation.Abstractions` | Contrats du système simulé : système de fichiers (`VirtualPath`, requêtes et résultats), passerelles d'app (`IAppFileSystemGateway`, `IAppEventGateway`, `IAppLoggingGateway`…), processus, sessions, paramètres, réseau, notifications, horloge, mesures. |
| `HackerOs.Theming.Abstractions` | Contrats du thème (jetons, apparence). |

### SDK des apps (`Shared/HackerOs.AppSdk*`)

| Projet | Contenu |
|---|---|
| `HackerOs.AppSdk` | Façades `Hack*` (fichiers, environnement, paramètres, réseau, processus, presse-papiers, administration…), `IAppExecutionContext`, classes de base `TerminalAppBase`, `ServiceAppBase` et `WebServerAppBase`. |
| `HackerOs.AppSdk.Blazor` | `WindowAppBase`, `HackComponentBase`, dialogues (fichiers, message, saisie, « Ouvrir avec », propriétés, conflit de noms), graphiques `HackLineChart`, téléchargement et presse-papiers du navigateur ; contrats fournis par l'OS à l'exécution (`HackContractComponent<T>`, `HackFileView`), menu contextuel de l'OS (`Menus.Attach`) et `[RegisterFileContextMenu]` (SDK 2.0). |
| `HackerOs.AppSdk.Icons` | Bibliothèques d'icônes et composant `HackerIcon`. |
| `HackerOs.AppSdk.Terminal` | Émulateur de terminal xterm.js (`TerminalPanel`, `TerminalTabs`). |
| `HackerOs.AppSdk.Web` | ASP.Hack : sites internes façon ASP.NET Core MVC (pipeline, routage, contrôleurs, session ; [`asp-hack-mvc.md`](../framework/asp-hack-mvc.md)). |
| `HackerOs.AppSdk.Web.Blazor` | Sites Blazor interactifs : routeur, navigation, vue de site dessinée par le navigateur ([`blazor-sites.md`](../framework/blazor-sites.md)). |
| `HackerOs.AppSdk.VirtualKeyboard` | Clavier virtuel pour le mobile. |

Le détail de chaque API est dans la [référence du SDK](../framework/sdk-reference.md).

### Plateforme (`Platform/`)

| Projet | Contenu |
|---|---|
| `HackerOs.Platform.Core` | Services de l'OS, sans interface graphique : catalogue d'apps, cycle de vie et processus, système de fichiers et montages (`/proc`, `/dev`), paramètres en fichiers, journaux, politique et capacités, élévation (`sudo`), sessions, shell `hsh` et son interpréteur, réseau simulé, presse-papiers, notifications, horloge, mesures. Les passerelles du SDK y sont implémentées ([`platform-core.md`](platform-core.md) : carte des dossiers). |
| `HackerOs.Platform.ResourceAccounting` | Mesure optionnelle du CPU et de la mémoire par processus (ADR 0066 à 0068) : crochet de changement de contexte sur le frame ambiant, diffuseur vers les mesures, interrupteurs. Dépend de Platform Core ; un hôte ne le référence que si `HackerOsCpuAccounting` ou `HackerOsMemoryAccounting` vaut `true` ([`resource-accounting.md`](resource-accounting.md)). |
| `HackerOs.Platform.WorkerHost` | Processus sans interface dans des Web Workers (ADR 0070, 0071) : côté OS, le pool de workers (un par cœur virtuel, worker prêt, débordement, workers dédiés), les sessions qui servent les appels sous les mêmes contrôles de capacités, le battement de cœur et l'escalade ; côté worker, le runtime .NET qui exécute la commande ou le service avec des passerelles distantes. Référencé par l'hôte Ecosystem dans le navigateur seulement ([`worker-processes.md`](worker-processes.md)). |
| `HackerOs.Platform.RemoteRendering` | Essai de l'ADR 0129, verdict « on s'arrête » (ADR 0133) : une fenêtre Blazor rendue par un `Renderer` dans un worker, ses lots traduits en opérations DOM appliquées par `RemoteWindowHost` sur la page. Coupé par défaut (`HackerOs:RemoteWindowRendering`) ; aucune app ne doit en dépendre ([`remote-window-rendering.md`](remote-window-rendering.md)). |
| `HackerOs.Platform.Blazor` | Interface de l'OS : bureau, menu Démarrer, hôte de fenêtres (`WindowAppRenderer`), dialogues, fenêtre d'approbation des capacités dangereuses, agent d'authentification, chargement différé des apps. |
| `HackerOs.Windowing.Abstractions`, `.Core`, `.Blazor` | Gestionnaire de fenêtres (état, déplacement, redimensionnement, ancrage). |
| `HackerOs.Taskbar.Blazor` | Barre des tâches. |
| `HackerOs.MobileShell.Blazor` | Interface mobile. |
| `HackerOs.Theming.Blazor` | Application du thème. |

### Infrastructure (`Infrastructure/`)

`HackerOs.Infrastructure.Browser` : persistance IndexedDB (fichiers en blocs
dédupliqués, paramètres hérités, catalogue, sessions), sauvegarde et
restauration, connexion au serveur optionnel, interop navigateur, démarrage des
Web Workers (`workerHost.js`, `workerBootstrap.js`). Tout ce qui touche le
navigateur est ici ([`browser-infrastructure.md`](browser-infrastructure.md)).

### Apps (`Apps/`)

| Dossier | Contenu |
|---|---|
| `Apps/System/` | Les 15 apps fenêtrées de base : Bureau (surface cachée rendue par la coquille sous les fenêtres, [`desktop.md`](desktop.md)), Terminal, File Explorer, Text Editor, Code Editor, Settings ([`settings-app.md`](settings-app.md)), System Monitor (gestionnaire de tâches), Error Log Viewer, Diagnostic, Browser, Calculator, HackPaint ([`hack-paint.md`](hack-paint.md)), Theme Editor ([`theme-editor.md`](theme-editor.md)), Icon Viewer, Web Server Manager (avec le service `hackwebd`, [`static-sites.md`](static-sites.md)). |
| `Apps/Commands/HackerOs.Commands` | Les 74 commandes du shell dans un seul projet (`ls`, `cat`, `cd`, `cp`, `mv`, `rm`, `grep`, `nano`, `sudo`, `ping`, `curl`, `nmap`, et depuis l'ADR 0064 `sort`, `cut`, `zip`, `top`, `htop`, `systemctl`, `ip`, `test`, `read`… : [`linux-commands.md`](linux-commands.md)), une classe `[AppManifest]` par commande (ADR 0051) ; elles tournent dans le pool de Web Workers sauf les 18 marquées `Execution = AppExecutionHost.MainThread` (ADR 0070). |
| `Apps/Samples/` | Apps d'exemple, une par genre (fenêtre, terminal, service, plateforme, site Blazor, site ASP.Hack complet) ; elles ne sont pas installées dans l'hôte, sauf les deux sites `HackerOs.Samples.BlazorSite` (`http://blazor-sample.hack/`, ADR 0084) et `HackerOs.Samples.Intranet` (`http://intranet.hack/`, ADR 0087, [guide ASP.Hack](../framework/asp-hack-developer-guide.md)), apps cachées installées pour que le navigateur puisse les servir. |

Une app est **système** quand l'hôte la déclare ainsi sur sa `ProjectReference`
(`IsSystemApp="true"`) : elle n'est pas désinstallable et reçoit d'office ses
capacités dangereuses. Liste et règles : [`app-model.md`](../framework/app-model.md#apps-système-adr-0048-1).

### Hôtes

| Projet | Rôle |
|---|---|
| `OS/HackerOs.Ecosystem` | **L'hôte livré** : PWA Blazor WebAssembly autonome (`wwwroot/index.html`, service worker), sans serveur. Il référence `HackerOs.Ecosystem.Host` et les apps, agrège leurs manifestes dans `BuiltInApplications.json` au build, et son `Program.cs` appelle `AddHackerOsBrowserHost`. Tout doit fonctionner avec lui seul, hors ligne. |
| `OS/HackerOs.Ecosystem.Host` | **L'OS lui-même**, en bibliothèque Razor publiée en paquet (ADR 0098 §2) : bureau (`App.razor`), écran de progression, `EcosystemBootCoordinator`, composition des services (`AddHackerOsEcosystem`, `AddHackerOsBrowserHost`), mesure des ressources, adaptateurs du navigateur, jetons de design (`_content/HackerOs.Ecosystem.Host/css/app.css`), `HostAppCatalog`. Un hôte d'un autre dépôt le référence ([`sdk-packages.md`](../framework/sdk-packages.md)). |
| `test/test` | Blazor Web App de débogage : rend le même arbre de composants en `InteractiveWebAssembly` pour profiter du débogueur. Pas une cible de distribution. |
| `Server/HackerOs.Server` | Serveur ASP.NET Core **optionnel** : synchronisation, comptes, relais HTTP/TCP/UDP vers le vrai réseau ([`server-network-relay.md`](server-network-relay.md), politique et quotas dans `appsettings.json`). Il sert aussi l'OS de l'Ecosystem tel quel, exécuté en WebAssembly dans le navigateur comme la PWA ; cet OS s'y connecte tout seul et montre l'état dans la zone de notification (ADR 0130, 0134, [`server-hosting.md`](server-hosting.md)). Aucune app ne doit en dépendre. |
| `Server/HackerOs.Server.Contracts` | Contrats partagés entre le serveur et le client. |

Plus de détails sur les hôtes dans [`old/hosting-model.md`](old/hosting-model.md)
(antérieur au plan, toujours exact sur ce point).

### Outils, exemples et tests

| Dossier | Contenu |
|---|---|
| `Tools/HackerOs.Tools.ManifestGenerator` | Génère `manifest.json` depuis la classe `[AppManifest]` de chaque app, vérifie la frontière du SDK et agrège `BuiltInApplications.json` dans l'hôte ([`app-model.md`](../framework/app-model.md)). |
| `Tools/HackerOs.Tools.WebRouteGenerator` | Générateur de sources Roslyn branché sur chaque app : table de routes des contrôleurs ASP.Hack et entrée des sites, erreurs `HOSWEB*` (ADR 0082, [`asp-hack-mvc.md`](../framework/asp-hack-mvc.md)). |
| `Tools/HackerOs.App.Build` | Paquet de l'intégration de build pour un autre dépôt : `build/HackerOs.Apps.targets`, générateur de manifestes, générateur de routes ([`sdk-packages.md`](../framework/sdk-packages.md)). |
| `Templates/HackerOs.Templates` | Modèles `dotnet new` : `hackeros-window`, `-command`, `-service`, `-site`, `-host` (ADR 0098 §6). |
| `Tools/HackerOs.Tools.BackgroundAnalyzer` | Analyseur Roslyn des appels à `HackBackground.RunAsync` : vérifie chaque appel contre les règles des tâches de fond (ADR 0128). |
| `Tools/icon-import` | Script Node d'import des bibliothèques d'icônes. |
| `Samples/HackerOs.Windowing.SampleHost` | Hôte d'exemple du gestionnaire de fenêtres seul. |
| `Tests/` | Un projet de tests xUnit par projet de code, des tests d'intégration MSBuild du générateur, des suites E2E Playwright (`HackerOs.E2E.Tests`, `HackerOs.UI.E2E.Tests`, `HackerOs.Pwa.E2E.Tests`) le test de démarrage des trois hôtes (`HackerOs.HostSmoke.Tests`, lancé par `build/verify-hosts.sh`), le test de la publication trimmée (`HackerOs.TrimmedPublish.E2E.Tests`, lancé par `build/publish-trimmed.sh`) le test des modèles (`HackerOs.Templates.E2E.Tests`) et le test de compatibilité du SDK (`HackerOs.SdkCompatibility.Tests`, apps de compatibilité gardées dans `Tests/fixtures/sdk-compat` : 1.0.0 et 1.1.0 refusées, 2.0.0 admise ; ADR 0114, ADR 0149). `HackerOs.Tests.Support` fournit des faux contextes et passerelles ; `HackerOs.E2E.Support`, le lancement de Chromium commun aux suites Playwright. |
| `build/HackerOs.Apps.targets` | Cibles MSBuild du modèle d'app (génération, agrégation, frontière du SDK, empaquetage des manifestes). |
| `build/Versions.props`, `build/pack-local.sh`/`.ps1` | Version unique de tout ce qui est publié et production des paquets locaux dans `artifacts/local-nupkg` ([`sdk-packages.md`](../framework/sdk-packages.md)). |
| `build/HackerOs.ResourceAccounting.targets` | Propriétés `HackerOsCpuAccounting` et `HackerOsMemoryAccounting` ([`resource-accounting.md`](resource-accounting.md)). |
| `build/asp-hack/`, `build/tools/ship-public-api.sh`, `build/nuget.config.sample`, `build/run-test-suite.ps1`, `build/assemble-pages.ps1` | Générateurs et contrôles ASP.Hack (`HackRazorCompat.cs`, `_ViewImports.cshtml`), publication de la surface publique du SDK, modèle de `nuget.config` pour un autre dépôt, suite de tests complète, assemblage du site Pages (ADR 0167). |

## Le système simulé

### Système de fichiers

Un arbre Unix unique, persisté dans IndexedDB, avec propriétaires, groupes,
bits de mode, liens symboliques, révisions optimistes et écritures atomiques.
Les emplacements à connaître :

| Chemin | Contenu |
|---|---|
| `/home/<login>` | Dossier personnel ; `~/.config/<appId>/` paramètres de l'app, `~/.local/share/Trash` corbeille, `~/.local/share/keyrings/login.keyring` trousseau chiffré (ADR 0100). |
| `/root` | Dossier de `root` (processus lancés par `sudo`). |
| `/etc/hackeros/` | Configuration de l'OS : `apps.config`, `security.config`, `app-permissions.config`, `appearance.config`, `policy.config`, `ui-platform-preference.config`, `file-associations.json` ; `themes/` (thèmes `.theme` de toute la machine, ADR 0103). |
| `/etc/<appId>/` | Couche système des paramètres d'une app, avec la section `[locked]` ; clés sensibles dans `<doc>.private.config` (`0600`, ADR 0100). |
| `/etc/environment`, `/etc/hostname` | Environnement de connexion et nom de machine. |
| `/var/log/` | `syslog`, `auth.log`, `audit.log` (écritures sous `/etc`, `/bin`, `/var`, ADR 0100), `apps/<appId>.log`, avec rotation. |
| `/proc/` | Virtuel : processus, `stat`, `diskstats`, `net/dev`, `mounts`… |
| `/dev/` | Virtuel : `null`, `zero`, `random`, `urandom`. |

Documents : [`hack-file-api.md`](../framework/hack-file-api.md),
[`settings-files.md`](../framework/settings-files.md), [`logging.md`](logging.md),
[`virtual-proc-and-dev.md`](virtual-proc-and-dev.md),
[`trash.md`](trash.md).

### Processus, shell et capacités

- Chaque app lancée est un **processus** (identifiant, parent, arguments,
  répertoire courant, variables d'environnement, compteurs d'entrées-sorties),
  visible dans `/proc` ([`process-environment.md`](process-environment.md),
  [`system-metrics.md`](system-metrics.md)).
- Le shell `hsh` est un processus enfant du terminal ; il gère tubes,
  redirections, `&&`, variables et `$?`. Chaque commande est elle-même un
  processus ([`shell-interpreter.md`](shell-interpreter.md),
  [`shell-host.md`](../framework/shell-host.md), [`shell-commands.md`](shell-commands.md)).
- Dans le navigateur, les commandes et les services tournent dans des
  **Web Workers** (un par cœur virtuel, plus un worker prêt) : une boucle
  infinie ne gèle plus l'OS, `kill -9` termine le worker
  ([`worker-processes.md`](worker-processes.md), `/proc/hackeros/workers`).
- Une app déclare des **capacités** dans sa définition. Les capacités normales
  sont accordées à la connexion ; une capacité **dangereuse** d'une app non
  système exige l'approbation d'un administrateur
  ([`capabilities.md`](../framework/capabilities.md),
  [`dangerous-capability-approval.md`](dangerous-capability-approval.md)).
- `sudo` et l'agent d'authentification de l'OS font confirmer un
  administrateur sans jamais montrer le mot de passe à l'app
  ([`admin-authentication-agent.md`](admin-authentication-agent.md)).

### Démarrage

1. `Program.cs` de l'hôte appelle `AddHackerOsBrowserHost`, qui enregistre les
   services (`AddHackerOsEcosystem`) et le catalogue lu dans
   `BuiltInApplications.json` (`HostAppCatalog`, `BuildKnownLazyApps` dans l'Ecosystem).
2. `EcosystemBootCoordinator` monte le stockage, attache l'écrivain de journaux
   à `/var/log`, installe `/etc/hackeros/apps.config`, démarre l'horloge
   simulée et l'échantillonnage des mesures, puis valide politique, sessions et
   catalogue.
3. Au premier démarrage, l'écran demande de créer le profil administrateur
   local ; ensuite, il propose la connexion.
4. À la connexion, l'OS accorde les capacités, démarre les services
   `AutoStart` et affiche le bureau. Les DLL des apps sont chargées à la
   demande, au premier lancement (`BlazorWebAssemblyLazyLoad`).

## Règles vérifiées au build

| Règle | Où | Document |
|---|---|---|
| Avertissements traités comme erreurs, analyse de compatibilité avec le *trimming* | `Directory.Build.props` | — |
| Publication Release trimmée de l'hôte : racines générées depuis les manifestes, assemblies `HackerOs.*` entières (ADR 0106, 0113) | `build/HackerOs.Apps.targets`, testée par `build/publish-trimmed.sh` | [`trimmed-publish.md`](trimmed-publish.md) |
| Pas de CSS ni de JavaScript en ligne dans un `.razor` (ADR 0007) | cible `ValidateHackerOsRazorAssets`, `Directory.Build.targets` | [`AGENTS.md`](../../../../AGENTS.md) |
| Chaque projet sous `Apps/` a une classe `[AppManifest]` valide ; `manifest.json` est régénéré (`HOSAPP0001`…) | `build/HackerOs.Apps.targets` | [`app-model.md`](../framework/app-model.md) |
| Une app n'utilise que le SDK (`HOSAPP0030`, `HOSAPP0031`, `HOSSDK0001`) | `build/HackerOs.Apps.targets` et générateur | [`app-sdk.md`](../framework/app-sdk.md) |
| La surface publique du SDK ne change pas sans trace : `PublicAPI.Shipped.txt`/`Unshipped.txt` (RS0016, RS0017) ; une app compilée contre la majeure courante (2.x) est admise, une app 1.x refusée (ADR 0114, ADR 0149) | analyseur d'API publique, `Tests/HackerOs.SdkCompatibility.Tests` | [`sdk-packages.md`](../framework/sdk-packages.md) |

## Organisation du travail

- Le code nouveau va **uniquement** dans `wasm2/HackerOs/` ; `src/` est en
  lecture seule ([`AGENTS.md`](../../../../AGENTS.md)).
- La direction est fixée par [`ecosystem-rebuild-plan.md`](plans/ecosystem-rebuild-plan.md) ;
  les décisions d'architecture sont des ADR dans [`adr/`](adr/), jamais
  modifiés après acceptation.
- Les documents sont rédigés en français dans `doc/fr/` et traduits en anglais
  dans `doc/en/` (mêmes dossiers, noms de fichiers en anglais) ; chaque
  changement met à jour les deux langues. Tout ajout ou renommage de document met à jour l'index
  [`README.md`](../README.md) dans le même changement.
- Chaque série de chantiers a sa branche d'intégration : `rebuild/base`
  (phases 1 à 4, fusionnée dans `main`), `web/base` (sites ASP.Hack et Blazor),
  puis `v3/base` (fin de la migration wasm v3, partie de `web/base`), `v3.1/base`
  et `exec/base` (suites de la phase 5, partie de `v3.1/base`). Chaque
  chantier part de la branche de sa série (`v3/<n>-<sujet>`…) et y revient ;
  seul le propriétaire du dépôt fusionne vers `main`. Le 2026-10-18, à la
  demande de Benoit, `exec/base` (qui contient toutes les autres séries) a été
  fusionnée dans `main` : `main` porte désormais tout le travail.
- État : la migration wasm v3 est terminée, le SDK a été gelé en 1.0.0
  (ADR 0114), publié en 1.1.0 par la série v3.1 (ADR 0117 §5), en 1.2.0
  à la fin de la série `exec` (ADR 0132 §2) puis en **2.0.0** à la fin de la
  série du bureau et du menu contextuel (`desk/base` : contrôle de fichiers
  fourni par l'explorateur, menu contextuel de l'OS, entrées des apps, icônes
  du bureau ; ADR 0149, 0161, [`sdk-packages.md`](../framework/sdk-packages.md#sdk-20),
  bilan dans [`desktop-and-context-menu-plan.md`](plans/desktop-and-context-menu-plan.md#13-vérification-finale)) ; le bilan de la migration est dans
  [`wasm-v3-final-status.md`](plans/wasm-v3-final-status.md), celui de la série `exec`
  (limites, OOM killer, priorités, tâches de fond, hôte Server en WebAssembly,
  isolation cross-origin) dans
  [`execution-followups-plan.md`](plans/execution-followups-plan.md#11-vérification-finale). La version publiée est **2.0.3** (`build/Versions.props`, publication du
  2026-10-24) ; un autre dépôt peut se construire sur le SDK 2.x
  ([`building-on-hackeros.md`](../framework/building-on-hackeros.md)).

## Tâches de ce document

- [x] Décrire les couches et la règle de dépendance.
- [x] Lister chaque projet de la solution et son rôle.
- [x] Résumer le système simulé (fichiers, processus, shell, capacités) avec les liens vers les documents détaillés.
- [x] Décrire la séquence de démarrage.
- [x] Lister les règles vérifiées au build.
- [x] Relecture finale de la migration v3 (`v3/16-final`) : branches d'intégration et état du projet.
- [x] Relecture finale de la série `exec` (`exec/10-final`) : SDK 1.2, branches d'intégration.
- [x] Relecture finale de la série du bureau (`desk/6-sdk-2`) : SDK 2.0, `FileView` hors du SDK, règle de compatibilité.
