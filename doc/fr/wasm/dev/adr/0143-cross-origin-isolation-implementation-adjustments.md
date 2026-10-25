# ADR 0143 : ajustements de la mise en œuvre de l'isolation cross-origin

## Statut

Accepté le 2026-10-18, chantier `exec/9-cross-origin-isolation`.

- **Complète :** ADR 0131 (isolation cross-origin optionnelle). Précise ce que
  le code a dû trancher et s'écarte de l'ADR 0131 sur quatre points : `MemSource`
  (§6), ce que portent les anneaux (§4), l'Ecosystem lancé par `dotnet run` (§3)
  et la suite de parité (§9).
- **Doc :** [`worker-processes.md`](../worker-processes.md) (section
  « Isolation cross-origin » et compte rendu de l'audit).

## Contexte

L'ADR 0131 fixe le réglage `HackerOs:CrossOriginIsolation`, qui pose les
en-têtes, l'audit, et ce que l'isolation apporte : anneaux partagés, appels
synchrones en worker, mémoire mesurée par le navigateur, minuteries fines. En
l'écrivant, plusieurs détails sont apparus : où vit la constante d'audit,
comment la page apprend pourquoi elle n'est pas isolée, comment garder l'ordre
des messages entre anneau et `postMessage`, comment un worker attend sans que
le fil principal attende, et comment attribuer la mémoire à un worker.

## Décision

### 1. Constante d'audit et réglage

- `CrossOriginIsolationPolicy.AuditPassed` (`HackerOs.Platform.Core`,
  espace `Isolation`) est la constante de build de l'ADR 0131 §1. Le service
  worker en garde une copie (`crossOriginIsolationAuditPassed`), puisqu'il ne
  peut pas lire un assembly ; un test unitaire vérifie que les deux disent la
  même chose.
- L'audit est passé (§10) : `AuditPassed = true`, donc `auto` vaut `on`.
- `Parse` lit `auto`, `on`/`true`, `off`/`false` ; toute autre valeur vaut
  `auto`. Les trois hôtes livrent `"CrossOriginIsolation": "auto"`.

### 2. Qui pose les en-têtes, et ce qu'ils couvrent

- **Service worker de l'Ecosystem** : il lit le réglage dans `appsettings.json`
  (en cache, sinon sur le réseau) et ajoute `COOP: same-origin`,
  `COEP: credentialless` et `Cross-Origin-Resource-Policy: same-origin` aux
  réponses de même origine qu'il sert (navigation, assets, `_framework`),
  jamais aux réponses opaques. Il relit le réglage à chaque navigation.
- **Repli `require-corp`** : la page demande au service worker ce qu'il a
  envoyé (`COI_STATE`). Si le service worker envoie `credentialless` et que la
  page n'est pas isolée, la page lui demande `require-corp` (`COI_STRICT`), le
  choix est gardé dans le cache du service worker, et la page se recharge
  **une seule fois** (garde dans `sessionStorage`).
- **`test/test` et `HackerOs.Server`** : un intergiciel posé juste après
  `builder.Build()` ajoute `COOP` et `COEP: credentialless` à toutes les
  réponses (`CrossOriginIsolationPolicy.ServerHeaders`), selon le réglage de
  l'hôte. Ces deux hôtes n'ont pas de repli `require-corp`.
- **Rechargement unique du premier chargement** : c'est celui qui existait
  déjà dans `index.html` (`controllerchange`), sans code nouveau.

### 3. Écart : l'Ecosystem par `dotnet run` n'est pas isolé

`index.html` n'enregistre pas le service worker sur `localhost` ni
`127.0.0.1`. Sur ces adresses, rien ne pose les en-têtes, donc l'Ecosystem lancé
par `dotnet run` (ou servi par un serveur statique en local) n'est pas isolé :
`/proc/hackeros/workers` dit `missing-headers` et l'OS fonctionne comme avant.
Pour essayer l'isolation, on sert la publication sur `127.0.0.2` (contexte
sécurisé où le service worker s'enregistre), ou on lance `test/test`. Question 2
ci-dessous.

### 4. Écart : les anneaux portent tous les messages du protocole

L'ADR 0131 §4 parle des flux standard. Les anneaux portent en fait **tous** les
messages texte du protocole (sorties, accusés, appels, résultats), un anneau
SPSC de 1 Mio par sens dans un `SharedArrayBuffer`, avec trames de longueur.

- **Ordre** : le lecteur vide son anneau avant de traiter tout message reçu par
  `postMessage`. Un message trop gros pour la place libre part par
  `postMessage`, et l'ordre ne change jamais.
- **Personne ne bloque sur un anneau** : un anneau plein retombe sur les
  messages ; le crédit des sorties (`OutputAck`, ADR 0070) reste le contrôle de
  flux. Même crédit, même `SIGPIPE`, même `/proc/<pid>/fd` qu'avant.
- **Sonnette** : l'écrivain envoie un petit `postMessage` seulement quand
  l'anneau était au repos.
- La page écrit dans l'anneau vers le worker seulement après le message
  `ready` du worker qui annonce `rings: true` ; un worker qui n'a pas pu ouvrir
  ses anneaux reste aux messages.

### 5. Appels synchrones

- Une case partagée par worker (en-tête `[état, appel attendu, longueur,
  appel répondu]`, 1 Mio de réponse). Le worker marque l'appel attendu, l'envoie
  (`"s": true` dans le message `call`), puis attend avec `Atomics.wait`. La page
  n'attend jamais : elle écrit la réponse et réveille le worker
  (`ISynchronousWebWorker.AnswerSynchronousCall`).
- **Délai** : 10 s (`WorkerRpcClient.SynchronousCallTimeout`), puis
  `TimeoutException`. Une réponse en retard trouve la case au repos et est
  jetée. Une réponse de plus de 1 Mio donne une `IOException` dans le worker.
- **Membres** : ceux que l'ADR 0124 §5 refusait, `ListProcesses` (nouvelle
  opération `proc.list`) et `Kill` d'un autre processus. Les autres membres
  synchrones gardent leur refus. Sans isolation, le refus et son message ne
  changent pas.
- **Exemple** : `sample-cmd --processes` et `sample-cmd --kill <pid>` appellent
  ces membres ; `HostSmoke` s'en sert.

### 6. Écart : `MemSource` reste `measured`

L'ADR 0131 §4 dit que `MemSource` indique la mémoire mesurée par le navigateur.
`MemSource` de `/proc/<pid>/status` garde `measured` : le gestionnaire de
tâches et `HostSmoke` comparent cette valeur exactement, et la mémoire d'un
processus reste une estimation en couches (ADR 0068) même quand celle du worker
vient du navigateur. Le détail est dans `/proc/hackeros/workers`
(`memory-measure: browser|estimated`). Question 1 ci-dessous.

- **Attribution** : l'URL de chaque worker porte `?hackeros-worker=N` ; le
  service worker l'ignore pour son cache (`ignoreSearch`). Le pool demande
  `performance.measureUserAgentSpecificMemory()` toutes les 20 s
  (`WorkerPoolOptions.MemoryMeasurePeriod`) par `IWebWorkerMemoryMeter`.
- Une mesure vaut pendant trois périodes ; sans elle, on revient à la mémoire
  du battement de cœur. La comptabilité par processus et l'OOM killer la
  reçoivent par `WorkerConnection.RuntimeMemoryBytes` (ADR 0126 §6). La garde
  du navigateur compare toujours le tas géré (ADR 0142 §4).

### 7. Minuteries

Rien à brancher : les tranches de l'ADR 0067 lisent `Stopwatch.GetTimestamp`,
c'est-à-dire `performance.now()`, que le navigateur arrondit à 5 µs dans une
page isolée au lieu de 100 µs. La sonde mesure la résolution réelle et
`/proc/hackeros/workers` l'affiche (`timer-resolution-us`).

### 8. Ce que la page dit, et où

- Au démarrage, `crossOriginIsolation.js` (`HackerOs.Infrastructure.Browser`)
  renvoie ce qu'il a trouvé ; `CrossOriginIsolationProbe.Read` en fait un
  `CrossOriginIsolationStatus`. Raisons, dans l'ordre : `insecure-context`,
  `setting`, `browser` (le service worker a posé les en-têtes et le navigateur
  les a ignorés), `missing-headers` (rien ne les a posés) ; `not-browser` hors
  navigateur.
- `/var/log/syslog` reçoit une ligne `isolation: …` (catégorie `kernel`).
- `/proc/hackeros/workers` commence par `cross-origin-isolated`, puis
  `isolation-reason`, `isolation-setting`, `embedder-policy`, `shared-pipes`,
  `sync-calls`, `memory-measure`, `timer-resolution-us`, avant `capacity`.
- Diagnostic lit ce fichier par `HackFile` et affiche un badge « Isolé » ou
  « Non isolé : » avec la raison, toutes les lignes dans l'infobulle. Lire
  `/proc` demande `filesystem.system.read`, capacité dangereuse : Diagnostic,
  déjà soumis à l'approbation d'un administrateur pour
  `system.storage.inspect`, la déclare en plus, et l'administrateur l'approuve
  au lancement suivant (ADR 0049). Sans elle, ou sans pool de workers, pas de
  badge.

### 9. Écart : la parité sous anneaux passe par `HostSmoke`

Les anneaux sont du JavaScript : la suite de parité des commandes, qui tourne
dans des workers de bouclage .NET, ne peut pas les exercer. À la place :
`sharedRing.js` a ses tests sous Node (ordre, bouclage, anneau plein, case
synchrone), les appels synchrones ont des tests de bouclage, et `HostSmoke`
rejoue sous isolation un tube long (`seq 30000 | wc -l`) et
`sample-cmd --processes` dans les parcours isolés de `verify-hosts.sh`
(`ecosystem-isolated`, `test-host`, `server`) et non isolés (les parcours de
l'Ecosystem sur `127.0.0.1`, `ecosystem-isolation-off`).

### 10. Audit

Fait dans Chromium avec l'isolation (compte rendu dans
[`worker-processes.md`](../worker-processes.md)) : rien n'a cassé, donc
`auto = on`. La suite UI E2E entière tourne isolée, puisque `test/test` pose
maintenant les en-têtes.

## Questions posées à Benoit

1. **`MemSource` quand le navigateur mesure le worker.** A (appliqué) :
   `measured` reste, le détail est dans `/proc/hackeros/workers`. B : nouvelle
   valeur `browser`, le gestionnaire de tâches et `HostSmoke` sont adaptés.
2. **Isolation de l'Ecosystem lancé en local (`dotnet run`).** A (appliqué) :
   non isolé, comme avant ; on essaie sur `127.0.0.2` ou avec `test/test`.
   B : enregistrer le service worker aussi sur `localhost`, au risque de servir
   des fichiers en cache pendant le développement.
3. **Navigateur sans `credentialless` (Safari).** A (appliqué, ADR 0131 §2) :
   repli `require-corp`, la page est isolée mais les images externes sans CORP
   des pages du navigateur simulé sont bloquées. B : pas de repli, la page reste
   non isolée et garde ces images.

## Conséquences

- `build/verify-hosts.sh` gagne `ecosystem-isolated` et
  `ecosystem-isolation-off` (sur `127.0.0.2`) et vérifie l'isolation de chaque
  parcours (`HACKEROS_SMOKE_EXPECT_ISOLATION`). Un parcours qui modifie
  `appsettings.json` recalcule son empreinte dans `service-worker-assets.js`.
- Docs : [`worker-processes.md`](../worker-processes.md),
  [`memory-accounting.md`](../memory-accounting.md),
  [`cpu-accounting.md`](../cpu-accounting.md),
  [`building-and-testing.md`](../building-and-testing.md),
  [`server-hosting.md`](../server-hosting.md),
  [`virtual-proc-and-dev.md`](../virtual-proc-and-dev.md).

## Références

- ADR 0067, 0068, 0070, 0124, 0126, 0130, 0131, 0142.
