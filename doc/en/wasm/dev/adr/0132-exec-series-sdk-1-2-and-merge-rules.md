# ADR 0132: `exec` series, SDK 1.2 additions, branches and merge bar

## Status

Accepted on 2026-10-18. A question is asked to Benoit (§2, timing of the
SDK 1.2 release); what follows applies the recommended answer while awaiting it.

- **Plan:** [`execution-followups-plan.md`](../plans/execution-followups-plan.md),
  sections 5 and 8.
- **Applies:** [ADR 0114](0114-sdk-1-0-freeze.md) (SDK freeze, SemVer
  promise) and [ADR 0117](0117-sdk-window-title-and-sdk-1-1.md) (release
  of a minor version in a final work item).

## Context

The SDK is at 1.1.0, its whole surface in `PublicAPI.Shipped.txt`
(`v3.1/4-sdk-1-1`). Several `exec` work items add public APIs:
`HackWireContextAttribute` (ADR 0124), limits and new exit reasons
(ADR 0125, 0126), `AppDefinition.OomScoreAdjust` (ADR 0126), `nice` (ADR 0127),
`HackBackground` and its analyzer (ADR 0128). We need to say how these additions
arrive without breaking a 1.0 or 1.1 app.

## Decision

### 1. Additions only

- Each public addition goes into the `PublicAPI.Unshipped.txt` of the project concerned,
  in the work item that introduces it. No line of `PublicAPI.Shipped.txt`
  is removed or changed: a break would require an SDK 2.0, outside this
  series.
- Values added to enumerations (`ProcessExitReason`,
  `FileSystemErrorCode`) take new numbers, at the end.
- The 1.0 compatibility app (`Tests/fixtures/sdk-compat`) keeps
  loading and running; `exec/10` adds an app compiled at 1.1.0 to it.

### 2. Release of SDK 1.2 (question asked to Benoit)

Applied while awaiting (recommended): **a single release, at the end**, in
`exec/10-final`: `HackerOsVersion` goes to `1.2.0`, the `Unshipped` files
are moved into `Shipped`, [`sdk-packages.md`](../../framework/sdk-packages.md) and
[`building-on-hackeros.md`](../../framework/building-on-hackeros.md) list the additions.
During the series, `exec/base` stays at 1.1.0 with unreleased additions.

The other choice: a minor version per work item that adds an API (1.2, 1.3,
…). The game repository could follow earlier, at the cost of a full verification
and one more compatibility app at each release.

### 3. Branches and merging

- Integration branch `exec/base`, created on 2026-10-18 from `v3.1/base`
  (with the validated plan coming from `web/base`).
- Each work item: branch `exec/<n>-<subject>` from `exec/base`, PR to
  `exec/base`. The thread merges by itself once the bar of §4 is passed
  (Benoit's "Yes" choice on the follow-up launch card, on
  2026-10-18). **Never merge to `main`**: Benoit decides.
- Before merging, a work item that touches the same files as an already merged
  work item brings `exec/base` into its branch.

### 4. Merge bar

GitHub Actions does not run in this repository (no runner since August
2026): the bar is local.

- Build of the solution with no new warning; unit and E2E tests of the
  scope; known pre-existing failures listed in the PR, not hidden.
- `build/verify-hosts.sh` on the three hosts, work item options enabled
  **and** turned off (plan, principle 6).
- Trimmed publish (`build/publish-trimmed.sh` and
  `HackerOs.TrimmedPublish.E2E.Tests`, ADR 0113) and SDK compatibility app,
  as since `v3/14` and `v3/15`.
- Docs and indexes up to date in the same change (AGENTS.md III.4).

### 5. ADR numbers

This work item took 0124 to 0132 and reserves **0133** for the decision on the
worker rendering trial (ADR 0129). A work item that must record implementation
adjustments takes the next free number that the coordinator reserves for it.

## Consequences

- `exec/10-final` does the SDK 1.2 release in addition to the final verification
  and the documents.
- The game repository stays on 1.1.0 until this release.

## References

- ADR 0098, 0113, 0114, 0117, 0124 to 0131.
- [`sdk-packages.md`](../../framework/sdk-packages.md),
  [`building-on-hackeros.md`](../../framework/building-on-hackeros.md).
