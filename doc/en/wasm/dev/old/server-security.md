# Optional Server Security Remediation

## Purpose

This document records the optional server's currently implemented security boundary and the evidence still required by the integration audit. The browser PWA remains independent of the server.

## Proxy architecture

- Endpoint identity comes from authenticated `NameIdentifier` and `device_id` claims.
- `ProxyService` verifies that the device still exists, belongs to the authenticated account, and is not revoked before any outbound work.
- `IProxyAddressResolver` provides a testable DNS boundary. Production uses the operating-system resolver; unit tests use deterministic addresses and never contact DNS.
- Every address returned by DNS is checked. Loopback, private, link-local, carrier-grade NAT, documentation, benchmark, multicast, unspecified, IPv6 unique-local, and IPv4-mapped variants are rejected.
- A deterministic validated address is carried by `IProxyConnectionPinAccessor` to `SocketsHttpHandler.ConnectCallback`. The socket connects to that exact address while the original URI host remains available to HTTP Host handling and TLS SNI.
- Redirects are manual and each target is resolved, validated, and pinned independently.

## Current evidence

- [x] Reject a device owned by a different authenticated account before transport.
- [x] Reject a revoked device before transport.
- [x] Reject non-HTTP protocols and non-HTTP(S) target schemes.
- [x] Reject IPv4-mapped IPv6 loopback.
- [x] Prove the validated address is visible during transport and cleared afterward.
- [x] Focused command: `dotnet test Tests/HackerOs.Server.Tests/HackerOs.Server.Tests.csproj --configuration Release --no-restore` — 40 passed, 0 failed, 0 skipped on 2026-08-04. `ServerStartupIntegrationTests` proves the documented `HACKEROS_ConnectionStrings__HackerOsDb` override drives fresh schema creation and `/health`; it also rejects anonymous account access and proves bounded SQLite backup/restore.

## Remaining gates

- [ ] Add a durable server app-registration and capability-grant model. `AppId` is currently validated syntactically but cannot yet be derived from or checked against a trusted registration.
- [ ] Add controlled socket-level integration tests for rebinding, multi-address hosts, TLS SNI/Host preservation, redirect chains, timeouts, cancellation, and response streaming limits.
- [ ] Add bandwidth and durable quota policies plus explicit startup warnings for every operator weakening.
- [ ] Complete durable sync idempotency, ownership checks, restart recovery, cursor, tombstone, and chunk-resume coverage.
- [ ] Run migration, persistence, backup/restore, server-absence, and authenticated endpoint integration matrices.

These incomplete items keep `P5-PROXY-002` through `P5-PROXY-007` and the corresponding Phase 5 gate unchecked.
