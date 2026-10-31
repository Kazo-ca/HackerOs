> [English version](../../../../en/wasm/dev/adr/0215-hack-store-answers-notification-and-ambiguous-app-id.md)

# ADR 0215 : Hack Store, mises à jour par notification seulement et appId ambigu refusé

## Statut

Accepté le 2026-10-31, chantier `todo/5b-hackstore-no-badge` (série
`todo/base`, issue `TODO` [#249](https://github.com/Kazo-ca/HackerOs/issues/249)).
Remplace l'[ADR 0212](0212-hack-store-app-build.md) §4 et §5, et le badge de
l'[ADR 0188](0188-hack-store-and-app-catalog.md) §3 (« il met un badge sur son
icône »). Le reste des ADR 0188 et 0212 ne change pas.

## Contexte

L'ADR 0212 a posé quatre questions à Benoit et appliqué les recommandations en
attendant. Le 2026-10-31, Benoit a confirmé **Q1 A** (« Redémarrer pour
terminer » sans mot de passe, comme le menu Démarrer) et **Q2 A** (source
`catalog:official/<appId>`), mais il a choisi autre chose pour les deux
autres :

- **Q3 C, « Notification seulement »** : pas de badge sur l'icône du Hack Store.
- **Q4 B, « Refuser et demander »** : un appId seul listé par plusieurs
  catalogues n'est pas installé ; l'utilisateur choisit le catalogue.

## Décision

1. **Mises à jour : notification seulement (remplace ADR 0212 §5).**
   `AppUpdateMonitor` vérifie toujours à l'ouverture de la session puis toutes
   les `[updates] interval-hours` heures, et envoie une notification (une seule
   par ensemble de mises à jour) dont l'action ouvre l'onglet Installées
   (`--page=installed`). Aucun badge n'est dessiné : ni dans le menu Démarrer,
   ni sur l'écran d'accueil mobile, ni sur le bouton de la barre des tâches.
   `AppUpdateMonitor.GetBadgeCount` et `TaskbarWindowItem.BadgeCount`
   disparaissent (ils n'ont jamais été publiés). La vérification du Store
   (`HackAppPackages.GetUpdatesAsync`) passe toujours par le moniteur, pour
   qu'une mise à jour déjà montrée par le Store ne soit pas notifiée une
   seconde fois.
2. **AppId seul dans plusieurs catalogues : refusé (remplace ADR 0212 §4).**
   `hackpkg install <appId>[@version]`, `hackpkg info <appId>` et l'installation
   par appId du SDK lisent tous les catalogues de `[catalogs]`. Un seul
   catalogue liste l'app : il est utilisé. Plusieurs la listent : l'opération
   échoue avec `apps.package.ambiguous` et un message qui nomme chaque source
   possible, par exemple « org.example.notes is in several catalogs; choose
   one: catalog:official/org.example.notes, catalog:mirror/org.example.notes. »
   (`hackpkg` l'affiche et sort avec le code 1). Aucun : toujours
   `apps.package.not-in-catalog`. Le Store, qui montre chaque catalogue,
   installe toujours avec la forme `catalog:<nom>/<appId>` et n'est pas
   concerné.

## Conséquences

- L'OS ne choisit jamais un catalogue à la place de l'utilisateur ; un
  catalogue ajouté qui reprend une app de l'officiel ne peut pas la remplacer
  par surprise.
- Les scripts qui font `hackpkg install <appId>` restent valables tant qu'un
  seul catalogue est configuré (le cas par défaut).
- Le menu Démarrer, l'écran mobile et la barre des tâches reviennent à leur
  dessin d'avant l'ADR 0212.

## Tâches

- [x] Retirer le badge (menu Démarrer, mobile, barre des tâches) et `GetBadgeCount`.
- [x] `apps.package.ambiguous` pour un appId seul listé par plusieurs catalogues, avec test.
- [x] Docs (`app-installation.md`, `linux-commands.md`, `features.md`, plan du Hack Store, mémoire) et entrée de changement.
