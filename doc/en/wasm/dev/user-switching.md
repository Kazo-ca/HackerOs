> [Version française](../../../fr/wasm/dev/user-switching.md)

# Switch user

"Switch user" of the Start menu's Power button keeps the session open in the
background, with its apps, and opens the sign-in screen for another account.
Decision: [ADR 0201](adr/0201-switch-user-simultaneous-sessions.md), issue
[#290](https://github.com/Kazo-ca/HackerOs/issues/290).

## What the user sees

1. Start menu ▸ Power ▸ **Switch user** (greyed out for the guest). No
   confirmation: the session moves to the background.
2. The sign-in screen opens, in the system language. The account list adds
   "— open session" to the accounts that have one.
3. Another account opens its own session: its desktop, its taskbar, its theme.
   The other session's windows are not visible, neither in the taskbar nor in
   Alt+Tab.
4. Coming back to one's account (password or passkey) **resumes** its session:
   the windows and what was typed in them are still there.
5. **Sign out** names the other open sessions; the "Also sign out the other
   sessions" box ends them too. **Restart** always ends them (the page is
   reloaded).
6. At most 3 open sessions. A fourth one is refused ("Too many open
   sessions"); resuming an open session is still possible.

## Architecture

| Piece | Role |
| --- | --- |
| `IUserSwitchingService` (`HackerOs.Simulation.Abstractions.Sessions`) | `OpenSessions`, `BackgroundSessions`, `MaxOpenSessions`, `SwitchAwayAsync`, `EndBackgroundSessionAsync`. |
| `LocalSessionService` | Implements `ISessionService` and `IUserSwitchingService`. List of open sessions (principal + root token); `CurrentPrincipal` = the foreground session. `LoginAsync` of an account in the background resumes it (`SessionResumedEvent`, audit `session.resume`) instead of opening another one. `HasOpenSession(userId)`, `DefaultMaxOpenSessions` = 3, `MaxOpenSessions` set at construction (`init`). |
| `SessionLimitReachedException` | One session more than the limit. |
| `SessionSwitchedOutEvent`, `SessionResumedEvent` | The foreground changes. `OsSettingsService`, `AppInstallationService` follow; `SyncScheduler` stops or starts again; `NetworkDriveService` unmounts or mounts again the network drives. Encoded for the Web Workers (`WirePayloadCodec`). |
| `SessionWindowFilter` (`HackerOs.Platform.Blazor.Shell`) | A window belongs to the session of its app instance (`IAppExecutionContext.SessionId`); only the foreground ones are shown. |
| `DesktopArea.IsWindowShown`, `WindowHost.IsSuppressed` (`HackerOs.Windowing.Blazor`) | A hidden window stays rendered (`hidden`), like a minimized window: the app keeps its interface. |
| `WindowKeyboardCommands.ListsWindow` | Alt+Tab and the active window only see the foreground. |
| `DesktopShell.ForegroundSession`, `DesktopShell.OnSwitchUser`, `AppLauncher.OnSwitchUser` | The host tells the desktop which session is in the foreground: the desktop icons and the taskbars are made again (`@key`), the other session's menus close. |
| `App.razor` (Ecosystem host) | Keeps the desktop in the page, hidden (`visibility: hidden`, `inert`) under the sign-in screen, while a session waits in the background. Switching user does not boot again: only the accounts and the system language are loaded again. |
| `LogoutDialog` | Names the other sessions, "Also sign out the other sessions" box, always ended by Restart (apps stopped by `StopSessionAppsAsync`, then `EndBackgroundSessionAsync`). |
| `LocalAccountService.HasOpenSession` | `userdel` refuses an account with an open session, even in the background. |

The hidden desktop keeps its size: `display: none` would make the desktop area
measure 0 × 0 and move the windows of the session in the background.

## Limits

- While the sign-in screen shows, `CurrentPrincipal` is `null`: an interface
  service sees nobody. Apps keep their user (`HackUser`).
- The keyring follows the foreground: an app of a session in the background
  that reads the keyring finds it locked.
- The mobile interface has no Power menu.
- One session per account.

## Tests

- `Tests/HackerOs.Platform.Core.Tests/Sessions/LocalSessionServiceUserSwitchingTests.cs`:
  background, resume, wrong password, tokens, ending a session in the
  background, limit, shutdown, `userdel`.
- `Tests/HackerOs.UI.E2E.Tests/StartMenuSwitchUserTests.cs`: resume with a
  window's text; a second account with its own desktop, back to the first one,
  sign-out dialog naming the other session.
- `Tests/HackerOs.UI.E2E.Tests/StartMenuPowerTests.cs`: "Switch user" enabled.

## Tasks

- [x] Several open sessions in `LocalSessionService`, one in the foreground.
- [x] "Switch user" screen: the session stays open, its processes keep running.
- [x] Windows, taskbar and Alt+Tab limited to the foreground session.
- [x] Live settings applied at each switch of session.
- [x] Sign out and Restart: the other open sessions are named and ended on request.
- [x] Limit of 3 open sessions.
- [x] Guest account: always greyed out.
- [x] Docs fr/en and index.
