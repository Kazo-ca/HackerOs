> [English version](../../../en/wasm/dev/process-priority-and-unresponsive-windows.md)

# Priorité `nice` et fenêtres qui ne répondent pas

Ce document couvre la mise en œuvre de la priorité `nice` et de la détection des fenêtres qui ne répondent pas. Ce qu'une app voit et doit faire (API `HackProcess.Nice`, règles de la priorité, boîte « Ne répond pas », limites d'une boucle sur le fil principal) est dans [`../framework/app-responsiveness-api.md`](../framework/app-responsiveness-api.md).

Mise en œuvre de l'[ADR 0127](adr/0127-priority-and-not-responding-dialog.md),
chantier `exec/4-not-responding-ui` de la série « suites de l'étape 5 »
([`execution-followups-plan.md`](plans/execution-followups-plan.md)).

## Objet

Deux comportements d'un vrai système, simulés sans quantum de temps :

1. **La priorité `nice`** : chaque processus a une gentillesse de `-20` à `19`
   (0 par défaut), héritée par ses enfants, que l'on choisit avec `nice` et
   change avec `renice`. Elle ne fait **qu'ordonner la file d'attente des
   workers** ; elle ne ralentit jamais un processus qui tourne.
2. **La fenêtre qui ne répond pas** : quand une app ne répond plus, sa fenêtre
   prend le suffixe « (Ne répond pas) », son contenu passe en gris et une boîte
   « « Calculatrice » ne répond pas. Vous pouvez attendre qu'elle réponde ou
   terminer l'application. » propose **Attendre** ou **Terminer l'application**,
   comme Windows (« Attendre / Fermer le programme ») ou GNOME (« Forcer à
   quitter / Attendre »).

## 1. Priorité `nice`

### Ce que voit l'utilisateur

```sh
$ nice                      # la gentillesse du shell
0
$ nice -n 10 find / -name '*.log'   # lance find à 10
$ nice -n -5 make           # sans sudo : refusé, lancé à la valeur courante
nice: cannot set niceness: Permission denied
$ sudo nice -n -5 make      # ADR 0136 : make tourne en root à -5
$ renice -n 5 -p 42
42 (process ID) old priority 0, new priority 5
$ ps -l
F S UID   PID  PPID  C PRI  NI     TIME CMD
0 R alice   1     0  0  80   0 00:00:00 org.hackeros.terminal
$ ps -o pid,ni,comm
```

- `ps -l` et `ps -o … ni …` affichent `NI` et `PRI` (`80 + nice`, comme
  procps) ; `top` et `htop` affichent `PR` (`20 + nice`) et `NI`.
- `/proc/<pid>/stat` porte la priorité (`20 + nice`) au champ 18 et la
  gentillesse au champ 19, comme `proc(5)`.
- Le gestionnaire de tâches a une colonne **Priorité**, masquée par défaut, que
  le bouton « Priorité » de la barre d'outils de l'onglet Processus affiche.

Les règles de la priorité (plage, héritage, droits, effet sur la file des workers, thread principal) sont dans [`../framework/app-responsiveness-api.md`](../framework/app-responsiveness-api.md).

### Architecture

| Élément | Projet | Rôle |
| --- | --- | --- |
| `ProcessNice` | `Shared/HackerOs.Simulation.Abstractions` | Bornes, `Clamp`, `ToPriority` (`20 + nice`). |
| `IProcessManager.GetNice/SetNice`, `ProcessStartRequest.Nice` | idem | Membres par défaut (0 / non pris en charge) pour les tables de processus qui n'en ont pas. |
| `InMemoryProcessManager` | `Platform/HackerOs.Platform.Core` | Garde la valeur dans l'entrée du processus, l'hérite du parent. |
| `IAppProcessGateway.GetNice/SetNice`, `AppProcessGateway` | Abstractions / Core | Contrôles de `setpriority(2)` (root, propriétaire, capacités). |
| `IShellSession.RunNicedAsync`, `ShellInterpreter.RunNicedAsync` | AppSdk / Core | Lance une commande à la gentillesse demandée (enfant du processus `nice`). |
| `nice`, `renice` | `Apps/Commands/HackerOs.Commands` | Commandes (thread principal : elles ont besoin du shell ou de la table des processus). |
| `ps`, `top`, `htop` | idem | Colonnes `NI` et `PRI`/`PR`. |
| `ProcFileSystemProvider` | Core | Champs 18 et 19 de `/proc/<pid>/stat`. |
| `HackProcess.Nice`, `HackProcess.BasePriority`, `HackProcess.ParseStatNice` | `Shared/HackerOs.AppSdk` | Lus depuis `/proc/<pid>/stat`. |
| `WorkerRunQueue` | `Platform/HackerOs.Platform.WorkerHost` | File des commandes en attente d'un worker, ordonnée par gentillesse puis arrivée. |
| `ProcessTable.ShowPriority`, `ProcessColumn.Priority` | `Apps/System/HackerOs.Apps.SystemMonitor` | Colonne Priorité masquée par défaut. |

## 2. Fenêtre qui ne répond pas

### Quand une fenêtre est jugée sans réponse

Toutes les fenêtres d'app partagent le thread principal du navigateur avec le
gestionnaire de fenêtres. Pendant qu'une app le tient, **rien d'autre ne
s'exécute**, pas même la détection : le verdict tombe dès que le thread est
rendu, à partir de ce que le comptable CPU a mesuré (ADR 0067) et du temps
d'attente des entrées de l'utilisateur. Une fenêtre est sans réponse dans l'un
des trois cas de l'ADR 0127 §2 (seuil `UnresponsiveThresholdMs`, 5 s) :

1. son processus est « Not responding » pour le comptable (une tranche plus
   longue que le seuil) **et** a encore consommé du CPU depuis le dernier
   échantillon (une tranche de 5 s qui vient de finir, ou un processus de worker
   bloqué dont le battement de cœur continue de compter) ;
2. une entrée (clic, touche) livrée à la fenêtre a attendu plus que le seuil
   alors que le processus de cette fenêtre tenait le thread pendant au moins la
   moitié de cette attente ;
3. une tranche plus longue que le seuil vient de finir et l'utilisateur a agi
   sur la fenêtre pendant cette tranche : la boîte s'affiche à la fin de la
   tranche.

L'état prend fin quand la fenêtre traite à nouveau une entrée sous le seuil,
quand le processus termine une tranche courte, ou quand il est resté **inactif**
pendant tout un seuil (un processus inactif répond aussitôt). Une app qui a
gelé l'OS 6 s puis s'est remise montre donc la boîte quelques secondes, puis
celle-ci disparaît d'elle-même, comme la fenêtre fantôme de Windows quand l'app
recommence à traiter ses messages.

Sans mesure du CPU (`Cpu:Enabled = false`), aucune attente ne peut être
attribuée à un processus : aucune fenêtre n'est marquée.

### Ce que voit l'utilisateur (fenêtre)

Le suffixe « (Ne répond pas) », le contenu grisé, la boîte « Attendre | Terminer l'application » et la croix sont décrits dans [`../framework/app-responsiveness-api.md`](../framework/app-responsiveness-api.md).

### Réglage

```ini
# /etc/hackeros/window-manager.config (et ~/.config/hackeros/window-manager.config)
[unresponsive]
dialog = true
```

`dialog = false` coupe l'ouverture automatique de la boîte : le suffixe et le
gris restent, et la croix ouvre encore la boîte. Le fichier système est créé
au premier démarrage avec la valeur commentée ; un administrateur peut figer la
clé avec `[locked]` (ADR 0044). Le seuil reste celui de la mesure CPU
(`HackerOs:ResourceAccounting:Cpu:UnresponsiveThresholdMs`).

### Architecture

| Élément | Projet | Rôle |
| --- | --- | --- |
| `WindowRuntimeState.IsResponding`, `SetWindowRespondingCommand`, `WindowRespondingChangedEvent` | `Platform/HackerOs.Windowing.Abstractions` / `Core` | État d'affichage de la fenêtre, comme le drapeau « hung » d'un gestionnaire de fenêtres. |
| `WindowChrome` | `Platform/HackerOs.Windowing.Blazor` | Suffixe localisé (`Chrome.NotResponding`). |
| `WindowHost.Overlay`, `DesktopArea.WindowOverlay`, `SingleSurfaceArea.WindowOverlay` | idem | Calque posé sur le contenu d'**une** fenêtre ; classe `is-not-responding` (gris), `aria-busy`. |
| `WindowResponsivenessMonitor` | `Platform/HackerOs.Platform.Blazor/Windows` | Les trois cas, la fin de l'état, Attendre, Terminer, la croix ; sans minuterie ni JavaScript, testé avec une horloge factice. |
| `WindowResponsivenessHost` (+ `.razor.js`) | idem | Échantillonne le moniteur chaque seconde et lui transmet l'attente des entrées (`performance.now() - event.timeStamp`), mesurée en phase de capture sur `.window-content`. |
| `UnresponsiveWindowDialog` (+ `.razor.css`, `.razor.js` pour le piège du focus), `UnresponsiveWindowOverlay` | idem | La boîte et son branchement dans le calque de chaque fenêtre. |
| `WindowManagerSettingsDocuments` | `Platform/HackerOs.Platform.Core/Shell` | Document `window-manager` (`[unresponsive] dialog`). |
| `DesktopShell`, `MobileShell` | `Platform/HackerOs.Platform.Blazor/Shell` | Posent le calque et l'hôte ; la croix passe par `InterceptClose`. |

Les fenêtres détachées de l'ADR 0078 n'hébergent aujourd'hui que des onglets
du navigateur (iframes), pas des fenêtres d'app : la boîte vit dans le
composant de la fenêtre, et une future surface détachée qui hébergerait une app
la rendra dans sa propre surface avec le même calque.

## Décisions de mise en œuvre

- **Pas de quantum** : la gentillesse ordonne une file, rien de plus
  (ADR 0127 §1). Le thread principal n'a pas d'ordonnanceur à pondérer.
- **`nice` sur une commande qui pilote son shell** (`nice watch …`,
  `nice nice`) : la commande reçoit le shell comme si elle était lancée
  directement (ADR 0051 §3). Les intégrés spéciaux (`cd`, `export`…) restent
  introuvables pour `nice`, comme sous GNU.
- **`sudo nice -n -5 …`** (ADR 0136, réponse de Benoit) : `sudo` accepte
  `nice`, seule exception aux commandes qui pilotent le shell. `nice` tourne
  sous l'utilisateur et passe le ticket root de `sudo` à sa commande, qui
  tourne en root et peut recevoir une valeur plus basse ; le ticket reste à
  usage unique. `sudo renice` marche toujours pour un processus déjà lancé.
- **`renice` sur le thread principal** : un worker n'a pas encore de passerelle
  vers les priorités de la table des processus (ADR 0072).
- **Attribution par le CPU mesuré** : une attente n'est imputée à une fenêtre
  que si son propre processus a consommé au moins la moitié de cette attente ;
  un service ou une autre app qui gèle le thread ne fait pas accuser la fenêtre
  qui a reçu le clic.
- **Drapeau collant du comptable** : le « Not responding » de l'ADR 0067 reste
  levé jusqu'à la prochaine tranche courte. Le moniteur ne le suit que s'il est
  accompagné de CPU consommé, et retient le nombre de tranches déjà
  « acquittées » par une entrée rapide, pour qu'un vieux drapeau ne remarque
  pas une fenêtre qui répond.
- **La fenêtre se ferme par son processus** : Terminer ne force pas la
  fermeture de la fenêtre ; c'est la fin du processus qui la ferme, comme pour
  « Fin de tâche » du gestionnaire de tâches.

## Tests

| Projet | Couverture |
| --- | --- |
| `HackerOs.Platform.Core.Tests` (`InMemoryProcessManagerTests.Nice`, `ProcFileSystemProviderTests`) | Valeur 0, héritage, valeur explicite, bornes, processus terminé, règles root/propriétaire/capacités de la passerelle, champs 18 et 19 de `stat`. |
| `HackerOs.Platform.WorkerHost.Tests` (`WorkerRunQueueTests`, `CommandParityTests`) | Ordre de la file (gentillesse puis arrivée, bornes), worker toujours prêt, parité de `nice`, `renice` et `ps -o` dans un worker. |
| `HackerOs.Commands.Wave5.Tests` (`PsCommandTests`) | `ps -l` (`PRI` 85 pour `nice` 5), `ps -o pid,ni,comm`, champ inconnu. |
| `HackerOs.AppSdk.Tests` (`HackProcessNiceTests`) | Lecture du champ 19, nom avec parenthèses, ligne courte ou invalide, bornes. |
| `HackerOs.Apps.SystemMonitor.Tests` (`ProcessTableTests`) | Colonne Priorité masquée par défaut, tri, retour au tri CPU quand on la masque. |
| `HackerOs.Windowing.Core.Tests` (`WindowRespondingTests`) | Drapeau, événement unique, titre inchangé, persistance jusqu'à l'effacement. |
| `HackerOs.Platform.Blazor.Tests` (`WindowResponsivenessMonitorTests`) | Les trois cas avec horloge factice, vieux drapeau ignoré, attente imputée à un autre processus, fin par entrée rapide, tranche courte ou inactivité, Attendre puis retour, croix, Terminer (2 s, `Terminated`), sans mesure, fenêtre sans processus. |
| `HackerOs.E2E.Tests` (`IndexedDbBrowserContractTests.Unresponsive`, scénario `?scenario=unresponsive` du harnais navigateur) | Dans Chromium : suffixe, gris, boîte, focus sur Attendre, `Tab` piégé, axe sans violation grave, `Échap`, retour de la boîte, croix, Terminer ; vrai gel du thread principal avec un clic en attente (cas 3) puis fin de l'état. |

## Liste des tâches

- [x] Gentillesse dans la table des processus : plage, héritage, `GetNice`/`SetNice`.
- [x] Passerelle des processus : contrôles root, propriétaire et capacités.
- [x] `nice` et `renice` (en/fr), `IShellSession.RunNicedAsync`.
- [x] `ps -l`, `ps -o`, `top`, `htop` : `NI` et `PRI`/`PR`.
- [x] `/proc/<pid>/stat` champs 18 et 19 ; `HackProcess.Nice`/`BasePriority`.
- [x] File des workers ordonnée par gentillesse (`WorkerRunQueue`), worker toujours prêt laissé aux commandes interactives.
- [x] Colonne Priorité masquée par défaut dans le gestionnaire de tâches.
- [x] État `IsResponding` de la fenêtre, commande et événement.
- [x] Suffixe localisé, contenu grisé (sans fondu sous réduction des animations).
- [x] Moniteur des trois cas, sonde des entrées, boîte Attendre | Terminer, croix.
- [x] Réglage `[unresponsive] dialog` de `window-manager.config`.
- [x] Tests unitaires, parité, navigateur et accessibilité.
- [x] Documentation (`cpu-accounting.md`, `window-model.md`, `task-manager.md`, `linux-commands.md`, `worker-processes.md`, `virtual-proc-and-dev.md`, `settings-files.md`, guide et référence du SDK) et index.
- [ ] Tâches de fond de l'ADR 0128 ordonnées par la même file (chantier `exec/5`).
