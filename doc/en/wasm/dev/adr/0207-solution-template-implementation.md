> [Version française](../../../../fr/wasm/dev/adr/0207-solution-template-implementation.md)

# ADR 0207: `hackeros-solution` template, implementation adjustments

## Status

Accepted on 2026-10-30, work item `todo/1-hackstore-solution-template` (`TODO`
issue #246, work item 1 of the Hack Store plan). Implements
[ADR 0176](0176-app-repository-template.md) and §7 of
[ADR 0188](0188-hack-store-and-app-catalog.md). It changes none of their
decisions; it settles what they left open and replaces one consequence of
ADR 0176 (two lists of the base apps) with a single list.

## Context

ADR 0176 decides a solution template, a mandatory HackerOS submodule, two
profiles (sources in `Debug`, packages in `Release`) and a third mode of
`HackerOs.Apps.targets`. ADR 0188 §7 adds a workflow to the template that
submits the app to the catalog on a tag. What remained: how package references
become projects, where that mode lives, how the submodule reaches a folder made
by `dotnet new`, and the details of the workflow.

## Decision

1. **Template content.** `dotnet new hackeros-solution -n <Name>` creates:
   `<Name>.sln`; `src/<Name>` (window app); `src/<Name>.Os` (the host, the base
   apps and the app, with `launchSettings.json` to debug in the browser);
   `tests/<Name>.Tests` (xUnit); `Directory.Build.props`,
   `Directory.Build.targets`, `global.json`, `.gitignore`, `.gitmodules`
   (`external/HackerOs`, `shallow = true`); `README.md` and `readme-fr.md`;
   `store/` (256×256 icon, short and long descriptions in English and French,
   `store.json`); `.github/workflows/ci.yml`,
   `.github/workflows/publish-to-hack-store.yml` and
   `.github/scripts/check-hackeros-submodule.sh`. Parameters: `--appId`,
   `--publisherId` and `--repository` (`owner/name`, written in `store.json`).
2. **A `build/HackerOs.ExternalSource.targets` file in the main repository.**
   The template's `Directory.Build.targets` imports it from the submodule when
   `HackerOsUseSource` is true. It replaces every `PackageReference` to a
   HackerOS package with a `ProjectReference` to the project of the same name
   in the submodule (the `IsSystemApp` metadata follows), removes
   `HackerOs.App.Build` and imports `HackerOs.Apps.targets` in its place for
   app projects (`HackerOsAppProject=true`) and hosts (`HackerOsAppHost=true`).
   **No list to maintain**: a HackerOS package is the project of the same name,
   one folder below `Shared/`, `Platform/`, `OS/`, `Infrastructure/`,
   `Server/` or `Tools/`, or two folders below `Apps/`. The list of base apps
   therefore exists once, as packages, in the host.
3. **SDK boundary in external source mode.** `HackerOs.Apps.targets` receives
   `HackerOsExternalSource=true`. The HackerOS root is the submodule's (the file
   is imported from there), hence `HackerOsSourceRoot`; the `HOSAPP0030` rule
   checks the references that **enter** the submodule. A reference to another
   project of the app's repository (outside the submodule) is that
   repository's business, as in package mode. An app outside the main
   repository gets `IsTrimmable` and the trim analysis, as in package mode.
4. **App templates write `HackerOsAppProject=true`.** MSBuild cannot decide at
   evaluation that a project is an app because it references
   `HackerOs.App.Build`. The five app templates (`window`, `command`,
   `service`, `site` and the solution's app) therefore declare the property; it
   changes nothing in package mode.
5. **One restore output per profile.** In `Debug` (sources),
   `BaseIntermediateOutputPath` is `obj/source/`; in `Release`, `obj/`.
   Switching configuration in Visual Studio never reads the other profile's
   references. `obj/**` is excluded from the default items.
6. **The submodule.** `dotnet new` cannot create a git submodule link: the
   generated `README.md` gives `git submodule add --depth 1 … external/HackerOs`
   then `git -C external/HackerOs checkout v<version>`. The GitHub template
   repository contains the link. Without the submodule the build stops on
   **`HOSAPP0070`** with the command `git submodule update --init`.
   `HackerOsSourceRoot` (by default `external/HackerOs/wasm2/HackerOs/`) can
   point elsewhere; the test of point 9 points it at the repository itself.
7. **Template CI.** `check-hackeros-submodule.sh` refuses a submodule that is
   modified or not exactly on the `v<version>` tag of its `Versions.props`;
   then `dotnet build -c Release` and `dotnet test`.
8. **Publishing workflow (ADR 0188 §7).** On a `v*` tag (or by hand): the same
   submodule check, `dotnet build -c Release -t:HackerOsPack` of the app,
   refusal if the tag is not the `Version` of the generated manifest, then the
   folder `apps/<appId>_<version>/`: `icon.png`, `short_description.*.txt`,
   `description.*.md`, `screenshots/` when present, the `manifest.json` of the
   Release build and `store.json` completed with the tag's commit. The folder
   is always left as a workflow artifact. With the author's
   **`HACK_STORE_TOKEN`** secret, the workflow creates the author's fork of
   `Kazo-ca/HackerOs-AppMarketPlace`, pushes a `<appId>_<version>` branch there
   and opens the pull request. The template's app declares its `Version` in its
   `[AppManifest]` class: change it, then tag.
9. **Test.** `HackerOs.Templates.E2E.Tests` generates a solution, checks
   `HOSAPP0070` without the submodule, then, with `HackerOsSourceRoot` pointed
   at the repository: build and tests in `Debug` (every HackerOS reference is a
   project, the host aggregates the app and the system apps), build and tests
   in `Release` (every one is a package of the local feed), and `HackerOsPack`
   as the workflow runs it.
10. **Visual Studio and Rider.** The six templates have a
    `.template.config/ide.host.json` (order, parameter labels) and a 32×32
    icon.
11. **The `Kazo-ca/HackerOs-AppTemplate` template repository.** It receives
    the output of `dotnet new hackeros-solution` (app `HackerOs.App`,
    `--appId com.example.my-app`) and the submodule link. External source mode
    and `HackerOsPack` only exist in a version released **after** this work
    item: until a release tag contains them, the template repository's
    submodule points at the tip of `main` that contains them and its CI fails
    on the tag check. The template repository's PR stays a draft until that
    release; Benoit ticks "Template repository" in the GitHub settings.

## Consequences

- The main repository carries one more build file,
  `HackerOs.ExternalSource.targets`, covered by the test of point 9.
- Renaming a published project, or moving it out of the folders listed in
  point 2, breaks the `Debug` profile of app repositories: the test of point 9
  catches it for the packages the template references.
- The `Debug` profile compiles the whole OS (a first build of about two
  minutes); later builds are incremental.
- An app repository created with a version that predates this work item has no
  `Debug` profile: it gets one by regenerating its build files from the
  template.

## Tasks

- [x] `build/HackerOs.ExternalSource.targets` and the external source mode of `HackerOs.Apps.targets`.
- [x] `hackeros-solution` template (submodule, profiles, CI, publishing workflow, `store/`).
- [x] `ide.host.json` and icon of the six templates; `HackerOsAppProject=true` in the app templates.
- [x] Test of both profiles in `HackerOs.Templates.E2E.Tests`.
- [x] `building-on-hackeros.md` fixed (packages on nuget.org), [`app-repository.md`](../../framework/app-repository.md).
- [ ] Template repository: draft PR, finished after the release that contains this work item; "Template repository" ticked by Benoit.
