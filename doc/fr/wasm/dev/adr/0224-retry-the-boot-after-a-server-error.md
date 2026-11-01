> [English version](../../../../en/wasm/dev/adr/0224-retry-the-boot-after-a-server-error.md)

# ADR 0224 : réessayer le démarrage après une erreur du serveur

## Statut

Accepté le 2026-11-01 (correctif demandé par Benoit, branche
`boot/retry-server-errors`).

- **Complète :** [ADR 0099](0099-os-backup-and-low-storage.md) §3 (garde de
  démarrage, cache PWA abîmé). Remplace sa règle « un seul rechargement par
  onglet, puis l'écran d'erreur » pour les erreurs de chargement que le cache
  n'explique pas.
- **Voir aussi :** [ADR 0223](0223-restart-onto-the-new-version.md).

## Contexte

Benoit (2026-11-01 23:16Z), sur `https://hackos.kazo.ca`, huit minutes après
un déploiement : `503` sur `_framework/System.a3z5tpzyvn.wasm`, puis « Failed
to find a valid digest in the 'integrity' attribute » et l'écran « HackerOS
could not start ».

Vérifié juste après : le fichier servi et l'empreinte de la configuration de
démarrage concordent (`sha256-Gs5b…`), dans ce déploiement comme dans le
précédent. Le fichier n'a donc jamais changé : c'est le corps de la réponse
`503` (GitHub Pages en renvoie un moment après un déploiement) qui ne passe
pas le contrôle d'intégrité. Le chargeur de .NET réessaie deux fois en une
fraction de seconde, puis abandonne.

La garde de l'ADR 0099 §3 ne sait réparer que le cache du service worker. Une
page sans worker (première visite, Ctrl+F5) ou dont le cache a déjà été
reconstruit affichait aussitôt l'état `failed`, alors qu'il suffisait de
réessayer un peu plus tard.

## Décision

1. Une erreur de chargement (intégrité, téléchargement) sur une page **sans
   worker**, ou après la reconstruction du cache, recharge la page toute seule
   après 5, 15 puis 30 secondes (état `retrying`, avec un compte à rebours et
   le bouton « Retry » pour réessayer tout de suite). Au-delà de trois essais
   dans l'onglet, l'état `failed` d'avant.
2. Hors ligne (`navigator.onLine === false`), pas d'essai automatique : l'état
   `offline` (page avec worker) ou `failed`.
3. Le compteur vit dans `sessionStorage['hackeros.bootRetries']` ; un
   démarrage réussi l'efface avec la garde `hackeros.bootRecovery`, et un clic
   sur « Retry » repart pour trois essais.
4. La garde écoute aussi le rejet « download '…' for … failed » qui sort seul
   du chargeur, pas seulement « Failed to start platform ».
5. Tout reste dans le script de démarrage de `index.html` (frontière
   d'amorçage de l'hôte, ADR 0099) ; aucun `<script src>` ajouté.

## Options écartées

- **Réessayer chaque fichier dans `loadBootResource`** : sans rechargement,
  mais il faudrait refaire le `fetch` du chargeur (mode de cache par fichier,
  intégrité) et le suivre à chaque version de .NET.
- **Ne rien changer** : l'utilisateur voit une erreur pour un incident qui
  passe tout seul en quelques minutes.

## Conséquences

- Juste après un déploiement, un démarrage peut prendre jusqu'à environ une
  minute de plus au lieu d'échouer.
- Un fichier réellement abîmé sur le serveur affiche `failed` après environ
  50 secondes au lieu de tout de suite.
