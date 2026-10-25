# Integration Plan — `FileView` & Topic Messaging

Parent document: [`../Global-FileView-And-MessagingSystem.md`](../Global-FileView-And-MessagingSystem.md)

This plan follows the task anatomy used by [`../integration-task-list.md`](../integration-task-list.md)
(scope/location, prerequisites, exclusions, checklist, validation) at a lighter weight appropriate to one
feature area rather than the whole migration. Task IDs: `MSG-*` (messaging), `FV-*` (`FileView` control),
`INT-*` (final wiring into FileExplorer/Desktop). Checkboxes reflect real, tested implementation state —
Phase 1, Phase 1B, and Phase 2 are complete as of 2026-08-17 (see their own completion evidence); every
later phase remains future work, and `FileView`'s own project (Phase 3) is still the interfaces/models
skeleton described in its own "Skeleton delivered now" note.

## Phase 0 — Required ADRs (blocking, before any implementation checklist below starts)

**Scope:** `docs/adr/`.
**Prerequisites:** This document's approval.
**Exclusions:** No code changes in this phase.

- [x] `ADR-FV-1` Record acceptance of `FileView` as the canonical file-listing control and the resulting
  deprecation path for `FileExplorerWindow`'s inline Details/Grid rendering.
  — [ADR 0037](../../adr/0037-reusable-file-view-control.md), accepted 2026-08-17.
- [x] `ADR-MSG-1` Record acceptance of the emitter-authorized topic messaging model and the breaking
  removal of unrestricted `IAppEventGateway.Publish<TEvent>` for platform event types (see
  [`MessagingSystem.md`](MessagingSystem.md#breaking-change-notice)). Enumerates every existing
  `.Publish(` call site in the solution and states, per site, whether it stays on the kernel-only lane or
  moves to an app-owned topic. — [ADR 0038](../../adr/0038-emitter-authorized-topic-messaging.md), accepted
  2026-08-17.
- [x] `ADR-INT-1` Record acceptance of the `inode/directory` media-type convention for directory file
  associations (see Phase 5 below).
  — [ADR 0039](../../adr/0039-directory-open-file-association.md), accepted 2026-08-17.

**Validation:** Each ADR is accepted and linked from [`../README.md`](../README.md) before its dependent
phase begins, per this repo's `AGENTS.md` documentation-maintenance rule. All three are linked there now;
Phases 1 onward may begin.

## Phase 1 — Topic messaging foundation

**Scope and location:** Contracts in `Shared/HackerOs.Simulation.Abstractions/Events/`; implementation in
`Platform/HackerOs.Platform.Core/Events/`; tests in `Tests/HackerOs.Platform.Core.Tests/Events/`.
**Prerequisites:** `ADR-MSG-1`.
**Explicit exclusions:** No filesystem-watch wiring yet (Phase 2); no `FileView` consumption yet (Phase 3).

**Delivered:** `TopicName`, `TopicNameBuilder`, `TopicNames`, `TopicMessage<TPayload>`,
`PublisherIdentity`, `SharedChannelPolicy`/`SharedChannelAccessMode` (tri-state per direction — see
Phase 1B), `TopicPublishOutcome`, `TopicPublishResult`, `ITopicMessageBus`,
`ITopicChannelSubscription<TPayload>` in `Shared/HackerOs.Simulation.Abstractions/Events/TopicMessagingContracts.cs`,
and `InMemoryTopicMessageBus` in `Platform/HackerOs.Platform.Core/Events/InMemoryTopicMessageBus.cs`.

- [x] `MSG-001` Implement `InMemoryTopicMessageBus : ITopicMessageBus` in `Platform.Core`, including exact
  namespace-ownership publish authorization and one-time shared-channel registration/ownership checks.
  Completed 2026-08-17.
- [x] `MSG-002` Implement `TopicName`/`TopicNameBuilder` segment validation (non-empty, lowercase,
  `[a-z0-9-]+`, no wildcards) with focused tests for every rejected shape. Completed 2026-08-17 —
  `Tests/HackerOs.Platform.Core.Tests/Events/TopicNameTests.cs`.
- [x] `MSG-003` Extend `IAppEventGateway`/`AppEventGateway` with the topic-bus members; change
  `Publish<TEvent>` to enforce the app-publishable allow-list decided by `ADR-MSG-1` (reject, don't throw,
  matching the existing `EventDispatchFault`-style isolation convention). Completed 2026-08-17 — no CLR
  event type is currently allow-listed, so `Publish<TEvent>` always returns an empty result for app
  callers; `AppExecutionContextFactory`/`EcosystemServiceCollectionExtensions.cs` wire `ITopicMessageBus`
  through to every app instance's `AppEventGateway`.
- [x] `MSG-004` Migrate every existing `Publish<TEvent>` call site enumerated by `ADR-MSG-1` to its
  decided lane. Completed 2026-08-17 — the seven kernel-lifecycle call sites (`LocalSessionService`,
  `InMemoryProcessManager`, `EventPublishingDiagnosticSink`, `AppLifecycleOrchestrator`) already injected
  `IEventBus` directly, never `IAppEventGateway`, so no code change was needed there; the one real
  app-facing site, `Apps/Samples/HackerOs.Samples.ServiceApp/SampleTickerService.cs`, migrated to
  `SampleTickerTopics.Ticked` (`app/org.hackeros.samples.service-app/ticked`).
- [x] `MSG-005` Add tests: cross-namespace publish denial, shared-channel capability enforcement (granted
  and denied), one-time channel-ownership registration conflict, exception isolation on both `Subscribe`
  overloads, and `ITopicChannelSubscription` disposal completing its channel and detaching the subscriber.
  Completed 2026-08-17 — `Tests/HackerOs.Platform.Core.Tests/Events/InMemoryTopicMessageBusTests.cs`.

**Validation and completion evidence:** `dotnet build HackerOs.sln --no-incremental` — 0 warnings, 0
errors. `dotnet test` on `HackerOs.Platform.Core.Tests` (370 tests), `HackerOs.Ecosystem.Tests` (12,
including a DI-graph assertion that `ITopicMessageBus` resolves), and `HackerOs.Samples.ServiceApp.Tests`
(4, proving the `SampleTickerEvent` migration works end-to-end) all pass, 2026-08-17.

## Phase 1B — Declared topic permissions and optional per-channel access

**Scope and location:** `Shared/HackerOs.App.Abstractions/` (`TopicPermissions.cs`, `AppManifest.cs`,
`AppManifestValidator.cs`, `Policy/CapabilityGrant.cs`, `Schema/manifest.schema.v1.json`);
`Shared/HackerOs.Simulation.Abstractions/Events/TopicMessagingContracts.cs` (`TopicPermissionNames`
extension methods); tests in `Tests/HackerOs.App.Abstractions.Tests/`.
**Prerequisites:** Phase 1 (`MSG-001`–`MSG-005`); [ADR 0040](../../adr/0040-declared-topic-permissions.md).
**Explicit exclusions:** No approval/permissions UI (future work, not started); `HackerOs.Apps.FileExplorer`
does not yet declare or use any topic permission (deferred to Phase 4, alongside the `Topics`/`Messages`
convention — see [`MessagingSystem.md`](MessagingSystem.md#process-targeted-topics-and-inter-app-messaging)).

- [x] `MSG-006` Refactor `SharedChannelPolicy` from a nullable-string pair to an explicit, optional,
  per-direction `SharedChannelAccessMode` (`Open`/`OwnerOnly`/`RequiresCapability`) tri-state; update
  `InMemoryTopicMessageBus`'s authorization logic and every existing test. Completed 2026-08-17.
- [x] `MSG-007` Implement `TopicPermissions.IsWellFormed`/`IsOwnedByApp` (`Shared/HackerOs.App.Abstractions/TopicPermissions.cs`)
  and `TopicName.ToPublishPermission()`/`ToSubscribePermission()` extension methods. Completed 2026-08-17.
- [x] `MSG-008` Extend `CapabilityGrant`'s constructor validation and `AppManifestValidator.ValidateCapabilities`
  to accept a well-formed topic permission alongside the fixed `AppCapabilities` catalog. Completed
  2026-08-17.
- [x] `MSG-009` Add `AppManifest.DeclaredTopicPermissions`/`TopicPermissionDeclarationManifest`, validated
  for well-formedness and self-namespace ownership (`manifest.topicPermission.malformed`/`.notOwned`);
  extend `manifest.schema.v1.json` and the canonical serialization fixture. Completed 2026-08-17.
- [x] `MSG-010` Add tests: tri-state access (open/owner-only/capability, both directions), constructor
  validation (`RequiresCapability` without a capability throws; a capability set outside
  `RequiresCapability` throws), manifest validation (well-formed/malformed/not-owned declared permissions,
  a well-formed permission accepted as a requested capability), and `CapabilityGrant` accepting/rejecting
  accordingly. Completed 2026-08-17.

**Validation and completion evidence:** `dotnet test` on `HackerOs.Platform.Core.Tests` (370 tests) and
`HackerOs.App.Abstractions.Tests` (91 tests, including the updated canonical-fixture round-trip and new
`TopicPermissionsTests.cs`/`AppManifestValidatorTests.cs` cases) pass, 2026-08-17.

## Phase 2 — Filesystem watch API

**Scope and location:** Contracts in `Shared/HackerOs.Simulation.Abstractions/FileSystem/`; provider
wiring in `Platform/HackerOs.Platform.Core/FileSystem/FileSystemService.cs`; gateway in
`Platform/HackerOs.Platform.Core/Execution/AppFileSystemWatchGateway.cs`; tests in
`Tests/HackerOs.Platform.Core.Tests/FileSystem/FileSystemWatchTests.cs`.
**Prerequisites:** Phase 1 complete.
**Explicit exclusions:** `FileSystemWatchScope.Recursive` **and** `FileSystemWatchScope.ThisEntry` are
deferred (see [`MessagingSystem.md`](MessagingSystem.md#filesystem-watch-api) — `ThisEntry` was
additionally narrowed out of scope during implementation, beyond the `Recursive`-only deferral originally
planned, since `FileView`'s only real consumer never needs it and a correct implementation would need a
server-side filtering channel wrapper with no current caller); no `FileView` consumption yet.

**Delivered:** `FileSystemChangeKind`, `FileSystemWatchScope`, `FileSystemChangeEvent`,
`FileSystemTopics.ForDirectory` (implemented — hex-encodes each path segment, never a single hashed
blob), `IAppFileSystemWatchGateway.WatchAsync` (renamed from the originally-skeletoned synchronous
`Watch` — every filesystem authorization check in this codebase is async, so the gateway had to be) in
`Shared/HackerOs.Simulation.Abstractions/FileSystem/FileSystemWatchContracts.cs`;
`AppFileSystemWatchGateway` in `Platform.Core`; `KernelPublisherIdentity` in
`Platform/HackerOs.Platform.Core/Events/KernelPublisherIdentity.cs`.

- [x] `MSG-011` Register the `shared/filesystem/changed` channel at platform startup (in
  `FileSystemService`'s constructor, run once per singleton construction) with
  `PublishAccess = SharedChannelAccessMode.OwnerOnly` under `KernelPublisherIdentity` and
  `SubscribeAccess = SharedChannelAccessMode.Open` — the real subscribe-side gate is
  `AppFileSystemWatchGateway`'s own `StatAsync` pre-check (see `MSG-013`), not the bus's generic
  capability mechanism, since that mechanism has no way to vary by watched path. Completed 2026-08-17.
- [x] `MSG-012` Publish a `FileSystemChangeEvent` from every mutating `IFileSystemService` operation
  (create/write/move/copy/delete/set-permissions) to `FileSystemTopics.ForDirectory` of the affected
  entry's parent (and, for a cross-directory move, the destination parent too, as `MovedTo`). Reuses each
  provider call's own `FileSystemMutationResult.Entry` snapshot for `EntryKind`/`Revision` — no extra stat
  calls — except `Delete`, which stats the entry before deleting (for `EntryKind`, since it no longer
  exists after) and the parent after (for the post-delete `Revision`). Completed 2026-08-17.
- [x] `MSG-013` Implement `AppFileSystemWatchGateway.WatchAsync` reusing the caller's existing
  filesystem-read capability/constraint evaluation via a plain `IFileSystemService.StatAsync` call on the
  watched path — no new capability identifier, no bus-level capability check. Completed 2026-08-17.
- [x] `MSG-014` Add `Watch` to `IAppExecutionContext`'s scoped gateway set, via the same
  default-interface-member-with-unsupported-fallback pattern `Intents` already established (so
  `MinimalAppExecutionContext`/hand-rolled test doubles keep compiling), wired through
  `AppExecutionContext`/`AppExecutionContextFactory`. Completed 2026-08-17.
- [x] `MSG-015` Add tests: change events observed for each mutation kind (including same-vs-cross-directory
  move), no delivery to a subscriber lacking read access to the watched path, `ThisEntry`/`Recursive`
  rejected with `NotSupportedException`, and subscription disposal stops delivery. Completed 2026-08-17 —
  `FileSystemWatchTests.cs` (13 tests).

**Validation and completion evidence:** `dotnet build HackerOs.sln --no-incremental` — 0 warnings, 0
errors. `dotnet test` on `HackerOs.Platform.Core.Tests` (383 tests, including the 13 new
`FileSystemWatchTests`) and `HackerOs.Ecosystem.Tests` (12, confirming `FileSystemService`'s new
`ITopicMessageBus` dependency and `AppExecutionContextFactory`'s new `Watch` wiring both resolve through
the real DI graph) pass, 2026-08-17.

## Phase 3 — `FileView` control

**Scope and location:** New project `Shared/HackerOs.AppSdk.FileView/`; tests in
`Tests/HackerOs.AppSdk.FileView.Tests/`.
**Prerequisites:** `ADR-FV-1`; Phase 2 for `Watch` support (the control can be built against a `null`
`Watch` parameter first and gain live updates once Phase 2 lands — these two phases may run in parallel
after `ADR-FV-1`).
**Explicit exclusions:** No `FileExplorerWindow`/Desktop wiring yet (Phase 4/6); external OS file drag-in
and literal host-OS icon extraction remain permanently out of scope per `FileViewControl.md`.

**Skeleton delivered now (this task):** the project itself (`.csproj`, referencing `HackerOs.App.Abstractions`,
`HackerOs.AppSdk`, `HackerOs.AppSdk.Icons`, `HackerOs.Simulation.Abstractions`), plus, as interfaces/models
with no behavior: `FileViewMode`, `FileViewFolderActivationMode`, `FileViewItem` (properties only, no
mutation logic), `FileViewColumn`, `IShellIconProvider`/`ShellIconDescriptor`/`ShellIconKind`,
the full `FileViewCancelEventArgs` hierarchy, `IFileViewContextMenuProvider`/`FileViewContextMenuScope`/
`FileViewMenuItem`/`FileViewMenuItemCollection`, and an empty `FileView.razor` shell (parameters declared,
renders nothing yet) with its collocated `.razor.css`.

- [x] `FV-001` Implement `FileViewItem` selection/rename state transitions and the shared backing
  (`FileView.razor.cs`) used by all three renderers. Completed 2026-08-17.
- [x] `FV-002` Implement `FileViewDetails.razor` (sortable table) reusing `FileExplorerState`'s
  existing sort-key semantics so `FileExplorerWindow`'s migration (Phase 4) is behavior-preserving.
  Completed 2026-08-17 — plain `<table>`, not a MudBlazor grid: this is `FileView`'s own reusable rendering
  surface (used before/independent of `FileExplorerWindow`), not a MudBlazor-hosted surface per se; revisit
  if Phase 4 needs MudBlazor-specific affordances.
- [x] `FV-003` Implement `FileViewIcons.razor` (tile grid, marquee multi-select). Completed 2026-08-18.
  Marquee (rubber-band) drag-select is computed via a collocated `FileViewIcons.razor.js` (hit-testing tile
  `getBoundingClientRect`s against the dragged rectangle — not available from C#); the visual marquee
  overlay itself is pure C#/`@attributes`-splatted style, no per-move-event JS calls. Ctrl-held drag unions
  with the existing selection instead of replacing it. Click/dblclick/context-menu/F2/Delete/rename reuse
  the exact same handlers as `FileViewDetails`.
- [x] `FV-004` Implement `FileViewTree.razor` (lazy-expand nodes, shared selection state with the other
  two modes). Completed 2026-08-18. Root nodes mirror `Owner.Items` (the current directory, same as the
  other two modes); each directory node lazily fetches its children via `FileSystem.EnumerateAsync` on
  first expand through a recursive `FileViewTreeNode` component. Expansion/children state is cached by
  path in `FileViewTree`'s `_nodeCache` (keyed by `FileViewTreeNodeState`, not by `FileViewItem` reference)
  so an expanded node survives `Owner` rebuilding its `FileViewItem` instances on refresh/live-watch —
  known limitation: only the root level is kept live by the watch subscription; already-expanded
  grandchildren are a one-time fetch and do not themselves live-update (revisit if this proves visible in
  practice once Phase 4 lands). ArrowRight/ArrowLeft expand/collapse; Enter/F2/Delete match the other modes.
- [x] `FV-005` Implement inline rename (label ↔ textbox swap, Enter/Escape/blur handling, routed through
  `MoveAsync` and the `Renaming`/`Moving` event pair). Completed 2026-08-17.
- [x] `FV-006` Implement drag & drop: `FileView.razor.js` `DataTransfer` interop, intra- and inter-control
  move/copy, `Moving`/`Copying` cancelable events. Completed 2026-08-18. Every row/tile/node across all
  three modes is `draggable`; decision logic (which item(s), copy-vs-move, valid drop target) is entirely
  C#, reached through `DragEventArgs`/`MouseEventArgs` inheritance (`CtrlKey`, and `DataTransfer.Types` for
  the read-only "is this a HackerOS-internal drag" check) — `FileView.razor.js` does only the two things
  Blazor's `DataTransfer` can't marshal: `setDragData`/`getData` under the
  `application/vnd.hackeros.file-drag+json` MIME type, using `VirtualFileDragPayload`/
  `VirtualFolderDragPayload` (now no longer unused) wrapped in a small `FileViewDragEnvelope` since a
  multi-select drag may mix files and folders. A plain drop routes through the new
  `FileView.MoveItemsAsync`; Ctrl-held routes through the already-existing `CopyItemsAsync` — both fire the
  pre-existing cancelable `Moving`/`Copying` events, so no new event surface was needed.
  `MoveItemsAsync` differs from every other mutation method here in re-Statting each dragged item's own
  parent directory (rather than assuming `_currentDirectoryRevision`), because a dragged item may come from
  a directory this `FileView` instance has never enumerated (inter-control drag from another window) —
  `CopyItemsAsync` never needed this since `FileSystemCopyRequest` doesn't take a source-parent revision at
  all. Self-drops (a folder dragged onto itself) are filtered before either method is even called. Added
  `OwnerAppId` (purely informational — a drop never needs to trust or even know the source app) for the
  payload's `SourceAppId`. **Caveat:** the JS side leans on `window.event` still referring to the live
  native event when a same-stack, no-prior-`await` JS interop call runs in Blazor WebAssembly (required
  because `dataTransfer.setData`/`getData` are only valid synchronously within `dragstart`/`drop`) — this
  timing assumption, and native drag visuals generally, are **unverified**: there is still no in-app host to
  drag-and-drop through (Phase 4 not started). Only the C# decision logic is test-covered, via
  `FakeJSRuntime`/`FakeJSObjectReference` standing in for the real `DataTransfer` interop.
- [x] `FV-007` Implement the default `IShellIconProvider` (extension→Lucide table + generic fallback) and
  `ShellIcon.razor` (Vector/Png rendering switch); register the default provider in
  `EcosystemServiceCollectionExtensions` alongside `IIconCatalog`. Completed 2026-08-17.
- [x] `FV-008` Implement `FileViewContextMenu.razor` and the provider-customization pipeline
  (Background/Directory/File/FileType ordering, `Insert(After|Before)`/`Clear`, then `ContextMenuOpening`).
  Completed 2026-08-17.
- [x] `FV-009` Implement folder double-click activation for all three `FileViewFolderActivationMode`
  values, including the `IAppIntentGateway.OpenFileAsync(path, mediaType: "inode/directory")` call for
  `NewWindow` (this call succeeds meaningfully only once Phase 5 lands; until then it correctly resolves
  to `NoHandler`/`ChooserRequired`, which is acceptable intermediate behavior, not a bug). Completed
  2026-08-17 — required adding the `mediaType` parameter to `IAppIntentGateway.OpenFileAsync` itself
  (previously path-only); `OpenFileIntent.MediaType`/`FileAssociationResolver` already supported it end to
  end, only the public gateway signature was missing this piece of `ADR-INT-1`'s contract.
- [x] `FV-010` Wire the `Watch` parameter to `IAppFileSystemWatchGateway`, replacing/augmenting manual
  `RefreshAsync` calls with live updates when supplied. Completed 2026-08-17 (delivered alongside FV-001 as
  part of the same backing file); covered by
  `FileViewTests.A_change_from_another_actor_is_picked_up_through_the_watch_gateway`.
- [x] `FV-011` Accessibility pass: keyboard navigation, focus indicators, ARIA roles per
  `../accessibility.md` for all three modes. Completed 2026-08-18 for everything programmatically
  verifiable; see the caveat below for what isn't. ARIA roles were already in place from `FV-002`/`FV-003`/
  `FV-004` (`role="grid"`/`"listbox"`/`"tree"` etc.) and `:focus-visible` styling existed per-renderer; the
  substantive gap this closed was **roving tabindex**: every row/tile/node previously had `tabindex="0"`
  unconditionally, meaning a keyboard user had to `Tab` through every single item in a directory just to
  get past the control — a real keyboard trap for anything but a tiny directory, not a cosmetic ARIA gap.
  Fixed by making only the active item (the selection, or the first item when nothing is selected yet) a
  Tab stop (`tabindex="0"`; everything else `tabindex="-1"`) across all three renderers, paired with
  ArrowUp/ArrowDown (Details: sorted order; Icons: all four arrows move through `Owner.Items` order, since
  true 2D grid navigation would need tile-per-row layout information that isn't available in C# — a
  deliberate scoping decision, not a bug; Tree: ArrowUp/Down walk `FileViewTree.GetVisibleNodesInOrder()`,
  a depth-first flatten respecting expansion state, alongside the existing ArrowRight/Left expand/collapse)
  that both move the selection and move real DOM focus via a new `focusItem` export in `FileView.razor.js`
  (reusing the same module `FV-006`'s drag-drop already loads) and `FileView.MoveActiveItemAsync`. Every
  row/tile/node now also carries `data-item-path` (Icons' existing `data-tile-path` was renamed to this for
  consistency) so `focusItem` can find any of them from one shared implementation regardless of mode.
  **Caveat, matching the pattern already noted for `FV-003`/`FV-004`/`FV-006`:** the accessibility.md
  "Human evidence checklist" (screen-reader pass, keyboard-only walkthrough with recorded AT versions) is
  explicitly something only a person can complete, and there is still no in-app host to walk through in the
  first place (Phase 4 not started) — neither is claimed here. What *is* covered: `IsTabStop` fallback
  logic and ArrowUp/Down/Left/Right movement (including at-boundary no-ops) across all three renderers, via
  direct component tests.
- [x] `FV-012` Add component tests for the slice delivered so far (`FV-001`/`FV-002`/`FV-005`/`FV-008`/
  `FV-009`/`FV-010`): every cancelable event's veto path (Navigating/Opening/Renaming/Deleting/Copying/
  SelectionChanging), rename commit/cancel/idempotency, all three folder-activation modes, delete, create
  (including the unique-name "New Folder (2)" interactive path), copy, and live-watch pickup. 25 tests,
  `Tests/HackerOs.AppSdk.FileView.Tests/FileViewTests.cs`. No bUnit in this solution: components are
  attached to a real `RenderHandle` via a minimal custom `Renderer` (`TestComponentRenderer.cs`) so
  `StateHasChanged`/`InvokeAsync` behave exactly as in production, while `FileView`'s real markup — which
  would otherwise pull in MudBlazor/JSInterop — is suppressed by a test-only `BuildRenderTree` override, per
  this repo's "component logic tested by direct instantiation, not full DOM rendering" convention. Drag-drop
  tests were added separately alongside `FV-006` (`FileViewDragDropTests.cs`); context-menu customization
  *ordering* tests remain for when a second real `IFileViewContextMenuProvider` exists (Phase 4's `.zip`
  provider, `INT-003`). Completed 2026-08-17.

**Validation and completion evidence:** `dotnet build HackerOs.sln --no-incremental` — 0 warnings, 0 errors.
`dotnet test` on `Tests/HackerOs.AppSdk.FileView.Tests` (63/63 — the original 25 (`FileViewTests.cs`) plus 5
(`FileViewDetailsTests.cs`), 10 (`FileViewIconsTests.cs`), 13 (`FileViewTreeTests.cs`), and 10
(`FileViewDragDropTests.cs`)), `HackerOs.Platform.Core.Tests` (383/383, unaffected), and
`HackerOs.Ecosystem.Tests` (12/12) all pass, 2026-08-18. The Icons tests cover tile click/dblclick/F2 and
the full marquee-select flow (replace, Ctrl-additive, below-threshold no-op, multiselect-disallowed no-op)
via a `FakeJSRuntime`/`FakeJSObjectReference` test double (no real browser/JS host in this xunit process —
see `FakeJSRuntime.cs`). The Tree tests cover root-node/`Owner.Items` mirroring, expansion-state cache
survival across a refresh, first-expand lazy load, collapse/re-expand without refetching, the file-node
no-op case, and click/dblclick/ArrowRight/ArrowLeft. The drag-drop tests reuse the same `FakeJSRuntime`
double to cover payload building (single item, whole-selection, `AllowDragDrop=false` no-op), move vs
Ctrl-held copy routing, the non-directory/foreign-MIME-type/self-drop guards, `Moving`-cancel, and
`MoveItemsAsync`'s general source-parent resolution for an item outside this instance's own `Items`. The new
`FileViewDetailsTests.cs` (this renderer previously had no dedicated test file — its click/rename/sort
handlers were only ever exercised indirectly, if at all) and the roving-tabindex additions to the Icons/Tree
test files cover `IsTabStop`'s selection-follows/first-item-fallback logic and ArrowUp/Down/Left/Right
movement including at-boundary no-ops, again via `FakeJSRuntime` standing in for `focusItem`. What none of
this covers is the real `DataTransfer`/`window.event` timing behavior itself (`FV-006`'s caveat) or actual
focus movement/native drag visuals in a browser. The shared `TestableFileView`/`FileViewTestFixture`/
`TestComponentHelpers` test scaffolding used to live nested inside `FileViewTests.cs`; it was extracted to
its own files so all renderers' test classes could reuse it without duplication. All three modes now render
real content instead of a placeholder, drag between/within them is wired end-to-end at the C# level, and
keyboard users can now Tab past the control in one step and move the active item with arrow keys — no
manual/E2E browser check yet since there is still no in-app host (Phase 4) to preview through; that check,
plus first-hand verification of the drag-drop/focus native-event timing assumptions and the
`accessibility.md` human-evidence checklist, all move to Phase 4 completion.

## Phase 4 — Migrate `HackerOs.Apps.FileExplorer` onto `FileView`

**Scope and location:** `Apps/System/HackerOs.Apps.FileExplorer/`.
**Prerequisites:** Phase 3 complete through at least `FV-008`.
**Explicit exclusions:** No change to `OpenWithDialog.razor`/`FilePropertiesDialog.razor` behavior — they
remain host-owned dialogs `FileView` doesn't know about.

- [x] `INT-001` Replace `FileExplorerWindow.razor`'s inline Details/Grid rendering and inline context menu
  with a hosted `FileView`, wiring `FileExplorerState`'s existing navigation history/search/sort concerns
  through `FileView`'s parameters/events instead of duplicating them. Completed 2026-08-18. Navigation:
  the toolbar/address bar call `FileExplorerState.NavigateBack/Forward/Up/To` for history bookkeeping
  *and* `FileView.NavigateAsync` explicitly; `FileView.OnPathChange` (raised for in-`FileView` navigation
  like a directory double-click) calls back into `FileExplorerState.NavigateTo`, which is naturally
  idempotent for the button-triggered case since `CurrentPath` was already updated before the second call
  arrives. Sort: deleted from `FileExplorerState` entirely — `FileViewDetails` owns it now, so there was
  nothing left to wire. Search: `FileView` had no filter hook at all (not a "wire it through" case), so a
  small `Filter`/`RefreshFilter()` addition was made to `FileView` itself first (see the entry below).
  Selection/rename/delete/properties/open-with now read `FileView.SelectedItem(s)`/call
  `FileViewItem.Rename()` directly — no dialog for rename anymore, since that's what `FV-005`'s inline
  rename exists for. `FileView` exposes no public Create/Delete method (only its internal context-menu
  handlers reach those), so the toolbar's New Folder/New File/Delete still call
  `AppContext.FileSystem` directly, the same raw gateway calls `FileView`'s own internal handlers make,
  just from this project instead of that one, then call `FileView.RefreshAsync()` — a small, deliberate,
  documented asymmetry (see "Known gaps" in `docs/apps/file-explorer.md`), not an oversight.
- [x] `INT-001a` (unplanned prerequisite) Add `FileView.Filter`/`RefreshFilter()`. `FileView`'s accepted
  spec had no search/filter hook at all; `Filter` limits what `RebuildItems` includes across all three
  modes, and `RefreshFilter()` re-derives `Items` from the last fetched snapshot (cached, no filesystem
  round-trip) so a search box can react per keystroke. Documented in `FileViewControl.md`'s parameter
  table and external scripting surface; covered by 3 new tests in `FileViewTests.cs`.
- [x] `INT-002` Add a Tree-mode toggle to the existing view-mode toolbar control (today Details/Grid only).
  Completed 2026-08-18 — trivial once `FileExplorerState.ViewMode` was retyped from the old two-value
  `FileExplorerViewMode` enum to `HackerOs.AppSdk.FileView.FileViewMode` directly (itself an instance of
  "wire through instead of duplicating"): a third toolbar button plus `@bind-Mode="_state.ViewMode"`.
- [x] `INT-003` Register a `.zip` `IFileViewContextMenuProvider` inserting `"UnZip Here…"` after `"open"`.
  Completed 2026-08-18 — `ZipFileContextMenuProvider.cs`, the first real `IFileViewContextMenuProvider` in
  the codebase, exactly the worked example from `FileViewControl.md`'s Context menu customization section.
  Takes a `Func<FileView?>` (not a direct instance) because the host constructs providers in
  `OnAppInitialized()`, before `@ref` captures the `FileView` instance. Extraction logic itself lives in
  `FileExplorerZipService.cs`, shared with the toolbar's own Extract button — one implementation, two
  entry points, per the plan's "reusing the same extraction logic" note.
- [x] `INT-004` Update `HackerOs.Apps.FileExplorer.csproj` to reference `HackerOs.AppSdk.FileView`.
  Completed 2026-08-18.
- [x] `INT-005` Update `Tests/HackerOs.Apps.FileExplorer.Tests/` for the new composition; update
  [`../apps/file-explorer.md`](../apps/file-explorer.md)'s architecture section per this repo's
  documentation-is-part-of-done rule. Completed 2026-08-18. `FileExplorerStateTests.cs`'s sort/selection
  tests were deleted (that functionality moved to `FileView`, tested there already) rather than adapted;
  two new navigation-edge-case tests and a `ViewMode` test were added in their place (4 tests total, was
  2). `file-explorer.md` rewritten to describe the hosted-`FileView` architecture, including a "Known
  gaps" section (see validation notes below).

**Validation and completion evidence:** `dotnet build HackerOs.sln --no-incremental` — 0 warnings, 0
errors. `dotnet test` on `HackerOs.Apps.FileExplorer.Tests` (4/4), `HackerOs.AppSdk.FileView.Tests`
(66/66 — the 63 from Phase 3 plus 3 new `Filter`/`RefreshFilter` tests), `HackerOs.Platform.Core.Tests`
(383/383), and `HackerOs.Ecosystem.Tests` (12/12) all pass, 2026-08-18.

**Live browser check — completed, 2026-08-18.** The earlier "partially blocked" state below was chased
down to its actual root causes (five separate, genuine, pre-existing infrastructure bugs, none introduced
by the `FileView`/`FileExplorer` migration itself) and all five are now fixed, verified across **all three
HackerOS hosts** — `HackerOs.Server` (Interactive Server), `test/test` (WASM debug harness), and the
standalone `HackerOs.Ecosystem` WASM PWA:

1. **`.claude/launch.json` missing `ASPNETCORE_ENVIRONMENT=Development`** for `HackerOs.Server` — without
   it, ASP.NET Core's static-web-assets *development* runtime handler (needed to compose assets from
   referenced projects like `HackerOs.Ecosystem`/`HackerOs.AppSdk.FileView`) never activates and every
   static asset 500s. Worked around per-session by launching with the env var set explicitly; the
   launch-config gap itself is unfixed (out of scope, flagged separately).
2. **Singleton services capturing a dead `IJSRuntime`** — `EcosystemServiceCollectionExtensions.cs`
   registered ~25 IndexedDB-backed services (`EcosystemBootCoordinator`, `IndexedDbFileSystemBootstrapper`,
   `WebCryptoPasswordHasher`, `HostExceptionReporter`, etc.) as `AddSingleton`, but Blazor Web Apps
   construct-inject a component's `[Inject]` properties during the static-SSR pass of the initial HTTP
   response even when that component's own `@rendermode` has `prerender:false` (prerender:false only skips
   its *lifecycle methods* during that pass, not construction) — so the first-ever resolution permanently
   captured that pass's unattached `IJSRuntime`, and every real circuit afterward reused the same dead
   instance, failing every JS interop call with "cannot be issued at this time... statically rendered."
   This was the "Recovery environment / Local storage is unavailable" screen. Fixed by converting the
   whole `IJSRuntime`-touching chain (and its transitive dependents — `IFileSystemService`,
   `ISessionService`, `AppLifecycleOrchestrator`, etc.) from `AddSingleton` to `AddScoped`, matching
   Blazor Server's real per-circuit `IJSRuntime` lifetime (a no-op for WASM hosts, which have exactly one
   implicit scope for the app's lifetime).
3. **`InProcessAssemblyTransport` never actually loading lazy app assemblies** — it only checked
   `AppDomain.CurrentDomain.GetAssemblies()`, but a compile-time project reference doesn't put an assembly
   into that list until something touches one of its types; since apps are wired up by name/reflection via
   the catalog, nothing forced that load before the first launch attempt. Fixed with an `Assembly.Load`
   fallback.
4. **SignalR's default 32KB message cap** — the initial interactive render (desktop shell + taskbar +
   app launcher, backed by the full app-catalog/manifest set) produces a render batch larger than that on
   first connect, closing the circuit immediately. Fixed via `Configure<HubOptions>` on the server host.
5. **Wrong `app.css`** — both `HackerOs.Server` and `test/test`'s host pages linked their own
   project-local `wwwroot/app.css` (the unmodified `dotnet new blazor` scaffold stub, 3 rules) instead of
   `HackerOs.Ecosystem`'s real design-system tokens at `wwwroot/css/app.css`, leaving every surface
   completely unstyled. Fixed by correcting the `<link>` path in both hosts' `App.razor`.
6. **`test/test` WASM entry-point conflict** — `HackerOs.Ecosystem`'s own `Program.cs` (needed for its
   separate standalone-PWA deployment) still executes when the assembly is merely loaded as a referenced
   component library by another Blazor WebAssembly host, because .NET's WASM boot process invokes `Main()`
   on every loaded assembly that declares one, not just the actual hosting project's. There, `"#app"`
   never exists (that host mounts `HackerOs.Ecosystem.App` via marker-based auto-discovery instead), so
   `RootComponents.Add<App>("#app")` failed at mount time and aborted `RunAsync()` entirely before the
   marker-based components ever got a turn — `test/test` has no dedicated `.Client` WASM project of its
   own, so this file's `RunAsync()` is what bootstraps the shared WASM runtime there too. Fixed by checking
   for `"#app"` via the already-built host's `IJSRuntime` and removing that root-component mapping when
   absent, before calling `RunAsync()`, leaving it free to complete its shared-runtime role.

All three hosts were exercised end-to-end in a real browser: boot → create administrator → sign in →
desktop → launch File Explorer (`org.hackeros.file-explorer`) → navigate into a subdirectory → switch
between Details/Icons/Tree view modes → create a file → select it → delete it, all working correctly. The
`HackerOs.Server` host was additionally confirmed properly styled (dark theme, design tokens) after fix
#5. Drag-drop between two windows and the zip context-menu item were not separately re-exercised in this
pass (already covered by `HackerOs.AppSdk.FileView.Tests`/component tests) but nothing in these fixes
touches that code path. `dotnet test` on `HackerOs.sln` (excluding `E2E`/`UI.E2E`, which need a Playwright
browser harness not available in this environment) — 28/28 test projects pass; the 3 pre-existing failures
in `HackerOs.Infrastructure.Browser.Tests` (`declaredTopicPermissions` manifest-fixture/serializer
mismatch) predate this session's work and are unrelated to `FileView`/`FileExplorer`, tracked separately.

## Phase 5 — Directory file association

**Scope and location:** `Shared/HackerOs.App.Abstractions/AppManifest.cs` (`FileHandlerManifest` matching
only, no schema shape change needed — `MediaType` already exists), `Platform/HackerOs.Platform.Core/Intents/FileAssociationResolver.cs`,
`Apps/System/HackerOs.Apps.FileExplorer/app.manifest.json`.
**Prerequisites:** `ADR-INT-1`.
**Explicit exclusions:** No change to how *files* (non-directories) are associated — this phase only adds
directory handling alongside the existing extension/media-type matching.

- [x] `INT-006` Adopt the `inode/directory` media-type convention (Unix precedent) for directory-open
  intents: `OpenFileIntent.MediaType = "inode/directory"` when the target path is a directory. Already
  true since `FV-009` (this phase's only job here was confirming/documenting it, not writing it — see
  `INT-010`): `FileView.ActivateItemAsync`'s `NewWindow` branch already calls
  `Intents.OpenFileAsync(item.FullPath, mediaType: "inode/directory")`, and the gateway/dispatcher already
  thread an optional `mediaType` straight through to `OpenFileIntent` with zero auto-detection — the
  convention is opt-in per caller, not inferred from the path by the resolver.
- [x] `INT-007` Confirm `FileAssociationResolver.HandlesFile`/`MatchesTarget` require no code change (they
  already match by `MediaType` when set) — completed 2026-08-18. Confirmed with zero production-code
  changes to `FileAssociationResolver`/`FileAssociationIndex`: `MatchesTarget` already falls through to
  its media-type branch whenever `extension` is null (true for every directory path, since
  `GetExtension` finds no dot in a bare directory name). 4 new regression tests added to
  `FileAssociationResolverTests.cs` proving a directory-only `FileHandlerManifest` resolves through all
  four outcomes: `An_explicit_valid_preferred_app_is_used_for_a_directory_target`,
  `A_configured_media_type_default_is_preferred_over_directory_candidates`,
  `A_sole_directory_candidate_is_used_without_a_configured_default`,
  `Multiple_directory_candidates_without_a_configured_default_require_a_chooser` (the latter two clear the
  now-seeded association document first — see `INT-009` — so they still genuinely exercise the
  no-configured-default paths rather than incidentally hitting the new seeded default).
- [x] `INT-008` Add a `FileHandlerManifest(MediaType: "inode/directory", Extensions: [], Actions: ["open"])`
  entry to `HackerOs.Apps.FileExplorer/app.manifest.json`'s (currently absent) `fileHandlers` declaration.
  Completed 2026-08-18.
- [x] `INT-009` Seed `org.hackeros.file-explorer` as the protected default handler for `inode/directory` in
  the canonical `/etc/hackeros/file-associations.json` document (`FileAssociationSettingsDocuments`),
  matching how other protected defaults are seeded. Completed 2026-08-18 —
  `FileAssociationSettingsDocuments.EmptyDocumentContent` now bakes in the seeded association, the same
  way `AppearanceSettingsDocuments.EmptyDocumentContent` already bakes in real default values despite its
  name (kept for consistency with that established precedent, not renamed).
- [x] `INT-010` Update [`../app-intents-and-associations.md`](../app-intents-and-associations.md) to
  document the `inode/directory` convention. Completed 2026-08-18 — new "The `inode/directory` convention"
  subsection covering both the caller side (`FileView.ActivateItemAsync`) and handler side
  (`org.hackeros.file-explorer`'s manifest + seeded default), plus updated `Tests` section entries.

**Validation and completion evidence:** `dotnet build HackerOs.sln --no-incremental` — 0 warnings, 0
errors. `dotnet test` on `HackerOs.Platform.Core.Tests` (388/388 — 383 from Phase 4 plus 4 new
`FileAssociationResolverTests` directory-handler tests plus 1 new `AppIntentDispatcherTests` end-to-end
test), `HackerOs.Apps.FileExplorer.Tests` (4/4), `HackerOs.AppSdk.FileView.Tests` (66/66),
`HackerOs.Ecosystem.Tests` (12/12), and `HackerOs.App.Abstractions.Tests` (91/91) all pass, 2026-08-18.
The headless end-to-end test the plan called for —
`AppIntentDispatcherTests.Open_file_intent_for_a_directory_resolves_org_hackeros_file_explorer_as_the_seeded_default_and_launches_it`
— opens a directory path via the real `AppIntentDispatcher` (the same call `AppIntentGateway.OpenFileAsync`
delegates to) with no explicit preferred app, against the fixture's real seeded
`FileAssociationSettingsDocuments.CreateDefinition()`, and observes `org.hackeros.file-explorer` resolved
as `ConfiguredDefault` (not `SoleCandidate`, since `INT-009`'s seeding makes `ConfiguredDefault` the actual
out-of-the-box outcome now) and dispatched (`AppIntentDispatchStatus.Dispatched`, `result.Process` set) —
matching `FV-009`'s `NewWindow` activation path end-to-end, exactly as the plan asked.

## Phase 6 — `DesktopArea`/`DesktopShell` background slot (infrastructure only)

**Scope and location:** `Platform/HackerOs.Windowing.Blazor/DesktopArea.razor`,
`Platform/HackerOs.Platform.Blazor/Shell/DesktopShell.razor`.
**Prerequisites:** None beyond this document's approval — independent of every phase above.
**Explicit exclusions:** Per the [confirmed
decision](../Global-FileView-And-MessagingSystem.md#key-decisions-already-made), this phase does **not**
host a `FileView` on the Desktop or render desktop icons. It adds the slot only; wiring an actual
`FileView`-backed desktop-icons feature into that slot is a distinct future phase, not opened by this
plan.

- [x] `INT-011` Add `[Parameter] public RenderFragment? BackgroundContent { get; set; }` to
  `DesktopArea.razor`, rendered as a new `<section class="background-layer">` immediately after the
  existing `desktop-grid` background `<div>` and before `<section class="window-layer">` — i.e. above the
  background, below every window, matching z-order requirements for window chrome to always occlude it.
  Completed 2026-08-18. No `z-index` needed: `.desktop-area` already has `isolation: isolate`, and all
  three siblings are `position: absolute` with default stacking order, so placing `background-layer`
  between the other two in DOM source order (which the `@if` block does unconditionally) is sufficient by
  itself. `background-layer` is `pointer-events: none` and `aria-hidden="true"`, matching the existing
  `desktop-grid` sibling's own decorative-background treatment.
- [x] `INT-012` Thread the parameter through `DesktopShell.razor` (which currently instantiates
  `DesktopArea` without a background slot) as its own optional `BackgroundContent` parameter, so a future
  composition root can supply content without `DesktopShell` needing to know what that content is.
  Completed 2026-08-18 — a one-line pass-through parameter, nothing more.
- [x] `INT-013` Add a component test in `Tests/HackerOs.Platform.Blazor.Tests/` proving supplied
  `BackgroundContent` renders in the correct DOM position relative to `window-layer`, and that omitting it
  changes nothing observable (backward compatible, opt-in). Completed 2026-08-18 —
  `DesktopAreaBackgroundContentTests.cs`, using a new `FrameCapturingRenderer` (same no-bUnit
  `TestComponentRenderer` pattern established for `HackerOs.AppSdk.FileView.Tests`, but capturing the
  render tree's frames via `Renderer.GetCurrentRenderTreeFrames` instead of discarding them, since this is
  the first test in this solution that needs to assert actual DOM element order rather than just C#
  backing state). One real wrinkle worth recording: `desktop-grid`'s `<div>` has nothing dynamic inside it,
  so the Blazor compiler collapses it into a single static `Markup` frame rather than live
  `Element`/`Attribute` frames — `background-layer`/`window-layer` both have dynamic children
  (`@BackgroundContent`/the window `@foreach`) so they stay as `Element` frames. The frame-walking helper
  handles both shapes. 2 tests, both passing; the pre-existing `DesktopShellTests.cs`/`Windowing.Core.Tests`
  suites are unaffected (34/34 and 11/11 respectively).
- [x] `INT-014` Update [`../desktop-shell.md`](../desktop-shell.md) to document the new slot. Completed
  2026-08-18.

**Validation and completion evidence:** `dotnet build HackerOs.sln --no-incremental` — 0 warnings, 0
errors. `dotnet test` on `HackerOs.Platform.Blazor.Tests` (34/34 — 32 existing plus 2 new
`DesktopAreaBackgroundContentTests`), `HackerOs.Platform.Core.Tests` (388/388), `HackerOs.Ecosystem.Tests`
(12/12), and `HackerOs.Windowing.Core.Tests` (11/11) all pass, 2026-08-18. No manual browser check: this
phase adds an unused slot with no current caller (per its own explicit exclusion), so there is nothing
visually different to observe yet — the component test is the complete verification surface for what this
phase actually delivers.

## Summary checklist (this task's actual deliverable)

- [x] Overview document (`../Global-FileView-And-MessagingSystem.md`).
- [x] `FileViewControl.md` full specification.
- [x] `MessagingSystem.md` full specification.
- [x] This integration plan.
- [x] `docs/README.md` index updated.
- [x] `TopicMessagingContracts.cs`, `FileSystemWatchContracts.cs`, and the `HackerOs.AppSdk.FileView`
  project — long since grown past "skeletons" into the full implementation tracked by Phases 1 through 6
  above, all now complete.

## Post-completion fixes (2026-08-19)

Real usage the day after Phase 4 completed surfaced two `FileView` bugs, both now fixed (not tracked
against a new `INT-xxx` item since they're fixes to already-`[x]` work, not new scope):

- **Details column relabeled `Kind` → `Type`, with folder-first sort grouping added.** The originally
  planned `Kind` column (`FileViewControl.md` line ~119) read poorly once real files were on screen;
  it now reads `"Folder"` for directories and `".ext File"` (or `"File"`) otherwise, and folders always
  sort before files regardless of the active sort column/direction. `FileViewControl.md` is updated to
  match.
- **In-place rename didn't commit when clicking a different row.** A blur-vs-click-re-render race left
  the rename textbox open. `FileViewDetails.razor.cs` now explicitly commits any in-progress rename
  (`CommitPendingRenameElsewhereAsync`) before handling a click, double-click, or context-menu on
  another row.
