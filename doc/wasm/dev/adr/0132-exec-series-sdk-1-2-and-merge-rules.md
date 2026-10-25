# ADR 0132 : série `exec` — ajouts du SDK 1.2, branches et barre de fusion

## Statut

Accepté le 2026-10-18. Une question est posée à Benoit (§2, moment de la
publication du SDK 1.2) ; ce qui suit applique la réponse recommandée en
attendant.

- **Plan :** [`execution-followups-plan.md`](../plans/execution-followups-plan.md),
  sections 5 et 8.
- **Applique :** l'[ADR 0114](0114-sdk-1-0-freeze.md) (gel du SDK, promesse
  SemVer) et l'[ADR 0117](0117-sdk-window-title-and-sdk-1-1.md) (publication
  d'une version mineure dans un chantier final).

## Contexte

Le SDK est en 1.1.0, toute sa surface dans `PublicAPI.Shipped.txt`
(`v3.1/4-sdk-1-1`). Plusieurs chantiers `exec` ajoutent des API publiques :
`HackWireContextAttribute` (ADR 0124), limites et nouvelles raisons de sortie
(ADR 0125, 0126), `AppDefinition.OomScoreAdjust` (ADR 0126), `nice` (ADR 0127),
`HackBackground` et son analyseur (ADR 0128). Il faut dire comment ces ajouts
arrivent sans casser une app 1.0 ou 1.1.

## Décision

### 1. Ajouts seulement

- Chaque ajout public va dans `PublicAPI.Unshipped.txt` du projet concerné,
  dans le chantier qui l'introduit. Aucune ligne de `PublicAPI.Shipped.txt`
  n'est retirée ni changée : une rupture demanderait un SDK 2.0, hors de cette
  série.
- Les valeurs ajoutées aux énumérations (`ProcessExitReason`,
  `FileSystemErrorCode`) prennent de nouveaux nombres, à la fin.
- L'app de compatibilité 1.0 (`Tests/fixtures/sdk-compat`) continue de se
  charger et de tourner ; `exec/10` y ajoute une app compilée en 1.1.0.

### 2. Publication du SDK 1.2 (question posée à Benoit)

Appliqué en attendant (recommandé) : **une seule publication, à la fin**, dans
`exec/10-final` : `HackerOsVersion` passe à `1.2.0`, les fichiers `Unshipped`
sont versés dans `Shipped`, [`sdk-packages.md`](../../framework/sdk-packages.md) et
[`building-on-hackeros.md`](../../framework/building-on-hackeros.md) listent les ajouts.
Pendant la série, `exec/base` reste en 1.1.0 avec des ajouts non publiés.

L'autre choix : une version mineure par chantier qui ajoute une API (1.2, 1.3,
…). Le dépôt du jeu pourrait suivre plus tôt, au prix d'une vérification
complète et d'une app de compatibilité de plus à chaque publication.

### 3. Branches et fusion

- Branche d'intégration `exec/base`, créée le 2026-10-18 depuis `v3.1/base`
  (avec le plan validé venu de `web/base`).
- Chaque chantier : branche `exec/<n>-<sujet>` depuis `exec/base`, PR vers
  `exec/base`. Le fil fusionne lui-même quand la barre de §4 est passée
  (choix « Oui » de Benoit sur la carte de lancement des suites, le
  2026-10-18). **Jamais de fusion vers `main`** : Benoit décide.
- Avant de fusionner, un chantier qui touche les mêmes fichiers qu'un chantier
  déjà fusionné ramène `exec/base` dans sa branche.

### 4. Barre de fusion

GitHub Actions ne tourne pas dans ce dépôt (aucun exécuteur depuis août
2026) : la barre est locale.

- Build de la solution sans avertissement nouveau ; tests unitaires et E2E du
  périmètre ; échecs préexistants connus listés dans la PR, pas masqués.
- `build/verify-hosts.sh` sur les trois hôtes, options du chantier activées
  **et** coupées (plan, principe 6).
- Publication trimmée (`build/publish-trimmed.sh` et
  `HackerOs.TrimmedPublish.E2E.Tests`, ADR 0113) et app de compatibilité du
  SDK, comme depuis `v3/14` et `v3/15`.
- Docs et index à jour dans le même changement (AGENTS.md III.4).

### 5. Numéros d'ADR

Ce chantier a pris 0124 à 0132 et réserve **0133** à la décision de l'essai
de rendu en worker (ADR 0129). Un chantier qui doit consigner des ajustements
de mise en œuvre prend le prochain numéro libre que le coordinateur lui
réserve.

## Conséquences

- `exec/10-final` fait la publication du SDK 1.2 en plus de la vérification
  finale et des documents.
- Le dépôt du jeu reste sur 1.1.0 jusqu'à cette publication.

## Références

- ADR 0098, 0113, 0114, 0117, 0124 à 0131.
- [`sdk-packages.md`](../../framework/sdk-packages.md),
  [`building-on-hackeros.md`](../../framework/building-on-hackeros.md).
