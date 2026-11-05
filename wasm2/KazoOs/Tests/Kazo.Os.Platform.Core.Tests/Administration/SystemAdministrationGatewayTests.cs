using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Administration;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.AppSdk.Settings;
using Kazo.Os.Platform.Core.Administration;
using Kazo.Os.Platform.Core.Backup;
using Kazo.Os.Platform.Core.Diagnostics;
using Kazo.Os.Platform.Core.Discovery;
using Kazo.Os.Platform.Core.Elevation;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Platform.Core.Policy;
using Kazo.Os.Platform.Core.Power;
using Kazo.Os.Platform.Core.Sessions;
using Kazo.Os.Platform.Core.Tests.Settings;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Simulation.Abstractions.Settings;

namespace Kazo.Os.Platform.Core.Tests.Administration;

/// <summary>
/// Contract tests of the system-administration gateways (ADR 0056, sections 8 to 11; chantier
/// 4-sdk-system-admin): <c>KazoSettings.OpenSystemAsync</c>, <c>KazoPermissionAdministration</c>,
/// <c>KazoServerConnection</c>, <c>KazoSystem</c> and <c>KazoStorageDiagnostics</c>.
/// </summary>
public sealed class SystemAdministrationGatewayTests
{
    private const string ScannerId = "org.example.scanner";
    private const string SettingsId = "org.kazoos.settings";
    private const string SystemToolId = "org.kazoos.system-tool";
    private const string AppearanceSystemFile = "/etc/kazoos/appearance.config";
    private const string AppearanceUserFile = "/home/alice/.config/kazoos/appearance.config";
    private const string MachinePermissionsFile = "/etc/kazoos/app-permissions.config";
    private const string SecurityFile = "/etc/kazoos/security.config";

    // ---- KazoSettings.OpenSystemAsync (section 8) ----

    [Fact]
    public async Task An_os_document_reads_both_layers_and_writes_the_users_own_layer_without_any_capability()
    {
        SettingsFilesHarness files = new();
        await files.BootAsync();
        AuthenticatedPrincipal alice = await files.LoginAsync(files.Alice);
        await files.WriteAsRootAsync(AppearanceSystemFile, "accent=cyan\n");
        SettingsContext app = new(files, alice);

        await using KazoSettingsDocument document = await KazoSettings.OpenSystemAsync(app, "appearance");
        Assert.Equal("cyan", document.Get<string>("accent"));
        Assert.Equal(SettingLayer.System, document.GetSource("accent"));

        await document.SetAsync("animationsEnabled", false);

        Assert.Contains("animationsEnabled=false", await files.ReadAsync(AppearanceUserFile));
        Assert.False(document.Get<bool>("animationsEnabled"));
        Assert.DoesNotContain("animationsEnabled", await files.ReadAsync(AppearanceSystemFile));
    }

    [Fact]
    public async Task The_system_layer_is_written_with_the_callers_own_file_rights()
    {
        SettingsFilesHarness files = new();
        await files.BootAsync();
        AuthenticatedPrincipal alice = await files.LoginAsync(files.Alice);

        await using KazoSettingsDocument plain = await KazoSettings.OpenSystemAsync(new SettingsContext(files, alice), "appearance");
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(
            () => plain.SetAsync("accent", "cyan", SettingLayer.System).AsTask());
        Assert.DoesNotContain("accent=cyan", await files.ReadAsync(AppearanceSystemFile) ?? string.Empty);

        // The same app run under sudo (root's file rights) writes /etc in one edit.
        await using KazoSettingsDocument root = await KazoSettings.OpenSystemAsync(
            new SettingsContext(files, alice, files.CreateRootGateway()), "appearance");
        await root.SetManyAsync(
            new Dictionary<string, object> { ["accent"] = "cyan", ["animationsEnabled"] = false },
            SettingLayer.System);

        string etc = await files.ReadAsync(AppearanceSystemFile) ?? string.Empty;
        Assert.Contains("accent=cyan", etc);
        Assert.Contains("animationsEnabled=false", etc);
    }

    [Fact]
    public async Task An_unknown_os_document_is_refused()
    {
        SettingsFilesHarness files = new();
        await files.BootAsync();
        AuthenticatedPrincipal alice = await files.LoginAsync(files.Alice);

        await Assert.ThrowsAsync<ArgumentException>(
            () => KazoSettings.OpenSystemAsync(new SettingsContext(files, alice), "passwords").AsTask());
    }

    // ---- KazoPermissionAdministration (section 9) ----

    [Fact]
    public async Task Permission_administration_requires_permissions_manage()
    {
        AdminHarness harness = await AdminHarness.CreateAsync();
        AppPermissionAdministrationGateway gateway = harness.CreatePermissionGateway(new CountingAgent(succeed: true));

        await Assert.ThrowsAsync<AppGatewayAccessDeniedException>(() => gateway.GetOverviewAsync().AsTask());
        await Assert.ThrowsAsync<AppGatewayAccessDeniedException>(
            () => gateway.SetPolicyAsync(KazoDangerousCapabilityPolicy.Block).AsTask());
    }

    [Fact]
    public async Task A_refused_administrator_prompt_writes_nothing()
    {
        AdminHarness harness = await AdminHarness.CreateAsync();
        CountingAgent agent = new(succeed: false);
        AppPermissionAdministrationGateway gateway = harness.CreatePermissionGateway(agent, AppCapabilities.PermissionsManage);

        await Assert.ThrowsAsync<KazoUnauthorizedAccessException>(() => gateway.SetMachineDecisionAsync(
            ScannerId, AppCapabilities.NetworkRealAccess, KazoPermissionDecision.Allow).AsTask());
        await Assert.ThrowsAsync<KazoUnauthorizedAccessException>(
            () => gateway.SetPolicyAsync(KazoDangerousCapabilityPolicy.Block).AsTask());

        Assert.Equal(2, agent.Calls);
        Assert.Equal("permissions.policy", agent.LastRequest!.Action);
        Assert.DoesNotContain(ScannerId, await harness.Files.ReadAsync(MachinePermissionsFile) ?? string.Empty);
        Assert.Equal(KazoDangerousCapabilityPolicy.Prompt, await gateway.GetPolicyAsync());
    }

    [Fact]
    public async Task An_authenticated_administrator_decides_for_the_machine_and_is_audited()
    {
        AdminHarness harness = await AdminHarness.CreateAsync();
        CountingAgent agent = new(succeed: true);
        AppPermissionAdministrationGateway gateway = harness.CreatePermissionGateway(agent, AppCapabilities.PermissionsManage);

        await gateway.SetMachineDecisionAsync(ScannerId, AppCapabilities.NetworkRealAccess, KazoPermissionDecision.Deny);

        Assert.Equal("permissions.machine-decision", agent.LastRequest!.Action);
        Assert.Contains($"{ScannerId}:{AppCapabilities.NetworkRealAccess}=deny", await harness.Files.ReadAsync(MachinePermissionsFile));
        KazoAppPermissionInfo scanner = Assert.Single(await gateway.GetOverviewAsync(), app => app.AppId == ScannerId);
        Assert.Equal(
            KazoPermissionState.DeniedByAdministrator,
            scanner.Capabilities.Single(capability => capability.Capability == AppCapabilities.NetworkRealAccess).State);

        await gateway.SetPolicyAsync(KazoDangerousCapabilityPolicy.Block);

        Assert.Equal("permissions.policy", agent.LastRequest!.Action);
        Assert.Contains("block", await harness.Files.ReadAsync(SecurityFile));
        Assert.Equal(KazoDangerousCapabilityPolicy.Block, await gateway.GetPolicyAsync());
        AuditEntry revoke = Assert.Single(harness.Audit.Entries, entry => entry.Action == "capability.revoke");
        Assert.Equal("admin", revoke.Properties["admin"]);
        Assert.Single(harness.Audit.Entries, entry => entry.Action == "capability.policy");
    }

    [Fact]
    public async Task A_system_app_or_an_undeclared_capability_is_refused_before_any_prompt()
    {
        AdminHarness harness = await AdminHarness.CreateAsync();
        CountingAgent agent = new(succeed: true);
        AppPermissionAdministrationGateway gateway = harness.CreatePermissionGateway(agent, AppCapabilities.PermissionsManage);

        await Assert.ThrowsAnyAsync<Exception>(() => gateway.SetMachineDecisionAsync(
            SystemToolId, AppCapabilities.ProcessManage, KazoPermissionDecision.Deny).AsTask());
        await Assert.ThrowsAnyAsync<Exception>(() => gateway.SetMachineDecisionAsync(
            ScannerId, AppCapabilities.SystemPower, KazoPermissionDecision.Allow).AsTask());

        Assert.Equal(0, agent.Calls);
    }

    // ---- KazoServerConnection (section 10) ----

    [Fact]
    public async Task Without_server_support_the_connection_is_absent_and_sync_is_a_no_op()
    {
        AppServerConnectionGateway gateway = new(services: null, new FixedCapabilities(AppCapabilities.ServerConnectionManage));

        Assert.Equal(KazoServerConnectionState.NotConnected, await gateway.GetStateAsync());
        await gateway.SyncAsync(KazoSyncDomain.FileSystem, KazoSyncDirection.Push);
        Assert.Empty(await gateway.GetConflictsAsync());
        await Assert.ThrowsAsync<KazoServerConnectionException>(
            () => gateway.ConnectAsync(new Uri("https://example.test"), "alice", "pw").AsTask());
    }

    [Fact]
    public async Task Server_connection_requires_its_capability_and_grants_are_pull_only()
    {
        AppServerConnectionGateway denied = new(services: null, new FixedCapabilities());
        AppServerConnectionGateway granted = new(services: null, new FixedCapabilities(AppCapabilities.ServerConnectionManage));

        await Assert.ThrowsAsync<AppGatewayAccessDeniedException>(() => denied.GetStateAsync().AsTask());
        await Assert.ThrowsAsync<ArgumentException>(
            () => granted.SyncAsync(KazoSyncDomain.Grants, KazoSyncDirection.Push).AsTask());
    }

    // ---- KazoSystem and KazoStorageDiagnostics (section 11) ----

    [Fact]
    public async Task Reboot_requires_system_power_and_an_administrator()
    {
        AdminHarness harness = await AdminHarness.CreateAsync();
        RecordingDevice device = new();
        CountingAgent refusing = new(succeed: false);

        await Assert.ThrowsAsync<AppGatewayAccessDeniedException>(
            () => harness.CreatePowerGateway(device, new CountingAgent(succeed: true)).RebootAsync().AsTask());
        await Assert.ThrowsAsync<KazoUnauthorizedAccessException>(
            () => harness.CreatePowerGateway(device, refusing, AppCapabilities.SystemPower).RebootAsync().AsTask());

        Assert.Equal("system.reboot", refusing.LastRequest!.Action);
        Assert.Empty(device.Calls);
        Assert.Equal(SessionState.Active, harness.Files.Session.State);
    }

    [Fact]
    public async Task Factory_reset_closes_the_session_then_erases_then_restarts()
    {
        AdminHarness harness = await AdminHarness.CreateAsync();
        RecordingDevice device = new();
        CountingAgent agent = new(succeed: true);

        await harness.CreatePowerGateway(device, agent, AppCapabilities.SystemPower).FactoryResetAsync();

        Assert.Equal("system.factory-reset", agent.LastRequest!.Action);
        Assert.Equal(["erase", "restart"], device.Calls);
        Assert.NotEqual(SessionState.Active, harness.Files.Session.State);
        AuditEntry entry = Assert.Single(harness.Audit.Entries, audit => audit.Action == "system.factory-reset");
        Assert.Equal("admin", entry.Properties["admin"]);
    }

    [Fact]
    public async Task Factory_reset_from_the_sign_in_screen_erases_then_restarts_and_audits_its_origin()
    {
        AdminHarness harness = await AdminHarness.CreateAsync();
        RecordingDevice device = new();
        SystemPowerService power = new(device, harness.Files.Session, auditLog: harness.Audit);

        await power.FactoryResetFromSignInAsync();

        // ADR 0226: nobody signed in and no administrator: the entry names the sign-in screen.
        Assert.Equal(["erase", "restart"], device.Calls);
        AuditEntry entry = Assert.Single(harness.Audit.Entries, audit => audit.Action == "system.factory-reset");
        Assert.Equal("sign-in", entry.Properties["origin"]);
        Assert.Equal("-", entry.Properties["admin"]);
        Assert.Equal("-", entry.Properties["requestedBy"]);
    }

    [Fact]
    public async Task The_operation_shown_to_the_administrator_follows_the_calling_process_language()
    {
        AdminHarness harness = await AdminHarness.CreateAsync();
        RecordingDevice device = new();
        CountingAgent agent = new(succeed: true);
        AdministratorGate french = new(
            () => agent, harness.Catalog.Manifests[SettingsId], harness.Principal, isRootElevated: false,
            language: () => System.Globalization.CultureInfo.GetCultureInfo("fr"));
        AppSystemPowerGateway gateway = new(
            () => new SystemPowerService(device, harness.Files.Session, auditLog: harness.Audit),
            new FixedCapabilities(AppCapabilities.SystemPower),
            french);

        await gateway.FactoryResetAsync();

        // ADR 0104: the prompt text is French; the audited action id stays stable.
        Assert.Equal("Effacer cette machine (tous les comptes, fichiers et réglages) et la redémarrer", agent.LastRequest!.Operation);
        Assert.Equal("system.factory-reset", agent.LastRequest.Action);
    }

    [Fact]
    public async Task Storage_inspection_requires_its_capability()
    {
        KazoStorageSnapshot snapshot = new(DateTimeOffset.UnixEpoch, "kazoos", 3, [], new Dictionary<string, string>(), 0);
        AppStorageDiagnosticsGateway denied = new(() => new FixedInspector(snapshot), new FixedCapabilities());
        AppStorageDiagnosticsGateway granted = new(() => new FixedInspector(snapshot), new FixedCapabilities(AppCapabilities.SystemStorageInspect));
        AppStorageDiagnosticsGateway absent = new(null, new FixedCapabilities(AppCapabilities.SystemStorageInspect));

        await Assert.ThrowsAsync<AppGatewayAccessDeniedException>(() => denied.GetSnapshotAsync().AsTask());
        Assert.Same(snapshot, await granted.GetSnapshotAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => absent.GetSnapshotAsync().AsTask());
    }

    // ---- KazoSystemBackup (ADR 0099, section 1) ----

    [Fact]
    public async Task Backup_requires_its_capability_and_a_supporting_host()
    {
        AdminHarness harness = await AdminHarness.CreateAsync();
        BackupDevice device = new();

        await Assert.ThrowsAsync<AppGatewayAccessDeniedException>(
            () => harness.CreateBackupGateway(device, new CountingAgent(succeed: true)).CreateAsync().AsTask());
        AppSystemBackupGateway absent = new(null, new FixedCapabilities(AppCapabilities.SystemBackup), harness.Gate(new CountingAgent(succeed: true)));
        Assert.False(absent.IsSupported);
        await Assert.ThrowsAsync<NotSupportedException>(() => absent.CreateAsync().AsTask());
        Assert.Empty(device.Calls);
    }

    [Fact]
    public async Task A_backup_is_downloaded_to_the_host_and_audited()
    {
        AdminHarness harness = await AdminHarness.CreateAsync();
        BackupDevice device = new();
        CountingAgent agent = new(succeed: true);

        KazoBackupInfo info = await harness.CreateBackupGateway(device, agent, AppCapabilities.SystemBackup).CreateAsync();

        Assert.Equal(SystemBackupService.CreateAction, agent.LastRequest!.Action);
        Assert.Equal("kazoos-20261017-071200Z.kosbak", info.FileName);
        Assert.Equal(["export", "save:kazoos-20261017-071200Z.kosbak"], device.Calls);
        AuditEntry entry = Assert.Single(harness.Audit.Entries, audit => audit.Action == SystemBackupService.CreateAction);
        Assert.Equal("admin", entry.Properties["admin"]);
        Assert.Equal("alice", entry.Properties["requestedBy"]);
        Assert.Equal("7", entry.Properties["records"]);
    }

    [Fact]
    public async Task A_refused_administrator_prompt_downloads_nothing()
    {
        AdminHarness harness = await AdminHarness.CreateAsync();
        BackupDevice device = new();

        await Assert.ThrowsAsync<KazoUnauthorizedAccessException>(
            () => harness.CreateBackupGateway(device, new CountingAgent(succeed: false), AppCapabilities.SystemBackup).CreateAsync().AsTask());

        Assert.Empty(device.Calls);
        Assert.DoesNotContain(harness.Audit.Entries, audit => audit.Action == SystemBackupService.CreateAction);
    }

    [Fact]
    public async Task A_closed_picker_or_an_invalid_file_asks_nobody_and_keeps_the_session()
    {
        AdminHarness harness = await AdminHarness.CreateAsync();
        CountingAgent agent = new(succeed: true);
        BackupDevice closed = new() { Picked = null };
        BackupDevice invalid = new() { Invalid = true };

        Assert.Null(await harness.CreateBackupGateway(closed, agent, AppCapabilities.SystemBackup).RestoreAsync(KazoBackupRestoreMode.Replace));
        await Assert.ThrowsAsync<KazoBackupInvalidException>(
            () => harness.CreateBackupGateway(invalid, agent, AppCapabilities.SystemBackup).RestoreAsync(KazoBackupRestoreMode.Merge).AsTask());

        Assert.Equal(0, agent.Calls);
        Assert.Equal(["pick"], closed.Calls);
        Assert.Equal(["pick"], invalid.Calls);
        Assert.Equal(SessionState.Active, harness.Files.Session.State);
    }

    [Fact]
    public async Task A_restore_picks_first_then_ends_the_session_restores_and_restarts()
    {
        AdminHarness harness = await AdminHarness.CreateAsync();
        BackupDevice device = new();
        CountingAgent agent = new(succeed: true);

        KazoBackupInfo? info = await harness.CreateBackupGateway(device, agent, AppCapabilities.SystemBackup)
            .RestoreAsync(KazoBackupRestoreMode.Replace);

        Assert.Equal("old.kosbak", info!.FileName);
        Assert.Equal(SystemBackupService.RestoreAction, agent.LastRequest!.Action);
        Assert.Contains("Replace", agent.LastRequest.Operation, StringComparison.Ordinal);
        Assert.Equal(["pick", "record:started", "restore:Replace", "record:success", "restart"], device.Calls);
        Assert.NotEqual(SessionState.Active, harness.Files.Session.State);
    }

    [Fact]
    public async Task A_failed_restore_still_restarts_and_the_next_boot_audits_the_failure()
    {
        AdminHarness harness = await AdminHarness.CreateAsync();
        BackupDevice device = new() { FailRestore = true };
        SystemBackupService service = harness.CreateBackupService(device);
        SystemBackupCandidate candidate = (await service.PickAsync())!;

        await service.RestoreAsync(candidate, KazoBackupRestoreMode.Merge, "admin", harness.Principal);
        await service.WritePendingRestoreRecordAsync();
        await service.WritePendingRestoreRecordAsync();

        Assert.Equal("restart", device.Calls[^1]);
        AuditEntry entry = Assert.Single(harness.Audit.Entries, audit => audit.Action == SystemBackupService.RestoreAction);
        Assert.Equal(AuditOutcome.Failed, entry.Outcome);
        Assert.Equal("merge", entry.Properties["mode"]);
        Assert.Equal("alice", entry.Properties["requestedBy"]);
        Assert.StartsWith("failure:", entry.Properties["outcome"], StringComparison.Ordinal);
    }

    private static AppManifest Manifest(string id, params string[] capabilities) => new()
    {
        Id = id,
        Name = id,
        Version = "1.0.0",
        PublisherId = id.StartsWith("org.kazoos", StringComparison.Ordinal) ? "pub.kazoos" : "org.example",
        Description = "System-administration fixture.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.Apps.Sample", "Kazo.Os.Apps.Sample.SampleApp"),
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None,
        Capabilities = capabilities
    };

    /// <summary>Settings (system app) calling the gateways on behalf of alice, with an admin account available.</summary>
    private sealed class AdminHarness
    {
        private AdminHarness(SettingsFilesHarness files, AuthenticatedPrincipal principal)
        {
            Files = files;
            Principal = principal;
            AppCatalogBuildResult built = AppCatalog.Build(
                [
                    Manifest(ScannerId, AppCapabilities.NetworkRealAccess, AppCapabilities.ProcessManage),
                    Manifest(SystemToolId, AppCapabilities.ProcessManage),
                    Manifest(SettingsId, AppCapabilities.PermissionsManage, AppCapabilities.SystemPower)
                ],
                [SystemToolId, SettingsId]);
            Assert.True(built.IsSuccess, string.Join(", ", built.Errors.Select(error => error.Message)));
            Catalog = built.Catalog!;
            Audit = new BoundedAuditLog(maxEntries: 200);
            Administration = new AppPermissionAdministration(
                Catalog, new AppPermissionStore(files.Settings), files.Settings, new AdminElevationVerifier(files.Users, Audit), Audit);
        }

        internal SettingsFilesHarness Files { get; }
        internal AuthenticatedPrincipal Principal { get; }
        internal AppCatalog Catalog { get; }
        internal BoundedAuditLog Audit { get; }
        internal AppPermissionAdministration Administration { get; }

        internal static async Task<AdminHarness> CreateAsync()
        {
            SettingsFilesHarness files = new();
            await files.BootAsync();
            AuthenticatedPrincipal alice = await files.LoginAsync(files.Alice);
            return new AdminHarness(files, alice);
        }

        internal AppPermissionAdministrationGateway CreatePermissionGateway(
            IAdministratorAuthenticationAgent agent, params string[] capabilities) =>
            new(() => Administration, new FixedCapabilities(capabilities), Gate(agent));

        internal AppSystemPowerGateway CreatePowerGateway(
            ISystemPowerDevice device, IAdministratorAuthenticationAgent agent, params string[] capabilities) =>
            new(() => new SystemPowerService(device, Files.Session, auditLog: Audit), new FixedCapabilities(capabilities), Gate(agent));

        internal SystemBackupService CreateBackupService(ISystemBackupDevice device) =>
            new(device, new SystemPowerService(new BackupPower(device), Files.Session, auditLog: Audit), Audit,
                () => new DateTimeOffset(2026, 10, 17, 7, 12, 0, TimeSpan.Zero));

        internal AppSystemBackupGateway CreateBackupGateway(
            BackupDevice device, IAdministratorAuthenticationAgent agent, params string[] capabilities) =>
            new(() => CreateBackupService(device), new FixedCapabilities(capabilities), Gate(agent));

        internal AdministratorGate Gate(IAdministratorAuthenticationAgent agent) =>
            new(() => agent, Catalog.Manifests[SettingsId], Principal, isRootElevated: false);
    }

    /// <summary>Records requests and answers with a fixed outcome.</summary>
    private sealed class CountingAgent(bool succeed) : IAdministratorAuthenticationAgent
    {
        public int Calls { get; private set; }

        public AdministratorAuthenticationRequest? LastRequest { get; private set; }

        public Task<AdministratorAuthenticationResult> AuthenticateAsync(
            AdministratorAuthenticationRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastRequest = request;
            return Task.FromResult(succeed
                ? new AdministratorAuthenticationResult(true, "admin", null)
                : AdministratorAuthenticationResult.Failed("cancelled"));
        }
    }

    /// <summary>Stands in for the browser: records erase and restart in order.</summary>
    private sealed class RecordingDevice : ISystemPowerDevice
    {
        public List<string> Calls { get; } = [];

        public Task RestartAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("restart");
            return Task.CompletedTask;
        }

        public Task EraseStorageAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("erase");
            return Task.CompletedTask;
        }
    }

    /// <summary>Stands in for the browser host of a backup: records every step in order.</summary>
    private sealed class BackupDevice : ISystemBackupDevice
    {
        private static readonly DateTimeOffset Exported = new(2026, 10, 17, 7, 12, 0, TimeSpan.Zero);
        private SystemRestoreRecord? _record;

        public List<string> Calls { get; } = [];

        public SystemBackupFile? Picked { get; init; } = new("old.kosbak", [1, 2, 3]);

        public bool Invalid { get; init; }

        public bool FailRestore { get; init; }

        public Task<SystemBackupArchive> ExportAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("export");
            return Task.FromResult(new SystemBackupArchive([0x1F, 0x8B], new SystemBackupSummary(Exported, 7)));
        }

        public Task SaveToHostAsync(string fileName, byte[] archive, CancellationToken cancellationToken = default)
        {
            Calls.Add($"save:{fileName}");
            return Task.CompletedTask;
        }

        public Task<SystemBackupFile?> PickFromHostAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("pick");
            return Task.FromResult(Picked);
        }

        public SystemBackupSummary Inspect(byte[] archive) =>
            Invalid ? throw new KazoBackupInvalidException("not a backup") : new SystemBackupSummary(Exported, 3);

        public Task RestoreAsync(byte[] archive, KazoBackupRestoreMode mode, CancellationToken cancellationToken = default)
        {
            Calls.Add($"restore:{mode}");
            return FailRestore ? Task.FromException(new IOException("disk on fire")) : Task.CompletedTask;
        }

        public Task SaveRestoreRecordAsync(SystemRestoreRecord record, CancellationToken cancellationToken = default)
        {
            Calls.Add($"record:{record.Outcome.Split(':')[0]}");
            _record = record;
            return Task.CompletedTask;
        }

        public Task<SystemRestoreRecord?> TakeRestoreRecordAsync(CancellationToken cancellationToken = default)
        {
            SystemRestoreRecord? record = _record;
            _record = null;
            return Task.FromResult(record);
        }

        public void Restarted() => Calls.Add("restart");
    }

    /// <summary>Power device of the backup tests: a restart lands in the backup device's step list.</summary>
    private sealed class BackupPower(ISystemBackupDevice device) : ISystemPowerDevice
    {
        public Task RestartAsync(CancellationToken cancellationToken = default)
        {
            ((BackupDevice)device).Restarted();
            return Task.CompletedTask;
        }

        public Task EraseStorageAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FixedInspector(KazoStorageSnapshot snapshot) : IStorageInspector
    {
        public Task<KazoStorageSnapshot> CaptureAsync(CancellationToken cancellationToken = default) => Task.FromResult(snapshot);
    }

    /// <summary>Grants exactly the listed capabilities (the gateways only call <see cref="Require"/>).</summary>
    private sealed class FixedCapabilities(params string[] granted) : ICapabilityChecker
    {
        public CapabilityPolicyEvaluation Evaluate(
            string capability, AppAuthority requiredAuthority = AppAuthority.User, CapabilityResourceCandidate? resourceCandidate = null) =>
            throw new NotSupportedException();

        public void Require(
            string capability, AppAuthority requiredAuthority = AppAuthority.User, CapabilityResourceCandidate? resourceCandidate = null)
        {
            if (!granted.Contains(capability, StringComparer.Ordinal))
            {
                throw new AppGatewayAccessDeniedException(capability, CapabilityPolicyEvaluation.DenyMissing(1));
            }
        }
    }

    /// <summary>
    /// Minimal context of an app run by alice whose <see cref="SystemSettings"/> is the real gateway;
    /// <paramref name="writer"/> replaces the app's own filesystem rights (root for a sudo run).
    /// </summary>
    private sealed class SettingsContext : IAppExecutionContext
    {
        internal SettingsContext(SettingsFilesHarness files, AuthenticatedPrincipal principal, IAppFileSystemGateway? writer = null)
        {
            Manifest = Manifest(SettingsId);
            UserId = principal.UserId.ToString();
            HomeDirectory = principal.HomePath;
            FileSystem = writer ?? files.CreateAppGateway(principal, SettingsId);
            SystemSettings = new AppSystemSettingsGateway(() => files.Settings, FileSystem);
        }

        public AppManifest Manifest { get; }
        public Guid InstanceId { get; } = Guid.NewGuid();
        public string UserId { get; }
        public string HomeDirectory { get; }
        public AppAuthority UserAuthority => AppAuthority.User;
        public IReadOnlySet<string> GrantedCapabilities { get; } = new HashSet<string>();
        public SessionId SessionId { get; } = SessionId.FromGuid(Guid.NewGuid());
        public ProcessId ProcessId { get; } = ProcessId.FromInt64(1);
        public CancellationToken CancellationToken => CancellationToken.None;
        public IAppFileSystemGateway FileSystem { get; }
        public IAppSystemSettingsGateway SystemSettings { get; }
        public ICapabilityChecker Capabilities => throw new NotSupportedException();
        public IAppEventGateway Events => throw new NotSupportedException();
        public IAppNotificationGateway Notifications => throw new NotSupportedException();
        public IAppClockGateway Clock => throw new NotSupportedException();
        public IAppProcessGateway Processes => throw new NotSupportedException();
        public IAppLoggingGateway Logging => throw new NotSupportedException();
    }
}
