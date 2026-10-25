# Icon Library Support

## Overview

`HackerOs.AppSdk.Icons` is a shared, dependency-light SDK project that gives every
app — and the OS shell itself — access to five icon libraries as themeable inline SVG:

| Library | `IconLibrary` value | License | Icons | Style |
| --- | --- | --- | --- | --- |
| Material Design | *(via MudBlazor, not this package — see below)* | MIT | ~2,000 per variant | Filled/Outlined/Rounded/Sharp/TwoTone |
| Bootstrap Icons | `IconLibrary.Bootstrap` | MIT | 2,078 | Filled |
| Font Awesome Free | `IconLibrary.FontAwesome` | Icons CC-BY-4.0 / Fonts SIL OFL-1.1 / Code MIT | 2,883 | Solid, Regular, Brands |
| Lucide | `IconLibrary.Lucide` | ISC | 2,007 | Stroke/outline (Feather-lineage) |
| Simple Icons | `IconLibrary.SimpleIcons` | CC0-1.0 | 3,453 | Brand/technology logos |

See ADR [0026](../adr/0026-icon-library-support.md) for why these libraries and this
storage format were chosen, and for the measured WASM payload cost.

Material icons aren't duplicated into this package — MudBlazor 9.7.0 (an existing
eager dependency, see [`platform-ui-library.md`](platform-ui-library.md)) already
bundles `MudBlazor.Icons.Material.Filled`/`.Outlined`/`.Rounded`/`.Sharp`/`.TwoTone` as
path-string constants. Use those directly (`Icons.Material.Filled.Dashboard`, as
`ErrorLogViewerWindow` and `SystemMonitorWindow` already do) in any app that already
references the `MudBlazor` package.

## Using an icon in your app

Reference `HackerOs.AppSdk.Icons` from your app's `.csproj` (it has no dependency on
`App.Abstractions`, `AppSdk`, or MudBlazor, so it never creates a layering violation —
see the wrapper conventions in [`platform-ui-library.md`](platform-ui-library.md)):

```xml
<ProjectReference Include="..\..\..\Shared\HackerOs.AppSdk.Icons\HackerOs.AppSdk.Icons.csproj" />
```

Then draw an icon with the `HackerIcon` component, either by name:

```razor
@using HackerOs.AppSdk.Icons

<HackerIcon Library="IconLibrary.Bootstrap" Name="house" Size="20" />
```

or with an already-resolved `IconDescriptor` (useful when you're iterating over search
results, as `IconViewerWindow` does):

```razor
<HackerIcon Icon="myDescriptor" Size="28" />
```

`HackerIcon` renders as inline SVG colored via inherited `fill`/`stroke:
currentColor`, so it always matches surrounding text color and picks up the active
theme automatically — there's nothing to configure. Unmatched attributes (e.g.
`class`, `style` is not permitted per [`design-system.md`](design-system.md), but
`aria-*`, `data-*`) splat onto the rendered `<svg>` via `AdditionalAttributes`.

`HackerIcon` requires `IIconCatalog` from DI (it's `@inject`ed internally), which is
registered as a process-wide singleton in `EcosystemServiceCollectionExtensions` — you
don't need to register anything yourself in a normal app.

## Using the catalog directly

For search, browsing, or anything beyond drawing one known icon, inject
`IIconCatalog`:

```csharp
@inject IIconCatalog Catalog

@code {
    protected override void OnAppInitialized()
    {
        IReadOnlyList<IconDescriptor> results = Catalog.Search("arrow", IconLibrary.Lucide);
        int total = Catalog.Count(IconLibrary.Bootstrap);
        bool found = Catalog.TryGet(IconLibrary.SimpleIcons, "github", out IconDescriptor github);
    }
}
```

`IIconCatalog` lazily parses its four embedded JSON resources on first use and then
serves everything from an in-memory index for the process lifetime — repeated calls
are cheap.

## Font Awesome attribution

Font Awesome Free icons are CC-BY-4.0 licensed, which requires attribution. If your
app or a published surface displays Font Awesome icons in a context where attribution
would normally be expected (e.g. an "about" or credits screen), credit "Font Awesome"
with a link to <https://fontawesome.com>. The Icon Viewer app's detail panel shows each
icon's library, which is sufficient in-context attribution for icons drawn incidentally
in the UI.

## Regenerating the bundled icon data

The JSON resources embedded in `HackerOs.AppSdk.Icons` (`Shared/HackerOs.AppSdk.Icons/
Data/*.json`) are generated from the upstream npm packages, not hand-written. To
refresh them (e.g. after an upstream icon library releases new icons):

```bash
cd Tools/icon-import
npm install
npm run extract
```

This downloads the four npm packages (`bootstrap-icons`, `@fortawesome/fontawesome-
free`, `lucide-static`, `simple-icons`) and regenerates
`Shared/HackerOs.AppSdk.Icons/Data/*.json` in place. Review the diff, bump the package
versions recorded in `Tools/icon-import/package.json` and in ADR
[0026](../adr/0026-icon-library-support.md)'s license table, and run
`dotnet test Tests/HackerOs.AppSdk.Icons.Tests` before committing — its tests assert
minimum icon counts per library and spot-check well-known icon names, which catches a
malformed or truncated regeneration.

### On-disk JSON shape

Each record uses single-letter keys to keep the embedded resource small (see
`IconCatalogJsonSerializerContext.cs`):

| Key | Meaning |
| --- | --- |
| `n` | Lookup name (kebab-case, unique within the library) |
| `v` | SVG `viewBox` |
| `b` | Inner SVG markup (`<path>`/shape elements only, no wrapping `<svg>`) |
| `s` | `1` if stroke-based (outline style), `0` if filled |
| `g` | Optional sub-style/group (Font Awesome's `solid`/`regular`/`brands`), or `null` |
| `t` | Human-readable display title |

## Browsing every icon

`HackerOs.Apps.IconViewer` — the "Icon Viewer" system app — lets you search and filter
every bundled icon (including Material) and copy a ready-to-paste `<HackerIcon>`
snippet. See [`apps/icon-viewer.md`](apps/icon-viewer.md).
