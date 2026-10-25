# Synchronisation entre appareils

Chantier `v3/9-sync`, [ADR 0101](adr/0101-synchronized-deletions-and-conflicts.md)
(complète les ADR [0025](adr/0025-record-synchronization-envelope-and-conflict-model.md)
et [0030](adr/0030-filesystem-sync.md)).

## Objectif

Quand l'OS est connecté au serveur optionnel, le dossier personnel de
l'utilisateur, ses associations de fichiers et ses accords voyagent entre ses
appareils :

- les suppressions se propagent (pierres tombales) et arrivent dans la
  corbeille de l'autre appareil, donc récupérables ;
- un conflit n'est jamais tranché à la place de l'utilisateur : un dialogue lui
  demande tout de suite quelle version garder, et le fichier reste figé
  jusqu'à sa réponse ;
- la synchro se fait seule, toutes les 5 minutes, à la reconnexion et 10
  secondes après une modification ;
- sans serveur, rien ne change : l'Ecosystem ne dépend jamais du serveur.

## Architecture

| Rôle | Fichier |
| --- | --- |
| Synchro du dossier personnel (push, pull, conflits, résolution) | `Platform/HackerOs.Platform.Core/ServerConnection/IFileSystemSyncService.cs` (`FileSystemSyncService`) |
| Opérations sur les fichiers de la synchro (écrire, déplacer, corbeille) | `ServerConnection/SyncFileOperations.cs` |
| Contrats des conflits et fichier `sync-conflicts.json` | `ServerConnection/SyncConflicts.cs` |
| Synchro périodique | `ServerConnection/SyncScheduler.cs` |
| État du dialogue de conflit | `ServerConnection/SyncConflictCenter.cs` |
| Réglages `sync.config` | `ServerConnection/SyncSettingsDocuments.cs` |
| Associations de fichiers (avec pierre tombale) | `ServerConnection/IFileAssociationsSyncService.cs` |
| États des enregistrements et curseurs (IndexedDB, schéma v4) | `Infrastructure/HackerOs.Infrastructure.Browser/Sync/IndexedDbSyncRecordStateRepository.cs`, `IndexedDbSyncCursorRepository.cs` |
| Dialogue de conflit de l'OS | `Platform/HackerOs.Platform.Blazor/Sync/SyncConflictOverlay.razor` (dans `DesktopShell` et `MobileShell`) |
| Onglet Synchronisation | `Apps/System/HackerOs.Apps.Settings/SettingsWindow.razor` |
| SDK | `Shared/HackerOs.AppSdk/Administration/HackServerConnection.cs`, `ServerConnectionContracts.cs` |
| Connexion automatique au serveur qui sert l'OS, icône d'état ([`server-hosting.md`](server-hosting.md)) | `ServerConnection/ServerPresenceService.cs`, `HostInfoClient.cs`, `Platform/HackerOs.Platform.Blazor/Shell/ServerStatusIndicator.razor` |
| Purge des pierres tombales (serveur) | `Server/HackerOs.Server/Services/SyncService.cs` (`PurgeExpiredTombstonesAsync`), `SyncTombstonePurgeService.cs` |

### État suivi par enregistrement

Pour chaque entrée déjà synchronisée, `SyncRecordTrackingState` garde la
révision, le hash de la description locale, le chemin relatif, l'identifiant
de l'entrée locale quand il diffère de celui de l'enregistrement (entrée créée
par un pull), le fait que ce soit une pierre tombale et l'utilisateur. C'est ce
qui permet de reconnaître une suppression : une entrée suivie, de cet
utilisateur, absente du dossier au push, part en pierre tombale. Une entrée
jamais synchronisée n'en laisse pas. Les états écrits avant l'ADR 0101 (sans
utilisateur) sont complétés au push suivant et ne produisent jamais de pierre
tombale.

Le hash suivi d'un dossier ignore ses dates de modification (elles changent à
chaque ajout dans le dossier) : un nouveau fichier n'envoie pas tous ses
dossiers parents.

### Suppressions (ADR 0101 §1)

- Mettre à la corbeille est un renommage vers `~/.local/share/Trash` ; vider la
  corbeille produit les pierres tombales.
- Une pierre tombale **reçue** met l'entrée locale à la corbeille (jamais une
  suppression définitive), avec deux clés d'extension dans le `.trashinfo` :

  ```ini
  [Trash Info]
  Path=/home/alice/note.txt
  DeletionDate=2026-10-17T08:00:00
  X-HackerOS-Origin=deleted-on-another-device
  X-HackerOS-Device=<identifiant de l'appareil qui a supprimé>
  ```

  Ces copies sont propres à l'appareil : elles ne sont jamais renvoyées
  (pseudo-domaine `filesystem.local-trash` dans le magasin des états). Une
  entrée déjà dans la corbeille est supprimée définitivement.
- Le serveur purge chaque jour les pierres tombales de plus de 90 jours (toutes
  les lignes de l'enregistrement, si la pierre tombale est toujours sa dernière
  révision ; jamais la ligne de plus grande séquence).
- Un appareil dont le dernier pull a plus de 90 jours repart sans curseur et
  compare tout : un enregistrement suivi que le serveur ne connaît plus est
  traité comme une suppression reçue.
- Si la racine du dossier personnel ne peut pas être listée, le push s'arrête :
  un dossier illisible n'est jamais pris pour un dossier vide.

### Même chemin créé sur deux appareils

Chaque appareil crée les dossiers standard à la première connexion, chacun avec
ses identifiants. Quand deux enregistrements visent le même chemin, celui dont
l'identifiant est le plus petit le garde, la même règle sur chaque appareil :
l'entrée locale est suivie sous l'enregistrement gagnant, et le perdant part en
pierre tombale, que les appareils qui ont adopté le gagnant ignorent. Deux
fichiers **différents** créés au même chemin deviennent un conflit.

### Conflits (ADR 0101 §2)

Un conflit naît quand :

- le serveur refuse un push (`CONCURRENT_EDIT`) ;
- un pull apporte une nouvelle version d'un fichier modifié localement depuis
  la dernière synchro, ou la suppression d'un fichier modifié localement ;
- deux fichiers différents ont été créés au même chemin.

Les conflits en attente sont dans `~/.local/state/hackeros/sync-conflicts.json`
(état local, jamais synchronisé), donc gardés après un rechargement. Tant
qu'un fichier y est, il n'est ni envoyé ni remplacé ; la version de l'autre
appareil est seulement retenue pour le dialogue. Un conflit se résout seul
quand les deux côtés ont supprimé le fichier ou ont le même contenu.

| Choix | Effet |
| --- | --- |
| Garder la mienne | Envoi du contenu, puis `ClientWins` avec la description locale (fichier supprimé ici : `ServerWins` puis la pierre tombale part au push suivant). |
| Garder celle de l'autre appareil | `ServerWins`, la version locale va à la corbeille, la version du serveur est écrite. |
| Garder les deux | La version locale est copiée en `<nom>.sync-conflict-<AAAAMMJJ-HHMMSS>-<appareil>.<ext>` (8 premiers chiffres hexadécimaux de l'appareil, numéro ajouté si le nom est pris), envoyée comme un nouveau fichier ; la version du serveur prend le chemin d'origine. |

Le dialogue « Conflit de synchronisation » s'ouvre seul à un nouveau conflit
et à la connexion s'il en reste. Chaque fichier montre la date, la taille et
l'appareil des deux versions, et sur demande un aperçu texte (2 000 caractères
au plus, UTF-8 sans octet nul ; contenu distant d'au plus 1 Mio). Avec
plusieurs fichiers, une ligne « Appliquer à tous » apparaît. « Plus tard » (ou
Échap) ferme le dialogue, laisse les fichiers figés et envoie une notification
qui renvoie à **Paramètres → Synchronisation**. Les choix qui demandent la
version distante restent désactivés tant qu'elle n'est pas reçue. Le dialogue
suit la langue de la session (ADR 0104) : les libellés cités sont ceux d'une
session française (« Keep mine », « Keep both », « Later »… en anglais), et la
date et la taille (o/Kio/Mio, ou B/KiB/MiB) suivent sa culture `LC_TIME`.

### Synchro périodique (ADR 0101 §4)

`SyncScheduler` (un par session) démarre avec le shell, à la connexion, et
s'arrête à la déconnexion. Un passage fait : push et pull du dossier personnel,
pull des accords, push et pull des associations. Il est lancé :

- toutes les `intervalMinutes` minutes (5 par défaut, entre 1 minute et 1 jour) ;
- à la reconnexion au serveur ;
- 10 secondes après la dernière modification d'un dossier synchronisé
  (abonnement aux sujets `FileSystemTopics.ForDirectory`, rafraîchi après chaque
  passage) ; les écritures de la synchro elle-même sont ignorées ;
- à la main (« Sync now »), même quand la synchro est suspendue.

Hors ligne, il réessaie toutes les 30 secondes sans rien écrire de plus. Chaque
passage écrit une ligne dans `/var/log/syslog` (envoyés, reçus, conflits), plus
une ligne quand le serveur redevient joignable ou qu'un passage échoue.

```ini
# /etc/hackeros/sync.config (administrateur ; l'intervalle n'est lu que là)
intervalMinutes = 5

# ~/.config/hackeros/sync.config (utilisateur)
paused = true
```

### Domaines

| Domaine | Client | Remarque |
| --- | --- | --- |
| `filesystem` | `FileSystemSyncService` | `/home/{user}` sauf `~/.local/state`. |
| `file_associations` | `FileAssociationsSyncService` | Pierre tombale si le fichier est supprimé ; une pierre tombale reçue rétablit les associations par défaut. |
| `grants` | `GrantsSyncService` | Pull seulement ; le serveur fait autorité. |
| `app_catalog`, `settings` | aucun | Le serveur garde les domaines ; l'OS ne les utilise pas (ADR 0101 §3, ADR 0044). |

## Utilisation / API

SDK (`HackServerConnection`, capacité dangereuse `server.connection.manage`) :

| Méthode | Effet |
| --- | --- |
| `GetConflictsAsync()` | Conflits en attente (`HackSyncConflict(Domain, Path, DetectedAt)`). |
| `ShowConflictsAsync()` | Ouvre le dialogue de conflit de l'OS. |
| `GetSyncStatusAsync()` | `HackSyncStatus` : actif, suspendu, en ligne, dernier passage, envoyés, reçus, dernière erreur, intervalle. |
| `SetSyncPausedAsync(bool)` | Écrit `paused` dans la couche utilisateur ; une reprise lance un passage. |
| `SyncNowAsync()` | Un passage complet tout de suite. |
| `SyncAsync(domaine, sens)` | Inchangé : un domaine dans un sens. |

Plateforme (`IFileSystemSyncService`) : `PushAsync` et `PullAsync` rendent un
`SyncPassResult(Sent, Received, Conflicts)` ; `GetConflictsAsync`,
`GetConflictPreviewAsync`, `ResolveConflictAsync(id, SyncConflictChoice)`,
l'événement `ConflictsChanged`, `ListSynchronizedDirectoriesAsync`.

Commande : `trash list` ajoute « (deleted on another device) » aux éléments
mis à la corbeille par la synchro ; `HackTrashItem.DeletedOnAnotherDevice` le
dit aux apps.

## Décisions de mise en œuvre

- La synchro démarre depuis le dialogue de conflit du shell, le seul composant
  qui existe pour toute la session connectée.
- Les entrées créées par un pull reçoivent un nouvel identifiant local ; le lien
  avec l'enregistrement passe par `LocalEntryId`.
- Après un pull, le hash suivi est celui de la description locale du résultat,
  et un pull ignore les révisions déjà connues : un fichier reçu n'est jamais
  renvoyé.
- Les pushs sont envoyés par lots de 100 enregistrements (limite du serveur).
- L'intervalle n'est lu que dans `/etc`, comme une tâche planifiée de root
  qu'un utilisateur ne peut pas changer ; `paused` suit les couches (un
  administrateur peut le verrouiller).

## Limites connues

- Le curseur de pull et l'heure du dernier pull sont partagés par tous les
  utilisateurs du même appareil (magasin IndexedDB sans utilisateur) ; les
  états d'enregistrement, eux, portent l'utilisateur.
- Pas de client `app_catalog` (voulu, ADR 0101 §3).
- Un passage lancé pendant un autre attend la fin du premier (un seul passage
  à la fois).

## Tests

- `Tests/HackerOs.Platform.Core.Tests/ServerConnection/FileSystemSyncDeletionAndConflictTests.cs` :
  deux appareils et un serveur en mémoire aux mêmes règles que le vrai
  (suppression propagée, pierre tombale reçue mise à la corbeille, entrée
  jamais synchronisée, renommage, corbeille vidée, fichier figé et reprise
  après rechargement, aperçu, trois choix, modifié ici et supprimé là-bas,
  résolution automatique, même chemin créé des deux côtés, hors ligne plus de
  90 jours, nom de la copie de conflit).
- `FileSystemSyncServiceTests.cs`, `FileAssociationsSyncServiceTests.cs`
  (pierre tombale envoyée une fois, jamais pour un fichier jamais synchronisé,
  pierre tombale reçue qui rétablit les valeurs par défaut).
- `Tests/HackerOs.Server.Tests/SyncServiceTests.cs` : purge après 90 jours, ligne
  de plus grande séquence gardée.
- `Tests/HackerOs.AppSdk.Tests/IO/HackTrashTests.cs` : origine lue dans le
  `.trashinfo`.

## Liste des tâches

- [x] États suivis par enregistrement (chemin, entrée locale, pierre tombale, utilisateur) et heure du dernier pull.
- [x] Pierres tombales envoyées et reçues (dossier personnel, associations).
- [x] Pierre tombale reçue mise à la corbeille, marquée « supprimé sur un autre appareil ».
- [x] Purge serveur après 90 jours et synchro complète après 90 jours hors ligne.
- [x] Conflits enregistrés, fichier figé, résolution automatique quand rien n'est à choisir.
- [x] Trois choix (`ClientWins`, `ServerWins`, copie `.sync-conflict-…`).
- [x] Même chemin créé sur deux appareils.
- [x] Dialogue de conflit de l'OS, « Appliquer à tous », « Plus tard » et notification.
- [x] Synchro périodique, `sync.config`, journal `/var/log/syslog`.
- [x] Onglet Synchronisation dans Paramètres et API du SDK.
- [x] Tests client et serveur.
