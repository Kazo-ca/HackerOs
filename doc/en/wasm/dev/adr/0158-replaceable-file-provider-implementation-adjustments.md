> [Version française](../../../../fr/wasm/dev/adr/0158-replaceable-file-provider-implementation-adjustments.md)

# ADR 0158: adjustments to the replaceable file provider

## Status

Accepted on 2026-10-20.

- **Work item:** `desk/3c-replaceable-file-provider` (work item 3c of the plan
  [`desktop-and-context-menu-plan.md`](../plans/desktop-and-context-menu-plan.md)).
- **Adjusts:** ADR 0156 on implementation points; its decisions
  remain true.

## Context

ADR 0156 opens `hackeros.file-view` and `hackeros.file-dialogs` to any windowed
app and has the provider chosen by an administrator, in
`/etc/hackeros/components.config`, with a logged fallback to the explorer.
Work item 3c implements it. Several details were not fixed.

## Decision

### 1. The open contracts keep a separate scope

`AppComponentProviderScope.SystemApps` becomes
`AppComponentProviderScope.AdministratorSelected` (API not yet published,
renaming at no cost). These contracts accept any windowed app, but remain
OS controls: the administrator's choice applies only to them, and
the absence of any provider that loads remains the
`component.unavailable` error. `AppManifestValidator.ValidateComponentProviders`,
`BuiltInApplicationsAggregator.FindReservedComponentContracts` and the
`catalog.component.reserved` warning disappear. The code **`HOSAPP0025` is
removed and will not be reused**.

### 2. The file is read line by line, at each resolution

Contract names contain dots: `components.config` has no declared
schema. `ComponentProvidersDocument` reads it with the tolerant reader of
`.config` files (ADR 0044 §5) and rewrites it without touching comments
(ADR 0044 §7). A key that is not an administrator-chosen contract
is ignored; so is an app unknown to the catalog, at resolution time.

The file is reread at each resolution of a contract (`IComponentProviderSelection`),
not cached: a control is resolved only when displayed, and a
change made with `sudo nano` thus applies to the next control, without
file watching. A fresh installation has no file; the first
choice writes it with a commented header. Going back to the explorer **removes the
line**: choosing the explorer never writes it.

### 3. An SDK gateway, `HackComponentProviders`

Settings uses the public SDK only (ADR 0055). The SDK gains
`HackComponentProviders` (`GetOverviewAsync`, `SetProviderAsync`) and its
contracts (`IAppComponentProviderAdministrationGateway`,
`HackComponentContractInfo`, `HackComponentProviderInfo`), on the model of
`HackPermissionAdministration`. The required capability is **`apps.manage`**, which
Settings already declares: choosing the provider is app administration,
no need for one more capability.

`SetProviderAsync` takes **a list of contracts and an app**: when the app
provides both contracts, Settings offers to set both together
(box checked by default), and a single approval covers both.

### 4. Approval reuses the dangerous capabilities window

For a non-system app, the OS shows the approval window of ADR 0049
(`IDangerousCapabilityApprovalPrompt`): the candidate app, and one line per
contract with its title and its risk sentence (core resources
`Capability.hackeros.file-view.*` and `Capability.hackeros.file-dialogs.*`).
Only a verified administrator attempt writes the file;
"Deny" or closing writes nothing and remembers nothing (no refusal retained as
for a capability). For a system app or the return to the
explorer, the authentication agent (ADR 0055 §5) only asks for an
administrator.

Each decision is written to `/var/log/auth.log` with the action
**`component.provider`** (subject `/etc/hackeros/components.config`, properties
`contracts`, `provider` (`default` for the return to the explorer) and
`user`). Password failures and the refusal are also there, as `Denied`.

### 5. The fallback is logged once

`HackComponentHost` puts the chosen provider first. If it is disabled for
the user, does not load, or is not an embeddable component, the
next one (the explorer) takes over and `component.fallback` is written as a
warning in `/var/log/syslog` (source `components`), **once per
contract, chosen app and provider used** for the duration of the session:
each displayed control would otherwise repeat the same line.

### 6. Only an uninstall for the machine removes the line

ADR 0156 §4 says that an uninstall removes the line. The file applies to
the whole machine: only an uninstall **for the machine**
(`UninstalledForMachine`) removes it, written by the OS as an audited system
operation, with `component.provider.removed` in `/var/log/syslog`. An app
uninstalled for a single user keeps its line: that user has the
fallback of §5, the others keep the choice. The removal follows any change of
the installed state (Settings, `apps.config` modified by hand).

### 7. Settings: "OS Components" tab

Settings has no "Applications" group: the page is a separate tab,
"OS Components", placed after "Installed applications". It shows,
for each contract, the apps that provide it (name, identifier, system
app or not, default, not installed), the one in service, and the button
"Revert to the file explorer" when a choice exists. The index of the
"Application" tab (ADR 0096 §4) is fixed in passing: it had not
followed the addition of the Memory tab.

## Consequences

- New unshipped APIs (`PublicAPI.Unshipped.txt`) in `AppSdk`
  (`HackComponentProviders`, gateway and records,
  `IAppExecutionContext.ComponentProviderAdministration`), `Platform.Core`
  (`ComponentProviderService`, `ComponentProvidersDocument`,
  `IComponentProviderSelection`) and `Platform.Blazor`
  (`HackComponentHost.Selection`, `ReportWarning`); they will be published with
  SDK 2.0 (work item 6).
- Tests: service (file, validation, approval, refusal, return to the
  explorer, uninstall), gateway (capability, approval window
  rather than password, refusal), host (choice first, fallback logged once,
  disabled app, open contract not concerned, `component.unavailable`),
  catalog, validator and aggregation.

## References

- ADR 0044, 0048, 0049, 0055, 0063, 0096, 0148, 0153, 0156.
- [`embedded-components.md`](../../framework/embedded-components.md),
  [`settings-files.md`](../../framework/settings-files.md).
