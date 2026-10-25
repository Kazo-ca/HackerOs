> [English version](../../../en/wasm/dev/process-environment.md)

# Environnement de processus

Chantier `rebuild/3-process-environment` de la phase 3 : ADR 0050, sections 5
et 6, précisé par l'ADR 0053.

## Objectif

Chaque processus HackerOS porte désormais, comme sous Linux :

- ses **arguments** (`argv`, nom de la commande compris) ;
- son **répertoire courant**, chemin absolu canonique ;
- son **environnement**, dictionnaire `NOM` → valeur.

Ces valeurs sont fixées à la création (copiées du parent, comme `fork`), ne
changent que pour le processus lui-même, et sont visibles dans
`/proc/<pid>/cmdline`, `environ` et `cwd`. Les apps les lisent et les
modifient avec `HackEnvironment`, calqué sur `System.Environment`.

## Architecture

| Élément | Emplacement | Rôle |
|---|---|---|
| `IProcessEnvironmentTable`, `ProcessEnvironmentSnapshot`, `ProcessEnvironmentRules` | `Shared/HackerOs.Simulation.Abstractions/Processes/ProcessEnvironmentContracts.cs` | Table par PID : lecture (instantané copié), changement du répertoire et des variables d'un PID, règles de nom (non vide, sans `=` ni NUL) et de valeur (sans NUL). |
| `ProcessStartRequest.Arguments`, `WorkingDirectory`, `Environment` | `ProcessManagerContracts.cs` | Valeurs demandées à la création. |
| `InMemoryProcessEnvironmentTable` | `Platform/HackerOs.Platform.Core/Processes/` | Implémentation. `Create` (copie du parent puis valeurs de la requête) et `Remove` sont internes : seul le gestionnaire de processus les appelle. |
| `InMemoryProcessManager.Environments` | idem | Remplit l'entrée dans `Start`, la vide à toute fin (`Complete`, `Fault`, `Kill`, descendants tués, délai dépassé). |
| `LoginEnvironment`, `LoginEnvironmentProvider` | `Processes/LoginEnvironment.cs` | Règles de l'environnement de connexion et de `/etc/hostname` ; lecture une fois par session (ADR 0053). |
| `AppLaunchRequest.Environment` | `Lifecycle/AppLaunchContracts.cs` | Variables ajoutées par l'appelant. |
| `AppLifecycleOrchestrator` | `Lifecycle/` | Calcule `argv` (`Terminal.Name` du manifeste, sinon l'identifiant d'app, puis les arguments) et l'état de départ (ci-dessous). |
| `IAppEnvironmentGateway` | `Shared/HackerOs.Simulation.Abstractions/Gateways/AppEnvironmentGatewayContracts.cs` | Vue d'une app sur **son** processus. Aucune capacité requise. |
| `AppEnvironmentGateway` | `Platform/HackerOs.Platform.Core/Execution/` | Passerelle liée au PID de l'app ; `DetachedAppEnvironmentGateway` quand il n'y a pas de table (tests). |
| `IAppExecutionContext.Environment` | `Shared/HackerOs.AppSdk/IAppExecutionContext.cs` | Membre par défaut détaché (dossier personnel, `HOME`), remplacé par la fabrique de confiance. |
| `HackEnvironment` | `Shared/HackerOs.AppSdk/HackEnvironment.cs` | Façade statique du SDK. |
| `HackExecutionContext.WorkingDirectory` | `Shared/HackerOs.AppSdk/HackExecutionContext.cs` | Lit le répertoire du processus, sauf si `Enter(context, dir)` en a fixé un et qu'aucun `SetCurrentDirectoryAsync` ne l'a remplacé depuis. |
| `ProcFileSystemProvider` | `FileSystem/Virtual/` | `cmdline`, `environ`, `cwd` lus dans la table à chaque lecture. |
| `FileSystemSeeder` | `FileSystem/` | Crée `/etc/hostname` (`hackeros`) et `/etc/environment` (commentaire) s'ils sont absents, `0644`, root. Ne les réécrit jamais. |

### État de départ d'un processus

1. **Sans parent** (bureau, service, commande d'un `ExecuteCommandIntent` sans
   contexte appelant) : environnement de connexion,
   répertoire `WorkingDirectory` de la requête ou dossier personnel, `PWD` égal
   à ce répertoire.
2. **Avec parent** : copie du répertoire et de l'environnement du parent au
   moment de la création ; un `WorkingDirectory` explicite remplace le
   répertoire et met `PWD` à jour.
3. Dans les deux cas, `AppLaunchRequest.Environment` s'applique en dernier.

Environnement de connexion : `HOME`, `USER`, `LOGNAME`, `SHELL=/bin/hsh`,
`PATH=/bin:/usr/bin` (informatif), `PWD`, `LANG=en_US.UTF-8`, puis la locale
du système (`/etc/locale.conf`), puis les lignes `NOM=valeur` de
`/etc/environment` (valeur éventuellement entre guillemets simples ou doubles),
puis la locale de l'utilisateur (`~/.config/locale.conf`). `HOME`, `USER` et
`LOGNAME` ne peuvent pas y être remplacés ; les fichiers `locale.conf` ne
posent que `LANG`, `LANGUAGE` et les `LC_*`. Les lignes vides et `#` sont
ignorées ; une ligne invalide ou protégée est ignorée avec un avertissement dans
`/var/log/syslog`.

`LANG`, `LC_MESSAGES` et `LC_ALL` choisissent la langue des textes du
processus (anglais ou français), `LC_TIME`/`LC_NUMERIC` le format des dates et
nombres ; `LANG=fr_FR.UTF-8 help` répond en français dans une session anglaise
(ADR 0104, [`localization.md`](../framework/localization.md)).

### `/proc`

| Fichier | Contenu |
|---|---|
| `/proc/<pid>/cmdline` | chaque argument suivi de NUL (`grep\0-i\0two words\0`) |
| `/proc/<pid>/environ` | chaque paire `NOM=valeur` suivie de NUL, mode `0400` |
| `/proc/<pid>/cwd` | lien vers le répertoire courant réel |

Sans entrée dans la table (anciens câblages, doubles de test), on retombe sur
l'identifiant d'app, un fichier vide et le dossier personnel.

## Utilisation

```csharp
string[] argv = HackEnvironment.GetCommandLineArgs();      // ["grep", "-i", "motif"]
string? editor = HackEnvironment.GetEnvironmentVariable("EDITOR");
HackEnvironment.SetEnvironmentVariable("EDITOR", "nano");  // null supprime ; "" est une valeur
await HackEnvironment.SetCurrentDirectoryAsync("Documents"); // relatif, absolu ou ~
string full = HackPath.GetFullPath("notes.txt");           // résolu dans le nouveau répertoire
int pid = HackEnvironment.ProcessId;
string user = HackEnvironment.UserName;                     // nom de connexion
string host = HackEnvironment.MachineName;                  // /etc/hostname
```

Toutes les opérations visent le processus courant. Une commande qui fait
`export` ou `cd` ne change pas le shell qui l'a lancée : c'est le rôle de
l'accès contrôlé au shell (ADR 0051, `IShellSession`, voir
[`shell-commands.md`](shell-commands.md)).

Depuis l'ADR 0064, le shell distingue aussi les **variables non exportées**
(`NOM=valeur` sans `export`) : elles vivent dans `ShellSession`, pas dans
l'environnement du processus `hsh`, et ne sont donc ni dans
`/proc/<pid>/environ` ni héritées par les commandes. `export NOM` les y
place ; `NOM=valeur commande` ne passe `NOM` qu'à cette commande
([`shell-interpreter.md`](shell-interpreter.md#variables)).

`TerminalExecutionContext.WorkingDirectory` et `Environment` restent, pour la
compatibilité : ils donnent l'état du processus au démarrage de la commande
(tout l'environnement, plus seulement `HOME`).

L'invite du terminal affiche le nom de la machine (`alice@hackeros:~$`) lu par
`IAppExecutionContext.Environment.MachineName`.

## Décisions clés

- **Table à part** plutôt que des champs dans `ProcessRecord`, qui reste un
  instantané immuable du cycle de vie (ADR 0050).
- **Copie à la création** : ni partage ni lien vivant entre parent et enfant.
- **`SetCurrentDirectoryAsync`** au lieu d'un `set` synchrone (ADR 0053).
- **Lecture des fichiers système une fois par session**, en root (ADR 0053).
- **Valeur vide conservée** : contrairement à `System.Environment` sous
  Windows, `""` n'efface pas une variable, comme sous Linux.
- **Shell** : depuis le chantier `3-shell-interpreter`, le shell est le
  processus de l'app qui l'héberge ; chaque commande est son enfant et hérite
  de son répertoire (mis à jour par `cd`) et de son environnement. Voir
  [`shell-interpreter.md`](shell-interpreter.md).

## Tests

- `Tests/HackerOs.Platform.Core.Tests/Processes/ProcessEnvironmentTests.cs` :
  héritage, isolation, instantanés, nettoyage, validation.
- `Tests/HackerOs.Platform.Core.Tests/Processes/LoginEnvironmentTests.cs` :
  règles de `/etc/environment` et `/etc/hostname`, amorçage.
- `Tests/HackerOs.Platform.Core.Tests/FileSystem/Virtual/ProcFileSystemProviderTests.cs` :
  `cmdline`, `environ`, `cwd`.
- `Tests/HackerOs.Platform.Core.Tests/Lifecycle/AppLifecycleOrchestratorTests.cs` :
  processus racine et enfant de bout en bout, lecture une fois par session.
- `Tests/HackerOs.AppSdk.Tests/HackEnvironmentTests.cs` : façade du SDK.

## Liste des tâches

- [x] Contrats `IProcessEnvironmentTable`, `ProcessEnvironmentSnapshot`, champs de `ProcessStartRequest`.
- [x] `InMemoryProcessEnvironmentTable` et branchement dans `InMemoryProcessManager` (création, fin).
- [x] Environnement de connexion, `/etc/environment`, `/etc/hostname` et leur amorçage.
- [x] `argv`, répertoire et environnement calculés par `AppLifecycleOrchestrator`, `AppLaunchRequest.Environment`.
- [x] `IAppEnvironmentGateway`, `IAppExecutionContext.Environment`, passerelle de la fabrique.
- [x] `HackEnvironment` et `HackExecutionContext.WorkingDirectory` relié au processus.
- [x] `/proc/<pid>/cmdline`, `environ`, `cwd`.
- [x] Invite du terminal depuis `/etc/hostname`.
- [x] Câblage de l'hôte Ecosystem.
- [x] Tests et documentation (ADR 0053).
