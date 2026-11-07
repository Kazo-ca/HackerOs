> [English version](../../../en/wasm/framework/capabilities.md)

# Capacités : catalogue, classement et attribution

## Objectif

Une capacité est un droit exact qu'une app déclare dans sa définition
(`AppDefinition.Capabilities`) et que l'OS lui accorde, ou non, à la connexion.
Ce document décrit le catalogue `AppCapabilities`, son classement en capacités
normales et dangereuses (ADR 0049, section 1) et la règle d'attribution du
seeder (ADR 0049, section 2).

La fenêtre d'approbation administrateur, `security.config`,
`app-permissions.config` et `auth.log` (ADR 0049, sections 3 à 8) sont décrits
dans [`dangerous-capability-approval.md`](../dev/dangerous-capability-approval.md). À
la connexion, une capacité dangereuse d'une app non système n'est **jamais**
accordée : seule la passerelle de lancement l'accorde, après une décision
« allow » d'un administrateur.

## Catalogue et classement

`Shared/Kazo.Os.App.Abstractions/AppCapabilities.cs` contient, pour chaque
capacité, une `CapabilityDescription` : identifiant, titre affiché, classement
`CapabilityRisk` et, pour une capacité dangereuse, la phrase de risque montrée à
l'administrateur. Le catalogue (`AppCapabilities.All`) est exactement l'ensemble
des clés de cette table : on ne peut pas ajouter une capacité sans choisir son
classement.

| Capacité | Classement |
|---|---|
| `filesystem.private.*`, `filesystem.user-selected.*`, `filesystem.user-home.*` | normale |
| `filesystem.system.read`, `filesystem.system.write` | **dangereuse** (Diagnostic lit l'isolation par `KazoCrossOriginIsolation`, sans capacité, et ne la déclare plus, ADR 0146) |
| `apps.launch` | normale |
| `apps.manage` | **dangereuse** (exigée par `KazoAppManagement`, ADR 0056 §3, et `KazoComponentProviders`, ADR 0156 ; déclarée par Paramètres) |
| `dialogs.file-open`, `dialogs.file-save`, `dialogs.folder-select` | normale |
| `file-associations.read` | normale |
| `file-associations.write` | **dangereuse** |
| `process.list` | normale |
| `process.manage`, `services.manage`, `windows.manage` | **dangereuse** |
| `notifications.post`, `clipboard.read`, `clipboard.write` | normale |
| `network.simulated.read`, `network.simulated.write` | normale |
| `network.real.access` | **dangereuse** (vérifiée aussi par le serveur optionnel avant de relayer, ADR 0102 §4 et 0108 §1 ; exigée aussi par `KazoClientWebSocket`, sans serveur, ADR 0235 §1, [`sdk-network.md`](sdk-network.md#websocket-vers-un-vrai-serveur-adr-0235-1)) |
| `network.listen` | normale (écouter un port qui n'est pas une URL de son manifeste, [`listening-ports-and-dns.md`](listening-ports-and-dns.md)) |
| `network.listen.privileged` | **dangereuse** (écouter un port sous 1024, comme `CAP_NET_BIND_SERVICE`) |
| `shell.control` | normale (ADR 0051 : n'agit que sur le shell de l'utilisateur qui a tapé la commande, voir [`shell-commands.md`](../dev/shell-commands.md)) |
| `screens.manage` | normale (ADR 0184 : lister, ouvrir, restaurer, fermer et identifier les écrans par `KazoScreens`, comme Paramètres ▸ Écrans ; chaque appel part d'un clic de l'utilisateur) |
| `shell.context-menu` | normale (ADR 0151 §6 : ajouter des entrées aux menus des fichiers ; sans elle, le générateur produit `KOSAPP0027` et l'OS ignore les entrées, voir [`context-menu.md`](context-menu.md#entrées-des-apps-dans-les-menus-des-fichiers-chantier-4)) |
| `permissions.manage` | **dangereuse** (`KazoPermissionAdministration`, ADR 0056 §9 ; déclarée par Paramètres) |
| `server.connection.manage` | **dangereuse** (`KazoServerConnection`, ADR 0056 §10 ; déclarée par Paramètres) |
| `system.power` | **dangereuse** (`KazoSystem` : redémarrage, réinitialisation, ADR 0056 §11 ; déclarée par Paramètres) |
| `system.storage.inspect` | **dangereuse** (`KazoStorageDiagnostics`, ADR 0056 §11 ; déclarée par Diagnostic, seule app non système de la base à demander une approbation au premier lancement) |
| `system.backup` | **dangereuse** (`KazoSystemBackup` : sauvegarder et restaurer toute la machine, ADR 0099 §1 ; déclarée par Paramètres, et par `kazobackup` qui exige `sudo`, voir [`system-backup.md`](../dev/system-backup.md)) |
| `shell.host` | **dangereuse** (ADR 0056 §7 : héberger un shell dont les commandes agissent avec leurs propres droits, voir [`shell-host.md`](shell-host.md)) |
| `users.manage` | **dangereuse** (ADR 0120 : réinitialiser ou retirer le mot de passe d'un autre compte par `KazoUserAccounts`, avec un administrateur ; déclarée par Paramètres et `passwd` ; voir [`sensitive-settings.md`](../dev/sensitive-settings.md)). Phrase de risque : « Réinitialiser ou retirer le mot de passe d'un autre compte, et donc ouvrir sa session. » Changer son propre mot de passe n'en demande aucune. |
| `keyring.read-all` | **dangereuse** (ADR 0100 §2 : lire les entrées du trousseau de toutes les apps, pour un gestionnaire de mots de passe ; voir [`sensitive-settings.md`](../dev/sensitive-settings.md)). Phrase de risque : « Lire les mots de passe, jetons et clés que toutes vos applications ont enregistrés. » |

Les phrases de risque sont celles de la table de l'ADR 0049.

**Retirées du catalogue :** `settings.read`, `settings.write`,
`settings.system.read`, `settings.system.write` (ADR 0044 : les paramètres sont
des fichiers, protégés par `filesystem.*`) et `diagnostics.read`,
`diagnostics.clear` (ADR 0045 : les journaux sont des fichiers). Une définition
d'app qui les cite ne compile plus, et un `manifest.json` qui les nomme est
rejeté par le validateur (`manifest.capability.unknown`). `SettingsScopePolicy`,
qui ne servait qu'aux tests et lisait `settings.system.*`, est supprimé.

### Capacité demandée selon le chemin

`FileSystemService.ResolveCapability` (Platform Core) choisit la capacité qu'une
opération de fichier demande, avant les bits de permission :

| Chemin | Lecture | Écriture |
|---|---|---|
| `~/.config/<appId>/` (ses paramètres, ADR 0044) | `filesystem.private.read` | `filesystem.private.write` |
| `/etc/<appId>/` | `filesystem.private.read` | `filesystem.system.write` (et l'autorité d'administrateur) |
| `/home/<utilisateur>/` de l'utilisateur qui agit | `filesystem.user-home.read` | `filesystem.user-home.write` |
| un lecteur réseau (`hostfs`, ADR 0181) monté par l'utilisateur qui agit ([ADR 0231](../dev/adr/0231-network-drives-count-as-personal-files.md)) | `filesystem.user-home.read` | `filesystem.user-home.write` |
| tout le reste : `/mnt` lui-même, le lecteur d'un autre utilisateur, `/etc`, `/var`… | `filesystem.system.read` | `filesystem.system.write` |

Pour son propre lecteur réseau, une app qui n'a que `filesystem.system.*`
(`mount`, `umount`, `df`…) garde l'accès qu'elle avait : l'ADR 0231 élargit,
elle ne retire rien. Les bits de permission et le propriétaire s'appliquent
ensuite comme partout (le lecteur reste en `0700`). Le résultat est le même pour
une app dans un Web Worker, dont chaque opération de fichier passe par ce
service. À l'installation, `filesystem.user-home.read` s'affiche « Lire vos
fichiers, y compris vos lecteurs réseau » et `.write` « Écrire dans vos
fichiers, y compris vos lecteurs réseau » (`Strings.fr.resx`, `Strings.resx`).

## API

```csharp
CapabilityRisk risk = AppCapabilities.GetRisk(AppCapabilities.ProcessManage); // Dangerous
bool dangerous = AppCapabilities.IsDangerous("network.real.access");          // true
CapabilityDescription d = AppCapabilities.Describe(AppCapabilities.FileSystemSystemWrite);
// d.Title          : "Modifier les fichiers du système"
// d.RiskDescription: "Modifier des fichiers du système, dont /etc et les paramètres de toutes les apps."
```

- `GetRisk` et `Describe` lèvent `ArgumentException` pour un identifiant hors
  catalogue.
- `IsDangerous` renvoie `false` pour tout identifiant hors catalogue, en
  particulier les permissions de sujet (`topic-publish:` / `topic-subscribe:`,
  ADR 0040), qu'un manifeste peut aussi lister et qui ne sont pas classées.

## Attribution à la connexion

`LocalSessionService` appelle, pour chaque manifeste du catalogue,
`CleanProfileCapabilityGrantSeeder.SeedDeclaredCapabilities(repository, manifest, userId, isSystemApp)`
avec `isSystemApp = AppCatalog.IsSystemApp(manifest.Id)` (ADR 0048).

| App | Capacité normale déclarée | Capacité dangereuse déclarée |
|---|---|---|
| Système | accordée, source `BuildProfile` | accordée, source `SystemPolicy` |
| Non système | accordée, source `BuildProfile` | **non accordée** ; la passerelle de lancement l'accorde (source `AdministratorApproval`) si une décision « allow » existe |

Une capacité non déclarée n'est jamais accordée. `AppLifecycleOrchestrator`
calcule les capacités d'une instance à partir du dépôt de grants : une app non
système qui utilise une capacité dangereuse reçoit donc
`KazoUnauthorizedAccessException` comme pour toute capacité absente.

Dans l'hôte Ecosystem, aucune app non système ne déclare de capacité
dangereuse : l'expérience de base ne change pas.

## Politique `policy.config`

La clé `capabilityGrantsLocked`, jamais évaluée, est retirée du schéma de
`/etc/kazoos/policy.config`. Il n'y reste que `allowRuntimePackageInstall`.
Une ancienne ligne `capabilityGrantsLocked=…` dans le fichier est désormais une
clé inconnue.

## Tests

- `AppCapabilitiesTests` (`Kazo.Os.App.Abstractions.Tests`) : classement exact
  de chaque capacité selon l'ADR, catalogue égal à la table classée, chaque
  capacité décrite (phrase de risque seulement pour les dangereuses),
  `settings.*` et `diagnostics.*` absents, permissions de sujet jamais
  dangereuses.
- `CleanProfileCapabilityGrantSeederTests` (`Kazo.Os.Platform.Core.Tests`) :
  source `BuildProfile` pour une capacité normale, `SystemPolicy` pour une
  capacité dangereuse d'une app système, aucune grant dangereuse pour une app
  non système.
- `BuildKnownLazyAppsTests.No_non_system_app_of_the_host_declares_a_dangerous_capability`
  (`Kazo.Os.Ecosystem.Tests`).
- `PolicySettingsDocumentsTests` : `capabilityGrantsLocked` absent du schéma.
- `NetworkDriveAppAccessTests` (`Kazo.Os.Platform.Core.Tests`, ADR 0231) :
  `filesystem.user-home.*` pour son propre lecteur réseau, `filesystem.system.*`
  pour le lecteur d'un autre utilisateur et pour `/mnt`, une app qui n'a que
  `filesystem.system.read` garde l'accès, un lecteur déconnecté répond comme
  avant.
- `NetworkDriveWorkerTests` (`Kazo.Os.Platform.WorkerHost.Tests`) : une commande
  qui n'a que `filesystem.user-home.read` lit son lecteur par
  `KazoFile.ReadAllBytesAsync`, dans un Web Worker et sur le fil principal, et
  ne lit pas celui d'un autre utilisateur.

## Tâches

- [x] `rebuild/2-capability-classification` : `CapabilityRisk`, `GetRisk`,
  `IsDangerous`, `Describe`, `apps.manage`, retrait de `settings.*` et
  `diagnostics.*` (catalogue, définitions d'app, manifestes générés, tests,
  `SettingsScopePolicy`), seeder selon le statut système, retrait de
  `capabilityGrantsLocked`.
- [x] `rebuild/2-dangerous-approval` (voir
  [`dangerous-capability-approval.md`](../dev/dangerous-capability-approval.md)) : `security.config`,
  `app-permissions.config`, `IDangerousCapabilityGate` au lancement, fenêtre
  spéciale, mode texte du terminal, `auth.log`, vue Permissions des Paramètres.
- [x] `v3/7-storage-backup` : capacité dangereuse `system.backup` (ADR 0099).
