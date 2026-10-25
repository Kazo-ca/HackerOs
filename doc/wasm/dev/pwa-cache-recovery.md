# Récupération d'un cache PWA corrompu

Mise en œuvre de la section 3 de
[l'ADR 0099](adr/0099-os-backup-and-low-storage.md) (chantier
`v3/7-storage-backup`), qui complète
[l'ADR 0017](adr/0017-pwa-cache-and-offline-strategy.md) (cache « cache
d'abord » versionné, intégrité vérifiée à l'installation).

## Objet

Une entrée du cache du service worker peut s'abîmer après l'installation
(disque, écriture interrompue, extension, outil de développement). Le service
worker la sert telle quelle : il ne revérifie pas l'intégrité à chaque requête.
Avant ce chantier, Blazor refusait la ressource, le démarrage s'arrêtait sur
l'anneau de chargement et la barre « An unhandled error has occurred », et
recharger ne changeait rien. Désormais :

- **en ligne**, la page demande au service worker de vider son cache et de le
  reprendre du serveur, puis recharge **une seule fois** ; l'OS démarre ;
- **hors ligne**, l'écran d'erreur de démarrage explique que le cache est abîmé
  et qu'il faut se reconnecter une fois ; le cache abîmé n'est pas touché, et
  le bouton « Retry » répare une fois la connexion revenue.

Les données de l'OS (IndexedDB) ne sont jamais concernées : seul le cache des
fichiers de l'application est reconstruit.

## Architecture

| Pièce | Fichier | Rôle |
|---|---|---|
| Vérification d'intégrité | Blazor (`dotnet.js`, empreintes SHA-256 de la configuration de démarrage) | Chaque ressource de démarrage est chargée avec `integrity` : des octets abîmés font échouer le chargement (« SRI's integrity checks failed »), puis le démarrage (« Failed to start platform. Reason: … download '…' failed »). |
| Garde de démarrage | `OS/HackerOs.Ecosystem/wwwroot/index.html` (script en ligne, frontière d'amorçage de l'hôte) | Lance Blazor (`autostart="false"`, puis `Blazor.start()`), détecte l'échec, décide de la réparation, affiche l'écran d'erreur. |
| Réparation | `OS/HackerOs.Ecosystem/wwwroot/service-worker.published.js`, message `RESET_CACHE` | Vide le cache de la version et le reprend du serveur, ou le garde intact si le serveur est injoignable. |
| Écran d'erreur | `index.html` (`#hackeros-boot-error`) et `OS/HackerOs.Ecosystem.Host/wwwroot/css/app.css` (servi sous `_content/HackerOs.Ecosystem.Host/css/app.css`) | Trois états (`data-state`) : `repairing`, `offline`, `failed`. Style console sombre ; l'anneau de chargement et la barre d'erreur de Blazor sont masqués (`html.hackeros-boot-failed`). |

### Déroulé

1. `Blazor.start()` échoue. En .NET 10, l'erreur « Failed to start platform »
   sort en rejet de promesse non géré et la promesse de `Blazor.start()` reste
   en attente : la garde écoute donc `unhandledrejection` **et** le rejet de
   `Blazor.start()`. Si `blazor.webassembly.js` lui-même ne s'est pas chargé
   (`window.Blazor` absent), c'est aussi un échec de chargement.
2. Seule une erreur d'intégrité ou de chargement (`integrity`, `SRI`,
   `download '`, `Failed to fetch`, `Failed to load`, …) déclenche la
   réparation, et seulement si un service worker contrôle la page. Toute autre
   erreur affiche l'état `failed`.
3. Si la garde `sessionStorage['hackeros.bootRecovery']` est déjà posée, la
   page a déjà réparé et rechargé dans cet onglet : pas de boucle, l'écran
   `failed` (ou `offline` si `navigator.onLine` est faux) s'affiche.
4. Sinon l'écran `repairing` s'affiche et la page envoie `RESET_CACHE` au
   service worker sur un `MessageChannel` (délai maximal 120 s). Le service
   worker :
   - sonde le serveur (`service-worker-assets.js`, `cache: 'no-store'`) ; s'il
     est injoignable, il répond `{ ok: false, reason: 'unreachable' }` sans rien
     effacer, et la page affiche l'état `offline` ;
   - sinon il retélécharge tous les fichiers du manifeste, avec leur
     `integrity`, dans un cache de transit `hackeros-cache-<version>-repair`,
     vide le cache abîmé, y recopie le transit, puis supprime le transit ;
   - si le serveur ne peut pas fournir une copie vérifiée de cette version
     (fichier abîmé sur le serveur, version plus récente déployée), il vide
     quand même le cache abîmé : les ressources viennent alors du serveur.
5. Sur `{ ok: true }`, la page pose la garde et recharge. Un démarrage réussi
   efface la garde : une corruption ultérieure dans le même onglet sera de
   nouveau réparée.

Le cache de transit commence par `hackeros-cache-` : s'il restait à moitié
écrit, la prochaine activation d'une version le supprime comme tout cache
périmé (ADR 0017).

## Décisions

- **Réparation demandée par la page, pas par le service worker** : seul Blazor
  sait qu'une ressource a été refusée ; vérifier l'intégrité à chaque requête
  dans le service worker coûterait un SHA-256 par fichier à chaque démarrage.
- **Hors ligne, rien n'est effacé** : vider le cache sans pouvoir le reprendre
  ôterait aussi `index.html`, donc l'écran qui explique quoi faire.
- **Un seul rechargement par onglet** (garde dans `sessionStorage`) : si la
  copie fraîche échoue aussi, l'utilisateur voit une erreur au lieu d'une
  boucle de rechargements.
- **Script en ligne dans `index.html`** : la garde doit tourner avant et
  pendant le démarrage de Blazor, sans dépendre d'une ressource qui pourrait
  elle-même être abîmée ; aucun `<script src>` n'est ajouté à l'hôte.
- En mode développement (`localhost`, `127.0.0.1`), aucun service worker n'est
  enregistré : un échec de démarrage affiche directement l'état `failed`.

## Tests

`Tests/HackerOs.Pwa.E2E.Tests/PublishedPwaTests.cs`, contre une publication
Release servie en statique (le test qui vérifiait que les octets corrompus
étaient servis tels quels est remplacé) :

- `Corrupted_boot_resource_in_the_cache_is_downloaded_again_and_the_app_boots_after_one_reload` :
  `System.Private.CoreLib.*.wasm` écrasé dans le cache, rechargement ; l'OS
  démarre après exactement un rechargement de récupération, le cache contient
  de nouveau le vrai fichier et la garde est effacée.
- `Corrupted_cache_offline_asks_to_reconnect_once_and_recovers_when_back_online` :
  même corruption, navigateur hors ligne et serveur arrêté (l'émulation hors
  ligne de Playwright n'atteint pas les requêtes du service worker) ; l'écran
  `offline` s'affiche, le cache abîmé est intact ; serveur relancé, « Retry »
  répare et l'OS démarre.

Sans IPv6 (conteneur cloud), le harnais se lie à `127.0.0.2` au lieu de
`[::1]` : adresse de bouclage, contexte sécurisé, et non exclue par le filtre
`localhost`/`127.0.0.1` d'`index.html`.

## Tâches

- [x] Lancer Blazor manuellement et détecter l'échec de démarrage (rejet non géré en .NET 10).
- [x] Message `RESET_CACHE` du service worker : sonde, cache de transit vérifié, remplacement.
- [x] Garde d'un seul rechargement dans `sessionStorage`, effacée par un démarrage réussi.
- [x] Écran d'erreur de démarrage (états `repairing`, `offline`, `failed`).
- [x] Tests E2E de récupération en ligne et hors ligne ; repli `127.0.0.2` du harnais.
- [x] Vérification manuelle (Playwright Node, publication Release servie par `python3 -m http.server`) : en ligne, hors ligne puis reconnexion, fichier abîmé sur le serveur (un seul rechargement, puis état `failed`).
