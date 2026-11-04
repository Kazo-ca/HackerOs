using System.Collections.Immutable;
using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Net;
using Kazo.Os.Commands.Cat;
using Kazo.Os.Commands.Curl;
using Kazo.Os.Commands.Nmap;
using Kazo.Os.Commands.Ping;
using Kazo.Os.Platform.Core.Network;
using Kazo.Os.Platform.Core.Network.Websites;
using Kazo.Os.Platform.Core.ServerConnection;
using Kazo.Os.Server.Contracts.Proxy;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Network;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.ServerConnection;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Xunit;

namespace Kazo.Os.Network.Tests;

/// <summary>
/// Comprehensive unit test suite for Phase 4 Wave 4 (Simulated Network, Websites, Commands).
/// Validates DNS resolution, website controllers, cookie sessions, redirects,
/// ping/nmap/curl commands, and proves zero real external network calls.
/// </summary>
public sealed partial class Wave4NetworkTests
{
    private readonly InMemorySimulatedNetworkService _network;

    public Wave4NetworkTests()
    {
        SimulatedHost[] hosts = [.. DefaultSimulatedHostCatalog.Build(), BodyEchoController.Host];

        var registry = new InMemorySimulatedWebsiteRegistry();
        registry.Register(new HackerSearchController());
        registry.Register(new HackMailController());
        registry.Register(new CryptoBankController());
        registry.Register(new DarknetMarketController());
        registry.Register(new HackerForumController());
        registry.Register(new BodyEchoController());

        _network = new InMemorySimulatedNetworkService(hosts, registry);
    }

    // ── P4-W4-001 / P4-W4-002: DNS & Network Service ────────────────────────

    [Fact]
    public void Dns_ResolvesKnownHosts_And_ReverseLookups()
    {
        var ip = _network.Dns.Resolve("hackersearch.net");
        Assert.NotNull(ip);
        Assert.Equal("192.168.1.90", ip);

        var host = _network.Dns.ReverseLookup("192.168.1.90");
        Assert.Equal("hackersearch.net", host);
    }

    [Fact]
    public void Dns_ReturnsNull_ForUnknownHost()
    {
        Assert.Null(_network.Dns.Resolve("nonexistent.invalid"));
    }

    [Fact]
    public void Ping_ReturnsLatency_ForUpHost_And_Null_ForDownHost()
    {
        var latency = _network.Ping("hackersearch.net");
        Assert.NotNull(latency);
        Assert.True(latency > 0);

        var downLatency = _network.Ping("hackerz-search.net");
        Assert.Null(downLatency);
    }

    [Fact]
    public void ScanPorts_ReturnsHostPorts_FilteredByRange()
    {
        var ports = _network.ScanPorts("localhost", 20, 100);
        Assert.NotEmpty(ports);
        Assert.Contains(ports, p => p.PortNumber == 22 && p.State == SimulatedPortState.Open);
        // ADR 0074: localhost's simulated ports are its system daemons; HTTP belongs to the apps that listen.
        Assert.DoesNotContain(ports, p => p.PortNumber == 80);
        Assert.DoesNotContain(ports, p => p.PortNumber == 3306);
    }

    // ── P4-W4-003 / P4-W4-005: Website Controllers & Navigation ────────────

    [Fact]
    public void Navigate_HackerSearch_ReturnsRenderablePage()
    {
        var cookies = new Dictionary<string, Dictionary<string, string>>();
        var result = _network.Navigate("https://hackersearch.net", cookies);

        Assert.True(result.IsSuccess);
        Assert.Equal(200, result.StatusCode);
        Assert.NotNull(result.Page);
        Assert.Equal("HackerSearch", result.Page.Title);
    }

    [Fact]
    public void Navigate_HackMail_RedirectsToLogin_WhenNotAuthenticated()
    {
        var cookies = new Dictionary<string, Dictionary<string, string>>();
        var result = _network.Navigate("https://hackmail.com/inbox", cookies);

        Assert.True(result.IsSuccess);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal("https://hackmail.com/login", result.FinalUrl);
        Assert.Equal("HackMail — Login", result.Page?.Title);
        Assert.Equal(1, result.RedirectCount);
    }

    [Fact]
    public void HackMail_PostLogin_SetsSessionCookie_And_RedirectsToInbox()
    {
        var cookies = new Dictionary<string, Dictionary<string, string>>();

        var postResp = _network.Post(
            "https://hackmail.com/login",
            ImmutableDictionary<string, string>.Empty
                .Add("username", "agent")
                .Add("password", "secret123"),
            cookies);

        Assert.True(postResp.IsRedirect);
        Assert.Contains("hackmail.com", cookies);
        Assert.Equal("user=agent", cookies["hackmail.com"]["hackmail_session"]);

        // Now navigate to /inbox using the updated cookies
        var navResult = _network.Navigate("https://hackmail.com/inbox", cookies);
        Assert.True(navResult.IsSuccess);
        Assert.Equal("HackMail — Inbox", navResult.Page?.Title);
    }

    [Fact]
    public void Navigate_UnknownHost_ReturnsNetworkError()
    {
        var cookies = new Dictionary<string, Dictionary<string, string>>();
        var result = _network.Navigate("https://unknown-host-12345.com", cookies);

        Assert.False(result.IsSuccess);
        Assert.Equal(SimulatedNetworkErrors.UnknownHost, result.NetworkError);
    }

    // ── P4-W4-006: Terminal Commands (ping, nmap, curl) ────────────────────

    [Fact]
    public async Task PingCommand_ExecutesSuccessfully_ForUpHost()
    {
        UseServer(new NeverConnectedServerConnectionService(), new UnusedProxyClient());
        var cmd = new PingCommand(AppDefinitionMapper.ToManifest(new PingApp()));

        using var stdoutWriter = new StringWriter();
        using var stderrWriter = new StringWriter();
        var context = CreateContext(["hackersearch.net"], stdoutWriter, stderrWriter);

        int exitCode = await cmd.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(0, exitCode);
        var output = stdoutWriter.ToString();
        Assert.Contains("PING hackersearch.net (192.168.1.90)", output);
        Assert.Contains("64 bytes from 192.168.1.90", output);
        Assert.Contains("0% packet loss", output);
    }

    [Fact]
    public async Task PingCommand_UnknownHost_WithoutServerConnection_ReportsCannotResolve()
    {
        UseServer(new NeverConnectedServerConnectionService(), new UnusedProxyClient());
        var cmd = new PingCommand(AppDefinitionMapper.ToManifest(new PingApp()));

        using var stdoutWriter = new StringWriter();
        using var stderrWriter = new StringWriter();
        var context = CreateContext(["unknown-host-12345.com"], stdoutWriter, stderrWriter);

        int exitCode = await cmd.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("cannot resolve", stdoutWriter.ToString());
    }

    [Fact]
    public async Task NmapCommand_ScansPorts_And_FormatsOutput()
    {
        UseServer(new NeverConnectedServerConnectionService(), new UnusedProxyClient());
        var cmd = new NmapCommand(AppDefinitionMapper.ToManifest(new NmapApp()));

        using var stdoutWriter = new StringWriter();
        using var stderrWriter = new StringWriter();
        var context = CreateContext(["-p", "1-100", "-sV", "localhost"], stdoutWriter, stderrWriter);

        int exitCode = await cmd.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(0, exitCode);
        var output = stdoutWriter.ToString();
        Assert.Contains("Nmap scan report for localhost (127.0.0.1)", output);
        Assert.Contains("22/tcp    open      ssh", output);
        // ADR 0074: nothing listens on localhost:80 until a web app opens it.
        Assert.DoesNotContain("80/tcp", output);
    }

    // ── ADR 0035: nmap single-port real-network fallback ────────────────────

    [Fact]
    public async Task NmapCommand_RangeAgainstUnknownHost_NeverAttemptsRealProbe_EvenWhenConnected()
    {
        // A port range (the default, or an explicit "-p 1-100") must never trigger a real probe --
        // only an explicit single port does. UnusedProxyClient throws if called, proving this.
        UseServer(new NeverConnectedServerConnectionService(), new UnusedProxyClient());
        var cmd = new NmapCommand(AppDefinitionMapper.ToManifest(new NmapApp()));

        using var stdoutWriter = new StringWriter();
        using var stderrWriter = new StringWriter();
        var context = CreateContext(["-p", "1-100", "unknown-host-12345.com"], stdoutWriter, stderrWriter);

        int exitCode = await cmd.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("Host seems down", stdoutWriter.ToString());
    }

    [Fact]
    public async Task NmapCommand_SinglePortAgainstUnknownHost_WithoutServerConnection_ReportsHostDown()
    {
        UseServer(new NeverConnectedServerConnectionService(), new UnusedProxyClient());
        var cmd = new NmapCommand(AppDefinitionMapper.ToManifest(new NmapApp()));

        using var stdoutWriter = new StringWriter();
        using var stderrWriter = new StringWriter();
        var context = CreateContext(["-p", "443", "unknown-host-12345.com"], stdoutWriter, stderrWriter);

        int exitCode = await cmd.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("Host seems down", stdoutWriter.ToString());
    }

    [Theory]
    [InlineData(ProxyTcpProbeState.Open, "open")]
    [InlineData(ProxyTcpProbeState.Closed, "closed")]
    [InlineData(ProxyTcpProbeState.Filtered, "filtered")]
    public async Task NmapCommand_SinglePortAgainstUnknownHost_WithServerConnection_UsesRealTcpProbe(
        ProxyTcpProbeState probeState, string expectedLabel)
    {
        UseServer(new ConnectedServerConnectionService(), new SuccessTcpProbeProxyClient(probeState));
        var cmd = new NmapCommand(AppDefinitionMapper.ToManifest(new NmapApp()));

        using var stdoutWriter = new StringWriter();
        using var stderrWriter = new StringWriter();
        var context = CreateContext(["-p", "8080", "real-external-site.example"], stdoutWriter, stderrWriter);

        int exitCode = await cmd.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(0, exitCode);
        var output = stdoutWriter.ToString();
        Assert.Contains("8080/tcp", output);
        Assert.Contains(expectedLabel, output);
    }

    [Fact]
    public async Task CurlCommand_FetchesPage_And_PrintsSections()
    {
        UseServer(new NeverConnectedServerConnectionService(), new UnusedProxyClient());
        var cmd = new CurlCommand(AppDefinitionMapper.ToManifest(new CurlApp()));

        using var stdoutWriter = new StringWriter();
        using var stderrWriter = new StringWriter();
        var context = CreateContext(["https://hackersearch.net"], stdoutWriter, stderrWriter);

        int exitCode = await cmd.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(0, exitCode);
        var output = stdoutWriter.ToString();
        Assert.Contains("Title: HackerSearch", output);
        Assert.Contains("=== HackerSearch ===", output);
        Assert.Contains("Search the dark corners of the web", output);
    }

    [Fact]
    public async Task CurlCommand_WithHeadersOnly_PrintsHttpStatus()
    {
        UseServer(new NeverConnectedServerConnectionService(), new UnusedProxyClient());
        var cmd = new CurlCommand(AppDefinitionMapper.ToManifest(new CurlApp()));

        using var stdoutWriter = new StringWriter();
        using var stderrWriter = new StringWriter();
        var context = CreateContext(["-I", "https://hackersearch.net"], stdoutWriter, stderrWriter);

        int exitCode = await cmd.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(0, exitCode);
        var output = stdoutWriter.ToString();
        Assert.Contains("HTTP/1.1 200", output);
    }

    // ── ADR 0034 Pass N+1a: curl -I real-network fallback ───────────────────

    [Fact]
    public async Task CurlCommand_HeadersOnly_UnknownHost_WithoutServerConnection_ReportsCannotResolve()
    {
        UseServer(new NeverConnectedServerConnectionService(), new UnusedProxyClient());
        var cmd = new CurlCommand(AppDefinitionMapper.ToManifest(new CurlApp()));

        using var stdoutWriter = new StringWriter();
        using var stderrWriter = new StringWriter();
        var context = CreateContext(["-I", "https://unknown-host-12345.com"], stdoutWriter, stderrWriter);

        int exitCode = await cmd.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(6, exitCode);
        Assert.Contains("Could not resolve host", stderrWriter.ToString());
    }

    [Fact]
    public async Task CurlCommand_HeadersOnly_UnknownHost_WithServerConnection_UsesRealProxyHead()
    {
        UseServer(new ConnectedServerConnectionService(), new SuccessProxyClient());
        var cmd = new CurlCommand(AppDefinitionMapper.ToManifest(new CurlApp()));

        using var stdoutWriter = new StringWriter();
        using var stderrWriter = new StringWriter();
        var context = CreateContext(["-I", "https://real-external-site.example"], stdoutWriter, stderrWriter);

        int exitCode = await cmd.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(0, exitCode);
        var output = stdoutWriter.ToString();
        Assert.Contains("HTTP/1.1 204 No Content", output);
        Assert.Contains("X-Test-Header: proxied", output);
    }

    // ── Body transfer (ADR 0028 follow-up): full-body curl real fallback ────

    [Fact]
    public async Task CurlCommand_FullBody_UnknownHost_WithoutServerConnection_ReportsCannotResolve()
    {
        UseServer(new NeverConnectedServerConnectionService(), new UnusedProxyClient());
        var cmd = new CurlCommand(AppDefinitionMapper.ToManifest(new CurlApp()));

        using var stdoutWriter = new StringWriter();
        using var stderrWriter = new StringWriter();
        var context = CreateContext(["https://unknown-host-12345.com"], stdoutWriter, stderrWriter);

        int exitCode = await cmd.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(6, exitCode);
        Assert.Contains("Could not resolve host", stderrWriter.ToString());
    }

    [Fact]
    public async Task CurlCommand_FullBody_UnknownHost_WithServerConnection_FetchesRealBody()
    {
        UseServer(new ConnectedServerConnectionService(), new SuccessBodyProxyClient(200, "hello from the real internet"));
        var cmd = new CurlCommand(AppDefinitionMapper.ToManifest(new CurlApp()));

        using var stdoutWriter = new StringWriter();
        using var stderrWriter = new StringWriter();
        var context = CreateContext(["https://real-external-site.example"], stdoutWriter, stderrWriter);

        int exitCode = await cmd.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("hello from the real internet", stdoutWriter.ToString());
    }

    [Fact]
    public async Task CurlCommand_Fail_UnknownHost_ServerError_ReportsFailureWithoutPrintingBody()
    {
        UseServer(new ConnectedServerConnectionService(), new SuccessBodyProxyClient(404, "Not Found"));
        var cmd = new CurlCommand(AppDefinitionMapper.ToManifest(new CurlApp()));

        using var stdoutWriter = new StringWriter();
        using var stderrWriter = new StringWriter();
        var context = CreateContext(["-f", "https://real-external-site.example"], stdoutWriter, stderrWriter);

        int exitCode = await cmd.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(22, exitCode);
        Assert.DoesNotContain("Not Found", stdoutWriter.ToString());
    }

    // ── cat URL-reading (ADR 0028 follow-up) ─────────────────────────────────

    private static readonly AppManifest CatManifest = new()
    {
        SchemaVersion = 1,
        Id = "org.hackeros.cmd.cat",
        Name = "cat",
        Version = "1.0.0",
        PublisherId = "pub.hackeros",
        Description = "Concatenate files and print on the standard output",
        Kind = AppKind.Terminal,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.Commands.dll", "Kazo.Os.Commands.Cat.CatCommand"),
        SdkCompatibility = new AppSdkCompatibilityManifest(HackerOsSdk.Version),
        Presentation = new PresentationManifest("utilities", AppLaunchVisibility.Hidden, []),
        Resources = AppResourceProfileManifest.None,
        Terminal = new TerminalCommandManifest("cat", [], "cat [file ...]")
    };

    [Fact]
    public async Task CatCommand_KnownSimulatedHostUrl_RendersPageLikeCurl()
    {
        UseServer(new NeverConnectedServerConnectionService(), new UnusedProxyClient());
        var cmd = new CatCommand(CatManifest);

        using var stdoutWriter = new StringWriter();
        using var stderrWriter = new StringWriter();
        var context = CreateContext(["https://hackersearch.net"], stdoutWriter, stderrWriter);

        int exitCode = await cmd.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("Title: HackerSearch", stdoutWriter.ToString());
    }

    [Fact]
    public async Task CatCommand_UnknownHostUrl_WithoutServerConnection_ReportsCannotResolve()
    {
        UseServer(new NeverConnectedServerConnectionService(), new UnusedProxyClient());
        var cmd = new CatCommand(CatManifest);

        using var stdoutWriter = new StringWriter();
        using var stderrWriter = new StringWriter();
        var context = CreateContext(["https://unknown-host-12345.com"], stdoutWriter, stderrWriter);

        int exitCode = await cmd.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("Could not resolve host", stderrWriter.ToString());
    }

    [Fact]
    public async Task CatCommand_UnknownHostUrl_WithServerConnection_FetchesRealBody()
    {
        UseServer(new ConnectedServerConnectionService(), new SuccessBodyProxyClient(200, "hello from the real internet"));
        var cmd = new CatCommand(CatManifest);

        using var stdoutWriter = new StringWriter();
        using var stderrWriter = new StringWriter();
        var context = CreateContext(["https://real-external-site.example"], stdoutWriter, stderrWriter);

        int exitCode = await cmd.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("hello from the real internet", stdoutWriter.ToString());
    }

    [Fact]
    public async Task CatCommand_PlainPathArgument_NeverTreatedAsUrl()
    {
        // Guards the IsUrl() detection: a bare filename must be routed to the VFS path, not the
        // network path. StubAppExecutionContext.FileSystem throws NotImplementedException, which
        // this test uses as a routing probe -- reaching it (rather than NotSupportedException from
        // UnusedProxyClient/NeverConnectedServerConnectionService) proves the argument was never
        // treated as a URL.
        UseServer(new NeverConnectedServerConnectionService(), new UnusedProxyClient());
        var cmd = new CatCommand(CatManifest);

        using var stdoutWriter = new StringWriter();
        using var stderrWriter = new StringWriter();
        var context = CreateContext(["not-a-url.txt"], stdoutWriter, stderrWriter);

        await Assert.ThrowsAsync<NotImplementedException>(
            () => cmd.ExecuteAsync(context, CancellationToken.None).AsTask());
    }

    // ── P4-W4-007: Proving Zero External Network Requests ──────────────────

    [Fact]
    public void NetworkService_MakesZeroRealSocketsOrHttpCalls()
    {
        // Prove all catalog hosts and DNS records are purely in-memory data structures.
        var hosts = _network.AllHosts;
        Assert.NotEmpty(hosts);

        foreach (var host in hosts)
        {
            Assert.False(string.IsNullOrWhiteSpace(host.Ip));
            Assert.False(string.IsNullOrWhiteSpace(host.Hostname));
            // All operations return immediately synchronously without async socket I/O
            var ping = _network.Ping(host.Hostname);
            if (host.IsUp)
                Assert.NotNull(ping);
        }
    }

    // ── Helper ─────────────────────────────────────────────────────────────

    private IServerConnectionService _serverConnection = new NeverConnectedServerConnectionService();
    private IProxyClient _proxy = new UnusedProxyClient();

    /// <summary>Sets the optional-server state the OS network gateway of the next context sees.</summary>
    private void UseServer(IServerConnectionService serverConnection, IProxyClient proxy)
    {
        _serverConnection = serverConnection;
        _proxy = proxy;
    }

    /// <summary>
    /// Builds a terminal context whose app has the ping/curl/nmap/cat capabilities and a real
    /// <see cref="AppNetworkGateway"/>, and makes it ambient for the rest of the calling test, as the
    /// OS does before running a command (ADR 0043). Called synchronously, so the scope set here stays
    /// on the test's own flow.
    /// </summary>
    private TerminalExecutionContext CreateContext(
        string[] args,
        TextWriter stdout,
        TextWriter stderr)
    {
        var manifest = AppDefinitionMapper.ToManifest(new PingApp());
        var app = new StubAppExecutionContext(
            manifest,
            new AppNetworkGateway(_network, new GrantAllCapabilityChecker(), manifest.Id, _serverConnection, _proxy));
        HackExecutionContext.Enter(app);

        return new TerminalExecutionContext(
            app: app,
            arguments: args.ToImmutableArray(),
            standardInput: TextReader.Null,
            standardOutput: stdout,
            standardError: stderr,
            workingDirectory: "/home/user",
            environment: ImmutableDictionary<string, string>.Empty);
    }

    private sealed class StubAppExecutionContext : IAppExecutionContext
    {
        public StubAppExecutionContext(AppManifest manifest, IAppNetworkGateway network)
        {
            Manifest = manifest;
            Network = network;
        }

        public AppManifest Manifest { get; }
        public IAppNetworkGateway Network { get; }
        public Guid InstanceId { get; } = Guid.NewGuid();
        public string UserId => "user";
        public AppAuthority UserAuthority => AppAuthority.User;
        public IReadOnlySet<string> GrantedCapabilities => new HashSet<string>(Manifest.Capabilities);
        public SessionId SessionId { get; } = SessionId.FromGuid(Guid.NewGuid());
        public ProcessId ProcessId { get; } = ProcessId.FromInt64(1);
        public CancellationToken CancellationToken => CancellationToken.None;
        public ICapabilityChecker Capabilities => throw new NotImplementedException();
        public IAppFileSystemGateway FileSystem => throw new NotImplementedException();
        public IAppEventGateway Events => throw new NotImplementedException();
        public IAppNotificationGateway Notifications => throw new NotImplementedException();
        public IAppLoggingGateway Logging => throw new NotImplementedException();
        public IAppClockGateway Clock => throw new NotImplementedException();
        public IAppProcessGateway Processes => throw new NotImplementedException();
    }

    /// <summary>
    /// Grants every capability except <paramref name="denied"/>, as the OS does for the system app
    /// <c>Kazo.Os.Commands</c> when nothing is denied.
    /// </summary>
    private sealed class GrantAllCapabilityChecker(params string[] denied) : ICapabilityChecker
    {
        public CapabilityPolicyEvaluation Evaluate(
            string capability, AppAuthority requiredAuthority = AppAuthority.User, CapabilityResourceCandidate? resourceCandidate = null) =>
            denied.Contains(capability)
                ? CapabilityPolicyEvaluation.DenyMissing(1)
                : CapabilityPolicyEvaluation.Permit(new CapabilityGrant(
                    CapabilityGrantId.FromGuid(Guid.NewGuid()), "org.hackeros.test", "user", capability, 1, CapabilityGrantSource.UserApproval));

        public void Require(
            string capability, AppAuthority requiredAuthority = AppAuthority.User, CapabilityResourceCandidate? resourceCandidate = null)
        {
            CapabilityPolicyEvaluation evaluation = Evaluate(capability, requiredAuthority, resourceCandidate);
            if (!evaluation.Granted)
            {
                throw new AppGatewayAccessDeniedException(capability, evaluation);
            }
        }
    }

    /// <summary>Fake used only to prove the pure-simulation path is unaffected: this device is never connected.</summary>
    private sealed class NeverConnectedServerConnectionService : IServerConnectionService
    {
        public ValueTask<ServerConnectionState?> GetStateAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ServerConnectionState?>(null);

        public Task<ServerConnectionState> ConnectWithNewAccountAsync(
            Uri serverBaseUrl, string username, string password, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<ServerConnectionState> ConnectWithExistingAccountAsync(
            Uri serverBaseUrl, string username, string password, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public Task<string?> EnsureAccessTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }

    /// <summary>Fake that always throws: proves the real-network path is never reached when disconnected.</summary>
    private sealed class UnusedProxyClient : IProxyClient
    {
        public Task<ProxyHttpResponse> ExecuteHttpRequestAsync(
            Uri serverBaseUrl, string accessToken, ProxyHttpRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The proxy client must not be called when disconnected.");

        public Task<ProxyTcpProbeResponse> ExecuteTcpProbeAsync(
            Uri serverBaseUrl, string accessToken, ProxyTcpProbeRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The proxy client must not be called when disconnected.");

        public Task<ProxyPolicyResponse> GetPolicyAsync(
            Uri serverBaseUrl, string accessToken, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The proxy client must not be called when disconnected.");
    }

    /// <summary>Fake used to prove the real-network path is reached when a device is connected.</summary>
    private sealed class ConnectedServerConnectionService : IServerConnectionService
    {
        private static readonly ServerConnectionState State = new(
            Guid.NewGuid(), Guid.NewGuid(), "https://server.hackeros.test", "fingerprint", "refresh-token",
            DateTimeOffset.UtcNow.AddDays(1));

        public ValueTask<ServerConnectionState?> GetStateAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ServerConnectionState?>(State);

        public Task<ServerConnectionState> ConnectWithNewAccountAsync(
            Uri serverBaseUrl, string username, string password, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<ServerConnectionState> ConnectWithExistingAccountAsync(
            Uri serverBaseUrl, string username, string password, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public Task<string?> EnsureAccessTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("access-token");
    }

    /// <summary>Fake that returns a canned successful proxy response, proving <c>curl -I</c> prints it verbatim.</summary>
    private sealed class SuccessProxyClient : IProxyClient
    {
        public Task<ProxyHttpResponse> ExecuteHttpRequestAsync(
            Uri serverBaseUrl, string accessToken, ProxyHttpRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProxyHttpResponse(
                request.RequestId,
                204,
                "No Content",
                [new ProxyHeader("X-Test-Header", "proxied")],
                BodyHash: null,
                BodyBytes: 0,
                FinalUrl: request.TargetUrl,
                RedirectHops: 0,
                DurationMs: 12));

        public Task<ProxyTcpProbeResponse> ExecuteTcpProbeAsync(
            Uri serverBaseUrl, string accessToken, ProxyTcpProbeRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<ProxyPolicyResponse> GetPolicyAsync(
            Uri serverBaseUrl, string accessToken, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }

    /// <summary>Fake that returns a canned status/body, proving full-body <c>curl</c>'s real fallback prints it.</summary>
    private sealed class SuccessBodyProxyClient(int statusCode, string? body) : IProxyClient
    {
        public Task<ProxyHttpResponse> ExecuteHttpRequestAsync(
            Uri serverBaseUrl, string accessToken, ProxyHttpRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProxyHttpResponse(
                request.RequestId,
                statusCode,
                "OK",
                [],
                BodyHash: null,
                BodyBytes: body?.Length ?? 0,
                FinalUrl: request.TargetUrl,
                RedirectHops: 0,
                DurationMs: 15,
                BodyBase64: request.IncludeBody && body is not null
                    ? Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(body))
                    : null));

        public Task<ProxyTcpProbeResponse> ExecuteTcpProbeAsync(
            Uri serverBaseUrl, string accessToken, ProxyTcpProbeRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<ProxyPolicyResponse> GetPolicyAsync(
            Uri serverBaseUrl, string accessToken, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }

    /// <summary>Fake that returns a canned TCP probe outcome, proving <c>nmap</c>'s real fallback prints it.</summary>
    private sealed class SuccessTcpProbeProxyClient(ProxyTcpProbeState state) : IProxyClient
    {
        public Task<ProxyHttpResponse> ExecuteHttpRequestAsync(
            Uri serverBaseUrl, string accessToken, ProxyHttpRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<ProxyTcpProbeResponse> ExecuteTcpProbeAsync(
            Uri serverBaseUrl, string accessToken, ProxyTcpProbeRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProxyTcpProbeResponse(request.RequestId, state, DurationMs: 8));

        public Task<ProxyPolicyResponse> GetPolicyAsync(
            Uri serverBaseUrl, string accessToken, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }
}
