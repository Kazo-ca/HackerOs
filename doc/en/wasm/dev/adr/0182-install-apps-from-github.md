> [Version française](../../../../fr/wasm/dev/adr/0182-install-apps-from-github.md)

# ADR 0182: install an app from its GitHub repository at the terminal

## Status

Accepted on 2026-10-28 by Benoit, who chose the recommended option for the first four
questions ("Answers" section): API check at install time, `hackeros-packages`
branch, only `Kazo-ca` repositories trusted by default, machine-wide
installation by an administrator; then C for Q5: the app appears after a restart, immediate addition comes later. Request of the same day: "we'll just make a
terminal command to install an application from its GitHub repository. So we
must make sure GitHub is allowed as a trusted host." Complements
[ADR 0176](0176-app-repository-template.md) (app repository template),
[ADR 0048](0048-system-apps-and-uninstall.md) (`apps.config`),
[ADR 0049](0049-dangerous-capabilities-and-admin-approval.md) (dangerous
capabilities) and [ADR 0098](0098-sdk-versions-packages-and-manifest-checks.md)
(SDK versions and manifest checks). **Revised on 2026-10-29 by
[ADR 0189](0189-app-sources-trust-and-per-user-install.md)**: per-user
install without an administrator from a trusted source and two-step validation
of system-wide installs replace the answer to Q4; §6 (Store) is described by
[ADR 0188](0188-hack-store-and-app-catalog.md).

## Context

- Today an app only enters the OS when a host is compiled:
  `BuiltInApplications.json` lists the apps, their assemblies go into
  `_framework` and are loaded on demand (`BlazorWebAssemblyLazyLoad`).
  "Install" and "uninstall" (ADR 0048) only turn on or remove an app already
  compiled into the host.
- The `Kazo-ca/HackerOs-AppTemplate` template repository (ADR 0176) produces a
  .NET solution: a windowed app, its `wwwroot/` resources declared in `Assets`
  (with their SHA-256), its generated `manifest.json`.
- Each third-party app lives in its own GitHub repository (for example
  `Kazo-ca/HackerOs-App-Webamp`, `Kazo-ca/HackerOs-App-IRC`). An app that wraps
  a JavaScript project does so as resources of a .NET app.
- The OS runs without a server on GitHub Pages (`hackos.kazo.ca`). An app's real
  network goes through the optional server's proxy and the dangerous
  `network.real.access` capability; without a server it does not exist.
- Checked on 2026-10-28 with an `Origin` header: `raw.githubusercontent.com` and
  `cdn.jsdelivr.net` answer `Access-Control-Allow-Origin: *`; downloading a
  release file (`github.com/<o>/<r>/releases/download/...`) answers `302`
  **without** that header, so a browser `fetch` fails; `api.github.com` limits
  calls without a token to 60 per hour and per IP address.
- The host publish is trimmed (`PublishTrimmed`, `TrimMode=partial`,
  [ADR 0113](0113-trimming-implementation-adjustments.md)): HackerOS and app
  assemblies stay whole, but the .NET framework is trimmed. An assembly that
  arrives after publishing can call a framework method that no longer exists.
- A .NET assembly loaded in the page shares the OS runtime: it can bypass the
  SDK through reflection. Trust in the source is therefore the real barrier,
  not capabilities.

## Decision

Points marked **(Qn)** follow Benoit's answer to question Qn.

### 1. The app package

A **HackerOS app package** is a zip archive with the `.hackapp` extension:

```text
hackeros-package.json      identity, version, list of files and SHA-256
manifest.json              generated manifest (ADR 0010, ADR 0047)
lib/<Assembly>.dll         app assemblies (IL, not WebCIL)
wwwroot/...                resources declared in Assets
```

`hackeros-package.json`:

```json
{
  "appId": "org.kazo.webamp",
  "version": "1.0.0",
  "files": { "lib/Kazo.Webamp.dll": "3f2a…", "manifest.json": "9c41…" }
}
```

SHA-256 values are lowercase hexadecimal, with no prefix. The target SDK is not
repeated: it is the manifest's `sdkCompatibility`. `lib/` only holds the
assemblies the manifest names (entry points and components).

The `HackerOsPack` MSBuild target (`HackerOs.App.Build` package) produces the
package: `dotnet build -c Release -t:HackerOsPack` writes
`<version>/<appId>-<version>.hackapp` and updates `index.json` in
`HackerOsPackageOutput` (by default `bin/hackeros-packages/`), the content of the
`hackeros-packages` branch. The same content gives the same package, so the same
SHA-256. The CI workflow of the `hackeros-solution` template (ADR 0176 §1) will
call it on every `v*` tag.

### 2. Where the OS finds the package **(Q2)**

The CI workflow pushes the package to a dedicated branch of the app's
repository, `hackeros-packages`, with no code history:

```text
index.json                 published versions, newest first, SHA-256 of each package
1.0.0/org.kazo.webamp-1.0.0.hackapp
```

The OS reads `https://raw.githubusercontent.com/<owner>/<repo>/hackeros-packages/index.json`
then the package. No call to `api.github.com` (60 calls per hour limit), no
GitHub release (blocked by CORS in the browser). A GitHub release may carry the
same file for humans; the OS does not read it.

### 3. GitHub as a trusted source **(Q3)**

New machine file `/etc/hackeros/app-sources.config` (`system:system 0644`,
`.config` grammar of ADR 0011):

```text
# Hosts the OS itself contacts to download app packages.
[hosts]
github=raw.githubusercontent.com

# GitHub owners whose apps install without a warning.
[trusted]
github.com/Kazo-ca=true
```

- The download is made **by the OS** (system service), directly with the
  browser's `fetch`, and only to a host listed in `[hosts]`. It goes through
  neither the simulated network nor the proxy, and does not need
  `network.real.access`: no app gets network access.
- A repository whose owner is not in `[trusted]` is refused, unless
  `--allow-untrusted` is given: an administrator then confirms a warning saying
  the app's code runs with the same rights as the OS.
- A startup migration creates the file with these values, commented; an
  administrator edits it with `sudo nano`.

### 4. The `hackpkg` command **(Q4)**

Catalog command `org.hackeros.commands.hackpkg`:

```text
hackpkg install <source>[@<version>] [--allow-untrusted]
hackpkg remove <appId>
hackpkg update [<appId>]
hackpkg list
hackpkg info <source>
hackpkg sources
```

`<source>` accepts `github:Kazo-ca/HackerOs-App-Webamp`,
`Kazo-ca/HackerOs-App-Webamp` and `https://github.com/Kazo-ca/HackerOs-App-Webamp`.
Without a version, the newest one in `index.json`.

Install, remove and update are **machine** operations: `sudo hackpkg install
...`, or the OS authentication agent (ADR 0055 §5). `list`, `info` and
`sources` are allowed to everyone.

### 5. Installation steps

1. Read `index.json`, pick the version, download the package; check its SHA-256
   against `index.json`, then each file against `hackeros-package.json`.
2. Refuse if `appId` is that of a built-in app or of an app already installed
   from another source (`apps.package.built-in`, `apps.package.other-source`); if
   the manifest's `sdkCompatibility` excludes the OS SDK
   (`apps.package.sdk-incompatible`); if the manifest fails the ADR 0098 checks;
   if the app does not run on the main thread (`apps.package.worker-unsupported`:
   a Web Worker cannot load an assembly from the file system).
3. **(Q1)** Check every member reference of the assemblies (methods, fields,
   types) against the loaded runtime, with `System.Reflection.Metadata`: an API
   trimmed out of the OS refuses the installation with the list of missing
   members (`apps.package.missing-api:<member>`).
4. Ask for an administrator (or nothing under `sudo`), after the download and
   the checks: no password is asked for a refused package. The prompt says when
   the source is not trusted. `hackpkg` then shows the app's capabilities; an
   installed app is not a system app, so its dangerous capabilities are asked to
   each user at its first launch (ADR 0049, section 7).
5. Write the files to `/usr/lib/hackeros/apps/<appId>/<version>/` (`root:root`,
   folders `0755`, files `0644`) and the source to `/etc/hackeros/apps.config`,
   new section:

   ```text
   [installed]
   org.kazo.webamp=github:Kazo-ca/HackerOs-App-Webamp@1.0.0
   ```

6. **(Q5)** Log to `/var/log/syslog` and offer to restart the OS ("Restart to
   finish installing", as in ADR 0162): the app appears in the launcher, search,
   "Open with" and the terminal after the restart. The catalog stays frozen
   during a session; immediate addition comes in a separate work item. Example
   line
   (`apps: org.kazo.webamp 1.0.0 installed from github:Kazo-ca/HackerOs-App-Webamp (by=alice)`).

At startup, before the catalog is used, the host reads `[installed]` and the
manifests under `/usr/lib/hackeros/apps/` and builds the catalog with the
built-in apps then the installed apps (an invalid installed app is left out
with a warning, never blocking). On launch, the assembly is
read from `/usr/lib/hackeros/apps/...` and loaded with
`AssemblyLoadContext.Default.LoadFromStream` instead of Blazor's lazy loading;
resources are served by `AppAssetStore` from the same folder. The
`/etc/hackeros/app-sources.config` file is created at startup when it does not
exist.

`hackpkg remove` removes the `[installed]` line and the folder; an instance
already open keeps running until the restart, which removes the app from the
catalog. The user's settings (`~/.config/<appId>/`) stay, as for an
uninstall. `[uninstalled]` keeps its meaning: it removes a built-in app without
deleting anything.

### 6. Next: the HackerOs Store

Outside this ADR. A future "HackerOs Store" system app, in the style of the Play
Store or Microsoft Store, will read the list in the
`Kazo-ca/HackerOs-AppMarketPlace` repository and install through the same
service as `hackpkg`. Note in [`../plans/hackeros-store.md`](../plans/hackeros-store.md),
to pick up once apps are published.

## Answers

Questions asked to Benoit on 2026-10-28; he chose **A** for the first four and **C** for the fifth.

- **Q1. Trimmed framework.** A (recommended): check references at install time,
  refuse with the list of missing APIs. B: the official host no longer trims the
  framework (every app works, a few more MiB to download for everyone). C: A
  now, B if too many apps are refused.
- **Q2. Where to publish the package.** A (recommended): `hackeros-packages`
  branch read through `raw.githubusercontent.com`. B: GitHub Pages of the app's
  repository (only one version, and the repository's page is taken). C: GitHub
  releases (only works with the server, through the proxy). D: jsDelivr
  (`cdn.jsdelivr.net/gh/...`), a third-party service between GitHub and the OS.
- **Q3. Trust.** A (recommended): only `Kazo-ca` repositories without a warning,
  others with `--allow-untrusted` and an administrator. B: all of GitHub without
  a warning. C: none by default, the administrator adds owners.
- **Q4. Scope.** A (recommended): machine-wide installation only, administrator
  required (like `apt`). B: also per user (`~/.local/share/hackeros/apps`),
  without an administrator; any user can then run third-party code in the
  shared runtime.
- **Q5. Immediate addition or after a restart** (asked after reading the code:
  the catalog is built once at startup and read by about fifty files). A: after
  a restart. B: immediately, mutable catalog. C (chosen): A now, B in a separate
  work item later.

## Consequences

- An app can be added to an already published OS, without recompiling the host;
  it appears after the OS restarts.
- Installed code is not isolated from the OS; the list of trusted sources and an
  administrator's approval stand in for that. Isolation (app in a Web Worker,
  [ADR 0070](0070-worker-hosted-processes.md)) remains possible later.
  An app's JavaScript (`wwwroot/` resources) also runs in the OS origin and can
  read its IndexedDB, unless it is served in an isolated iframe.
- Packages are not signed: the SHA-256 values in `index.json` protect against a
  damaged download, not against a compromised repository. Signing remains to be
  decided with the Store.
- Two origins of apps in the catalog: built-in (build) and installed
  (`/usr/lib/hackeros/apps`). Views that list apps must show the source.
- The `hackeros-solution` template gains a publishing workflow; an app
  repository created before must add it.
- The system backup ([ADR 0099](0099-os-backup-and-low-storage.md)) includes
  `/usr/lib/hackeros/apps`.

### Limits of the first version

- The `wwwroot/` resources of an installed app (images, JavaScript, CSS) are
  copied and checked by `AppAssetStore`, but not served at the
  `_content/<assembly>/…` address Blazor uses: an `<img>` or `<script src>` tag
  does not find them. An app that wraps a JavaScript project (Webamp, IRC)
  therefore waits for a work item that serves them (service worker or `blob:`
  addresses).
- An installed app runs on the main thread only.
- An app's dependencies other than the SDK (third-party NuGet packages) are not
  carried: only the assemblies the manifest names go into `lib/`.
- The Blazor Web App host (`test/test`) does not load installed apps; only the
  WebAssembly host does.

## Tasks

- [x] Benoit's answers to Q1 to Q5 (A for the first four, C for Q5, 2026-10-28).
- [ ] Later: adding to the catalog without a restart (Q5 B).
- [x] `HackerOsPack` target in `HackerOs.App.Build` (`pack` command of the manifest generator).
- [ ] Publishing workflow in `hackeros-solution` (`Kazo-ca/HackerOs-AppTemplate` repository).
- [x] `app-sources.config` (reading, creation at startup).
- [x] `[installed]` section in `AppsConfigDocument`; registration in the catalog at startup.
- [x] Loading an installed assembly from the file system, resources through `AppAssetStore`.
- [x] Reference check against the runtime (Q1).
- [x] `hackpkg` command and its docs (`linux-commands.md`, `app-installation.md`).
- [x] Unit tests (sources, package, index, reference check, gateway, command).
- [x] End-to-end test in the browser with a test package served locally ([ADR 0206](0206-serve-installed-app-assets.md)).
- [x] Serve the `wwwroot/` resources of an installed app (see "Limits"): [ADR 0206](0206-serve-installed-app-assets.md).
