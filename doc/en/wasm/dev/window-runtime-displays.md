> [Version française](../../../fr/wasm/dev/window-runtime-displays.md)

# Screens in the window manager

Work item `multi-monitor/1-window-runtime-displays` of the
[multi-monitor plan](plans/multi-monitor-plan.md), implementing
[ADR 0178 §2](adr/0178-extended-screens-in-display-surfaces.md). This work item
changes nothing on screen: without a secondary screen, the OS behaves as before.
The next work items (window portal, screen service) build on it.

## 1. Screen identity

`DisplayId` (`HackerOs.Windowing.Abstractions`) is the number the user sees:

| Member | Role |
| --- | --- |
| `DisplayId.Primary` | Screen 1, the main tab. It is also the default value (`default(DisplayId)`). |
| `DisplayId.FromNumber(n)` | Screen `n` (1 or more). |
| `Number`, `IsPrimary` | Displayed number, whether it is screen 1. |

`WindowRuntimeState.DisplayId` tells which screen shows the window (screen 1 by
default). `Bounds` and `RestoreBounds` are in that screen's coordinates.

## 2. One work area per screen

`WindowRuntime` keeps one work area per registered screen:

| Member | Role |
| --- | --- |
| `WorkArea` | Work area of screen 1, as before. |
| `Displays` | Registered screens, screen 1 first, then by number. |
| `GetWorkArea(DisplayId)` | Work area of a screen; `KeyNotFoundException` if it does not exist. |
| `NextDisplayId` | The smallest free number, for the next screen (ADR 0178 §5). |

Moving, resizing, maximizing and constraining a window happen in the work area of
**its** screen. There is still one focus and one Z order for the whole OS.
`WindowKeyboardCommands` (snapping, window menu) and `WindowGeometryRecorder`
(snap detection) also read the work area of the window's screen.

## 3. Commands and events

| Command | Effect | Events |
| --- | --- | --- |
| `ChangeViewportCommand(area)` | New work area for screen 1 (existing code unchanged). | `WindowViewportChangedEvent` (`DisplayId` = screen 1) |
| `ChangeViewportCommand(area, screen)` | New work area for a screen; only its windows are constrained. | `WindowViewportChangedEvent` (`DisplayId` = screen) |
| `AddDisplayCommand(screen, area)` | Registers an empty screen. Already registered: `InvalidOperationException`. | `DisplayAddedEvent` |
| `MoveWindowToDisplayCommand(window, screen)` | Moves the window to the screen, with its modal family. Same screen: no event. Unknown screen: `KeyNotFoundException`. | one `WindowDisplayChangedEvent` per moved window |
| `RemoveDisplayCommand(screen)` | Brings its windows back to screen 1, then removes the screen. Screen 1: `InvalidOperationException`; unknown screen: no event. | `WindowDisplayChangedEvent` per window, then `DisplayRemovedEvent` |

`WindowDisplayChangedEvent` gives the screen left, the new screen, the new bounds
and the restore geometry. `WindowAppEventDispatcher` uses it to call
`OnWindowResized` when the client area size changes.

## 4. Changing screen keeps the state

| Window state | On the target screen |
| --- | --- |
| Maximized | Stays maximized and fills the target work area. Its restore geometry keeps its relative position. |
| Snapped (exact left or right half of its work area) | Stays snapped on the same side, on the half of the target work area. |
| Normal | Keeps its size and its position relative to the top-left corner, in proportion to the work area sizes, then it is kept inside the target work area (the declared minimum wins, ADR 0095 §2). |
| Minimized | Changes screen and stays minimized. If it was minimized from maximized, it fills the target work area. |

Focus and Z order do not change.

ADR 0177 (thread "Corriger les boutons des fenêtres maximisées") adds a memory of
the size before snapping. When it reaches the series, that memory follows the
window like its restore geometry.

## 5. Modal windows

- A modal window (`OwnerModal`) opens on its owner's screen, whatever screen was
  requested.
- Moving a window moves its whole modal family: its owner (following modal links
  up) and every modal window below it. Events follow the Z order, back to front.

## 6. Removed screen

When a screen goes away (surface closed, lost, monitor unplugged), the screen
service (work item 3) sends `RemoveDisplayCommand`. Each window of the screen comes
back to screen 1 with the rules of §4, like the content of a surface returning
under its anchor. A removed screen frees its number.

## 7. Public API and tests

- The new types and members are in `PublicAPI.Unshipped.txt` of
  `HackerOs.Windowing.Abstractions` and `HackerOs.Windowing.Core`. Existing
  signatures do not change (`ChangeViewportCommand` and
  `WindowViewportChangedEvent` keep their constructor and get a second one).
- Tests: `Tests/HackerOs.Windowing.Core.Tests/WindowRuntimeDisplayTests.cs`
  (numbers, work areas per screen, changing screen in every state, modals,
  removed screen).
