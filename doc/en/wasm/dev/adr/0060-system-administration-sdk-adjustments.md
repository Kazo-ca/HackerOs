> [Version française](../../../../fr/wasm/dev/adr/0060-system-administration-sdk-adjustments.md)

# ADR 0060: Adjustments to the system administration API

## Status

Accepted on 2026-10-11.

- **Plan phase:** phase 4: complete SDK and base apps, work item
  `rebuild/4-sdk-system-admin` (ADR 0058).
- **Partially supersedes:** ADR 0056, sections 8, 9 and 11, on the points
  listed below. The rest of ADR 0056 does not change.

## Context

ADR 0056 describes `HackSettings.OpenSystemAsync`, `HackPermissionAdministration`,
`HackServerConnection`, `HackSystem` and `HackStorageDiagnostics`. While
writing them, five points of the ADR did not hold as written:

1. The gateway of `HackSystem` is named `IAppSystemGateway System`. A
   property `System` on `IAppExecutionContext` hides the `System` namespace
   in any class that implements the interface (`System.IO`,
   `System.Text.Json`... no longer resolve without `global::`).
2. `HackPermissionAdministration.SetMachineDecisionAsync` takes a `bool?`
   decision (`null` = "ask again at launch"). A `null` that means
   something reads poorly in the call and in the logs.
3. The ADR does not say who writes the layers of an OS document. The
   `/etc/hackeros` layer belongs to root; the `~/.config/hackeros` layer to
   the user.
4. Settings changes the four appearance keys at once. With one write
   per key, the system layer would ask for four approvals.
5. Diagnostic copies the JSON "through `IAppClipboardGateway`". This
   clipboard is that of the simulated session (ADR 0056 §12): the JSON does not
   leave HackerOS, whereas the export is meant to be pasted into a bug
   report, outside the simulation.

## Decision

1. **`IAppSystemPowerGateway SystemPower`** replaces `IAppSystemGateway System`
   on `IAppExecutionContext`. The public facade stays `HackSystem`.
2. **`HackPermissionDecision { Allow, Deny, Ask }`** replaces `bool?`. `Ask`
   removes the machine decision.
3. **Writes to an OS document:**
   - the user layer is written by the OS on behalf of the session's
     user (even under `sudo`, it is their layer, not root's);
   - the system layer is written with the file rights **of the calling
     app**: `sudo`, or the administrator approval of ADR 0042;
     a refusal gives `UnauthorizedAccessException`;
   - the open documents are listed by `OsSettingsDocumentRegistry`
     (`appearance`, `ui-platform-preference`, `security`, `policy`). The
     policy and the machine decisions stay reserved to
     `HackPermissionAdministration`, which goes through the authentication agent.
4. **`HackSettingsDocument.SetManyAsync(values, layer)`** writes several keys
   of a layer in a single edit of the file, hence a single approval.
5. **Diagnostic copies to the host clipboard** through
   `BrowserClipboard` (`HackerOs.AppSdk.Blazor`, module `clipboard.js`), and
   also offers "Download JSON" and "Save to HackerOS". It
   no longer declares `clipboard.write`.

## Consequences

- Diagnostic remains the only non-system app of the base to declare a
  dangerous capability (`system.storage.inspect`): its first launch
  asks for an administrator, as ADR 0056 intended. The test
  `No_non_system_app_of_the_host_declares_a_dangerous_capability` names it
  as an exception.
- `IAppExecutionContext` receives five default members (`SystemSettings`,
  `PermissionAdministration`, `ServerConnection`, `SystemPower`,
  `StorageDiagnostics`); an implementation that does not provide them throws
  `NotSupportedException` (`UnsupportedAdministrationGateways`).
- The exemption list of the SDK boundary is empty after this work item.

## References

- ADR 0042 (`sudo` and approval), ADR 0044 (settings in files), ADR
  0049 (dangerous capabilities), ADR 0055 §5 (authentication agent), ADR
  0056 §8 to §12.
- [`system-administration.md`](../../framework/system-administration.md).
