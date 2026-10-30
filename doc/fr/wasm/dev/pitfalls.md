> [English version](../../../en/wasm/dev/pitfalls.md)

# Pièges rencontrés et leurs solutions

Ce document résume, en grandes lignes, les problèmes qui ont coûté du temps
pendant la refonte de HackerOS (phases 1 à 5, phase web, séries v3, v3.1,
`exec` et `desk`) et la façon dont ils ont été réglés. Pour chaque piège :
le **symptôme** tel qu'on le voit, la **cause**, la **solution** (ou le
contournement) et la source détaillée.

Le détail technique de certains pièges plus anciens est dans
[`old/common-pitfalls.md`](old/common-pitfalls.md) (en anglais) ; les
commandes exactes de build et de test sont dans
[`building-and-testing.md`](building-and-testing.md).

## 1. Build et NuGet

| Symptôme | Cause | Solution |
| --- | --- | --- |
| `NU1902` sur `Microsoft.Build.Tasks.Git` traité comme une erreur | Avis de sécurité NuGet sur une dépendance de build seulement | `export NuGetAudit=false` **en variable d'environnement** (pas seulement `-p:`) : les suites `UI.E2E` et `Templates.E2E` relancent elles-mêmes `dotnet run`/`dotnet pack`, qui refont un restore. Sans la variable, toutes échouent par « harness did not become ready ». |
| Erreurs de compilation Razor dans `Apps.Browser` (`ResponseBodyViewer.razor`, `switch`/`case`) alors que le code est correct | Le SDK .NET 10 compilé par Ubuntu (`/usr/bin/dotnet`, 10.0.113) a un compilateur Razor différent du SDK Microsoft | Installer le SDK Microsoft (10.0.402 ou plus récent) dans `~/.dotnet` et le mettre en tête : `export PATH=$HOME/.dotnet:$PATH DOTNET_ROOT=$HOME/.dotnet` ([`building-and-testing.md`](building-and-testing.md)). |
| `NU1301` sur la source `local-nupkg` | Le dossier du flux local n'existe pas sur un checkout neuf | `mkdir -p wasm2/HackerOs/artifacts/local-nupkg`. Le `nuget.config` de l'exemple utilisait des `\` lus littéralement sous Linux ; il utilise maintenant `/`. |
| `IOException … being used by another process` dans `obj/…/compressed/` d'Ecosystem | `test/test` et `HackerOs.Server` compilés en même temps partagent Ecosystem | Compiler la solution une fois, puis lancer les hôtes avec `--no-build`. |
| `MsBuildIntegrationTests` du générateur de manifestes bloqués sans fin | Le `dotnet build` lancé par le test laisse des nœuds MSBuild réutilisés qui gardent la sortie redirigée | Lancer les tests avec `MSBUILDDISABLENODEREUSE=1`. |
| Un test casse après la fusion de deux branches parallèles alors que chacune était verte | Des tests comptent des choses (apps, manifestes, commandes : `BuildKnownLazyAppsTests`, `EcosystemBootCoordinatorTests`, `CommandParityTests`) et les documents citent ces nombres | Après chaque fusion, relancer la suite complète et mettre à jour les nombres dans les tests **et** dans la doc (`app-model.md`, `linux-commands.md`…). |
| Un test refuse une app « incompatible » après un changement de version du SDK | Un manifeste de test écrivait la version du SDK en dur (`"1.0.0"`) | Les manifestes de test utilisent `HackerOsSdk.Version` ; les tests de refus épinglent des versions explicites. |
| Le paquet de compatibilité du SDK change sans raison | Il a été reconstruit | Les paquets `Tests/fixtures/sdk-compat/packages/*.nupkg` (1.0.0, 1.1.0, 2.0.0) sont committés et **jamais** reconstruits ; ils prouvent que les apps compilées contre une version publiée se chargent toujours ([`../framework/sdk-packages.md`](../framework/sdk-packages.md)). |
| `NETSDK1098` en publiant `HackerOs.Server` pour un RID avec `-p:PublishSingleFile=true` | Une propriété passée par `-p:` sur la ligne de commande s'applique aussi au projet WebAssembly référencé (Ecosystem), qui ne peut pas être publié en un seul fichier | Mettre `PublishSingleFile` (conditionné au RID) dans le `.csproj` du serveur, jamais en `-p:` ; `build/publish-server.sh` ne passe que `-r` et `--self-contained` ([`installable-server.md`](installable-server.md)). |

## 2. Publication trimmée et WebAssembly

| Symptôme | Cause | Solution |
| --- | --- | --- |
| Une publication non trimmée (`-p:PublishTrimmed=false`) contient des assemblies élaguées et plante à l'exécution | La conversion WebCIL du SDK .NET est incrémentale par dates de fichiers : elle reprend les sorties de la publication trimmée précédente | Supprimer `OS/HackerOs.Ecosystem/obj/Release/net10.0/{linked,webcil,tmp-webcil}` avant de publier ; `build/publish-trimmed.sh` le fait ([ADR 0113](adr/0113-trimming-implementation-adjustments.md), [`trimmed-publish.md`](trimmed-publish.md)). |
| Les avertissements `IL2110`/`IL2111` n'apparaissent que dans l'app livrée, jamais dans les tests | Les projets de test n'exécutent pas l'analyseur de trimming | Vérifier avec `build/publish-trimmed.sh` (porte de fusion locale, échoue sur toute alerte `IL…`). Ne pas mettre `DynamicallyAccessedMembers` sur des paramètres de composants capturés par des closures Razor : utiliser `UnconditionalSuppressMessage` sur la méthode, ou passer le `Type` dans un objet porteur. |
| Le trimming retire des types utilisés par réflexion | Racines ILLink incomplètes | Les assemblies HackerOS et les apps restent entières ; seules les assemblies du framework sont élaguées, avec des racines générées depuis les manifestes (`HackerOs.TrimmerRoots.xml`, [ADR 0106](adr/0106-trimmed-release-publish.md)). |
| Le rendu d'une fenêtre Blazor dans un Web Worker coûte trop de mémoire | Chaque worker de fenêtre charge son propre runtime .NET : 66,5 Mio mesurés pour 40 Mio permis | Essai arrêté, prototype gardé coupé par défaut ([ADR 0133](adr/0133-window-rendering-in-worker-verdict.md), [`remote-window-rendering.md`](remote-window-rendering.md)). Les calculs lourds passent par `HackBackground.RunAsync`. |
| Le serveur publié répond `404` à `/` et à `/index.html` alors que `dotnet run` marche | `OverrideHtmlAssetPlaceholders` laisse `index.html` hors du manifeste des ressources statiques publié : aucun point de terminaison ne sert la page | L'intergiciel `UseOsPageFile` (`OsPageRewrite.cs`) sert `wwwroot/index.html` sans cache quand aucun point de terminaison ne correspond ([`server-hosting.md`](server-hosting.md)). |

## 3. Navigateur, cache et page périmée

| Symptôme | Cause | Solution |
| --- | --- | --- |
| Une app ne s'ouvre plus après un rebuild : `download '…/HackerOs.Apps.<Nom>.<empreinte>.wasm' failed … TypeError: Failed to fetch` (erreur d'intégrité SRI) | L'onglet garde le `dotnet.js` du build précédent, qui demande des fichiers dont l'empreinte a changé ; le build est correct | Ctrl+F5 (ou F12 ▸ Application ▸ Clear site data) et relancer le serveur de dev après un rebuild. Depuis l'[ADR 0162](adr/0162-restart-after-a-host-build-change.md), l'OS détecte le nouveau build et propose de redémarrer. |
| Page blanche de `test/test` ou `HackerOs.Server` lancé avec `--no-build` (« Static Web Assets are not enabled ») | Sans profil de lancement, l'hôte démarre en `Production` et ne sert pas les actifs des projets référencés | `ASPNETCORE_ENVIRONMENT=Development`. |
| Les actifs statiques d'une app référencée sont introuvables sous `_content/<Projet>/…` | Un projet d'app Blazor WebAssembly référencé publie ses actifs sans préfixe | Voir [`old/common-pitfalls.md`](old/common-pitfalls.md) §1. |
| Le cache PWA abîmé bloque le démarrage | Octets corrompus dans le cache du service worker | Garde de démarrage dans `index.html`, message `RESET_CACHE`, rechargement unique ([`pwa-cache-recovery.md`](pwa-cache-recovery.md)). |
| Ctrl+Maj+T et Ctrl+Maj+W n'arrivent pas au terminal ou à l'OS | Chrome garde ces raccourcis pour lui hors d'une PWA installée | Problème connu : installer l'OS en PWA, ou utiliser les touches de remplacement de `keybindings.config` ([`../framework/window-model.md`](../framework/window-model.md)). |
| Les tests PWA échouent par `SocketException : Address family not supported` | La suite se liait à `[::1]` et le conteneur n'a pas d'IPv6 | Repli sur `127.0.0.2`, adresse de bouclage acceptée comme contexte sécurisé. |

## 4. Tests et E2E

| Symptôme | Cause | Solution |
| --- | --- | --- |
| Les 16 tests de `E2E.Tests` échouent par « harness did not become ready » | Le harnais se lance avec `--no-build -c Release` | Compiler d'abord en Release (`dotnet build HackerOs.sln -c Release` ou `Tests/HackerOs.BrowserHarness.Tests -c Release`). |
| Tests E2E instables quand plusieurs suites tournent | Les suites Playwright se disputent le processeur et les ports | Lancer `E2E`, `UI.E2E`, `Pwa.E2E` et `TrimmedPublish.E2E` **une à la fois**. |
| Chromium introuvable pour Playwright | Navigateurs non installés à l'endroit attendu | `ChromiumLauncher` se replie sur `/opt/pw-browsers/chromium`. Ne jamais lancer `playwright install`. |
| Un test lit la sortie du terminal avant qu'elle soit complète | La sortie d'une commande en worker arrive en plusieurs écritures | Attendre le prompt suivant (`OutputFinished`) avant de lire. |
| Le test du choix Mobile échoue de façon intermittente | L'écriture du réglage dans `~/.config/hackeros` n'est pas instantanée (2 s avant l'[ADR 0090](adr/0090-filesystem-path-lookup-in-one-request.md), 235 ms après) | Attendre la bascule avant de recharger. |
| États ARIA faux ou test axe rouge | Un attribut ARIA lié directement à un `bool` en Razor disparaît quand il vaut `false` | Écrire `"true"`/`"false"` ([`old/common-pitfalls.md`](old/common-pitfalls.md) §8). |

### Tests instables : causes trouvées et corrigées (2026-10-25)

Ces tests échouaient de façon intermittente sur la CI ou sous charge ; la
cause est corrigée (dans le code ou dans le test), aucun test n'a été ignoré.
Dans un conteneur cloud à quatre cœurs, chacun a passé de 8 à 20 fois de
suite sous charge (quatre boucles CPU en parallèle), sauf
`CpuAccountingOverheadTests`, qui passe 10 fois sur 10 au repos mais peut
encore échouer sur une machine saturée : c'est une mesure de temps.

| Test | Cause | Correction |
| --- | --- | --- |
| `A_command_that_spins_at_once` (`WorkerHost.Tests`) | Un cœur passait pour « libre » dès qu'un battement arrivait une période après le placement ; un battement envoyé pendant le chargement de l'assembly de la commande (qui rend la main) suffisait sur une machine chargée, et la commande suivante se plaçait derrière la boucle. | Le battement rapporte depuis combien de temps le code de chaque processus a le thread (`rn`) ; le cœur n'est libre que si chaque processus hébergé a exécuté son code depuis une période ([`worker-processes.md`](worker-processes.md)). |
| `Kill_minus_9_of_a_spinning_command…` (`WorkerHost.Tests`) | Trois courses : les voisins d'un worker terminé sortaient 137 avant que l'OS note la raison `Killed` (pas de « Killed » du shell) ; la sortie reçue d'une session abandonnée n'était pas écrite ; la commande tuée rendait la main au shell avant que sa sortie reçue soit écrite. Le test tuait aussi parfois `spin` avant qu'il ait démarré. | Voisins marqués tués avant la fin de leur session ; sortie reçue écrite avant la fin (bornée) ; le test attend `TestCommands.Spinning`. |
| `StaticSitesTests` (sous charge) | `hackwebd` ouvrait un port et acceptait des requêtes avant de connaître le site de cette liaison : 503. Le test attendait le premier port ouvert, pas toutes les liaisons (`intranet.hack` partait alors vers une app de la fixture). | L'acceptation commence une fois la table des sites à jour ; le test attend toutes les liaisons. |
| Parité `zip`/`unzip` (`CommandParityTests`) | `ZipArchive.CreateEntry` date l'entrée avec l'horloge réelle de la machine : les deux exécutions différaient quand elles tombaient de part et d'autre d'une frontière de 2 s. | `zip` date chaque entrée avec la date de modification du fichier, comme Info-ZIP. |
| `CpuAccountingOverheadTests` (`ResourceAccounting.Tests`) | Une passe du même scénario variait du simple au triple : la compilation à niveaux recompilait le code en arrière-plan entre les passes, et les 300 `Task.Yield` attendaient un thread du pool. Le surcoût mesuré allait de -9 % à +15 %. | Compilation à niveaux coupée dans ce projet de test, collecte complète avant chaque passe, boucle d'événements à un seul thread (comme le fil principal du navigateur), ordre des deux modes alterné, cinq essais. Surcoût mesuré : environ 2 %. |
| `A_child_with_a_parent_exit_grace_period…` (`Core.Tests`) | Le test vérifiait que le petit-enfant était tué au moment où l'enfant disparaissait ; le code le tue juste après, dans la même tâche (ADR 0147). | Le test attend les deux. |

Tests **instables connus**, non reproduits en 25 essais sous charge le
2026-10-25 : un test d'`AppInstallationService` (environ une fois sur douze
avant), le web worker de `LocalNetworkTests`. Côté navigateur (workflow
manuel) : le harnais de fenêtre détachée de l'UI, `FileExplorerDownload` et
`BrowserHomePage` d'`UI.E2E` sous la charge de la suite complète, et deux
tests `Pwa.E2E` qui attendent l'explorateur de fichiers. Relancer la suite
seule avant de conclure, et noter ici la cause d'un nouveau cas. Sur
GitHub Actions, un test qui échoue est relancé une fois et signalé comme
instable s'il passe ([`ci-workflows.md`](ci-workflows.md#tests-instables-et-nouvel-essai)) :
ce signal est le point de départ pour trouver sa cause.

**À retenir pour écrire un test** : attendre l'événement qui prouve l'état
voulu (la commande tourne, toutes les liaisons sont ouvertes, les deux
processus sont terminés), jamais un signe qui le précède (le processus est
placé, le premier port est ouvert) ; ne jamais comparer une sortie qui
contient l'heure réelle de la machine ; pour mesurer un temps, couper la
compilation à niveaux et comparer des passes faites dans les mêmes conditions.

## 5. Blazor, fenêtres et SDK

| Symptôme | Cause | Solution |
| --- | --- | --- |
| Déplacer ou redimensionner une fenêtre ne fait rien, sans erreur | Une sous-classe surchargeait `OnAfterRenderAsync` sans appeler la base : le module JavaScript du déplacement n'était jamais chargé | `WindowAppBase` scelle les méthodes du cycle de vie Blazor et expose des crochets `OnApp*` ([`../memory/blazor-window-manager-bugs.md`](../memory/blazor-window-manager-bugs.md)). |
| Minuteries, délais d'arrêt et horloge de la barre des tâches ne se déclenchent jamais | Rien n'avançait l'horloge simulée en production | `SimulationClockDriver` fait avancer `ManualSimulationClock`. |
| Une façade statique du SDK (`HackUser`…) ne trouve pas son contexte | `BuildRenderTree` et les composants `ComponentBase` simples tournent hors de la portée ambiante | Utiliser `HackUser.Describe(AppContext)` ou `AppContext.Catalog` dans ces endroits ([`../framework/hack-file-api.md`](../framework/hack-file-api.md)). |
| Le build refuse une app avec `HOSAPP0030`/`HOSAPP0031` | L'app référence un projet interne ou injecte un service hors SDK (par exemple `IServiceProvider`) | Les apps n'utilisent que le SDK public, vérifié au build ([`../framework/app-sdk.md`](../framework/app-sdk.md)). Injecter directement l'interface du SDK. |
| Une propriété ajoutée à `AppManifest` disparaît du `manifest.json` | La liste blanche du sérialiseur strict n'était pas à jour | Mettre à jour `AppManifestJsonSerializer` en même temps ([`old/common-pitfalls.md`](old/common-pitfalls.md) §2). |
| Tests de pages de site incohérents (`AmbientFrameSourceTests`) | Chaque page `.razor` doit déclarer son `@inherits HackComponentBase` (pas dans `_Imports.razor`) | Ajouter `@inherits` explicitement dans chaque page. |
| `@await Component.InvokeAsync<T>(…)` ne compile pas en Razor | Le `<` générique est lu comme du balisage | Écrire `@(await Component.InvokeAsync<T>(…))`. |
| `View("texte")` d'ASP.Hack cherche une vue nommée « texte » | Même résolution de surcharge qu'ASP.NET Core | Passer une classe de modèle ou `View(viewName: null, model: x)` ([`../framework/asp-hack-razor-views.md`](../framework/asp-hack-razor-views.md)). |
| `curl -d` vers un formulaire ASP.Hack répond 400 | Anti-falsification : chaque `curl` a son propre pot à cookies | Comportement attendu. |
| Les terminaux xterm.js effacent la dernière cellule ou ont un fond noir forcé | `ESC[K` après une ligne pleine efface la dernière cellule ; `xterm.css` peint `.xterm-viewport` en noir | Gérés dans la discipline de ligne C# et surchargés en CSS isolé ([`terminal-emulator.md`](terminal-emulator.md)). |
| Un élément déplacé dans un écran secondaire ne reçoit plus de `ResizeObserver` ni de touches (`window.addEventListener`) | Un observateur ne suit que la mise en page de sa fenêtre, et le registre des surfaces ne reproduit que les écouteurs posés sur `document` | Créer l'observateur avec `element.ownerDocument.defaultView.ResizeObserver` ; écouter sur `document`, ou sur la fenêtre de chaque surface avec `watch(…)` du registre ([`secondary-screen-desktop.md`](secondary-screen-desktop.md)). |

## 6. IndexedDB et fichiers

| Symptôme | Cause | Solution |
| --- | --- | --- |
| Base IndexedDB cassée après une montée de version | Numéro de version du schéma écrit en dur | Toujours référencer `HackerOsIndexedDbSchema.CurrentVersion` ; les montées de version sont testées avec des fixtures ([`old/common-pitfalls.md`](old/common-pitfalls.md) §3, [`building-and-testing.md`](building-and-testing.md)). |
| Bandeau « Unknown IndexedDB transaction boundary » au démarrage | Une frontière de transaction utilisée par un dépôt n'était pas déclarée dans le schéma | Déclarée ; un test vérifie maintenant que chaque `TransactionBoundary` est déclarée. Si le bandeau revient, c'est un nouveau bogue. |
| Écrire un réglage prenait 845 ms et 604 transactions | La résolution de chemin parcourait le stockage segment par segment | Une seule requête `resolvePath` (604 → 51 transactions, [`filesystem-path-lookup.md`](filesystem-path-lookup.md)). |
| Un enregistrement `HackWebDb` avec `Id = 0` est stocké sous la clé 0 au lieu d'être numéroté | Comportement normal d'IndexedDB avec `autoIncrement` | Mettre `[JsonIgnore(Condition = WhenWritingDefault)]` sur `Id` ([`../framework/asp-hack-site-data.md`](../framework/asp-hack-site-data.md)). |
| `GET /api/devices` du serveur échoue sous SQLite (`NotSupportedException` sur `ORDER BY`) | SQLite ne sait pas trier une colonne `DateTimeOffset` côté base | Trier en mémoire après la requête (`AccountService.GetDevicesAsync`). |

## 7. Conteneurs cloud, GitHub et organisation

| Symptôme | Cause | Solution |
| --- | --- | --- |
| Les GitHub Actions échouaient toutes (août à octobre 2026) | Plus d'exécuteur disponible | Pendant cette période, un build et des tests locaux verts faisaient foi. Depuis le 2026-10-24, les workflows tournent de nouveau (`Verify HackerOS WASM`, `Deploy to GitHub Pages`) ; les tests Playwright sont dans un workflow manuel séparé (`verify-browser-tests.yml`). Un build et des tests locaux verts restent exigés avant de fusionner. Les PR échouaient ensuite sur des tests instables (`Kill_minus_9`, `ResourceAccounting`) : les workflows relancent une fois les tests en échec ([`ci-workflows.md`](ci-workflows.md)). |
| GitHub Actions échoue à « Setup .NET 10 » : `409 Unable to download https://dotnetorigin.blob.core.windows.net/...` | Avec un canal (`10.0.x`), le script d'installation suit le lien `aka.ms`, qui pointait vers un stockage en erreur | Installer une version exacte : l'action `.github/actions/setup-dotnet-sdk` lit `latest-sdk` et télécharge depuis `builds.dotnet.microsoft.com` ([`ci-workflows.md`](ci-workflows.md#installation-du-sdk-net)). |
| `dotnet` absent dans un conteneur cloud | Rien n'est préinstallé | `curl -sSL https://dot.net/v1/dotnet-install.sh \| bash -s -- --channel 10.0`. |
| Un thread cloud ne peut pas supprimer une branche GitHub (403) | Droits insuffisants des sessions cloud | Benoit supprime les branches fusionnées lui-même avec une commande préparée. |
| Le nom de branche reçu porte un suffixe (`…-txf6mn`) | Le harnais attribue des noms suffixés | Travailler sur la branche donnée ; vérifier qu'aucune branche orpheline ne reste. |
| Un thread s'arrête sans reprendre | Limite d'usage du compte atteinte | Le coordinateur relance les threads arrêtés. |
| `pkill -f <motif>` tue la commande elle-même (code 144) | Le motif correspond aussi à la ligne de commande du shell | Lancer `pkill` seul, avec un motif qui ne se correspond pas à lui-même. |
| Mauvaise base pour un chantier ou fusion non voulue dans `main` | Plusieurs séries en parallèle | Une branche d'intégration `<série>/base` par série, une branche `<série>/<n>-<sujet>` par chantier ; seul Benoit fusionne dans `main` ([`../memory/README.md`](../memory/README.md)). |

## Tâches de ce document

- [x] Rassembler les pièges des plans, bilans, ADR et de la mémoire du projet.
- [ ] Ajouter ici tout nouveau piège qui a coûté du temps, dans la même PR que sa solution.
