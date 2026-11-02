> [English version](../../../../en/wasm/dev/adr/0225-serve-installed-app-files-to-every-page.md)

# ADR 0225 : servir les fichiers des apps installées à chaque page

## Statut

Accepté le 2026-11-02 (correctif demandé par Benoit, branche
`installed-apps/serve-uncontrolled-page`).

- **Complète :** [ADR 0206](0206-serve-installed-app-assets.md) (fichiers
  `wwwroot/` des apps installées servis par le service worker). Remplace sa
  règle « sans service worker, les fichiers ne sont pas servis pour la
  session » et sa règle de ménage du cache au premier lancement.
- **Voir aussi :** [ADR 0214](0214-hack-store-hot-add-signing-transactions.md) (ajout d'une app sans
  redémarrage), [ADR 0223](0223-restart-onto-the-new-version.md) (redémarrage
  sur la nouvelle version).

## Contexte

Benoit (2026-11-02 00:46Z), sur `https://hackos.kazo.ca`, après avoir installé
une app « pour moi seulement » sans redémarrer : « Failed to fetch dynamically
imported module: …/_content/Kazo.Chess/js/chess-rules.js ». Le fichier est
déclaré dans le manifeste de l'app ; le serveur ne l'a pas, c'est le service
worker qui doit le servir depuis le cache `hackeros-installed-apps`. Benoit a vu
l'erreur après un Ctrl+F5 **et** dans une session sans rechargement.

Deux causes, trouvées à la lecture du code et reproduites dans Chromium pour la
première :

1. **Page chargée avec Ctrl+F5.** Le navigateur contourne le service worker pour
   ce chargement : la page n'a pas de contrôleur, même si un worker est actif.
   `prepare()` attendait 10 s un `controllerchange` qui ne vient jamais (un
   worker déjà actif ne s'active plus, donc ne réclame pas la page), puis
   l'OS retenait « pas de service worker » **pour toute la session** : aucune
   app installée n'avait ses fichiers.
2. **Cache partagé entre onglets.** Le cache est commun à tous les onglets et
   fenêtres de l'OS, mais le ménage du premier lancement retirait toute adresse
   qu'aucune app **du catalogue de cette page** ne déclare. Un onglet ouvert
   avant l'installation (autre écran, autre fenêtre) effaçait donc, au premier
   lancement d'une de ses apps installées, les fichiers de l'app que l'autre
   onglet venait d'installer ; et celui-ci ne les recopie pas (une fois par
   session).

## Décision

1. **Prendre le contrôle d'une page qui n'en a pas.** Si aucun worker ne
   contrôle la page mais qu'un worker est actif, `installedAppAssets.js` lui
   envoie `HACKEROS_CLAIM_FOR_INSTALLED_APPS` avec la version de la version
   publiée que le serveur livre maintenant (`version` de
   `service-worker-assets.js`, lu sans cache : c'est celle d'une page chargée
   avec Ctrl+F5). Le worker (`installedAppAssets.sw.js`) appelle
   `clients.claim()` seulement si c'est la version qu'il met en cache, ou s'il
   ne garde aucun cache de version (mode `installed-apps`, développement). Une
   page d'une autre version ne reçoit jamais les fichiers en cache d'un vieux
   worker : elle reste sans contrôleur, comme avant, jusqu'au redémarrage.
2. **Le changement de contrôleur attendu ne recharge pas la page.**
   `installedAppAssets.js` met `window.hackerOsClaimForInstalledApps` à
   `pending` avant de demander ; l'écouteur `controllerchange` de `index.html`
   le passe à `done` et ne recharge pas. `hostUpdate.js` compte ensuite la page
   comme contrôlée (redémarrage propre si un autre onglet active une nouvelle
   version, ADR 0223).
3. **Sans worker, l'OS redemande au lancement suivant.** Seul un succès est
   retenu pour la session ; une app lancée sans worker n'est pas marquée
   « publiée ».
4. **Le ménage suit le disque.** Avant de retirer une adresse du cache, l'OS
   relit les apps installées sur le disque
   (`InstalledAppsBoot.ReadInstalledOnDiskAsync`, sans toucher au catalogue) ;
   une adresse reste si une app de cette page **ou** du disque la déclare.

## Options écartées

- **Activer un nouveau worker ou recharger la page** : couperait la session et
  les autres onglets (raison de l'ADR 0223).
- **Réclamer la page sans comparer les versions** : après un Ctrl+F5 sur une
  nouvelle version, le vieux worker servirait ses fichiers en cache à une page
  plus récente (mélange de versions).
- **Ménage par app seulement** (au lancement, retirer les adresses de cette app
  qu'elle ne déclare plus) : laisserait pour toujours les fichiers d'une app
  désinstallée.
- **Servir les fichiers sans service worker** (URL `blob:`) : demande une API
  du SDK et une modification de chaque app (option B de l'ADR 0206, écartée).

## Conséquences

- Après un Ctrl+F5, une app installée trouve ses fichiers dès le premier
  lancement, si la version de la page est celle du worker.
- Si la page a une autre version que le worker (nouvelle version déployée,
  worker en attente), les fichiers ne sont pas servis avant le redémarrage ; une
  ligne dans la console et dans `/var/log/syslog` le dit.
- Le premier lancement d'une app installée lit une fois de plus la liste des
  apps installées (quelques lectures IndexedDB).

Tests : `InstalledAppAssetPublisherTests`
(`A_launch_after_a_page_without_service_worker_asks_again_and_copies_the_files`,
`Files_of_an_app_another_tab_installed_are_kept`). Vérifié dans Chromium
(Playwright, `service-worker.published.js` servi sur `127.0.0.2`) : après un
rechargement forcé, la page est réclamée, ne se recharge pas et importe le
module en cache ; avec une autre version sur le serveur, le worker refuse.
