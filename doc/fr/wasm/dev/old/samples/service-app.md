# First Session Service App (`org.hackeros.samples.service-app`)

## Purpose

The First Session Service App demonstrates non-visual background service execution within HackerOS sessions.
It illustrates how a background task derives `ServiceAppBase`, runs during an active user session, responds
to session cancellation signals, publishes typed events on its own topic via `IAppEventGateway`'s topic-bus
members, and cleans up without leaving volatile state across session restarts. It is also the first real
example of the app-owned topic messaging lane described in
[`docs/adr/0038-emitter-authorized-topic-messaging.md`](../../adr/0038-emitter-authorized-topic-messaging.md).

## Architecture

| File | Role |
|---|---|
| `SampleTickerService.cs` | Background service loop deriving `ServiceAppBase`, auto-started at session login |
| `SampleTickerEvent.cs` | Typed event payload published on every tick |
| `SampleTickerTopics.cs` | Names the topic (`app/org.hackeros.samples.service-app/ticked`) `SampleTickerEvent` is published on, built only through `TopicNames` |
| `app.manifest.json` | Manifest for `org.hackeros.samples.service-app` (`kind`: `"service"`, `autoStart`: `true`) |
| `README.md` | Service documentation inside the project directory |
| `Tests/HackerOs.Samples.ServiceApp.Tests/SampleTickerServiceTests.cs` | Unit tests for startup, event publishing, cancellation, cleanup, and fresh restart |

## Service Lifecycle

```
[Session Login]
      │
      ▼
SampleTickerService.RunAsync
      │
      ├──> Reset volatile state (_tickCount = 0)
      │
      ├──> Ticker Loop (Clock.DelayAsync)
      │      ├── Publish SampleTickerEvent(TickCount, Timestamp, Status) on SampleTickerTopics.Ticked
      │      └── Log Diagnostic Information
      │
      ▼
[Session Cancellation Signal (Logout / Disable / Shutdown)]
      │
      ├──> OperationCanceledException caught
      ├──> Mark IsStopping = true
      │
      ▼
SampleTickerService.OnStoppingAsync
      │
      └──> Bounded cleanup (_tickCount = 0)
```

## Key Decisions

1. **No Window Component**: Service apps run purely in the background without UI components or razor views.
2. **AutoStart Configuration**: Manifest sets `autoStart: true` and `presentation.launchVisibility: "hidden"`.
3. **Session Bound**: Volatile state exists strictly for the lifetime of the session. `OnStoppingAsync` resets in-memory counts so no state leaks across restarts.
4. **Deterministic Simulation Clock**: Uses `IAppClockGateway.DelayAsync` rather than direct `Thread.Sleep`.

## Task List

- [x] `P2-SVC-001` Implement a small deterministic status/ticker service deriving `ServiceAppBase` with on-login or manual activation.
- [x] `P2-SVC-002` Observe session cancellation, perform bounded cleanup, publish health/status events, and retain no volatile work across restart.
- [x] `P2-SVC-003` Test start, duplicate prevention, cancellation, timeout, fault, disable, logout, shutdown, and fresh restart state.
