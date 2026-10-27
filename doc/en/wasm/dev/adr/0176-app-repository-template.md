> [Version française](../../../../fr/wasm/dev/adr/0176-app-repository-template.md)

# ADR 0176: app repository template, submodule for debugging and packages for production

## Status

Accepted on 2026-10-27 by Benoit, after the evaluation he asked for the same
day ("a project and a git submodule pointing to the main repository to help
debugging, using the NuGet packages for production"). His answers: a template
repository with a mandatory submodule; the OS is not modified while developing
an app, the submodule is there to control deployment and to launch the app when
debugging; packages in production and the submodule's source when debugging
("both, depending on the profile"); Visual Studio through the `dotnet new`
templates, with no VSIX extension. Complements [ADR 0098](0098-sdk-versions-packages-and-manifest-checks.md)
(packages, templates, package mode) and [ADR 0173](0173-automatic-releases-from-the-version-file.md)
(publishing to nuget.org). This ADR writes no code.

## Context

- `HackerOs.Templates` holds five **project** templates (`hackeros-window`,
  `-command`, `-service`, `-site`, `-host`). They go to nuget.org with every
  release, along with the 41 other packages, their symbols (`.snupkg`) and
  Source Link.
- A third-party repository follows [`building-on-hackeros.md`](../../framework/building-on-hackeros.md):
  it assembles its own solution. That guide still says the packages are never
  published to nuget.org, which is wrong since 2.1.0.
- `build/HackerOs.Apps.targets` has two modes: the repository (project
  references, manifest generator built in the solution) and package mode
  (`HackerOsBuildFromPackage=true`, everything comes from `HackerOs.App.Build`).
- Benoit wants a ready-to-use starting point: a solution that launches in the OS
  when debugging, whose deployment is controlled by the OS version it pins.

## Decision

1. **The template's source stays in the main repository.** A new solution
   template `hackeros-solution` (type `solution`) in `HackerOs.Templates`
   generates: `.sln`, a host (`src/<Name>.Os`, the `hackeros-host` one), a window
   app, a test project, `Directory.Build.props`, `global.json`, `.gitignore`,
   `.gitmodules`, a CI workflow and a `README.md`. It ships with the other
   templates, at the release's version.
2. **GitHub template repository.** A repository in the Kazo-ca organization,
   marked "Template repository", holds the output of `dotnet new hackeros-solution`.
   It is regenerated from the template, never edited by hand anywhere but in the
   template.
3. **Mandatory submodule.** `external/HackerOs` points to the main repository, at
   a release tag `v<version>`, as a shallow clone (`shallow = true` in
   `.gitmodules`). Its `build/Versions.props` is **the** version of the app
   repository: `Directory.Build.props` imports it instead of writing
   `HackerOsVersion`. Changing the OS version means moving the submodule to
   another tag. If the submodule is missing, the build fails with a message
   giving `git submodule update --init`.
4. **Two build profiles.**
   - **Production** (`Release`, CI, `dotnet publish`): `PackageReference` to the
     `HackerOs.*` packages from nuget.org, at the submodule's version. This is
     today's package mode, unchanged.
   - **Debugging** (`Debug`): the same references become `ProjectReference` items
     to the submodule's projects (SDK, `HackerOs.Ecosystem.Host`, the host's base
     apps, manifest generator). You can step into the OS code and set breakpoints
     in it.
   - The `HackerOsUseSource` property picks the profile (true by default in
     `Debug`, false otherwise); it can be forced either way.
5. **Third mode of `HackerOs.Apps.targets`: external source.** Turned on by
   `HackerOsUseSource=true` outside the main repository. The generator comes from
   the submodule's `Tools` project; the boundary rule on `ProjectReference` items
   (`HOSAPP0030`) is computed from `HackerOsSourceRoot` (by default
   `external/HackerOs/wasm2/HackerOs`) instead of the repository's paths. The
   inspections and `HOSAPP*` codes stay the same.
6. **The OS is not modified from an app repository.** The submodule is read-only
   by convention. The template's CI checks that the submodule is clean and exactly
   on tag `v$(HackerOsVersion)`, then builds in `Release`: a local change to the OS
   cannot reach production.
7. **Visual Studio.** Each template gets a `.template.config/ide.host.json`
   (icon, labels of the `appId` and `publisherId` parameters). Visual Studio and
   Rider list the templates installed by `dotnet new install HackerOs.Templates`.
   No VSIX extension.
8. **Tested in the main repository.** `HackerOs.Templates.E2E.Tests` generates a
   solution with `hackeros-solution`, points `HackerOsSourceRoot` at the
   repository itself, and builds both profiles: `Debug` (external source) and
   `Release` (local package feed).

## Consequences

- The main repository stays the only source of the templates; the template
  repository is only a generated copy.
- A third build mode has to be maintained in `HackerOs.Apps.targets`; the test in
  point 8 guards it.
- `Debug` and `Release` do not build the OS the same way. Since both come from
  the same tag, the expected behavior is identical; CI tests `Release`, which is
  what ships.
- Cloning the app repository needs `--recurse-submodules`; the shallow clone
  keeps the submodule small.
- The host's list of base apps exists in two forms (packages and projects); the
  host template keeps them side by side and the test in point 8 checks they match.

## Tasks

- [ ] Fix `building-on-hackeros.md` (fr and en): packages and templates on nuget.org.
- [ ] `ide.host.json` for the five existing templates.
- [ ] External source mode in `HackerOs.Apps.targets` (`HackerOsUseSource`, `HackerOsSourceRoot`).
- [ ] `hackeros-solution` template (submodule, `Versions.props` import, profiles, CI).
- [ ] Test of both profiles in `HackerOs.Templates.E2E.Tests`.
- [ ] Create the template repository from the template (Benoit creates the repository in Kazo-ca).
