# Temps CPU mesuré par processus et « Not responding »

Mise en œuvre du chantier `rebuild/5-cpu-accounting` de la phase 5
([`execution-isolation-plan.md`](plans/execution-isolation-plan.md)) :
[ADR 0067](adr/0067-measured-cpu-time.md). Il s'appuie sur les interrupteurs et
le crochet de changement de contexte de l'ADR 0066
([`resource-accounting.md`](resource-accounting.md)).

## Rôle

- Chronométrer le temps que chaque processus passe réellement sur le thread
  du navigateur : `utime` dans son propre code, `stime` dans les passerelles de
  l'OS qu'il appelle (fichiers, réseau, presse-papiers, espace disque).
- Imputer le temps hors de tout processus au compositeur de la session (rendu
  Blazor, boucle de l'OS) ou au temps `idle`.
- Signaler les *long tasks* (tranche de plus de 50 ms) et l'état « Not
  responding » (une tranche de plus de `UnresponsiveThresholdMs`), sans jamais
  rien interrompre.
- Exposer ces mesures là où Linux les expose : `/proc/<pid>/stat`,
  `/proc/<pid>/status`, `/proc/stat`, et `HackProcess` comme
  `System.Diagnostics.Process`.

Aucune app ne change une ligne : tout passe par le frame ambiant que l'OS ouvre
déjà autour de leur code (ADR 0043).

## Architecture

| Élément | Projet | Rôle |
|---|---|---|
| `ProcessCpuTimes`, `MeasuredThreadCpuTimes`, `ICpuTimeAccountant.TryGetProcessTimes` / `TryGetThreadTimes` | `Shared/HackerOs.Simulation.Abstractions` (`Processes/ResourceAccountingContracts.cs`) | contrats lus par `/proc` et `CpuTimeAccounting` |
| `ExecutionContextSwitch.FromKernel` / `ToKernel`, `IExecutionModeSwitchObserver` | idem | mode (utilisateur ou noyau) du processus quitté et du processus entré ; changement de mode sans changement de processus |
| `HackExecutionContext.EnterKernel` (interne) | `Shared/HackerOs.AppSdk` | sous-frame « noyau » du processus courant ; ne fait rien sans crochet |
| `AppFileSystemGateway`, `AppNetworkGateway`, `AppClipboardGateway`, `AppDriveGateway` | `Platform/HackerOs.Platform.Core` | ouvrent le sous-frame noyau autour de chaque appel |
| `MeasuredCpuTimeAccountant` | `Platform/HackerOs.Platform.ResourceAccounting` | le comptable : tranches, `utime`/`stime`, compositeur, `idle`, long tasks, réponse |
| `ContextSwitchDispatcher` | idem | diffuse aussi les changements de mode aux mesures qui les observent |
| `ResourceAccountingServiceCollectionExtensions` | `OS/HackerOs.Ecosystem` | crée le comptable quand la mesure CPU est active et l'inscrit sur le diffuseur |
| `CpuTimeAccounting` | `Platform/HackerOs.Platform.Core/Metrics` | `/proc/stat` réel quand le comptable mesure |
| `ProcFileSystemProvider` | `Platform/HackerOs.Platform.Core/FileSystem/Virtual` | `/proc/<pid>/stat` (nouveau), champs mesurés de `status` |
| `HackProcess` | `Shared/HackerOs.AppSdk/Diagnostics` | `UserProcessorTime`, `PrivilegedProcessorTime`, `TotalProcessorTime`, `Responding` |
| `ProcessRow.Responding`, `ProcessesTab` | `Apps/System/HackerOs.Apps.SystemMonitor` | « Not responding » dans la colonne d'état |

### Ce que voit le comptable

```
             Enter(P)        EnterKernel      fin passerelle     Dispose(P)
temps ──────────┼───────────────┼──────────────────┼──────────────────┼──────────
hors frame      │  utime de P   │   stime de P     │   utime de P     │ hors frame
(idle ou        └──────────── une tranche de P (un changement de contexte) ──┘ (compositeur
 compositeur)                                                                    ou idle)
```

- **Changement de processus** (`OnContextSwitch`) : le temps écoulé depuis le
  dernier événement de ce thread va au processus qui sort (`utime` ou `stime`
  selon `FromKernel`) ; sa tranche se termine. Hors frame, il va au compositeur
  ou à `idle`.
- **Changement de mode** (`OnModeSwitch`) : le temps écoulé va au mode quitté ;
  la tranche continue (même processus).
- Les continuations asynchrones restaurent le frame : un `await` dans une
  passerelle reprend en mode noyau, un `await` dans l'app reprend en mode
  utilisateur. Le sous-frame noyau est une copie du frame de l'app (même
  processus, même répertoire courant).

### Compositeur ou `idle`

Le crochet voit le thread quitter un processus, pas la boucle d'événements du
navigateur se vider. Un intervalle hors frame est donc classé selon la façon
dont le processus est sorti :

| Sortie | Ce qui suit | Imputation |
|---|---|---|
| l'OS ferme la portée lui-même (gestionnaire d'événement, rendu, lancement) | l'OS continue : rendu Blazor, gestionnaire de fenêtres | compositeur, jusqu'à `CompositorSliceBudget` (4 ms, une passe de rendu), le reste en `idle` |
| le runtime restaure un contexte vide (fin d'une continuation, d'une minuterie) | retour à la boucle d'événements | `idle` |
| démarrage, avant tout processus | — | `idle` |

Comme Xorg ou `gnome-shell`, le temps du compositeur est du temps `system` dans
`/proc/stat`. `idle` est le reste du temps écoulé depuis la création du
comptable : `user + system + idle` vaut toujours le temps écoulé.

### Tranches, long tasks et « Not responding »

- Une **tranche** est une occupation ininterrompue du thread par un processus,
  modes utilisateur et noyau confondus. Chaque tranche terminée compte dans
  `voluntary_ctxt_switches` (le processus a rendu le thread) ;
  `nonvoluntary_ctxt_switches` reste à `0` : rien n'est préempté.
- Une tranche de plus de **50 ms** est une *long task* (`LongTasks:`).
- Une tranche de plus de `UnresponsiveThresholdMs` (5 s par défaut,
  `HackerOs:ResourceAccounting:Cpu:UnresponsiveThresholdMs`) met le processus
  **Not responding** (`Responding: 0`) ; il redevient `1` dès qu'il termine une
  tranche sous le seuil, c'est-à-dire qu'il répond vite au prochain événement.
- Sur le thread principal, l'état n'est visible qu'**après coup**, à la fin de
  la tranche : rien d'autre ne s'exécute pendant. Une boucle infinie gèle
  encore l'OS ; seul un Web Worker (ADR 0070) l'isole.
- **Attention (ADR 0127 §4)** : une app fenêtrée tourne sur le fil principal.
  Une boucle longue sans `await` y gèle tout le bureau, souris comprise, et
  aucune boîte ne peut s'afficher pendant le gel. Un calcul lourd va dans
  `HackBackground.RunAsync` (ADR 0128) ; une boucle longue qui doit rester sur
  le fil appelle `CheckpointAsync` (ADR 0069) assez souvent pour rendre la main.
- Une fenêtre dont le processus reste « Not responding » reçoit le suffixe
  « (Not responding) », un voile gris et la boîte « Attendre | Terminer
  l'application » (ADR 0127 §2-3, [`process-priority-and-unresponsive-windows.md`](process-priority-and-unresponsive-windows.md)).
- Dans un Web Worker (commandes et services, [`worker-processes.md`](worker-processes.md)),
  le worker mesure les tranches de ses processus avec le même crochet et les
  envoie dans son battement de cœur (250 ms) ; l'OS les ajoute au temps noyau
  passé à servir leurs appels. Un worker sans battement depuis 5 s met ses
  processus **Not responding pendant** le blocage, et ce temps compte comme
  occupé (ADR 0072 §6).

### Coût

Résolution : dans le navigateur, `Stopwatch.GetTimestamp` lit
`performance.now()`, arrondi à 100 µs, ou à 5 µs quand la page est isolée
(ADR 0131 §4, ADR 0143 §7) ; `/proc/hackeros/workers` affiche la valeur
mesurée (`timer-resolution-us`).

Le chemin chaud lit l'horloge (`Stopwatch.GetTimestamp`), quelques champs
`[ThreadStatic]`, et additionne avec `Interlocked` dans un emplacement trouvé
par PID dans un tableau : pas de verrou, pas d'allocation, sauf la première
fois qu'un processus s'exécute (son emplacement) et quand les PID dépassent le
tableau (il double, sous verrou, hors du chemin chaud). L'état par thread garde
le décompte juste quand les tests ou le serveur exécutent du code sur plusieurs
threads.

`EnterKernel` rend une structure : un appel de passerelle n'alloue rien pour
la portée, et sans crochet (mesures coupées) il ne fait rien du tout.

## Utilisation

### Lire

```bash
cat /proc/42/stat
# 42 (code-editor) R 1 42 42 0 -1 0 0 0 0 0 153 27 0 0 20 0 1 0 1234 4194304 1024 18446744073709551615 0 0 ... 0
grep -E 'Cpu|Responding|LongTasks|ctxt' /proc/42/status
cat /proc/stat
```

```csharp
HackProcess editor = await HackProcess.GetProcessByIdAsync(42);
TimeSpan user = editor.UserProcessorTime;          // utime
TimeSpan system = editor.PrivilegedProcessorTime;  // stime
TimeSpan total = editor.TotalProcessorTime;
bool responding = editor.Responding;               // false : « Not responding »
```

| Fichier | Mesure active | Mesure coupée |
|---|---|---|
| `/proc/<pid>/stat` | `utime`, `stime` mesurés (champs 14 et 15, `USER_HZ`) | `utime` reconstruit depuis l'historique borné du simulateur (un échantillon par seconde), `stime` à `0` |
| `/proc/<pid>/status` | `Cpu` = usage de la dernière seconde, `CpuSource: measured`, puis `Responding`, `LongTasks`, `voluntary_ctxt_switches`, `nonvoluntary_ctxt_switches` | exactement le fichier d'avant : `Cpu` simulé, `CpuSource: simulated`, sans les quatre champs |
| `/proc/stat` | `cpu0` = le thread principal (`user`, `system` = `stime` + compositeur, `idle`) ; le cœur `k` ajoute le temps occupé du worker du pool qui le porte (ADR 0070, [`worker-processes.md`](worker-processes.md)) ; sans worker, les autres cœurs restent `idle` | temps simulé (ADR 0057) |

Un processus qui n'a encore rien exécuté (ou un processus simulé,
qui n'exécute pas de code) n'est pas mesuré : il garde ses valeurs simulées.

### Configurer

Voir [`resource-accounting.md`](resource-accounting.md) : propriété MSBuild
`HackerOsCpuAccounting`, section `HackerOs:ResourceAccounting:Cpu` des
`appsettings.json` (`Enabled`, `UnresponsiveThresholdMs`). Coupée, la mesure
n'installe rien (si la mémoire est coupée aussi, aucun crochet) et `/proc` rend
exactement les fichiers d'avant la phase 5, plus le nouveau `stat` simulé.

## Décisions et précisions par rapport à l'ADR 0067

- **Le compositeur n'est pas un processus.** La session n'a pas de processus
  d'interface graphique dans la table des processus ; le temps du compositeur
  est tenu par le comptable (`MeasuredThreadCpuTimes.Compositor`) et compté en
  `system` dans `/proc/stat`, sans ligne `/proc/<pid>`. Le jour où la session
  aura un tel processus, il suffira de lui imputer ce compteur.
- **Budget du compositeur.** Le crochet ne voit pas le retour à la boucle
  d'événements ; le budget de 4 ms par intervalle évite que le compositeur
  absorbe l'attente des événements. C'est une approximation : un long travail
  de l'OS hors de tout processus est compté en partie en `idle`.
- **Le mode noyau passe par une interface à part.** Le contrat
  `IExecutionContextSwitchObserver` ne change pas de sens (seuls les
  changements de processus) ; `ExecutionContextSwitch` gagne deux champs
  optionnels (`FromKernel`, `ToKernel`) et les changements de mode vont à
  `IExecutionModeSwitchObserver`, que la mesure de la mémoire peut ignorer.
- **Sous-frame noyau réservé à l'OS.** `EnterKernel` est interne au SDK et
  visible de Platform Core seulement : une app ne peut pas déclarer son propre
  code en temps système.
- **Usage de la dernière seconde calculé à la lecture.** Le comptable garde,
  par processus, le total au dernier échantillon ; une lecture plus d'une
  seconde après le précédent échantillon en prend un nouveau. Pas de minuterie
  de plus.
- **`/proc/<pid>/stat` existe même mesure coupée.** C'est un nouveau fichier
  Linux ; simulé, il reste cohérent avec `Cpu` de `status`.

## Tests

| Suite | Couvre |
|---|---|
| `HackerOs.Platform.ResourceAccounting.Tests` (`MeasuredCpuTimeAccountantTests`) | `utime` dans une portée, `stime` dans une vraie passerelle (directement et par le diffuseur composé comme dans l'OS), processus imbriqués, long tasks et « Not responding » puis retour, compositeur et budget, `idle` après une continuation, somme égale au temps écoulé, usage de la dernière seconde, processus jamais exécuté, croissance du tableau, continuations async réelles |
| idem (`RealAppsMeasurementTests`) | section 5.1 du plan : la vraie Calculatrice (fenêtre), le vrai `echo` (commande) et le service d'exemple, sans changer leur code ; `/proc/<pid>/stat` (52 champs, `utime`/`stime`) et `status` (`CpuSource: measured`, `Responding: 1`) |
| idem (`CpuAccountingOverheadTests`, catégorie `Performance`) | scénario de référence (dix fenêtres, frappe dans un éditeur avec rendu à chaque touche et appels de passerelle, pipeline `echo \| grep`) avec et sans mesure : un écart de plus de 5 % fait échouer le test (meilleur de 9 manches alternées, trois tentatives) |
| `HackerOs.Platform.Core.Tests` | format de `/proc/<pid>/stat` mesuré et simulé, champs ajoutés à `status` seulement quand c'est mesuré, `/proc/stat` mesuré sur un cœur, `HackProcess.*ProcessorTime`, `Responding`, `ParseStatTimes` |
| `HackerOs.Ecosystem.Tests` | comptable créé et inscrit seulement si la mesure CPU est active (16 combinaisons), seuil lu dans la configuration, `/proc/stat` composé avec la mesure |
| `HackerOs.Apps.SystemMonitor.Tests` | « Not responding » dans la colonne d'état et le tri |

Vérifié à la main : un build de `HackerOs.Ecosystem` avec
`-p:HackerOsCpuAccounting=false -p:HackerOsMemoryAccounting=false` ne contient
aucun fichier `HackerOs.Platform.ResourceAccounting*`.

## Liste des tâches

- [x] Contrats : `ProcessCpuTimes`, `MeasuredThreadCpuTimes`, `TryGetProcessTimes`, `TryGetThreadTimes`, mode noyau dans `ExecutionContextSwitch`, `IExecutionModeSwitchObserver`.
- [x] Sous-frame noyau `HackExecutionContext.EnterKernel`, sans coût quand le crochet est absent.
- [x] Passerelles fichiers, réseau, presse-papiers et disque en mode noyau.
- [x] `MeasuredCpuTimeAccountant` : `utime`/`stime`, compositeur, `idle`, tranches, long tasks, « Not responding ».
- [x] Diffusion des changements de mode par `ContextSwitchDispatcher` ; composition quand la mesure CPU est active.
- [x] `/proc/<pid>/stat` (format `proc(5)`), champs mesurés de `/proc/<pid>/status`, `/proc/stat` réel via `CpuTimeAccounting`.
- [x] `HackProcess.UserProcessorTime`, `PrivilegedProcessorTime`, `TotalProcessorTime`, `Responding`.
- [x] Colonne d'état « Not responding » du gestionnaire de tâches (affichage seulement).
- [x] Mesure coupée : comportement d'avant (vérifié au build et par les tests).
- [x] Test de surcoût (< 5 %).
- [x] Apps existantes (section 5.1) : aucune modification de code d'app ; tests sur une fenêtre, une commande et un service réels.
- [x] Documentation : ce document, `resource-accounting.md`, `system-metrics.md`, `task-manager.md`, `virtual-proc-and-dev.md`, `app-sdk.md`, `sdk-reference.md`, plan, index.
