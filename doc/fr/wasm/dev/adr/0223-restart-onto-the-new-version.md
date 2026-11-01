> [English version](../../../../en/wasm/dev/adr/0223-restart-onto-the-new-version.md)

# ADR 0223 : redémarrer sur la nouvelle version, pas sur l'ancienne

## Statut

Proposé le 2026-11-01. Questions posées à Benoit dans le fil « Corriger le
redémarrage vers une ancienne version » ; rien n'est codé avant ses réponses.

- **Complète :** [ADR 0017](0017-pwa-cache-and-offline-strategy.md) (cache de
  la PWA), [ADR 0162](0162-restart-after-a-host-build-change.md) (bandeau de
  redémarrage), [ADR 0174](0174-announce-a-new-version-to-open-sessions.md)
  (`/version.json`), [ADR 0195](0195-start-menu-power-button.md) (« Redémarrer »
  du menu Démarrer).

## Contexte

Benoit (2026-11-01) : quand une nouvelle version est proposée et qu'il clique
« Redémarrer », il se retrouve sur une version **encore plus ancienne**, sans
avoir installé l'application. Il propose l'idée classique du numéro dans l'URL
(`mon-script.js?v=1101`), mais sans numéro à changer à chaque déploiement.

### Ce qui marche déjà : l'empreinte des fichiers

Le build .NET 10 fait déjà ce que fait `?v=`, automatiquement et mieux : chaque
fichier de `_framework/` porte une empreinte de son contenu dans son nom
(`dotnet.h8uf4fadjg.js`), l'import map de `index.html` les relie, les CSS
utilisent `#[.{fingerprint}]`, et chaque ressource est vérifiée par son SHA-256
(SRI). Un fichier modifié change donc de nom à chaque build, sans rien à la
main. Ajouter `?v=` ne changerait rien : le fichier qui reste ancien est
`index.html` lui-même, et ce n'est pas le cache HTTP qui le garde.

### La cause : le service worker garde l'ancienne version

Vérifié dans le code de `main` et sur `https://hackos.kazo.ca` le 2026-11-01 :

1. **Le worker est installé même sans installer l'app.** `index.html`
   enregistre `service-worker.js` dans tout onglet qui n'est pas `localhost`.
   Installer la PWA ne change rien au cache.
2. **Il sert tout depuis son cache, `index.html` compris** (cache d'abord,
   `service-worker.published.js`, `fetchShellOrAsset`). Le cache porte le nom de
   la version (`hackeros-cache-<version du manifeste>`).
3. **Le nouveau worker attend sans fin.** Après un déploiement, le navigateur
   télécharge le nouveau worker, qui remplit son cache puis reste « en
   attente » tant qu'un onglet est contrôlé par l'ancien. Le worker sait
   recevoir `SKIP_WAITING`, mais **rien ne l'envoie**, et l'événement
   `hackeros-pwa-update-available` de `index.html` **n'a aucun écouteur** dans
   l'OS.
4. **« Redémarrer » est un simple `location.reload()`**
   (`BrowserSystemPowerDevice`). Un rechargement ne libère pas l'ancien worker :
   la page rechargée lui appartient encore, et il la sert depuis son ancien
   cache.
5. **Pourquoi « encore plus ancienne » :** la page ouverte peut être plus
   récente que le cache du worker. Un Ctrl+F5 (que la mémoire du projet donne
   comme correctif) contourne le worker et charge la dernière version du
   réseau ; le « Redémarrer » du menu Démarrer (ADR 0195) vide le cache quand
   l'ancien worker ne peut plus télécharger sa version, et la page suivante vient
   du réseau. Au rechargement normal, l'ancien worker reprend la main avec le
   cache de sa version, qui peut dater de plusieurs déploiements puisqu'il n'est
   jamais remplacé tant qu'un onglet reste ouvert.
6. **Le cache HTTP de GitHub Pages joue peu.** Pages envoie
   `Cache-Control: max-age=600` pour `index.html` et `version.json`,
   `max-age=14400` pour `service-worker.js` et `service-worker-assets.js`. Le
   worker est enregistré avec `updateViaCache: 'none'`, son installation
   télécharge avec `cache: 'no-cache'`, et `version.json` est demandé avec
   `?t=` et `no-store` : ces durées ne retardent pas la mise à jour, sauf la
   page servie par le réseau quand elle n'est pas dans le cache du worker
   (jusqu'à 10 minutes).

Le diagnostic vient de la lecture du code et des en-têtes du site ; il n'a pas
été reproduit dans un navigateur.

## Options

### A. Activer le nouveau worker au redémarrage (recommandé)

Le flux de mise à jour standard d'un service worker, qui manque aujourd'hui :

1. Quand `/version.json` annonce une version (ADR 0174) ou que le build a
   changé (ADR 0162), l'OS appelle `registration.update()` : le nouveau worker
   télécharge la nouvelle version dans son propre cache, vérifiée par SRI.
2. Le bouton « Redémarrer pour mettre à jour » ferme la session proprement
   (ADR 0162 §2), envoie `SKIP_WAITING` au worker en attente, et la page se
   recharge sur `controllerchange` (déjà écrit dans `index.html`). L'ancien
   cache est supprimé à l'activation (déjà écrit).
3. Hors ligne ou téléchargement échoué : on garde l'ancienne version et le
   bandeau le dit, rien n'est cassé.

Garde le fonctionnement hors ligne et les en-têtes d'isolation que le worker
ajoute sur un hôte statique (ADR 0131). Aucun numéro à la main : le nom du
cache vient déjà du manifeste généré au build. Petit module JS dans
`HackerOs.Ecosystem.Host` (API navigateur que C# ne peut pas appeler), la
logique reste en C#.

### B. Réseau d'abord pour la page

Le worker demande `index.html` au réseau, et ne prend son cache que hors ligne.
Plus simple, mais la nouvelle page charge des fichiers que l'ancien cache n'a
pas : ils viennent du réseau sans être gardés, et l'OS ne démarre plus hors
ligne avant que le nouveau worker soit actif. Mélange deux versions.

### C. Pas de service worker sans installation

N'enregistrer le worker que dans la PWA installée ; un onglet ordinaire suit
le cache HTTP et les empreintes. Corrige le cas de Benoit, mais l'onglet perd
le hors ligne **et** l'isolation cross-origin sur GitHub Pages (ADR 0131 :
c'est le worker qui ajoute COOP/COEP, Pages ne sait pas envoyer d'en-têtes),
donc les processus en Web Workers à mémoire partagée. Ne corrige pas la PWA
installée, qui a le même défaut.

### D. Numéro de version dans les URL (idée de départ)

Un script lit `version.json` et réécrit `data-v-src` en `src?v=<version>`.
Redondant avec les empreintes de .NET, ne touche pas `index.html` servi par
le worker, et casse le contrôle d'intégrité et l'import map si on l'applique
aux fichiers de `_framework/`. Écarté.

## Questions posées à Benoit

1. **Méthode** : A (recommandé), B ou C.
2. **Quand télécharger** : (a) en arrière-plan dès l'annonce, le bouton
   redémarre aussitôt (recommandé) ; (b) seulement au clic, avec une attente.
3. **Autres onglets** ouverts sur HackerOS : (a) ils font le même redémarrage
   propre avant de recharger (recommandé) ; (b) ils rechargent sans fermer la
   session ; (c) pas de mise à jour tant qu'ils sont ouverts.
4. **« Redémarrer » du menu Démarrer** (ADR 0195) : (a) prend la nouvelle
   version s'il y en a une, avec le même flux (recommandé) ; (b) reste comme
   aujourd'hui.

## Décision

À écrire après les réponses de Benoit.
