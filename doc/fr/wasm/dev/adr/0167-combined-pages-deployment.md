# ADR 0167 : publication Pages combinée et builds indépendants

## Statut

Accepté le 2026-10-24 à la demande de Benoit. Complète la publication PWA et
restreint l'interception du service worker de l'ADR 0131 pour le sous-site legacy.

## Décision

- Dépôt public : `https://github.com/Kazo-ca/HackerOs`.
- Release WASM : `2.0.3`, via la version unique existante de l'ADR 0098.
- Le workflow Pages publie un seul artefact : PWA autonome à `/`, ancien bundle
  TypeScript à `/legacy/`, domaine `hackos.kazo.ca`.
- Deux caches exacts, calculés sur les entrées respectives, permettent de ne
  reconstruire que la version modifiée. Aucun cache par préfixe ; une sortie
  absente ou évincée est reconstruite pour que l'artefact reste complet.
- Le workflow WASM distinct continue la validation et la publication d'un
  artefact téléchargeable, sans deuxième déploiement concurrent de Pages.
- L'assemblage ne modifie aucun octet signé par le manifeste PWA. Le worker
  laisse `/legacy` et `/legacy/**` au réseau, hors fallback et isolation WASM.
- Les écrans d'information produit affichent le dépôt. Les écrans de connexion
  et de création de compte annoncent le legacy uniquement sur le nom d'hôte
  exact `hackos.kazo.ca`, avec des textes anglais/français.
- Les modifications du legacy autorisées ici restent limitées à son identité,
  son build de publication et les corrections de démarrage nécessaires à sa
  conservation sous le nouveau chemin.

## Usage et vérification

Voir [github-pages-deployment.md](../github-pages-deployment.md) : commandes,
conditions de cache, script d'assemblage et tests de la publication réelle.
