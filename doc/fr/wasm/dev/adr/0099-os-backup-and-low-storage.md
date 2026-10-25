> [English version](../../../../en/wasm/dev/adr/0099-os-backup-and-low-storage.md)

# ADR 0099 : sauvegarde et restauration de l'OS, espace faible et `ENOSPC`, cache PWA corrompu, montées de version IndexedDB testées

## Statut

Accepté le 2026-10-17 (chantier `v3/1-adr`, pour le chantier `v3/7-storage-backup`).

- **Complète :** ADR 0017 (cache PWA et hors ligne), ADR 0018 (défaillances
  d'IndexedDB et récupération), [ADR 0094](0094-deferred-decisions-status-and-maximum-file-size.md)
  (16 Mio par fichier).
- **Plan :** [`wasm-v3-completion-plan.md`](../plans/wasm-v3-completion-plan.md),
  ligne `v3/7-storage-backup` ; manques R13, R14, R15 ; décision différée n° 6.

## Contexte

- `IndexedDbBackupService` (`Infrastructure/HackerOs.Infrastructure.Browser/Backup/`)
  exporte toute la base en un JSON (`formatVersion: 1`, version du schéma,
  magasins, SHA-256) et la restaure en fusion ou en remplacement. Il est testé
  mais enregistré nulle part.
- `BrowserStorageManager.IsLowSpace` (moins de 64 Mio ou de 10 % libres, d'après
  `navigator.storage.estimate`) est calculé et jamais utilisé.
  `FileSystemErrorCode` n'a pas de code « plus de place ».
- Le service worker (`service-worker.published.js`) ne détecte pas une entrée de
  cache corrompue ; le test PWA vérifie même que les octets corrompus sont
  servis tels quels.
- Le schéma IndexedDB est en version 4 (migrations v1 à v4), sans données de
  versions antérieures pour tester la montée de version.

## Décision

### 1. Sauvegarde : une archive qui sort de l'OS

- Une sauvegarde contient **toute** la base : fichiers de tous les utilisateurs,
  `/etc`, comptes et empreintes de mots de passe. Elle est donc réservée à
  l'administrateur (élévation de l'ADR 0042), comme une sauvegarde système.
- L'archive est le JSON `formatVersion: 1` existant, compressé en gzip
  (`GZipStream`), extension **`.hosbak`**. La restauration accepte aussi le JSON
  non compressé.
- L'archive est **téléchargée sur la machine hôte**, pas écrite dans le système
  de fichiers de l'OS : une archive de l'OS dépasse vite la limite de 16 Mio par
  fichier (ADR 0094) et une copie à l'intérieur de la base sauvegardée n'aurait
  pas de sens (c'est l'équivalent d'une sauvegarde sur disque externe). Le
  plan prévoyait « archive dans `~` » ; cette limite l'empêche.
- Accès :
  - **Paramètres → Système → Sauvegarde** : « Créer une sauvegarde » (téléchargement)
    et « Restaurer… » (sélecteur de fichier de l'hôte), avec le choix fusion ou
    remplacement ;
  - **commande `hackbackup`** : `sudo hackbackup create` déclenche le
    téléchargement ; `sudo hackbackup restore [--merge|--replace]` ouvre le
    sélecteur de l'hôte (l'appui sur Entrée compte comme geste de l'utilisateur
    pour le navigateur ; si le navigateur refuse, la commande renvoie vers
    Paramètres).
- Le téléchargement et le sélecteur passent par le module JS partagé du SDK
  Blazor (`download.js` et son pendant pour l'ouverture), seule API que C# ne
  peut pas atteindre.
- **Remplacement** : l'OS ferme toutes les sessions, restaure, puis recharge la
  page (redémarrage). **Fusion** : les enregistrements de l'archive écrasent ceux
  de même clé, le reste est gardé.
- Une archive d'une autre version de schéma est refusée avec un message clair
  (comportement actuel du service). Une création ou une restauration est
  journalisée dans `/var/log/auth.log` (qui, quand, mode).

### 2. Espace faible et plus de place

- **Seuil d'alerte** : quand `IsLowSpace` devient vrai, l'OS affiche une seule
  notification par session (« Espace de stockage faible ») qui ouvre Diagnostic ;
  `df` montre la taille, l'utilisé et le libre réels du stockage du navigateur
  pour `/`.
- **Blocs réservés à root**, comme ext4 : quand le libre passe sous **5 % du
  quota** (au plus 256 Mio), les écritures d'un utilisateur non administrateur
  échouent avec `ENOSPC` ; celles du système et de l'administrateur (journaux,
  réglages de `/etc`, sauvegarde) continuent jusqu'à la limite réelle.
- **Plus de place** : `FileSystemErrorCode.NoSpace` (« No space left on
  device », `ENOSPC`) est ajouté, avec `FileTooLarge` (`EFBIG`, ADR 0094). Une
  `QuotaExceededError` d'IndexedDB devient `NoSpace` ; l'écriture est annulée en
  entier (transaction), jamais à moitié écrite. `HackFile`/`HackStream` lèvent
  `HackIOException` avec ce code, et le shell affiche le message Linux.
- Quand `LogFileWriter` ne peut plus écrire, il abandonne les lignes et en garde
  le compte, écrit au premier retour de la place (« N lignes perdues »).

### 3. Cache PWA corrompu

- Blazor vérifie l'intégrité de ses ressources de démarrage. Si le démarrage
  échoue sur une erreur d'intégrité ou de chargement, la page demande au service
  worker de **vider son cache et de recharger**, une seule fois (garde dans
  `sessionStorage`) ; en ligne, les ressources sont reprises du serveur.
- Hors ligne, l'écran d'erreur de démarrage explique que le cache est abîmé et
  qu'il faut se reconnecter une fois.
- Le test PWA qui vérifiait la diffusion des octets corrompus est remplacé par un
  test de récupération. Un test de quota limite le stockage de l'origine
  (`Storage.overrideQuotaForOrigin` de Chromium) et vérifie la notification,
  `ENOSPC` et le maintien des écritures système.

### 4. Montées de version IndexedDB testées

- `Tests/fixtures/indexeddb/` reçoit un vidage de base aux versions 1, 2 et 3,
  produit en démarrant l'OS d'un commit de chaque version (la procédure est
  écrite dans `building-and-testing.md`).
- Un test E2E crée la base à l'ancienne version avec ces données, démarre l'OS
  courant et vérifie que la montée en version 4 garde fichiers, comptes et
  réglages.
- Règle : tout chantier qui augmente `HackerOsIndexedDbSchema.CurrentVersion`
  ajoute le vidage de la version précédente.

## Conséquences

- `v3/7` enregistre `IndexedDbBackupService` dans l'hôte Ecosystem (et l'hôte de
  test), ajoute la page de Paramètres, `hackbackup`, les codes `NoSpace` et
  `FileTooLarge`, la réserve root, la récupération du cache et les tests.
- Tant que l'hôte Server rend l'interface côté serveur (ADR 0027), il n'a pas
  IndexedDB et la page de sauvegarde y est masquée. Quand il enverra le même OS
  en WebAssembly que l'Ecosystem (choix de Benoit du 2026-10-17, chantier
  `exec/8`), la sauvegarde y marchera comme dans l'Ecosystem, sans rien de
  propre au serveur.
- `sdk-reference.md` (codes d'erreur), `linux-commands.md`, `system-administration.md`
  et `building-and-testing.md` sont mis à jour.
