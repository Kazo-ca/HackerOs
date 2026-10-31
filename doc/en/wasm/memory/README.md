> [Version française](../../../fr/wasm/memory/README.md)

# HackerOS project memory

Project status and working conventions as of **2026-10-31**. This folder serves as a
resumption point: a new contributor (human or agent) finds here what is
done, how we work and what must not be redone. It is updated
at the end of each series or when a convention changes.

## Where the project stands

- **Active code**: `wasm2/HackerOs/` (simulated OS in Blazor WebAssembly, .NET 10,
  solution `HackerOs.sln`). `src/` is the old version, kept as a
  reference.
- **Version**: HackerOS and the SDK move to **2.2.0** (`build/Versions.props`),
  prepared on 2026-10-31 in the `release/2.2.0` PR to `main`, merged after
  `todo/base`; it is published (`v2.2.0` tag, GitHub Release, nuget.org) when
  that PR lands in `main`. It folds into `Shipped` the 3020 API lines added
  since 2.1.0 (Hack Store, `hackeros-solution` template, themes…) and gathers
  48 entries into `changes/2.2.0.json`. SDK 2.0.0 removed
  `HackerOs.AppSdk.FileView` and refuses 1.x apps
  ([ADR 0161](../dev/adr/0161-sdk-2-0-implementation-adjustments.md)).
  2.1.0 (2026-10-26) is the first version released automatically (tag,
  GitHub Release, nuget.org; ADR 0173); it folded every addition since 2.0
  into `Shipped`.
- **Series completed and merged into `main`**:

| Series | Content | Summary |
| --- | --- | --- |
| Rebuild, phases 1 to 4 | `Hack*` file API, manifest as a C# class, settings and logs as files, `/proc` and `/dev`, POSIX shell, trash, dangerous capabilities, base apps on the public SDK | [`ecosystem-rebuild-plan.md`](../dev/plans/ecosystem-rebuild-plan.md) |
| Phase 5 | CPU and memory measurement, "Not responding", clean stop, processes in Web Workers | [`execution-isolation-plan.md`](../dev/plans/execution-isolation-plan.md) |
| Web phase | ASP.Hack (MVC, `.razor`/`.cshtml` views), Blazor sites, static sites, browser HTML rendering, detached windows | [`asp-hack-plan.md`](../dev/plans/asp-hack-plan.md) |
| V3 (`v3/1` to `v3/16`) | End of the wasm v3 migration, SDK 1.0 frozen, trimmed publishing | [`wasm-v3-final-status.md`](../dev/plans/wasm-v3-final-status.md) |
| V3.1 | Window geometry and title, SDK 1.1 | [`v3-1-series-plan.md`](../dev/plans/v3-1-series-plan.md) |
| EXEC (`exec/*`) | Process limits, OOM killer, remote gateways, "not responding" box, `HackBackground`, OS served by `HackerOs.Server`, cross-origin isolation, SDK 1.2 | [`execution-followups-plan.md`](../dev/plans/execution-followups-plan.md) |
| DESK (`desk/0` to `desk/6`) | OS-provided file control, central context menu, app menu entries, desktop, SDK 2.0 | [`desktop-and-context-menu-plan.md`](../dev/plans/desktop-and-context-menu-plan.md) |
| Multi-monitor (`multi-monitor/0` to `multi-monitor/9`) | Extended desktop: one screen per display surface, `DisplayId` and work area per screen, windows moved through a portal, screen service (Chrome/Edge, fallback elsewhere), one taskbar per screen, "Other screen" menus and identification veil, Settings ▸ Screens, drag between screens; no change to the app SDK | [`multi-monitor-plan.md`](../dev/plans/multi-monitor-plan.md) |
| File Explorer (`explorer/1` to `explorer/9`) | Resizable sidebar, empty state inside the list, drag out to the computer, shortcuts, tracked network drives, errors in a `MessageBox`; the `.zip` opened as a folder is done in `todo/base` (`todo/10-zip-as-folder`, issue #241) | [ADR 0185](../dev/adr/0185-file-explorer-improvements.md), [`file-explorer.md`](../dev/file-explorer.md) |
| Guest (`guest/*`) | Guest start when no account exists, elevation by a plain confirmation, "Create my account" (migrate or delete the guest), sign-in credits | [ADR 0187](../dev/adr/0187-guest-start-and-sign-in-credits.md), [`guest-account.md`](../dev/guest-account.md) |

- **Since DESK** (on `main`, October 2026): restart offered when the
  build changes under the page (ADR 0162), local preview of window
  moves (ADR 0163), app icons and window icon menu
  (ADR 0164), mobile home and drawer (ADR 0165, 0166), combined PWA + legacy
  GitHub Pages publishing (ADR 0167), updated NuGet packages.
- **Passkeys and accounts** (October 2026, PR to `main`): sign-in with a
  WebAuthn passkey and format 2 keyring with slots (ADR 0168,
  [`passkeys.md`](../dev/passkeys.md)), adding and removing accounts by an
  administrator (ADR 0169). SDK additions in `PublicAPI.Unshipped.txt`
  (next minor).
- **Mobile explorer** (October 2026, PR #212 to `main`): mobile entry point of
  apps honored, mobile file explorer, full-screen dialogs on mobile, one tap
  opens (`HackFileView.OpenOnTap`) (ADR 0171).
- **Since multi-monitor** (on `main`, October 28 to 30, 2026): a folder of
  the computer mounted as a network drive (ADR 0181), `hackpkg install` from
  GitHub (ADR 0182), features overview `features.md` (ADR 0192), local time
  and time zone (ADR 0193, work item 2 of issue #242), taskbar context menus
  (ADR 0194, work item 4 of #242). The Hack Store ADRs (0188 to 0190) are
  accepted, nothing is coded (`TODO` issues #246 to #257). The installable
  local server (ADR 0191, issue #267) is coded in the `todo/base` series (work
  item `todo/14-installable-server`,
  [`installable-server.md`](../dev/installable-server.md)); the Windows and
  macOS scripts and the Docker image still need a real run.
- **Power button in the Start menu** (ADR 0195, PR #291, merged on
  2026-10-30): "Switch user" stays greyed out, Benoit's choice on
  2026-10-30 (ADR 0201, issue #290; unmerged prototype on the branch
  `switch-user/simultaneous-sessions-prototype`).
- **Cursor themes** (ADR 0205, series `todo/base`, work item
  `todo/9-cursor-themes`, issue #257): `cursor.theme` folders of PNGs in
  `/etc/hackeros/cursors/` and `~/.config/hackeros/cursors/`, `cursors.config`,
  built-in System (default), HackerOS Neon and Gothic Crimson themes, applied
  to the whole surface by `cursorTheme.js`, apps included. Merged into
  `todo/base` (PR #301); on 2026-10-31 Benoit confirmed System as the default
  theme and the reach everywhere.
- **`todo/base` series** (2026-10-30, every `TODO` issue in parallel, one
  final PR to `main`, branches `todo/<n>-<topic>`, ADR 0197 to 0211
  reserved): Hack Store work item 1 done, `hackeros-solution`
  template and catalog publishing workflow (ADR 0207,
  [`app-repository.md`](../framework/app-repository.md), issue #246). The
  `HackerOs-AppTemplate` repository will only be regenerated with a submodule
  on the release that contains this template, 2.2.0 (draft PR in that repository).
  Image wallpapers done too ([ADR 0202](../dev/adr/0202-desktop-wallpaper-images.md),
  issue #254, [`wallpapers.md`](../dev/wallpapers.md)).
  ADR 0206 is taken by issue #247 (installed apps' `wwwroot/` served by the
  service worker, [ADR 0206](../dev/adr/0206-serve-installed-app-assets.md)).
- **Icon packs** (ADR 0203, `TODO` issue #255, series `todo/base`): PNG packs
  for apps and files under `/usr/share/icons` and `~/.local/share/icons`, chosen
  in Settings ▸ Appearance (`icons` settings document, key `iconPackId`); the
  built-in Néon pack is seeded. See [`icon-packs.md`](../dev/icon-packs.md).
- **Hack Store 3, trusted sources** (ADR 0208, `TODO` issue #248, series
  `todo/base`): install for oneself in `~/.local/share/hackeros/apps`, block
  list `/etc/hackeros/app-policy.config`, two-step approval
  (`AppInstallApprovalOverlay`), `hackpkg --user/--system`, `sources add/remove`,
  `policy`, Settings ▸ Sources and block list. The catalog loads everyone's
  personal apps at startup and hides them per session (question asked to
  Benoit). See [`app-installation.md`](../dev/app-installation.md).
- **Hack Store 5, the app** (ADR 0212, `TODO` issue #249, series `todo/base`,
  branch `todo/5-hackstore-app`): system app `org.hackeros.store`
  (`HackerOs.Apps.Store`), `catalog:<name>/<appId>@<version>` source,
  `hackpkg install <appId>`, `AppUpdateMonitor` and notification, default pin
  (`defaultPinsApplied` of `start-menu.json`). Benoit's answers (2026-10-31):
  Q1 A restart without administrator, Q2 A `catalog:official/<appId>` source,
  Q3 C notification only (no badge), Q4 B an appId listed by several catalogs
  refused (`apps.package.ambiguous`); Q3 and Q4 in ADR 0215 (branch
  `todo/5b-hackstore-no-badge`). See
  [`app-installation.md`](../dev/app-installation.md#the-hack-store-and-the-catalogs).
- **Hack Store v2, theme packs** (ADR 0213, `TODO` issue #253, series
  `todo/base`, branch `todo/18-theme-packs`): `.hacktheme` format
  (`ThemePack.Read`), `kind: "theme-pack"` in `catalog.json`, pack unpacked as
  is in `theme-packs/<id>/` and read in place by themes, wallpapers, icons,
  sounds and cursors, Store Themes tab. Four questions asked to Benoit (files
  read in place, catalog CI, nothing applied after the install, no badge),
  answered A for all four (2026-10-31). Catalog CI: `TODO` issue AppMarketPlace #4.
- **Hack Store, next steps** (ADR 0214, `TODO` issue #251, series
  `todo/base`, branch `todo/19-hackstore-later`): adding without a restart
  (live catalog `AppCatalog.Changed`), ECDSA P-256 signatures
  (`[signing-keys]`, `HackerOsAppSigningKeys`), `.transaction` journal,
  `hackpkg rollback`/`repair`, `AppDataGuard`. Five questions asked to Benoit,
  answer A to each. The official catalog key is still to be created by Benoit
  (`keygen`, `HACK_STORE_SIGNING_KEY` secret); tool for authors: `TODO`
  issue #321.
- **Notification bubbles** (ADR 0197, work item 1 of #242, series
  `todo/base`): notification bubbles and "Update ready" as a persistent
  notification with "Restart". See
  [`notification-toasts.md`](../dev/notification-toasts.md).
- **Window animations** (ADR 0198, work item 3 of #242, branch
  `todo/12-window-animations` into `todo/base`): open, close, minimize into the
  taskbar of its screen, restore, maximize, snap; `--hos-window-anim-*`
  variables ([`window-animations.md`](../dev/window-animations.md)).
- **System sounds** (ADR 0204, issue #256, `todo/base` series, branch
  `todo/8-os-sounds`): freedesktop.org sound themes in `/usr/share/sounds`
  (built-in `hackeros` and `soft` themes written by `FileSystemSeeder`) and
  `~/.local/share/sounds`, `sound.config`, Settings ▸ Sound. No SDK API to play a
  sound (question Q2 to Benoit). See [`system-sounds.md`](../dev/system-sounds.md).
- **Taskbar previews** (ADR 0199, work item 5 of issue #242, `todo/` series,
  branch `todo/13-taskbar-previews` into `todo/base`): reduced live copy of a
  window when hovering its button, and peek. Number 0199 taken from the
  0197 to 0205 reservation of the `todo/` series.
- **Real sites in the browser** (ADR 0210, `todo/base` series, issue #266): the
  Browser is a system app with `network.real.access`; with a connected server it
  opens real sites through the proxy, cookies included. The public proxy and the
  Wayback Machine are left.
- **Unread notifications dot next to the clock** (ADR 0196, PR #294, merged on
  `main` 2026-10-31): replaces the what's new Start menu dot; brought into
  `todo/base` by the merge of `main`.
- **ADR**: 0001 to 0171 and 0173 to 0196 exist on `main` (a few numbers skipped;
  0141 and 0172 were not used). Recent landmarks: 0173 automatic release, 0174
  new-version announcement, 0175 "New folder" in dialogs, 0176 app repository
  template, 0177 title bar of maximized windows, 0178 to 0180, 0183 and 0184
  multi-monitor, 0181 network drive, 0182 `hackpkg`, 0185 File Explorer, 0186
  what's new ([`change-list.md`](../dev/change-list.md)), 0187 guest and credits,
  0188 to 0190 Hack Store ([`hackeros-store.md`](../dev/plans/hackeros-store.md)),
  0191 installable local server, 0192 `features.md` and exception to ADR 0135,
  0193 local time, 0194 taskbar menus, 0195 Start menu Power button, 0196 unread notifications dot;
  `todo/base` series: 0197 notification bubbles, 0201 "Switch user" stays greyed out, 0203 icon packs, 0207
  `hackeros-solution` template, 0208 Hack Store trusted sources, 0212 Hack Store app, 0213 theme packs, 0214 Hack Store without a restart, signing and transactions, 0215 Hack Store answers (no badge, ambiguous appId refused) (the other numbers up to 0211 are reserved by
  its work items). **Next free number:
  0216.** Before taking one, check `dev/adr/` on
  `main` and the open PRs.

## Working conventions

The rules for working with Benoit (language, questions, `TODO` issues, branch
names, merging into `main`, ADR numbers, change entries) are in section **IV**
of [`AGENTS.md`](../../../../AGENTS.md), which Claude Code reads through
[`CLAUDE.md`](../../../../CLAUDE.md). In short:

- **Language**: answer Benoit in French.
- **To do**: "add it to the to-do list" = a GitHub issue with the `TODO`
  label and a "Dépendances" section; "what is left to do?" = the open
  `TODO` issues first, then the docs.
- **No game in this repository** ([ADR 0135](../dev/adr/0135-no-game-in-this-repository.md)):
  neither code, nor mention in the docs, plans, ADRs or memory.
- **Branches**: descriptive names (`taskbar/4-context-menus`), never
  `claude/project-thread-…`. One integration branch `<series>/base` per series, one branch
  `<series>/<n>-<topic>` per work item, PR to the integration branch. The ADRs
  of a phase are written before its code.
- **`main`**: only Benoit merges into `main` and decides when an integration
  branch goes there.
- **Questions**: they can be noted in an ADR, but they are always asked to
  Benoit directly, with choices, context and implications.
- **Documentation**: written in French in `doc/fr/`, translated to English in `doc/en/` (same folders and file names, in English); every change updates both languages. Root readmes: `readme.md` in English, `readme-fr.md` in French, each linking to the other on its first line. `dev/` for OS
  development, `framework/` for building apps and sites,
  `memory/` for this folder. Each change updates the docs it touches and
  the index [`../README.md`](../README.md); replaced docs go to
  `dev/old/`. An ADR is never modified: a new ADR replaces it.
- **Merge bar**: local build and tests green (E2E suites one at a time),
  `build/publish-trimmed.sh` and SDK compatibility
  (`Tests/fixtures/sdk-compat`) when they are concerned. GitHub Actions
  run again since 2026-10-24 and check every PR to `main`
  and `*/base` (failing tests rerun once; Playwright every night on `main`
  when `main` changed, a failure opens an issue): [`../dev/ci-workflows.md`](../dev/ci-workflows.md).
- **SDK**: the public API is tracked by `PublicAPI.Shipped.txt` /
  `PublicAPI.Unshipped.txt`; an addition goes through `Unshipped` until the
  next release (`build/tools/ship-public-api.sh`).
- **Publishing**: change `HackerOsVersion` in `build/Versions.props` and
  merge into `main`; the `Release HackerOS` workflow creates the tag, the
  GitHub Release and pushes to nuget.org ([`releases.md`](../dev/releases.md),
  ADR 0173).

## Decisions and context outside the code

| Note | Content |
| --- | --- |
| [`decisions-not-built.md`](decisions-not-built.md) | Decisions Benoit took in discussion that are not coded nor written anywhere else yet: windows and taskbar (remaining work items of #242), real sites in the browser (#266: public proxy and Wayback left), ported third-party apps, server-hosted apps, ideas taken from daedalOS, editors. |
| [`known-test-failures.md`](known-test-failures.md) | Known pre-existing test failures (to name in a PR, not to hide) and recent environment tips. |

## Environment and pitfalls

Build workarounds (NuGet, Linux container, Playwright) and known flaky
tests are gathered in [`../dev/pitfalls.md`](../dev/pitfalls.md)
and [`../dev/building-and-testing.md`](../dev/building-and-testing.md).

## Historical notes

Notes written during the first phases; the paths and numbers they
cite date from when they were written.

| Note | Content |
| --- | --- |
| [`blazor-window-manager-bugs.md`](blazor-window-manager-bugs.md) | Lessons from the old Blazor window manager (base call forgotten in `OnAfterRenderAsync`, state of the v3 solution in August 2026). |
| [`manifest-serializer-notes.md`](manifest-serializer-notes.md) | Strict `AppManifest` serializer and canonical fixture. |
| [`p2-idb-008-filesystem-repository.md`](p2-idb-008-filesystem-repository.md) | Analysis of the IndexedDB file repository transactions (phase 2). |
