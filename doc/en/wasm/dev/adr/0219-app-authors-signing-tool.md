> [Version française](../../../../fr/wasm/dev/adr/0219-app-authors-signing-tool.md)

# ADR 0219: app repository packages, signed only in the marketplace

## Status

Accepted on 2026-10-31, work item `todo/25-app-signing-tool` (`todo/base`
series, `TODO` issue [#321](https://github.com/Kazo-ca/Os/issues/321)).
Completes [ADR 0214](0214-hack-store-hot-add-signing-transactions.md) §2 for
`github:` sources; changes nothing in the check the OS makes.

**Amended on 2026-11-07** from Benoit's rule of 2026-11-06: "No signing of
applications outside the marketplace repository." Benoit chose to amend this
ADR rather than write a new one (see "Questions asked to Benoit"): the
authors' signing tool described in the first version is removed; see
"History".

## Context

- ADR 0214 §2 accepts a `signature` field (ECDSA P-256, SHA-256, IEEE P1363 in
  base64) for each version of an app repository's `index.json`, and public
  keys in `[signing-keys]` of `/etc/kazoos/app-sources.config`.
- The Kazo OS Store builds and signs apps in `Kazo-ca/Os-AppMarketPlace`
  (`Kazo.Os.MarketPlace.Tool keygen` / `sign`, `publish.yml` workflow,
  `KAZO_STORE_SIGNING_KEY` secret): it is the only signature the OS requires by
  default (`catalog:official`).
- An author also publishes their app on the `kazoos-packages` branch of their
  repository (`github:<owner>/<repo>`), for `kazopkg install <owner>/<repo>`
  without the Store.
- Benoit wants a single private key, kept in a single place: the marketplace
  repository.

## Decision

### 1. No signing outside `Kazo-ca/Os-AppMarketPlace`

Only the `publish.yml` workflow of the marketplace repository signs packages.
No app repository, no template and no SDK tool signs. The `HACK_STORE_TOKEN`
secret of app repositories, which opens the pull request to the catalog (fork
and pull request), signs nothing and stays.

### 2. The SDK has no signing tool (Q2 A)

- The manifest generator (`Kazo.Os.Tools.ManifestGenerator`, package
  `Kazo.Os.App.Build`) has no `keygen` or `sign` command.
- `Kazo.Os.Apps.targets` has no `KazoOsKeyGen` target and no
  `KazoOsSigningKeyFile` property; `KazoOsPack` does not read
  `HACKEROS_APP_SIGNING_KEY` and never signs.
- Codes `KOSAPP0061` to `KOSAPP0063` (removed tool) are no longer raised; their
  numbers are not reused.

### 3. Index: signatures are kept

`AppPackageWriter.AddToIndex` writes the `signature` field of each version that
has one: republishing a version or adding one does not lose the others'
signatures. A republished version loses its own (its bytes may have changed).

### 4. `kazoos-packages` branch: workflow without signing (Q3 A)

The `kazoos-solution` template (and so `Kazo-ca/Os-AppTemplate` and the app
repositories made from it) has `.github/workflows/publish-packages.yml`, on a
`v*` tag:

1. checks the submodule and the tag (= manifest version), like the Store
   workflow;
2. fetches the `kazoos-packages` branch (or creates an orphan one) into a
   folder, to keep the versions already published;
3. runs `KazoOsPack` with `KazoOsPackageOutput` on that folder, with no key;
4. pushes the branch. It is the only template workflow with
   `contents: write`.

The package carries its SHA-256 only. The OS treats that source like any
source without a key (ADR 0214 §2.4): the install is accepted, with an
administrator's approval for an untrusted source (ADR 0208).

### 5. Secrets and author keys

- The `HACKEROS_APP_SIGNING_KEY` secrets of app repositories have no use any
  more: Benoit deletes them (Settings ▸ Secrets and variables ▸ Actions). No
  agent handles a private key.
- The OS still reads `[signing-keys]` and `KazoOsAppSigningKeys` (ADR 0214
  §2.3), but the only keys planned are catalog keys (`catalog:<name>`). A
  `github.com/<owner>` key added by an administrator would make the unsigned
  packages of that repository refused: it must be removed.

## Consequences

- One private key to keep and rotate: the catalog's.
- An author cannot prove that a package of their `kazoos-packages` branch
  comes from them; a user who wants a signed app installs it from the Kazo OS
  Store.
- Change to the `Kazo.Os.App.Build` package (MSBuild tooling, no public C#
  API): an app repository that called `KazoOsKeyGen` or passed
  `KazoOsSigningKeyFile` must remove that call.

## History

The first version (2026-10-31) gave each author their own key: generator
`keygen` and `sign`, `KazoOsKeyGen` target, signing by `KazoOsPack` with
`HACKEROS_APP_SIGNING_KEY` or `KazoOsSigningKeyFile` (warning `KOSAPP0063`
without a key), and the author's key in `[signing-keys]`
(`github.com/<owner>=…`). It shipped in Kazo OS 2.2.1 and was removed on
2026-11-07 (pull request [#390](https://github.com/Kazo-ca/Os/pull/390)).

## Questions asked to Benoit

First version, asked and answered on 2026-10-31:

| Question | Choices | Benoit's answer (2026-10-31) |
|---|---|---|
| Q1 Where to put `keygen` and `sign` | **A build tooling (recommended)**; B separate `dotnet` tool; C MarketPlace tool | A (removed on 2026-11-07) |
| Q2 Who publishes `kazoos-packages` | **A new `publish-packages.yml` workflow (recommended)**; B inside the Store workflow; C by hand | A |
| Q3 Without a secret | **A publish unsigned with a warning (recommended)**; B fail | A (moot on 2026-11-07: never signed) |
| Q4 Secret name | **A `HACKEROS_APP_SIGNING_KEY` (recommended)**; B `HACK_STORE_SIGNING_KEY` | A (secret removed on 2026-11-07) |

Amendment, asked on 2026-11-07 in the thread "Signer les apps seulement dans
le marketplace":

| Question | Choices | Benoit's answer (2026-11-07) |
|---|---|---|
| Q1 How to remove per-repository signing | A new ADR superseding 0219 (recommended); **B amend ADR 0219**; C delete ADR 0219 | B |
| Q2 Also remove the signing tool from the SDK | **A yes (recommended)**; B only the workflows | A |
| Q3 Keep publishing to `kazoos-packages` | **A keep, unsigned (recommended)**; B delete the workflow | A |

## Tasks

- [x] `AddToIndex` keeps the signatures.
- [x] `publish-packages.yml` workflow of the `kazoos-solution` template, `*.pem` ignored.
- [x] `Kazo-ca/Os-AppTemplate` regenerated on a version that contains this work item.
- [x] Signing removed from the SDK, the template and the `Os-AppTemplate`, `Os-App-Webamp`, `Os-App-Chess` repositories (2026-11-07).
- [ ] Benoit deletes the `HACKEROS_APP_SIGNING_KEY` secrets of the app repositories.
