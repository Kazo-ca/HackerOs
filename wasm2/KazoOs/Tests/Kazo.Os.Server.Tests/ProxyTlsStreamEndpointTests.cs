using System.Net;
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
/// TLS opened by the relay through the real server composition (ADR 0235 §2): <c>tls=1</c> on the
/// stream WebSocket, the TLS session in the opening frame, and a refused certificate reaching the
/// browser client as <see cref="ProxyStreamCloseCodes.Rejected"/> with <see cref="ProxyErrorCode.TlsFailed"/>.
/// </summary>
[Collection(ServerEnvironmentCollection.Name)]
public sealed class ProxyTlsStreamEndpointTests : IAsyncLifetime
{
    private const string App = "org.kazoos.test";
    private const int TrustedPort = 6697;
    private const int UntrustedPort = 7000;

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"kazoos-tls-relay-{Guid.NewGuid():N}.db");
    private readonly TestCertificates _certificates = new();
    private readonly TlsEchoServer _trusted;
    private readonly TlsEchoServer _untrusted;
    private string? _previousConnectionString;
    private TlsRelayApplicationFactory? _factory;
    private string _accessToken = string.Empty;

    public ProxyTlsStreamEndpointTests()
    {
        _trusted = new TlsEchoServer(_certificates.Signed);
        _untrusted = new TlsEchoServer(_certificates.Untrusted);
    }

    public async Task InitializeAsync()
    {
        _previousConnectionString = Environment.GetEnvironmentVariable("KAZOOS_ConnectionStrings__KazoOsDb");
        Environment.SetEnvironmentVariable("KAZOOS_ConnectionStrings__KazoOsDb", $"Data Source={_databasePath}");
        _factory = new TlsRelayApplicationFactory(_certificates, _trusted.Port, _untrusted.Port);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        KazoOsServerDbContext db = scope.ServiceProvider.GetRequiredService<KazoOsServerDbContext>();
        Guid accountId = Guid.NewGuid();
        Guid deviceId = Guid.NewGuid();
        db.Accounts.Add(new AccountEntity { AccountId = accountId, Username = "tls-owner", PasswordHash = "h", Salt = "s", CreatedUtc = DateTimeOffset.UtcNow });
        db.Devices.Add(new DeviceEntity { DeviceId = deviceId, AccountId = accountId, DeviceName = "tls", DeviceFingerprint = "fp-tls", RegisteredUtc = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        (_accessToken, _) = await _factory.Services.GetRequiredService<ITokenService>().IssueAccessTokenAsync(accountId, deviceId, CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        _trusted.Dispose();
        _untrusted.Dispose();
        _certificates.Dispose();
        Environment.SetEnvironmentVariable("KAZOOS_ConnectionStrings__KazoOsDb", _previousConnectionString);
    }

    [Fact]
    public async Task TlsStream_OverTheWebSocket_CarriesClearText_AndTheSessionInItsOpeningFrame()
    {
        await using IProxyStreamChannel channel = await Client().OpenStreamAsync(
            ServerUrl, _accessToken, ProxyProtocol.Tcp,
            new ProxyStreamRequest(TestCertificates.ServerName, TrustedPort, App) { UseTls = true });

        await channel.SendAsync(Encoding.ASCII.GetBytes("CAP LS 302\r\n"));
        byte[]? echoed = await channel.ReceiveAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("CAP LS 302\r\n", Encoding.ASCII.GetString(echoed!));
        Assert.StartsWith("TLS 1.", channel.Remote.TlsProtocol);
        Assert.Equal($"CN={TestCertificates.ServerName}", channel.Remote.CertificateSubject);
    }

    [Fact]
    public async Task TlsStream_ToAnUntrustedCertificate_ReachesTheClientAsTlsFailed()
    {
        var refused = await Assert.ThrowsAsync<ProxyStreamException>(() => Client().OpenStreamAsync(
            ServerUrl, _accessToken, ProxyProtocol.Tcp,
            new ProxyStreamRequest(TestCertificates.ServerName, UntrustedPort, App) { UseTls = true }));

        Assert.Equal(ProxyErrorCode.TlsFailed, refused.ErrorCode);
    }

    [Fact]
    public async Task TlsStream_WithAnotherTargetHost_ReachesTheClientAsTlsFailed()
    {
        var refused = await Assert.ThrowsAsync<ProxyStreamException>(() => Client().OpenStreamAsync(
            ServerUrl, _accessToken, ProxyProtocol.Tcp,
            new ProxyStreamRequest(TestCertificates.ServerName, TrustedPort, App) { UseTls = true, TargetHost = "other.tls-test.example" }));

        Assert.Equal(ProxyErrorCode.TlsFailed, refused.ErrorCode);
    }

    private static Uri ServerUrl => new("http://localhost/");

    private HttpProxyClient Client()
    {
        WebSocketClient sockets = _factory!.Server.CreateWebSocketClient();
        return new HttpProxyClient(_factory.CreateClient(), async (uri, cancellationToken) => await sockets.ConnectAsync(uri, cancellationToken));
    }

    private sealed class TlsRelayApplicationFactory(TestCertificates certificates, int trustedPort, int untrustedPort) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ProxyRelayPolicy>();
                services.AddSingleton(ProxyRelayPolicy.From(new ProxyOptions
                {
                    GrantedApps = [App],
                    Tcp = new ProxyPortOptions { AllowedPorts = [TrustedPort, UntrustedPort] }
                }));
                services.RemoveAll<IProxyAddressResolver>();
                services.AddSingleton<IProxyAddressResolver>(new FakeProxyAddressResolver(IPAddress.Parse("93.184.216.34")));
                services.RemoveAll<IProxyStreamConnector>();
                services.AddSingleton<IProxyStreamConnector>(new PortLoopbackConnector(trustedPort, untrustedPort));
                services.RemoveAll<IProxyTlsNegotiator>();
                services.AddSingleton<IProxyTlsNegotiator>(new SslStreamProxyTlsNegotiator(certificates.TrustedRoots));
            });
        }
    }

    /// <summary>Sends the trusted port to the echo with the signed certificate, the other to the self-signed one.</summary>
    private sealed class PortLoopbackConnector(int trustedPort, int untrustedPort) : IProxyStreamConnector
    {
        public Task<System.Net.Sockets.Socket> ConnectAsync(ProxyProtocol protocol, IPAddress address, int port, CancellationToken cancellationToken) =>
            new SocketProxyStreamConnector().ConnectAsync(protocol, IPAddress.Loopback, port == TrustedPort ? trustedPort : untrustedPort, cancellationToken);
    }
}
