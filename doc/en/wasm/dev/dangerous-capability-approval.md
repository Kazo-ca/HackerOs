# Dangerous capability approval

## Goal

A non-system app never silently receives a dangerous capability
(`process.manage`, `network.real.access`, `filesystem.system.write`,
`keyring.read-all` from ADR 0100, `users.manage` from ADR 0120…, see
[`capabilities.md`](../framework/capabilities.md)). At launch, the OS asks an
administrator to approve it, like the Windows UAC "secure desktop", and
remembers the decision in real files. This document describes the
implementation of the `rebuild/2-dangerous-approval` work item of
[ADR 0049](adr/0049-dangerous-capabilities-and-admin-approval.md)
(sections 3 to 8).

System apps (ADR 0048) never see the window, and no non-system app of the
Ecosystem host declares a dangerous capability today: the basic experience
does not change.

## Files

| File | Owner | Content |
|---|---|---|
| `/etc/hackeros/security.config` | `system:system 0644`, `SystemOnly` document | `dangerousCapabilities=prompt` (default) or `block` |
| `/etc/hackeros/app-permissions.config` | `system:system 0644` | machine `allow` / `deny` decisions; **the only source of an `allow`** |
| `~/.config/hackeros/app-permissions.config` | the user | refusals (`deny`) valid for that user only |

The two `/etc` files are delivered commented out at first startup
(`SettingsStoreToFilesMigration.MigrateSystemAsync`).

```text
# /etc/hackeros/app-permissions.config
[permissions]
org.example.scanner:network.real.access=allow
org.example.scanner:filesystem.system.read=deny
```

- Key `<appId>:<capability>`, value `allow` or `deny`, section `[permissions]`.
- An invalid line (form, unknown capability, value) is ignored with a warning
  in `/var/log/syslog`. A line targeting a normal capability or a system app
  is ignored without effect. An `allow` in the user file is ignored with a
  warning.
- An invalid value of `dangerousCapabilities` is ignored (ADR 0044): `prompt`
  applies.
- `cat /etc/hackeros/app-permissions.config` shows what an administrator has
  approved; `sudo nano` allows editing it by hand.

## Decision table

`DangerousCapabilityEvaluator.Evaluate(manifest, isSystemApp, policy, decisions)`
gives the state of each declared dangerous capability. For a non-system app,
the first rule that applies wins:

| Rule | State (`DangerousCapabilityState`) | Granted? |
|---|---|---|
| `block` policy | `BlockedByPolicy` (existing `allow` entries are ignored) | no |
| machine `deny` | `DeniedByAdministrator` | no |
| user `deny` | `DeniedByUser` (wins over a machine `allow`) | no |
| machine `allow` | `Allowed` | **yes**, source `AdministratorApproval` |
| no decision | `Pending`: the gate asks | no |

A system app has the `System` state for all its dangerous capabilities
(granted at login, source `SystemPolicy`).

## Architecture

| Element | Project | Role |
|---|---|---|
| `SecuritySettingsDocuments`, `DangerousCapabilityPolicy` | Platform.Core/Policy | `security.config` schema, policy reading |
| `AppPermissionsDocument`, `AppPermissionDecisions` | Platform.Core/Policy | paths, parsing and line-by-line editing of the two files |
| `IAppPermissionStore` / `AppPermissionStore` | Platform.Core/Policy | reading the two files; machine writing in an **audited system context**, user writing with the user's own rights |
| `DangerousCapabilityEvaluator` | Platform.Core/Policy | pure decision table |
| `IDangerousCapabilityGate` / `DangerousCapabilityGate` | Platform.Core/Policy | launch gate, password verification, `auth.log`, grant alignment |
| `IDangerousCapabilityApprovalPrompt` | Platform.Core/Policy | prompt contract (window or terminal) |
| `DangerousCapabilityApprovalBroker` | Platform.Blazor/Security | host default prompt: request queue for the window |
| `DangerousCapabilityApprovalOverlay` | Platform.Blazor/Security | the special window, rendered by `DesktopShell` and `MobileShell` |
| `ShellHostPrompts` | Platform.Core/Shell | text mode for a command launched from a hosted shell (`HackShell`, [`shell-host.md`](../framework/shell-host.md)); written and read through the terminal's `HackCredentialPrompt` |
| `AppPermissionAdministration`, `AppPermissionsPanel` | Platform.Core/Policy, Apps.Settings | Settings Permissions tab |

### Launch

`AppLifecycleOrchestrator.LaunchAsync`, after the singleton check and the
consumption of any elevation ticket, calls
`IDangerousCapabilityGate.EnsureDecidedAsync(manifest, principal, request.ApprovalPrompt, request.Interactive)`
for any app that declares a dangerous capability, **before** creating the
process:

1. System app: nothing to do.
2. `block` policy: no window, `capability.blocked` entry.
3. `prompt` policy and at least one `Pending` capability:
   - non-interactive launch (`AppLaunchRequest.Interactive = false`, services
     started automatically with the session) or no prompt available (no shell
     displayed): `capability.deferred` entry, approval will happen from
     Settings or at the next interactive launch;
   - otherwise the prompt opens (`request.ApprovalPrompt`, otherwise the
     window).
4. The gate grants the `Allowed` capabilities and **revokes** a grant that no
   longer is (switch to `block`, denial). A change therefore takes effect at
   the next launch, and an instance's capabilities stay frozen at its launch
   (ADR 0042).

An app started without a capability receives
`HackUnauthorizedAccessException` when it uses it, as for any missing
capability.

### Prompt

`DangerousCapabilityApprovalRequest` carries the manifest, the pending
capabilities (`AppCapabilities.Describe`), the user, the administrator name to
prefill (the user only if they are an administrator) and
`ApproveAsync(login, password)`. The prompt decides nothing: `ApproveAsync`
verifies the credentials with `IAdminElevationVerifier` (ADR 0042; an account
other than the current user is accepted, as for `sudo`), then writes the
`allow` lines to the machine file in an audited system context. Even an
administrator must enter their password. After three failures,
`ApproveAsync` refuses without verifying and the prompt closes: nothing is
written. A prompt that answered `Approved` without a verified attempt is
treated as a close.

| End of the prompt | Effect |
|---|---|
| *Allow* succeeded | machine `allow`, `AdministratorApproval` grants, `capability.approve` (`success`) |
| wrong password | one `capability.approve` entry (`failure:<reason>`) per attempt |
| *Deny* | `deny` in the user's file, `capability.refuse`; the question no longer comes back for them |
| Esc / close / three failures | nothing written, the question comes back at the next launch |

### The special window

`DangerousCapabilityApprovalOverlay` (native Blazor, collocated `.razor.css`,
no MudBlazor) is rendered by the shell, outside any app window: a dark veil
covers the desktop and the taskbar (`z-index` above everything), captures the
mouse and keyboard (the shell shortcuts no longer receive the keys). Centered
window, not movable, red banner "Administrator authorization required" with a
shield, app identity (name, identifier, publisher, version, "Non-system app"
mention), capabilities with title and risk sentence, name and password
fields, **Deny** and **Allow** buttons (disabled without name or password),
footer "This decision will be recorded in /var/log/auth.log". The password
field takes the focus on opening. The labels follow the session language
(ADR 0104): those quoted here are the English ones, the default language; in a
French session the window shows the French equivalents.

`DangerousCapabilityApprovalBroker` shows one request at a time. Without an
attached window, a request ends as `Unavailable` (hence
`capability.deferred`); if the shell disappears (logout), pending requests are
closed without a decision.

### Terminal text mode

A shell hosted by `HackShell` (ADR 0056 §7) passes its `ShellHostPrompts` to
the interpreter, which hands it to each command launch. It writes the warning
banner and the capability list to the terminal, offers Yes (administrator) /
No (deny) / Cancel, then asks for the name and password with the same box as
`sudo` (through the terminal's `HackCredentialPrompt`:
`TerminalCredentialPrompt` for `TerminalPanel`), up to three attempts ("Sorry,
try again."). Host commands are system: only third-party commands see this
mode.

### One-time approval (ADR 0042) and `block`

With `dangerousCapabilities=block`, for a non-system app:

- `LaunchAsync` refuses a `CapabilityApproval` ticket that targets a
  dangerous capability (`ElevationDenied`,
  `lifecycle.elevation.blocked-by-policy`);
- the instance receives a `OneShotElevationGrantSet` where each dangerous
  capability is blocked (`Block` / `IsBlocked`): `Grant` is ignored and
  `TryConsume` fails;
- `WindowAppBase` no longer offers the one-time approval prompt for a blocked
  capability.

`sudo` is not concerned: it is an administrator acting themselves.

### Settings > Permissions

New **Permissions** tab of the Settings app (`AppPermissionsPanel`, component
separate from the "Installed Apps" tab of ADR 0048). It lists each app that
declares a dangerous capability and the state of each capability (*System*,
*Allowed*, *Denied by you*, *Denied by the administrator*, *Blocked by
policy*, *Pending*), and allows:

- changing `dangerousCapabilities` (prompt / block);
- allowing, revoking (machine `deny`) or returning to pending (line removal)
  a capability for the machine;
- removing one's own denial (without an administrator).

Each machine action opens the credentials prompt of ADR 0042;
`AppPermissionAdministration` verifies the administrator, writes in an audited
system context and logs. The panel resolves this service optionally: on a
host without ADR 0049 it displays a message instead of failing.

## Log `/var/log/auth.log`

All entries go through `IAuditLog` (`FileAuditLog`, ADR 0045); passwords are
never logged.

| Action | When | Properties |
|---|---|---|
| `capability.approve` | credential success or failure (window, terminal, Settings) | `app`, `capabilities`, `requestedBy`, `admin`, `outcome` |
| `capability.refuse` | the user denies | `app`, `capabilities`, `user` |
| `capability.blocked` | `block` policy at launch | `app`, `capabilities`, `user` |
| `capability.deferred` | no interactive session / no prompt | `app`, `capabilities` |
| `capability.revoke` | revocation or return to pending from Settings | `app`, `capability`, `admin`, `decision` |
| `capability.policy` | change of `dangerousCapabilities` from Settings (added by this work item) | `dangerousCapabilities`, `admin` |

## Composition (hosts)

`AddHackerOsEcosystem` (Ecosystem, `test/test` and the server) registers, as
`Scoped`: `IAppPermissionStore`, `DangerousCapabilityApprovalBroker` (also as
`IDangerousCapabilityApprovalPrompt`), `IDangerousCapabilityGate`,
`AppPermissionAdministration`, and passes the gate to
`AppLifecycleOrchestrator`. The parameter is optional: without a gate, a
non-system app simply never holds a dangerous capability.

## Decisions

- The gate is the **only** code that grants a dangerous capability to a
  non-system app; it also aligns the grants at each launch (grant and
  revocation), which makes the files the source of truth without rereading the
  files at login.
- Credential verification is in the gate, not in the prompt: an interface (or
  an app imitating the prompt) cannot fabricate an approval.
- The terminal text mode asks for credentials with the existing `sudo` box,
  since the terminal has no line reading while a command is running.
- The `capability.policy` action completes the table of section 8 to trace the
  policy change made from Settings.

## Tests

- `DangerousCapabilityApprovalTests` (`HackerOs.Platform.Core.Tests`): file
  parsing, decision table, system app without a prompt, non-system app
  without a decision not granted and prompt called at launch, `allow` +
  `prompt` granted / + `block` refused, user `deny` takes priority, user
  `allow` ignored, three wrong passwords (nothing written, three failed
  `capability.approve`), successful machine write for a non-administrator
  user, denial, `deferred`, unverified approval rejected, revocation on
  switch to `block`, `block` that disables one-time approval but not `sudo`,
  default files, Settings administration.
- `DangerousCapabilityApprovalOverlayTests` (`HackerOs.Platform.Blazor.Tests`,
  `HtmlRenderer` rendering, the solution not using bUnit): capabilities and
  risks displayed, prefilled name, Allow button disabled without a password,
  prompt unavailable without a window, request closed when the shell
  disappears.

## Tasks

- [x] `security.config` (schema, delivered default, policy reading).
- [x] Machine and user `app-permissions.config` (parsing, editing, warnings).
- [x] `DangerousCapabilityEvaluator` decision table.
- [x] `IDangerousCapabilityGate` called by `AppLifecycleOrchestrator.LaunchAsync`, grants aligned.
- [x] Special window `DangerousCapabilityApprovalOverlay` in both shells.
- [x] Terminal text mode.
- [x] `auth.log` log (`capability.*`).
- [x] Interaction with the one-time approval of ADR 0042 under `block`.
- [x] Settings Permissions view.
- [x] Contract tests and window rendering.
