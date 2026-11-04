> [Version française](../../../fr/wasm/dev/sign-in-factory-reset.md)

# Forgot password: reset from the sign-in screen

## Purpose

When nobody knows an administrator password any more, the sign-in screen
offers to erase Kazo OS in this browser and start over at the first-run
setup. Decision: [ADR 0226](adr/0226-forgot-password-factory-reset.md).

## For the user

1. On the sign-in screen, under **Start session**, click **Forgot password?**.
2. The "Reset this machine?" box lists what will be deleted: every account
   (password and passkeys), every file, the installed apps and their data,
   every setting, theme and log.
3. Type `ERASE` (`EFFACER` in French) then **Erase and restart**. **Cancel**
   or Escape closes the box without erasing anything.
4. The boot screen shows, the page reloads and Kazo OS starts again on the
   creation of the first account (or **Start as guest**).

The link is not shown while the guest is the only account: it has no password
to forget.

## For whoever deploys it

The link is on by default. To remove it (shared computer, public demo), set
the key to `false` in `wwwroot/appsettings.json` of the published files,
without rebuilding:

```json
{
  "HackerOs": {
    "SignIn": {
      "FactoryReset": { "Enabled": false }
    }
  }
}
```

The Kazo OS server (`HackerOs.Server`) serves the OS's published files as
they are: the same key applies. A missing or unreadable value reads as
`true`. An administrator can still erase the machine from Settings ▸ System.

The service worker caches `appsettings.json` without checking its hash: the
edited file does not stop the offline install. A browser that already
installed this release keeps the previous file until the next release; for a
change to apply everywhere, make it before the first deployment (or in
`OS/HackerOs.Ecosystem/wwwroot/appsettings.json` before `dotnet publish`).

## Architecture

| Element | Role |
| --- | --- |
| `SignInFactoryResetSettings` (`Ecosystem.Host`) | Reads `HackerOs:SignIn:FactoryReset:Enabled` once at boot; registered by `AddHackerOsBrowserHost`. |
| `SignInFactoryReset` (`Ecosystem.Host`) | `IsOffered` (deployment key, `true` when the host registered nothing) and `RunAsync`, which checks the key again then calls the power service. |
| `SignInFactoryResetPrompt` (`.razor`, `.razor.cs`, `.razor.css`) | Link and confirmation box; `IsConfirmationTyped` compares the typed word ignoring case and surrounding spaces. |
| `SystemPowerService.FactoryResetFromSignInAsync` (`Platform.Core`) | Same sequence as `FactoryResetAsync` (logs flushed, session ended, IndexedDB database deleted, page reloaded); `system.factory-reset` audit with `origin = sign-in`. |
| `hostFactoryReset.js` (`Ecosystem.Host`, ADR 0227) | `eraseEverything`: erases every IndexedDB database, Cache Storage, `localStorage`, `sessionStorage`, OPFS, cookies and unregisters the service worker; called by `BrowserSystemPowerDevice`. |
| `App.razor` | Shows the link under the password form; on confirmation, shows the boot screen and runs `SignInFactoryReset.RunAsync`. |

## Limits

- Since [ADR 0227](adr/0227-factory-reset-erases-everything.md), nothing of
  the OS stays in the browser (every IndexedDB database, Cache Storage,
  `localStorage`, `sessionStorage`, OPFS, cookies, service worker), here as in
  Settings. Offline, the OS can only restart once the network is back.
- Registered passkeys stay in the device's authenticator; the OS no longer
  knows them.
- Another tab open on the OS loses its data and must be reloaded.

## Tests

- `Tests/HackerOs.Ecosystem.Tests/SignInFactoryResetTests.cs`: reading the
  key, the published `appsettings.json`, the confirmation word, a disabled
  deployment that erases nothing, closed and open rendering in English and
  French.
- `Tests/HackerOs.Platform.Core.Tests/Administration/SystemAdministrationGatewayTests.cs`:
  erase then restart, `origin = sign-in` audit entry.

## Task list

- [x] ADR 0226 and questions asked to Benoit.
- [x] Deployment key `HackerOs:SignIn:FactoryReset:Enabled`.
- [x] `SystemPowerService.FactoryResetFromSignInAsync` and its audit.
- [x] Link and confirmation box on the sign-in screen (fr, en).
- [x] Unit and rendering tests.
- [x] Documentation, `features.md`, change entry.
