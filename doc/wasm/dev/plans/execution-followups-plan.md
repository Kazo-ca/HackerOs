# Suites de la phase 5 : limites, OOM killer, fenêtres en worker, hôte Server, isolation cross-origin

**Statut :** plan **validé par Benoit le 2026-10-17** (réponses aux questions de
la section 6). Aucun code n'est écrit. Benoit a demandé « plan d'abord, mais
faire plus tard après la migration » : ce plan **s'exécute après la fin de la
migration wasm v3** ([`wasm-v3-completion-plan.md`](wasm-v3-completion-plan.md),
branche `v3/base`). Aucun chantier ne commence avant que ce plan-là soit
terminé.

**Mise à jour du 2026-10-18 :** la migration wasm v3 et la série v3.1 sont
terminées. La branche d'intégration `exec/base` est créée depuis `v3.1/base`,
et le chantier des ADR (`exec/0-adr`, mené sur la branche `exec/1-adr`) a
écrit les ADR [0124](../adr/0124-remote-event-and-watch-gateways.md) à
[0132](../adr/0132-exec-series-sdk-1-2-and-merge-rules.md) (section 7) ; l'ADR
[0133](../adr/0133-window-rendering-in-worker-verdict.md) conclut l'essai de rendu
en worker (`exec/6`) : **on s'arrête**, `exec/7` n'a pas lieu. Benoit a répondu le
2026-10-18 aux quatre questions nées de l'écriture des ADR (section 6.1) :
l'ADR [0134](../adr/0134-automatic-server-connection-and-status-icon.md) les
consigne, et l'ADR [0135](../adr/0135-no-game-in-this-repository.md) retire le
jeu de la documentation.

Il reprend les points que [`execution-isolation-plan.md`](execution-isolation-plan.md)
(phase 5, ADR 0066 à 0072) a laissés hors périmètre (sa section 7), ainsi que
ceux que les ADR de la phase 5 et la mise en œuvre des workers ont notés comme
« plus tard ».

## 1. Objet

- **Contraindre** les processus, et plus seulement les mesurer : limites par
  processus façon `ulimit`/`setrlimit`, et un **OOM killer** quand la mémoire du
  système simulé est épuisée.
- **Sortir du fil principal ce qui y reste par nécessité** : services qui ont
  besoin des événements typés ou des abonnements de fichiers, apps tierces, et
  surtout la logique des **apps à fenêtre**.
- Faire servir par **`HackerOs.Server`** le même OS en WebAssembly que
  l'Ecosystem, et ne garder sur le serveur que son API (relais, synchronisation,
  authentification) : les workers et les mesures y deviennent identiques aux
  autres hôtes (décision de Benoit, question 7).
- Étudier puis activer, en option, l'**isolation cross-origin** (COOP/COEP), qui
  ouvre `SharedArrayBuffer`, la mesure réelle de la mémoire par worker et des
  minuteries précises.

## 2. Constat de départ

Sur `web/base` au 2026-10-17 (phase 5 et phase web fusionnées) :

| Sujet | État | Référence |
|---|---|---|
| Limites | Aucune. La phase 5 a posé « mesurer, pas contraindre » : pas de quantum, pas de limite mémoire, pas d'OOM killer, pas de fenêtre « Forcer à quitter ». | `execution-isolation-plan.md` §3 et §7, ADR 0068 §3 |
| Mesures | CPU (`utime`/`stime`, compositeur) et mémoire en quatre couches, optionnelles au build et dans `appsettings.json`. Compteurs en mémoire, remis à zéro au démarrage. | ADR 0066 à 0068, [`cpu-accounting.md`](../cpu-accounting.md), [`memory-accounting.md`](../memory-accounting.md) |
| Arrêt | `StopRequested`, `CheckpointAsync`, escalade en quatre étapes ; l'étape 4 (terminer le worker) n'existe que dans un worker. Sur le fil principal, un processus bloqué gèle encore l'OS. | ADR 0069, ADR 0072 §5 |
| Workers | Commandes en pool (49 sur 63) et services en worker dédié, chacun avec son runtime .NET (~15 à 40 Mo par worker). Le pool ne précharge que `HackerOs.Commands.dll`. | ADR 0070 à 0072, [`worker-processes.md`](../worker-processes.md) |
| Ce qu'un worker n'offre pas | `HackEvents`, `Watch`, hôte de shell, administration et page HTTP du navigateur simulé lèvent `NotSupportedException`. Le service d'exemple et `hackwebd` restent donc en `MainThread`. | ADR 0072 §3 |
| Apps à fenêtre | Toujours sur le fil principal (pas de DOM dans un worker). Une boucle dans une fenêtre gèle le bureau ; « Not responding » ne s'affiche qu'après la fin de la tranche. | ADR 0070 §1, ADR 0067 §4 |
| `HackerOs.Server` | Mesures coupées par défaut, pas de worker : `AppExecutionHost` est ignoré, une ligne dans `/var/log/syslog`, pas de `/proc/hackeros`. | ADR 0066 §3, ADR 0067 §6, ADR 0070 §6, ADR 0072 §7 |
| Cross-origin | Aucun hôte n'envoie `Cross-Origin-Opener-Policy` ni `Cross-Origin-Embedder-Policy` ; `crossOriginIsolated` est faux, donc pas de `SharedArrayBuffer`, pas de `performance.measureUserAgentSpecificMemory()`, `performance.now()` arrondi. Les échanges avec les workers passent par `postMessage` (crédit de 64 Kio par flux). | ADR 0072 §2 |
| Trimming | `PublishTrimmed=False` : le coût d'un worker en dépend. Le point appartient à la migration wasm v3, pas à ce plan. Fait par `v3/14` (2026-10-17, [`trimmed-publish.md`](../trimmed-publish.md)) : 50,9 → 26,5 Mio de WebAssembly. | `/mnt/project-files/wasm-v3/wasm-v3-remaining-work.md` n° 12 |

### 2.1 Ce que v3 et v3.1 ont changé (2026-10-18, `exec/base`)

| Sujet | Changement | Effet sur ce plan |
|---|---|---|
| Passerelles d'un worker | Catalogue et applications par défaut, intents, contrôle et notification des services, presse-papiers, notifications, ports à l'écoute, base d'un site passent désormais par messages. Restent refusés : `HackEvents`, `Watch`, `StartChild`, `Kill` et `ListProcesses` synchrones, `WithSelectedHandle`. | La décision 1 se limite aux événements et à la surveillance (ADR 0124). |
| SDK | Gelé en 1.0 (ADR 0114), puis 1.1.0 publié (ADR 0117) ; `PublicAPI.Shipped.txt` vérifié au build. | Les ajouts de la série vont en `Unshipped` et sont publiés en 1.2 par `exec/10-final` (ADR 0132). |
| Localisation, accessibilité | Anglais et français (ADR 0104), préférences d'accessibilité et tests axe (ADR 0105). | La boîte « ne répond pas » et les notifications de l'OOM killer sont traduites et testées (ADR 0126, 0127). |
| Titre de fenêtre | Posé par l'app (`SetWindowTitleAsync`, ADR 0117). | Le suffixe « (Ne répond pas) » est un affichage du gestionnaire de fenêtres (ADR 0127). |
| Relais du serveur | Corps HTTP, TCP et UDP en WebSocket (ADR 0102, 0108). | Rien à changer : le relais est déjà une API, indépendante de la façon dont le serveur sert l'OS (ADR 0130). |
| Installation au runtime | Toujours différée (ADR 0094). | Le pool s'ouvre aux assemblies connues au build seulement (ADR 0124 §4). |

## 3. Principes

Ceux de la phase 5 restent, sauf le premier, que ce plan remplace en partie.

1. **Contraindre seulement là où la contrainte est réelle.** Dans un worker,
   une limite dépassée termine vraiment le processus. Sur le fil principal, une
   limite passe par la demande d'arrêt et l'escalade (ADR 0069), et la
   documentation dit qu'elle est « au mieux ». On n'affiche jamais une
   protection que l'OS ne peut pas tenir.
2. **Optionnel, comme les mesures.** Chaque contrainte se coupe dans
   `appsettings.json` (réglage d'hôte) ou dans `/etc` (réglage de l'OS simulé,
   ADR 0044) selon sa nature ; coupée, on revient au comportement actuel. Une
   limite fondée sur une mesure est inactive quand cette mesure est coupée
   (`CpuSource`/`MemSource: simulated`).
3. **Mimic the Metal.** `setrlimit`, `ulimit`, `/proc/<pid>/limits`,
   `/etc/security/limits.conf`, `SIGXCPU`, `oom_score`, `oom_score_adj`, la
   ligne « Out of memory: Killed process » du noyau, un serveur d'affichage qui
   reçoit les dessins d'un client (X11, Wayland).
4. **Le SDK ne change pas pour les apps existantes.** Une app existante garde
   son comportement sans changer une ligne. Les nouveautés (processus de
   travail d'une fenêtre, réaction à une limite) sont des ajouts.
5. **Les apps existantes suivent chaque nouveauté dans le même chantier**
   (section 5.1), comme en phase 5.
6. **Les trois hôtes restent opérationnels après chaque chantier.**
   `build/verify-hosts.sh` passe avant toute fusion, avec les options du
   chantier activées et coupées. L'Ecosystem fonctionne toujours sans serveur
   et hors ligne (AGENTS.md, section I).
7. **Aucun jeu sur ce repository** ([ADR 0135](../adr/0135-no-game-in-this-repository.md)) : ce plan ne prévoit rien de tel.

## 4. Registre des décisions

Décisions validées par Benoit le 2026-10-17 (section 6). Chacune devient une
décision d'ADR dans `exec/0-adr` ; les numéros d'ADR sont attribués au moment
de l'écriture (section 7).

| # | Décision | Question |
|---|---|---|
| 1 | Passerelles distantes pour `HackEvents` et `Watch` ; le service d'exemple et `hackwebd` passent en worker ; le pool charge à la demande l'assembly de n'importe quelle commande ou service, plus seulement `HackerOs.Commands.dll`. | — |
| 2 | Limites par processus façon `setrlimit` : `RLIMIT_CPU` (`SIGXCPU` puis `SIGKILL`), une limite mémoire sur `VmRSS` mesuré, `RLIMIT_NOFILE`, `RLIMIT_NPROC`. Commande `ulimit` (intégrée au shell), `prlimit`, `/proc/<pid>/limits`, `/etc/security/limits.conf`. Héritées par les enfants. | Q1 |
| 3 | OOM killer : quand la mémoire disponible du système simulé passe sous un seuil, l'OS choisit la victime par `oom_score` (résident + `oom_score_adj`), la tue et l'écrit dans le journal du noyau. **Désactivé par défaut** : il s'active dans `/etc` (réglage de l'OS simulé) et seulement si la mémoire est mesurée. `MemTotal` reste la valeur **simulée** du profil matériel ; l'occupation est mesurée, ou estimée au plus juste quand une mesure exacte manque (mémoire réelle par worker avec l'isolation de la décision 9). | Q2, Q3 |
| 4 | Garde du vrai navigateur : quand le tas du runtime principal ou d'un worker approche la limite réelle de la page, l'OS avertit (journal, notification) ; si l'OOM killer est activé, il agit même si `MemTotal` simulé n'est pas atteint. | Q2, Q3 |
| 5 | Pas de quantum imposé ; `nice` et `renice` règlent seulement l'ordre de la file du pool et le choix du worker. | Q4 |
| 6 | Fenêtre sans réponse, comme dans un navigateur : titre « (Not responding) », fenêtre grisée et boîte **« L'application ne répond pas : Attendre \| Terminer l'application »**, dès que le bureau peut se redessiner (fenêtre dont la logique est en worker, ou fin d'une longue tranche). « Terminer l'application » suit l'escalade de l'ADR 0069. | Q3 |
| 7 | Apps à fenêtre, en deux étapes. **Étape 1** (idée de Benoit, l'inverse de `Invoke` de WinForms) : une fonction du SDK exécute la charge lourde dans un worker, par exemple `await HackBackground.RunAsync(MyJobs.Compress, input, progress, ct)`. Le worker ayant sa propre mémoire, la tâche est une méthode **statique** (ou une classe de tâche) de l'assembly de l'app, avec une entrée et un résultat sérialisables, une progression et un jeton d'annulation ; un analyseur refuse au build une lambda qui capture des variables. La tâche a l'utilisateur et les droits de la fenêtre, apparaît dans `ps` comme enfant de l'app et peut être tuée sans fermer la fenêtre. **Étape 2** : essai de rendu Blazor dans un worker, lots de rendu appliqués par le fil principal façon serveur d'affichage ; rien n'est livré aux apps si l'essai échoue. | Q5 |
| 8 | `HackerOs.Server` sert l'OS **en WebAssembly** (comme `test/test`) et garde côté serveur seulement son API (relais, synchronisation, authentification, ADR 0028) ; le mode Interactive Server de l'ADR 0027 et l'injection directe de l'ADR 0036 sont retirés. Les workers, les mesures et `/proc/hackeros` y deviennent ceux de l'Ecosystem. | Q7 |
| 9 | Isolation cross-origin optionnelle, **active par défaut si l'audit passe** : en-têtes COOP/COEP envoyés par les hôtes et par le service worker de la PWA (donc hors ligne et sur un hébergement statique), `COEP: credentialless` là où le navigateur le permet, sinon `require-corp`, détection par `crossOriginIsolated`, repli exact sur le comportement actuel. | Q6 |
| 10 | Avec l'isolation : tubes en anneaux de `SharedArrayBuffer` entre workers et fil principal, appels synchrones du worker vers l'OS (`Atomics.wait` dans le worker seulement), mémoire réelle par worker par `measureUserAgentSpecificMemory()`, minuteries à 5 µs. **Pas de threads .NET WebAssembly** (`WasmEnableThreads`). | Q6, Q8 |

## 5. Chantiers

Branches `exec/<n>-<topic>`, chacune partant de la branche d'intégration
`exec/base` (créée le 2026-10-18 depuis `v3.1/base`), et sa PR la visant
(question Q9, ADR 0132) ; le fil
fusionne lui-même quand la build et les tests passent en local, jamais dans
`main`. Les ADR de la
phase sont écrits et acceptés avant le code (`exec/0-adr`), comme pour les
phases précédentes.

| Branche | Contenu | Dépend de |
|---|---|---|
| `exec/0-adr` | **Fait** (branche `exec/1-adr`, 2026-10-18) : ADR 0124 à 0132 des décisions 1 à 10, mise à jour de ce plan et de l'index. | fin de la migration wasm v3 |
| `exec/1-remote-gateways` | Décision 1 : passerelles distantes `HackEvents` (abonnement côté worker, livraison par message, désabonnement à la fin du processus) et `Watch` ; tests de parité pour ces deux passerelles ; service d'exemple et `hackwebd` en worker ; chargement à la demande de toute assembly d'app dans le pool ; une commande d'un projet d'exemple tiers exécutée en worker. | `0` |
| `exec/2-process-limits` | Décision 2 : contrat de limites dans Simulation Abstractions, lecture de `/etc/security/limits.conf` (et `limits.d/`), héritage au `fork`, `ulimit` et `prlimit`, `/proc/<pid>/limits`, `SIGXCPU`, application stricte en worker et au mieux sur le fil principal, sortie 152 (`SIGXCPU`) et 137 ; erreur `EMFILE`/`EAGAIN` pour `NOFILE`/`NPROC` par les passerelles. | `0` |
| `exec/3-oom-killer` | Décisions 3 et 4 : réglage d'activation dans `/etc` (désactivé par défaut), seuil de mémoire disponible, `oom_score`, `oom_score_adj` (fichier modifiable, `-1000` exempte, réservé à l'administrateur pour baisser), choix et mise à mort, ligne du noyau dans `/var/log/kern.log` et `syslog`, `dmesg`, notification à l'utilisateur pour une fenêtre tuée, garde du vrai navigateur, compteur `oom_kill` dans `/proc/vmstat`. | `2` |
| `exec/4-not-responding-ui` | **Fait** (2026-10-18, [`process-priority-and-unresponsive-windows.md`](../process-priority-and-unresponsive-windows.md)). Décisions 5 et 6 : fenêtre « (Not responding) » et boîte « L'application ne répond pas : Attendre \| Terminer l'application », `nice`/`renice` et colonne « NI » de `ps`/`top`, ordre de la file du pool. | `0` |
| `exec/5-background-jobs` | Décision 7, étape 1 : `HackBackground.RunAsync` (méthode statique ou classe de tâche, entrée et résultat sérialisables, progression, annulation), analyseur qui refuse une lambda qui capture, processus enfant de l'app avec son identité et ses droits, repli sur le fil principal quand il n'y a pas de worker (tests, hôte sans workers), exemple dans `Apps/Samples`, migration des traitements longs des apps de base, guide et référence du SDK. | `1` |
| `exec/6-remote-renderer-poc` | Décision 7, étape 2 : preuve de faisabilité d'un `Renderer` Blazor dans un worker, lots de rendu sérialisés vers le fil principal, événements du DOM renvoyés au worker ; mesure du coût. Résultat : un ADR « on y va » ou « on s'arrête ». Rien n'est livré aux apps si la preuve échoue. | `5` |
| `exec/7-window-workers` | Seulement si `6` réussit : `AppExecutionHost.Dedicated` permis pour une fenêtre sans interop JavaScript propre, gestion du clavier, du focus et des dialogues, tests sur la Calculatrice. **Retiré** : l'[ADR 0133](../adr/0133-window-rendering-in-worker-verdict.md) conclut « on s'arrête ». | `6` |
| `exec/8-server-wasm` | **Fait** (2026-10-18, [`server-hosting.md`](../server-hosting.md), [ADR 0137](../adr/0137-server-hosting-implementation-adjustments.md)) : fichiers publiés de l'Ecosystem servis tels quels. Décision 8 : `HackerOs.Server` sert l'OS en WebAssembly (rendu `InteractiveWebAssembly` ou fichiers publiés de l'Ecosystem, selon l'ADR), retrait du mode Interactive Server et de l'injection directe, l'OS appelle l'API du serveur par HTTP comme l'Ecosystem, connexion automatique au serveur qui a servi l'OS et icône d'état dans la zone de notification (ADR 0134), `build/verify-hosts.sh` et `HackerOs.HostSmoke.Tests` adaptés (workers et `/proc/hackeros` présents sur cet hôte), docs d'hébergement à jour. | `0` |
| `exec/9-cross-origin-isolation` | Décisions 9 et 10 : audit de compatibilité (iframes des sites ASP.Hack et du navigateur, fenêtres détachées, repli sur le réseau réel, workers de Monaco, téléchargements, PWA), en-têtes dans les trois hôtes et le service worker, repli, puis tubes partagés, appels synchrones, mémoire réelle par worker et minuteries. | `3`, `5` |
| `exec/10-final` | Publication du SDK 1.2 (ADR 0132), app de compatibilité 1.1 ; vérification de bout en bout, documents à jour (`system-metrics.md`, `task-manager.md`, `worker-processes.md`, `app-sdk.md`, `sdk-reference.md`, `linux-commands.md`, `virtual-proc-and-dev.md`, `building-and-testing.md`), index, cette liste. | tous |

Ordre :

1. `exec/0-adr`.
2. En parallèle : `exec/1-remote-gateways`, `exec/2-process-limits`,
   `exec/4-not-responding-ui`, `exec/8-server-wasm`.
3. En parallèle : `exec/3-oom-killer`, `exec/5-background-jobs`.
4. `exec/6-remote-renderer-poc`, puis `exec/7-window-workers` s'il est décidé.
5. `exec/9-cross-origin-isolation` (il donne la mesure réelle par worker à
   l'OOM killer, et doit vérifier les fenêtres en worker s'il y en a).
6. `exec/10-final`.

### 5.1 Ce que chaque chantier fait pour les apps existantes

| Chantier | Migration des apps existantes |
|---|---|
| `exec/1-remote-gateways` | Revoir chaque app en `MainThread` pour cause de passerelle manquante (service d'exemple, `hackwebd`) et la passer en worker quand la raison disparaît ; mettre à jour le commentaire de raison des autres. |
| `exec/2-process-limits` | Chaque commande et service traite `EMFILE`/`EAGAIN` comme une erreur d'E/S ordinaire (message et code de sortie). Les commandes longues ont déjà `CheckpointAsync` (phase 5) : vérifier qu'une limite CPU les arrête proprement. |
| `exec/3-oom-killer` | Les apps qui gardent un état (éditeurs, CodeEditor, explorateur, Paramètres) gardent leur brouillon de récupération quand elles sont tuées par l'OOM killer comme par « Force kill » ; les services système critiques reçoivent un `oom_score_adj` négatif dans leur définition. |
| `exec/4-not-responding-ui` | Aucune modification d'app ; le gestionnaire de tâches et `top` affichent « NI ». |
| `exec/5-background-jobs` | Inventaire des traitements longs des apps à fenêtre (recherche du CodeEditor, zip et copies de l'explorateur, calculs de Hack Paint) ; chacun passe par `HackBackground.RunAsync` ou garde sa place avec la raison écrite. |
| `exec/7-window-workers` | Inventaire des apps à fenêtre sans interop JavaScript propre ; celles qui passent les tests en worker changent d'`AppExecutionHost`, les autres gardent `MainThread` avec la raison. |
| `exec/8-server-wasm` | Aucune modification d'app ; les apps qui utilisaient l'injection directe des services du serveur (ADR 0036) passent par l'API HTTP. |
| `exec/9-cross-origin-isolation` | Chaque app qui charge une ressource externe (navigateur, CodeEditor) est vérifiée avec l'isolation activée ; ce qui casse est corrigé dans le chantier ou empêche d'activer l'isolation par défaut. |

### 5.2 Vérification des hôtes

`build/verify-hosts.sh` (phase 5) gagne, au fil des chantiers :

- un parcours avec des limites basses (`ulimit -t 1` puis `stress`, sortie 152) ;
- un parcours de l'OOM killer avec une `MemTotal` simulée réduite ;
- sur l'hôte Server servi en WebAssembly, `/proc/hackeros/workers` présent et `kill -9` d'une boucle ;
- un parcours de l'Ecosystem publié avec et sans isolation cross-origin, avec
  `crossOriginIsolated` vérifié dans la page.

## 6. Questions tranchées

Posées à Benoit le 2026-10-17 dans le fil « Plan : suites de l'étape 5 », avec
contexte et conséquences de chaque choix ; ses réponses (05:55 à 06:19 UTC) :

| # | Question | Réponse de Benoit | Conséquence dans ce plan |
|---|---|---|---|
| Q1 | Limites d'un processus sur le fil principal | **Au mieux** | Stricte en worker ; sur le fil principal, demande d'arrêt puis escalade (ADR 0069), dit dans `/proc/<pid>/limits` et la documentation. |
| Q2 | Mémoire totale de l'OOM killer | **Simulée (estimation si possible)** | `MemTotal` du profil simulé ; occupation mesurée ou estimée au plus juste (décision 3). |
| Q3 | OOM killer actif par défaut | **Désactivé** ; ouvrir à la place une fenêtre comme les navigateurs : « L'application ne répond pas : Attendre \| Terminer l'application » | OOM killer à activer dans `/etc` ; boîte de la décision 6. |
| Q4 | Quantum imposé | **Non** | Décision 5. |
| Q5 | Apps à fenêtre dans un worker | Une fonction du SDK qui exécute la charge lourde dans un autre thread, l'inverse de `Invoke` ; puis **garder l'essai** de rendu en worker | Décision 7, chantiers `exec/5` et `exec/6`. |
| Q6 | Isolation cross-origin | **Optionnelle, active par défaut si l'audit passe** | Décisions 9 et 10. |
| Q7 | Isolation sur l'hôte Server | **Server = OS en WebAssembly + API** (Benoit pensait que le serveur hébergeait déjà un client WebAssembly) | Décision 8, chantier `exec/8-server-wasm` ; remplace les ADR 0027 et 0036. |
| Q8 | Threads .NET WebAssembly | **Non** | Décision 10. |
| Q9 | Branches | **`exec/<n>-<sujet>`** | Section 5 ; intégration sur `exec/base` (carte « Lancer maintenant les suites de l'étape 5 », réponse « Oui », 2026-10-18). |

### 6.1 Questions nées de l'écriture des ADR (2026-10-18)

Posées à Benoit dans le fil « ADR des suites de l'étape 5 », avec le contexte
et les conséquences de chaque choix ; réponses du 2026-10-18 (ADR 0134).

| # | Question | Choix (⭐ recommandé) | Réponse de Benoit | ADR |
|---|---|---|---|---|
| E1 | Comment le serveur sert l'OS | Fichiers publiés de l'Ecosystem tels quels ⭐ \| Blazor Web App en `InteractiveWebAssembly` comme `test/test` | **A** | 0130 §2 |
| E2 | L'OS servi par le serveur se connecte-t-il à lui | Adresse préremplie, l'utilisateur choisit de se connecter ⭐ \| Rien de spécial, saisie manuelle | **A**, précisée : connexion automatique sans demande, icône d'état dans la zone de notification | 0134 (remplace 0130 §4) |
| E3 | Limites par défaut | Valeurs Linux pour `nofile` et `nproc`, CPU et mémoire illimités ⭐ \| Tout illimité | **A** | 0125 §6 |
| E4 | Publication du SDK 1.2 | Une fois, à la fin (`exec/10`) ⭐ \| Une mineure par chantier qui ajoute une API | **A** ; et **Aucun jeu sur ce repository** dans les docs et la mémoire | 0132 §2, 0135 |

## 7. ADR de la série

Écrits par `exec/0-adr` le 2026-10-18 (numéros 0124 à 0135 ; 0133 reste réservé) :

| ADR | Contenu | Complète ou remplace |
|---|---|---|
| [0124](../adr/0124-remote-event-and-watch-gateways.md) Passerelles distantes d'événements et de surveillance | décision 1 | remplace l'ADR 0072 §3 en partie, complète 0070 |
| [0125](../adr/0125-process-resource-limits.md) Limites par processus | décision 2 | remplace le principe « mesurer, pas contraindre » de la phase 5 pour les limites, complète 0067, 0068, 0069 |
| [0126](../adr/0126-oom-killer.md) OOM killer | décisions 3 et 4 | remplace l'ADR 0068 §3 |
| [0127](../adr/0127-priority-and-not-responding-dialog.md) Priorité et fenêtre sans réponse | décisions 5 et 6 | complète l'ADR 0067 §4 |
| [0128](../adr/0128-background-jobs-hack-background.md) Tâches lourdes en arrière-plan (`HackBackground`) | décision 7, étape 1 | complète l'ADR 0070 §1 |
| [0129](../adr/0129-window-rendering-in-worker-trial.md) Essai du rendu de fenêtre dans un worker, critères ; **0133** (réservé) décision après l'essai | décision 7, étape 2 | 0133 remplace l'ADR 0070 §1 (fenêtres) si on y va |
| [0130](../adr/0130-server-serves-webassembly-os-and-api.md) `HackerOs.Server` servi en WebAssembly | décision 8 | remplace l'ADR 0027 et l'ADR 0036 ; rend sans objet l'ADR 0066 §3 (défaut Server), 0067 §6, 0070 §6, 0072 §7 |
| [0131](../adr/0131-cross-origin-isolation.md) Isolation cross-origin | décisions 9 et 10 | complète l'ADR 0072 §2 (tubes) et l'ADR 0068 (mémoire réelle par worker) |
| [0132](../adr/0132-exec-series-sdk-1-2-and-merge-rules.md) Ajouts du SDK 1.2, branches et barre de fusion | sections 5 et 8 | applique les ADR 0114 et 0117 |
| [0133](../adr/0133-window-rendering-in-worker-verdict.md) Verdict de l'essai de rendu en worker : on s'arrête | décision 7, étape 2 (`exec/6`) | ne remplace pas l'ADR 0070 §1 ; `exec/7` retiré |
| [0134](../adr/0134-automatic-server-connection-and-status-icon.md) Connexion automatique au serveur et icône d'état | réponses E1 à E4 | remplace l'ADR 0130 §4 |
| [0135](../adr/0135-no-game-in-this-repository.md) Aucun jeu sur ce repository | réponse E4 | remplace l'ADR 0092 |

## 8. Liste des tâches

### Préparation
- [x] Écrire ce plan (2026-10-17, fil « Plan : suites de l'étape 5 »).
- [x] Faire valider le plan et trancher les questions Q1 à Q9 avec Benoit (2026-10-17, section 6).
- [x] Attendre la fin de la migration wasm v3 (terminée le 2026-10-17, puis série v3.1).
- [x] Créer `exec/base` depuis `v3.1/base` (2026-10-18).
- [x] `exec/0-adr` : écrire et faire accepter les ADR de la section 7 (0124 à 0132, 2026-10-18).
- [x] Réponses de Benoit aux questions E1 à E4 (section 6.1, ADR 0134 et 0135).

### Passerelles distantes
- [x] `HackEvents` et `Watch` en worker, tests de parité (`exec/1-remote-gateways`, ADR 0138).
- [x] Service d'exemple et `hackwebd` en worker (`exec/1-remote-gateways`, ADR 0138).
- [x] Pool ouvert à toute assembly de commande ou de service (`exec/1-remote-gateways`, ADR 0138).

### Limites et OOM killer
- [x] Contrat de limites, `limits.conf`, héritage, `ulimit`, `prlimit`, `/proc/<pid>/limits` (`exec/2-process-limits`, ADR 0139).
- [x] `SIGXCPU`, limite mémoire, `EMFILE`/`EAGAIN` (`exec/2-process-limits`, ADR 0139).
- [x] `oom_score`, `oom_score_adj`, choix et mise à mort, journal du noyau, `dmesg` (`exec/3-oom-killer`, ADR 0142).
- [x] Garde du vrai navigateur (`exec/3-oom-killer`, ADR 0142).

### Fenêtres
- [x] « (Not responding) » et « Attendre \| Terminer l'application », `nice`/`renice` (`exec/4-not-responding-ui`, ADR 0127, PR #183).
- [x] `HackBackground.RunAsync`, analyseur, exemple, migration des apps de base (`exec/5-background-jobs`, ADR 0140).
- [x] Preuve de faisabilité du rendu en worker et ADR de décision (`exec/6-remote-renderer-poc`, ADR 0133 : on s'arrête).
- [x] ~~Fenêtres en worker (si décidé).~~ Sans objet : l'ADR 0133 conclut « on s'arrête » (mémoire d'un worker de fenêtre 66,5 Mio pour 40 Mio permis).

### Hôte Server
- [x] OS servi en WebAssembly, API seulement côté serveur, retrait d'Interactive Server, vérification des hôtes adaptée (`exec/8-server-wasm`, ADR 0134 et 0137).

### Isolation cross-origin
- [x] Audit de compatibilité (`exec/9-cross-origin-isolation`, ADR 0143 : passé, `auto = on`).
- [x] En-têtes dans les hôtes et le service worker, repli (`exec/9-cross-origin-isolation`, ADR 0143).
- [x] Tubes partagés, appels synchrones, mémoire réelle par worker, minuteries (`exec/9-cross-origin-isolation`, ADR 0143).

### Fin
- [x] Publication du SDK 1.2 et app de compatibilité 1.1 (`exec/10-final`, ADR 0132).
- [x] `build/verify-hosts.sh` étendu (section 5.2, fait au fil des chantiers), vérification finale (section 11), documents et index (`exec/10-final`).

## 9. Hors périmètre

- Tout ce que planifie la migration wasm v3 (trimming, localisation, gabarits
  `dotnet new`, sauvegarde et restauration, etc.).
- Plusieurs utilisateurs simultanés sur l'API de `HackerOs.Server`, au-delà de
  ce qu'elle fait déjà.
- Déplacer le shell (`hsh`) et ses scripts dans un worker (ADR 0072 §1 reste
  valable).
- Compteurs de CPU et de mémoire persistants d'un démarrage à l'autre.

## 10. Références

- [`execution-isolation-plan.md`](execution-isolation-plan.md) (phase 5) et ses
  ADR [0066](../adr/0066-optional-resource-accounting.md),
  [0067](../adr/0067-measured-cpu-time.md),
  [0068](../adr/0068-measured-process-memory.md),
  [0069](../adr/0069-graceful-stop-and-checkpoints.md),
  [0070](../adr/0070-worker-hosted-processes.md),
  [0071](../adr/0071-always-ready-worker.md),
  [0072](../adr/0072-worker-processes-implementation-adjustments.md).
- [`worker-processes.md`](../worker-processes.md),
  [`cpu-accounting.md`](../cpu-accounting.md),
  [`memory-accounting.md`](../memory-accounting.md),
  [`resource-accounting.md`](../resource-accounting.md),
  [`ecosystem-rebuild-plan.md`](ecosystem-rebuild-plan.md).

## 11. Vérification finale

Faite par `exec/10-final` le 2026-10-18 sur `exec/base` (avec `exec/1` à
`exec/9`, PR #177 à #186) plus ce chantier. GitHub Actions ne tournant pas, la
barre est la suite locale de l'ADR 0132 §4 (.NET 10, `NuGetAudit=false`,
`MSBUILDDISABLENODEREUSE=1`, suites E2E en série).

| Étape | Résultat |
|---|---|
| SDK 1.2 | `build/tools/ship-public-api.sh` : 795 lignes versées de `Unshipped` dans `Shipped` (une ligne `*REMOVED*`, constructeur d'`EcosystemBootCoordinator`), `HackerOsVersion` = `1.2.0`, manifestes générés à `minimumVersion: 1.2.0` ([`sdk-packages.md`](../../framework/sdk-packages.md#sdk-12)) |
| App de compatibilité 1.1 | `Contoso.Sdk11Compat.1.1.0.nupkg`, compilée contre les paquets du sommet de `v3.1/base` (titre de fenêtre, `HackUserAccounts.ValidateNewPassword`) |
| Build Debug et Release de `HackerOs.sln` | 0 avertissement, 0 erreur |
| Tests unitaires et de composants (42 projets) | 3761 réussis, 0 échec (dont `HackerOs.SdkCompatibility.Tests` 10/10 : apps 1.0.0 et 1.1.0) |
| `HackerOs.E2E.Tests` | 18/18 |
| `HackerOs.UI.E2E.Tests` | 27/27 ; `WindowKeyboardCommandsTests` a échoué une fois, le hôte `test/test` n'ayant pas répondu dans le délai du harnais (instabilité connue sous charge), puis réussi seul deux fois |
| `HackerOs.Pwa.E2E.Tests` | 6/6 après correction de trois tests : depuis `exec/9`, le service worker garde le cache `hackeros-isolation`, indépendant des versions (ADR 0143), que les tests ne connaissaient pas |
| `HackerOs.Templates.E2E.Tests` | 6/6, dont l'hôte généré avec les apps de compatibilité 1.0.0 et 1.1.0 dans Chromium |
| `build/publish-trimmed.sh` | PASS (136 fichiers `.wasm`, 27,2 Mio, 8,2 Mio en Brotli) |
| `build/verify-hosts.sh` | PASS sur les 9 parcours : `ecosystem`, `ecosystem-build-off`, `ecosystem-settings-off`, `ecosystem-limits-off`, `ecosystem-oom`, `ecosystem-isolated`, `ecosystem-isolation-off`, `test-host`, `server` |

Documents relus et mis à jour : `system-metrics.md`, `task-manager.md`,
`worker-processes.md`, `app-sdk.md`, `sdk-reference.md`,
`sdk-developer-guide.md`, `app-model.md`, `linux-commands.md`,
`virtual-proc-and-dev.md`, `building-and-testing.md`, `sdk-packages.md`,
`building-on-hackeros.md`, `project-overview.md`, index `README.md`.

Restent ouvertes, avec le choix recommandé appliqué en attendant Benoit : les
cartes des fils « Exec 1 » (publication depuis un worker), « Exec 4 »
(`sudo nice`), « Exec 5 » (délai de grâce, PID de la tâche ; tranchées le
2026-10-19 : 5 s et pas de PID, ADR 0147), « Exec 6 »
(arrêt de l'essai de rendu en worker, `exec/7` n'a pas lieu) et « Exec 9 »
(quatre cartes sur l'isolation). La fusion d'`exec/base` vers `main` est la
décision de Benoit : il l'a demandée le 2026-10-18 (« on peut tout ramener sur
main »), et elle a été faite le même jour après une nouvelle passe de la barre
locale ; `exec/base` contenait déjà `web/base`, `v3/base` et `v3.1/base`, et
`rebuild/base` y a été fusionnée sans changer de fichier (sa PR #133 était
déjà portée par la PR #148).

