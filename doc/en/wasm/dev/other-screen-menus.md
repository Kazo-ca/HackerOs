> [Version française](../../../fr/wasm/dev/other-screen-menus.md)

# "Other screen" menus, identification veil and screen shortcuts

Work item `multi-monitor/6-other-screen-menus` of the [multi-monitor plan](plans/multi-monitor-plan.md),
implementing [ADR 0179 §3-6](adr/0179-per-screen-taskbars-and-other-screen-menus.md) (menus and
veil) and [ADR 0178 §6](adr/0178-extended-screens-in-display-surfaces.md) (shortcuts). It builds
on the per-screen overlay layer ([`secondary-screen-desktop.md`](secondary-screen-desktop.md)) and
on the screen service ([`screen-service.md`](screen-service.md)).

## 1. The two menus

| Opened by | Menu (`data-context-menu`) | Entries |
| --- | --- | --- |
| Right click on the app icon, `Alt+Space`, `Alt+F3` | `window-menu` | Maximize/Restore, Minimize, Move, Resize, Snap left, Snap right, Full screen, Close; separator; **Other screen ▸** |
| Right click on the Maximize/Restore button | `window-maximize-menu` | Snap left, Snap right, Full screen; separator; **Other screen ▸** |

**Other screen ▸**: Screen 1, Screen 2, … (every screen of `WindowRuntime.Displays`, the window's
own screen disabled); separator; **New screen**. The submenu also exists with a single screen.
The left click on Maximize/Restore does not change (ADR 0177). The mobile shell has no such menus.

The shortcuts shown are the active ones (Linux keys under the keyboard lock, fallback keys
otherwise), as before.

## 2. The window menu goes through the OS context menu

`WindowManagerOverlays` no longer draws its own menu (`nav.window-menu` is gone). It keeps
`WindowKeyboardCommands.Menu` as the source of truth and shows it in `ContextMenuService`:

```
WindowChrome ─ right click (icon | Maximize) ─► WindowMenuRequest { Kind }
DesktopShell.HandleWindowMenu ─► WindowKeyboardCommands.OpenMenu(id, x, y, kind)
WindowManagerOverlays (Commands.Changed)
  └─ ContextMenuService.OpenAsync(shell scope, "window-menu" | "window-maximize-menu", …)
       └─ BuildMenu: GetMenuItems(id, kind) + GetScreenMenuItems(id) + New screen
ContextMenuOverlay ─ draws, submenu, keyboard, placement on the screen where the user acts
```

- A shell `ContextMenuScope` (no app context) registers both menus. The entries call
  `ExecuteForWindowAsync`, `MoveToScreen` or `MoveToNewScreenAsync`.
- Opened from the keyboard (`Alt+Space`), the menu highlights its first entry
  (`ContextMenuOrigin.Keyboard`).
- Synchronized both ways: `Commands.Menu` set back to `null` closes the context menu; the context
  menu closed (Esc, outside click, another menu opened) sets `Commands.Menu` back to `null`.
- Entry ids: `window.<keybindings.config key>` (`window.close`…), `window.other-screen`,
  `window.screen.<N>`, `window.new-screen`.

## 3. Changing screen

| API (`WindowKeyboardCommands`) | Role |
| --- | --- |
| `GetMenuItems(id, WindowMenuKind)` | Entries of one of the two menus, without the submenu. |
| `GetScreenMenuItems(id)` | Screens of the submenu, the window's own disabled. |
| `MoveToScreen(id, screen)` | `MoveWindowToDisplayCommand`, then focus unless the window is minimized. |
| `OpenScreen` | Routine given by `DesktopShell`: `ScreenService.OpenScreenAsync()`, the opened screen or `null`. |
| `CanOpenScreen` | `OpenScreen` set and a free number (`ScreensSettingsDocuments.MaxScreenNumber`). |
| `MoveToNewScreenAsync(id)` | Opens the screen during the click (the gesture the browser asks for), then moves the window there. If the browser refuses, the window stays and the surfaces' "window blocked" notice explains. |

**Shortcuts** (`keybindings.config`, ADR 0095):

| Key | `[linux]` | `[fallback]` |
| --- | --- | --- |
| `move-to-previous-screen` | `Super+Shift+Left` | `Ctrl+Alt+Shift+Left` |
| `move-to-next-screen` | `Super+Shift+Right` | `Ctrl+Alt+Shift+Right` |

They move the active window to the screen before or after it **by number**, going round (from
screen 3 back to screen 1). With a single screen nothing happens.

## 4. Identification veil

`ScreenIdentifyVeil` (parameter `Number`) is rendered once per screen: in `DesktopShell` for
screen 1, in each screen of `ScreenLayer` for the others.

- `ContextMenuService` publishes the highlighted entry (`Highlighted`, `HighlightChanged`, internal
  to the OS). `ContextMenuOverlay` updates it on hover (even a disabled entry, so the disabled
  "Screen 1" also shows its screen), when the pointer leaves the entry, and on every key (the
  highlighted entry of the deepest level). Closing the menu sets it back to `null`.
- When the entry is `window.screen.<N>`, each veil shows: 33 % black over the whole screen, a black
  square in the middle with **its own** number in white; on screen N, the square has a border in
  the accent color (`.is-target`).
- **The screen that shows the menu has no veil**: CSS rule
  `:root:has(.screen-overlays) .screen-identify-veil { display: none }`. The overlay container is
  in the document of the screen where the menu is shown (work item 4).
- **Safeguard**: the veil disappears as soon as the pointer passes over it (`pointerenter`,
  `pointermove`, `pointerdown`). It comes back only when another screen entry is hovered. A click
  on it does nothing else.

## 5. Tests

- `Tests/HackerOs.Platform.Blazor.Tests/OtherScreenMenuTests.cs`: Maximize menu entries, kind and
  origin of the open menu, submenu screens (current screen disabled, single screen, French), move
  and focus, shortcuts going round, shortcuts without effect on one screen, shortcut defaults, New
  screen accepted then refused, number read by the veil.
- `Tests/HackerOs.Platform.Blazor.Tests/WindowMenuContextMenuTests.cs`: both menus opened in
  `ContextMenuService` with their submenu, choosing a screen, closing both ways, veil shown on
  each screen with the target marked, then removed on close.
- `Tests/HackerOs.UI.E2E.Tests/ScreenServiceTests.cs`, real Chromium with screen 2 restored: right
  click on Maximize; hovering "Screen 2" → veil on screen 2 only, removed when the pointer passes
  over it and when it leaves the entry; "Screen 2" moves the window; `Ctrl+Alt+Shift+Left` brings
  it back; "New screen" opens screen 3 with the window.
- `Tests/HackerOs.UI.E2E.Tests/WindowAppIconTests.cs`: same expectations as before; the menu is
  now found by `[data-context-menu='window-menu']`.

## 6. Limits

- The menu's full screen applies to the main tab, like the shortcut (work item 4 §6).
- No "go to screen N" shortcut: only previous and next (ADR 0178 §6).
