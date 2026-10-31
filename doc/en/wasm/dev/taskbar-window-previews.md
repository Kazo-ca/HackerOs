> [Version française](../../../fr/wasm/dev/taskbar-window-previews.md)

# Taskbar window previews and "peek"

Work item 5 of the taskbar series (issue [#242](https://github.com/Kazo-ca/HackerOs/issues/242)),
implementation of [ADR 0199](adr/0199-taskbar-window-previews.md). It builds on the per-screen
taskbars ([`per-screen-taskbars.md`](per-screen-taskbars.md)) and the taskbar menus
([`taskbar-context-menus.md`](taskbar-context-menus.md)).

## 1. What the user sees

- Hovering a window's button with the mouse for 400 ms opens a **preview card** above the
  button: icon, title, a "×" button and a **reduced live copy** of the window, refreshed twice a
  second.
- While a card is open, hovering another button moves to it at once.
- **Click on the thumbnail**: the window comes to the front (restored if it was minimized).
  **"×"**: the window closes, as from its title bar. **Right click**: the window button's menu.
- **Peek**: resting the pointer 300 ms on the card shows the window alone in place (even
  minimized); the other windows of the screen keep only their outline. Leaving the card puts
  everything back.
- No preview with a finger (mobile, touch screen) nor with the keyboard.
- A window that shows an `iframe`, a video or a plugin shows its icon and title instead of the
  copy.

## 2. Architecture

| Piece | Project | Role |
| --- | --- | --- |
| `ITaskbarWindowPreviews` | `HackerOs.Taskbar.Blazor` | Contract supplied by the host: `LiveCopySelector(id)` and `Peek(id?)`. |
| `Taskbar.Previews` | `HackerOs.Taskbar.Blazor` | Parameter; no preview without it. |
| `TaskbarPreviewController` | `HackerOs.Taskbar.Blazor` (internal) | Timing: opening (400 ms), moving between buttons, closing (300 ms), peek (300 ms), touch ignored. |
| `Taskbar.razor.js` | `HackerOs.Taskbar.Blazor` | Places the card above the button, clones the window, strips ids and active attributes, copies canvases and scroll positions, again every 500 ms. |
| `TaskbarWindowInteraction.Show` | `HackerOs.Taskbar.Blazor` (internal) | Click on the thumbnail: restore when minimized, then activate. |
| `TaskbarWindowPreviews` | `HackerOs.Platform.Blazor` (`Shell/`) | The desktop's implementation: `[data-window-id="…"]` selector, peeked window and `Changed` event. One instance for every taskbar. |
| `DesktopArea.PeekWindowId`, `WindowHost.PeekState`, `WindowPeekState` | `HackerOs.Windowing.Blazor` | The peek: `is-peek-shown` (the window and its dialogs, on top, even minimized) and `is-peek-see-through` (the other windows of the same screen, outline only, no pointer). |

The flow: hover → `TaskbarPreviewController` → card rendered → `OnAfterRenderAsync` calls the
module's `showPreview`. Rest on the card → `Peek(id)` → `TaskbarWindowPreviews.Changed` →
`DesktopShell` renders again → `DesktopArea` gives each window a `WindowPeekState`.

## 3. Live copy: rules

- Removed from the copy: `id`, `name`, `tabindex`, `autofocus`, `accesskey`, `contenteditable`,
  `popover`, `for`, `form`, `list`, the `aria-*` attributes that name an id, and the
  `data-window-*` attributes (the copy is never taken for a window).
- The copy layer is `inert` and `aria-hidden="true"`: nothing takes the focus or the pointer,
  and screen readers do not read it.
- The size is the window's on screen; when minimized, its geometry's (`--window-width`,
  `--window-height`).

## 4. Themes

Added tokens: `--hos-taskbar-preview-width`, `--hos-taskbar-preview-height`,
`--hos-taskbar-preview-background`, `--hos-taskbar-preview-border`,
`--hos-taskbar-preview-radius`, `--hos-taskbar-preview-shadow`,
`--hos-taskbar-preview-duration`, `--hos-taskbar-preview-close-hover-border`,
`--hos-taskbar-preview-close-hover-background`, `--hos-window-peek-outline`, `--hos-window-peek-glass` and
`--hos-window-peek-z`. The appearance is turned off by "Reduce animations" (`data-motion`) and
`prefers-reduced-motion`.

## 5. Tests

- `Tests/HackerOs.Taskbar.Blazor.Tests/TaskbarPreviewControllerTests.cs`: timing, touch, moving
  between buttons, peek, closing, click on the thumbnail, rendered attributes.
- `Tests/HackerOs.Platform.Blazor.Tests/Shell/TaskbarWindowPreviewsTests.cs`: selector, peek
  announced once, peek classes per window and per screen.
- `Tests/HackerOs.UI.E2E.Tests/TaskbarWindowPreviewTests.cs`: in Chromium, hover, copy without
  ids, peek, "×".

## 6. Task list

- [x] ADR 0199.
- [x] `ITaskbarWindowPreviews` contract and `Taskbar.Previews` parameter.
- [x] Timing in C# (`TaskbarPreviewController`) and tests.
- [x] Preview card (icon, title, "×", thumbnail, context menu) and fr/en texts.
- [x] Live copy (the package's JavaScript module).
- [x] Peek in `DesktopArea`/`WindowHost` and the desktop's implementation.
- [x] E2E test, docs, `features.md`, change entry.
