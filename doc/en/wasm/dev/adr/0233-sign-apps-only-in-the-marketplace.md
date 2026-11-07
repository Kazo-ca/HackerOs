> [Version française](../../../../fr/wasm/dev/adr/0233-sign-apps-only-in-the-marketplace.md)

# ADR 0233: apps are signed only in the marketplace repository

## Status

Accepted on 2026-11-07, from Benoit's rule of 2026-11-06: "No signing of
applications outside the marketplace repository." The three implementation
questions are in "Questions asked to Benoit".

- **Supersedes:** [ADR 0219](0219-app-authors-signing-tool.md) entirely (the
  authors' signing tool, the `HACKEROS_APP_SIGNING_KEY` secret, the author's key
  in `[signing-keys]`), except its §2 (`index.json` keeps the signatures already
  there) and the publishing of the `kazoos-packages` branch (its §3, without the
  signature).
- **Does not change:** signature checking by the OS and the official catalog
  key ([ADR 0214](0214-hack-store-hot-add-signing-transactions.md) §2); the
  `Kazo.Os.MarketPlace.Tool` tool (`keygen`, `sign`) and the
  `KAZO_STORE_SIGNING_KEY` secret of the `Kazo-ca/Os-AppMarketPlace` repository;
  the `HACK_STORE_TOKEN` secret of app repositories, which opens the pull
  request to the catalog (fork and pull request, no signing).

## Context

- ADR 0219 gave app authors their own key: `KazoOsKeyGen` creates the pair,
  `KazoOsPack` signs each package with `HACKEROS_APP_SIGNING_KEY`, and the
  `publish-packages.yml` workflow of the `kazoos-solution` template pushes the
  signed package to the `kazoos-packages` branch of the app repository.
- That workflow is in `Kazo-ca/Os-AppTemplate`, `Kazo-ca/Os-App-Webamp` and
  `Kazo-ca/Os-App-Chess`: one private key per app repository, kept in a secret
  of each repository.
- The Kazo OS Store builds and signs apps in `Kazo-ca/Os-AppMarketPlace`
  (`publish.yml`, ADR 0214 §2.5): it is the only signature the OS requires by
  default (`catalog:official`).
- Benoit wants a single key, kept in a single place: the marketplace
  repository.

## Decision

### 1. No signing outside `Kazo-ca/Os-AppMarketPlace`

Only the `publish.yml` workflow of the marketplace repository signs packages,
with `Kazo.Os.MarketPlace.Tool` and the `KAZO_STORE_SIGNING_KEY` secret. No app
repository, no template and no SDK tool signs.

### 2. The signing tool leaves the SDK (Q2 A)

- The manifest generator (`Kazo.Os.Tools.ManifestGenerator`, package
  `Kazo.Os.App.Build`) loses the `keygen` and `sign` commands and the
  `PackageSigner` class.
- `Kazo.Os.Apps.targets` loses the `KazoOsKeyGen` target and the
  `KazoOsSigningKeyFile` property; `KazoOsPack` no longer reads
  `HACKEROS_APP_SIGNING_KEY`.
- Codes `KOSAPP0061` (`KazoOsKeyGen` property), `KOSAPP0062` (signing failed)
  and `KOSAPP0063` (unsigned package) are no longer raised; their numbers are
  not reused.
- `AppPackageWriter.AddToIndex` keeps the `signature` field of a version that
  already has one (ADR 0219 §2): an index written by an earlier version loses
  nothing.

### 3. `kazoos-packages` is still published, unsigned (Q3 A)

The `publish-packages.yml` workflow of the `kazoos-solution` template (and of
the app repositories that have it) keeps its role: on a `v*` tag, it pushes the
package to the `kazoos-packages` branch, for `kazopkg install <owner>/<repo>`.
It has no secret and no warning any more: the package carries its SHA-256 only.
The OS treats that source like any source without a key (ADR 0214 §2.4): the
install is accepted, with an administrator's approval for an untrusted source
(ADR 0208).

### 4. The `HACKEROS_APP_SIGNING_KEY` secrets are to be deleted

App repositories no longer use it. Benoit deletes the secret from each app
repository that has it (Settings ▸ Secrets and variables ▸ Actions); no agent
handles a private key.

### 5. `github.com/<owner>` keys in `[signing-keys]`

The OS still reads `[signing-keys]` and `KazoOsAppSigningKeys` (ADR 0214 §2.3),
but the documentation no longer suggests adding an author's key there: the only
keys planned are catalog keys (`catalog:<name>`). A `github.com/<owner>` key
already added by an administrator would make the unsigned packages of that
repository refused: it must be removed.

## Consequences

- One private key to keep and rotate: the catalog's.
- An author can no longer prove that a package of their `kazoos-packages`
  branch comes from them; a user who wants a signed app installs it from the
  Kazo OS Store.
- Change to the `Kazo.Os.App.Build` package (MSBuild tooling, no public C#
  API): an app repository that called `KazoOsKeyGen` or passed
  `KazoOsSigningKeyFile` must remove that call; without it, `KazoOsPack` works
  as before, unsigned.
- The workflows of `Kazo-ca/Os-AppTemplate`, `Kazo-ca/Os-App-Webamp` and
  `Kazo-ca/Os-App-Chess` lose their signing step (one pull request per
  repository; Chess is in production, only its workflow changes).

## Questions asked to Benoit

Asked on 2026-11-07 in the thread "Signer les apps seulement dans le
marketplace", with the recommendations (A) implemented meanwhile.

| Question | Choices | Benoit's answer |
|---|---|---|
| Q1 How to remove per-repository signing from ADR 0219 | **A new ADR superseding 0219 (recommended)**; B edit ADR 0219; C delete ADR 0219 | pending |
| Q2 Also remove the signing tool from the SDK | **A yes (recommended)**; B only the workflows | pending |
| Q3 Keep publishing to `kazoos-packages` | **A keep, unsigned (recommended)**; B delete the workflow | pending |

## Tasks

- [x] ADR 0233 (fr, en), ADR 0219 status, ADR index.
- [x] `keygen`, `sign`, `PackageSigner`, `KazoOsKeyGen` and `KazoOsPack` signing removed; tests updated.
- [x] `publish-packages.yml` workflow and readmes of the `kazoos-solution` template without signing.
- [x] Documentation (`app-installation.md`, `framework/app-repository.md`, `framework/sdk-packages.md`, `features.md`, memory).
- [x] Pull requests on `Os-AppTemplate`, `Os-App-Webamp` and `Os-App-Chess` (workflow without signing).
- [ ] Benoit deletes the `HACKEROS_APP_SIGNING_KEY` secrets of the app repositories.
