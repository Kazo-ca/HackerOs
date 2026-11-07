using System.Net;
using System.Text;
using Kazo.Os.Server.Contracts.Proxy;
using Kazo.Os.Server.Contracts.Sync;
using Kazo.Os.Server.Data;
using Kazo.Os.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Kazo.Os.Server.Tests;

// =============================================================================
// ADR 0235 §3 — ports declared by the app: the relay opens a port the app declared and had
// approved (its synchronized grant's realNetworkPorts), on top of AllowedPorts, and never a port
// of BlockedPorts, which wins over every other rule.
// =============================================================================

public sealed class ProxyDeclaredPortsTests : IDisposable
{
    private const string IrcApp = "ca.kazo.irc";
    private readonly KazoOsServerDbContext _db;
    private readonly AuditService _audit;
    private readonly ProxyConnectionPinAccessor _pin = new();
    private readonly FakeHttpMessageHandler _handler;
    private readonly Guid _accountId = Guid.NewGuid();

    public ProxyDeclaredPortsTests()
    {
        _db = new KazoOsServerDbContext(new DbContextOptionsBuilder<KazoOsServerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        _audit = new AuditService(_db);
        _handler = new FakeHttpMessageHandler(_pin);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task DeclaredAndApprovedPort_IsOpened_OnTopOfTheAllowList()
    {
        using TcpEchoServer echo = new();
        Guid device = AddDevice();
        AddGrant(IrcApp, "tcp/6697", "tcp/6667");
        ProxyService proxy = StreamProxy(echo.Port, new ProxyOptions { AllowPrivateNetworks = true });

        await using IProxyStreamChannel channel = await proxy.OpenStreamAsync(
            _accountId, device, ProxyProtocol.Tcp, new ProxyStreamRequest("irc.lan.example", 6697, IrcApp), CancellationToken.None);
        await channel.SendAsync(Encoding.ASCII.GetBytes("PING :kazo\r\n"));

        Assert.Equal(6697, channel.Remote.RemotePort);
    }

    [Fact]
    public async Task DeclaredAndApprovedPort_InBlockedPorts_IsRefused()
    {
        Guid device = AddDevice();
        AddGrant(IrcApp, "tcp/25");

        var ex = await Assert.ThrowsAsync<ProxyRequestException>(() => Proxy().OpenStreamAsync(
            _accountId, device, ProxyProtocol.Tcp, new ProxyStreamRequest("mail.example.com", 25, IrcApp), CancellationToken.None));

        Assert.Equal(ProxyErrorCode.BlockedPort, ex.ErrorCode);
        Assert.Contains("BlockedPorts", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UndeclaredPort_IsRefused_EvenForAGrantedApp()
    {
        Guid device = AddDevice();
        AddGrant(IrcApp, "tcp/6697");

        var ex = await Assert.ThrowsAsync<ProxyRequestException>(() => Proxy().OpenStreamAsync(
            _accountId, device, ProxyProtocol.Tcp, new ProxyStreamRequest("irc.example.com", 6668, IrcApp), CancellationToken.None));

        Assert.Equal(ProxyErrorCode.BlockedPort, ex.ErrorCode);
    }

    [Fact]
    public async Task DeclaredTcpPort_DoesNotOpenTheSameUdpPort()
    {
        Guid device = AddDevice();
        AddGrant(IrcApp, "tcp/6697");

        var ex = await Assert.ThrowsAsync<ProxyRequestException>(() => Proxy().OpenStreamAsync(
            _accountId, device, ProxyProtocol.Udp, new ProxyStreamRequest("irc.example.com", 6697, IrcApp), CancellationToken.None));

        Assert.Equal(ProxyErrorCode.BlockedPort, ex.ErrorCode);
    }

    [Fact]
    public async Task RevokedGrant_NoLongerOpensItsPorts()
    {
        Guid device = AddDevice();
        Guid record = Guid.NewGuid();
        AddGrant(record, 1, IrcApp, revoked: false, "tcp/6697");
        AddGrant(record, 2, IrcApp, revoked: true, "tcp/6697");

        var ex = await Assert.ThrowsAsync<ProxyRequestException>(() => Proxy().OpenStreamAsync(
            _accountId, device, ProxyProtocol.Tcp, new ProxyStreamRequest("irc.example.com", 6697, IrcApp), CancellationToken.None));

        Assert.Equal(ProxyErrorCode.GrantMissing, ex.ErrorCode);
    }

    [Fact]
    public async Task InvalidPortEntries_OfAGrant_AreIgnored_NeverWidened()
    {
        AddGrant(IrcApp, "tcp/0", "tcp/6697-6699", "smtp/25", "tcp/70000", "tcp/6697");

        ProxyAppGrant? grant = await new ProxyGrantAuthority(ProxyRelayPolicy.Default, _db)
            .GetGrantAsync(_accountId, IrcApp, CancellationToken.None);

        Assert.NotNull(grant);
        Assert.Equal([new App.Abstractions.RealNetworkPort("tcp", 6697)], grant.ApprovedPorts);
    }

    [Fact]
    public async Task BlockedPorts_WinsOverAllowedPorts_AndAllowAllPorts()
    {
        Guid device = AddDevice();
        ProxyOptions allowList = new() { GrantedApps = [IrcApp], Tcp = new ProxyPortOptions { AllowedPorts = [25, 443] } };
        ProxyOptions allowAll = new() { GrantedApps = [IrcApp], AllowAllPorts = true };

        foreach (ProxyOptions options in new[] { allowList, allowAll })
        {
            var ex = await Assert.ThrowsAsync<ProxyRequestException>(() => Proxy(options).OpenStreamAsync(
                _accountId, device, ProxyProtocol.Tcp, new ProxyStreamRequest("mail.example.com", 25, IrcApp), CancellationToken.None));
            Assert.Equal(ProxyErrorCode.BlockedPort, ex.ErrorCode);
        }
    }

    [Fact]
    public async Task HttpRequest_ToABlockedTcpPort_IsRefused_EvenWithAllowAllPorts()
    {
        Guid device = AddDevice();
        ProxyHttpRequest request = ProxyHttpRequest.Create(
            Guid.NewGuid(), "http://mail.example.com:25/", "GET", [], null, null, 10, IrcApp, includeBody: true);

        var ex = await Assert.ThrowsAsync<ProxyRequestException>(() => Proxy(new ProxyOptions { GrantedApps = [IrcApp], AllowAllPorts = true })
            .ExecuteHttpRequestAsync(_accountId, device, request, CancellationToken.None));

        Assert.Equal(ProxyErrorCode.BlockedPort, ex.ErrorCode);
    }

    [Fact]
    public void Policy_ShipsBlockedPorts_WithMailAndAmplificationServices()
    {
        ProxyRelayPolicy policy = ProxyRelayPolicy.Default;

        Assert.Contains(25, policy.TcpBlockedPorts);
        Assert.Contains(445, policy.TcpBlockedPorts);
        Assert.Contains(11211, policy.UdpBlockedPorts);
        Assert.DoesNotContain(6697, policy.TcpBlockedPorts);
        Assert.DoesNotContain(53, policy.UdpBlockedPorts);
        Assert.Empty(policy.Weakenings());
    }

    [Fact]
    public void EmptyBlockedPorts_IsWarnedAbout()
    {
        ProxyRelayPolicy policy = ProxyRelayPolicy.From(new ProxyOptions
        {
            Tcp = new ProxyPortOptions { BlockedPorts = [] },
            Udp = new ProxyPortOptions { BlockedPorts = [] }
        });

        Assert.Empty(policy.TcpBlockedPorts);
        Assert.Contains(policy.Weakenings(), warning => warning.Contains("Tcp:BlockedPorts is empty", StringComparison.Ordinal));
        Assert.Contains(policy.Weakenings(), warning => warning.Contains("Udp:BlockedPorts is empty", StringComparison.Ordinal));
    }

    [Fact]
    public void AnExplicitEmptyListInAppSettings_EmptiesBlockedPorts_AndAnAbsentOneKeepsTheDefault()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(
                """{ "KazoOs": { "Proxy": { "Tcp": { "BlockedPorts": [] }, "Udp": { "AllowedPorts": [ 53 ] } } } }""")))
            .Build();
        IConfigurationSection section = configuration.GetSection(ProxyOptions.SectionName);
        ProxyOptions options = section.Get<ProxyOptions>() ?? new ProxyOptions();

        ProxyOptions.KeepExplicitEmptyLists(options, section);
        ProxyRelayPolicy policy = ProxyRelayPolicy.From(options);

        Assert.Empty(policy.TcpBlockedPorts);
        Assert.Equal(ProxyRelayPolicy.DefaultUdpBlockedPorts, policy.UdpBlockedPorts);
    }

    [Fact]
    public async Task PolicyResponse_ListsBlockedPorts_AndTheApprovedPortsOfEachApp()
    {
        Guid device = AddDevice();
        AddGrant(IrcApp, "tcp/6697", "tcp/6667");
        AddGrant("org.example.nodeclared");

        ProxyPolicyResponse policy = await Proxy().GetPolicyAsync(_accountId, device, CancellationToken.None);

        Assert.Equal(ProxyRelayPolicy.DefaultTcpBlockedPorts, policy.TcpBlockedPorts);
        Assert.Equal(ProxyRelayPolicy.DefaultUdpBlockedPorts, policy.UdpBlockedPorts);
        ProxyAppPorts irc = Assert.Single(policy.AppPorts!);
        Assert.Equal(IrcApp, irc.AppId);
        Assert.Equal(["tcp/6667", "tcp/6697"], irc.Ports);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private ProxyService Proxy(ProxyOptions? options = null) =>
        new(new FakeHttpClientFactory(_handler), _audit, _db,
            new FakeProxyAddressResolver(IPAddress.Parse("93.184.216.34")),
            _pin, new FakeProxyTcpConnector(), policy: ProxyRelayPolicy.From(options ?? new ProxyOptions()));

    private ProxyService StreamProxy(int echoPort, ProxyOptions options) =>
        new(new FakeHttpClientFactory(_handler), _audit, _db,
            new FakeProxyAddressResolver(IPAddress.Parse("10.0.0.7")),
            _pin, new FakeProxyTcpConnector(),
            new LoopbackStreamConnector(echoPort),
            ProxyRelayPolicy.From(options));

    private Guid AddDevice()
    {
        Guid device = Guid.NewGuid();
        _db.Devices.Add(new DeviceEntity
        {
            DeviceId = device,
            AccountId = _accountId,
            DeviceName = "Declared ports test device",
            DeviceFingerprint = Guid.NewGuid().ToString("N"),
            RegisteredUtc = DateTimeOffset.UtcNow
        });
        _db.SaveChanges();
        return device;
    }

    private void AddGrant(string appId, params string[] ports) => AddGrant(Guid.NewGuid(), 1, appId, revoked: false, ports);

    private void AddGrant(Guid record, long revision, string appId, bool revoked, params string[] ports)
    {
        string portsJson = ports.Length == 0
            ? string.Empty
            : ",\"realNetworkPorts\":[" + string.Join(",", ports.Select(static port => $"\"{port}\"")) + "]";
        _db.SyncRecords.Add(new SyncRecordEntity
        {
            RecordId = record,
            Domain = SyncDomain.Grants,
            OwnerAccountId = _accountId,
            Revision = revision,
            ContentHash = "hash" + revision,
            PayloadJson = $$"""{"appId":"{{appId}}","userId":"u","capability":"network.real.access","source":"AdministratorApproval","constraints":[],"isRevoked":{{(revoked ? "true" : "false")}}{{portsJson}}}"""
        });
        _db.SaveChanges();
    }
}
