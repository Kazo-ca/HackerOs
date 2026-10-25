> [English version](../../../en/wasm/dev/shell-commands.md)

# Commandes du shell et accès contrôlé au shell

Phase 3 du plan de reconstruction, chantier `rebuild/3-shell-commands`
([ADR 0051](adr/0051-unified-commands-project.md), sections 2 à 5). Dernier
chantier de la phase 3 : il s'appuie sur l'interpréteur
([`shell-interpreter.md`](shell-interpreter.md), ADR 0050 et 0054) et sur
l'environnement de processus ([`process-environment.md`](process-environment.md)).

> Depuis l'[ADR 0064](adr/0064-posix-shell-scripts-jobs-and-app-commands.md),
> quelques **built-ins spéciaux** POSIX (`break`, `return`, `set`, `local`,
> `eval`, `source`, `wait`, `fg`, `trap`…) vivent dans l'interpréteur
> ([`shell-interpreter.md`](shell-interpreter.md#built-ins-spéciaux)) ; tout le
> reste, y compris `read`, `jobs`, `test` et `which`, reste au catalogue. Les
> commandes Linux ajoutées sont décrites dans [`linux-commands.md`](linux-commands.md).

## Objectif

Le terminal ne connaît plus **aucun** nom de commande. `cd`, `pwd`, `clear`,
`help`, `sudo`, et les nouvelles commandes `export`, `unset` et `env` sont des
commandes du catalogue comme `ls` ou `grep`. Celles qui doivent agir sur le
shell lui-même (comme les built-ins de bash) le font par une interface du SDK,
`IShellSession`, réservée aux commandes qui déclarent la capacité normale
`shell.control`. Une commande tierce peut donc fournir son propre `cd`
amélioré sans toucher au terminal.

```text
alice@hackeros:~$ cd projets && export CIBLE=10.0.0.5 && env | grep CIBLE
CIBLE=10.0.0.5
alice@hackeros:~/projets$ cd -
/home/alice
alice@hackeros:~$ sudo cat /etc/shadow | wc -l
```

## Architecture

| Élément | Emplacement | Rôle |
|---|---|---|
| `IShellSession`, `ShellCommandInfo`, `ShellJobInfo`, `ShellJobState` | `Shared/HackerOs.AppSdk/IShellSession.cs` | Accès au shell appelant : répertoire, environnement, historique, `ChangeDirectoryAsync`, `SetVariable`, `UnsetVariable`, `GetCommands`, `RunElevatedAsync` ; depuis l'ADR 0064 `GetVariable` (variables non exportées comprises), `AssignVariable` (`read`), `RunAsync` (une ligne dans un sous-shell, `watch`), `Jobs` et `KillJob` (`jobs`, `kill %n`). Membres par défaut : une implémentation existante compile toujours. |
| `TerminalExecutionContext.Shell` | `Shared/HackerOs.AppSdk/` | Le shell, ou `null`. |
| `AppCapabilities.ShellControl` | `Shared/HackerOs.App.Abstractions/` | `shell.control`, classée **normale** (ADR 0049). |
| `ShellSessionAccess` | `Platform/HackerOs.Platform.Core/Shell/` | Implémentation interne d'`IShellSession`, créée par l'interpréteur pour une étape. |
| `ShellSession.Fork()` | idem | Copie du shell pour une étape d'un tube (sous-shell). |
| `ShellInterpreter.GetInstalledCommands()` / `RunElevatedAsync` | idem | Liste des commandes installées ; exécution élevée de `sudo`. |
| `ShellExecutionOptions.CredentialPrompt` | idem | Fenêtre d'identifiants fournie par l'hôte du shell (le terminal). |
| `AppLaunchRequest.Shell`, `CommandName` | `Platform/HackerOs.Platform.Core/Lifecycle/` | Shell transmis à la commande ; nom d'appel qui devient `argv[0]`. |
| `TerminalControlSequenceDecoder` | `Shared/HackerOs.AppSdk.Terminal/` | Interprète les séquences ECMA-48 écrites par les commandes (effacement d'écran). |
| Commandes | `Apps/Commands/HackerOs.Commands/{Cd,Pwd,Clear,Help,Sudo,Export,Unset,Env,Alias}/` | Commandes du catalogue. |

### Qui reçoit le shell

L'interpréteur crée un `ShellSessionAccess` pour une étape seulement si le
shell est **interactif** (le terminal) et que le manifeste de la commande
déclare `shell.control`. L'orchestrateur le transmet ensuite à
`TerminalExecutionContext.Shell` seulement si la capacité est **accordée**.

- Commande seule dans son pipeline (y compris après `&&`) : le vrai shell.
  `cd /tmp && ls` liste `/tmp`.
- Étape d'un tube (`cd /tmp | cat`) : une copie (`ShellSession.Fork()`), avec
  son propre répertoire et son propre environnement, comme le sous-shell où
  bash exécute alors ses built-ins. La commande réussit, ses changements sont
  perdus à la fin de l'étape.
- `ExecuteCommandIntent` (shell non interactif) : `null`. `cd` y affiche
  `cd: no shell session` et rend `1`.
- Script (ADR 0064) : le shell du script, dans son processus `hsh` ; un `cd`
  ou un `read` du script n'atteint pas le terminal.

### `cd` : vérification sous l'identité de la commande

`ShellSession.ChangeDirectoryAsync` fait un vrai `chdir` du processus qui
vérifie, résolu depuis le répertoire du shell : le contexte ambiant de la
commande `cd` (ses capacités `filesystem.user-home.read` et
`filesystem.system.read`, et les droits du dossier), sinon celui de l'hôte. Le
chemin canonique obtenu est adopté par le shell ; le processus qui a vérifié
revient à son répertoire. `PWD` et `OLDPWD` sont mis à jour tout de suite, pour
que la suite d'une liste `&&` voie le changement.

### `sudo`

`sudo` n'a aucune autorité : il appelle `IShellSession.RunElevatedAsync`. Le
shell :

1. résout la commande cible (`sudo: x: command not found`, code `1`) ;
2. refuse une commande qui déclare `shell.control` (`sudo: cd: shell builtin`,
   code `1`), sans demander d'identifiants, sauf `nice` (ADR 0136) : `nice`
   reçoit l'accès au shell et le ticket root, qu'il passe à la commande qu'il
   lance ;
3. appelle `ShellExecutionOptions.CredentialPrompt` (le terminal ouvre la
   fenêtre d'identifiants d'un administrateur et obtient un ticket `SudoRoot`,
   ADR 0042) ; un refus donne `sudo: authentication failed`, code `1` ;
4. lance la cible comme **enfant du processus `sudo`**, dans le répertoire du
   shell, avec le ticket, sans accès au shell, sa sortie reliée à celle de
   `sudo`. Son entrée est celle de l'étape `sudo` (`echo x | sudo tee f`).

Un hôte sans `CredentialPrompt` fait échouer `sudo`
(`sudo: a terminal is required to read the password`). La redirection reste
ouverte par le shell non élevé : `sudo echo x > /etc/f` échoue, comme sous Linux.

### `clear` et les séquences d'effacement

`clear` écrit `ESC[H ESC[2J ESC[3J` sur sa sortie. C'est le terminal qui les
interprète : depuis l'ADR 0061, l'émulateur xterm.js reçoit les octets tels
quels ([`terminal-emulator.md`](terminal-emulator.md)), donc `ESC[2J` et
`ESC[3J` effacent l'écran et le défilement, la bannière comprise, et les autres
séquences (curseur, couleurs) s'appliquent comme sous Linux. Une séquence
coupée entre deux écritures est complétée par la suivante. `clear > f` écrit la
séquence dans `f`. Ctrl+L reste un raccourci clavier du terminal.

### Alias statiques et `argv[0]`

`argv[0]` d'une commande est désormais le **nom sous lequel elle a été
appelée** (`AppLaunchRequest.CommandName`), y compris un alias du manifeste,
comme pour un binaire multi-appel. `alias` déclare `addalias`, `rmalias` et
`unalias` ; appelé sous `rmalias` ou `unalias`, il se comporte comme
`alias -r`. L'interpréteur ne réécrit plus ces noms. L'expansion des alias de
l'utilisateur (`~/.hackeros/aliases`) reste dans le shell.

## Commandes

| Commande | Identifiant d'app | Capacités | Comportement |
|---|---|---|---|
| `cd [dir\|-]` | `org.hackeros.cmd.cd` | `shell.control`, `filesystem.user-home.read`, `filesystem.system.read` | Sans argument : `HOME`. `-` : `OLDPWD`, affiché. Erreurs `No such file or directory`, `Not a directory`, `Permission denied`, code `1`. |
| `pwd` | `org.hackeros.cmd.pwd` | — | Son propre répertoire, hérité du shell. |
| `clear` (`cls`) | `org.hackeros.commands.clear` | — | Écrit `ESC[H ESC[2J ESC[3J`. |
| `help [cmd…]` (`man`) | `org.hackeros.commands.help` | `shell.control` | Liste les commandes installées (`GetCommands`) ou l'usage et les alias d'une commande. Sans shell : code `1`. |
| `export [NOM=valeur…]` | `org.hackeros.commands.export` | `shell.control` | Modifie l'environnement du shell. Sans argument (ou `-p`) : lignes `declare -x NOM="valeur"`. `export NOM` garde la valeur ou exporte une valeur vide. Nom invalide : ``export: `x': not a valid identifier``, code `1`. |
| `unset NOM…` | `org.hackeros.commands.unset` | `shell.control` | Retire des variables du shell ; une variable absente n'est pas une erreur. |
| `env` | `org.hackeros.commands.env` | — | Son propre environnement, une ligne `NOM=valeur` par variable, triée. `env NOM=v commande` n'est pas pris en charge (code `125`). |
| `sudo cmd args…` | `org.hackeros.commands.sudo` | `shell.control` | Voir ci-dessus. |
| `exit [n]` | `org.hackeros.commands.exit` | `shell.control` | `IShellSession.Exit` : le shell sort avec `n` (modulo 256) ou `$?` ; la suite de la ligne n'est pas exécutée ; argument non numérique : code `2` ; trop d'arguments : code `1`, le shell reste ([`terminal-console.md`](terminal-console.md)). |
| `alias`, `addalias`, `rmalias`, `unalias` | `org.hackeros.commands.alias` | `filesystem.user-home.*` | `rmalias`/`unalias` = `alias -r`, choisi par `argv[0]`. |
| `read [-r] [-p p] [nom…]` | `org.hackeros.commands.read` | `shell.control` | ADR 0064 : lit une ligne de l'entrée standard et affecte les champs par `AssignVariable` ([`linux-commands.md`](linux-commands.md)). |
| `jobs [-lprs]` | `org.hackeros.commands.jobs` | `shell.control` | ADR 0064 : jobs du shell (`IShellSession.Jobs`). |
| `kill [-SIG] pid\|%job` | `org.hackeros.commands.kill` | `process.manage`, `shell.control` | ADR 0064 : `%n` passe par `IShellSession.KillJob`. ADR 0069 : sans option (ou `-15`, `-TERM`), `SIGTERM` — la cible reçoit `StopRequested` (raison `Terminated`, échéance à 5 s simulées), puis son jeton est annulé, et elle est tuée si elle ne s'est pas terminée ; `-9`/`-KILL` tue tout de suite, sans `StopRequested`. Une commande tuée sort avec `137`, une commande qui s'arrête sur son jeton avec `130`. |
| `which nom…`, `watch cmd` | `org.hackeros.commands.which`, `…watch` | `shell.control` | ADR 0064 : liste des commandes du shell ; `watch` relance la ligne par `IShellSession.RunAsync`. |

Ouvrir un fichier, un dossier ou une URI depuis le shell passe par
`xdg-open` (ADR 0096 §3, [`linux-commands.md`](linux-commands.md)) : la
commande n'a pas besoin du shell (`shell.control`), elle envoie un
`OpenFileIntent` ou un `OpenUriIntent` avec `apps.launch` et rend la main
(codes 0, 1, 3, 4 de freedesktop). Un `ExecuteCommandIntent` qui lance
`xdg-open` se comporte de même. `launch <app-id> [args]` reste le moyen
d'ouvrir une app précise. `xdg-mime default <app-id> <type>` choisit l'app
qui ouvre un type pour l'utilisateur (ADR 0121).

Un nom de variable valide suit `[A-Za-z_][A-Za-z0-9_]*` (ADR 0050, section 3) :
`IShellSession.SetVariable` et `UnsetVariable` lèvent `ArgumentException`
sinon.

## Terminal

`TerminalPanel` ne garde que l'édition de ligne, l'affichage, la complétion et
les fenêtres d'identifiants. La complétion par Tab propose les noms et alias
des commandes **installées**, comme `help` ; depuis `4-sdk-shell-host`, c'est le
shell qui la calcule (`IHackShell.CompleteAsync`, [`shell-host.md`](../framework/shell-host.md)), chemins compris depuis `4-terminal` ([`terminal-console.md`](terminal-console.md)). `TerminalBuiltins` et le crochet `IShellBuiltins` de l'interpréteur ont
disparu.

## Décisions clés

- **Pas de crochet de built-ins dans l'interpréteur.** `IShellBuiltins`
  servait seulement aux noms provisoires du terminal ; il est supprimé plutôt
  que gardé vide, pour qu'aucun hôte ne réintroduise de noms codés en dur.
- **`argv[0]` = nom d'appel.** C'est ce qui permet de déclarer
  `unalias`/`rmalias` comme simples alias du manifeste, sans réécriture dans
  le shell (précise l'ADR 0050, section 5, qui ne disait pas quel nom).
- **`sudo` hérite de l'entrée de son étape.** L'ADR 0051 ne donne pas d'entrée
  à `RunElevatedAsync` ; la passer rend `echo x | sudo tee f` possible, comme
  sous Linux, sans changer la signature.
- **Commande introuvable sous `sudo` : code `1`**, comme le vrai `sudo`, au
  lieu de `127` pour le shell.
- **`cd` vérifie par un vrai `chdir`** du processus de la commande, puis le
  défait : la vérification passe par l'API `Hack*` publique, avec les droits
  de la commande, sans nouvelle API interne.

## Tests

- `Tests/HackerOs.Platform.Core.Tests/Shell/ShellInterpreterTests.cs` : bout en
  bout avec les vraies commandes du catalogue (`cd` et `&&`, `cd -`, erreurs,
  copie dans un tube, `export`/`unset`/`env`, `help` qui suit les commandes
  installées, `clear` et sa redirection, `sudo` enfant de `sudo` avec ticket,
  refus de `sudo cd` et des identifiants, shell absent pour
  `ExecuteCommandIntent` et pour une commande sans `shell.control`, `unalias`).
- `Tests/HackerOs.AppSdk.Terminal.Tests/TerminalControlSequenceDecoderTests.cs`.
- `Tests/HackerOs.Commands.Tests/CoreCommandsTests.cs` et
  `Tests/HackerOs.Commands.Wave5.Tests/Wave5Tests.cs` : `cd`, `clear`, `help`
  avec `FakeShellSession` (`Tests/HackerOs.Tests.Support/`).
- `Tests/HackerOs.Ecosystem.Tests` : 87 apps intégrées, 70 commandes (`stress` depuis `rebuild/5-worker-processes`, `ausearch` depuis `v3/8`, `xdg-open` depuis `v3/4`, `hackbackup` depuis `v3/7`, `localectl` depuis `v3/12`, `xdg-mime` depuis `v3/4b`, `passwd` depuis `v3/8`).

## Liste des tâches

- [x] `IShellSession`, `ShellCommandInfo` et `TerminalExecutionContext.Shell` dans le SDK.
- [x] Capacité `shell.control`, classée normale.
- [x] `ShellSessionAccess`, copie du shell dans un tube, transmission seulement si la capacité est accordée.
- [x] `cd` (`HOME`, `-`, erreurs Linux) par `IShellSession.ChangeDirectoryAsync`.
- [x] `pwd` et `env` sur leur propre processus.
- [x] `clear` par séquence d'effacement, interprétée par le terminal.
- [x] `help` depuis les commandes installées.
- [x] `export` et `unset`.
- [x] `sudo` du catalogue, identifiants gardés par le shell (`RunElevatedAsync`).
- [x] Alias statiques `rmalias`/`unalias` par `argv[0]`.
- [x] Retrait de `TerminalBuiltins`, d'`IShellBuiltins` et du filtre de noms du terminal ; complétion depuis le catalogue.
- [x] Tests et documentation.
