> [English version](../../../../en/wasm/dev/adr/0206-serve-installed-app-assets.md)

# ADR 0206 : servir les ressources `wwwroot/` des apps installées

## Statut

Accepté le 2026-10-30 sur la recommandation (question Q1 posée à Benoit le même
jour, choix A recommandé ; une autre réponse donnera une nouvelle ADR). Issue
[#247](https://github.com/Kazo-ca/HackerOs/issues/247), chantier 2 du
[plan du Hack Store](../plans/hackeros-store.md), série `todo/base`.

- **Lève :** la première limite de l'[ADR 0182](0182-install-apps-from-github.md)
  (« les ressources `wwwroot/` d'une app installée ne sont pas servies »).
- **Complète :** [ADR 0098](0098-sdk-versions-packages-and-manifest-checks.md) §5 (empreinte vérifiée à
  la première lecture), [ADR 0131](0131-cross-origin-isolation.md) §2 (en-têtes
  COOP/COEP du service worker), [ADR 0017](0017-pwa-cache-and-offline-strategy.md) (cache du
  service worker).

## Contexte

- Une app compilée dans l'OS référence ses fichiers à
  `_content/<assemblage>/<chemin>` : Blazor publie le `wwwroot/` de chaque projet
  à cette adresse. Le serveur statique les sert.
- Une app installée par `hackpkg` arrive après la publication : ses fichiers sont
  dans IndexedDB, sous `/usr/lib/hackeros/apps/<id>/<version>/wwwroot/`. Le
  serveur ne les a pas. `AppAssetStore` les lit (icônes) mais une balise image ou
  script, un `import()` ou un `new Worker(...)` ne les trouvent pas.
- Une app qui enveloppe un projet JavaScript (moteur dans un Web Worker qui
  charge des scripts et un `.wasm` par des chemins relatifs) en a besoin. Le Hack
  Store (ADR 0188) aussi.
- `index.html` n'enregistre aucun service worker sur `localhost` et `127.0.0.1`
  (développement, et `HackerOs.Server` lancé en local).

## Décision

### 1. La même adresse qu'une app compilée

Un fichier `wwwroot/x/y.js` d'une app installée dont le point d'entrée est
`Contoso.Notes.dll` est servi à `_content/Contoso.Notes/x/y.js`, comme pour une
app compilée dans l'OS. Le code de l'app ne change pas : elle marche installée ou
compilée. Toutes les formes de référence marchent : balises, `import()`, `url()`
en CSS, `new Worker(...)`, `importScripts` et `fetch` relatifs dans le worker,
`WebAssembly.instantiateStreaming`.

### 2. Un cache servi par le service worker

- Cache Storage, nom `hackeros-installed-apps`, une entrée par fichier, clé =
  adresse absolue sans requête, avec `Content-Type` (d'après l'extension) et
  `Cross-Origin-Resource-Policy: same-origin`.
- Les deux fichiers du service worker de l'hôte chargent
  `_content/HackerOs.Infrastructure.Browser/installedAppAssets.sw.js`
  (`importScripts`). Pour une requête `GET` de même origine sous `_content/`, le
  worker répond depuis ce cache s'il a l'adresse, **avant** le cache de la
  version ; sinon la requête suit son chemin habituel. La requête est ignorée
  (`?hackeros-worker=N`).
- Le service worker publié ajoute ses en-têtes COOP/COEP à ces réponses comme aux
  autres (ADR 0131) : un worker lancé par une page isolée démarre.

### 3. Copie au premier lancement de l'app dans la session

- Avant de charger l'assemblage d'une app installée,
  `InstalledAppAssemblyTransport` appelle `InstalledAppAssetPublisher` : chaque
  ressource déclarée dans le manifeste est lue par `AppAssetStore` (empreinte
  SHA-256 vérifiée, ADR 0098 §5) puis copiée dans le cache. Une fois par app et
  par session.
- Seules les ressources déclarées dans `Assets` sont servies. Un fichier abîmé ou
  absent n'est pas servi, son ancienne copie est retirée, une ligne va dans
  `/var/log/syslog` ; l'app démarre quand même (comme une app compilée à qui il
  manque un fichier). `AppAssetStore` marque l'app abîmée, comme avant.
- La première copie de la session retire du cache toute adresse qu'aucune app
  installée ne déclare plus (app retirée ou mise à jour avant le redémarrage).

### 4. Sur un hôte de développement

Sur `localhost` et `127.0.0.1`, l'OS enregistre lui-même
`service-worker.js?mode=installed-apps` au premier lancement d'une app installée
qui a des ressources, et attend qu'il prenne la main (10 s au plus). Dans ce mode,
le worker ne remplit pas le cache de la version et ne répond qu'aux adresses des
apps installées ; il copie sur ces réponses les en-têtes COOP/COEP de la page
(lus une fois par `HEAD`). `index.html` ne le désinscrit pas. Ailleurs, le
service worker habituel (enregistré par `index.html`) suffit.

### 5. Sans service worker

Un navigateur sans service worker (ou qui le refuse) : rien n'est copié, une ligne
`apps: no service worker serves this page, the wwwroot/ files of <id> are not
served` va dans `/var/log/syslog`, l'app démarre sans ses fichiers. Pas de repli
par adresses `blob:` (choix B ou C de Q1).

### 6. Hôtes

Seul l'hôte WebAssembly charge des apps installées (ADR 0182) : la publication
n'existe que là. `HackerOs.Server` sert les mêmes fichiers d'OS, donc le même
service worker.

## Question posée à Benoit

**Q1. Comment servir le `wwwroot/` d'une app installée ?**
- **A (recommandé, retenu en attendant)** : service worker + Cache Storage,
  sections 1 à 5.
- **B** : adresses `blob:` rendues par une nouvelle API du SDK. Marche sans
  service worker, mais les chemins relatifs dans un JS, un CSS ou un worker
  cassent et l'app doit être écrite exprès.
- **C** : A, plus B en secours sans service worker.

## Conséquences

- Une app qui enveloppe un projet JavaScript peut être installée par `hackpkg`
  puis par le Hack Store.
- Le code de l'app installée tourne toujours dans l'origine de l'OS (ADR 0182,
  Conséquences) ; ses scripts aussi.
- Le cache est une copie : la vérité reste le fichier sous `/usr/lib/hackeros/apps`,
  relu et revérifié à chaque session.
- Sur `localhost`, un service worker reste enregistré après la première app
  installée lancée ; il ne sert que les apps installées.
- La sauvegarde système (ADR 0099) n'a rien de nouveau à inclure : le cache se
  reconstruit.

## Tâches

- [x] `installedAppAssets.sw.js` et `installedAppAssets.js` (Infrastructure.Browser), import dans les deux service workers.
- [x] `BrowserInstalledAppAssets` (JSImport), `IInstalledAppAssetCache`, `BrowserInstalledAppAssetCache`, `InstalledAppAssetPublisher`, `InstalledAppAssetContentTypes`.
- [x] Appel depuis `InstalledAppAssemblyTransport` ; `index.html` garde le worker du mode `installed-apps`.
- [x] Tests unitaires (`InstalledAppAssetPublisherTests`).
- [x] Paquet de test `Tests/fixtures/installed-app` et tests de bout en bout dans `HackerOs.TrimmedPublish.E2E.Tests` (installation par `hackpkg`, module, worker, `importScripts`, image ; service worker publié).
- [x] Docs : `app-installation.md`, plan du Hack Store, `features.md`.
