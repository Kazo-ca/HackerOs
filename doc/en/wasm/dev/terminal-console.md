# Console : complétion des chemins, `exit` et onglets

## Objet

Chantier `rebuild/4-terminal` de la phase 4 (ADR 0058, section 4). La console
atteint la parité avec un terminal Linux :

- **Tab complète aussi les chemins**, et c'est le shell qui le fait
  (`IHackShell.CompleteAsync`), pas l'émulateur de terminal ;
- **`exit [n]`** est une commande du catalogue qui termine le shell ;
- **plusieurs onglets**, chacun avec son propre shell `hsh`.

```text
Terminal (fenêtre, PID 12)
 ├─ hsh (PID 13)        ← onglet « projets »
 │   └─ ls (PID 20)
 └─ hsh (PID 14)        ← onglet « ~ »
```

Le rendu (grille de caractères, couleurs, édition de la ligne, écran
alternatif) est décrit dans [`terminal-emulator.md`](terminal-emulator.md)
(ADR 0061).

## Complétion par Tab

Calculée par `ShellCompletion` (Platform Core) dans le contexte d'exécution du
shell : un chemin relatif part du répertoire **du shell**, et la lecture du
dossier passe par l'API publique `HackDirectoryInfo` avec les droits du shell,
comme un `ls` qu'il lancerait.

| Mot sous le curseur | Candidats |
|---|---|
| Premier mot d'une commande (début de ligne, après `\|` ou `&&`), sans `/` | Commandes installées et alias statiques, sensibles à la casse. |
| Tout autre mot, ou premier mot avec un `/` (`./build.sh`) | Entrées du dossier désigné par le début du mot. |
| `~` seul | `~/`. |
| Mot entre guillemets | Rien (il faudrait fermer le guillemet). |

Règles des chemins :

- le début tapé (`~/`, `../`, `docs/`) est gardé tel quel, seul le nom est
  complété ; `~` est développé avec le `HOME` du shell pour la lecture ;
- un dossier finit par `/` (aucune espace n'est ajoutée après) ;
- les entrées cachées ne sont proposées que si le nom tapé commence par `.` ;
- les caractères que le parseur couperait ou développerait (espace, `|`, `&`,
  `;`, `<`, `>`, `$`, guillemets, `*`, `?`…) sont échappés par `\` :
  `cat My` donne `cat My\ Notes.txt ` ; un mot déjà échappé (`My\ N`) est
  reconnu comme un seul mot ;
- une redirection termine un mot : `echo x >no` complète `notes.txt` ;
- un dossier absent ou illisible (droits, capacité) ne propose rien, sans
  erreur.

`HackShellCompletion.DisplayCandidates` donne ce que le terminal liste au
second Tab : le dernier segment, sans ses échappements (`My Notes.txt`,
`sub/`), comme `bash`.

## `exit [n]`

| Appel | Effet |
|---|---|
| `exit` | Le shell sort avec `$?`. |
| `exit n` | Sort avec `n` modulo 256 (`exit 300` → `44`, `exit -1` → `255`). |
| `exit abc` | `exit: abc: numeric argument required`, sort avec `2`. |
| `exit 1 2` | `exit: too many arguments`, code `1`, le shell reste. |
| `exit 4 \| cat` | Dans un tube, la demande atteint une copie du shell et se perd, comme le sous-shell de `bash`. |

La commande (`org.hackeros.commands.exit`, capacité `shell.control`) appelle
`IShellSession.Exit(int?)`. `ShellSession.RequestExit` note le statut ;
l'interpréteur n'exécute pas la suite de la ligne (`exit 0 && echo late`
n'affiche rien), puis l'hôte du shell (`ShellHostService`) termine le
processus `hsh` avec ce statut. `IHackShell.HasExited` devient vrai et
`ExecuteAsync` lève ensuite `ObjectDisposedException`.

**Ctrl+D** sur une ligne vide, sans commande en cours, fait la même chose :
le terminal affiche `exit` et termine le shell avec `$?`.

`TerminalPanel.OnExitRequested` est appelé quand le shell est sorti. Sans
abonné, le panneau reste ouvert et affiche `[hsh exited with status n]`.
Un hôte qui embarque le terminal par contrat (ADR 0063) reçoit aussi
l'événement `IHackTerminal.Exited` : l'éditeur de code ferme l'onglet du
terminal intégré, et masque le dock pour le dernier.

## Onglets

`TerminalTabs` (`Shared/HackerOs.AppSdk.Terminal`) est un composant
réutilisable : chaque onglet est un `TerminalPanel`, donc un `hsh` distinct
avec son répertoire, son environnement, son historique et son `$?`. La
fenêtre Terminal n'est plus qu'un hôte de `TerminalTabs`.

| Action | Effet |
|---|---|
| `+`, Ctrl+Maj+T | Nouvel onglet après l'onglet actif, dans le répertoire de l'onglet actif. |
| `×`, Ctrl+Maj+W, `exit`, Ctrl+D | Ferme l'onglet ; son shell et ses commandes s'arrêtent (disposition du panneau). L'onglet de droite (sinon de gauche) devient actif. |
| Ctrl+PgPréc, Ctrl+PgSuiv | Onglet précédent, suivant, en boucle. |
| Dernier onglet fermé | `OnLastTabClosed` : la fenêtre Terminal arrête son propre processus (`Processes.StopAsync` sur son PID), ce qui ferme la fenêtre. |

Titre d'un onglet (`TerminalPanel.Title`, `OnTitleChanged`) : nom de la
commande en cours, sinon dernier segment du répertoire de l'invite (`~`,
`projets`, `/`).

Les raccourcis sont interceptés par `TerminalTabs.razor.js`, qui annule
l'action du navigateur. Un navigateur peut garder certains raccourcis pour ses
propres onglets (Chrome garde Ctrl+Maj+T et Ctrl+Maj+W hors application
installée) ; les boutons `+` et `×` marchent toujours.

Tab ne déplace pas le focus du navigateur : xterm.js le reçoit et l'envoie au
terminal (ADR 0061). Le gestionnaire de touches de xterm.js laisse remonter
les raccourcis d'onglets ci-dessus jusqu'à `TerminalTabs`.

### API

| Membre | Rôle |
|---|---|
| `TerminalTabs.AppContext`, `CallerAppId`, `InitialCwd` | Comme `TerminalPanel`, pour le premier onglet. |
| `TerminalTabs.OnLastTabClosed` | Le dernier onglet s'est fermé. |
| `TerminalTabs.OpenTabAsync`, `CloseActiveTabAsync`, `SwitchTabAsync(±1)` | Les actions des raccourcis. |
| `TerminalTabSet`, `TerminalTabState` | Règles d'ordre des onglets, testables sans rendu. |
| `TerminalPanel.Title`, `OnTitleChanged`, `WorkingDirectory`, `FocusAsync` | Ce dont un hôte d'onglets a besoin. |

## Décisions

- **`exit` est une commande du catalogue**, comme `cd` (ADR 0051), et non un
  nom codé en dur dans le terminal : elle passe par `IShellSession.Exit`, un
  membre à implémentation par défaut (`NotSupportedException`) pour ne pas
  casser les autres implémentations.
- **Candidats = mots complets** (`~/docs/notes.txt`) pour que `ApplyTo` et
  `CommonPrefix` restent simples ; l'affichage court est
  `DisplayCandidates`.
- **Pas de complétion dans les guillemets** ni de `~utilisateur` : absents du
  legacy, ils compliqueraient le découpage des mots.
- **La fenêtre se ferme en arrêtant son processus** : le SDK n'a pas d'API
  « fermer ma fenêtre », et le bureau ferme déjà la fenêtre d'un processus
  arrêté.
- **CodeEditor garde ses propres onglets** (ADR 0058) ; depuis l'ADR 0063,
  chacun embarque le terminal de l'app Terminal par contrat, et l'éditeur
  réagit à `exit` par `IHackTerminal.Exited`.
- **`;` n'est pas reconnu par le parseur** : la complétion le traite comme un
  séparateur pour être prête, mais les exemples utilisent `&&`.

## Tests

- `Tests/HackerOs.Platform.Core.Tests/Shell/ShellInterpreterTests.HostedShell.cs` :
  chemins (relatifs, `~`, `../`, absolus, échappements, cachés, redirections,
  `./`, dossier absent, casse), répertoire du shell, dossier illisible,
  `DisplayCandidates`, `exit` (statuts, modulo, non numérique, fin du
  processus, suite de la ligne, tube, trop d'arguments, `$?`).
- `Tests/HackerOs.Commands.Tests/CoreCommandsTests.cs` : `ExitCommand`.
- `Tests/HackerOs.AppSdk.Terminal.Tests/TerminalTabSetTests.cs` : ordre,
  fermeture, bascule des onglets.
- `Tests/HackerOs.Ecosystem.Tests/BuildKnownLazyAppsTests.cs` : `exit` au
  catalogue (75 apps, 63 commandes depuis `stress`, ADR 0070).
- Vérification dans le navigateur (publication Release) : `cd pro` + Tab →
  `cd projets/`, second Tab liste `alpha/  notes.txt`, le focus reste sur la
  ligne, un nouvel onglet s'ouvre dans `~/projets`, `exit` ferme l'onglet puis
  la fenêtre, aucune erreur.

## Liste des tâches du chantier `rebuild/4-terminal`

- [x] Complétion des chemins dans `ShellCompletion`, avec les droits et le répertoire du shell.
- [x] `HackShellCompletion.DisplayCandidates`, liste courte au second Tab.
- [x] `IShellSession.Exit`, `ShellSession.RequestExit`, arrêt de la ligne, fin du processus `hsh`.
- [x] Commande `exit [n]` dans `HackerOs.Commands`.
- [x] `TerminalTabs`, `TerminalTabSet`, raccourcis, titre des onglets ; fenêtre Terminal sur `TerminalTabs`, fermée avec le dernier onglet.
- [x] Ctrl+D termine le shell ; Tab ne déplace plus le focus.
- [x] CodeEditor ferme l'onglet dont le shell sort.
- [x] Tests, ce document, `shell-host.md`, `shell-commands.md`, `app-sdk.md`, plan et index.
