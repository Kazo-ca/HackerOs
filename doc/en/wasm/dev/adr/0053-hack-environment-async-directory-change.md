> [Version française](../../../../fr/wasm/dev/adr/0053-hack-environment-async-directory-change.md)

# ADR 0053: Asynchronous current directory change and reading the login environment

## Status

Accepted on 2026-10-10.

- **Plan phase:** phase 3, Shell, commands and trash
  ([`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), section 7.2).
- **Partially replaces:** ADR 0050, section 6, for a single point: the
  mutator `HackEnvironment.CurrentDirectory { set; }`. The rest of
  section 6 and all of section 5 remain in force.
- **Clarifies:** ADR 0050, sections 5 and 6, on when
  `/etc/environment` and `/etc/hostname` are read.

## Context

ADR 0050 gives `HackEnvironment.CurrentDirectory` a `set` accessor that
throws `HackDirectoryNotFoundException` if the path does not exist. Checking
existence requires a `stat` in the file system. In HackerOS, every file system
access is asynchronous (IndexedDB in the browser) and the `Hack*` API of
ADR 0043 only exposes `...Async` methods. A synchronous `set` accessor would
have to either block the browser's single thread (impossible in
WebAssembly), or accept a path without checking it, which contradicts the ADR.

ADR 0050 also says that the login environment is "built when the session
opens" and that `MachineName` is the first line of
`/etc/hostname`, without saying when these files are read.

## Decision

1. `HackEnvironment.CurrentDirectory` is **read-only**. Changing it
   goes through:

   ```csharp
   public static Task SetCurrentDirectoryAsync(string path, CancellationToken cancellationToken = default);
   ```

   The path can be absolute, relative to the current directory, or start with
   `~`. A missing path throws `HackDirectoryNotFoundException`; a file throws
   `HackIOException` (`NotDirectory`); a denial throws
   `HackUnauthorizedAccessException`. Like `chdir`, the check goes through
   the app's file gateway, with its capabilities and permissions.

2. `/etc/environment` and `/etc/hostname` are read **once per session**, on the
   first launch of a process with no parent in that session, by
   `LoginEnvironmentProvider`, as root (like `pam_env` during login). A change
   to these files applies to the next session, not to processes already
   launched. For `/etc/hostname`, this matches Linux, where the kernel's
   host name is read at boot and does not follow the file.

## Consequences

- No blocking call and no missing check in the SDK.
- The `cd` commands (work item `3-shell-commands`) call
  `SetCurrentDirectoryAsync` or go through the shell's controlled access
  (ADR 0051), which performs the same check.
- Changing the machine name requires a new session, like a
  reboot on Linux.

## References

- ADR 0043 (`Hack*` API), ADR 0050 (process environment), ADR 0051.
- [`process-environment.md`](../process-environment.md).
