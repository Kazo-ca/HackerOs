> [English version](../../../../en/wasm/dev/adr/0051-unified-commands-project.md)

# ADR 0051 : Commandes regroupées, sans exception codée en dur

## Statut

Accepté le 2026-10-10.

- **Phase du plan :** phase 3 — Shell, commandes et corbeille
  ([`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), section 7.3,
  décision D14).
- **Remplace :** la section 1 de l'ADR 0034 (exclusion de `cd`, `pwd`,
  `clear` et `help` du catalogue, parce que le terminal les interceptait).
  Les sections 2 à 5 de l'ADR 0034 (construction par injection, réseau
  simulé, capacités corrigées) restent valides.
- **Complète :** ADR 0042 : `sudo` devient une commande du catalogue ; ses
  règles (une seule commande élevée, identifiants d'un administrateur,
  ticket à usage unique) ne changent pas.
- **S'appuie sur :** ADR 0047 (plusieurs classes `[AppManifest]` dans un
  projet produisent `manifests/<appId>.json`), ADR 0048 (`IsSystemApp` sur la
  `ProjectReference`), ADR 0050 (shell et environnement de processus).

## Contexte

Les commandes sont 28 projets `Apps/Commands/HackerOs.Commands.*`, chacun avec
une classe de commande et une classe manifeste. L'hôte a 24 `ProjectReference`
pour elles, la solution 28 projets, et quatre projets de tests en référencent
une partie. `TerminalPanel.razor` intercepte `clear`, `pwd`, `cd`, `help` et
`sudo` avant toute résolution : leurs projets `Cd`, `Pwd`, `Clear` et `Help`
existent mais ne sont pas référencés par l'hôte (ADR 0034, section 1), et la
complétion comme `help` listent ces noms en dur.

## Décision

### 1. Un seul projet `HackerOs.Commands`

- Nouveau projet `Apps/Commands/HackerOs.Commands/HackerOs.Commands.csproj`.
  Chaque commande occupe un dossier (`Cat/CatApp.cs`, `Cat/CatCommand.cs`) et
  garde son espace de noms (`HackerOs.Commands.Cat`), ses classes et sa
  déclaration `[AppManifest]`.
- **Les identifiants d'app ne changent pas** (`org.hackeros.commands.cat`…).
  Les fichiers `apps.config`, `app-permissions.config` et les alias déjà
  écrits restent valides.
- Le générateur écrit `manifests/<appId>.json` pour chaque commande (ADR 0047) ;
  les `manifest.json` des anciens projets disparaissent avec eux.
- L'hôte n'a plus qu'**une** référence :
  `<ProjectReference Include="..\..\Apps\Commands\HackerOs.Commands\HackerOs.Commands.csproj" IsSystemApp="true" />`.
  Toutes les commandes intégrées sont donc des apps système, comme les 24
  actuelles. L'assembly est chargée à la demande en un seul bloc.
- Les 28 anciens projets sont supprimés de la solution et du disque. Les
  projets de tests référencent le nouveau projet. Les échantillons
  (`Apps/Samples/HackerOs.Samples.TerminalApp`) restent un projet à part :
  c'est le modèle d'une commande tierce.
- **Format des commandes tierces inchangé :** un projet, une app de type
  `Terminal` avec sa classe manifeste. Un projet tiers peut aussi regrouper
  plusieurs commandes de la même manière.

### 2. Plus aucune exception dans le terminal

`TerminalPanel` et `ShellInterpreter` ne connaissent **aucun** nom de
commande. Tout passe par la résolution du catalogue : `cd`, `pwd`, `clear`,
`help`, `sudo` et les nouvelles commandes `export`, `unset` et `env`. La
complétion et `help` lisent la liste des commandes installées. Désinstaller
une commande système reste impossible (ADR 0048), donc `cd` ne peut pas
disparaître.

### 3. Accès contrôlé au shell appelant

Comme sous Linux, `cd` et `export` doivent agir sur le shell lui-même, pas sur
leur propre processus. Le SDK donne cet accès :

- `TerminalExecutionContext.Shell`, de type `IShellSession?`
  (`Shared/HackerOs.AppSdk/`).
- Nouvelle capacité **`shell.control`** (`AppCapabilities.ShellControl`),
  classée **normale** (ADR 0049) : elle n'agit que sur le shell de
  l'utilisateur qui a tapé la commande.
- `Shell` est non nul seulement si la commande déclare `shell.control` et
  qu'elle est lancée par un shell **interactif**.
- Seule dans son pipeline (y compris après un `&&`), elle reçoit le **vrai**
  shell : `cd /tmp && ls` liste `/tmp`.
- Dans un tube (`cd /tmp | cat`), elle reçoit une **copie** du shell, comme
  le sous-shell dans lequel bash exécute alors ses commandes internes : la
  commande réussit, mais ses changements sont perdus à la fin de l'étape.

```csharp
public interface IShellSession
{
    string WorkingDirectory { get; }
    IReadOnlyDictionary<string, string> Environment { get; }
    IReadOnlyList<string> History { get; }

    // cd : vérifie le dossier (existence, type, droit de traverser) sous l'identité de la commande,
    // puis met à jour le répertoire du processus du shell, PWD et OLDPWD.
    ValueTask ChangeDirectoryAsync(string path, CancellationToken cancellationToken);

    void SetVariable(string name, string value);   // nom invalide : ArgumentException
    void UnsetVariable(string name);

    // help : commandes installées (nom, alias, usage, description).
    IReadOnlyList<ShellCommandInfo> GetCommands();

    // sudo : exécute une commande comme root (section 4).
    ValueTask<int> RunElevatedAsync(IReadOnlyList<string> argv, TextWriter stdout, TextWriter stderr,
        CancellationToken cancellationToken);
}
```

Le changement est appliqué tout de suite au processus du shell (ADR 0050,
section 5), pas à la fin de la commande, pour que la suite d'une liste `&&`
le voie. Une commande déclarant `shell.control` lancée sans shell (par une
app par `ExecuteCommandIntent`) affiche `<nom>: no shell session` et rend `1`, sauf
indication contraire ci-dessous.

### 4. Comportement des commandes concernées

| Commande | Capacités | Comportement |
|---|---|---|
| `cd [dir\|-]` | `shell.control`, `filesystem.user-home.read`, `filesystem.system.read` | Sans argument : `HOME`. `-` : `OLDPWD`, affiché. Erreurs Linux (`No such file or directory`, `Not a directory`, `Permission denied`), code `1`. |
| `pwd` | — | Affiche son **propre** répertoire courant, hérité du shell : fonctionne sans accès au shell, comme `/bin/pwd`. |
| `clear` | — | Écrit la séquence `ESC[H ESC[2J ESC[3J` sur sa sortie. Le terminal interprète ces séquences et vide l'écran ; `clear > f` écrit la séquence dans `f`, comme sous Linux. |
| `help [cmd]` | `shell.control` | Liste les commandes installées ou affiche l'usage d'une commande. Sans shell : liste vide et message, code `1`. |
| `export NOM=valeur…` | `shell.control` | Modifie l'environnement du shell ; les commandes lancées ensuite en héritent. Sans argument : liste `declare -x NOM="valeur"`. |
| `unset NOM…` | `shell.control` | Retire des variables du shell. |
| `env` | — | Affiche son propre environnement (hérité du shell), une ligne `NOM=valeur` par variable. `env NOM=v commande` est hors périmètre. |
| `sudo cmd args…` | `shell.control` | Voir ci-dessous. |

**`sudo`.** La commande appelle `IShellSession.RunElevatedAsync`. Le shell
garde la fenêtre d'identifiants et le ticket de l'ADR 0042 : il demande les
identifiants d'un administrateur, crée le ticket `SudoRoot`, lance la commande
cible comme enfant avec ce ticket, et relie sa sortie à celle de `sudo`. La
commande cible ne reçoit **jamais** l'accès au shell : `sudo cd /root` affiche
`sudo: cd: shell builtin` et rend `1`. Un échec d'authentification affiche
`sudo: authentication failed` et rend `1`. `sudo` dans un tube
fonctionne (`sudo cat /etc/shadow | wc`) : la copie du shell sait aussi
exécuter `RunElevatedAsync`.

**Alias.** `alias`, `unalias` et `rmalias` restent la commande `alias` du
catalogue (alias statiques `unalias`, `rmalias` déclarés dans son manifeste au
lieu de la réécriture actuelle dans le terminal) ; l'expansion reste dans le
shell (ADR 0050, section 4).

### 5. Approbation ponctuelle après un refus

La reprise « Permission denied — approuver cette opération ? » de l'ADR 0042
reste une fonction du shell interactif : elle s'applique à une commande seule
(pas dans un tube ni redirigée, pour ne pas rejouer d'effets de bord), et
relance la même commande avec le ticket `CapabilityApproval`.

## Conséquences

- Une seule `ProjectReference` dans l'hôte pour toutes les commandes ; une
  seule assembly chargée à la demande.
- Le terminal n'a plus de liste de noms : une commande tierce peut fournir son
  propre `cd` amélioré en déclarant `shell.control`, sans toucher au terminal.
- `help`, la complétion et `ps` voient les mêmes commandes que le shell.
- Les projets de tests et la solution changent de références en une fois ;
  aucun identifiant d'app ne change.

## Chantiers d'implémentation

Voir l'ADR 0052 pour le tableau complet de la phase 3. Chantiers de cet ADR :
`rebuild/3-commands-merge` (section 1, sans changement de comportement) puis
`rebuild/3-shell-commands` (sections 2 à 5).

## Références

- ADR 0034 (section 1 remplacée), ADR 0042 (`sudo`, approbation ponctuelle),
  ADR 0047, ADR 0048, ADR 0049 (classement de `shell.control`), ADR 0050.
- [`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), section 7.3.
- `OS/HackerOs.Ecosystem/HackerOs.Ecosystem.csproj`, `Apps/Commands/`,
  `Shared/HackerOs.AppSdk.Terminal/TerminalPanel.razor`.
