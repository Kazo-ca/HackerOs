# ADR 0092 : le jeu vit dans un dépôt séparé

## Statut

Accepté le 2026-10-17 (décision de Benoit, réponse du 2026-10-17 01:28Z :
« Rien en ce qui concerne le jeu, cela sera dans une autre repository qui va
utiliser l'écosystème que nous créons présentement »).

- **Remplace :** [ADR 0023](0023-optional-game-domain-and-proxy-fallback.md)
  (domaine de jeu optionnel dans la solution, capacité `gameplay.domain.access`,
  `IGameDomainGateway`, propriété `<EnableGameDomain>`).
- **Plan :** [`wasm-v3-completion-plan.md`](../plans/wasm-v3-completion-plan.md), §1.

## Contexte

L'analyse de migration wasm v3 (`doc/en/wasm/dev/plans/wasm-v3-migration-analyse.md`, vague 6)
et `doc/en/wasm/gameplay-v3-analyse.md` prévoyaient de construire le jeu
(missions, contrats, économie, améliorations matérielles, sécurité simulée,
scripts du joueur, sauvegarde de partie) dans cette solution, sous forme d'un
domaine optionnel (ADR 0023). Il n'en existe qu'une ébauche :
`Game/HackerOs.Game.Core` (en mémoire, référencé par aucun hôte),
`Shared/HackerOs.Game.Abstractions`, `Tests/HackerOs.Game.Tests` et la capacité
`gameplay.domain.access`. La propriété `<EnableGameDomain>` n'a jamais existé.

## Décision

1. Ce dépôt construit **la plateforme** : l'OS simulé, son SDK public, ses
   apps et commandes de base, ses hôtes et son serveur optionnel. Il ne
   contient aucun contenu ni règle de jeu.
2. Le jeu est développé dans **un autre dépôt**, qui utilise cet écosystème
   comme le ferait n'importe quel développeur tiers : par le SDK public et les
   paquets de la plateforme, sans référence à des types internes.
3. Ce que le jeu demandera à l'OS est ajouté ici sous forme de **points
   d'extension génériques** du SDK (utiles à toute app), jamais sous forme
   d'une API propre au jeu.
4. Le code de jeu existant et la capacité `gameplay.domain.access` sont retirés
   de la solution (chantier `v3/2-game-removal`). Jusqu'à ce retrait, ils
   restent en place mais ne doivent pas être étendus.

## Conséquences

- La vague 6 de l'analyse de migration n'est plus un reste à faire de ce dépôt.
  `doc/en/wasm/gameplay-v3-analyse.md` et `docs/old/migration/wave-6.md` restent
  des références pour le dépôt du jeu.
- Permettre à un autre dépôt de construire sur HackerOS devient un objectif :
  versions et paquets du SDK, gabarits `dotnet new`, gel du SDK 1.0
  (chantiers `v3/6` et `v3/15`).
- Le catalogue passera de 32 à 31 capacités au retrait ; `capabilities.md` et
  `sdk-reference.md` le noteront à ce moment-là.
- Les commandes réseau (`ping`, `curl`, `nmap`, `cat`) n'ont pas de mode « jeu » :
  elles utilisent le réseau simulé de l'OS et le relais du serveur optionnel
  (ADR 0056, 0085).
