# ADR 0162: restart when the build changed under the page

## Status

Accepted on 2026-10-22.

- **Question put to Benoit** (2026-10-22, thread "NuGet package
  update"): how to prevent an app from refusing to open after a
  recompilation. Proposed choices: (A) the OS detects the new build and offers
  a restart, (B) forbid caching in development, (C) document
  only. Benoit's answer on 2026-10-22: "a and c".

## Context

On-demand apps (ADR 0113, `BuildKnownAssemblyLoaderRegistry`)
fetch their `.wasm` at first launch, under the fingerprinted name that
the page's `dotnet.js` has known since startup
(`HackerOs.Apps.Settings.yesfevpgxl.wasm`). If the origin serves another build
in the meantime (F5 in Visual Studio while a tab stays open, hot
reload, deployment of a new version), this file no longer exists or no longer has
the right fingerprint: the browser refuses the response (integrity check) and
the launch fails with
`download '…/_framework/HackerOs.Apps.Settings.yesfevpgxl.wasm' failed 0
TypeError: Failed to fetch`. Nothing explained to the user that a
reload was enough.

The "A HackerOS update is ready" banner existed
(`EcosystemHostState.UpdateAvailable`) but was never displayed and its
button was disabled.

## Decision

### 1. A load failure triggers a build comparison

- `HostBuildUpdateMonitor` (Platform.Blazor) compares the running build with
  the one the origin is serving now, through an `IHostBuildProbe`.
- A build's identity is the fingerprinted name of `dotnet.js`
  (`HostBuildIdentity.FromMarkup`): the build embeds its startup
  configuration in it, so this name changes as soon as an assembly changes. The name is read
  from the import map of the current page, then from the page requested again without
  cache (`fetch(document.baseURI, { cache: 'no-store' })`).
- An unreachable origin, a page without a fingerprinted `dotnet.js` or a probe
  that throws are not an update: the original error remains (offline,
  server stopped).
- `BuildKnownAssemblyLoaderRegistry` receives the monitor through a new
  constructor. When the transport throws and the build has changed, the result
  is `BuildKnownAssemblyLoadStatus.HostBuildChanged`, with the message
  "HackerOS was updated while it was running: restart it to open this app."
  Without a monitor, nothing changes.

### 2. The OS offers a restart, like after a system update

- The monitor raises `UpdatePending` once. `App.razor` (Ecosystem.Host)
  then displays the existing banner, completed by "Open apps will be closed.",
  and enables its "Restart to update" button.
- The button goes through `SystemPowerService.RestartAfterAsync`: logs flushed,
  session closed (apps receive their cancellation), then page
  reload. No elevation is requested: nothing changes on the machine, we
  only load the already installed files.
- The module `OS/HackerOs.Ecosystem.Host/wwwroot/hostBuild.js` reads the import map
  and requests the page again; the comparison stays in C#.

### 3. Outside the code

The pitfall and its manual fix (restart the app then Ctrl+F5) are described
in [`common-pitfalls.md`](../old/common-pitfalls.md) §9. A development server left running during the recompilation (`dotnet run` without
restart) serves a mix of the two builds: the restarted page then fails
at startup, with the existing error screen. The server must be restarted,
which Visual Studio does at each F5.

## Consequences

- After a recompilation or a deployment, the first app that no longer loads
  displays the banner instead of an unexplained failure; one click restarts on
  the new build.
- The installed PWA (service worker, ADR 0017) keeps its own update
  flow; the monitor only triggers on a real load failure.
- Option B (no cache in development) is not adopted: it required
  replacing Microsoft's DevServer and did not cover the tab left
  open during the recompilation.
