# ADR 0045 : Journaux en fichiers texte dans `/var/log`, avec rotation

## Statut

Accepté le 2026-10-10.

- **Phase du plan :** phase 1 — Fichiers ([`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), section 5.3, décision D19).
- **Remplace :** aucun ADR. Retire les magasins IndexedDB de diagnostics et
  d'audit comme destination des journaux.
- **Valeurs par défaut retenues** (plan, section 11) : rotation à **1 Mio** par
  fichier, **5 archives**.

## Contexte

`/var/log` est créé par `FileSystemSeeder` mais reste vide. Les entrées passent
par `HackerOsDiagnosticLoggerProvider` (pour les `ILogger<T>` de la plateforme)
et par `IAppLoggingGateway` (pour les apps), puis vont dans
`EventPublishingDiagnosticSink`, `BoundedDiagnosticSink` et
`IPersistentDiagnosticRepository` (magasin IndexedDB). L'audit (`IAuditLog`,
`BoundedAuditLog`, `IndexedDbAuditRepository`) a son propre circuit. Seul Error
Log Viewer peut lire ces journaux, à travers `IAppDiagnosticsGateway`. Ni `cat`,
ni `tail`, ni `grep` n'y ont accès.

## Décision

### 1. Fichiers

| Fichier | Contenu | Propriétaire et mode |
|---|---|---|
| `/var/log/syslog` | journaux de la plateforme : toutes les catégories `ILogger` (`HackerOs.*`), le démarrage, les erreurs des services de l'OS, les avertissements de paramètres de l'OS (ADR 0044) | `system:system 0644` |
| `/var/log/auth.log` | toutes les entrées `IAuditLog` : actions existantes `session.login` et `session.elevate` (`sudo`, ADR 0042), et plus tard les décisions d'approbation de permissions (ADR de phase 2) | `system:system 0600` (lisible avec `sudo` ou un ticket d'approbation) |
| `/var/log/apps/<appId>.log` | entrées de `IAppLoggingGateway.Log` pour cette app, et ses avertissements de paramètres (ADR 0044) | `system:system 0644` |

Le dossier `/var/log/apps` (`system:system 0755`) est créé par `FileSystemSeeder`.
Les fichiers sont créés à la première écriture. Les apps n'écrivent **jamais**
directement dans `/var/log` : seul l'écrivain de la plateforme (section 4) le
fait, avec un contexte système. Les lecteurs ordinaires passent par les droits
du fichier, ce qui exige `filesystem.system.read` puisque `/var` est un chemin
système.

### 2. Format de ligne

Une entrée tient sur **une** ligne UTF-8 terminée par `\n` :

```text
<horodatage> <NIVEAU> <source>[<pid>]: <message>[ {clé=valeur, ...}]
```

- `horodatage` : heure UTC de la simulation (`ISimulationClock`), ISO 8601 à la
  milliseconde, par exemple `2026-10-10T03:11:42.123Z`.
- `NIVEAU` : `DiagnosticSeverity` traduit en `TRACE`, `DEBUG`, `INFO`, `WARN`,
  `ERROR`, `CRIT`, complété à 5 caractères.
- `source` : la catégorie `ILogger` pour `syslog`, l'`appId` pour les journaux
  d'app, l'`Action` de l'`AuditEntry` pour `auth.log`. `[<pid>]` est présent
  quand un processus est connu.
- `message` : les `\`, retours à la ligne et tabulations sont échappés en `\\`,
  `\n` et `\t`, pour qu'`grep` trouve une entrée sur une seule ligne.
- Propriétés : déjà caviardées par `IDiagnosticRedactor`
  (`SensitiveKeyDiagnosticRedactor`), puis triées par clé.
- Entrée d'audit : `<horodatage> <NIVEAU> <action>: actor=<Actor> subject=<Subject> outcome=<Success|Denied|Failed> {propriétés}`,
  avec le niveau `INFO` pour `Success` et `WARN` sinon.

Exemple :

```text
2026-10-10T03:11:42.123Z WARN  org.hackeros.text-editor[42]: settings: /etc/org.hackeros.text-editor/settings.config:12: invalid value for 'tab-size' (expected int64), using default
```

Le type public `LogLineFormat` (`Shared/HackerOs.Simulation.Abstractions/Diagnostics/LogLineFormat.cs`)
contient `Format(...)` et `TryParse(string line, out LogLine entry)`. Error Log
Viewer et les tests l'utilisent pour relire les lignes.

### 3. Taille et rotation

- **Taille maximale : 1 Mio (1 048 576 octets) par fichier actif. Nombre
  d'archives : 5.** Ce sont les valeurs retenues par défaut. Elles sont des
  constantes de `LogRotationPolicy` (`MaxFileBytes`, `MaxArchives`) et ne sont
  pas des paramètres en phase 1.
- Avant d'ajouter un lot, si `taille actuelle + taille du lot` dépasse
  `MaxFileBytes`, on fait une rotation, à la manière de `logrotate` :
  supprimer `<f>.5`, puis renommer `<f>.4` en `<f>.5`, …, `<f>.1` en `<f>.2`, et
  `<f>` en `<f>.1`, puis créer un `<f>` vide. Chaque étape est un `MoveAsync`
  ou un `DeleteAsync` atomique du moteur.
- Les archives ne sont pas compressées. Le `.gz` est reporté.
- Un lot seul plus gros que `MaxFileBytes` est écrit tel quel dans un fichier
  neuf. On ne coupe jamais une ligne.

### 4. Écriture groupée en arrière-plan

Nouveau service `LogFileWriter` (`Platform/HackerOs.Platform.Core/Diagnostics/LogFileWriter.cs`),
enregistré en singleton :

- `Enqueue(LogFileTarget target, string line)` est synchrone et ne bloque
  jamais : la ligne va dans un `Channel<T>` borné à **10 000** lignes. Au-delà,
  les plus anciennes sont perdues et un compteur est tenu. Au vidage suivant, une
  ligne `WARN logging: <n> entries dropped` est écrite dans `syslog`.
- **Vidage** toutes les **1 s** de temps simulé (`ISimulationClock.Schedule`),
  ou dès que **100** lignes attendent, et aussi à la déconnexion et à l'arrêt.
  Il y a un seul vidage à la fois. Les lignes d'un même fichier sont regroupées
  en **un seul** ajout (lecture, concaténation, écriture avec la révision lue,
  comme `AppendAllTextAsync` de l'ADR 0043).
- **Avant que le système de fichiers soit prêt :** les lignes s'accumulent dans
  le canal. La séquence de démarrage appelle
  `LogFileWriter.AttachAsync(IFileSystemService)` une fois l'amorçage IndexedDB
  et `FileSystemSeeder` terminés. Si le système de fichiers ne devient jamais
  disponible, les entrées restent visibles dans le tampon mémoire (section 5)
  pour l'écran d'erreur de démarrage.
- **Pas de récursion :** un échec d'écriture du journal n'est jamais journalisé
  dans un fichier. Il est noté une seule fois dans le tampon mémoire, avec la
  catégorie `logging`.
- Le coût d'un ajout reste borné : le stockage par blocs dédupliqués ne réécrit
  que le dernier bloc de 256 Kio, et un fichier ne dépasse jamais 1 Mio.

### 5. Branchements

| Producteur | Avant | Après |
|---|---|---|
| `ILogger<T>` (`HackerOsDiagnosticLoggerProvider`) | sink en mémoire + `IPersistentDiagnosticRepository` | `LogFileWriter` → `syslog`, plus le tampon mémoire |
| `IAppLoggingGateway.Log` (`AppLoggingGateway`) | même sink | `LogFileWriter` → `apps/<appId>.log`, plus le tampon mémoire |
| `IAuditLog.Record` | `BoundedAuditLog` + `IndexedDbAuditRepository` | nouvelle implémentation `FileAuditLog` → `auth.log` ; `IAuditLog.Entries` reste servi par un tampon mémoire de 512 entrées |

- `BoundedDiagnosticSink` et `EventPublishingDiagnosticSink` restent comme
  **tampon mémoire** des entrées récentes (événement `DiagnosticEntryRecordedEvent`
  pour l'affichage en direct). Ce n'est plus une source de vérité.
- `IPersistentDiagnosticRepository`, `IndexedDbDiagnosticRepository`,
  `IPersistentAuditRepository` et `IndexedDbAuditRepository` ne sont plus
  branchés dans la composition. Leurs magasins IndexedDB ne sont ni supprimés ni
  migrés, et **la version de schéma IndexedDB ne change pas** (voir
  [`old/common-pitfalls.md`](../old/common-pitfalls.md)). Les anciennes entrées
  ne sont pas copiées : ce sont des diagnostics éphémères.
- `IAppDiagnosticsGateway` (`Entries`, `Clear`) est **retiré** du SDK, ainsi que
  `IAppExecutionContext.Diagnostics`. Les capacités `diagnostics.read` et
  `diagnostics.clear` ne sont plus évaluées. Elles restent dans le catalogue
  jusqu'à l'ADR de phase 2 sur les permissions.
- Les deux hôtes (`OS/HackerOs.Ecosystem` et `test/test`) passent par
  `EcosystemServiceCollectionExtensions`. Le serveur (ADR 0036) garde sa propre
  composition, qui n'est pas modifiée.

### 6. Lecteurs

- **Error Log Viewer** (`Apps/System/HackerOs.Apps.ErrorLogViewer`) lit
  `/var/log/syslog` et `/var/log/apps/*.log` (et leurs archives `.1` à `.5` à la
  demande) avec `HackFile`, analyse les lignes avec `LogLineFormat.TryParse` et
  suit les nouveautés avec `HackFileSystemWatcher`. « Effacer » devient
  « tronquer le fichier », ce qui demande le droit d'écrire dans `/var/log`
  (`sudo` ou approbation, ADR 0042).
- `cat`, `tail`, `grep` et `less` lisent ces fichiers comme n'importe quel autre.

## Conséquences

- `tail /var/log/syslog` et `grep ERROR /var/log/apps/*.log` fonctionnent dans le
  terminal.
- Un journal occupe au plus 6 Mio par fichier (l'actif plus 5 archives).
- Les journaux arrivent jusqu'à environ 1 s après l'événement. Un arrêt brutal
  de l'onglet peut perdre la dernière seconde. C'est accepté, puisque le tampon
  mémoire reste visible tant que l'onglet vit.
- `auth.log` en `0600` réserve l'audit à l'administrateur, comme sous Linux.
- Les tests d'Error Log Viewer et des diagnostics qui utilisaient
  `IAppDiagnosticsGateway` sont réécrits sur des fichiers.

## Références

- ADR 0012 : processus et horloge (`ISimulationClock`).
- ADR 0042 : `sudo` et audit `session.elevate`.
- ADR 0043 : API `Hack*`.
- ADR 0044 : avertissements de paramètres invalides.
- [`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), sections 5.3 et 11.
- `Platform/HackerOs.Platform.Core/Diagnostics/`, `Shared/HackerOs.Simulation.Abstractions/Diagnostics/DiagnosticsContracts.cs`.
