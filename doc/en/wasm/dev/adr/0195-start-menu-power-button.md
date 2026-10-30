> [Version française](../../../../fr/wasm/dev/adr/0195-start-menu-power-button.md)

# ADR 0195: Power button of the Start menu

## Status

Accepted on 2026-10-30. Benoit's request of 2026-10-30 (thread "Déplacer Power
dans le menu Démarrer"). Tracking issue for "Switch user":
[#290](https://github.com/Kazo-ca/HackerOs/issues/290).

- **Complements:** [ADR 0187](0187-guest-start-and-sign-in-credits.md) (guest
  account and how the Start menu shows it),
  [ADR 0162](0162-restart-after-a-host-build-change.md) (restart after an
  update), [ADR 0099](0099-os-backup-and-low-storage.md) §3 (the service
  worker's `RESET_CACHE`).
- **Does not change:** the administrator reboot of ADR 0056 §11 (`HackSystem`,
  `system.power` capability).

## Context

- The taskbar had a "Power" button right of the clock. It opened the "Sign out
  of HackerOS" dialog.
- The Start menu already shows, for every user, their name and login at the top
  right; the guest account adds "Create my account" (ADR 0187 §5).
- Benoit wants the Power button in the Start menu, with three choices: Sign out,
  Switch user (greyed out for the guest) and Restart. Restart has the same effect
  as Sign out, followed by a full reload ("hard refresh").
- The OS has one session at a time (`ISessionService.CurrentPrincipal`).

## Decision

### 1. The button in the Start menu

- The Start menu's right column ("Quick access") ends with the **Power** button
  (`power` icon). It opens a menu (`role="menu"`) above it: **Sign out**,
  **Switch user**, **Restart**.
- Keyboard: the first choice takes the focus; Up, Down, Home and End move between
  the enabled choices; Escape closes the Power menu first (the focus goes back to
  the button), then the Start menu.
- On a small screen the button stays visible, across the whole column.
- The HackerOS taskbar no longer has a session button. The
  `HackerOs.Taskbar.Blazor` package keeps `ITaskbarSessionCommands` for other
  hosts.
- The user stays shown at the top of the Start menu, as before, for every
  account.

### 2. Sign out

Opens the existing sign-out dialog (process list, confirmation). Confirming stops
the session's apps, ends the session and opens the sign-in screen.

### 3. Restart

- Same dialog, titled "Restart HackerOS", saying the system will be reloaded from
  the server. Confirming ends the session like a sign-out.
- Then the browser host shows the boot screen, empties the caches holding the copy
  of the OS (`hostRestart.js`), then reloads the page with
  `SystemPowerService.RestartAfterAsync` (logs flushed), as after an update
  (ADR 0162):
  - with the PWA service worker it sends `RESET_CACHE` (ADR 0099 §3): the release
    is downloaded again; offline or with the server unreachable, the cache is kept
    and the OS restarts anyway;
  - without a service worker (local development) it deletes .NET's
    `dotnet-resources*` caches.
- No administrator approval: nothing is erased, only the user's session ends.
- The OS database (IndexedDB) is never touched.

### 4. Switch user

- Shown but **greyed out** for now, with "Coming soon".
- Always greyed out for the guest ("Not available in a guest session"): the guest
  only exists while no other account exists (ADR 0187), and goes through "Create
  my account".
- Doing it needs several sessions open at once, which the OS cannot do:
  `ISessionService`, windows, processes, the app lifecycle and the live
  preferences all assume a single session. The work is listed in issue
  [#290](https://github.com/Kazo-ca/HackerOs/issues/290) (label `TODO`).

### 5. Themes

New Start menu tokens in the token catalog (theme editor):
`--hos-launcher-power-background`, `--hos-launcher-power-color`,
`--hos-launcher-power-hover-background`. `themes.css` also adds
`--hos-launcher-power-hover-color`, `-border`, `-radius` and `-shadow`. Every
built-in theme sets them: raised Windows 98 button, orange Windows XP "Turn off"
button, glassy Windows 7 button, flat Windows 10 button, subtle red on macOS and
iOS, Ubuntu orange, Android pill, high-contrast black and white.

## Consequences

- One more click to sign out, but the taskbar has more room for windows, as on
  Windows.
- The mobile interface still has no sign-out: outside this ADR.
- The sign-out dialog shows on screen 1, even when the Start menu was open on
  another screen.
- End-to-end test: `StartMenuPowerTests`.
