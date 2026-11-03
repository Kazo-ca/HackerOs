> [English version](../../../../en/wasm/dev/adr/0227-factory-reset-erases-everything.md)

# ADR 0227 : la réinitialisation efface tout ce que HackerOS garde dans le navigateur

## Statut

Accepté le 2026-11-02 (demandé par Benoit, branche
`login/factory-reset-erase-everything`).

- **Complète :** [ADR 0056](0056-sdk-apis-for-system-apps.md) §11 et
  [ADR 0226](0226-forgot-password-factory-reset.md). Remplace la conséquence
  de l'ADR 0226 « seule la base de l'OS est supprimée ».

## Contexte

Benoit (2026-11-02, après la fusion de l'ADR 0226) : « Valide que quand on
réinitialise, toutes les données gardées dans le navigateur sont supprimées.
Rien ne doit rester. » Puis, pour les bases des sites web et le cache des apps
installées : « Elles doivent être nettoyées aussi. »

Jusque-là, `BrowserSystemPowerDevice.EraseStorageAsync` ne supprimait que la
base IndexedDB `hackeros`. Restaient dans le navigateur : les bases des sites
web (`hackweb.*`, ADR 0088), les dossiers de l'ordinateur montés
(`hackeros-host-folders`), le cache des apps installées
(`hackeros-installed-apps`, ADR 0206), le cache de l'isolation cross-origin,
le `localStorage` (restauration de sauvegarde, sites), le `sessionStorage`,
l'OPFS et le service worker.

## Décision

1. Réinitialiser (Paramètres ▸ Système ou « Mot de passe oublié ? ») efface,
   pour l'origine de l'OS : **toutes** les bases IndexedDB
   (`indexedDB.databases()`), **tous** les caches du Cache Storage, le
   `localStorage`, le `sessionStorage`, le système de fichiers privé de
   l'origine (OPFS), les cookies lisibles par script, et désinscrit le
   service worker. La page se recharge ensuite.
2. Le travail est fait par le module `hostFactoryReset.js`
   (`eraseEverything`) de `HackerOs.Ecosystem.Host`, appelé par
   `BrowserSystemPowerDevice` après la suppression de la base de l'OS (par le
   module qui tient ses connexions). Chaque étape est indépendante : un échec
   est écrit dans la console et n'arrête pas les autres.
3. Une base encore ouverte ailleurs (autre onglet) bloque sa suppression : le
   module attend au plus 5 secondes, la demande reste en file et se termine
   quand la connexion se ferme ou au rechargement.

## Conséquences

- Après le rechargement, seul ce que le démarrage recrée existe : une base
  `hackeros` vide (première configuration), le service worker et le cache de
  la version de l'OS retéléchargés, des caches vides. Vérifié dans Chromium
  (HTTPS, service worker actif) avec des marqueurs dans chaque stockage.
- Le cache de la version de l'OS étant effacé, une réinitialisation faite
  hors ligne ne peut pas redémarrer l'OS avant le retour du réseau.
- Les clés d'accès restent dans l'authentificateur de l'appareil (hors de
  portée d'une page web).
