> [Version française](../../../../fr/wasm/dev/adr/0220-mouse-back-forward-buttons.md)

# ADR 0220: mouse Back and Forward buttons and system Back sent to the active app

## Status

Accepted on 2026-10-31; Benoit chose the recommendations (A, A, A, see
"Questions asked to Benoit"). Series
`mouse-back/base` (branches `mouse-back/<n>-<topic>`).

- **Complements:** the SDK `IAppBackHandler` contract
  ([`mobile-interface-platform-plan.md`](../old/mobile-interface-platform-plan.md)
  §8, `MOB-012`), the mobile Back sequence (§7.3, `MOB-011`) and
  [ADR 0171](0171-mobile-file-explorer-and-full-screen-dialogs.md) (mobile
  File Explorer).
- **Does not change:** the mobile bar's ◁ button, the title-bar Back button of
  a window that declares `supportsBack`, nor the toolbar arrows of File
  Explorer and the browser.

## Context

- A mouse's Back button (button 4, `MouseEvent.button === 3`) and Forward
  button (button 5, `button === 4`) move **the host browser tab** back or
  forward. In HackerOS this leaves the OS (or reloads the previous page)
  instead of going back inside the active app. The same goes for Alt+← and
  Alt+→, for Android's Back button or gesture, and for the left-edge swipe in
  Safari on iOS.
- The SDK already has the contract of an app that can go back:
  `IAppBackHandler` (`CanNavigateBack`, `CanNavigateBackChanged`,
  `NavigateBackAsync(AppBackRequest)`) with `AppBackSource`
  (`SystemNavigationBar`, `DesktopChromeButton`, `KeyboardShortcut`). The
  window renderer registers every component that implements it in
  `AppBackHandlerRegistry`, whether the app declares `supportsBack` or not.
- Today only the **mobile** File Explorer implements `IAppBackHandler`. The
  desktop File Explorer (`FileExplorerState` already has a Back stack and a
  Forward stack) and the browser (`GoBack`, `GoForward`, Alt+←/→ inside its
  window) have their own history, but nothing sends them the mouse button.
- Browser pages are shown in an iframe: mouse and keyboard events inside it
  never reach the OS document. The page bridge (`PageBridge.js`) already
  relays events to the browser window.
- Chrome and Edge cancel the navigation when the page calls
  `preventDefault()` on the `mouseup` of buttons 4 and 5; Firefox and Safari
  do not guarantee it. A phone's Back gesture produces no mouse event: only
  the browser history (`popstate`) sees it.

## Decision

### 1. Two ways to catch Back: the mouse and the history (Q1)

A `hardwareNavigation.js` module (in `HackerOs.Platform.Blazor`'s `wwwroot/`,
imported by the service of §2) listens to the OS document and holds no rule:
it turns a gesture into "Back" or "Forward" with its source, and calls C#.

- **Mouse**: `mouseup` (capture phase, on `window`) with `button === 3` →
  Back, `button === 4` → Forward, source `MouseButton`. The module calls
  `preventDefault()` and stops propagation on `mousedown`, `mouseup` and
  `auxclick` of these two buttons, which cancels Chrome's and Edge's
  navigation.
- **Keyboard**: `keydown` Alt+← / Alt+→ and the `BrowserBack` /
  `BrowserForward` keys, source `KeyboardShortcut`, with `preventDefault()`.
- **History**: after the user's first gesture in the OS (Chrome skips entries
  added without a user gesture), the module adds a "sentinel" entry with
  `history.pushState` on the same address, keeping the existing
  `history.state` (Blazor stores its own there). When the browser goes back
  onto that entry (`popstate`), the module puts the sentinel back and sends
  Back with the source `SystemBack`. Its `popstate` listener is added first,
  in the capture phase, and stops propagation for returns onto the sentinel:
  the Blazor router never sees them.
- **No double Back**: if the mouse or the keyboard just sent Back (less than
  500 ms ago), the following `popstate` (Firefox, Safari) puts the sentinel
  back without sending Back again.
- The history only serves Back: going forward would need a second sentinel
  "ahead", which is fragile. Forward comes from the mouse and the keyboard.

### 2. `HardwareNavigationService` sends the gesture to the active window

A scoped service of `HackerOs.Platform.Blazor` receives the module's calls and
picks the target:

- **Desktop**: the focused window (not minimized). Back calls its
  `IAppBackHandler`, Forward its `IAppForwardHandler` (§3). If the window has
  none or answers `NotHandled`, nothing happens (Q3). An open context menu or
  Start menu closes first, as with Escape.
- **Mobile**: Back follows the existing ◁ button sequence
  (`MobileNavigationCommandsAdapter.RequestBack`: open shade or Recents,
  cancelable dialog, the app's `IAppBackHandler`, previous window, close,
  home) with the gesture's source. On the home screen nothing happens: the OS
  is never left (Q3). Forward calls the foreground app's
  `IAppForwardHandler`.
- The module is attached by `DesktopShell` and `MobileShell` (once per
  document; a detached window or an extra screen has its own document and
  its own module, which targets that screen's active window).

### 3. SDK: two more sources and `IAppForwardHandler` (Q2)

Additions in `HackerOs.AppSdk.Blazor` (`PublicAPI.Unshipped.txt`, next minor),
with no change to what exists:

- `AppBackSource.MouseButton`: the mouse Back or Forward button;
  `AppBackSource.SystemBack`: the browser's or the phone's Back (Android
  button or gesture, iOS swipe, host browser arrow).
- `IAppForwardHandler`, modelled on `IAppBackHandler`:
  `CanNavigateForward`, `event Action? CanNavigateForwardChanged`,
  `ValueTask<AppBackResult> NavigateForwardAsync(AppForwardRequest request,
  CancellationToken cancellationToken = default)`;
  `AppForwardRequest(AppBackSource Source)` reuses the source enumeration
  (its name stays `AppBackSource`, already shipped).
- `AppBackHandlerRegistry` also tracks `IAppForwardHandler` (same
  reference-safe registration); `WindowAppRenderer` registers every component
  that implements either interface.
- An app declares nothing in its manifest: implementing the interface is
  enough. `supportsBack` keeps its only role (title-bar Back button and
  discovery-time validation).

### 4. Apps wired in

- **Desktop File Explorer**: implements `IAppBackHandler` and
  `IAppForwardHandler` over `FileExplorerState` (same actions as its toolbar
  arrows). It does not declare `supportsBack`: its toolbar already has its
  arrows.
- **Mobile File Explorer**: keeps its Back sequence and implements
  `IAppForwardHandler`.
- **Browser**: implements both interfaces over its history (`GoBack`,
  `GoForward`). `PageBridge.js` relays buttons 4 and 5 and Alt+←/→ made in the
  page (with `preventDefault()` inside the iframe); the window then calls its
  own Back or Forward directly. Its current Alt+←/→ shortcut goes through the
  same path.
- **Settings and Hack Store**: when they show a sub-page (a section on mobile,
  an app's details), Back returns to the list. Checked during the work item;
  an app with no page to go back from implements nothing.
- Other apps (terminal, editors, calculator…) have no navigation history: the
  gesture does nothing. Undoing an edit stays Ctrl+Z.

### 5. Known limits

- A page shown in another iframe (an ASP.Hack site in an app other than the
  browser, third-party content) keeps the host browser's behaviour for the
  mouse; Back through the history works everywhere.
- On iOS, an app installed on the home screen has no system Back gesture: the
  mobile bar's ◁ button stays the way to go back.
- The user can still leave HackerOS through the address bar or the host
  browser's Back button menu (long press).

## Questions asked to Benoit

Asked in the project thread on 2026-10-31; Benoit answered A to all three
the same day.

1. **Mechanism.** A (recommended): mouse and history (§1). B: history only,
   one mechanism but no reliable Forward and no known source. C: mouse only,
   nothing changes on mobile.
2. **Forward button.** A (recommended): yes, `IAppForwardHandler` (§3). B:
   Back only.
3. **When the app has nothing left to go back to.** A (recommended): never
   leave the OS (§2). B: let the browser go back, which leaves HackerOS.

## Consequences

- The mouse Back button, Alt+← and the phone's Back go back inside the active
  app instead of leaving HackerOS.
- Every SDK app benefits by implementing `IAppBackHandler` (already shipped)
  and, if it wants, `IAppForwardHandler`.
- One more entry in the tab's history: the host browser's Back button no
  longer leaves the OS in one click.

## Implementation notes

Work item `mouse-back/1-platform` (2026-10-31), with no change to the decision:

- The service is `HardwareNavigationService` (scoped); the
  `HardwareNavigationListener` component (with its `.razor.js`) is rendered by
  `DesktopShell` and `MobileShell`, and each shell sets its route
  (`HardwareNavigationService.Route`): the focused window on the desktop, the
  ◁ sequence (`MobileNavigationCommandsAdapter.RequestBackAsync` and
  `RequestForwardAsync`) on mobile.
- The Blazor router adds its `popstate` listener at boot, before the module:
  it still sees the return onto the entry below, but the address does not
  change, so it stays on the same page. Listeners added after the module do
  not see it.
- Alt+←/→ inside an input field stays with the field (macOS uses it to move
  the caret); if the host browser goes back anyway, the history path sends it
  to the app.
- For now only the main document is listened to: an extra screen or a
  detached window keeps the host browser's behaviour, like the window
  manager's shortcuts.

## Work items

- [x] `mouse-back/0-adr`: this ADR, indexes, memory, `features.md`.
- [x] `mouse-back/1-platform`: `hardwareNavigation.js`,
  `HardwareNavigationService`, `MouseButton` and `SystemBack` sources,
  `IAppForwardHandler`, registry, desktop and mobile routing, tests.
- [x] `mouse-back/2-apps`: File Explorer (desktop and mobile), browser and
  `PageBridge.js`, Settings and Hack Store, tests, app and SDK docs.
