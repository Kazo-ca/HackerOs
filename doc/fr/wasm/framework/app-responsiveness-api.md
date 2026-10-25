# Priorité et réactivité des apps

Ce document décrit ce qu'une app voit et doit faire pour rester réactive : la priorité `nice` et la fenêtre « Ne répond pas ». La mise en œuvre (détection, file des workers, architecture, tests) est dans [`../dev/process-priority-and-unresponsive-windows.md`](../dev/process-priority-and-unresponsive-windows.md).

## Priorité `nice`

La gentillesse (`nice`) d'un processus va de `-20` à `19`, vaut `0` par défaut et est héritée par ses enfants. Elle ne fait qu'ordonner la file d'attente des workers : elle ne ralentit jamais un processus qui tourne.

### API

- `HackProcess.Nice` (`int`, lecture seule) : gentillesse du processus, lue dans `/proc/<pid>/stat` (champ 19).
- `HackProcess.BasePriority` (`int`, lecture seule) : priorité du processus, `20 + nice` (champ 18 de `/proc/<pid>/stat`).
- `HackProcess.ParseStatNice(string)` : extrait la gentillesse d'une ligne `/proc/<pid>/stat`.
- `IAppProcessGateway.GetNice` / `SetNice` (`Simulation.Abstractions`, via `IAppExecutionContext.Processes`) : lire ou changer la gentillesse d'un processus. Monter la valeur se fait sur son propre processus ; la baisser, ou changer un autre utilisateur, demande root. Changer un processus qui n'est ni le sien ni un de ses enfants demande `process.manage` (ADR 0140).
- `HackBackgroundOptions.Nice` : gentillesse d'une tâche lancée par `HackBackground.RunAsync`, `0` par défaut. Une valeur négative est ramenée à `0` pour un utilisateur qui n'est pas administrateur.

### Règles

| Règle | Détail |
| --- | --- |
| Plage | `-20`..`19`, toute valeur hors plage est ramenée aux bornes. |
| Héritage | Un enfant prend la valeur de son parent, sauf si la demande de lancement en donne une (`ProcessStartRequest.Nice`, `AppLaunchRequest.Nice`). |
| Baisser | Seul root (`sudo`, ADR 0042) baisse une valeur (`EACCES` sinon : « Permission denied »). |
| Autre utilisateur | Seul root change la valeur d'un processus d'un autre utilisateur (`EPERM` : « Operation not permitted »). |
| Capacités | Lire un autre processus demande `process.list` ; changer un processus qui n'est ni le sien ni un de ses enfants demande `process.manage`, comme `kill` (ADR 0140). |
| Effet | Ordre de la file des workers (ADR 0071), commandes et tâches de fond `HackBackground.RunAsync` (ADR 0128, valeur `HackBackgroundOptions.Nice`, 0 par défaut) : la plus petite valeur d'abord, puis l'ordre d'arrivée. |
| Worker toujours prêt | Une commande à valeur **positive** ne prend pas le worker toujours prêt pendant qu'un autre worker démarre : il reste pour la prochaine commande interactive. |
| Thread principal | **Aucun effet.** Un processus du thread principal (fenêtres, commandes `MainThread`) tourne quand il est appelé ; il n'y a pas d'ordonnanceur à pondérer. |

## Fenêtre qui ne répond pas

### Ce que voit l'utilisateur

- Le titre affiché prend le suffixe « (Ne répond pas) » / « (Not responding) »,
  dans la langue de la session. C'est **de l'affichage** : le titre posé par
  l'app (`SetWindowTitleAsync`) ne change pas et aucun
  `WindowTitleChangedEvent` n'est émis.
- Le contenu passe en gris (`filter: grayscale(1) brightness(0.7)`), en fondu
  sauf sous « réduire les animations » (ADR 0105).
- La boîte est attachée **à cette fenêtre seulement** : elle couvre son
  contenu, pas sa barre de titre ; les autres fenêtres et la barre des tâches
  restent utilisables. `role="alertdialog"`, le focus va sur **Attendre**,
  `Tab` reste entre les deux boutons, `Échap` vaut **Attendre**.
- **Attendre** ferme la boîte ; elle revient un seuil plus tard (5 s) si la
  fenêtre ne répond toujours pas.
- **Terminer l'application** suit l'escalade de l'ADR 0069 : raison
  `Terminated`, délai de grâce de **2 s**, puis arrêt forcé (étape 3) et, pour
  un worker, terminaison du worker (étape 4). La fenêtre se ferme quand son
  processus se termine.
- La croix (et `Alt+F4`) d'une fenêtre sans réponse ouvre la même boîte au lieu
  d'une fermeture que l'app gelée ne pourrait pas traiter.
### Ce que la boîte ne peut pas faire

Une boucle **qui ne rend jamais la main** sur le thread principal gèle tout
l'OS : ni la boîte ni rien d'autre ne peut s'afficher, et c'est le navigateur
qui finit par proposer sa propre boîte « Page sans réponse ». Le travail long
doit partir dans un worker (`HackBackground.RunAsync`, ADR 0128, à venir) ou
rendre la main régulièrement avec `HackProcess.CheckpointAsync` (ADR 0069) ;
voir [`cpu-accounting.md`](../dev/cpu-accounting.md) et
[`sdk-developer-guide.md`](sdk-developer-guide.md).
