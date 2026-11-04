> [English version](CONTRIBUTING.md)

# Contribuer à Kazo OS

Merci de votre intérêt pour Kazo OS, un système d'exploitation simulé qui
tourne dans le navigateur (C#, Blazor WebAssembly, .NET 10). Ce guide explique
comment demander une fonctionnalité, signaler un bogue et proposer un
changement.

Les règles détaillées, partagées avec les agents de code, sont dans
[`AGENTS.md`](AGENTS.md). L'état actuel du projet et ses conventions (séries en
cours, prochain numéro d'ADR libre, échecs de tests connus) sont dans
[`doc/fr/wasm/memory/`](doc/fr/wasm/memory/README.md).

## Code de conduite

Aidez-nous à garder ce projet ouvert et accueillant. En participant, vous
acceptez de :

- respecter les points de vue et les expériences de chacun ;
- accepter les critiques constructives avec ouverture ;
- penser d'abord à ce qui est le mieux pour le projet et ses utilisateurs ;
- faire preuve d'empathie envers les autres contributeurs.

## Demander une fonctionnalité

1. Lisez la [présentation des fonctionnalités](doc/fr/wasm/features.md) : la
   fonctionnalité existe peut-être déjà, ou figure dans « En cours de
   développement ».
2. Cherchez dans les [issues ouvertes](https://github.com/Kazo-ca/Os/issues),
   y compris celles avec l'étiquette `TODO` (la liste des choses à faire du
   mainteneur) : quelqu'un l'a peut-être déjà demandée. Dans ce cas, ajoutez
   votre cas d'usage en commentaire plutôt que d'ouvrir un doublon.
3. Ouvrez une [nouvelle issue avec le formulaire **Demande de fonctionnalité**](https://github.com/Kazo-ca/Os/issues/new?template=feature_request.yml).
   Décrivez d'abord le besoin (ce que vous voulez faire et pourquoi), puis la
   solution que vous imaginez, les parties de l'OS touchées et les
   solutions de rechange envisagées. Vous pouvez écrire en français ou en
   anglais.
4. Le mainteneur répond dans l'issue. Une grosse fonctionnalité reçoit en
   général une décision d'architecture (ADR) dans
   [`doc/fr/wasm/dev/adr/`](doc/fr/wasm/dev/adr/README.md) avant d'écrire du
   code.

Une fonctionnalité pour une app tierce (une app portée dans un dépôt
`Kazo-ca/Os-App-*`, le modèle d'app ou le magasin d'apps) se demande dans
le dépôt de cette app.

## Signaler un bogue

Ouvrez une issue avec le
[formulaire **Rapport de bogue**](https://github.com/Kazo-ca/Os/issues/new?template=bug_report.yml) :
étapes pour reproduire, ce que vous attendiez, ce qui s'est passé, la version
de Kazo OS (Paramètres ▸ À propos, ou la fenêtre « Nouveautés »), le
navigateur et l'appareil. Une capture d'écran ou la sortie de la console du
navigateur aide beaucoup.

## Faire tourner le code

Prérequis : le SDK .NET 10 (`wasm2/KazoOs/global.json`).

```bash
cd wasm2/KazoOs
dotnet build Kazo.Os.sln
dotnet test Kazo.Os.sln
dotnet run --project OS/Kazo.Os.Ecosystem     # l'OS en PWA autonome
```

Les hôtes de débogage, la publication trimmée, les tests E2E et les
contournements connus sont dans
[`building-and-testing.md`](doc/fr/wasm/dev/building-and-testing.md).
Commencez par [`project-overview.md`](doc/fr/wasm/dev/project-overview.md) pour
vous repérer dans le code, et lisez [`pitfalls.md`](doc/fr/wasm/dev/pitfalls.md)
avant votre premier changement.

Pour créer une app, une commande ou un site **en dehors** de ce dépôt, utilisez
le [modèle d'app Kazo OS](https://github.com/Kazo-ca/Os-AppTemplate) et
[`building-on-hackeros.md`](doc/fr/wasm/framework/building-on-hackeros.md).

## Où va le code

- **Tout nouveau code va dans [`wasm2/KazoOs/`](wasm2/KazoOs/readme-fr.md).**
  [`src/`](src/readme-fr.md) est la version legacy en TypeScript, gardée en
  lecture seule comme référence de comportement : n'y ajoutez ni n'y modifiez
  jamais de code.
- La PWA autonome (`OS/Kazo.Os.Ecosystem`) doit toujours fonctionner sans
  serveur. `Server/Kazo.Os.Server` est optionnel : rien ne doit en dépendre.
- C# d'abord. Du JavaScript seulement pour les API du navigateur que C# ne
  peut pas atteindre, livré comme ressource du composant ou de la bibliothèque
  qui en a besoin, jamais comme nouveau `<script src>` dans un hôte.
- Pas de CSS ni de JS en ligne dans le balisage `.razor` : les styles dans
  `MonComposant.razor.css`, le script dans `MonComposant.razor.js` (vérifié à la
  compilation, [ADR 0007](doc/fr/wasm/dev/adr/0007-enforce-collocated-razor-assets.md)).
- MudBlazor seulement pour les surfaces complexes (menus, grilles, onglets,
  formulaires, dialogues), jamais dans le SDK ni dans les contrats
  d'abstraction.
- Le style est « moderne » ou « gothique/hacker », jamais des contrôles bruts
  sans style.
- Les noms de fichiers et de dossiers sont en anglais. Les méthodes publiques
  et le code non évident portent des commentaires de documentation XML.
- Aucun jeu sur ce repository
  ([ADR 0135](doc/fr/wasm/dev/adr/0135-no-game-in-this-repository.md)).

## Proposer une pull request

1. Faites un fork du dépôt (ou, pour les mainteneurs, créez une branche) à
   partir de `main`.
2. Donnez à la branche un nom qui décrit son contenu, par exemple
   `docs/contributing-feature-requests` ou `desktop/icon-grid-snapping`,
   jamais un nom générique comme `feature/1`. Une série de changements liés
   utilise une branche d'intégration `<serie>/base` et une branche
   `<serie>/<n>-<sujet>` par chantier.
3. Faites le changement, avec ses tests et sa documentation (voir plus bas).
4. Écrivez les messages de commit au format
   [Conventional Commits](https://www.conventionalcommits.org/fr/)
   (`feat:`, `fix:`, `docs:`, `test:`, `refactor:`, `chore:`…).
5. Ouvrez la pull request vers `main` et remplissez le
   [modèle](.github/pull_request_template.md). Liez l'issue qu'elle ferme
   (`Closes #…`).
6. Seul le mainteneur fusionne dans `main`.

### Tests

- Une compilation et une exécution des tests vertes en local sont requises
  avant de demander une fusion. Lancez les suites E2E une à la fois.
- Quand le changement les concerne, lancez aussi
  `wasm2/KazoOs/build/publish-trimmed.sh` et le projet de compatibilité du SDK
  `wasm2/KazoOs/Tests/fixtures/sdk-compat`.
- Ne jamais sauter, désactiver ni mettre en quarantaine un test pour obtenir
  du vert. Les échecs déjà connus sont listés dans
  [`known-test-failures.md`](doc/fr/wasm/memory/known-test-failures.md) :
  nommez-les dans la PR, ne les cachez pas.
- GitHub Actions vérifie aussi chaque PR.

### Documentation

- La documentation est bilingue, avec les mêmes dossiers et noms de fichiers :
  le français (l'original) dans [`doc/fr/`](doc/fr/README.md), l'anglais dans
  [`doc/en/`](doc/en/README.md). Chaque changement de doc met à jour les deux
  langues dans la même PR, ainsi que l'index `README.md` de chaque dossier
  touché.
- Une PR qui livre, modifie ou planifie une fonctionnalité met à jour la
  présentation des fonctionnalités
  ([`doc/fr/wasm/features.md`](doc/fr/wasm/features.md) et
  [`doc/en/wasm/features.md`](doc/en/wasm/features.md)).
- Une décision d'architecture est consignée dans une ADR sous
  `doc/*/wasm/dev/adr/`, avec le prochain numéro libre (voir le README de la
  mémoire et les PR ouvertes). Une ADR acceptée n'est jamais modifiée sur
  place : une nouvelle ADR la remplace.
- Un changement du SDK public met à jour le fichier `PublicAPI.Unshipped.txt`
  du paquet concerné.

### Entrées de changement

Une PR vers `main` qui change des fichiers du produit sous `wasm2/KazoOs/`
(hors `Tests/`, `docs/`, `changes/` et fichiers `.md`) ajoute une entrée
`wasm2/KazoOs/changes/unreleased/<guid>.json` avec un texte en anglais et en
français, affichée dans la fenêtre « Nouveautés »
([ADR 0186](doc/fr/wasm/dev/adr/0186-whats-new-change-list.md),
[`change-list.md`](doc/fr/wasm/dev/change-list.md)). Vérifiez-la en local après
le commit avec `wasm2/KazoOs/build/tools/check-change-entry.sh`. Une PR sans
changement visible porte plutôt l'étiquette `no-changelog`.

## Versions

La version est dans `wasm2/KazoOs/build/Versions.props`. Quand une PR qui la
change est fusionnée dans `main`, le workflow de release crée le tag, la
GitHub Release et publie les paquets NuGet
([ADR 0173](doc/fr/wasm/dev/adr/0173-automatic-releases-from-the-version-file.md),
[`releases.md`](doc/fr/wasm/dev/releases.md)).

## Obtenir de l'aide

Ouvrez une issue avec l'étiquette `question`, ou lisez
l'[index de la documentation](doc/fr/wasm/README.md).

Merci de contribuer à Kazo OS !
