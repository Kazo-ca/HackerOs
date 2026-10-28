> [Version française](../../../../fr/wasm/dev/adr/0179-per-screen-taskbars-and-other-screen-menus.md)

# ADR 0179: one taskbar per screen, "Other screen" menus and identification veil

## Status

Accepted on 2026-10-28. Takes over Benoit's answers of 2026-10-28: one taskbar
per screen with a setting for the primary screen's taskbar, the two right-click
menus with the "Other screen" submenu, and the identification veil (each screen
shows its own number, the hovered screen highlighted). Complements
[ADR 0178](0178-extended-screens-in-display-surfaces.md) (extended screens),
[ADR 0150](0150-os-context-menu.md) (OS context menu) and
[ADR 0164](0164-app-icons-and-window-icon-context-menu.md) (window icon menu).
Plan: [`multi-monitor-plan.md`](../plans/multi-monitor-plan.md).

## Context

- The taskbar (`HackerOs.Taskbar.Blazor.Taskbar`), the Start menu and the
  notification area exist once, in the main tab. `TaskbarWindowSourceAdapter`
  gives it every window of `WindowRuntime`.
- The window menu (ADR 0095 §3, `WindowManagerOverlays`) is a single-level list:
  Restore, Minimize, Move, Resize, Maximize, Snap left/right, Full screen, Close.
  It opens on a right click on the icon (ADR 0164) and with `Alt+Space` /
  `Alt+F3`. The Maximize/Restore button has no menu.
- The OS context menu (ADR 0150) already shows submenus and can be used with the
  keyboard.

## Decision

1. **One taskbar per screen.** Each screen renders its taskbar, with its Start
   button and clock.
   - A secondary screen's taskbar shows the windows of its screen.
   - Screen 1's taskbar follows the `primary-taskbar` setting: `all` (every
     window, the default, as today with one screen) or `own` (only screen 1's
     windows).
   - Clicking the button of a window on another screen activates it on its
     screen, without moving it.
   - The Start menu opens on the screen of the clicked taskbar. An app launched
     from it opens its window on that screen. A window opened another way
     (command, another app) opens on the screen of the active window.
   - The notification area and notifications stay on screen 1.
2. **One overlay layer per screen.** The window menu, context menu, window
   switcher and keyboard move appear on the screen where the user acts. Secure
   dialogs (approval, ADR 0049) stay on screen 1, and a secondary screen then
   shows a veil that points to screen 1.
3. **The window menu goes through the OS context menu** (ADR 0150), to get
   submenus. Its entries, displayed shortcuts and keyboard access do not change.
   On a right click on the icon:
   - Restore, Minimize, Move, Resize, Maximize, Snap left, Snap right, Full
     screen, Close (as today);
   - separator;
   - **Other screen ▸**: Screen 1, Screen 2, …; separator; **New screen**.
4. **New menu on a right click on the Maximize/Restore button**:
   - Snap left, Snap right, Full screen, with the Linux shortcuts shown
     (`Super+Left`, `Super+Right`, …);
   - separator;
   - **Other screen ▸** (the same submenu).

   A left click on this button does not change (ADR 0177).
5. **"Other screen" submenu**:
   - It lists every screen by number. The window's screen is shown but disabled,
     so the numbers stay in place.
   - **New screen** opens the surface during the click (a gesture the browser
     requires), then moves the window into it (ADR 0178 §4). If the browser
     blocks the popup, the surfaces' "window blocked" notification explains how
     to allow it.
   - The submenu also exists with a single screen: "Screen 1" disabled, then
     "New screen". It does not exist on mobile.
6. **Identification veil.** When the pointer hovers "Screen N" (or the keyboard
   focuses it), every **other** screen shows:
   - a black veil at 33% opacity that covers the whole screen;
   - in the center, a black square with **that** screen's number in white;
   - on screen N, the square is highlighted (border in the theme's accent
     color).

   The screen that shows the menu gets no veil, so the menu stays usable. The
   veil disappears when hover or focus leaves the entry, when the menu closes,
   and **as soon as the pointer moves over the veil** (safeguard against a
   stuck veil). It never takes a click.
7. **Settings in a file** ("settings are files" rule):
   `/etc/hackeros/screens.config` (system defaults) and
   `~/.config/hackeros/screens.config` (user):

   ```ini
   [taskbar]
   primary-taskbar=all      ; all | own

   [screens]
   ; screens of the last session, for "Restore screens"
   ; and the remembered position in fallback mode (ADR 0178 §4)
   2=1920,0,1920,1080
   ```

   **Settings ▸ Screens**: list of open screens (number, and monitor name when
   the browser gives it), Identify, New screen, Restore screens and Full screen
   buttons, the state of the `window-management` permission, and the
   `primary-taskbar` choice.

## Consequences

- The taskbar gets a per-screen filter; `HackerOs.Taskbar.Blazor` has no other
  public change.
- The window menu changes component. The menu tests (`WindowKeyboardCommands`,
  `WindowAppIconTests`) must stay green without changing their expectations.
- The OS context menu gets an internal "entry highlighted" signal to trigger the
  veil; no SDK API changes.

## Verification

- Unit tests: taskbar filter (`all` / `own`), content of both menus (current
  screen disabled, New screen), reading `screens.config`.
- UI.E2E: right click on the icon and on Maximize, submenu with the keyboard,
  veil shown on hover then removed when the pointer leaves, when the menu
  closes and when the pointer moves over the veil; two taskbars, each with its
  windows.

## Tasks

- [ ] Per-screen taskbars and the `primary-taskbar` setting (work item 5).
- [ ] Window menu through the OS context menu, Maximize button menu, "Other
  screen" submenu, veil (work item 6).
- [ ] Settings ▸ Screens and `screens.config` (work item 7).
