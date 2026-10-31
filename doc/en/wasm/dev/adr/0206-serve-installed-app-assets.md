> [Version française](../../../../fr/wasm/dev/adr/0206-serve-installed-app-assets.md)

# ADR 0206: serve the `wwwroot/` resources of installed apps

## Status

Accepted on 2026-10-30 on the recommendation (question Q1 asked to Benoit the same
day, choice A recommended; another answer will give a new ADR). Issue
[#247](https://github.com/Kazo-ca/HackerOs/issues/247), work item 2 of the
[Hack Store plan](../plans/hackeros-store.md), `todo/base` series.

- **Lifts:** the first limit of [ADR 0182](0182-install-apps-from-github.md)
  ("the `wwwroot/` resources of an installed app are not served").
- **Complements:** [ADR 0098](0098-sdk-versions-packages-and-manifest-checks.md) §5
  (hash checked at the first read), [ADR 0131](0131-cross-origin-isolation.md) §2
  (COOP/COEP headers of the service worker), [ADR 0017](0017-pwa-cache-and-offline-strategy.md)
  (service worker cache).

## Context

- An app compiled into the OS references its files at
  `_content/<assembly>/<path>`: Blazor publishes each project's `wwwroot/` at
  that address. The static server serves them.
- An app installed by `hackpkg` arrives after publishing: its files are in
  IndexedDB, under `/usr/lib/hackeros/apps/<id>/<version>/wwwroot/`. The server
  does not have them. `AppAssetStore` reads them (icons), but an image or script
  tag, an `import()` or a `new Worker(...)` does not find them.
- An app that wraps a JavaScript project (an engine in a Web Worker that loads
  scripts and a `.wasm` by relative paths) needs them. So does the Hack Store
  (ADR 0188).
- `index.html` registers no service worker on `localhost` and `127.0.0.1`
  (development, and `HackerOs.Server` run locally).

## Decision

### 1. The same address as a compiled app

A file `wwwroot/x/y.js` of an installed app whose entry point is
`Contoso.Notes.dll` is served at `_content/Contoso.Notes/x/y.js`, as for an app
compiled into the OS. The app's code does not change: it works installed or
compiled. Every form of reference works: tags, `import()`, CSS `url()`,
`new Worker(...)`, relative `importScripts` and `fetch` inside the worker,
`WebAssembly.instantiateStreaming`.

### 2. A cache served by the service worker

- Cache Storage, name `hackeros-installed-apps`, one entry per file, key = the
  absolute address without query, with a `Content-Type` (from the extension) and
  `Cross-Origin-Resource-Policy: same-origin`.
- Both service-worker files of the host load
  `_content/HackerOs.Infrastructure.Browser/installedAppAssets.sw.js`
  (`importScripts`). For a same-origin `GET` under `_content/`, the worker
  answers from this cache when it has the address, **before** the release cache;
  otherwise the request goes its usual way. The query is ignored
  (`?hackeros-worker=N`).
- The published service worker adds its COOP/COEP headers to these responses as
  to the others (ADR 0131): a worker started by an isolated page runs.

### 3. Copy at the app's first launch in the session

- Before loading an installed app's assembly, `InstalledAppAssemblyTransport`
  calls `InstalledAppAssetPublisher`: each resource declared in the manifest is
  read through `AppAssetStore` (SHA-256 checked, ADR 0098 §5) then copied into
  the cache. Once per app and per session.
- Only the resources declared in `Assets` are served. A damaged or missing file
  is not served, its old copy is removed, a line goes to `/var/log/syslog`; the
  app starts anyway (as a compiled app missing a file would). `AppAssetStore`
  marks the app damaged, as before.
- The first copy of the session removes from the cache every address no
  installed app declares any more (app removed or updated before the restart).

### 4. On a development host

On `localhost` and `127.0.0.1`, the OS registers
`service-worker.js?mode=installed-apps` itself at the first launch of an
installed app that has resources, and waits for it to take control (10 s at
most). In this mode, the worker does not fill the release cache and answers only
the addresses of installed apps; it copies the page's COOP/COEP headers (read
once with `HEAD`) onto those responses. `index.html` does not unregister it.
Elsewhere, the usual service worker (registered by `index.html`) is enough.

### 5. Without a service worker

A browser without a service worker (or that refuses it): nothing is copied, a
line `apps: no service worker serves this page, the wwwroot/ files of <id> are
not served` goes to `/var/log/syslog`, the app starts without its files. No
fallback through `blob:` addresses (choice B or C of Q1).

### 6. Hosts

Only the WebAssembly host loads installed apps (ADR 0182): the publication
exists only there. `HackerOs.Server` serves the same OS files, so the same
service worker.

## Question asked to Benoit

**Q1. How to serve an installed app's `wwwroot/`?**
- **A (recommended, taken meanwhile)**: service worker + Cache Storage,
  sections 1 to 5.
- **B**: `blob:` addresses returned by a new SDK API. Works without a service
  worker, but relative paths inside a JS, a CSS or a worker break and the app
  must be written on purpose.
- **C**: A, plus B as a fallback without a service worker.

## Consequences

- An app that wraps a JavaScript project can be installed by `hackpkg`, then by
  the Hack Store.
- An installed app's code still runs in the OS origin (ADR 0182, Consequences);
  so do its scripts.
- The cache is a copy: the truth stays the file under `/usr/lib/hackeros/apps`,
  read and checked again each session.
- On `localhost`, a service worker stays registered after the first installed
  app is launched; it only serves installed apps.
- The system backup (ADR 0099) has nothing new to include: the cache is rebuilt.

## Tasks

- [x] `installedAppAssets.sw.js` and `installedAppAssets.js` (Infrastructure.Browser), imported by both service workers.
- [x] `BrowserInstalledAppAssets` (JSImport), `IInstalledAppAssetCache`, `BrowserInstalledAppAssetCache`, `InstalledAppAssetPublisher`, `InstalledAppAssetContentTypes`.
- [x] Call from `InstalledAppAssemblyTransport`; `index.html` keeps the `installed-apps` mode worker.
- [x] Unit tests (`InstalledAppAssetPublisherTests`).
- [x] Test package `Tests/fixtures/installed-app` and end-to-end tests in `HackerOs.TrimmedPublish.E2E.Tests` (installation by `hackpkg`, module, worker, `importScripts`, image; published service worker).
- [x] Docs: `app-installation.md`, Hack Store plan, `features.md`.
