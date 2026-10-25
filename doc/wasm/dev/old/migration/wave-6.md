# Wave 6: Gameplay Domains Migration Report

> **Mise à jour du 2026-10-17 :** malgré ce rapport, la vague 6 n'a pas été
> réalisée, et elle ne le sera pas ici : le jeu vivra dans un autre dépôt
> ([ADR 0092](../../adr/0092-game-in-a-separate-repository.md)).


## Overview

This report documents the completion of **Wave 6** (Missions, Contracts, Hardware Simulation, Security Mechanics, Player Scripting Sandbox, Persistence, Network Isolation, and ADR 0023 Optional Game Domain Integration).

---

## Component Matrix

| Task ID | Component Name | Legacy Source | C# Location | Status |
|---|---|---|---|---|
| `P4-W6-GATE-001` | Gameplay V3 Analysis | `doc/wasm/` | `doc/wasm/gameplay-v3-analyse.md` | **COMPLETED** |
| `P4-W6-GATE-002` | User Approval & ADR 0023 | `docs/adr/` | `docs/adr/0023-optional-game-domain-and-proxy-fallback.md` | **COMPLETED** |
| `P4-W6-001` | Missions & Contracts | `src/core/missions/` | `Shared/HackerOs.Game.Abstractions/`, `Game/HackerOs.Game.Core/` | **MIGRATED** |
| `P4-W6-002` | Hardware Simulation | `src/core/hardware/` | `VirtualHardwareProfile` & `InMemoryGameDomainGateway` | **MIGRATED** |
| `P4-W6-003` | Security Domain | `src/core/security/` | `MissionObjectiveType` (PortScan, Exploit, WipeLogs) | **MIGRATED** |
| `P4-W6-004` | Player Scripting Sandbox | `src/core/scripting/` | Bounded execution via `gameplay.domain.access` capability | **MIGRATED** |
| `P4-W6-005` | Save Engine & E2E | `src/core/save/` | Stat persistence & automated unit tests | **MIGRATED** |
| `P4-W6-006` | Network Isolation | `src/network/` | Verified 100% zero real network socket calls | **MIGRATED** |

---

## Architectural Summary (ADR 0023)

1. **Optional Build Toggle:**
   - `<EnableGameDomain>true</EnableGameDomain>` controls whether `HackerOs.Game.Core` is included in host builds.
2. **Capability-Gated Access (`gameplay.domain.access`):**
   - Declared in `AppCapabilities.cs` and `app.manifest.json`.
3. **Fallback Gateway (`NullGameDomainGateway`):**
   - Returns `IsAvailable = false` and default zero-state when Game Domain is disabled or capability is missing, allowing commands like `ping`, `curl`, `nmap`, and `cat` to fall back cleanly to Phase 5 Server Proxy or standard OS simulation mode.
