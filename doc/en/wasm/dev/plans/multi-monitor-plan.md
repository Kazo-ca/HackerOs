> [Version française](../../../../fr/wasm/dev/plans/multi-monitor-plan.md)

# Multi-monitor: series plan

**Status:** series started by Benoit on 2026-10-28 ("we can start the
multi-monitor feature"). ADRs [0178](../adr/0178-extended-screens-in-display-surfaces.md)
and [0179](../adr/0179-per-screen-taskbars-and-other-screen-menus.md) are written
before the code (work item 0). Work item 1 done: screens in the window manager
([`window-runtime-displays.md`](../window-runtime-displays.md)).

## 1. Purpose

Give HackerOS an **extended desktop across several screens**. Each additional
screen is a display surface (`window.open` of the same instance) that holds its
own desktop, taskbar and windows. A window moves from one screen to another
without losing its state.

## 2. Benoit's decisions (2026-10-28)

| Topic | Decision |
| --- | --- |
| Browsers | Full Chrome and Edge (automatic placement, full screen per monitor, `window-management` permission), with a **fallback** elsewhere: the user places the window and the OS remembers its position. |
| Taskbar | **One taskbar per screen**, and a setting: the primary screen's taskbar shows every window or only those of its screen. Other taskbars show their own windows. |
| Moving a window to another screen | **Menu and shortcut first**; dragging with the mouse comes in a separate work item. |
| Menus | Right click on the icon: current menu, separator, "Other screen ▸" (Screen 1, Screen 2, …, separator, New screen). Right click on Maximize/Restore: Snap left, Snap right, Full screen (Linux keys), separator, "Other screen ▸". |
| Identification veil | When hovering "Screen N", black veil at 33% on the other screens, black square and white number in the center. **Each screen shows its own number**, the hovered screen highlighted. The veil disappears if the pointer moves over it. |
| Merges | One branch and one PR per work item into `multi-monitor/base`. Since 2026-10-28, Benoit allows these PRs to be merged without Benoit's review, to move to the next step. At the end, one PR to `main`, which only Benoit allows to merge. |
| Buttons thread | When "Fix the buttons of maximized windows" (ADR 0177) is merged, its changes are brought into **every** branch of the series, without conflicts. |

## 3. Existing state (main, 2026-10-28)

| Item | State | Where |
| --- | --- | --- |
| Display surfaces | `IDisplaySurfaceService`, `DisplaySurfaceContent` portal, styles and theme copied, size and focus events, end states | ADR 0078, 0081, [`detached-display-windows.md`](../detached-display-windows.md) |
| Window manager | One `WorkArea` (`ChangeViewportCommand`); maximize and snap refer to it | `Platform/HackerOs.Windowing.Core/WindowRuntime.cs` |
| Desktop | One `DesktopArea` in `DesktopShell`, desktop app (`org.hackeros.desktop`) as background | `Platform/HackerOs.Platform.Blazor/Shell/` |
| Taskbar | One instance, every window | `Platform/HackerOs.Taskbar.Blazor` |
| Window menu | Single-level list in `WindowManagerOverlays`; right click on the icon, `Alt+Space` | ADR 0095, 0164 |
| OS context menu | Multi-level, keyboard | ADR 0150 |

## 4. Work items

Each work item starts from an up-to-date `multi-monitor/base`, on the branch
shown, and comes back through a PR into `multi-monitor/base`.

| No. | Branch | Content | Depends on |
| --- | --- | --- | --- |
| 0 | `multi-monitor/0-adr` | ADRs 0178 and 0179 (fr and en), this plan, indexes. | — |
| 1 | `multi-monitor/1-window-runtime-displays` | `DisplayId`, work area per screen, `AddDisplayCommand`, `RemoveDisplayCommand`, `MoveWindowToDisplayCommand` and their events; changing screens in each state; modal following its owner; screen removed. Public API in `Unshipped`. Unit tests. No visible change. | 0, ADR 0177 |
| 2 | `multi-monitor/2-window-portal` | Windows rendered once and moved through a portal into their screen's window layer; `DesktopArea` filtered by screen. Browser tests (no component recreated). | 1 |
| 3 | `multi-monitor/3-screen-service` | Screen service: `window-management` permission, `getScreenDetails`, one surface per screen on a free monitor, `screenschange`, fallback with remembered position, full screen by button, "Restore screens", "main tab hidden" banner. | 1 |
| 4 | `multi-monitor/4-secondary-desktop` | `DesktopArea` in each screen, overlay layer per screen (window menu, context menu, switcher, keyboard move), secure dialogs pointed to screen 1. Wallpaper only on secondary screens; icons stay on screen 1 (Q2). | 2, 3 |
| 5 | `multi-monitor/5-taskbars` | One taskbar per screen, per-screen filter, `primary-taskbar` setting, Start menu on the clicked screen, new windows on the right screen, notifications on screen 1. | 4 |
| 6 | `multi-monitor/6-other-screen-menus` | Window menu through the OS context menu, new Maximize/Restore button menu, "Other screen" submenu, New screen, identification veil, `MoveToPreviousScreen` / `MoveToNextScreen` shortcuts. | 4, ADR 0177 |
| 7 | `multi-monitor/7-screen-settings` | `screens.config` (system and user), Settings ▸ Screens. | 5, 6 |
| 8 | `multi-monitor/8-drag-between-screens` | Drag a window from one screen to another (pointer capture, `screenX`/`screenY` converted to the target screen). | 4 |
| 9 | `multi-monitor/9-final` | Final check of `multi-monitor/base`, project memory, then PR to `main` (merge allowed by Benoit only). | all |

After work item 1, items 2 and 3 can move in parallel, and item 8 can move in
parallel with items 5 to 7.

## 5. Series rules

- **Merges**: work-item PRs are merged into `multi-monitor/base` without
  waiting for Benoit (permission of 2026-10-28). The final PR to `main` is only merged on Benoit's authorization.
- **ADR 0177**: as soon as the buttons thread is merged, `main` is merged into
  `multi-monitor/base`, then into each open work-item branch. Conflicts are
  resolved in the series, never by rewriting history.
- **Docs**: each work item updates the docs in French and English, and the
  `README.md` indexes of the folders it touches.
- **Merge bar**: green local build and test suite. For a work item that touches
  a public API: `PublicAPI.Unshipped.txt` and the SDK compatibility test.
- **Questions**: asked directly to Benoit, with choices, context and their
  consequences; a document is not enough.

## 6. Questions asked to Benoit (2026-10-28)

| No. | Question | Recommended choice |
| --- | --- | --- |
| Q1 | Is an SDK API for screens (list screens, my window's screen) part of this series? | **Answered on 2026-10-28: later.** The series does not touch the app SDK. |
| Q2 | What does a secondary screen's desktop show? | **Answered on 2026-10-28: the wallpaper only.** The `~/Desktop` icons stay on screen 1. |

The answers will be copied here and into the ADRs.

## 7. Known limits

- Automatic placement and full screen per monitor: Chrome and Edge only.
- The main tab must stay visible, otherwise the browser slows down the whole OS.
- Reloading the OS closes the secondary screens; one click reopens them.
- An iframe in a window that changes screens reloads.
- No mirroring, no multiple screens on mobile.
