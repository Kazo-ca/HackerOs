# ADR 0042: `sudo` Root Elevation and the Admin-Approval Prompt

## Status

Accepted on 2026-08-23.

## Context

The capability/authority model (ADR 0002, ADR 0003) is deny-by-default: an app's manifest
declares exact capabilities, `CleanProfileCapabilityGrantSeeder` grants exactly those, and
`FileSystemAuthorizer` enforces authority, capability, and Unix-style mode bits on every
operation. Until now, a denial was a dead end — a window app showed a plain "Permission
Denied" `MessageBox` (`WindowAppBase.HandlePermissionDenied`) and a terminal command printed
its own error text, with no way to unblock the action short of editing the offending app's
manifest by hand (exactly what an earlier pass in this session had to do to fix `cd`/`ls`/etc.
missing `filesystem.system.read`).

The user asked for two related capabilities a real Unix shell has: an admin-approval prompt for
a denied permission, and a `sudo` command that runs one command as root. ADR 0013's "Authority
and elevation" section already anticipates something like this — "a normal User may request a
short-lived elevation operation when an enabled Administrator reauthenticates" — but explicitly
scopes it to **never grant `AppAuthority.System`** and never mutate the session. The user
explicitly chose full, unrestricted root semantics for `sudo` (bypassing both Unix-mode-bit
checks and capability grants entirely for the elevated command), which is a deliberate,
narrower exception to that constraint rather than an implementation of it.

## Decision

1. **`sudo` is scoped to exactly one launched Terminal command, never the session.** Typing
   `sudo <command> [args...]` prompts for any enabled Administrator account's credentials
   (UAC-style "over-the-shoulder" — not necessarily the current user's own), verifies them via
   the new `AdminElevationVerifier` (reusing `LocalSessionService`'s own lookup/hash-check
   sequence without touching session state), and — only on success — mints a single-use,
   30-second-TTL `ElevationTicket` (`ElevationScope.SudoRoot`) via `InMemoryElevationTicketStore`.
   That token threads through `ExecuteCommandIntent` → `AppLaunchRequest` →
   `AppLifecycleOrchestrator.LaunchAsync`, which consumes it exactly once and sets the new
   `AppOperationContext.IsRootElevated` flag for that one launch only. `EffectiveAuthority`
   becomes `AppAuthority.System` and `HasCapability` unconditionally returns `true` for that
   instance — a deliberate, narrow exception to ADR 0013's "never grants System" rule, not a
   general-purpose way to reach `System` authority. `AuthenticatedPrincipal.Authority` and the
   interactive session are never touched; a fresh `sudo` invocation always re-authenticates.
2. **The admin-approval prompt grants a one-shot, in-memory ticket, not a standing
   `CapabilityGrant`.** `CapabilityGrantSource.AdministratorApproval` already existed in the
   capability-grant model but was never produced by any code path; it remains unused here on
   purpose. A standing grant persists until explicitly revoked and is the wrong shape for
   "unblock exactly the one retried operation" — so approval instead mints an
   `ElevationScope.CapabilityApproval` ticket (or, for an already-running window app, grants
   directly into that instance's live `OneShotElevationGrantSet`) scoped to the exact
   `(capability, path)` that was denied. `FileSystemAuthorizer.Authorize` consumes a matching
   ticket before any other check and only once; a second attempt is denied again unless
   re-approved. Because the set is a live object referenced by the running `AppOperationContext`
   (not a snapshot copied at launch), granting into it after launch takes effect on a window
   app's very next matching call — unlike its frozen `GrantedCapabilities` set, which is
   computed once and would otherwise require relaunching the app to see a new standing grant.
3. **Every workflow re-authenticates; nothing is remembered.** Both features share
   `AdminElevationVerifier` and `IElevationTicketStore`/`IElevationService`, but never a session
   flag, a cached credential, or a persisted grant. Every verification attempt — success or
   failure — is recorded in `IAuditLog` under the `session.elevate` action, mirroring
   `LocalSessionService.RecordAudit`'s existing pattern for `session.login`.
4. **`IsRootElevated` is a dedicated field, not a reuse of `IsSystemOperation`.**
   `AppOperationContext.IsSystemOperation` is reserved for trusted, platform-owned operations
   (seeders, the orchestrator's own association-cleanup pass) that already supply their own
   narrow `GrantedCapabilities` set. Conflating it with a user-invoked, credential-gated `sudo`
   command would blur an audit-relevant distinction — "the OS did this" vs. "a user, after
   re-authenticating as an administrator, did this" — for no implementation benefit, since both
   flags fold into the same `EffectiveAuthority` computation anyway.
5. **The dialog reuses the existing basic-dialog pipeline**, not a new one. `IDialogService`
   (`DialogCoordinator` → `FileDialogWindowAdapter` → `WindowRuntime` → `DesktopShell`) already
   projects `MessageBoxDialog`/`TextInputDialog` as owner-modal windows; a new
   `CredentialPromptDialogRequest`/`CredentialPromptDialog` presentation follows the identical
   shape, so both the terminal's `sudo`/retry flow and `WindowAppBase.HandlePermissionDenied`
   reuse the same queueing, focus, and window-projection code that file dialogs already exercise
   end-to-end.

## Consequences

- `sudo <command>` genuinely runs one command as unrestricted root — real risk if the game later
  wants "malware that steals sudo" scenarios to matter; the mitigation is that it always demands
  a *fresh* Administrator credential (no session, no remembered grant), so a compromised
  low-privilege process cannot self-elevate without a human in the loop.
- Denied window-app operations now offer a real, working recovery path (approve, then retry the
  action) instead of a dead-end message; terminal commands additionally offer an automatic
  one-shot retry with the newly granted ticket.
- ADR 0013's "Authority and elevation" section describes a narrower, non-`System`-granting
  elevation this ADR does not implement — `sudo` is this ADR's own, separate, explicitly scoped
  exception, not a realization of ADR 0013's design. A future "operation-scoped Administrator
  elevation that never reaches System" remains open and unimplemented.
- `FileSystemError` now carries `RequiredCapability` on every authority/capability/permission
  denial, which any future UI (not just this admin-approval prompt) can use to explain exactly
  what was missing.

## References

- ADR 0002: Authority Comes from Trusted Policy
- ADR 0003: Exact Capability Matching
- ADR 0013: Local User Session Model (the "Authority and elevation" section this ADR
  deliberately diverges from for `sudo` specifically)
