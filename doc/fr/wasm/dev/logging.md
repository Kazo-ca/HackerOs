> [English version](../../../en/wasm/dev/logging.md)

# Journaux en fichiers (`/var/log`)

Les fichiers de journal qu'une app produit et les API qui les écrivent sont dans [`app-log-files.md`](../framework/app-log-files.md).

Mise en œuvre de l'[ADR 0045](adr/0045-logs-as-files.md) et de la section 5.3 du
[plan de refonte](plans/ecosystem-rebuild-plan.md).

## Rôle

Les journaux de HackerOS sont de vrais fichiers texte. `cat`, `tail`, `grep` et
Error Log Viewer les lisent avec les droits de fichier ordinaires.

| Fichier | Contenu | Mode |
|---|---|---|
| `/var/log/syslog` | `ILogger<T>` de la plateforme, démarrage (`kernel`), pannes de l'hôte (`ecosystem-host`), lignes perdues (`logging`) | `system:system 0644` |
| `/var/log/kern.log` | Journal du noyau (`KernelLog`) : OOM killer, garde de la mémoire du navigateur ; aussi copié dans `syslog` et lu par `dmesg` depuis `/dev/kmsg` (ADR 0126 §3, [`oom-killer.md`](oom-killer.md)) | `system:system 0644` |
| `/var/log/auth.log` | `IAuditLog` (`session.login`, `session.elevate`, `capability.*` de l'ADR 0049…) | `system:system 0600` |
| `/var/log/audit.log` | Audit des créations, écritures, renommages, copies, suppressions et `chmod` sous `/etc`, `/bin` et `/var` (sauf `/var/log`), au format d'`auditd` (ADR 0100 §3, [`sensitive-settings.md`](sensitive-settings.md)) ; `ausearch` le filtre | `system:system 0600` |

## Architecture

```text
ILogger<T> ──► HackerOsDiagnosticLoggerProvider ─┐
IAppLoggingGateway ──► AppLoggingGateway ────────┼─► LogFileWriter ─► IFileSystemService ─► /var/log/…
IAuditLog ──► FileAuditLog ──────────────────────┘        │ (canal borné, vidage groupé, rotation)
HostExceptionReporter ───────────────────────────┘        ▼
                                     IDiagnosticSink (tampon mémoire des entrées récentes)
```

- `LogFileTarget` : `Syslog`, `AuthLog`, `AuditLog` (ADR 0100), `ForApp(appId)`, avec chemin et mode.
- `LogFileWriter` (singleton, `Platform.Core/Diagnostics/`) :
  - `Enqueue(target, line)` et `Write(target, severity, source, message, pid)`,
    synchrones, jamais bloquants. Canal de 10 000 lignes ; au-delà, les plus
    anciennes sont perdues et une ligne `WARN logging: <n> entries dropped` est
    écrite dans `syslog` au vidage suivant.
  - Plus de place (`NoSpace`, ADR 0099 §2) : le lot refusé est abandonné et
    ses lignes comptées (`PendingNoSpaceLoss`) ; au premier vidage réussi,
    une seule ligne `WARN logging: <n> lines lost (No space left on device)`
    est écrite dans `syslog` ([`system-backup.md`](system-backup.md)).
  - Vidage toutes les 1 s de temps simulé, dès 100 lignes en attente, à la
    déconnexion (`SessionLoggedOutEvent`) et à `DisposeAsync`. Un seul vidage à
    la fois par système de fichiers attaché ; une seule écriture par fichier et
    par vidage.
  - `AttachAsync(IFileSystemService)` est appelé par `EcosystemBootCoordinator`
    juste après `EnsureRootAsync`. Avant, les lignes attendent dans le canal.
    Le rédacteur crée lui-même `/var/log` et `/var/log/apps` s'ils manquent.
  - Attacher un **autre** système de fichiers abandonne le précédent : son
    jeton est annulé et les vidages suivants ne l'attendent plus. Sur
    `HackerOs.Server`, chaque circuit monte le sien ; un rechargement peut
    couper l'ancien circuit au milieu d'un vidage (appel JS sans réponse), et
    le démarrage du nouveau circuit restait bloqué derrière, environ une fois
    sur cinq (trouvé par `build/verify-hosts.sh`). Les lignes du lot abandonné
    ne restent que dans le tampon mémoire.
  - Un échec d'écriture n'est jamais journalisé dans un fichier : il est noté une
    fois par fichier (catégorie `logging`) dans le tampon mémoire.
- `FileAuditLog` remplace `BoundedAuditLog` dans la composition ; `Entries` reste
  servi par un tampon de 512 entrées.
- `SimulationClockDriver` (`Platform.Core/Time/`) : l'adaptateur de production
  prévu par l'ADR 0012, qui n'existait pas. Il fait avancer `ManualSimulationClock`
  depuis un minuteur de la plateforme (au plus 50 tics par battement, le reste
  est abandonné quand l'onglet est ralenti). Sans lui, le vidage périodique, l'horloge
  de la barre des tâches et les délais d'arrêt des processus ne s'exécutaient
  jamais. Il est démarré par `EcosystemBootCoordinator`. `ManualSimulationClock`
  protège désormais sa file de rappels par un verrou.

## Retraits

- `IPersistentDiagnosticRepository` et `IndexedDbDiagnosticRepository` ne sont
  plus enregistrés ; l'audit IndexedDB n'était déjà pas branché. Les classes et
  leurs magasins restent, et la version du schéma IndexedDB ne change pas.
- L'hôte `OS/HackerOs.Ecosystem` ne retire plus son `ILoggerProvider` : toutes ses
  dépendances sont des singletons. Le serveur garde sa composition (le pont
  `ILogger` y reste retiré, aucun système de fichiers n'y est attaché).

## Error Log Viewer

- Manifeste : `filesystem.system.read` et `filesystem.system.write` (au lieu de
  `diagnostics.*`).
- `LogFileReader` liste `syslog` puis `apps/*.log` (et les archives `.1` à `.5`
  si la case « Archives » est cochée), lit et analyse avec `LogLineFormat.TryParse`,
  et compte les lignes étrangères. Il ne lit jamais `auth.log` (`sudo cat` pour ça).
- `audit.log` et ses archives (ADR 0100) sont listés pour un administrateur ou
  une session élevée seulement (`LogSource.RequiresRoot`) et ne sont pas lus
  par la vue « tous les journaux ». Chaque ligne `type=PATH` est analysée par
  `AuditLineFormat` : un refus devient un avertissement, une écriture réussie
  une information, avec l'app comme source.
- Suivi en direct par `AppContext.Watch` sur `/var/log` et `/var/log/apps`.
- « Truncate » vide le journal sélectionné (jamais une archive). Pour un
  utilisateur ordinaire c'est refusé, la fenêtre propose l'approbation
  administrateur (ADR 0042), puis un second clic réussit.
- **Écart temporaire avec l'ADR 0045 §6 :** le lecteur passe par
  `IAppFileSystemGateway` et non par `HackFile`/`HackFileSystemWatcher`, qui
  arrivent avec la branche `rebuild/1-file-api` (ADR 0043). Le passage à `Hack*`
  se fera une fois cette API mergée ; le comportement ne change pas.

## Tests de contrat

- `Tests/HackerOs.Platform.Core.Tests/Diagnostics/LogLineFormatTests.cs` : format,
  exemple exact de l'ADR, échappements, propriétés, audit, rejet des lignes étrangères.
- `.../LogFileWriterTests.cs` : création et modes, vidage périodique et par seuil,
  une écriture par fichier, rotation et archives, lot géant, perte de lignes,
  échecs non récursifs, déconnexion, arrêt, droits de lecture, nouvel attachement
  qui n'attend pas un vidage bloqué sur le précédent.
- `.../LogProducersTests.cs` : `ILogger`, `AppLoggingGateway`, `FileAuditLog`.
- `Tests/HackerOs.Platform.Core.Tests/Time/SimulationClockDriverTests.cs`.
- `Tests/HackerOs.Apps.ErrorLogViewer.Tests/` : manifeste et `LogFileReader` sur un
  vrai `AppFileSystemGateway`.

## Liste des tâches

- [x] `LogLineFormat` (`Format`, `FormatAudit`, `TryParse`).
- [x] `LogFileTarget`, `LogRotationPolicy` (1 Mio × 5).
- [x] `LogFileWriter` : canal borné, vidage groupé, rotation, pas de récursion.
- [x] `ILogger` → `syslog`, `IAppLoggingGateway` → `apps/<appId>.log`, `IAuditLog` → `auth.log`.
- [x] `HostExceptionReporter` sur `syslog`.
- [x] Attache au démarrage et ligne `kernel: boot` ; `/var/log/apps` créé par `FileSystemSeeder`.
- [x] `SimulationClockDriver` pour que le temps simulé avance.
- [x] Retrait de `IAppDiagnosticsGateway` et des dépôts IndexedDB de la composition.
- [x] Error Log Viewer lit les fichiers.
- [x] Tests de contrat.
- [x] Lignes perdues faute de place, comptées puis signalées (`v3/7`, ADR 0099 §2).
- [ ] Passer Error Log Viewer sur `HackFile` après le merge de l'ADR 0043.
