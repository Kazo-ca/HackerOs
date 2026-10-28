> [English version](../../../../en/wasm/dev/adr/0181-host-folder-mounted-as-a-network-drive.md)

# ADR 0181 : un dossier de l'ordinateur monté comme un lecteur réseau

## Statut

Proposé le 2026-10-28 à la demande de Benoit (« ajouter la possibilité de monter un
dossier local de l'ordinateur […] j'aimerais qu'il soit monté comme un disque réseau
dans HackOs »). Les choix marqués **(question)** ont été posés à Benoit dans le fil ;
la décision suit la recommandation tant qu'il n'a pas répondu, et l'ADR sera mise à
jour avec ses réponses.

Complète l'[ADR 0008](0008-virtual-filesystem-model.md) (modèle du système de
fichiers), l'[ADR 0046](0046-virtual-proc-and-dev-filesystems.md) (montages `/proc` et
`/dev`) et l'[ADR 0057](0057-proc-metrics-for-task-manager.md), section 7
(`HackDriveInfo`).

## Contexte

- Chrome et Edge sur ordinateur offrent la *File System Access API* :
  `window.showDirectoryPicker({ mode: 'readwrite' })` ouvre le dialogue de dossiers de
  l'ordinateur et rend un `FileSystemDirectoryHandle`. Après l'accord de l'utilisateur
  (une invite du navigateur), la page lit, crée, renomme et supprime des fichiers dans
  ce dossier. Le handle se range dans IndexedDB (il est clonable) ; après un
  rechargement, `queryPermission` répond `prompt` et `requestPermission` demande un
  nouveau geste de l'utilisateur, sauf si l'utilisateur a choisi « Autoriser à chaque
  visite » (Chrome 122 et plus).
- Firefox, Safari et Chrome Android n'ont pas `showDirectoryPicker`.
- Le système de fichiers de HackerOS route chaque chemin vers un fournisseur
  (`IFileSystemProvider`) par `FileSystemMountRouter` : `/` (IndexedDB, `hda`),
  `/proc` et `/dev`. La table est fixée au démarrage ; il n'existe ni montage ni
  démontage à chaud, ni commande `mount`.
- `FileSystemService` refuse déjà un déplacement entre deux montages
  (`CrossMountOperation`) et le SDK (`HackFile.Move`) se rabat alors sur copier puis
  supprimer. `HackDriveInfo` liste les montages qui ont une taille ; `HackDriveType`
  ne connaît que `Fixed`.
- Décisions déjà prises : les permissions de fichiers sont réelles ; les droits
  système avancés passent par un dialogue d'approbation spécial ; les apps de base
  n'utilisent que le SDK public ([ADR 0055](0055-base-apps-on-public-sdk-only.md)).

## Décision

1. **Montage dynamique.** `IDynamicFileSystemMountRouter` (nouveau, étend
   `IFileSystemMountRouter`) ajoute `Mount(FileSystemMount)`, `Unmount(VirtualPath)` et
   l'événement `MountsChanged` ; `FileSystemMountRouter` l'implémente en remplaçant sa
   table immuable sous un verrou. On ne peut pas monter sur un montage existant, à
   l'intérieur d'un montage autre que `/`, ni autour d'un montage. Les montages fixes
   (`/`, `/proc`, `/dev`) ne se démontent pas.
2. **Fournisseur `hostfs`.** `HostFolderFileSystemProvider` (Platform.Core) implémente
   `IFileSystemProvider` sur un `IHostFolder` (Simulation.Abstractions). Dans le
   navigateur, `BrowserHostFolderAccess` (Infrastructure.Browser) en donne un par
   `FileSystemDirectoryHandle`, avec `hostFolder.js`. Correspondance :
   - fichiers et dossiers seulement ; les noms que HackerOS refuse sont ignorés ;
   - lecture par `getFile()`, écriture par `createWritable()` (le navigateur écrit dans
     un fichier temporaire et le remplace à la fermeture : l'écriture d'un fichier est
     atomique) ;
   - renommer et déplacer par `FileSystemHandle.move()` quand le navigateur l'a, sinon
     copier puis supprimer ; copier un dossier se fait fichier par fichier ;
   - révision d'un fichier dérivée de sa date de modification et de sa taille (le
     dossier peut changer hors de HackerOS) ; révision d'un dossier = compteur en
     mémoire ; identifiants stables (empreinte SHA-256 du chemin) ; les révisions sont
     vérifiées comme sur le disque `/` ;
   - propriétaire = l'utilisateur qui a monté, groupe = son groupe principal, modes
     fixes `0700` (dossiers) et `0600` (fichiers) : le navigateur ne montre pas les
     vraies permissions de l'ordinateur. `chmod` répond `UnsupportedOperation` ; la
     racine du montage ne se supprime pas (`ProtectedEntry`) ;
   - pas de surveillance des changements faits hors de HackerOS : l'explorateur relit
     le dossier quand on l'ouvre.
3. **Lecteur réseau.** Le montage apparaît comme un lecteur réseau :
   `HackDriveType.Network` (nouveau, comme `System.IO.DriveType.Network`),
   `DriveFormat` = `hostfs`, `VolumeLabel` = nom du dossier de l'ordinateur ;
   `/proc/mounts` montre `host:<nom> <point> hostfs rw,nosuid,nodev 0 0` (une espace
   s'écrit `\040`, comme sous Linux). Le navigateur ne donne ni la taille ni l'espace
   libre : `TotalSize` et `AvailableFreeSpace` valent 0 et `df` affiche `-`.
   `HackDriveInfo.IsReady` est faux tant que la permission du navigateur n'est pas
   accordée.
4. **Où (question).** Un lecteur se monte sur un enfant direct de `/mnt`. Sans point
   choisi, c'est `/mnt/<nom du dossier>` (puis `-2`, `-3`… si le nom est pris), créé
   au montage (`0700`, propriétaire l'utilisateur) et supprimé au démontage. Un point
   choisi doit être absent ou un dossier vide de l'utilisateur. `/mnt` est créé au
   démarrage (`0755`, root). Hors du home, le lecteur n'entre ni dans la
   synchronisation ni dans la sauvegarde (section 9).
5. **Qui (question).** Tout utilisateur monte un dossier pour lui-même, comme un
   lecteur réseau Windows : son choix dans le dialogue du navigateur est déjà une
   approbation explicite. Le point de montage lui appartient ; les autres comptes n'y
   entrent pas (mode `0700`). Seuls le propriétaire et un administrateur démontent ou
   reconnectent un lecteur.
6. **Accès des apps (question).** Les règles de fichiers ordinaires s'appliquent,
   comme pour `/tmp` : hors du home, une app a besoin de `filesystem.system.read` ou
   `filesystem.system.write` (l'explorateur et les commandes les ont), ou passe par les
   dialogues d'ouverture et d'enregistrement. Aucune capacité nouvelle.
7. **Monter demande un geste.** Le SDK gagne `HackDriveInfo.IsHostFolderSupportedAsync`,
   `MountHostFolderAsync(string? mountPoint = null)`, `ReconnectAsync(string)` et
   `UnmountAsync(string)`, par `IAppDriveGateway` (membres par défaut, pour ne pas
   casser les implémentations existantes), jusqu'au `NetworkDriveService` de l'OS et,
   pour une app en worker, par le protocole du worker (`drv.hostsup`, `drv.mount`,
   `drv.reconnect`, `drv.umount`). Le dialogue s'ouvre sur la page principale ; l'appel
   doit suivre un clic ou une touche (activation de l'utilisateur), sinon il échoue
   avec « user activation required ». Erreurs : `NotSupportedException` (navigateur),
   `UnauthorizedAccessException` (pas propriétaire, permission refusée),
   `IOException` (point de montage invalide, rien de monté),
   `InvalidOperationException` (pas de geste, personne de connecté).
8. **Persistance.** La liste des lecteurs de l'utilisateur est un vrai fichier,
   `~/.local/state/hackeros/network-drives.json` (`0600` ; point de montage, nom, clé
   du handle) : un état local à l'appareil, jamais synchronisé, car les handles ne
   valent que dans ce navigateur. Les handles sont dans la base IndexedDB
   `hackeros-host-folders` (un handle n'est pas du JSON). À la connexion de
   l'utilisateur, chaque lecteur est remonté ; si le navigateur répond `prompt`, il est
   monté **déconnecté** (question) : il apparaît dans l'explorateur avec
   « Reconnecter », et toute opération répond `ProviderUnavailable` jusqu'au clic. Un
   lecteur dont le handle a disparu est retiré de la liste. À la déconnexion de
   l'utilisateur, ses lecteurs sont démontés (la liste et les handles restent).
9. **Hors synchronisation et sauvegarde.** Le contenu d'un lecteur n'est jamais
   synchronisé avec le serveur ni inclus dans la sauvegarde de l'OS
   ([ADR 0099](0099-os-backup-and-low-storage.md)) : il est déjà sur l'ordinateur, et
   la synchronisation ne parcourt que le home, la sauvegarde que la base IndexedDB.
10. **Suppression (question).** Supprimer sur un lecteur est définitif, après
    confirmation, sans corbeille, comme sur un lecteur réseau Windows : l'explorateur
    passe en suppression définitive dès qu'un élément choisi est sur un lecteur.
11. **Explorateur.** La barre latérale (version bureau) gagne une section « Lecteurs
    réseau » : un élément par lecteur (grisé quand il est déconnecté ; un clic
    reconnecte), un bouton d'éjection pour « Déconnecter », et « Connecter un lecteur
    réseau… ». L'explorateur mobile n'est pas modifié.
12. **Terminal.** `mount` (ou `mount -l`) liste `/proc/mounts` au format
    `source on point type type (options)` ; `mount -t hostfs [point]` ouvre le dialogue
    (Entrée compte comme geste) ; `mount -o remount <point>` reconnecte ;
    `umount <point>` démonte. Codes de sortie de util-linux : 1 pour l'usage, 32 pour
    un échec. `df` montre un lecteur réseau avec `host:<nom>` et des tailles `-`.
13. **Navigateurs sans l'API (question).** Sur Firefox, Safari et mobile,
    « Connecter un lecteur réseau » est masqué, et `mount -t hostfs` répond
    « hostfs: not supported by this browser ».

## Questions posées à Benoit

1. Où monter : `/mnt/<nom>` (recommandé), `/media/<utilisateur>/<nom>` ou
   `~/Réseau/<nom>`.
2. Qui peut monter : tout utilisateur pour lui-même (recommandé) ou administrateurs
   seulement.
3. Accès des apps : règles de fichiers ordinaires (recommandé) ou capacité dédiée
   accordée par le dialogue d'approbation spécial.
4. Suppression : définitive avec confirmation (recommandé) ou copie dans la corbeille.
5. Navigateurs sans l'API : fonction masquée (recommandé) ou import en lecture seule
   (copie, pas montage).
6. Après un rechargement : lecteur déconnecté avec « Reconnecter » (recommandé) ou
   demande à la connexion.

## Conséquences

- Seuls Chrome et Edge sur ordinateur montent un dossier.
- Les handles ne vivent que sur le fil principal de la page ; les apps en worker
  passent par le service de fichiers, comme pour le disque `/`.
- Les opérations sur un gros dossier sont plus lentes que sur IndexedDB (copie
  fichier par fichier, pas de transaction sur plusieurs fichiers : une copie de
  dossier interrompue laisse une copie partielle).
- API publique : `IDynamicFileSystemMountRouter`, `IHostFolder`, `IHostFolderAccess` et
  leurs types, `HostFolderFileSystemProvider`, `NetworkDriveService`,
  `HackDriveType.Network`, `HackDriveInfo.IsHostFolderSupportedAsync`,
  `MountHostFolderAsync`, `ReconnectAsync`, `UnmountAsync`, et les membres par défaut
  correspondants d'`IAppDriveGateway`, dans les `PublicAPI.Unshipped.txt` (pour la
  version 2.1 du SDK).
- Tests : routeur (montage, démontage, refus), fournisseur sur un faux dossier en
  mémoire (déplacement avec et sans `move()`, révisions, racine protégée, permission
  retirée), service des lecteurs (montage, liste, restauration connectée ou non,
  démontage, propriétaire, points de montage refusés, `/proc/mounts`), SDK, protocole
  du worker, commandes `mount`, `umount` et `df`. Un test navigateur Playwright qui
  monte un dossier de l'origine privée (OPFS, `navigator.storage.getDirectory()`, qui a
  la même interface sans dialogue, par `globalThis.__hackerOsPickHostFolder`) reste à
  écrire.
