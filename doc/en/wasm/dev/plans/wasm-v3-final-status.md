> [Version française](../../../../fr/wasm/dev/plans/wasm-v3-final-status.md)

# Status report of the wasm v3 migration

## Purpose

This document closes the plan [`wasm-v3-completion-plan.md`](wasm-v3-completion-plan.md)
(work item `v3/16-final`, 2026-10-17). It says what was delivered, how the
`v3/base` branch was verified end to end, and what remains open:
known gaps and questions put to Benoit without an answer. For the current
state, it replaces [`old/implementation-status.md`](../old/implementation-status.md).

The original analysis, [`doc/en/wasm/dev/plans/wasm-v3-migration-analyse.md`](wasm-v3-migration-analyse.md),
now carries a section 24 that compares its definition of success with this
state.

## What was delivered

The sixteen work items are merged into `v3/base` (PRs #152 to #167):

| Work item | PR | Decisions | Document |
|---|---|---|---|
| `v3/1-adr` | #152 | ADR 0094 to 0106 | n/a |
| `v3/2-game-removal` | #154 | ADR 0092 | n/a |
| `v3/3-window-model` | #157 | ADR 0095 | [`window-model.md`](../../framework/window-model.md) |
| `v3/4-intents` | #160 | ADR 0096 | [`app-intents.md`](../../framework/app-intents.md) |
| `v3/5-services` | #159 | ADR 0097, 0107 | [`linux-commands.md`](../linux-commands.md) (`systemctl`) |
| `v3/6-sdk-versioning` | #161 | ADR 0098 §1-7 | [`sdk-packages.md`](../../framework/sdk-packages.md) |
| `v3/7-storage-backup` | #163 | ADR 0099, 0110 | [`system-backup.md`](../system-backup.md) |
| `v3/8-settings-security` | #155 | ADR 0100 | [`sensitive-settings.md`](../sensitive-settings.md) |
| `v3/9-sync` | #156 | ADR 0101 | [`synchronization.md`](../synchronization.md) |
| `v3/10-server-proxy` | #158 | ADR 0102, 0108 | [`server-network-relay.md`](../server-network-relay.md) |
| `v3/11-apps` | #162 | ADR 0103 | [`hack-paint.md`](../hack-paint.md), [`theme-editor.md`](../theme-editor.md) |
| `v3/12-localization` | #164 | ADR 0104, 0111 | [`localization.md`](../../framework/localization.md) |
| `v3/13-accessibility` | #165 | ADR 0105, 0112 | [`accessibility.md`](../accessibility.md) |
| `v3/14-trimming` | #166 | ADR 0106, 0113 | [`trimmed-publish.md`](../trimmed-publish.md) |
| `v3/15-sdk-freeze` | #167 | ADR 0098 §8, 0114 | [`building-on-hackeros.md`](../../framework/building-on-hackeros.md) |
| `v3/16-final` | this work item | none | this document |

In numbers, on `v3/base`: 14 system window apps, 68 shell
commands, 6 sample apps, SDK 1.0.0 (about 940 public types listed in the
`PublicAPI.Shipped.txt` files), interface in English and French,
trimmed Release publication with 26.5 MiB of `.wasm` (50.9 MiB before).

The twelve deferred decisions of the analysis (section 20) all have a written
status in ADR 0094: nine settled, three postponed to the plan
"app installation at runtime" (package format and signing, isolation
of untrusted apps, restart after installation).

## Final verification

Done on 2026-10-17 on `v3/base` 46e3d6c (with only the documentation
changes of this work item), in a Linux container, .NET SDK 10.0.402,
Chromium from `/opt/pw-browsers`. GitHub Actions is down across the whole repository
(no runner): this local suite is authoritative.

| Step | Result |
|---|---|
| `dotnet build HackerOs.sln`, Debug then Release | succeeded, 0 warnings, 0 errors |
| Unit and integration tests, 40 projects (`--no-build -c Debug`) | 3,397 passed, 0 failed (including `HackerOs.SdkCompatibility.Tests` 5/5, `HackerOs.Platform.Core.Tests` 1,125/1,125, `HackerOs.Tools.ManifestGenerator.Tests` 67/67) |
| `HackerOs.E2E.Tests` | 16/16 |
| `HackerOs.UI.E2E.Tests` | 24/24 |
| `HackerOs.Templates.E2E.Tests` (including the host that loads the compatibility app 1.0.0) | 6/6 |
| `HackerOs.Pwa.E2E.Tests` | 5/6 on the first run, then 6/6 twice in a row after the fix below |
| `build/publish-trimmed.sh` (merge bar) | PASS: 134 `.wasm` files, 26.4 MiB (8.0 MiB Brotli); login, every window app in the Start menu, pipe in the worker pool, Intranet, sample Blazor site, offline restart |
| `build/verify-hosts.sh` (three hosts) | PASS: published Ecosystem (metrics on, off at build, off by setting), `test/test`, `HackerOs.Server` |

The only code fix of this work item: the PWA test
`Reload_fully_offline_boots_the_app_and_preserves_a_saved_file` clicked the
file row only once; when the explorer list
reloaded right after navigation, the selection was lost and the
"Download" button stayed disabled. `PwaTestSupport.AssertFileContentAsync` takes up
the loop of `SelectFileExplorerEntryAsync` from `HackerOs.UI.E2E.Tests`: click
until the row is selected. This was the "offline" failure
observed on `v3/base` since `v3/12`. Without IPv6, the suite falls back to
`127.0.0.2` since `v3/7` ([`pwa-cache-recovery.md`](../pwa-cache-recovery.md)).

## Series v3.1 and SDK 1.1

Series v3.1 ([`v3-1-series-plan.md`](v3-1-series-plan.md)) is merged
into `v3.1/base`: window geometry (PRs #170, #173), window title
(PR #174), merge of `v3/base` with `passwd` and per-user
associations (PR #175), then publication of SDK **1.1.0** (`v3.1/4-sdk-1-1`:
`Unshipped` moved into `Shipped`, see [`sdk-packages.md`](../../framework/sdk-packages.md#sdk-11)).
The compatibility app compiled as 1.0.0 loads and runs on OS 1.1.

Full verification redone on 2026-10-17 on the work item's branch (same
container, .NET SDK 10.0.402):

| Step | Result |
|---|---|
| `dotnet build HackerOs.sln`, Debug then Release | succeeded, 0 warnings, 0 errors |
| Unit and integration tests, 40 projects (`--no-build -c Debug`) | 3,510 passed, 0 failed (including `HackerOs.SdkCompatibility.Tests` 5/5) |
| `HackerOs.E2E.Tests` | 16/16 |
| `HackerOs.UI.E2E.Tests` | 27/27 |
| `HackerOs.Templates.E2E.Tests` (including the host that loads the 1.0.0 app) | 6/6 |
| `HackerOs.Pwa.E2E.Tests` | 6/6 |
| `build/publish-trimmed.sh` | PASS: 134 `.wasm` files, 26.6 MiB (8.0 MiB Brotli), every window app, worker pool, Intranet, Blazor site, offline restart |
| `build/verify-hosts.sh` (three hosts) | PASS: Ecosystem (three metrics modes), `test/test`, `HackerOs.Server` |

During the merge of `v3/base`, the `kill -9` escalation failed once
(known unstable test, see below); it passes in the full series.

## What remains open

### Known gaps

Series v3.1 ([`v3-1-series-plan.md`](v3-1-series-plan.md)) handled
window geometry (ADR 0116, 0122) and the window title (ADR 0117,
0123); per-user associations (ADR 0121) and `passwd` (ADR 0120) were
done by the `v3/4` and `v3/8` threads in `v3/base`, then brought into
`v3.1/base` (PR #175). SDK 1.1.0 publishes them all (see "Series v3.1"
above).

| Gap | Origin | Effect |
|---|---|---|
| ~~Window geometry is saved but never read back: a window does not reopen at its last position.~~ Fixed by `v3.1/2-window-geometry` (ADR 0116 and 0122: size and maximized state, not the position; [`window-geometry.md`](../window-geometry.md)). | `v3/3` | Deviation from ADR 0095, resolved. |
| A key pressed in an iframe (browser, sites) stays in the iframe; the OS shortcuts do not work there. | `v3/3` | Implementation choice, not validated. |
| ~~No user layer for file and scheme associations.~~ Filled by `v3/4b` (ADR 0121, [`user-file-associations.md`](../user-file-associations.md)). "Show in folder" requires the `apps.launch` capability. | `v3/4` | Closed for associations. |
| `hackwebd` starts at login, not at boot. | `v3/5` | Open question; moving it to boot needs its own ADR. |
| `hackbackup` requires `sudo`; a restore in merge mode also restarts the machine; no IndexedDB v1 fixture (never committed). | `v3/7` | Documented in [`system-backup.md`](../system-backup.md). |
| ~~No `passwd` command and no reset of a password by an administrator.~~ Filled by `v3/8` (PR #172, ADR 0120, [`sensitive-settings.md`](../sensitive-settings.md)). | `v3/8` | Closed. |
| On a single device, the synchronization cursor is shared by all users. | `v3/9` | Documented in [`synchronization.md`](../synchronization.md). |
| The SDK has no API to change a window's title; Hack Paint displays the file name in the app. | `v3/11` | Fixed by `v3.1/3-window-title` (ADR 0117, 0123): `SetWindowTitleAsync`, published in SDK 1.1.0 by `v3.1/4-sdk-1-1`. Closed. |
| Monaco's internal menus (Code Editor) stay in English. | `v3/12` | Open question (default: English). |
| An app compiled against `0.9.0-preview` is refused by OS 1.0. | `v3/15` | Intended (different major version); an app from another repository is rebuilt against 1.0.0. |
| Installation of apps at runtime. | plan, question 1 | Postponed to a separate plan. |

### Questions put to Benoit without an answer

Each thread applied the recommended option in the meantime. Answers are
given in the work item's thread.

| Work item | Question | Default applied |
|---|---|---|
| ~~`v3/4`~~ | ~~Add per-user file associations?~~ Answered by Benoit: do it (PR #171, ADR 0121). | n/a |
| `v3/5` | Should `hackwebd` start at boot? (Keep at login / Boot + reload of user sites / Boot + second instance under the user) | Keep at login |
| `v3/5` | How should the `hackeros-svc` service account be created? (Built-in account / IndexedDB user store / Store + generated `/etc/passwd`) | Built-in account (ADR 0107) |
| ~~`v3/8`~~ | ~~Add `passwd` and reset by an administrator?~~ Answered by Benoit: done (PR #172, ADR 0120). | n/a |
| `v3/10` | Where does the server get an app's `network.real.access` grant from? (Server list / Grants pushed by the OS / Real network turned off for all apps) | Server list (`Proxy:GrantedApps`) |
| `v3/12` | 1. French name "Gestionnaire de serveur web" (keep / English name); 2. a site's storage access buttons (follow the language / force French); 3. Monaco menus (English / language pack, several hundred KiB); 4. names of the built-in themes (English / translated) | 1A 2A 3A 4A |
| `v3/13` | 1. Does the Accessibility page modify the user only or also "All users"; 2. does high contrast cover the theme previews; 3. "Reduce animations" as Auto/On/Off or a simple switch | 1A 2A 3A |
| `v3/14` | Keep whole HackerOS assemblies in the trimmed publication, or trim everything later (~280 interop calls to make safe, ~0.4 MiB Brotli gain)? | Keep whole (ADR 0113) |
| `v3/15` | Remove the three `[Obsolete]` APIs before 1.0? | Remove (done) |
| `v3/15` | Which packages does the 1.0 compatibility promise cover? (SDK only / everything) | SDK only (ADR 0114) |

### Known red or unstable tests

They were already failing or flaky on `v3/base` before this work item; none
failed during this verification, and none is a regression of the
migration:

- `A_command_that_spins_at_once` (`HackerOs.Platform.WorkerHost.Tests`) failed
  on every run on `v3/base` during the day; it passes here, on a
  less loaded machine, which places it among the load-sensitive tests;
- the `kill -9` escalation, `CommandParityTests` (`zip` then `unzip`),
  `CpuAccountingOverheadTests` and `AppInstallationService` are unstable under
  load;
- the offline test of `HackerOs.Pwa.E2E.Tests` failed on `v3/base`
  since `v3/12`; it is fixed by this work item (see above);
- `HackerOs.Pwa.E2E.Tests` publishes the OS with versions `1.0.0-pwatest-v1` and
  `-v2`; the generator then rewrites the committed `manifest.json` files of the apps
  with `sdkCompatibility.minimumVersion` = `1.0.0-pwatest-v2` (85 files). These
  changes must be undone (`git checkout -- wasm2/HackerOs/Apps`) before
  committing after this suite;
- the UI bench for detached windows sometimes starts badly when suites
  run back to back.

## Tasks of this work item

- [x] Re-read plan §4 and list the questions left open in threads `v3/3` to `v3/15`.
- [x] Build the whole solution in Debug and Release.
- [x] Run the unit and integration tests of each project.
- [x] Run the E2E suites in series, the merge bar (`build/publish-trimmed.sh`, SDK compatibility) and `build/verify-hosts.sh` (three hosts).
- [x] Add the final status to `doc/en/wasm/dev/plans/wasm-v3-migration-analyse.md` (section 24).
- [x] Update `old/implementation-status.md`, `project-overview.md`, `sdk-reference.md`, the plan and the index.
- [x] Series v3.1 (`v3.1/4-sdk-1-1`): closed gaps marked, questions answered, full verification redone after SDK 1.1.
