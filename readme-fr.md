> [English version](readme.md)

# HackerSimulator : HackerOS

HackerOS est un **système d'exploitation simulé qui tourne dans le navigateur**,
écrit en C# avec Blazor WebAssembly (.NET 10). Il offre un environnement complet
pour développer et exécuter des applications : bureau et fenêtres, système de
fichiers persistant, processus, shell POSIX et commandes Linux, réseau simulé,
sites web internes, et un **SDK public** (paquets NuGet) pour créer ses propres
apps, commandes, services et sites.

- **Essayer** : <https://hackos.kazo.ca/> (PWA, fonctionne hors ligne).
- **Fonctionnalités** : [présentation et fonctionnalités de HackerOS](doc/fr/wasm/features.md) : ce que HackerOS offre aujourd'hui, le but de l'écosystème, les façons de le déployer et ce qui est en cours de développement.
- **Code** : [`wasm2/HackerOs/`](wasm2/HackerOs/readme-fr.md).
- **Documentation** : [`doc/fr/wasm/README.md`](doc/fr/wasm/README.md) (en anglais : [`doc/en/wasm/README.md`](doc/en/wasm/README.md)).
- Dépôt source : [Kazo-ca/HackerOs](https://github.com/Kazo-ca/HackerOs).

## Démarrer

Prérequis : le SDK .NET 10 (`wasm2/HackerOs/global.json`).

```bash
cd wasm2/HackerOs
dotnet build HackerOs.sln
dotnet test HackerOs.sln
dotnet run --project OS/HackerOs.Ecosystem     # l'OS en PWA autonome
```

Les hôtes de débogage, la publication trimmée, les contournements connus et
l'exécution des tests E2E sont décrits dans
[`building-and-testing.md`](doc/fr/wasm/dev/building-and-testing.md).

## Documentation

| Pour… | Lire |
| --- | --- |
| Découvrir ce que HackerOS offre et ce qui s'en vient | [`features.md`](doc/fr/wasm/features.md) |
| Comprendre le code de l'OS | [`project-overview.md`](doc/fr/wasm/dev/project-overview.md), puis [`doc/fr/wasm/dev/`](doc/fr/wasm/dev/README.md) |
| Écrire une app, une commande ou un service | [`sdk-developer-guide.md`](doc/fr/wasm/framework/sdk-developer-guide.md) et [`sdk-reference.md`](doc/fr/wasm/framework/sdk-reference.md) |
| Construire sur HackerOS depuis un autre dépôt | [`building-on-hackeros.md`](doc/fr/wasm/framework/building-on-hackeros.md) |
| Créer un site web interne (ASP.Hack, Blazor) | [`asp-hack-developer-guide.md`](doc/fr/wasm/framework/asp-hack-developer-guide.md) |
| Éviter les problèmes déjà rencontrés | [`pitfalls.md`](doc/fr/wasm/dev/pitfalls.md) |
| Connaître l'état du projet et ses conventions | [`doc/fr/wasm/memory/`](doc/fr/wasm/memory/README.md) |
| Les décisions d'architecture | [`doc/fr/wasm/dev/adr/`](doc/fr/wasm/dev/adr/README.md) |

## Structure du dépôt

| Dossier | Contenu |
| --- | --- |
| [`wasm2/HackerOs/`](wasm2/HackerOs/readme-fr.md) | **Version active** : la solution HackerOS (OS, SDK, apps, hôtes, tests). |
| [`doc/`](doc/fr/README.md) | Documentation. |
| [`src/`](src/readme-fr.md) | Version legacy en TypeScript, gardée en lecture seule comme référence. |
| [`.github/workflows/`](.github/workflows/) | Vérification et publication GitHub Pages. |
| `Art/` | Icônes et captures d'écran. |

## Version legacy (TypeScript)

La première version, en TypeScript et Webpack, reste dans [`src/`](src/readme-fr.md)
et est publiée sur <https://hackos.kazo.ca/legacy/>. Elle ne reçoit plus de
nouvelles fonctionnalités. Sa documentation (compilation avec Node.js 20,
structure, fonctionnalités) est dans [`src/README.md`](src/readme-fr.md).

Les deux versions sont publiées par un seul
[workflow GitHub Pages](.github/workflows/deploy-to-pages.yml)
([guide de déploiement](doc/fr/wasm/dev/github-pages-deployment.md)).

## Contribuer

Les règles de contribution sont dans [`CONTRIBUTING.md`](CONTRIBUTING.md) et
[`AGENTS.md`](AGENTS.md) (emplacement du code, documentation à jour à chaque
changement, index des dossiers de la doc, travail avec le mainteneur :
questions, issues `TODO`, branches, fusions). Claude Code les lit par
[`CLAUDE.md`](CLAUDE.md) ; l'état et la mémoire du projet sont dans
[`doc/fr/wasm/memory/`](doc/fr/wasm/memory/README.md).

## Licence

Voir [`LICENSE.md`](LICENSE.md).
