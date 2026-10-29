> [Version française](../../../../fr/wasm/dev/adr/0188-hack-store-and-app-catalog.md)

# ADR 0188: the Hack Store and the `HackerOs-AppMarketPlace` catalog

## Status

Accepted on 2026-10-29 by Benoit, after two rounds of questions in the "Plan
the Hack Store" thread (answers at the end). This ADR writes no code: it sets
the target, and the work items are listed in
[`../plans/hackeros-store.md`](../plans/hackeros-store.md). Complements
[ADR 0182](0182-install-apps-from-github.md) (`.hackapp` packages, `hackpkg`)
and [ADR 0176](0176-app-repository-template.md) (app repository template).
Trust and install rights are in
[ADR 0189](0189-app-sources-trust-and-per-user-install.md), theme packs (v2)
in [ADR 0190](0190-theme-packs.md).

## Context

- `hackpkg` (ADR 0182) installs an app from the `hackeros-packages` branch of
  its GitHub repository, read through `raw.githubusercontent.com`. There is no
  list of apps and no graphical interface.
- The [`Kazo-ca/HackerOs-AppMarketPlace`](https://github.com/Kazo-ca/HackerOs-AppMarketPlace)
  repository exists and is empty.
- The Start menu has a **Pinned** section (the "quick access"); a new profile
  has no app there today.
- Benoit wants everything published in our catalog to be reviewed before it is
  approved, and to depend on no binary built by a third party.

## Decision

### 1. The Hack Store system app

- System app `org.hackeros.store`, display name **Hack Store**, in the HackerOs
  repository, shipped with the OS and **not uninstallable** (like Settings).
- It is **pinned by default** in the Pinned section of the Start menu: for
  every new profile, and added **once** to existing profiles by a migration (a
  flag in the Start menu file prevents adding it again after it was unpinned).
  The user can unpin it.
- It works on the desktop and on the mobile (touch) interface.

### 2. Content of v1

- Home page by category, search (name, short description, tags).
- App page: icon, screenshots, description, version, size, license, requested
  capabilities, source and trust level (ADR 0189).
- Install (for me or for the system, ADR 0189), Open, Update, Uninstall
  buttons; an **Installed** tab.
- No ratings or reviews (they would need a server).
- External apps only: apps built into the OS stay managed in Settings ▸ Apps
  (ADR 0048).
- An install needs an **OS restart** for the app to appear (ADR 0182, Q5 C).
  The Store groups pending installs and offers "Restart to finish". Hot
  addition stays a separate work item.

### 3. Updates

The Store checks for updates when the session starts, then every 6 hours
(configurable in `/etc/hackeros/app-sources.config`, key
`[updates] interval-hours`). When it finds some, it shows a badge on its icon
and sends a notification; updating takes one click, with the same rights as
installing (ADR 0189). No automatic updates.

### 4. The `HackerOs-AppMarketPlace` catalog repository

`main` branch, reviewed through pull requests: **one folder per app
version**, named `<appId>_<version>`.

```text
apps/
  org.example.myapp_1.1.2/
    icon.png                     PNG 256×256, 200 KB at most
    short_description.en.txt     120 characters at most, English required
    short_description.fr.txt     optional (one language = one suffix)
    description.en.md            English required
    description.fr.md            optional
    manifest.json                the app's generated manifest (ADR 0047)
    store.json                   see below
    screenshots/1.png … 5.png    optional, 1 to 5 PNG screenshots
categories.json                  closed list of categories, French and English labels
.github/pull_request_template.md
.github/workflows/validate.yml   checks a PR
.github/workflows/publish.yml    publishes after the merge
```

`store.json`:

```json
{
  "repository": "https://github.com/example/HackerOs-App-MyApp",
  "commit": "3f2a9c1…",
  "category": "productivity",
  "tags": ["notes", "markdown"],
  "license": "MIT"
}
```

- `commit` is the full id of this version's commit in the app's repository.
  The author **provides no binary**: our CI builds the app itself from that
  commit and publishes **our** package. What we publish is exactly the code
  that was reviewed.
- Tags are free; the category comes from `categories.json`.

### 5. Checking a PR (`validate.yml`)

On every PR, the action:

1. checks the folder format (names, sizes, icon dimensions, short description
   length, English present, known category);
2. checks that `manifest.json` passes the ADR 0098 checks and that its `appId`
   and version match the folder name;
3. checks that the commit exists in the given repository;
4. builds the app at that commit (`dotnet build -c Release -t:HackerOsPack`,
   ADR 0182) and checks that the produced manifest is the folder's one;
5. runs the API check against the trimmed runtime (ADR 0182, Q1);
6. runs a CodeQL analysis of the app's code;
7. posts as a PR comment the requested capabilities (dangerous ones
   highlighted) and the list of bundled JavaScript files.

Benoit (or a maintainer he names) reviews the code and approves; nothing is
merged automatically.

### 6. Publishing (`publish.yml`)

After the merge, the action rebuilds the app at the same commit and updates a
separate **`packages`** branch, with no code history, read through
`raw.githubusercontent.com` (GitHub releases are blocked by CORS, ADR 0182):

```text
catalog.json
packages/<appId>/<version>/<appId>-<version>.hackapp
assets/<appId>/<version>/icon.png, screenshots/…, description.*.md
```

`catalog.json` lists, per app: `appId`, `kind` (`app` or `theme-pack`,
ADR 0190), name, category, tags, short descriptions, license, repository,
capabilities, `sdkCompatibility`, and its published versions (package path,
SHA-256, size, commit). The **last 3 versions** of each app stay downloadable;
older ones are removed from `packages` (their folder stays in the history of
`main`).

### 7. The action for app authors

The `hackeros-solution` template (ADR 0176) gets a workflow: on a `v*` tag, it
builds the app, produces the `<appId>_<version>` folder (icon, descriptions and
`store.json` taken from a `store/` folder of the app repository, commit of the
tag) and opens the PR to `HackerOs-AppMarketPlace` from the author's fork, with
a token the author puts in their secrets. Without a token, the folder is left
as a workflow artifact, to submit by hand.

### 8. `hackpkg` and the catalog

`hackpkg` reads the same catalogs as the Store: `hackpkg install <appId>` looks
for the app in the catalog sources (ADR 0189), in addition to
`github:owner/repo` (ADR 0182). The Store and `hackpkg` use the same install
service.

## Consequences

- An author publishes through a PR; the code is reviewed and built by us
  before it reaches users.
- The catalog repository has two branches of different kinds: `main` (small
  reviewed files) and `packages` (generated files, rewritten by the action).
- The Store depends on three earlier work items: the `hackeros-solution`
  template (ADR 0176), serving the `wwwroot/` assets of installed apps (ADR 0182
  limit), and per-source trust (ADR 0189).
- Packages are still not signed: `catalog.json` carries the SHA-256 values, and
  the `packages` branch is written only by our action.

## Benoit's answers

Questions asked on 2026-10-28 and 2026-10-29. No answer or "ok" = the
recommendation.

- **Q1** System app in the HackerOs repository, not uninstallable: A.
- **Q2** Pinned by default, added once to existing profiles, can be unpinned: A.
- **Q3** Catalog: one folder per version `<appId>_<version>` with icon,
  descriptions, manifest and a JSON (repository, commit, tags, category); PR
  template; checking action; action that updates `catalog.json` and the built
  package after acceptance; action for authors (Benoit's proposal).
- **Q6** Restart after install in v1: A. **Q7** Check at startup and
  periodically, badge, one-click update: A. **Q8** v1 content without ratings
  or reviews: A. **Q9** External apps only: A.
- **Q17** Desktop and mobile: A.
- **Q18** Our CI builds from the commit, the author provides no binary: A.
  **Q19** `packages` branch: A. **Q20** Last 3 versions: A. **Q21** Folder
  content (256×256 icon, French/English descriptions with English required,
  optional screenshots): A. **Q22** Checks with CodeQL and Benoit's approval: A.
  **Q23** Author action that opens the PR from a fork: A.

## Tasks

- [ ] `hackeros-solution` template (ADR 0176 tasks), with the §7 workflow.
- [ ] Serve the `wwwroot/` assets of installed apps (ADR 0182 limit).
- [ ] `HackerOs-AppMarketPlace` repository: `categories.json`, PR template,
      `validate.yml`, `publish.yml`, `packages` branch, `catalog.json`.
- [ ] Hack Store system app (v1, §1 to §3), default pin and migration.
- [ ] `hackpkg install <appId>` from catalogs.
- [ ] Documentation: `app-installation.md`, publishing guide for authors.
