> [Version française](../../../../fr/wasm/dev/adr/0234-window-styles-inspired-by-winforms.md)

# ADR 0234: window styles inspired by WinForms

## Status

Proposed on 2026-11-07, waiting for Benoit's answers to questions 1 to 6
(thread "Préparer la prochaine version de Kazo OS", see "Questions asked to
Benoit"). Each point of the decision follows the recommendation; a different
answer changes that point before any code.

- **Asked by:** Benoit, 2026-11-07 (points 2 and 5 of his answer): three window
  styles `Normal`, `Tool`, `None` and six boolean options, like
  `FormBorderStyle`, `ShowIcon`, `MaximizeBox`, `MinimizeBox`, `Modal` and
  `ShowInTaskbar` of a WinForms `Form`.
- **Also settles:** [#384](https://github.com/Kazo-ca/Os/issues/384) (an app
  minimizes its own window); [#378](https://github.com/Kazo-ca/Os/issues/378)
  (frameless, transparent windows) if question 4 keeps the recommendation.
- **Completes:** [ADR 0095](0095-window-events-sizes-and-keyboard.md) §2
  (declared sizes, `WindowOptions`) and
  [ADR 0117](0117-sdk-window-title-and-sdk-1-1.md) §2 (`IAppWindowGateway`,
  bound to the app's own window only, no capability).
- **Does not change:** the look of an app that declares nothing (`Normal`
  style, every boolean at its default); detached windows
  ([ADR 0078](0078-detached-display-windows.md)), which stay browser windows.

## Context

- A window app has **one window** per instance. Its options are declared in the
  manifest (`WindowOptions`: sizes, minimum, maximum, `Resizable`,
  `RememberGeometry`) and do not change at run time.
- The title bar (`WindowChrome.razor`) always shows the icon, the title,
  minimize, maximize (when resizable) and close. Every window has its taskbar
  button.
- The window manager can already make a window modal to its owner
  (`WindowModality.OwnerModal`), but only the OS dialogs use it (MessageBox,
  open, save…).
- An app that draws its own buttons (Webamp) can neither minimize its window
  (#384) nor do without the OS frame (#378).

## Decision

### 1. The style: `WindowStyle`

New `WindowStyle` enumeration in `Kazo.Os.App.Abstractions`:

| Style | Title bar | Border and handles | Buttons |
|---|---|---|---|
| `Normal` (default) | yes | yes | per the booleans |
| `Tool` | yes, shorter | no | close only |
| `None` | no | no | none: the app draws its own |

- `Tool` forces `Resizable`, `MaximizeBox` and `MinimizeBox` to false, like
  WinForms `FixedToolWindow`. The OS dialogs use it too (§6).
- `None` shows the client area only. The user neither moves nor resizes it
  through the frame; the app does it through the API (§4). Its background is
  transparent and clicks go through the areas where the app draws nothing (§5,
  question 4).
- A `None` window keeps everything else: taskbar button, preview
  ([ADR 0199](0199-taskbar-window-previews.md)), Alt+Tab, icon menu with
  Alt+Space, animations ([ADR 0198](0198-window-animations.md)), "Other screen"
  ([ADR 0178](0178-extended-screens-in-display-surfaces.md)).

### 2. The boolean options

Added to `WindowOptions`, with the defaults Benoit gave:

| Option | Default | Effect |
|---|---|---|
| `ShowIcon` | `true` | App icon in the title bar (the icon menu stays on right click in the bar and on Alt+Space) |
| `Resizable` | `true` | Already exists: handles, maximize, snapping and keyboard size commands |
| `MaximizeBox` | `true` | Maximize button; false also removes double-click on the bar and full-screen snapping |
| `MinimizeBox` | `true` | Minimize button; false also removes "Minimize" from the icon menu and the keyboard command |
| `ShowInTaskbar` | `true` | Taskbar button; false: the window stays in Alt+Tab and the switcher (question 6) |
| `Modal` | `false` | The window blocks its owner until it closes (§3) |

Combinations follow WinForms: a style that removes a button wins over the
boolean. The manifest writes a field only when it differs from its default, so
existing manifests stay byte-identical (as `rememberGeometry`,
[ADR 0116](0116-window-geometry-restore.md)).

### 3. Child windows and `Modal` (question 3)

`WindowAppBase` gets the equivalent of `Form.Show` and `Form.ShowDialog`:

```csharp
protected ValueTask<IKazoWindow> ShowWindowAsync<TComponent>(KazoWindowRequest request, CancellationToken cancellationToken = default);
protected ValueTask<KazoDialogResult> ShowDialogAsync<TComponent>(KazoWindowRequest request, CancellationToken cancellationToken = default);
```

- `KazoWindowRequest` carries the title, sizes, style, booleans and the
  component parameters. `ShowDialogAsync` forces `Modal = true`.
- The child window belongs to the app instance (same process, same rights) and
  is owned by the window that opens it. It closes with its app. A modal window
  follows its owner from one screen to another (ADR 0178 §2).
- The child component derives from `KazoWindowBase` (new class, without the app
  lifecycle of `WindowAppBase`), which gives `CloseAsync(result)` and the same
  window methods as §4.

### 4. What the app can ask of its window (#384)

`IAppWindowGateway`, `WindowAppBase` and `KazoWindowBase` get, with no
capability since they only reach the app's own window (ADR 0117 §2):

- `MinimizeWindowAsync`, `MaximizeWindowAsync`, `RestoreWindowAsync`;
- `SetWindowStyleAsync(WindowStyle)` and `SetWindowOptionsAsync(...)` to change
  style and booleans at run time (question 2);
- `MoveWindowAsync` and `ResizeWindowAsync`, bounded by the screen's work area
  and the declared sizes;
- the `data-kazo-window-drag` attribute on an app element: pressing it moves
  the window, like its title bar (for `None`).

A detached window (ADR 0078) is a browser window: these requests do nothing
there and return false. The booleans set the buttons shown to the user, not
what the app may do: an app with `MinimizeBox = false` can still minimize its
window, as `WindowState` in WinForms.

### 5. `None` transparency (question 4)

- The surface of a `None` window has no background and no shadow; the client
  area gets `pointer-events: none`, and every element the app draws takes the
  mouse back (`pointer-events: auto`, applied to direct children and to any
  element marked `data-kazo-window-hit`).
- A click on an empty area reaches what is below (desktop or another window),
  like a Windows layered window.
- This point is a separate work item: if it gets stuck, the release ships with
  an opaque `None` and #378 stays open.

### 6. OS dialogs (question 5)

MessageBox, open, save, select folder, text input, app chooser and "not
responding" switch to the `Tool` style with `ShowInTaskbar = false` (they are
already modal to their owner). On mobile they stay full screen
([ADR 0171](0171-mobile-file-explorer-and-full-screen-dialogs.md)).

### 7. SDK and version (question 1)

All API goes into `PublicAPI.Unshipped.txt`; the version is **3.1.0** (minor:
non-breaking addition). The `Tests/fixtures/sdk-compat` fixture must keep
compiling unchanged.

## Consequences

- An existing app does not change. An app can have child windows and its own
  dialogs, with the same frame as the OS.
- Webamp can minimize its window (#384) and then float on the desktop (#378).
- The taskbar, Alt+Tab, previews and the icon menu read the new options; the
  keyboard commands (ADR 0095 §3) respect `MinimizeBox`, `MaximizeBox` and
  `Resizable`.

## Work items (series `os-3-1/base`)

1. `os-3-1/1-adr-window-styles`: this ADR.
2. `os-3-1/2-file-dialog-home`: #385 (PR #389).
3. `os-3-1/3-capabilities-on-install`: #386 (PR #391).
4. `os-3-1/4-window-style-options`: §1, §2, §4 (#384) and §6.
5. `os-3-1/5-child-windows`: §3.
6. `os-3-1/6-transparent-none`: §5 (#378).
7. `os-3-1/7-release-3-1-0`: `Versions.props`, `collect-changes.sh`,
   `Shipped`, `features.md`; then the `os-3-1/base` to `main` PR, which Benoit
   merges when he decides.

## Questions asked to Benoit

Asked on 2026-11-07 in the thread, each with its recommendation (⭐).

1. Version: A ⭐ 3.1.0; B 3.0.1.
2. Where style and booleans are set: A ⭐ manifest and at run time; B manifest
   only.
3. `Modal`: A ⭐ child windows `ShowWindowAsync`/`ShowDialogAsync`; B modal when
   an app launches another; C no modal for apps (`TODO` issue).
4. `None`: A ⭐ transparent, clicks through, closes #378; B opaque only.
5. OS dialogs as `Tool`: A ⭐ all; B MessageBox and text input only; C none.
6. `ShowInTaskbar = false` and minimized: A ⭐ stays in Alt+Tab and the
   switcher; B no minimize button.
