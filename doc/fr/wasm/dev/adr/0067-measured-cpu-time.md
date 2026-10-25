> [English version](../../../../en/wasm/dev/adr/0067-measured-cpu-time.md)

# ADR 0067 : temps CPU mesuré par processus et état « Not responding »

## Statut

Accepté le 2026-10-16.

- **Phase du plan :** phase 5 — exécution des apps
  ([`execution-isolation-plan.md`](../plans/execution-isolation-plan.md)).
- **Optionnel :** interrupteur `HackerOsCpuAccounting` /
  `HackerOs:ResourceAccounting:Cpu:Enabled` (ADR 0066).
- **S'appuie sur :** ADR 0043 (contexte d'exécution ambiant), ADR 0057
  (`/proc/stat`, `CpuTimeAccounting`, `ResourceSamplingService`).

## Contexte

En Blazor WebAssembly, toutes les apps partagent **un seul thread**. Le
navigateur ne donne aucun temps CPU par app. Mais à chaque instant, un seul
processus exécute du code : si l'OS sait lequel, il peut chronométrer le temps
réellement passé dans chacun, comme un noyau qui compte le temps à chaque
changement de contexte.

L'OS sait déjà qui s'exécute : `HackExecutionContext` porte le processus
courant dans un `AsyncLocal` (ADR 0043), positionné par l'OS au lancement, et
par `AmbientComponentDispatch` pour les événements et le rendu des composants.
L'`AsyncLocal` suit le code dans ses continuations asynchrones.

Benoit veut **mesurer**, pas contraindre : pas de quantum imposé, pas d'arrêt
automatique. Un processus qui monopolise le thread est seulement signalé
« Not responding » dans le gestionnaire de tâches.

## Décision

### 1. Les changements de contexte comme points de mesure

Le frame ambiant de `HackExecutionContext` utilise le constructeur
`AsyncLocal<T>(Action<AsyncLocalValueChangedArgs<T>>)`. Le runtime appelle ce
rappel à chaque fois que la valeur change, y compris quand il **restaure un
`ExecutionContext`** au début ou à la fin d'une continuation
(`ThreadContextChanged == true`). C'est l'équivalent exact d'un changement de
contexte :

- passage au processus *P* : l'horloge de *P* démarre ;
- sortie de *P* : le temps écoulé s'ajoute au temps CPU de *P*.

L'horloge est `Stopwatch.GetTimestamp()` (`performance.now()` dans le
navigateur). Aucun `TaskScheduler` ni aucune boucle n'est remplacé : le rappel
suffit, et il couvre les événements, les minuteries, les continuations de
`Task` et le code lancé par l'OS.

Le rappel n'est installé que si la mesure du CPU est active (ADR 0066) : sinon
le frame est un `AsyncLocal` ordinaire, sans aucun coût ajouté.

### 2. Temps utilisateur et temps système

- **`utime`** : temps passé dans le code de l'app (frame ambiant de son
  processus).
- **`stime`** : temps passé dans les passerelles de l'OS appelées par l'app
  (fichiers, réseau, presse-papiers). Les passerelles ouvrent un sous-frame
  « noyau » du même processus ; le temps y est compté à part.
- Le temps hors de tout frame (rendu Blazor, boucle de l'OS, minuteries
  système) est imputé au processus de l'interface graphique de la session (le
  « compositeur »), comme Xorg ou `gnome-shell` sous Linux. Le reste est du
  temps `idle`.

### 3. Où le temps apparaît

- `/proc/<pid>/stat` (nouveau fichier, format Linux : `utime` et `stime` en
  `USER_HZ`, champs 14 et 15) ;
- `/proc/<pid>/status` : `Cpu` devient l'usage mesuré sur la dernière seconde,
  avec `CpuSource: measured` (ADR 0066) ;
- `/proc/stat` : `CpuTimeAccounting` (ADR 0057) reçoit les vrais temps `user`,
  `system` et `idle` au lieu des valeurs simulées ;
- `HackProcess.TotalProcessorTime`, `UserProcessorTime`,
  `PrivilegedProcessorTime`, calqués sur `System.Diagnostics.Process`.

Le cœur virtuel est unique tant que les processus tournent sur le thread
principal. Les processus en Web Worker (ADR 0070) ajoutent leurs cœurs.

### 4. « Not responding »

Le rappel connaît la durée de chaque tranche. Une tranche de plus de **50 ms**
est une *long task* (seuil du navigateur) et compte dans un compteur par
processus (`nonvoluntary_ctxt_switches` reste à zéro : rien n'est préempté).

Un processus est **Not responding** quand une seule tranche dure plus de
`UnresponsiveThresholdMs` (5 s par défaut, ADR 0066). Sur le thread principal,
cet état n'est visible **qu'après coup**, à la fin de la tranche, puisque rien
d'autre ne s'exécute pendant ; il reste affiché tant que le processus n'a pas
répondu à un nouvel événement en moins du seuil. Dans un Web Worker (ADR 0070),
l'OS le voit **pendant** le blocage, grâce au battement de cœur du worker.

L'OS ne fait **rien** de plus : pas de fenêtre « Attendre / Forcer à quitter »,
pas d'arrêt. Le gestionnaire de tâches affiche « Not responding » dans la
colonne d'état, et `/proc/<pid>/status` ajoute `Responding: 0|1`.

### 5. Coût et garde-fous

- Le rappel ne fait que lire l'horloge et additionner dans un tableau indexé
  par processus : pas d'allocation, pas de verrou sur le thread principal.
- Un test de performance compare le temps d'un scénario de référence (ouvrir
  dix fenêtres, taper dans l'éditeur, pipeline shell) avec et sans la mesure ;
  un écart de plus de 5 % fait échouer le test.
- Les compteurs vivent en mémoire et repartent de zéro au démarrage.

### 6. Hôte serveur

Sur `HackerOs.Server` (rendu Interactive Server), plusieurs threads exécutent
du code en même temps : le rappel reste juste par thread, mais le temps
`idle` et le compositeur n'ont plus de sens simple. La mesure y est coupée par
défaut (ADR 0066) jusqu'à un ADR propre à cet hôte.

## Conséquences

- Le CPU affiché devient réel pour toutes les apps, sans qu'elles changent une
  ligne.
- Une boucle synchrone infinie n'est **pas** interrompue : elle gèle encore
  l'OS sur le thread principal. Seul un Web Worker (ADR 0070) l'isole.
- `system-metrics.md` et `task-manager.md` documentent la mesure.

## Références

- [`execution-isolation-plan.md`](../plans/execution-isolation-plan.md)
- `Shared/HackerOs.AppSdk/HackExecutionContext.cs`,
  `Shared/HackerOs.AppSdk.Blazor/AmbientComponentDispatch.cs`,
  `Platform/HackerOs.Platform.Core/FileSystem/Virtual/ProcFileSystemProvider.cs`.
- `proc(5)` : `/proc/<pid>/stat`, `/proc/stat`.
- ADR 0043, 0057, 0066, 0068, 0070.
