> [Version française](../../../fr/wasm/dev/building-and-testing.md)

# Build, run and test HackerOS

## Purpose

How to build the `wasm2/HackerOs` solution, run the OS in the browser and
execute the tests, including in a Linux container with no preinstalled tools.
For the code structure, see [`project-overview.md`](project-overview.md).

To publish WASM at the root and legacy under `/legacy/` together, see
[the GitHub Pages guide](github-pages-deployment.md) (independent builds and caches).

## Prerequisites

- **.NET 10 SDK** (`global.json`: `10.0.100`, `rollForward: latestFeature`).
  In a container without .NET:

  ```bash
  curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir ~/.dotnet
  export PATH="$HOME/.dotnet:$PATH"
  ```

- **Node.js** only to regenerate the xterm.js bundle
  ([`terminal-emulator.md`](terminal-emulator.md)) or import icons
  (`Tools/icon-import`). The .NET build does not need it.
  The combined legacy publication separately uses Node 20 and npm; see
  [GitHub Pages](github-pages-deployment.md).
- **Playwright and Chromium** only for the E2E suites.

## Build

All commands run from `wasm2/HackerOs`.

```bash
dotnet build HackerOs.sln
```

The build applies the project rules (warnings as errors, inline assets
forbidden, regenerated manifests, SDK boundary): see
[`project-overview.md`](project-overview.md#rules-checked-at-build). An app
`manifest.json` modified by the build is a change to commit.

The verification workflow explicitly installs `ripgrep` before the additional
Razor source scan; this tool is not guaranteed on GitHub Ubuntu runners.
This scan excludes the host's startup document and only allows the three
existing ASP.Hack script reference lines, at their exact paths. The other
lines of these views and all script references of ordinary components remain
checked.

The public constants of the fixtures use explicit `\n` escapes: multi-line
raw strings inherit the checkout's line endings and produce different values
between Windows and Linux, which invalidates the `PublicAPI.Unshipped.txt`
baselines.

### Resource measurement (ADR 0066)

Two MSBuild properties choose the measurement code shipped by the hosts
(`build/HackerOs.ResourceAccounting.targets`, imported by the three hosts):

| Property | Default | Effect when `false` |
|---|---|---|
| `HackerOsCpuAccounting` | `true` | CPU time measurement (ADR 0067) off; runtime option `HackerOs.ResourceAccounting.Cpu.IsSupported=false` |
| `HackerOsMemoryAccounting` | `true` | memory measurement (ADR 0068) off; option `HackerOs.ResourceAccounting.Memory.IsSupported=false` |

Both `false`: `HackerOs.Platform.ResourceAccounting` is no longer referenced,
its code is not in the binary.

```bash
dotnet build HackerOs.sln -p:HackerOsCpuAccounting=false -p:HackerOsMemoryAccounting=false
```

On the command line (or in a `Directory.Build.props`), the property applies to
the whole graph; in the `.csproj` of `test/test` or of the server alone, it
only turns off that host's runtime option. At startup, the
`HackerOs:ResourceAccounting` section of `appsettings.json` turns a
measurement off without recompiling: see
[`resource-accounting.md`](resource-accounting.md).

### Linux container: known workarounds

| Symptom | Cause | Workaround |
|---|---|---|
| `NU1902` on `Microsoft.Build.Tasks.Git` treated as an error | NuGet security advisory on a build dependency | `export NuGetAudit=false` (or `-p:NuGetAudit=false`) |
| `NU1301` on the `local-nupkg` source | the local feed folder is missing on a fresh checkout | `mkdir -p artifacts/local-nupkg` for `ProjectReference` mode; the package verification workflow fills this feed before its restore. The `nuget.config` path uses `/`, compatible with Windows/Linux. |
| Blank page from `test/test` or `HackerOs.Server` launched with `dotnet run --no-build --urls …` (log: "Static Web Assets are not enabled") | without a launch profile, the host starts in `Production` and does not serve the assets of referenced projects outside a publication | `ASPNETCORE_ENVIRONMENT=Development` |
| `IOException … being used by another process` in Ecosystem's `obj/…/compressed/` | `test/test` and `HackerOs.Server` built at the same time: they share Ecosystem | build the solution once, then launch the hosts with `--no-build` |

## Run the OS

| Host | Command | Usage |
|---|---|---|
| Standalone PWA | `dotnet run --project OS/HackerOs.Ecosystem` | The shipped host, without a server. |
| Debug host | `dotnet run --project test/test` | Browser or IDE WebAssembly debugger ([`old/webassembly-debugging.md`](old/webassembly-debugging.md)). `dotnet run --project test/test -- --test-demo` shows the button that creates the E2E test files ([`old/e2e-test-demo-mode.md`](old/e2e-test-demo-mode.md)). |
| Optional server | `dotnet run --project Server/HackerOs.Server` | Synchronization, accounts, network relay, and the Ecosystem OS served as is in WebAssembly ([`server-hosting.md`](server-hosting.md)). |

Publish the PWA (trimmed Release, see [Publishing a trimmed Release](#publishing-a-trimmed-release)):

```bash
dotnet publish OS/HackerOs.Ecosystem -c Release -o artifacts/ecosystem
python3 -m http.server -d artifacts/ecosystem/wwwroot 8080   # any static server
```

At first startup, the OS asks to create the local administrator profile, then
shows the desktop. The data lives in the browser's IndexedDB: clearing the
site data resets the machine to new.

## Verify the three hosts

One command builds, launches and walks through the three hosts
([`execution-isolation-plan.md`](plans/execution-isolation-plan.md), section 5.2):

```bash
build/verify-hosts.sh                 # everything: build, publications, nine flows
build/verify-hosts.sh --skip-build    # reuses the outputs already built
build/verify-hosts.sh --only server,test-host
```

Prerequisites: .NET 10 SDK (`~/.dotnet` is added to the `PATH` if it exists),
`python3` (static server for the published PWA), `curl`, `setsid`, and a
Chromium that Playwright can launch (in a cloud container,
`/opt/pw-browsers/chromium` is picked up automatically, see
[Chromium for Playwright](#chromium-for-playwright)). The script applies the
Linux workarounds above itself, builds once and then launches the hosts with
`--no-build`, and sets `ASPNETCORE_ENVIRONMENT=Development` for the hosts
launched by `dotnet run` ([`old/common-pitfalls.md`](old/common-pitfalls.md) §4).

Sequence:

1. `dotnet build` (Debug) of `OS/HackerOs.Ecosystem`, `test/test`,
   `Server/HackerOs.Server` and `Tests/HackerOs.HostSmoke.Tests`;
   `dotnet publish` (Release) of Ecosystem, twice (default settings, then
   measurements turned off at build).
2. For each flow, the host starts on a free port of `127.0.0.1`
   (`127.0.0.2` for the two isolation flows) in its own process group:
   published Ecosystem behind a Python static server, the other two through
   `dotnet run --no-build`. The server's database, blobs and backups go to a
   disposable folder: the `Server/HackerOs.Server/hackeros.db` tracked by git
   is never modified.
3. `Tests/HackerOs.HostSmoke.Tests` opens the host in Chromium (fresh
   context): profile creation, reload and "Sign in" screen, desktop (scoped
   styles applied to the taskbar, then to the window frame: computed
   `display: flex`, otherwise the host's `{Project}.styles.css` bundle is
   missing), Terminal with `ps` then `cat /proc/self/status` (values of
   `CpuSource:` and `MemSource:` verified; each output is read once the next
   prompt is displayed, because a command in a Web Worker writes in several
   messages), System Monitor (the terminal's process is listed), closing of
   each window. On the `server` flow, the server icon in the notification area
   must change on its own to "Connected, not identified", then to "Connected"
   after "Identify" and creating an account from its panel (ADR 0134); on the
   other hosts, no server icon. Any console or page error fails the flow, as
   does any `fail:` line or "Unhandled exception" in the host's log (errors of
   an API request, invisible on the browser side).
4. The host is stopped (SIGTERM to the group, SIGKILL after 15 s), then the
   next flow begins. The script exits with a non-zero code at the slightest
   failure and prints one line per step:

```text
== HackerOS host verification (9d26a77) ==
build HackerOs.Ecosystem         PASS
build test                       PASS
build HackerOs.Server            PASS
build HackerOs.HostSmoke.Tests   PASS
publish ecosystem                PASS
publish ecosystem-build-off      PASS
ecosystem                        PASS  23s
ecosystem-build-off              PASS  21s
ecosystem-settings-off           PASS  21s
test-host                        PASS  38s
server                           PASS  34s
RESULT: PASS
```

| Flow | Host | Measurements (ADR 0066) | Expected `CpuSource:` / `MemSource:` |
|---|---|---|---|
| `ecosystem` | published PWA | build and default setting | `measured` |
| `ecosystem-build-off` | PWA published with `-p:HackerOsCpuAccounting=false -p:HackerOsMemoryAccounting=false` | off at build; no `HackerOs.Platform.ResourceAccounting*` must be published | `simulated` |
| `ecosystem-settings-off` | same publication as `ecosystem`, `wwwroot/appsettings.json` rewritten with `Enabled: false` for `Cpu` and `Memory` | off at startup | `simulated` |
| `ecosystem-limits-off` | same publication as `ecosystem`, `wwwroot/appsettings.json` rewritten with `HackerOs:ResourceLimits:Enabled: false` (ADR 0125) | build and default setting | `measured` |
| `ecosystem-oom` | same publication as `ecosystem`, `wwwroot/appsettings.json` rewritten with `HackerOs:Hardware:MemoryMiB` = `${OOM_MEMORY_MIB:-512}` (ADR 0142 §11) | build and default setting | `measured` |
| `ecosystem-isolated` | same publication as `ecosystem`, served on `127.0.0.2`: the service worker registers and sets COOP/COEP (ADR 0131, 0143) | build and default setting | `measured` |
| `ecosystem-isolation-off` | same, `wwwroot/appsettings.json` rewritten with `HackerOs:CrossOriginIsolation` = `off` | build and default setting | `measured` |
| `test-host` | `test/test` | host setting (on) | `measured` |
| `server` | `HackerOs.Server` (serves the Ecosystem OS in WebAssembly, ADR 0130) | Ecosystem `appsettings.json` (on) | `measured` |

On a tree without the switches of ADR 0066
(`build/HackerOs.ResourceAccounting.targets`, `HackerOs:ResourceAccounting`
section of `appsettings.json`), the two `-off` flows are marked `SKIP` and the
sources are not verified.

Each flow also verifies the process limits (`exec/2`,
[`process-limits.md`](process-limits.md)): the `# enforcement:` line of
`/proc/self/limits`, then `sh -c 'ulimit -S -t 1; stress --yield'` which must
exit with 152 and "CPU time limit exceeded" where the CPU is measured, and
normally elsewhere. In `ecosystem-limits-off`, `ulimit -n 100` must answer
"cannot modify limit".

Each flow verifies cross-origin isolation (`exec/9`,
[`worker-processes.md`](worker-processes.md#cross-origin-isolation-adr-0131-adr-0143)):
`crossOriginIsolated` in the page, the `cross-origin-isolated:` line of
`/proc/hackeros/workers`, `sample-cmd --processes` (synchronous answer if the
page is isolated, refusal otherwise) and `seq 30000 | wc -l`. Isolated:
`ecosystem-isolated`, `test-host` and `server` (middleware). Not isolated: the
Ecosystem flows on `127.0.0.1` (no service worker on this address) and
`ecosystem-isolation-off`. A flow that rewrites `appsettings.json` recomputes
its fingerprint in `service-worker-assets.js`, otherwise the service worker
installation fails on `127.0.0.2`.

The `ecosystem-oom` flow (`exec/3`, [`oom-killer.md`](oom-killer.md))
enables the OOM killer through `sudo tee /etc/hackeros/memory.config`,
launches `stress --vm 1 --vm-bytes` computed from `/proc/meminfo`, waits for
exit 137 and "Killed", then verifies the `Out of memory: Killed process` line
of `dmesg` and `oom_kill 1` in `/proc/vmstat`.

Logs, publications and screenshots of failures:
`artifacts/verify-hosts/` (ignored by git). Without `HACKEROS_SMOKE_URL`,
which the script sets, the startup test is marked "skipped": a
`dotnet test HackerOs.sln` does not depend on it. To run it against a host
started by hand:

```bash
HACKEROS_SMOKE_URL=http://127.0.0.1:5000 dotnet test Tests/HackerOs.HostSmoke.Tests \
  --logger "console;verbosity=detailed"
```

Optional variables: `HACKEROS_SMOKE_HOST` (displayed name),
`HACKEROS_SMOKE_EXPECT_CPU_SOURCE` / `HACKEROS_SMOKE_EXPECT_MEM_SOURCE`
(value expected in `/proc/self/status`), `HACKEROS_SMOKE_ARTIFACTS`
(screenshot folder), `HACKEROS_SMOKE_EXPECT_ISOLATION` (`yes` or `no`).

Each `rebuild/5-*` PR pastes the output of `build/verify-hosts.sh`.

## Hosting and isolation headers

The OS uses cross-origin isolation when the page has it (ADR 0131, 0143) and
works without it. `HackerOs:CrossOriginIsolation` (`auto`, `on`, `off`) in
each host's `appsettings.json`; `auto` equals `on` since the `exec/9` audit.

- **Ecosystem on static hosting**: nothing to configure. The PWA's service
  worker adds `Cross-Origin-Opener-Policy: same-origin` and
  `Cross-Origin-Embedder-Policy: credentialless` (`require-corp` for a browser
  that ignores `credentialless`, with a single reload). A secure context is
  required (HTTPS, or a loopback address other than `localhost`/`127.0.0.1`,
  on which `index.html` does not register the service worker): locally, serve
  the publication on `127.0.0.2`.
- A host that already sets these headers also works; a proxy that strips them
  gives the exact fallback (`/proc/hackeros/workers` says why).
- **`test/test` and `HackerOs.Server`** set the headers themselves (middleware
  in `Program.cs`).
- After modifying a publication's `wwwroot/appsettings.json`, recompute its
  fingerprint in `service-worker-assets.js` (see `rehash_settings` in
  `build/verify-hosts.sh`).

## Publishing a trimmed Release

`dotnet publish` of the Ecosystem in Release is trimmed by ILLink
(`PublishTrimmed=true`, `TrimMode=partial`, ADR 0106 and 0113): the framework
is trimmed, the HackerOS assemblies and the apps stay whole. One command
publishes, measures and tests this publication ([`trimmed-publish.md`](trimmed-publish.md)):

```bash
build/publish-trimmed.sh             # publication, sizes, E2E tests of the publication
build/publish-trimmed.sh --compare   # also publishes without trimming and shows both sizes
build/publish-trimmed.sh --skip-build
```

| Ecosystem publication (2026-10-17) | `.wasm` files | Size | Brotli |
|---|---|---|---|
| before `v3/14` (`PublishTrimmed=false`) | 273 | 50.9 MiB | 13.5 MiB |
| after `v3/14` | 134 | 26.5 MiB | 8.0 MiB |

The script fails on any `IL…` warning, serves the publication on `127.0.0.1`
and on `127.0.0.2` (for the service worker) and runs
`Tests/HackerOs.TrimmedPublish.E2E.Tests` (four tests, about 3 min). It is a
local merge gate since `v3/14`. Without `HACKEROS_TRIMMED_URL`, the suite is
ignored by `dotnet test HackerOs.sln`.

Pitfall: the SDK's WebCIL conversion is incremental by file dates; a
`-p:PublishTrimmed=false` publication made after a trimmed publication may
reuse the trimmed assemblies. First delete
`OS/HackerOs.Ecosystem/obj/Release/net10.0/{linked,webcil,tmp-webcil}` (the
script does this).

## Producing the local packages

```bash
build/pack-local.sh            # or build/pack-local.ps1 on Windows
```

Packs in Release each project that imports `Platform/Packaging.props` (SDK,
platform, base apps, `HackerOs.App.Build`, `HackerOs.Ecosystem.Host`,
`HackerOs.Templates`) into `artifacts/local-nupkg`, at the version of
`build/Versions.props`, and removes this version from the global NuGet cache
so that another repository does not receive old bytes. Nothing is published to
nuget.org. Details: [`sdk-packages.md`](../framework/sdk-packages.md).

## Testing

The automatic verification (every PR, push to `main` and `*/base`) runs the
non-browser tests; see [`ci-workflows.md`](ci-workflows.md). After a Release
build, the same selector is used locally:

```powershell
./build/run-test-suite.ps1 -Suite NonBrowser
./build/run-test-suite.ps1 -Suite Browser -ListOnly
./build/run-test-suite.ps1 -Suite Browser
```

The script chooses the projects referencing `Microsoft.NET.Test.Sdk`, then
separates those depending on `Microsoft.Playwright` or on the shared E2E
support. It runs the projects sequentially, keeps all TRX results and fails if
a project fails. It expects already built assemblies (`--no-build`,
`--no-restore`). Currently, 41 projects are non-browser and six use
Playwright. A new Playwright project is thus automatically excluded from the
non-browser check.

`-RetryFailedTests 1` (used by GitHub Actions) reruns only the failing tests
once and reports as flaky a test that passes on retry; without this
parameter, the script stays strict.

In GitHub Actions, the Playwright suites are launched only through
**Verify HackerOS browser tests** (`verify-browser-tests.yml`): every night
on `main` when `main` changed, or by hand (`workflow_dispatch`); see
[`ci-workflows.md`](ci-workflows.md#browser-tests-every-night). They do not belong to the Pages deployment.
The conditional scenarios for already-launched hosts or for the trimmed
publication keep their enabling variables documented below.

To explicitly request all suites, including Playwright:

```bash
dotnet test HackerOs.sln
```

| Suites | Content |
|---|---|
| `Tests/*.Tests` (non-E2E) | Unit and component tests (xUnit, bUnit), one project per code project. Fast, no browser. |
| `Tests/HackerOs.AppSdk.Web.Tests` | ASP.Hack: router, pipeline of a site compiled with the route generator, generator driven on snippets (`HOSWEB*`), host page and `blazor.boot.json` of a Blazor site. |
| `Tests/HackerOs.AppSdk.Web.Blazor.Tests` | Blazor sites: router (`HackSiteRouter`), tab navigation manager, `HackWebSite`. |
| `Tests/HackerOs.Samples.Intranet.Tests` | In-memory Intranet sample (`HackWebHost.CreateApplication`): `.cshtml` and `.razor` views, `wwwroot`, session, anti-forgery, form CRUD, REST API, host page of the Blazor dashboard. The flow in Chromium is `BrowserIntranetTests` (UI E2E). |
| `Tests/HackerOs.Tools.ManifestGenerator.Tests` | Real MSBuild builds of the `Tests/ManifestGenerator.Fixtures.*` projects: generation, aggregation, SDK boundary. Slower. |
| `Tests/HackerOs.E2E.Tests`, `HackerOs.UI.E2E.Tests`, `HackerOs.Pwa.E2E.Tests` | Playwright against the OS launched in Chromium. Known pitfalls: [`old/common-pitfalls.md`](old/common-pitfalls.md) §4 to §6. |
| `Tests/HackerOs.Templates.E2E.Tests` | `dotnet new` templates (ADR 0098 §6): packs everything with `build/pack-local.sh` (or takes the feed from `HACKEROS_LOCAL_FEED`), generates and builds each template outside the repository with an isolated NuGet cache, then launches the generated host in Chromium with a generated windowed app and command, and another that verifies that a host referencing the apps built against SDKs 1.0.0 and 1.1.0 does not build (`HOSAPP0021`), then that with the app built against SDK 2.0.0 it starts, shows `HackFileView` in its window and runs its command (ADR 0114, ADR 0132 §1, ADR 0161 §3). |
| `Tests/HackerOs.SdkCompatibility.Tests` | Apps built against SDKs 1.0.0, 1.1.0 and 2.0.0, kept in `Tests/fixtures/sdk-compat` (ADR 0114 §4, ADR 0132 §1, ADR 0161 §3): the catalog refuses the 1.x apps (`catalog.sdk.incompatible`) and admits the 2.0 app; for the latter, references, JIT compilation of each method and execution of the command against the current SDK (`build/Versions.props`). No browser, run by `dotnet test HackerOs.sln`. |
| `Tests/HackerOs.HostSmoke.Tests` | Startup of an already launched host, driven by `build/verify-hosts.sh` (see above); "skipped" without `HACKEROS_SMOKE_URL`. |

Test a single project or a single test:

```bash
dotnet test Tests/HackerOs.AppSdk.Tests
dotnet test Tests/HackerOs.Platform.Core.Tests --filter "FullyQualifiedName~ShellInterpreterTests"
```

### Chromium for Playwright

Each version of `Microsoft.Playwright` expects a specific revision of
Chromium (or Google Chrome for `Channel = "chrome"`). All suites launch the
browser through `ChromiumLauncher` (`Tests/HackerOs.E2E.Support`):

1. `HACKEROS_CHROMIUM_EXECUTABLE`, if set, is used as is;
2. otherwise, the launch planned by the test;
3. if it fails, retry with `$PLAYWRIGHT_BROWSERS_PATH/chromium` then
   `/opt/pw-browsers/chromium` (the cloud containers' link), without a channel.

In a cloud container, **never run `playwright install`**: the revision
provided under `/opt/pw-browsers` serves as the fallback.

To test an app without an OS, `Tests/HackerOs.Tests.Support` provides
`MinimalAppExecutionContext` and fake gateways (files, clock, logs, processes,
intents, environment, shell). Example: see the
[SDK guide](../framework/sdk-developer-guide.md#8-testing-an-app).

### IndexedDB version upgrade fixtures

ADR 0099 §4. `Tests/fixtures/indexeddb/` contains a dump of the `hackeros`
database for each schema version already published.
`IndexedDbUpgradeFixtureTests` (`HackerOs.UI.E2E.Tests`) recreates each
database at its old version with `indexedDB.open(name, oldVersion)`, from a
page of the same origin that does not start the OS (served by Playwright
itself; waiting for the host is done over HTTP, because opening the OS page
would create a fresh v4 database), then starts the current OS and verifies:

- the "Sign in" screen (not the "Create administrator" welcome) and login with
  the administrator password from the dump: the account survived;
- `cat` of the dump's file in the Terminal: entries, links and content;
- `cat /etc/hackeros/appearance.config` contains `accent=cyan`: the setting
  stored in the old `settings` store was picked up by the migration to files
  (ADR 0044);
- the database is at version 4 with `serverConnection`, `syncCursors` and
  `syncRecordState`.

`IndexedDbUpgradeFixtureCatalogTests` (`HackerOs.Infrastructure.Browser.Tests`,
no browser, hence in every ordinary run) verifies that a dump exists for each
version from 2 to `CurrentVersion - 1` and that it matches the schema
(database name, version, known stores, key path, auto-increment,
administrator present).

| File | Version | Commit started | Production |
|---|---|---|---|
| none | 1 | none | **No dump.** Version 1 existed before any commit of the repository: the root commit `fdca68e` already declares `CurrentVersion = 2`. No build can produce it and a reconstructed dump would be invented. Step 1 of the plan remains tested by the creation of a fresh database (each empty startup). |
| `hackeros-v2.json` | 2 | `e46e897` (last commit at v2, before `7a20401`) | OS actually started |
| `hackeros-v3.json` | 3 | `7a20401` (only commit at v3, before `2481d67`) | OS actually started |

Each dump contains `databaseName`, `version`, `producedFrom` (commit, method,
host, date), `expectations` (administrator identifiers, file path and content,
expected setting) and `objectStores`: for each store, `keyPath`,
`autoIncrement`, `indexes` and `records` (plus `keys` for a store without a
key path; none for now).

**Procedure** (used on 2026-10-17 for v2 and v3):

1. Extract the old commit outside the repository:
   `git archive <commit> wasm2/HackerOs | tar -x -C <folder>`.
2. Commits before `2fcccba` (2026-08-17) do not build as is:
   `Infrastructure/HackerOs.Infrastructure.Browser/Backup/` was excluded by the
   `Backup*/` rule of the Visual Studio `.gitignore`. Put back there
   `IndexedDbBackupService.cs` taken from `2fcccba`
   (`git show 2fcccba:wasm2/HackerOs/Infrastructure/HackerOs.Infrastructure.Browser/Backup/IndexedDbBackupService.cs`).
3. At that time, the standalone Ecosystem host rendered nothing
   (`RootComponents.Add<App>` commented out): start `test/test`.
   `dotnet build test/test/test.csproj -c Release`, then
   `ASPNETCORE_ENVIRONMENT=Development dotnet run --no-build -c Release --project test/test/test.csproj --urls http://127.0.0.1:5301`.
   The current .NET 10 SDK is enough (same `global.json`).
4. `node Tests/fixtures/indexeddb/produce-fixture.js http://127.0.0.1:5301/ <version> <commit> Tests/fixtures/indexeddb/hackeros-v<version>.json <screenshot-prefix>`
   (`PLAYWRIGHT_MODULE=/opt/node22/lib/node_modules/playwright` and
   `CHROMIUM=/opt/pw-browsers/chromium` in a cloud container). The script, in a
   blank Chromium context, creates the `admin` administrator (password
   `Fixture-v<version>-pass`), uploads `~/Documents/upgrade-note.txt` through
   the File Explorer (the text editor of `7a20401` does not save a new file),
   chooses the Cyan accent in Settings, then dumps all the stores and refuses a
   dump whose version is not the expected one.
5. Run `IndexedDbUpgradeFixtureTests` and
   `IndexedDbUpgradeFixtureCatalogTests`; add the version to the first one's
   `[InlineData]`.

**Rule:** any work item that increases `HackerOsIndexedDbSchema.CurrentVersion`
adds the dump of the previous version, produced by this procedure from the
last commit of that version, and adds it to the E2E test.
`IndexedDbUpgradeFixtureCatalogTests` fails as long as it is missing.

```bash
dotnet test Tests/HackerOs.UI.E2E.Tests -c Release --no-build --filter "FullyQualifiedName~IndexedDbUpgradeFixtureTests"
```

### Accessibility (ADR 0105)

Two classes of `HackerOs.UI.E2E.Tests` run axe-core (Deque.AxeCore.Playwright);
a serious or critical violation fails, and each surface leaves a screenshot in
`artifacts/playwright/accessibility/`:

- `AccessibilityAndVisualCoverageTests`: desktop, launcher, explorer, Settings, Code Editor,
  Hack Paint, and a mobile screen;
- `AccessibilityPreferencesTests` (`v3/13`): Terminal, browser, calculator, system
  monitor, Theme Editor, Settings > Backup and > Accessibility, each without then with
  high contrast; a keyboard-only flow (launcher, `` Alt+` ``, `Alt+F7`, `Alt+Q`, visible
  focus); and text size at 200% without overflow. See
  [`accessibility.md`](accessibility.md).

```bash
dotnet test Tests/HackerOs.UI.E2E.Tests -c Release --no-build --filter "FullyQualifiedName~AccessibilityPreferencesTests"
```

### Known state

- **GitHub Actions**: with no runner from August to 23 October 2026, the
  workflows have been running again since 2026-10-24 and have verified every PR
  to `main` and `*/base` since 2026-10-25 ([`ci-workflows.md`](ci-workflows.md);
  Playwright every night on `main` in `verify-browser-tests.yml`). A green local
  build and tests are still required before merging.
- In a cloud container, the Playwright suites run on the Chromium of
  `/opt/pw-browsers` ([fallback](#chromium-for-playwright)). State on
  2026-10-16 (after `dotnet build HackerOs.sln -c Release`, one suite at a
  time):
  - `HackerOs.E2E.Tests`: 16/16;
  - `HackerOs.UI.E2E.Tests`: 10/10 since `rebuild/5-ui-a11y-mkdir`, 15/15 on
    `web/base` since its port (`web/13`, 2026-10-17), 18/18 with the two
    [IndexedDB version upgrade](#indexeddb-version-upgrade-fixtures) tests
    (`v3/7`, 2026-10-17, about 1 min 20 s each), 20/20 after the rebase of
    `v3/7` onto `v3/3` with `LowStorageSpaceTests` (Chromium quota lowered,
    [`system-backup.md`](system-backup.md)), 24/24 with the three accessibility
    tests (`v3/13`, 2026-10-17, about 16 min 30 s)
    (2026-10-16: contrast of `--hos-text-muted` of the HackerOS theme, ARIA
    states written as `"true"`/`"false"`, see
    [`old/common-pitfalls.md`](old/common-pitfalls.md) §8; the Mobile choice
    test waits for the switch before reloading, because writing the setting to
    `~/.config/hackeros` is not instantaneous: about 2 s before `web/15`,
    235 ms since, see
    [`filesystem-path-lookup.md`](filesystem-path-lookup.md));
  - `HackerOs.Pwa.E2E.Tests`: the suite binds to `[::1]` (the service worker
    refuses `127.0.0.1` and `localhost`); without IPv6 it has fallen back since
    `v3/7-storage-backup` to `127.0.0.2`, a loopback address and secure context
    that the `index.html` filter lets through (before: 0/5,
    `SocketException: Address family not supported by protocol`). It has 6
    tests: the test that verified that corrupted cache bytes were served as is
    is replaced by two cache recovery tests (online, offline then
    reconnection), see [`pwa-cache-recovery.md`](pwa-cache-recovery.md). State
    on 2026-10-17: 6/6 alone; `Reload_fully_offline_boots_the_app_and_preserves_a_saved_file`
    failed once ("Download" button left disabled) while another Playwright
    suite was running in parallel, then passed alone
    ([`old/common-pitfalls.md`](old/common-pitfalls.md) §6). On rebased `v3/7`,
    with no other suite in parallel, three runs give 5/6: sometimes this test
    ("Download" button), sometimes
    `Update_to_a_new_release_purges_the_old_cache_and_preserves_compatible_data`
    (saved file not yet listed by the explorer after 20 s); both pass together
    or alone on the next run. **Flaky, to be stabilized**: the wait is on the
    file explorer, not on the cache.
- Phase 5: each `rebuild/5-*` PR pastes the output of
  `build/verify-hosts.sh` ([Verify the three hosts](#verify-the-three-hosts)).
- A test of `AppInstallationService` is flaky (about one in twelve), on the
  base too.

## Tasks of this document

- [x] Prerequisites and .NET installation in a container.
- [x] `HackerOsCpuAccounting` and `HackerOsMemoryAccounting` properties (ADR 0066).
- [x] Solution build and known Linux workarounds.
- [x] Launching the three hosts and publishing the PWA.
- [x] Test suites, filtering, app testing tools, known state.
- [x] `build/verify-hosts.sh`: the three hosts, the measurement combinations, the startup test (`rebuild/5-host-smoke`).
- [x] Fallback of the Playwright suites to the container's Chromium and their state in a container.
- [x] Local packages (`build/pack-local.sh`) and template test (v3/6).
- [x] Trimmed Release publication, sizes before and after, `build/publish-trimmed.sh` (`v3/14`, ADR 0106 and 0113).
- [x] IndexedDB version upgrade fixtures (ADR 0099 §4, `v3/7`):
  - [x] schema history: v1 absent from the repository, v2 up to `e46e897`, v3 at `7a20401`;
  - [x] v2 and v3 dumps produced by starting the OS of those commits (`produce-fixture.js`);
  - [x] E2E test per dump (account, file, setting, version 4);
  - [x] non-browser check of the rule "one dump per previous version".
