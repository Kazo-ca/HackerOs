> [Version française](../../../../fr/wasm/dev/adr/0092-game-in-a-separate-repository.md)

# ADR 0092: the game lives in a separate repository

## Status

Accepted on 2026-10-17 (decision by Benoit, answer of 2026-10-17 01:28Z:
"Nothing concerning the game, it will be in another repository that will
use the ecosystem we are currently creating").

- **Supersedes:** [ADR 0023](0023-optional-game-domain-and-proxy-fallback.md)
  (optional game domain in the solution, capability `gameplay.domain.access`,
  `IGameDomainGateway`, property `<EnableGameDomain>`).
- **Plan:** [`wasm-v3-completion-plan.md`](../plans/wasm-v3-completion-plan.md), §1.

## Context

The wasm v3 migration analysis (`doc/en/wasm/dev/plans/wasm-v3-migration-analyse.md`, wave 6)
and `doc/en/wasm/gameplay-v3-analyse.md` planned to build the game
(missions, contracts, economy, hardware upgrades, simulated security,
player scripts, game save) in this solution, as an optional domain
(ADR 0023). Only a sketch of it exists:
`Game/HackerOs.Game.Core` (in memory, referenced by no host),
`Shared/HackerOs.Game.Abstractions`, `Tests/HackerOs.Game.Tests` and the capability
`gameplay.domain.access`. The `<EnableGameDomain>` property never existed.

## Decision

1. This repository builds **the platform**: the simulated OS, its public SDK, its
   base apps and commands, its hosts and its optional server. It
   contains no game content or rules.
2. The game is developed in **another repository**, which uses this ecosystem
   as any third-party developer would: through the public SDK and the
   platform packages, without reference to internal types.
3. What the game will ask of the OS is added here as **generic extension
   points** of the SDK (useful to any app), never as
   a game-specific API.
4. The existing game code and the `gameplay.domain.access` capability are removed
   from the solution (work item `v3/2-game-removal`). Until this removal, they
   stay in place but must not be extended.

## Consequences

- Wave 6 of the migration analysis is no longer a remaining task of this repository.
  `doc/en/wasm/gameplay-v3-analyse.md` and `docs/old/migration/wave-6.md` remain
  references for the game repository.
- Allowing another repository to build on HackerOS becomes an objective:
  SDK versions and packages, `dotnet new` templates, SDK 1.0 freeze
  (work items `v3/6` and `v3/15`).
- The catalog will go from 32 to 31 capabilities at removal; `capabilities.md` and
  `sdk-reference.md` will note it at that time.
- The network commands (`ping`, `curl`, `nmap`, `cat`) have no "game" mode:
  they use the OS's simulated network and the optional server's relay
  (ADR 0056, 0085).
