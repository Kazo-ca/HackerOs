> [Version française](../../../../fr/wasm/dev/old/progress-and-plan-2026-08-17.md)

# Progress status and action plan: 2026-08-17

## Purpose

Snapshot of the progress of the HackerOS v3 migration at this date, and proposal
of a prioritized action plan. This document does not replace
[`integration-task-list.md`](integration-task-list.md) (source of truth for
the task-by-task detail) nor [`server-implementation-pass.md`](server-implementation-pass.md)
(source of truth for the server/proxy); it serves as a summary view and
sequencing to decide what to tackle next.

## 1. Executive summary

| Priority | Work item | Why now |
|---|---|---|
| ~~1~~ | ~~Close the Phase 2 exit gate~~ | **Almost done on 2026-08-17**; see section 4; only GitHub billing remains, outside the code (the human checklist of `P2-GATE-003` was explicitly marked skip) |
| 2 | Continue Phase 5 (server/proxy) | In progress in another session; logical next step already identified in `server-implementation-pass.md` |
| ~~3~~ | ~~Actually close the windows/taskbar extraction~~ | **Done on 2026-08-17**; see section 2 |
| 4 | Mobile platform | Logical follow-up of the extraction, now closed (the mobile doc has no checked task) |
| 5 | Finish Wave 3 / Wave 5 (outside simulated network/gameplay) | Remaining functional migration work, prioritized before network/gameplay content |
| Postponed | Simulated network expansion & gameplay content | The technical core (Wave 4, Wave 6) is already done; only the content expansion remains, and it is deliberately set aside |

## 2. Windows/taskbar extraction (`window-taskbar-export-plan.md`): CLOSED 2026-08-17

**Already confirmed done** (PR #79 merged, branch `feature/windowing-taskbar-extraction`,
history `EXT-WIN-001` to `EXT-WIN-015`):

- `HackerOs.Windowing.Core` (headless engine, zero HackerOS dependency): exists.
- `HackerOs.Windowing.Blazor` (windows/chrome RCL): exists.
- `HackerOs.Taskbar.Blazor` (taskbar RCL through contracts): exists.
- `Samples/HackerOs.Windowing.SampleHost`: exists, does not reference `HackerOs.Ecosystem`.
- HackerOS adapters (`Shell/TaskbarAdapters.cs`) and migration of `DesktopShell`: done, verified in the browser.
- Local NuGet packaging + public API baseline + Release/trimming proof: done.
- Test coverage (headless, components, Playwright E2E `taskbar`): done.

**The two points that stayed open despite the mention "extraction completed on
2026-08-13" were closed today:**

- [x] **`EXT-WIN-001`**: New project `Platform/HackerOs.Windowing.Abstractions/`
  created: `WindowId`, `WindowOwnerId`, `WindowBounds`, `WindowConstraints`,
  `WindowVisualState`, `WindowModality`, `WindowRuntimeState`, and all the
  commands/events are moved there (same names, new namespace).
  `HackerOs.Windowing.Core` keeps only the `WindowRuntime` class and
  references Abstractions. Added to `HackerOs.sln` via `dotnet sln add`. The
  30 consumer files updated. `dotnet build` (Debug and Release) and
  `dotnet test` all pass with 0 failures; `dotnet pack`/`dotnet publish`
  produce the 4 exported packages/assemblies without warnings.
- [x] **`WindowSurface`**: Component implemented in `HackerOs.Windowing.Blazor`
  (`WindowSurface.razor`): receives `Runtime` + `WindowContent`, subscribes to
  `Runtime.StateChanged`, and translates the `DesktopArea` callbacks into
  `Runtime.Apply(...)`. The sample host is migrated to
  `<WindowSurface Runtime="WindowRuntime">`, eliminating the boilerplate it
  reimplemented by hand. Verified in the browser: opening, focus,
  interaction, maximize/restore, minimize/restore through the taskbar, close,
  with no console error. `DesktopShell` (the full HackerOS host) deliberately stays
  on its current path through `TaskbarAdapters`: it also handles dialogs and
  closing with guard/confirmation, two concerns outside the minimal scope of
  `WindowSurface`; migrating would introduce a break with no real benefit.

**Point noted during verification, out of scope for this batch**: the
`HackerOs.E2E.Tests` suite has a pre-existing flaky/racy test unrelated to
windowing, `IndexedDbBrowserContractTests.Group_and_settings_contracts_pass_in_real_browser`,
which fails reproducibly with `IndexedDB database 'hackeros' version 2 is
not open`, a race between `VerifyBackupRestoreAsync` (destructive restore) and
`VerifyOrphanCleanupAsync` (raw JS call right after) in
`Tests/HackerOs.BrowserHarness.Tests/App.razor`. Reproduced identically
before and after this batch of work; affects no test related to
windows/taskbar. Left for a future pass on the IndexedDB backup/
recovery topic rather than handled here.

## 3. Mobile platform (`mobile-interface-platform-plan.md`): Phases 0, 1 and 2 done, 2026-08-18

`MOB-001`, `MOB-006` (persistence slice), `MOB-007` (Phase 0), then
`MOB-002`, `MOB-003`, `MOB-004`, `MOB-005`, `MOB-014` (partial, Phase 1), then
`MOB-008`, `MOB-009`, `MOB-010`, `MOB-012`, `MOB-013` (Phase 2, complete and
its sub-slice 2c) are done; see `mobile-interface-platform-plan.md`
§14/§16 for the file-by-file detail.

Phase 0: the taskbar exposes a generic anchor point
(`ITaskbarClockPanelSource` + `RenderFragment ClockPanelContent`) opened by
clicking the clock, where the Auto/Desktop/Mobile selector lives. The toggle persists
the preference but does not yet change the shell rendering (`MOB-008`/`009`,
Phase 2).

Phase 1: the manifest now supports several entry points per
platform (`AppManifest.Platform`, mutually exclusive with the former
`EntryPoint`) without any of the existing `app.manifest.json` having had to be
modified (an `entryPoint`-only manifest is treated as Desktop-only by
normalization, not by physical migration). `IAppPlatformEntryPointResolver`
resolves the effective entry per platform and is wired into
`AppEntryPointDiscovery` (default `desktop`, so behavior is unchanged for the
current Desktop shell). Filtering intents/associations/launcher on the
*active* platform preference is deliberately deferred to `MOB-008`
(Phase 2): applying it now would hide all existing apps as soon as a user
flips the Phase 0 Mobile toggle, without a Mobile shell to take them over.
`MOB-008` to `MOB-018` remain to be done, resequenced into phases
in `mobile-interface-platform-plan.md` §16.4/§16.5.

Phase 2a (sub-slice, after the feasibility spike §16.2): the windowing engine
gains a "single full-screen surface" presentation mode
reusing the existing Maximize/Minimize mechanisms
(`SingleSurfacePresentationPolicy`, `WindowHost.ShowChrome`,
`SingleSurfaceArea`), and a new `HackerOs.MobileShell.Blazor` package
(sibling of `HackerOs.Taskbar.Blazor`) provides `MobileSystemNavigationBar`
(Back/Home/Recent). As a bonus, a real latent bug was fixed along the way:
`WindowChrome` already ignored `Constraints.IsResizable` (the non-resizable file
dialogs were already affected).

Phase 2b (sub-slice, `MOB-008`): `PlatformShellSwitchCoordinator` confirms
then stops (`ProcessExitReason.PlatformChanged`, new) only the windows
whose entry point really changes between the two platforms
(a window with a shared entry point, e.g. `HackerOs.Samples.PlatformApp`,
survives as is thanks to the `WindowRuntime` singleton shared between the two
shells), then persists the choice, in this order, unlike the Phase 0 wiring
of `ClockPanel.razor` which persisted without confirming. `App.razor` now renders
`MobileShell` or `DesktopShell` according to the active preference, live
and without reload, proven by a real browser test
(`PlatformShellSwitchTests.cs`). A notification pane reachable by
swiping from the top of the Mobile screen (`MobileShell.razor.js`,
`attachSwipeDownGesture`) reuses `ClockPanel` (hence the Auto/Desktop/Mobile
selector), replacing a first placeholder button. Filtering of
launcher/intents/associations by platform (§6.3 step 8) remains
deferred: no embedded manifest declares a Mobile-only entry point,
so nothing is currently incorrect by leaving it unfiltered.

Phase 2c (sub-slice, `MOB-012`/`MOB-013`): the `IAppBackHandler` SDK contract
(`AppBackContracts.cs`) exists, `AppManifest.SupportsBack`
declares support per entry point, `AppEntryPointDiscovery` validates by
reflection (same pattern as `DerivesFrom`) that the declared type does implement
the contract, and `AppBackHandlerRegistry` captures the live component as
`WindowCloseGuardRegistry` already does for closing. `WindowChrome`
shows a Desktop-only Back button when `Window.SupportsBack`, Mobile
routes its system triangle to the same registry
(`MobileNavigationCommandsAdapter.RequestBack`). Only step 2 of the ordered
Back sequence of §7.3 is covered (the application handler); steps 1/3/4
(dialog stack, navigation stack, close-and-Home) are waiting for `MOB-011`. Real latent bug found by the new test:
`WindowRuntime.Copy()` forgot to carry over `SupportsBack` when
creating a window. `MOB-011`, `MOB-015` to `MOB-018` remain to be done.

The windows/taskbar extraction document already references this plan as a risk
to avoid ("Mobile break: do not freeze in the engine assumptions
specific to Desktop floating windows"), so the sequence
extraction → mobile is consistent with what is already written, even if it
is not formulated as a strict prerequisite in the mobile document
itself.

**Recommendation**: section 2 above is now closed. Mobile is
ready to start as the next feature work item (after the Phase 2 gate
and in parallel with or after the server follow-up depending on available bandwidth).

## 4. Phase 2 exit gate (priority 1): ALMOST CLOSED 2026-08-17

See [`integration-task-list.md#22-phase-2-acceptance-and-exit-gate`](integration-task-list.md)
and [`phase-2-acceptance.md`](phase-2-acceptance.md) for the full detail of the
evidence. State at the end of the 2026-08-17 session:

- [x] `P2-ACC-015`: Published PWA works after online install, server
  stopped, browser offline. **Real evidence**:
  `Tests/HackerOs.Pwa.E2E.Tests` (new project), against a real
  `dotnet publish` serving the real `service-worker.published.js`.
- [x] `P2-ACC-016`: A PWA update preserves compatible data and
  never mixes the assets of two versions. **Real evidence**, same
  project.
- [ ] `P2-ACC-017`: Real, hosted and green browser CI matrix.
  `HackerOs.Pwa.E2E.Tests` already runs automatically in the existing CI step
  (no additional wiring needed) and local runs are
  green. **Still blocked**: the GitHub account is locked for billing
  (`gh run view`: "the job was not started because your account is locked
  due to a billing issue"); user action, not code.
- [x] `P2-GATE-002`: Release publication without unexplained trimming/asset/
  console/network errors. **Two real bugs found and fixed** while
  building this evidence: the standalone PWA never mounted anything anymore
  (`RootComponents.Add<App>` was commented out in `Program.cs`) and a CSS link
  systematically returned 404.
- [x] `P2-GATE-003`: **SKIPPED (manual part), explicit user decision of 2026-08-17**;
  see detailed plan below. Desktop/mobile screenshots + a11y checks. Real, green **automated**
  evidence (new `AccessibilityAndVisualCoverageTests` test, 8 categories of
  real violations found and fixed on 6 surfaces, including an `aria-labelledby`
  regression on every window, and the discovery that
  `test/test` and `HackerOs.Server` never loaded the app's real dark theme). The
  keyboard/screen reader **human checklist**
  ([`Human-test-needed-p2-GATE-003.md`](Human-test-needed-p2-GATE-003.md)) was
  **explicitly set to skip so as not to block closing the gate anymore**;
  this is not a silent abandonment, it is a decision made in chat by the
  user. **Plan for later**: run the checklist when
  bandwidth allows, ideally before freezing the SDK surface in
  Phase 3, and record the result following the dated-box convention already
  in place in `docs/accessibility.md` §2.4.
- [x] `P2-GATE-004`: `phase-2-acceptance.md` rewritten: each line points
  to a real test or an explicitly documented gap (CI billing,
  human checklist skip). No more evidence by implementation file.
- [ ] `P2-GATE-005`: Explicit user approval. **Deliberately not
  requested** before CI billing is settled (the human checklist
  is no longer a blocker, see `P2-GATE-003` above), in line with
  `integration-audit-remediation.md` ("cannot be inferred from source code or
  a self-declared status line").

`P2-GATE-001` (build + tests) reconfirmed clean on 2026-08-17 after this batch.
A pre-existing unrelated failure (`Group_and_settings_contracts_pass_in_real_browser`,
IndexedDB) was found again and documented separately (section 2 above and
dedicated follow-up task); reproducible before and after this batch of work.

**Only one thing remains, out of reach of the code**: settle
GitHub billing (`P2-ACC-017`). The human checklist of `P2-GATE-003` was
explicitly marked skip (see above) rather than remaining a
blocker. Once billing is settled, `P2-GATE-005` can be requested.

## 5. Phase 5: Server/Proxy (in progress elsewhere, priority 2)

Full and up-to-date detail in
[`server-implementation-pass.md`](server-implementation-pass.md). Next
items in order of proximity, as identified in that document:

1. `cat` without URL read capability: never built, needs its
   own scope decision.
2. `curl -d` (real POST): outside current scope.
3. Durable Grants → in-memory application bridge (`LocalSessionService`):
   broader change, needs its own design.
4. Idempotence of the sync push at restart, per-domain conflict resolution
   evidence, FileSystem deletion tombstones.
5. Incomplete proxy security suite (SSRF/redirect/rebinding, quotas,
   bandwidth policy).
6. Pass N+5 (direct service injection for the server-hosted host) and
   Pass N+6 (concurrent multi-tenant): deferred by ADR 0027, not oversights.

This work item continues in the other session; no action here except coordination
of global priority.

## 6. Wave 3 / Wave 5: complete before network/gameplay content

See [`integration-task-list.md#28-wave-3-editing-clipboard-and-dragdrop`](integration-task-list.md)
and [`#30-wave-5-remaining-utility-apps-and-commands`](integration-task-list.md).

- [ ] `P4-W3-002` / `P4-W3-007`: Code Editor: real rendered reload evidence
  and full offline/VFS validation missing.
- [ ] `P4-W5-APP-002`: Hack Paint: VFS image files, import/export dialogs,
  full touch/pixel E2E coverage missing.

## 7. Deliberately postponed: simulated network & gameplay (Phase 4, Waves 4 and 6)

The technical core of Wave 4 (simulated network, Browser app, `curl`/`ping`/
`nmap`, simulated sites) and of Wave 6 (gameplay domains) is already marked
complete in `implementation-status.md`. What would remain to be done in this
direction is **content expansion**, not missing plumbing,
notably the "Game domain" content pack that ADR 0023 scopes separately from the current minimal
`SmokeTestNetworkSeed` (see the "Open questions" section of
`server-implementation-pass.md`). In line with the decision made here, this
work is postponed after priorities 1 to 5 above.

Along the same lines of uncommitted ideas: hybrid compilation/execution of
user C# code (designing an app for the ecosystem and running it in the
browser and/or server side, without duplicating the compiler) was explored
in a session on 2026-08-17 and documented in
[`user-code-compilation-execution-plan.md`](user-code-compilation-execution-plan.md)
(also referenced as suggestion `S-007` in `integration-task-list.md`).
No `D-xxx` decision was made; it is not prioritized in the sequence
above.

## 8. Proposed sequence

```text
1. Close the Phase 2 gate (section 4)                    — short term
2. Continue Phase 5 server/proxy (section 5)              — in parallel, other session
✓  Windows/taskbar extraction (section 2)                 — DONE on 2026-08-17
3. Start the Mobile platform (section 3)                  — ready to start
4. Finish Wave 3 / Wave 5 outside network (section 6)      — in parallel with 1-3
—  Simulated network expansion / gameplay content (section 7) — postponed
```

## 9. Reference documents

- [`implementation-status.md`](implementation-status.md): condensed implemented state
- [`integration-task-list.md`](integration-task-list.md): exhaustive task list, source of truth
- [`server-implementation-pass.md`](server-implementation-pass.md): up-to-date server/proxy roadmap
- [`phase-2-acceptance.md`](phase-2-acceptance.md): Phase 2 evidence matrix
- [`integration-audit-remediation.md`](integration-audit-remediation.md): audit of 2026-08-03
- [`window-taskbar-export-plan.md`](window-taskbar-export-plan.md): windows/taskbar extraction plan
- [`mobile-interface-platform-plan.md`](mobile-interface-platform-plan.md): Mobile platform proposal
- [`user-code-compilation-execution-plan.md`](user-code-compilation-execution-plan.md): proposal (not committed) for hybrid WASM/server compilation/execution of user C# code
