> [English version](../../../../en/wasm/dev/plans/execution-isolation-plan.md)

# Phase 5 — exécution des apps : mesure du CPU et de la mémoire, arrêt propre, Web Workers

**Statut :** plan validé avec Benoit le 2026-10-16, **terminé le 2026-10-16**
(PR #124 à #129, puis vérification finale `rebuild/5-final`, section 6). Il prolonge
[`ecosystem-rebuild-plan.md`](ecosystem-rebuild-plan.md) (phases 1 à 4,
fusionnées dans `main` par la PR #114) et lève l'exclusion « isolation
d'exécution » de sa section 9. Les décisions sont dans les ADR 0066 à 0071.

## 1. Objet

- **Mesurer** le temps CPU et la mémoire réels de chaque processus, à la place
  des valeurs simulées par `ResourceProfile`, et afficher « Not responding »
  pour un processus qui monopolise son thread.
- Permettre à un processus de **se terminer proprement** quand l'OS lui demande
  de s'arrêter, et de rendre la main pendant un traitement long.
- **Isoler** les processus sans interface (commandes, services) dans des Web
  Workers, pour qu'une boucle infinie ne gèle plus l'OS et qu'un kill soit réel.

## 2. Constat de départ

- Blazor WebAssembly exécute toutes les apps sur **un seul thread**, dans un seul
  tas .NET. Le navigateur ne mesure ni le CPU ni la mémoire par app.
- `/proc/<pid>/status` (`VmRSS`, `Cpu`) et `/proc/stat` affichent des valeurs
  tirées du `ResourceProfile` de l'app (ADR 0012, 0057). Le disque et le réseau
  sont déjà réellement comptés.
- `HackExecutionContext` sait à chaque instant quel processus exécute du code
  (`AsyncLocal`, ADR 0043) : c'est la base de la mesure.
- `StopAsync` fait déjà `SIGTERM` puis `SIGKILL` ; « End task » fait un
  `kill -9`.
- Les hôtes publient sans trimming (`PublishTrimmed=False`).

## 3. Principes

1. **Mesurer, pas contraindre.** Pas de quantum imposé, pas de limite mémoire,
   pas d'OOM killer, pas de fenêtre « Forcer à quitter ».
2. **Optionnel.** Chaque mesure se coupe au build (le code n'est pas livré) et
   dans `appsettings.json` (aucun crochet installé). Coupée, on revient
   exactement au comportement actuel.
3. **Mimic the Metal.** Changements de contexte, `utime`/`stime`, compositeur,
   `SIGTERM`/`SIGKILL`, workers = cœurs virtuels, formats `proc(5)`.
4. **Le SDK ne change pas pour les apps existantes.** Une app gagne la mesure
   sans changer une ligne ; une commande ne sait pas si elle tourne dans un
   worker.
5. **Les apps existantes suivent chaque nouveauté dans le même chantier.**
   Chaque chantier fait l'inventaire des apps, commandes et services de
   `Apps/` concernés et les migre avant sa PR (section 5.1) : ce n'est pas
   laissé à un chantier ultérieur.
6. **Les trois hôtes restent opérationnels après chaque chantier.**
   `OS/HackerOs.Ecosystem` (PWA autonome, sans serveur), `test/test` (Blazor
   Web App de débogage) et `Server/HackerOs.Server` (rendu Interactive Server)
   doivent compiler, se publier et **démarrer jusqu'au bureau** avant toute
   fusion. La vérification de démarrage (section 5.2) fait partie du « fini »
   de chaque PR `rebuild/5-*`, avec les mesures activées et coupées.

## 4. Registre des décisions

| # | Décision | ADR |
|---|---|---|
| 1 | Mesures CPU et mémoire indépendantes, coupables au build (projet `HackerOs.Platform.ResourceAccounting` référencé sous condition) et au démarrage (`appsettings.json`) ; coupées, retour au simulateur avec `CpuSource`/`MemSource: simulated` et la mention « estimated ». | 0066 |
| 2 | Temps CPU mesuré par le rappel de changement de valeur de l'`AsyncLocal` ambiant ; `utime`, `stime`, temps hors frame imputé au compositeur ; `/proc/<pid>/stat`. | 0067 |
| 3 | « Not responding » quand une tranche dépasse `UnresponsiveThresholdMs` (5 s) ; affichage seulement. | 0067 |
| 4 | Mémoire en quatre couches : image, ressources de l'OS, allocations par tranche, tas réel réparti après chaque GC ; pas de limite. Le comptage du marshaling est écarté. | 0068 |
| 5 | `StopRequested` (raison, échéance), `CheckpointAsync`, `TerminateAsync`, `ProcessExitReason.Terminated` ; escalade en quatre étapes ; « End task » devient `SIGTERM`, « Force kill » est ajouté. | 0069 |
| 6 | Modèle hybride : apps à fenêtre sur le thread principal, commandes dans un pool d'un worker par cœur virtuel, services dans un worker dédié avec une réserve ; passerelles distantes ; battement de cœur ; seul un worker muet est terminé. | 0070 |
| 7 | Une fois le système démarré, toujours au moins un worker prêt ; il sert aux processus dédiés et au débordement du pool quand aucun worker n'est libre ; plafond `navigator.hardwareConcurrency` ; `/proc/hackeros/workers`. | 0071 |
| 8 | `HackerOs.Server` : mesures coupées par défaut, pas de worker, jusqu'à un ADR propre à cet hôte. | 0066, 0067, 0070 |

## 5. Chantiers

Chaque branche part de `rebuild/base` et sa PR vise `rebuild/base`.

| Branche | Contenu | Dépend de |
|---|---|---|
| `rebuild/5-accounting-switches` | ADR 0066 : propriétés MSBuild, `build/HackerOs.ResourceAccounting.targets`, `RuntimeHostConfigurationOption`, projet `HackerOs.Platform.ResourceAccounting` (vide de mesure, avec le **crochet de changement de contexte** commun aux deux mesures), contrats nuls dans Simulation Abstractions, `wwwroot/appsettings.json` des hôtes, `CpuSource`/`MemSource`, mention « estimated ». | — |
| `rebuild/5-cpu-accounting` | ADR 0067 : comptage `utime`/`stime`, compositeur, `/proc/<pid>/stat`, `/proc/stat` réel, `HackProcess.*ProcessorTime`, long tasks, « Not responding » et `Responding:`, test de surcoût (< 5 %). | `5-accounting-switches` |
| `rebuild/5-memory-accounting` | ADR 0068 : quatre couches, champs de `/proc/<pid>/status`, `/proc/meminfo`, `PrivateMemorySize64`. | `5-accounting-switches` |
| `rebuild/5-graceful-stop` | ADR 0069 : `StopRequested`, `CheckpointAsync`, `TerminateAsync`, `Terminated`, ordonnanceur par processus et `HackProcessKilledException`, « End task » poli et « Force kill », `CheckpointAsync` dans les commandes longues. | — |
| `rebuild/5-worker-processes` | ADR 0070 et 0071 : worker toujours prêt après le boot, débordement et plafond, `/proc/hackeros/workers`, `AppExecutionHost` dans le manifeste, `HackerOs.Platform.WorkerHost`, passerelles distantes et tests de parité, pool et réserve, battement de cœur, escalade étape 4, d'abord pour `HackerOs.Commands`. | les quatre autres |
| `rebuild/5-host-smoke` | Section 5.2 : `build/verify-hosts.sh`, test Playwright de démarrage des trois hôtes avec repli sur le Chromium du conteneur, combinaisons des mesures ; corrige aussi le lancement des suites E2E existantes dans un conteneur. | — |
| `rebuild/5-final` | Vérification de bout en bout, `system-metrics.md`, `task-manager.md`, `app-sdk.md`, `sdk-reference.md`, index, cette liste à jour. | tous |

### 5.1 Ce que chaque chantier fait pour les apps existantes

| Chantier | Migration des apps existantes |
|---|---|
| `5-accounting-switches` | Vérifier que tout le code des apps passe par un frame ambiant (`WindowAppBase`, `HackComponentBase`, `TerminalAppBase`, `ServiceAppBase`) ; ajouter un test qui échoue si une app de `Apps/` exécute du code hors frame, faute de quoi son temps irait au compositeur. |
| `5-cpu-accounting`, `5-memory-accounting` | Aucune modification de code d'app ; le générateur de manifeste fournit la taille des assemblies (couche image) pour **toutes** les apps ; tests de `/proc/<pid>/stat` et `status` sur un échantillon de chaque famille (fenêtre, commande, service). |
| `5-graceful-stop` | Chaque app à fenêtre et chaque service qui tient un état (éditeurs, explorateur pendant une copie, Paramètres, services) réagit à `StopRequested` : finit ou annule proprement son opération en cours, sans perte silencieuse (un éditeur avec des modifications non enregistrées garde un brouillon de récupération). Toute boucle longue de `Apps/` (commandes, copie récursive de l'explorateur, recherche du CodeEditor, zip) appelle `CheckpointAsync`. |
| `5-worker-processes` | Chaque commande de `HackerOs.Commands` et chaque service déclare son `AppExecutionHost` (ou reçoit le bon défaut) ; les tests de parité exécutent **toutes** les commandes intégrées dans les deux modes ; une app qui ne peut pas tourner en worker reste en `MainThread`, avec la raison en commentaire. |

### 5.2 Vérification des hôtes

Le chantier `rebuild/5-host-smoke` ajoute un test de démarrage des trois hôtes,
lancé par une seule commande (`build/verify-hosts.sh`) :

- compile les trois hôtes et publie Ecosystem ;
- lance chaque hôte (Ecosystem publié derrière un serveur statique, `test/test`
  et `HackerOs.Server` par `dotnet run`), l'ouvre dans Chromium par Playwright
  (repli sur `/opt/pw-browsers/chromium` quand la version attendue par Playwright
  est absente, le cas des conteneurs) ;
- attend l'écran de connexion puis le bureau, sans erreur dans la console, ouvre
  le terminal, exécute `ps` et `cat /proc/self/status`, ouvre le gestionnaire de
  tâches, puis ferme tout ;
- refait le parcours d'Ecosystem avec les mesures coupées au build
  (`-p:HackerOsCpuAccounting=false -p:HackerOsMemoryAccounting=false`) et par
  `appsettings.json`.

Tant que ce test n'est pas fusionné, chaque chantier fait la même vérification à
la main et la décrit dans sa PR. Une fois fusionné, chaque PR `rebuild/5-*`
indique sa sortie.

Ordre :

1. En parallèle : `5-accounting-switches` et `5-graceful-stop`.
2. Après `5-accounting-switches`, en parallèle : `5-cpu-accounting` et
   `5-memory-accounting`.
3. `5-worker-processes`.
4. `5-final`.

## 6. Liste des tâches

### Préparation
- [x] Discuter les options avec Benoit (mesure optionnelle, mesure seulement,
  « Not responding » seulement, pool ou worker par processus, jeton d'arrêt).
- [x] Écrire ce plan et les ADR 0066 à 0070 (2026-10-16).

### Mesure optionnelle (ADR 0066)
Fait par `rebuild/5-accounting-switches` ([`resource-accounting.md`](../resource-accounting.md)).
- [x] Propriétés MSBuild, `.targets` et référence conditionnelle.
- [x] `appsettings.json` des trois hôtes et lecture à la composition.
- [x] Retour au simulateur, `CpuSource`/`MemSource`, mention « estimated ».
- [x] Tests des quatre combinaisons, sans crochet quand c'est coupé.
- [x] Crochet commun de changement de contexte (`AsyncLocal` avec rappel, contrat d'observateur `IExecutionContextSwitchObserver`, diffuseur), installé seulement si une mesure est active.

### CPU (ADR 0067)
Fait par `rebuild/5-cpu-accounting` ([`cpu-accounting.md`](../cpu-accounting.md)).
- [x] Comptage `utime`/`stime`/compositeur sur le crochet de changement de contexte (crochet livré par `5-accounting-switches`).
- [x] `/proc/<pid>/stat`, `/proc/stat` réel, `HackProcess.*ProcessorTime`.
- [x] Long tasks, « Not responding », `Responding:` et colonne du gestionnaire de tâches.
- [x] Test de surcoût.

### Mémoire (ADR 0068)
Fait par `rebuild/5-memory-accounting` ([`memory-accounting.md`](../memory-accounting.md)).
- [x] Couches image, ressources de l'OS, allocations, résident.
- [x] `/proc/<pid>/status`, `/proc/meminfo`, `PrivateMemorySize64`.

### Arrêt propre (ADR 0069)
- [x] `StopRequested`, `TerminateAsync`, `ProcessExitReason.Terminated`.
- [x] `CheckpointAsync` et son usage dans les commandes longues.
- [x] Ordonnanceur par processus, continuations jetées, `HackProcessKilledException`.
- [x] « End task » en `SIGTERM`, « Force kill » ajouté.

Précisions de mise en œuvre (`rebuild/5-graceful-stop`, détail dans
[`app-sdk.md`](../../framework/app-sdk.md#arrêt-propre-et-points-darrêt-adr-0069)) :

- Le noyau tient un `ProcessControlBlock` par processus (demande d'arrêt,
  drapeau « tué », `ProcessTaskScheduler`). Est un `SIGKILL` : `Killed`
  (`kill -9`, « Force kill ») et `Timeout` (fin du délai de grâce, raison gardée
  pour ne pas changer `CloseMainWindowAsync`) ; une fenêtre fermée, une
  déconnexion ou une app désactivée gardent l'accès à l'OS pour leur nettoyage.
- L'ordonnanceur par processus est installé pour les commandes et les services
  (point d'entrée lancé par l'orchestrateur), en contexte de synchronisation et
  en `TaskScheduler` ; les apps à fenêtre restent sur le dispatcher du rendu.
- `HackStopRequestedEventArgs.Defer(Task)` laisse une app finir un travail
  avant l'action par défaut ; l'action par défaut de `SIGTERM` pour une app à
  fenêtre est de fermer sa fenêtre, le processus finit alors avec la raison de
  la demande (`Terminated`) au lieu d'attendre le délai.
- Les services et, à la déconnexion ou à l'extinction, les fenêtres reçoivent
  aussi `StopRequested` (`Logout`, `Shutdown`) ; une fenêtre que l'utilisateur
  ferme ne le reçoit pas (`IWindowCloseGuard` a déjà posé la question).
- La tranche de `CheckpointAsync` est un horodatage par thread
  (`HackProcessSlice`), recommencé par l'ordonnanceur à chaque continuation ;
  l'ADR 0067 pourra le remplacer par la mesure.

### Web Workers (ADR 0070)
- [x] `AppExecutionHost` dans `[AppManifest]` et le générateur de manifeste. (`5-worker-processes`, [`worker-processes.md`](../worker-processes.md))
- [x] Hôte de processus dans un worker et passerelles distantes, tests de parité. (`5-worker-processes`, [`worker-processes.md`](../worker-processes.md))
- [x] Pool (un worker par cœur virtuel), réserve dédiée, placement des pipelines. (`5-worker-processes`, [`worker-processes.md`](../worker-processes.md))
- [x] Battement de cœur, « Not responding » pendant le blocage, terminaison du worker. (`5-worker-processes`, [`worker-processes.md`](../worker-processes.md))
- [x] Worker toujours prêt après le boot, débordement du pool, plafond, `/proc/hackeros/workers` (ADR 0071). (`5-worker-processes`, [`worker-processes.md`](../worker-processes.md))

### Apps existantes (section 5.1)
- [x] Toutes les apps de `Apps/` exécutent leur code dans un frame ambiant (test `AmbientFrameSourceTests`, `5-accounting-switches`).
- [x] Temps CPU : `/proc/<pid>/stat` et `status` testés sur une fenêtre (Calculatrice), une commande (`echo`) et un service (service d'exemple) réels, sans modifier leur code (`5-cpu-accounting`).
- [x] Apps à fenêtre et services avec état : `StopRequested` géré (TextEditor et CodeEditor : brouillon ; FileExplorer : élément en cours puis arrêt propre ; Settings : écriture en cours ; services : `OnStoppingAsync` une fois).
- [x] Boucles longues de `Apps/` : `CheckpointAsync` (commandes, `FileTransfer`, zip de l'explorateur, corbeille, recherche et parcours du CodeEditor).
- [x] Commandes et services : `AppExecutionHost` déclaré, parité testée pour toutes les commandes. (`5-worker-processes`, [`worker-processes.md`](../worker-processes.md))

### Hôtes (section 5.2)
- [x] `build/verify-hosts.sh` : les trois hôtes compilent, se publient et démarrent jusqu'au bureau ; repli sur `/opt/pw-browsers/chromium` pour toutes les suites Playwright (`rebuild/5-host-smoke`).
- [x] Parcours avec les mesures coupées au build et par `appsettings.json` (`CpuSource`/`MemSource` vérifiés pour chaque parcours).
- [x] Chaque PR `rebuild/5-*` donne la sortie de la vérification : sortie de `build/verify-hosts.sh` dans #128 et #129 ; #124 à #127, fusionnées avant le script, décrivent le même parcours fait à la main (règle de la section 5.2).

### Fin de phase
- [x] Vérification finale et documentation à jour (`rebuild/5-final`, ci-dessous).

### Vérification finale (`rebuild/5-final`, 2026-10-16)

Sur `rebuild/base` à `47de11c` (après #129), dans un conteneur cloud :

- `dotnet build HackerOs.sln -c Release` : 0 erreur, 0 avertissement.
- Les 35 projets `Tests/*.Tests` hors E2E (environ 2 400 tests) passent, dont
  `HackerOs.Platform.ResourceAccounting.Tests` (52), `HackerOs.Platform.WorkerHost.Tests`
  (82, parité des 63 commandes) et `HackerOs.Platform.Core.Tests` (890).
- `build/verify-hosts.sh` : le premier passage a échoué sur `test-host`. Le test de
  démarrage lisait la sortie de `ps` dès l'en-tête `PID`, alors que la sortie d'une
  commande lancée dans un Web Worker arrive en plusieurs écritures ; les lignes des
  processus n'étaient pas encore affichées. `HostBootSmokeTests` attend maintenant
  l'invite suivante après la sortie de `ps` et de `cat /proc/self/status`
  (`OutputFinished`). Deux passages complets ensuite :

```text
== HackerOS host verification (47de11c) ==
build HackerOs.Ecosystem         PASS
build test                       PASS
build HackerOs.Server            PASS
build HackerOs.HostSmoke.Tests   PASS
publish ecosystem                PASS
publish ecosystem-build-off      PASS
ecosystem                        PASS  30s
ecosystem-build-off              PASS  31s
ecosystem-settings-off           PASS  30s
test-host                        PASS  46s
server                           PASS  36s
RESULT: PASS
```

- Documentation relue contre le code : `system-metrics.md`, `task-manager.md`,
  `app-sdk.md`, `sdk-reference.md`, `project-overview.md`, `virtual-proc-and-dev.md`
  et `linux-commands.md` décrivent déjà les ADR 0066 à 0072 (chaque chantier les a
  mis à jour) ; les décomptes (32 capacités, 75 apps dont 63 commandes) sont exacts.
- Restent hors de cette phase, notés dans
  [`building-and-testing.md`](../building-and-testing.md#état-connu) : deux échecs de
  `HackerOs.UI.E2E.Tests` dus à l'app (accessibilité, bouton « Back » mobile), corrigés
  ensuite par `rebuild/5-ui-a11y-mkdir`, et la suite PWA qui demande IPv6.

## 7. Hors périmètre

- Quantum imposé, limites mémoire, OOM killer, fenêtre « Forcer à quitter ».
- Isolation cross-origin (COOP/COEP), threads WASM, `SharedArrayBuffer`.
- Apps à fenêtre dans un worker.
- Workers et mesures sur `HackerOs.Server`.

Ces points sont repris par [`execution-followups-plan.md`](execution-followups-plan.md)
(plan validé le 2026-10-17, exécuté après la migration wasm v3).

## 8. Références

- ADR [0066](../adr/0066-optional-resource-accounting.md),
  [0067](../adr/0067-measured-cpu-time.md),
  [0068](../adr/0068-measured-process-memory.md),
  [0069](../adr/0069-graceful-stop-and-checkpoints.md),
  [0070](../adr/0070-worker-hosted-processes.md),
  [0071](../adr/0071-always-ready-worker.md).
- [`system-metrics.md`](../system-metrics.md), [`task-manager.md`](../task-manager.md),
  [`app-sdk.md`](../../framework/app-sdk.md).
