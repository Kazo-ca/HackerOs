# ADR 0059 : Codes des diagnostics de la frontière du SDK

## Statut

Accepté le 2026-10-10.

- **Phase du plan :** phase 4 — SDK complet et apps de base, chantier
  `rebuild/4-sdk-boundary` (ADR 0058).
- **Remplace en partie :** ADR 0055, sections 2 et 3, pour les seuls numéros
  de diagnostic. Les règles de l'ADR 0055 ne changent pas.

## Contexte

L'ADR 0055 attribue deux codes aux nouvelles vérifications de la frontière du
SDK :

- `HOSAPP0002` pour une `ProjectReference` interdite dans un projet d'app ;
- `HOSAPP0003` pour une injection interdite, signalée par le générateur de
  manifeste.

Ces deux numéros sont déjà pris depuis l'ADR 0047 et sont émis par le même
générateur : `HOSAPP0002` signale une classe `[AppManifest]` non publique,
`HOSAPP0003` une classe `[AppManifest]` abstraite (voir
[`app-model.md`](../../framework/app-model.md#codes-derreur)). Un même code aurait donc deux
sens, et les tests qui recherchent `error HOSAPP0002` ne sauraient plus quelle
règle ils vérifient.

## Décision

Les codes de l'ADR 0047 gardent leur sens. Les vérifications de l'ADR 0055
prennent les numéros libres suivants, dans une plage réservée à la frontière du
SDK (`HOSAPP0030` à `HOSAPP0039`) :

| Code | Règle (ADR 0055) | Émis par |
|---|---|---|
| `HOSAPP0030` | Un projet d'app référence un projet hors de la liste permise (section 2). | cible MSBuild `ValidateHackerOsSdkBoundary` |
| `HOSAPP0031` | Un projet d'app injecte un service qui n'est ni un type du SDK, ni un service MudBlazor, ni `IJSRuntime` (section 3). | générateur de manifeste |
| `HOSSDK0001` | Une bibliothèque SDK référence un projet autre qu'une abstraction ou une autre bibliothèque SDK (section 2). Inchangé. | cible MSBuild `ValidateHackerOsSdkBoundary` |

Partout où l'ADR 0055 et le tableau des chantiers de l'ADR 0058 écrivent
`HOSAPP0002` (référence) et `HOSAPP0003` (injection), il faut lire
`HOSAPP0030` et `HOSAPP0031`.

## Conséquences

- Aucun code existant ne change : les projets et les tests de l'ADR 0047
  continuent de fonctionner.
- La documentation des codes ([`app-model.md`](../../framework/app-model.md#codes-derreur)
  et [`app-sdk.md`](../../framework/app-sdk.md)) liste les deux nouveaux codes.

## Alternatives écartées

- **Renuméroter les codes de l'ADR 0047** pour libérer `HOSAPP0002` et
  `HOSAPP0003` : change le sens de codes déjà publiés, déjà testés et déjà
  décrits par un ADR accepté, sans rien apporter.

## Références

- ADR 0047 (codes `HOSAPP0001` à `HOSAPP0099`), ADR 0055, ADR 0058.
- `Tools/HackerOs.Tools.ManifestGenerator/GeneratorDiagnostic.cs`,
  `build/HackerOs.Apps.targets`.
