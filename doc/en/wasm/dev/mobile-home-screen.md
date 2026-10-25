> [Version française](../../../fr/wasm/dev/mobile-home-screen.md)

# Mobile home screen and app drawer

## Purpose

The Android/iOS home screen presents shortcuts across several pages and a
dock. All compatible mobile apps remain accessible in the drawer, even
without a shortcut. [ADR 0165](adr/0165-mobile-home-drawer-and-mouse-controls.md)
completes the [archived mobile plan](old/mobile-interface-platform-plan.md).
[ADR 0166](adr/0166-mobile-layout-conf-on-desktop.md) replaces its path and
its persistence format with a text file under the desktop.

## Usage

- Return to the desktop: touch/click the top handle or drag down, then choose
  **Desktop** in the panel. A simple click also works with the mouse.
- Open the drawer: drag up from Home (Android circle, iOS handle) or the dock;
  or use **All applications** in the dock.
- Search then touch an app to launch it. **+** adds a shortcut to the current
  page; the star adds/removes from the dock. An app that is already open is
  restored.
- The drawer puts the focus in the search, keeps Tab within its controls and
  restores the focus on closing; Esc closes the drawer.
- Home hides the app without stopping it and closes the panels. Back first
  closes the drawer, the panel or the recents before querying the app.
  Recents keeps its controls.
- Dragging sideways changes page. The dots, the arrows and the left/right keys
  offer alternatives. The dots appear after navigation.
- **Edit**, a long press or a right click on an icon enters edit mode.
  Mouse drag and drop and the arrows reorder the shortcuts. The transfer
  arrows move to another page; the next page is created if needed. An empty
  page can be deleted. Removing a shortcut does not uninstall the app.
- **Settings > Appearance > Mobile mode mouse controls** hides or reveals the
  page arrows and gesture hints. The gestures remain usable with the mouse.

## Architecture

| Element | Responsibility |
| --- | --- |
| `MobileShell` | Composes home and the current app, keeps the drawer state for Back/Home, tracks the workspace size. |
| `MobileHomeScreen` and its collocated files | Allowed catalog, pages, search, editing and dock. Parameters `IsDrawerOpen`, `OnDrawerChanged`, `OnAppSelected`. |
| `MobileAppIcon` | Declared image of the app, shared between pages, dock and drawer; Lucide fallback. |
| `MobileHomeLayout` | Bounded internal model, reading with `ConfigDocumentFormat`, deterministic text writing and legacy JSON reading for migration only. |
| `MobileHomeLayoutStore` | Hidden VFS file through `IOsSettingsService`, serialized edits and projection published after success. |
| `MobileSettingsDocuments` | Schema of the `mobile` OS document, accessible to Settings through `HackSettings.OpenSystemAsync`. |
| `WindowProcessObserver` | Process/window bridge shared with the desktop for indirect launches and stops. |
| `wwwroot/mobileGestures.js` | Browser primitives: capture, long press, stray click, size; C# decides the direction and the thresholds. |

The offered apps are of kind `Window`, visible, enabled and `mobile`-compatible
according to their manifest. Names follow the session language. All texts are
available in French and English. The files stay local to the simulated system,
without depending on the optional backend.

The fourteen windowed system apps declare a `MobileEntryPoint` shared with
their desktop component; the generator emits the `platform` form with both
platforms. The drawer filter and the orchestrator keep the compatibility
checks for the other apps.

## Persistence and settings

`~/Desktop/.mobile/layout.conf`:

```ini
# HackerOS mobile Home layout
version=1

[dock]
apps=org.hackeros.browser,org.hackeros.settings

[pages]
1=org.hackeros.text-editor
2=org.hackeros.calculator
```

Pages are numbered from 1 to N with no gap; an empty value keeps an empty
page. The order of the comma-separated IDs defines the positions. `#`
comments, sections and keys follow the OS settings grammar. Limits: 24 pages,
20 shortcuts per page, 5 in the dock. There is always one page; empty pages
are allowed. Duplicates, invalid IDs, unknown versions and oversized files are
refused. Unavailable IDs remain recorded and are temporarily hidden at render
time. An invalid file is kept until an explicit edit action.

The `.mobile` directory lives in each user's desktop. The desktop hides names
starting with a dot. The explorer hides names starting with a dot by default.
Its **Show hidden files and folders** button (eye) toggles their display in
the current window, with the same filter as the search and without writing.
The layout is part of the user's desktop files. Each edit replaces a single
file; changing page writes nothing. No localStorage and no IndexedDB schema
change.

If the new file does not exist, a valid legacy layout from
`~/.config/hackeros/.mobile/layout.json` is converted automatically at load.
The old file stays intact; no JSON write is performed. An existing
`layout.conf` always takes priority, even if invalid, and is never overwritten
by this migration. Without a valid old file, the first edit creates the
configuration. Subsequent writes normalize its text.

`OsSettingsLocation.UserHome` lets `OsSettingsFile` resolve the relative path
`Desktop/.mobile/layout.conf` with the session's rights.

`/etc/hackeros/mobile.config`, overridden by `~/.config/hackeros/mobile.config`:

```ini
mouseHelpers=true
```

Administrator locking, value origin and permissions follow the layered
settings engine. Disabling hides the helpers, never the gestures.

## Full-screen dialogs (ADR 0171)

`MobileShell` creates `FileDialogWindowAdapter`, as the Desktop shell does: the
session's dialogs (Open, Save, Choose folder, text input, message, Open with,
Properties, conflict) become windows.
`SingleSurfacePresentationPolicy.SelectSurfaceStack` gives the stack shown: the
app's window, then its modal dialogs. `SingleSurfaceArea` draws the whole stack
maximized to the screen; only the last window takes input, the owner stays
mounted underneath (`inert`) and is no longer minimized. The system Back button
closes the top dialog. In the file dialogs, one tap enters a folder or takes a
file (`HackFileView.OpenOnTap`); there is no double click on mobile. Mobile
dialogs have no title bar: they have their Cancel button and the system Back.

An app can also declare a phone-specific window with
`AppDefinition.MobileEntryPoint`: the OS opens it on the mobile platform
(`AppDescriptor.EntryPointTypeFor`), and falls back to the main entry point when
the type cannot be found. See
[ADR 0171](adr/0171-mobile-file-explorer-and-full-screen-dialogs.md) and the
[mobile explorer](file-explorer.md#mobile-explorer-adr-0171).

## Verification

- `MobileHomeLayoutTests`: hidden path, order, unavailable IDs, invalid data
  left intact, limits/duplicates, serialized edits and absence of a session,
  comments/whitespace, one-time JSON migration and priority of an existing
  conf.
- Existing mobile navigation tests: history, Back and closing of panels.
- `BuildKnownLazyAppsTests`: the aggregated catalog keeps all desktop
  assemblies when apps adopt `platform.entryPoints`; the calculator declares
  the same assembly for desktop and mobile.
- `MobileHomeScreenTests` (UI.E2E): top click, return to desktop, drawer by
  gesture from Home, add/dock, editing, pagination, live mouse option and
  persistence after reload, Android/mouse and iOS/touch emulated in Chromium;
  `.mobile` hidden/revealed by the explorer command.
- `FileViewTreeTests`: the hidden files filter also follows the already
  loaded descendants and the keyboard navigation order.
- Regressions: shell change, panel, recents and desktop keyboard navigation.

## Task list

- [x] Fix the top handle and the stray click after a drag.
- [x] Add home, dock, drawer and restoration of already open apps.
- [x] Save the pages and the dock in the hidden VFS configuration.
- [x] Add editing, pagination and touch/mouse gestures.
- [x] Add the mouse helpers and their option in the layered settings.
- [x] Add the explorer hidden files command and filter the tree.
- [x] Add the tests and the documentation.
- [x] Finish the Android/iOS browser validation and the shell regressions.
- [x] Move the layout to `~/Desktop/.mobile/layout.conf` and migrate the old JSON (ADR 0166).

Initial validation of 2026-10-23 (ADR 0165): 53 targeted Platform Blazor tests
(layout, navigation, launch, geometry), 20 Settings, 94 file control and
30 Explorer passed. The seven browser scenarios cover Android/mouse,
iOS/touch, shell change, mobile choice after reload, panel by drag,
recents/Back and desktop keyboard shortcuts. The full flow also verifies the
hiding of `.mobile`. The Release build of the Ecosystem PWA is verified
without a backend.

ADR 0166 validation: the 20 layout/migration tests and the two browser flows
Android/mouse and iOS/touch pass with the conf file. They verify its
persistence after reload, the folder being absent from the desktop icons, its
explicit visibility in the explorer and the presence of `layout.conf`. Release
compilation succeeded without warnings.
