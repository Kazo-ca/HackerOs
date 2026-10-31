using System.Net;
using System.Net.Sockets;
using System.Text;
using HackerOs.Server.Contracts.Proxy;
using HackerOs.Server.Contracts.Sync;
using HackerOs.Server.Data;
using HackerOs.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HackerOs.Server.Tests;

// =============================================================================
// Relay tests — ADR 0102: request bodies, the server-side grant, the appsettings policy
// (ports, private networks, quotas, disabled relay), startup warnings, and TCP/UDP streams
// against local echo servers.
// =============================================================================

public sealed class ProxyRelayTests : IDisposable
{
    private const string TestApp = "org.hackeros.test";
    private readonly HackerOsServerDbContext _db;
    private readonly AuditService _audit;
    private readonly ProxyConnectionPinAccessor _pin = new();
    private readonly FakeHttpMessageHandler _handler;
    private readonly Guid _accountId = Guid.NewGuid();

    public ProxyRelayTests()
    {
        _db = new HackerOsServerDbContext(new DbContextOptionsBuilder<HackerOsServerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        _audit = new AuditService(_db);
        _handler = new FakeHttpMessageHandler(_pin);
    }

    public void Dispose() => _db.Dispose();

    // ── §1 Request bodies ───────────────────────────────────────────────────

    [Fact]
    public async Task Body_IsForwarded_WithItsContentType()
    {
        Guid device = AddDevice();
        byte[] body = Encoding.UTF8.GetBytes("name=benoit&lang=fr");

        await Proxy().ExecuteHttpRequestAsync(_accountId, device,
            Post("https://example.com/form", body, "application/x-www-form-urlencoded"), CancellationToken.None);

        var sent = Assert.Single(_handler.Requests);
        Assert.Equal("POST", sent.Method);
        Assert.Equal(body, sent.Body);
        Assert.Equal("application/x-www-form-urlencoded", sent.ContentType);
    }

    [Fact]
    public async Task Body_ThatDoesNotMatchItsHash_IsRefusedBeforeTransport()
    {
        Guid device = AddDevice();
        ProxyHttpRequest request = Post("https://example.com/", Encoding.UTF8.GetBytes("original"), "text/plain")
            with { Body = Encoding.UTF8.GetBytes("tampered") };

        var ex = await Assert.ThrowsAsync<ProxyRequestException>(() =>
            Proxy().ExecuteHttpRequestAsync(_accountId, device, request, CancellationToken.None));

        Assert.Equal(ProxyErrorCode.BodyMismatch, ex.ErrorCode);
        Assert.Equal(0, _handler.SendCount);
    }

    [Fact]
    public async Task Body_AnnouncedButMissing_IsRefused()
    {
        Guid device = AddDevice();
        ProxyHttpRequest request = Post("https://example.com/", Encoding.UTF8.GetBytes("abc"), "text/plain") with { Body = null };

        var ex = await Assert.ThrowsAsync<ProxyRequestException>(() =>
            Proxy().ExecuteHttpRequestAsync(_accountId, device, request, CancellationToken.None));

        Assert.Equal(ProxyErrorCode.BodyMismatch, ex.ErrorCode);
    }

    [Fact]
    public async Task Body_OverMaxRequestBytes_IsRefused()
    {
        Guid device = AddDevice();
        ProxyService proxy = Proxy(new ProxyOptions { Quotas = new ProxyQuotaOptions { MaxRequestBytes = 4 } });

        var ex = await Assert.ThrowsAsync<ProxyRequestException>(() =>
            proxy.ExecuteHttpRequestAsync(_accountId, device, Post("https://example.com/", new byte[5], "application/octet-stream"), CancellationToken.None));

        Assert.Equal(ProxyErrorCode.PayloadTooLarge, ex.ErrorCode);
    }

    [Fact]
    public async Task SeeOther_AfterPost_ContinuesAsBodilessGet()
    {
        Guid device = AddDevice();
        HttpResponseMessage redirect = new(HttpStatusCode.SeeOther);
        redirect.Headers.Location = new Uri("https://example.com/done");
        _handler.ScriptedResponses.Enqueue(redirect);

        var response = await Proxy().ExecuteHttpRequestAsync(_accountId, device,
            Post("https://example.com/form", Encoding.UTF8.GetBytes("a=1"), "application/x-www-form-urlencoded"), CancellationToken.None);

        Assert.Equal(1, response.RedirectHops);
        Assert.Equal(("GET", (byte[]?)null), (_handler.Requests[1].Method, _handler.Requests[1].Body));
    }

    // ── ADR 0210: real sites in the browser ────────────────────────────────

    [Fact]
    public async Task FollowRedirects_Off_AnswersTheRedirect_WithItsLocationAndCookies()
    {
        Guid device = AddDevice();
        HttpResponseMessage redirect = new(HttpStatusCode.Found);
        redirect.Headers.Location = new Uri("https://example.com/home");
        redirect.Headers.Add("Set-Cookie", "session=abc; Path=/");
        _handler.ScriptedResponses.Enqueue(redirect);

        var response = await Proxy().ExecuteHttpRequestAsync(_accountId, device,
            Get("https://example.com/login") with { FollowRedirects = false }, CancellationToken.None);

        Assert.Equal(302, response.StatusCode);
        Assert.Equal(0, response.RedirectHops);
        Assert.Equal(1, _handler.SendCount);
        Assert.Contains(response.Headers, header => header.Name == "Location" && header.Value == "https://example.com/home");
        Assert.Contains(response.Headers, header => header.Name == "Set-Cookie" && header.Value == "session=abc; Path=/");
    }

    [Fact]
    public async Task CookieHeader_IsRelayed_ButAuthorizationAndForwardingHeadersAreNot()
    {
        Guid device = AddDevice();
        ProxyHttpRequest request = ProxyHttpRequest.Create(Guid.NewGuid(), "https://example.com/", "GET",
            [new("Cookie", "session=abc"), new("Authorization", "Bearer x"), new("X-Forwarded-For", "1.2.3.4")],
            null, null, 10, TestApp, includeBody: true);

        await Proxy().ExecuteHttpRequestAsync(_accountId, device, request, CancellationToken.None);

        var headers = Assert.Single(_handler.RequestHeaders);
        Assert.Equal(["session=abc"], headers.GetValues("Cookie"));
        Assert.False(headers.Contains("Authorization"));
        Assert.False(headers.Contains("X-Forwarded-For"));
    }

    [Fact]
    public async Task DefaultPolicy_GrantsTheBrowser()
    {
        Guid device = AddDevice();
        ProxyService proxy = new(new FakeHttpClientFactory(_handler), _audit, _db,
            new FakeProxyAddressResolver(IPAddress.Parse("93.184.216.34")), _pin, new FakeProxyTcpConnector());

        var response = await proxy.ExecuteHttpRequestAsync(_accountId, device,
            Get("https://example.com/") with { AppId = "org.hackeros.browser" }, CancellationToken.None);

        Assert.Equal(200, response.StatusCode);
    }

    // ── §4 Grant checked by the server ──────────────────────────────────────

    [Fact]
    public async Task App_WithoutServerGrant_IsRefused_WithGrantMissing()
    {
        Guid device = AddDevice();
        ProxyHttpRequest request = Get("https://example.com/") with { AppId = "org.example.unknown" };

        var ex = await Assert.ThrowsAsync<ProxyRequestException>(() =>
            Proxy().ExecuteHttpRequestAsync(_accountId, device, request, CancellationToken.None));

        Assert.Equal(ProxyErrorCode.GrantMissing, ex.ErrorCode);
        Assert.Equal(0, _handler.SendCount);
    }

    [Fact]
    public async Task DefaultPolicy_GrantsTheSystemCommands()
    {
        Guid device = AddDevice();
        ProxyService proxy = new(new FakeHttpClientFactory(_handler), _audit, _db,
            new FakeProxyAddressResolver(IPAddress.Parse("93.184.216.34")), _pin, new FakeProxyTcpConnector());

        var response = await proxy.ExecuteHttpRequestAsync(_accountId, device,
            Get("https://example.com/") with { AppId = "org.hackeros.commands.curl" }, CancellationToken.None);

        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public async Task SynchronizedGrant_OfTheAccount_LetsTheAppThrough_UntilItsLatestRevisionRevokesIt()
    {
        Guid device = AddDevice();
        Guid record = Guid.NewGuid();
        AddGrantRecord(record, 1, "org.example.mail", revoked: false);
        ProxyHttpRequest request = Get("https://example.com/") with { AppId = "org.example.mail" };

        var response = await Proxy().ExecuteHttpRequestAsync(_accountId, device, request, CancellationToken.None);
        Assert.Equal(200, response.StatusCode);

        AddGrantRecord(record, 2, "org.example.mail", revoked: true);
        var ex = await Assert.ThrowsAsync<ProxyRequestException>(() =>
            Proxy().ExecuteHttpRequestAsync(_accountId, device, request, CancellationToken.None));
        Assert.Equal(ProxyErrorCode.GrantMissing, ex.ErrorCode);
    }

    [Fact]
    public async Task SynchronizedGrant_OfAnotherAccount_DoesNotCount()
    {
        Guid device = AddDevice();
        _db.SyncRecords.Add(GrantRecord(Guid.NewGuid(), 1, Guid.NewGuid(), "org.example.mail", revoked: false));
        await _db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<ProxyRequestException>(() =>
            Proxy().ExecuteHttpRequestAsync(_accountId, device, Get("https://example.com/") with { AppId = "org.example.mail" }, CancellationToken.None));

        Assert.Equal(ProxyErrorCode.GrantMissing, ex.ErrorCode);
    }

    // ── §5 Policy ───────────────────────────────────────────────────────────

    [Fact]
    public async Task DisabledRelay_RefusesEverything()
    {
        Guid device = AddDevice();
        ProxyService proxy = Proxy(new ProxyOptions { Enabled = false });

        var ex = await Assert.ThrowsAsync<ProxyRequestException>(() =>
            proxy.ExecuteHttpRequestAsync(_accountId, device, Get("https://example.com/"), CancellationToken.None));
        var streamEx = await Assert.ThrowsAsync<ProxyRequestException>(() =>
            proxy.OpenStreamAsync(_accountId, device, ProxyProtocol.Tcp, new ProxyStreamRequest("example.com", 443, TestApp), CancellationToken.None));

        Assert.Equal(ProxyErrorCode.RelayDisabled, ex.ErrorCode);
        Assert.Equal(ProxyErrorCode.RelayDisabled, streamEx.ErrorCode);
        Assert.Empty((await proxy.GetPolicyAsync(device, CancellationToken.None)).AllowedProtocols);
    }

    [Fact]
    public async Task HttpPorts_ComeFromTheConfiguration_AndAllowAllPortsLiftsTheFilter()
    {
        Guid device = AddDevice();

        var custom = await Proxy(new ProxyOptions { Http = new ProxyPortOptions { AllowedPorts = [8080] } })
            .ExecuteHttpRequestAsync(_accountId, device, Get("http://example.com:8080/"), CancellationToken.None);
        var all = await Proxy(new ProxyOptions { AllowAllPorts = true })
            .ExecuteHttpRequestAsync(_accountId, device, Get("http://example.com:8443/"), CancellationToken.None);
        var ex = await Assert.ThrowsAsync<ProxyRequestException>(() =>
            Proxy(new ProxyOptions { Http = new ProxyPortOptions { AllowedPorts = [8080] } })
                .ExecuteHttpRequestAsync(_accountId, device, Get("https://example.com/"), CancellationToken.None));

        Assert.Equal(200, custom.StatusCode);
        Assert.Equal(200, all.StatusCode);
        Assert.Equal(ProxyErrorCode.BlockedPort, ex.ErrorCode);
    }

    [Theory]
    [InlineData("10.0.0.5", false)]
    [InlineData("192.168.1.20", false)]
    [InlineData("fd00::1", false)]
    [InlineData("127.0.0.1", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("::1", true)]
    public void AllowPrivateNetworks_OpensPrivateRanges_ButNeverLoopbackOrMetadata(string address, bool stillBlocked)
    {
        Assert.True(ProxyService.IsBlockedAddress(IPAddress.Parse(address), allowPrivateNetworks: false));
        Assert.Equal(stillBlocked, ProxyService.IsBlockedAddress(IPAddress.Parse(address), allowPrivateNetworks: true));
    }

    [Fact]
    public async Task DisabledQuotas_LiftTheResponseSizeLimit()
    {
        Guid device = AddDevice();
        _handler.ResponseBody = new byte[11 * 1024 * 1024];

        var response = await Proxy(new ProxyOptions { Quotas = new ProxyQuotaOptions { Enabled = false } })
            .ExecuteHttpRequestAsync(_accountId, device, Get("https://example.com/"), CancellationToken.None);

        Assert.Equal(11 * 1024 * 1024, response.BodyBytes);
    }

    [Fact]
    public void Policy_FillsDefaults_AndListsEachWeakening()
    {
        ProxyRelayPolicy defaults = ProxyRelayPolicy.From(new ProxyOptions());
        ProxyRelayPolicy weak = ProxyRelayPolicy.From(new ProxyOptions
        {
            AllowAllPorts = true,
            AllowPrivateNetworks = true,
            Quotas = new ProxyQuotaOptions { Enabled = false }
        });

        Assert.Empty(defaults.Weakenings());
        Assert.Equal(ProxyRelayPolicy.DefaultGrantedApps, defaults.GrantedApps);
        Assert.Equal(3, weak.Weakenings().Count);
        Assert.Equal(int.MaxValue, weak.EffectiveMaxStreamsPerDevice);
        Assert.Equal(Timeout.InfiniteTimeSpan, weak.EffectiveStreamIdleTimeout);
    }

    [Fact]
    public async Task StartupReporter_WarnsForEachWeakening_AndRemindsSingleUser()
    {
        RecordingLogger<ProxyPolicyStartupReporter> logger = new();
        ProxyPolicyStartupReporter reporter = new(
            ProxyRelayPolicy.From(new ProxyOptions { AllowAllPorts = true, AllowPrivateNetworks = true }), logger);

        await reporter.StartAsync(CancellationToken.None);

        Assert.Equal(3, logger.Entries.Count(entry => entry.Level == LogLevel.Warning));
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("single-user", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartupReporter_IsSilent_WithTheDefaults()
    {
        RecordingLogger<ProxyPolicyStartupReporter> logger = new();

        await new ProxyPolicyStartupReporter(ProxyRelayPolicy.Default, logger).StartAsync(CancellationToken.None);

        Assert.Empty(logger.Entries);
    }

    // ── §2 TCP streams ──────────────────────────────────────────────────────

    [Fact]
    public async Task TcpStream_RoundTripsBytes_ThroughALocalEcho_WithAllowPrivateNetworks()
    {
        using TcpEchoServer echo = new();
        Guid device = AddDevice();
        ProxyService proxy = StreamProxy(echo.Port, new ProxyOptions
        {
            AllowPrivateNetworks = true,
            Tcp = new ProxyPortOptions { AllowedPorts = [7] }
        });

        await using IProxyStreamChannel channel = await proxy.OpenStreamAsync(
            _accountId, device, ProxyProtocol.Tcp, new ProxyStreamRequest("echo.lan.example", 7, TestApp), CancellationToken.None);
        await channel.SendAsync(Encoding.ASCII.GetBytes("hello relay"));
        byte[] echoed = await ReadAtLeastAsync(channel, 11);

        Assert.Equal("10.0.0.7", channel.Remote.RemoteAddress);
        Assert.Equal("hello relay", Encoding.ASCII.GetString(echoed));
        Assert.True(await _db.AuditEntries.AnyAsync(entry => entry.EventCode == "PROXY_TCP_OPEN"));
    }

    [Fact]
    public async Task TcpStream_ToAPrivateAddress_IsBlocked_WithoutAllowPrivateNetworks()
    {
        using TcpEchoServer echo = new();
        Guid device = AddDevice();
        ProxyService proxy = StreamProxy(echo.Port, new ProxyOptions { Tcp = new ProxyPortOptions { AllowedPorts = [7] } });

        var ex = await Assert.ThrowsAsync<ProxyRequestException>(() => proxy.OpenStreamAsync(
            _accountId, device, ProxyProtocol.Tcp, new ProxyStreamRequest("echo.lan.example", 7, TestApp), CancellationToken.None));

        Assert.Equal(ProxyErrorCode.BlockedAddress, ex.ErrorCode);
    }

    [Fact]
    public async Task TcpStream_OnAPortOutsideTheList_IsBlocked()
    {
        Guid device = AddDevice();

        var ex = await Assert.ThrowsAsync<ProxyRequestException>(() => Proxy().OpenStreamAsync(
            _accountId, device, ProxyProtocol.Tcp, new ProxyStreamRequest("example.com", 22, TestApp), CancellationToken.None));

        Assert.Equal(ProxyErrorCode.BlockedPort, ex.ErrorCode);
    }

    [Fact]
    public async Task TcpStream_ClosedByTheRemote_EndsWithRemoteClosed()
    {
        using TcpEchoServer echo = new(closeAfterFirstRead: true);
        Guid device = AddDevice();
        ProxyService proxy = StreamProxy(echo.Port, new ProxyOptions { AllowPrivateNetworks = true, AllowAllPorts = true });

        await using IProxyStreamChannel channel = await proxy.OpenStreamAsync(
            _accountId, device, ProxyProtocol.Tcp, new ProxyStreamRequest("echo.lan.example", 7, TestApp), CancellationToken.None);
        await channel.SendAsync(Encoding.ASCII.GetBytes("bye"));
        await ReadAtLeastAsync(channel, 3);

        Assert.Null(await channel.ReceiveAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(ProxyStreamCloseCodes.RemoteClosed, channel.Closure!.CloseCode);
    }

    [Fact]
    public async Task TcpStream_Idle_IsClosedWithIdleTimeout()
    {
        using TcpEchoServer echo = new();
        Guid device = AddDevice();
        ProxyService proxy = StreamProxy(echo.Port, new ProxyOptions
        {
            AllowPrivateNetworks = true,
            AllowAllPorts = true,
            Quotas = new ProxyQuotaOptions { StreamIdleTimeoutSeconds = 1 }
        });

        await using IProxyStreamChannel channel = await proxy.OpenStreamAsync(
            _accountId, device, ProxyProtocol.Tcp, new ProxyStreamRequest("echo.lan.example", 7, TestApp), CancellationToken.None);

        Assert.Null(await channel.ReceiveAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(ProxyStreamCloseCodes.IdleTimeout, channel.Closure!.CloseCode);
    }

    [Fact]
    public async Task StreamQuota_RefusesTheStreamOverTheLimit_AndFreesTheSlotOnClose()
    {
        using TcpEchoServer echo = new();
        Guid device = AddDevice();
        ProxyService proxy = StreamProxy(echo.Port, new ProxyOptions
        {
            AllowPrivateNetworks = true,
            AllowAllPorts = true,
            Quotas = new ProxyQuotaOptions { MaxStreamsPerDevice = 1 }
        });
        ProxyStreamRequest request = new("echo.lan.example", 7, TestApp);

        IProxyStreamChannel first = await proxy.OpenStreamAsync(_accountId, device, ProxyProtocol.Tcp, request, CancellationToken.None);
        var ex = await Assert.ThrowsAsync<ProxyRequestException>(() =>
            proxy.OpenStreamAsync(_accountId, device, ProxyProtocol.Tcp, request, CancellationToken.None));
        Assert.Equal(1, (await proxy.GetPolicyAsync(device, CancellationToken.None)).CurrentStreams);
        await first.DisposeAsync();
        await using IProxyStreamChannel second = await proxy.OpenStreamAsync(_accountId, device, ProxyProtocol.Tcp, request, CancellationToken.None);

        Assert.Equal(ProxyErrorCode.QuotaExceeded, ex.ErrorCode);
        Assert.Null(second.Closure);
    }

    [Fact]
    public async Task TcpStream_ToAClosedPort_IsRefused_WithConnectionRefused()
    {
        int closedPort;
        using (TcpListener probe = new(IPAddress.Loopback, 0))
        {
            probe.Start();
            closedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        Guid device = AddDevice();
        ProxyService proxy = StreamProxy(closedPort, new ProxyOptions { AllowPrivateNetworks = true, AllowAllPorts = true });

        var ex = await Assert.ThrowsAsync<ProxyRequestException>(() => proxy.OpenStreamAsync(
            _accountId, device, ProxyProtocol.Tcp, new ProxyStreamRequest("echo.lan.example", 7, TestApp), CancellationToken.None));

        Assert.Equal(ProxyErrorCode.ConnectionRefused, ex.ErrorCode);
        Assert.Equal(0, (await proxy.GetPolicyAsync(device, CancellationToken.None)).CurrentStreams);
    }

    // ── §3 UDP ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task UdpStream_RoundTripsOneDatagram_ThroughALocalEcho()
    {
        using UdpEchoServer echo = new();
        Guid device = AddDevice();
        ProxyService proxy = StreamProxy(echo.Port, new ProxyOptions
        {
            AllowPrivateNetworks = true,
            Udp = new ProxyPortOptions { AllowedPorts = [53] }
        });

        await using IProxyStreamChannel channel = await proxy.OpenStreamAsync(
            _accountId, device, ProxyProtocol.Udp, new ProxyStreamRequest("dns.lan.example", 53, TestApp), CancellationToken.None);
        await channel.SendAsync(Encoding.ASCII.GetBytes("query"));
        byte[]? datagram = await channel.ReceiveAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("query", Encoding.ASCII.GetString(datagram!));
        Assert.Equal(ProxyProtocol.Udp, channel.Protocol);
    }

    [Fact]
    public async Task UdpStream_DefaultPorts_AreDnsAndNtpOnly()
    {
        Guid device = AddDevice();

        var ex = await Assert.ThrowsAsync<ProxyRequestException>(() => Proxy().OpenStreamAsync(
            _accountId, device, ProxyProtocol.Udp, new ProxyStreamRequest("example.com", 5353, TestApp), CancellationToken.None));

        Assert.Equal(ProxyErrorCode.BlockedPort, ex.ErrorCode);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private ProxyService Proxy(ProxyOptions? options = null) =>
        new(new FakeHttpClientFactory(_handler), _audit, _db,
            new FakeProxyAddressResolver(IPAddress.Parse("93.184.216.34")),
            _pin, new FakeProxyTcpConnector(), policy: Policy(options));

    /// <summary>A relay whose DNS answers a private address and whose sockets land on a loopback echo.</summary>
    private ProxyService StreamProxy(int echoPort, ProxyOptions options) =>
        new(new FakeHttpClientFactory(_handler), _audit, _db,
            new FakeProxyAddressResolver(IPAddress.Parse("10.0.0.7")),
            _pin, new FakeProxyTcpConnector(),
            new LoopbackStreamConnector(echoPort),
            Policy(options));

    private static ProxyRelayPolicy Policy(ProxyOptions? options)
    {
        options ??= new ProxyOptions();
        options.GrantedApps ??= [TestApp];
        return ProxyRelayPolicy.From(options);
    }

    private Guid AddDevice()
    {
        Guid device = Guid.NewGuid();
        _db.Devices.Add(new DeviceEntity
        {
            DeviceId = device,
            AccountId = _accountId,
            DeviceName = "Relay test device",
            DeviceFingerprint = Guid.NewGuid().ToString("N"),
            RegisteredUtc = DateTimeOffset.UtcNow
        });
        _db.SaveChanges();
        return device;
    }

    private void AddGrantRecord(Guid record, long revision, string appId, bool revoked)
    {
        _db.SyncRecords.Add(GrantRecord(record, revision, _accountId, appId, revoked));
        _db.SaveChanges();
    }

    private static SyncRecordEntity GrantRecord(Guid record, long revision, Guid owner, string appId, bool revoked) => new()
    {
        RecordId = record,
        Domain = SyncDomain.Grants,
        OwnerAccountId = owner,
        Revision = revision,
        ContentHash = "hash" + revision,
        PayloadJson = $$"""{"appId":"{{appId}}","userId":"u","capability":"network.real.access","source":"AdministratorApproval","constraints":[],"isRevoked":{{(revoked ? "true" : "false")}}}"""
    };

    private static ProxyHttpRequest Get(string url) =>
        ProxyHttpRequest.Create(Guid.NewGuid(), url, "GET", [], null, null, 10, TestApp, includeBody: true);

    private static ProxyHttpRequest Post(string url, byte[] body, string contentType) =>
        ProxyHttpRequest.Create(Guid.NewGuid(), url, "POST", [], body, contentType, 10, TestApp, includeBody: true);

    private static async Task<byte[]> ReadAtLeastAsync(IProxyStreamChannel channel, int count)
    {
        List<byte> bytes = [];
        while (bytes.Count < count)
        {
            byte[]? chunk = await channel.ReceiveAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(chunk);
            bytes.AddRange(chunk);
        }

        return [.. bytes];
    }
}

/// <summary>Validates against the fake private address, then really connects to a loopback echo.</summary>
internal sealed class LoopbackStreamConnector(int port) : IProxyStreamConnector
{
    public Task<Socket> ConnectAsync(ProxyProtocol protocol, IPAddress address, int ignoredPort, CancellationToken cancellationToken) =>
        new SocketProxyStreamConnector().ConnectAsync(protocol, IPAddress.Loopback, port, cancellationToken);
}

/// <summary>A one-connection-at-a-time TCP echo on loopback.</summary>
internal sealed class TcpEchoServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();

    public TcpEchoServer(bool closeAfterFirstRead = false)
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception)
                {
                    return;
                }

                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        NetworkStream stream = client.GetStream();
                        byte[] buffer = new byte[4096];
                        try
                        {
                            int read;
                            while ((read = await stream.ReadAsync(buffer, _stop.Token)) > 0)
                            {
                                await stream.WriteAsync(buffer.AsMemory(0, read), _stop.Token);
                                if (closeAfterFirstRead)
                                {
                                    return;
                                }
                            }
                        }
                        catch (Exception)
                        {
                            // Test teardown.
                        }
                    }
                });
            }
        });
    }

    public int Port { get; }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }
}

/// <summary>A UDP echo on loopback.</summary>
internal sealed class UdpEchoServer : IDisposable
{
    private readonly UdpClient _socket = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly CancellationTokenSource _stop = new();

    public UdpEchoServer()
    {
        Port = ((IPEndPoint)_socket.Client.LocalEndPoint!).Port;
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    UdpReceiveResult datagram = await _socket.ReceiveAsync(_stop.Token);
                    await _socket.SendAsync(datagram.Buffer, datagram.RemoteEndPoint, _stop.Token);
                }
                catch (Exception)
                {
                    return;
                }
            }
        });
    }

    public int Port { get; }

    public void Dispose()
    {
        _stop.Cancel();
        _socket.Dispose();
        _stop.Dispose();
    }
}

/// <summary>Collects formatted log entries.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullLogger.Instance.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));
}
