> [Version française](../../../fr/wasm/framework/app-repository.md)

# App repository: the `hackeros-solution` template

## Purpose

The starting point of a HackerOS app in its own repository: a solution that
runs in the OS under the debugger, whose HackerOS version is set by a
submodule, and that is published to the Hack Store with a tag. Decisions:
[ADR 0176](../dev/adr/0176-app-repository-template.md),
[ADR 0188](../dev/adr/0188-hack-store-and-app-catalog.md) §7,
[ADR 0207](../dev/adr/0207-solution-template-implementation.md).
The general guide for a repository that uses HackerOS is
[`building-on-hackeros.md`](building-on-hackeros.md).

## Creating the repository

Two ways, same result:

- **From GitHub**: "Use this template" on
  [`Kazo-ca/HackerOs-AppTemplate`](https://github.com/Kazo-ca/HackerOs-AppTemplate),
  then `git clone --recurse-submodules`. The submodule is already there.
- **From the command line or Visual Studio**:

  ```bash
  dotnet new install HackerOs.Templates
  dotnet new hackeros-solution -n Contoso.Notes --appId com.contoso.notes \
      --publisherId pub.contoso --repository contoso/HackerOs-App-Notes
  cd Contoso.Notes
  git init
  git submodule add --depth 1 https://github.com/Kazo-ca/HackerOs.git external/HackerOs
  git -C external/HackerOs checkout v<HackerOS version>
  ```

  `dotnet new` cannot create the submodule link; without it, the build stops
  on `HOSAPP0070` with the command to run. In Visual Studio (New project) or
  Rider, the template is called "HackerOS app solution".

## What is generated

| Path | Content |
| --- | --- |
| `<Name>.sln` | the solution (`src` and `tests` folders) |
| `src/<Name>/` | the window app; its `[AppManifest]` class (with `Version`) generates `manifest.json` |
| `src/<Name>.Os/` | the host: the whole HackerOS, the base apps and the app; the project to start |
| `tests/<Name>.Tests/` | xUnit tests of the app |
| `Directory.Build.props` | submodule root, profile, import of `Versions.props` |
| `Directory.Build.targets` | import of the external source mode, submodule check |
| `.gitmodules` | `external/HackerOs`, shallow clone |
| `store/` | `icon.png` (256×256), `short_description.{en,fr}.txt`, `description.{en,fr}.md`, `store.json`, optional `screenshots/` |
| `.github/workflows/ci.yml` | CI: submodule checked, build and tests in `Release` |
| `.github/workflows/publish-to-hack-store.yml` | submission to the catalog on a `v*` tag |
| `README.md`, `readme-fr.md` | these steps, for the created repository |

## The HackerOS version

`external/HackerOs/wasm2/HackerOs/build/Versions.props` is the repository's
version: `Directory.Build.props` imports it, and every `HackerOs.*` reference
uses `$(HackerOsVersion)`. Changing the version means moving the submodule to
another release tag and committing that change. The submodule is never edited:
CI refuses a submodule that is modified or off its tag
(`.github/scripts/check-hackeros-submodule.sh`).

## The two profiles

| Profile | HackerOS comes from | Restore output |
| --- | --- | --- |
| `Debug` (`HackerOsUseSource=true`) | the projects of the submodule: step into the SDK and the OS, breakpoints anywhere | `obj/source/` |
| `Release` (`HackerOsUseSource=false`) | the `HackerOs.*` packages of nuget.org, at the submodule's version | `obj/` |

`-p:HackerOsUseSource=true` or `false` forces either one;
`-p:HackerOsSourceRoot=<folder>` points at another copy of HackerOS (the folder
of `HackerOs.sln`). The first `Debug` build compiles the whole OS.

### How the `Debug` profile works

`build/HackerOs.ExternalSource.targets` (in the HackerOS repository) is
imported from the submodule. It:

1. finds the projects that can be packages: `Shared/*`, `Platform/*`, `OS/*`,
   `Infrastructure/*`, `Server/*`, `Tools/*` and `Apps/*/*` (a package = the
   project of the same name);
2. replaces every `PackageReference` to one of them with a `ProjectReference`,
   keeping `IsSystemApp` for the host's base apps;
3. removes `HackerOs.App.Build` and imports `HackerOs.Apps.targets` for the
   projects that declare `HackerOsAppProject` or `HackerOsAppHost`: the same
   manifest generator, analyzers and host aggregation as in the HackerOS
   repository (`HackerOsExternalSource=true`).

The SDK boundary (`HOSAPP0030`) checks the references that enter the
submodule; a project of your repository may reference another one. An app
added with `dotnet new hackeros-window` (or `-command`, `-service`, `-site`)
already declares `HackerOsAppProject=true`; add it to the host with
`dotnet add src/<Name>.Os reference …`.

## Publishing to the Hack Store

1. Fill `store/`: 256×256 PNG icon (200 KB at most), English short description
   of 120 characters at most (French is optional), descriptions, and in
   `store.json` the category (closed list of the catalog), tags and license.
   `repository` comes from `--repository`.
2. Change `Version` in the `[AppManifest]` class, commit, create the tag
   `v<version>` and push it.
3. The **Publish to the Hack Store** workflow checks the submodule, builds the
   app in `Release`, packs it (`HackerOsPack`), refuses a tag that is not the
   manifest version and produces `apps/<appId>_<version>/` (content of
   `store/`, generated `manifest.json`, `store.json` with the tag's commit),
   left as a workflow artifact.
4. With the `HACK_STORE_TOKEN` secret (an author token allowed to fork and open
   a pull request), the workflow opens the pull request to
   `Kazo-ca/HackerOs-AppMarketPlace` from your fork. Without it, download the
   artifact and open the pull request yourself.

The catalog never takes a binary: after review and merge, its CI compiles the
app at the given commit (ADR 0188 §4 to §6).

## The GitHub template repository

[`Kazo-ca/HackerOs-AppTemplate`](https://github.com/Kazo-ca/HackerOs-AppTemplate)
is the output of `dotnet new hackeros-solution -n HackerOs.App` plus the
submodule link. It is regenerated from the template, never edited by hand. It
can only work with a submodule on a release that contains this template
(2.2.0 or later, ADR 0207 §11).

## Tasks

- [x] External source mode (`HackerOs.ExternalSource.targets`, `HackerOs.Apps.targets`).
- [x] `hackeros-solution` template, CI and publishing workflow.
- [x] `ide.host.json` of the templates.
- [x] Test of both profiles (`HackerOs.Templates.E2E.Tests`).
- [ ] Template repository regenerated on the release that contains this template.
