# ADR 0038: Emitter-Authorized Topic Messaging

## Status

Accepted on 2026-08-17.

## Context

`Shared/HackerOs.Simulation.Abstractions/Events/EventBusContracts.cs` (`IEventBus`), implemented by
`Platform/HackerOs.Platform.Core/Events/InMemoryEventBus.cs`, is the one in-process pub/sub mechanism in
HackerOS (`P1-SYS-008`). It is exposed to app code as `IAppEventGateway`
(`Shared/HackerOs.Simulation.Abstractions/Gateways/AppGatewayContracts.cs`), whose current implementation,
`Platform/HackerOs.Platform.Core/Execution/AppEventGateway.cs`, is a blind pass-through:

```csharp
public IReadOnlyList<EventDispatchFault> Publish<TEvent>(TEvent @event) where TEvent : notnull =>
    _eventBus.Publish(@event);
```

Every other scoped gateway an app receives through its `IAppExecutionContext`
(`IAppFileSystemGateway`, `IAppSettingsGateway`, `IAppNotificationGateway`, ...) evaluates trusted
capability policy before acting. `AppEventGateway.Publish` does not: any app holding an
`IAppEventGateway` reference can publish **any** event type, including the platform's own trusted
lifecycle events. Enumerating every real (non-test) `.Publish(` call site in the solution today
(`Grep -n "\.Publish\(" --glob *.cs`) confirms both halves of the problem concretely:

| Call site | Event type(s) | Caller |
| --- | --- | --- |
| `Platform.Core/Sessions/LocalSessionService.cs` | `SessionActivatedEvent`, `SessionLoggedOutEvent`, `SessionShutDownEvent` | Platform.Core (trusted) |
| `Platform.Core/Processes/InMemoryProcessManager.cs` (4 sites) | `ProcessStateChangedEvent` | Platform.Core (trusted) |
| `Platform.Core/Diagnostics/EventPublishingDiagnosticSink.cs` | `DiagnosticEntryRecordedEvent`, `DiagnosticLogClearedEvent` | Platform.Core (trusted) |
| `Platform.Core/Lifecycle/AppLifecycleOrchestrator.cs` | `AppDisabledEvent` | Platform.Core (trusted) |
| `Apps/Samples/HackerOs.Samples.ServiceApp/SampleTickerService.cs` | `SampleTickerEvent` | **App code, via `context.Events.Publish` (`IAppEventGateway`)** |

Every trusted lifecycle event is already published exclusively from `Platform.Core`. Nothing today
actually publishes a kernel event from app code — but nothing *prevents* it either, and the one existing
app-facing call site (`SampleTickerService`) demonstrates the exact shape of the gap: an app publishing
its own custom event type through the same unguarded `Publish<TEvent>` that would just as readily accept
`SessionLoggedOutEvent` or `AppDisabledEvent` from any other app. Closing this — not merely documenting it
— was confirmed as a real requirement, not a hypothetical hardening exercise, alongside a companion need:
a named, namespaced, `System.Threading.Channels`-based publish/subscribe mechanism for app-to-app and
platform-to-app notifications (starting with filesystem-directory-change events for
[`FileView`](0037-reusable-file-view-control.md)), built as an extension of the existing bus rather than a
second parallel system.

The full specification this ADR accepts is
[`../Global-FileView-And-MessagingSystem/MessagingSystem.md`](../old/Global-FileView-And-MessagingSystem/MessagingSystem.md);
this ADR records the binding architectural commitments and the concrete migration table, not the complete
contract shapes (those are specified in full there and in the already-added skeleton
`TopicMessagingContracts.cs`/`FileSystemWatchContracts.cs`).

## Decision

### 1. Two authorization lanes over one delivery mechanism

`InMemoryEventBus`/`IEventBus` itself does not change. What changes is what `IAppEventGateway` is allowed
to forward:

- **Kernel lane** (unchanged surface, narrowed access): `SessionActivatedEvent`, `SessionLoggedOutEvent`,
  `SessionShutDownEvent`, `ProcessStateChangedEvent`, `DiagnosticEntryRecordedEvent`,
  `DiagnosticLogClearedEvent`, and `AppDisabledEvent` remain published only from their existing
  `Platform.Core` call sites, calling `IEventBus.Publish<TEvent>` directly (not through
  `IAppEventGateway`). Every app may still **subscribe** to these — they are broadcast notifications, not
  secrets, and read access was never the problem — via `IAppEventGateway.Subscribe<TEvent>`, unchanged.
- **App topic lane** (new): apps publish through named, namespaced `TopicName`s
  (`TopicNames.ForApp(...)`/`TopicNames.Shared(...)`, never a hand-typed string) instead of bare CLR
  types. `SampleTickerService` migrates its `SampleTickerEvent` to a topic under
  `TopicNames.ForApp("org.hackeros.samples.service-app")`, becoming the first real example of this lane.

### 2. `IAppEventGateway.Publish<TEvent>` becomes an app-publishable allow-list check

`AppEventGateway.Publish<TEvent>`'s blind pass-through is replaced with a check against an explicit
allow-list of event types apps may publish. Denial returns an empty/denied result (matching the existing
`EventDispatchFault`-style isolation convention already used for subscriber exceptions) rather than
throwing, so a denied publish fails predictably for the caller instead of crashing it. The exact mechanism
for defining the allow-list (e.g. requiring event types to implement a marker interface carrying their
owning `TopicName`, vs. an explicit type registry populated at composition-root time) is left to the
implementing task (`MSG-003` in `integrationPlan.md`) to choose during implementation, not fixed by this
ADR — but no kernel lifecycle event type listed above may ever appear on that allow-list.

### 3. Shared channels and directory-watch reuse the existing capability model

A module may declare a shared channel (`ITopicMessageBus.RegisterSharedChannel`) restricted by an optional
`SharedChannelPolicy` evaluated through the same `ICapabilityChecker`/`CapabilityPolicyEvaluation` every
other gateway already uses — no new grant storage, no new evaluation algorithm, no new capability
identifiers are introduced by this ADR. The filesystem-watch channel specifically reuses each caller's
*existing* filesystem-read capability/constraint rather than inventing a "can watch" capability: watching
a directory must never reveal more than reading it already would.

### 4. Topic names are exact-match only, mirroring ADR 0003

No wildcard/prefix topic subscriptions in this version, deliberately kept symmetric with ADR 0003's
exact-capability-matching precedent.

## Consequences

- **Breaking change** to the Phase 1 baseline event-bus contract (`P1-SYS-008`) and its app-facing gateway
  (`P1-EXEC-004`): `IAppEventGateway.Publish<TEvent>` no longer succeeds for an arbitrary event type. This
  is intentional and is the entire point of this ADR; any future app relying on publishing a kernel event
  type directly must instead publish to its own topic and have interested parties subscribe to that topic.
- `SampleTickerService` (`Apps/Samples/HackerOs.Samples.ServiceApp`) must migrate to the topic lane as part
  of implementation (`MSG-004`) — it is the one real call site this ADR's table identifies as needing a
  code change, not just a policy statement.
- `FileView`'s directory-watch support (ADR 0037) depends on `MSG-006` through `MSG-009` (shared
  `filesystem/changed/...` channel, kernel-only publish restriction, capability-reused subscribe) landing
  before it has live-update behavior; until then it degrades to manual refresh, per ADR 0037's
  consequences.
- `docs/session-and-process-lifecycle.md` and any other doc describing `IEventBus`/`IAppEventGateway`
  publish semantics must be updated in the same change that implements this ADR, per this repo's
  documentation-maintenance rule.

## References

- [`../Global-FileView-And-MessagingSystem.md`](../old/Global-FileView-And-MessagingSystem.md) and
  [`../Global-FileView-And-MessagingSystem/MessagingSystem.md`](../old/Global-FileView-And-MessagingSystem/MessagingSystem.md) —
  full specification.
- [`../Global-FileView-And-MessagingSystem/integrationPlan.md`](../old/Global-FileView-And-MessagingSystem/integrationPlan.md) —
  Phase 1 (`MSG-001`–`MSG-005`) and Phase 2 (`MSG-006`–`MSG-010`).
- `Shared/HackerOs.Simulation.Abstractions/Events/TopicMessagingContracts.cs` and
  `Shared/HackerOs.Simulation.Abstractions/FileSystem/FileSystemWatchContracts.cs` — accepted contract
  skeletons.
- ADR 0002: Authority Comes from Trusted Policy — the general principle this ADR extends to publish
  authorization.
- ADR 0003: Exact Capability Matching — the precedent for exact-match-only topic subscriptions.
- ADR 0037: `FileView` as the Canonical File-Listing Control — the primary consumer of the
  filesystem-watch API this messaging model enables.
