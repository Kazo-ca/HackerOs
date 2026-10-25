# Décisions d'architecture (ADR)

Un ADR accepté n'est jamais modifié : un nouvel ADR le remplace et le dit. Le
prochain numéro libre est indiqué dans
[`../../memory/README.md`](../../memory/README.md). L'index général
([`../../README.md`](../../README.md)) résume les ADR par phase.

| ADR | Titre |
| --- | --- |
| [`0001-target-dotnet-10.md`](0001-target-dotnet-10.md) | ADR 0001: Target .NET 10 |
| [`0002-authority-comes-from-policy.md`](0002-authority-comes-from-policy.md) | ADR 0002: Authority Comes from Trusted Policy |
| [`0003-exact-capability-matching.md`](0003-exact-capability-matching.md) | ADR 0003: Match Capabilities Exactly |
| [`0004-settings-files-are-projections.md`](0004-settings-files-are-projections.md) | ADR 0004: Settings Files Are Canonical Projections |
| [`0005-deterministic-app-dependency-order.md`](0005-deterministic-app-dependency-order.md) | ADR 0005: Deterministic App Dependency Order |
| [`0006-seal-window-component-lifecycle.md`](0006-seal-window-component-lifecycle.md) | ADR 0006: Seal the Window Component Lifecycle |
| [`0007-enforce-collocated-razor-assets.md`](0007-enforce-collocated-razor-assets.md) | ADR 0007: Enforce Collocated Razor Assets |
| [`0008-virtual-filesystem-model.md`](0008-virtual-filesystem-model.md) | ADR 0008: Virtual Filesystem Model |
| [`0009-window-runtime-strategy.md`](0009-window-runtime-strategy.md) | ADR 0009: Purpose-Built Window Runtime |
| [`0010-manifest-json-and-schema.md`](0010-manifest-json-and-schema.md) | ADR 0010: Canonical Manifest JSON and Schema |
| [`0011-settings-scope-layout.md`](0011-settings-scope-layout.md) | ADR 0011: Settings Scope Keys and Projection Paths |
| [`0012-process-and-clock-model.md`](0012-process-and-clock-model.md) | ADR 0012: Deterministic Process, Clock, and Resource Model |
| [`0013-local-user-session.md`](0013-local-user-session.md) | ADR 0013: Local User and Session Model |
| [`0014-shell-grammar-boundary.md`](0014-shell-grammar-boundary.md) | ADR 0014: First-Slice Shell Grammar Boundary |
| [`0015-browser-storage-and-indexeddb-adapter.md`](0015-browser-storage-and-indexeddb-adapter.md) | ADR 0015: Browser Support Baseline and IndexedDB Adapter Approach |
| [`0016-platform-ui-library.md`](0016-platform-ui-library.md) | ADR 0016: Platform UI Library Boundary |
| [`0017-pwa-cache-and-offline-strategy.md`](0017-pwa-cache-and-offline-strategy.md) | ADR 0017: Progressive Web App Caching, Offline Strategy, and Version Migration |
| [`0018-indexeddb-failure-and-recovery-policy.md`](0018-indexeddb-failure-and-recovery-policy.md) | ADR 0018: IndexedDB Failure and Recovery Policy |
| [`0019-sdk-versioning-and-compatibility.md`](0019-sdk-versioning-and-compatibility.md) | ADR 0019: App SDK Versioning, Compatibility, and Deprecation Policy |
| [`0020-editor-framework-and-script-sandbox.md`](0020-editor-framework-and-script-sandbox.md) | ADR 0020: Code Editor Architecture, Framework Selection, and Script Execution Sandbox Policy |
| [`0021-simulated-network-and-browser-rendering.md`](0021-simulated-network-and-browser-rendering.md) | ADR 0021 — Simulated Network Contracts and Browser Rendering Model |
| [`0022-multi-monitor-requirement-position.md`](0022-multi-monitor-requirement-position.md) | ADR 0022 — Multi-Monitor Requirement Position |
| [`0023-optional-game-domain-and-proxy-fallback.md`](0023-optional-game-domain-and-proxy-fallback.md) | ADR 0023: Optional Game Domain Integration & Network Proxy Fallback |
| [`0024-server-identity-and-device-registration.md`](0024-server-identity-and-device-registration.md) | ADR 0024: Server Identity and Device Registration |
| [`0025-record-synchronization-envelope-and-conflict-model.md`](0025-record-synchronization-envelope-and-conflict-model.md) | ADR 0025: Record Synchronization Envelope, Conflict Model, and Cursor Strategy |
| [`0026-icon-library-support.md`](0026-icon-library-support.md) | ADR 0026: Shared Icon Library Support |
| [`0027-server-hosted-blazor-ui.md`](0027-server-hosted-blazor-ui.md) | ADR 0027: Server-Hosted Blazor UI (Third Host, Single-Tenant Phase) |
| [`0028-client-side-server-connection.md`](0028-client-side-server-connection.md) | ADR 0028: Client-Side Optional-Server Connection and Proxy Bridge |
| [`0029-settings-sync.md`](0029-settings-sync.md) | ADR 0029: Settings Domain Sync (First Client Sync Implementation) |
| [`0030-filesystem-sync.md`](0030-filesystem-sync.md) | ADR 0030: FileSystem Domain Sync |
| [`0031-grants-sync.md`](0031-grants-sync.md) | ADR 0031: Grants Domain Sync (Pull-Only) |
| [`0032-app-enablement-management.md`](0032-app-enablement-management.md) | ADR 0032: App Enablement Management |
| [`0033-appcatalog-and-fileassociations-sync.md`](0033-appcatalog-and-fileassociations-sync.md) | ADR 0033: AppCatalog and FileAssociations Domain Sync |
| [`0034-wire-terminal-command-catalog.md`](0034-wire-terminal-command-catalog.md) | ADR 0034: Wire the Terminal Command Catalog |
| [`0035-nmap-tcp-probe-fallback.md`](0035-nmap-tcp-probe-fallback.md) | ADR 0035: Single-Port TCP Reachability Probe for nmap's Real-Network Fallback |
| [`0036-direct-service-injection-for-server-host.md`](0036-direct-service-injection-for-server-host.md) | ADR 0036: Direct Service Injection for the Server-Hosted UI (Pass N+5) |
| [`0037-reusable-file-view-control.md`](0037-reusable-file-view-control.md) | ADR 0037: `FileView` as the Canonical File-Listing Control |
| [`0038-emitter-authorized-topic-messaging.md`](0038-emitter-authorized-topic-messaging.md) | ADR 0038: Emitter-Authorized Topic Messaging |
| [`0039-directory-open-file-association.md`](0039-directory-open-file-association.md) | ADR 0039: `inode/directory` Media-Type Convention for Directory File Association |
| [`0040-declared-topic-permissions.md`](0040-declared-topic-permissions.md) | ADR 0040: Declared Topic Permissions and Optional Per-Channel Access |
| [`0041-shared-theme-catalog-and-scoped-tokens.md`](0041-shared-theme-catalog-and-scoped-tokens.md) | ADR 0041: Shared Theme Catalog and Scoped CSS Tokens |
| [`0042-sudo-root-elevation.md`](0042-sudo-root-elevation.md) | ADR 0042: `sudo` Root Elevation and the Admin-Approval Prompt |
| [`0043-hack-file-api-and-ambient-context.md`](0043-hack-file-api-and-ambient-context.md) | ADR 0043 : API fichier `Hack*` de type System.IO et contexte d'exécution ambiant |
| [`0044-settings-as-files.md`](0044-settings-as-files.md) | ADR 0044 : Paramètres en fichiers, en couches `/etc` puis `~/.config` |
| [`0045-logs-as-files.md`](0045-logs-as-files.md) | ADR 0045 : Journaux en fichiers texte dans `/var/log`, avec rotation |
| [`0046-virtual-proc-and-dev-filesystems.md`](0046-virtual-proc-and-dev-filesystems.md) | ADR 0046 : Systèmes de fichiers virtuels `/proc` et `/dev` |
| [`0047-app-definition-and-generated-manifests.md`](0047-app-definition-and-generated-manifests.md) | ADR 0047 : Manifeste en classe C# (`AppDefinition`) et manifestes JSON générés |
| [`0048-system-apps-and-uninstall.md`](0048-system-apps-and-uninstall.md) | ADR 0048 : Apps système, désinstallation machine ou utilisateur, et réinstallation |
| [`0049-dangerous-capabilities-and-admin-approval.md`](0049-dangerous-capabilities-and-admin-approval.md) | ADR 0049 : Capacités dangereuses et approbation par un administrateur |
| [`0050-shell-grammar-and-process-environment.md`](0050-shell-grammar-and-process-environment.md) | ADR 0050 : Grammaire du shell et environnement de processus |
| [`0051-unified-commands-project.md`](0051-unified-commands-project.md) | ADR 0051 : Commandes regroupées, sans exception codée en dur |
| [`0052-trash.md`](0052-trash.md) | ADR 0052 : Corbeille |
| [`0053-hack-environment-async-directory-change.md`](0053-hack-environment-async-directory-change.md) | ADR 0053 : Changement asynchrone du répertoire courant et lecture de l'environnement de connexion |
| [`0054-shell-exit-status-and-detached-execution.md`](0054-shell-exit-status-and-detached-execution.md) | ADR 0054 : `$?` par pipeline, shell détaché et écritures synchrones dans un tube |
| [`0055-base-apps-on-public-sdk-only.md`](0055-base-apps-on-public-sdk-only.md) | ADR 0055 : Apps de base sur le SDK public uniquement |
| [`0056-sdk-apis-for-system-apps.md`](0056-sdk-apis-for-system-apps.md) | ADR 0056 : API du SDK qui remplacent les services internes |
| [`0057-proc-metrics-for-task-manager.md`](0057-proc-metrics-for-task-manager.md) | ADR 0057 : Mesures système dans `/proc` et `HackDriveInfo` |
| [`0058-base-app-features.md`](0058-base-app-features.md) | ADR 0058 : Fonctionnalités des apps de base |
| [`0059-sdk-boundary-diagnostic-codes.md`](0059-sdk-boundary-diagnostic-codes.md) | ADR 0059 : Codes des diagnostics de la frontière du SDK |
| [`0060-system-administration-sdk-adjustments.md`](0060-system-administration-sdk-adjustments.md) | ADR 0060 : Ajustements de l'API d'administration du système |
| [`0061-xterm-terminal-emulator.md`](0061-xterm-terminal-emulator.md) | ADR 0061 : xterm.js comme émulateur du terminal |
| [`0062-code-editor-workbench.md`](0062-code-editor-workbench.md) | ADR 0062 : l'éditeur de code en atelier à la VS Code |
| [`0063-embeddable-app-components.md`](0063-embeddable-app-components.md) | ADR 0063 : composants d'app intégrables, enregistrés auprès de l'OS |
| [`0064-posix-shell-scripts-jobs-and-app-commands.md`](0064-posix-shell-scripts-jobs-and-app-commands.md) | ADR 0064 : shell POSIX, scripts, jobs, apps lancées par leur nom et commandes Linux |
| [`0065-os-clipboard-and-host-bridge.md`](0065-os-clipboard-and-host-bridge.md) | ADR 0065 : presse-papiers de l'OS et partage de texte avec l'hôte |
| [`0066-optional-resource-accounting.md`](0066-optional-resource-accounting.md) | ADR 0066 : mesure des ressources optionnelle, au build et au démarrage |
| [`0067-measured-cpu-time.md`](0067-measured-cpu-time.md) | ADR 0067 : temps CPU mesuré par processus et état « Not responding » |
| [`0068-measured-process-memory.md`](0068-measured-process-memory.md) | ADR 0068 : mémoire mesurée par processus, en couches |
| [`0069-graceful-stop-and-checkpoints.md`](0069-graceful-stop-and-checkpoints.md) | ADR 0069 : arrêt demandé, points d'arrêt volontaires et escalade |
| [`0070-worker-hosted-processes.md`](0070-worker-hosted-processes.md) | ADR 0070 : processus sans interface dans des Web Workers |
| [`0071-always-ready-worker.md`](0071-always-ready-worker.md) | ADR 0071 : toujours un worker prêt pour la prochaine demande |
| [`0072-worker-processes-implementation-adjustments.md`](0072-worker-processes-implementation-adjustments.md) | ADR 0072 : ajustements de la mise en œuvre des processus dans des Web Workers |
| [`0073-web-apps.md`](0073-web-apps.md) | ADR 0073 : apps web (genre `Web`), sites externes et sites hébergés |
| [`0074-listening-ports-and-name-resolution.md`](0074-listening-ports-and-name-resolution.md) | ADR 0074 : ports à l'écoute, résolveur DNS de l'OS et corps HTTP réels |
| [`0075-browser-html-rendering.md`](0075-browser-html-rendering.md) | ADR 0075 : rendu HTML du navigateur dans une iframe isolée |
| [`0076-asp-hack-mvc.md`](0076-asp-hack-mvc.md) | ADR 0076 : ASP.Hack MVC, pipeline, routage et vues |
| [`0077-hosted-static-sites.md`](0077-hosted-static-sites.md) | ADR 0077 : sites statiques hébergés, service `hackwebd` et Web Server Manager |
| [`0078-detached-display-windows.md`](0078-detached-display-windows.md) | ADR 0078 : fenêtres d'affichage détachées (préparation du deuxième écran) |
| [`0079-browser-html-rendering-adjustments.md`](0079-browser-html-rendering-adjustments.md) | ADR 0079 : ajustements du rendu HTML du navigateur |
| [`0080-hosted-static-sites-adjustments.md`](0080-hosted-static-sites-adjustments.md) | ADR 0080 : ajustements des sites statiques hébergés |
| [`0081-detached-display-windows-adjustments.md`](0081-detached-display-windows-adjustments.md) | ADR 0081 : fenêtres détachées, ajustements de la mise en œuvre |
| [`0082-asp-hack-route-source-generator.md`](0082-asp-hack-route-source-generator.md) | ADR 0082 : table de routes ASP.Hack produite par un générateur de sources Roslyn |
| [`0083-asp-hack-razor-views.md`](0083-asp-hack-razor-views.md) | ADR 0083 : vues `.razor` d'ASP.Hack, anti-falsification et `wwwroot` embarqué |
| [`0084-blazor-sites.md`](0084-blazor-sites.md) | ADR 0084 : sites Blazor interactifs dessinés par le navigateur |
| [`0085-default-simulated-network-in-the-os.md`](0085-default-simulated-network-in-the-os.md) | ADR 0085 : le réseau simulé par défaut est branché dans l'OS |
| [`0086-asp-hack-cshtml-views.md`](0086-asp-hack-cshtml-views.md) | ADR 0086 : vues `.cshtml` d'ASP.Hack compilées par le compilateur Razor, sans ASP.NET MVC |
| [`0087-asp-hack-intranet-sample-adjustments.md`](0087-asp-hack-intranet-sample-adjustments.md) | ADR 0087 : exemple Intranet, ajustements de fin de phase web |
| [`0088-site-data-debug-logs-and-form-history.md`](0088-site-data-debug-logs-and-form-history.md) | ADR 0088 : base d'un site, journaux de débogage et saisie semi-automatique |
| [`0089-explicit-cookie-header.md`](0089-explicit-cookie-header.md) | ADR 0089 : en-tête `Cookie` écrit par l'app |
| [`0090-filesystem-path-lookup-in-one-request.md`](0090-filesystem-path-lookup-in-one-request.md) | ADR 0090 : recherche d'un chemin en une seule requête au stockage |
| [`0092-game-in-a-separate-repository.md`](0092-game-in-a-separate-repository.md) | ADR 0092 : le jeu vit dans un dépôt séparé |
| [`0094-deferred-decisions-status-and-maximum-file-size.md`](0094-deferred-decisions-status-and-maximum-file-size.md) | ADR 0094 : état des décisions différées de la migration v3 et taille maximale des fichiers |
| [`0095-window-events-sizes-and-keyboard.md`](0095-window-events-sizes-and-keyboard.md) | ADR 0095 : événements de fenêtre pour les apps, tailles déclarées et commandes clavier |
| [`0096-added-intents-and-app-defined-intents.md`](0096-added-intents-and-app-defined-intents.md) | ADR 0096 : intents ajoutés (afficher, modifier, ouvrir une URI, réglages d'une app) et intents définis par les apps |
| [`0097-service-health-and-startup.md`](0097-service-health-and-startup.md) | ADR 0097 : état et santé des services, démarrage au boot ou à la connexion, désinstallation d'une app active |
| [`0098-sdk-versions-packages-and-manifest-checks.md`](0098-sdk-versions-packages-and-manifest-checks.md) | ADR 0098 : versions et paquets NuGet du SDK et de la plateforme, vérifications du manifeste au chargement, gabarits et gel du SDK 1.0 |
| [`0099-os-backup-and-low-storage.md`](0099-os-backup-and-low-storage.md) | ADR 0099 : sauvegarde et restauration de l'OS, espace faible et `ENOSPC`, cache PWA corrompu, montées de version IndexedDB testées |
| [`0100-sensitive-settings-and-protected-write-audit.md`](0100-sensitive-settings-and-protected-write-audit.md) | ADR 0100 : réglages sensibles (fichiers privés, masquage, trousseau chiffré) et audit des écritures protégées |
| [`0101-synchronized-deletions-and-conflicts.md`](0101-synchronized-deletions-and-conflicts.md) | ADR 0101 : suppressions synchronisées, conflits résolus par l'utilisateur, synchro périodique et domaines de synchro restants |
| [`0102-server-network-relay-tcp-udp-and-policy.md`](0102-server-network-relay-tcp-udp-and-policy.md) | ADR 0102 : relais réseau du serveur optionnel (corps HTTP, flux TCP, UDP, accord vérifié, politique dans `appsettings`) |
| [`0103-hack-paint-image-files-and-theme-editor.md`](0103-hack-paint-image-files-and-theme-editor.md) | ADR 0103 : images de Hack Paint dans les fichiers de l'OS et éditeur de thèmes (thèmes utilisateur en fichiers de jetons) |
| [`0104-localization.md`](0104-localization.md) | ADR 0104 : localisation (anglais et français, `LANG` par processus, `locale.conf`, ressources `.resx`) |
| [`0105-accessibility-preferences.md`](0105-accessibility-preferences.md) | ADR 0105 : préférences d'accessibilité en fichiers (contraste élevé, taille du texte, mouvement réduit) et tests automatisés |
| [`0106-trimmed-release-publish.md`](0106-trimmed-release-publish.md) | ADR 0106 : publication Release trimmée de l'hôte Ecosystem |
| [`0107-service-startup-implementation-adjustments.md`](0107-service-startup-implementation-adjustments.md) | ADR 0107 : ajustements de la mise en œuvre des états et du démarrage des services |
| [`0108-server-relay-implementation-adjustments.md`](0108-server-relay-implementation-adjustments.md) | ADR 0108 : ajustements de la mise en œuvre du relais du serveur |
| [`0110-backup-and-storage-implementation-adjustments.md`](0110-backup-and-storage-implementation-adjustments.md) | ADR 0110 : ajustements de la mise en œuvre de la sauvegarde, de l'espace de stockage et des fixtures IndexedDB |
| [`0111-localization-implementation-adjustments.md`](0111-localization-implementation-adjustments.md) | ADR 0111 : ajustements de la mise en œuvre de la localisation |
| [`0112-accessibility-implementation-adjustments.md`](0112-accessibility-implementation-adjustments.md) | ADR 0112 : ajustements de la mise en œuvre de l'accessibilité |
| [`0113-trimming-implementation-adjustments.md`](0113-trimming-implementation-adjustments.md) | ADR 0113 : ajustements de la publication trimmée |
| [`0114-sdk-1-0-freeze.md`](0114-sdk-1-0-freeze.md) | ADR 0114 : gel du SDK 1.0, périmètre de la promesse de compatibilité et app de compatibilité |
| [`0116-window-geometry-restore.md`](0116-window-geometry-restore.md) | ADR 0116 : géométrie des fenêtres enregistrée et restaurée |
| [`0117-sdk-window-title-and-sdk-1-1.md`](0117-sdk-window-title-and-sdk-1-1.md) | ADR 0117 : titre de fenêtre posé par l'app et publication du SDK 1.1 |
| [`0120-passwd-and-administrator-password-reset.md`](0120-passwd-and-administrator-password-reset.md) | ADR 0120 : `passwd`, réinitialisation du mot de passe par un administrateur et effet sur le trousseau |
| [`0121-user-file-associations.md`](0121-user-file-associations.md) | ADR 0121 : associations de fichiers et de schémas propres à chaque utilisateur |
| [`0122-window-size-only-not-position.md`](0122-window-size-only-not-position.md) | ADR 0122 : une fenêtre retient sa taille, pas sa position |
| [`0123-window-title-implementation-adjustments.md`](0123-window-title-implementation-adjustments.md) | ADR 0123 : ajustements de la mise en œuvre du titre de fenêtre |
| [`0124-remote-event-and-watch-gateways.md`](0124-remote-event-and-watch-gateways.md) | ADR 0124 : passerelles distantes d'événements et de surveillance de fichiers, pool ouvert à toute assembly d'app |
| [`0125-process-resource-limits.md`](0125-process-resource-limits.md) | ADR 0125 : limites de ressources par processus (`setrlimit`, `ulimit`, `limits.conf`) |
| [`0126-oom-killer.md`](0126-oom-killer.md) | ADR 0126 : OOM killer désactivé par défaut et garde de la mémoire du vrai navigateur |
| [`0127-priority-and-not-responding-dialog.md`](0127-priority-and-not-responding-dialog.md) | ADR 0127 : priorité `nice` sans quantum, et boîte « L'application ne répond pas : Attendre | Terminer l'application » |
| [`0128-background-jobs-hack-background.md`](0128-background-jobs-hack-background.md) | ADR 0128 : tâches lourdes en arrière-plan (`HackBackground.RunAsync`) |
| [`0129-window-rendering-in-worker-trial.md`](0129-window-rendering-in-worker-trial.md) | ADR 0129 : essai du rendu d'une fenêtre Blazor dans un worker, et critères pour y aller |
| [`0130-server-serves-webassembly-os-and-api.md`](0130-server-serves-webassembly-os-and-api.md) | ADR 0130 : `HackerOs.Server` sert l'OS en WebAssembly et ne garde que son API |
| [`0131-cross-origin-isolation.md`](0131-cross-origin-isolation.md) | ADR 0131 : isolation cross-origin optionnelle (COOP/COEP), tubes partagés et mesures fines |
| [`0132-exec-series-sdk-1-2-and-merge-rules.md`](0132-exec-series-sdk-1-2-and-merge-rules.md) | ADR 0132 : série `exec` — ajouts du SDK 1.2, branches et barre de fusion |
| [`0133-window-rendering-in-worker-verdict.md`](0133-window-rendering-in-worker-verdict.md) | ADR 0133 : rendu d'une fenêtre dans un worker, verdict de l'essai : on s'arrête |
| [`0134-automatic-server-connection-and-status-icon.md`](0134-automatic-server-connection-and-status-icon.md) | ADR 0134 : connexion automatique au serveur qui sert l'OS, et icône d'état dans la zone de notification |
| [`0135-no-game-in-this-repository.md`](0135-no-game-in-this-repository.md) | ADR 0135 : aucun jeu sur ce repository |
| [`0136-sudo-nice.md`](0136-sudo-nice.md) | ADR 0136 : `sudo nice` lance la commande en root |
| [`0137-server-hosting-implementation-adjustments.md`](0137-server-hosting-implementation-adjustments.md) | ADR 0137 : ajustements de la mise en œuvre de l'OS servi par `HackerOs.Server` |
| [`0138-remote-gateways-implementation-adjustments.md`](0138-remote-gateways-implementation-adjustments.md) | ADR 0138 : ajustements de la mise en œuvre de l'ADR 0124 (passerelles distantes) |
| [`0139-process-limits-implementation-adjustments.md`](0139-process-limits-implementation-adjustments.md) | ADR 0139 : ajustements de la mise en œuvre des limites de processus |
| [`0140-background-jobs-implementation-adjustments.md`](0140-background-jobs-implementation-adjustments.md) | ADR 0140 : ajustements de la mise en œuvre de l'ADR 0128 (tâches en arrière-plan) |
| [`0142-oom-killer-implementation-adjustments.md`](0142-oom-killer-implementation-adjustments.md) | ADR 0142 : ajustements de la mise en œuvre de l'OOM killer |
| [`0143-cross-origin-isolation-implementation-adjustments.md`](0143-cross-origin-isolation-implementation-adjustments.md) | ADR 0143 : ajustements de la mise en œuvre de l'isolation cross-origin |
| [`0145-publish-async-from-a-worker.md`](0145-publish-async-from-a-worker.md) | ADR 0145 : `PublishAsync`, le vrai résultat d'une publication depuis un worker |
| [`0146-cross-origin-isolation-sdk-api.md`](0146-cross-origin-isolation-sdk-api.md) | ADR 0146 : `HackCrossOriginIsolation`, l'état de l'isolation dans le SDK |
| [`0147-background-task-grace-period-on-app-close.md`](0147-background-task-grace-period-on-app-close.md) | ADR 0147 : délai de 5 s pour les tâches en arrière-plan quand leur app se ferme |
| [`0148-sdk-contracts-and-os-provided-controls.md`](0148-sdk-contracts-and-os-provided-controls.md) | ADR 0148 : le SDK ne porte que des contrats ; l'OS fournit les contrôles et dialogues partagés |
| [`0149-sdk-2-0-file-view-removal.md`](0149-sdk-2-0-file-view-removal.md) | ADR 0149 : SDK 2.0, retrait de `HackerOs.AppSdk.FileView` et contrats de fichiers |
| [`0150-os-context-menu.md`](0150-os-context-menu.md) | ADR 0150 : menu contextuel central de l'OS (`hackeros.context-menu`) |
| [`0151-app-registered-file-menu-entries.md`](0151-app-registered-file-menu-entries.md) | ADR 0151 : entrées de menu de fichiers inscrites par les apps |
| [`0152-desktop-icons.md`](0152-desktop-icons.md) | ADR 0152 : icônes du bureau |
| [`0153-contract-host-implementation-adjustments.md`](0153-contract-host-implementation-adjustments.md) | ADR 0153 : ajustements de l'hôte de contrats générique |
| [`0154-context-menu-implementation-adjustments.md`](0154-context-menu-implementation-adjustments.md) | ADR 0154 : ajustements du menu contextuel de l'OS |
| [`0155-file-control-implementation-adjustments.md`](0155-file-control-implementation-adjustments.md) | ADR 0155 : ajustements du contrôle de fichiers fourni par l'explorateur |
| [`0156-replaceable-file-control-provider.md`](0156-replaceable-file-control-provider.md) | ADR 0156 : fournisseur du contrôle de fichiers remplaçable par une app tierce |
| [`0157-file-dialogs-show-the-file-control.md`](0157-file-dialogs-show-the-file-control.md) | ADR 0157 : les dialogues Ouvrir / Enregistrer / Choisir un dossier montrent le contrôle de fichiers |
| [`0158-replaceable-file-provider-implementation-adjustments.md`](0158-replaceable-file-provider-implementation-adjustments.md) | ADR 0158 : ajustements du fournisseur de fichiers remplaçable |
| [`0159-app-menu-entries-implementation-adjustments.md`](0159-app-menu-entries-implementation-adjustments.md) | ADR 0159 : entrées de menu des apps, ajustements d'implémentation |
| [`0160-desktop-implementation-adjustments.md`](0160-desktop-implementation-adjustments.md) | ADR 0160 : bureau, ajustements d'implémentation |
| [`0161-sdk-2-0-implementation-adjustments.md`](0161-sdk-2-0-implementation-adjustments.md) | ADR 0161 : SDK 2.0, ajustements d'implémentation |
| [`0162-restart-after-a-host-build-change.md`](0162-restart-after-a-host-build-change.md) | ADR 0162 : redémarrer quand le build a changé sous la page |
| [`0163-window-pointer-preview-and-invariant-coordinates.md`](0163-window-pointer-preview-and-invariant-coordinates.md) | ADR 0163 : aperçu du déplacement et coordonnées invariantes |
| [`0164-app-icons-and-window-icon-context-menu.md`](0164-app-icons-and-window-icon-context-menu.md) | ADR 0164 : icônes des apps et menu contextuel de l'icône de fenêtre |
| [`0165-mobile-home-drawer-and-mouse-controls.md`](0165-mobile-home-drawer-and-mouse-controls.md) | ADR 0165 : accueil mobile, tiroir et aides souris |
| [`0166-mobile-layout-conf-on-desktop.md`](0166-mobile-layout-conf-on-desktop.md) | ADR 0166 : disposition mobile en fichier conf sous Desktop |
| [`0167-combined-pages-deployment.md`](0167-combined-pages-deployment.md) | ADR 0167 : publication Pages combinée et builds indépendants |
| [`0170-settings-side-menu.md`](0170-settings-side-menu.md) | ADR 0170 : menu latéral réductible dans Paramètres |

Index du dossier parent : [`../README.md`](../README.md).
