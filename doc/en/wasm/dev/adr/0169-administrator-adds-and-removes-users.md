> [Version française](../../../../fr/wasm/dev/adr/0169-administrator-adds-and-removes-users.md)

# ADR 0169: an administrator adds and removes accounts

## Status

Proposed on 2026-10-25, at Benoit's request ("add the ability to
add or remove users when you are an administrator"). The
question about the home folder was put to Benoit in the work item thread; the
code follows the recommended choice (★) pending his answer.

- **Completes:** [ADR 0013](0013-local-user-session.md) (local accounts and
  groups), [ADR 0120](0120-passwd-and-administrator-password-reset.md)
  (`users.manage`, `HackUserAccounts`, Settings ▸ Users page).
- **Goes with:** [ADR 0168](0168-passkey-sign-in.md) (passkeys).
- **Adds to SDK 2.1:** members in `PublicAPI.Unshipped.txt`.
- **Documents:** [`settings-app.md`](../settings-app.md),
  [`../../framework/system-administration.md`](../../framework/system-administration.md).

## Context

- The only account created is created by the first-boot wizard
  (administrator, `administrators` group). Nothing allows adding another
  or removing one.
- `ILocalUserRepository` knows how to create an account but not delete it. The home
  folder is created at first sign-in (`FileSystemSeeder`).
- The dangerous capability `users.manage` and the administrator authentication
  agent already exist (ADR 0120).

## Decision

### 1. Adding an account, like `useradd -m` + `passwd`

- `HackUserAccounts.CreateAsync(new HackNewUserAccount(login, displayName,
  authority, password))` requires `users.manage` **and** an administrator (agent
  window; nothing under `sudo`). `auth.log`: `users.add`.
- The login name follows `LocalLoginName` (lowercase, digits, `-`, `_`,
  at most 32 characters), is not reserved (`hackeros-svc`, ADR 0097, `root` and
  `administrators`) and is neither
  an existing account nor group. The display name is mandatory (at most 128
  characters).
- The initial password is mandatory and follows the rules of ADR 0120.
- Groups, like `USERGROUPS_ENAB`: a private group of the same name becomes the
  primary group; an administrator is also a member of `administrators`.
- The home folder is created at first sign-in, as today.

### 2. Deleting an account, like `userdel` ★

- `HackUserAccounts.DeleteAsync(login, deleteFolder)` requires `users.manage`
  and an administrator. `auth.log`: `users.delete`.
- Refused for: the session's account (one does not delete oneself), the
  last active administrator. The service account `hackeros-svc` is not
  a local account and cannot be targeted.
- The account and its passkeys (ADR 0168) are deleted. The account's private
  group is left (no file is reassigned). The capability authorizations
  granted to the account remain in their store, which has no
  per-user search: they are orphaned and have no effect, because an
  account identifier (GUID) is never reused, even for an account
  recreated under the same name.
- **Home folder ★**: a checkbox "Also delete the home folder",
  unchecked by default (`userdel` keeps it, `userdel -r` deletes it). The
  folder is deleted as a system operation, keyring included. If kept, it
  stays at `/home/<login>`; an account recreated under the same name finds it again.

### 3. Settings ▸ Users

- For an administrator: an "Add user" form (login name, display
  name, standard/administrator type, password and
  confirmation) and, in the list, a "Delete" button for each account other than
  their own, with the home folder checkbox and a confirmation.
- For everyone: a "Passkeys" section (ADR 0168 §5) that lists their keys,
  adds and removes them.
- Only the content of the Users page changes; the Settings navigation
  is not touched.

### 4. Where the code lives

- `Simulation.Abstractions`: `ILocalUserRepository.DeleteUserAsync`
  (default implementation: `NotSupportedException`); the repository itself refuses
  to delete the last active administrator.
- `Platform.Core/Sessions/LocalAccountService`: creation and deletion,
  rules of §1 and §2, `auth.log`.
- `AppUserAccountsGateway`: capability, administrator agent, then the service.

### 5. Question put to Benoit

Home folder on deletion: **A ★** checkbox unchecked by default,
**B** always deleted, **C** never deleted.

## Consequences

- Several people can have their session on the same installation,
  each with their own folder, settings and keyring.
- A deleted account whose folder is kept leaves files whose
  owner no longer exists; `ls -l` then shows the raw identifier.
