> [Version française](../../../../fr/wasm/dev/adr/0233-server-issued-real-network-grants.md)

# ADR 0233: the server issues the real-network grant approved in the OS, with its ports

## Status

Accepted on 2026-11-07: Benoit chose the recommendations of the four
questions of the thread "Accepter les ports déclarés par les apps" (see
"Questions put to Benoit"). Code: `real-network/base` series, issue
[#395](https://github.com/Kazo-ca/Os/issues/395).

- **Completes:** [ADR 0235](0235-real-network-for-chat-apps.md) §3 (ports
  declared by the app) and [ADR 0031](0031-grants-sync.md) (pull-only
  synchronized grants): this is the "grant-issuing authority" ADR 0031
  announced.
- **Does not change:** the OS still pushes no `grants` record through sync;
  the approval window, its passwords and `/etc/kazoos/app-permissions.config`
  (ADR 0049); `GrantedApps`; blocked ranges, quotas; `BlockedPorts`.

## Context

- The relay grants `network.real.access` only to the apps of `GrantedApps`
  (`appsettings.json`) or to those a synchronized `grants` record of the
  account names (ADR 0108 §1). Work item `real-network/3-declared-ports`
  (#401) also reads the approved ports in that record (`realNetworkPorts`).
- But no code writes such a record: the OS never pushes a grant (ADR 0031 §1,
  so that a forged push cannot widen anything) and the server has no route to
  issue one. A third-party app approved by an administrator in the OS thus
  gets `GRANT_MISSING` from the relay, and its declared ports are useless.
  ADR 0235 precisely wanted to avoid an administrator editing
  `appsettings.json`.

## Decision

### 1. A server route issues the grant

`POST /api/proxy/grants` (authenticated like the other relay routes: account
and device checked) receives `ProxyGrantIssueRequest`:

| Field | Role |
|---|---|
| `AppId` | the approved app (same check as the relay routes) |
| `UserId` | the OS user the grant applies to |
| `RealNetworkPorts` | the approved ports, `tcp/6697` (`RealNetworkPorts.TryParse`); empty allowed |
| `Revoked` | `true` to withdraw the grant |

The server validates the **shape** itself (capability fixed to
`network.real.access`, valid ports without duplicates, at most 64 ports), then
**writes the `grants` record itself**: stable id per account, app and user (a
new approval is a new revision of the same record), `GrantsSyncPayload`
payload (source `AdministratorApproval`, `IsRevoked`, `RealNetworkPorts`),
hash computed by the server, audit entry `PROXY_GRANT_ISSUE` or
`PROXY_GRANT_REVOKE`. A request identical to the latest revision creates
nothing. The OS gets the record back through the usual (pull-only) sync.

A port of `BlockedPorts` is accepted in the grant (it stays refused on use,
and Settings shows it): the policy can change without rewriting grants.

### 2. Trust

The server is single-user (ADR 0027): it trusts the authenticated account and
device to say that an OS administrator approved, as it already trusts them for
the app id (ADR 0108 §1). The route only allows `network.real.access` and
well-formed ports; `BlockedPorts`, blocked ranges and quotas keep the last
word. The operator can close this door: `KazoOs:Proxy:AcceptOsGrants`
(default `true`); when `false`, the route answers `403 GRANT_ISSUING_DISABLED`,
the relay ignores grants issued that way, and only `GrantedApps` counts.
Startup warning when `AcceptOsGrants` and `AllowAllPorts` are both true.

### 3. When the OS asks for the grant

- On approval: when `network.real.access` becomes `allow` (special window,
  terminal text mode, Permissions tab of Settings) and a server is connected,
  the OS calls the route with the approved ports. Without a server nothing is
  sent; the local approval still counts for `KazoClientWebSocket`
  (ADR 0235 §1).
- At each sync (the "Sync" button and the scheduled task) and on connecting to
  a server, the OS **reconciles**: it issues the grant of every app allowed
  `network.real.access` in the machine file, and withdraws it for every app
  denied (machine or user `deny`) or whose permission disappeared. Identical
  requests create nothing.
- A network failure blocks neither the launch nor the approval: it is written
  to `/var/log/syslog` and retried at the next sync.

### 4. The approved ports are kept in the OS

The approval writes the ports it showed in a new section of the machine file:

```text
# /etc/kazoos/app-permissions.config
[permissions]
ca.kazo.irc:network.real.access=allow

[real-network-ports]
ca.kazo.irc=tcp/6667,tcp/6697
```

The OS sends the server these ports, not the manifest's. When an app update
declares a port that is not there, `network.real.access` goes back to
`Pending` for that app (same decision table, ADR 0049): the window comes back
at the next interactive launch and lists the new ports, like a system asking
again for a widened permission. A port the update removed stays approved until
the next approval, with no effect since the app no longer asks for it. An
invalid line of the section is ignored with a warning, as in `[permissions]`.

## Questions put to Benoit

Benoit's answer (2026-11-07): "Je suis d'accord avec tes recommandations",
so 1 A, 2 A, 3 A and 4 A.

1. How do the grant and its ports reach the server?
   - A) New server route that issues the grant after the OS approval.
     **(Recommended, chosen)**
   - B) The administrator only, in `GrantedApps`.
   - C) The OS sends the ports with each stream.
2. Shape of the declaration: `AppDefinition.RealNetworkPorts`, single ports
   `tcp/N` or `udp/N`. **(Recommended, chosen, shipped by #401)**
3. Shipped `BlockedPorts`: mail, RPC and file sharing over TCP, amplification
   services over UDP. **(Recommended, chosen, shipped by #401)**
4. `BlockedPorts` always wins, even over `AllowedPorts` and `AllowAllPorts`.
   **(Recommended, chosen, shipped by #401)**

## Consequences

- A third-party app approved in the OS reaches its declared ports through the
  relay without an administrator touching the server; the operator can forbid
  it (`AcceptOsGrants: false`).
- Contracts: `ProxyGrantIssueRequest`, `ProxyGrantIssued`, code
  `GRANT_ISSUING_DISABLED`; Platform Core: route client, reconciliation,
  `[real-network-ports]` section. The public SDK does not change.
- Planned tests: issue and revision, identical request without effect, badly
  written port refused, `AcceptOsGrants: false`, reconciliation (issues,
  withdraws), new port of an update asking for approval again, relay accepting
  the port after issuing.
- Docs: [`server-network-relay.md`](../server-network-relay.md),
  [`dangerous-capability-approval.md`](../dangerous-capability-approval.md),
  `features.md`, `changes/unreleased` entry.
