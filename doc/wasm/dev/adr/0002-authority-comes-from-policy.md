# ADR 0002: Authority Comes from Trusted Policy

## Status

Accepted on 2026-08-01.

## Context

HackerOS requires `System > Administrator > User` authority. System apps have
high operational rights, but allowing a package manifest to declare itself a
System app would make the hierarchy meaningless. A system UI also must not lend
its authority to the normal user operating it.

## Decision

App manifests do not contain an authority grant. Trusted build/install policy
assigns system-app status. Authorization evaluates the granted app capability
and the acting user's authority. Explicit OS-owned work uses a separate audited
System execution context.

## Consequences

- Installing an app cannot elevate it merely by changing manifest JSON.
- A normal user cannot modify protected settings through a system Text Editor.
- Registry and policy implementation must produce execution contexts rather than
  trusting app-provided claims.
- Protected writes must record user, app, authority, operation, and timestamp.