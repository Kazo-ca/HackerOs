> [Version française](../../../../fr/wasm/dev/adr/0095-window-events-sizes-and-keyboard.md)

# ADR 0095: window events for apps, declared sizes and keyboard commands

## Status

Accepted on 2026-10-17 (work item `v3/1-adr`, for work item `v3/3-window-model`).
§3 follows Benoit's answer of 2026-10-17: Linux keys when the keyboard
is locked, plus a replacement set that is always active.

- **Completes:** [ADR 0009](0009-window-runtime-strategy.md) (window
  manager in C#, "keyboard commands stay in C#", criterion 6 of the proof
  without chosen keys) and [ADR 0006](0006-seal-window-component-lifecycle.md)
  (`OnApp*` hooks of `WindowAppBase`).
- **Plan:** [`wasm-v3-completion-plan.md`](../plans/wasm-v3-completion-plan.md),
  row `v3/3-window-model`; gaps R7 and R8.

## Context

- `WindowRuntime` (`Platform/HackerOs.Windowing.Core/WindowRuntime.cs`) already
  produces the events `WindowFocused`, `WindowResized`, `WindowVisualStateChanged`
  (Normal, Minimized, Maximized), but only the desktop, the taskbar and the
  window surface subscribe to them, to redraw. No app receives them.
- `WindowLaunchCoordinator` (`Platform/HackerOs.Platform.Blazor/Windows/WindowLaunchCoordinator.cs:73-77`)
  opens every window at 800×560 with a minimum of 360×240, no maximum. Neither
  `AppDefinition`, nor `AppManifest`, nor `manifest.schema.v1.json` has a size
  field.
- The desktop only handles Escape (`DesktopShell.razor:356`). There is no
  global shortcut service.
- In a browser tab, the host system captures Alt+Tab, Super and often
  Alt+F4 (which closes the browser on Windows) before the page. Only the Keyboard Lock API
  (`navigator.keyboard.lock()`, in full screen, Chromium) makes it possible to
  receive them.

## Decision

### 1. Window events in `WindowAppBase`

`WindowAppBase` receives new virtual hooks, called by the framework
in the same order as the `WindowRuntime` events, on the window's render
thread:

| Hook | Called when |
|---|---|
| `OnWindowActivated()` / `OnWindowDeactivated()` | the window gains or loses focus |
| `OnWindowStateChanged(HackWindowState previous, HackWindowState current)` | minimize, maximize, restore |
| `OnWindowResized(HackWindowSize size)` | end of a resize (not at every pixel: a single call when the pointer is released or after a keyboard command) |

- `HackWindowState` (`Normal`, `Minimized`, `Maximized`) and `HackWindowSize`
  (width and height of the client area in CSS pixels) are SDK types
  (`HackerOs.AppSdk.Blazor`), never the types of `HackerOs.Windowing.*`
  (ADR 0055).
- Each hook has its `...Async` variant. An exception in a hook is
  logged in `/var/log/apps/<appId>.log` and does not prevent the transition:
  the window manager remains master of the state (ADR 0009).
- A minimized app keeps running, as under a real window manager;
  it is up to the app to slow its work if it wishes, in
  `OnWindowStateChanged`.
- Detached windows (ADR 0078, 0081) call the same hooks from the
  `focus`, `blur` and `resize` events of the browser window that
  carries them.

### 2. App-declared sizes

- `AppDefinition` receives an optional `Window` property of type
  `WindowOptions`:
  `DefaultWidth`, `DefaultHeight`, `MinWidth`, `MinHeight`, `MaxWidth?`,
  `MaxHeight?`, `Resizable` (true by default).
- Default values when nothing is declared: the current values
  (800×560, minimum 360×240, no maximum, resizable). No
  existing app therefore changes appearance.
- The generator writes the `window` object into `manifest.json`; schema v1
  adds it as an optional field (compatible addition). The validator refuses
  `window` on an app that is not of kind `Window`, a negative or zero
  size, a minimum greater than the default or a default greater than the
  maximum (new `manifest.window.*` codes).
- `WindowLaunchCoordinator` reads these values. The initial size is reduced to the
  work area if the screen is smaller; the minimum, however, is respected, and
  the window then overflows as on a small real screen. Maximizing
  ignores the declared maximum (behavior of common Linux window
  managers), unless `Resizable` is false: the Maximize button is then hidden.
- The mobile interface (full screen) ignores these sizes.

### 3. Window manager keyboard commands

Shortcuts are held by a platform C# service
(`WindowKeyboardCommands`), plugged into the desktop's `keydown` handler, and
read from a settings file, as on a real Linux desktop:
`/etc/hackeros/keybindings.config` (machine default) and
`~/.config/hackeros/keybindings.config` (user choice), resolved by
`LayeredConfigResolver` (ADR 0044).

Two sets of keys are provided by default:

| Action | Linux keys (active when the keyboard is locked) | Replacement keys (always active) |
|---|---|---|
| Switch to next / previous window (switcher displayed while Alt is held) | Alt+Tab / Alt+Shift+Tab | Alt+` / Alt+Shift+` |
| Close the window (goes through the close confirmation, ADR 0006) | Alt+F4 | Alt+Q |
| Window menu | Alt+Space | Alt+F3 |
| Move with the keyboard (arrows, Enter confirms, Escape cancels) | Alt+F7 | Alt+F7 |
| Resize with the keyboard | Alt+F8 | Alt+F8 |
| Minimize | Super+Down (from the normal state) | Alt+F9 |
| Maximize / restore | Super+Up / Super+Down | Alt+F10 |
| Snap left / right | Super+Left / Super+Right | Alt+Shift+Left / Alt+Shift+Right |

- The replacement keys take GNOME's where the browser does not
  intercept them.
- The Linux keys are active only when the OS has obtained the keyboard
  lock: in full screen, the platform calls `navigator.keyboard.lock()`
  through a JS module of the windowing library (the only JS exception, the API
  not existing in C#); outside full screen or without the API, they remain with the
  host system.
- All actions are also in the window menu, so that they
  remain reachable without a shortcut (criterion 6 of ADR 0009).
- An app that handles a key itself (for example the Terminal) receives it
  first only for keys that are not window commands;
  window commands always go first, as under X11 with a window manager that has grabbed these keys.

## Consequences

- `v3/3` adds the hooks, `WindowOptions`, the schema's `window` field,
  the shortcut service, the `keybindings.config` file and
  unit tests (hook order, validation) and E2E tests (Alt+` and Alt+Q in a
  tab).
- `v3/6` also touches `AppDefinition`: the second merged work item takes over the
  other beforehand (plan, §4).
- `sdk-reference.md`, `sdk-developer-guide.md`, `app-sdk.md` and the manifest
  schema doc describe the hooks and `Window`.
- `v3/13` tests these keyboard paths with axe and Playwright.
