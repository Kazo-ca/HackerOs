> [Version française](../../../fr/wasm/dev/guest-account.md)

# Guest account

## Purpose

Describes the HackerOS guest account: try the OS without creating an account,
then create your own account, keeping the guest's work or not. Decision:
[ADR 0187](adr/0187-guest-start-and-sign-in-credits.md), part A. Tracking:
[issue #243](https://github.com/Kazo-ca/HackerOs/issues/243).

## For the user

1. **First start.** Under the form that creates the first account, the
   **Start as a Guest** button opens the desktop at once. It is offered only
   while no account exists yet.
2. **Later visits.** While the guest is the only account, the sign-in screen
   shows "Guest" with **Continue as Guest** (no password) and the **Create my
   account** link.
3. **Administrator actions.** The guest is an administrator. When the OS asks
   an administrator to confirm (authentication agent window, sensitive
   capability), the guest clicks **Confirm** without a password.
4. **Create my account.** It opens from three places:
   - the link under **Continue as Guest** on the sign-in screen;
   - the "You are using the guest account" banner of Settings ▸ Users (the
     guest session closes first: save your work);
   - the "Guest · Create my account" reminder under the name in the Start
     menu, which opens Settings ▸ Users.

   The form asks for a login name, a display name, a password (8 characters
   at least) and a choice:
   - **Migrate** (default): the guest becomes your account. Files, settings,
     installed apps and grants are kept; `/home/guest` becomes
     `/home/<login>`.
   - **Delete the guest**: a new empty account is created, then the guest and
     its `/home/guest` folder are deleted. Apps installed for the machine stay.

   The new account is an administrator and its session opens at once. After
   that, the guest button never comes back on this installation.

## How it works

| Part | Where | Role |
| --- | --- | --- |
| `LocalUser.IsGuest` | `Simulation.Abstractions` | Marks the guest (stored as `isGuest` in IndexedDB). The `guest` name is also reserved (`LocalAccountService.IsReserved`). |
| `GuestAccountService` | `Platform.Core/Sessions` | `CanCreateAsync` (no account), `CreateAsync` (`guest` account, `guest` + `administrators` groups, no password), `FindLoneGuestAsync`, `MigrateAsync`, `ReplaceAsync`. |
| `AdminElevationVerifier` | `Platform.Core/Elevation` | Accepts an empty password only for the lone, password-less, enabled guest; `CanConfirmWithoutPasswordAsync` makes the agent windows show **Confirm**. |
| `GuestAccountRequests` | `Platform.Core/Sessions` | An app asks the host for the form (`CreateAccountRequested`); the host owns the form, the app never sees the password. |
| `HackUserAccounts.RequestGuestAccountCreationAsync` | SDK | Used by Settings ▸ Users; accepted only for the guest's own session. `HackUserAccount.IsGuest` marks the guest in the list. |
| `App.razor` | `Ecosystem.Host` | Guest buttons, "Create my account" form, closing the guest session when Settings asks. |
| `AppLauncher` | `Platform.Blazor/Shell` | Reminder in the user tile (`OnCreateAccount`), which opens `--page=users`. |

### Migrate

`MigrateAsync` does the equivalent of `usermod -l NEW -d /home/NEW -m`,
`groupmod -n NEW guest` then `passwd NEW`:

1. checks the form (`HackUserAccounts.ValidateNewAccount`), a reserved or
   taken name, and refuses while the guest's session is open;
2. **copies** `/home/guest` to `/home/<login>` as the new login, keeping each
   entry's mode. The `/home/guest` paths of the text files of `~/.config` and
   of symbolic link targets are rewritten;
3. converts the account: same `LocalUserId`, new login and display name,
   password, guest mark removed;
4. renames the `guest` group, then deletes `/home/guest`;
5. writes `users.guest-migrate` to `auth.log`.

If the copy or the conversion fails, the copy is erased and the guest stays
intact.

### Delete the guest

`ReplaceAsync` creates the new administrator (own group and `administrators`,
password), then deletes the `guest` account, its group and `/home/guest` (as
`userdel -r`), and writes `users.guest-delete`.

### Closing the guest session

From Settings, the host first stops every app of the session
(`AppLifecycleOrchestrator.StopSessionAppsAsync`, which closes their windows),
ends the session, then shows the form on the sign-in screen. The normal
sign-out (session menu) does the same since this work.

## Limits

- The home folder is copied, then the old one deleted: file modification dates
  are those of the copy.
- Only the files of `~/.config` are rewritten; a `/home/guest` path the user
  wrote elsewhere stays as it is.
- The guest had no keyring: the account's keyring is created at its first
  sign-in with the chosen password.
- The position of windows open when the guest session closes is not saved.

## Tests

- `GuestAccountServiceTests`, `GuestConversionTests`, `GuestElevationTests`,
  `GuestAccountRequestsTests` (Platform.Core).
- `AdministratorAuthenticationOverlayTests` (Platform.Blazor), `UsersPanelTests`
  (Settings), `EcosystemHostStateTests` (Ecosystem).
- `AppLifecycleOrchestratorTests.Signing_out_stops_the_window_apps_of_the_session_too`.
