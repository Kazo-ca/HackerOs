# ADR 0063 : composants d'app intégrables, enregistrés auprès de l'OS

## Statut

Accepté le 2026-10-11.

- **Contexte du plan :** après la refonte, demande de Benoit du 2026-10-11 :
  l'éditeur de code doit avoir un dock en bas avec des onglets (état,
  terminal, erreurs) et, pour le terminal, « réutiliser le contrôle de
  l'application de terminal. Terminal doit faire un register de son contrôle
  dans l'OS qui respecte une interface pour le contrôler depuis
  l'application ; set the current directory, read output, send commands ».
- **Complète** l'ADR 0047 (manifeste en classe C#) : `AppDefinition` et
  `AppManifest` gagnent `Components`.
- **Remplace** la phrase de l'ADR 0062 §1 sur le « panneau terminal » de
  l'éditeur : ce panneau devient un dock à onglets (Status, Terminal,
  Problems) et ses terminaux sont ceux de l'app Terminal, embarqués par
  contrat, plus des `TerminalPanel` référencés directement.
- **Respecte** l'ADR 0055 : l'éditeur n'utilise que le SDK public ; il ne
  référence plus `HackerOs.AppSdk.Terminal`.

## Contexte

Jusqu'ici, une app qui voulait un terminal référençait la bibliothèque
`HackerOs.AppSdk.Terminal` et dessinait elle-même un `TerminalPanel`. Le
terminal de l'éditeur et celui de la console avaient le même code, mais
aucun lien avec l'app Terminal : rien ne permettait de le piloter
(changer de dossier, lancer une commande, lire la sortie), et une autre app
aurait dû refaire la même chose.

Les bureaux Linux règlent ce cas par des composants enregistrés : KDE charge
la KPart de Konsole dans Kate ou Dolphin (l'app hôte ne connaît que
l'interface `TerminalInterface`), GNOME embarque le widget VTE. C'est ce
modèle qui est retenu.

## Décision

### 1. Une app enregistre des composants dans son manifeste

`AppDefinition.Components` liste des `AppComponent(Contract, Type)`. Le
manifeste généré porte `components: [{ contract, assembly, type }]`.

- Le **contrat** est un nom stable en mots minuscules séparés par des points
  (`hackeros.terminal`). Il désigne une interface du SDK (le contrôleur) et un
  enregistrement d'options.
- Seules les apps `Window` peuvent enregistrer des composants ; un contrat
  n'est enregistré qu'une fois par app ; le type vit dans l'assemblage du
  point d'entrée, que le chargement paresseux récupère déjà
  (`AppManifestValidator`).
- `components` est **omis** quand l'app n'en a pas (`null` dans
  `AppManifest`) : les manifestes existants gardent leurs octets.
- Le schéma, la liste blanche de `AppManifestJsonSerializer`, le canonique de
  test et le validateur sont mis à jour ensemble (common-pitfalls §2).

### 2. Le composant hérite de `HackEmbeddableComponent`

Le composant dérive de `HackEmbeddableComponent` (`HackerOs.AppSdk.Blazor.Components`),
qui dérive de `HackComponentBase`. Il reçoit :

- le contexte de la fenêtre **hôte** (paramètre en cascade `AppContext`) : il
  s'exécute dans le processus de l'hôte, avec ses capacités, comme une
  bibliothèque chargée dans le processus ;
- `Options` : l'enregistrement d'options du contrat, choisi par l'hôte ;
- `ControllerReady` : le rappel qui remet le contrôleur à l'hôte une fois le
  composant prêt.

### 3. L'OS trouve le fournisseur, l'hôte demande un contrat

`IHackComponentHost.ResolveAsync(contract)` (implémenté par
`HackComponentHost` dans `Platform.Blazor`, enregistré dans l'écosystème) :

1. liste les apps **installées** qui enregistrent le contrat, les apps système
   d'abord, puis par identifiant ;
2. récupère le descripteur de l'app avec
   `AppLifecycleOrchestrator.EnsureDescriptorAsync`, qui charge son assemblage
   comme un lancement ;
3. vérifie que le type existe, n'est pas abstrait et dérive de
   `HackEmbeddableComponent` ;
4. renvoie le type, ou la raison pour laquelle rien n'est disponible. Un
   fournisseur qui ne se charge pas laisse la place au suivant.

L'hôte affiche `<HackEmbeddedComponent TController="…" Contract="…">` : le
composant résout le contrat, rend le composant fourni et convertit le
contrôleur reçu en `TController`. Sans fournisseur (app Terminal
désinstallée, hôte de test), il affiche la raison ou le fragment
`Unavailable`.

### 4. Le premier contrat : `hackeros.terminal`

`HackComponentContracts.Terminal` désigne `IHackTerminal` et
`HackTerminalOptions` (`HackerOs.AppSdk.Components`). L'app Terminal
l'enregistre avec `TerminalEmbed`, qui rend le même `TerminalPanel` que sa
fenêtre ; `TerminalPanel` implémente `IHackTerminal` :

| Membre | Ce qu'il fait, comme le ferait une personne au clavier |
| --- | --- |
| `RunCommandAsync(ligne)` | La ligne est mise dans la file d'entrée du terminal (après les touches déjà tapées), affichée à l'invite, exécutée ; la ligne en cours de saisie est mise de côté puis rendue (comme le push-line de zsh). Renvoie `$?` et la sortie, sans séquences de contrôle. |
| `SetWorkingDirectoryAsync(chemin)` | Lance un `cd` visible (chemin cité si besoin). |
| `SendInputAsync(texte)` | Les octets du clavier : texte, `\r` pour Entrée, `\u0003` pour Ctrl+C. |
| `ReadOutput(max)` | Les 256 derniers Kio affichés : lignes validées, sorties des commandes, messages du terminal ; pas les redessins de la ligne. |
| `ClearAsync`, `FocusAsync` | `clear`, focus. |
| `WorkingDirectory`, `Title`, `IsRunning`, `IsBusy` | État du shell. |
| `OutputReceived`, `CommandCompleted`, `StateChanged`, `Exited` | Événements, levés sur le thread de rendu. |

Une commande interrompue (Ctrl+C ou jeton annulé) finit avec le statut 130,
comme dans bash.

### 5. Le dock de l'éditeur

L'éditeur remplace son panneau terminal par un dock à onglets :

- **Status** : faits sur l'éditeur (dossier, fichier, langage, taille, onglets,
  terminaux) et journal horodaté des messages (200 au plus) ; les erreurs
  restent aussi affichées au-dessus de l'éditeur, les autres messages passent
  dans la barre d'état ;
- **Terminal** : un `hsh` par onglet, embarqué par contrat ; « Open in
  Integrated Terminal » dans l'explorateur déplace le terminal actif par un
  `cd` visible, ou en ouvre un nouveau s'il est occupé ;
- **Problems** : vide pour l'instant (pas de diagnostic sans compilation).

Le dock est caché, pas détruit, quand on le ferme : ses shells continuent.
Ctrl+J l'affiche ou le cache ; Ctrl+` va à l'onglet Terminal.

## Conséquences

- Une app tierce peut embarquer un terminal par le SDK public, avec la
  capacité `shell.host` (le shell est son enfant).
- Désinstaller l'app Terminal retire le terminal de l'éditeur, qui affiche
  pourquoi : c'est le comportement d'un composant fourni par une autre app.
- D'autres contrats pourront suivre (visionneuse d'images, vue de fichiers)
  sans changer ce mécanisme.

## Alternatives écartées

- **Référencer `HackerOs.AppSdk.Terminal` dans chaque app** (l'existant) : pas
  de pilotage, et rien n'est enregistré auprès de l'OS.
- **Un service DI global par composant** : il faudrait enregistrer chaque
  composant dans l'écosystème et charger toutes les apps au démarrage ; le
  manifeste garde le chargement paresseux.
- **`DynamicComponent`** : ses annotations de découpage échouent au build
  (IL2111) ; `HackEmbeddedComponent` ouvre le composant lui-même.
