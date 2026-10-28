> [Version française](../../../fr/wasm/framework/system-administration.md)

# System administration in the SDK

## Purpose

Gives system apps the administration operations that used to go through internal
services (ADR 0056 §8 to §11, adjusted by ADR 0060):

- read and write the OS settings documents;
- administer dangerous capabilities (policy, machine decisions);
- connect to the optional server and synchronize;
- restart or reset the machine;
- read the raw storage snapshot;
- back up and restore the whole machine (ADR 0099 §1).

Settings and Diagnostics use these APIs and no longer reference Platform
Core or Infrastructure.Browser.

## Architecture

| Layer | Files |
|---|---|
| SDK (contracts, facades) | `Shared/HackerOs.AppSdk/Administration/` (`HackPermissionAdministration`, `HackComponentProviders`, `HackServerConnection`, `HackSystem`, `HackStorageDiagnostics`, `HackScreens` and their contracts), `Shared/HackerOs.AppSdk/Settings/IAppSystemSettingsGateway.cs`, `SettingsDocumentStores.cs` |
| Platform Core (gateways) | `Platform/HackerOs.Platform.Core/Administration/`: `AppSystemSettingsGateway`, `AppPermissionAdministrationGateway`, `AppServerConnectionGateway`, `AppSystemPowerGateway`, `AppStorageDiagnosticsGateway`, `AppSystemBackupGateway`, `AppUserAccountsGateway`, `AppScreensGateway` (over `IScreenControl`, ADR 0184), `AdministratorGate`, `SystemAdministrationGatewayServices` |
| Platform Core (services) | `Power/SystemPowerService.cs` (shutdown sequence, `RestartAfterAsync`), `Backup/SystemBackupService.cs`, `Sessions/LocalPasswordService.cs` (ADR 0120), `Settings/OsSettingsDocumentRegistry.cs`, `Policy/AppPermissionAdministration.cs` (`ApplyMachineDecisionAsync`, `ApplyPolicyAsync`) |
| Ecosystem host | `Administration/BrowserSystemPowerDevice.cs` (`location.reload`, deletion of the IndexedDB database), `Administration/BrowserSystemBackupDevice.cs` (export, download, host picker), `Administration/IndexedDbStorageInspector.cs` (adapter for `IndexedDbInspectorService`) |
| Blazor SDK | `Interop/BrowserClipboard.cs` and `wwwroot/clipboard.js` (host clipboard), `[Obsolete]` since ADR 0065 then removed when SDK 1.0 was frozen (ADR 0114): Diagnostics copies through `HackClipboard`, which the OS copies over to the host ([`clipboard.md`](../dev/clipboard.md)) |

`AppExecutionContextFactory` receives a `SystemAdministrationGatewayServices`
(lazy resolutions; `Passwords` is an `init` property added in 1.1) and creates, for each instance, the seven gateways
with the app's capabilities and principal. `AdministratorGate` calls
the OS authentication agent
([`admin-authentication-agent.md`](../dev/admin-authentication-agent.md)): the app never
sees the password, root through `sudo` goes through without a prompt, and the OS then
acts by itself.

## API

### OS documents

```csharp
await using HackSettingsDocument appearance = await HackSettings.OpenSystemAsync(AppContext, "appearance");
await appearance.SetAsync("accent", "cyan");                               // couche utilisateur
await appearance.SetManyAsync(values, SettingLayer.System);                // /etc : une seule approbation
```

See [`settings-files.md`](settings-files.md#os-documents-hacksettingsopensystemasync-adr-0056-8).

### Permissions (`permissions.manage`)

| Method | Effect | Administrator |
|---|---|---|
| `GetPolicyAsync`, `GetOverviewAsync` | policy and state of each dangerous capability of each app | no |
| `SetPolicyAsync(Prompt / Block)` | writes `/etc/hackeros/security.config`, log `capability.policy` | yes (`permissions.policy`) |
| `SetMachineDecisionAsync(app, capability, Allow / Deny / Ask)` | writes `/etc/hackeros/app-permissions.config`, log `capability.approve` / `capability.revoke` | yes (`permissions.machine-decision`) |
| `RemoveOwnDenialAsync(app, capability)` | removes the user's denial, the question comes back at launch | no |

A system app or a capability not declared by the app is refused **before**
any credentials request.

### Server (`server.connection.manage`)

`GetStateAsync`, `ConnectAsync`, `CreateAccountAsync`, `DisconnectAsync`,
`SyncAsync(domain, direction)`, `GetConflictsAsync`. Domains: `FileSystem`
(push, pull), `Grants` (pull only: `ArgumentException` otherwise),
`FileAssociations` (push, pull). With no server configured: `NotConnected`, and
synchronization does nothing. Network errors become
`HackServerConnectionException`.

`GetRelayPolicyAsync` returns the network relay policy of the connected server
(`HackServerRelayPolicy`: ports, private ranges, quotas, open streams, granted
apps, warnings), or `null` without a server; Settings displays it
in its server section ([`server-network-relay.md`](../dev/server-network-relay.md)).

ADR 0101 (work item `v3/9-sync`): `GetConflictsAsync` reads the pending conflicts
recorded in `~/.local/state/hackeros/sync-conflicts.json`
(`HackSyncConflict` carries `DetectedAt`); `ShowConflictsAsync` opens the OS
conflict dialog; `GetSyncStatusAsync` returns a `HackSyncStatus`
(periodic sync active, paused, online, last run, sent,
received, last error, interval); `SetSyncPausedAsync` pauses or resumes
the user's sync (`~/.config/hackeros/sync.config`);
`SyncNowAsync` launches a full run. Deletions, conflicts and
periodic sync are described in
[`synchronization.md`](../dev/synchronization.md).

### Power (`system.power`)

`HackSystem.RebootAsync` and `HackSystem.FactoryResetAsync` always ask for
an administrator (`system.reboot`, `system.factory-reset`). Sequence of
`SystemPowerService`, like a `shutdown`:

1. audit entry;
2. log flush;
3. session closing (`ISessionService.ShutdownAsync`);
4. for the reset, storage erasure;
5. host restart.

### Storage (`system.storage.inspect`)

`HackStorageDiagnostics.GetSnapshotAsync` returns a `HackStorageSnapshot`
(stores, record count, JSON, resolved paths, links) and
`ToExportJson()`. The snapshot bypasses file permissions: that is
why Diagnostics, a non-system app, asks for an administrator on its first
launch.

`GetWebSiteDatabasesAsync` lists the sites' IndexedDB databases (`hackweb.<id>`:
version, stores, record count, estimated size) and
`DeleteWebSiteDatabaseAsync(name)` deletes one, with the same capability
(ADR 0088 §4, [`asp-hack-site-data.md`](asp-hack-site-data.md)). Diagnostics shows them
under *Website databases*.

### Backup (`system.backup`)

`HackSystemBackup.CreateAsync` downloads a `.hosbak` archive of the whole
machine to the host computer; `RestoreAsync(Merge | Replace)` opens the
host's picker, validates the archive, **then** asks for an administrator,
closes the session, restores and restarts. Creation and restoration are logged in
`/var/log/auth.log`. Details: [`system-backup.md`](../dev/system-backup.md).

### Screens (`screens.manage`)

`HackScreens` (ADR 0184) lists, opens, restores, closes and identifies the
session's secondary screens, puts the calling window's screen in full screen
and asks for the `window-management` permission. A normal capability, with no
administrator: the browser already confines each call to the user's click.
`IsSupportedFor(context)` is false on a host without screens (mobile, tests).
Host side: `ScreenControl` over `ScreenService`
([`screens-settings.md`](../dev/screens-settings.md)).

### Accounts and passwords (`users.manage`)

`HackUserAccounts.ListAsync` and `ChangePasswordAsync` (one's own password,
after the current password) require no capability;
`ResetPasswordAsync` and `RemovePasswordAsync` require `users.manage` and an
administrator (actions `users.password-reset` and `users.password-delete`).
The target account's keyring is set aside. Details:
[`sensitive-settings.md`](../dev/sensitive-settings.md#4-passwords-passwd-and-reset)
and [ADR 0120](../dev/adr/0120-passwd-and-administrator-password-reset.md).

`CreateAsync(new HackNewUserAccount(login, nomAffiché, autorité, motDePasse))`
and `DeleteAsync(login, removeHome)` also require `users.manage` and an
administrator (actions `users.add` and `users.delete`, ADR 0169). Creation
gives the account its own group, and `administrators` for an administrator;
the home folder is born at first sign-in. Deletion refuses the
session's account and the last active administrator, takes with it the account's
passkeys and, with `removeHome`, its `/home/<login>` folder.
`ValidateNewAccount` checks an account before sending (login name, display
name of at most 128 characters, password). `HackUserAccount.PasskeyCount`
gives the number of passkeys of each account.

Session passkeys (ADR 0168), without a capability: `ListPasskeysAsync`,
`AddPasskeyAsync(name)` (the browser asks for the user's presence;
`OperationCanceledException` if the request is closed, `NotSupportedException`
without WebAuthn) and `RemovePasskeyAsync(id)`. `HackPasskey.UnlocksKeyring` says whether
the key opens the keyring. Details: [`passkeys.md`](../dev/passkeys.md).

## Decisions

- See ADR 0060: `SystemPower` property (not `System`),
  `HackPermissionDecision.Ask`, system layer written with the caller's
  rights, `SetManyAsync`, host clipboard for Diagnostics
  (the latter is replaced by ADR 0065: `HackClipboard` and
  `clipboard.write`).
- `IndexedDbInspectorService` stays in Infrastructure.Browser;
  the adapter to the SDK lives in the Ecosystem host.
- The old password path of `AppPermissionAdministration` is kept
  for its tests; apps go through the agent.

## Tests

`Tests/HackerOs.Platform.Core.Tests/Administration/SystemAdministrationGatewayTests.cs`:
OS document read and written without a capability, system layer refused to a
simple user then written under root in one edit, unknown document;
required capabilities; agent refusal without a write; machine decision and
policy logged; system app refused before any request; server
absent; `Grants` pull only; restart refused; reset
in the order close, erase, restart; storage inspection;
backup (capability, host without backup, administrator refusal,
picker before administrator, restore order, failure logged at the next
startup).
`Tests/HackerOs.Platform.Core.Tests/Sessions/LocalPasswordServiceTests.cs`:
adding and removing accounts through the gateway (`users.manage` and
administrator), session passkeys without capability or administrator.
`Tests/HackerOs.Platform.Core.Tests/Sessions/LocalAccountServiceTests.cs`:
groups, reserved or taken names, deletion with or without home folder, refusal for
oneself and for the last administrator.

## Task list

See [`app-sdk.md`](app-sdk.md#task-list-of-work-item-rebuild4-sdk-system-admin).
