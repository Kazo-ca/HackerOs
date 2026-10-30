> [Version française](../../../../fr/wasm/dev/adr/0199-taskbar-window-previews.md)

# ADR 0199: taskbar window hover previews, and "peek"

## Status

Accepted on 2026-10-30, work item 5 of the taskbar series (issue
[#242](https://github.com/Kazo-ca/HackerOs/issues/242), points 10 and 11 of the decisions taken
with Benoit on 2026-10-29: choice B, "preview + peek like on Windows"). Done in the `todo/`
series (branch `todo/13-taskbar-previews`). Builds on the per-screen taskbars
([ADR 0179](0179-per-screen-taskbars-and-other-screen-menus.md)) and the taskbar menus
([ADR 0194](0194-taskbar-context-menus.md)). Implementation:
[`taskbar-window-previews.md`](../taskbar-window-previews.md).

## Context

- The taskbar (`HackerOs.Taskbar.Blazor`) is a neutral package: it receives its sources and
  commands through interfaces. Hovering a window's button shows nothing.
- Each window is an `article.window-host` element with `data-window-id` (package
  `HackerOs.Windowing.Blazor`). A minimized window keeps its element, with the `hidden`
  attribute. The windows of a secondary screen are moved by portal into that screen's document,
  which also holds its taskbar (ADR 0178, 0179).
- Benoit's decisions: the preview is a reduced **live** copy of the window (cloned HTML, CSS
  `scale`), with a fallback image (icon + title) when it cannot be copied; it shows after
  ~400 ms; a click activates the window, a "×" closes it; no preview on mobile; hovering the
  preview makes the other windows transparent ("peek").

## Decision

1. **The taskbar package stays neutral.** It gets a new `Previews` parameter
   (`ITaskbarWindowPreviews`) with two members: `LiveCopySelector(id)`, the CSS selector of the
   element to copy in the taskbar's document (or `null` for the icon and title), and
   `Peek(id?)`, which starts or ends the peek. Without it, nothing changes. The taskbar owns the
   timing, the preview card and the live copy. An addition to the package's public API
   (`PublicAPI.Unshipped.txt`), not a break.
2. **Timing** (C#, `TaskbarPreviewController`, tested without a browser):
   - the preview opens after **400 ms** of a mouse or pen hovering a button; a touch pointer
     never opens a preview (no hover), so nothing on mobile;
   - while a preview is open, hovering another button moves to it **at once**, as on Windows;
   - leaving the button or the preview closes it after **300 ms**, the time to cross from one to
     the other;
   - a click on a button closes the preview (then does its usual action);
   - the preview closes when its window leaves the taskbar (closed, or sent to another screen).
3. **The preview card**, above the button, in the taskbar: the icon, the title, a "×" button
   (`Close {title}`) that closes the window like its title bar does, and the thumbnail. A click
   on the thumbnail shows the window: restores it when minimized, then activates it. Unlike the
   button, it never minimizes it. A right click on the card opens the window button's menu
   (ADR 0194: same attributes).
4. **The live copy.** A JavaScript module of the package (`Taskbar.razor.js`), limited to what
   C# cannot do: placing the card above the button and copying the window.
   - It clones the window's element, scales it down with `transform: scale` in the thumbnail,
     and does it again every **500 ms** while the preview is open.
   - The copy duplicates no id and is not interactive: `id`, `name`, `tabindex`, `for`,
     `aria-labelledby` and the other id references are removed; the copy lives in an `inert`,
     `aria-hidden` layer. Without `name`, a copied radio button does not uncheck the original.
   - Canvases are copied pixel by pixel (`drawImage`): a cloned canvas is blank. A window that
     holds an `iframe`, a `video`, an `embed` or an `object` keeps the icon and title: their
     content cannot be cloned. *(Refines decision 10: Benoit planned the fallback for canvases
     too; copying the pixels shows them.)*
   - A minimized window is copied at its normal size.
5. **The peek.** After **300 ms** on the card, the taskbar calls `Peek(id)`; leaving the card (or
   closing it) calls `Peek(null)`. The desktop passes the window to `DesktopArea`
   (`PeekWindowId`), which gives each `WindowHost` a `WindowPeekState`:
   - `Shown` for the window and its own dialogs: it shows above the others, even minimized;
   - `SeeThrough` for the other windows **of the same screen**: only their outline stays, like
     glass, and they no longer take the pointer;
   - `None` for the windows of other screens.
   The peek changes neither the order nor the state of the windows: nothing is saved.
6. **Multi-monitor.** The desktop gives the same instance (`TaskbarWindowPreviews`) to every
   screen's taskbar. The module looks for the window in the taskbar's document, which is also
   the document of its screen's windows.
7. **Themes and animations.** The card uses the existing `--hos-*` tokens and adds:
   `--hos-taskbar-preview-width`, `-height`, `-background`, `-border`, `-radius`, `-shadow`,
   `-duration`, `--hos-taskbar-preview-close-hover-*`, `--hos-window-peek-outline`, `--hos-window-peek-glass` and `--hos-window-peek-z`. Its
   appearance (140 ms fade) is turned off by the "Reduce animations" setting and by
   `prefers-reduced-motion`.
8. **Texts**: "Preview of {0}" and "Close {0}" in the taskbar package's resources (English and
   French).

## Consequences

- Hovering a taskbar button shows the window without activating it; the peek shows it in place.
- The taskbar package gains `ITaskbarWindowPreviews`, `Taskbar.Previews` and
  `Taskbar.DisposeAsync`; the windowing package gains `WindowPeekState`,
  `DesktopArea.PeekWindowId` and `WindowHost.PeekState`. They will ship in the next minor
  version of these packages. No change to the app SDK.
- The copy follows the app's scoped styles (`b-…`), since it keeps their attributes; a style
  that depends on an ancestor outside the window may look slightly different in the thumbnail.
- The keyboard has no preview (as on Windows): the window menu and the window switcher (Alt+`, ADR 0095) remain the keyboard
  paths.

## Rejected alternatives

- **An image capture** (`html2canvas` or similar): a heavy, slow library, and not "live".
- **The peek in JavaScript** (styles set on the windows from the taskbar): the taskbar would
  have had to know the windows' layout, and Blazor could have overwritten those styles. The
  peek goes through C# and `WindowHost`'s scoped CSS.
- **Callbacks (`EventCallback`) from the taskbar to the desktop for the timing**: the timing is
  the same for every host, it stays in the package.
