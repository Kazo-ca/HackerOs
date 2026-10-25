# ADR 0023: Optional Game Domain Integration & Network Proxy Fallback

## Status
**ACCEPTED** (Approval evidence: User directive on `P4-W6-GATE-002`)

## Context
During Wave 6 (Gameplay Domains), the system introduces `HackerOs.Game.Core` (missions, contracts, virtual security mechanics, hardware upgrades, and reputation). However, HackerOS v3 requires a modular build where the **Game Domain is completely optional** at compile time and runtime:

1. Certain host deployments or utility builds of HackerOS may be compiled without the Game Domain library/services (`HackerOs.Game.Core`).
2. Terminal commands and apps (e.g., `ping`, `curl`, `nmap`, `cat`) must support dual mode:
   - **Standalone / Proxy Mode:** Operates either against local virtual OS primitives or routes through Phase 5 HTTP/WebSocket Server Proxy.
   - **Game Domain Mode:** When Game Domain is enabled in the host DI container AND declared in the app's `app.manifest.json` capabilities (`gameplay.domain.access`), commands interact with the Game Domain state machine, mission targets, and simulated game topology.

## Decision

1. **Capability Requirement (`gameplay.domain.access`):**
   - Apps and commands requiring Game Domain integration MUST declare `"gameplay.domain.access"` in their manifest `capabilities` list.
   - Without this capability, the app container will not inject `IGameDomainGateway` or route network calls to the game simulation state machine.

2. **DI Registration & Fallback Gateway (`IGameDomainGateway`):**
   - Define `IGameDomainGateway` in `HackerOs.Simulation.Abstractions/Gateways/AppGatewayContracts.cs` (or `HackerOs.Game.Abstractions`).
   - If `HackerOs.Game.Core` is not registered in DI, a `NullGameDomainGateway` (or `ServerProxyGameDomainGateway`) fallback is provided.
   - Commands check `IGameDomainGateway.IsAvailable`. If false (or if server proxy mode is active), network commands (`ping`, `curl`, `cat`) fall back to the Phase 5 Server Proxy / standard simulated network gateway.

3. **Conditional Build Slices:**
   - `HackerOs.Game.Core` is packaged as an optional project reference in `HackerOs.Platform.Blazor.csproj` / `HackerOs.Platform.Core.csproj`, controllable via build property `<EnableGameDomain>true</EnableGameDomain>`.

## Consequences

- **Pros:**
  - Complete decoupling: OS shell & utility apps can be compiled without gameplay assets or game logic.
  - Flexibility: Commands can operate seamlessly in pure OS simulation mode, server proxy mode, or gameplay mission mode.
  - Manifest enforcement: Auditable capability grant for apps attempting to interact with game contracts or player save state.
- **Cons:**
  - Gateway routing in network/filesystem commands must handle fallback branches cleanly.
