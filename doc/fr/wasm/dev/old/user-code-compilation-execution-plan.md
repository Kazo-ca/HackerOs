# Plan de compilation et d'exécution de code utilisateur (hybride WASM/serveur)

**Statut :** proposition technique — non implémentée, aucune décision `D-xxx`
actée, aucun code écrit.

## 1. Objectif

Permettre de concevoir une application pour l'écosystème HackerOS en C#, de la
compiler et de l'exécuter directement dans le navigateur (Blazor WebAssembly),
tout en gardant une voie d'exécution serveur équivalente, **sans dupliquer la
logique de compilation** entre les deux environnements.

Ce document capture l'exploration de faisabilité menée en session (2026-08-17)
avant tout engagement d'implémentation. Il sert de point de référence si ce
chantier est un jour priorisé.

## 2. Contexte et documents liés

- [ADR 0020](../adr/0020-editor-framework-and-script-sandbox.md) sépare
  strictement l'édition de code (`vfs.read`/`vfs.write`, aucune exécution) de
  l'exécution d'un script/programme utilisateur, contrôlée par la capacité
  `user-scripts.execute` et destinée à tourner dans un sandbox isolé. Ce plan
  est la suite naturelle de cette décision : il ne la remet pas en cause, il
  propose comment `user-scripts.execute` pourrait être honorée pour du C#
  compilé plutôt que pour un simple script.
- [`code-editor.md`](code-editor.md) documente l'éditeur actuel, qui n'a et ne
  doit garder aucune capacité de compilation ou d'exécution tant que ce plan
  (ou une ADR qui en découle) n'est pas accepté.
- [ADR 0023](../adr/0023-optional-game-domain-and-proxy-fallback.md) établit déjà
  le pattern architectural repris ici : une interface partagée dans un projet
  `*.Abstractions`, avec plusieurs implémentations (locale, proxy serveur,
  fallback) sélectionnées par DI/capability manifest — voir `IGameDomainGateway`.
  Ce plan applique le même pattern à la compilation/exécution de code.

## 3. Faisabilité technique (résumé de l'exploration)

La compilation et l'exécution de C# arbitraire depuis l'intérieur d'un host
Blazor WebAssembly sont réalisables, avec des contraintes précises :

- **Compilation en mémoire** via Roslyn (`Microsoft.CodeAnalysis.CSharp`,
  `CSharpCompilation`) puis `Assembly.Load(byte[])` : l'assembly obtenu est
  exécuté par l'interpréteur IL de Mono, sans besoin de JIT natif. C'est
  l'approche déjà utilisée par des projets externes connus (ex. Blazor REPL).
- **Pas de `Reflection.Emit`** : aucune génération d'IL dynamique à la volée
  n'est possible en WASM. Seule une compilation source complète via Roslyn
  fonctionne.
- **Mode AOT** : un assembly chargé dynamiquement à l'exécution ne peut pas
  être lui-même AOT-compilé ; il retombe sur l'interpréteur (mode mixte
  supporté depuis .NET 7+). Vérifié dans ce repo : aucun `.csproj` n'active
  `RunAOTCompilation` aujourd'hui, donc ce chemin n'est pas bloqué en l'état.
- **Trimming** : la compilation Roslyn a besoin des reference assemblies et de
  métadonnées de réflexion intactes. `PublishTrimmed` est déjà à `false` sur
  `HackerOs.Ecosystem` et `HackerOs.Server` aujourd'hui ; un futur passage à
  `true` sur ces projets casserait ce chemin sans annotations
  `[DynamicDependency]` complètes.
- **Isolation** : WebAssembly n'apporte **aucune** isolation contre le propre
  code hôte C#. Un assembly chargé dynamiquement tourne dans le même
  processus, avec le même accès JS-interop que le reste de l'application. La
  garantie d'isolation exigée par l'ADR 0020 ("isolated web-worker sandbox
  without access to host DOM or sensitive OS APIs") doit donc être construite
  architecturalement — par exemple un Web Worker dédié sans aucun binding
  JS-interop vers le DOM/API host, communication limitée à un canal
  `postMessage` typé — et non supposée fournie par le runtime.

## 4. Architecture hybride proposée

Sur le modèle de `IGameDomainGateway` (ADR 0023) :

- Un nouveau projet partagé, p. ex. `Shared/HackerOs.CodeExecution.Abstractions`,
  définirait deux contrats distincts :
  - **`ICompilationService`** — source C# → assembly en mémoire + diagnostics.
    Peut être **réellement partagé à 100 %** entre client et serveur : Roslyn
    est du C# portable, une seule implémentation suffit pour les deux côtés.
  - **`IExecutionSandbox`** — exécution d'un assembly compilé. Le **contrat**
    est partagé, mais **pas l'implémentation** :
    - Côté client WASM : exécution locale via l'interpréteur Mono, isolation
      construite via Worker dédié (voir section 3).
    - Côté serveur : exécution isolée au niveau OS (conteneur ou processus
      séparé — .NET moderne n'a plus d'AppDomains), exposée par
      `HackerOs.Server` et atteinte depuis le client via le pont proxy déjà
      utilisé pour `curl`/`nmap` (même famille de pattern que le proxy réseau
      documenté dans `server-implementation-pass.md`).
- Sélection de l'implémentation (locale vs serveur) au moment de l'exécution,
  selon disponibilité du serveur et policy — même logique de fallback que
  `IGameDomainGateway`/`NullGameDomainGateway`.
- Gate de capacité manifeste : `user-scripts.execute` (déjà défini par l'ADR
  0020), qu'il s'agisse de la voie locale ou de la voie serveur.

## 5. Hors scope pour l'instant

- Toute implémentation de code (ce document est volontairement non codé).
- Le choix définitif du mécanisme d'isolation serveur (conteneur vs sandbox
  process natif) — nécessite son propre design/ADR le moment venu.
- Les limites de ressources (CPU, mémoire, temps d'exécution) imposées au code
  utilisateur exécuté.
- L'UI/UX d'une éventuelle app "concevoir et exécuter une app pour
  l'écosystème" — ce document couvre seulement la couche
  compilation/exécution sous-jacente.

## 6. Prochaines étapes possibles (non engagées)

- [ ] Décision `D-xxx` formelle si ce chantier est un jour priorisé.
- [ ] ADR dédiée une fois l'architecture validée par le user.
- [ ] Prototype isolé de compilation seule (sans exécution) pour confirmer
  Roslyn en mode interpréteur WASM dans ce repo précis, avant tout travail sur
  le sandbox d'exécution.

## 7. Documents de référence

- [ADR 0020 — Code editor framework and script sandbox policy](../adr/0020-editor-framework-and-script-sandbox.md)
- [ADR 0023 — Optional Game Domain integration and network proxy fallback](../adr/0023-optional-game-domain-and-proxy-fallback.md)
- [`code-editor.md`](code-editor.md)
- [`server-implementation-pass.md`](server-implementation-pass.md)
- [`progress-and-plan-2026-08-17.md`](progress-and-plan-2026-08-17.md)
