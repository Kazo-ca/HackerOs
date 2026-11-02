> [Version française](../../../../fr/wasm/dev/adr/0226-forgot-password-factory-reset.md)

# ADR 0226: reset the OS from the sign-in screen (forgotten password)

## Status

Accepted on 2026-11-02 (asked by Benoit, branch
`login/forgot-password-factory-reset`). The three questions below were asked
to Benoit in the conversation; the recommended choices apply until he picks
others.

- **Complements:** [ADR 0056](0056-sdk-apis-for-system-apps.md) §11 (restart
  and factory reset from Settings).
- **See also:** [ADR 0187](0187-guest-start-and-sign-in-credits.md) (sign-in
  screen, guest) and
  [ADR 0120](0120-passwd-and-administrator-password-reset.md) (an
  administrator resets another account's password).

## Context

Benoit (2026-11-02): "Add the possibility to reset the OS on the sign-in page
if the password was forgotten. It erases all the data. This behavior must be
possible to turn off when the application is deployed."

An administrator can already reset another account's password (ADR 0120) and
erase the machine from Settings ▸ System (ADR 0056 §11). But when the only
administrator forgot their password, there is no way back: the site's storage
must be cleared by hand in the browser. On a real computer, one reinstalls
the system.

## Decision

1. **"Forgot password?" link** under the password sign-in form. It is not
   shown for the lone guest (no password) nor on the first-run setup.
2. **Confirmation (question 3, choice A).** The link opens a modal box
   (`role="alertdialog"`) that lists what is lost (accounts with their
   password and passkeys, files, installed apps and their data, settings,
   themes and logs) and says it cannot be undone. The "Erase and restart"
   button stays disabled until the word `ERASE` (`EFFACER` in French) is
   typed; letter case and surrounding spaces do not count. Escape or "Cancel"
   closes the box.
3. **Same erase as Settings.** `SystemPowerService.FactoryResetFromSignInAsync`
   runs the sequence of `FactoryResetAsync`: flush the logs, end what is left
   of a session, delete the OS IndexedDB database, reload the page. The OS
   restarts on the first-run setup. No administrator authorized it: the
   `system.factory-reset` audit entry carries `admin = -`, `requestedBy = -`
   and `origin = sign-in`.
4. **Can be turned off at deployment (question 1, choice A).** The key
   `HackerOs:SignIn:FactoryReset:Enabled` of `wwwroot/appsettings.json`
   (`SignInFactoryResetSettings`), read once at boot like `ResourceLimits`.
   It is changed in the published files without rebuilding; the HackerOS
   server serves the same files. At `false`, the link is gone and
   `SignInFactoryReset.RunAsync` erases nothing even when called.
5. **On by default (question 2, choice A).** A missing or unreadable key reads
   as `true`; the published `appsettings.json` writes it explicitly.

## Questions asked to Benoit

1. How to turn it off at deployment? **A (taken)** a key in
   `appsettings.json`, changed without rebuilding; B an MSBuild property
   compiled into the OS; C both, MSBuild first.
2. Default value? **A (taken)** on; B off.
3. Confirmation? **A (taken)** list of losses and a word to type; B a plain
   "Cancel / Reset" box.

## Rejected options

- **MSBuild property only**: the OS would have to be rebuilt for a deployment
  choice, while the other host settings are in `appsettings.json`.
- **Asking for a proof of identity** (secret question, email): the OS is local
  to the browser, with no online account; anyone at the screen can clear the
  site's storage anyway.

## Consequences

- Anyone at the sign-in screen can erase the machine, as one can reinstall a
  computer. A shared deployment (public computer, demo) sets the key to
  `false`.
- Like the Settings reset, only the OS database is deleted. Web site databases
  (`hackweb.*`) and installed apps' files in Cache Storage stay in the browser,
  unused; passkeys stay in the authenticator.
- Another tab open on the OS closes its database connection
  (`onversionchange`) and stops working until it is reloaded.

## Implementation

- `Platform.Core`: `SystemPowerService.FactoryResetFromSignInAsync`.
- `Ecosystem.Host`: `SignInFactoryResetSettings` (deployment key),
  `SignInFactoryReset` (`IsOffered`, `RunAsync`), component
  `SignInFactoryResetPrompt` (`.razor`, `.razor.cs`, `.razor.css`), wired in
  `App.razor`; `FactoryReset.*` texts in `Strings.resx` and `Strings.fr.resx`.
- Documentation: [`sign-in-factory-reset.md`](../sign-in-factory-reset.md).
