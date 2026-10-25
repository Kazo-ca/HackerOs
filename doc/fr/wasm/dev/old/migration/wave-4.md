# Wave 4: Simulated Network, Browser, and Websites Migration Report

## Overview

This report documents the migration of the simulated network, DNS resolver, website controllers, browser application, and network terminal commands (`ping`, `nmap`, `curl`) into Blazor WebAssembly C# (`wasm2/HackerOs/`).

---

## Migrated Component Matrix

| Task ID | Feature Name | Legacy Source | C# Project / Location | Automated Test Evidence | Status |
|---|---|---|---|---|---|
| `P4-W4-001` | Network Domain Contracts | `src/core/network.ts` | `Shared/HackerOs.Simulation.Abstractions/Network/` | `Wave4NetworkTests` | **MIGRATED** |
| `P4-W4-002` | In-Memory Network Registry | `src/core/network.ts` | `Platform/HackerOs.Platform.Core/Network/` | `Wave4NetworkTests` | **MIGRATED** |
| `P4-W4-003` | Browser App Port | `src/apps/browser.ts` | `Apps/System/HackerOs.Apps.Browser/` | `BrowserWindow`, `SimulatedPageRenderer` | **MIGRATED** |
| `P4-W4-004` | Rendering Strategy ADR | — | `docs/adr/0021-simulated-network-and-browser-rendering.md` | ADR 0021 (DECISION: D-015 Component Model) | **MIGRATED** |
| `P4-W4-005` | Website Controllers | `src/websites/` | `Platform/HackerOs.Platform.Core/Network/Websites/` | `HackerSearch`, `HackMail`, `CryptoBank`, `Darknet`, `Forum` | **MIGRATED** |
| `P4-W4-006` | Network Terminal Commands | `src/commands/` | `Apps/Commands/HackerOs.Commands.{Ping,Nmap,Curl}/` | `PingCommandTests`, `NmapCommandTests`, `CurlCommandTests` | **MIGRATED** |
| `P4-W4-007` | Zero External Network Proof | — | `Tests/HackerOs.Network.Tests/Wave4NetworkTests.cs` | `NetworkService_MakesZeroRealSocketsOrHttpCalls` | **MIGRATED** |

---

## Key Decisions Made

- **Component Model Rendering (ADR 0021 / D-015):** Simulated pages produce structured `SimulatedPage` objects (typed C# records) instead of raw HTML. Rendered headlessly in unit tests and using Blazor components in the Browser window app. Zero XSS risk, zero DOM iframe dependence.
- **Session Cookie Isolation:** Each Browser window instance maintains an isolated session cookie jar in C#. Closing the window clears session cookies.
- **Capabilities:** Added `network.simulated.read` and `network.simulated.write` to `AppCapabilities.cs`. Real proxy calls remain excluded until Phase 5.
- **Zero Network Traffic:** All DNS, ping, port scan, and HTTP requests are executed against pure in-memory data structures.
