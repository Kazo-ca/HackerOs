> [Version française](../../../../fr/wasm/dev/adr/0138-remote-gateways-implementation-adjustments.md)

# ADR 0138: implementation adjustments for ADR 0124 (remote gateways)

## Status

Accepted on 2026-10-18.

- **Work item:** `exec/1-remote-gateways` (plan
  [`execution-followups-plan.md`](../plans/execution-followups-plan.md), decision 1).
- **Complements:** ADR 0124, without changing its decision. It records what
  the code had to specify or do differently, and why.

## Context

ADR 0124 has `HackEvents` and `Watch` go from a worker to the OS through
messages, opens the pool to any assembly known at build time, and moves the sample
service and `hackwebd` off the main thread. Three constraints weighed on the
implementation:

1. A worker's runtime has only one thread: it cannot wait synchronously
   for the OS's response (it would never receive the message that carries it).
2. The `IAppEventGateway` contract of SDK 1.0 is synchronous: `Publish` and
   `RegisterSharedChannel` return their result immediately.
3. The OS buses dispatch strictly by CLR type; an app's type does not
   necessarily exist in the OS runtime under the same identity.

## Decision

### 1. "Wire" bus on the OS side

`InMemoryTopicMessageBus` and `InMemoryEventBus` implement two internal
contracts of `HackerOs.Platform.Core` (`IWireTopicMessageBus`,
`IWireEventBus`). They index a worker's subscriptions by **type name**
(`"{FullName}, {AssemblyName}"`, `WirePayloadNames.Of`):

- a main-thread publication delivers the object to a wire subscription, which
  serializes it for its worker;
- a worker's publication (`PublishWire`) passes the JSON as is to the
  other workers and materializes it as an object **only once per type** for
  main-thread subscribers;
- the same authorization rules as a typed call apply (topic of another
  app, locked shared channel).

`AppEventGateway` exposes these members with the process identity; the
protocol only accesses them through the trusted context of `RemoteProcessSession`.

### 2. Operations added

| Operation | Direction | Shape |
|---|---|---|
| `ev.sub` | worker → OS, `call` | topic or type, capacity; the subscription exists before returning |
| `ev.unsub` | worker → OS, `note` | subscription identifier |
| `ev.pub` | worker → OS, `note` | topic, type name, JSON |
| `ev.chan` | worker → OS, `note` | topic, policy |
| `ev.ack` | worker → OS, `note` | identifier, number of messages delivered |
| `watch.open` | worker → OS, `call` | path, scope |
| `ev` | OS → worker | identifier, JSON, topic, publishing app, date |

The credit of 256 messages in flight per subscription (ADR 0124 §1) applies to
**all** subscriptions, bounded channels included: the channel keeps its capacity
on the OS side (loss of the oldest when it overflows), and the worker returns credit
through `ev.ack` after each delivery.

### 3. Publishing from a worker (deviation)

A worker's `Publish` **posts** the publication (`ev.pub`, a `note`) and
immediately returns `TopicPublishResult.Delivered` without faults. The real result
is only known to the OS: a refusal (topic of another app, locked channel)
is written to the app's log (`/var/log/apps/<app>.log`). ADR 0124
planned to wait for the result, which is impossible without changing the
synchronous contract (constraints 1 and 2). The question is asked to Benoit (see
"Open question").

A publication of a **kernel** event type (`Publish<TEvent>`) from a
worker returns an empty fault list without sending anything: these events are
published by the OS, not by apps.

### 4. Refusal of a subscription (deviation)

`Subscribe` is synchronous too: the worker returns the subscription immediately
and opens it in the background.

- A refused **channel** subscription ends the channel with the exception
  (`AppGatewayAccessDeniedException`): the app's `await foreach` receives it.
- A refused **callback** subscription is written to the app's log.

### 5. `RegisterSharedChannel`

It goes through a `note` (`ev.chan`); only the `VirtualPath` resource scope
is accepted by the protocol (the other scopes have no
codec); a refusal is written to the app's log.

### 6. Built-in codecs

Fixed list, hand-encoded (`WirePayloadCodec`, without reflection):
`FileSystemChangeEvent`, `AppDisabledEvent`, `ProcessStateChangedEvent`,
`SessionActivatedEvent`, `SessionLoggedOutEvent`, `SessionShutDownEvent`,
`DiagnosticLogClearedEvent`. Another public OS type that must cross
gets its codec in this file. App types go through
`[assembly: HackWireContext(typeof(...))]` (ADR 0124 §2); the context is created
by its parameterless constructor, hence the attribute's
`DynamicallyAccessedMembers` annotation (trimming-compatible).

### 7. Delivery order

In a worker, deliveries are always **asynchronous**, even to a
subscription of the same process: the publication makes the round trip through the OS.
On the main thread, delivery remains synchronous as before.

### 8. Pool and known assemblies

- `WorkerPoolOptions.KnownAssemblies` (new, empty by default) carries the build
  list; the Ecosystem fills it with the catalog's `LazyAssemblyNames`. It
  is sent to the worker in `init` (`known`).
- An assembly outside the list and absent from the worker's runtime is rejected:
  the worker writes the error to the process's error output and ends it
  with code **127** (like a shell for a command not found). An empty list
  (host that does not fill it) imposes no restriction.
- The Ecosystem now references `HackerOs.Samples.TerminalApp`: its command
  `sample-cmd` serves as a third-party command loaded on demand in the hosts
  test (`HostSmoke`). Like any command, it is hidden from the Start menu
  (`LaunchVisibility = Hidden`) and remains uninstallable. `test/test` is unchanged.

### 9. Review of `MainThread` apps

The 16 `mainThread` commands of `HackerOs.Commands` were reviewed; their
reason still holds (shell built-ins, alternate screen, `sudo`,
`passwd`, `hackbackup`). None depended on `HackEvents` or `Watch`.

## Open question

Synchronous publication from a worker (§3), asked to Benoit with these choices:

- **A (default, in place)**: provisional result `Delivered`, refusal in the log.
- **B**: add `PublishAsync` to SDK 1.2, which returns the real result;
  `Publish` keeps behavior A.
- **C**: `Publish` to a topic that does not belong to the app throws
  `NotSupportedException` in a worker.

His answer will be recorded here or in an ADR that replaces this paragraph.

## Consequences

- An app that needs to know whether its publication was refused reads its log, or
  publishes from the main thread, as long as the question is not settled.
- The sample service and `hackwebd` run in a dedicated worker.
- Tests: `RemoteEventsTests` (main thread / worker parity for topics,
  channels, kernel events, `Watch` and refusals; undeclared payload;
  unsubscription at `kill`; sample service; `sample-cmd` loaded on
  demand; refusal of an unknown assembly), `WireTopicMessageBusTests`.

## References

- ADR 0070, 0072, 0080, 0113, 0124, 0132.
- [`worker-processes.md`](../worker-processes.md).
