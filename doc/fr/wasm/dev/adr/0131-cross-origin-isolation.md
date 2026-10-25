> [English version](../../../../en/wasm/dev/adr/0131-cross-origin-isolation.md)

# ADR 0131 : isolation cross-origin optionnelle (COOP/COEP), tubes partagés et mesures fines

## Statut

Accepté le 2026-10-18.

- **Plan :** [`execution-followups-plan.md`](../plans/execution-followups-plan.md),
  décisions 9 et 10 (réponses Q6 « optionnelle, active par défaut si l'audit
  passe » et Q8 « pas de threads .NET ») ; mis en œuvre par
  `exec/9-cross-origin-isolation`, après `exec/3-oom-killer` et
  `exec/5-background-jobs`.
- **Complète :** l'ADR 0072 §2 (tubes par messages), l'ADR 0068 (mémoire d'un
  worker), l'ADR 0067 (précision des tranches), l'ADR 0124 §5 (appels
  synchrones refusés en worker), l'ADR 0126 §6.

## Contexte

Sans les en-têtes `Cross-Origin-Opener-Policy` et
`Cross-Origin-Embedder-Policy`, `crossOriginIsolated` est faux : pas de
`SharedArrayBuffer`, pas de `performance.measureUserAgentSpecificMemory()`,
`performance.now()` arrondi à 100 µs. Les workers parlent à l'OS par
`postMessage` seulement, ce qui interdit tout appel synchrone d'un worker vers
l'OS. L'Ecosystem est une PWA qui doit marcher sur un hébergement statique
qui ne permet pas de poser des en-têtes, et hors ligne.

## Décision

### 1. Réglage

`HackerOs:CrossOriginIsolation` dans `appsettings.json` de l'hôte : `auto`
(défaut), `on` ou `off`.

- `auto` vaut `on` si l'audit de §3 est passé pour cette version, `off`
  sinon. Le résultat de l'audit est une constante de build écrite par
  `exec/9`, avec le compte rendu dans
  [`worker-processes.md`](../worker-processes.md).
- L'OS décide **toujours** sur `crossOriginIsolated` dans la page, jamais sur
  le réglage seul : un en-tête perdu (proxy, navigateur) donne le repli exact.

### 2. Qui pose les en-têtes

- `COOP: same-origin` ; `COEP: credentialless` (une ressource tierce sans CORS
  est chargée sans cookies au lieu d'être bloquée), `require-corp` pour un
  navigateur qui ne connaît pas `credentialless`.
- **Ecosystem** : le service worker de la PWA (`service-worker.published.js`)
  ajoute les en-têtes aux réponses qu'il sert, navigation comprise ; au premier
  chargement sans service worker actif, la page s'enregistre puis se recharge
  **une fois**. Cela marche hors ligne et sur un hébergement statique.
- **`test/test` et `HackerOs.Server`** : un intergiciel pose les mêmes en-têtes
  (le service worker reste, comme pour l'Ecosystem servi par le serveur, ADR
  0130).
- `off` : aucun en-tête, et le service worker n'en ajoute pas.

### 3. Audit avant d'activer par défaut

`exec/9` vérifie avec l'isolation, et corrige dans le chantier ou garde
`auto = off` :

| Surface | Risque |
|---|---|
| Rendu HTML du navigateur simulé (iframe isolée, ADR 0075, 0079) et sites ASP.Hack | iframes et ressources incorporées sous COEP |
| Fenêtres détachées (`window.open`, ADR 0078) | lien avec la fenêtre ouvrante sous COOP |
| Repli sur le réseau réel et images externes du navigateur | ressources sans CORS |
| Workers de Monaco (CodeEditor), xterm.js (Terminal) | scripts et workers sous COEP |
| Téléchargements (`download.js`), sauvegarde `.hosbak` | URL `blob:` |
| Installation de la PWA, mise à jour du service worker | rechargement unique de §2 |

### 4. Ce que l'isolation apporte (décision 10)

- **Tubes en anneau** dans un `SharedArrayBuffer` entre chaque worker et la
  couche JavaScript de l'OS, à la place des messages pour les flux standard ;
  même crédit, même `SIGPIPE`, même `/proc/<pid>/fd`.
- **Appels synchrones** d'un worker vers l'OS par `Atomics.wait` **dans le
  worker seulement** (le fil principal n'attend jamais). Les membres
  synchrones refusés par l'ADR 0124 §5 (`Kill`, `ListProcesses`) deviennent
  possibles en worker quand l'isolation est active ; sans elle, ils restent
  refusés avec le même message.
- **Mémoire réelle** d'un worker par `measureUserAgentSpecificMemory()` là où
  le navigateur l'offre ; `MemSource` l'indique (ADR 0066), et l'OOM killer
  s'en sert (ADR 0126 §6).
- **Minuteries** de 5 µs pour les tranches de l'ADR 0067.
- **Pas de threads .NET WebAssembly** (`WasmEnableThreads`) : Blazor ne les
  prend pas en charge, et ils interdisent de bloquer le fil principal. Le
  `SharedArrayBuffer` sert seulement entre les workers et la couche JavaScript.

### 5. Visibilité

`/proc/hackeros/workers` commence par `cross-origin-isolated: yes|no` ;
Diagnostic l'affiche, avec la raison quand c'est `no` (réglage, en-tête
absent, navigateur).

## Conséquences

- `build/verify-hosts.sh` gagne un parcours de l'Ecosystem publié avec et sans
  isolation, `crossOriginIsolated` vérifié dans la page, et la suite de parité
  des commandes rejouée avec les tubes partagés.
- Les apps qui chargent une ressource externe (navigateur, CodeEditor) sont
  vérifiées avec l'isolation ; ce qui casse est corrigé ou empêche `auto = on`.
- Docs : [`worker-processes.md`](../worker-processes.md),
  [`memory-accounting.md`](../memory-accounting.md),
  [`cpu-accounting.md`](../cpu-accounting.md),
  [`building-and-testing.md`](../building-and-testing.md) (hébergement
  statique, en-têtes).

## Références

- ADR 0066, 0067, 0068, 0072, 0075, 0078, 0079, 0124, 0126, 0130.
- HTML : `crossOriginIsolated`, COOP, COEP `credentialless` ;
  `performance.measureUserAgentSpecificMemory()`.
