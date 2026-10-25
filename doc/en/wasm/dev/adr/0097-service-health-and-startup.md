> [Version française](../../../../fr/wasm/dev/adr/0097-service-health-and-startup.md)

# ADR 0097: service state and health, start at boot or at login, uninstalling an active app

## Status

Accepted on 2026-10-17 (work item `v3/1-adr`, for the work item `v3/5-services`).
§4 follows Benoit's answer of 2026-10-17: only system apps
start at boot.

- **Complements:** ADR 0056 (`HackServiceController`), ADR 0064 (`systemctl`),
  ADR 0069 (requested stop), ADR 0048 (uninstallation), ADR 0049 §5.4
  (automatic start and dangerous capabilities).
- **Plan:** [`wasm-v3-completion-plan.md`](../plans/wasm-v3-completion-plan.md),
  line `v3/5-services`; gaps R9 and R23.

## Context

- `ServiceHealth` (`Unknown`, `Healthy`, `Degraded`, `Unhealthy`,
  `Shared/HackerOs.Simulation.Abstractions/Processes/ProcessContracts.cs:115`)
  is always `Unknown`: `InMemoryProcessManager` sets it at creation and
  nothing changes it. Nothing displays it.
- `HackServiceControllerStatus` (`Stopped`, `StartPending`, `StopPending`,
  `Running`) has no failure state: a service that crashes goes back to
  `Stopped`, and `systemctl status` displays `inactive (dead)`.
- The only start setting is `AutoStart` (package preset) and
  `ServiceStartModeStore` (`~/.config/<appId>/service.conf`, effective mode).
  `AppLifecycleOrchestrator.StartAllServicesAsync` launches the `Automatic`
  services, but **no production code calls it** (only tests): today, no
  service starts on its own, except the web apps launched by `WebAppActivator`.
- `AppInstallationService.UninstallAsync` silently stops the processes of the
  uninstalled apps (`AppLifecycleOrchestrator.DisableAsync`) and the result does
  not list them.

## Decision

### 1. Service states, systemd style

- A service has an **active state**: `inactive`, `activating`, `active`,
  `deactivating`, `failed`, like systemd's `ActiveState`. It is carried by
  `HackServiceController.ActiveState` (new type `HackServiceActiveState`).
  `Status` (modeled on `System.ServiceProcess`) remains and is derived from the
  active state; `failed` gives `Stopped`.
- `activating` lasts from the process launch until the return of the service's
  `OnStartAsync`; `active` afterwards.
- A service goes to `failed` when its process ends without having been asked
  to: unhandled exception, non-zero exit code, forced stop after
  escalation (ADR 0069). `HackServiceController.Result` gives the reason, with
  systemd's words: `exit-code`, `exception`, `killed`, `timeout` (start taking
  longer than 30 s), `success` otherwise. The `failed` state remains until the next
  start or until `systemctl reset-failed`.
- No automatic restart (`Restart=`) in this work item.

### 2. Health reported by the service

- `ServiceApp` receives `ReportHealth(HackServiceHealth health, string? status)`,
  the equivalent of `sd_notify("STATUS=…")`: an active but degraded service (for
  example a site that can no longer reach its database) says so without stopping.
- Default health: `Healthy` when `OnStartAsync` returns, `Unknown` before.
  `ProcessRecord.Health` carries the value, and the workers protocol already
  transports it.
- `HackServiceController.Health` and `.StatusText` expose it.

### 3. Display

- `systemctl status` displays `Active: failed (Result: exit-code) since …`,
  `activating (start)`, `active (running)` and the line `Status: "…"` when the
  service has given a text. `systemctl is-failed` and `systemctl reset-failed`
  are added; `is-active` answers `failed` with code 3, like systemd.
- The Services tab of the task manager displays the active state, the health and
  the text.
- A transition to `failed` is written to `/var/log/syslog` ("`<unit>`: Failed
  with result 'exit-code'.").

### 4. Start at boot or at login

- New manifest field `ServiceStartup` (`AppDefinition.ServiceStartup`):
  `Login` (default) or `Boot`. It only applies to an app of kind
  `Service`; the validator rejects it elsewhere.
- **Session services (`Login`)**: started after login, under the
  logged-in user, stopped at logout. This is the role of
  `StartAllServicesAsync`, which the work item finally wires into the OS login
  sequence.
- **System services (`Boot`)**: started when the OS has mounted its file
  system, before the login screen, and stopped only at shutdown.
  Only a **system app** (ADR 0048, `IsSystemApp` on the host
  reference) can request `Boot`: the catalog ignores `Boot` for another app
  and starts it at login, with a warning in `/var/log/syslog`.
  A system service runs under a service account with no login
  (`hackeros-svc` by default, created by the OS; `hackwebd` keeps `www-data`,
  ADR 0080), never under a human user. `hackwebd` moves to `Boot`,
  like a real web server.
- The effective mode of a system service is set in
  `/etc/<appId>/service.conf` (administrator), that of a session service in
  `~/.config/<appId>/service.conf` (unchanged).

### 5. Uninstalling an app with a running process

- `AppInstallationService` gains `PlanUninstallAsync(appId)`, which returns, without
  changing anything, the set of apps that will be uninstalled (dependents
  included) and **the active processes** of that set (PID, name, user).
- Settings displays this list in the confirmation ("These processes will
  be stopped: …") before uninstalling. (There is no uninstall
  command; if one is added later, it will do the same.)
- The processes are stopped by the requested stop of ADR 0069, then forced
  after the delay; `AppInstallationResult` lists those that were stopped and
  those that had to be forced.

## Consequences

- `v3/5` modifies `HackServiceController`, `ServiceApp`, `systemctl`, the Services
  tab, `AppInstallationService`, the manifest validator and the OS startup
  sequence, with tests for each state transition.
- `sdk-reference.md`, `app-model.md`, `linux-commands.md`, `task-manager.md`
  and `app-installation.md` are updated.
- The `hackeros-svc` account is added to `/etc/passwd` at OS creation and
  by a migration for existing OSes.
