> [English version](../../../../en/wasm/dev/adr/0012-process-and-clock-model.md)

# ADR 0012: Deterministic Process, Clock, and Resource Model

## Status

Accepted on 2026-08-01.

## Context

HackerOS simulates processes and hardware resources for lifecycle, monitoring,
commands, services, and later gameplay. Browser timers are throttled, real device
metrics vary by host, and uncontrolled randomness makes tests irreproducible.
The simulation must remain useful offline and independent of native OS process
APIs.

## Decision

### Process identity and parentage

`ProcessId` is a positive 64-bit integer allocated monotonically per installation
runtime. PID `0` is reserved for no process. Allocation never reuses a PID during
one OS boot, even after history eviction.

Each process record has immutable:

- PID and optional parent PID;
- app ID, app instance ID, and app kind;
- user ID and session ID;
- creation correlation ID; and
- resource profile snapshot.

Parent PID is validated when the child is created and never transferred. A
process cannot parent itself. Orphan behavior is explicit: normal parent stop
cancels descendants according to lifecycle policy; forced history cleanup never
rewrites parent IDs.

### State machine

Processes use these states:

```text
Created -> Starting -> Running -> Stopping -> Stopped
                     \-> Faulted -> Stopping/Stopped
```

`Created`, `Starting`, `Running`, `Stopping`, `Stopped`, and `Faulted` transitions
are platform-owned. Terminal command completion may move directly from Running to
Stopped with an exit code. Service/window close requests move through Stopping;
kill may force Stopped after cancelling the process token.

Terminal states carry UTC stop time, integer exit code where applicable, and an
exit reason such as completed, close request, cancelled, killed, logout,
shutdown, disabled, timeout, dependency stop, or fault. Fault details are
diagnostic references, not unbounded exception objects retained forever.

### Cancellation and bounded stop

Every process owns a cancellation source linked to its session token. The token
is handed to app execution but the source remains platform-owned. Targeted close
or kill cancels that process and policy-defined descendants, not unrelated
session work. Logout/shutdown cancels the session root.

Graceful stop receives a deterministic deadline from the simulation clock. If
cleanup does not finish before the deadline, the platform records timeout/forced
stop and removes active lifecycle state. Correctness never depends on browser
unload cleanup.

### Simulation clock and scheduler

Domain code uses `ISimulationClock`, never `DateTimeOffset.UtcNow`,
`Task.Delay`, `PeriodicTimer`, or browser timers directly. The clock exposes:

- current UTC simulation time;
- monotonic tick number;
- fixed tick duration;
- deterministic delayed/scheduled work; and
- explicit advancement for tests.

Production uses a browser-aware adapter that advances available ticks from one
platform timer without pretending every wall-clock interval executed while the
tab was suspended. Tests use a manual clock and never sleep.

Scheduled callbacks execute by due tick, then insertion sequence. One callback
failure is isolated and reported without preventing later callbacks. Disposal or
cancellation removes pending work and retained app references.

### Seeded randomness

Domain randomness uses `ISimulationRandom`. The installation/boot supplies a
recorded root seed. Independent streams derive from stable domain keys such as
`process:{pid}:resources`, so adding random calls in one subsystem does not change
another subsystem's sequence.

Cryptographic IDs, tokens, signatures, and secrets never use the simulation
random source.

### Resource declarations and hardware influence

App manifests declare a bounded `ResourceProfile` with baseline and burst weights
for CPU, memory, storage I/O, and network I/O. These are simulation inputs, not
resource reservations or host measurements.

Each deterministic resource tick derives usage from:

1. process state;
2. the immutable resource profile;
3. explicit workload/activity signals;
4. the selected virtual hardware profile; and
5. the process-specific seeded random stream.

Stopped processes consume zero active resources. Starting/stopping may use
defined transition costs. Usage is clamped to virtual hardware capacity and
aggregates deterministically by PID. Hardware upgrades change future tick
capacity/coefficients but do not rewrite process history.

Resource history uses bounded per-process and system-wide samples. Storage values
come from virtual filesystem accounting; network values come from simulated or
explicit proxy activity. The API does not expose browser memory, host CPU load,
native process IDs, or real operating-system telemetry as simulation truth.

### Persistence

Active process execution and volatile service state are not persisted or resumed.
Bounded terminal process summaries and resource samples may be persisted for
diagnostics. A fresh OS session creates fresh active process records and tokens.

## Consequences

- Lifecycle and resource tests require no sleeps or wall-clock assumptions.
- Identical seeds, profiles, activity, and ticks produce identical usage.
- Background-tab throttling affects observation cadence, not domain ordering.
- System Monitor displays virtual HackerOS resources, not misleading host data.
- Later hardware/gameplay systems can influence the same deterministic model.
- A production clock adapter is still required in Phase 2, but it cannot change
  the headless contracts.

## References

- `docs/session-and-process-lifecycle.md`
- `doc/fr/wasm/dev/plans/wasm-v3-migration-analyse.md` sections 7.7 and 12.2