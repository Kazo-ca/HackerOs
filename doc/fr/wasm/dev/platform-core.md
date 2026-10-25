> [English version](../../../en/wasm/dev/platform-core.md)

# Plateforme : `HackerOs.Platform.Core`

## Objet

`Platform/HackerOs.Platform.Core` contient les services de l'OS, sans interface
graphique : ce qui fait le système simulé (fichiers, processus, shell, sessions,
capacités, réseau simulé, journaux). Il implémente les passerelles que le SDK
expose aux apps (`IAppExecutionContext`, `Hack*`) et ne doit jamais être
référencé par une app (règle de la frontière, ADR 0055). Les contrats qu'il
implémente sont dans `Shared/HackerOs.Simulation.Abstractions` et
`Shared/HackerOs.App.Abstractions` ; l'interface graphique est dans
`HackerOs.Platform.Blazor`, le stockage navigateur dans
`HackerOs.Infrastructure.Browser` ([`browser-infrastructure.md`](browser-infrastructure.md)).

Vue d'ensemble des couches : [`project-overview.md`](project-overview.md).
Environ 240 fichiers C# (51 000 lignes) ; le dossier se lit par sous-système.

## Où se trouve quoi

| Dossier | Rôle | Documents |
|---|---|---|
| `FileSystem/` | Service de fichiers, routeur de montages, autorisation, audit, fournisseurs `/proc` et `/dev`, amorçage de l'arbre | [`virtual-proc-and-dev.md`](virtual-proc-and-dev.md), [`hack-file-api.md`](../framework/hack-file-api.md) |
| `Execution/` | Passerelles par instance (`AppFileSystemGateway`, `AppProcessGateway`…), `AppExecutionContextFactory`, tâches de fond (`BackgroundJobService`) | [`app-sdk.md`](../framework/app-sdk.md), [`background-jobs.md`](../framework/background-jobs.md) |
| `Processes/` | Processus en mémoire (`InMemoryProcessManager`, blocs de contrôle), environnement, limites (`ResourceLimitEnforcer`), tueur de mémoire (`OomKiller`), échantillonnage | [`process-environment.md`](process-environment.md), [`process-limits.md`](process-limits.md), [`oom-killer.md`](oom-killer.md) |
| `Lifecycle/` | Cycle de vie des apps : lancement, arrêt, services `AutoStart`, activation des apps (`AppLifecycleOrchestrator`) | [`app-installation.md`](app-installation.md) |
| `Shell/` | Interpréteur `hsh` (analyse, expansion, redirections, tubes, tâches, `ulimit`, scripts), complétion, `ShellHostService` | [`shell-interpreter.md`](shell-interpreter.md), [`shell-host.md`](../framework/shell-host.md) |
| `Sessions/`, `Elevation/` | Comptes locaux et mots de passe (hachage), session de connexion, `sudo`, agent d'authentification de l'administrateur | [`admin-authentication-agent.md`](admin-authentication-agent.md) |
| `Policy/` | Accord des capacités, capacités dangereuses (`DangerousCapabilityGate`), décisions par app | [`dangerous-capability-approval.md`](dangerous-capability-approval.md), [`capabilities.md`](../framework/capabilities.md) |
| `Settings/`, `Appearance/` | Documents de réglages en fichiers (`OsSettingsService`, validateurs, migration depuis l'ancien magasin), apparence, accessibilité, thèmes utilisateur | [`settings-files.md`](../framework/settings-files.md), [`sensitive-settings.md`](sensitive-settings.md), [`accessibility.md`](accessibility.md) |
| `Apps/`, `Discovery/` | Catalogue et installation des apps (`AppInstallationService`), manifestes, découverte des points d'entrée | [`app-installation.md`](app-installation.md) |
| `Intents/` | Résolution des intents et associations de fichiers par utilisateur | [`user-file-associations.md`](user-file-associations.md), [`app-intents.md`](../framework/app-intents.md) |
| `ContextMenus/`, `ComponentProviders/` | Registre du menu contextuel de l'OS, fournisseurs de contrats de composants | [`context-menu.md`](../framework/context-menu.md), [`embedded-components.md`](../framework/embedded-components.md) |
| `Network/` | Réseau simulé : service, table des ports en écoute, résolution DNS, relais vers le serveur, sites simulés (`WebAppActivator`) | [`listening-ports-and-dns.md`](../framework/listening-ports-and-dns.md), [`sdk-network.md`](../framework/sdk-network.md), [`server-network-relay.md`](server-network-relay.md) |
| `ServerConnection/` | Connexion optionnelle au serveur, présence, planificateur de synchronisation, conflits, client de synchro | [`synchronization.md`](synchronization.md), [`server-hosting.md`](server-hosting.md) |
| `Keyring/` | Trousseau de l'utilisateur, chiffrement de l'enveloppe, clé de session | [`sensitive-settings.md`](sensitive-settings.md) |
| `Diagnostics/`, `Metrics/` | Journal noyau (`KernelLog`) et fichiers `/var/log`, rotation, redaction, compteurs CPU et E/S | [`logging.md`](logging.md), [`system-metrics.md`](system-metrics.md) |
| `Backup/`, `Storage/` | Sauvegarde et restauration de la machine, surveillance de l'espace | [`system-backup.md`](system-backup.md) |
| `Administration/` | Passerelles d'administration du SDK (paramètres système, alimentation, sauvegarde, diagnostics du stockage, comptes) | [`sdk-reference.md`](../framework/sdk-reference.md) |
| `Clipboard/`, `Notifications/`, `Time/`, `Power/`, `Isolation/`, `Globalization/`, `Events/` | Presse-papiers, notifications, horloge simulée, arrêt, isolation cross-origin, langue de session, bus d'événements | [`clipboard.md`](clipboard.md), [`worker-processes.md`](worker-processes.md#isolation-cross-origin-adr-0131-adr-0143) |
| `WebData/` | Base de données des sites ASP.Hack (`WebSiteDatabaseStore`) | [`asp-hack-site-data.md`](../framework/asp-hack-site-data.md) |

## Services et composition

- `AppExecutionContextFactory` construit le `IAppExecutionContext` de chaque
  instance (fenêtre, service, commande) à partir des singletons de la plateforme
  et des capacités accordées. Chaque passerelle vérifie la capacité qu'elle
  exige avant d'agir (`AppCapabilityChecker`).
- Les services sont enregistrés par `AddHackerOsEcosystem`
  (`OS/HackerOs.Ecosystem.Host`), qui déclare les services de cette couche ;
  `AddHackerOsPlatformLocalization` (`Globalization/`) en fait partie.
- Plusieurs services ont une implémentation `InMemory*` (tests, mode sans
  navigateur) ; la persistance réelle vient de l'infrastructure navigateur.

## Points à connaître

- Ce projet n'a pas de dépendance vers `Platform.Blazor` ni vers
  `Infrastructure.Browser` : le code qui touche le navigateur passe par des
  interfaces (par exemple `IStorageCapacityProbe`, `IHostAccessibilityProbe`).
- Les fichiers de réglages, de journaux et de processus sont des fichiers de
  l'arbre virtuel : écrire un réglage passe par `FileSystemService` et ses
  règles d'autorisation, pas par un magasin séparé (ADR 0044, ADR 0046).
- Les sites simulés (`Network/`) sont des contenus fictifs de l'OS, déclarés
  par l'OS et non par les apps.

## Tests

`Tests/HackerOs.Platform.Core.Tests` couvre les sous-systèmes ci-dessus ; les
tests par fonctionnalité sont dans les projets `Tests/HackerOs.Platform.*`,
`Tests/HackerOs.Ecosystem.Tests` et `Tests/HackerOs.Commands.Tests`. Commandes :
[`building-and-testing.md`](building-and-testing.md).
