# HackerSimulator : HackerOS

HackerOS est un **système d'exploitation simulé qui tourne dans le navigateur**,
écrit en C# avec Blazor WebAssembly (.NET 10). Il offre un environnement complet
pour développer et exécuter des applications : bureau et fenêtres, système de
fichiers persistant, processus, shell POSIX et commandes Linux, réseau simulé,
sites web internes, et un **SDK public** (paquets NuGet) pour créer ses propres
apps, commandes, services et sites.

- **Essayer** : <https://hackos.kazo.ca/> (PWA, fonctionne hors ligne).
- **Code** : [`wasm2/HackerOs/`](wasm2/HackerOs/README.md).
- **Documentation** (en français) : [`doc/wasm/README.md`](doc/wasm/README.md).
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
[`building-and-testing.md`](doc/wasm/dev/building-and-testing.md).

## Documentation

| Pour… | Lire |
| --- | --- |
| Comprendre le code de l'OS | [`project-overview.md`](doc/wasm/dev/project-overview.md), puis [`doc/wasm/dev/`](doc/wasm/dev/README.md) |
| Écrire une app, une commande ou un service | [`sdk-developer-guide.md`](doc/wasm/framework/sdk-developer-guide.md) et [`sdk-reference.md`](doc/wasm/framework/sdk-reference.md) |
| Construire sur HackerOS depuis un autre dépôt | [`building-on-hackeros.md`](doc/wasm/framework/building-on-hackeros.md) |
| Créer un site web interne (ASP.Hack, Blazor) | [`asp-hack-developer-guide.md`](doc/wasm/framework/asp-hack-developer-guide.md) |
| Éviter les problèmes déjà rencontrés | [`pitfalls.md`](doc/wasm/dev/pitfalls.md) |
| Connaître l'état du projet et ses conventions | [`doc/wasm/memory/`](doc/wasm/memory/README.md) |
| Les décisions d'architecture | [`doc/wasm/dev/adr/`](doc/wasm/dev/adr/README.md) |

## Structure du dépôt

| Dossier | Contenu |
| --- | --- |
| [`wasm2/HackerOs/`](wasm2/HackerOs/README.md) | **Version active** : la solution HackerOS (OS, SDK, apps, hôtes, tests). |
| [`doc/`](doc/README.md) | Documentation. |
| [`src/`](src/README.md) | Version legacy en TypeScript, gardée en lecture seule comme référence. |
| [`.github/workflows/`](.github/workflows/) | Vérification et publication GitHub Pages. |
| `Art/` | Icônes et captures d'écran. |

## Version legacy (TypeScript)

La première version, en TypeScript et Webpack, reste dans [`src/`](src/README.md)
et est publiée sur <https://hackos.kazo.ca/legacy/>. Elle ne reçoit plus de
nouvelles fonctionnalités. Sa documentation (compilation avec Node.js 20,
structure, fonctionnalités) est dans [`src/README.md`](src/README.md).

Les deux versions sont publiées par un seul
[workflow GitHub Pages](.github/workflows/deploy-to-pages.yml)
([guide de déploiement](doc/wasm/dev/github-pages-deployment.md)).

## Contribuer

Les règles de contribution sont dans [`CONTRIBUTING.md`](CONTRIBUTING.md) et
[`AGENTS.md`](AGENTS.md) (emplacement du code, documentation à jour à chaque
changement, index des dossiers de la doc).

## Licence

Voir [`LICENSE.md`](LICENSE.md).
