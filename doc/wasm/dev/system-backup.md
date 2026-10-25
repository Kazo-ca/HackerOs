# Sauvegarde de la machine et espace de stockage

## Objet

Chantier `v3/7-storage-backup`, d'après l'[ADR 0099](adr/0099-os-backup-and-low-storage.md) :

- **Sauvegarde et restauration** de toute la machine (fichiers de tous les
  utilisateurs, `/etc`, comptes) dans une archive `.hosbak` téléchargée sur
  l'ordinateur hôte, depuis Paramètres ou la commande `hackbackup`.
- **Espace faible** : une notification par session, `df` avec les vrais
  chiffres du navigateur, une réserve pour root comme les blocs réservés
  d'ext4.
- **Plus de place** : codes `NoSpace` (`ENOSPC`) et `FileTooLarge` (`EFBIG`,
  16 Mio par fichier, ADR 0094), jusqu'au message Linux du shell ; les
  journaux comptent les lignes perdues.
- **Cache PWA abîmé** et **montées de version IndexedDB testées** : voir
  [Cache PWA](#cache-pwa-abîmé-adr-0099-3) et
  [Montées de version](#montées-de-version-indexeddb-adr-0099-4).

```text
alice@hackeros:~$ sudo hackbackup create
hackbackup: exporting the whole machine...
hackbackup: hackeros-20261017-071200Z.hosbak (1.2M, 3481 records) downloaded to the host computer
alice@hackeros:~$ mkdir projets   # le libre est passé sous la réserve de root
mkdir: cannot create directory 'projets': No space left on device
```

## Architecture

| Couche | Fichiers |
|---|---|
| Contrats de simulation | `Shared/HackerOs.Simulation.Abstractions/FileSystem/FileSystemResults.cs` (`NoSpace = 1800`, `FileTooLarge = 1801`), `Metrics/StorageSpaceContracts.cs` (`StorageCapacity`, `IStorageCapacityProbe`, `IStorageSpaceGuard`, `StorageSpacePolicy`), `Notifications/NotificationContracts.cs` (`NotificationAction.LaunchApp`) |
| SDK | `Shared/HackerOs.AppSdk/Administration/HackSystemBackup.cs`, `SystemBackupContracts.cs` (`IAppSystemBackupGateway`, `HackBackupRestoreMode`, `HackBackupInfo`, `HackBackupInvalidException`) ; `IO/HackFileSystemErrorTranslator.cs` et `ShellErrorText.cs` pour les deux codes |
| SDK Blazor | `Interop/BrowserFilePicker.cs` et `wwwroot/pickFile.js` (pendant de `download.js` pour ouvrir un fichier de l'hôte) |
| Platform Core | `Backup/SystemBackupService.cs` (`ISystemBackupDevice`, séquence), `Administration/AppSystemBackupGateway.cs`, `Storage/StorageSpaceMonitor.cs`, `FileSystem/FileSystemService.cs` (réserve), `Diagnostics/LogFileWriter.cs` (lignes perdues), `Power/SystemPowerService.cs` (`RestartAfterAsync`) |
| Infrastructure.Browser | `Backup/IndexedDbBackupService.cs` (gzip, `ReadArchive`, `Inspect`, fusion par `put`), `Storage/BrowserStorageManager.cs` (`IStorageCapacityProbe`), `FileSystem/IndexedDbFileSystemProvider.cs` (`QuotaExceededError` → `NoSpace`), `IndexedDbFileContentRepository.cs` (16 Mio → `FileTooLarge`), `IndexedDbStorageUsageProvider.cs` (`df`) |
| Hôte Ecosystem | `Administration/BrowserSystemBackupDevice.cs`, `EcosystemServiceCollectionExtensions.cs` (enregistrements), `EcosystemBootCoordinator.cs` (surveillance de l'espace, entrée d'audit d'une restauration) |
| Apps | `Apps/System/HackerOs.Apps.Settings/BackupPanel.razor` (onglet *Backup*), `Apps/Commands/HackerOs.Commands/Hackbackup/` ; `Platform/HackerOs.Platform.Blazor/Shell/ClockPanel.razor` (boutons d'action des notifications) |

Les trois hôtes appellent `AddHackerOsEcosystem`. La sauvegarde n'est branchée
que dans un navigateur (`OperatingSystem.IsBrowser()`) : l'hôte Server en rendu
serveur n'a pas IndexedDB, `HackSystemBackup.IsSupported` y vaut `false` et
l'onglet est masqué (ADR 0099, conséquences).

## Sauvegarde et restauration (ADR 0099 §1)

### Archive `.hosbak`

Le JSON `formatVersion: 1` de `IndexedDbBackupService` (version du schéma,
tous les magasins, SHA-256), compressé en gzip. `ReadArchive` reconnaît le gzip
à ses deux premiers octets (`1F 8B`) et accepte aussi le JSON brut. Une archive
abîmée, vide, falsifiée (empreinte fausse) ou d'une autre version de schéma est
refusée **avant** toute écriture, avec un message qui nomme les deux versions :

```text
This backup uses database schema version 3; this HackerOS uses version 4. It cannot be restored here.
```

Nom : `hackeros-AAAAMMJJ-HHMMSSZ.hosbak` (heure UTC de l'export).

### API

```csharp
// Capacité system.backup (dangereuse) et un administrateur à chaque appel.
HackBackupInfo backup = await HackSystemBackup.CreateAsync(AppContext);
HackBackupInfo? restored = await HackSystemBackup.RestoreAsync(AppContext, HackBackupRestoreMode.Replace);
// restored == null : l'utilisateur a fermé le sélecteur ; sinon la machine redémarre.
```

| Membre | Effet |
|---|---|
| `IsSupported`, `IsSupportedFor(context)` | `false` sur un hôte sans IndexedDB. |
| `CreateAsync` | Demande un administrateur, exporte, télécharge l'archive sur l'hôte, journalise. |
| `RestoreAsync(mode)` | Ouvre le sélecteur de l'hôte, valide l'archive, **puis** demande un administrateur, ferme la session, restaure, redémarre. |
| `HackBackupInvalidException` | Archive refusée (sous-classe de `HackIOException`). |

L'ordre de `RestoreAsync` est voulu : le navigateur n'ouvre son sélecteur que
juste après un geste de l'utilisateur (clic, ou Entrée dans le terminal), et
un mauvais fichier ne doit coûter à personne un mot de passe.

### Séquence de `SystemBackupService`

**Création** : export, téléchargement, entrée `system.backup.create` dans
`/var/log/auth.log` (`admin`, `requestedBy`, `file`, `bytes`, `records`).

**Restauration**, comme un outil de sauvegarde système :

1. sélecteur de l'hôte et validation complète (rien n'est arrêté) ;
2. demande d'administrateur (`system.backup.restore`) ;
3. `SystemPowerService.RestartAfterAsync` : vidage des journaux, fermeture
   de la session (chaque processus est annulé) ;
4. l'entrée de restauration est gardée **hors de la base** (`localStorage`,
   clé `hackeros.backup.restore-record`), état `started` ;
5. restauration en une transaction IndexedDB : **fusion** = `put` de chaque
   enregistrement de l'archive (même clé : écrasé ; le reste est gardé),
   **remplacement** = `clear` de chaque magasin puis `put` ;
6. l'entrée passe à `success` ou `failure:<raison>` ;
7. redémarrage de l'hôte (`location.reload`), même après un échec ;
8. au démarrage suivant, `EcosystemBootCoordinator` écrit l'entrée gardée
   dans `/var/log/auth.log` (`system.backup.restore`, `mode`, `outcome`) puis
   l'efface.

### Paramètres et `hackbackup`

- **Paramètres → Backup** (onglet visible seulement si l'hôte sait
  sauvegarder) : « Create backup », choix *Merge* ou *Replace* en cartes,
  « Restore… », message d'erreur de l'archive refusée.
- **`hackbackup create | restore [--merge|--replace]`** : doit tourner en
  root (`sudo hackbackup …`), sinon `must be run as root` et code 1, sans
  ouvrir la demande graphique ; mode par défaut `--merge` ; sélecteur fermé :
  `no file chosen`, code 1 ; navigateur qui refuse le sélecteur : renvoi vers
  Paramètres ; usage invalide : code 2. La commande tourne sur le fil
  principal (`Execution = MainThread`) pour garder le geste de l'utilisateur.

## Espace de stockage (ADR 0099 §2)

### Mesure et seuils

`BrowserStorageManager` lit `navigator.storage.estimate()` et rend
`StorageCapacity(UsageBytes, QuotaBytes)` (`null` si le navigateur ne donne
pas de quota). `StorageSpacePolicy` fixe les seuils :

| Seuil | Valeur |
|---|---|
| Espace faible (`IsLow`) | moins de 64 Mio **ou** de 10 % du quota libres |
| Réserve de root (`ReserveBytes`) | 5 % du quota, au plus 256 Mio |

`StorageSpaceMonitor` (service de portée, démarré au boot) mesure toutes les
30 s, et peu après une écriture persistante qui le demande
(`IStorageSpaceGuard.RequestRefresh`, au plus une mesure toutes les 5 s). Une
écriture ne l'attend jamais : `FileSystemService` lit seulement l'état en
cache `IsRootReserveReached`.

### Notification

Quand l'espace devient faible, l'utilisateur de la session reçoit **une**
notification par session (une nouvelle connexion avertit de nouveau) :
« Low storage space », gravité avertissement, action « Open Diagnostic ».
Les actions de notification de forme `launch:<appId>`
(`NotificationAction.LaunchApp`) sont des boutons du panneau de l'horloge qui
lancent l'app puis ferment la notification.

### Réserve de root

Dans la réserve, `FileSystemService` refuse `Create`, `Write` et `Copy` d'un
appelant dont l'autorité effective est sous Administrateur, avec `NoSpace`.
Le système (journaux, démarrage), root (`sudo`) et l'administrateur écrivent
jusqu'à la vraie limite du navigateur. Les montages virtuels (`/proc`, `/dev`)
ne sont jamais concernés.

### `ENOSPC` et `EFBIG`

| Origine | Code | Message du shell |
|---|---|---|
| `QuotaExceededError` d'IndexedDB (transaction annulée en entier) | `NoSpace` | `No space left on device` |
| Réserve de root | `NoSpace` | `No space left on device` |
| Contenu de plus de 16 Mio | `FileTooLarge` | `File too large` |

`HackFile`/`HackFileStream` lèvent `HackIOException` avec `ErrorCode` ;
`ShellErrorText.Describe` et les commandes (`chmod`, `mkdir`…) affichent le
message Linux.

### `df`

`/` montre maintenant l'utilisé et le quota **réels** du navigateur
(`IndexedDbStorageUsageProvider`) : tout ce que l'origine stocke, pas
seulement le contenu des fichiers. Sans quota connu, `df` retombe sur les
octets du contenu des fichiers. Cela remplace la règle de l'ADR 0057 §7 pour
les chiffres de `df` ([`system-metrics.md`](system-metrics.md)).

### Journaux sans place

Quand une écriture de journal échoue avec `NoSpace`, `LogFileWriter` abandonne
le lot et compte ses lignes (`PendingNoSpaceLoss`). À la première écriture
réussie de `syslog`, il ajoute une seule ligne
`WARN logging: N lines lost (No space left on device)`
([`logging.md`](logging.md)).

## Cache PWA abîmé (ADR 0099 §3)

Au démarrage, une erreur d'intégrité ou de chargement des ressources de
Blazor fait vider le cache du service worker et recharger la page une seule
fois ; hors ligne, un écran explique qu'il faut se reconnecter une fois. Voir
[`pwa-cache-recovery.md`](pwa-cache-recovery.md).

## Montées de version IndexedDB (ADR 0099 §4)

Vidages des versions 2 et 3 dans `Tests/fixtures/indexeddb/` (la version 1 n'a jamais été committée, ADR 0110 §1), test E2E `IndexedDbUpgradeFixtureTests` et procédure : voir
[`building-and-testing.md`](building-and-testing.md#fixtures-de-montée-de-version-indexeddb).

## Décisions

- Les précisions ci-dessous sont consignées dans
  l'[ADR 0110](adr/0110-backup-and-storage-implementation-adjustments.md).
- **Une fusion redémarre aussi.** L'ADR 0099 décrit le redémarrage pour le
  remplacement ; la fusion le fait aussi, parce que l'OS en marche garde en
  mémoire des chemins, des réglages et des comptes que la restauration vient
  de changer sous lui.
- **L'entrée de restauration attend hors de la base** : un remplacement
  écrase `/var/log/auth.log` lui-même. Elle est écrite au démarrage suivant.
- **`hackbackup` exige `sudo`** au lieu d'ouvrir la demande graphique, comme
  un outil système (`apt` : « are you root? »).
- **Sélecteur avant administrateur** (voir plus haut).
- La capacité **`system.backup`** est dangereuse (ADR 0049) : seul Paramètres,
  app système, la déclare.

## Tests

- `Tests/HackerOs.Infrastructure.Browser.Tests` : `IndexedDbBackupServiceTests`
  (export puis remplacement atomique, archive falsifiée refusée sans
  écriture, fusion par `put` sans `clear`, aller-retour gzip et JSON brut,
  gzip abîmé, autre version de schéma) ; `IndexedDbFileSystemProviderTests`
  (`QuotaExceededError` → `NoSpace` sans rien écrire, plus de 16 Mio →
  `FileTooLarge` avant toute écriture).
- `Tests/HackerOs.Platform.Core.Tests` : `SystemAdministrationGatewayTests`
  (capacité, hôte sans sauvegarde, création téléchargée et journalisée, refus
  de l'administrateur sans téléchargement, sélecteur fermé ou archive
  invalide sans demande ni arrêt, ordre de la restauration, échec qui
  redémarre et journalise au démarrage suivant) ; `Storage/StorageSpaceMonitorTests`
  (une notification par utilisateur, réserve de 5 % plafonnée, quota
  inconnu) ; `FileSystemServiceTests` (réserve : utilisateur refusé, système
  accepté, nouvelle mesure demandée) ; `LogFileWriterTests` (lignes perdues
  comptées puis signalées une fois).
- `Tests/HackerOs.AppSdk.Tests/IO/HackErrorTranslationTests.cs` : deux codes
  traduits en `HackIOException` et en message Linux.
- `Tests/HackerOs.Commands.Wave5.Tests/HackbackupCommandTests.cs` : usage,
  root exigé, création, fusion par défaut, remplacement, archive refusée,
  hôte sans sauvegarde.
- `Tests/HackerOs.UI.E2E.Tests/LowStorageSpaceTests.cs` : quota de Chromium abaissé
  (`Storage.overrideQuotaForOrigin`) à 3 % de libre ; la notification arrive,
  l'action « Open Diagnostic » est là, et l'administrateur se reconnecte après
  un rechargement (le système écrit encore dans la réserve).
- `Tests/HackerOs.App.Abstractions.Tests/AppCapabilitiesTests.cs` :
  `system.backup` dangereuse.

## Liste des tâches

- [x] Codes `NoSpace` et `FileTooLarge`, traduits dans le SDK et le shell.
- [x] `QuotaExceededError` et contenu de plus de 16 Mio traduits par le fournisseur IndexedDB.
- [x] Archive `.hosbak` (gzip), validation complète, fusion par écrasement.
- [x] `StorageSpaceMonitor` : mesure, notification unique, réserve de root.
- [x] Réserve appliquée par `FileSystemService` ; `df` avec les vrais chiffres.
- [x] `LogFileWriter` : lignes perdues comptées puis signalées.
- [x] `HackSystemBackup`, passerelle, `SystemBackupService`, appareil du navigateur, `pickFile.js`.
- [x] Onglet *Backup* de Paramètres et commande `hackbackup`.
- [x] Actions de notification qui lancent une app.
- [x] Cache PWA abîmé : vidage et rechargement une fois, écran hors ligne.
- [x] Fixtures IndexedDB v2 et v3 (pas de v1, ADR 0110) et test de montée de version.
- [x] Test E2E du quota (`LowStorageSpaceTests` : notification, réserve de root).
- [x] Tests unitaires et documentation.
