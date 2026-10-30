> [Version française](../../../fr/wasm/dev/pitfalls.md)

# Pitfalls encountered and their solutions

This document summarizes, in broad strokes, the problems that cost time
during the rebuild of HackerOS (phases 1 to 5, web phase, series v3, v3.1,
`exec` and `desk`) and how they were resolved. For each pitfall:
the **symptom** as it is seen, the **cause**, the **solution** (or
workaround) and the detailed source.

The technical detail of some older pitfalls is in
[`old/common-pitfalls.md`](old/common-pitfalls.md) (in English); the exact
build and test commands are in
[`building-and-testing.md`](building-and-testing.md).

## 1. Build and NuGet

| Symptom | Cause | Solution |
| --- | --- | --- |
| `NU1902` on `Microsoft.Build.Tasks.Git` treated as an error | NuGet security advisory on a build-only dependency | `export NuGetAudit=false` **as an environment variable** (not only `-p:`): the `UI.E2E` and `Templates.E2E` suites themselves rerun `dotnet run`/`dotnet pack`, which redo a restore. Without the variable, they all fail with "harness did not become ready". |
| Razor compilation errors in `Apps.Browser` (`ResponseBodyViewer.razor`, `switch`/`case`) although the code is correct | The .NET 10 SDK built by Ubuntu (`/usr/bin/dotnet`, 10.0.113) has a Razor compiler different from the Microsoft SDK | Install the Microsoft SDK (10.0.402 or newer) in `~/.dotnet` and put it first: `export PATH=$HOME/.dotnet:$PATH DOTNET_ROOT=$HOME/.dotnet` ([`building-and-testing.md`](building-and-testing.md)). |
| `NU1301` on the `local-nupkg` source | The local feed folder does not exist on a fresh checkout | `mkdir -p wasm2/HackerOs/artifacts/local-nupkg`. The `nuget.config` of the sample used `\` read literally on Linux; it now uses `/`. |
| `IOException … being used by another process` in `obj/…/compressed/` of Ecosystem | `test/test` and `HackerOs.Server` built at the same time share Ecosystem | Build the solution once, then run the hosts with `--no-build`. |
| `MsBuildIntegrationTests` of the manifest generator blocked forever | The `dotnet build` launched by the test leaves reused MSBuild nodes that hold the redirected output | Run the tests with `MSBUILDDISABLENODEREUSE=1`. |
| A test breaks after merging two parallel branches although each was green | Some tests count things (apps, manifests, commands: `BuildKnownLazyAppsTests`, `EcosystemBootCoordinatorTests`, `CommandParityTests`) and the documents quote those numbers | After each merge, rerun the complete suite and update the numbers in the tests **and** in the docs (`app-model.md`, `linux-commands.md`…). |
| A test refuses an "incompatible" app after an SDK version change | A test manifest hard-coded the SDK version (`"1.0.0"`) | Test manifests use `HackerOsSdk.Version`; refusal tests pin explicit versions. |
| The SDK compatibility package changes for no reason | It was rebuilt | The packages `Tests/fixtures/sdk-compat/packages/*.nupkg` (1.0.0, 1.1.0, 2.0.0) are committed and **never** rebuilt; they prove that apps compiled against a published version still load ([`../framework/sdk-packages.md`](../framework/sdk-packages.md)). |
| `NETSDK1098` when publishing `HackerOs.Server` for a RID with `-p:PublishSingleFile=true` | A property passed with `-p:` on the command line also applies to the referenced WebAssembly project (Ecosystem), which cannot be published as a single file | Put `PublishSingleFile` (conditioned on the RID) in the server's `.csproj`, never as `-p:`; `build/publish-server.sh` only passes `-r` and `--self-contained` ([`installable-server.md`](installable-server.md)). |

## 2. Trimmed publication and WebAssembly

| Symptom | Cause | Solution |
| --- | --- | --- |
| A non-trimmed publication (`-p:PublishTrimmed=false`) contains trimmed assemblies and crashes at runtime | The WebCIL conversion of the .NET SDK is incremental by file dates: it reuses the outputs of the previous trimmed publication | Delete `OS/HackerOs.Ecosystem/obj/Release/net10.0/{linked,webcil,tmp-webcil}` before publishing; `build/publish-trimmed.sh` does it ([ADR 0113](adr/0113-trimming-implementation-adjustments.md), [`trimmed-publish.md`](trimmed-publish.md)). |
| The `IL2110`/`IL2111` warnings appear only in the shipped app, never in tests | Test projects do not run the trimming analyzer | Verify with `build/publish-trimmed.sh` (local merge gate, fails on any `IL…` alert). Do not put `DynamicallyAccessedMembers` on component parameters captured by Razor closures: use `UnconditionalSuppressMessage` on the method, or pass the `Type` in a carrier object. |
| Trimming removes types used through reflection | Incomplete ILLink roots | The HackerOS assemblies and the apps stay whole; only the framework assemblies are trimmed, with roots generated from the manifests (`HackerOs.TrimmerRoots.xml`, [ADR 0106](adr/0106-trimmed-release-publish.md)). |
| Rendering a Blazor window in a Web Worker costs too much memory | Each window worker loads its own .NET runtime: 66.5 MiB measured for 40 MiB allowed | Trial stopped, prototype kept off by default ([ADR 0133](adr/0133-window-rendering-in-worker-verdict.md), [`remote-window-rendering.md`](remote-window-rendering.md)). Heavy computations go through `HackBackground.RunAsync`. |
| The published server answers `404` at `/` and `/index.html` while `dotnet run` works | `OverrideHtmlAssetPlaceholders` leaves `index.html` out of the published static assets manifest: no endpoint serves the page | The `UseOsPageFile` middleware (`OsPageRewrite.cs`) serves `wwwroot/index.html` without cache when no endpoint matches ([`server-hosting.md`](server-hosting.md)). |

## 3. Browser, cache and stale page

| Symptom | Cause | Solution |
| --- | --- | --- |
| An app no longer opens after a rebuild: `download '…/HackerOs.Apps.<Nom>.<empreinte>.wasm' failed … TypeError: Failed to fetch` (SRI integrity error) | The tab keeps the `dotnet.js` of the previous build, which requests files whose hash has changed; the build is correct | Ctrl+F5 (or F12 ▸ Application ▸ Clear site data) and restart the dev server after a rebuild. Since [ADR 0162](adr/0162-restart-after-a-host-build-change.md), the OS detects the new build and offers to restart. |
| Blank page from `test/test` or `HackerOs.Server` launched with `--no-build` ("Static Web Assets are not enabled") | Without a launch profile, the host starts in `Production` and does not serve the assets of referenced projects | `ASPNETCORE_ENVIRONMENT=Development`. |
| The static assets of a referenced app are not found under `_content/<Projet>/…` | A referenced Blazor WebAssembly app project publishes its assets without a prefix | See [`old/common-pitfalls.md`](old/common-pitfalls.md) §1. |
| A damaged PWA cache blocks startup | Corrupted bytes in the service worker cache | Startup guard in `index.html`, `RESET_CACHE` message, single reload ([`pwa-cache-recovery.md`](pwa-cache-recovery.md)). |
| Ctrl+Shift+T and Ctrl+Shift+W do not reach the terminal or the OS | Chrome keeps these shortcuts for itself outside an installed PWA | Known problem: install the OS as a PWA, or use the replacement keys of `keybindings.config` ([`../framework/window-model.md`](../framework/window-model.md)). |
| The PWA tests fail with `SocketException : Address family not supported` | The suite bound to `[::1]` and the container has no IPv6 | Fallback to `127.0.0.2`, a loopback address accepted as a secure context. |

## 4. Tests and E2E

| Symptom | Cause | Solution |
| --- | --- | --- |
| The 16 tests of `E2E.Tests` fail with "harness did not become ready" | The harness launches with `--no-build -c Release` | Build in Release first (`dotnet build HackerOs.sln -c Release` or `Tests/HackerOs.BrowserHarness.Tests -c Release`). |
| Unstable E2E tests when several suites run | The Playwright suites compete for CPU and ports | Run `E2E`, `UI.E2E`, `Pwa.E2E` and `TrimmedPublish.E2E` **one at a time**. |
| Chromium not found for Playwright | Browsers not installed at the expected location | `ChromiumLauncher` falls back to `/opt/pw-browsers/chromium`. Never run `playwright install`. |
| A test reads the terminal output before it is complete | The output of a command in a worker arrives in several writes | Wait for the next prompt (`OutputFinished`) before reading. |
| The Mobile choice test fails intermittently | Writing the setting in `~/.config/hackeros` is not instantaneous (2 s before [ADR 0090](adr/0090-filesystem-path-lookup-in-one-request.md), 235 ms after) | Wait for the switch before reloading. |
| Wrong ARIA states or red axe test | An ARIA attribute bound directly to a `bool` in Razor disappears when it is `false` | Write `"true"`/`"false"` ([`old/common-pitfalls.md`](old/common-pitfalls.md) §8). |

### Unstable tests: causes found and fixed (2026-10-25)

These tests failed intermittently on CI or under load; the cause is fixed
(in the code or in the test), and no test was skipped. In a four-core cloud
container, each one passed 8 to 20 times in a row under load (four CPU loops
in parallel), except `CpuAccountingOverheadTests`, which passes 10 times out
of 10 on an idle machine but can still fail on a saturated one: it is a time
measurement.

| Test | Cause | Fix |
| --- | --- | --- |
| `A_command_that_spins_at_once` (`WorkerHost.Tests`) | A core counted as "free" as soon as a heartbeat arrived one period after the placement; a heartbeat sent while the command's assembly was loading (which yields) was enough on a loaded machine, and the next command was placed behind the loop. | The heartbeat reports how long each process's code has held the thread (`rn`); the core is free only if every hosted process has run its own code for at least one period ([`worker-processes.md`](worker-processes.md)). |
| `Kill_minus_9_of_a_spinning_command…` (`WorkerHost.Tests`) | Three races: the neighbors of a terminated worker exited 137 before the OS recorded the `Killed` reason (no "Killed" from the shell); output received from an abandoned session was not written; the killed command handed control back to the shell before its received output was written. The test also sometimes killed `spin` before it had started. | Neighbors marked killed before their session ends; received output written before the end (bounded); the test waits for `TestCommands.Spinning`. |
| `StaticSitesTests` (under load) | `hackwebd` opened a port and accepted requests before knowing the site of that binding: 503. The test waited for the first open port, not for all bindings (`intranet.hack` then went to an app of the fixture). | Accepting starts once the site table is up to date; the test waits for all bindings. |
| `zip`/`unzip` parity (`CommandParityTests`) | `ZipArchive.CreateEntry` stamps the entry with the machine's real clock: the two runs differed when they fell on either side of a 2 s boundary. | `zip` stamps each entry with the file's modification date, like Info-ZIP. |
| `CpuAccountingOverheadTests` (`ResourceAccounting.Tests`) | One pass of the same scenario varied by a factor of three: tiered compilation recompiled code in the background between passes, and the 300 `Task.Yield` waited for a thread-pool thread. The measured overhead ranged from -9 % to +15 %. | Tiered compilation turned off in this test project, full collection before each pass, single-threaded event loop (like the browser's main thread), order of the two modes alternated, five tries. Measured overhead: about 2 %. |
| `A_child_with_a_parent_exit_grace_period…` (`Core.Tests`) | The test checked that the grandchild was killed at the moment the child disappeared; the code kills it right after, in the same task (ADR 0147). | The test waits for both. |

**Known unstable** tests, not reproduced in 25 runs under load on
2026-10-25: one `AppInstallationService` test (about once in twelve before),
the web worker of `LocalNetworkTests`. Browser side (manual workflow): the
detached window harness of the UI, `FileExplorerDownload` and
`BrowserHomePage` of `UI.E2E` under the load of the complete suite, and two
`Pwa.E2E` tests that wait for the file explorer. Rerun the suite alone before
concluding, and record the cause of any new case here. On GitHub Actions, a
failing test is rerun once and reported as unstable if it passes
([`ci-workflows.md`](ci-workflows.md#flaky-tests-and-retry)): that
signal is the starting point to find its cause.

**Remember when writing a test**: wait for the event that proves the wanted
state (the command runs, all bindings are open, both processes have ended),
never for a sign that comes before it (the process is placed, the first port
is open); never compare output that contains the machine's real time; to
measure time, turn off tiered compilation and compare passes made under the
same conditions.

## 5. Blazor, windows and SDK

| Symptom | Cause | Solution |
| --- | --- | --- |
| Moving or resizing a window does nothing, without error | A subclass overrode `OnAfterRenderAsync` without calling the base: the JavaScript module for moving was never loaded | `WindowAppBase` seals the Blazor lifecycle methods and exposes `OnApp*` hooks ([`../memory/blazor-window-manager-bugs.md`](../memory/blazor-window-manager-bugs.md)). |
| Timers, shutdown delays and the taskbar clock never fire | Nothing advanced the simulated clock in production | `SimulationClockDriver` advances `ManualSimulationClock`. |
| A static SDK facade (`HackUser`…) does not find its context | `BuildRenderTree` and plain `ComponentBase` components run outside the ambient scope | Use `HackUser.Describe(AppContext)` or `AppContext.Catalog` in these places ([`../framework/hack-file-api.md`](../framework/hack-file-api.md)). |
| The build refuses an app with `HOSAPP0030`/`HOSAPP0031` | The app references an internal project or injects a service outside the SDK (for example `IServiceProvider`) | Apps use only the public SDK, verified at build time ([`../framework/app-sdk.md`](../framework/app-sdk.md)). Inject the SDK interface directly. |
| A property added to `AppManifest` disappears from `manifest.json` | The allowlist of the strict serializer was not up to date | Update `AppManifestJsonSerializer` at the same time ([`old/common-pitfalls.md`](old/common-pitfalls.md) §2). |
| Inconsistent site page tests (`AmbientFrameSourceTests`) | Each `.razor` page must declare its `@inherits HackComponentBase` (not in `_Imports.razor`) | Add `@inherits` explicitly in each page. |
| `@await Component.InvokeAsync<T>(…)` does not compile in Razor | The generic `<` is read as markup | Write `@(await Component.InvokeAsync<T>(…))`. |
| ASP.Hack `View("texte")` looks for a view named "texte" | Same overload resolution as ASP.NET Core | Pass a model class or `View(viewName: null, model: x)` ([`../framework/asp-hack-razor-views.md`](../framework/asp-hack-razor-views.md)). |
| `curl -d` to an ASP.Hack form answers 400 | Anti-forgery: each `curl` has its own cookie jar | Expected behavior. |
| xterm.js terminals erase the last cell or have a forced black background | `ESC[K` after a full line erases the last cell; `xterm.css` paints `.xterm-viewport` black | Handled in the C# line discipline and overridden in isolated CSS ([`terminal-emulator.md`](terminal-emulator.md)). |
| An element moved into a secondary screen no longer gets `ResizeObserver` callbacks or keys (`window.addEventListener`) | An observer only follows its own window's layout, and the surface registry only copies listeners put on `document` | Create the observer with `element.ownerDocument.defaultView.ResizeObserver`; listen on `document`, or on each surface's window with the registry's `watch(…)` ([`secondary-screen-desktop.md`](secondary-screen-desktop.md)). |

## 6. IndexedDB and files

| Symptom | Cause | Solution |
| --- | --- | --- |
| IndexedDB database broken after a version upgrade | Schema version number hard-coded | Always reference `HackerOsIndexedDbSchema.CurrentVersion`; version upgrades are tested with fixtures ([`old/common-pitfalls.md`](old/common-pitfalls.md) §3, [`building-and-testing.md`](building-and-testing.md)). |
| "Unknown IndexedDB transaction boundary" banner at startup | A transaction boundary used by a repository was not declared in the schema | Declared; a test now verifies that each `TransactionBoundary` is declared. If the banner comes back, it is a new bug. |
| Writing a setting took 845 ms and 604 transactions | Path resolution walked the storage segment by segment | A single `resolvePath` request (604 → 51 transactions, [`filesystem-path-lookup.md`](filesystem-path-lookup.md)). |
| A `HackWebDb` record with `Id = 0` is stored under key 0 instead of being numbered | Normal IndexedDB behavior with `autoIncrement` | Put `[JsonIgnore(Condition = WhenWritingDefault)]` on `Id` ([`../framework/asp-hack-site-data.md`](../framework/asp-hack-site-data.md)). |
| The server's `GET /api/devices` fails on SQLite (`NotSupportedException` on `ORDER BY`) | SQLite cannot sort a `DateTimeOffset` column in the database | Sort in memory after the query (`AccountService.GetDevicesAsync`). |

## 7. Cloud containers, GitHub and organization

| Symptom | Cause | Solution |
| --- | --- | --- |
| The GitHub Actions all failed (August to October 2026) | No runner available | During this period, a green local build and tests were authoritative. Since 2026-10-24, the workflows run again (`Verify HackerOS WASM`, `Deploy to GitHub Pages`); the Playwright tests are in a separate manual workflow (`verify-browser-tests.yml`). A green local build and tests are still required before merging. PRs then failed on unstable tests (`Kill_minus_9`, `ResourceAccounting`): the workflows rerun failing tests once ([`ci-workflows.md`](ci-workflows.md)). |
| GitHub Actions fails at "Setup .NET 10": `409 Unable to download https://dotnetorigin.blob.core.windows.net/...` | With a channel (`10.0.x`), the install script follows the `aka.ms` link, which pointed to a failing storage | Install an exact version: the `.github/actions/setup-dotnet-sdk` action reads `latest-sdk` and downloads from `builds.dotnet.microsoft.com` ([`ci-workflows.md`](ci-workflows.md#net-sdk-installation)). |
| `dotnet` missing in a cloud container | Nothing is preinstalled | `curl -sSL https://dot.net/v1/dotnet-install.sh \| bash -s -- --channel 10.0`. |
| A cloud thread cannot delete a GitHub branch (403) | Insufficient rights of cloud sessions | Benoit deletes merged branches himself with a prepared command. |
| The received branch name carries a suffix (`…-txf6mn`) | The harness assigns suffixed names | Work on the given branch; check that no orphan branch remains. |
| A thread stops without resuming | Account usage limit reached | The coordinator restarts stopped threads. |
| `pkill -f <motif>` kills the command itself (code 144) | The pattern also matches the command line of the shell | Run `pkill` alone, with a pattern that does not match itself. |
| Wrong base for a work item or unwanted merge into `main` | Several series in parallel | One integration branch `<série>/base` per series, one branch `<série>/<n>-<sujet>` per work item; only Benoit merges into `main` ([`../memory/README.md`](../memory/README.md)). |

## Tasks of this document

- [x] Gather the pitfalls from the plans, reviews, ADRs and project memory.
- [ ] Add here any new pitfall that cost time, in the same PR as its solution.
