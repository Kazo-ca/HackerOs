using System.Net;
using System.Text;
using Kazo.Os.Server.Contracts.Proxy;
using Kazo.Os.Server.Contracts.Sync;
using Kazo.Os.Server.Data;
using Kazo.Os.Server.Endpoints;
using Kazo.Os.Server.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kazo.Os.Server.Tests;

// =============================================================================
// ADR 0233 — the server issues the network.real.access grant an OS administrator approved, with
// its ports (POST /api/proxy/grants): one stable grants record per account, app and user, written
// by the server itself, read back by the relay; KazoOs:Proxy:AcceptOsGrants closes the door.
// =============================================================================

public sealed class ProxyGrantIssuerTests : IDisposable
{
    private const string IrcApp = "ca.kazo.irc";
    private const string User = "alice";
    private readonly KazoOsServerDbContext _db;
    private readonly AuditService _audit;
    private readonly ProxyConnectionPinAccessor _pin = new();
    private readonly Guid _accountId = Guid.NewGuid();
    private readonly Guid _device;

    public ProxyGrantIssuerTests()
    {
        _db = new KazoOsServerDbContext(new DbContextOptionsBuilder<KazoOsServerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        _audit = new AuditService(_db);
        _device = AddDevice(_accountId);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Issue_WritesOneGrantRecord_ThatTheRelayReadsWithItsPorts()
    {
        ProxyGrantIssued issued = await Issuer().IssueAsync(_accountId, _device, Request("tcp/6697", "TCP/6667"), CancellationToken.None);

        Assert.True(issued.Changed);
        Assert.Equal(1, issued.Revision);
        Assert.Equal(["tcp/6667", "tcp/6697"], issued.RealNetworkPorts);
        SyncRecordEntity record = Assert.Single(_db.SyncRecords.AsNoTracking());
        Assert.Equal(SyncDomain.Grants, record.Domain);
        Assert.Equal(ProxyGrantIssuer.RecordIdOf(_accountId, IrcApp, User), record.RecordId);
        Assert.Contains("\"source\":\"AdministratorApproval\"", record.PayloadJson, StringComparison.Ordinal);
        ProxyAppGrant? grant = await Authority().GetGrantAsync(_accountId, IrcApp, CancellationToken.None);
        Assert.NotNull(grant);
        Assert.True(grant.Approves("tcp", 6697));
        Assert.True(grant.Approves("tcp", 6667));
    }

    [Fact]
    public async Task AnIdenticalRequest_WritesNothing()
    {
        await Issuer().IssueAsync(_accountId, _device, Request("tcp/6697"), CancellationToken.None);

        ProxyGrantIssued again = await Issuer().IssueAsync(_accountId, _device, Request("tcp/6697"), CancellationToken.None);

        Assert.False(again.Changed);
        Assert.Equal(1, again.Revision);
        Assert.Single(_db.SyncRecords.AsNoTracking());
        Assert.Single(_db.AuditEntries.AsNoTracking(), entry => entry.EventCode == "PROXY_GRANT_ISSUE");
    }

    [Fact]
    public async Task ANewApproval_IsANewRevisionOfTheSameRecord_AndReplacesThePorts()
    {
        await Issuer().IssueAsync(_accountId, _device, Request("tcp/6697"), CancellationToken.None);

        ProxyGrantIssued second = await Issuer().IssueAsync(_accountId, _device, Request("tcp/6667"), CancellationToken.None);

        Assert.Equal(2, second.Revision);
        Assert.Equal(2, _db.SyncRecords.AsNoTracking().Count(record => record.RecordId == second.RecordId));
        ProxyAppGrant? grant = await Authority().GetGrantAsync(_accountId, IrcApp, CancellationToken.None);
        Assert.True(grant!.Approves("tcp", 6667));
        Assert.False(grant.Approves("tcp", 6697));
    }

    [Fact]
    public async Task Revoke_WithdrawsTheGrant_AndIsAudited()
    {
        await Issuer().IssueAsync(_accountId, _device, Request("tcp/6697"), CancellationToken.None);

        ProxyGrantIssued revoked = await Issuer().IssueAsync(_accountId, _device, Request() with { Revoked = true }, CancellationToken.None);

        Assert.True(revoked.Changed);
        Assert.Null(await Authority().GetGrantAsync(_accountId, IrcApp, CancellationToken.None));
        Assert.Single(_db.AuditEntries.AsNoTracking(), entry => entry.EventCode == "PROXY_GRANT_REVOKE");
    }

    [Fact]
    public async Task RevokingAGrantThatWasNeverIssued_WritesNothing()
    {
        ProxyGrantIssued revoked = await Issuer().IssueAsync(_accountId, _device, Request() with { Revoked = true }, CancellationToken.None);

        Assert.False(revoked.Changed);
        Assert.Empty(_db.SyncRecords.AsNoTracking());
    }

    [Theory]
    [InlineData("tcp/0")]
    [InlineData("tcp/6697-6699")]
    [InlineData("smtp/25")]
    [InlineData("tcp/070")]
    public async Task ABadlyWrittenPort_IsRefused(string port)
    {
        var ex = await Assert.ThrowsAsync<ProxyRequestException>(() =>
            Issuer().IssueAsync(_accountId, _device, Request(port), CancellationToken.None));

        Assert.Equal(ProxyErrorCode.MalformedRequest, ex.ErrorCode);
        Assert.Empty(_db.SyncRecords.AsNoTracking());
    }

    [Fact]
    public async Task ADuplicatePort_OrTooManyPorts_IsRefused()
    {
        var duplicate = await Assert.ThrowsAsync<ProxyRequestException>(() =>
            Issuer().IssueAsync(_accountId, _device, Request("tcp/6697", "TCP/6697"), CancellationToken.None));
        string[] many = [.. Enumerable.Range(1000, ProxyGrantIssuer.MaxPorts + 1).Select(static port => $"tcp/{port}")];
        var tooMany = await Assert.ThrowsAsync<ProxyRequestException>(() =>
            Issuer().IssueAsync(_accountId, _device, Request(many), CancellationToken.None));

        Assert.Equal(ProxyErrorCode.MalformedRequest, duplicate.ErrorCode);
        Assert.Equal(ProxyErrorCode.MalformedRequest, tooMany.ErrorCode);
    }

    [Fact]
    public async Task AMissingAppOrUser_IsRefused()
    {
        var noApp = await Assert.ThrowsAsync<ProxyRequestException>(() =>
            Issuer().IssueAsync(_accountId, _device, Request() with { AppId = " " }, CancellationToken.None));
        var noUser = await Assert.ThrowsAsync<ProxyRequestException>(() =>
            Issuer().IssueAsync(_accountId, _device, Request() with { UserId = "" }, CancellationToken.None));

        Assert.Equal(ProxyErrorCode.CapabilityDenied, noApp.ErrorCode);
        Assert.Equal(ProxyErrorCode.MalformedRequest, noUser.ErrorCode);
    }

    [Fact]
    public async Task ADeviceOfAnotherAccount_IsRefused()
    {
        Guid stranger = AddDevice(Guid.NewGuid());

        var ex = await Assert.ThrowsAsync<ProxyRequestException>(() =>
            Issuer().IssueAsync(_accountId, stranger, Request("tcp/6697"), CancellationToken.None));

        Assert.Equal(ProxyErrorCode.CapabilityDenied, ex.ErrorCode);
    }

    [Fact]
    public async Task APortOfBlockedPorts_IsKeptInTheGrant_ButStillRefusedOnUse()
    {
        await Issuer().IssueAsync(_accountId, _device, Request("tcp/25"), CancellationToken.None);

        ProxyAppGrant? grant = await Authority().GetGrantAsync(_accountId, IrcApp, CancellationToken.None);
        var ex = await Assert.ThrowsAsync<ProxyRequestException>(() => Proxy().OpenStreamAsync(
            _accountId, _device, ProxyProtocol.Tcp, new ProxyStreamRequest("mail.example.com", 25, IrcApp), CancellationToken.None));

        Assert.True(grant!.Approves("tcp", 25));
        Assert.Equal(ProxyErrorCode.BlockedPort, ex.ErrorCode);
    }

    [Fact]
    public async Task TheRelay_OpensTheIssuedPort()
    {
        using TcpEchoServer echo = new();
        await Issuer().IssueAsync(_accountId, _device, Request("tcp/6697"), CancellationToken.None);
        ProxyService proxy = new(new FakeHttpClientFactory(new FakeHttpMessageHandler(_pin)), _audit, _db,
            new FakeProxyAddressResolver(IPAddress.Parse("10.0.0.7")),
            _pin, new FakeProxyTcpConnector(),
            new LoopbackStreamConnector(echo.Port),
            ProxyRelayPolicy.From(new ProxyOptions { AllowPrivateNetworks = true }));

        await using IProxyStreamChannel channel = await proxy.OpenStreamAsync(
            _accountId, _device, ProxyProtocol.Tcp, new ProxyStreamRequest("irc.lan.example", 6697, IrcApp), CancellationToken.None);
        await channel.SendAsync(Encoding.ASCII.GetBytes("PING :kazo\r\n"));

        Assert.Equal(6697, channel.Remote.RemotePort);
    }

    [Fact]
    public async Task AcceptOsGrantsOff_RefusesTheRoute_AndTheRelayIgnoresSynchronizedGrants()
    {
        await Issuer().IssueAsync(_accountId, _device, Request("tcp/6697"), CancellationToken.None);
        ProxyRelayPolicy closed = ProxyRelayPolicy.From(new ProxyOptions { AcceptOsGrants = false });

        var ex = await Assert.ThrowsAsync<ProxyRequestException>(() =>
            Issuer(closed).IssueAsync(_accountId, _device, Request("tcp/6667"), CancellationToken.None));

        Assert.Equal(ProxyErrorCode.GrantIssuingDisabled, ex.ErrorCode);
        Assert.Equal(403, ProxyEndpoints.StatusCodeOf(ex.ErrorCode));
        Assert.Null(await new ProxyGrantAuthority(closed, _db).GetGrantAsync(_accountId, IrcApp, CancellationToken.None));
        Assert.NotNull(await new ProxyGrantAuthority(closed, _db).GetGrantAsync(_accountId, "org.kazoos.commands.curl", CancellationToken.None));
    }

    [Fact]
    public void AcceptOsGrantsWithAllowAllPorts_IsWarnedAbout()
    {
        Assert.Contains(ProxyRelayPolicy.From(new ProxyOptions { AllowAllPorts = true }).Weakenings(),
            static warning => warning.StartsWith("KazoOs:Proxy:AcceptOsGrants and AllowAllPorts are both on", StringComparison.Ordinal));
        Assert.DoesNotContain(ProxyRelayPolicy.From(new ProxyOptions { AllowAllPorts = true, AcceptOsGrants = false }).Weakenings(),
            static warning => warning.StartsWith("KazoOs:Proxy:AcceptOsGrants", StringComparison.Ordinal));
        Assert.True(ProxyRelayPolicy.Default.AcceptOsGrants);
    }

    [Fact]
    public void TheRecordId_IsStablePerAccountAppAndUser()
    {
        Assert.Equal(ProxyGrantIssuer.RecordIdOf(_accountId, IrcApp, User), ProxyGrantIssuer.RecordIdOf(_accountId, IrcApp, User));
        Assert.NotEqual(ProxyGrantIssuer.RecordIdOf(_accountId, IrcApp, User), ProxyGrantIssuer.RecordIdOf(_accountId, IrcApp, "bob"));
        Assert.NotEqual(ProxyGrantIssuer.RecordIdOf(_accountId, IrcApp, User), ProxyGrantIssuer.RecordIdOf(Guid.NewGuid(), IrcApp, User));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static ProxyGrantIssueRequest Request(params string[] ports) => new(IrcApp, User, ports);

    private ProxyGrantIssuer Issuer(ProxyRelayPolicy? policy = null) => new(policy ?? ProxyRelayPolicy.Default, _db, _audit);

    private ProxyGrantAuthority Authority() => new(ProxyRelayPolicy.Default, _db);

    private ProxyService Proxy() =>
        new(new FakeHttpClientFactory(new FakeHttpMessageHandler(_pin)), _audit, _db,
            new FakeProxyAddressResolver(IPAddress.Parse("93.184.216.34")),
            _pin, new FakeProxyTcpConnector(), policy: ProxyRelayPolicy.Default);

    private Guid AddDevice(Guid accountId)
    {
        Guid device = Guid.NewGuid();
        _db.Devices.Add(new DeviceEntity
        {
            DeviceId = device,
            AccountId = accountId,
            DeviceName = "Grant issuer test device",
            DeviceFingerprint = Guid.NewGuid().ToString("N"),
            RegisteredUtc = DateTimeOffset.UtcNow
        });
        _db.SaveChanges();
        return device;
    }
}
