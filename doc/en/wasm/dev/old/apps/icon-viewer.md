# Icon Viewer (`org.hackeros.icon-viewer`)

## Purpose

`Apps/System/HackerOs.Apps.IconViewer/` lets a user or developer browse, search, and
copy a ready-to-use snippet for every icon bundled with HackerOS: Bootstrap Icons,
Font Awesome (solid/regular/brands), Lucide, and Simple Icons, via the shared
`IIconCatalog` (see [`../icon-library.md`](../icon-library.md) and ADR
[0026](../../adr/0026-icon-library-support.md)), plus MudBlazor's bundled Material Design
icons for comparison.

## Layout

- **Header** — a live search box and a running match count.
- **Library filter** — a row of toggle buttons (All / Bootstrap / Font Awesome / Lucide
  / Simple Icons); each shows that library's total icon count.
- **Grid** — up to 200 matching icons at a time, each a clickable tile showing the
  rendered `HackerIcon` and its lookup name. A "Show more" button reveals the next 200
  when there are more matches, so opening the ~10,400-icon "All" view never renders
  more DOM than the user has actually asked to see.
- **Detail panel** — appears after clicking a tile: a larger preview, the icon's
  library/variant/name, and a read-only input containing the exact Razor snippet
  (`<HackerIcon Library="IconLibrary.X" Name="..." />`) to reproduce it. Click (or
  focus) the input to auto-select its text, then copy with Ctrl+C/Cmd+C.

Search matches against both the icon's lookup name and its human-readable display
name, is case-insensitive, and is scoped to whichever library filter is active.

## Why click-to-select instead of a Copy button

The detail panel selects the snippet text via plain DOM `element.select()` rather than
writing to the clipboard directly. HackerOS's capability model defines
`clipboard.write` and an `IAppClipboardGateway` contract, but neither is wired into
`IAppExecutionContext` yet; calling the real browser Clipboard API directly from app
code would bypass the deny-by-default capability model the platform is built on (ADR
0002). Plain text selection needs no capability grant, so it's the compliant choice
today. See ADR [0026](../../adr/0026-icon-library-support.md) for the full reasoning.

## Manifest

- **ID**: `org.hackeros.icon-viewer`
- **Category**: `utilities`
- **Capabilities**: none — the app only reads the in-memory icon catalog.
- **Single instance**: no.

## Completed Task Checklist

- [x] Create project file `HackerOs.Apps.IconViewer.csproj` referencing
  `HackerOs.AppSdk.Icons`.
- [x] Create manifest `app.manifest.json` declaring reverse-domain ID
  `org.hackeros.icon-viewer`.
- [x] Implement `IconViewerWindow.razor` & `IconViewerWindow.razor.css` with the
  search/filter/grid/detail UI in the Gothic/Hacker dark theme.
- [x] Register `HackerOs.Apps.IconViewer` in `HackerOs.Ecosystem.csproj` (project
  reference, lazy-load declaration, embedded manifest).
- [x] Verify project compilation, manifest validation
  (`HackerOs.Tools.ManifestValidator`), and test suite execution.
