# Interpréteur du shell `hsh`

Phase 3 du plan de reconstruction, chantier `rebuild/3-shell-interpreter`
([ADR 0050](adr/0050-shell-grammar-and-process-environment.md), sections 1 à 4
et 7, précisé par l'[ADR 0054](adr/0054-shell-exit-status-and-detached-execution.md)),
étendu en sous-ensemble POSIX `sh` par l'[ADR 0064](adr/0064-posix-shell-scripts-jobs-and-app-commands.md)
(scripts, jobs, built-ins spéciaux, apps lancées par leur nom).

## Objectif

Un seul shell, sans interface, testable sans navigateur, utilisé par le
terminal, par les scripts et par `ExecuteCommandIntent`. Il comprend les
listes (`;`, `&&`, `||`, `&`), les tubes, les redirections (fichiers,
descripteurs, here-documents), les commandes composées, les fonctions et toutes
les expansions POSIX, lance chaque commande comme processus enfant du shell et
affiche la sortie au fil de l'eau.

```text
alice@hackeros:~$ for f in *.log; do n=$(grep -c ERROR "$f"); [ "$n" -gt 0 ] && echo "$f: $n"; done | sort -t: -k2 -n
alice@hackeros:~$ ./deploy.sh web db || echo "échec : $?" >&2
alice@hackeros:~$ text-editor notes.txt &
[1] 214
```

## Architecture

Tout est dans `Platform/HackerOs.Platform.Core/Shell/` :

| Type | Rôle |
|---|---|
| `ShellParser` | Lexer (mots, opérateurs, commentaires, here-documents lus au retour à la ligne, `$(…)`, `` `…` ``, `$((…))`) puis descente récursive ; renvoie un `ShellCommandList` ou un `ShellSyntaxError` (code, message, position, longueur). Ne lève jamais d'exception pour une saisie ; `IsIncomplete` signale une saisie à continuer. |
| `ShellSyntax.cs` | L'arbre : `ShellCommandList` → `ShellListItem` (`&` ou non) → `ShellAndOrList` (`&&`, `||`) → `ShellPipeline` (`!`) → `ShellCommand` : `ShellSimpleCommand` (affectations, mots, redirections), `ShellGroupCommand` (`{ }`, `( )`), `ShellIfCommand`, `ShellLoopCommand`, `ShellForCommand`, `ShellCaseCommand`, `ShellFunctionDefinition`. Un `ShellWord` garde ses morceaux (`ShellLiteralPart`, `ShellVariablePart`, `ShellSpecialParameterPart`, `ShellParameterExpansionPart`, `ShellCommandSubstitutionPart`, `ShellArithmeticPart`, `ShellExitStatusPart`, `ShellTildePart`), chacun sachant s'il était entre guillemets. |
| `ShellExpander` | Expansion POSIX d'un mot : paramètres, substitutions, arithmétique, découpage sur `IFS`, chemins (`*`, `?`, `[…]`), suppression des guillemets. |
| `ShellArithmetic` | `$((…))` : entiers 64 bits, opérateurs et priorités du C, affectations, `++`/`--`, `?:`, `**`, littéraux octal, hexadécimal, `base#n`. |
| `ShellPattern` | Motifs du shell (chemins, `case`, `${x#motif}`) traduits en expressions régulières. |
| `ShellInterpreter` (fichier partagé en `.cs`, `.SimpleCommands.cs`, `.Redirections.cs`, `.Builtins.cs`, `.Scripts.cs`) | Exécute l'arbre : listes, tubes, commandes composées, redirections, recherche de la commande (built-in, fonction, chemin, catalogue, `sh`, app), scripts, jobs. Fournit aussi `GetInstalledCommands()`, `GetLaunchableApplications()` et l'exécution élevée de `sudo`. |
| `ShellSession` | État d'un shell : processus, historique, `LastExitCode`, variables non exportées, paramètres positionnels, `$0`, fonctions, options (`set -eux`), jobs, `trap … EXIT`. Le répertoire et les variables **exportées** sont **ceux du processus**, lus par `IAppEnvironmentGateway`. `Fork()` donne la copie d'un sous-shell. |
| `ShellJob`, `ShellJobTable` | Jobs en arrière-plan : commande, processus, état, statut, annulation. |
| `ShellPipe` | Tube en mémoire de 64 Ki caractères entre deux étapes. |
| `ShellCommandResolver` | Recherche d'une commande (nom, alias de manifeste ou identifiant d'app) et, depuis l'ADR 0064, d'une app fenêtrée par son nom court (`ResolveApplication`). |
| `ShellExecution.cs` | `ShellExecutionOptions`, `ShellExecutionResult`, `ShellFailure`, `ShellElevation`. |
| `ShellSessionAccess` | `IShellSession` remis aux commandes qui déclarent `shell.control` ([`shell-commands.md`](shell-commands.md)). |
| `ShellErrorText` | Texte court d'une erreur de fichier (`No such file or directory`, `Permission denied`…), dans le SDK. |

Côté terminal (`Shared/HackerOs.AppSdk.Terminal/`) :

| Type | Rôle |
|---|---|
| `TerminalPanel` | Discipline de ligne et affichage dans un terminal xterm.js ([`terminal-emulator.md`](terminal-emulator.md), ADR 0061). Démarre son shell par `HackShell.StartAsync` ; chaque ligne passe par `IHackShell.ExecuteAsync`. |
| `TerminalInputReader` | Entrée standard de la ligne en cours (ADR 0064 §6) : tty en mode canonique, Ctrl+D = fin d'entrée, entrée non lue rendue à l'invite. |
| `TerminalOutputWriter` | `TextWriter` qui pousse chaque morceau écrit vers le terminal. |

## Grammaire

```text
programme   := liste
liste       := et_ou ( ( ";" | "&" | nouvelle-ligne ) et_ou )* [ ";" | "&" ]
et_ou       := pipeline ( ( "&&" | "||" ) pipeline )*
pipeline    := [ "!" ] commande ( "|" commande )*
commande    := simple | composée redirection* | fonction
simple      := ( affectation | mot | redirection )+
composée    := "{" liste "}" | "(" liste ")"
             | "if" liste "then" liste ( "elif" liste "then" liste )* [ "else" liste ] "fi"
             | ( "while" | "until" ) liste "do" liste "done"
             | "for" NOM [ "in" mot* ] ( ";" | nouvelle-ligne ) "do" liste "done"
             | "case" mot "in" ( [ "(" ] motif ( "|" motif )* ")" [ liste ] ";;" )* "esac"
fonction    := NOM "(" ")" composée | "function" NOM [ "(" ")" ] composée
redirection := [n] ( ">" | ">>" | ">|" | "<" | ">&" | "<&" ) mot
             | ( "&>" | "&>>" ) mot | [n] ( "<<" | "<<-" ) délimiteur
```

- Mots réservés (`if`, `then`, `fi`, `do`, `done`, `case`, `esac`, `{`, `}`,
  `!`, `in`, `function`…) seulement non quotés et en position de commande :
  `echo done` affiche `done`.
- Guillemets et échappements de l'ADR 0014 : `'…'` littéral ; `"…"` garde les
  espaces, développe `$` et `` ` ``, et comprend `\"`, `\\`, `\$`, `` \` ``,
  `\n`, `\r`, `\t` ; hors guillemets, `\` rend le caractère suivant littéral ;
  `\` + retour à la ligne joint deux lignes. `""` donne un argument vide.
- `#` en début de mot commence un commentaire.
- `NOM=valeur` en tête : affectation (voir [Variables](#variables)).
- Here-documents : le corps est lu après la ligne de la commande jusqu'au
  délimiteur ; `<<-` retire les tabulations ; un délimiteur quoté
  (`<<'EOF'`) rend le corps littéral, sinon `$` et `` ` `` y sont développés.
- Refusés : `<>`, `<<<` (`shell.syntax.unsupported`). Erreurs :
  `shell.syntax.unexpected-token`, `shell.syntax.unexpected-end` (saisie
  incomplète), `shell.syntax.missing-redirect-target`,
  `shell.syntax.unterminated-quote`, `shell.syntax.unterminated-substitution`,
  `shell.syntax.unterminated-here-document`, `shell.syntax.trailing-escape`,
  `shell.syntax.bad-substitution`, `shell.syntax.bad-function-name`.

## Expansion

Juste avant de lancer chaque commande simple, dans l'ordre POSIX :

1. **Tilde** : `~` et `~/…` en début de mot (ou de valeur d'affectation)
   non quoté : `HOME`.
2. **Paramètres** : `$NOM`, `${NOM}` ; `$1`…`$9`, `${10}` ; `$#`, `$@`, `$*`,
   `$$` (PID du shell), `$!` (dernier job), `$-` (options), `$0` (nom du shell
   ou du script), `$?` ; `${NOM:-défaut}`, `${NOM:=défaut}`,
   `${NOM:+autre}`, `${NOM:?message}` (et sans `:` : seulement si non défini),
   `${#NOM}`, `${NOM#motif}`, `${NOM##motif}`, `${NOM%motif}`,
   `${NOM%%motif}`. Avec `set -u`, un paramètre non défini est une erreur.
3. **Substitution de commande** : `$(…)` et `` `…` `` tournent dans une copie
   du shell ; leur sortie, sans les retours à la ligne finaux, est insérée ;
   `$?` devient leur statut.
4. **Arithmétique** : `$((…))`, après expansion de ses `$`.
5. **Découpage en champs** des résultats **non quotés** sur `IFS`
   (espace, tabulation, retour à la ligne par défaut) ; `"$@"` donne un champ
   par paramètre.
6. **Chemins** : un champ qui contient `*`, `?` ou `[…]` non quotés est
   remplacé par les chemins correspondants, triés, lus avec l'identité du shell
   (`set -f` désactive) ; sans correspondance il reste tel quel ; un nom
   commençant par `.` n'est pris que par un motif commençant par `.`.
7. Suppression des guillemets.

**Alias** : pour une ligne tapée (pas dans un script), si le premier mot est un
mot simple non quoté qui n'est pas une fonction, le shell cherche `nom=valeur`
dans `~/.hackeros/aliases` ; la valeur doit être une commande simple.

### Variables

`NOM=valeur` seul crée une **variable de shell non exportée** (ou modifie une
variable exportée existante) ; les commandes ne l'héritent pas.
`export NOM` l'exporte. `NOM=valeur commande` ne vaut que pour la commande.
`local` (dans une fonction) sauvegarde et rétablit une variable.

## Exécution

- **Listes** : `a; b` dans l'ordre ; `a && b` si `a` vaut 0 ; `a || b` sinon ;
  `! p` inverse le code ; `set -e` arrête le shell sur un échec non testé.
- **Tubes** : toutes les étapes démarrent en même temps dans des copies du
  shell (sous-shells), reliées par des `ShellPipe`. Quand une étape se
  termine, son entrée se ferme : l'écrivain suivant reçoit
  `HackIOException("Broken pipe")`, et une commande qui la laisse passer sort
  avec `141`.
- **Flux** : entrée de la première étape = entrée standard du shell (celle du
  terminal, ADR 0064 §6) ; sortie de la dernière vers le terminal ; erreurs de
  toutes les étapes vers le terminal. Seule une étape qui écrit sur le
  terminal lui-même reçoit l'écran alternatif (`nano`, `top`).
- **Redirections** : ouvertes de gauche à droite avec l'identité de l'hôte du
  shell ; `>` vide à l'ouverture, `>>` crée ou ajoute, `<` lit, `n>&m` copie
  le descripteur tel qu'il est à ce point (`>f 2>&1` ≠ `2>&1 >f`), `&>f` =
  `>f 2>&1`, here-document = entrée ; `/dev/null`, `/dev/stdin`,
  `/dev/stdout`, `/dev/stderr`, `/dev/tty` sont les flux du shell. Échec :
  `hsh: <cible>: <raison>`, commande non lancée, code `1`. Les fichiers sont
  publiés à la fin de la commande (`HackFileStream`).
- **Recherche d'une commande** : built-in spécial, puis fonction, puis chemin
  (nom contenant `/`), puis commande du catalogue (`[` = `test`), puis `sh`,
  `bash`, `hsh`, puis app fenêtrée par son nom court, sinon `127`.
- **Codes** : `127` introuvable (ou désinstallée), `126` non exécutable, `2`
  syntaxe, `130` interruption, `1` faute ou redirection, `141` tube cassé,
  `143` job terminé par `kill`.
- **Interruption** : Ctrl+C annule le jeton : toutes les étapes reçoivent
  l'annulation, la liste s'arrête ; une app fenêtrée au premier plan est
  fermée. Les boucles cèdent la main au navigateur toutes les 64 itérations.
- **Processus** : chaque commande est lancée avec `ParentPid` = processus du
  shell et `WorkingDirectory` = répertoire du shell. `ps` montre donc
  `terminal → hsh → grep, wc`.
- **Approbation ponctuelle** (ADR 0042) : si une commande seule tapée au
  terminal échoue sur un refus de permission, `PermissionApproval` peut fournir
  un jeton ; la commande est relancée une fois avec lui.
- `sudo echo x > /etc/f` échoue : la redirection est ouverte par le shell non
  élevé, comme sous Linux.

### Built-ins spéciaux

`:`, `break [n]`, `continue [n]`, `return [n]`, `shift [n]`,
`set [-eufx] [-o nom] [-- args]` (sans argument : toutes les variables),
`local nom[=valeur]`, `eval args`, `. fichier` / `source fichier [args]`,
`wait [%n|pid]`, `fg [%n]`, `bg`, `exec commande`, `trap action EXIT`,
`command [-v|-V] nom`, `type nom`, `ulimit [-SH] [-a|-f|-m|-n|-t|-u] [limite]`
(limites du processus du shell, héritées par les commandes lancées ensuite ;
ADR 0125, [`process-limits.md`](process-limits.md)). Tout le reste vient du catalogue
([`shell-commands.md`](shell-commands.md), [`linux-commands.md`](linux-commands.md)).

### Fonctions

`nom() { …; }` : appelée comme une commande, avec ses propres paramètres
positionnels, `local` et `return` ; au plus 200 niveaux d'imbrication
(fonctions, scripts, `eval`, substitutions).

### Scripts

- `./script.sh` (tout nom avec `/`) : le fichier doit exister, ne pas être un
  dossier, être **exécutable** (bit `x` du propriétaire pour le propriétaire,
  du groupe ou des autres sinon ; root : un bit quelconque) et lisible. Sinon :

  ```text
  hsh: ./deploy.sh: Permission denied
  hsh: hint: './deploy.sh' is not executable. Make it executable with: chmod +x ./deploy.sh  (or run it with: sh ./deploy.sh)
  ```

  (code 126). Un fichier binaire donne `cannot execute binary file`.
- `#!` : `sh`, `bash`, `hsh`, `dash`, `ash`, `zsh`, `ksh` (directement ou
  par `/usr/bin/env`) → `hsh`, avec les options de la ligne (`#!/bin/sh -e`) ;
  une autre commande installée reçoit `[arg] script args…` ; sinon
  `bad interpreter`. Sans `#!`, `hsh` exécute le fichier.
- Le script tourne dans un **nouveau processus `hsh`**, enfant du shell
  (`AppLifecycleOrchestrator.StartShellProcess`), qui hérite des capacités, de
  l'élévation, du répertoire et des variables exportées ; `$0` est le chemin
  tapé, `$1`… les arguments. Ses `cd`, variables, fonctions et `exit` ne
  touchent pas le shell appelant ; ses commandes `shell.control` (`cd`,
  `read`) agissent sur le shell du script. Le processus finit avec le statut
  du script, après `trap … EXIT`.
- Une erreur de syntaxe est signalée avant de lancer quoi que ce soit :
  `deploy.sh: line 4: syntax error …` (code 2).
- `sh script [args]` (lecture suffit), `sh -c 'cmd' [nom args…]`,
  `sh -e/-x/-u` ; sans script, `sh` lit ses commandes sur l'entrée standard
  (`echo 'ls' | sh`, ou tapées jusqu'à Ctrl+D).

### Jobs

`commande &` tourne dans une copie du shell, entrée vide, avec sa propre
annulation : Ctrl+C à l'invite ne l'atteint pas, la fermeture du terminal si
(SIGHUP). En interactif, le shell affiche `[1] 214`, puis avant l'invite
suivante `[1]+  Done                    sleep 5`. `$!` est le PID du dernier
processus lancé par le dernier job. `jobs`, `kill %1`, `wait`, `fg`.

### Apps lancées par leur nom

Quand aucune commande n'a le nom tapé, le shell cherche une app **fenêtrée**
dont le dernier segment de l'identifiant (sans l'espace de noms) est ce nom :
`text-editor notes.txt`, `browser`, `terminal`, `calculator`. L'app est lancée
comme enfant du shell, dans son dossier, avec ses arguments
(`WindowAppBase.LaunchArguments`) et les flux du shell : ce qu'elle écrit par
`HackConsole.WriteLine` apparaît dans le terminal. Comme un programme
graphique lancé depuis bash, **le shell attend la fermeture de la fenêtre** ;
`text-editor notes.txt &` rend l'invite tout de suite ; Ctrl+C ferme la
fenêtre (code 130). Une app à instance unique déjà ouverte est mise au premier
plan (code 0). La complétion propose ces noms.

## Terminal

Depuis le chantier `4-sdk-shell-host` (ADR 0056 §7), le shell est son propre
processus `hsh`, enfant de la fenêtre Terminal (ou de l'app qui embarque
`TerminalPanel`) : chaque panneau a le sien. `InitialCwd` est le répertoire
de départ du shell ([`shell-host.md`](../framework/shell-host.md)). L'invite vient du
processus : `utilisateur@machine:répertoire$`, `~` pour le dossier personnel,
`#` pour root.

Le terminal ne connaît aucun nom de commande : `cd`, `clear`, `help`, `sudo`
sont des commandes du catalogue, et la complétion propose les commandes
installées, les built-ins, `sh` et les apps.

## `ExecuteCommandIntent`

`AppIntentDispatcher` exécute `CommandLine` avec le même interpréteur, en mode
non interactif, sortie en tampon dans `AppIntentDispatchResult`. `apps.launch`
de l'appelant est vérifié une fois. Avec `callerContext`, le shell est le
processus de l'appelant ; sans, il est détaché (ADR 0054) et ne peut ni ouvrir
de fichiers ni lancer de scripts. Statut : `NotFound`, `Disabled`,
`ElevationDenied` ou `EntryPointFault` selon l'échec de la dernière commande,
sinon `Dispatched` avec le code de sortie (une erreur de syntaxe donne
`Dispatched`, code `2` et `ErrorCode` = code de l'erreur).

## Décisions clés

- Le shell n'a pas de répertoire ni de variables exportées à lui : ceux du
  processus font foi, ce qui garde `/proc/<pid>/cwd`, `ps` et les commandes
  cohérents. Les variables non exportées, elles, n'existent que dans le shell.
- Découpage en champs et chemins POSIX (ADR 0064) : l'écart de l'ADR 0014
  (pas de découpage) est abandonné, car les scripts Linux en dépendent.
- Un script est un processus `hsh` séparé, pas une fonction du shell
  appelant : ses `cd` et `exit` restent chez lui.
- Les built-ins spéciaux sont réduits au strict nécessaire (ADR 0051 amendé).
- Langue des messages (ADR 0104) : comme bash avec gettext, les diagnostics
  (`commande introuvable`, erreurs de syntaxe, d'expansion, `type`, jobs, `sudo`)
  suivent `LC_ALL` > `LC_MESSAGES` > `LANG` de la session, relus à chaque ligne
  (`ShellText`, clés `Shell.*`, `Syntax.*`, `Arithmetic.*`, `Errno.*` des
  `Strings` de Platform Core). Les préfixes `hsh: ` / `sudo: `, les noms et les
  chemins ne sont pas traduits. `ShellSyntaxError.Message` et les messages
  d'exception restent en anglais (journaux, appelants) ; `TextKey` /
  `TextArguments` portent le texte à traduire.

## Tests

- `Tests/HackerOs.Platform.Core.Tests/Shell/ShellParserTests.cs` : guillemets,
  opérateurs, listes, redirections de descripteurs, here-documents, commandes
  composées, fonctions, saisies incomplètes, erreurs, morceaux de mots.
- `Tests/HackerOs.Platform.Core.Tests/Shell/ShellPipeTests.cs`.
- `Tests/HackerOs.Platform.Core.Tests/Shell/ShellInterpreterTests.cs` et
  `ShellInterpreterTests.Scripting.cs` : bout en bout avec orchestrateur,
  processus et fichiers réels (tubes, `;`, `||`, affectations, substitutions,
  arithmétique, `${x%motif}`, `if`/`for`/`while`/`until`/`case`, `break 2`,
  fonctions et `local`, `2>&1`, here-documents, `/dev/null`, chemins, scripts
  avec et sans `+x`, `#!`, `set -e`, erreur de syntaxe de script, `sh -c`,
  `| bash`, `.`, jobs, apps par leur nom et `HackConsole`, Ctrl+C sur une app).
- `ShellInterpreterTests.Commands.cs` : les commandes de
  [`linux-commands.md`](linux-commands.md) dans le vrai shell.
- `Tests/HackerOs.Platform.Core.Tests/Shell/ShellCommandResolverTests.cs`.

## Liste des tâches

- [x] Parseur en arbre avec erreurs structurées (`ShellParser`, `ShellSyntax`).
- [x] Expansion `$VAR`, `${VAR}`, `$?`, `~` (sans découpage jusqu'à l'ADR 0064).
- [x] `ShellPipe` 64 Ki, tube cassé → 141.
- [x] Redirections `>` et `>>` ouvertes par le shell avec l'identité de l'hôte.
- [x] `&&`, codes de sortie, interruption.
- [x] `ShellInterpreter` et `ShellSession` dans Platform Core.
- [x] Flux sur `AppLaunchRequest`, sortie au fil de l'eau dans le terminal.
- [x] Commandes enfants du processus du shell, dans son répertoire.
- [x] `ExecuteCommandIntent` par l'interpréteur.
- [x] `cd`, `export`, `sudo`… du catalogue et `IShellSession`.
- [x] ADR 0064 : `;`, `||`, `&`, `!`, `<`, `n>&m`, `&>`, here-documents.
- [x] ADR 0064 : `if`, `while`, `until`, `for`, `case`, `{ }`, `( )`, fonctions.
- [x] ADR 0064 : paramètres positionnels et spéciaux, `${…}` avec opérateurs, `$(…)`, `` `…` ``, `$((…))`.
- [x] ADR 0064 : découpage sur `IFS`, expansion des chemins, variables non exportées, affectations en préfixe.
- [x] ADR 0064 : built-ins spéciaux (`break`, `return`, `set`, `local`, `eval`, `source`, `wait`, `fg`, `trap`…).
- [x] ADR 0064 : scripts (`+x` et message `chmod +x`, `#!`, processus `hsh`), `sh`/`bash`.
- [x] ADR 0064 : jobs (`&`, `$!`, `jobs`, `kill %n`, `wait`, `fg`).
- [x] ADR 0064 : apps fenêtrées par leur nom court, `HackConsole`, attente de la fermeture.
- [x] ADR 0064 : entrée standard du terminal (`TerminalInputReader`).
- [x] ADR 0064 : invite de continuation `>` pour une commande inachevée (`IHackShell.NeedsMoreInput`).
