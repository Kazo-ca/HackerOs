> [Version française](../../../../fr/wasm/dev/adr/README.md)

# Architecture decisions (ADR)

An accepted ADR is never modified: a new ADR replaces it and says so. The
next free number is given in
[`../../memory/README.md`](../../memory/README.md). The general index
([`../../README.md`](../../README.md)) summarizes the ADRs by phase.

| ADR | Title |
| --- | --- |
| [`0001-target-dotnet-10.md`](0001-target-dotnet-10.md) | ADR 0001: Target .NET 10 |
| [`0002-authority-comes-from-policy.md`](0002-authority-comes-from-policy.md) | ADR 0002: Authority Comes from Trusted Policy |
| [`0003-exact-capability-matching.md`](0003-exact-capability-matching.md) | ADR 0003: Match Capabilities Exactly |
| [`0004-settings-files-are-projections.md`](0004-settings-files-are-projections.md) | ADR 0004: Settings Files Are Canonical Projections |
| [`0005-deterministic-app-dependency-order.md`](0005-deterministic-app-dependency-order.md) | ADR 0005: Deterministic App Dependency Order |
| [`0006-seal-window-component-lifecycle.md`](0006-seal-window-component-lifecycle.md) | ADR 0006: Seal the Window Component Lifecycle |
| [`0007-enforce-collocated-razor-assets.md`](0007-enforce-collocated-razor-assets.md) | ADR 0007: Enforce Collocated Razor Assets |
| [`0008-virtual-filesystem-model.md`](0008-virtual-filesystem-model.md) | ADR 0008: Virtual Filesystem Model |
| [`0009-window-runtime-strategy.md`](0009-window-runtime-strategy.md) | ADR 0009: Purpose-Built Window Runtime |
| [`0010-manifest-json-and-schema.md`](0010-manifest-json-and-schema.md) | ADR 0010: Canonical Manifest JSON and Schema |
| [`0011-settings-scope-layout.md`](0011-settings-scope-layout.md) | ADR 0011: Settings Scope Keys and Projection Paths |
| [`0012-process-and-clock-model.md`](0012-process-and-clock-model.md) | ADR 0012: Deterministic Process, Clock, and Resource Model |
| [`0013-local-user-session.md`](0013-local-user-session.md) | ADR 0013: Local User and Session Model |
| [`0014-shell-grammar-boundary.md`](0014-shell-grammar-boundary.md) | ADR 0014: First-Slice Shell Grammar Boundary |
| [`0015-browser-storage-and-indexeddb-adapter.md`](0015-browser-storage-and-indexeddb-adapter.md) | ADR 0015: Browser Support Baseline and IndexedDB Adapter Approach |
| [`0016-platform-ui-library.md`](0016-platform-ui-library.md) | ADR 0016: Platform UI Library Boundary |
| [`0017-pwa-cache-and-offline-strategy.md`](0017-pwa-cache-and-offline-strategy.md) | ADR 0017: Progressive Web App Caching, Offline Strategy, and Version Migration |
| [`0018-indexeddb-failure-and-recovery-policy.md`](0018-indexeddb-failure-and-recovery-policy.md) | ADR 0018: IndexedDB Failure and Recovery Policy |
| [`0019-sdk-versioning-and-compatibility.md`](0019-sdk-versioning-and-compatibility.md) | ADR 0019: App SDK Versioning, Compatibility, and Deprecation Policy |
| [`0020-editor-framework-and-script-sandbox.md`](0020-editor-framework-and-script-sandbox.md) | ADR 0020: Code Editor Architecture, Framework Selection, and Script Execution Sandbox Policy |
| [`0021-simulated-network-and-browser-rendering.md`](0021-simulated-network-and-browser-rendering.md) | ADR 0021: Simulated Network Contracts and Browser Rendering Model |
| [`0022-multi-monitor-requirement-position.md`](0022-multi-monitor-requirement-position.md) | ADR 0022: Multi-Monitor Requirement Position |
| [`0023-optional-game-domain-and-proxy-fallback.md`](0023-optional-game-domain-and-proxy-fallback.md) | ADR 0023: Optional Game Domain Integration & Network Proxy Fallback |
| [`0024-server-identity-and-device-registration.md`](0024-server-identity-and-device-registration.md) | ADR 0024: Server Identity and Device Registration |
| [`0025-record-synchronization-envelope-and-conflict-model.md`](0025-record-synchronization-envelope-and-conflict-model.md) | ADR 0025: Record Synchronization Envelope, Conflict Model, and Cursor Strategy |
| [`0026-icon-library-support.md`](0026-icon-library-support.md) | ADR 0026: Shared Icon Library Support |
| [`0027-server-hosted-blazor-ui.md`](0027-server-hosted-blazor-ui.md) | ADR 0027: Server-Hosted Blazor UI (Third Host, Single-Tenant Phase) |
| [`0028-client-side-server-connection.md`](0028-client-side-server-connection.md) | ADR 0028: Client-Side Optional-Server Connection and Proxy Bridge |
| [`0029-settings-sync.md`](0029-settings-sync.md) | ADR 0029: Settings Domain Sync (First Client Sync Implementation) |
| [`0030-filesystem-sync.md`](0030-filesystem-sync.md) | ADR 0030: FileSystem Domain Sync |
| [`0031-grants-sync.md`](0031-grants-sync.md) | ADR 0031: Grants Domain Sync (Pull-Only) |
| [`0032-app-enablement-management.md`](0032-app-enablement-management.md) | ADR 0032: App Enablement Management |
| [`0033-appcatalog-and-fileassociations-sync.md`](0033-appcatalog-and-fileassociations-sync.md) | ADR 0033: AppCatalog and FileAssociations Domain Sync |
| [`0034-wire-terminal-command-catalog.md`](0034-wire-terminal-command-catalog.md) | ADR 0034: Wire the Terminal Command Catalog |
| [`0035-nmap-tcp-probe-fallback.md`](0035-nmap-tcp-probe-fallback.md) | ADR 0035: Single-Port TCP Reachability Probe for nmap's Real-Network Fallback |
| [`0036-direct-service-injection-for-server-host.md`](0036-direct-service-injection-for-server-host.md) | ADR 0036: Direct Service Injection for the Server-Hosted UI (Pass N+5) |
| [`0037-reusable-file-view-control.md`](0037-reusable-file-view-control.md) | ADR 0037: `FileView` as the Canonical File-Listing Control |
| [`0038-emitter-authorized-topic-messaging.md`](0038-emitter-authorized-topic-messaging.md) | ADR 0038: Emitter-Authorized Topic Messaging |
| [`0039-directory-open-file-association.md`](0039-directory-open-file-association.md) | ADR 0039: `inode/directory` Media-Type Convention for Directory File Association |
| [`0040-declared-topic-permissions.md`](0040-declared-topic-permissions.md) | ADR 0040: Declared Topic Permissions and Optional Per-Channel Access |
| [`0041-shared-theme-catalog-and-scoped-tokens.md`](0041-shared-theme-catalog-and-scoped-tokens.md) | ADR 0041: Shared Theme Catalog and Scoped CSS Tokens |
| [`0042-sudo-root-elevation.md`](0042-sudo-root-elevation.md) | ADR 0042: `sudo` Root Elevation and the Admin-Approval Prompt |
| [`0043-hack-file-api-and-ambient-context.md`](0043-hack-file-api-and-ambient-context.md) | ADR 0043: `Hack*` file API modeled on System.IO and ambient execution context |
| [`0044-settings-as-files.md`](0044-settings-as-files.md) | ADR 0044: Settings as files, layered `/etc` then `~/.config` |
| [`0045-logs-as-files.md`](0045-logs-as-files.md) | ADR 0045: Logs as text files in `/var/log`, with rotation |
| [`0046-virtual-proc-and-dev-filesystems.md`](0046-virtual-proc-and-dev-filesystems.md) | ADR 0046: Virtual file systems `/proc` and `/dev` |
| [`0047-app-definition-and-generated-manifests.md`](0047-app-definition-and-generated-manifests.md) | ADR 0047: Manifest as a C# class (`AppDefinition`) and generated JSON manifests |
| [`0048-system-apps-and-uninstall.md`](0048-system-apps-and-uninstall.md) | ADR 0048: System apps, machine or user uninstall, and reinstall |
| [`0049-dangerous-capabilities-and-admin-approval.md`](0049-dangerous-capabilities-and-admin-approval.md) | ADR 0049: Dangerous capabilities and administrator approval |
| [`0050-shell-grammar-and-process-environment.md`](0050-shell-grammar-and-process-environment.md) | ADR 0050: Shell grammar and process environment |
| [`0051-unified-commands-project.md`](0051-unified-commands-project.md) | ADR 0051: Grouped commands, with no hard-coded exception |
| [`0052-trash.md`](0052-trash.md) | ADR 0052: Trash |
| [`0053-hack-environment-async-directory-change.md`](0053-hack-environment-async-directory-change.md) | ADR 0053: Asynchronous change of the current directory and reading of the login environment |
| [`0054-shell-exit-status-and-detached-execution.md`](0054-shell-exit-status-and-detached-execution.md) | ADR 0054: `$?` per pipeline, detached shell and synchronous writes in a pipe |
| [`0055-base-apps-on-public-sdk-only.md`](0055-base-apps-on-public-sdk-only.md) | ADR 0055: Base apps on the public SDK only |
| [`0056-sdk-apis-for-system-apps.md`](0056-sdk-apis-for-system-apps.md) | ADR 0056: SDK APIs that replace internal services |
| [`0057-proc-metrics-for-task-manager.md`](0057-proc-metrics-for-task-manager.md) | ADR 0057: System metrics in `/proc` and `HackDriveInfo` |
| [`0058-base-app-features.md`](0058-base-app-features.md) | ADR 0058: Base app features |
| [`0059-sdk-boundary-diagnostic-codes.md`](0059-sdk-boundary-diagnostic-codes.md) | ADR 0059: SDK boundary diagnostic codes |
| [`0060-system-administration-sdk-adjustments.md`](0060-system-administration-sdk-adjustments.md) | ADR 0060: Adjustments to the system administration API |
| [`0061-xterm-terminal-emulator.md`](0061-xterm-terminal-emulator.md) | ADR 0061: xterm.js as the terminal emulator |
| [`0062-code-editor-workbench.md`](0062-code-editor-workbench.md) | ADR 0062: the code editor as a VS Code-style workbench |
| [`0063-embeddable-app-components.md`](0063-embeddable-app-components.md) | ADR 0063: embeddable app components, registered with the OS |
| [`0064-posix-shell-scripts-jobs-and-app-commands.md`](0064-posix-shell-scripts-jobs-and-app-commands.md) | ADR 0064: POSIX shell, scripts, jobs, apps launched by name and Linux commands |
| [`0065-os-clipboard-and-host-bridge.md`](0065-os-clipboard-and-host-bridge.md) | ADR 0065: OS clipboard and text sharing with the host |
| [`0066-optional-resource-accounting.md`](0066-optional-resource-accounting.md) | ADR 0066: optional resource accounting, at build time and at startup |
| [`0067-measured-cpu-time.md`](0067-measured-cpu-time.md) | ADR 0067: CPU time measured per process and "Not responding" state |
| [`0068-measured-process-memory.md`](0068-measured-process-memory.md) | ADR 0068: memory measured per process, in layers |
| [`0069-graceful-stop-and-checkpoints.md`](0069-graceful-stop-and-checkpoints.md) | ADR 0069: requested stop, voluntary checkpoints and escalation |
| [`0070-worker-hosted-processes.md`](0070-worker-hosted-processes.md) | ADR 0070: headless processes in Web Workers |
| [`0071-always-ready-worker.md`](0071-always-ready-worker.md) | ADR 0071: always one worker ready for the next request |
| [`0072-worker-processes-implementation-adjustments.md`](0072-worker-processes-implementation-adjustments.md) | ADR 0072: adjustments to the implementation of processes in Web Workers |
| [`0073-web-apps.md`](0073-web-apps.md) | ADR 0073: web apps (`Web` kind), external sites and hosted sites |
| [`0074-listening-ports-and-name-resolution.md`](0074-listening-ports-and-name-resolution.md) | ADR 0074: listening ports, OS DNS resolver and real HTTP bodies |
| [`0075-browser-html-rendering.md`](0075-browser-html-rendering.md) | ADR 0075: browser HTML rendering in an isolated iframe |
| [`0076-asp-hack-mvc.md`](0076-asp-hack-mvc.md) | ADR 0076: ASP.Hack MVC, pipeline, routing and views |
| [`0077-hosted-static-sites.md`](0077-hosted-static-sites.md) | ADR 0077: hosted static sites, `hackwebd` service and Web Server Manager |
| [`0078-detached-display-windows.md`](0078-detached-display-windows.md) | ADR 0078: detached display windows (preparation for the second screen) |
| [`0079-browser-html-rendering-adjustments.md`](0079-browser-html-rendering-adjustments.md) | ADR 0079: adjustments to the browser HTML rendering |
| [`0080-hosted-static-sites-adjustments.md`](0080-hosted-static-sites-adjustments.md) | ADR 0080: adjustments to hosted static sites |
| [`0081-detached-display-windows-adjustments.md`](0081-detached-display-windows-adjustments.md) | ADR 0081: detached windows, implementation adjustments |
| [`0082-asp-hack-route-source-generator.md`](0082-asp-hack-route-source-generator.md) | ADR 0082: ASP.Hack route table produced by a Roslyn source generator |
| [`0083-asp-hack-razor-views.md`](0083-asp-hack-razor-views.md) | ADR 0083: ASP.Hack `.razor` views, anti-forgery and embedded `wwwroot` |
| [`0084-blazor-sites.md`](0084-blazor-sites.md) | ADR 0084: interactive Blazor sites drawn by the browser |
| [`0085-default-simulated-network-in-the-os.md`](0085-default-simulated-network-in-the-os.md) | ADR 0085: the default simulated network is wired into the OS |
| [`0086-asp-hack-cshtml-views.md`](0086-asp-hack-cshtml-views.md) | ADR 0086: ASP.Hack `.cshtml` views compiled by the Razor compiler, without ASP.NET MVC |
| [`0087-asp-hack-intranet-sample-adjustments.md`](0087-asp-hack-intranet-sample-adjustments.md) | ADR 0087: Intranet sample, end-of-web-phase adjustments |
| [`0088-site-data-debug-logs-and-form-history.md`](0088-site-data-debug-logs-and-form-history.md) | ADR 0088: a site's database, debug logs and autocomplete |
| [`0089-explicit-cookie-header.md`](0089-explicit-cookie-header.md) | ADR 0089: `Cookie` header written by the app |
| [`0090-filesystem-path-lookup-in-one-request.md`](0090-filesystem-path-lookup-in-one-request.md) | ADR 0090: looking up a path in a single storage request |
| [`0092-game-in-a-separate-repository.md`](0092-game-in-a-separate-repository.md) | ADR 0092: the game lives in a separate repository |
| [`0094-deferred-decisions-status-and-maximum-file-size.md`](0094-deferred-decisions-status-and-maximum-file-size.md) | ADR 0094: status of the deferred decisions of the v3 migration and maximum file size |
| [`0095-window-events-sizes-and-keyboard.md`](0095-window-events-sizes-and-keyboard.md) | ADR 0095: window events for apps, declared sizes and keyboard commands |
| [`0096-added-intents-and-app-defined-intents.md`](0096-added-intents-and-app-defined-intents.md) | ADR 0096: added intents (view, edit, open a URI, app settings) and app-defined intents |
| [`0097-service-health-and-startup.md`](0097-service-health-and-startup.md) | ADR 0097: service state and health, startup at boot or at login, uninstalling an active app |
| [`0098-sdk-versions-packages-and-manifest-checks.md`](0098-sdk-versions-packages-and-manifest-checks.md) | ADR 0098: SDK and platform NuGet versions and packages, manifest checks at load time, templates and SDK 1.0 freeze |
| [`0099-os-backup-and-low-storage.md`](0099-os-backup-and-low-storage.md) | ADR 0099: OS backup and restore, low space and `ENOSPC`, corrupted PWA cache, tested IndexedDB version upgrades |
| [`0100-sensitive-settings-and-protected-write-audit.md`](0100-sensitive-settings-and-protected-write-audit.md) | ADR 0100: sensitive settings (private files, masking, encrypted keyring) and protected write audit |
| [`0101-synchronized-deletions-and-conflicts.md`](0101-synchronized-deletions-and-conflicts.md) | ADR 0101: synchronized deletions, user-resolved conflicts, periodic sync and remaining sync domains |
| [`0102-server-network-relay-tcp-udp-and-policy.md`](0102-server-network-relay-tcp-udp-and-policy.md) | ADR 0102: optional server network relay (HTTP bodies, TCP streams, UDP, verified agreement, policy in `appsettings`) |
| [`0103-hack-paint-image-files-and-theme-editor.md`](0103-hack-paint-image-files-and-theme-editor.md) | ADR 0103: Hack Paint images in the OS files and theme editor (user themes as token files) |
| [`0104-localization.md`](0104-localization.md) | ADR 0104: localization (English and French, per-process `LANG`, `locale.conf`, `.resx` resources) |
| [`0105-accessibility-preferences.md`](0105-accessibility-preferences.md) | ADR 0105: accessibility preferences as files (high contrast, text size, reduced motion) and automated tests |
| [`0106-trimmed-release-publish.md`](0106-trimmed-release-publish.md) | ADR 0106: trimmed Release publication of the Ecosystem host |
| [`0107-service-startup-implementation-adjustments.md`](0107-service-startup-implementation-adjustments.md) | ADR 0107: adjustments to the implementation of service states and startup |
| [`0108-server-relay-implementation-adjustments.md`](0108-server-relay-implementation-adjustments.md) | ADR 0108: adjustments to the implementation of the server relay |
| [`0110-backup-and-storage-implementation-adjustments.md`](0110-backup-and-storage-implementation-adjustments.md) | ADR 0110: adjustments to the implementation of backup, storage space and IndexedDB fixtures |
| [`0111-localization-implementation-adjustments.md`](0111-localization-implementation-adjustments.md) | ADR 0111: adjustments to the implementation of localization |
| [`0112-accessibility-implementation-adjustments.md`](0112-accessibility-implementation-adjustments.md) | ADR 0112: adjustments to the implementation of accessibility |
| [`0113-trimming-implementation-adjustments.md`](0113-trimming-implementation-adjustments.md) | ADR 0113: adjustments to the trimmed publication |
| [`0114-sdk-1-0-freeze.md`](0114-sdk-1-0-freeze.md) | ADR 0114: SDK 1.0 freeze, scope of the compatibility promise and compatibility app |
| [`0116-window-geometry-restore.md`](0116-window-geometry-restore.md) | ADR 0116: window geometry saved and restored |
| [`0117-sdk-window-title-and-sdk-1-1.md`](0117-sdk-window-title-and-sdk-1-1.md) | ADR 0117: window title set by the app and publication of SDK 1.1 |
| [`0120-passwd-and-administrator-password-reset.md`](0120-passwd-and-administrator-password-reset.md) | ADR 0120: `passwd`, password reset by an administrator and effect on the keyring |
| [`0121-user-file-associations.md`](0121-user-file-associations.md) | ADR 0121: per-user file and scheme associations |
| [`0122-window-size-only-not-position.md`](0122-window-size-only-not-position.md) | ADR 0122: a window remembers its size, not its position |
| [`0123-window-title-implementation-adjustments.md`](0123-window-title-implementation-adjustments.md) | ADR 0123: adjustments to the implementation of the window title |
| [`0124-remote-event-and-watch-gateways.md`](0124-remote-event-and-watch-gateways.md) | ADR 0124: remote event and file watching gateways, pool open to any app assembly |
| [`0125-process-resource-limits.md`](0125-process-resource-limits.md) | ADR 0125: per-process resource limits (`setrlimit`, `ulimit`, `limits.conf`) |
| [`0126-oom-killer.md`](0126-oom-killer.md) | ADR 0126: OOM killer disabled by default and real-browser memory guard |
| [`0127-priority-and-not-responding-dialog.md`](0127-priority-and-not-responding-dialog.md) | ADR 0127: `nice` priority without quantum, and "The application is not responding: Wait | End the application" dialog |
| [`0128-background-jobs-hack-background.md`](0128-background-jobs-hack-background.md) | ADR 0128: heavy background tasks (`HackBackground.RunAsync`) |
| [`0129-window-rendering-in-worker-trial.md`](0129-window-rendering-in-worker-trial.md) | ADR 0129: trial of rendering a Blazor window in a worker, and criteria for going ahead |
| [`0130-server-serves-webassembly-os-and-api.md`](0130-server-serves-webassembly-os-and-api.md) | ADR 0130: `HackerOs.Server` serves the OS in WebAssembly and keeps only its API |
| [`0131-cross-origin-isolation.md`](0131-cross-origin-isolation.md) | ADR 0131: optional cross-origin isolation (COOP/COEP), shared pipes and fine-grained measurements |
| [`0132-exec-series-sdk-1-2-and-merge-rules.md`](0132-exec-series-sdk-1-2-and-merge-rules.md) | ADR 0132: `exec` series: SDK 1.2 additions, branches and merge bar |
| [`0133-window-rendering-in-worker-verdict.md`](0133-window-rendering-in-worker-verdict.md) | ADR 0133: rendering a window in a worker, verdict of the trial: we stop |
| [`0134-automatic-server-connection-and-status-icon.md`](0134-automatic-server-connection-and-status-icon.md) | ADR 0134: automatic connection to the server that serves the OS, and status icon in the notification area |
| [`0135-no-game-in-this-repository.md`](0135-no-game-in-this-repository.md) | ADR 0135: no game in this repository |
| [`0136-sudo-nice.md`](0136-sudo-nice.md) | ADR 0136: `sudo nice` runs the command as root |
| [`0137-server-hosting-implementation-adjustments.md`](0137-server-hosting-implementation-adjustments.md) | ADR 0137: adjustments to the implementation of the OS served by `HackerOs.Server` |
| [`0138-remote-gateways-implementation-adjustments.md`](0138-remote-gateways-implementation-adjustments.md) | ADR 0138: adjustments to the implementation of ADR 0124 (remote gateways) |
| [`0139-process-limits-implementation-adjustments.md`](0139-process-limits-implementation-adjustments.md) | ADR 0139: adjustments to the implementation of process limits |
| [`0140-background-jobs-implementation-adjustments.md`](0140-background-jobs-implementation-adjustments.md) | ADR 0140: adjustments to the implementation of ADR 0128 (background tasks) |
| [`0142-oom-killer-implementation-adjustments.md`](0142-oom-killer-implementation-adjustments.md) | ADR 0142: adjustments to the implementation of the OOM killer |
| [`0143-cross-origin-isolation-implementation-adjustments.md`](0143-cross-origin-isolation-implementation-adjustments.md) | ADR 0143: adjustments to the implementation of cross-origin isolation |
| [`0145-publish-async-from-a-worker.md`](0145-publish-async-from-a-worker.md) | ADR 0145: `PublishAsync`, the real result of a publication from a worker |
| [`0146-cross-origin-isolation-sdk-api.md`](0146-cross-origin-isolation-sdk-api.md) | ADR 0146: `HackCrossOriginIsolation`, the isolation state in the SDK |
| [`0147-background-task-grace-period-on-app-close.md`](0147-background-task-grace-period-on-app-close.md) | ADR 0147: 5 s grace period for background tasks when their app closes |
| [`0148-sdk-contracts-and-os-provided-controls.md`](0148-sdk-contracts-and-os-provided-controls.md) | ADR 0148: the SDK carries only contracts; the OS provides the shared controls and dialogs |
| [`0149-sdk-2-0-file-view-removal.md`](0149-sdk-2-0-file-view-removal.md) | ADR 0149: SDK 2.0, removal of `HackerOs.AppSdk.FileView` and file contracts |
| [`0150-os-context-menu.md`](0150-os-context-menu.md) | ADR 0150: central OS context menu (`hackeros.context-menu`) |
| [`0151-app-registered-file-menu-entries.md`](0151-app-registered-file-menu-entries.md) | ADR 0151: file menu entries registered by apps |
| [`0152-desktop-icons.md`](0152-desktop-icons.md) | ADR 0152: desktop icons |
| [`0153-contract-host-implementation-adjustments.md`](0153-contract-host-implementation-adjustments.md) | ADR 0153: adjustments to the generic contract host |
| [`0154-context-menu-implementation-adjustments.md`](0154-context-menu-implementation-adjustments.md) | ADR 0154: adjustments to the OS context menu |
| [`0155-file-control-implementation-adjustments.md`](0155-file-control-implementation-adjustments.md) | ADR 0155: adjustments to the file control provided by the explorer |
| [`0156-replaceable-file-control-provider.md`](0156-replaceable-file-control-provider.md) | ADR 0156: file control provider replaceable by a third-party app |
| [`0157-file-dialogs-show-the-file-control.md`](0157-file-dialogs-show-the-file-control.md) | ADR 0157: the Open / Save / Choose a folder dialogs show the file control |
| [`0158-replaceable-file-provider-implementation-adjustments.md`](0158-replaceable-file-provider-implementation-adjustments.md) | ADR 0158: adjustments to the replaceable file provider |
| [`0159-app-menu-entries-implementation-adjustments.md`](0159-app-menu-entries-implementation-adjustments.md) | ADR 0159: app menu entries, implementation adjustments |
| [`0160-desktop-implementation-adjustments.md`](0160-desktop-implementation-adjustments.md) | ADR 0160: desktop, implementation adjustments |
| [`0161-sdk-2-0-implementation-adjustments.md`](0161-sdk-2-0-implementation-adjustments.md) | ADR 0161: SDK 2.0, implementation adjustments |
| [`0162-restart-after-a-host-build-change.md`](0162-restart-after-a-host-build-change.md) | ADR 0162: restart when the build changed under the page |
| [`0163-window-pointer-preview-and-invariant-coordinates.md`](0163-window-pointer-preview-and-invariant-coordinates.md) | ADR 0163: move preview and invariant coordinates |
| [`0164-app-icons-and-window-icon-context-menu.md`](0164-app-icons-and-window-icon-context-menu.md) | ADR 0164: app icons and the window icon context menu |
| [`0165-mobile-home-drawer-and-mouse-controls.md`](0165-mobile-home-drawer-and-mouse-controls.md) | ADR 0165: mobile home, drawer and mouse aids |
| [`0166-mobile-layout-conf-on-desktop.md`](0166-mobile-layout-conf-on-desktop.md) | ADR 0166: mobile layout as a conf file under Desktop |
| [`0167-combined-pages-deployment.md`](0167-combined-pages-deployment.md) | ADR 0167: combined Pages publication and independent builds |
| [`0168-passkey-sign-in.md`](0168-passkey-sign-in.md) | ADR 0168: sign-in with a passkey (WebAuthn) and keyring unlock |
| [`0169-administrator-adds-and-removes-users.md`](0169-administrator-adds-and-removes-users.md) | ADR 0169: an administrator adds and removes accounts |
| [`0170-settings-side-menu.md`](0170-settings-side-menu.md) | ADR 0170: collapsible side menu in Settings |
| [`0171-mobile-file-explorer-and-full-screen-dialogs.md`](0171-mobile-file-explorer-and-full-screen-dialogs.md) | ADR 0171: per-platform entry point, mobile file explorer, full-screen dialogs on mobile, Start menu alignment |
| [`0173-automatic-releases-from-the-version-file.md`](0173-automatic-releases-from-the-version-file.md) | ADR 0173: automatic release (tag, GitHub Release, nuget.org) from the version file |
| [`0174-announce-a-new-version-to-open-sessions.md`](0174-announce-a-new-version-to-open-sessions.md) | ADR 0174: `version.json` read every 5 minutes to announce a new version to open sessions |
| [`0175-new-folder-in-the-choose-folder-dialog.md`](0175-new-folder-in-the-choose-folder-dialog.md) | ADR 0175: "New folder" button in the toolbar of the Choose folder and Save dialogs; turned on for Code Editor's "Open Folder" |
| [`0176-app-repository-template.md`](0176-app-repository-template.md) | ADR 0176: app repository template generated by `hackeros-solution`, OS submodule (source in `Debug`, nuget.org packages in `Release`), templates shown in Visual Studio |
| [`0177-title-bar-of-maximized-and-snapped-windows.md`](0177-title-bar-of-maximized-and-snapped-windows.md) | ADR 0177: title bar of maximized and snapped windows (buttons above the resize handles, snapping keeps the earlier geometry, drag to restore, double click to maximize) |
| [`0178-extended-screens-in-display-surfaces.md`](0178-extended-screens-in-display-surfaces.md) | ADR 0178: multi-monitor extended screens, one screen per display surface, `DisplayId` and work area per screen in `WindowRuntime`, windows moved through a portal, screen service (Chrome/Edge with `window-management`, fallback elsewhere) |
| [`0179-per-screen-taskbars-and-other-screen-menus.md`](0179-per-screen-taskbars-and-other-screen-menus.md) | ADR 0179: one taskbar per screen (`primary-taskbar` setting), "Other screen" submenu on the icon and the Maximize button, identification veil, `screens.config` and Settings ▸ Screens |
| [`0180-screen-service-placement-and-memory.md`](0180-screen-service-placement-and-memory.md) | ADR 0180: screen service, monitor chosen before `window.open`, move after the permission, place read every second, `[screens]` with `open` and one place per screen, "Restore screens" |
| [`0181-host-folder-mounted-as-a-network-drive.md`](0181-host-folder-mounted-as-a-network-drive.md) | ADR 0181: a folder of the computer (File System Access API, Chrome and Edge) mounted as a network drive under `/mnt`, `mount` and `umount` commands |
| [`0182-install-apps-from-github.md`](0182-install-apps-from-github.md) | ADR 0182: `hackpkg` command that installs an app from its GitHub repository, `.hackapp` packages on the `hackeros-packages` branch, GitHub (`raw.githubusercontent.com`) as a trusted host in `/etc/hackeros/app-sources.config` |
| [`0183-drag-a-window-between-screens.md`](0183-drag-a-window-between-screens.md) | ADR 0183: drag a window from one screen to another by its title bar, screen under the pointer found from its position on the physical desktop, outline on the target screen, screen changed on release |
| [`0184-screens-administration-sdk.md`](0184-screens-administration-sdk.md) | ADR 0184: system screens API `HackScreens` for Settings ▸ Screens (normal capability `screens.manage`), list, open, restore, close, identify, full screen, permission |
| [`0187-guest-start-and-sign-in-credits.md`](0187-guest-start-and-sign-in-credits.md) | ADR 0187: start as a guest without an account (password-less administrator guest, only when no account exists, never recreated; "Create my account" migrates or deletes the guest) and credits on the sign-in page (author Benoit Robin → kazo.ca, GitHub project Kazo-ca/HackerOs); credits implemented, guest to be implemented later |
| [`0188-hack-store-and-app-catalog.md`](0188-hack-store-and-app-catalog.md) | ADR 0188: Hack Store system app (pinned by default, updates, v1 without ratings or reviews), `HackerOs-AppMarketPlace` catalog repository with one reviewed folder per app version, built by our CI from the commit, `packages` branch and `catalog.json`, action for app authors |
| [`0189-app-sources-trust-and-per-user-install.md`](0189-app-sources-trust-and-per-user-install.md) | ADR 0189: trusted app sources (built into the OS or added by an administrator), per-user install without an administrator, `app-policy.config` block list, two-step validation of system-wide installs; revises ADR 0182 Q4 |
| [`0190-theme-packs.md`](0190-theme-packs.md) | ADR 0190: Hack Store v2, code-free `.hacktheme` packs (themes, wallpapers, icons, sounds, cursors), and the OS systems to build first |
| [`0191-installable-local-server.md`](0191-installable-local-server.md) | ADR 0191: installable HackerOS server (Windows service, systemd, launchd, Docker) on the computer then on the local network over HTTPS, secure defaults (`Host`, local listening, GitHub Pages CORS), publishing by `release.yml`, Settings "Local server" section with pairing |
| [`0185-file-explorer-improvements.md`](0185-file-explorer-improvements.md) | ADR 0185: File Explorer improvements (resizable panel, empty folder inside the list, drop outside a zone does nothing, drag out of the browser to download, `.zip` browsed read-only, Compress and Send to ▸, folder previews, network drive refresh, hover tooltips), to implement later in the `explorer/base` series |
| [`0186-whats-new-change-list.md`](0186-whats-new-change-list.md) | ADR 0186: change list, one file `changes/unreleased/<guid>.json` per change (category, user/developer audience, English and French texts), grouped by version; what's new since the last visit in the OS, Release and NuGet notes, PR check with the `no-changelog` label |
| [`0192-features-overview-and-ported-apps.md`](0192-features-overview-and-ported-apps.md) | ADR 0192: features overview (`features.md`), "In development" section kept current by every PR to `main`, and exception to ADR 0135 §2 to name the apps ported in their own repositories |
| [`0193-local-time-and-time-zone.md`](0193-local-time-and-time-zone.md) | ADR 0193: local time and time zone, `timeZone` of `datetime.config` (`auto` = the browser's zone), `SessionTimeZoneService` applied at once, `IAppExecutionContext.TimeZone` and `HackTimeZone` in the SDK (`TZ` wins), clock, `date`, `ls -l`, `dmesg`, Settings ▸ Date and time; logs in UTC |

Parent folder index: [`../README.md`](../README.md).
