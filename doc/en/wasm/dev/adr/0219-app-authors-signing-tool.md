> [Version française](../../../../fr/wasm/dev/adr/0219-app-authors-signing-tool.md)

# ADR 0219: package signing tool for app authors

## Status

Accepted on 2026-10-31, work item `todo/25-app-signing-tool` (`todo/base`
series, `TODO` issue [#321](https://github.com/Kazo-ca/HackerOs/issues/321)).
Completes [ADR 0214](0214-hack-store-hot-add-signing-transactions.md) §2 for
`github:` sources; changes nothing in the check the OS makes.

## Context

ADR 0214 §2 accepts a `signature` field (ECDSA P-256, SHA-256, IEEE P1363 as
base64) for each version of an app repository's `index.json`, and public keys
in `[signing-keys]` of `/etc/hackeros/app-sources.config`. The official catalog
already signs its packages (`HackerOs.MarketPlace.Tool keygen` / `sign`,
`HACK_STORE_SIGNING_KEY` secret). An author who publishes from their own
repository (`github:<owner>/<repo>`, `hackeros-packages` branch) has no tool:

- `HackerOsPack` (ADR 0182 §2) writes the package and `index.json`, unsigned;
  worse, `AppPackageWriter.AddToIndex` rewrites the index **without** the
  signatures already there.
- The `hackeros-solution` template (ADR 0207) has a workflow that submits the
  app to the catalog, but none pushes the `hackeros-packages` branch.

## Decision

### 1. Tool: the HackerOS build tooling (Q1 A)

The manifest generator (`HackerOs.Tools.ManifestGenerator`, shipped in the
`HackerOs.App.Build` package and used by `HackerOsPack`) gets two commands, in
the same format as the catalog's tool:

- `keygen --private-key <file.pem>`: creates a P-256 pair, writes the PKCS#8
  private key to a **new** file (mode `0600` outside Windows; an existing file
  is never replaced) and prints the public key (base64 SubjectPublicKeyInfo)
  and the `[signing-keys]` line to give to administrators.
- `sign --package <file.hackapp> [--private-key <file.pem>]`: prints the
  signature of a package (key read from `HACKEROS_APP_SIGNING_KEY` without
  `--private-key`).

MSBuild targets of `HackerOs.Apps.targets`:

- `dotnet build -t:HackerOsKeyGen -p:HackerOsSigningKeyFile=<file.pem>`: runs
  `keygen`. The property is required (`HOSAPP0061`): no default path, so that a
  key is never created in the repository by accident.
- `HackerOsPack` signs each package when the `HACKEROS_APP_SIGNING_KEY`
  environment variable holds the private key (PEM), or when
  `HackerOsSigningKeyFile` points at the file. The key never goes through the
  command line. The signature is checked with the public key before it is
  written to `index.json`.

The new targets need the HackerOS version that contains this work item (the
next one after 2.2.0): an app repository on 2.2.0 keeps today's behavior.

### 2. Index: signatures are kept

`AppPackageWriter.AddToIndex` writes the `signature` field of every version
that has one: republishing a version or adding one no longer loses the
signatures of the others. A version republished without a key loses its own
(its bytes may have changed).

### 3. `hackeros-packages` branch: new workflow (Q2 A)

The `hackeros-solution` template (and so `Kazo-ca/HackerOs-AppTemplate`) gets
`.github/workflows/publish-packages.yml`, on a `v*` tag:

1. checks the submodule and the tag (= manifest version), like the Store
   workflow;
2. fetches the `hackeros-packages` branch (or creates an orphan one) into a
   folder, to keep the versions already published;
3. runs `HackerOsPack` with `HackerOsPackageOutput` on that folder and
   `HACKEROS_APP_SIGNING_KEY` from the secret of the same name;
4. commits and pushes the branch. It is the only workflow of the template with
   `contents: write`.

`hackpkg install <owner>/<repo>` then works without the Store.

### 4. Without a secret: published unsigned, with a warning (Q3 A)

Without `HACKEROS_APP_SIGNING_KEY`, the package is published with its SHA-256
only and the workflow writes a warning (`::warning::`). This is the rule of ADR
0214 §2.4: a source with no key in `[signing-keys]` is accepted. As soon as an
administrator has added the author's key, an unsigned package from that
repository is refused (`apps.package.unsigned`).

### 5. Secret name: `HACKEROS_APP_SIGNING_KEY` (Q4 A)

Distinct from `HACK_STORE_SIGNING_KEY` (official catalog): an author's key
protects only their repositories.

### 6. The author's key at the administrators

The author publishes their public key (repository README, project page). An
administrator adds it with `sudo nano /etc/hackeros/app-sources.config`:

```text
[signing-keys]
github.com/contoso=MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE…
```

`github.com/<owner>` covers every repository of the author,
`github.com/<owner>/<repo>` only one. To change keys without a break, the
author creates the new pair, has it added after the old one (separated by a
comma), changes the secret, then the old one is removed. A host can also
compile the key in with `HackerOsAppSigningKeys`.

## Consequences

- An author signs with what they already have (`dotnet build`), nothing to
  install.
- The official catalog keeps its own tool (`HackerOs.MarketPlace.Tool`), in the
  same format; the two share no code (the catalog builds the OS from its
  submodule).
- `Kazo-ca/HackerOs-AppTemplate` gets the workflow when it is regenerated on
  the version that contains this work item (draft PR until then).
- The private key never leaves the author's machine, except in their
  repository's secret; no agent handles it.

## Questions asked to Benoit

Asked on 2026-10-31; the recommendations (A) are applied in the meantime.

| Question | Choices | Benoit's answer |
|---|---|---|
| Q1 Where to put `keygen` and `sign` | **A HackerOS build tooling (recommended)**; B separate `dotnet` tool; C MarketPlace tool | pending |
| Q2 Who publishes `hackeros-packages` | **A new `publish-packages.yml` workflow (recommended)**; B inside the Store workflow; C by hand | pending |
| Q3 Without a secret | **A publish unsigned with a warning (recommended)**; B fail | pending |
| Q4 Secret name | **A `HACKEROS_APP_SIGNING_KEY` (recommended)**; B `HACK_STORE_SIGNING_KEY` | pending |

## Tasks

- [x] Generator `keygen` and `sign`, signature in `pack`.
- [x] `HackerOsKeyGen` target and signing in `HackerOsPack`.
- [x] `AddToIndex` keeps signatures.
- [x] `publish-packages.yml` workflow of the `hackeros-solution` template, `*.pem` ignored.
- [x] Tests (`ManifestGenerator`, `AppPackageWriterTests`).
- [x] Documentation (`app-installation.md`, `framework/app-repository.md`).
- [ ] `Kazo-ca/HackerOs-AppTemplate` regenerated on the version that contains this work item.
