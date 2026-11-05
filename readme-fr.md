> [English version](readme.md)

[![Release Kazo OS](https://github.com/Kazo-ca/Os/actions/workflows/release.yml/badge.svg)](https://github.com/Kazo-ca/Os/actions/workflows/release.yml)
[![Verify WASM](https://github.com/Kazo-ca/Os/actions/workflows/deploy-wasm.yml/badge.svg?branch=main)](https://github.com/Kazo-ca/Os/actions/workflows/deploy-wasm.yml)
[![Latest release](https://img.shields.io/github/v/release/Kazo-ca/Os?label=release)](https://github.com/Kazo-ca/Os/releases/latest)
[![NuGet SDK](https://img.shields.io/nuget/v/Kazo.Os.AppSdk?label=NuGet%20SDK)](https://www.nuget.org/packages/Kazo.Os.AppSdk)
[![NuGet SDK downloads](https://img.shields.io/nuget/dt/Kazo.Os.AppSdk?label=SDK%20downloads)](https://www.nuget.org/packages/Kazo.Os.AppSdk)
[![NuGet templates downloads](https://img.shields.io/nuget/dt/Kazo.Os.Templates?label=templates%20downloads)](https://www.nuget.org/packages/Kazo.Os.Templates)
[![Last commit](https://img.shields.io/github/last-commit/Kazo-ca/Os)](https://github.com/Kazo-ca/Os/commits/main)
[![License: CC BY-NC 4.0](https://img.shields.io/badge/license-CC%20BY--NC%204.0-lightgrey)](LICENSE.md)

# Kazo OS

Kazo OS est un **système d'exploitation simulé qui tourne dans le navigateur**,
écrit en C# avec Blazor WebAssembly (.NET 10). Il offre un environnement complet
pour développer et exécuter des applications : bureau et fenêtres, système de
fichiers persistant, processus, shell POSIX et commandes Linux, réseau simulé,
sites web internes, et un **SDK public** (paquets NuGet) pour créer ses propres
apps, commandes, services et sites.

- **Essayer** : <https://os.kazo.ca/> (PWA, fonctionne hors ligne).
- **Fonctionnalités** : [présentation et fonctionnalités de Kazo OS](doc/fr/wasm/features.md) : ce que Kazo OS offre aujourd'hui, le but de l'écosystème, les façons de le déployer et ce qui est en cours de développement.
- **Code** : [`wasm2/KazoOs/`](wasm2/KazoOs/readme-fr.md).
- **Documentation** : [`doc/fr/wasm/README.md`](doc/fr/wasm/README.md) (en anglais : [`doc/en/wasm/README.md`](doc/en/wasm/README.md)).
- Dépôt source : [Kazo-ca/Os](https://github.com/Kazo-ca/Os).

## Démarrer

Prérequis : le SDK .NET 10 (`wasm2/KazoOs/global.json`).

```bash
cd wasm2/KazoOs
dotnet build Kazo.Os.sln
dotnet test Kazo.Os.sln
dotnet run --project OS/Kazo.Os.Ecosystem     # l'OS en PWA autonome
```

Les hôtes de débogage, la publication trimmée, les contournements connus et
l'exécution des tests E2E sont décrits dans
[`building-and-testing.md`](doc/fr/wasm/dev/building-and-testing.md).

## Documentation

| Pour… | Lire |
| --- | --- |
| Découvrir ce que Kazo OS offre et ce qui s'en vient | [`features.md`](doc/fr/wasm/features.md) |
| Comprendre le code de l'OS | [`project-overview.md`](doc/fr/wasm/dev/project-overview.md), puis [`doc/fr/wasm/dev/`](doc/fr/wasm/dev/README.md) |
| Écrire une app, une commande ou un service | [`sdk-developer-guide.md`](doc/fr/wasm/framework/sdk-developer-guide.md) et [`sdk-reference.md`](doc/fr/wasm/framework/sdk-reference.md) |
| Construire sur Kazo OS depuis un autre dépôt | [`building-on-kazoos.md`](doc/fr/wasm/framework/building-on-kazoos.md) |
| Créer un site web interne (ASP.Kazo, Blazor) | [`asp-kazo-developer-guide.md`](doc/fr/wasm/framework/asp-kazo-developer-guide.md) |
| Éviter les problèmes déjà rencontrés | [`pitfalls.md`](doc/fr/wasm/dev/pitfalls.md) |
| Connaître l'état du projet et ses conventions | [`doc/fr/wasm/memory/`](doc/fr/wasm/memory/README.md) |
| Les décisions d'architecture | [`doc/fr/wasm/dev/adr/`](doc/fr/wasm/dev/adr/README.md) |

## Structure du dépôt

| Dossier | Contenu |
| --- | --- |
| [`wasm2/KazoOs/`](wasm2/KazoOs/readme-fr.md) | **Version active** : la solution Kazo OS (OS, SDK, apps, hôtes, tests). |
| [`doc/`](doc/fr/README.md) | Documentation. |
| [`src/`](src/readme-fr.md) | Version legacy en TypeScript, gardée en lecture seule comme référence. |
| [`.github/workflows/`](.github/workflows/) | Vérification et publication GitHub Pages. |
| `Art/` | Icônes et captures d'écran. |

## Version legacy (TypeScript)

La première version, en TypeScript et Webpack, reste dans [`src/`](src/readme-fr.md)
et est publiée sur <https://os.kazo.ca/legacy/>. Elle ne reçoit plus de
nouvelles fonctionnalités. Sa documentation (compilation avec Node.js 20,
structure, fonctionnalités) est dans [`src/README.md`](src/readme-fr.md).

Les deux versions sont publiées par un seul
[workflow GitHub Pages](.github/workflows/deploy-to-pages.yml)
([guide de déploiement](doc/fr/wasm/dev/github-pages-deployment.md)).

## Demander une fonctionnalité

1. Consultez la [présentation des fonctionnalités](doc/fr/wasm/features.md) :
   la fonctionnalité existe peut-être déjà ou est en cours de développement.
2. Cherchez dans les [issues ouvertes](https://github.com/Kazo-ca/Os/issues) :
   si quelqu'un l'a déjà demandée, ajoutez votre cas d'usage en commentaire.
3. Sinon, ouvrez une issue avec le
   [formulaire **Demande de fonctionnalité**](https://github.com/Kazo-ca/Os/issues/new?template=feature_request.yml) :
   décrivez le besoin (ce que vous voulez faire et pourquoi), la solution que
   vous imaginez et la partie de l'OS concernée. Le français comme l'anglais
   conviennent.
4. Le mainteneur répond dans l'issue. Pour signaler un problème, utilisez
   plutôt le
   [formulaire **Rapport de bogue**](https://github.com/Kazo-ca/Os/issues/new?template=bug_report.yml).

Les détails sont dans [`CONTRIBUTING-fr.md`](CONTRIBUTING-fr.md#demander-une-fonctionnalité).

## Contribuer

Les règles de contribution sont dans [`CONTRIBUTING-fr.md`](CONTRIBUTING-fr.md) et
[`AGENTS.md`](AGENTS.md) (emplacement du code, documentation à jour à chaque
changement, index des dossiers de la doc, travail avec le mainteneur :
questions, issues `TODO`, branches, fusions). Claude Code les lit par
[`CLAUDE.md`](CLAUDE.md) ; l'état et la mémoire du projet sont dans
[`doc/fr/wasm/memory/`](doc/fr/wasm/memory/README.md).

## Activité du dépôt

![Activité du dépôt](https://repobeats.axiom.co/api/embed/b0f0139bc9926c01d0d3e1df65a930041bba0d8c.svg "Repobeats analytics image")

## Licence

Voir [`LICENSE.md`](LICENSE.md).
