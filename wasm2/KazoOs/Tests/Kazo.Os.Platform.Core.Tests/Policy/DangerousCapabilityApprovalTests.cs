using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.Platform.Core.Diagnostics;
using Kazo.Os.Platform.Core.Discovery;
using Kazo.Os.Platform.Core.Elevation;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Platform.Core.Notifications;
using Kazo.Os.Platform.Core.Policy;
using Kazo.Os.Platform.Core.Processes;
using Kazo.Os.Platform.Core.Sessions;
using Kazo.Os.Platform.Core.Tests.Settings;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Tests.Policy;

/// <summary>Contract tests of ADR 0049 sections 3 to 9 (chantier dangerous-approval).</summary>
public sealed class DangerousCapabilityApprovalTests
{
    private const string ScannerId = "org.example.scanner";
    private const string SystemToolId = "org.hackeros.system-tool";
    private const string MachineFile = "/etc/hackeros/app-permissions.config";
    private const string UserFile = "/home/alice/.config/hackeros/app-permissions.config";
    private const string SecurityFile = "/etc/hackeros/security.config";
    private const string AdminPassword = "s3cret-admin";

    // ---- Parsing of app-permissions.config (section 4) ----

    [Fact]
    public void Machine_allow_and_deny_lines_are_read_from_the_permissions_section()
    {
        AppPermissionDecisions decisions = AppPermissionDecisions.Parse(
            """
            # comment
            [permissions]
            org.example.scanner:network.real.access=allow
            org.example.scanner:filesystem.system.read=deny
            """,
            null);

        Assert.Equal(AppPermissionDecision.Allow, decisions.GetMachineDecision(ScannerId, AppCapabilities.NetworkRealAccess));
        Assert.Equal(AppPermissionDecision.Deny, decisions.GetMachineDecision(ScannerId, AppCapabilities.FileSystemSystemRead));
        Assert.Null(decisions.GetMachineDecision(ScannerId, AppCapabilities.ProcessManage));
        Assert.Empty(decisions.Warnings);
    }

    [Fact]
    public void Invalid_lines_are_ignored_with_a_warning_and_normal_capabilities_silently()
    {
        AppPermissionDecisions decisions = AppPermissionDecisions.Parse(
            """
            [permissions]
            org.example.scanner:network.real.access=maybe
            org.example.scanner:does.not.exist=allow
            no-capability-separator=allow
            org.example.scanner:clipboard.read=allow
            garbage line
            """,
            null);

        Assert.Null(decisions.GetMachineDecision(ScannerId, AppCapabilities.NetworkRealAccess));
        Assert.Null(decisions.GetMachineDecision(ScannerId, AppCapabilities.ClipboardRead));
        Assert.Equal(4, decisions.Warnings.Count);
    }

    [Fact]
    public void An_allow_in_the_user_file_is_ignored_with_a_warning()
    {
        AppPermissionDecisions decisions = AppPermissionDecisions.Parse(
            null,
            "[permissions]\norg.example.scanner:network.real.access=allow\norg.example.scanner:process.manage=deny\n");

        Assert.Null(decisions.GetMachineDecision(ScannerId, AppCapabilities.NetworkRealAccess));
        Assert.False(decisions.IsDeniedByUser(ScannerId, AppCapabilities.NetworkRealAccess));
        Assert.True(decisions.IsDeniedByUser(ScannerId, AppCapabilities.ProcessManage));
        Assert.Contains(decisions.Warnings, warning => warning.Contains("only", StringComparison.Ordinal));
    }

    // ---- Decision table (sections 2 to 4) ----

    [Fact]
    public void Machine_allow_with_prompt_grants_and_with_block_refuses()
    {
        AppManifest scanner = Manifest(ScannerId, AppCapabilities.NetworkRealAccess, AppCapabilities.ClipboardRead);
        AppPermissionDecisions decisions = AppPermissionDecisions.Parse(
            "[permissions]\norg.example.scanner:network.real.access=allow\n", null);

        DangerousCapabilityStatus prompt = Assert.Single(
            DangerousCapabilityEvaluator.Evaluate(scanner, false, DangerousCapabilityPolicy.Prompt, decisions));
        DangerousCapabilityStatus block = Assert.Single(
            DangerousCapabilityEvaluator.Evaluate(scanner, false, DangerousCapabilityPolicy.Block, decisions));

        Assert.Equal(DangerousCapabilityState.Allowed, prompt.State);
        Assert.True(prompt.IsGranted);
        Assert.Equal(DangerousCapabilityState.BlockedByPolicy, block.State);
        Assert.False(block.IsGranted);
    }

    [Fact]
    public void User_deny_beats_machine_allow_and_no_decision_is_pending()
    {
        AppManifest scanner = Manifest(ScannerId, AppCapabilities.NetworkRealAccess, AppCapabilities.ProcessManage);
        AppPermissionDecisions decisions = AppPermissionDecisions.Parse(
            "[permissions]\norg.example.scanner:network.real.access=allow\n",
            "[permissions]\norg.example.scanner:network.real.access=deny\n");

        IReadOnlyList<DangerousCapabilityStatus> statuses =
            DangerousCapabilityEvaluator.Evaluate(scanner, false, DangerousCapabilityPolicy.Prompt, decisions);

        Assert.Equal(DangerousCapabilityState.DeniedByUser, statuses[0].State);
        Assert.Equal(DangerousCapabilityState.Pending, statuses[1].State);
    }

    [Fact]
    public void A_system_app_ignores_decision_lines()
    {
        AppManifest tool = Manifest(SystemToolId, AppCapabilities.ProcessManage);
        AppPermissionDecisions decisions = AppPermissionDecisions.Parse(
            $"[permissions]\n{SystemToolId}:process.manage=deny\n", null);

        DangerousCapabilityStatus status = Assert.Single(
            DangerousCapabilityEvaluator.Evaluate(tool, true, DangerousCapabilityPolicy.Block, decisions));

        Assert.Equal(DangerousCapabilityState.System, status.State);
    }

    // ---- Launch gate (section 5) ----

    [Fact]
    public async Task System_app_gets_its_dangerous_capabilities_without_a_prompt()
    {
        GateHarness harness = await GateHarness.CreateAsync();
        AppManifest tool = harness.Catalog.Manifests[SystemToolId];

        DangerousCapabilityGateResult result = await harness.Gate.EnsureDecidedAsync(tool, harness.Principal, harness.Prompt);

        Assert.Equal(0, harness.Prompt.Calls);
        Assert.True(result.IsSystemApp);
        Assert.All(result.Statuses, status => Assert.Equal(DangerousCapabilityState.System, status.State));
    }

    [Fact]
    public async Task Pending_capability_opens_the_prompt_and_dismissing_writes_nothing()
    {
        GateHarness harness = await GateHarness.CreateAsync();
        harness.Prompt.Script = (_, _) => Task.FromResult(DangerousCapabilityApprovalOutcome.Dismissed);
        string? machineBefore = await harness.Files.ReadAsync(MachineFile);

        DangerousCapabilityGateResult result = await harness.Gate.EnsureDecidedAsync(harness.Scanner, harness.Principal, harness.Prompt);

        Assert.Equal(1, harness.Prompt.Calls);
        Assert.Equal(DangerousCapabilityApprovalOutcome.Dismissed, result.PromptOutcome);
        Assert.Equal(machineBefore, await harness.Files.ReadAsync(MachineFile));
        Assert.Null(await harness.Files.ReadAsync(UserFile));
        Assert.False(harness.IsGranted(ScannerId, AppCapabilities.NetworkRealAccess));
        Assert.Equal(
            [AppCapabilities.NetworkRealAccess, AppCapabilities.ProcessManage],
            harness.Prompt.LastRequest!.Capabilities.Select(capability => capability.Capability));
    }

    [Fact]
    public async Task Approval_by_another_administrator_writes_the_machine_file_and_grants_before_launch()
    {
        // Alice is an ordinary user: the write to /etc must still succeed (audited system context).
        GateHarness harness = await GateHarness.CreateAsync();
        harness.Prompt.Script = async (request, ct) =>
        {
            Assert.Null(request.SuggestedAdministratorLogin);
            DangerousCapabilityApprovalAttempt attempt = await request.ApproveAsync("admin", AdminPassword, ct);
            Assert.True(attempt.Succeeded);
            return DangerousCapabilityApprovalOutcome.Approved;
        };

        DangerousCapabilityGateResult result = await harness.Gate.EnsureDecidedAsync(harness.Scanner, harness.Principal, harness.Prompt);

        Assert.Equal(DangerousCapabilityApprovalOutcome.Approved, result.PromptOutcome);
        string machine = (await harness.Files.ReadAsync(MachineFile))!;
        Assert.Contains("org.example.scanner:network.real.access=allow", machine, StringComparison.Ordinal);
        Assert.Contains("org.example.scanner:process.manage=allow", machine, StringComparison.Ordinal);
        Assert.True(harness.IsGranted(ScannerId, AppCapabilities.NetworkRealAccess));
        Assert.True(harness.IsGranted(ScannerId, AppCapabilities.ProcessManage));

        AuditEntry approve = Assert.Single(harness.Audit.Entries, entry => entry.Action == "capability.approve");
        Assert.Equal(AuditOutcome.Success, approve.Outcome);
        Assert.Equal("admin", approve.Properties["admin"]);
        Assert.Equal("alice", approve.Properties["requestedBy"]);
        Assert.Equal("success", approve.Properties["outcome"]);
        Assert.DoesNotContain(harness.Audit.Entries, entry => entry.Properties.Values.Any(value => value.Contains(AdminPassword, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Three_wrong_passwords_write_nothing_and_log_three_failed_approvals()
    {
        GateHarness harness = await GateHarness.CreateAsync();
        List<DangerousCapabilityApprovalAttempt> attempts = [];
        harness.Prompt.Script = async (request, ct) =>
        {
            for (int i = 0; i < 4; i++)
            {
                attempts.Add(await request.ApproveAsync("admin", "wrong", ct));
            }

            return DangerousCapabilityApprovalOutcome.Dismissed;
        };
        string? machineBefore = await harness.Files.ReadAsync(MachineFile);

        await harness.Gate.EnsureDecidedAsync(harness.Scanner, harness.Principal, harness.Prompt);

        Assert.Equal([2, 1, 0, 0], attempts.Select(attempt => attempt.RemainingAttempts));
        Assert.All(attempts, attempt => Assert.False(attempt.Succeeded));
        Assert.Equal(machineBefore, await harness.Files.ReadAsync(MachineFile));
        List<AuditEntry> approvals = [.. harness.Audit.Entries.Where(entry => entry.Action == "capability.approve")];
        Assert.Equal(3, approvals.Count);
        Assert.All(approvals, entry => Assert.Equal(AuditOutcome.Denied, entry.Outcome));
        Assert.False(harness.IsGranted(ScannerId, AppCapabilities.NetworkRealAccess));
    }

    [Fact]
    public async Task Refusing_writes_user_denials_and_the_question_is_not_asked_again()
    {
        GateHarness harness = await GateHarness.CreateAsync();
        harness.Prompt.Script = (_, _) => Task.FromResult(DangerousCapabilityApprovalOutcome.Refused);

        DangerousCapabilityGateResult first = await harness.Gate.EnsureDecidedAsync(harness.Scanner, harness.Principal, harness.Prompt);
        await harness.Gate.EnsureDecidedAsync(harness.Scanner, harness.Principal, harness.Prompt);

        Assert.Equal(1, harness.Prompt.Calls);
        Assert.All(first.Statuses, status => Assert.Equal(DangerousCapabilityState.DeniedByUser, status.State));
        string user = (await harness.Files.ReadAsync(UserFile))!;
        Assert.Contains("org.example.scanner:network.real.access=deny", user, StringComparison.Ordinal);
        AuditEntry refuse = Assert.Single(harness.Audit.Entries, entry => entry.Action == "capability.refuse");
        Assert.Equal("alice", refuse.Properties["user"]);
    }

    [Fact]
    public async Task Block_policy_logs_blocked_without_prompt_and_ignores_existing_allow()
    {
        GateHarness harness = await GateHarness.CreateAsync();
        await harness.Files.WriteAsRootAsync(MachineFile, "[permissions]\norg.example.scanner:network.real.access=allow\n");
        await harness.Files.WriteAsRootAsync(SecurityFile, "dangerousCapabilities=block\n");

        DangerousCapabilityGateResult result = await harness.Gate.EnsureDecidedAsync(harness.Scanner, harness.Principal, harness.Prompt);

        Assert.Equal(0, harness.Prompt.Calls);
        Assert.True(result.BlocksOneShotApproval);
        Assert.False(harness.IsGranted(ScannerId, AppCapabilities.NetworkRealAccess));
        AuditEntry blocked = Assert.Single(harness.Audit.Entries, entry => entry.Action == "capability.blocked");
        Assert.Equal("network.real.access,process.manage", blocked.Properties["capabilities"]);
    }

    [Fact]
    public async Task Switching_to_block_revokes_a_previous_approval_at_the_next_launch()
    {
        GateHarness harness = await GateHarness.CreateAsync();
        await harness.Files.WriteAsRootAsync(
            MachineFile,
            "[permissions]\norg.example.scanner:network.real.access=allow\norg.example.scanner:process.manage=allow\n");
        await harness.Gate.EnsureDecidedAsync(harness.Scanner, harness.Principal, harness.Prompt);
        Assert.True(harness.IsGranted(ScannerId, AppCapabilities.NetworkRealAccess));

        await harness.Files.WriteAsRootAsync(SecurityFile, "dangerousCapabilities=block\n");
        await harness.Gate.EnsureDecidedAsync(harness.Scanner, harness.Principal, harness.Prompt);

        Assert.False(harness.IsGranted(ScannerId, AppCapabilities.NetworkRealAccess));
    }

    [Fact]
    public async Task Without_an_interactive_session_the_decision_is_deferred()
    {
        GateHarness harness = await GateHarness.CreateAsync();

        DangerousCapabilityGateResult result = await harness.Gate.EnsureDecidedAsync(
            harness.Scanner, harness.Principal, harness.Prompt, interactive: false);

        Assert.Equal(0, harness.Prompt.Calls);
        Assert.Equal(DangerousCapabilityApprovalOutcome.Unavailable, result.PromptOutcome);
        AuditEntry deferred = Assert.Single(harness.Audit.Entries, entry => entry.Action == "capability.deferred");
        Assert.Equal(ScannerId, deferred.Properties["app"]);
    }

    [Fact]
    public async Task A_prompt_cannot_forge_an_approval_without_verified_credentials()
    {
        GateHarness harness = await GateHarness.CreateAsync();
        harness.Prompt.Script = (_, _) => Task.FromResult(DangerousCapabilityApprovalOutcome.Approved);

        DangerousCapabilityGateResult result = await harness.Gate.EnsureDecidedAsync(harness.Scanner, harness.Principal, harness.Prompt);

        Assert.Equal(DangerousCapabilityApprovalOutcome.Dismissed, result.PromptOutcome);
        Assert.False(harness.IsGranted(ScannerId, AppCapabilities.NetworkRealAccess));
    }

    // ---- Orchestrator integration and one-shot approval (sections 3 and 5) ----

    [Fact]
    public async Task Launch_calls_the_gate_and_freezes_the_approved_capabilities_in_the_new_instance()
    {
        GateHarness harness = await GateHarness.CreateAsync();
        harness.Prompt.Script = async (request, ct) =>
        {
            await request.ApproveAsync("admin", AdminPassword, ct);
            return DangerousCapabilityApprovalOutcome.Approved;
        };
        AppLifecycleOrchestrator orchestrator = harness.CreateOrchestrator();

        AppLaunchResult launch = await orchestrator.LaunchAsync(new AppLaunchRequest(ScannerId, harness.Principal, []));

        Assert.Equal(AppLaunchStatus.Launched, launch.Status);
        Assert.Equal(1, harness.Prompt.Calls);
        Assert.Contains(AppCapabilities.NetworkRealAccess, launch.Context!.GrantedCapabilities);
        Assert.Contains(AppCapabilities.ProcessManage, launch.Context.GrantedCapabilities);
    }

    [Fact]
    public async Task Without_a_decision_the_app_starts_without_its_dangerous_capabilities()
    {
        GateHarness harness = await GateHarness.CreateAsync();
        AppLifecycleOrchestrator orchestrator = harness.CreateOrchestrator();

        AppLaunchResult launch = await orchestrator.LaunchAsync(new AppLaunchRequest(ScannerId, harness.Principal, []));

        Assert.Equal(AppLaunchStatus.Launched, launch.Status);
        Assert.Equal(1, harness.Prompt.Calls);
        Assert.DoesNotContain(AppCapabilities.NetworkRealAccess, launch.Context!.GrantedCapabilities);
        Assert.False(launch.Context.OneShotElevation.IsBlocked(AppCapabilities.NetworkRealAccess));
    }

    [Fact]
    public async Task Block_disables_one_shot_approval_of_dangerous_capabilities_but_not_sudo()
    {
        GateHarness harness = await GateHarness.CreateAsync();
        await harness.Files.WriteAsRootAsync(SecurityFile, "dangerousCapabilities=block\n");
        InMemoryElevationTicketStore tickets = new();
        AppLifecycleOrchestrator orchestrator = harness.CreateOrchestrator(tickets);

        ElevationTicket approval = tickets.Issue(
            ElevationScope.CapabilityApproval, "admin-id", "admin", AppCapabilities.FileSystemSystemWrite, VirtualPath.Parse("/etc/x"));
        AppLaunchResult refused = await orchestrator.LaunchAsync(new AppLaunchRequest(
            ScannerId, harness.Principal, [], ElevationToken: approval.Token, ElevationScope: ElevationScope.CapabilityApproval));

        ElevationTicket sudo = tickets.Issue(ElevationScope.SudoRoot, "admin-id", "admin");
        AppLaunchResult elevated = await orchestrator.LaunchAsync(new AppLaunchRequest(
            ScannerId, harness.Principal, [], ElevationToken: sudo.Token, ElevationScope: ElevationScope.SudoRoot));

        Assert.Equal(AppLaunchStatus.ElevationDenied, refused.Status);
        Assert.Equal("lifecycle.elevation.blocked-by-policy", refused.ErrorCode);
        Assert.Equal(AppLaunchStatus.Launched, elevated.Status);
        Assert.True(elevated.Context!.OneShotElevation.IsBlocked(AppCapabilities.FileSystemSystemWrite));
        Assert.False(elevated.Context.OneShotElevation.IsBlocked(AppCapabilities.ClipboardRead));
    }

    [Fact]
    public void A_blocked_capability_cannot_be_granted_once()
    {
        OneShotElevationGrantSet set = new();
        set.Grant(AppCapabilities.FileSystemSystemWrite, VirtualPath.Parse("/etc/a"));
        set.Block(AppCapabilities.FileSystemSystemWrite);
        set.Grant(AppCapabilities.FileSystemSystemWrite, VirtualPath.Parse("/etc/b"));

        Assert.False(set.TryConsume(AppCapabilities.FileSystemSystemWrite, VirtualPath.Parse("/etc/a")));
        Assert.False(set.TryConsume(AppCapabilities.FileSystemSystemWrite, VirtualPath.Parse("/etc/b")));
    }

    // ---- Files and Settings administration (sections 3, 4, 7, 8) ----

    [Fact]
    public async Task Fresh_installation_ships_security_config_and_an_empty_machine_permission_file()
    {
        SettingsFilesHarness files = new();
        await files.BootAsync();

        Assert.Contains("dangerousCapabilities=prompt", await files.ReadAsync(SecurityFile), StringComparison.Ordinal);
        Assert.Contains("[permissions]", await files.ReadAsync(MachineFile), StringComparison.Ordinal);
        Assert.Equal(DangerousCapabilityPolicy.Prompt, await SecuritySettingsDocuments.ReadPolicyAsync(files.Settings));
    }

    [Fact]
    public async Task Invalid_policy_value_falls_back_to_prompt()
    {
        SettingsFilesHarness files = new();
        await files.BootAsync();
        await files.WriteAsRootAsync(SecurityFile, "dangerousCapabilities=sometimes\n");

        Assert.Equal(DangerousCapabilityPolicy.Prompt, await SecuritySettingsDocuments.ReadPolicyAsync(files.Settings));
    }

    [Fact]
    public async Task Settings_administration_changes_policy_and_revokes_with_an_administrator_only()
    {
        GateHarness harness = await GateHarness.CreateAsync();
        AppPermissionAdministration administration = new(
            harness.Catalog, harness.Store, harness.Files.Settings, harness.Verifier, harness.Audit);

        AppPermissionAdministrationResult denied = await administration.SetPolicyAsync(
            DangerousCapabilityPolicy.Block, "alice", SettingsFilesHarness.Password, harness.Principal);
        AppPermissionAdministrationResult allowed = await administration.SetMachineDecisionAsync(
            ScannerId, AppCapabilities.NetworkRealAccess, AppPermissionDecision.Allow, "admin", AdminPassword, harness.Principal);
        AppPermissionAdministrationResult revoked = await administration.SetMachineDecisionAsync(
            ScannerId, AppCapabilities.NetworkRealAccess, AppPermissionDecision.Deny, "admin", AdminPassword, harness.Principal);

        Assert.False(denied.Succeeded);
        Assert.Equal("not-administrator", denied.FailureReason);
        Assert.Equal(DangerousCapabilityPolicy.Prompt, await administration.GetPolicyAsync());
        Assert.True(allowed.Succeeded);
        Assert.True(revoked.Succeeded);

        AppPermissionOverview scanner = Assert.Single(await administration.GetOverviewAsync(), app => !app.IsSystemApp);
        Assert.Equal(DangerousCapabilityState.DeniedByAdministrator, scanner.Capabilities[0].State);
        AuditEntry revoke = Assert.Single(harness.Audit.Entries, entry => entry.Action == "capability.revoke");
        Assert.Equal("admin", revoke.Properties["admin"]);
        Assert.Equal(AppCapabilities.NetworkRealAccess, revoke.Properties["capability"]);
    }

    [Fact]
    public async Task A_user_can_withdraw_their_own_denial()
    {
        GateHarness harness = await GateHarness.CreateAsync();
        harness.Prompt.Script = (_, _) => Task.FromResult(DangerousCapabilityApprovalOutcome.Refused);
        await harness.Gate.EnsureDecidedAsync(harness.Scanner, harness.Principal, harness.Prompt);
        AppPermissionAdministration administration = new(
            harness.Catalog, harness.Store, harness.Files.Settings, harness.Verifier, harness.Audit);

        await administration.RemoveOwnDenialAsync(ScannerId, AppCapabilities.NetworkRealAccess);

        AppPermissionDecisions decisions = await harness.Store.ReadAsync();
        Assert.False(decisions.IsDeniedByUser(ScannerId, AppCapabilities.NetworkRealAccess));
        Assert.True(decisions.IsDeniedByUser(ScannerId, AppCapabilities.ProcessManage));
    }

    private static AppManifest Manifest(string id, params string[] capabilities) => new()
    {
        Id = id,
        Name = id == ScannerId ? "Scanner" : "System Tool",
        Version = "1.2.0",
        PublisherId = id == ScannerId ? "org.example" : "pub.hackeros",
        Description = "Dangerous-capability fixture.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.Apps.Sample", "Kazo.Os.Apps.Sample.SampleApp"),
        SdkCompatibility = new AppSdkCompatibilityManifest(HackerOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None,
        Capabilities = capabilities
    };

    /// <summary>Real files, settings, grants and administrator verification around the gate.</summary>
    private sealed class GateHarness
    {
        private GateHarness(SettingsFilesHarness files, AuthenticatedPrincipal principal)
        {
            Files = files;
            Principal = principal;
            AppCatalogBuildResult built = AppCatalog.Build(
                [
                    Manifest(ScannerId, AppCapabilities.NetworkRealAccess, AppCapabilities.ProcessManage, AppCapabilities.ClipboardRead),
                    Manifest(SystemToolId, AppCapabilities.ProcessManage)
                ],
                [SystemToolId]);
            Assert.True(built.IsSuccess, string.Join(", ", built.Errors.Select(error => error.Message)));
            Catalog = built.Catalog!;
            Audit = new BoundedAuditLog(maxEntries: 200);
            Verifier = new AdminElevationVerifier(files.Users, Audit);
            Store = new AppPermissionStore(files.Settings);
            Gate = new DangerousCapabilityGate(Catalog, files.Grants, Store, files.Settings, Verifier, Audit);
        }

        internal SettingsFilesHarness Files { get; }
        internal AuthenticatedPrincipal Principal { get; }
        internal AppCatalog Catalog { get; }
        internal BoundedAuditLog Audit { get; }
        internal AdminElevationVerifier Verifier { get; }
        internal AppPermissionStore Store { get; }
        internal DangerousCapabilityGate Gate { get; }
        internal FakePrompt Prompt { get; } = new();
        internal AppManifest Scanner => Catalog.Manifests[ScannerId];

        internal static async Task<GateHarness> CreateAsync()
        {
            SettingsFilesHarness files = new();
            files.Users.CreateUser(
                LocalLoginName.Parse("admin"), "Admin", AppAuthority.Administrator, files.Alice.PrimaryGroupId,
                credential: LocalPasswordHasher.Create(AdminPassword, iterations: 100));
            await files.BootAsync();
            AuthenticatedPrincipal alice = await files.LoginAsync(files.Alice);
            return new GateHarness(files, alice);
        }

        internal bool IsGranted(string appId, string capability) =>
            Files.Grants.Evaluate(appId, Principal.UserId.ToString(), capability, AppAuthority.User, AppAuthority.User).Granted;

        internal AppLifecycleOrchestrator CreateOrchestrator(IElevationTicketStore? tickets = null)
        {
            InMemoryProcessManager manager = new(Files.Clock, Files.Session, Files.EventBus);
            AppExecutionContextFactory contextFactory = new(
                Files.Grants, Files.FileSystem, Files.EventBus, Files.TopicBus,
                new InMemoryNotificationQueue(maxEntriesPerUser: 20), new BoundedDiagnosticSink(maxEntries: 100),
                Files.Clock, manager);
            Dictionary<string, AppDescriptor> descriptors = Catalog.Manifests.Values.ToDictionary(
                manifest => manifest.Id,
                manifest => new AppDescriptor(manifest, typeof(object), typeof(object).Assembly),
                StringComparer.Ordinal);
            DangerousCapabilityGate gate = new(Catalog, Files.Grants, Store, Files.Settings, Verifier, Audit, Prompt);
            return new AppLifecycleOrchestrator(
                Catalog, descriptors, new AppEnablementRegistry(Catalog), manager, Files.Grants, contextFactory,
                Files.EventBus, elevationTickets: tickets, dangerousCapabilityGate: gate);
        }
    }

    private sealed class FakePrompt : IDangerousCapabilityApprovalPrompt
    {
        internal Func<DangerousCapabilityApprovalRequest, CancellationToken, Task<DangerousCapabilityApprovalOutcome>> Script { get; set; } =
            (_, _) => Task.FromResult(DangerousCapabilityApprovalOutcome.Dismissed);

        internal int Calls { get; private set; }

        internal DangerousCapabilityApprovalRequest? LastRequest { get; private set; }

        public Task<DangerousCapabilityApprovalOutcome> RequestAsync(
            DangerousCapabilityApprovalRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastRequest = request;
            return Script(request, cancellationToken);
        }
    }
}
