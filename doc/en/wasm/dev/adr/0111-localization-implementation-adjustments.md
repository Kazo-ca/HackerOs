> [Version française](../../../../fr/wasm/dev/adr/0111-localization-implementation-adjustments.md)

# ADR 0111: implementation adjustments for localization

## Status

Accepted on 2026-10-17 (work item `v3/12-localization`).

- **Complements:** [ADR 0104](0104-localization.md). Replaces a word of its §4
  ("the asset `localization/fr.json`") and clarifies its §2 and §3.
- Number: `0109` may be taken by a parallel work item; this work item
  takes `0111`, reserved for it.

## Context

The implementation of ADR 0104 ran into five points that the ADR does not settle,
or that it describes in a way that does not work as written.

## Decision

1. **Loading satellites.** The startup option
   `loadAllSatelliteResources` downloads the satellite assemblies but
   `Assembly.GetSatelliteAssembly` does not find them for a lazily loaded app
   (`FileNotFoundException`, verified in Chromium). HackerOS therefore
   calls the runtime function `INTERNAL.loadSatelliteAssemblies(["fr"])`
   through `[JSImport]` from C#, like Blazor's `WebAssemblyCultureProvider`:
   at host startup (`BrowserSatelliteAssemblies`, Infrastructure.Browser)
   and once in each Web Worker (`BrowserWorkerAssemblyLoader`). No
   JavaScript file is added. The hosts also declare
   `SatelliteResourceLanguages=en;fr` and the ICU data `icudt_EFIGS.dat`
   (`Directory.Build.props`) for the `fr-CA`, `fr-FR`, `en-GB` formats.
2. **Manifest texts embedded, not assets.** `localization/<culture>.json`
   is produced and referenced by `localizations`, as ADR 0104 §4 says,
   but is **not** declared in the manifest's `assets`: the aggregator copies
   its texts into the optional `localized` property of
   `BuiltInApplications.json`, and the catalog reads them from there
   (`AppCatalog.Localize`). The launcher therefore has nothing to download, and the asset
   hash (ADR 0098) is not touched. An app installed later (runtime
   installation plan) will read the same files from its package.
3. **Projects with several apps.** In a project that declares several apps
   (the commands), the keys of `AppManifest.<culture>.resx` are prefixed with
   the id (`org.hackeros.commands.ls.Description`) and the files go in
   `localization/<appId>/<culture>.json`. A missing key keeps the English
   text; an unknown key is the error `HOSAPP0040`.
4. **Platform language.** The platform components receive
   `IStringLocalizer<T>` by injection, bound to `SessionLocaleService` (system
   language before login, user's after), and never to
   `CultureInfo.CurrentUICulture`. `AddHackerOsPlatformLocalization` replaces
   the registration of `IStringLocalizer<>` (even after a library's
   `AddLocalization()`). MudBlazor keeps its own localization.
5. **`localectl set-locale`.** The right to write `/etc/locale.conf` comes from the
   file permissions (`root`, `0644`): `sudo localectl set-locale …`
   succeeds, a user receives `Access denied (try sudo)`. An unknown locale
   (outside `localectl list-locales`) is rejected; `LANGUAGE` is not
   checked, as under systemd.

## Consequences

- [`localization.md`](../../framework/localization.md) describes these points;
  [`app-model.md`](../../framework/app-model.md) lists `HOSAPP0040` and the
  `localized` property.
- A third-party app translates its name through `Resources/AppManifest.fr.resx`, its
  texts through `Resources/Strings*.resx` and `AppContext.GetLocalizer<T>()`.
