> [Version française](../../../../fr/wasm/dev/adr/0201-switch-user-simultaneous-sessions.md)

# ADR 0201: switch user, several open sessions

## Status

Accepted on 2026-10-30. Issue [#290](https://github.com/Kazo-ca/HackerOs/issues/290)
(label `TODO`), series `todo/base`, work item `todo/16-switch-user`.

- **Completes:** [ADR 0195](0195-start-menu-power-button.md) §4 ("Switch
  user" greyed out until now), [ADR 0013](0013-local-user-session.md) (local
  session), [ADR 0187](0187-guest-start-and-sign-in-credits.md) (guest
  account), [ADR 0100](0100-sensitive-settings-and-protected-write-audit.md)
  §2 (keyring).
- **Choice made while waiting for Benoit's answer** to the question "How must
  Switch user work?" (simultaneous sessions, close then choose, or keep it
  greyed out), asked in the thread "Move Power into the Start menu" and asked
  again in the work item's thread: the recommendation, simultaneous sessions
  like Windows. If Benoit chooses otherwise, a new ADR will replace this one.

## Context

- The Start menu's Power menu (ADR 0195) showed "Switch user" greyed out: the
  OS had a single session (`ISessionService.CurrentPrincipal`).
- Processes already know which user they run as (`HackUser`,
  `ProcessRecord.UserId`, `IAppExecutionContext.SessionId`): an app does not
  read the interface's session.
- What follows the interface's session: the live settings (theme, language,
  time zone, accessibility, Start menu, through `OsSettingsService`), the
  desktop, the taskbar, the windows shown.
- The interface of an app window lives in the `DesktopShell` component: if
  the desktop is removed from the page, the app loses its interface.

## Decision

### 1. Several open sessions, a single one in the foreground

- `LocalSessionService` keeps the list of open sessions. A single one is in
  the **foreground**: it is the one `CurrentPrincipal` gives. The others are
  in the **background**: their root token is not cancelled, their processes,
  services and windows keep running.
- New interface `IUserSwitchingService` (`HackerOs.Simulation.Abstractions.Sessions`),
  implemented by `LocalSessionService`, so `ISessionService` does not change:
  - `OpenSessions`: every open session (the foreground one included);
  - `BackgroundSessions`: those in the background;
  - `MaxOpenSessions`: the limit (§6);
  - `SwitchAwayAsync()`: the foreground session moves to the background and
    the state returns to `LoggedOut` (the sign-in screen may open);
  - `EndBackgroundSessionAsync(SessionId)`: ends a session in the background,
    like `loginctl terminate-session`.
- `LoginAsync` (password or passkey) of an account that already has a session
  in the background **resumes** it: the password is verified, the home is not
  prepared again, the `SessionId` stays the same. Otherwise a new session
  opens, as before.
- `LogoutAsync` only ends the foreground session; `ShutdownAsync` ends them
  all.
- `CreateLinkedCancellationSource()` links to the foreground session's token,
  as before; the tokens already linked by a session in the background stay
  linked to its own.
- New events: `SessionSwitchedOutEvent` (a session moves to the background)
  and `SessionResumedEvent` (it comes back to the foreground).
  `SessionActivatedEvent` is only for a new session, and
  `SessionLoggedOutEvent` only for an ended session (in the foreground or
  not).

### 2. What follows the foreground session

- `OsSettingsService` and `AppInstallationService` follow both new events:
  the theme, time zone, accessibility and Start menu of the resumed session
  apply at once.
- Language: the switch-user screen speaks the system language, then the
  resumed session gets its own back ([ADR 0104](0104-localization.md)).
- The keyring (ADR 0100 §2) follows the foreground: locked when the session
  moves to the background, unlocked again by the password (or the passkey)
  that resumes it.
- The periodic synchronization ([ADR 0101](0101-synchronized-deletions-and-conflicts.md))
  stops when no session is in the foreground any more and starts again at the
  resume.
- The network drives ([ADR 0181](0181-host-folder-mounted-as-a-network-drive.md))
  of the session moving to the background are unmounted, then mounted again
  when it resumes: the other user does not see them. Ending a session in the
  background does not unmount those of the foreground.

### 3. The windows and the desktop

- The desktop (`DesktopShell`) stays in the page while a session is open, even
  during the switch-user screen (it is then hidden and inert): the apps of the
  sessions in the background keep their interface.
- A window belongs to the session of its app instance
  (`IAppExecutionContext.SessionId`). Only the windows of the foreground
  session are visible; the others stay rendered but hidden (`hidden`), like a
  minimized window. New parameters `DesktopArea.IsWindowShown` and
  `WindowHost.IsSuppressed` in `HackerOs.Windowing.Blazor`.
- The taskbar and the window switcher (Alt+Tab) only list the foreground
  windows.
- The desktop icons, the Start menu and the taskbar are made again at each
  switch of session: they read the folder and the settings of the foreground
  user.

### 4. "Switch user" screen

- "Switch user" of the Power menu is enabled for every account but the guest
  (always greyed out, ADR 0195 §4 and ADR 0187).
- A click moves the session to the background without confirmation, like
  Windows, then opens the sign-in screen. It shows the list of accounts; those
  with an open session carry "open session". Choosing one's own account and
  typing its password resumes its session.

### 5. Sign out and Restart with other open sessions

- The sign-out dialog names the other open sessions.
- **Sign out** only ends the foreground session; the "Also sign out the other
  sessions" box ends the others too (their apps stop through the normal path,
  drafts included).
- **Restart** reloads the page: every session is ended. The dialog says so and
  names the sessions that will be ended.
- After a sign-out, if sessions remain in the background, the sign-in screen
  offers them; the desktop stays in the page, hidden.

### 6. Memory limit

- Everything runs in a single WebAssembly tab: at most **3 open sessions**
  (`LocalSessionService.DefaultMaxOpenSessions`). Opening a fourth session is
  refused ("Too many open sessions"); resuming an open session is still
  possible.

### 7. Accounts

- `userdel` (and Settings ▸ Users) refuses to delete an account that has an
  open session, in the foreground or not ("is currently signed in").

## Consequences

- The apps of a session in the background keep running: their CPU time and
  memory still count (task manager, OOM killer).
- During the switch-user screen, `CurrentPrincipal` is `null`: an interface
  service that reads the session at that time sees nobody. Apps keep their
  user.
- The mobile interface has no Power menu: nothing changes on mobile.
- The window geometry is still not saved at sign-out (known, ADR 0187).
- The hidden desktop keeps its size (`visibility: hidden`): with
  `display: none` the desktop area would measure 0 × 0 and move the windows.
- Tests: `LocalSessionServiceUserSwitchingTests` (core), end-to-end tests
  `StartMenuSwitchUserTests` and `StartMenuPowerTests`. Doc:
  [`user-switching.md`](../user-switching.md).
