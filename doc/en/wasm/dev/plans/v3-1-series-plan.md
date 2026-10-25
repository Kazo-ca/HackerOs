# Series v3.1: gaps in the wasm v3 migration

## Purpose

The status report [`wasm-v3-final-status.md`](wasm-v3-final-status.md) leaves
gaps after the v3 migration. On 2026-10-17, Benoit chose "Series v3.1
first": small work items on a `v3.1/base` branch, before the follow-ups of
step 5 ([`execution-followups-plan.md`](execution-followups-plan.md)),
and the SDK moves to 1.1.

Two gaps from the starting list are handled elsewhere, by the original
threads, in PRs to `v3/base`:

- per-user file and scheme associations
  (`~/.config/hackeros/file-associations.json`): thread "V3 4: intents";
- the `passwd` command and the reset of a password by an administrator
  (Settings, keyring): thread "V3 8: sensitive settings".

Series v3.1 therefore keeps two code work items, plus the publication of 1.1.

## Branches

- `v3.1/base` starts from `v3/base` at `ce8c33a` (merge of PR #168).
- Each work item starts from `v3.1/base`, on a `v3.1/<n>-<subject>` branch, and
  its PR targets `v3.1/base`. Never merge into `main`: Benoit decides when
  to bring these branches back.
- When the PRs for associations and `passwd` are merged into
  `v3/base`, `v3/base` is merged into `v3.1/base` (merge commit, no
  rebase) by a `v3.1/sync-v3-base` PR. This is mandatory before
  `v3.1/4-sdk-1-1`, so that their SDK additions ship in 1.1.

## Work items

| Work item | Content | ADR | Depends on |
|---|---|---|---|
| `v3.1/1-adr` | This plan and the series ADRs | 0116, 0117 | n/a |
| `v3.1/2-window-geometry` | Size and maximized state saved and restored, not the position (ADR 0122, Benoit's answer); `WindowOptions.RememberGeometry` (**done**, see [`window-geometry.md`](../window-geometry.md)) | [0116](../adr/0116-window-geometry-restore.md), [0122](../adr/0122-window-size-only-not-position.md) | `v3.1/1` |
| `v3.1/3-window-title` | `WindowAppBase.SetWindowTitleAsync`, `WindowTitleChanged` event, adoption by Hack Paint, the editors and the explorer | [0117](../adr/0117-sdk-window-title-and-sdk-1-1.md) §1-4, [0123](../adr/0123-window-title-implementation-adjustments.md) (done, see [`window-model.md`](../../framework/window-model.md) §4) | `v3.1/1` |
| `v3.1/sync-v3-base` | Merge of `v3/base` (per-user associations, `passwd`) into `v3.1/base` (**done**, PR #175) | n/a | these two PRs merged into `v3/base` |
| `v3.1/4-sdk-1-1` | `Unshipped` moved into `Shipped`, version 1.1.0, compatibility app 1.0 loaded on 1.1, full verification, status report updated (**done**, see [`sdk-packages.md`](../../framework/sdk-packages.md#sdk-11)) | [0117](../adr/0117-sdk-window-title-and-sdk-1-1.md) §5 | `v3.1/2`, `v3.1/3`, `v3.1/sync-v3-base` |

- `v3.1/2` and `v3.1/3` can proceed in parallel. They both touch
  `WindowRuntime`, `WindowLaunchCoordinator`, `window-model.md` and the
  `PublicAPI.Unshipped.txt` files: whichever is merged second first picks up the other.
- ADR numbers reserved for the series: 0116 to 0119; 0118 and 0119 remain
  free for an implementation adjustment (one ADR per work item that departs from
  its decision). ADR 0122 (size only, without the position) corrects
  ADR 0116 after Benoit's answer. `v3.1/3` took 0123, assigned by
  the orchestration.

## Merge bar

GitHub Actions does not run in the repository: the local suite is authoritative, as
for the v3 series. Before merging, each work item builds the solution and
passes the tests of the projects it touches, the UI E2E suite for windows,
`HackerOs.SdkCompatibility.Tests` and `build/publish-trimmed.sh`.

## Tasks of this work item

- [x] Create and push `v3.1/base` from `v3/base`.
- [x] Re-read the v3 status report, ADRs 0044, 0095, 0098, 0103, 0114 and the window code.
- [x] Write ADR 0116 (window geometry).
- [x] Write ADR 0117 (window title and publication of SDK 1.1).
- [x] Write this plan and update the index and the v3 status report.
