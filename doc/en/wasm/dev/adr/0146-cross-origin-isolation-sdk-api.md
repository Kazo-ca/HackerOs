> [Version française](../../../../fr/wasm/dev/adr/0146-cross-origin-isolation-sdk-api.md)

# ADR 0146: `HackCrossOriginIsolation`, the isolation state in the SDK

## Status

Accepted on 2026-10-18.

- **Benoit's answer:** option **C** ("SDK API") of the card "Let
  Diagnostic read /proc (dangerous capability) to display isolation?"
  (2026-10-18, 16:34Z, thread "Exec 9: cross-origin isolation").
- **Replaces:** the last bullet of ADR 0143 §8 (Diagnostic reads
  `/proc/hackeros/workers` and declares `filesystem.system.read`). The rest of
  ADR 0143 §8 remains true: the probe, the reasons, the log line and the
  lines of `/proc/hackeros/workers` do not change.
- **Version:** the card said "in SDK 1.2", but 1.2.0 is already published
  (ADR 0132 §2, work item `exec/10-final`). Like ADR 0145, the addition stays
  in `PublicAPI.Unshipped.txt` and ships with the next minor version (1.3.0),
  according to SemVer ([`sdk-packages.md`](../../framework/sdk-packages.md)).

## Context

ADR 0131 §5 wants Diagnostic to show whether the page is isolated. ADR 0143 §8
made it read `/proc/hackeros/workers` through `HackFile`. But reading `/proc`
requires `filesystem.system.read`, a dangerous capability (ADR 0049).
Diagnostic thus had a second dangerous capability, which an administrator
who had already approved it had to approve at the next launch. Three choices were
proposed: keep (A), remove the badge (B), add a small non-dangerous public API
(C). Benoit chose C.

## Decision

1. **API** (`HackerOs.AppSdk`, namespace `Diagnostics`):
   - `HackCrossOriginIsolation.GetStatusAsync(CancellationToken)` returns a
     `HackCrossOriginIsolationStatus`: `IsIsolated`, `Reason`
     (`HackCrossOriginIsolationReason`: `None`, `Setting`, `MissingHeaders`,
     `InsecureContext`, `Browser`, `NotBrowser`), `Setting`
     (`HackCrossOriginIsolationSetting`: `Auto`, `On`, `Off`),
     `EmbedderPolicy`, `SharedPipes`, `SynchronousWorkerCalls`,
     `BrowserMemoryMeasurement`, `TimerResolution`;
   - these are the facts from the first lines of `/proc/hackeros/workers`, which
     the OS decided at startup; they do not change before a reload of
     the page.
2. **No capability.** Like `uname` or `/proc/cpuinfo` under Linux, the state
   describes the machine, not another app or a user's file. Any app
   can read it. `/proc` remains protected by `filesystem.system.read`.
3. **Gateway** `IAppCrossOriginIsolationGateway`, returned by
   `IAppExecutionContext.CrossOriginIsolation`. The member has a default
   implementation, which throws `NotSupportedException`, like `Drives`: an
   existing implementation of `IAppExecutionContext` still compiles, and a
   1.0, 1.1 or 1.2 app is not affected.
4. **On the main thread**, `AppCrossOriginIsolationGateway` (Platform Core,
   namespace `Isolation`) reads `CrossOriginIsolationState`. The host gives it to the
   factory through `SystemMetricsServices.CrossOriginIsolation` (`init` property,
   non-breaking addition). Before the probe and outside a browser, the reason is
   `NotBrowser`.
5. **In a worker**, it is a call (`call`) of the operation `coi.get`; the OS
   replies with the eight fields. The call is asynchronous, so it works with or
   without isolation.
6. **Diagnostic** reads the state through `HackCrossOriginIsolation` and no longer declares
   `filesystem.system.read`. Its only dangerous capability becomes
   `system.storage.inspect` again, as before `exec/9`. The badge tooltip keeps
   the text of `/proc/hackeros/workers` (same keys, same values). The badge
   is also displayed without a worker pool; it disappears only if the context
   does not provide the gateway.
7. **Example**: `sample-cmd --isolation` writes `isolation: yes|no <reason>`.

## Consequences

- An administrator who approved Diagnostic since `exec/9` keeps a
  decision for `filesystem.system.read` in
  `/etc/hackeros/app-permissions.config`. It is no longer used, since the
  manifest no longer declares this capability. No new approval
  window appears.
- New lines in `Shared/HackerOs.AppSdk/PublicAPI.Unshipped.txt` and
  `Platform/HackerOs.Platform.Core/PublicAPI.Unshipped.txt`; `HackerOsVersion`
  stays `1.2.0`.
- Tests:
  - `CrossOriginIsolationStatusTests` (WorkerHost): same response on the main
    thread and in a worker, without capability; round trip of each field through the
    protocol;
  - `CrossOriginIsolationTests` (Platform Core): each reason and the setting;
  - `BuildKnownLazyAppsTests`: Diagnostic has only `system.storage.inspect`
    as a dangerous capability.

## References

- ADR 0049, 0057, 0131, 0132, 0143, 0145.
- [`worker-processes.md`](../worker-processes.md), [`app-sdk.md`](../../framework/app-sdk.md),
  [`sdk-reference.md`](../../framework/sdk-reference.md), [`sdk-packages.md`](../../framework/sdk-packages.md),
  [`capabilities.md`](../../framework/capabilities.md).
