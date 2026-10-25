# ADR 0120: `passwd`, password reset by an administrator and effect on the keyring

## Status

Accepted on 2026-10-17 (follow-up of the work item `v3/8-settings-security`). Benoit
chose "In v3/8" on the decision card of the work item thread: the
`passwd` command and the reset in Settings are added now.

- **Complements:** [ADR 0100](0100-sensitive-settings-and-protected-write-audit.md)
  §2 (the keyring is re-encrypted on password change and is set
  aside on a reset), [ADR 0013](0013-local-user-session.md)
  (local accounts, `LocalPasswordHasher`), ADR 0055 §5 (administrator
  authentication agent).
- **Adds to SDK 1.1:** members in `PublicAPI.Unshipped.txt`, with no
  break ([ADR 0114](0114-sdk-1-0-freeze.md)).
- **Document:** [`sensitive-settings.md`](../sensitive-settings.md).

## Context

- An account has a `LocalPasswordCredential` (PBKDF2-SHA-256) set at creation,
  but nothing allowed changing or resetting it:
  `ILocalUserRepository` had no operation for that.
- The keyring of ADR 0100 already had the two entry points
  `UserKeyringService.ChangePasswordAsync` and `SetAsideAsync`, which nothing
  called.
- The terminal had no masked input: the line of a program that reads
  standard input is always displayed. `sudo` goes through a window of the
  authentication agent, not through the terminal.

## Decision

### 1. Changing one's own password, without a capability

- Like `passwd`, which is `setuid` and checks the old password through PAM,
  an app changes the password of **the session user** without a
  capability: `HackUserAccounts.ChangePasswordAsync(current, new)`.
- The current password is checked (empty if the account has none). A failure
  is written to `auth.log` (`passwd.change`, denied) and throws
  `UnauthorizedAccessException`.
- Rules for the new password: at least 8 characters (like the wizard that
  creates the first account), at most 256, different from the current one
  (`Password unchanged`). Otherwise `ArgumentException`.
- The user's keyring, unlocked, is re-encrypted under the new
  password (`ChangePasswordAsync`). An account that had no
  password had no keyring: it is created at the next login.

### 2. Resetting another account's password: dangerous capability and administrator

- `HackUserAccounts.ResetPasswordAsync(login, new)` and
  `RemovePasswordAsync(login)` (`passwd -d`) require the new **dangerous**
  capability `users.manage` **and** an administrator, through the authentication
  agent (ADR 0055 §5); nothing is requested under `sudo`.
- The target account's keyring becomes unreadable: the OS renames it
  `login.keyring.old` (`SetAsideAsync`), like GNOME. If the target account is
  the session's, the open keyring is re-encrypted (reset) or
  set aside then locked (removal).
- Removing the password of the **last active administrator** is refused: without a
  password, anyone could open the session that can approve everything.
- `auth.log` receives `passwd.reset` or `passwd.delete` with the administrator
  who confirmed.

### 3. Listing accounts

`HackUserAccounts.ListAsync()` gives, without a capability, the login name, the
display name, the authority, the state and the presence of a password for each account,
as `/etc/passwd` is readable by everyone. Never the hash.

### 4. Masked input in the terminal

- The terminal's standard input implements `ITerminalInputMode`
  (`EchoEnabled`), the equivalent of the `ECHO` flag of `termios`. When it is
  turned off, the terminal's line discipline shows neither the characters nor their
  erasure; Enter still moves to the next line.
- `TerminalExecutionContext.ReadSecretLineAsync(prompt)` writes the prompt, turns off
  echo, reads a line and restores it, like `getpass(3)`. An input that is
  not a terminal (pipe) is read as is.
- `passwd` stays on the main thread (ADR 0070): a worker's input is
  not the terminal.

### 5. The `passwd` command

| Form | Effect |
|---|---|
| `passwd` | Changes one's password: `Current password:`, `New password:`, `Retype new password:`. |
| `passwd NAME` | One's own account: like `passwd`. Another account: `sudo` required (`You may not view or modify password information for NAME.`), then `New password:` and `Retype new password:`. |
| `passwd -d NAME` | Removes the password (under `sudo`). |
| `passwd -S [NAME]` | `passwd -S` style status: `NAME P` (password), `NP` (none) or `L` (account disabled), with the date of last change. |

Under `sudo` without a name, `passwd` refuses: HackerOS has no `root` account with
a password.

### 6. Settings: "Users" tab

The tab lists the accounts. Everyone can change their password there; an
administrator can reset another account's (with the warning
that its keyring will be set aside).

### 7. API additions

| Library | Addition |
|---|---|
| `HackerOs.Simulation.Abstractions` | `ILocalUserRepository.SetCredentialAsync` (default member that throws `NotSupportedException`) |
| `HackerOs.App.Abstractions` | `AppCapabilities.UsersManage` (`users.manage`, dangerous) |
| `HackerOs.AppSdk` | `HackUserAccounts`, `IAppUserAccountsGateway`, `HackUserAccount`, `IAppExecutionContext.UserAccounts` (default member), `ITerminalInputMode`, `TerminalExecutionContext.ReadSecretLineAsync` |
| `HackerOs.AppSdk.Terminal` | `TerminalInputReader` implements `ITerminalInputMode` |
| `HackerOs.Platform.Core` | `LocalPasswordService`, `SystemAdministrationGatewayServices.Passwords` (`init` property) |

## Consequences

- An app compiled against 1.0 keeps running: each addition to an
  interface has a default implementation.
- The catalog gains a capability (36); the `passwd` command is added to the base
  commands.
- No complexity policy or expiration (`chage`): only the minimum length
  is checked.
