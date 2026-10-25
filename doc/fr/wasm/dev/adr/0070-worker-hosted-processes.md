# ADR 0070 : processus sans interface dans des Web Workers

## Statut

Accepté le 2026-10-16.

- **Phase du plan :** phase 5 — exécution des apps
  ([`execution-isolation-plan.md`](../plans/execution-isolation-plan.md)). Dernier
  chantier de la phase : il dépend des ADR 0067 à 0069.
- **Lève :** l'exclusion « isolation d'exécution (watchdog, Web Workers) » de la
  section 9 de [`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md).

## Contexte

Une boucle synchrone infinie sur le thread principal gèle tout l'OS, et rien sur
ce thread ne peut l'interrompre (ADR 0067 §4). Le seul moyen d'isoler un
processus est de l'exécuter **ailleurs** : un Web Worker, qui peut être arrêté
de force par `worker.terminate()`.

Un Web Worker fait tourner son propre runtime .NET :

- environ 15 à 40 Mo et 0,5 à 2 s de démarrage à froid, même avec les fichiers
  en cache ;
- pas d'accès au DOM : les apps à fenêtre restent sur le thread principal ;
- tout ce que l'OS possède (fichiers, `/proc`, presse-papiers, réseau) passe par
  des messages.

Un worker par processus rendrait le shell inutilisable (`ls | grep | wc`
démarrerait trois runtimes). Un pool seul ferait tomber les voisins à chaque
kill forcé. Benoit a validé un modèle hybride, rendu sûr par l'escalade de
l'ADR 0069.

## Décision

### 1. Qui tourne où

| Genre de processus | Hôte d'exécution |
|---|---|
| App à fenêtre, composant embarqué | thread principal (inchangé) |
| Commande, pipeline, script shell | worker du **pool** |
| Service, tâche longue déclarée | worker **dédié** |

Le manifeste déclare l'hôte voulu :
`[AppManifest(Execution = AppExecutionHost.Pool | Dedicated | MainThread)]`.
Défaut : `MainThread` pour une app à fenêtre, `Pool` pour une commande,
`Dedicated` pour un service (`ServiceAppBase`). Une app peut garder
`MainThread` si elle a besoin d'un service que le worker n'offre pas.

### 2. Le pool : un worker par cœur virtuel

- Le pool compte `CpuCapacity` workers (`VirtualHardwareProfile`, arrondi au
  supérieur, au moins 1, au plus `navigator.hardwareConcurrency - 1`). Ce sont
  les **cœurs** de la machine simulée : `/proc/cpuinfo` et `/proc/stat` les
  listent, et l'ADR 0067 compte leur temps.
- Ils démarrent au boot, en arrière-plan, après l'ouverture de session.
- L'ordonnanceur de l'OS place chaque nouveau processus sur le worker le moins
  chargé. Les étapes d'un même pipeline vont sur le **même** worker, pour que
  leurs tubes restent en mémoire.

### 3. Workers dédiés

- Un worker de réserve, déjà démarré, est gardé prêt : un processus dédié démarre
  sans attente, et l'OS relance une réserve derrière lui.
- Un processus dédié qui se termine libère son worker.

### 4. L'OS vu depuis un worker

- Le worker charge les assemblies du SDK et de l'app, et un **hôte de
  processus** (`HackerOs.Platform.WorkerHost`) qui implémente
  `IAppExecutionContext` par des **passerelles distantes** : chaque appel du
  SDK devient un message vers l'OS sur le thread principal, qui applique les
  mêmes contrôles de capacités qu'aujourd'hui. Le SDK ne change pas : une
  commande ne sait pas où elle tourne.
- Les flux standard (stdin, stdout, stderr) sont des tubes par messages.
- Le JavaScript (création du worker, `postMessage`, `terminate`) est un module
  dans le `wwwroot/` de `HackerOs.Infrastructure.Browser`, importé par le code
  C#. Aucun hôte ne le référence (AGENTS.md).

### 5. Battement de cœur et escalade

- Chaque worker envoie un battement de cœur toutes les 250 ms, avec ses temps
  CPU et la mémoire de son runtime (ADR 0067 et 0068).
- Sans battement depuis `UnresponsiveThresholdMs`, les processus actifs du worker
  passent **Not responding** (ADR 0067 §4), visible **pendant** le blocage.
- L'arrêt d'un processus suit l'ADR 0069 §3. À l'étape 4 (worker muet après le
  délai de grâce), l'OS termine le worker :
  - les autres processus de ce worker passent à `Exited (Killed)` avec un
    message dans `/var/log/syslog` (« killed: worker 2 terminated to stop pid
    42 ») et leur shell affiche `Killed` ;
  - un worker du pool est relancé à sa place.
- Un worker termine son processus dédié de la même manière, sans voisin.

### 6. Hors navigateur

`HackerOs.Server` (rendu Interactive Server) n'a pas de Web Worker .NET : tous
les processus y restent dans le processus serveur. `AppExecutionHost` y est
ignoré, avec une ligne d'information dans le journal. Cela pourra changer dans
un ADR propre à cet hôte.

## Conséquences

- Une commande ou un service qui boucle ne gèle plus l'OS, et peut toujours
  être tué.
- Démarrer le pool coûte environ `CpuCapacity` × 15 à 40 Mo de mémoire du
  navigateur.
- Le prix est une couche de passerelles distantes, à maintenir à parité avec les
  passerelles locales. Un test de contrat exécute les mêmes scénarios du SDK
  dans les deux modes.
- La première version peut limiter le pool aux commandes intégrées
  (`HackerOs.Commands`), puis l'ouvrir aux autres apps.

## Références

- [`execution-isolation-plan.md`](../plans/execution-isolation-plan.md)
- ADR 0012, 0050, 0054 (shell détaché), 0055, 0067, 0068, 0069.
