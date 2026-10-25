> [Version française](../../../../fr/wasm/dev/adr/0145-publish-async-from-a-worker.md)

# ADR 0145: `PublishAsync`, the real result of a publication from a worker

## Status

Accepted on 2026-10-18.

- **Benoit's answer:** option **B** of the card "How should an event Publish
  from an app running in a worker be handled?" (2026-10-18,
  15:12Z, thread "Exec 1: remote gateways").
- **Replaces:** the "Open question" paragraph of ADR 0138. ADR 0138
  §3 remains true for `Publish`.
- **Version:** addition to the SDK after the publication of 1.2.0 (ADR 0132 §2,
  work item `exec/10-final`); it stays in `PublicAPI.Unshipped.txt` and ships
  with the next minor version (1.3.0), according to SemVer
  ([`sdk-packages.md`](../../framework/sdk-packages.md)).

## Context

In a worker, the .NET runtime has only one thread and cannot wait for the OS
synchronously. `IAppEventGateway.Publish(topic, payload)` is synchronous:
ADR 0138 §3 makes it return a provisional `Delivered` and write an OS refusal
to the app's log. Three choices were proposed: keep this behavior alone (A),
add an asynchronous form that returns the real result (B), or refuse in a
worker the publications outside the app's topics (C). Benoit chose B.

## Decision

1. `IAppEventGateway` receives
   `Task<TopicPublishResult> PublishAsync<TPayload>(TopicName topic, TPayload payload, CancellationToken cancellationToken = default)`.
   Same rules as `Publish`: identity of the process, the app's topic or the allowed
   shared channel.
2. The member has a default implementation that calls `Publish`, like
   `IAppProcessGateway.KillAsync` (ADR 0072 §4): an existing implementation
   still compiles, and a 1.0, 1.1 or 1.2 app is not affected. On the main
   thread, `PublishAsync` therefore gives exactly the result of `Publish`.
3. In a worker, `PublishAsync` is a call (`call`) of the operation
   `ev.pubr`. The OS publishes on behalf of the pid (same path as `ev.pub`) and replies:
   - `o`: the value of `TopicPublishOutcome`;
   - `f`: the subscribers' faults, each with the protocol's error encoding
     (`RemoteErrors`). The worker recreates them as
     `EventDispatchFault` with the payload type.
4. A refusal returned by `PublishAsync` is **not** written to the app's log:
   the app is the one reading the result. `Publish` keeps the behavior
   of ADR 0138 §3 (provisional, refusal in the log).

## Consequences

- An app that needs to know the real result calls `PublishAsync`, everywhere.
  The SDK guide recommends this form for code that may run in a
  worker.
- New line in `Shared/HackerOs.Simulation.Abstractions/PublicAPI.Unshipped.txt`;
  `HackerOsVersion` stays `1.2.0` until the next publication.
- Test: `RemoteEventsTests.PublishAsync_returns_the_outcome_and_the_subscriber_faults_decided_by_the_OS`
  checks the same result on the main thread and in a worker (delivered, refused,
  fault of a main-thread subscriber), with no refusal line in the log.

## References

- ADR 0072, 0124, 0132, 0138.
- [`worker-processes.md`](../worker-processes.md), [`app-sdk.md`](../../framework/app-sdk.md),
  [`sdk-packages.md`](../../framework/sdk-packages.md).
