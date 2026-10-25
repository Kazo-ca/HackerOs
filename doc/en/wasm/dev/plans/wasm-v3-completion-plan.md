> [Version française](../../../../fr/wasm/dev/plans/wasm-v3-completion-plan.md)

# End of the wasm v3 migration: work item plan

**Status:** proposed on 2026-10-17, **approved by Benoit on 2026-10-17**
(answers to the five questions, section 7). The first work item is `v3/1-adr`.
**Finished on 2026-10-17**: the sixteen work items are merged into `v3/base`;
the status report, the final verification and what remains open are in
[`wasm-v3-final-status.md`](wasm-v3-final-status.md).

This plan finishes what the original analysis
[`doc/en/wasm/dev/plans/wasm-v3-migration-analyse.md`](wasm-v3-migration-analyse.md)
(2026-08-01) asked for and which is still not done. It relies on two
surveys made on 2026-10-16 on `web/base` (07c9c77) and rechecked on
2026-10-17 on `web/base` (34952e2): nothing changed for these points during the
web phase.

- `/mnt/project-files/wasm-v3/wasm-v3-remaining-work.md` (23 numbered gaps,
  cited here as "R1" to "R23");
- `/mnt/project-files/audits/wasm-v3-migration-analysis-gaps.md`.

## 1. Prior decision: **No game in this repository**

This repository builds **the platform**: the simulated OS, its SDK and its base apps
([ADR 0135](../adr/0135-no-game-in-this-repository.md), which replaces ADR 0092 and ADR 0023).

Consequences for this plan:

- wave 6 of the analysis is **removed**; it is neither to be done here nor
  counted as a gap;
- the old out-of-scope domain code (three projects and a capability) is
  removed by work item `v3/2`;
- another repository that uses the ecosystem goes through the **generic
  SDK extension points**;
- the ability of another repository to consume the ecosystem (packages, versions,
  templates) becomes an objective of this plan: work items `v3/6` and `v3/15`.

## 2. What is not taken up

| Item | Why |
|---|---|
| Wave 6 (`P4-W6-*`) | Out of scope (section 1). |
| ShopZone site (`ecommerce-controller.ts`) | Out of scope (section 1); it was not even registered in `src/`. |
| App/device settings scopes, roaming, settings sync | Replaced by ADR 0044. |
| Atomic rejection of an invalid settings document | Replaced by ADR 0044 §5. |
| Enabling/disabling an app | Replaced by uninstall/reinstall (ADR 0048). |
| Permissions granted per user | Replaced by ADR 0049. |
| Recovery interface if Settings and Terminal are disabled (deferred decision no. 12) | Moot: system apps cannot be uninstalled (ADR 0048). Noted in the ADR of work item `v3/1`. |
| Manual accessibility checklist | Dropped by Benoit; only automated tests (`v3/13`). |
| Follow-ups of phase 5 (quotas and OOM killer, window apps in workers, workers on the Server host, COOP/COEP) | Separate plan (thread "Plan: follow-ups of step 5"), to be done **after** this migration (Benoit, 2026-10-17). |
| Installation of apps at runtime (R1, R2, deferred decisions no. 2 and no. 10) | Separate plan later (question 1). |

## 3. Principles

1. **Mimic the Metal.** The language follows `LANG` and `/etc/locale.conf`; a
   synchronized deletion is a tombstone; a service has an
   `active`/`failed` state as under systemd; a backup is an archive that can
   be copied.
2. **Public SDK only** for base apps (ADR 0055); any new
   possibility goes through a `Hack*` facade documented in `sdk-reference.md`.
3. **Designed for a third-party repository.** Each addition to the SDK must be able to serve
   another repository without knowing anything about it.
4. **The three hosts stay operational** after each work item
   (`build/verify-hosts.sh`) and the Ecosystem works without a server.
5. **ADR before code**, documentation and `docs/README.md` index up to date in
   each work item.
6. **Merge bar:** build and tests green locally, as long as GitHub
   Actions no longer runs (billing block on the account side, outside this plan).

## 4. Work items

Integration branch: `v3/base`, created from `web/base` after the merge of
`web/12` to `web/15` (question 5). Benoit decides when `web/base` goes back
into `main`. Each work item starts
from `v3/base`, is named `v3/<n>-<subject>` and its PR targets `v3/base`. Nothing is
merged into `main` without Benoit.

| Branch | Content | Gaps covered | Depends on |
|---|---|---|---|
| `v3/1-adr` | ADRs for work items 3 to 15 (numbers taken from the first free one). Also records the moot deferred decisions (no. 12) and the maximum file size already set by `FileContentStoragePolicy` (16 MiB, blocks of 256 KiB, analysis §20.5). | R22 (in part) | approval |
| `v3/2-game-removal` | Removal of the old out-of-scope domain code (three projects) from the solution and from the SDK boundary rules, and of its capability (32 → 31 capabilities); docs (`project-overview`, `capabilities`, `sdk-reference`, `app-sdk`). | n/a | `1` |
| `v3/3-window-model` | Window events in `WindowAppBase` (focus, loss of focus, minimize, maximize, restore, resize); initial, minimum and maximum sizes declared in `AppDefinition` instead of hard-coded `360×240` (`WindowLaunchCoordinator.cs:77`); keyboard operations from ADR 0009: Alt+Tab, Alt+F4, Super+arrows, move and resize with the keyboard (window menu Alt+Space). | R7, R8 | `1` |
| `v3/4-intents` | Real `RevealFileIntent` (the explorer opens the folder and selects the file); `Edit` reachable through `IAppIntentGateway`; `OpenUri` intents (default browser, `xdg-open`) and `ShowAppSettings`; intents declared by apps and routed by the dispatcher instead of `intent.unknown`; `xdg-open` command. | R6 | `1` |
| `v3/5-services` | Service health (`ServiceHealth`: `active`, `activating`, `failed`, reason) reported by `HackServiceController`, `systemctl status` and the task manager; startup at OS boot (before login) or at login, declared in the manifest; clear message when uninstalling an app stops an active process. | R9, R23 | `1` |
| `v3/6-sdk-versioning` | Versions on the SDK libraries; NuGet packages of the SDK and the platform in a local feed `artifacts/local-nupkg` so that another repository can build an app or an OS; `SdkCompatibility` range checked at load (`AppCatalog`); duplicate command names rejected at build and at load; Sha256 hash of assets verified; `BuildProfileManifest` removed (replaced by ADR 0047/0048); `dotnet new` templates (window app, command, service, ASP.Hack site). | R3, R4, R5, R20, R21 (in part) | `1` |
| `v3/7-storage-backup` | OS backup and restore exposed (`IndexedDbBackupService` registered, page in Settings or Diagnostics, `hackbackup` command, archive in `~`); "low space" behavior (`IsLowSpace`: notification, write refused with `ENOSPC`, `df`); IndexedDB fixtures of previous versions and version upgrade test; corrupted PWA cache detected and reloaded, quota test. | R13, R14, R15 | `1` |
| `v3/8-settings-security` | `SettingSensitivity` values hidden on read by a non-administrator and in exports; encryption of sensitive settings (deferred decision no. 9, ADR); audit log of protected writes in `/etc` (`/var/log/audit.log`, `auditd` style). | R16, R17, R22 (no. 9) | `1` |
| `v3/9-sync` | Synchronized deletions (tombstones instead of hard-coded `IsTombstone: false`); conflict resolution called and offered to the user; periodic sync; AppCatalog domain client. | R18 | `1` |
| `v3/10-server-proxy` | Relay of the optional server: HTTP request bodies forwarded, TCP and UDP streams, server-side grant check, quotas and policy in `appsettings` (allow everything, disable quotas), startup warnings. | R19 | `1` |
| `v3/11-apps` | Hack Paint opens and saves images in the OS files (PNG, dialogs, "Open with"); theme editor ported from `src/` (editing, preview, documentation, theme saved in `~/.config`); recheck of the Code Editor's open points (offline assets, large files, worker cleanup). | migration §2 | `1`, `3` |
| `v3/12-localization` | `IStringLocalizer` in apps and the platform, `.resx` resources; user language in `~/.config/locale.conf`, system language in `/etc/locale.conf`, `LANG` variable in the process environment (ADR 0050); language gateway in `IAppExecutionContext`; manifest `Localizations` used (app name in the launcher); translation of base apps and the platform; fix of `docs/old/localization.md`, which describes a nonexistent state. | R10 | `3`, `4`, `5`, `7`, `11` |
| `v3/13-accessibility` | Accessibility preferences as files (high contrast, text size, reduced motion) read by the theme and exposed by the SDK; axe scans added for Terminal, Browser, Calculator, System Monitor and the apps of work items 7 and 11; keyboard navigation tested in E2E. | R11 | `3`, `12` |
| `v3/14-trimming` | `PublishTrimmed=True` in the Ecosystem, trimming warnings fixed in all shipped projects, trimmed Release publication tested in E2E (boot, apps, sites). AOT out of scope. | R12 | `2` to `13` |
| `v3/15-sdk-freeze` | `PublicAPI.Shipped.txt` for all SDK libraries; SDK 1.0 freeze; test of an app compiled against an earlier SDK version; guide "build on HackerOS from another repository". | R21 | `14` |
| `v3/16-final` | Final verification (three hosts, all suites), status added to `doc/en/wasm/dev/plans/wasm-v3-migration-analyse.md`, update of `docs/old/implementation-status.md`, `project-overview.md`, `sdk-reference.md`. | n/a | all |

### Order and parallelism

1. `v3/1-adr`.
2. **Wave 1**, in parallel (distinct code areas): `2`, `3`, `4`, `5`,
   `6`, `7`, `8`, `9`, `10`. Recommended: at most four threads at a time,
   starting with `2`, `3`, `6` and `7`. `3` and `6` both touch
   `AppDefinition`: whichever is merged second first picks up the other.
3. **Wave 2**: `11` (after `3`), then `12`, which touches all displayed
   strings and therefore comes after the work items that add some.
4. `13`, then `14`, then `15`, then `16`, one after the other (each touches
   the whole solution).

Another repository can start as soon as `v3/6` is merged (packages in preview
version); it moves to SDK 1.0 after `v3/15`.

### Planned ADRs (written by `v3/1`)

Windows (events, sizes, keyboard; completes ADR 0009); added intents
and app intents; service health and startup; versions, packages and
manifest checks (completes ADR 0019); backup and low space;
encrypted sensitive settings and audit; synchronized deletions (completes
ADR 0025 and 0030); server TCP/UDP relay; localization; accessibility
preferences; trimming; moot deferred decisions and maximum file
size. Numbers are assigned at the time of writing (first free
number; several threads write ADRs in parallel).

**ADRs written by `v3/1` (2026-10-17):**

| Work item | ADR |
|---|---|
| `v3/1-adr` | [0094](../adr/0094-deferred-decisions-status-and-maximum-file-size.md) deferred decisions and maximum file size |
| `v3/2-game-removal` | [0092](../adr/0092-game-in-a-separate-repository.md) (already written), replaced by ADR 0135 |
| `v3/3-window-model` | [0095](../adr/0095-window-events-sizes-and-keyboard.md) |
| `v3/4-intents` | [0096](../adr/0096-added-intents-and-app-defined-intents.md) |
| `v3/5-services` | [0097](../adr/0097-service-health-and-startup.md) |
| `v3/6-sdk-versioning` | [0098](../adr/0098-sdk-versions-packages-and-manifest-checks.md) §1 to §7 |
| `v3/7-storage-backup` | [0099](../adr/0099-os-backup-and-low-storage.md) |
| `v3/8-settings-security` | [0100](../adr/0100-sensitive-settings-and-protected-write-audit.md) |
| `v3/9-sync` | [0101](../adr/0101-synchronized-deletions-and-conflicts.md) |
| `v3/10-server-proxy` | [0102](../adr/0102-server-network-relay-tcp-udp-and-policy.md) |
| `v3/11-apps` | [0103](../adr/0103-hack-paint-image-files-and-theme-editor.md) |
| `v3/12-localization` | [0104](../adr/0104-localization.md) |
| `v3/13-accessibility` | [0105](../adr/0105-accessibility-preferences.md) |
| `v3/14-trimming` | [0106](../adr/0106-trimmed-release-publish.md) |
| `v3/15-sdk-freeze` | [0098](../adr/0098-sdk-versions-packages-and-manifest-checks.md) §8 |
| `v3/16-final` | none |

Two deviations from the plan, decided in these ADRs: the backup is downloaded to
the host instead of being written to `~` (an OS archive exceeds the
16 MiB per file limit, ADR 0099 §1); there is no client for the sync domain
`app_catalog`, the user's app choice already traveling as a file of
`~/.config` (ADR 0101 §3). The next free number is 0109 (0107 reserved by `v3/5`, 0108: relay adjustments, `v3/10`).

## 5. Out of scope

- Installation of apps at runtime, package format and signing, app
  store: postponed to a separate plan (question 1).
- Follow-ups of phase 5: separate plan.
- AOT, compiling apps inside the OS.

## 6. Risks

- **Localization**: the longest work item (all strings of the platform
  and of the 13 system apps). Can be split into an infrastructure thread then one
  thread per group of apps.
- **Trimming**: lazily loaded assemblies and the manifest generator's
  reflection can break in Release only; that is why the work item comes late,
  once the APIs are stable, and why it adds an E2E test on the trimmed publication.
- **Tests without CI**: the bar is local (section 3, principle 6). The PWA suites
  run in a container since `v3/7`: without IPv6, their harness falls back
  to `127.0.0.2` ([`building-and-testing.md`](../building-and-testing.md)).

## 7. Decisions (questions of 2026-10-17)

Answers from Benoit of 2026-10-17 05:52Z.

| # | Question | Answer |
|---|---|---|
| 1 | Installation of apps at runtime (packages, signing, transaction, data migrations) | **Later**, in a separate plan. |
| 2 | How another repository consumes the ecosystem | "Not important": we keep the recommendation, **NuGet packages** (local feed, then GitHub Packages when CI works again). |
| 3 | Localization languages | **English and French**, English by default. |
| 4 | Port the theme editor from `src/` | **Yes**, as a system app on the public SDK. |
| 5 | Integration branch | **`v3/base`**, created from `web/base` once `web/12` to `web/15` are merged; Benoit decides when to bring `web/base` back to `main`. |

## 8. Task list

### Preparation
- [x] Survey of what remains (2026-10-16), rechecked on `web/base` 34952e2.
- [x] Prior decision recorded (ADR 0092, replaced by ADR 0135: **No game in this repository**).
- [x] This plan.
- [x] Approval of the plan and answers to the questions by Benoit (2026-10-17).
- [x] Creation of `v3/base` from `web/base` (after `web/12` to `web/15`).

### Work items
- [x] `v3/1-adr` (ADR 0094 to 0106)
- [x] `v3/2-game-removal`
- [x] `v3/3-window-model` ([`window-model.md`](../../framework/window-model.md))
- [x] `v3/4-intents` ([`app-intents.md`](../../framework/app-intents.md))
- [x] `v3/5-services` (ADR 0097, adjusted by ADR 0107)
- [x] `v3/6-sdk-versioning` (ADR 0098 §1-7, [`sdk-packages.md`](../../framework/sdk-packages.md))
- [x] `v3/7-storage-backup` ([`system-backup.md`](../system-backup.md); archive downloaded to the host, not in `~`, ADR 0099)
- [x] `v3/8-settings-security` ([`sensitive-settings.md`](../sensitive-settings.md))
- [x] `v3/9-sync` ([`synchronization.md`](../synchronization.md))
- [x] `v3/10-server-proxy` ([`server-network-relay.md`](../server-network-relay.md), ADR 0108)
- [x] `v3/11-apps` ([`hack-paint.md`](../hack-paint.md), [`theme-editor.md`](../theme-editor.md))
- [x] `v3/12-localization` (ADR 0104, ADR 0111, [`localization.md`](../../framework/localization.md))
- [x] `v3/13-accessibility` ([`accessibility.md`](../accessibility.md), ADR 0112)
- [x] `v3/14-trimming` ([`trimmed-publish.md`](../trimmed-publish.md), ADR 0113; HackerOS assemblies kept whole)
- [x] `v3/15-sdk-freeze` (ADR 0114, [`building-on-hackeros.md`](../../framework/building-on-hackeros.md))
- [x] `v3/16-final` (status report: [`wasm-v3-final-status.md`](wasm-v3-final-status.md))
