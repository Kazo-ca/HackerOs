> [Version française](../../../fr/wasm/dev/hack-paint.md)

# Hack Paint

Drawing app `org.hackeros.hack-paint` (`Apps/System/HackerOs.Apps.HackPaint`,
uninstallable). Work item `v3/11-apps`,
[ADR 0103](adr/0103-hack-paint-image-files-and-theme-editor.md) §1: images
live in the OS files.

## Purpose

Draw (brush, color, size, zoom, grid, move, crop, rotate, undo and redo) and
**open, save, save as** images from the simulated filesystem.

## Formats

| Action | Formats |
|---|---|
| Open | PNG, JPEG, BMP, GIF (first frame), WebP |
| Save | PNG by default; JPEG when the name ends in `.jpg`/`.jpeg` (quality 10 to 100, 92 by default, image flattened onto white) |

- Maximum size: 4,096 × 4,096 pixels and 16 MiB per file (ADR 0094).
  The header is read in C# (`ImageFileFormat.TryReadHeader`) **before** any
  decoding: a file that is too large, damaged, or of another format yields a
  message, without freezing the app.
- The saved file carries its media type (`image/png`, `image/jpeg`) and is
  created as `0644`.

## Usage

- Toolbar: *New*, *Open…*, *Save*, *Save As…*, JPEG quality; errors in a
  banner, file path in the status bar.
- Shortcuts: Ctrl+N, Ctrl+O, Ctrl+S, Ctrl+Shift+S, Ctrl+Z, Ctrl+Y (the JS
  module prevents the browser from taking them).
- Window title: `nom.png — Hack Paint`, preceded by `*` when the image has
  unsaved changes, set by `SetWindowTitleAsync` (ADR 0117 §4) and displayed by
  the window manager (title bar, taskbar, `` Alt+` ``). It is no longer
  repeated in the app; "opening… / saving…" appears in the status bar.
- Closing with unsaved changes asks for confirmation
  (`IWindowCloseGuard`, Yes/No box).
- Opening from the explorer: Hack Paint declares `FileHandlers` for the five
  types (`open` and `edit` actions) and `file-associations.json` makes it the
  default handler of `edit` for `.png .jpg .jpeg .bmp .gif .webp`
  (ADR 0096). "Open with" and a path passed at launch also open the file.
- Window 960 × 680, minimum 600 × 420 (`AppDefinition.Window`, ADR 0095).

## Architecture

| Piece | Role |
|---|---|
| `ImageFileFormat` | formats, limits, header reading (PNG IHDR, GIF, BMP, WebP VP8/VP8L/VP8X, JPEG SOF), save format from the name |
| `PaintFileStore` | reading (`ReadAsync`, `Inspect`) and writing (`WriteAsync`) through the app's file gateway |
| `PaintCanvasState` | RGBA pixels, history, `IsDirty`/`MarkSaved` |
| `HackPaintWindow.razor(.cs)` | window, SDK dialogs (`OpenFileAsync`, `SaveFileAsync`), close guard |
| `HackPaintWindow.razor.js` | drawing on the canvas; `decodeImage` (`createImageBitmap` → RGBA, errors returned as `decode-failed` / `too-large` codes that the C# translates) and `encodeImage` (`toBlob`); never accesses files |

Bytes are read and written in C# by `IAppFileSystemGateway`, like the text
editor: a file chosen in a dialog outside the home folder is only accessible
through the token handed over by the dialog, which `HackFile` cannot carry. The
JS only receives bytes and returns pixels.

### Computations outside the window (ADR 0140)

Hack Paint has no image filter. Its whole-image operations (rotation, crop)
stay in the window rather than in a `HackBackground` task
([`background-jobs.md`](../framework/background-jobs.md)): they are passes of
copies over the buffer, which can be 64 MiB (4096 × 4096 in RGBA), four times
what a task input can carry in JSON (16 MiB), and the return trip would cost
more than the pass. A future filter will go through a task and a temporary
file.

## Languages (ADR 0104)

Toolbar, status bar, dialogs and errors come from
`Resources/Strings.resx` / `Strings.fr.resx`. `PaintFileStore` writes its
messages in the language of the process that calls it
(`HackLocalization.GetLocalizer<Strings>()`); the JavaScript module only
returns error codes. The title keeps the product name "Hack Paint"; only
"Untitled" becomes "Sans titre".

## Tasks

- [x] Header reading and limits before decoding.
- [x] Open (dialog, explorer, launch argument), decode through the browser.
- [x] Save, Save As, PNG and JPEG with quality.
- [x] Title with `*`, confirmation on close.
- [x] Title set in the window by SDK 1.1 (work item `v3.1/3-window-title`, E2E
  `HackPaintWindowTitleTests`).
- [x] `FileHandlers` and default `edit` association for images.
- [x] Tests (`HackerOs.Apps.HackPaint.Tests`): headers of the five formats,
  truncated files, limits, save format, change tracking, title.
- [ ] axe scans: work item `v3/13`.
