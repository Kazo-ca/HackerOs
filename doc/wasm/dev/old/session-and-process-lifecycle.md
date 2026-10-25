# Session and Process Lifecycle

## Purpose

Define deterministic HackerOS session, process, cancellation, clock, and virtual
resource behavior independently from Blazor and browser timers.

## Status

The process/clock/resource model was accepted on 2026-08-01 in ADR 0012
(`docs/adr/0012-process-and-clock-model.md`). The local identity/session model was
accepted on 2026-08-01 in ADR 0013 (`docs/adr/0013-local-user-session.md`).
Contracts, implementation, and tests for Section 7 of
`docs/integration-task-list.md` (`P1-SYS-001` through `P1-SYS-011`) are
complete as of 2026-08-01 — see "Implemented architecture" and "Task list"
below.

## Proposed local session

- Clean Release profiles require first-run creation of one Administrator; no
	default credentials ship.
- Local passwords are optional, versioned KDF verifiers and never server
	credentials.
- One active session per tab owns the root cancellation source.
- Process tokens are linked but independently cancellable.
- Logout/shutdown stops launches, cancels the root, performs bounded cleanup,
	and deactivates apps in reverse dependency order.
- App identity never elevates user authority. User elevation requires explicit
	Administrator reauthentication and is operation-scoped/audited.
- First login provisions `/home/{loginName}` idempotently through the filesystem
	seeder.

## Proposed architecture

- Positive monotonic PIDs identify processes for one boot.
- Parent, app, instance, user, session, and resource-profile identity is immutable.
- Platform-owned state transitions and cancellation sources control lifecycle.
- `ISimulationClock` and a deterministic scheduler replace domain timers/sleeps.
- `ISimulationRandom` provides stable domain-keyed streams from a recorded seed.
- Virtual resource usage derives from app profile, workload, hardware, and ticks.
- Active execution is volatile and is not resumed after shutdown/browser exit.

## Resource meaning

CPU, memory, storage, and network values are game/simulation measurements. They
do not claim to expose browser memory, host CPU utilization, native PIDs, or
operating-system telemetry. Storage and network activity come from HackerOS
services, while virtual hardware controls capacity and coefficients.

## Validation

Headless tests will advance a manual clock, use fixed random seeds, and assert
exact transition/resource sequences without sleeping. Lifecycle tests cover
parent/child cancellation, singleton lookup, bounded stop, kill, logout,
shutdown, faults, and bounded history.

## Exclusions

- Browser login/session UI.
- Native OS processes or metrics.
- Work continuing after the PWA is terminated.
- Automatic resume of volatile service work.
- Gameplay hardware balancing before its dedicated analysis.

## Implemented architecture

**Contracts** — `Shared/HackerOs.Simulation.Abstractions/`:

- `Sessions/IdentityContracts.cs` — `LocalUserId`, `LocalGroupId`, `SessionId`,
  `InstallationId`, `DeviceId`, `LocalLoginName`, `LocalPasswordCredential`,
  `LocalUser`, `LocalGroup`, `AuthenticatedPrincipal`.
- `Sessions/UserRepositoryContracts.cs` — `ILocalUserRepository` (create,
  lookup by ID/login name, enumerate, enable/disable and authority mutation
  with last-administrator protection), `ILocalGroupRepository`.
- `Sessions/SessionServiceContracts.cs` — `SessionState`, `ISessionService`
  (`LoginAsync`, `LogoutAsync`, `ShutdownAsync`,
  `CreateLinkedCancellationSource`), `SessionActivatedEvent`,
  `SessionLoggedOutEvent`, `SessionShutDownEvent`.
- `Processes/ProcessContracts.cs` — `ProcessId`, `AppInstanceId`,
  `ProcessState`, `ProcessExitReason`, `ServiceHealth`, `ResourceProfile`,
  `ProcessRecord`.
- `Processes/ProcessManagerContracts.cs` — `ProcessStartRequest`,
  `IProcessManager` (`Start`, `MarkRunning`, `Complete`, `Fault`, `StopAsync`,
  `Kill`, `TryGetActive`, `TryGetSingleton`, `GetActiveProcesses`,
  `GetHistory`, `GetCancellationToken`), `ProcessStateChangedEvent`.
- `Processes/ResourceSimulationContracts.cs` — `VirtualHardwareProfile`,
  `WorkloadActivity`, `ProcessResourceSample`, `SystemResourceSample`,
  `IResourceSimulator` (`Tick`, `GetHistory`, `GetSystemHistory`).
- `Time/SimulationClockContracts.cs` — `ISimulationClock`, `ISimulationRandom`,
  `ISimulationRandomStream` (defined alongside Section 7 as a prerequisite).
- `Events/EventBusContracts.cs`, `Diagnostics/DiagnosticsContracts.cs` — typed
  event bus and diagnostics/audit contracts (also Section 7 prerequisites).

**Implementation** — `Platform/HackerOs.Platform.Core/`:

- `Sessions/LocalPasswordHasher.cs` — PBKDF2-SHA256 (`"pbkdf2-sha256-v1"`,
  210,000 iterations by default) credential creation/verification using
  `CryptographicOperations.FixedTimeEquals`; unknown KDF identifiers fail
  closed.
- `Sessions/InMemoryLocalUserAndGroupRepositories.cs` — in-memory
  `ILocalUserRepository`/`ILocalGroupRepository` with last-enabled-administrator
  guards on disable and demotion.
- `Sessions/LocalSessionService.cs` — `ISessionService` implementation. Login
  validates credentials, seeds the user's home via `FileSystemSeeder`, builds
  an `AuthenticatedPrincipal`, and publishes `SessionActivatedEvent`; logout
  cancels and disposes the root cancellation source and publishes
  `SessionLoggedOutEvent`; shutdown is a terminal transition from any
  non-terminal state and publishes `SessionShutDownEvent`. Every transition is
  audited via `IAuditLog`.
- `Processes/InMemoryProcessManager.cs` — `IProcessManager` implementation.
  Allocates strictly increasing positive PIDs, validates an active parent
  before `Start`, links each process's cancellation token to the session's
  root token via `ISessionService.CreateLinkedCancellationSource()`,
  implements cooperative `StopAsync` racing a `ISimulationClock`-driven
  timeout against a stop signal, and implements `Kill` with recursive
  descendant cleanup (children/grandchildren transition with
  `ProcessExitReason.DependencyStop`). History is bounded (default 200
  entries) and evicts the oldest entry.
- `Processes/DeterministicResourceSimulator.cs` — `IResourceSimulator`
  implementation. Each tick computes a baseline/burst band per resource
  dimension (`baseline + (burst - baseline) * activity`), scaled by a
  process-state transition factor (`Running` = 1.0, `Starting`/`Stopping` =
  0.5, otherwise 0.0) and a small per-process seeded jitter (±5%, cached per
  PID so the stream progresses across ticks instead of resetting). Aggregate
  demand across all active processes is scaled down proportionally whenever it
  would exceed `VirtualHardwareProfile` capacity. Per-process and system-wide
  history are each bounded (default 120 samples) and evict the oldest entry.

## API summary

```csharp
// Session
SessionState state = session.State;
AuthenticatedPrincipal principal = await session.LoginAsync(loginName, password, ct);
await session.LogoutAsync(ct);       // cancels the root token, back to LoggedOut
await session.ShutdownAsync(ct);     // terminal, cancels the root token

// Process
ProcessStartRequest request = new(parentPid, appId, instanceId, kind, userId, sessionId, resourceProfile);
ProcessRecord process = processManager.Start(request);
processManager.MarkRunning(process.Pid);
processManager.Complete(process.Pid, exitCode: 0);
processManager.Fault(process.Pid, reason: ProcessExitReason.Fault);
ProcessRecord stopped = await processManager.StopAsync(process.Pid, TimeSpan.FromSeconds(5), ct: ct);
processManager.Kill(process.Pid); // cascades to descendants as DependencyStop

// Resources
IReadOnlyList<ProcessResourceSample> samples = resourceSimulator.Tick(processManager.GetActiveProcesses());
```

## Task list

- [x] Draft ADR 0012 for process, clock, random, and resource semantics.
- [x] Obtain Product + architecture approval for D-005.
- [x] Define session and process contracts.
  - **Completed: 2026-08-01** — See "Implemented architecture" above.
- [x] Implement deterministic clock, scheduler, random, processes, and resources.
  - **Completed: 2026-08-01** — `LocalSessionService`,
    `InMemoryProcessManager`, and `DeterministicResourceSimulator` implement
    the full stack in memory with no browser/timer dependency.
- [x] Add lifecycle/resource contract tests with no sleeps.
  - **Completed: 2026-08-01** — 95 Section 7 tests pass, including 4
    cross-cutting tests in
    `Tests/HackerOs.Platform.Core.Tests/Processes/CrossCuttingLifecycleTests.cs`
    that combine session, process manager, event bus, audit log, and resource
    simulator behavior in a single deterministic scenario. Full solution:
    277 tests pass with warnings as errors.