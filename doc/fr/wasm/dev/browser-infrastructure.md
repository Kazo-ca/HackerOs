> [English version](../../../en/wasm/dev/browser-infrastructure.md)

# Infrastructure navigateur : `HackerOs.Infrastructure.Browser`

## Objet

`Infrastructure/HackerOs.Infrastructure.Browser` est la seule couche qui
touche le navigateur : persistance IndexedDB (ADR 0015), récupération après
une panne de stockage (ADR 0018), presse-papiers de l'hôte, chiffrement par
WebCrypto, isolation cross-origin, Web Workers. Le reste de l'OS n'appelle
jamais `IJSRuntime` directement : il passe par les contrats de
`Shared/HackerOs.Simulation.Abstractions` que cette couche implémente.

La plateforme (`Platform/HackerOs.Platform.Core`) ne dépend pas de ce projet
([`platform-core.md`](platform-core.md)). C'est `AddHackerOsEcosystem`
(`OS/HackerOs.Ecosystem.Host`) qui enregistre les implémentations
navigateur, dans la composition de l'hôte.

## Où se trouve quoi

| Dossier | Contenu |
|---|---|
| `Schema/` | `HackerOsIndexedDbSchema` (nom de la base, version, magasins et index), modèle de schéma, politique de stockage des contenus (`FileContentStoragePolicy`). |
| `FileSystem/` | Fournisseur du système de fichiers (`IndexedDbFileSystemProvider`), lecteur et écrivain, planificateur de mutations, amorçage, maintenance, contenu en blocs. |
| `Sessions/`, `Policy/`, `Catalog/`, `Settings/`, `ServerConnection/`, `Sync/`, `WebData/` | Un dépôt IndexedDB par domaine (utilisateurs, groupes, grants, catalogue des apps, réglages hérités, connexion serveur, curseurs et états de synchro, bases des sites ASP.Hack). |
| `Diagnostics/` | Journal de diagnostic et d'audit persistés, inspecteur du stockage. |
| `Backup/` | Sauvegarde et restauration du stockage (`IndexedDbBackupService`). |
| `Recovery/` | `BrowserStorageRecoveryClassifier` : traduit une erreur du navigateur en état de récupération (ADR 0018, `StorageRecoveryState`). |
| `Storage/` | `BrowserStorageManager` : estimation de quota, demande de persistance, seuils d'espace faible. |
| `Interop/` | `IndexedDbInteropAdapter` (appels JavaScript), `WebCryptoPasswordHasher`, canal du presse-papiers de l'hôte. |
| `Isolation/` | `BrowserCrossOriginIsolation` : état COOP/COEP (ADR 0131, 0146). |
| `Workers/` | `BrowserWebWorkerRuntime` : runtime des Web Workers côté navigateur ([`worker-processes.md`](worker-processes.md)). |
| `Globalization/` | Satellites de langue chargés par le navigateur. |
| `wwwroot/` | Scripts JavaScript servis sous `_content/HackerOs.Infrastructure.Browser/` : `indexedDb.js`, `storageManager.js`, `webSiteDatabases.js`, `cryptoHasher.js`, `hostClipboard.js`, `crossOriginIsolation.js`, `sharedRing.js` (anneau partagé des pipes des workers), `workerHost.js` et `workerBootstrap.js`. |

## Base IndexedDB

- Nom : `hackeros`. Version courante : **4** (`HackerOsIndexedDbSchema.CurrentVersion`).
  Changer la version est le seul moyen d'ajouter, retirer ou modifier un
  magasin ou un index ; chaque montée a son étape de migration.
- Magasins principaux : `users`, `groups`, `sessions`, `settings`, `fsEntries`
  (métadonnées : fichiers, dossiers, liens), `fsLinks`, `fsContent` (blocs),
  `catalog`, `grants`, `audit`, `diagnostics`, `syncMetadata`, `serverConnection`,
  `syncCursors`, `syncRecordState`, et une base par site ASP.Hack (`hackweb.<appId>`).
- Contenu de fichier : découpé en blocs adressés par SHA-256, de 256 Kio au plus
  par défaut (`FileContentStoragePolicy.DefaultMaxChunkSizeBytes`) ; un bloc identique
  n'est stocké qu'une fois.

## Récupération

`BrowserStorageRecoveryClassifier` range une panne dans l'un des états de
`StorageRecoveryState` : stockage indisponible, quota épuisé, migration
échouée, contenu invalide. Le contexte (`StorageRecoveryContext`) distingue le
démarrage (boot) d'une opération ordinaire. Une réinitialisation destructive
passe toujours par une confirmation explicite (`AwaitDestructiveConfirmation`).
La règle : ne jamais transformer une panne opérationnelle en perte de données
silencieuse (ADR 0018). La procédure de récupération du cache PWA est dans
[`pwa-cache-recovery.md`](pwa-cache-recovery.md) ; la sauvegarde de la machine
dans [`system-backup.md`](system-backup.md).

## Points à connaître

- Les mutations de métadonnées sont des lots d'opérations IndexedDB ordonnés,
  construits par `IndexedDbFileSystemMutationPlanner` pour être atomiques. Les
  blocs de contenu sont adressés par hachage ; ceux qui ne sont plus référencés
  sont ramassés par une passe bornée, après une période de rétention
  (`OrphanRetention`), et jamais sans revalidation de leurs références.
- Le chemin d'un fichier se résout en une requête (`resolvePath`, ADR 0090,
  [`filesystem-path-lookup.md`](filesystem-path-lookup.md)).
- Les scripts `wwwroot/*.js` sont testés en contrat par
  `Tests/HackerOs.Infrastructure.Browser.Tests` (tests Node pour `sharedRing.js`)
  et `Tests/HackerOs.E2E.Tests` (`IndexedDbBrowserContractTests`, dans un vrai navigateur).
- Les anciennes notes de migration IndexedDB (versions, reprise, contrat de
  récupération) sont archivées dans [`old/`](old/) ; elles décrivent l'état
  antérieur et ne font pas foi pour le schéma actuel.

## Liens

- Décisions : [ADR 0015](adr/0015-browser-storage-and-indexeddb-adapter.md), [ADR 0018](adr/0018-indexeddb-failure-and-recovery-policy.md), [ADR 0099](adr/0099-os-backup-and-low-storage.md), [ADR 0131](adr/0131-cross-origin-isolation.md).
- Système de fichiers : [`virtual-proc-and-dev.md`](virtual-proc-and-dev.md), [`../framework/hack-file-api.md`](../framework/hack-file-api.md).
- Hôte et construction : [`building-and-testing.md`](building-and-testing.md).
