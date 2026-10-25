# Administration du système dans le SDK

## Objet

Donne aux apps système les opérations d'administration qui passaient par des
services internes (ADR 0056 §8 à §11, ajusté par l'ADR 0060) :

- lire et écrire les documents de paramètres de l'OS ;
- administrer les capacités dangereuses (politique, décisions machine) ;
- se connecter au serveur optionnel et synchroniser ;
- redémarrer ou réinitialiser la machine ;
- lire l'instantané brut du stockage ;
- sauvegarder et restaurer toute la machine (ADR 0099 §1).

Paramètres et Diagnostic utilisent ces API et ne référencent plus Platform
Core ni Infrastructure.Browser.

## Architecture

| Couche | Fichiers |
|---|---|
| SDK (contrats, façades) | `Shared/HackerOs.AppSdk/Administration/` (`HackPermissionAdministration`, `HackComponentProviders`, `HackServerConnection`, `HackSystem`, `HackStorageDiagnostics` et leurs contrats), `Shared/HackerOs.AppSdk/Settings/IAppSystemSettingsGateway.cs`, `SettingsDocumentStores.cs` |
| Platform Core (passerelles) | `Platform/HackerOs.Platform.Core/Administration/` : `AppSystemSettingsGateway`, `AppPermissionAdministrationGateway`, `AppServerConnectionGateway`, `AppSystemPowerGateway`, `AppStorageDiagnosticsGateway`, `AppSystemBackupGateway`, `AppUserAccountsGateway`, `AdministratorGate`, `SystemAdministrationGatewayServices` |
| Platform Core (services) | `Power/SystemPowerService.cs` (séquence d'arrêt, `RestartAfterAsync`), `Backup/SystemBackupService.cs`, `Sessions/LocalPasswordService.cs` (ADR 0120), `Settings/OsSettingsDocumentRegistry.cs`, `Policy/AppPermissionAdministration.cs` (`ApplyMachineDecisionAsync`, `ApplyPolicyAsync`) |
| Hôte Ecosystem | `Administration/BrowserSystemPowerDevice.cs` (`location.reload`, suppression de la base IndexedDB), `Administration/BrowserSystemBackupDevice.cs` (export, téléchargement, sélecteur de l'hôte), `Administration/IndexedDbStorageInspector.cs` (adaptateur de `IndexedDbInspectorService`) |
| SDK Blazor | `Interop/BrowserClipboard.cs` et `wwwroot/clipboard.js` (presse-papiers de l'hôte), `[Obsolete]` depuis l'ADR 0065 puis retirés au gel du SDK 1.0 (ADR 0114) : Diagnostic copie par `HackClipboard`, que l'OS recopie vers l'hôte ([`clipboard.md`](../dev/clipboard.md)) |

`AppExecutionContextFactory` reçoit un `SystemAdministrationGatewayServices`
(résolutions paresseuses ; `Passwords` est une propriété `init` ajoutée en 1.1) et crée, pour chaque instance, les sept passerelles
avec les capacités et le principal de l'app. `AdministratorGate` appelle
l'agent d'authentification de l'OS
([`admin-authentication-agent.md`](../dev/admin-authentication-agent.md)) : l'app ne
voit jamais le mot de passe, root par `sudo` passe sans demande, et l'OS agit
ensuite lui-même.

## API

### Documents de l'OS

```csharp
await using HackSettingsDocument appearance = await HackSettings.OpenSystemAsync(AppContext, "appearance");
await appearance.SetAsync("accent", "cyan");                               // couche utilisateur
await appearance.SetManyAsync(values, SettingLayer.System);                // /etc : une seule approbation
```

Voir [`settings-files.md`](settings-files.md#documents-de-los-hacksettingsopensystemasync-adr-0056-8).

### Permissions (`permissions.manage`)

| Méthode | Effet | Administrateur |
|---|---|---|
| `GetPolicyAsync`, `GetOverviewAsync` | politique et état de chaque capacité dangereuse de chaque app | non |
| `SetPolicyAsync(Prompt / Block)` | écrit `/etc/hackeros/security.config`, journal `capability.policy` | oui (`permissions.policy`) |
| `SetMachineDecisionAsync(app, capacité, Allow / Deny / Ask)` | écrit `/etc/hackeros/app-permissions.config`, journal `capability.approve` / `capability.revoke` | oui (`permissions.machine-decision`) |
| `RemoveOwnDenialAsync(app, capacité)` | retire le refus de l'utilisateur, la question revient au lancement | non |

Une app système ou une capacité non déclarée par l'app est refusée **avant**
toute demande d'identifiants.

### Serveur (`server.connection.manage`)

`GetStateAsync`, `ConnectAsync`, `CreateAccountAsync`, `DisconnectAsync`,
`SyncAsync(domaine, sens)`, `GetConflictsAsync`. Domaines : `FileSystem`
(push, pull), `Grants` (pull seulement : `ArgumentException` sinon),
`FileAssociations` (push, pull). Sans serveur configuré : `NotConnected`, et
la synchronisation ne fait rien. Les erreurs réseau deviennent
`HackServerConnectionException`.

`GetRelayPolicyAsync` rend la politique du relais réseau du serveur connecté
(`HackServerRelayPolicy` : ports, plages privées, quotas, flux ouverts, apps
accordées, avertissements), ou `null` sans serveur ; Paramètres l'affiche
dans sa section du serveur ([`server-network-relay.md`](../dev/server-network-relay.md)).

ADR 0101 (chantier `v3/9-sync`) : `GetConflictsAsync` lit les conflits en
attente enregistrés dans `~/.local/state/hackeros/sync-conflicts.json`
(`HackSyncConflict` porte `DetectedAt`) ; `ShowConflictsAsync` ouvre le
dialogue de conflit de l'OS ; `GetSyncStatusAsync` rend un `HackSyncStatus`
(synchro périodique active, suspendue, en ligne, dernier passage, envoyés,
reçus, dernière erreur, intervalle) ; `SetSyncPausedAsync` suspend ou reprend
la synchro de l'utilisateur (`~/.config/hackeros/sync.config`) ;
`SyncNowAsync` lance un passage complet. Les suppressions, les conflits et la
synchro périodique sont décrits dans
[`synchronization.md`](../dev/synchronization.md).

### Alimentation (`system.power`)

`HackSystem.RebootAsync` et `HackSystem.FactoryResetAsync` demandent toujours
un administrateur (`system.reboot`, `system.factory-reset`). Séquence de
`SystemPowerService`, comme un `shutdown` :

1. entrée d'audit ;
2. vidage des journaux ;
3. fermeture de la session (`ISessionService.ShutdownAsync`) ;
4. pour la réinitialisation, effacement du stockage ;
5. redémarrage de l'hôte.

### Stockage (`system.storage.inspect`)

`HackStorageDiagnostics.GetSnapshotAsync` rend un `HackStorageSnapshot`
(magasins, nombre d'enregistrements, JSON, chemins résolus, liens) et
`ToExportJson()`. L'instantané contourne les droits des fichiers : c'est
pourquoi Diagnostic, app non système, demande un administrateur à son premier
lancement.

`GetWebSiteDatabasesAsync` liste les bases IndexedDB des sites (`hackweb.<id>` :
version, magasins, nombre d'enregistrements, taille estimée) et
`DeleteWebSiteDatabaseAsync(name)` en supprime une, avec la même capacité
(ADR 0088 §4, [`asp-hack-site-data.md`](asp-hack-site-data.md)). Diagnostic les
montre sous *Website databases*.

### Sauvegarde (`system.backup`)

`HackSystemBackup.CreateAsync` télécharge une archive `.hosbak` de toute la
machine sur l'ordinateur hôte ; `RestoreAsync(Merge | Replace)` ouvre le
sélecteur de l'hôte, valide l'archive, **puis** demande un administrateur,
ferme la session, restaure et redémarre. Création et restauration vont dans
`/var/log/auth.log`. Détails : [`system-backup.md`](../dev/system-backup.md).

### Comptes et mots de passe (`users.manage`)

`HackUserAccounts.ListAsync` et `ChangePasswordAsync` (son propre mot de passe,
après le mot de passe courant) ne demandent aucune capacité ;
`ResetPasswordAsync` et `RemovePasswordAsync` exigent `users.manage` et un
administrateur (actions `users.password-reset` et `users.password-delete`).
Le trousseau du compte visé est mis de côté. Détails :
[`sensitive-settings.md`](../dev/sensitive-settings.md#4-mots-de-passe--passwd-et-réinitialisation)
et [ADR 0120](../dev/adr/0120-passwd-and-administrator-password-reset.md).

`CreateAsync(new HackNewUserAccount(login, nomAffiché, autorité, motDePasse))`
et `DeleteAsync(login, removeHome)` exigent aussi `users.manage` et un
administrateur (actions `users.add` et `users.delete`, ADR 0169). La création
donne au compte son propre groupe, et `administrators` pour un administrateur ;
le dossier personnel naît à la première connexion. La suppression refuse le
compte de la session et le dernier administrateur actif, emporte les clés
d'accès du compte et, avec `removeHome`, son dossier `/home/<login>`.
`ValidateNewAccount` vérifie un compte avant l'envoi (nom de connexion, nom
affiché de 128 caractères au plus, mot de passe). `HackUserAccount.PasskeyCount`
donne le nombre de clés d'accès de chaque compte.

Clés d'accès de la session (ADR 0168), sans capacité : `ListPasskeysAsync`,
`AddPasskeyAsync(nom)` (le navigateur demande la présence de l'utilisateur ;
`OperationCanceledException` si la demande est fermée, `NotSupportedException`
sans WebAuthn) et `RemovePasskeyAsync(id)`. `HackPasskey.UnlocksKeyring` dit si
la clé ouvre le trousseau. Détails : [`passkeys.md`](../dev/passkeys.md).

## Décisions

- Voir l'ADR 0060 : propriété `SystemPower` (pas `System`),
  `HackPermissionDecision.Ask`, couche système écrite avec les droits de
  l'appelant, `SetManyAsync`, presse-papiers de l'hôte pour Diagnostic
  (ce dernier point est remplacé par l'ADR 0065 : `HackClipboard` et
  `clipboard.write`).
- `IndexedDbInspectorService` reste dans Infrastructure.Browser ;
  l'adaptateur vers le SDK vit dans l'hôte Ecosystem.
- L'ancien chemin par mot de passe de `AppPermissionAdministration` est gardé
  pour ses tests ; les apps passent par l'agent.

## Tests

`Tests/HackerOs.Platform.Core.Tests/Administration/SystemAdministrationGatewayTests.cs` :
document de l'OS lu et écrit sans capacité, couche système refusée à un
utilisateur simple puis écrite sous root en une édition, document inconnu ;
capacités exigées ; refus de l'agent sans écriture ; décision machine et
politique journalisées ; app système refusée avant toute demande ; serveur
absent ; `Grants` en pull seulement ; redémarrage refusé ; réinitialisation
dans l'ordre fermeture, effacement, redémarrage ; inspection du stockage ;
sauvegarde (capacité, hôte sans sauvegarde, refus de l'administrateur,
sélecteur avant administrateur, ordre de la restauration, échec journalisé au
démarrage suivant).
`Tests/HackerOs.Platform.Core.Tests/Sessions/LocalPasswordServiceTests.cs` :
ajout et suppression de comptes par la passerelle (`users.manage` et
administrateur), clés d'accès de la session sans capacité ni administrateur.
`Tests/HackerOs.Platform.Core.Tests/Sessions/LocalAccountServiceTests.cs` :
groupes, noms réservés ou pris, suppression avec ou sans dossier, refus pour
soi-même et pour le dernier administrateur.

## Liste des tâches

Voir [`app-sdk.md`](app-sdk.md#liste-des-tâches-du-chantier-rebuild4-sdk-system-admin).
