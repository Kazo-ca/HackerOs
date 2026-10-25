> [Version française](../../../../fr/wasm/dev/adr/0107-service-startup-implementation-adjustments.md)

# ADR 0107: implementation adjustments for service states and startup

## Status

Accepted on 2026-10-17 (work item `v3/5-services`).

- **Adjusts:** [ADR 0097](0097-service-health-and-startup.md) §1, §4 and
  "Consequences" (ADR 0097 is not modified).
- **Questions asked to Benoit** in the work item thread, with choices and
  implications: the `hackeros-svc` account (§2) and the startup of `hackwebd`
  (§3). The code follows the recommended option while awaiting his answer; a different
  answer will be handled in a follow-up work item.

## Context

While writing `v3/5`, four points of ADR 0097 did not match the code:

- `ServiceAppBase` had no `OnStartAsync`: there was therefore no
  "startup is finished" moment to go from `activating` to `active`.
- The OS has no `/etc/passwd` file: accounts are in the IndexedDB user
  store, and the login screen lists that store.
- `hackwebd` reads `~/.config/hackweb/sites` of the logged-in user
  (ADR 0077, 0080): started at boot under `hackeros-svc`, it would no longer find
  the user's sites.
- `AppLifecycleOrchestrator.StartAllServicesAsync` existed but nothing
  called it; nothing started the services at boot nor stopped them at
  logout.

## Decision

### 1. `OnStartAsync` and the "ready" notification

- `ServiceAppBase` gains `protected virtual Task OnStartAsync(context, token)`
  (empty by default). `RunAsync` awaits it, sends the "ready" notification
  (`IAppExecutionContext.ServiceNotify`, the equivalent of `sd_notify("READY=1")`),
  then calls `RunCoreAsync`. An existing service, which only has
  `RunCoreAsync`, becomes `active` right away.
- `ReportHealth(health, status)` goes through the same channel. In a worker
  (ADR 0070), the notification crosses the protocol (`svc.notify`); the state
  (`svc.status`) and `reset-failed` (`svc.resetfailed`) too.
- The start timeout (30 s, `AppLifecycleOrchestrator.ServiceStartTimeout`)
  is counted in simulated time when the OS has a clock: a paused simulation
  does not make a start fail.
- A service whose token is canceled underneath it (session root at
  logout, system scope at shutdown) ends its process instead of
  staying `active` with no code running.

### 2. Built-in `hackeros-svc` account (recommended option, open question)

- `ServiceAccount` (Platform Core) defines a fixed account: stable identifier
  (`00000000-0000-4000-8000-0000000003e7`), own group, home
  `/var/lib/hackeros-svc`, shell `/usr/sbin/nologin`. It is not in the user
  store: it does not appear on the login screen, no
  IndexedDB migration, and the creation of the first administrator rejects this name.
- There is no `/etc/passwd`. The sentence "added to `/etc/passwd` … by a
  migration" of ADR 0097 does not apply.
- The processes of a system service belong to a fixed system session
  (`ServiceAccount.SystemSessionId`) and to a **system scope** of the process
  manager (`ProcessStartRequest.SystemScope`): logout does not cancel
  their token, shutdown does (`InMemoryProcessManager.CancelSystemScope`).

### 3. `hackwebd` remains a login service (recommended option, open question)

- `hackwebd` does not declare `ServiceStartup = Boot`: it starts at login
  under the user, as before, and serves that user's sites. Moving it
  to `Boot` requires moving its sites out of `~` (for example
  `/srv/hackweb`) or one instance per user; this is the question asked to
  Benoit.

### 4. Who starts what, and when

- `ServiceStartupCoordinator` (Platform Core) plays the role of
  `multi-user.target` and `systemd --user`:
  - end of boot (`EcosystemBootCoordinator.BootAsync`, after applying
    `/etc/hackeros/apps.config`): `StartBootServicesAsync` under `hackeros-svc`;
  - after each login (`SessionActivatedEvent`): the user's `apps.config`
    is applied, then `StartAllServicesAsync` launches their `Automatic`
    services;
  - logout (`SessionLoggedOutEvent`): `StopSessionServicesAsync` stops
    the services of that session; system services continue;
  - shutdown (`SessionShutDownEvent`): `StopAllAsync(Shutdown)`, then
    cancellation of the system scope.
  A failure is written to `/var/log/syslog` and blocks neither boot nor
  login.
- Starting, stopping or changing the mode of a system service requires an
  administrator (`service.system.administrator-required`), like
  `sudo systemctl` for a system unit. Started by hand, it still runs
  under `hackeros-svc`, in the system scope. Reading its state remains free.

### 5. Uninstall report

- `AppInstallationService.UninstallAsync` collects the active processes before
  modifying `apps.config`: the file watcher may apply the
  change (and stop the processes) before the reload that follows, and the
  report must still list them. SDK side:
  `HackAppManagement.PlanUninstallAsync` and `UninstallWithReportAsync`.

## Consequences

- Manifest field `serviceStartup` (`login` | `boot`, absent = `login`) in
  the schema, the serializer (allow-list) and the validator
  (`manifest.serviceStartup.forbidden`, `manifest.serviceStartup.invalid`).
- If Benoit chooses another option for §2 or §3, a new ADR replaces it
  and a follow-up work item implements it.
