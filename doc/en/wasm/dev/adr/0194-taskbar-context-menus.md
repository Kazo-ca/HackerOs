> [Version française](../../../../fr/wasm/dev/adr/0194-taskbar-context-menus.md)

# ADR 0194: taskbar context menus

## Status

Accepted on 2026-10-29, work item 4 of the taskbar series (issue
[#242](https://github.com/Kazo-ca/HackerOs/issues/242), points 8 and 9 of the decisions made with
Benoit on 2026-10-29). Builds on the OS context menu ([ADR 0150](0150-os-context-menu.md)), the
window menu with "Other screen ▸" ([ADR 0179](0179-per-screen-taskbars-and-other-screen-menus.md)
§3-5) and the per-screen taskbars (ADR 0179 §1). Implementation:
[`taskbar-context-menus.md`](../taskbar-context-menus.md).

## Context

- The taskbar (`HackerOs.Taskbar.Blazor`) is a package with no dependency on the OS: it gets its
  sources and commands through interfaces (`ITaskbarWindowSource`, `ITaskbarCommandDispatcher`,
  `ITaskbarLauncher`…). It has no context menu: a right click shows the browser's menu.
- The window menu exists (`WindowManagerOverlays`): Maximize/Restore, Minimize, Move, Resize, Snap
  left/right, Full screen, Close, then "Other screen ▸". It opens from the title bar's icon and with
  `Alt+Space`.
- The OS context menu (ADR 0150) opens on any element carrying the attributes given by
  `ContextMenuScope.Attach`. Its script already handles the right click, the touch long press, the
  Menu key and `Shift+F10`, on screen 1 and on the secondary screens.
- Settings has no "Taskbar" page. The taskbar's only setting, `primary-taskbar` (ADR 0179 §7), is on
  the "Screens" page.

## Decision

1. **The taskbar package stays neutral.** It gets a new `ContextMenus` parameter
   (`ITaskbarContextMenus`) giving, for the taskbar and for each window's button, HTML attributes
   to set on the element. The taskbar does not know what they hold. Without the parameter nothing
   changes. This adds to the package's public API (`PublicAPI.Unshipped.txt`) without a break.
2. **The OS uses its context menu.** The desktop (`DesktopShell`) gives each taskbar (one per
   screen) an object that attaches two OS menus: `taskbar-window-menu` (attached object: the
   window) and `taskbar-menu` (attached object: the taskbar's screen). The right click, the long
   press, the Menu key and `Shift+F10` therefore work everywhere, as on the desktop.
3. **Right click on a window's button.** The menu holds, in order:
   - "New window": starts the window's app once more, on this taskbar's screen. The entry is
     disabled when the app accepts a single instance (`SingleInstancePerUser`) or is no longer in
     the catalog;
   - the window menu's entries, the same as the title bar's, with their shortcuts and state
     (disabled when they do not apply);
   - "Other screen ▸", with "New screen".
   The same code builds the title bar's menu and the taskbar's (a single builder,
   `WindowMenuDefinitions`). The right click does not activate the window, as in Windows.
4. **Right click on the taskbar, outside a window's button.** The menu holds:
   - "Task manager": opens the system monitor (`org.hackeros.system-monitor`) on this taskbar's
     screen;
   - "Show the desktop": minimizes every window of this screen;
   - "Cascade windows" and "Tile windows": arrange the windows of this screen (point 5);
   - "Taskbar settings": opens Settings on the "Screens" page, which holds the taskbar's setting.
   Entries with nothing to do (no visible window, app missing from the catalog) are disabled. The
   Start button and the clock are inside the taskbar: a right click on them opens this menu too.
5. **Cascade and tile.** Only the top-level windows of this screen count: visible (not minimized),
   without an owner, and not blocked by a modal dialog. A maximized window is restored first, a
   snapped window leaves its tile.
   - **Cascade**: from back to front, each window keeps its size and steps down from the corner of
     the work area, with the window opening step (28 px) over at most 8 slots, like
     `WindowLaunchCoordinator`.
   - **Tile**: a grid of `ceil(√n)` columns and as many rows as needed covers the work area. A
     resizable window takes its cell; the others move to their cell's corner. Each window's
     minimum size is still respected.
   The stacking order does not change, and each arranged window saves its geometry as after a
   manual move (ADR 0116).
6. **Texts.** The new texts go into the resources of `HackerOs.Platform.Blazor` (English and
   French), next to the window menu's.

## Consequences

- A right click on the taskbar no longer shows the browser's menu.
- The taskbar package gains a public interface; it will be in the package's next minor version.
- "Show the desktop" does not undo itself on a second click (Windows does): windows are reopened
  from their button. An undo may come later.
- Hover previews (work item 5) can give the same menu on a right click of the preview, with the
  same attributes.

## Rejected alternatives

- **A menu drawn by the taskbar itself.** The package would have had to redo the OS menu's
  placement, keyboard, long press and submenus, and would have depended on the OS.
- **Callbacks (`EventCallback`) from the taskbar to the desktop.** The desktop would have had to
  open the menu itself and redo the Menu key, `Shift+F10` and the long press, which the OS menu's
  script already handles.
- **A new "Taskbar" page in Settings.** It would hold a single setting, already on the "Screens"
  page.
