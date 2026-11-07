> [Version française](../../../fr/wasm/framework/window-model.md)

# Window model: events, declared sizes and keyboard

Work item `v3/3-window-model` of the [wasm v3 migration completion plan](../dev/plans/wasm-v3-completion-plan.md),
implementation of [ADR 0095](../dev/adr/0095-window-events-sizes-and-keyboard.md).

## Purpose

- A windowed app learns what happens to its window: it gains or loses focus, it is
  minimized, maximized or restored, it changes size.
- A windowed app declares its window size (initial, minimum, maximum, resizable)
  in its `AppDefinition` class instead of the hard-coded 800×560 / 360×240.
- The window manager is driven from the keyboard like a Linux desktop: switch window,
  close, window menu, move and resize with the keyboard, minimize, maximize, snap to
  the left or right, full screen.
- A windowed app writes its window title (`document — App`), displayed by the window
  manager (ADR 0117, §4).

## 1. Window events (ADR 0095 §1)

### For the app

`WindowAppBase` (Blazor SDK) exposes:

| Member | Role |
| --- | --- |
| `OnWindowActivated[Async]()` | The window receives focus. |
| `OnWindowDeactivated[Async]()` | The window loses focus (another window, minimizing, no window left in the foreground). |
| `OnWindowStateChanged[Async](previous, current)` | `KazoWindowState`: `Normal`, `Minimized`, `Maximized`. |
| `OnWindowResized[Async](KazoWindowSize size)` | Size of the client area (without the title bar), in CSS pixels, **only once at the end** of a resize (mouse gesture, keyboard, maximizing, restoring, change of the work area). |
| `IsWindowActive`, `WindowState`, `WindowSize` | Last known state, to be read in rendering. |
| `TrackDisplaySurface(IDisplaySurface)` | Makes a detached window followed (ADR 0078/0081): its focus, loss of focus and size trigger the same hooks. `KazoWindowSize.DisplaySurfaceId` then indicates the surface. The result is released when the surface is reattached. |

Guaranteed order:

- a window's hooks go through a queue of its own, one at a time, in the order of the facts;
- a state change precedes the resize it causes (maximizing gives
  `OnWindowStateChanged(Normal, Maximized)` then `OnWindowResized`);
- when the app is rendered in a window that is already maximized or already in the foreground, it receives
  `OnWindowStateChanged(Normal, current state)` then `OnWindowActivated`;
- the synchronous version is called before the `Async` version, on the component's dispatcher, in
  the app's ambient context (the `Kazo*` facades work); a new render follows.

A hook that throws an exception is written to the app's log
(`/var/log/apps/<appId>.log`, level `Error`, prefix `window:`); the window's transition
has already happened and continues.

```razor
@inherits WindowAppBase

<p class="@(IsWindowActive ? "active" : "idle")">@WindowState</p>

@code {
    protected override Task OnWindowDeactivatedAsync() => PauseAnimationAsync();
    protected override void OnWindowResized(KazoWindowSize size) => _columns = (int)(size.Width / 120);
}
```

### Architecture

- `WindowRuntime` (Windowing.Core) already produces facts (`WindowFocusedEvent`,
  `WindowVisualStateChangedEvent`, `WindowViewportChangedEvent`, `WindowClosedEvent`). It
  now publishes each batch through the `EventsApplied` event, before `StateChanged`.
- Screens: a window has a `DisplayId` and each screen its own work area; changing screen
  calls `OnWindowResized` if the size changes (ADR 0178, [`window-runtime-displays.md`](../dev/window-runtime-displays.md)). The window
  is not recreated: its element moves into its screen's layer ([`window-portal.md`](../dev/window-portal.md)).
- New fact: `CompleteWindowResizeCommand` → `WindowResizeCompletedEvent(id, bounds)`. The
  chrome (`WindowChrome.razor.js`) sends it when a resize gesture ends; the
  keyboard sends it when a resize is confirmed.
- `WindowAppEventDispatcher` (Platform.Blazor, one per session) translates these facts into calls on
  `IWindowAppEventTarget` (SDK interface, implemented by `WindowAppBase`). `WindowAppRenderer`
  registers the app's component there. The size is measured in the browser
  (`IWindowClientAreaProbe`, `windowClientArea.js`, two frames after rendering); failing that, it
  is estimated from the window size minus the chrome.
- `DesktopShell` and `MobileShell` pass the dispatcher to `WindowAppRenderer`. On mobile,
  windows are full screen: the activation and state hooks remain valid.

## 2. Declared sizes (ADR 0095 §2)

### Pointer gestures (ADR 0163)

The `data-window-x/y/width/height` attributes use the invariant culture,
even when the desktop is in French. Moving gives focus at the start,
projects a local CSS translation during the gesture and sends a single final delta
to C# on release. The runtime then applies its constraints; the position
is never saved to disk (ADR 0122). Resizing keeps the C# constraints live,
with one sample in progress and the most recent one pending
instead of a queue per movement. A lost or canceled capture ends the gesture
on the last valid delta; removing the component releases the listeners and the capture.

This temporary projection amends ADR 0009 without moving the runtime state
into JavaScript: see [ADR 0163](../dev/adr/0163-window-pointer-preview-and-invariant-coordinates.md).

On release, the preview targets an absolute destination and its translation subtracts
the frame's current CSS position. Thus, a render of the new position before
the interop returns cannot add the move twice.
`ReportGestureCompletedAsync("move")`, called after the command returns,
projects the current C# position (constraints included) and removes the preview in
a single JavaScript task (`completeWindowMove`). This also covers an interop
return that precedes the `WindowHost.OnAfterRenderAsync` projection, with no frame at the
old position. No additional movement or disk write is emitted.

### Declaration by the app

```csharp
public override WindowOptions? Window => new()
{
    DefaultWidth = 720, DefaultHeight = 520,
    MinWidth = 480, MinHeight = 360,
    MaxWidth = null, MaxHeight = null,   // pas de limite
    Resizable = true,
};
```

- `WindowOptions` (App.Abstractions): outer sizes (title bar included) in CSS
  pixels. Without a declaration: `WindowOptions.Default`, 800×560 and minimum 360×240, as before.
- Manifest: optional `window` object (`defaultWidth`, `defaultHeight`, `minWidth`, `minHeight`,
  `maxWidth`, `maxHeight`, `resizable`, and since SDK 3.1 `style`, `showIcon`, `maximizeBox`,
  `minimizeBox`, `showInTaskbar`, `modal`, see §6), produced by the generator and absent if the app declares
  nothing. An omitted field takes its default value. The `manifest.schema.v1.json` schema forbids
  `window` outside apps of the `window` kind.
- Validator codes:

| Code | Rule |
| --- | --- |
| `manifest.window.forbidden` | `window` on an app that is not of the `window` kind. |
| `manifest.window.size.invalid` | A zero or negative size. |
| `manifest.window.minimum.exceedsDefault` | Minimum larger than the initial size. |
| `manifest.window.default.exceedsMaximum` | Initial size larger than the maximum. |

- Opening (`WindowLaunchCoordinator.InitialBounds`): initial size reduced to the work
  area, but never below the minimum; a window whose minimum exceeds the screen overflows,
  as on a real small screen (`WindowRuntime.Constrain` keeps the minimum).
- Maximizing ignores the maximum. `Resizable = false`: no handles, no Maximize button, and
  the keyboard commands resize, maximize and snap do nothing.
- Remembered size (ADR 0116 and 0122, [`window-geometry.md`](../dev/window-geometry.md)): the size and
  maximized state of each app window, not its position, are written to
  `~/.local/state/kazoos/windows/<appId>.config` and reread by
  `WindowLaunchCoordinator.PresentAsync`, which corrects the size for the screen and the current
  declared sizes; the window always opens at its cascade position (`InitialBounds`).
  `WindowOptions.RememberGeometry = false` refuses.
- The mobile shell ignores these sizes.

## 3. Keyboard commands (ADR 0095 §3)

### `keybindings.config` file

Read by `WindowKeyboardCommands` with the usual layers
([`settings-files.md`](settings-files.md)): `/etc/kazoos/keybindings.config` (written at
first startup, all commented out), then `~/.config/kazoos/keybindings.config`. A
change to the file applies immediately. Several combinations separated by commas;
an empty value removes the key.

| Action (key) | `[linux]` (under keyboard lock) | `[fallback]` (always) |
| --- | --- | --- |
| `switch-next` | `Alt+Tab` | `` Alt+` `` (`Alt+Backquote`) |
| `switch-previous` | `Alt+Shift+Tab` | `Alt+Shift+Backquote` |
| `close` | `Alt+F4` | `Alt+Q` |
| `window-menu` | `Alt+Space` | `Alt+F3` |
| `move` | `Alt+F7` | `Alt+F7` |
| `resize` | `Alt+F8` | `Alt+F8` |
| `minimize` | | `Alt+F9` |
| `maximize` | `Super+Up` | |
| `restore-or-minimize` | `Super+Down` | |
| `toggle-maximize` | | `Alt+F10` |
| `snap-left` / `snap-right` | `Super+Left` / `Super+Right` | `Alt+Shift+Left` / `Alt+Shift+Right` |
| `toggle-fullscreen` | | `Alt+F11` |
| `move-to-previous-screen` / `move-to-next-screen` | `Super+Shift+Left` / `Super+Shift+Right` | `Ctrl+Alt+Shift+Left` / `Ctrl+Alt+Shift+Right` |

The Linux keys are only active if the keyboard is locked (`navigator.keyboard.lock()`,
called in full screen): otherwise, the host system intercepts them. The replacement keys
work in an ordinary tab. A window manager combination takes precedence over the app
that has focus. A letter is recognized by its physical key when Alt changes the character
(Option+Q on macOS).

### Behavior

- **Switching windows**: as long as Alt is held, a switcher lists the windows (non-minimized
  first, from most recent to oldest) and the selection advances on each press; releasing
  Alt switches to the chosen window (restored if it was minimized). Esc cancels.
- **Move / resize**: a banner indicates the mode; arrows move or
  resize by 10 px (1 px with Ctrl), Enter keeps, Esc puts the window back where it was.
- **Snap**: left or right half of the work area. The window keeps its earlier geometry
  (ADR 0177): `restore-or-minimize`, or dragging its title bar, gives it back; Maximize maximizes
  it and Restore then puts it back to its size from before the snap.
- **Title bar** (ADR 0177): a double click outside the buttons toggles maximized ↔ normal;
  dragging the title bar of a maximized or snapped window gives it back its earlier size, under
  the pointer. A maximized window has no resize handles, and the buttons always sit above the
  handles.
- **Window menu** (ADR 0164): `Alt+Space`/`Alt+F3` or right click on the app
  icon in the title bar. A left click on the icon only activates the
  window; the rest of the title bar does not open the menu. It contains all actions with their active shortcut:
  Maximize/Restore, Minimize, Move, Resize, Snap left/right, Full screen,
  Close, then **Other screen ▸** (Screen 1, Screen 2…, New screen). It opens in the OS context
  menu (ADR 0179 §3): arrows, Enter, Esc and the submenu work from the keyboard.
- **Maximize/Restore button menu** (ADR 0179 §4): right click on the button. Snap left, Snap
  right, Full screen, then Other screen ▸. The left click does not change.
- **Change screen** (ADR 0178 §6): `move-to-previous-screen` / `move-to-next-screen` send the
  active window to the screen before or after it, by number, going round. Details in
  [`../dev/other-screen-menus.md`](../dev/other-screen-menus.md).
- **Close** follows the path of the Close button (`IWindowCloseGuard` included).
- **Keyboard focus**: the window that becomes the active window (switcher, closing the one
  on top, taskbar) also receives keyboard focus: the last control that had it in
  that window, otherwise its first focusable element, otherwise the window itself. Nothing moves
  if focus is already there (click, app that focuses itself). `WindowHost` does it on render
  (`WindowHost.razor.js`, work item `v3/13`).

### Architecture

- `WindowKeyBindingsSettingsDocuments`, `KeyChord`, `WindowKeyboardAction` (Platform.Core):
  document schema, combination parsing, default content.
- `WindowKeyboardCommands` (Platform.Blazor, one per session): all decisions (modes,
  switcher, menu, moves) in C#, tested without a browser.
- `WindowManagerOverlays` (`.razor`, `.razor.cs`, `.razor.css`, `.razor.js`): draws the
  switcher, the banner and the menu. Its JS listens to `keydown` in the capture phase, recognizes the
  combinations sent by C#, calls C# and blocks the key; it handles full screen and
  keyboard locking.

## 4. Window title (ADR 0117, ADR 0123)

### Application icon (ADR 0164)

The first image declared in `AppDefinition.Icons` becomes the icon of the
window and of its taskbar entry. It must also appear in `Assets` and exist under
`wwwroot/` in the app's project. `WindowLaunchCoordinator` turns its
package-relative path into a `_content/<assembly>/<path>` URL (segments escaped),
shared by the chrome, the taskbar and the switcher. The fourteen windowed system
apps each provide their `icons/app.svg`; the build computes the asset's
fingerprint. Without a declaration, a window symbol serves as a fallback.

All desktop themes display the title icon except macOS, which hides
the icon and its context button with `--kos-window-icon-display: none`.
The macOS dock keeps the app icons. The menu remains accessible from the keyboard.
The Lucide choice of the launcher and shortcuts remains independent of the chrome images.
See [ADR 0164](../dev/adr/0164-app-icons-and-window-icon-context-menu.md).

Work item `v3.1/3-window-title` of the [v3.1 series](../dev/plans/v3-1-series-plan.md).

### For the app

The app writes its **whole** title, as an X11 client writes `_NET_WM_NAME`; the window manager
displays it as is in the title bar, the taskbar, the `` Alt+` `` switcher,
the mobile desktop's recents and the frame's accessible name (`aria-labelledby`).

| `WindowAppBase` member | Role |
| --- | --- |
| `WindowTitle` | Displayed title; the app's name in the desktop language as long as the app has set nothing. |
| `SetWindowTitleAsync(string? title, CancellationToken)` | Replaces the title of **this** window. `null`, empty or blank gives back the app name. No capability. |
| `FormatWindowTitle(string? document, bool modified = false)` | Composes the base apps' convention: `document — App`, `*` in front if `modified`, the name alone without a document. |

```razor
@inherits WindowAppBase

@code {
    protected override Task OnAppInitializedAsync() => SetWindowTitleAsync(FormatWindowTitle(_name, _dirty)).AsTask();

    // Ouvrir, modifier, enregistrer finissent par un rendu : le titre suit ici. Redemander le même
    // titre ne fait rien.
    protected override Task OnAppAfterRenderAsync(bool firstRender) =>
        SetWindowTitleAsync(FormatWindowTitle(_name, _dirty)).AsTask();
}
```

- The title is cleaned: control characters removed, leading and trailing spaces trimmed, at most
  256 characters (cut with `…`, never in the middle of a surrogate pair).
- Set in `OnAppInitialized[Async]`, it is displayed with the window, without flicker.
- Detached windows followed by `TrackDisplaySurface` take the same title once the app
  has set one.
- Sensitive OS windows (authentication agent, dangerous capability approval)
  are drawn by the OS, outside app windows, and never take an app title; the taskbar
  shows the owning app's icon next to the title.
- An app without an interface (`Terminal`, `Service`) does not have this API.

### Architecture

- `WindowRuntime.SetTitle(windowId, title)` (Windowing.Core) applies `SetWindowTitleCommand`,
  cleans the title with `WindowTitles.Sanitize` (Windowing.Abstractions) and publishes
  `WindowTitleChangedEvent`; an unchanged title produces nothing. Surfaces reread
  `WindowRuntimeState.Title` on each `StateChanged`.
- `AppWindowGatewayFactory` (Platform.Blazor, registered by `AddKazoOsEcosystem`) creates one
  `IAppWindowGateway` per window, bound to its `WindowId`. `WindowAppRenderer` receives it
  (`WindowGateways`, passed by `DesktopShell` and `MobileShell`) and gives it to the app through a fixed
  `CascadingValue`: the app can only reach its own window.
- The default title comes from `WindowLaunchCoordinator.DefaultTitle(manifest)` (translated name).

### Base apps

| App | Title |
| --- | --- |
| Kazo Paint | `photo.png — Kazo Paint`, `*` if the image is modified; the name leaves the toolbar. |
| Text Editor | `*notes.txt — Text Editor`; the app's banner keeps its display. |
| Code Editor | Active tab, then open folder: `*Program.cs — src — Code Editor`. |
| Theme Editor | `*Name — Theme Editor` (also in its toolbar). |
| File Explorer | `Documents — File Explorer`, `/` at the root, the trash when it is displayed. |

## 5. Window that does not respond (ADR 0127 §2-3)

- `WindowRuntimeState.IsResponding` (true by default) is changed by
  `SetWindowRespondingCommand`; `WindowRuntime` publishes
  `WindowRespondingChangedEvent` only when the value changes.
- `WindowChrome` adds "(Not responding)" / "(Ne répond pas)" to the displayed
  title (the app's title does not change); `WindowHost` greys out the content
  (`is-not-responding`, `aria-busy`) and draws its `Overlay` parameter over
  the content. `DesktopArea` and `SingleSurfaceArea` fill it with
  `WindowOverlay` (a `RenderFragment<WindowRuntimeState>`).
- The `WindowResponsivenessMonitor` monitor, the box and the `window-manager.config`
  setting are described in [`process-priority-and-unresponsive-windows.md`](../dev/process-priority-and-unresponsive-windows.md).

## 6. WinForms-style window styles and options (ADR 0234)

### For the app

```csharp
public override WindowOptions? Window => new()
{
    DefaultWidth = 400, DefaultHeight = 300,
    Resizable = false,
    Style = WindowStyle.Tool,     // Normal (default), Tool, None
    ShowIcon = true,              // icon in the title bar
    MaximizeBox = true,           // Maximize button
    MinimizeBox = true,           // Minimize button
    ShowInTaskbar = true,         // taskbar button
    Modal = false,                // child windows (work item 5)
};
```

| Style | Title bar | Border and handles | Buttons |
| --- | --- | --- | --- |
| `Normal` | yes | yes | per the booleans |
| `Tool` | yes, shorter (`--kos-tool-titlebar-height`) | no | Close only |
| `None` | no | no | none: the app draws its own |

- Manifest: `style` (`"normal"`, `"tool"`, `"none"`) and the booleans are written only when they
  differ from their default; an existing manifest stays byte-identical.
- `ShowInTaskbar = false` removes the taskbar button **and** the Minimize button: the user can no
  longer minimize the window (Benoit's answer B to question 6). It stays in Alt+` and the switcher.
- `MaximizeBox = false` also removes double-click on the bar; `MinimizeBox = false` also removes
  "Minimize" from the window menu and the keyboard command. `Tool` and `None` remove the handles,
  Maximize and Minimize; the window menu stays on right click on the bar and on Alt+Space.
- Without an icon (`ShowIcon = false` or `Tool`), right click on the title opens the window menu.
- A `None` window moves through any element marked `data-kazo-window-drag` (fields, links and
  editable elements inside it stay clickable).

What the app asks of **its own** window, with no capability (#384):

| `WindowAppBase` member | Effect |
| --- | --- |
| `WindowOptions` | Current options (the manifest until the app changes them). |
| `MinimizeWindowAsync`, `MaximizeWindowAsync`, `RestoreWindowAsync` | Window state; a fixed-size window is never maximized. |
| `MoveWindowAsync(x, y)` | Top-left corner, relative to its screen's work area; the window stays on the screen. |
| `ResizeWindowAsync(w, h)` | Outer size, bounded by the declared sizes and the screen; needs a resizable window in its normal state. |
| `SetWindowStyleAsync(style)`, `SetWindowOptionsAsync(options)` | Style, booleans and sizes at run time (start from `WindowOptions with { … }`). |

Each method returns `true` when the request is applied, `false` otherwise (closed window,
detached window, request refused by the window's state, platform older than SDK 3.1). The
booleans set what the user sees, not what the app may do: an app with `MinimizeBox = false` can
still minimize its window.

### Architecture

- `WindowFrame` (Windowing.Abstractions): style (`WindowFrameStyle`) and booleans in
  `WindowRuntimeState.Frame`, with `UserCanMinimize`, `UserCanMaximize(constraints)`,
  `UserCanResize(constraints)`. The chrome, keyboard commands, window arrangement and taskbar read
  these rules; `WindowRuntime` only limits what the constraints forbid, so the app keeps its
  requests.
- `SetWindowFrameCommand` and `WindowFrameChangedEvent`: change the frame and constraints; a
  maximized window that becomes fixed-size is restored.
- `WindowLaunchCoordinator.ToFrame(options)` maps the manifest; `AppWindowGatewayFactory` applies
  the app's requests through the runtime commands.
- `WindowRuntime.ActivationTarget(id)`: the topmost modal window of an owner. A dialog with no
  taskbar button comes forward through its owner's button.
- OS dialogs (MessageBox, open, save, folder, text input, app chooser, "not responding"):
  `WindowFrame.Dialog`, `Tool` style with no taskbar button (ADR §6). On mobile they stay full
  screen.
- `WindowHost` adds `is-tool` or `is-frameless`.

### `None` transparency (ADR 0234 §5, #378)

- The surface of a `None` window has no background, border or shadow. The surface and the client
  area get `pointer-events: none`: a click where the app draws nothing reaches the desktop or the
  window below, like a Windows layered window.
- What the app draws takes the mouse back: the client area's direct children and any element
  marked `data-kazo-window-hit` (useful inside a container the app itself makes transparent with
  `pointer-events: none`). An app that fills its client area with one opaque block lets nothing
  through: it draws its shape with separate elements.
- Over an interactive desktop (icons), `DesktopArea` no longer gives the mouse back to a `None`
  window (`.window-host:not(.is-frameless)`).
- Test: `WindowFramelessTransparencyTests` (UI.E2E) loads the real styles and the real pointer
  module in Chromium: click through to an icon, drawn areas, marked element, surface without
  background, `data-kazo-window-drag` area that moves the window except on a field.

## 7. An app's child windows and dialogs (ADR 0234 §3)

```csharp
// In a WindowAppBase: the equivalent of Form.ShowDialog(owner).
KazoDialogResult result = await ShowDialogAsync<RenameDialog>(new KazoWindowRequest
{
    Title = "Rename",
    Options = new WindowOptions { DefaultWidth = 360, DefaultHeight = 180, MinWidth = 300, MinHeight = 160, Style = WindowStyle.Tool },
    Parameters = new Dictionary<string, object?> { [nameof(RenameDialog.Name)] = fileName },
});
if (result.IsAccepted) { fileName = result.ValueAs<string>()!; }

// Modeless window: the equivalent of Form.Show(owner).
IKazoWindow palette = await ShowWindowAsync<PaletteWindow>(new KazoWindowRequest { Title = "Palette" });
```

```razor
@inherits KazoWindowBase
<input @bind="Name" />
<button @onclick="() => CloseAsync(KazoDialogResult.Ok(Name))">OK</button>
<button @onclick="() => CloseAsync(KazoDialogResult.Cancel)">Cancel</button>
@code { [Parameter] public string? Name { get; set; } }
```

- The child window belongs to the app instance: same process, same rights, same `AppContext`,
  same context menus. Its owner is the window that opens it. It opens centered on its owner, on
  the same screen, and closes with its app or its owner (result `None`).
- `ShowDialogAsync` forces `Modal = true`: the owner is blocked, and the owner's taskbar button
  brings the dialog forward. Cancelling the token closes the dialog (`Cancel`).
- The Close button, Alt+F4 and the taskbar give `KazoDialogResult.Cancel`.
- `KazoWindowBase` (derives from `KazoComponentBase`): `CloseAsync(result)`, `WindowTitle`,
  `SetWindowTitleAsync`, `WindowOptions` and the window methods of §6, and its own
  `ShowWindowAsync` and `ShowDialogAsync` for a child of a child window.
- `KazoDialogResult`: `Kind` (`None`, `Ok`, `Cancel`, `Abort`, `Retry`, `Ignore`, `Yes`, `No`, as
  `DialogResult`) and an optional `Value`.
- Without a window manager (tests, detached window, platform older than SDK 3.1),
  `ShowWindowAsync` and `ShowDialogAsync` throw `InvalidOperationException`.

Architecture: `IAppChildWindowGateway.OpenWindowAsync`, implemented by the gateway from
`AppWindowGatewayFactory.Create(windowId, context)`. The window is a `WindowRuntimeState` with
`Content` (as the OS dialogs) that cascades the app's context, its menus, its own gateway and its
`IKazoWindow`. It has no remembered size (ADR 0116).

## Implementation decisions

- **Full screen added** (`toggle-fullscreen`, `Alt+F11`, menu entry): keyboard
  locking only exists in full screen, so a way to enter it was needed.
- **`KazoWindowSize.DisplaySurfaceId`**: distinguishes the resizing of a detached window from
  that of the desktop window.
- **Measured size** in the browser, estimated only as a fallback (tests, server rendering).
- **`/etc/kazoos/keybindings.config` shipped as comments**: active values come from
  the code as long as nobody writes the file, like the other OS documents.
- **Snapping keeps the earlier geometry** (ADR 0177, replaces "Snapping keeps no restore
  geometry"): the snapped window stays `Normal`, with `IsSnapped` and `RestoreBounds`. The saved
  size (ADR 0116, 0122) ignores this half screen and keeps the one from before snapping.
- **Keys in an iframe** (browser, sites): they stay in the iframe, the page does not see
  them; focus must first be given back to the desktop.
- **Fix**: minimizing a background window threw an exception in `WindowRuntime`
  (it tried to give it focus). It is now minimized without touching focus.

## Tests

- `WindowAppIconTests` (UI.E2E): app images decoded and identical in
  chrome/taskbar, seven themes and the macOS exception, menu on right click on the icon
  only, left click and keyboard. `WindowLaunchCoordinatorTests` covers
  the resolution of relative paths and segment escaping.

- `WindowPointerGestureTests` (UI.E2E): real pointer module and frame projection
  in Chromium; sampling of positions over several frames with
  projection before/after the interop return, with or without constraint correction,
  successive moves, lost capture, component removal, interop error and
  slowed resizing. `WindowGeometryRestoreTests` also checks each
  frame on release during two moves in the complete OS.

- `WindowAppBaseWindowEventsTests` (Blazor SDK): hook order, logged error,
  detached surfaces.
- `WindowAppEventDispatcherTests`, `WindowKeyboardCommandsTests`, `WindowLaunchCoordinatorTests`
  (Platform.Blazor): translation of facts, a single end of resize, switcher, menu,
  keyboard modes, initial sizes.
- `WindowKeyBindingsTests` (Platform.Core): parsing of combinations and of the layered file.
- `WindowRuntimeTests` (Windowing.Core): end of resize, overflowing minimum, minimizing
  in the background.
- Validator, serializer, mapper and schema (App.Abstractions).
- `WindowAppBaseWindowTitleTests` (Blazor SDK), `WindowTitleTests` (Windowing.Core),
  `AppWindowGatewayFactoryTests` (Platform.Blazor): title set before the first render, same title
  ignored, cleaning, return to the app name, a single window targeted, closed window, surfaces.
- E2E `KazoPaintWindowTitleTests` (UI.E2E): `Untitled — Kazo Paint`, `*` after a stroke, file
  name after "Save As" in the title bar and the taskbar.
- E2E `WindowKeyboardCommandsTests` (UI.E2E): `` Alt+` `` switches window and `Alt+Q` closes the
  active window in a Chromium tab.

- `WindowFrameTests` (Windowing.Core), `WindowStyleOptionsTests` (Platform.Blazor),
  `WindowAppBaseWindowFrameTests` (SDK Blazor), serializer (App.Abstractions): styles and booleans
  (ADR 0234), chrome drawn per style, taskbar, app requests (#384), dialog brought forward by its
  owner, fields written only when not default.
- `ChildWindowTests` (Platform.Blazor), `WindowAppBaseChildWindowTests` (SDK Blazor): child window
  centered, modal, result, Close button, closing with the owner, component parameters and
  cascade, `Modal` forced by `ShowDialogAsync`, cancelled token.

## Task list

- [x] Fix the double move on release and the transition before/after projection.
- [x] Cover intermediate frames in Chromium and in the OS restore scenario.

- [x] Window hooks in `WindowAppBase` and SDK contracts (`KazoWindowState`, `KazoWindowSize`).
- [x] Runtime facts: published batch, end of resize, fix for minimizing in the background.
- [x] `WindowAppEventDispatcher` dispatcher and client area measurement.
- [x] Detached windows followed (`TrackDisplaySurface`, browser).
- [x] `WindowOptions` in `AppDefinition`, manifest, schema, validator, strict serializer.
- [x] Sizes applied on opening; Maximize button hidden if not resizable.
- [x] `keybindings.config` (schema, layers, commented default, reload).
- [x] `WindowKeyboardCommands`: switcher, close, menu, move, resize, minimize, maximize, snap, full screen.
- [x] Window menu and overlays: keyboard and right click on the icon (ADR 0164).
- [x] Declared icons of system apps, RCL URLs and macOS exception (ADR 0164).
- [x] WindowApp sample app: declared sizes and event log.
- [x] Unit and E2E tests.
- [x] Documentation and index.

### Work item `v3.1/3-window-title`

- [x] `SetWindowTitleCommand` command, `WindowTitleChangedEvent` event, `WindowTitles.Sanitize`.
- [x] `IAppWindowGateway`, `AppWindowGatewayFactory`, cascade in `WindowAppRenderer` (desktop and mobile).
- [x] `WindowTitle`, `SetWindowTitleAsync`, `FormatWindowTitle` in `WindowAppBase`; detached surfaces followed.
- [x] Kazo Paint, text editor, code editor, theme editor, explorer.
- [x] Unit and E2E tests.
- [x] ADR 0123, documentation, SDK reference and index.

### Work item `os-3-1/4-window-style-options` (ADR 0234 §1, §2, §4, §6)

- [x] `WindowStyle` and booleans in `WindowOptions`, manifest, schema, serializer.
- [x] `WindowFrame`, `SetWindowFrameCommand`, `ActivationTarget` in the runtime.
- [x] Chrome, handles, keyboard, arrangement and taskbar follow the style.
- [x] OS dialogs as `Tool` with no taskbar button.
- [x] `IAppWindowGateway` and `WindowAppBase` methods (#384).
- [x] Tests and documentation.

### Work item `os-3-1/5-child-windows` (ADR 0234 §3)

- [x] `KazoWindowRequest`, `KazoDialogResult`, `IKazoWindow`, `IAppChildWindowGateway`, `KazoWindowBase`.
- [x] `ShowWindowAsync` and `ShowDialogAsync` in `WindowAppBase` and `KazoWindowBase`.
- [x] Child windows in `AppWindowGatewayFactory`: cascade, modal, closing with the owner.
- [x] Tests and documentation.

### Work item `os-3-1/6-transparent-none` (ADR 0234 §5, #378)

- [x] `None` surface without background or shadow, clicks through, `data-kazo-window-hit`.
- [x] Interactive desktop: the `None` window lets the mouse through.
- [x] E2E test and documentation.
