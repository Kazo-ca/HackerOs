using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Kazo.Os.Platform.Core.ServerConnection;
using Kazo.Os.Server.Contracts.Proxy;
using Kazo.Os.Server.Data;
using Kazo.Os.Server.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Kazo.Os.Server.Tests;

/// <summary>
/// The TCP/UDP stream relay through the real server composition (ADR 0102 §2, §3): the WebSocket
/// routes, the access_token query parameter, and the browser client (<see cref="HttpProxyClient"/>)
/// on the other end, including a refusal reported as a close code.
/// </summary>
[Collection(ServerEnvironmentCollection.Name)]
public sealed class ProxyStreamEndpointTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hackeros-relay-{Guid.NewGuid():N}.db");
    private readonly TcpEchoServer _tcpEcho = new();
    private readonly UdpEchoServer _udpEcho = new();
    private string? _previousConnectionString;
    private RelayApplicationFactory? _factory;
    private string _accessToken = string.Empty;

    public async Task InitializeAsync()
    {
        _previousConnectionString = Environment.GetEnvironmentVariable("HACKEROS_ConnectionStrings__HackerOsDb");
        Environment.SetEnvironmentVariable("HACKEROS_ConnectionStrings__HackerOsDb", $"Data Source={_databasePath}");
        _factory = new RelayApplicationFactory(_tcpEcho.Port, _udpEcho.Port);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        KazoOsServerDbContext db = scope.ServiceProvider.GetRequiredService<KazoOsServerDbContext>();
        Guid accountId = Guid.NewGuid();
        Guid deviceId = Guid.NewGuid();
        db.Accounts.Add(new AccountEntity { AccountId = accountId, Username = "relay-owner", PasswordHash = "h", Salt = "s", CreatedUtc = DateTimeOffset.UtcNow });
        db.Devices.Add(new DeviceEntity { DeviceId = deviceId, AccountId = accountId, DeviceName = "relay", DeviceFingerprint = "fp-relay", RegisteredUtc = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        (_accessToken, _) = await _factory.Services.GetRequiredService<ITokenService>().IssueAccessTokenAsync(accountId, deviceId, CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        _tcpEcho.Dispose();
        _udpEcho.Dispose();
        Environment.SetEnvironmentVariable("HACKEROS_ConnectionStrings__HackerOsDb", _previousConnectionString);
    }

    [Fact]
    public async Task TcpStream_OverTheWebSocket_RoundTripsBytes()
    {
        await using IProxyStreamChannel channel = await Client().OpenStreamAsync(
            ServerUrl, _accessToken, ProxyProtocol.Tcp, new ProxyStreamRequest("echo.example.com", 7, "org.hackeros.test"));

        await channel.SendAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.0\r\n\r\n"));
        byte[]? echoed = await channel.ReceiveAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("93.184.216.34", channel.Remote.RemoteAddress);
        Assert.Equal("GET / HTTP/1.0\r\n\r\n", Encoding.ASCII.GetString(echoed!));
    }

    [Fact]
    public async Task UdpStream_OverTheWebSocket_RoundTripsOneDatagram()
    {
        await using IProxyStreamChannel channel = await Client().OpenStreamAsync(
            ServerUrl, _accessToken, ProxyProtocol.Udp, new ProxyStreamRequest("dns.example.com", 53, "org.hackeros.test"));

        await channel.SendAsync(Encoding.ASCII.GetBytes("dns?"));
        byte[]? datagram = await channel.ReceiveAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("dns?", Encoding.ASCII.GetString(datagram!));
    }

    [Fact]
    public async Task Refusal_ReachesTheClient_AsACloseCodeWithItsErrorCode()
    {
        var ungranted = await Assert.ThrowsAsync<ProxyStreamException>(() => Client().OpenStreamAsync(
            ServerUrl, _accessToken, ProxyProtocol.Tcp, new ProxyStreamRequest("echo.example.com", 7, "org.example.unknown")));
        var blockedPort = await Assert.ThrowsAsync<ProxyStreamException>(() => Client().OpenStreamAsync(
            ServerUrl, _accessToken, ProxyProtocol.Tcp, new ProxyStreamRequest("echo.example.com", 22, "org.hackeros.test")));

        Assert.Equal(ProxyErrorCode.GrantMissing, ungranted.ErrorCode);
        Assert.Equal(ProxyErrorCode.BlockedPort, blockedPort.ErrorCode);
    }

    [Fact]
    public async Task AccessTokenInTheQuery_IsAcceptedOnlyOnTheStreamRoutes()
    {
        using HttpClient http = _factory!.CreateClient();

        using HttpResponseMessage policy = await http.GetAsync($"/api/proxy/policy?{ProxyStreamRoutes.AccessTokenParameter}={_accessToken}");
        using HttpResponseMessage notUpgrade = await http.GetAsync($"/{ProxyStreamRoutes.TcpPath}?{ProxyStreamRoutes.AccessTokenParameter}={_accessToken}");
        using HttpResponseMessage anonymous = await http.GetAsync($"/{ProxyStreamRoutes.TcpPath}");

        Assert.Equal(HttpStatusCode.Unauthorized, policy.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, notUpgrade.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task PolicyEndpoint_ReportsTheConfiguredPolicy()
    {
        using HttpClient http = _factory!.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

        ProxyPolicyResponse policy = await Client(http).GetPolicyAsync(ServerUrl, _accessToken);

        Assert.True(policy.Enabled);
        Assert.Equal([7], policy.TcpPorts!);
        Assert.Contains("org.hackeros.test", policy.GrantedApps!);
    }

    private static Uri ServerUrl => new("http://localhost/");

    private HttpProxyClient Client(HttpClient? http = null)
    {
        WebSocketClient sockets = _factory!.Server.CreateWebSocketClient();
        return new HttpProxyClient(
            http ?? _factory.CreateClient(),
            async (uri, cancellationToken) => await sockets.ConnectAsync(uri, cancellationToken));
    }

    private sealed class RelayApplicationFactory(int tcpEchoPort, int udpEchoPort) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ProxyRelayPolicy>();
                services.AddSingleton(ProxyRelayPolicy.From(new ProxyOptions
                {
                    GrantedApps = ["org.hackeros.test"],
                    Tcp = new ProxyPortOptions { AllowedPorts = [7] },
                    Udp = new ProxyPortOptions { AllowedPorts = [53] }
                }));
                services.RemoveAll<IProxyAddressResolver>();
                services.AddSingleton<IProxyAddressResolver>(new FakeProxyAddressResolver(IPAddress.Parse("93.184.216.34")));
                services.RemoveAll<IProxyStreamConnector>();
                services.AddSingleton<IProxyStreamConnector>(new ProtocolLoopbackConnector(tcpEchoPort, udpEchoPort));
            });
        }
    }

    /// <summary>Sends TCP streams to the TCP echo and UDP sockets to the UDP echo.</summary>
    private sealed class ProtocolLoopbackConnector(int tcpPort, int udpPort) : IProxyStreamConnector
    {
        public Task<System.Net.Sockets.Socket> ConnectAsync(ProxyProtocol protocol, IPAddress address, int port, CancellationToken cancellationToken) =>
            new SocketProxyStreamConnector().ConnectAsync(protocol, IPAddress.Loopback, protocol == ProxyProtocol.Udp ? udpPort : tcpPort, cancellationToken);
    }
}
