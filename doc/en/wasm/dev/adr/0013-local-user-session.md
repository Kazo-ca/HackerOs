# ADR 0013: Local User and Session Model

## Status

Accepted on 2026-08-01.

## Context

The first HackerOS slice is local-first and must work offline without a server.
It needs User and Administrator identities, login/logout/shutdown cancellation,
protected settings authority, deterministic test users, and per-user home
directories. Server OIDC, synchronized identity, and device registration belong
to Phase 5.

Client-side local authentication is a product boundary for honest local users,
not protection against a user who can inspect or modify the PWA/IndexedDB.
Server authorization must never trust this local authority claim.

## Decision

### Stable identities

The local repository stores immutable opaque user and group IDs separately from
mutable display names. Login names are unique, normalized, case-insensitive ASCII
identifiers used in home paths. Display names may contain Unicode.

Every user has:

- immutable user ID;
- normalized login name;
- display name;
- enabled/disabled state;
- authority (`User` or `Administrator`);
- primary group ID and additional group IDs;
- optional local password credential;
- creation/update revision and UTC audit timestamps; and
- home path `/home/{loginName}`.

`System` is not a login-capable user role. System authority exists only in
explicit audited kernel operation contexts and still requires exact capabilities.

### First-run administrator

A clean Release profile enters first-run setup and requires creation of one local
Administrator before normal desktop startup. There is no shipped username,
default password, hidden backdoor, or automatic Release administrator.

Development/test build profiles may explicitly define a deterministic bootstrap
administrator. The profile is rejected in Release publish and its use is visible
in diagnostics. Automated tests create users through repositories/fixtures rather
than depending on UI setup.

The last enabled Administrator cannot be disabled, demoted, or deleted until a
different enabled Administrator exists.

### Passwords

Local passwords are optional by product policy. First-run setup may require one
for the initial Administrator; later local-only User accounts may be configured
for passwordless login.

Passwords are never stored or logged as plaintext. A credential record stores a
random salt, versioned password-based KDF identifier/parameters, and verifier.
The implementation uses a reviewed platform cryptographic primitive with a
configurable work factor and constant-time verifier comparison. It supports
rehashing after successful login when parameters are obsolete.

Password reset is an explicit Administrator operation and is audited. Local
credentials are not sync credentials and are not sent to the optional server.
The UI must not describe client-only passwords as protection against local data
inspection or modification.

### Session state machine

The browser runtime owns at most one active HackerOS user session per tab:

```text
Uninitialized -> LoggedOut -> Starting -> Active -> LoggingOut -> LoggedOut
                                      \-> ShuttingDown -> Stopped
```

Starting validates the selected user, creates an immutable authenticated
principal and session ID, owns the root session cancellation source, provisions
the home layout idempotently, and activates required apps in dependency order.
Failure rolls back started volatile lifecycle state and returns to LoggedOut.

Logout and shutdown first stop new launches, cancel the session root, request
bounded process/service cleanup, record forced stops, deactivate apps in reverse
dependency order, clear volatile principal/session state, and return to LoggedOut
or Stopped. Abrupt browser close provides no cleanup guarantee.

Each process receives a linked independently cancellable token. Process close/
kill never cancels the entire session unless process policy explicitly defines a
session shutdown operation. Tokens and process parents are never transferred.

### Authority and elevation

The session principal carries the user's actual authority. Running a first-party
or system app does not elevate it. Administrator users perform Administrator
operations through their session authority and exact app capabilities.

A normal User may request a short-lived elevation operation when an enabled
Administrator reauthenticates. Elevation produces an audited, operation-scoped
authorization result; it does not mutate the User session into an Administrator
session, lend credentials to the app, or grant System authority. Persistent
administrator actions remain unavailable without explicit reauthentication.

**Exception:** see ADR 0042 for the terminal's `sudo` command, which intentionally grants
full unrestricted root (`AppAuthority.System`) for exactly one launched command after live
Administrator reauthentication — a deliberate, narrower exception to this section's "never
grants System" rule, not a realization of the elevation model described above. The
admin-approval prompt on a denied permission (also ADR 0042) stays within this section's
intent: it never grants System, only a one-shot ticket scoped to the exact denied operation.

Failed login/elevation attempts use bounded in-memory throttling and audit events.
This improves UX/abuse resistance but is not presented as a tamper-resistant
security control in client code.

### Home and settings ownership

Successful first login calls the filesystem seeder for
`/home/{loginName}` and standard directories. Provisioning is idempotent and uses
the immutable user/group identity in metadata. User-owned app settings use the
ADR 0011 app/user or roaming projection paths. Disabling a user retains data;
deletion requires an explicit separate data-retention choice and is not part of
the first slice.

### Persistence and tabs

User records and credentials persist locally behind repository contracts. Active
session/process tokens are volatile. Reload starts LoggedOut unless a later
explicit remembered-session decision is approved.

Multiple browser tabs may hold independent volatile sessions but must detect
persistent revision conflicts. Cross-tab single-session enforcement is deferred
until browser repository behavior is implemented; no headless contract assumes
it.

## Exclusions

- Server OIDC, passkeys, synchronized credentials, and device registration.
- Password recovery through email or a server.
- Claiming local credentials resist a user who controls browser storage/code.
- Persisting or resuming volatile active process/service state.
- Automatic deletion of a disabled/deleted user's home or app data.

## Consequences

- Offline startup and login do not require a server.
- Release builds never ship default administrator credentials.
- Authority remains user/session-owned rather than app-owned.
- Elevation is explicit, short-lived, audited, and never grants System.
- Home provisioning reuses the validated filesystem seeder.
- Phase 5 can add synchronized identity without changing local-only users.

## References

- ADR 0002: Authority Comes from Trusted Policy
- ADR 0011: Settings Scope Keys and Projection Paths
- ADR 0012: Deterministic Process, Clock, and Resource Model
- `docs/session-and-process-lifecycle.md`
- `docs/virtual-filesystem.md`
- `doc/en/wasm/dev/plans/wasm-v3-migration-analyse.md` sections 10.1 and 15