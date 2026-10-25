> [Version française](../../../../fr/wasm/dev/adr/0136-sudo-nice.md)

# ADR 0136: `sudo nice` runs the command as root

## Status

Accepted on 2026-10-18 (Benoit's answer "Yes, allow it" to the card
"Should `sudo nice -n -5 command` work?", thread of ADR 0127).

- **Amends:** ADR 0051 §4 (`sudo` rejects any command that declares
  `shell.control`) for the `nice` command only.
- **Complements:** ADR 0127 §1 (only root lowers a `nice` value).

## Context

- On Linux, `sudo nice -n -5 make` is the usual way to run a
  command with a higher priority: `nice` does an `exec` of the command,
  which inherits the root identity of `sudo`.
- In HackerOS, `nice` declares `shell.control`: it launches its command through the
  shell (`IShellSession.RunNicedAsync`). ADR 0051 §4 makes `sudo` reject
  any command of this kind ("shell builtin"), because an elevated `cd`
  would have nothing to act on. `sudo nice …` was therefore rejected, and you had to
  launch the command then `sudo renice -n -5 -p PID`.
- An elevation ticket (ADR 0042) is only good once: if `nice` itself
  were elevated, its command could no longer be.

## Decision

1. `sudo` accepts `nice` (`org.hackeros.commands.nice`), and only it, among the
   commands that declare `shell.control`. `sudo cd`, `sudo export` remain
   rejected as before.
2. As for any command, `sudo` first asks for an administrator's credentials
   (a single request: "Run as root: nice -n -5 make").
3. `nice` runs under the user's identity and receives `sudo`'s root ticket with its
   shell access. It passes it to the command it launches, like an
   `exec`: it is the command that runs as root. It is the child of
   `nice`, itself a child of `sudo`.
4. With this ticket, `nice` can lower the value: the command starts at
   `nice + adjustment` (bounded to -20..19), without the "Permission
   denied" warning.
5. The ticket remains single-use: a second launch by the same `nice` is
   rejected (`ElevationDenied`).

## Consequences

- `sudo nice -n -5 make` works as on Linux. `sudo renice` remains possible
  for an already launched process.
- `ps` shows `nice` under the user and the command under root, whereas
  Linux has no `nice` process left after the `exec`. This is the accepted gap of
  point 3: it keeps the ticket single-use.
- No public API changes: the ticket goes through the internal shell access
  (`ShellSessionAccess`).
