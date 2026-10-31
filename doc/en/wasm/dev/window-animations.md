> [Version française](../../../fr/wasm/dev/window-animations.md)

# Window animations

Work item 3 of issue [#242](https://github.com/Kazo-ca/HackerOs/issues/242), implementation
of [ADR 0198](adr/0198-window-animations.md).

## 1. What the user sees

| Action | Animation |
| --- | --- |
| Open a window | fade and slight scale (0.95 → 1) |
| Close | the window fades out and shrinks a little |
| Minimize | the window shrinks and fades into its button on the taskbar of its screen |
| Restore from the taskbar | the reverse, from the button |
| Maximize, restore the size, snap | the window glides from its old box to the new one |

Duration: 180 ms by default. "Reduce motion" (Settings ▸ Accessibility) or the system
preference turns everything off. Nothing is animated on mobile (one surface, no chrome).

## 2. Architecture

Everything is in `Platform/HackerOs.Windowing.Blazor`:

- **`WindowHost.razor`** compares, at each `OnParametersSet`, the window's previous snapshot
  with the new one and picks the animation of the render: `open` (first render),
  `minimize`, `restore`, `geometry` (Normal ↔ Maximized, or a change of `IsSnapped`) or
  `none`.
  - During `minimize`, the `hidden` attribute waits for the end of the animation: a counter
    (`_minimizeGeneration`) makes an animation interrupted by a restore stale.
  - The first render carries the `is-opening` class (opacity 0), which the script removes
    when the open animation starts: the module import never shows the window one frame too
    early.
  - On disposal (`DisposeAsync`), it calls `playClose`.
- **`WindowHost.razor.js`**:
  - `projectWindowGeometry(element, x, y, w, h, z, transition, windowId)` writes the
    geometry, plays the requested animation and remembers the window's box (without
    transforms) for the next animation. For `minimize`, the promise settles when the
    animation ends (or a little after its duration, when the tab is in the background).
  - `playClose(element)` inserts a frozen copy of the window (`is-closing-ghost`) into the
    layer of the screen and fades it out.
  - Animations use `element.animate` (Web Animations). Each new animation cancels the
    previous one: a window never stays transparent.
  - The minimize target is the `[data-taskbar-window-id="<id>"]` button closest to the
    layer of the window's screen, else the bottom of the screen.
- **`WindowChrome.razor.js`** remembers, during a drag, the drawn box (`__hosPreviewBox`):
  a drag that ends in a snap animates from there.
- **`HackerOs.Taskbar.Blazor`**: each window button carries `data-taskbar-window-id`.

## 3. Theme variables

| Variable | Default | Where to change it |
| --- | --- | --- |
| `--hos-window-anim-duration` | `180ms` | theme editor (Animations), theme, pack |
| `--hos-window-anim-easing` | `cubic-bezier(0.2, 0, 0, 1)` | theme sheet or pack |
| `--hos-window-anim-scale` | `0.95` | theme sheet or pack |

`themes.css` sets the duration to `0ms` under `data-motion="reduced"` and
`prefers-reduced-motion: reduce`. A zero duration, or a missing variable with
`prefers-reduced-motion: reduce`: no animation. A curve the browser does not understand is
replaced by the default curve.

## 4. Key decisions

- A frozen copy for the close animation rather than delaying the removal in the window
  manager: the C# state stays exact (the window is closed) and nothing changes for apps or
  for tests that count windows.
- FLIP rather than a CSS transition on `top/left/width/height`: mouse moves and resizes
  stay instant.
- Nothing new in the app SDK; only the `WindowHost.OnParametersSet` override enters the package's `PublicAPI.Unshipped.txt`.

## 5. Tests

- `Tests/HackerOs.Platform.Blazor.Tests/WindowAnimationTests.cs`: rendering of
  `WindowHost` (`is-opening` class, `hidden` after the minimize ends, animation passed to
  the script).
- `Tests/HackerOs.Apps.ThemeEditor.Tests`: the `--hos-window-anim-duration` token is translated.
- `Tests/HackerOs.UI.E2E.Tests/WindowAnimationTests.cs`: in Chromium, each animation ends with the window opaque and untransformed, the close copy is an inert `div` with no `data-window-id` that leaves the page, and nothing is animated with `prefers-reduced-motion: reduce`. `WindowTitleBarTests` waits for the animation to end before measuring the buttons.

## 6. Task list

- [x] Pick the animation of each render in `WindowHost` and delay `hidden`.
- [x] Play open, minimize, restore, geometry and close in the script.
- [x] Theme variables and catalog token (fr + en), turned off by "Reduce motion".
- [x] `data-taskbar-window-id` attribute on the taskbar buttons.
- [x] Tests, ADR 0198, docs, `features.md`, change entry.
