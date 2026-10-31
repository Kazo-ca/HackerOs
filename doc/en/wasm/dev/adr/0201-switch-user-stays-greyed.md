> [Version française](../../../../fr/wasm/dev/adr/0201-switch-user-stays-greyed.md)

# ADR 0201: "Switch user" stays greyed out

## Status

Accepted on 2026-10-30 by Benoit. Issue [#290](https://github.com/Kazo-ca/HackerOs/issues/290)
(`TODO` label), series `todo/base`, work item `todo/16-switch-user`.

- **Completes:** [ADR 0195](0195-start-menu-power-button.md) §4 ("Switch user"
  greyed out in the Power menu).

## Context

- The Start menu's Power menu (ADR 0195) shows "Switch user" greyed out: the OS
  has a single session (`ISessionService.CurrentPrincipal`).
- The question "How should Switch user work?" was asked to Benoit with three
  choices:
  1. simultaneous sessions, as on Windows (several open sessions, only one in
     the foreground);
  2. close the session then pick an account (same as Sign out);
  3. keep the entry greyed out.
- A prototype of choice 1 was written while waiting for the answer: several
  session services, windows hidden per session, a switch-user screen, a limit of
  3 sessions, unit and end-to-end tests. It is kept on the branch
  `switch-user/simultaneous-sessions-prototype` and is not merged.

## Decision

- Benoit picked choice 3: **"Switch user" stays greyed out**, for every
  account, guest included. The OS code does not change.
- The OS keeps a single open session at a time (ADR 0013). To move to another
  account: **Sign out**, then pick the account on the sign-in screen.
- The prototype is neither merged nor deleted: it is a starting point if Benoit
  later decides to open several sessions. That day, a new ADR will supersede
  this one.

## Consequences

- No what's-new entry: nothing changes for the user.
- Issue #290 has no work left while the decision holds; Benoit decides whether
  to close it or keep it for later.
- The prototype branch is not kept up to date with `main`; picking it up again
  will need a new merge.
