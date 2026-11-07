using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Diagnostics;
using Kazo.Os.Platform.Core.Elevation;
using Kazo.Os.Platform.Core.Policy;
using Kazo.Os.Platform.Core.ServerConnection;
using Kazo.Os.Platform.Core.Sessions;
using Kazo.Os.Platform.Core.Tests.Settings;
using Kazo.Os.Server.Contracts.Proxy;
using Kazo.Os.Simulation.Abstractions.ServerConnection;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Tests.Policy;

/// <summary>
/// ADR 0233 §3 and §4: the approved real-network ports are kept in <c>[real-network-ports]</c>, an
/// update that declares a new port asks again, and the OS asks the server to issue or withdraw the
/// grant with those ports.
/// </summary>
public sealed class RealNetworkGrantTests
{
    private const string IrcId = "org.example.irc";
    private const string MachineFile = "/etc/kazoos/app-permissions.config";
    private const string AdminPassword = "s3cret-admin";

    // ---- [real-network-ports] (section 4) ----

    [Fact]
    public void The_machine_file_gives_the_approved_ports_of_each_app()
    {
        AppPermissionDecisions decisions = AppPermissionDecisions.Parse(
            """
            [permissions]
            org.example.irc:network.real.access=allow

            [real-network-ports]
            org.example.irc=tcp/6667, TCP/6697
            """,
            null);

        Assert.Equal(
            [new RealNetworkPort("tcp", 6667), new RealNetworkPort("tcp", 6697)],
            decisions.GetApprovedRealNetworkPorts(IrcId).OrderBy(static port => port.Port));
        Assert.Empty(decisions.GetApprovedRealNetworkPorts("org.example.other"));
        Assert.Empty(decisions.Warnings);
    }

    [Fact]
    public void A_bad_port_drops_its_line_and_the_user_file_cannot_approve_ports()
    {
        AppPermissionDecisions decisions = AppPermissionDecisions.Parse(
            "[real-network-ports]\norg.example.irc=tcp/6697,tcp/6697-6699\n",
            "[real-network-ports]\norg.example.other=tcp/6697\n");

        Assert.Empty(decisions.GetApprovedRealNetworkPorts(IrcId));
        Assert.Empty(decisions.GetApprovedRealNetworkPorts("org.example.other"));
        Assert.Equal(2, decisions.Warnings.Count);
        Assert.Contains(decisions.Warnings, static warning => warning.Contains("only the machine file", StringComparison.Ordinal));
    }

    [Fact]
    public void The_document_writes_the_ports_in_canonical_order_and_removes_them()
    {
        string written = AppPermissionsDocument.SetRealNetworkPorts(null, IrcId, ["tcp/6697", "udp/5353", "TCP/6667"]);
        string removed = AppPermissionsDocument.RemoveRealNetworkPorts(written, IrcId);

        Assert.Contains("[real-network-ports]", written, StringComparison.Ordinal);
        Assert.Contains("org.example.irc=tcp/6667,tcp/6697,udp/5353", written, StringComparison.Ordinal);
        Assert.Empty(AppPermissionDecisions.Parse(removed, null).GetApprovedRealNetworkPorts(IrcId));
        Assert.Contains("[permissions]", removed, StringComparison.Ordinal);
    }

    // ---- Decision table ----

    [Fact]
    public void An_allowed_app_whose_ports_were_all_approved_stays_allowed()
    {
        AppPermissionDecisions decisions = Allowed("tcp/6667,tcp/6697");

        Assert.Equal(DangerousCapabilityState.Allowed, StateOf(Irc("tcp/6697"), decisions));
        Assert.Empty(DangerousCapabilityEvaluator.UnapprovedRealNetworkPorts(Irc("tcp/6697"), decisions));
    }

    [Fact]
    public void An_update_that_declares_a_new_port_asks_again()
    {
        AppPermissionDecisions decisions = Allowed("tcp/6697");

        Assert.Equal(DangerousCapabilityState.Pending, StateOf(Irc("tcp/6697", "tcp/7000"), decisions));
        Assert.Equal([new RealNetworkPort("tcp", 7000)], DangerousCapabilityEvaluator.UnapprovedRealNetworkPorts(Irc("tcp/6697", "tcp/7000"), decisions));
    }

    [Fact]
    public void An_app_that_declares_no_port_needs_no_ports_line()
    {
        AppPermissionDecisions decisions = AppPermissionDecisions.Parse("[permissions]\norg.example.irc:network.real.access=allow\n", null);

        Assert.Equal(DangerousCapabilityState.Allowed, StateOf(Irc(), decisions));
    }

    // ---- The gate writes what the window showed ----

    [Fact]
    public async Task Approving_network_real_access_records_the_declared_ports_and_tells_the_server()
    {
        Harness harness = await Harness.CreateAsync(Irc("tcp/6697", "tcp/6667"));
        harness.Prompt.Script = async (request, ct) =>
        {
            await request.ApproveAsync("admin", AdminPassword, ct);
            return DangerousCapabilityApprovalOutcome.Approved;
        };

        DangerousCapabilityGateResult result = await harness.Gate.EnsureDecidedAsync(harness.App, harness.Principal, harness.Prompt);

        Assert.Equal(DangerousCapabilityApprovalOutcome.Approved, result.PromptOutcome);
        string machine = (await harness.Files.ReadAsync(MachineFile))!;
        Assert.Contains("[real-network-ports]", machine, StringComparison.Ordinal);
        Assert.Contains("org.example.irc=tcp/6667,tcp/6697", machine, StringComparison.Ordinal);
        (string userId, string? appId) call = Assert.Single(harness.Reconciler.Calls);
        Assert.Equal((harness.Principal.UserId.ToString(), IrcId), call);
    }

    [Fact]
    public async Task A_new_port_after_an_update_shows_the_window_again()
    {
        Harness harness = await Harness.CreateAsync(Irc("tcp/6697", "tcp/7000"));
        await harness.Store.WriteMachineDecisionsAsync(IrcId, [AppCapabilities.NetworkRealAccess], AppPermissionDecision.Allow);
        await harness.Store.WriteApprovedRealNetworkPortsAsync(IrcId, ["tcp/6697"]);

        await harness.Gate.EnsureDecidedAsync(harness.App, harness.Principal, harness.Prompt);

        Assert.Equal(1, harness.Prompt.Calls);
        Assert.Equal(AppCapabilities.NetworkRealAccess, Assert.Single(harness.Prompt.LastRequest!.Capabilities).Capability);
    }

    [Fact]
    public async Task Settings_allow_approves_the_declared_ports_and_a_reset_removes_them()
    {
        Harness harness = await Harness.CreateAsync(Irc("tcp/6697"));
        BoundedAuditLog audit = new(maxEntries: 50);
        AppPermissionAdministration administration = new(
            harness.Catalog, harness.Store, harness.Files.Settings, new AdminElevationVerifier(harness.Files.Users, audit), audit)
        {
            RealNetworkGrants = harness.Reconciler
        };

        await administration.ApplyMachineDecisionAsync(IrcId, AppCapabilities.NetworkRealAccess, AppPermissionDecision.Allow, "admin", harness.Principal);
        string allowed = (await harness.Files.ReadAsync(MachineFile))!;
        await administration.ApplyMachineDecisionAsync(IrcId, AppCapabilities.NetworkRealAccess, null, "admin", harness.Principal);
        string reset = (await harness.Files.ReadAsync(MachineFile))!;

        Assert.Contains("org.example.irc=tcp/6697", allowed, StringComparison.Ordinal);
        Assert.DoesNotContain("org.example.irc=tcp/6697", reset, StringComparison.Ordinal);
        Assert.Equal(2, harness.Reconciler.Calls.Count);
    }

    // ---- Reconciliation with the server (section 3) ----

    [Fact]
    public void The_request_carries_the_approved_ports_or_withdraws_the_grant()
    {
        ProxyGrantIssueRequest allowed = RealNetworkGrantReconciler.RequestFor(
            Irc("tcp/6697"), "u1", DangerousCapabilityPolicy.Prompt, Allowed("tcp/6697,tcp/6667"));
        ProxyGrantIssueRequest pending = RealNetworkGrantReconciler.RequestFor(
            Irc("tcp/6697", "tcp/7000"), "u1", DangerousCapabilityPolicy.Prompt, Allowed("tcp/6697"));
        ProxyGrantIssueRequest blocked = RealNetworkGrantReconciler.RequestFor(
            Irc("tcp/6697"), "u1", DangerousCapabilityPolicy.Block, Allowed("tcp/6697"));
        ProxyGrantIssueRequest deniedByUser = RealNetworkGrantReconciler.RequestFor(
            Irc("tcp/6697"), "u1", DangerousCapabilityPolicy.Prompt,
            AppPermissionDecisions.Parse(
                "[permissions]\norg.example.irc:network.real.access=allow\n[real-network-ports]\norg.example.irc=tcp/6697\n",
                "[permissions]\norg.example.irc:network.real.access=deny\n"));

        Assert.False(allowed.Revoked);
        Assert.Equal(["tcp/6667", "tcp/6697"], allowed.RealNetworkPorts);
        Assert.True(pending.Revoked);
        Assert.True(blocked.Revoked);
        Assert.True(deniedByUser.Revoked);
        Assert.Empty(deniedByUser.RealNetworkPorts!);
    }

    [Fact]
    public async Task Reconciliation_sends_each_change_once_and_nothing_without_a_server()
    {
        Harness harness = await Harness.CreateAsync(Irc("tcp/6697"));
        await harness.Store.WriteMachineDecisionsAsync(IrcId, [AppCapabilities.NetworkRealAccess], AppPermissionDecision.Allow);
        await harness.Store.WriteApprovedRealNetworkPortsAsync(IrcId, ["tcp/6697"]);
        GrantProxy proxy = new();
        RealNetworkGrantReconciler offline = new(harness.Catalog, harness.Store, harness.Files.Settings, new Connection(null), proxy);
        RealNetworkGrantReconciler online = new(harness.Catalog, harness.Store, harness.Files.Settings, new Connection(Connected), proxy);

        await offline.ReconcileAsync("u1");
        await online.ReconcileAsync("u1");
        await online.ReconcileAsync("u1");
        await harness.Store.WriteMachineDecisionsAsync(IrcId, [AppCapabilities.NetworkRealAccess], AppPermissionDecision.Deny);
        await online.ReconcileAsync("u1");

        Assert.Equal(2, proxy.Requests.Count);
        Assert.Equal(["tcp/6697"], proxy.Requests[0].RealNetworkPorts);
        Assert.False(proxy.Requests[0].Revoked);
        Assert.True(proxy.Requests[1].Revoked);
    }

    [Fact]
    public async Task A_server_failure_never_reaches_the_caller_and_is_retried()
    {
        Harness harness = await Harness.CreateAsync(Irc("tcp/6697"));
        await harness.Store.WriteMachineDecisionsAsync(IrcId, [AppCapabilities.NetworkRealAccess], AppPermissionDecision.Allow);
        GrantProxy proxy = new() { Failure = new ServerConnectionException("unreachable") };
        RealNetworkGrantReconciler reconciler = new(harness.Catalog, harness.Store, harness.Files.Settings, new Connection(Connected), proxy);

        await reconciler.ReconcileAsync("u1");
        proxy.Failure = null;
        await reconciler.ReconcileAsync("u1");

        Assert.Equal(2, proxy.Requests.Count);
    }

    [Fact]
    public async Task A_server_that_refuses_os_grants_stops_the_pass()
    {
        Harness harness = await Harness.CreateAsync(Irc("tcp/6697"));
        GrantProxy proxy = new()
        {
            Failure = new ServerConnectionException("closed", null, ProxyErrorCode.GrantIssuingDisabled)
        };
        RealNetworkGrantReconciler reconciler = new(harness.Catalog, harness.Store, harness.Files.Settings, new Connection(Connected), proxy);

        await reconciler.ReconcileAsync("u1");

        Assert.Single(proxy.Requests);
    }

    // ---- Helpers ----

    private static readonly ServerConnectionState Connected = new(
        Guid.NewGuid(), Guid.NewGuid(), "https://server.example/", "fp", "refresh", DateTimeOffset.UtcNow.AddDays(1));

    private static AppPermissionDecisions Allowed(string ports) => AppPermissionDecisions.Parse(
        $"[permissions]\norg.example.irc:network.real.access=allow\n[real-network-ports]\norg.example.irc={ports}\n", null);

    private static DangerousCapabilityState StateOf(AppManifest manifest, AppPermissionDecisions decisions) =>
        Assert.Single(DangerousCapabilityEvaluator.Evaluate(manifest, false, DangerousCapabilityPolicy.Prompt, decisions)).State;

    private static AppManifest Irc(params string[] ports) => new()
    {
        Id = IrcId,
        Name = "Chat",
        Version = "1.0.0",
        PublisherId = "org.example",
        Description = "Real-network ports fixture.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.Apps.Sample", "Kazo.Os.Apps.Sample.SampleApp"),
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None,
        Capabilities = [AppCapabilities.NetworkRealAccess],
        RealNetworkPorts = ports.Length == 0 ? null : ports
    };

    private sealed class Harness
    {
        private Harness(SettingsFilesHarness files, AuthenticatedPrincipal principal, AppManifest manifest)
        {
            Files = files;
            Principal = principal;
            AppCatalogBuildResult built = AppCatalog.Build([manifest], []);
            Assert.True(built.IsSuccess, string.Join(", ", built.Errors.Select(error => error.Message)));
            Catalog = built.Catalog!;
            BoundedAuditLog audit = new(maxEntries: 200);
            Store = new AppPermissionStore(files.Settings);
            Gate = new DangerousCapabilityGate(Catalog, files.Grants, Store, files.Settings, new AdminElevationVerifier(files.Users, audit), audit)
            {
                RealNetworkGrants = Reconciler
            };
        }

        internal SettingsFilesHarness Files { get; }
        internal AuthenticatedPrincipal Principal { get; }
        internal AppCatalog Catalog { get; }
        internal AppPermissionStore Store { get; }
        internal DangerousCapabilityGate Gate { get; }
        internal RecordingReconciler Reconciler { get; } = new();
        internal Prompt Prompt { get; } = new();
        internal AppManifest App => Catalog.Manifests[IrcId];

        internal static async Task<Harness> CreateAsync(AppManifest manifest)
        {
            SettingsFilesHarness files = new();
            files.Users.CreateUser(
                LocalLoginName.Parse("admin"), "Admin", AppAuthority.Administrator, files.Alice.PrimaryGroupId,
                credential: LocalPasswordHasher.Create(AdminPassword, iterations: 100));
            await files.BootAsync();
            AuthenticatedPrincipal alice = await files.LoginAsync(files.Alice);
            return new Harness(files, alice, manifest);
        }
    }

    private sealed class Prompt : IDangerousCapabilityApprovalPrompt
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

    private sealed class RecordingReconciler : IRealNetworkGrantReconciler
    {
        internal List<(string UserId, string? AppId)> Calls { get; } = [];

        public Task ReconcileAsync(string userId, string? appId = null, CancellationToken cancellationToken = default)
        {
            Calls.Add((userId, appId));
            return Task.CompletedTask;
        }
    }

    private sealed class GrantProxy : IProxyClient
    {
        internal List<ProxyGrantIssueRequest> Requests { get; } = [];

        internal Exception? Failure { get; set; }

        public Task<ProxyGrantIssued> IssueGrantAsync(
            Uri serverBaseUrl, string accessToken, ProxyGrantIssueRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Failure is not null
                ? Task.FromException<ProxyGrantIssued>(Failure)
                : Task.FromResult(new ProxyGrantIssued(request.AppId, request.UserId, Guid.NewGuid(), 1, true, request.RealNetworkPorts ?? [], request.Revoked));
        }

        public Task<ProxyHttpResponse> ExecuteHttpRequestAsync(
            Uri serverBaseUrl, string accessToken, ProxyHttpRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ProxyTcpProbeResponse> ExecuteTcpProbeAsync(
            Uri serverBaseUrl, string accessToken, ProxyTcpProbeRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ProxyPolicyResponse> GetPolicyAsync(
            Uri serverBaseUrl, string accessToken, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class Connection(ServerConnectionState? state) : IServerConnectionService
    {
        public ValueTask<ServerConnectionState?> GetStateAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(state);

        public Task<ServerConnectionState> ConnectWithNewAccountAsync(
            Uri serverBaseUrl, string username, string password, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ServerConnectionState> ConnectWithExistingAccountAsync(
            Uri serverBaseUrl, string username, string password, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public Task<string?> EnsureAccessTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(state is null ? null : "access-token");
    }
}
