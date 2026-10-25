> [English version](../../../../en/wasm/dev/adr/0040-declared-topic-permissions.md)

# ADR 0040: Declared Topic Permissions and Optional Per-Channel Access

## Status

Accepted on 2026-08-17.

## Context

[ADR 0038](0038-emitter-authorized-topic-messaging.md) accepted emitter-authorized topic messaging and
[`InMemoryTopicMessageBus`](../../../../../wasm2/HackerOs/Platform/HackerOs.Platform.Core/Events/InMemoryTopicMessageBus.cs)
(`MSG-001`–`MSG-005`) implements it: an app-owned topic namespace, plus shared channels whose
`SharedChannelPolicy` could require a fixed, curated `AppCapabilities` identifier to publish or
subscribe. Two needs surfaced immediately once real inter-app messaging was considered, using
`HackerOs.Apps.FileExplorer` accepting a "change directory" command from another app as the concrete
example (the "`SendMessage` in `user32.dll`" analogy — a general-purpose way for one app to address a
command at another running app instance, not a filesystem-watch-specific mechanism):

1. **An app needs to gate its own channel behind a permission it defines itself** —
   `topic:app/org.hackeros.file-explorer/change-directory` in the original phrasing — not one of the
   fixed, centrally curated `AppCapabilities` values, which exist for OS-resource access (filesystem,
   network, notifications, ...) and are deliberately not meant to grow per-app entries (ADR 0003).
2. **A command topic naturally targets one running instance**, not just an app's static namespace — a
   caller wants to reach *this* `FileExplorer` window, addressed by `ProcessId`, the way `SendMessage`
   addresses an `HWND`.
3. **The permission a channel requires must be entirely optional**, decided per channel by its owner —
   some channels need no permission at all (an open, ad hoc command surface), some need only ownership
   (nothing external may ever act on them), and some need an actual granted permission. The `MSG-001`
   implementation's `SharedChannelPolicy` only expressed two of these three states cleanly (a `null`
   capability meant "owner-only" for publish but "unrestricted" for subscribe — an inconsistent default
   with no way to express a fully open publish side).

Separately, whether granting a declared topic permission should be automatic (both manifests simply
agreeing is enough) or require the same explicit user/administrator approval every other capability
needs was raised and decided: **explicit approval is required**, matching the existing deny-by-default
model rather than introducing a quieter, auto-granted class of permission.

## Decision

### 1. Process-targeted command topics need no new mechanism

A topic parameterized by a target `ProcessId` is just another validated segment on the existing
`TopicNameBuilder` (`MSG-002`, already implemented):

```csharp
public static class FileExplorerTopics
{
    public static TopicName ChangeDirectory(ProcessId targetProcessId) =>
        TopicNames.ForApp(FileExplorerWindow.AppId)
            .Segment("change-directory")
            .Segment(targetProcessId.ToString())
            .Build();
    // => "app/org.hackeros.file-explorer/change-directory/42"
}
```

Each running instance subscribes to the topic parameterized by its *own* `ProcessId` at startup.
Mechanically this is still fan-out pub/sub, but because exactly one process subscribes to its own
segment, it behaves as a unicast address in practice — the same effect as `SendMessage(hwnd, ...)`
without a distinct delivery primitive. **Recommended app-authored convention** (not framework-generated):
a window app exposes `Topics` (pure `TopicName` builders) and `Messages` (one-line send helpers taking
the caller's own `IAppEventGateway`, never a raw `ITopicMessageBus` — apps never touch the bus directly,
only their own scoped gateway) as nested static classes alongside the app itself:

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

This ADR documents the convention; adopting it in the real `HackerOs.Apps.FileExplorer` is deferred to
Phase 4 of [`integrationPlan.md`](../old/Global-FileView-And-MessagingSystem/integrationPlan.md), once it
hosts `FileView`.

### 2. `SharedChannelPolicy` becomes an explicit, optional, per-direction tri-state

**Implemented** (superseding the `MSG-001` shape): `SharedChannelAccessMode { Open, OwnerOnly,
RequiresCapability }`, independently for publish and subscribe, with a capability string required
exactly when (and only when) the mode is `RequiresCapability`:

```csharp
public sealed record SharedChannelPolicy(
    SharedChannelAccessMode publishAccess,
    SharedChannelAccessMode subscribeAccess,
    string? publishCapability = null,
    string? subscribeCapability = null,
    CapabilityResourceCandidate? resourceScope = null);
```

`Open` and `OwnerOnly` require no permission at all — a channel owner who wants a "SendMessage"-style, no
permission-dance command surface simply registers `SharedChannelPolicy.Open()` (or `OwnerOnly()` to keep
it private to itself) and is done. This directly answers "the permission on a topic must be optional":
requiring a capability is one of three choices the owner makes, not the default.

### 3. Declared Topic Permissions: a parallel, app-declared permission space

Reuses every existing capability-grant mechanism (`ICapabilityGrantRepository`, `CapabilityGrant`,
`CapabilityGrantSource`, audit log, deny-by-default `Evaluate`) — **no new grant storage, no new
evaluation algorithm** — by accepting one additional, syntactically-recognized shape of capability
identifier, kept deliberately separate from the fixed `AppCapabilities` catalog so ADR 0003's exact-match
guarantee over that catalog is untouched:

- **Identifier shape**: `topic-publish:app/{appId}/{segment}[/{segment}...]` or
  `topic-subscribe:app/{appId}/{segment}[/{segment}...]`, produced only via
  `TopicName.ToPublishPermission()`/`.ToSubscribePermission()` extension methods — never hand-typed,
  matching the same discipline as `TopicName` itself.
- **`TopicPermissions`** (`Shared/HackerOs.App.Abstractions/TopicPermissions.cs`, **implemented**):
  `IsWellFormed(string)`/`IsOwnedByApp(string, string)` — pure syntax validation, no dependency on
  `Simulation.Abstractions` or the app catalog, so it can be referenced from the lower-layer
  `AppManifestValidator` and `CapabilityGrant` without a circular project reference.
- **`CapabilityGrant`'s constructor** (**implemented**) now accepts a capability that is either a known
  `AppCapabilities` identifier or a well-formed topic permission — both remain rejected otherwise
  (`P1-CAP-003`'s "reject unknown capabilities" guarantee holds for both spaces, just widened to
  recognize two, not one).
- **`AppManifest.DeclaredTopicPermissions`** (**implemented**, optional, defaults to `[]`): lets an app
  export `{ Id, Description }` pairs for discoverability (so a future grant/approval UI can show "Allows
  another app to change this window's current directory" instead of a raw string) — validated to be a
  well-formed identifier rooted under the declaring app's own namespace (`AppManifestValidator`, new
  error codes `manifest.topicPermission.malformed`/`manifest.topicPermission.notOwned`).
- **Requesting side**: unchanged — a topic permission is just another entry in the existing
  `AppManifest.Capabilities` list, accepted by `AppManifestValidator.ValidateCapabilities` when
  `TopicPermissions.IsWellFormed` is true even though it is not in `AppCapabilities.All`.
- **Declaration is documentation, not a hard gate**: this deliberately does **not** add catalog-level
  cross-manifest validation rejecting a manifest that requests a permission nobody declares. A grant for
  an undeclared/not-yet-installed permission is harmless — it simply can never be consulted until some
  app registers the matching channel — so requiring every requester to be validated against every
  installed app's declarations at catalog-build time would only make an optional integration (an app that
  *can* talk to `FileExplorer` if present) a hard install-time failure when `FileExplorer` isn't part of
  a given build profile. `DeclaredTopicPermissions` exists for a future approval UI's benefit, not as a
  build-time dependency check.
- **Grant flow**: identical to every other capability — `ICapabilityGrantRepository.Grant(appId, userId,
  capability, CapabilityGrantSource.UserApproval | AdministratorApproval, actingAuthority, constraints)`.
  Per the explicit decision above, there is no `CapabilityGrantSource.AppDeclaredConsent` auto-grant path;
  a topic permission is approved exactly like a filesystem or network capability.
- **JSON manifest schema**: `Shared/HackerOs.App.Abstractions/Schema/manifest.schema.v1.json` gained a
  `declaredTopicPermissions` array property (**implemented**), matching the regex grammar above; the
  canonical serialization fixture was updated to match.

### 4. `SharedChannelPolicy.PublishCapability`/`SubscribeCapability` need no bus-side change

`InMemoryTopicMessageBus.EvaluatePublishDenial`/`EnsureSubscribeAllowed` already call
`ICapabilityGrantRepository.Evaluate(...)` generically — they never re-validate "known-ness" (that check
exists only at `Grant()`/manifest-validation time). A `SharedChannelPolicy` built with
`publishCapability: root.ToPublishPermission()` works with zero changes to the bus implementation.

## Consequences

- `SharedChannelPolicy`'s shape from the initial `MSG-001` pass is superseded by the tri-state form above;
  every call site and test was updated in the same change as this ADR (no separate migration task).
- A future Settings/Permissions UI (not built by this ADR) is the natural consumer of
  `DeclaredTopicPermissions.Description` when presenting an approval prompt; until then, an administrator
  grants a topic permission the same way as any other capability, using the raw identifier.
- `HackerOs.Apps.FileExplorer` does not yet declare or use any topic permission or the `Topics`/`Messages`
  convention — this ADR specifies the pattern; adopting it is Phase 4 work in `integrationPlan.md`, once
  `FileExplorerWindow` hosts `FileView` and has a concrete command (e.g. `ChangeDirectory`) worth
  exposing to other apps.
- `TopicPermissions` living in `App.Abstractions` (lower layer) while `TopicName` stays in
  `Simulation.Abstractions.Events` (higher layer) is a deliberate layering choice — it avoids moving
  `TopicName` down a layer purely to satisfy one validator, at the cost of `TopicPermissions` validating
  identifier *shape* independently of `TopicNameBuilder`'s own segment grammar. Both use the same kebab-case
  segment rule; a future divergence between the two is a risk worth watching if either grammar changes,
  not a risk eliminated by this design.

## References

- ADR 0038: Emitter-Authorized Topic Messaging — the base model this ADR extends.
- ADR 0003: Exact Capability Matching — the fixed-catalog invariant this ADR deliberately does not alter.
- [`../Global-FileView-And-MessagingSystem/MessagingSystem.md`](../old/Global-FileView-And-MessagingSystem/MessagingSystem.md) —
  updated with the tri-state `SharedChannelPolicy` and Declared Topic Permissions sections.
- `Shared/HackerOs.App.Abstractions/TopicPermissions.cs`,
  `Shared/HackerOs.Simulation.Abstractions/Events/TopicMessagingContracts.cs` (`TopicPermissionNames`),
  `Platform/HackerOs.Platform.Core/Events/InMemoryTopicMessageBus.cs`.
