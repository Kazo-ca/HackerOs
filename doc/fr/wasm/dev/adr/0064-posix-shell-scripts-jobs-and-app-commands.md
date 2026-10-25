# ADR 0064 : shell POSIX, scripts, jobs, apps lancées par leur nom et commandes Linux

## Statut

Accepté le 2026-10-13.

- **Contexte :** demande de l'utilisateur du 2026-10-13 : intégrer les tubes et
  `>` dans le terminal ; ajouter `sort`, `head`, `locate`, `zip`, `unzip`,
  `tail`, `cut`, `diff`, `tee`, `whoami`, `df`, `du`, `top`, `htop`,
  `systemctl`, `watch`, `jobs`, `kill`, `wget`, `curl`, `ip`, `netstat`, `ln`,
  `unalias` ; exécuter des scripts `sh` « le plus ressemblant au shell script
  de Linux », avec une erreur qui donne la commande `chmod +x` quand le bit
  d'exécution manque ; lancer une app par son nom sans espace de noms, avec ses
  arguments et en voyant sa sortie standard ; s'assurer que `nano` fonctionne
  avec le terminal xterm.js (ADR 0061).
- **Remplace** la grammaire de l'ADR 0050 §2 (qui refusait `;`, `||`, `&`,
  `<`, `2>`, `$(`, les parenthèses et `NOM=valeur`) et la règle « pas de
  découpage en mots après expansion » de l'ADR 0050 §3 (reprise de l'ADR 0014).
- **Amende** l'ADR 0051 §1 (« aucun built-in ») : quelques built-ins spéciaux
  POSIX vivent dans le shell (voir §4). Tout le reste reste au catalogue.
- **Complète** l'ADR 0056 §7 (shell hôte) : le terminal fournit maintenant une
  entrée standard aux commandes.

## Contexte

Le shell `hsh` savait faire `|`, `>`, `>>` et `&&`, mais aucun script Linux
réel ne passait : pas de `;`, de `||`, de `if`, de boucles, de fonctions, de
`$(…)`, de `$((…))`, de paramètres positionnels ni de redirection d'entrée.
Les commandes ne recevaient jamais d'entrée du clavier (stdin vide), si bien
que `read` ou `cat` interactif étaient impossibles. Une app fenêtrée n'avait
aucune sortie standard. `chmod` ne connaissait que l'octal, alors que la
demande cite `chmod +x`.

## Décision

### 1. Un sous-ensemble POSIX `sh`

`ShellParser` lit une ligne ou un script entier (lexer puis descente
récursive) :

- listes : `;`, retour à la ligne, `&` (job en arrière-plan) ; `&&` et `||` de
  même priorité, de gauche à droite ; `! pipeline` inverse le code ;
- commandes composées : `{ …; }`, `( … )` (sous-shell), `if/elif/else/fi`,
  `while`, `until`, `for NOM [in mots]`, `case … esac` ; fonctions `nom() { … }`
  et `function nom { … }` ;
- redirections : `[n]>`, `[n]>>`, `[n]<`, `[n]>&m`, `[n]<&m`, `&>`, `&>>`,
  here-documents `<<` et `<<-` (délimiteur entre guillemets : corps littéral),
  descripteurs 0 à 2 ; `/dev/null`, `/dev/stdin`, `/dev/stdout`,
  `/dev/stderr` et `/dev/tty` sont les flux du shell ;
- expansions : `$NOM`, `${NOM}`, `${NOM:-x}`, `:=`, `:+`, `:?` (et sans `:`),
  `${#NOM}`, `${NOM#motif}`, `##`, `%`, `%%`, `$1`…`${10}`, `$#`, `$@`, `$*`,
  `$$`, `$!`, `$-`, `$0`, `$?`, `$(…)`, `` `…` ``, `$((…))` (arithmétique
  entière 64 bits du C), `~` ;
- **découpage en champs** sur `IFS` des résultats non quotés, puis
  **expansion des chemins** (`*`, `?`, `[…]`, avec l'identité du shell ; un
  motif sans correspondance reste tel quel ; `.` initial explicite) ;
- `NOM=valeur` seul crée une **variable de shell non exportée** ; devant une
  commande, il ne vaut que pour elle ; `export` l'exporte ;
- commentaires `#`, continuation `\` + retour à la ligne.

Les échappements de l'ADR 0014 entre guillemets doubles (`\n`, `\t`) restent.
`<>`, `<<<`, `select`, `[[ ]]` ne sont pas pris en charge
(`shell.syntax.unsupported` ou erreur de syntaxe). Une saisie incomplète
(`if` sans `fi`, `|` final, guillemet ouvert) donne
`ShellParseResult.IsIncomplete`.

### 2. Scripts

- Un nom contenant `/` est un chemin : le fichier doit exister, ne pas être un
  dossier, être **exécutable** par l'utilisateur (bit `x` du propriétaire, du
  groupe ou des autres selon l'identité ; root : un bit quelconque) et lisible.
  Sinon : `hsh: ./x.sh: Permission denied` (code 126) suivi de
  `hsh: hint: './x.sh' is not executable. Make it executable with: chmod +x ./x.sh  (or run it with: sh ./x.sh)`.
- La ligne `#!` choisit l'interpréteur : `sh`, `bash`, `hsh`, `dash`, `zsh`…
  (directement ou par `/usr/bin/env`) → `hsh` ; une autre commande installée
  reçoit `[arg] chemin args…` ; sinon `bad interpreter` (126). Sans `#!`, le
  shell exécute le fichier (comme bash).
- Un script tourne dans un **nouveau processus `hsh`**, enfant du shell
  (`AppLifecycleOrchestrator.StartShellProcess`), avec les capacités et
  l'élévation du shell parent, ses propres variables (les exportées héritées),
  fonctions, options et paramètres positionnels ; `cd` et `exit` y restent.
  Une erreur de syntaxe est signalée avant tout lancement
  (`script.sh: line N: …`, code 2). `trap … EXIT` s'exécute à la sortie.
- `sh`, `bash` et `hsh` sont le shell lui-même : `sh script [args]` (lecture
  seule suffit), `sh -c 'commande' [nom args…]`, `sh -e/-x/-u`, et sans script
  les commandes lues sur l'entrée standard (`echo ls | sh`).

### 3. Jobs

`liste &` tourne dans une copie du shell, entrée vide, avec sa propre
annulation (Ctrl+C ne l'atteint pas, la fermeture du terminal si). Le shell
interactif affiche `[n] pid`, puis `[n]+  Done  commande` à la ligne suivante.
`$!`, `wait [%n|pid]`, `fg [%n]` (built-ins), `jobs` et `kill %n` (catalogue,
par `IShellSession.Jobs` et `IShellSession.KillJob`). Les processus d'un job
sont connus par `AppLaunchRequest.ProcessStarted`.

### 4. Built-ins spéciaux

Seules les commandes qui changent le contrôle ou l'état du shell d'une façon
qu'aucun processus séparé ne peut reproduire sont dans l'interpréteur : `:`,
`break`, `continue`, `return`, `shift`, `set` (`-e -u -x -f`, `-o`, `--`),
`local`, `eval`, `.`/`source`, `wait`, `fg`, `bg`, `exec`, `trap`,
`command`, `type`. `help` et la complétion les listent avec `sh`. `cd`,
`export`, `read`, `test`, `jobs`… restent des commandes du catalogue (ADR
0051) : `read` modifie le shell par `IShellSession.AssignVariable`. `[` est le
lien du shell vers `test` (un nom de commande de manifeste ne peut pas contenir
de crochet) ; `argv[0]` vaut `[`, ce qui exige le `]` final.

### 5. Apps lancées par leur nom court

Quand aucune commande ne porte le nom tapé, le shell cherche une app
**fenêtrée** dont le dernier segment de l'identifiant est ce nom
(`text-editor` pour `org.hackeros.text-editor`, `terminal`, `browser`…) ou
l'identifiant entier. Elle est lancée comme enfant du shell, dans son dossier,
avec les arguments et les flux du shell ; le shell **attend la fermeture de la
fenêtre** (comme bash avec un programme graphique ; `&` rend l'invite), Ctrl+C
la ferme (130). Une app à instance unique déjà ouverte est seulement mise au
premier plan (code 0). La complétion propose ces noms.

La sortie standard d'une app est la nouvelle façade `HackConsole`
(`Out`, `Error`, `In`, `WriteLine`…) sur `IAppExecutionContext.Console`
(`IAppConsoleGateway`) : les flux du shell qui l'a lancée, protégés contre un
terminal fermé ou un tube cassé ; `/dev/null` pour une app lancée du bureau.
Une commande du terminal voit les mêmes flux que son `TerminalExecutionContext`.

### 6. Entrée standard du terminal

Pendant qu'une ligne s'exécute, le terminal fournit une entrée standard
(`TerminalInputReader`) gérée comme un tty en mode canonique : écho, Retour
arrière, Ctrl+U, Entrée envoie la ligne, Ctrl+D sur ligne vide = fin d'entrée
(une fois), Ctrl+C interrompt. Ce qui n'a pas été lu revient à l'invite
(type-ahead), comme sous Linux. Seule une commande écrivant sur le terminal
lui-même reçoit l'écran alternatif (`nano`, `top`, `htop`, `watch`) : dans un
tube ou redirigée, elle produit son texte. Une commande inachevée tapée au
terminal (`IHackShell.NeedsMoreInput`) affiche l'invite de continuation `>`
(`PS2`) et les lignes s'exécutent ensemble ; l'historique les garde en une
ligne.

### 7. Commandes et `nano`

Commandes ajoutées au projet `HackerOs.Commands` (un dossier par commande) :
`locate`, `zip`, `unzip`, `cut`, `tee`, `whoami`, `df`, `du`, `top`, `htop`,
`systemctl` (alias `service`), `watch`, `jobs`, `wget`, `ip` (alias
`ifconfig`), `netstat` (alias `ss`), `ln`, `test`, `true`, `false`, `read`,
`sleep`, `printf`, `seq`, `basename`, `dirname`, `which`, `uniq`. Commandes
revues : `chmod` (modes symboliques, `-R`), `sort` (`-k -t -h -V -f -c -o`),
`head`/`tail` (`-N`, `-c`, `+N`, `tail -f`), `kill` (`%job`, noms de signaux,
`-l`), `curl` (`-o`, `-O`, `-H`, `-A`, `-s`). Le SDK gagne
`HackFile.CreateSymbolicLinkAsync` (`ln -s`) ; le système de fichiers n'a pas
de liens physiques, `ln` sans `-s` répond `Operation not supported`.

`nano` adopte les raccourcis et la disposition de GNU nano : `^O` demande le
nom (pré-rempli ; c'est aussi « enregistrer sous », car un vrai terminal ne
distingue pas Ctrl+Maj+O), `^S`, `^W`/`^F`, `^K`/`^U`, `^C`, `^G`, `^Y`/`^V`,
barre de titre et deux lignes de raccourcis, défilement horizontal, tabulations
sur 8 colonnes. Le rendu plein écran accepte les séquences SGR (couleurs) sans
les compter dans la largeur.

## Conséquences

- Les scripts Linux courants tournent tels quels ; les tests de bout en bout
  de `ShellInterpreterTests` le vérifient avec le vrai orchestrateur.
- Le découpage en champs change la sémantique d'un `$X` non quoté qui contient
  des espaces : il faut l'écrire `"$X"`, comme sous Linux.
- Une app lancée depuis le terminal bloque l'invite jusqu'à sa fermeture.
- 28 commandes de plus : le catalogue de l'hôte compte 74 apps dont 62
  commandes.

## Alternatives écartées

- **Garder les refus de l'ADR 0050** et n'ajouter que `;` : les scripts réels
  utilisent `if`, `for`, `$(…)` et `$((…))`.
- **Tout en built-ins comme bash** : contraire à l'ADR 0051 ; seuls les
  built-ins indispensables ont été ajoutés.
- **Exécuter les scripts dans le processus du shell** : `cd` ou `exit` dans un
  script changeraient le terminal, contrairement à Linux.
- **Ouvrir une app sans attendre sa fermeture** : plus pratique mais différent
  de Linux ; `&` donne ce comportement.
