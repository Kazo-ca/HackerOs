> [Version française](../../../../fr/wasm/dev/adr/0222-installed-app-satellite-assemblies.md)

# ADR 0222: translations of installed apps, satellite assemblies in the package and loaded by the OS

## Status

Proposed on 2026-10-31 (issue #337), built with the recommended choices;
questions asked to Benoit (see "Questions").

- **Complements:** [ADR 0182](0182-install-apps-from-github.md) §1 and §5
  (`hackpkg`, `HackerOsPack`), [ADR 0221](0221-pack-all-static-web-assets-of-an-app.md)
  (every static resource of an app in its package) and
  [ADR 0104](0104-localization.md) §3 (satellites in the browser).

## Context

- An app with per-culture `.resx` files (`Resources/Strings.fr.resx`) compiles a
  satellite assembly `bin/<config>/<tfm>/fr/<Assembly>.resources.dll`.
- An app compiled into the OS gets its satellites loaded by Blazor and
  `BrowserSatelliteAssemblies` (ADR 0104 §3), for every HackerOS language:
  each process has its own `LANG`.
- An app installed by `hackpkg` or the Hack Store did not get its own:
  `HackerOsPack` packed the declared assemblies only, `AppPackage` accepted
  `lib/*.dll` only, and the OS, which reads an installed app's code from the
  virtual file system (`LoadFromStream`), loaded no satellite. An installed app
  stayed in English.

## Decision

### 1. Package format

- The satellite of an assembly the manifest declares (entry point or
  component) is stored at `lib/<culture>/<assembly>.resources.dll`. Its SHA-256
  is listed in `hackeros-package.json`, like every file of the package
  (ADR 0182 §1); the manifest does not change.
- `<culture>` is a BCP 47 name made of letters, digits and dashes (`fr`,
  `fr-CA`, `zh-Hant`), 20 characters at most.
- `AppPackage` refuses (`apps.package.unexpected-file`) a satellite that
  translates no declared assembly: the SDK's come with the OS, and a package
  does not ship another one's translations.
- `AppPackage.Assemblies` now lists `lib/*.dll` only;
  `AppPackage.SatelliteAssemblies` lists the satellites.

### 2. `HackerOsPack` adds the app's satellites

- The `pack` tool looks, in the output folder (`--bin`), for the satellite of
  each declared assembly in each culture subfolder (`PackagedSatellites`). The
  satellites of the SDK and of referenced libraries, copied there by the build,
  stay out.

### 3. The OS loads the satellites before the first render

- `InstalledAppAssemblyTransport` reads, before loading an installed app's
  assembly, `lib/<culture>/<assembly>.resources.dll` for each HackerOS language
  (`HackLocale.SupportedLanguages`, except neutral English), and loads it into
  the default context (`InstalledAppSatellites`). A culture missing from the
  package is skipped: the app shows its neutral texts.
- An `AssemblyLoadContext.Default.Resolving` handler answers the runtime's
  request (`<assembly>.resources, Culture=fr`) with the loaded satellite,
  whether or not the runtime finds an assembly loaded from a stream by name.
- Web Workers are not concerned: an installed app's code runs on the page only
  (workers load build-known assemblies only).

## Consequences

- An installed app is translated as if it were compiled into the OS, without
  declaring anything.
- **Compatibility:** an OS 2.2.0 or older refuses a package that holds
  `lib/fr/...` (`apps.package.unexpected-file`). Such a package is compiled
  with the SDK of the version that brings this ADR, which its
  `sdkCompatibility` already requires (ADR 0098 §3): it would not install on
  that OS either. An app with no per-culture `.resx` gives the same package as
  before.
- The catalog's CI (`PackageCheck` of the HackerOs-AppMarketPlace repository)
  reads packages with `HackerOs.Platform.Core`: it must move to that version to
  accept translated packages.
- Opening an installed app's first window reads one more file per language
  (`fr` today), even when absent; the cost is one IndexedDB read.

## Questions

Asked to Benoit in the conversation on 2026-10-31; answers to be recorded here.

1. **Which languages to load?** A (recommended): every HackerOS language, like
   compiled apps, because each process has its `LANG`. B: only the session's
   language at launch; a window opened later in another language stays in
   English.
2. **Which satellites to pack?** A (recommended): every one the build writes
   for the app's assemblies, even for a language HackerOS does not speak yet
   (ready for a language added later). B: only those of HackerOS's languages
   (`fr` today), for a smaller package.

## Tasks

- [x] ADR (fr/en), index, memory.
- [x] `AppPackage`: `lib/<culture>/<declared assembly>.resources.dll` accepted and checked, `SatelliteAssemblies`, `TryParseSatellitePath`.
- [x] `AppPackageWriter.Write`: satellites; `pack` tool: `PackagedSatellites`.
- [x] `InstalledApp.GetSatellitePath`, `InstalledAppSatellites`, `InstalledAppAssemblyTransport.Satellites`.
- [x] Unit tests (`AppPackageTests`, `AppPackageWriterTests`, `PackagedSatellitesTests`, `InstalledAppSatellitesTests`).
- [x] Test package `Tests/fixtures/installed-app` with `Resources/ProbeStrings.fr.resx`; end-to-end test in `HackerOs.TrimmedPublish.E2E.Tests`.
- [x] Docs `app-installation.md`, `localization.md`, `app-repository.md`, `features.md`, change entry.
- [ ] HackerOs-AppMarketPlace: move `PackageCheck` to the version that brings this ADR (once released).
