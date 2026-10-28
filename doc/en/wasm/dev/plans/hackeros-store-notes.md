> [Version française](../../../../fr/wasm/dev/plans/hackeros-store-notes.md)

# Notes: third-party apps, installing from GitHub and the HackerOs Store

Benoit's notes of 2026-10-28, to pick up when the first third-party apps are
available. This is not a validated plan: no decision and no code.

## Repositories

- **Separate apps**: each third-party app lives in its own repository
  `Kazo-ca/HackerOs-App-<Name>`, outside this repository.
- **Template**: `Kazo-ca/HackerOs-AppTemplate`, the starting point of an app
  repository ([ADR 0176](../adr/0176-app-repository-template.md)).
- **List of installable apps**: `Kazo-ca/HackerOs-AppMarketPlace` holds the
  list of app repositories that can be installed in HackerOS.

## First milestone: a terminal command

Install an app from its GitHub repository with a terminal command. The OS
must therefore accept GitHub as a trusted host to download app packages.
Points to settle before any code (evaluation of 2026-10-28):

- runtime installation does not exist yet: today an app enters the OS at
  build time (`ProjectReference` or `PackageReference` of the host);
- the framework is trimmed at publish time (ADR 0106): an app loaded at
  runtime must only call members that were kept;
- GitHub Release files do not send CORS headers, while `api.github.com`,
  `raw.githubusercontent.com` and GitHub Pages do;
- an app's JavaScript runs in the same origin as the OS.

## Later: the HackerOs Store app

A `HackerOs Store` system app, like the Play Store or the Microsoft Store,
will read the list in `Kazo-ca/HackerOs-AppMarketPlace`, show the available
apps and install them through the same service as the terminal command. The
future app store is already anticipated by the `manifest.json` format
([`app-model.md`](../../framework/app-model.md)); an app it installs is never
a system app.
