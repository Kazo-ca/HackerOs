# ADR 0134: automatic connection to the server that serves the OS, and status icon in the notification area

## Status

Accepted on 2026-10-18 (Benoit's answers to questions E1 to E4 of the thread
"ADR for the step 5 follow-ups").

- **Replaces:** [ADR 0130](0130-server-serves-webassembly-os-and-api.md)
  §4 (prefilled address, connection at the user's choice).
- **Confirms:** ADR 0130 §2 (E1: the server serves the published files of
  the Ecosystem), ADR 0125 §6 (E3: Linux values for `nofile` and `nproc`),
  ADR 0132 §2 (E4: SDK 1.2 released once, at the end of the series).
- **Implemented by:** `exec/8-server-wasm`.

## Context

Benoit chose answer A to E2, specifying it: "automatic connection,
without asking the user. An icon in the notification area
shows the connection state".

## Decision

### 1. Connection without a question

- At startup, the OS queries `GET /api/hackeros/host-info` on its own
  origin (short timeout, nothing offline). If the server answers, the OS
  registers it as the machine's server and **connects to it right away**,
  with no box or question.
- Connecting means: checking the API version, opening the synchronization channel
  and the relay. If the session user has already opened an
  account on this server (token kept, ADR 0028), the session also resumes
  without a question. Otherwise, the OS is connected **without an account**: what requires
  an account waits until the user identifies themselves from the icon (§2).
- A server lost along the way is retried with an increasing delay. The OS never depends
  on the server (AGENTS.md I.1): disconnected, it keeps working offline.
- An administrator can turn off this behavior in
  `/etc/hackeros/server.config` (`auto-connect=false`); the OS then goes back to
  manual entry in Settings.

### 2. Status icon

- An icon in the taskbar's notification area, visible as soon as
  a server is configured (automatically or by hand, Ecosystem
  included).
- States: **Connected**, **Connected, not identified**, **Connecting…**,
  **Offline** (server unreachable), **Error** (incompatible version, access
  denied). Tooltip with the server address and state.
- A click opens a small panel: state, address, account, last
  synchronization, buttons "Sign in" (if not identified), "Reconnect"
  and "Server settings".
- Platform component in native Blazor with isolated CSS (taskbar,
  AGENTS.md II.1); texts in English and French (ADR 0104);
  `aria-live` for state changes and keyboard navigation
  (ADR 0105).

## Consequences

- `HackerOs.Server` gains `GET /api/hackeros/host-info` (API version,
  server name); the OS gains the detection and the icon.
- Tests: boot served by the server → connection without a box; server stopped →
  "Offline" icon, OS usable; `auto-connect=false` respected.
- Docs: [`server-network-relay.md`](../server-network-relay.md),
  [`project-overview.md`](../project-overview.md) and the server connection
  doc, at the time of the work item.

## References

- ADR 0028, 0104, 0105, 0125, 0130, 0132.
