> [English version](../../../en/wasm/dev/app-installation.md)

# Désinstallation et réinstallation des apps

Phase 2 du [plan de refonte](plans/ecosystem-rebuild-plan.md) (section 6.3, décisions
D10 et D11), chantier `rebuild/2-app-uninstall` de l'[ADR 0048](adr/0048-system-apps-and-uninstall.md).
Les apps système (D9) sont décrites dans [`app-model.md`](../framework/app-model.md#apps-système-adr-0048-1).

## Objectif

Désinstaller une app la retire de l'OS sans rien effacer : elle disparaît du
lanceur, de la recherche, de « Ouvrir avec » et du terminal, ne se lance plus
par aucune voie, ses fenêtres ouvertes se ferment. Ses réglages
(`~/.config/<appId>/`, `/etc/<appId>/`), ses journaux et les fichiers de
l'utilisateur restent en place : une réinstallation retrouve tout.

L'état vit dans deux fichiers texte, comme sous Linux : `cat` montre ce qui
est retiré, `nano` a le même effet que l'app Paramètres. Il remplace le drapeau
IndexedDB `IsEnabled` de l'ADR 0032.

## Fichiers `apps.config`

| Portée | Fichier | Propriétaire et mode | Qui écrit |
| --- | --- | --- | --- |
| Machine | `/etc/hackeros/apps.config` | `system:system 0644` | un administrateur, par `sudo` ou l'agent d'authentification de l'OS (ADR 0055 §5) |
| Utilisateur | `~/.config/hackeros/apps.config` | l'utilisateur, `0644` | l'utilisateur lui-même |

```text
# /etc/hackeros/apps.config
[uninstalled]
org.hackeros.hack-paint=true
org.hackeros.calculator=true
```

Règles de lecture (`AppsConfigDocument.Read`) :

- grammaire `.config` de l'ADR 0011, lue ligne à ligne sans schéma ; seules les
  clés de `[uninstalled]` comptent et la dernière occurrence d'une clé l'emporte ;
- `true` : désinstallée ; `false` ou absence : installée ; autre valeur : ligne
  ignorée avec un avertissement dans `/var/log/syslog` ;
- identifiant inconnu de ce build : ignoré **sans** avertissement, ligne
  conservée (un même fichier peut servir à plusieurs builds) ;
- identifiant d'app système, ou app dont une app système dépend : ignoré avec
  un avertissement. Éditer le fichier ne contourne donc pas la protection ;
- fichier absent ou illisible : rien de désinstallé.

Les écritures (`ConfigDocumentEditor`) ne touchent que la ligne concernée :
commentaires, ordre et identifiants inconnus sont préservés. Réinstaller retire
la ligne.

### État effectif

Une app est désinstallée pour la session si elle est listée dans le fichier
machine **ou** dans le fichier de l'utilisateur, ou si elle dépend (dépendance
non optionnelle, transitivement) d'une app ainsi désinstallée.

| `AppInstallationState` | Sens |
| --- | --- |
| `Installed` | ni listée ni dépendante d'une app listée |
| `UninstalledForUser` | désinstallée par le fichier de l'utilisateur |
| `UninstalledForMachine` | désinstallée par le fichier machine : seul un administrateur la réinstalle |
| `System` | app système, jamais désinstallable |

## Architecture

| Type | Projet | Rôle |
| --- | --- | --- |
| `IAppInstallationService`, `AppInstallationService` | `Platform/HackerOs.Platform.Core/Apps` | lit, surveille et écrit les deux fichiers ; tient `AppEnablementRegistry` égal à l'état effectif |
| `AppsConfigDocument` | idem | chemins, lecture tolérante, écriture ligne à ligne, fermetures de dépendances |
| `AppEnablementToFilesMigration` | idem | crée `/etc/hackeros/apps.config` commenté et y recopie une fois les anciens `IsEnabled = false` |
| `AppEnablementRegistry` | `Platform/HackerOs.Platform.Core/Lifecycle` | point de contrôle unique du lancement, désormais alimenté par le service ; événement `Changed` |
| `InstalledAppsPanel` | `Apps/System/HackerOs.Apps.Settings` | onglet « Installed Apps » de l'app Paramètres |

Flux unique, quelle que soit l'origine du changement (app Paramètres, `nano`,
synchronisation de `/home`, connexion, déconnexion) :

1. `ReloadAsync` relit les deux fichiers (contexte système pour `/etc`, contexte
   de l'utilisateur pour son fichier) ;
2. l'état effectif est calculé (union, puis dépendants) ;
3. les apps nouvellement désinstallées sont arrêtées et désactivées, dépendants
   d'abord, par `AppLifecycleOrchestrator.DisableAsync` (instances arrêtées,
   `AppDisabledEvent` publié) ; les apps réinstallées sont réactivées,
   dépendances d'abord, par `EnableAsync` ;
4. `Changed` est levé.

Au démarrage, `EcosystemBootCoordinator` exécute la migration puis
`AppInstallationService.StartAsync`, qui applique le fichier machine et pose
les surveillances (`SettingsFilesWatcher`) avant toute connexion. La connexion
et la déconnexion (`SessionActivatedEvent`, `SessionLoggedOutEvent`) relisent
la couche utilisateur et déplacent sa surveillance.

### Filtrage

Toutes les voies consultent `IAppEnablementRegistry` :

| Voie | Effet d'une app désinstallée |
| --- | --- |
| `AppLifecycleOrchestrator.LaunchAsync` | `AppLaunchStatus.Disabled` |
| `AppIntentDispatcher` (intentions, `launch`) | refus ; `launch` répond « is not installed » |
| `FileAssociationResolver` | ni défaut ni candidate ; les préférences de `/etc/hackeros/file-associations.json` et de `~/.config/hackeros/file-associations.json` ne sont **pas** réécrites (une entrée périmée est sautée, ADR 0121) |
| Lanceur et recherche (`LauncherAppProjection`) | absente ; le menu ouvert se met à jour sur `Changed` |
| Explorateur, « Open With... » | absente de la liste |
| Contrôle de fichiers et dialogues (`HackComponentHost`) | sautée ; si c'était le fournisseur choisi, l'explorateur prend le relais (`component.fallback`) ; une désinstallation pour la machine retire sa ligne de `/etc/hackeros/components.config` (ADR 0156 §4, ADR 0158 §6) |
| Terminal (`ShellCommandResolver`, `ShellInterpreter`) | `command not found`, absente de `help` et de la complétion |

## Utilisation

```csharp
public interface IAppInstallationService
{
    event EventHandler? Changed;
    AppInstallationState GetState(string appId);
    IReadOnlyList<string> GetUninstallClosure(string appId);
    ValueTask<AppInstallationResult> UninstallAsync(string appId, AppInstallationScope scope,
        IAppFileSystemGateway? writer = null, CancellationToken cancellationToken = default);
    ValueTask<AppInstallationResult> ReinstallAsync(string appId, AppInstallationScope scope,
        IAppFileSystemGateway? writer = null, CancellationToken cancellationToken = default);
}
```

- `GetUninstallClosure(appId)` : les apps qui en dépendent (dépendants d'abord)
  puis l'app elle-même ; la boîte de confirmation les liste.
- `PlanUninstallAsync(appId)` (ADR 0097 §5) : la fermeture et les processus
  actifs qu'elle arrêtera (`AppProcessInfo` : PID, app, nom, utilisateur), sans
  rien changer. Paramètres les liste dans la confirmation (« These processes
  will be stopped »). Le résultat de `UninstallAsync` donne
  `StoppedProcesses`, avec `Forced` pour ceux tués après leur délai ; chacun est
  écrit dans `/var/log/syslog`. Dans le SDK : `HackAppManagement.PlanUninstallAsync`
  et `UninstallWithReportAsync` (`HackUninstallPlan`, `HackUninstallReport`).
- `UninstallAsync` écrit toute la fermeture dans le fichier de la portée.
  Refus : `apps.app.system:<id>`, `apps.closure.system-app:<id système>`,
  `apps.app.not-found:<id>`, `apps.session.required`.
- `ReinstallAsync` retire l'app et ses dépendances obligatoires listées dans la
  même portée. Refus en portée utilisateur si l'app (ou une dépendance) est
  désinstallée pour la machine : `apps.uninstalled-by-administrator:<id>`.
- `writer` : passerelle dont les droits décident d'une écriture **machine**,
  normalement `AppContext.FileSystem` de l'app appelante. Un refus lève
  `HackUnauthorizedAccessException`, ce qui déclenche l'invite d'approbation
  administrateur de l'ADR 0042 ; l'app réessaie une fois l'approbation donnée.
  Sans `writer`, l'utilisateur de la session écrit avec ses propres droits. La
  portée utilisateur est toujours écrite comme l'utilisateur de la session.
- Chaque changement est journalisé dans `/var/log/syslog`, par exemple
  `apps: org.hackeros.calculator uninstalled (scope=machine, by=alice)`.

Depuis la phase 4 (`rebuild/4-sdk-user-and-apps`), les apps n'utilisent plus ce
service directement : elles passent par `HackAppManagement` du SDK, derrière la
capacité dangereuse `apps.manage` (ADR 0056 §3, [`app-sdk.md`](../framework/app-sdk.md)).
Pour la portée machine, la passerelle demande d'abord à l'agent
d'authentification de l'OS de confirmer un administrateur
([`admin-authentication-agent.md`](admin-authentication-agent.md)), puis l'OS
écrit `/etc/hackeros/apps.config` lui-même comme `system` : le paramètre
`writer` reçoit cette passerelle système, et l'invite de l'ADR 0042 n'est plus
utilisée pour ce fichier. La liste des apps installées, elle, est lue par
`HackApps` sans capacité.

### App Paramètres, onglet « Installed Apps »

- une ligne par app fenêtrée (nom, éditeur, version), puis les commandes du
  terminal dans une section repliée ; les services restent gérés par l'onglet
  Services du Moniteur système ;
- badge « System » sans bouton pour une app système ;
- **Uninstall** pour une app installée, avec confirmation : liste des
  dépendants emportés, choix « Only for me » / « For all users » pour un
  administrateur (un utilisateur standard n'a que sa propre portée) ; « For all
  users » ouvre la fenêtre de l'agent d'authentification, où l'administrateur
  retape son mot de passe ;
- pour un site (app `Web`) emporté par la désinstallation, la confirmation
  annonce que sa base de navigateur `hackweb.<id>` sera supprimée ; elle l'est
  après la désinstallation par `UninstallAsync`, seule donnée qu'une
  désinstallation efface (ADR 0088 §4, [`asp-hack-site-data.md`](../framework/asp-hack-site-data.md)) ;
- **Reinstall** pour une app désinstallée dans une portée que l'utilisateur
  peut modifier ; sinon la mention « Uninstalled by the administrator » ;
- l'ancien interrupteur activer/désactiver a disparu ;
- depuis la phase 4, le panneau n'utilise que le SDK : `AppContext.AppManagement`
  pour la liste et les changements, `AppContext.Catalog.Changed` pour se
  rafraîchir, `HackUser.Describe(AppContext)` pour savoir si l'utilisateur est
  administrateur.

## Décisions de mise en œuvre

- **Un seul état.** « Désactivée » et « désinstallée » ne font plus qu'un. Le
  magasin IndexedDB garde l'instantané des manifestes, mais l'OS ne lit ni
  n'écrit plus `IsEnabled` ; sa version de schéma ne change pas.
- **`DisableAsync` et `EnableAsync` deviennent internes** à
  `HackerOs.Platform.Core` (visibles des tests par `InternalsVisibleTo`) : seul
  le service d'installation les appelle. L'orchestrateur ne reçoit plus
  `IOsSettingsService` ni `IPersistentAppCatalogRepository`.
- **Dépendants désinstallés implicitement.** Une ligne écrite à la main pour
  une bibliothèque désinstalle aussi ses dépendants, même s'ils ne sont pas
  listés : une app dont une dépendance obligatoire manque ne peut pas tourner.
  L'app Paramètres, elle, écrit toute la fermeture, pour que la réinstallation
  soit explicite.
- **Fichier machine créé au démarrage** par la migration (contexte système),
  pour qu'il appartienne à `system` et que les écritures suivantes ne modifient
  que son contenu.
- **Synchronisation.** Le domaine « AppCatalog » de l'ADR 0033
  (`AppCatalogSyncService`, `AppCatalogSyncPayload`) est retiré. Le fichier
  utilisateur est sous `/home` et voyage avec le domaine « FileSystem » ;
  `/etc` n'est pas synchronisé. L'énumération `SyncDomain.AppCatalog` reste
  côté serveur pour les données déjà stockées.
- **Écart avec la signature de l'ADR :** `UninstallAsync` et `ReinstallAsync`
  prennent un paramètre `writer` en plus. « Le contexte de l'appelant » de
  l'ADR est ainsi transmis explicitement, comme pour `IOsSettingsService`
  (ADR 0044), plutôt que par le contexte ambiant `Hack*`, qui n'existe pas dans
  un gestionnaire d'événement Blazor.
- **`GetState` d'un identifiant inconnu** lève `ArgumentException` : l'ADR ne
  prévoit pas d'état « inconnu ».
- **Interface en anglais**, comme le reste de l'app Paramètres.

## Tests de contrat

`Tests/HackerOs.Platform.Core.Tests/Apps/AppInstallationServiceTests.cs`
(ADR 0048 section 7) :

- désinstallation utilisateur : fichier écrit, lancement refusé, instance
  arrêtée, données conservées, ligne de journal ;
- réinstallation : ligne retirée, lancement de nouveau possible ;
- app système et fermeture contenant une app système refusées ;
- lignes `apps.config` d'app système ou de valeur invalide ignorées avec
  avertissement, identifiant inconnu ignoré en silence ;
- portée machine sans droit : `HackUnauthorizedAccessException` ; écriture
  approuvée : fichier écrit, effet pour tous les utilisateurs ;
- état effectif machine OU utilisateur, couche utilisateur retirée à la
  déconnexion, réinstallation utilisateur refusée pour une désinstallation
  machine ;
- fermeture des dépendances à la désinstallation et à la réinstallation ;
- modification de `apps.config` par l'API fichier prise en compte sans
  redémarrage ;
- édition qui préserve commentaires et identifiants inconnus ;
- migration : `IsEnabled = false` devient une ligne `[uninstalled]`, une seule
  fois, jamais pour une app système, fichier possédé par `system`.

Ailleurs : `ShellCommandResolverTests.Uninstalled_commands_are_neither_resolved_nor_listed`,
`EcosystemBootCoordinatorTests.BootAsync_moves_persisted_disabled_entries_into_the_machine_apps_config`,
et les tests existants du lanceur, du résolveur d'associations et du
répartiteur d'intentions, qui s'appuient déjà sur le registre.

## Apps installées depuis un dépôt GitHub

L'ADR [0182](adr/0182-install-apps-from-github.md) ajoute une troisième
origine d'app, à côté des apps système et des apps intégrées : un paquet
`.hackapp` publié par le dépôt GitHub de l'app sur sa branche
`hackeros-packages`, lu par `raw.githubusercontent.com`.

```text
hackpkg install Kazo-ca/HackerOs-App-Webamp            # pour soi si la source est de confiance
hackpkg install Kazo-ca/HackerOs-App-Webamp@1.0.0 --system   # pour tous, validation admin
hackpkg update                                         # toutes les apps installées
hackpkg list | info <dépôt> | remove <appId> [--user|--system]
hackpkg rollback <appId> [--user|--system]             # revient à la version gardée (ADR 0214)
hackpkg repair [--system]                              # finit ou annule une installation interrompue
hackpkg sources [add <source> [--trusted] | remove <source>]
hackpkg policy [block|unblock app|category|capability <valeur>]
```

L'ADR [0189](adr/0189-app-sources-trust-and-per-user-install.md) (mise en
œuvre décrite par l'ADR [0208](adr/0208-hack-store-sources-trust-build.md))
ajoute les sources de confiance, l'installation pour soi, la liste noire et la
validation en deux étapes :

- **Sources** : `/etc/hackeros/app-sources.config` (créé au démarrage, mode
  0644) a quatre sections. `[hosts]` donne l'hôte de téléchargement ;
  `[catalogs]` les catalogues (`official=https://…/catalog.json`) ;
  `[trusted]` les sources (`catalog:<nom>`, `github.com/<owner>` ou
  `github.com/<owner>/<repo>`) mises à `true` (de confiance) ou `false`
  (ajoutée mais non fiable) ; `[updates]` l'intervalle `interval-hours`
  (1 à 720, 6 par défaut). Les valeurs par défaut sont fixées à la compilation
  de l'hôte (catalogue officiel, `catalog:official` et `github.com/Kazo-ca` de
  confiance) par les propriétés MSBuild `HackerOsAppCatalogs`,
  `HackerOsTrustedAppSources` et `HackerOsAppUpdateIntervalHours`. Un
  administrateur ajoute une source sans redéployer, par `hackpkg sources add`,
  Paramètres ▸ Apps ▸ Sources et liste noire, ou `sudo nano`.
- **Niveaux de confiance** : *vérifiée* pour le catalogue officiel, *de
  confiance* pour une source intégrée ou marquée `true`, *non fiable* sinon.
- **Liste noire** : `/etc/hackeros/app-policy.config` (créé au démarrage) a
  `[blocked-apps]`, `[blocked-categories]` et `[blocked-capabilities]`, avec des
  défauts fixés à la compilation (`HackerOsBlockedApps`,
  `HackerOsBlockedAppCategories`, `HackerOsBlockedAppCapabilities`). Une app
  bloquée ne s'installe pas pour soi : seulement pour tous, après validation.
- **Installation pour soi** : sans `--system`, une app d'une source de
  confiance, non bloquée et pas déjà installée pour le système s'installe dans
  `~/.local/share/hackeros/apps/<appId>/<version>/` avec une ligne dans
  `[installed]` de `~/.config/hackeros/installed-apps.config`, sans
  administrateur. Une seule copie à la fois : une installation pour tous retire
  les copies personnelles, et une copie personnelle d'une app déjà installée
  pour le système est refusée.
- **Validation en deux étapes** : une installation pour tous, une source non
  fiable (même pour soi) et une désinstallation pour tous demandent, à chaque
  fois et même sous `sudo`, une connexion administrateur fraîche puis la
  fenêtre qui montre l'app, sa version, sa taille, sa source, son niveau de
  confiance et ses capacités (`AppInstallApprovalOverlay`). La même fenêtre sert
  au Store et à `hackpkg`. `--allow-untrusted` n'existe plus. Modifier les
  sources ou la liste noire demande une seule authentification administrateur.
- **Catalogue** : il est construit au démarrage de la page, avec les apps
  personnelles de tous les utilisateurs ; chaque session ne voit que les
  siennes (`InstalledPackageCatalog`). Si deux utilisateurs ont des versions
  différentes de la même app, la plus récente est chargée pour les deux.
- **Vérifications avant la demande d'administrateur** : SHA-256 du paquet
  (`index.json`) et de chaque fichier, identifiant, `sdkCompatibility`, API
  présentes dans l'OS, app sur le fil principal.
- **Écriture pour tous** : `/usr/lib/hackeros/apps/<appId>/<version>/` et une ligne
  `<appId>=github:owner/repo@version` dans `[installed]` de
  `/etc/hackeros/apps.config`. Le dossier de la version précédente est gardé pour
  `hackpkg rollback` (une seule, [ADR 0214](adr/0214-hack-store-hot-add-signing-transactions.md)) ;
  `hackpkg update` garde chaque copie là où elle est.
- **Sans redémarrage** ([ADR 0214](adr/0214-hack-store-hot-add-signing-transactions.md)) :
  l'app apparaît tout de suite. Seule la mise à jour, le retrait ou le retour
  arrière d'une app déjà chargée dans la page attend un redémarrage de HackerOS ;
  `hackpkg list` affiche `(restart pending)` d'ici là.
- **Capacités dangereuses** : demandées au premier lancement, comme pour toute
  app non système.
- **Publication** : `dotnet build -c Release -t:HackerOsPack` dans le projet de
  l'app écrit le paquet et `index.json` dans `bin/hackeros-packages` (propriété
  `HackerOsPackageOutput`), à pousser sur la branche `hackeros-packages`.
- **Ressources `wwwroot/`** ([ADR 0206](adr/0206-serve-installed-app-assets.md)) :
  au premier lancement de l'app dans la session, chaque ressource déclarée est
  lue par `AppAssetStore` (empreinte vérifiée) et copiée dans le cache
  `hackeros-installed-apps` ; le service worker la sert à
  `_content/<assemblage>/<chemin>`, la même adresse qu'une app compilée dans
  l'OS (balises, `import()`, CSS, Web Workers et leurs `importScripts`). Sur
  `localhost`, l'OS enregistre `service-worker.js?mode=installed-apps`, qui ne
  sert que ces fichiers. Sans service worker, les fichiers ne sont pas servis
  (ligne dans `/var/log/syslog`).
- **Limites** : voir la section du même nom de l'ADR 0182 (fil principal
  seulement, dépendances NuGet tierces non transportées) ; la limite des
  ressources `wwwroot` est levée par l'ADR 0206.

Tests : `AppPackageGatewayTests`, `AppSourcesAndPolicyTests`,
`InstalledAppsImageTests`, `AppPackageWriterTests`, `AppPackageTests`
(Platform.Core), `AppInstallApprovalOverlayTests` (Platform.Blazor),
`HackpkgCommandTests` (Commands.Wave5), le scénario `hackpkg` de
`CommandParityTests`, `InstalledAppAssetPublisherTests` (Ecosystem) et
`TrimmedPublishTests.InstalledApps.cs` (paquet de test
`Tests/fixtures/installed-app` installé par `hackpkg` dans la publication
réduite, lancé par `build/publish-trimmed.sh`).

## Le Hack Store et les catalogues

L'ADR [0212](adr/0212-hack-store-app-build.md) met en œuvre l'app **Hack
Store** (`org.hackeros.store`, ADR [0188](adr/0188-hack-store-and-app-catalog.md)) :
app système, non désinstallable, épinglée par défaut dans le menu Démarrer.

```text
hackpkg install org.example.notes          # cherché dans les catalogues
hackpkg install org.example.notes@1.0.0    # une version publiée
hackpkg install catalog:official/org.example.notes
hackpkg info org.example.notes
```

- **Catalogues** : chaque entrée de `[catalogs]` désigne un `catalog.json`
  (`schemaVersion` 1, branche `packages` du dépôt catalogue). Les chemins du
  fichier (paquet, icône, captures, descriptions) sont relatifs à
  `catalog.json`. L'OS le garde 5 minutes ; une app ou une version invalide est
  ignorée avec un avertissement dans `/var/log/syslog`.
- **Source écrite** : `<appId>=catalog:<nom>/<appId>@<version>` dans
  `[installed]`. La confiance est celle de `catalog:<nom>` ; le catalogue
  officiel est *vérifié*. Le SHA-256 et la taille viennent du catalogue ; les
  autres vérifications sont celles d'une app GitHub.
- **AppId seul** : cherché dans le catalogue officiel, puis dans les autres
  par ordre de nom.
- **L'app** : accueil par catégories, recherche (nom, description courte,
  étiquettes, identifiant, sans casse ni accents), fiche (icône, captures,
  description Markdown dessinée sans HTML, version, taille, licence,
  catégorie, date, dépôt, capacités, confiance), Installer pour moi ou pour
  tous, Ouvrir, Mettre à jour, Désinstaller, onglet **Installées** avec « Tout
  mettre à jour ». Quand une app attend un redémarrage, le bandeau propose
  **Redémarrer pour terminer** (sans administrateur). Arguments de lancement :
  `--page=installed`, `--app=<appId>`.
- **Mises à jour** : `AppUpdateMonitor` vérifie à l'ouverture de la session
  puis toutes les `[updates] interval-hours` heures, met le nombre de mises à
  jour en badge sur l'icône du Store (menu Démarrer, écran d'accueil mobile,
  barre des tâches) et envoie une notification qui ouvre l'onglet Installées.
- **Épinglage par défaut** : `start-menu.json` note par profil
  `defaultPinsApplied` ; le Store est ajouté une fois, à un nouveau profil
  comme à un profil existant, et ne revient pas après un désépinglage.

Tests : `AppCatalogDocumentTests`, les tests de catalogue
d'`AppPackageGatewayTests`, `StartMenuPreferencesServiceTests`
(Platform.Core) et `HackerOs.Apps.Store.Tests` (`StoreCatalog`,
`StoreMarkdown`, manifeste).

## Ajout sans redémarrage, signature et transactions

L'ADR [0214](adr/0214-hack-store-hot-add-signing-transactions.md) (issue #251)
termine le Hack Store.

- **Catalogue en direct** : après chaque installation, mise à jour, retrait ou
  retour arrière, la passerelle appelle `IInstalledAppsActivator`
  (`InstalledAppsActivator` dans `HackerOs.Ecosystem.Host`). Il relit les apps
  installées, remplace le contenu de l'unique `AppCatalog` de la page
  (`InstalledAppsBoot.Live`, `AppCatalog.ReplaceContents`, évènement
  `AppCatalog.Changed`) et ajoute les assemblies au registre de chargement
  paresseux. Une app dont une assembly est déjà chargée garde sa version jusqu'au
  redémarrage (`HackPackageInstallReport.RestartRequired`).
- **Signature** : champ `signature` facultatif de chaque version (`index.json`,
  `catalog.json`), ECDSA P-256 / SHA-256 en IEEE P1363, base64. Les clés
  publiques sont dans `[signing-keys]` de `/etc/hackeros/app-sources.config`
  (`catalog:official=MFkw…`, plusieurs clés séparées par des virgules) et dans la
  propriété MSBuild `HackerOsAppSigningKeys` de l'hôte, toujours exigées. Une
  source qui a une clé refuse un paquet non signé (`apps.package.unsigned`) ou mal
  signé (`apps.package.bad-signature`). La vérification passe par Web Crypto
  dans le navigateur (`BrowserAppPackageSignatureVerifier`, `webAuthn.js`
  `verify`), par `ECDsa` ailleurs.
- **Transactions** : journal `<racine>/<appId>/.transaction`, fichiers écrits
  dans `<version>.partial/` puis renommés, ligne `[installed]` comme point de
  validation, ménage après. `hackpkg repair [--system]` finit ou annule ce
  qu'une coupure a laissé (`AppPackageInstaller.RecoverAsync`, `RepairAsync`).
  `update.minimumUpgradableVersion` refuse la mise à jour en place d'une version
  trop ancienne (`apps.package.reinstall-required`).
- **Retour arrière** : une mise à jour garde la version qu'elle remplace ;
  `hackpkg rollback <appId>` et le bouton « Revenir à <version> » du Store la
  remettent, avec les approbations d'une installation de la même portée.
- **Données** : au lancement d'une app installée dont la version a changé,
  `AppDataGuard` copie `~/.config/<appId>/` et `~/.local/share/<appId>/` dans
  `~/.local/share/hackeros/app-backups/<appId>/<ancienne version>/`, puis donne
  `HACKEROS_PREVIOUS_VERSION` à l'app (mise à jour) ou remet la sauvegarde de la
  version retrouvée (retour arrière). La dernière version lancée est notée dans
  `~/.config/hackeros/app-data-versions.config`. Deux sauvegardes au plus par app.

Tests : `AppPackageGatewayTests.Transactions.cs`, `AppDataGuardTests`,
`AppPackageSignatureTests`, `AppCatalogTests` (Platform.Core),
`InstalledAppsBootTests` (Ecosystem), `HackpkgCommandTests` (Commands.Wave5).

## Tâches

- [x] `IAppInstallationService`, `AppInstallationService`, `AppsConfigDocument`.
- [x] Lecture tolérante, avertissements dans `/var/log/syslog`, identifiants inconnus conservés.
- [x] Surveillance des deux fichiers, rechargement à la connexion et à la déconnexion.
- [x] Fermeture des dépendances (désinstallation, réinstallation), refus des apps système.
- [x] Arrêt des instances, `AppEnablementRegistry.Changed`.
- [x] Filtrage : lanceur, recherche, « Open With... », associations, intentions, `launch`, terminal.
- [x] Migration `AppEnablementToFilesMigration` et retrait de l'hydratation `IsEnabled` au démarrage.
- [x] Retrait d'`AppCatalogSyncService` et de son contrat.
- [x] Onglet « Installed Apps » refait (`InstalledAppsPanel`).
- [x] Tests de contrat et documentation.
- [x] Hack Store : catalogues, `hackpkg install <appId>`, mises à jour, badge, épinglage par défaut (ADR 0212).
- [x] Ajout sans redémarrage, signature, installation transactionnelle, `hackpkg rollback` et `repair`, sauvegarde des données (ADR 0214).
