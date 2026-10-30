> [Version française](readme-fr.md)

# HackerOs.Template.Solution

A HackerOS app repository, made with the `hackeros-solution` template.

| Folder | Content |
| --- | --- |
| `src/HackerOs.Template.Solution` | The app (window app, its `[AppManifest]` class generates `manifest.json`) |
| `src/HackerOs.Template.Solution.Os` | A host that runs the whole HackerOS with the base apps and this app: the project to start |
| `tests/HackerOs.Template.Solution.Tests` | Tests of the app |
| `external/HackerOs` | The HackerOS repository as a git submodule, on a release tag |
| `store/` | What the Hack Store shows: icon (PNG 256×256), short descriptions, descriptions, `store.json` |

## First steps

```bash
# A repository created from the GitHub template already has the submodule:
git clone --recurse-submodules <your repository>

# A folder made with "dotnet new hackeros-solution" adds it once:
git init
git submodule add --depth 1 https://github.com/Kazo-ca/HackerOs.git external/HackerOs
git -C external/HackerOs checkout v<HackerOS version>
```

Then open the `.sln` in Visual Studio or Rider and start `HackerOs.Template.Solution.Os`, or run
`dotnet run --project src/HackerOs.Template.Solution.Os`.

## The HackerOS version

`external/HackerOs/wasm2/HackerOs/build/Versions.props` is the HackerOS version of this repository.
To move to another version, move the submodule to its tag and commit it:

```bash
git -C external/HackerOs fetch --depth 1 origin tag v2.2.0
git -C external/HackerOs checkout v2.2.0
git add external/HackerOs
```

Never edit the submodule: the OS is not modified from an app repository, and CI refuses a
submodule with local changes or off its tag.

## Two build profiles

| Profile | HackerOS comes from | Use |
| --- | --- | --- |
| `Debug` | the projects of the submodule (`HackerOsUseSource=true`) | step into the SDK and the OS, breakpoints anywhere |
| `Release` | the `HackerOs.*` packages of nuget.org, at the submodule's version | CI, `dotnet publish`, the Hack Store |

Force either one with `-p:HackerOsUseSource=true` or `false`.

## Publishing to the Hack Store

1. Fill `store/` (icon, short descriptions, descriptions, category and tags in `store.json`).
2. Change `Version` in the app's `[AppManifest]` class, commit, then tag: `git tag v1.0.0 && git push --tags`.
3. The **Publish to the Hack Store** workflow builds the app and makes the catalog folder
   `apps/<appId>_<version>/`. With a `HACK_STORE_TOKEN` secret (a token allowed to fork and open
   pull requests), it opens the pull request to `Kazo-ca/HackerOs-AppMarketPlace` from your fork;
   without it, download the run's artifact and open the pull request yourself.

The catalog never takes a binary from you: once your pull request is reviewed and merged, its CI
compiles the app at the tagged commit.
