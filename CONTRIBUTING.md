> [Version française](CONTRIBUTING-fr.md)

# Contributing to Kazo OS

Thank you for your interest in Kazo OS, a simulated operating system that runs
in the browser (C#, Blazor WebAssembly, .NET 10). This guide explains how to
ask for a feature, report a bug and send a change.

The detailed rules, shared with coding agents, are in [`AGENTS.md`](AGENTS.md).
The project's current state and conventions (series in progress, next free ADR
number, known test failures) are in
[`doc/en/wasm/memory/`](doc/en/wasm/memory/README.md).

## Code of conduct

Please help keep this project open and inclusive. By taking part, you agree to:

- be respectful of different viewpoints and experiences;
- accept constructive criticism gracefully;
- focus on what is best for the project and its users;
- show empathy towards other contributors.

## Asking for a feature

1. Read the [features overview](doc/en/wasm/features.md): the feature may
   already exist, or be listed under "In development".
2. Search the [open issues](https://github.com/Kazo-ca/Os/issues),
   including the ones labelled `TODO` (the maintainer's to-do list): someone
   may already have asked for it. If so, add your use case in a comment rather
   than opening a duplicate.
3. Open a [new issue with the **Feature request** form](https://github.com/Kazo-ca/Os/issues/new?template=feature_request.yml).
   Describe the need first (what you want to do and why), then the solution
   you imagine, the parts of the OS it touches and the alternatives you
   considered. You may write in French or in English.
4. The maintainer answers in the issue. A large feature usually gets an
   architecture decision record (ADR) under
   [`doc/en/wasm/dev/adr/`](doc/en/wasm/dev/adr/README.md) before any code is
   written.

A feature for a third-party app (an app ported in a `Kazo-ca/Os-App-*`
repository, the app template or the app marketplace) is asked for in that
app's repository.

## Reporting a bug

Open an issue with the
[**Bug report** form](https://github.com/Kazo-ca/Os/issues/new?template=bug_report.yml):
steps to reproduce, what you expected, what happened, the Kazo OS version
(Settings ▸ About, or the "What's new" window), the browser and the device.
A screenshot or the browser console output helps a lot.

## Getting the code running

Prerequisite: the .NET 10 SDK (`wasm2/KazoOs/global.json`).

```bash
cd wasm2/KazoOs
dotnet build Kazo.Os.sln
dotnet test Kazo.Os.sln
dotnet run --project OS/Kazo.Os.Ecosystem     # the OS as a standalone PWA
```

Debug hosts, the trimmed publish, the E2E tests and known workarounds are in
[`building-and-testing.md`](doc/en/wasm/dev/building-and-testing.md). Start with
[`project-overview.md`](doc/en/wasm/dev/project-overview.md) to find your way
in the code, and read [`pitfalls.md`](doc/en/wasm/dev/pitfalls.md) before your
first change.

To build an app, a command or a site **outside** this repository, use the
[Kazo OS app template](https://github.com/Kazo-ca/Os-AppTemplate) and
[`building-on-kazoos.md`](doc/en/wasm/framework/building-on-kazoos.md).

## Where the code goes

- **All new code goes in [`wasm2/KazoOs/`](wasm2/KazoOs/README.md).**
  [`src/`](src/README.md) is the legacy TypeScript version, kept read-only as a
  behavioral reference: never add or change code there.
- The standalone PWA (`OS/Kazo.Os.Ecosystem`) must always work with no server.
  `Server/Kazo.Os.Server` is optional: nothing may require it.
- C# first. JavaScript only for browser APIs C# cannot reach, shipped as an
  asset of the component or library that needs it, never as a new
  `<script src>` in a host.
- No inline CSS or JS in `.razor` markup: styles in `MyComponent.razor.css`,
  script in `MyComponent.razor.js` (enforced by the build,
  [ADR 0007](doc/en/wasm/dev/adr/0007-enforce-collocated-razor-assets.md)).
- MudBlazor only for complex surfaces (menus, grids, tabs, forms, dialogs),
  never in the SDK or abstraction contracts.
- The look is "modern" or "gothic/hacker", never plain unstyled controls.
- File and folder names are in English. Public methods and non-obvious code
  carry XML doc comments.
- No game in this repository
  ([ADR 0135](doc/en/wasm/dev/adr/0135-no-game-in-this-repository.md)):
  "Aucun jeu sur ce repository".

## Sending a pull request

1. Fork the repository (or, for the maintainers, create a branch) from `main`.
2. Give the branch a name that describes its content, for example
   `docs/contributing-feature-requests` or `desktop/icon-grid-snapping`,
   never a generic name such as `feature/1`. A series of related changes uses
   an integration branch `<series>/base` and one branch `<series>/<n>-<topic>`
   per work item.
3. Make the change, with its tests and its documentation (see below).
4. Commit with [Conventional Commits](https://www.conventionalcommits.org/)
   messages (`feat:`, `fix:`, `docs:`, `test:`, `refactor:`, `chore:`…).
5. Open the pull request to `main` and fill in the
   [template](.github/pull_request_template.md). Link the issue it closes
   (`Closes #…`).
6. Only the maintainer merges into `main`.

### Tests

- A green local build and test run is required before asking for a merge.
  Run the E2E suites one at a time.
- When the change concerns them, also run
  `wasm2/KazoOs/build/publish-trimmed.sh` and the SDK compatibility fixture
  `wasm2/KazoOs/Tests/fixtures/sdk-compat`.
- Never skip, disable or quarantine a test to get green. Failures already
  known are listed in
  [`known-test-failures.md`](doc/en/wasm/memory/known-test-failures.md): name
  them in the PR, do not hide them.
- GitHub Actions check every PR as well.

### Documentation

- The documentation is bilingual, with the same folders and file names:
  French (the original) in [`doc/fr/`](doc/fr/README.md), English in
  [`doc/en/`](doc/en/README.md). Every doc change updates both languages in the
  same PR, and the `README.md` index of each touched folder.
- A PR that ships, changes or plans a feature updates the features overview
  ([`doc/fr/wasm/features.md`](doc/fr/wasm/features.md) and
  [`doc/en/wasm/features.md`](doc/en/wasm/features.md)).
- An architecture decision is recorded as an ADR in `doc/*/wasm/dev/adr/`,
  with the next free number (see the memory README and the open PRs). An
  accepted ADR is never edited in place: a new ADR supersedes it.
- A change to the public SDK updates the `PublicAPI.Unshipped.txt` file of the
  package concerned.

### Change entries

A PR to `main` that changes product files under `wasm2/KazoOs/` (outside
`Tests/`, `docs/`, `changes/` and `.md` files) adds an entry
`wasm2/KazoOs/changes/unreleased/<guid>.json` with an English and a French
text, shown in the "What's new" window
([ADR 0186](doc/en/wasm/dev/adr/0186-whats-new-change-list.md),
[`change-list.md`](doc/en/wasm/dev/change-list.md)). Check it locally after
committing with `wasm2/KazoOs/build/tools/check-change-entry.sh`. A PR with
no user-visible change carries the `no-changelog` label instead.

## Releases

The version lives in `wasm2/KazoOs/build/Versions.props`. When a PR that
changes it is merged into `main`, the release workflow creates the tag, the
GitHub Release and publishes the NuGet packages
([ADR 0173](doc/en/wasm/dev/adr/0173-automatic-releases-from-the-version-file.md),
[`releases.md`](doc/en/wasm/dev/releases.md)).

## Getting help

Open an issue with the `question` label, or read the
[documentation index](doc/en/wasm/README.md).

Thank you for contributing to Kazo OS!
