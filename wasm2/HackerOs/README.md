# HackerOS

Simulation d'un système d'exploitation de type Linux dans le navigateur, en C#
et Blazor WebAssembly, pour servir d'environnement où développer des applications.
**Aucun jeu sur ce repository** (ADR 0135). C'est le portage du simulateur JavaScript de `src/`, gardé en
lecture seule comme référence de comportement et qui n'est pas une dépendance
de cette solution.

## Démarrage rapide

```bash
dotnet build HackerOs.sln                      # .NET 10 (global.json)
dotnet test HackerOs.sln
dotnet run --project OS/HackerOs.Ecosystem     # l'OS, en PWA autonome
```

Prérequis, hôtes de débogage, publication et contournements en conteneur :
[`doc/wasm/dev/building-and-testing.md`](../../doc/wasm/dev/building-and-testing.md).

## Structure

| Dossier | Contenu |
|---|---|
| `Shared/` | Contrats (`*.Abstractions`) et SDK public des apps (`HackerOs.AppSdk*`). |
| `Platform/` | Services de l'OS (`Platform.Core`) et son interface (`Platform.Blazor`, fenêtres, barre des tâches, mobile, thème). |
| `Infrastructure/` | Persistance IndexedDB et interop navigateur. |
| `Apps/` | Apps de base (`System/`), commandes du shell (`Commands/`), exemples (`Samples/`). |
| `OS/HackerOs.Ecosystem` | Hôte livré : PWA autonome, hors ligne. |
| `test/test`, `Server/` | Hôte de débogage ; serveur optionnel (synchronisation, comptes, relais réseau). |
| `Tools/`, `build/` | Générateur de manifestes et cibles MSBuild du modèle d'app. |
| `Tests/` | Tests unitaires, d'intégration MSBuild et E2E. |

Le détail de chaque projet est dans
[`doc/wasm/dev/project-overview.md`](../../doc/wasm/dev/project-overview.md).

## Documentation

[`doc/wasm/README.md`](../../doc/wasm/README.md) est l'index de toute la documentation
(rédigée en français). Par où commencer :

- [`doc/wasm/dev/project-overview.md`](../../doc/wasm/dev/project-overview.md) : comprendre le code ;
- [`doc/wasm/framework/sdk-developer-guide.md`](../../doc/wasm/framework/sdk-developer-guide.md) et
  [`doc/wasm/framework/sdk-reference.md`](../../doc/wasm/framework/sdk-reference.md) : écrire une app ;
- [`doc/wasm/dev/plans/ecosystem-rebuild-plan.md`](../../doc/wasm/dev/plans/ecosystem-rebuild-plan.md) : la
  direction du projet et ses décisions ;
- [`doc/wasm/dev/adr/`](../../doc/wasm/dev/adr/) : les décisions d'architecture ;
- [`doc/wasm/dev/pitfalls.md`](../../doc/wasm/dev/pitfalls.md) : les pièges rencontrés et leurs solutions ;
- [`doc/wasm/memory/`](../../doc/wasm/memory/README.md) : la mémoire du projet (état des séries, conventions).

Les règles de contribution (emplacement du code, CSS et JavaScript
collocalisés, documentation à jour à chaque changement) sont dans
[`AGENTS.md`](../../AGENTS.md) à la racine du dépôt.
