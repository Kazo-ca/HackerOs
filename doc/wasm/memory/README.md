# Mémoire du projet HackerOS

État du projet et conventions de travail au **2026-10-25**. Ce dossier sert de
point de reprise : un nouveau contributeur (humain ou agent) y trouve ce qui
est fait, comment on travaille et ce qu'il ne faut pas refaire. Il est mis à
jour à la fin de chaque série ou quand une convention change.

## Où en est le projet

- **Code actif** : `wasm2/HackerOs/` (OS simulé en Blazor WebAssembly, .NET 10,
  solution `HackerOs.sln`). `src/` est l'ancienne version, gardée comme
  référence.
- **Version** : HackerOS et le SDK sont en **2.0.3** (`build/Versions.props`).
  Le SDK 2.0.0 a retiré `HackerOs.AppSdk.FileView` et refuse les apps 1.x
  ([ADR 0161](../dev/adr/0161-sdk-2-0-implementation-adjustments.md)).
  `IAppEventGateway.PublishAsync` (ADR 0145), `HackCrossOriginIsolation`
  (ADR 0146) et le délai de grâce des tâches de fond (ADR 0147) sont les
  ajouts prévus pour la prochaine mineure.
- **Séries terminées et fusionnées dans `main`** :

| Série | Contenu | Bilan |
| --- | --- | --- |
| Refonte, phases 1 à 4 | API fichier `Hack*`, manifeste en classe C#, réglages et journaux en fichiers, `/proc` et `/dev`, shell POSIX, corbeille, capacités dangereuses, apps de base sur le SDK public | [`ecosystem-rebuild-plan.md`](../dev/plans/ecosystem-rebuild-plan.md) |
| Phase 5 | Mesure CPU et mémoire, « Not responding », arrêt propre, processus en Web Workers | [`execution-isolation-plan.md`](../dev/plans/execution-isolation-plan.md) |
| Phase web | ASP.Hack (MVC, vues `.razor`/`.cshtml`), sites Blazor, sites statiques, rendu HTML du navigateur, fenêtres détachées | [`asp-hack-plan.md`](../dev/plans/asp-hack-plan.md) |
| V3 (`v3/1` à `v3/16`) | Fin de la migration wasm v3, SDK 1.0 gelé, publication trimmée | [`wasm-v3-final-status.md`](../dev/plans/wasm-v3-final-status.md) |
| V3.1 | Géométrie et titre des fenêtres, SDK 1.1 | [`v3-1-series-plan.md`](../dev/plans/v3-1-series-plan.md) |
| EXEC (`exec/*`) | Limites de processus, OOM killer, passerelles distantes, boîte « ne répond pas », `HackBackground`, OS servi par `HackerOs.Server`, isolation cross-origin, SDK 1.2 | [`execution-followups-plan.md`](../dev/plans/execution-followups-plan.md) |
| DESK (`desk/0` à `desk/6`) | Contrôle de fichiers fourni par l'OS, menu contextuel central, entrées de menu des apps, bureau, SDK 2.0 | [`desktop-and-context-menu-plan.md`](../dev/plans/desktop-and-context-menu-plan.md) |

- **Depuis DESK** (sur `main`, octobre 2026) : redémarrage proposé quand le
  build change sous la page (ADR 0162), aperçu local du déplacement des
  fenêtres (ADR 0163), icônes des apps et menu de l'icône de fenêtre
  (ADR 0164), accueil et tiroir mobiles (ADR 0165, 0166), publication GitHub
  Pages combinée PWA + legacy (ADR 0167), paquets NuGet mis à jour.
- **ADR** : 0001 à 0167 existent (quelques numéros sautés). **Prochain numéro
  libre : 0168.** Des threads parallèles réservent des numéros distincts.

## Conventions de travail

- **Aucun jeu sur ce repository** ([ADR 0135](../dev/adr/0135-no-game-in-this-repository.md)) :
  ni code, ni mention dans la doc, les plans, les ADR ou la mémoire.
- **Branches** : une branche d'intégration `<série>/base` par série, une branche
  `<série>/<n>-<sujet>` par chantier, PR vers la branche d'intégration. Les ADR
  d'une phase sont écrits avant son code.
- **`main`** : seul Benoit fusionne dans `main` et décide quand une branche
  d'intégration y va.
- **Questions** : on peut les noter dans un ADR, mais on les pose toujours à
  Benoit directement, avec des choix, le contexte et les implications.
- **Documentation** : en français, noms de fichiers en anglais. `dev/` pour le
  développement de l'OS, `framework/` pour créer des apps et des sites,
  `memory/` pour ce dossier. Chaque changement met à jour la doc touchée et
  l'index [`../README.md`](../README.md) ; les docs remplacées vont dans
  `dev/old/`. Un ADR n'est jamais modifié : un nouvel ADR le remplace.
- **Barre de fusion** : build et tests locaux verts (suites E2E une à la fois),
  `build/publish-trimmed.sh` et la compatibilité du SDK
  (`Tests/fixtures/sdk-compat`) quand ils sont concernés. Les GitHub Actions
  tournent de nouveau depuis le 2026-10-24 et vérifient chaque PR vers `main`
  et `*/base` (tests en échec relancés une fois, Playwright dans un workflow
  manuel) : [`../dev/ci-workflows.md`](../dev/ci-workflows.md).
- **SDK** : l'API publique est suivie par `PublicAPI.Shipped.txt` /
  `PublicAPI.Unshipped.txt` ; un ajout passe par `Unshipped` jusqu'à la
  publication suivante (`build/tools/ship-public-api.sh`).

## Environnement et pièges

Les contournements de build (NuGet, conteneur Linux, Playwright) et les tests
instables connus sont regroupés dans [`../dev/pitfalls.md`](../dev/pitfalls.md)
et [`../dev/building-and-testing.md`](../dev/building-and-testing.md).

## Notes historiques

Notes écrites pendant les premières phases ; les chemins et nombres qu'elles
citent datent de leur écriture.

| Note | Contenu |
| --- | --- |
| [`blazor-window-manager-bugs.md`](blazor-window-manager-bugs.md) | Leçons de l'ancien gestionnaire de fenêtres Blazor (appel de base oublié dans `OnAfterRenderAsync`, état de la solution v3 en août 2026). |
| [`manifest-serializer-notes.md`](manifest-serializer-notes.md) | Sérialiseur strict de `AppManifest` et fixture canonique. |
| [`p2-idb-008-filesystem-repository.md`](p2-idb-008-filesystem-repository.md) | Analyse des transactions du dépôt de fichiers IndexedDB (phase 2). |
