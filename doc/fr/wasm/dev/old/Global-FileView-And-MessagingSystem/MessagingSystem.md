# Topic Messaging & Filesystem-Watch System Specification

Parent document: [`../Global-FileView-And-MessagingSystem.md`](../Global-FileView-And-MessagingSystem.md)

## Purpose

Extend the existing in-process event bus (`Shared/HackerOs.Simulation.Abstractions/Events/EventBusContracts.cs`
`IEventBus`, implemented by `Platform/HackerOs.Platform.Core/Events/InMemoryEventBus.cs`, exposed to apps
as `IAppEventGateway`) so that:

1. Publishing is **authorized by emitter**, not open to any subscriber of the gateway. Today
   `AppEventGateway.Publish<TEvent>` is a blind pass-through to `IEventBus.Publish<TEvent>` — any app can
   publish any event type, including platform-trusted lifecycle events. This was confirmed as a real gap
   to close, not a hypothetical.
2. Cross-module notification channels are addressed by **named topics**, built exclusively through typed
   const/helper APIs — never a hand-typed string at a call site — so topic names can't typo-drift between
   publisher and subscriber and are discoverable via IntelliSense/compile-time reference.
3. A module can declare a **shared channel** other modules subscribe to, optionally restricted by the
   existing capability-grant security model (`Shared/HackerOs.App.Abstractions/Policy/`) — no new
   authorization mechanism is introduced.
4. Directory-change notifications are exposed as a **disposable object wrapping a
   `System.Threading.Channels.Channel<T>`**, so a consumer can `await foreach` and simply dispose to
   unsubscribe, per the original request.

## Relationship to the existing `IEventBus`

Per the confirmed decision to extend rather than parallel the existing bus, this design keeps exactly one
in-process pub/sub implementation, split into two authorization lanes over the same underlying delivery
mechanism:

- **Kernel/platform lane** (unchanged surface): `IEventBus.Publish<TEvent>` remains available, but only to
  `Platform.Core` call sites — the lifecycle/session/process/window events already defined
  (`SessionLoggedOutEvent`, `ProcessStateChangedEvent`, etc., per `../session-and-process-lifecycle.md`).
  Every app may still **subscribe** to these (they are broadcast notifications, not secrets — read access
  was never the problem), but `IAppEventGateway` no longer exposes a way to **publish** an arbitrary
  `TEvent`. `InMemoryEventBus` itself does not change; only what `AppEventGateway` is allowed to forward
  changes.
- **App topic lane** (new): apps publish and subscribe through named `TopicName`s instead of bare CLR
  types. A topic belongs to the namespace of the app that owns it (see [Topic
  naming](#topic-naming)), and publishing outside your own namespace requires that the target topic was
  explicitly registered as a **shared channel** by its owner, with the caller holding whatever capability
  that channel's policy requires (see [Shared channels](#shared-channels-and-optional-per-channel-access)).

### Breaking change notice

Removing `IAppEventGateway.Publish<TEvent>` for arbitrary platform event types is a breaking change to a
Phase 1 baseline contract (`P1-SYS-008`, `P1-EXEC-004` in `../integration-task-list.md`). Per this repo's
`AGENTS.md`, that requires its own ADR before implementation; this document specifies the target shape,
and [ADR 0038](../../adr/0038-emitter-authorized-topic-messaging.md) accepts it, including the concrete
per-call-site migration table.

## Topic naming

A hardcoded topic string is exactly the failure mode this design exists to prevent, so topic names are
only ever produced by a builder, never written by hand at a call site:

```csharp
public readonly record struct TopicName
{
    public string Value { get; }
    public override string ToString() => Value;
}

public static class TopicNames
{
    /// <summary>Starts building a topic owned by the given app's own reverse-domain namespace.</summary>
    public static TopicNameBuilder ForApp(string appId);

    /// <summary>Starts building a topic under a platform-owned shared root (e.g. "filesystem", "clipboard").
    /// Only Platform.Core call sites may successfully register (not merely reference) a shared root.</summary>
    public static TopicNameBuilder Shared(string sharedRootName);
}

public sealed class TopicNameBuilder
{
    /// <summary>Appends one validated lowercase-kebab-case segment (no '/', no whitespace, no wildcards).</summary>
    public TopicNameBuilder Segment(string segment);

    public TopicName Build();
}
```

Examples:

```csharp
// An app's own topic, e.g. FileExplorer announcing it finished a bulk operation.
TopicName t = TopicNames.ForApp("org.hackeros.file-explorer").Segment("bulk-operation-completed").Build();
// => "app/org.hackeros.file-explorer/bulk-operation-completed"

// A platform-owned shared channel, addressed by every subscriber via the same helper — never a string.
TopicName changed = FileSystemTopics.ForDirectory(path); // see below — wraps TopicNames.Shared("filesystem")
```

Segments are validated (non-empty, lowercase, `[a-z0-9-]+`) at build time so a malformed topic fails
immediately at the call site that constructs it, not at some later mismatched-subscription runtime
surprise. Topic matching for subscribe/publish is **exact only** — no wildcard/glob subscriptions in this
version, deliberately mirroring ADR 0003's exact-capability-matching precedent so the two authorization
surfaces (capabilities and topics) stay conceptually symmetric.

## Core contracts

**Implemented** (`MSG-001`–`MSG-005`) in
`Shared/HackerOs.Simulation.Abstractions/Events/TopicMessagingContracts.cs` and
`Platform/HackerOs.Platform.Core/Events/InMemoryTopicMessageBus.cs`. The shapes below are current; the
`Subscribe`/`SubscribeChannel` signatures additionally carry the caller's `PublisherIdentity` (used as
subscriber identity for shared-channel subscribe policy — see [Shared channels](#shared-channels-and-optional-per-channel-access)),
which the app-facing `IAppEventGateway` stamps automatically so app code never supplies it directly:

```csharp
public sealed record TopicMessage<TPayload>(
    TopicName Topic,
    TPayload Payload,
    string PublisherAppId,
    DateTimeOffset PublishedAtUtc);

public enum TopicPublishOutcome { Delivered, TopicNotOwnedByCaller, SharedChannelAccessDenied }

public sealed record TopicPublishResult(
    TopicPublishOutcome Outcome,
    IReadOnlyList<EventDispatchFault> SubscriberFaults);

/// <summary>Trusted publisher identity, supplied only by the execution-context factory — never
/// self-declared by app code, mirroring <see cref="AppOperationContext"/>'s trust model.</summary>
public readonly record struct PublisherIdentity(string AppId, string UserId, string ProcessId);

public interface ITopicMessageBus
{
    IDisposable Subscribe<TPayload>(TopicName topic, PublisherIdentity subscriber, Action<TopicMessage<TPayload>> handler)
        where TPayload : notnull;

    /// <summary>Returns a disposable Channel-backed subscription; disposing unsubscribes and completes
    /// the channel. This is the primitive the filesystem-watch API below is built on.</summary>
    ITopicChannelSubscription<TPayload> SubscribeChannel<TPayload>(
        TopicName topic, PublisherIdentity subscriber, int? boundedCapacity = null)
        where TPayload : notnull;

    TopicPublishResult Publish<TPayload>(TopicName topic, TPayload payload, PublisherIdentity publisher)
        where TPayload : notnull;

    /// <summary>Idempotent one-time declaration of a shared channel's ownership and access policy.
    /// Throws if a different owner already registered the same root.</summary>
    void RegisterSharedChannel(TopicName root, SharedChannelPolicy policy, PublisherIdentity owner);
}

public interface ITopicChannelSubscription<TPayload> : IAsyncDisposable where TPayload : notnull
{
    ChannelReader<TopicMessage<TPayload>> Reader { get; }
}
```

Publish authorization rule (enforced by the implementation, `InMemoryTopicMessageBus`, in
`Platform.Core`): a `Publish` call succeeds only when either (a) `topic` falls under
`TopicNames.ForApp(publisher.AppId)`'s own namespace, or (b) `topic` was registered as a shared channel
and `ICapabilityChecker.Evaluate(policy.PublishCapability, ...)` for the publisher's bound execution
context grants it. Both checks reuse machinery that already exists (`ICapabilityChecker`,
`CapabilityPolicyEvaluation`) — no new grant storage, no new evaluation algorithm.

`IAppEventGateway` (existing, `Shared/HackerOs.Simulation.Abstractions/Gateways/AppGatewayContracts.cs`)
gains the topic-bus members alongside its existing (now subscribe-only, for kernel event types)
`Subscribe<TEvent>`:

```csharp
public interface IAppEventGateway
{
    IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : notnull; // kernel lane, read-only for apps
    IReadOnlyList<EventDispatchFault> Publish<TEvent>(TEvent @event) where TEvent : notnull; // Platform.Core only — see note

    IDisposable Subscribe<TPayload>(TopicName topic, Action<TopicMessage<TPayload>> handler) where TPayload : notnull;
    ITopicChannelSubscription<TPayload> SubscribeChannel<TPayload>(TopicName topic, int? boundedCapacity = null) where TPayload : notnull;
    TopicPublishResult Publish<TPayload>(TopicName topic, TPayload payload) where TPayload : notnull; // publisher identity stamped by the gateway itself, not the caller
    void RegisterSharedChannel(TopicName root, SharedChannelPolicy policy);
}
```

> **Note on `Publish<TEvent>`**: the app-facing gateway keeps the method signature for source
> compatibility, but its implementation is expected to reject (return an empty/denied result, not throw —
> matching the existing fault-isolation convention) any `TEvent` that isn't on an explicit
> app-publishable allow-list. The exact mechanism (attribute on the event type vs. an explicit registry)
> is an implementation decision for the follow-up ADR, not fixed by this spec.

## Shared channels and optional per-channel access

**Implemented**, per [ADR 0040](../../adr/0040-declared-topic-permissions.md) — a permission requirement is
entirely optional and chosen per direction (publish/subscribe) by the channel's owner at registration
time, not a fixed feature every shared channel must use:

```csharp
public enum SharedChannelAccessMode { Open, OwnerOnly, RequiresCapability }

public sealed record SharedChannelPolicy(
    SharedChannelAccessMode PublishAccess,
    SharedChannelAccessMode SubscribeAccess,
    string? PublishCapability = null,   // required exactly when PublishAccess == RequiresCapability
    string? SubscribeCapability = null, // required exactly when SubscribeAccess == RequiresCapability
    CapabilityResourceCandidate? ResourceScope = null);
```

- **`Open`**: no restriction at all — any app may act, no permission, no ownership check. The channel
  owner deliberately declares no permission is needed, matching an ad hoc "`SendMessage`-style" command
  surface. `SharedChannelPolicy.Open()` is the convenience factory for both directions.
- **`OwnerOnly`**: only the channel's registered owner may act — no capability required, but non-owners
  are always denied. `SharedChannelPolicy.OwnerOnly()` is the convenience factory for both directions.
  This is also how the kernel-only filesystem-watch channel (below) is expressed: the platform registers
  it under its own reserved app ID, so `OwnerOnly` alone makes it un-publishable by any app, with no
  special-cased "not app-publishable at all" branch needed in the bus.
- **`RequiresCapability`**: the acting app must hold the paired capability (`PublishCapability`/
  `SubscribeCapability`), evaluated through the exact same `ICapabilityGrantRepository.Evaluate` every
  other gateway already uses — the channel owner always bypasses this check on its own channel. The
  capability is either a fixed `AppCapabilities` identifier or an app-declared **topic permission** (next
  section) — the bus does not care which; `Evaluate` never re-validates "known-ness."
- `ResourceScope` lets a channel's policy be structurally scoped the same way filesystem capabilities
  already are (`VirtualPathCapabilityConstraint`, etc.) — e.g. "you may subscribe to
  `filesystem/changed/*` only for paths under your own granted `VirtualPathCapabilityConstraint`," which
  is exactly how the filesystem-watch channel below reuses a caller's *existing* filesystem read grant
  instead of inventing a new "can watch" capability.
- Registration is one-time and owner-checked: a second `RegisterSharedChannel` call for the same root by
  a *different* `PublisherIdentity.AppId` throws — channel ownership is fixed at first registration,
  mirroring how a settings document's protected scope can't be silently reassigned
  (`../policy-system.md`). A second call by the *same* owner replaces the stored policy.

## Declared topic permissions

**Implemented** (`Shared/HackerOs.App.Abstractions/TopicPermissions.cs`), per
[ADR 0040](../../adr/0040-declared-topic-permissions.md) — lets an app gate its own shared channel behind a
permission it defines itself, e.g. `FileExplorer` requiring a permission before another app may command
it to change directory, without adding a per-app entry to the fixed, centrally curated `AppCapabilities`
OS-resource catalog (ADR 0003).

```csharp
// Producing side — never a hand-typed string:
TopicName root = TopicNames.ForApp(FileExplorerWindow.AppId).Segment("change-directory").Build();
string permission = root.ToPublishPermission(); // "topic-publish:app/org.hackeros.file-explorer/change-directory"
```

- **Shape**: `topic-publish:app/{appId}/{segment}[/{segment}...]` or the `topic-subscribe:` equivalent.
  `TopicPermissions.IsWellFormed`/`IsOwnedByApp` validate this syntax independently of `TopicNameBuilder`
  (a deliberate layering choice: `TopicPermissions` lives in `App.Abstractions`, below
  `Simulation.Abstractions` where `TopicName` lives, so the manifest validator and `CapabilityGrant` can
  reference it without a circular project reference).
- **Reuses every existing grant mechanism** — `CapabilityGrant`, `ICapabilityGrantRepository`, audit log,
  deny-by-default `Evaluate` — by accepting this second, syntactically-recognized identifier shape
  alongside the fixed `AppCapabilities` catalog. No new grant storage, no new evaluation algorithm.
- **`AppManifest.DeclaredTopicPermissions`** (optional, defaults to empty): `{ Id, Description }` pairs a
  manifest exports for discoverability by a future approval UI; validated to be well-formed and rooted
  under the declaring app's own namespace. **Declaring is documentation, not a hard gate** — a requesting
  manifest lists the permission string in its ordinary `Capabilities` list and is accepted if the string
  is syntactically well-formed, whether or not any installed app currently declares it (an undeclared/
  not-yet-installed permission simply can never be granted usefully, not a catalog-build error — this is
  what makes the permission optional for an integration that may or may not be present in a given build).
- **Grant flow**: identical to every other capability. Per an explicit decision, there is **no
  auto-grant** path — a topic permission requires the same `CapabilityGrantSource.UserApproval`/
  `AdministratorApproval` explicit approval as filesystem/network/notification capabilities.

## Process-targeted topics and inter-app messaging

A command topic addressed at one running instance needs no new mechanism — a `ProcessId` is just another
validated `TopicNameBuilder` segment (already implemented):

```csharp
public static class FileExplorerTopics
{
    public static TopicName ChangeDirectory(ProcessId targetProcessId) =>
        TopicNames.ForApp(FileExplorerWindow.AppId)
            .Segment("change-directory")
            .Segment(targetProcessId.ToString())
            .Build();
}
```

Each running instance subscribes to the topic parameterized by its *own* `ProcessId` at startup — still
mechanically fan-out pub/sub, but since exactly one process subscribes to its own segment, it behaves as
a unicast address in practice, the same effect as `SendMessage(hwnd, ...)` without a distinct delivery
primitive. **Recommended app-authored convention** (not framework-generated): a window app exposes
`Topics` (pure `TopicName` builders) and `Messages` (one-line send helpers taking the caller's own
`IAppEventGateway`, never a raw `ITopicMessageBus`) as nested static classes:

```csharp
public static class FileExplorerWindow
{
    public static class Topics
    {
        public static TopicName ChangeDirectory(ProcessId targetProcessId) => /* ... */;
    }

    public static class Messages
    {
        public static TopicPublishResult SendChangeDirectory(
            IAppEventGateway events, ProcessId targetProcessId, string path) =>
            events.Publish(Topics.ChangeDirectory(targetProcessId), new ChangeDirectoryMessage(path));
    }
}

// Caller: FileExplorerWindow.Messages.SendChangeDirectory(context.Events, explorerPid, "/etc/someDir");
```

Adopting this in the real `HackerOs.Apps.FileExplorer` is deferred to Phase 4 of `integrationPlan.md`,
once it hosts `FileView` and has a concrete command worth exposing to other apps.

## Filesystem watch API

**Implemented** (`MSG-011`–`MSG-015`), built entirely on the primitives above — no separate mechanism:

```csharp
public enum FileSystemChangeKind { Created, ContentModified, MetadataModified, Deleted, MovedFrom, MovedTo }
public enum FileSystemWatchScope { ThisEntry, ImmediateChildren, Recursive }

public sealed record FileSystemChangeEvent(
    VirtualPath Path,
    FileSystemChangeKind Kind,
    FileSystemEntryKind EntryKind,
    long Revision,
    DateTimeOffset OccurredAtUtc,
    VirtualPath? MovedToPath = null); // set only for MovedFrom

public static class FileSystemTopics
{
    /// <summary>Topic a directory's changes are published on; built only through this helper, never a
    /// hand-built string, so the filesystem provider and every watcher agree by construction. Each of
    /// path's segments is lowercase-hex-encoded into its own topic segment (never a single hashed blob),
    /// so the topic mirrors the path's hierarchy and TopicNameBuilder's kebab-case grammar is satisfied
    /// for arbitrary Unicode file/directory names.</summary>
    public static TopicName ForDirectory(VirtualPath path) =>
        TopicNames.Shared("filesystem").Segment("changed")
            .Segment(HexEncode(pathSegment1)).Segment(HexEncode(pathSegment2)) /* ... */ .Build();
}

public interface IAppFileSystemWatchGateway
{
    /// <summary>Starts watching; disposing the returned subscription stops delivery and completes the channel.</summary>
    ValueTask<ITopicChannelSubscription<FileSystemChangeEvent>> WatchAsync(
        VirtualPath path, FileSystemWatchScope scope, CancellationToken cancellationToken = default);
}
```

`Watch` from the original skeleton was renamed `WatchAsync` and made asynchronous during implementation:
every filesystem authorization check in this codebase (`IFileSystemService.StatAsync` and everything it
calls) is async, so a synchronous `Watch` could not have performed the real read-authorization check this
API exists to reuse.

- The filesystem provider (`FileSystemService` in `Platform/HackerOs.Platform.Core/FileSystem/`) is the
  **sole** registered owner/publisher of the `shared/filesystem/changed` channel root — registered once,
  in the singleton's constructor, under `KernelPublisherIdentity` (a reserved `"kernel"` app ID no
  installed manifest can ever validate to — `AppManifestValidator.ValidateAppId` requires at least three
  dot-separated reverse-domain segments). `PublishAccess = SharedChannelAccessMode.OwnerOnly`: no app,
  regardless of any capability it holds, may ever publish to this channel — only the registered owner may,
  which is exactly `OwnerOnly`'s meaning, no special-cased "not app-publishable at all" branch required in
  the bus itself.
- `SubscribeAccess` for this channel is `SharedChannelAccessMode.Open` at the **bus** level — the real
  gate is one layer up, in `AppFileSystemWatchGateway.WatchAsync`, which calls
  `IFileSystemService.StatAsync` on the watched path using the caller's own authorization context before
  ever subscribing. This is deliberate, not a relaxation: the bus's shared-channel capability check is a
  single fixed capability evaluated for the whole channel, but which filesystem-read capability applies
  (`FileSystemUserHomeRead` vs. `FileSystemPrivateRead` vs. `FileSystemSystemRead`, each with its own
  structural path constraint) depends on the *specific watched path* — something only the filesystem
  authorizer itself can resolve correctly. Watching a directory can therefore never reveal more than
  reading it already would, without inventing a new capability.
- `IAppFileSystemWatchGateway.Watch` is exposed on `IAppExecutionContext` (as `Watch`) via the same
  default-interface-member-with-unsupported-fallback pattern `Intents` already established, so existing
  hand-rolled `IAppExecutionContext` test doubles keep compiling without changes; the trusted
  `AppExecutionContextFactory` wires the real `AppFileSystemWatchGateway`. `FileView`'s `Watch` parameter
  (see `FileViewControl.md`) is simply this gateway passed straight through by the host app — `FileView`
  itself never touches `ITopicMessageBus` directly.
- **Scope, as actually implemented**: only `FileSystemWatchScope.ImmediateChildren` is supported.
  `Recursive` was deferred from the original design, as planned. `ThisEntry` was **additionally** narrowed
  out during implementation — `FileView`'s only real consumer never needs it, and a correct implementation
  would require a server-side filtering channel wrapper (subscribe to the parent's topic, forward only
  matching-path events) with no current caller to justify it. Both throw `NotSupportedException` with a
  clear message; this is an intentional, honest scope reduction, not an oversight, matching this
  codebase's established pattern for such deferrals (see `P1-EXEC-004`'s network/intent gateway notes).

## Non-goals (this version)

- Cross-tab/cross-process delivery — this remains one in-memory bus inside a single WASM process, matching
  ADR 0009's scope; nothing here implies IndexedDB-backed or BroadcastChannel-backed delivery.
- Message persistence or replay-on-subscribe — a subscriber only sees messages published after it
  subscribes, identical to today's `IEventBus`.
- Wildcard/prefix topic subscriptions — exact match only, see [Topic naming](#topic-naming).
- A generic "any app can declare any shared channel" free-for-all — shared-channel registration is
  intentionally still gated by which project's code runs the registration call; this spec does not add a
  runtime "request a channel" API surface for arbitrary apps in this version.
