> [Version française](../../../fr/wasm/framework/cursor-themes.md)

# Cursor themes

Decision: [ADR 0205](../dev/adr/0205-cursor-themes.md).

## Purpose

A cursor theme replaces the pointer of the whole HackerOS surface (desktop,
taskbar, windows and apps) with PNG images. It is chosen in **Settings ▸
Appearance ▸ Cursors**. Three themes are built in: **System** (the device's
cursors, the default), **HackerOS Neon** and **Gothic Crimson**; anyone can add
their own.

## Creating a theme

1. Create a folder in `~/.config/hackeros/cursors/` (for you) or, as an
   administrator, in `/etc/hackeros/cursors/` (for everyone):

   ```sh
   mkdir -p ~/.config/hackeros/cursors/neon-crypt
   ```

2. Copy your PNG images into it (at most 128 × 128 pixels and 64 KiB each;
   32 × 32 is the usual size).
3. Write the folder's `cursor.theme` index:

   ```ini
   [cursor-theme]
   id = neon-crypt
   name = Neon Crypt

   [cursors]
   # name = file.png  column  row of the hot spot
   default = arrow.png 2 1
   pointer = hand.png 11 1
   text = ibeam.png 16 16
   wait = hourglass.png 16 16
   ns-resize = resize-ns.png 16 16
   ew-resize = resize-ew.png 16 16
   ```

4. Open Settings ▸ Appearance: the theme shows under **Cursors**; choose it.
   The choice is written to `~/.config/hackeros/cursors.config`
   (`theme=neon-crypt`) and applies at once.

Rules:

- `id`: 1 to 64 lowercase letters, digits and dashes, different from the
  built-in themes and the `/etc` themes.
- The **hot spot** is the pixel that points, counted from the top left corner
  of the image; it must be inside the image.
- A file is a plain name of the folder, ending in `.png`: no path, no `..`, no
  web address, no SVG.
- A wrong line or a refused image is ignored and listed under the choices in
  Settings; the rest of the theme applies.

## Cursor names

The names are the CSS `cursor` keywords. A name missing from the theme takes
its fallback; without a fallback, the device's cursor stays.

| Name | Fallback |
| --- | --- |
| `default`, `pointer`, `text`, `wait`, `help`, `crosshair`, `move`, `not-allowed`, `grab`, `ns-resize`, `ew-resize`, `nesw-resize`, `nwse-resize` | none |
| `progress` | `wait` |
| `grabbing` | `grab` |
| `n-resize`, `s-resize`, `row-resize` | `ns-resize` |
| `e-resize`, `w-resize`, `col-resize` | `ew-resize` |
| `ne-resize`, `sw-resize` | `nesw-resize` |
| `nw-resize`, `se-resize` | `nwse-resize` |
| `all-scroll` | `move` |
| `no-drop` | `not-allowed` |
| `vertical-text` | `text` |
| `cell` | `crosshair` |
| `copy`, `alias`, `context-menu`, `zoom-in`, `zoom-out` | none |

## For app developers

Nothing to do: write `cursor: pointer`, `cursor: ew-resize`… in your
components' `.razor.css`; the OS swaps the image for the chosen theme. Do not
set a cursor through a `style` attribute or JavaScript (`element.style.cursor`):
those keep the device's cursor.

## API

Package `HackerOs.Theming.Abstractions`:

- `CursorNames`: the list of names (`All`), the Settings preview (`Preview`) and
  the fallbacks (`GetFallback`).
- `CursorThemeFile`: `Parse` (`cursor.theme` index), `TryValidateImage` (checks
  a PNG without decoding it), `ToDataUrl`, limits `MaxImageBytes` and
  `MaxImageSize`.
- `CursorThemeLibrary.LoadAsync`: reads the `/etc` and `~` folders with your own
  read functions (`HackDirectory`/`HackFile` in an app).
- `CursorThemeCatalog`: the built-in themes; `CursorTheme.Resolve` and
  `ResolveAll` apply the fallbacks.

Package `HackerOs.Theming.Blazor`: `CursorThemeSurface`, a component that
renders nothing and that a host places once in its root (`Theme="…"`).

Setting: document `cursors` (`HackSettings.OpenSystemAsync(context, "cursors")`),
key `theme`.

## Built-in themes

The images of HackerOS Neon and Gothic Crimson are in
`wasm2/HackerOs/Platform/HackerOs.Theming.Blazor/wwwroot/cursors/<id>/` and are
drawn by `wasm2/HackerOs/build/tools/generate-cursor-themes.py` (Pillow).
Changing a drawing or a hot spot means changing the script and
`CursorThemeCatalog` together; the test
`Every_built_in_cursor_is_a_shipped_png_whose_hot_spot_is_inside` checks that
they agree.

## Limits

- Style sheets of another origin, inline `style` attributes and `<iframe>`s
  keep the device's cursors.
- A theme copied into a folder shows the next time the Settings page opens;
  changing the images of the current theme needs choosing it again or opening
  the session again.
- Ordinary text shows the theme's arrow (not the I-beam); input fields show the
  theme's I-beam.
