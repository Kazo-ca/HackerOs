using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.AppSdk.Administration;
using Kazo.Os.Platform.Core.Administration;
using Kazo.Os.Platform.Core.ServerConnection;
using Kazo.Os.Server.Contracts.Proxy;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.ServerConnection;
using Xunit;

namespace Kazo.Os.Platform.Core.Tests.Administration;

/// <summary>
/// ADR 0102 §5: Settings reads the connected server's relay policy through
/// <see cref="IAppServerConnectionGateway.GetRelayPolicyAsync"/>, with the access token kept inside the OS.
/// </summary>
public sealed class ServerRelayPolicyGatewayTests
{
    [Fact]
    public async Task Relay_policy_is_null_without_a_server_or_a_connection()
    {
        AppServerConnectionGateway noHost = new(services: null, new Capabilities(AppCapabilities.ServerConnectionManage));
        AppServerConnectionGateway disconnected = new(
            new ServerConnectionGatewayServices(() => new Connection(connected: false), Proxy: () => new PolicyClient()),
            new Capabilities(AppCapabilities.ServerConnectionManage));

        Assert.Null(await noHost.GetRelayPolicyAsync());
        Assert.Null(await disconnected.GetRelayPolicyAsync());
    }

    [Fact]
    public async Task Relay_policy_maps_the_server_answer_and_needs_the_capability()
    {
        PolicyClient client = new();
        ServerConnectionGatewayServices services = new(() => new Connection(connected: true), Proxy: () => client);
        AppServerConnectionGateway denied = new(services, new Capabilities());
        AppServerConnectionGateway granted = new(services, new Capabilities(AppCapabilities.ServerConnectionManage));

        KazoServerRelayPolicy? policy = await granted.GetRelayPolicyAsync();

        await Assert.ThrowsAsync<AppGatewayAccessDeniedException>(() => denied.GetRelayPolicyAsync().AsTask());
        Assert.NotNull(policy);
        Assert.Equal("token-1", client.SeenToken);
        Assert.Equal([53, 123], policy.UdpPorts);
        Assert.True(policy.AllowPrivateNetworks);
        Assert.Equal(TimeSpan.FromSeconds(300), policy.StreamIdleTimeout);
        Assert.Equal(["warning"], policy.Warnings);
        Assert.Equal(1, policy.OpenStreams);
        // ADR 0235 section 3: block-lists and the approved ports of each app reach Settings.
        Assert.Equal([25, 445], policy.TcpBlockedPorts);
        Assert.Empty(policy.UdpBlockedPorts);
        KazoServerAppPorts irc = Assert.Single(policy.AppPorts);
        Assert.Equal("ca.kazo.irc", irc.AppId);
        Assert.Equal(["tcp/6667", "tcp/6697"], irc.Ports);
    }

    private sealed class Capabilities(params string[] granted) : ICapabilityChecker
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

    private sealed class Connection(bool connected) : IServerConnectionService
    {
        public ValueTask<ServerConnectionState?> GetStateAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(connected
                ? new ServerConnectionState(Guid.NewGuid(), Guid.NewGuid(), "https://server.example/", "fp", "refresh", DateTimeOffset.UtcNow.AddDays(1))
                : null);

        public Task<ServerConnectionState> ConnectWithNewAccountAsync(Uri serverBaseUrl, string username, string password, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ServerConnectionState> ConnectWithExistingAccountAsync(Uri serverBaseUrl, string username, string password, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public Task<string?> EnsureAccessTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(connected ? "token-1" : null);
    }

    private sealed class PolicyClient : IProxyClient
    {
        public string? SeenToken { get; private set; }

        public Task<ProxyHttpResponse> ExecuteHttpRequestAsync(Uri serverBaseUrl, string accessToken, ProxyHttpRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ProxyTcpProbeResponse> ExecuteTcpProbeAsync(Uri serverBaseUrl, string accessToken, ProxyTcpProbeRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ProxyPolicyResponse> GetPolicyAsync(Uri serverBaseUrl, string accessToken, CancellationToken cancellationToken = default)
        {
            SeenToken = accessToken;
            return Task.FromResult(new ProxyPolicyResponse(
                Guid.NewGuid(), 8, 0, 10_485_760, 30, 5, ["http", "https", "tcp", "udp"], 30, ["warning"],
                Enabled: true,
                HttpPorts: [80, 443],
                TcpPorts: [80, 443],
                UdpPorts: [53, 123],
                AllowPrivateNetworks: true,
                MaxRequestBytes: 10_485_760,
                MaxStreamsPerDevice: 4,
                CurrentStreams: 1,
                StreamIdleTimeoutSeconds: 300,
                GrantedApps: ["org.kazoos.commands.curl"])
            {
                TcpBlockedPorts = [25, 445],
                UdpBlockedPorts = [],
                AppPorts = [new ProxyAppPorts("ca.kazo.irc", ["tcp/6667", "tcp/6697"])]
            });
        }
    }
}
