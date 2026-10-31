> [Version française](../../../../fr/wasm/dev/adr/0216-pack-all-static-web-assets-of-an-app.md)

# ADR 0216: pack every static resource of an app and load its scoped CSS

## Status

Accepted on 2026-10-31 (Benoit chose to fix HackerOS rather than ask every app
to declare everything).

- **Complements:** [ADR 0206](0206-serve-installed-app-assets.md) (`wwwroot/`
  resources of installed apps) and [ADR 0182](0182-install-apps-from-github.md)
  §1 (`HackerOsPack`).
- **Does not change:** [ADR 0098](0098-sdk-versions-packages-and-manifest-checks.md)
  §5: every served file stays declared in the manifest with its SHA-256, and is
  checked at the first read.

## Context

- An app compiled into the OS gets all its static resources from Blazor at
  `_content/<assembly>/`: its `wwwroot/`, a component's collocated JavaScript
  (`Component.razor.js`, AGENTS.md II.2) and the bundle of its scoped CSS
  (`<assembly>.bundle.scp.css`), which the host's
  `HackerOs.Ecosystem.styles.css` sheet imports.
- An app installed by `hackpkg` or the Hack Store only gets the files of its
  package. `HackerOsPack` only puts the `Assets` declared by
  `AppDefinition.Assets` in it (often the icons), and ADR 0206 only serves those.
  The rest of `wwwroot/` is missing, and so is the collocated JavaScript, which is
  not in `wwwroot/` and cannot be declared.
- The scoped CSS of an installed app is never loaded: no sheet of the OS imports
  it. An app written by the project's rules (styles in `.razor.css`) shows
  without its styles once installed.
- Building a Razor project already writes the exact list of those resources to
  `obj/<configuration>/<tfm>/staticwebassets.build.json` (served path, source
  file, origin).

## Decision

### 1. `HackerOsPack` packs every static resource of the project

- The target passes the build's static web asset manifest
  (`$(StaticWebAssetBuildManifestPath)`, otherwise
  `$(IntermediateOutputPath)staticwebassets.build.json`) to the `pack` tool,
  with `--web-assets`.
- The tool keeps the resources **of the project itself** (origin `Discovered` or
  `Computed`, role `Primary`, not publish-only): the `wwwroot/`, the collocated
  JavaScript and the `<assembly>.bundle.scp.css` bundle. It leaves out the
  resources of referenced packages and projects (the SDK comes with the OS) and
  the `<assembly>.styles.css` application bundle (a host's).
- The served path is the build's `RelativePath` without its fingerprint pattern
  (`#[.{fingerprint}]?`, `#[.{fingerprint}]!`): `Components/Board.razor.js`,
  `Contoso.Notes.bundle.scp.css`.
- Every resource not already in `Assets` is added to it, with its SHA-256 and a
  kind taken from the extension (`.css` → `css`, `.js`/`.mjs` →
  `javaScriptModule`, image → `image`, otherwise `data`). A resource already
  declared keeps its declaration (icon role included). A path the manifest would
  refuse (`..`, `#`, `?`) is left out with a warning.
- Only the **package's** manifest changes. The `manifest.json` generated in the
  project and the manifest of apps compiled into the OS stay as they are: a
  compiled app already gets everything from Blazor.

### 2. The OS loads an installed app's scoped CSS

- When `InstalledAppAssetPublisher` has copied the app's
  `<assembly>.bundle.scp.css` file (entry-point assembly) into the cache (ADR
  0206 §3), it adds, once per session, a `<link rel="stylesheet">` to
  `_content/<assembly>/<assembly>.bundle.scp.css` at the end of the page's
  `<head>`, through `installedAppAssets.js`.
- Without a service worker (ADR 0206 §5), nothing is copied and no link is
  added: the sheet would not be found.
- The sheet stays loaded until the page reloads, like a compiled app's CSS; its
  selectors carry the assembly's own isolation attribute, so they do not touch
  other apps.

### 3. The catalog folder gets the package's manifest

The catalog compares the `manifest.json` of the `apps/<appId>_<version>/`
folder with the one of the package its CI builds (`PackageCheck`, ADR 0188 §5
step 4). The `publish-to-hack-store.yml` workflow of the `hackeros-solution`
template therefore copies the **package's** `manifest.json` (taken from the
`.hackapp`), no longer the project's.

## Consequences

- An installed app looks and behaves as when compiled into the OS, with nothing
  more to declare: its modules, collocated JavaScript, Web Workers and styles are
  there.
- The package grows by the project's whole `wwwroot/`: a file that must not ship
  has no place in `wwwroot/` (same rule as for a compiled app, which Blazor
  publishes entirely). The package limits (2,000 files, 64 MiB compressed,
  128 MiB extracted, `AppPackage`) now apply to the whole `wwwroot/`, and the
  copy into the cache at the session's first launch covers all those files.
- The OS finds an app's files and CSS under its entry-point assembly name; the
  SDK publishes them under `$(PackageId)`. An app therefore keeps `PackageId`
  equal to `AssemblyName` (the default).
- A Razor project packed without `staticwebassets.build.json` gets the
  `HOSAPP0062` warning and a package limited to the declared `Assets`; a path the
  manifest refuses gets `HOSAPP0061`.
- A repository made with an older template updates its
  `publish-to-hack-store.yml` (§3), otherwise the catalog refuses its version.
- Apps already published must be packed again with the new version to benefit;
  an old package works as before.
- The package stays readable by an older OS, which serves its declared files
  (all of them, now) but does not load its scoped CSS.

## Tasks

- [x] `pack` tool: `--web-assets` option, reading `staticwebassets.build.json`, adding the resources to the package's manifest (`PackagedWebAssets`).
- [x] `HackerOsPack` target: passes the static web asset manifest.
- [x] `hackeros-solution` template: `publish-to-hack-store.yml` copies the package's manifest.
- [x] `InstalledAppAssetPublisher`: link to the scoped CSS (`linkStyleSheet` in `installedAppAssets.js`).
- [x] Unit tests (`PackagedWebAssetsTests`, `InstalledAppAssetPublisherTests`).
- [x] Test package `Tests/fixtures/installed-app`: collocated JavaScript and an undeclared file; end-to-end tests (`HackerOs.TrimmedPublish.E2E.Tests`): collocated module and scoped CSS.
- [x] Docs: `app-installation.md`, `app-repository.md`, `features.md`, memory, change entry.
