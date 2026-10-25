> [English version](../../../en/wasm/framework/shell-host.md)

# Hôte de shell : `HackShell` et capacité `shell.host`

## Objet

Un émulateur de terminal (la fenêtre Terminal, le panneau terminal de
l'éditeur de code, ou un terminal tiers) héberge un shell `hsh` par le SDK
public, sans toucher à `ShellInterpreter`, `ISessionService` ni
`IElevationService` (ADR 0055). C'est la section 7 de l'ADR 0056, réalisée par
le chantier `rebuild/4-sdk-shell-host`.

Comme sous Linux, `gnome-terminal` lance `bash` puis lui transmet les lignes :

```text
Terminal (fenêtre, PID 12)
 └─ hsh (PID 13)            ← HackShell.StartAsync, un par panneau ou onglet
     ├─ ls (PID 14)          ← chaque commande, enfant du shell
     └─ grep (PID 15)
```

## Utilisation

```csharp
await using IHackShell shell = await HackShell.StartAsync(new HackShellOptions
{
    WorkingDirectory = "~/projets",       // facultatif ; absent ou illisible : répertoire de l'appelant
    CredentialPrompt = myTerminalPrompt,  // facultatif ; sans lui, sudo échoue
});

int status = await shell.ExecuteAsync("ls ~ | grep notes", stdin: null, stdout, stderr, ct);
HackShellCompletion completion = await shell.CompleteAsync("ec", cursor: 2);
string line = completion.ApplyTo("ec", out int cursor);   // "echo "
```

Un composant Razor qui reçoit le contexte de son app en paramètre utilise
`HackShell.StartAsync(AppContext, options, ct)`.

### `IHackShell`

| Membre | Rôle |
|---|---|
| `ProcessId` | PID du processus `hsh` (`$$`). |
| `WorkingDirectory` | Répertoire courant du shell (changé par `cd`). |
| `ExitStatus` | `$?` du dernier pipeline. |
| `Prompt` | Invite `utilisateur@machine:répertoire$` (`#` pour root), sans l'espace finale. |
| `HasExited` | Le processus est terminé (shell libéré, ou tué avec son hôte). |
| `History`, `AddHistory`, `NavigateHistory` | Historique du shell (exposé aussi aux commandes par `IShellSession.History`) ; ↑ et ↓ comme `readline`. |
| `GetInstalledCommands()` | Commandes installées, triées (ce que liste `help`). |
| `ExecuteAsync(ligne, stdin, stdout, stderr, ct)` | Exécute une ligne ; la sortie arrive au fil de l'eau. |
| `ExecuteAsync(ligne, HackShellExecution, ct)` | Même chose avec l'écran alternatif (`FullScreen`) pour `nano`. |
| `CompleteAsync(ligne, curseur, ct)` | Complétion par Tab (voir plus bas). |
| `NeedsMoreInput(texte)` | ADR 0064 : la commande est-elle inachevée (`if` sans `fi`, guillemet ouvert, `\|` final…) ? Le terminal affiche alors l'invite `>` ([`terminal-emulator.md`](../dev/terminal-emulator.md)). |
| `ExecuteAsync(…, HackShellExecution)` : `StandardInput` | ADR 0064 : l'entrée standard que le terminal donne à la ligne (`TerminalInputReader`). |
| `DisposeAsync()` | Fermeture du terminal : tue les commandes encore en cours, puis termine `hsh` avec `$?`. La commande `exit [n]` fait de même depuis le shell (`HasExited` devient vrai, [`terminal-console.md`](../dev/terminal-console.md)). |

Une seule ligne s'exécute à la fois ; une seconde attend la fin de la
première, comme un shell qui lit son terminal. Ctrl+C annule le jeton passé à
`ExecuteAsync` (code `130`). Après `DisposeAsync`, `ExecuteAsync` lève
`ObjectDisposedException`.

`stdin` alimente la première commande de chaque pipeline non redirigée
(`ShellExecutionOptions.StandardInput`). Une commande qui l'a lu n'est pas
relancée par l'approbation ponctuelle de l'ADR 0042 : son entrée est consommée.

### Complétion

`CompleteAsync` fait la complétion côté shell, comme `bash` (ADR 0058,
section 4) :

- premier mot d'une commande (début de ligne, après `|`, `&&` ou `;`) : noms des
  commandes installées et de leurs alias statiques, sensibles à la casse ;
- autres mots (et premier mot contenant `/`) : chemins, relatifs au
  répertoire du shell, `~` développé, dossiers finis par `/`, cachés après un
  `.` tapé, caractères spéciaux échappés par `\` ; un dossier illisible ne
  propose rien (chantier `4-terminal`, voir
  [`terminal-console.md`](../dev/terminal-console.md)).

`HackShellCompletion` donne `WordStart`, `WordLength`, `Candidates` (triés,
un dossier finit par `/`) et `CommonPrefix`. `ApplyTo` applique la règle de
Tab : un seul candidat est inséré suivi d'une espace (aucune après un `/`),
plusieurs insèrent leur préfixe commun. `TerminalPanel` affiche la liste au
second Tab sur la même ligne, avec `DisplayCandidates` (dernier segment de
chaque chemin).

### `HackCredentialPrompt`

Les questions que le shell pose à son terminal :

| Méthode | Utilisée pour | Défaut |
|---|---|---|
| `ReadCredentialAsync(HackCredentialRequest)` | `sudo`, approbation ponctuelle (ADR 0042), identifiants d'un administrateur pour les capacités dangereuses | abstraite |
| `ConfirmAsync(HackConfirmationRequest)` | Oui / Non / Annuler de l'approbation en mode texte (ADR 0049 §5) | `Cancel` |
| `WriteLineAsync(texte, HackPromptLineKind)` | bandeau d'avertissement, « Sorry, try again. » | ignoré |

Le terminal **lit** ; l'OS **vérifie**. `ShellHostPrompts` (Platform Core)
transmet les identifiants à `IElevationService` (jeton `SudoRoot` ou
`CapabilityApproval`) ou à `DangerousCapabilityApprovalRequest.ApproveAsync`,
qui écrit `/etc/hackeros/app-permissions.config` et `/var/log/auth.log`.
`HackCredential.ToString()` masque le mot de passe.

`TerminalCredentialPrompt` (`HackerOs.AppSdk.Terminal`) est l'implémentation de
`TerminalPanel` : boîte d'identifiants et boîte Oui / Non / Annuler de la
plateforme (`IDialogService`), lignes écrites dans le terminal.

## Capacité `shell.host`

| Capacité | Classement | Risque affiché |
|---|---|---|
| `shell.host` | **dangereuse** | « Exécuter des commandes en votre nom, avec les droits de ces commandes. » |

Une commande lancée par le shell s'exécute avec **ses** capacités et sans
fenêtre : une app qui n'aurait que `notifications.post` pourrait sinon lire
tout `~` par `cat`. Sans `shell.host`, `HackShell.StartAsync` lève
`HackUnauthorizedAccessException` (`RequiredCapability = "shell.host"`) et
aucun processus n'est créé.

Terminal et CodeEditor la déclarent ; les deux sont des apps système
(`IsSystemApp="true"` sur leur `ProjectReference` dans
`HackerOs.Ecosystem.csproj`, ADR 0048), donc la reçoivent sans approbation
d'administrateur.

## Architecture

| Type | Projet | Rôle |
|---|---|---|
| `HackShell` | `Shared/HackerOs.AppSdk/Shell` | Façade ; traduit le refus de capacité en `HackUnauthorizedAccessException`. |
| `IHackShell`, `HackShellOptions`, `HackShellExecution`, `HackShellCompletion` | idem | Contrats publics. |
| `HackCredentialPrompt` et ses types | idem | Questions au terminal. |
| `IAppShellHostGateway` | idem | Passerelle `IAppExecutionContext.ShellHost` (par défaut : « non prise en charge »). |
| `AppShellHostGateway` | `Platform/HackerOs.Platform.Core/Execution` | Vérifie `shell.host`, puis appelle le service. |
| `ShellHostService` | `Platform/HackerOs.Platform.Core/Shell` | Crée le processus `hsh`, son contexte et sa `ShellSession` ; implémente `IHackShell`. |
| `ShellHostPrompts` | idem | Adapte `HackCredentialPrompt` aux invites de `ShellInterpreter` et à `IDangerousCapabilityApprovalPrompt`. |
| `ShellCompletion` | idem | Complétion des noms de commande. |
| `TerminalPanel`, `TerminalCredentialPrompt` | `Shared/HackerOs.AppSdk.Terminal` | Émulateur de terminal sur le SDK seul. |

### Démarrage d'un shell

1. `AppShellHostGateway` vérifie `shell.host` par le vérificateur de capacités de
   l'app.
2. `ShellHostService` démarre un processus `org.hackeros.hsh` (`argv[0]` =
   `hsh`, nom `hsh` dans `/proc/<pid>/status`), enfant du processus de l'app.
   Le gestionnaire de processus lui copie le répertoire et l'environnement du
   parent (ADR 0050).
3. La fabrique de contextes crée le contexte du shell : manifeste
   `ShellHostService.ShellManifest` (hors catalogue), **capacités de l'app
   hôte** (une redirection s'ouvre avec les droits du terminal, comme un
   `fork` garde les droits du parent), même élévation root si l'hôte a été
   lancé par `sudo`.
4. Une `ShellSession` est créée sur ce contexte (`TERM`, `PWD`), puis le shell
   se place dans `WorkingDirectory` avec ses propres droits ; en cas d'échec
   il reste dans le répertoire hérité.

Chaque ligne s'exécute sous le contexte ambiant du shell : `sudo` et les
commandes ont le shell pour parent. Le jeton de la ligne est lié à celui du
processus `hsh` : tuer le shell (ou son terminal) arrête la ligne.

### Commandes dans des Web Workers (ADR 0070)

Le shell lui-même (`hsh`, son interpréteur, ses tubes et ses redirections)
reste sur le fil principal. Chaque commande externe dont le manifeste dit
`pool` ou `dedicated` y est lancée par `AppLifecycleOrchestrator` dans un Web
Worker, avec des passerelles distantes : ses flux standard passent par messages
et ses appels reviennent à l'OS. Toutes les étapes d'un pipeline sont placées
sur le même worker (`ExecutionPlacement.BeginGroup`). Les commandes intégrées
(`cd`, `export`, `jobs`, `kill`, `read`…) et celles qui pilotent le shell ou
l'écran (`sudo`, `nano`, `top`, `watch`…) restent sur le fil principal : `kill`
répond même quand un worker est bloqué. Une commande tuée (code 137, raison
`Killed` ou `Timeout`) fait écrire « Killed » sur la sortie d'erreur, comme
`bash`. Voir [`worker-processes.md`](../dev/worker-processes.md).

### Fin d'un shell

`DisposeAsync` tue d'abord les commandes encore en cours (raison
`DependencyStop`, comme un `SIGHUP`), puis termine `hsh` avec `$?` comme code
de sortie. `AppLifecycleOrchestrator` accepte désormais qu'une commande ait été
tuée pendant qu'elle tournait : son enregistrement de fin est conservé au lieu
de lever `KeyNotFoundException`.

## Décisions

- **Le shell est son propre processus**, enfant de l'app hôte (ADR 0056 §7).
  Avant ce chantier, le shell partageait le processus du terminal (ADR 0050
  §1) : deux onglets de l'éditeur de code partageaient leur répertoire. Chaque
  `TerminalPanel` a maintenant le sien. `ExecuteCommandIntent` garde le shell
  détaché ou celui de l'appelant de l'ADR 0050 §7 : il n'a pas de terminal.
- **Historique et invite dans `IHackShell`**, en plus des membres listés par
  l'ADR : l'historique est celui du shell (`IShellSession.History` le donne
  aux commandes), et l'invite dépend du répertoire du shell.
- **`HackCredentialPrompt` est une classe abstraite** avec deux méthodes
  facultatives : un terminal minimal n'implémente que la saisie des
  identifiants.
- **`sudo` ne fait qu'un essai** et écrit « Sorry, try again. » avant
  « sudo: authentication failed », comme avant ce chantier.
- **Complétion sensible à la casse**, comme `bash` (l'ancien `TerminalPanel`
  ignorait la casse et prenait le premier candidat).

## Tests

- `Tests/HackerOs.Platform.Core.Tests/Shell/ShellInterpreterTests.HostedShell.cs` :
  refus sans `shell.host`, processus enfant et parent des commandes, répertoires
  et `$?` séparés, `WorkingDirectory` et repli, fin du shell qui tue les
  commandes, `sudo` par `HackCredentialPrompt`, entrée standard et historique,
  complétion (positions de commande, alias, casse).
- `Tests/HackerOs.App.Abstractions.Tests/AppCapabilitiesTests.cs` : `shell.host`
  dangereuse.

## Liste des tâches du chantier `rebuild/4-sdk-shell-host`

- [x] Capacité `shell.host` (dangereuse) dans `AppCapabilities`.
- [x] `HackShell`, `IHackShell`, `HackShellOptions`, `HackShellExecution`, `HackShellCompletion`, `HackCredentialPrompt`, `IAppShellHostGateway` dans le SDK.
- [x] `ShellHostService`, `ShellHostPrompts`, `ShellCompletion`, `AppShellHostGateway` ; passerelle branchée par `AppExecutionContextFactory` ; enregistrement dans l'Ecosystem.
- [x] `ShellExecutionOptions.StandardInput`.
- [x] `TerminalPanel` sur le SDK seul, `TerminalCredentialPrompt` à la place de `TerminalDangerousCapabilityApprovalPrompt`, référence `Platform.Core` retirée, exemption retirée.
- [x] Terminal et CodeEditor déclarent `shell.host` ; CodeEditor est une app système.
- [x] Orchestrateur : une commande tuée pendant son exécution ne lève plus d'exception.
- [x] Tests, ce document, `app-sdk.md`, `capabilities.md`, `shell-interpreter.md`, `shell-commands.md`, `dangerous-capability-approval.md`, plan et index.
