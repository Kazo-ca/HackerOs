> [Version française](../../../fr/wasm/framework/app-repository.md)

# App repository: the `kazoos-solution` template

## Purpose

The starting point of a Kazo OS app in its own repository: a solution that
runs in the OS under the debugger, whose Kazo OS version is set by a
submodule, and that is published to the Kazo OS Store with a tag. Decisions:
[ADR 0176](../dev/adr/0176-app-repository-template.md),
[ADR 0188](../dev/adr/0188-hack-store-and-app-catalog.md) §7,
[ADR 0207](../dev/adr/0207-solution-template-implementation.md).
The general guide for a repository that uses Kazo OS is
[`building-on-kazoos.md`](building-on-kazoos.md).

## Creating the repository

Two ways, same result:

- **From GitHub**: "Use this template" on
  [`Kazo-ca/Os-AppTemplate`](https://github.com/Kazo-ca/Os-AppTemplate),
  then `git clone --recurse-submodules`. The submodule is already there.
- **From the command line or Visual Studio**:

  ```bash
  dotnet new install Kazo.Os.Templates
  dotnet new kazoos-solution -n Contoso.Notes --appId com.contoso.notes \
      --publisherId pub.contoso --repository contoso/Os-App-Notes
  cd Contoso.Notes
  git init
  git submodule add --depth 1 https://github.com/Kazo-ca/Os.git external/KazoOs
  git -C external/KazoOs checkout v<Kazo OS version>
  ```

  `dotnet new` cannot create the submodule link; without it, the build stops
  on `HOSAPP0070` with the command to run. In Visual Studio (New project) or
  Rider, the template is called "Kazo OS app solution".

## What is generated

| Path | Content |
| --- | --- |
| `<Name>.sln` | the solution (`src` and `tests` folders) |
| `src/<Name>/` | the window app; its `[AppManifest]` class (with `Version`) generates `manifest.json` |
| `src/<Name>.Os/` | the host: the whole Kazo OS, the base apps and the app; the project to start |
| `tests/<Name>.Tests/` | xUnit tests of the app |
| `Directory.Build.props` | submodule root, profile, import of `Versions.props` |
| `Directory.Build.targets` | import of the external source mode, submodule check |
| `.gitmodules` | `external/KazoOs`, shallow clone |
| `store/` | `icon.png` (256×256), `short_description.{en,fr}.txt`, `description.{en,fr}.md`, `store.json`, optional `screenshots/` |
| `.github/workflows/ci.yml` | CI: submodule checked, build and tests in `Release` |
| `.github/workflows/publish-to-kazoos-store.yml` | submission to the catalog on a `v*` tag |
| `.github/workflows/publish-packages.yml` | signed package pushed to the `kazoos-packages` branch on a `v*` tag ([ADR 0219](../dev/adr/0219-app-authors-signing-tool.md)) |
| `README.md`, `readme-fr.md` | these steps, for the created repository |

## The Kazo OS version

`external/KazoOs/wasm2/KazoOs/build/Versions.props` is the repository's
version: `Directory.Build.props` imports it, and every `Kazo.Os.*` reference
uses `$(KazoOsVersion)`. Changing the version means moving the submodule to
another release tag and committing that change. The submodule is never edited:
CI refuses a submodule that is modified or off its tag
(`.github/scripts/check-kazoos-submodule.sh`).

## The two profiles

| Profile | Kazo OS comes from | Restore output |
| --- | --- | --- |
| `Debug` (`KazoOsUseSource=true`) | the projects of the submodule: step into the SDK and the OS, breakpoints anywhere | `obj/source/` |
| `Release` (`KazoOsUseSource=false`) | the `Kazo.Os.*` packages of nuget.org, at the submodule's version | `obj/` |

`-p:KazoOsUseSource=true` or `false` forces either one;
`-p:KazoOsSourceRoot=<folder>` points at another copy of Kazo OS (the folder
of `Kazo.Os.sln`). The first `Debug` build compiles the whole OS.

### How the `Debug` profile works

`build/Kazo.Os.ExternalSource.targets` (in the Kazo OS repository) is
imported from the submodule. It:

1. finds the projects that can be packages: `Shared/*`, `Platform/*`, `OS/*`,
   `Infrastructure/*`, `Server/*`, `Tools/*` and `Apps/*/*` (a package = the
   project of the same name);
2. replaces every `PackageReference` to one of them with a `ProjectReference`,
   keeping `IsSystemApp` for the host's base apps;
3. removes `Kazo.Os.App.Build` and imports `Kazo.Os.Apps.targets` for the
   projects that declare `KazoOsAppProject` or `KazoOsAppHost`: the same
   manifest generator, analyzers and host aggregation as in the Kazo OS
   repository (`KazoOsExternalSource=true`).

The SDK boundary (`HOSAPP0030`) checks the references that enter the
submodule; a project of your repository may reference another one. An app
added with `dotnet new kazoos-window` (or `-command`, `-service`, `-site`)
already declares `KazoOsAppProject=true`; add it to the host with
`dotnet add src/<Name>.Os reference …`.

## Publishing to the Kazo OS Store

1. Fill `store/`: 256×256 PNG icon (200 KB at most), English short description
   of 120 characters at most (French is optional), descriptions, and in
   `store.json` the category (closed list of the catalog), tags and license.
   `repository` comes from `--repository`; `project` names the app's `.csproj`,
   which the catalog builds (at the root, `KazoOsPack` would also run on the
   tests project, which has no such target).
2. Change `Version` in the `[AppManifest]` class, commit, create the tag
   `v<version>` and push it.
3. The **Publish to the Kazo OS Store** workflow checks the submodule, builds the
   app in `Release`, packs it (`KazoOsPack`), refuses a tag that is not the
   manifest version and produces `apps/<appId>_<version>/` (content of
   `store/`, the **package's** `manifest.json`, `store.json` with the tag's
   commit), left as a workflow artifact. The package's manifest is the generated
   manifest plus every static resource `KazoOsPack` added to it
   ([ADR 0221](../dev/adr/0221-pack-all-static-web-assets-of-an-app.md)): the one
   the catalog compares with the package its CI builds. The package also holds
   the app's satellite assemblies (its per-culture `.resx` files,
   [ADR 0222](../dev/adr/0222-installed-app-satellite-assemblies.md)).
4. With the `HACK_STORE_TOKEN` secret (an author token allowed to fork and open
   a pull request), the workflow opens the pull request to
   `Kazo-ca/Os-AppMarketPlace` from your fork. Without it, download the
   artifact and open the pull request yourself.

The catalog never takes a binary: after review and merge, its CI compiles the
app at the given commit (ADR 0188 §4 to §6).

### The `HACK_STORE_TOKEN` secret

It is a GitHub personal access token of **the app's author**: neither Kazo OS
nor the catalog provides it. With it, the workflow reads the author's name,
forks `Kazo-ca/Os-AppMarketPlace` to the author's account, pushes the
`<appId>_<version>` branch there and opens the pull request to the catalog.

Creating the token (a "classic" token):

1. GitHub ▸ profile picture ▸ **Settings** ▸ **Developer settings** ▸
   **Personal access tokens** ▸ **Tokens (classic)** ▸ **Generate new token
   (classic)**.
2. *Note*: for example `<repository> → Kazo OS Store`; *Expiration*: short (90
   days for example), renewed when it ends.
3. Scopes: **`repo`** (fork, push, open the pull request) and **`workflow`**
   (GitHub refuses a push that brings to the fork changes of the catalog's
   `.github/workflows` the fork does not have yet).
4. **Generate token**, then copy the token: it is shown only once.

Adding it to the app's repository: **Settings** ▸ **Secrets and variables** ▸
**Actions** ▸ **New repository secret**, name `HACK_STORE_TOKEN`. Or from the
command line:

```bash
gh secret set HACK_STORE_TOKEN --repo <owner>/<repository>
```

- **Why "classic"**: a fine-grained token is valid for one owner only (the
  author's account **or** an organization), while the workflow touches the
  author's fork and the catalog's repository. If the organization of the app's
  repository forbids classic tokens, allow them in its settings (*Personal
  access tokens*).
- **Caution**: `repo` gives access to all the author's repositories. Keep a
  short expiration, put the token only in this secret, revoke it at the
  slightest doubt.
- **Without the secret**, nothing blocks: the `apps/<appId>_<version>/` folder
  stays as a workflow artifact, and the author opens the pull request.
- Not to be confused with `HACKEROS_APP_SIGNING_KEY` (next section), a key the
  Kazo OS tooling generates to sign packages.

## Publishing on `kazoos-packages` and signing

[ADR 0219](../dev/adr/0219-app-authors-signing-tool.md). The same `v*` tag runs
**Publish the Kazo OS packages** (`publish-packages.yml`, the only workflow
with `contents: write`): it fetches the `kazoos-packages` branch (orphan the
first time), packs the app there (`KazoOsPack`, `KazoOsPackageOutput`),
keeps the versions already published and their signatures, then pushes the
branch. `kazopkg install <owner>/<repo>` then installs the app without the
Store.

To sign:

1. Once, outside the repository:
   `dotnet build src/<Name> -t:KazoOsKeyGen -p:KazoOsSigningKeyFile=$HOME/kazoos-signing-key.pem`.
   The private key (PKCS#8, mode `0600`) goes to that new file, never replaced;
   the public key and the `github.com/<owner>=…` line are printed. `*.pem` is in
   the template's `.gitignore`.
2. Put the content of the file in the repository's `HACKEROS_APP_SIGNING_KEY`
   secret, then keep the file offline.
3. Publish the public key; an administrator adds it to `[signing-keys]` of
   `/etc/kazoos/app-sources.config`. From then on, an unsigned or badly signed
   package from this repository is refused.

Without the secret, the package goes out unsigned (SHA-256 only) with a
warning. Locally: `HACKEROS_APP_SIGNING_KEY` variable or
`-p:KazoOsSigningKeyFile=<file.pem>` on `KazoOsPack`. It needs a Kazo OS
version that contains ADR 0219 (2.2.1 or later).

## The GitHub template repository

[`Kazo-ca/Os-AppTemplate`](https://github.com/Kazo-ca/Os-AppTemplate)
is the output of `dotnet new kazoos-solution -n Kazo.Os.App` plus the
submodule link. It is regenerated from the template, never edited by hand. It
can only work with a submodule on a release that contains this template
(2.2.0 or later, ADR 0207 §11).

## Tasks

- [x] External source mode (`Kazo.Os.ExternalSource.targets`, `Kazo.Os.Apps.targets`).
- [x] `kazoos-solution` template, CI and publishing workflow.
- [x] `ide.host.json` of the templates.
- [x] Test of both profiles (`Kazo.Os.Templates.E2E.Tests`).
- [ ] Template repository regenerated on the release that contains this template.
- [x] `publish-packages.yml` workflow and package signing (ADR 0219).
