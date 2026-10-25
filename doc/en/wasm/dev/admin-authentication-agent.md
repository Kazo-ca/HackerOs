> [Version française](../../../fr/wasm/dev/admin-authentication-agent.md)

# OS authentication agent

## Purpose

Some operations require an administrator **in addition to** the app's
capability: uninstalling an app for all users today, and later
changing the security policy or resetting the machine (ADR 0056).
Like `polkit` on Linux, the app never sees the password (ADR 0055,
section 5):

1. the app calls an SDK API, without credentials (for example
   `HackAppManagement.UninstallAsync(id, HackInstallScope.Machine)`);
2. the gateway checks the capability (`apps.manage`), then asks the agent to
   confirm an administrator;
3. the agent shows its own window, drawn by the platform above
   all windows: the operation in plain language, the app, the user who is
   asking, an administrator name and a password;
4. each attempt goes through `IAdminElevationVerifier`, the `sudo` verifier
   (ADR 0042), and the outcome is written to `/var/log/auth.log`;
5. if an administrator authenticated, the OS performs the operation itself, in
   an audited system context; otherwise the gateway throws
   `HackUnauthorizedAccessException`.

A process launched by `sudo` (`IsRootElevated`) already acts as root: the agent does not
ask for anything and the log carries `via=sudo`.

## Architecture

| Element | Project | Role |
|---|---|---|
| `IAdministratorAuthenticationAgent`, `AdministratorAuthenticationRequest`, `AdministratorAuthenticationResult` | `Platform/HackerOs.Platform.Core/Elevation` | contract called by the gateways |
| `AdministratorAuthenticationAgent` | same | implementation: direct root, three attempts, log |
| `IAdministratorAuthenticationPrompt`, `AdministratorAuthenticationPromptRequest`, `AdministratorAuthenticationAttempt` | same | surface that shows the request; it decides nothing |
| `AdministratorAuthenticationBroker` | `Platform/HackerOs.Platform.Blazor/Security` | request queue, implements `IAdministratorAuthenticationPrompt` |
| `AdministratorAuthenticationOverlay` | same | "secure desktop" window rendered by `DesktopShell` and `MobileShell` |

Registration in the Ecosystem host (`EcosystemServiceCollectionExtensions`):
the broker and the agent are `Scoped`, like the approval window for
dangerous capabilities. The agent is passed to `AppExecutionContextFactory` by
`AppCatalogGatewayServices`, resolved on demand (otherwise there is a circular dependency with
the orchestrator).

## Behavior

- **No surface** (no shell rendered, headless host): immediate
  refusal, reason `unavailable`.
- **Three attempts** at most, like the approval window of ADR 0049; on
  the third failure the window closes, reason `too-many-attempts`, and later
  attempts are refused without verification.
- **Cancel** (**Cancel** in an English session: the window follows the
  session language, ADR 0104), Escape, or closing the shell: reason
  `cancelled`, nothing is written.
- **Prefill**: the session user's name if they are an
  administrator. They still retype their password (ADR 0049, D24). Another
  administrator account can answer, as with `sudo`.
- **The window cannot forge an approval**: only an attempt verified by the
  agent counts, whatever the surface returns.
- **Passkey** (ADR 0168 section 4): when the host wires
  `AdministratorAuthenticationAgent.PasskeyVerifier`, the window shows
  "Use a passkey"; `AdminElevationVerifier.VerifyWithPasskeyAsync`
  verifies a passkey of the entered account (reason `no-passkey` if it has none).
  A closed browser prompt (`passkey-cancelled`) or a browser without
  WebAuthn (`passkey-unsupported`) does not count as a failed attempt.

### Log `/var/log/auth.log`

Action `admin.authenticate`, one entry per attempt and one for the final failure:

| Property | Content |
|---|---|
| `app` | id of the requesting app |
| `operation` | stable id of the operation: `apps.uninstall.machine`, `apps.reinstall.machine`, `permissions.policy`, `permissions.machine-decision`, `system.reboot`, `system.factory-reset`, `users.password-reset`, `users.password-delete`, `users.add`, `users.delete` |
| `requestedBy` | login of the session user |
| `admin` | login entered (or `root`, or `-`) |
| `outcome` | `success` or `failure:<reason>` |
| `via` | `sudo` when the process was already root |

The password is never logged.

## Use by a gateway

```csharp
AdministratorAuthenticationResult auth = await agent.AuthenticateAsync(
    new AdministratorAuthenticationRequest(
        callerManifest,
        "Uninstall Calculator for all users",   // displayed
        "apps.uninstall.machine",                // logged
        principal,
        isRootElevated),
    cancellationToken);
if (!auth.Succeeded)
{
    throw new HackUnauthorizedAccessException($"Administrator authentication failed ({auth.FailureReason}).");
}
// The OS then acts itself, in a system context.
```

`AppManagementGateway` (`Platform/HackerOs.Platform.Core/Execution`) is the
first user. The work item `4-sdk-system-admin` reuses it as is
through `AdministratorGate` (`Platform/HackerOs.Platform.Core/Administration`):
`AppPermissionAdministrationGateway` (policy and machine decisions) and
`AppSystemPowerGateway` (reboot, reset). See
[`system-administration.md`](../framework/system-administration.md).

## Decisions

- **A separate window** from the one for dangerous capabilities, in the same
  visual family (red banner, veil, "auth.log" footer): one approves
  capabilities for an app, the other authorizes a single operation.
- **The OS writes itself after authentication** instead of widening the app's
  rights: the app receives no reusable ticket, like `pkexec`, which
  launches the helper as root.
- **Terminal text mode not covered here.** ADR 0055 asks for text-mode input
  when the operation starts from a terminal. The terminal does not yet have
  a caller-supplied credentials prompt: it arrives with
  `HackCredentialPrompt` (work item `4-sdk-shell-host`). Until then, a command
  that asks for an administrator sees the desktop window, and `sudo` remains the
  text way to act as root.

## Tests

- `Tests/HackerOs.Platform.Core.Tests/Elevation/AdministratorAuthenticationAgentTests.cs`:
  root without a request, no surface, wrong attempt then correct one, three failures,
  surface that tries to forge an approval, closed passkey request
  that costs no attempt then verified key, no button without a verifier.
- `Tests/HackerOs.Platform.Core.Tests/Execution/AppCatalogGatewayTests.cs`:
  machine scope refused or accepted, system app refused before any request,
  `sudo` without a request.
- `Tests/HackerOs.Platform.Blazor.Tests/Security/AdministratorAuthenticationOverlayTests.cs`:
  window rendering and broker behavior.

## Task list (`rebuild/4-sdk-user-and-apps`)

- [x] Agent contracts and implementation in Platform Core.
- [x] Broker and window in Platform Blazor, rendered by both shells.
- [x] Registration in the Ecosystem host and passing to the context factory.
- [x] Use by `HackAppManagement` for the machine scope.
- [x] Tests and documentation.
- [ ] Text mode in the terminal (with `HackCredentialPrompt`, `4-sdk-shell-host`).
