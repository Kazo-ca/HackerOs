# ADR 0162 : redémarrer quand le build a changé sous la page

## Statut

Accepté le 2026-10-22.

- **Question posée à Benoit** (2026-10-22, fil « mise à jour des packages
  NuGet ») : comment éviter qu'une app refuse de s'ouvrir après une
  recompilation. Choix proposés : (A) l'OS détecte le nouveau build et propose
  un redémarrage, (B) interdire le cache en développement, (C) documenter
  seulement. Réponse de Benoit le 2026-10-22 : « a et c ».

## Contexte

Les apps chargées à la demande (ADR 0113, `BuildKnownAssemblyLoaderRegistry`)
vont chercher leur `.wasm` au premier lancement, sous le nom à empreinte que
le `dotnet.js` de la page connaît depuis le démarrage
(`HackerOs.Apps.Settings.yesfevpgxl.wasm`). Si l'origine sert un autre build
entre-temps (F5 dans Visual Studio pendant qu'un onglet reste ouvert, hot
reload, déploiement d'une nouvelle version), ce fichier n'existe plus ou n'a
plus la bonne empreinte : le navigateur refuse la réponse (contrôle
d'intégrité) et le lancement échoue avec
`download '…/_framework/HackerOs.Apps.Settings.yesfevpgxl.wasm' failed 0
TypeError: Failed to fetch`. Rien n'expliquait à l'utilisateur qu'un
rechargement suffisait.

Le bandeau « Une mise à jour de HackerOS est prête » existait
(`EcosystemHostState.UpdateAvailable`) mais n'était jamais affiché et son
bouton était désactivé.

## Décision

### 1. Un échec de chargement déclenche une comparaison de builds

- `HostBuildUpdateMonitor` (Platform.Blazor) compare le build qui tourne avec
  celui que l'origine sert maintenant, par un `IHostBuildProbe`.
- L'identité d'un build est le nom à empreinte de `dotnet.js`
  (`HostBuildIdentity.FromMarkup`) : le build y intègre sa configuration de
  démarrage, donc ce nom change dès qu'un assemblage change. Le nom est lu
  dans l'import map de la page en cours, puis dans la page redemandée sans
  cache (`fetch(document.baseURI, { cache: 'no-store' })`).
- Une origine injoignable, une page sans `dotnet.js` à empreinte ou une sonde
  qui lève ne sont pas une mise à jour : l'erreur d'origine reste (hors ligne,
  serveur arrêté).
- `BuildKnownAssemblyLoaderRegistry` reçoit le moniteur par un nouveau
  constructeur. Quand le transport lève et que le build a changé, le résultat
  est `BuildKnownAssemblyLoadStatus.HostBuildChanged`, avec le message
  « HackerOS was updated while it was running: restart it to open this app. »
  Sans moniteur, rien ne change.

### 2. L'OS propose un redémarrage, comme après une mise à jour système

- Le moniteur lève `UpdatePending` une fois. `App.razor` (Ecosystem.Host)
  affiche alors le bandeau existant, complété par « Les apps ouvertes seront
  fermées. », et active son bouton « Redémarrer pour mettre à jour ».
- Le bouton passe par `SystemPowerService.RestartAfterAsync` : journaux vidés,
  session fermée (les apps reçoivent leur annulation), puis rechargement de la
  page. Aucune élévation n'est demandée : rien ne change sur la machine, on
  charge seulement les fichiers déjà installés.
- Le module `OS/HackerOs.Ecosystem.Host/wwwroot/hostBuild.js` lit l'import map
  et redemande la page ; la comparaison reste en C#.

### 3. Hors du code

Le piège et son correctif manuel (relancer l'app puis Ctrl+F5) sont décrits
dans [`common-pitfalls.md`](../old/common-pitfalls.md) §9. Un serveur de
développement resté allumé pendant la recompilation (`dotnet run` sans
redémarrage) sert un mélange des deux builds : la page redémarrée échoue alors
au démarrage, avec l'écran d'erreur existant. Il faut relancer le serveur, ce
que fait Visual Studio à chaque F5.

## Conséquences

- Après une recompilation ou un déploiement, la première app qui ne se charge
  plus affiche le bandeau au lieu d'un échec inexpliqué ; un clic redémarre sur
  le nouveau build.
- La PWA installée (service worker, ADR 0017) garde son propre flux de mise à
  jour ; le moniteur ne se déclenche que sur un échec de chargement réel.
- L'option B (pas de cache en développement) n'est pas retenue : elle demandait
  de remplacer le DevServer de Microsoft et ne couvrait pas l'onglet resté
  ouvert pendant la recompilation.
