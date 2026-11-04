> [Version française](readme-fr.md)

# Kazo.Os.Template.Solution

A Kazo OS app repository, made with the `hackeros-solution` template.

| Folder | Content |
| --- | --- |
| `src/Kazo.Os.Template.Solution` | The app (window app, its `[AppManifest]` class generates `manifest.json`) |
| `src/Kazo.Os.Template.Solution.Os` | A host that runs the whole Kazo OS with the base apps and this app: the project to start |
| `tests/Kazo.Os.Template.Solution.Tests` | Tests of the app |
| `external/HackerOs` | The Kazo OS repository as a git submodule, on a release tag |
| `store/` | What the Hack Store shows: icon (PNG 256×256), short descriptions, descriptions, `store.json` |

## First steps

```bash
# A repository created from the GitHub template already has the submodule:
git clone --recurse-submodules <your repository>

# A folder made with "dotnet new hackeros-solution" adds it once:
git init
git submodule add --depth 1 https://github.com/Kazo-ca/Os.git external/HackerOs
git -C external/HackerOs checkout v<Kazo OS version>
```

Then open the `.sln` in Visual Studio or Rider and start `Kazo.Os.Template.Solution.Os`, or run
`dotnet run --project src/Kazo.Os.Template.Solution.Os`.

## The Kazo OS version

`external/HackerOs/wasm2/KazoOs/build/Versions.props` is the Kazo OS version of this repository.
To move to another version, move the submodule to its tag and commit it:

```bash
git -C external/HackerOs fetch --depth 1 origin tag v2.2.0
git -C external/HackerOs checkout v2.2.0
git add external/HackerOs
```

Never edit the submodule: the OS is not modified from an app repository, and CI refuses a
submodule with local changes or off its tag.

## Two build profiles

| Profile | Kazo OS comes from | Use |
| --- | --- | --- |
| `Debug` | the projects of the submodule (`HackerOsUseSource=true`) | step into the SDK and the OS, breakpoints anywhere |
| `Release` | the `Kazo.Os.*` packages of nuget.org, at the submodule's version | CI, `dotnet publish`, the Hack Store |

Force either one with `-p:HackerOsUseSource=true` or `false`.

## Publishing to the Hack Store

1. Fill `store/` (icon, short descriptions, descriptions, category and tags in `store.json`).
2. Change `Version` in the app's `[AppManifest]` class, commit, then tag: `git tag v1.0.0 && git push --tags`.
3. The **Publish to the Hack Store** workflow builds the app and makes the catalog folder
   `apps/<appId>_<version>/`. With a `HACK_STORE_TOKEN` secret (a token allowed to fork and open
   pull requests), it opens the pull request to `Kazo-ca/Os-AppMarketPlace` from your fork;
   without it, download the run's artifact and open the pull request yourself.

`HACK_STORE_TOKEN` is a GitHub personal access token of yours, not something Kazo OS gives you:

1. GitHub ▸ **Settings** ▸ **Developer settings** ▸ **Personal access tokens** ▸ **Tokens (classic)** ▸
   **Generate new token (classic)**, with a short expiration and the **`repo`** and **`workflow`**
   scopes (a fine-grained token cannot cover both your fork and the catalog's repository).
2. Copy it (shown once) into this repository: **Settings** ▸ **Secrets and variables** ▸ **Actions** ▸
   **New repository secret**, name `HACK_STORE_TOKEN` (or `gh secret set HACK_STORE_TOKEN`).

`repo` reaches all your repositories: keep the token only in this secret and revoke it if in doubt.

The catalog never takes a binary from you: once your pull request is reviewed and merged, its CI
compiles the app at the tagged commit.

## Publishing on the `hackeros-packages` branch, signed

The same `v*` tag also runs **Publish the Kazo OS packages**: it packs the app and pushes it to the
`hackeros-packages` branch of this repository, keeping the versions already published. Kazo OS
users then install it with `hackpkg install <owner>/<repo>`, without the Hack Store.

Sign your packages so that nobody else can publish under your name:

1. Once, on your machine, outside the repository:
   `dotnet build src/Kazo.Os.Template.Solution -t:HackerOsKeyGen -p:HackerOsSigningKeyFile=$HOME/hackeros-signing-key.pem`.
   The private key goes to that new file (`*.pem` is ignored by git); the public key is printed.
2. Put the content of the file in the `HACKEROS_APP_SIGNING_KEY` secret of this repository
   (Settings ▸ Secrets and variables ▸ Actions), then keep the file offline.
3. Publish the printed public key (in this README for example). An administrator adds it to
   `[signing-keys]` of `/etc/hackeros/app-sources.config`, as `github.com/<owner>=<key>` (all your
   repositories) or `github.com/<owner>/<repo>=<key>` (this one). From then on, an unsigned or
   badly signed package of yours is refused.

Without the secret the package is published unsigned (SHA-256 only) and the run shows a warning.
To sign locally, set `HACKEROS_APP_SIGNING_KEY` or pass `-p:HackerOsSigningKeyFile=<file.pem>` to
`HackerOsPack`. To change keys without a break, have the new key added after the old one
(separated by a comma), change the secret, then have the old one removed.
