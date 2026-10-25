# HackerOs.Samples.ServiceApp

Sample session service application for HackerOS demonstrating non-visual background service execution,
event publishing, session cancellation handling, and bounded cleanup.

## Features

- Derives `ServiceAppBase` with `AutoStart: true` manifest configuration.
- Runs a deterministic ticker loop using `IAppClockGateway.DelayAsync`.
- Publishes `SampleTickerEvent` on its own topic (`SampleTickerTopics.Ticked`) via `IAppEventGateway`'s
  topic-bus members on every tick — the first real example of the app-owned topic lane described in
  [`doc/en/wasm/dev/adr/0038-emitter-authorized-topic-messaging.md`](../../../../../doc/en/wasm/dev/adr/0038-emitter-authorized-topic-messaging.md).
- Handles `CancellationToken` for graceful stop during logout, disable, or system shutdown.
- Performs bounded cleanup in `OnStoppingAsync` with no volatile state leak across restarts.
