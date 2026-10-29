> [English version](../../../../en/wasm/dev/adr/0192-features-overview-and-ported-apps.md)

# ADR 0192 : présentation des fonctionnalités et apps portées nommées

## Statut

Accepté le 2026-10-29 par Benoit, dans le fil « Présenter les fonctionnalités
de HackerOS ».

- **Précise :** l'[ADR 0135](0135-no-game-in-this-repository.md) §2, pour le
  seul document [`features.md`](../../features.md).

## Contexte

Benoit a demandé une page de présentation de HackerOS, liée depuis le
`readme.md` racine, qui servira plus tard à créer le site du produit. Elle
liste les fonctionnalités disponibles, le but de l'écosystème, les façons de
déployer l'OS, et une section « En cours de développement » avec les ADR et
les issues. Il a demandé d'y lister les apps portées dans leurs propres dépôts
(`Kazo-ca/HackerOs-App-*`) comme fonctionnalités optionnelles en cours, et a
choisi de nommer aussi Chess et EmulatorJS, malgré l'ADR 0135 §2.

## Décision

1. La présentation vit dans `doc/fr/wasm/features.md` (original) et
   `doc/en/wasm/features.md` (traduction), liée depuis `readme.md`,
   `readme-fr.md` et l'index de `doc/<langue>/wasm/`.
2. « Disponible » veut dire fusionné dans `main`. La section « En cours de
   développement » est un tableau par thème (Fonctionnalité, Description, ADR,
   Issue) ; une ADR pas encore fusionnée est liée par sa PR.
3. **Exception à l'ADR 0135 §2** : `features.md` peut nommer les apps portées
   dans leurs propres dépôts, Chess et EmulatorJS compris, comme apps
   optionnelles installables à part. Le reste de l'ADR 0135 tient : aucun code
   de jeu dans ce dépôt, et aucun autre document ne décrit de jeu.
4. Toute PR vers `main` qui livre, modifie ou planifie une fonctionnalité met
   à jour `features.md` dans les deux langues. Le modèle de PR
   (`.github/pull_request_template.md`) a une case pour le rappeler, et la
   règle est écrite dans `AGENTS.md` et `CONTRIBUTING.md`.

## Conséquences

- Un lecteur trouve en une page ce que HackerOS offre et ce qui s'en vient.
- La section « En cours » dépend de la discipline des PR : la case du modèle
  de PR est un rappel, pas une vérification automatique.
