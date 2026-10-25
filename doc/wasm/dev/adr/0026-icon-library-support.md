# ADR 0026: Shared Icon Library Support

## Status

Accepted on 2026-08-07.

## Context

HackerOS had no shared icon system. The shell drew hardcoded emoji glyphs
(`Shell/AppLauncher.razor`'s app tiles, `Shell/Taskbar.razor`'s clock/notification/
logout icons), and only a handful of apps (`ErrorLogViewerWindow`, `SystemMonitorWindow`,
`WindowChrome`, and the file dialogs) drew icons at all, via MudBlazor's bundled
Material Design path-string constants (`MudBlazor.Icons.Material.*`). There was no way
for an app, or the OS shell itself, to draw a Bootstrap, Font Awesome, Lucide, or brand
(Simple Icons) icon, and no way to browse what icons were available.

Per `docs/platform-ui-library.md`/ADR 0016, MudBlazor types must not appear in App
Abstractions, App SDK contracts, Simulation Abstractions, Platform Core, or Browser
Infrastructure, and remote font dependencies are prohibited. Per `AGENTS.md`, no new
`<script>`/`<link>` may be added to host `index.html`; new static assets ship as Razor
Class Library `wwwroot/` content or embedded resources instead.

## Decision

Add a new Shared SDK-tier project, `HackerOs.AppSdk.Icons`, that any app or OS shell
surface can reference directly (it takes no dependency on `App.Abstractions`,
`AppSdk`, or MudBlazor, so it cannot create a layering violation). It provides:

- `IconLibrary` — an enum for the four bundled libraries: `Bootstrap`, `FontAwesome`,
  `Lucide`, `SimpleIcons`.
- `IconDescriptor` / `IIconCatalog` — a renderer-independent contract for resolving,
  listing, and searching icons, registered as a process-wide singleton in
  `EcosystemServiceCollectionExtensions` (`services.AddSingleton<IIconCatalog, IconCatalog>()`),
  matching the same "one shared instance, ambient via DI" pattern used for every other
  process-wide service.
- `HackerIcon.razor` — a native Blazor rendering component (no MudBlazor) that draws
  an icon as inline SVG, colored via inherited `fill`/`stroke: currentColor` so it
  always matches surrounding text color and the active theme.

### Library selection and licensing

| Library | License | Icons bundled | Notes |
| --- | --- | --- | --- |
| Bootstrap Icons | MIT | 2,078 | Single-color, 16x16 viewBox. |
| Font Awesome Free | Icons CC-BY-4.0 / Fonts SIL OFL-1.1 / Code MIT | 2,883 (solid 2,001, regular 273, brands 609) | Only the free styles are bundled; Pro icons are out of scope. CC-BY-4.0 requires attribution — see `docs/icon-library.md`. |
| Lucide | ISC | 2,007 | The actively maintained continuation of Feather Icons; stroke-based ("outline") style. |
| Simple Icons | CC0-1.0 | 3,453 | Brand/technology logo glyphs, rendered single-color via `currentColor` rather than each brand's native multi-color mark. |

Material Design icons are deliberately **not** duplicated here: MudBlazor 9.7.0 (already
an eager dependency, per ADR 0016) already bundles `Icons.Material.Filled/Outlined/
Rounded/Sharp/TwoTone` as the same kind of path-string constants, at zero additional
payload. `HackerOs.Apps.IconViewer` (see below) surfaces Material alongside the four
new libraries for browsing, by reflecting over MudBlazor's own `Icons.Material.Filled`
type directly — that reflection stays local to the viewer app; it is not part of the
shared `IIconCatalog` contract.

### Storage format: embedded JSON, not an icon font

Each library's icon set (name, SVG `viewBox`, inner markup, stroke-vs-fill, and a
human-readable display name) is generated from the upstream npm package
(`bootstrap-icons`, `@fortawesome/fontawesome-free`, `lucide-static`, `simple-icons`)
into a compact JSON file, embedded as a .NET manifest resource in
`HackerOs.AppSdk.Icons` (`Shared/HackerOs.AppSdk.Icons/Data/*.json`, see
`docs/icon-library.md` for the generation tooling). `IconCatalog` parses these lazily,
once, on first use, via a source-generated `JsonSerializerContext`
(`IconCatalogJsonSerializerContext`) — consistent with the trim-safe JSON pattern
already used for `AppManifestJsonSerializer`/`BuildProfileJsonSerializer`, and required
because every non-test project has `EnableTrimAnalyzer=true` with
`TreatWarningsAsErrors=true` (`Directory.Build.props`).

This was chosen over an icon font (`.woff2` + CSS classes, the traditional Font Awesome
delivery mechanism) because:

- it renders as real, inspectable, themeable SVG (`currentColor` fill/stroke) rather
  than opaque font glyphs, matching how MudBlazor's own Material icons already render
  in this codebase;
- it needs no `@font-face` asset and no new host CSS, so no `index.html` changes are
  needed at all (`HackerIcon.razor` and its embedded data are the entire integration
  surface); and
- per-icon JSON records are trivially searchable/enumerable in C#, which the Icon
  Viewer app depends on.

### Payload cost — measured, not assumed

Per the evidence-based precedent set by ADR 0016's MudBlazor proof, this was measured
with a Release publish of `HackerOs.Ecosystem` (`dotnet publish -c Release`):

| Asset | Raw bytes | Brotli bytes |
| --- | ---: | ---: |
| `HackerOs.AppSdk.Icons.wasm` | 8,670,489 | 2,413,151 |
| `MudBlazor.wasm` (for scale) | 340,245 | 109,401 |

`HackerOs.AppSdk.Icons` is referenced directly by `HackerOs.Ecosystem` (like
`HackerOs.Platform.Core`/`HackerOs.AppSdk.Blazor`) rather than being declared
`BlazorWebAssemblyLazyLoad`, so — unlike the individual System apps — it is part of the
eager initial download, not deferred until an app that uses it launches.

This was a deliberate, disclosed tradeoff, not an oversight:

- HackerOS's lazy-loading mechanism (`BuildKnownAssemblyLoaderRegistry`,
  `BuildKnownLazyAppDescriptorRegistry`) only loads **one** assembly per app —
  exactly `manifest.EntryPoint.Assembly` — keyed off the app catalog. It has no
  concept of "an app's additional shared-library dependencies" today. Making
  `HackerOs.AppSdk.Icons` lazy-loadable so that only `HackerOs.Apps.IconViewer` (or a
  future consumer) pays for it would require extending that platform lifecycle
  mechanism — real surgery on well-tested, security-relevant infrastructure — which is
  out of proportion to this change.
- 2.3 MB brotli is a real but bounded, one-time addition, in the same spirit as the
  MudBlazor adoption in ADR 0016.
- If payload becomes a concern later, the fix is scoped and known: extend
  `BuildKnownLazyAppDescriptorRegistry`/`BuildKnownAssemblyLoaderRegistry` to support
  loading a declared set of extra assemblies per app (not just the entry point), then
  mark `HackerOs.AppSdk.Icons.dll` lazy. This ADR intentionally does not attempt that
  extension now.

### The Icon Viewer app

`HackerOs.Apps.IconViewer` (`org.hackeros.icon-viewer`) is a new first-party System
app, built the same way as every other window app (references `App.Abstractions` +
`AppSdk.Blazor` + `Platform.Core`, plus `AppSdk.Icons`). It lets a user or developer
search and filter every bundled icon (including Material, via local MudBlazor
reflection) and click one to see its exact `<HackerIcon>` Razor usage snippet in a
read-only, click-to-select input field. See `docs/apps/icon-viewer.md`.

Copying that snippet deliberately uses plain DOM text selection (`element.select()`
via a collocated JS module), not the browser Clipboard API. HackerOS's capability
model defines `clipboard.write`/`clipboard.read` and an `IAppClipboardGateway`
contract, but neither is wired into `IAppExecutionContext` yet — calling the real
browser clipboard directly from an app would bypass the deny-by-default capability
model this platform is built on (ADR 0002). Native text selection needs no such
capability, so it was the compliant choice available today.

### Shell adoption is deliberately out of scope here

`Shell/AppLauncher.razor` and `Shell/Taskbar.razor` still draw hardcoded emoji. Wiring
them to `IIconCatalog`/`HackerIcon` is a natural follow-up this ADR does not attempt,
because both live in `HackerOs.Platform.Blazor`, which `HackerOs.Ecosystem` already
loads eagerly — adopting icons there does not change the payload math above (the data
is already eager), but it is a shell-behavior change with its own visual-regression
surface, deserving its own review rather than being bundled into this addition.

**Update 2026-08-13:** the taskbar moved to the standalone `HackerOs.Taskbar.Blazor`
package (`docs/window-taskbar-export-plan.md`, `EXT-WIN-007`/`008`); its hardcoded
emoji now live in `Platform/HackerOs.Taskbar.Blazor/Taskbar.razor`, not
`HackerOs.Platform.Blazor`. Adopting `IIconCatalog`/`HackerIcon` there would add an
`AppSdk.Icons` dependency to a package designed to be consumable outside HackerOS —
worth weighing against the emoji-icon tradeoff explicitly if this follow-up is picked
up. `Shell/AppLauncher.razor` is unaffected and remains in `HackerOs.Platform.Blazor`.

## Consequences

- Any app, and the OS shell, can now draw a themeable, inline-SVG icon from five
  libraries (four new plus Material via MudBlazor) through one small, dependency-light
  contract (`IIconCatalog`/`HackerIcon`).
- `HackerOs.AppSdk.Icons` adds a measured ~2.3 MB brotli to the eager initial payload;
  this is disclosed above rather than silently absorbed.
- Font Awesome Free icons carry a CC-BY-4.0 attribution requirement; see
  `docs/icon-library.md` for how that's satisfied.
- Extending lazy-loading to cover secondary (non-entry-point) assemblies per app
  remains open future work if payload becomes a constraint.

## References

- ADR 0002: Authority comes from trusted policy, never a manifest's self-claim
  (clipboard capability reasoning)
- ADR 0010: Canonical manifest JSON and schema evolution
- ADR 0016: Platform UI library boundary (MudBlazor) — measurement precedent
- `docs/icon-library.md`
- `docs/apps/icon-viewer.md`
- `docs/lazy-loading.md`
