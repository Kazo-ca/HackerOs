# ADR 0049: Dangerous capabilities and administrator approval

## Status

Accepted on 2026-10-10.

- **Plan phase:** phase 2, App model ([`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), section 6.4, decisions D23 to D25).
- **Complements:** ADR 0002 (system status, coming from the trust policy,
  now also decides on dangerous capabilities) and ADR 0003 (each exact
  capability in the catalog receives a classification).
- **Complements:** ADR 0042. One-off approval of a denied operation and
  `sudo` remain in place; this ADR adds a **durable** approval of
  dangerous capabilities, requested at launch.
- **Settles** the question left open by ADRs 0044 and 0045: the
  `settings.*` and `diagnostics.*` capabilities are removed from the catalog.

## Context

`CleanProfileCapabilityGrantSeeder` grants at sign-in all the capabilities
declared by each manifest (`LocalSessionService`, source `BuildProfile`), without
asking anything. An app can therefore declare `process.manage` or
`network.real.access` and obtain them silently. The grant repository
(`CapabilityGrantRepository`) is in memory and rebuilt on each sign-in.
`CapabilityGrantSource.AdministratorApproval` exists but is produced by no
code. `/etc/hackeros/policy.config` declares `capabilityGrantsLocked`, which is read
nowhere.

## Decision

### 1. Classification of capabilities (D23)

`AppCapabilities` gains an exact classification by identifier, in the same file
(`Shared/HackerOs.App.Abstractions/AppCapabilities.cs`):

```csharp
public enum CapabilityRisk { Normal, Dangerous }

public static CapabilityRisk GetRisk(string capability);      // ArgumentException if unknown
public static bool IsDangerous(string capability);
public static CapabilityDescription Describe(string capability); // title and risk, for the interface
```

| Capability | Classification | Risk shown to the administrator |
|---|---|---|
| `filesystem.private.read`, `filesystem.private.write` | normal | n/a |
| `filesystem.user-selected.read`, `filesystem.user-selected.write` | normal | n/a |
| `filesystem.user-home.read`, `filesystem.user-home.write` | normal | n/a |
| `filesystem.system.read` | **dangerous** | Read files outside the home folder, including the system configuration. |
| `filesystem.system.write` | **dangerous** | Modify system files, including `/etc` and the settings of all apps. |
| `apps.launch` | normal | n/a |
| `apps.manage` (**new**) | **dangerous** | Install, uninstall or reinstall applications. |
| `dialogs.file-open`, `dialogs.file-save`, `dialogs.folder-select` | normal | n/a |
| `file-associations.read` | normal | n/a |
| `file-associations.write` | **dangerous** | Choose which app opens each file type for the whole machine. |
| `process.list` | normal | n/a |
| `process.manage` | **dangerous** | Stop the processes of other applications. |
| `services.manage` | **dangerous** | Start, stop and configure system services. |
| `windows.manage` | **dangerous** | Read, move or close the windows of other applications. |
| `notifications.post` | normal | n/a |
| `clipboard.read`, `clipboard.write` | normal | n/a |
| `network.simulated.read`, `network.simulated.write` | normal | n/a |
| `network.real.access` | **dangerous** | Reach real servers on the Internet through the relay server. |
| `gameplay.domain.access` | normal | n/a |

- Every new capability must receive a classification: a test walks the
  catalog and fails if a capability has none.
- **Removed from the catalog:** `settings.read`, `settings.write`,
  `settings.system.read`, `settings.system.write` (not evaluated since
  ADR 0044; settings are files, covered by `filesystem.*`) and
  `diagnostics.read`, `diagnostics.clear` (not evaluated since ADR 0045). An
  app definition that cites them no longer compiles; an old in-memory grant that
  names them is simply ignored.
- `apps.manage` is added now so that the classification is complete. It will
  protect the public app management API in phase 4 (ADR 0048, section 4).

### 2. Who receives what

| App | Declared normal capability | Declared dangerous capability |
|---|---|---|
| System (ADR 0048) | granted automatically, source `BuildProfile` | granted automatically, source `SystemPolicy` |
| Non-system | granted automatically, source `BuildProfile` | granted **only** if an "allow" decision exists and the policy permits it (sections 3 and 4), source `AdministratorApproval` |

An undeclared capability is never granted, in any case (ADR 0002).

`CleanProfileCapabilityGrantSeeder.SeedDeclaredCapabilities` receives the app's
system status and the decisions read (section 4), and applies this table.

### 3. Security policy (D25)

File `/etc/hackeros/security.config`, system-only document
(`OsSettingsDocument` with `SystemOnly: true`, like `policy.config`),
`system:system 0644`:

```text
# /etc/hackeros/security.config
# prompt: a non-system app that requests a dangerous capability opens the
#         administrator approval window (default).
# block:  dangerous capabilities are never granted to non-system apps.
dangerousCapabilities=prompt
```

- Key `dangerousCapabilities`, type `Enum` (`prompt`, `block`), default `prompt`.
  An invalid value is ignored with a warning (ADR 0044), so `prompt`
  applies.
- With `block`, existing "allow" decisions are ignored as long as the
  policy stays at `block`, and the one-off approval of ADR 0042 is no longer
  offered for a dangerous capability of a non-system app. `sudo` is not
  affected: it is an administrator acting themselves.
- The `capabilityGrantsLocked` key of `policy.config`, never evaluated, is removed
  from the schema.

### 4. Remembered decisions

Machine file `/etc/hackeros/app-permissions.config` (`system:system 0644`) and
user file `~/.config/hackeros/app-permissions.config`:

```text
# /etc/hackeros/app-permissions.config
[permissions]
org.example.scanner:network.real.access=allow
org.example.scanner:filesystem.system.read=deny
```

- Key `<appId>:<capability>`, value `allow` or `deny`. Read by
  `ConfigDocumentFormat.Parse` without a schema; an invalid line is ignored with
  a warning in `/var/log/syslog`. A line that targets a system app or
  a normal capability is ignored with no effect.
- **The machine file is the only source of an "allow".** It is written by the OS
  after an administrator approval (section 5), or by an administrator with
  `sudo nano`.
- The user file contains only `deny` entries: a user's refusal
  applies to them alone and avoids asking them again at every launch. An `allow`
  in this file is ignored with a warning.

**Dangerous capability granted** to a non-system app =
`prompt` policy **and** `allow` in the machine file **and** no `deny`
in the user file.

A dangerous capability is **pending** if the machine file has no
decision and the user has not denied it. An app update that
adds a dangerous capability thus creates a new request, for that capability
only.

### 5. Approval window at launch (D23, D24)

When a non-system app that has at least one **pending** dangerous capability
is launched, `AppLifecycleOrchestrator.LaunchAsync` calls, before creating the
process, `IDangerousCapabilityGate.EnsureDecidedAsync(manifest, principal)`
(`Platform/HackerOs.Platform.Core/Policy/`):

1. **`block` policy:** no window. The app starts without its dangerous
   capabilities. `capability.blocked` entry in `/var/log/auth.log`.
2. **`prompt` policy:** the gate calls
   `IDangerousCapabilityApprovalPrompt`, which has two implementations:
   - **special window** for window and service apps launched from the
     desktop (section 6);
   - **text mode** in `TerminalPanel` for a command launched from the
     terminal: warning banner, list of capabilities, then a request for the
     administrator name and password, like `sudo`.
3. **Result:**
   - *Allow*: an enabled administrator enters their name and password,
     verified by `IAdminElevationVerifier` (ADR 0042). It can be a different
     account than the current user, as with `sudo`. Even an administrator
     user must enter their password (D24). Three failures cancel the
     request. On success, the OS writes the `allow` lines to the machine
     file **with an audited system context** (ADR 0002), then grants the
     grants (source `AdministratorApproval`) before the process starts.
   - *Deny*: `deny` lines in the user's file. The app starts without
     these capabilities.
   - *Close* without choosing: nothing is written; the app starts without these capabilities and
     the question will come back at the next launch.
4. **Without an interactive session** (service started at boot before any sign-in):
   no window, capabilities not granted, `capability.deferred` entry in
   `auth.log`. Approval is then done from Settings (section 7).

An app started without a dangerous capability receives
`HackUnauthorizedAccessException` (ADR 0043) when it uses it, as for
any missing capability.

### 6. The special window

Native Blazor component `Platform/HackerOs.Platform.Blazor/Security/DangerousCapabilityApprovalOverlay.razor`,
with a collocated `.razor.css`; no MudBlazor, since it is part of the
OS chrome. It imitates the Windows UAC "secure desktop":

- dark veil over the **whole** desktop, above all windows and the
  taskbar; it captures the keyboard and mouse as long as it is open;
- centered window, not movable, with no minimize button, visually
  distinct from ordinary dialog boxes: red banner "Administrator
  authorization required", alert border, shield icon;
- name, icon, identifier, publisher and version of the app, with the note "Non-system
  app";
- the list of requested capabilities, each with its title and the risk sentence
  from `AppCapabilities.Describe`;
- administrator name field (prefilled with the current user if they are an
  administrator) and password field;
- **Allow** (danger style) and **Deny** buttons; Escape is equivalent to closing;
- in the footer: "This decision will be recorded in /var/log/auth.log".

It is rendered by the platform shell, outside any app window: an app
cannot draw over it or imitate it in its own window.

### 7. Settings > Applications > Permissions

In the Settings app, a per-app view (a separate component, so as not to conflict
with the tab from ADR 0048) shows each declared dangerous capability
with its state: *system*, *allowed*, *denied by you*, *denied by the
administrator*, *blocked by policy*, *pending*.

- Allowing or revoking for the machine writes to
  `/etc/hackeros/app-permissions.config`, and therefore goes through the approval prompt
  of ADR 0042; the action is logged as in section 8.
- A user can remove their own `deny`.
- An administrator changes `dangerousCapabilities` from the same page.
- A change takes effect at the app's next launch: the capabilities of a running
  instance are frozen at its launch (ADR 0042).

### 8. Log `/var/log/auth.log`

Each decision goes through `IAuditLog` (`FileAuditLog`, ADR 0045):

| Action | When | Properties |
|---|---|---|
| `capability.approve` | password success or failure in the window or the terminal | `app`, `capabilities`, `requestedBy`, `admin`, `outcome` |
| `capability.refuse` | the user denies | `app`, `capabilities`, `user` |
| `capability.blocked` | `block` policy | `app`, `capabilities`, `user` |
| `capability.deferred` | no interactive session | `app`, `capabilities` |
| `capability.revoke` | revocation from Settings | `app`, `capability`, `admin` |

Passwords are never logged; the existing redactor
applies.

### 9. Contract tests

- Every capability in the catalog has a classification; `settings.*` and `diagnostics.*`
  no longer exist.
- A system app receives its dangerous capabilities without a window.
- A non-system app without a decision does not receive its dangerous capabilities, and
  the gate is called at launch.
- Machine `allow` + `prompt` = granted; + `block` = denied; user `deny`
  wins over machine `allow`; `allow` in the user file
  ignored.
- Wrong password three times: nothing written, three failed `capability.approve`
  entries.
- Writing the machine file after approval succeeds even if the current
  user is not an administrator.
- `block` disables the one-off approval of ADR 0042 for a dangerous
  capability of a non-system app, but not `sudo`.
- bUnit rendering of the window: capabilities and risks displayed, Allow button
  disabled without a password.

## Consequences

- A third-party app can no longer silently obtain the right to kill processes,
  modify `/etc` or reach the Internet.
- System apps see no window, and no non-system app of the Ecosystem
  host declares a dangerous capability today: the base experience
  does not change.
- `cat /etc/hackeros/app-permissions.config` shows what an administrator
  approved, as `apps.config` shows what they uninstalled.
- A decision applies to the entire machine; there is no per-user
  approval. A user can only deny for themselves.
- Terminal commands provided by the host are system and never trigger
  the prompt; only third-party commands will see it.
- Apps that cite `settings.*` or `diagnostics.*` must remove those
  capabilities in the same change as their removal from the catalog.

## Implementation work items

| Branch | Content | Depends on |
|---|---|---|
| `rebuild/2-capability-classification` | `CapabilityRisk`, `GetRisk`, `IsDangerous`, `Describe`, `apps.manage`, removal of `settings.*` and `diagnostics.*` (catalog, app definitions, tests), seeder per section 2 without a window (a dangerous capability of a non-system app is not granted), removal of `capabilityGrantsLocked`. | `2-app-migration` (ADR 0047), `2-system-apps` (ADR 0048) |
| `rebuild/2-dangerous-approval` | `security.config`, `app-permissions.config` files, `IDangerousCapabilityGate` in the launch, special window, terminal text mode, `auth.log` log, interaction with one-off approval, Permissions view in Settings. | `2-capability-classification` |

## References

- ADR 0002 and ADR 0003 (complemented), ADR 0042 (complemented), ADR 0043 (exceptions),
  ADR 0044 (settings as files), ADR 0045 (`auth.log`), ADR 0048 (system
  apps).
- [`ecosystem-rebuild-plan.md`](../plans/ecosystem-rebuild-plan.md), section 6.4.
- `Platform/HackerOs.Platform.Core/Policy/CleanProfileCapabilityGrantSeeder.cs`,
  `Shared/HackerOs.App.Abstractions/AppCapabilities.cs`.
