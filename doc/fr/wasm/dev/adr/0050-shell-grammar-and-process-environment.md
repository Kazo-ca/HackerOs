> [English version](../../../../en/wasm/dev/adr/0050-shell-grammar-and-process-environment.md)

# ADR 0050 : Grammaire du shell et environnement de processus

## Statut

Accepté le 2026-10-10.

- **Phase du plan :** phase 3 — Shell, commandes et corbeille
  ([`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), sections 7.1 et
  7.2, décisions D12 et D13).
- **Remplace :** ADR 0014 (grammaire de la première tranche). Les règles de
  découpage en mots, de guillemets et d'échappement de l'ADR 0014 sont
  reprises telles quelles ; la liste des opérateurs refusés et l'environnement
  en lecture seule sont remplacés.
- **Complète :** ADR 0012 (modèle de processus) et ADR 0046 (`/proc`) : un
  processus gagne des arguments, un répertoire courant et un environnement,
  que `/proc/<pid>/cmdline`, `cwd` et `environ` exposent enfin.
- **Complète :** ADR 0043 : le contexte ambiant porte aussi l'environnement,
  lu par la nouvelle classe `HackEnvironment`.

## Contexte

Le shell actuel est la méthode `ExecuteInputAsync` de
`Shared/HackerOs.AppSdk.Terminal/TerminalPanel.razor`. Elle mélange la
grammaire, la résolution, `sudo`, les alias et l'affichage. Son parseur
(`ShellParser`) ne connaît ni l'expansion de variables, ni les opérateurs.
Le texte de la ligne est ensuite reconstruit avec des espaces
(`$"{cmd} {string.Join(" ", args)}"`) puis redécoupé sur les espaces par
`AppIntentDispatcher.DispatchExecuteCommandAsync` : un argument entre
guillemets qui contient un espace est donc coupé en deux.

`AppLifecycleOrchestrator.RunTerminalAsync` donne à chaque commande une entrée
vide, une sortie mise en tampon (`StringWriter`) rendue seulement à la fin, et
un environnement réduit à `HOME`. `TerminalSession` tient pourtant un
environnement (`USER`, `HOME`, `SHELL`, `TERM`, `PATH`) qui n'atteint jamais
les commandes. Les processus n'ont ni arguments, ni répertoire, ni
environnement : `ProcFileSystemProvider` renvoie l'identifiant d'app pour
`cmdline`, un `environ` vide et le dossier personnel pour `cwd`.

## Décision

### 1. Un interpréteur sans interface, dans Platform Core

Le shell devient un moteur sans interface dans
`Platform/HackerOs.Platform.Core/Shell/` :

- `ShellParser` produit un arbre (section 2) ou une erreur structurée (code,
  position, longueur), sans jamais lever d'exception pour une saisie.
- `ShellInterpreter` exécute cet arbre : expansion, redirections, tubes,
  lancement des étapes par `AppLifecycleOrchestrator`, code de sortie.
- `ShellSession` est l'état d'un shell : son processus, l'historique, le
  dernier code de sortie. Le répertoire courant et l'environnement ne sont
  **pas** copiés dans la session : ce sont ceux du processus du shell
  (section 5).

`TerminalPanel` ne garde que l'édition de ligne, l'affichage, l'historique au
clavier, la complétion et la fenêtre d'identifiants. L'ancien `ShellParser`
et `TerminalSession` de `HackerOs.AppSdk.Terminal` disparaissent.
`ExecuteCommandIntent` passe par le même interpréteur, en mode non interactif
(section 7) : plus aucun découpage sur les espaces.

### 2. Grammaire

```text
liste      := pipeline ( "&&" pipeline )*
pipeline   := commande ( "|" commande )*
commande   := ( mot | redirection )+
redirection:= ( ">" | ">>" ) mot
```

- **Mots, guillemets et échappements :** inchangés depuis l'ADR 0014
  (guillemets simples littéraux, guillemets doubles avec expansion et
  échappements `\"`, `\\`, `\$`, `\n`, `\r`, `\t`, barre oblique inverse hors
  guillemets).
- **Opérateurs reconnus** hors guillemets : `|`, `&&`, `>`, `>>`. Ils n'ont pas
  besoin d'espaces autour (`ls|wc`, `echo a>f` sont valides).
- **Opérateurs encore refusés** hors guillemets, avec l'erreur
  `shell.syntax.unsupported` : `;`, `||`, `&` seul, `<`, `2>`, `&>`, `` ` ``,
  `$(`, `(`, `)`. Ils restent utilisables entre guillemets ou échappés. Ils
  pourront être ajoutés par un ADR ultérieur sans casser cette grammaire.
- **Erreurs de syntaxe :** opérateur en tête ou en fin (`| wc`, `ls &&`),
  deux opérateurs de suite, redirection sans cible, commande vide dans un
  tube. Code de sortie `2`, aucune commande lancée.
- **Affectation :** un premier mot de la forme `NOM=valeur` est refusé avec
  l'erreur `shell.syntax.assignment` (« use export NOM=valeur »). La forme
  `NOM=valeur commande` est hors périmètre.
- **Non pris en charge (inchangé) :** globbing, expansion d'accolades,
  substitution de commande, sous-shells, fonctions, scripts, tâches en
  arrière-plan.

### 3. Expansion

Elle a lieu après le découpage, mot par mot, et ne relit jamais le résultat
comme de la syntaxe.

- `$NOM` et `${NOM}` hors guillemets simples, noms
  `[A-Za-z_][A-Za-z0-9_]*`. Une variable absente donne une chaîne vide.
- `$?` : code de sortie de la dernière liste exécutée par ce shell.
- `~` et `~/…` au début d'un mot non entre guillemets : valeur de `HOME`.
  `~autre` n'est pas pris en charge et reste littéral.
- **Pas de découpage en mots après expansion** (écart volontaire avec POSIX,
  repris de l'ADR 0014) : `$X` reste un seul argument même s'il contient des
  espaces. C'est plus sûr pour des chemins qui contiennent des espaces.
- La cible d'une redirection est développée de la même façon.

### 4. Exécution

**Tubes.** Les étapes d'un tube tournent **en même temps**, comme sous Linux.
Elles sont reliées par un `ShellPipe` : un tampon en mémoire de 64 Kio (la
taille par défaut d'un tube Linux). Un écrivain attend quand le tampon est
plein ; un lecteur attend quand il est vide ; la fin de l'écrivain ferme le
tube et le lecteur reçoit la fin de flux. Le navigateur n'a qu'un fil
d'exécution : les lectures synchrones (`Read`, `ReadLine`, `ReadToEnd`) sur un
tube vide encore ouvert lèvent `InvalidOperationException` avec un message qui
demande la forme asynchrone. Toutes les commandes actuelles lisent déjà en
asynchrone. Quand un lecteur se termine avant son écrivain, l'écriture
suivante lève `HackIOException` (« Broken pipe »), que la commande peut
ignorer ; l'étape se termine alors avec le code `141` si elle laisse passer
l'exception, comme `SIGPIPE`.

**Flux.** L'entrée de la première étape est vide (fin de flux immédiate,
comme `/dev/null`) : l'entrée clavier d'une commande en cours n'est pas
prévue dans cette phase, `nano` garde son écran plein. La sortie de la
dernière étape et la sortie d'erreur de **toutes** les étapes vont au
terminal **au fil de l'eau**, plus seulement à la fin.
`AppLaunchRequest` gagne des flux optionnels `StandardInput`,
`StandardOutput` et `StandardError` ; quand ils sont fournis,
`RunTerminalAsync` les utilise à la place de ses tampons, et
`AppLaunchResult.StandardOutput`/`StandardError` restent vides. Sans flux
fournis, le comportement en tampon actuel est conservé pour les autres
appelants.

**Redirections.** C'est le shell, pas la commande, qui ouvre la cible, avant
de lancer l'étape, avec l'identité et les capacités de l'app qui héberge le
shell (le Terminal), et sous les droits du fichier (D16). `>` crée ou vide le
fichier, `>>` crée ou ajoute à la fin. Plusieurs redirections dans une même
commande sont ouvertes dans l'ordre (chacune crée ou vide son fichier) et la
dernière reçoit la sortie, comme sous Linux. Une redirection qui échoue
affiche `hsh: <cible>: <raison>`, ne lance pas l'étape et vaut le code `1`.
Conséquence voulue et fidèle à Linux : `sudo echo x > /etc/f` échoue, car la
redirection n'est pas élevée. Une étape redirigée qui n'est pas la dernière
d'un tube envoie sa sortie au fichier, et l'étape suivante lit une entrée
vide.

**`&&`.** Le pipeline suivant ne s'exécute que si le code de sortie du
précédent vaut `0`. Le code d'un pipeline est celui de sa dernière étape (pas
de `pipefail`). Le code de la ligne est celui du dernier pipeline exécuté.

**Codes de sortie** (repris de l'ADR 0014) : `127` commande introuvable, `2`
erreur de syntaxe, `130` interruption, `1` faute non gérée, `141` tube cassé.

**Interruption.** Ctrl+C annule toutes les étapes du pipeline en cours et
arrête la liste.

**Processus.** Chaque étape est un processus enfant du processus du shell
(`AppLaunchRequest.ParentPid`). Le processus du shell est celui de l'app qui
l'héberge : la fenêtre Terminal pour le terminal, l'app appelante pour
`ExecuteCommandIntent`. `ps` montre donc l'arbre `terminal` → `grep`, `wc`.

**Alias.** L'expansion des alias reste une étape du shell (comme sous Linux)
et s'applique au premier mot de chaque commande, avant la résolution. Leur
fichier ne change pas.

### 5. Environnement par processus

Un processus porte trois nouvelles données, fixées à sa création :

- ses **arguments** (`argv`, nom de la commande compris) ;
- son **répertoire courant**, chemin absolu canonique ;
- son **environnement**, dictionnaire ordinal nom → valeur.

Elles vivent dans une table à part, `IProcessEnvironmentTable`
(`Shared/HackerOs.Simulation.Abstractions/Processes/`), indexée par PID et
vidée quand le processus se termine. `ProcessRecord` reste un instantané
immuable et ne change pas.

`ProcessStartRequest` et `AppLaunchRequest` gagnent `Environment` (optionnel)
et réutilisent `Arguments` et `WorkingDirectory`. À la création :

1. **avec parent :** copie du répertoire et de l'environnement du parent au
   moment de la création, puis application des valeurs fournies par la
   requête ;
2. **sans parent** (lancement depuis le bureau, service) : environnement de
   connexion (ci-dessous) et dossier personnel.

Une modification ultérieure du parent ne touche jamais un enfant déjà créé, et
inversement. Un processus ne peut changer que **son propre** répertoire et
**son propre** environnement (section 6). Changer ceux de son shell parent
passe par l'accès contrôlé de l'ADR 0051.

**Environnement de connexion**, construit à l'ouverture de session :

| Variable | Valeur |
|---|---|
| `HOME` | dossier personnel |
| `USER`, `LOGNAME` | nom de connexion |
| `SHELL` | `/bin/hsh` |
| `PATH` | `/bin:/usr/bin` (informatif : la résolution passe par le catalogue) |
| `PWD` | répertoire courant, tenu à jour par le shell |
| `LANG` | `en_US.UTF-8` |

Puis les lignes `NOM=valeur` de **`/etc/environment`** (fichier texte, droits
`0644`, propriétaire root, modifiable par un administrateur, comme
`pam_env`), qui ajoutent ou remplacent des variables, sauf `HOME`, `USER` et
`LOGNAME`. Lignes vides et `#` ignorées, ligne invalide ignorée avec un
avertissement dans le journal (D17). Le shell ajoute `TERM=xterm-256color`
et `OLDPWD` à son propre environnement. Un environnement utilisateur persistant
(`~/.profile`) est hors périmètre, faute de scripts.

**`/proc`** (complète l'ADR 0046) : `/proc/<pid>/cmdline` donne les arguments
séparés par `\0` (l'identifiant d'app quand il n'y en a pas), `environ` les
paires `NOM=valeur` séparées par `\0` (droits `0400` inchangés), `cwd` est un
lien vers le répertoire courant réel.

### 6. `HackEnvironment` dans le SDK

`Shared/HackerOs.AppSdk/HackEnvironment.cs`, statique, calqué sur
`System.Environment`, et lu dans le contexte ambiant de l'ADR 0043 :

```csharp
public static class HackEnvironment
{
    public static string CurrentDirectory { get; set; }        // set : chemin existant, sinon HackDirectoryNotFoundException
    public static string? GetEnvironmentVariable(string name);
    public static IReadOnlyDictionary<string, string> GetEnvironmentVariables();
    public static void SetEnvironmentVariable(string name, string? value); // null supprime
    public static string[] GetCommandLineArgs();
    public static int ProcessId { get; }
    public static string UserName { get; }
    public static string MachineName { get; }                  // première ligne de /etc/hostname
    public static string NewLine => "\n";
}
```

- Toutes ces opérations visent le processus courant uniquement ; aucune
  capacité n'est nécessaire, comme sous Linux.
- `CurrentDirectory` est le répertoire utilisé par `HackPath` pour les chemins
  relatifs : `HackExecutionContext.WorkingDirectory` lit désormais le
  répertoire du processus.
- `/etc/hostname` (`0644`, root) est créé par l'amorçage du système de
  fichiers avec `hackeros` s'il est absent ; l'invite du terminal l'utilise
  au lieu de la valeur codée en dur.
- `TerminalExecutionContext.Environment` et `WorkingDirectory` restent, pour
  la compatibilité, et donnent l'état du processus au démarrage de la
  commande.

### 7. Shell non interactif

`ExecuteCommandIntent.CommandLine` est exécutée par `ShellInterpreter` avec la
même grammaire, dans un shell sans session interactive : pas d'historique,
pas d'accès au shell pour les commandes (ADR 0051), sortie en tampon dans
`AppIntentDispatchResult`. Le répertoire de départ est
`ExecuteCommandIntent.WorkingDirectory`, sinon celui du processus appelant.
L'autorisation `apps.launch` de l'appelant est vérifiée une fois pour la ligne.

## Conséquences

- Une seule implémentation du shell, testable sans navigateur, pour le
  terminal et pour les apps.
- Les arguments entre guillemets arrivent intacts aux commandes.
- `ps`, `/proc` et les commandes voient des processus réalistes : arguments,
  répertoire, environnement, arbre parent-enfant.
- Les commandes qui lisent leur entrée doivent rester asynchrones ; c'est
  déjà le cas.
- La sortie du terminal s'affiche au fil de l'eau, ce qui change le rendu de
  `ping` et de `curl`.
- `;`, `||`, `<` et le globbing restent à faire ; ils sont refusés clairement.

## Chantiers d'implémentation

Voir l'ADR 0052 pour le tableau complet de la phase 3. Chantiers de cet ADR :
`rebuild/3-process-environment` (sections 5 et 6, `/proc`) puis
`rebuild/3-shell-interpreter` (sections 1 à 4 et 7).

## Références

- ADR 0012 (processus), ADR 0014 (remplacé), ADR 0042 (`sudo`), ADR 0043
  (contexte ambiant), ADR 0046 (`/proc`), ADR 0051 (commandes).
- [`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), sections 7.1 et 7.2.
- `Shared/HackerOs.AppSdk.Terminal/TerminalPanel.razor`, `ShellParser.cs`,
  `TerminalSession.cs` ; `Platform/HackerOs.Platform.Core/Intents/AppIntentDispatcher.cs` ;
  `Platform/HackerOs.Platform.Core/Lifecycle/AppLifecycleOrchestrator.cs` ;
  `Platform/HackerOs.Platform.Core/FileSystem/Virtual/ProcFileSystemProvider.cs`.
