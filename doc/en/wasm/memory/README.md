> [Version française](../../../fr/wasm/memory/README.md)

# HackerOS project memory

Project status and working conventions as of **2026-10-28**. This folder serves as a
resumption point: a new contributor (human or agent) finds here what is
done, how we work and what must not be redone. It is updated
at the end of each series or when a convention changes.

## Where the project stands

- **Active code**: `wasm2/HackerOs/` (simulated OS in Blazor WebAssembly, .NET 10,
  solution `HackerOs.sln`). `src/` is the old version, kept as a
  reference.
- **Version**: HackerOS and the SDK are at **2.1.0** (`build/Versions.props`).
  SDK 2.0.0 removed `HackerOs.AppSdk.FileView` and refuses 1.x apps
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
- **ADR**: 0001 to 0171 and 0173 to 0185 exist (a few numbers skipped); 0170 is the
  Settings side menu (PR #210), 0171 the mobile file explorer, 0172,
  reserved with it, was not used, 0173 is the automatic release, and 0174 the new-version announcement
  (`version.json`), 0175 the "New folder" button of the Choose folder and Save dialogs, 0176 the app repository template (submodule for debugging, packages for production), 0177 the title bar of maximized and snapped windows, 0178 to 0180, 0183 and 0184 the multi-monitor series, 0181 a folder of the computer mounted as a network drive, 0182 installing an app from GitHub at the terminal (`hackpkg`), 0185 to 0187 reserved by other work, 0188 to 0190 the Hack Store (catalog, trust and per-user install, theme packs; plan [`hackeros-store.md`](../dev/plans/hackeros-store.md)). 0191 reserved by the installable local server (PR #269), 0192 the features overview (`features.md`) and the exception to ADR 0135 for ported apps. **Next free number: 0193**. Parallel
  threads reserve distinct numbers.

## Working conventions

- **No game in this repository** ([ADR 0135](../dev/adr/0135-no-game-in-this-repository.md)):
  neither code, nor mention in the docs, plans, ADRs or memory.
- **Branches**: one integration branch `<series>/base` per series, one branch
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
  and `*/base` (failing tests rerun once, Playwright in a manual
  workflow): [`../dev/ci-workflows.md`](../dev/ci-workflows.md).
- **SDK**: the public API is tracked by `PublicAPI.Shipped.txt` /
  `PublicAPI.Unshipped.txt`; an addition goes through `Unshipped` until the
  next release (`build/tools/ship-public-api.sh`).
- **Publishing**: change `HackerOsVersion` in `build/Versions.props` and
  merge into `main`; the `Release HackerOS` workflow creates the tag, the
  GitHub Release and pushes to nuget.org ([`releases.md`](../dev/releases.md),
  ADR 0173).

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
