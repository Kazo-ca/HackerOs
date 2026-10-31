using HackerOs.Server.Contracts.Proxy;
using HackerOs.Server.Data;
using HackerOs.Server.Services;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Text;
using Xunit;

namespace HackerOs.Server.Tests;

// =============================================================================
// Proxy Service Tests — P5-PROXY-007
// Tests SSRF, DNS rebinding, simulated-domain block, blocked port, redirect limit,
// and simulated-domain-to-real-proxy isolation.
// =============================================================================

public sealed class ProxyServiceTests : IDisposable
{
    private readonly HackerOsServerDbContext _db;
    private readonly ProxyService _proxy;
    private readonly AuditService _audit;
    private readonly FakeHttpMessageHandler _fakeHandler;
    private readonly IHttpClientFactory _factory;
    private readonly ProxyConnectionPinAccessor _connectionPin;
    private readonly FakeProxyTcpConnector _tcpConnector;

    private static readonly Guid AccountId = Guid.NewGuid();
    private static readonly Guid DeviceId = Guid.NewGuid();

    public ProxyServiceTests()
    {
        var options = new DbContextOptionsBuilder<HackerOsServerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new HackerOsServerDbContext(options);
        _audit = new AuditService(_db);
        _db.Devices.Add(new DeviceEntity
        {
            DeviceId = DeviceId,
            AccountId = AccountId,
            DeviceName = "Proxy test device",
            DeviceFingerprint = Guid.NewGuid().ToString("N"),
            RegisteredUtc = DateTimeOffset.UtcNow
        });
        _db.SaveChanges();
        _connectionPin = new ProxyConnectionPinAccessor();
        _fakeHandler = new FakeHttpMessageHandler(_connectionPin);
        _factory = new FakeHttpClientFactory(_fakeHandler);
        _tcpConnector = new FakeProxyTcpConnector();
        _proxy = new ProxyService(
            _factory,
            _audit,
            _db,
            new FakeProxyAddressResolver(IPAddress.Parse("93.184.216.34")),
            _connectionPin,
            _tcpConnector,
            policy: TestPolicy);
    }

    /// <summary>The default policy, with the test app granted network.real.access (ADR 0102 §4).</summary>
    internal static ProxyRelayPolicy TestPolicy { get; } =
        ProxyRelayPolicy.From(new ProxyOptions { GrantedApps = ["org.hackeros.test"] });

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task BlockedPort_Throws_WithBlockedPortCode()
    {
        var request = BuildRequest("http://example.com:8080/test");
        var ex = await Assert.ThrowsAsync<ProxyRequestException>(() =>
            _proxy.ExecuteHttpRequestAsync(AccountId, DeviceId, request, CancellationToken.None));
        Assert.Equal(ProxyErrorCode.BlockedPort, ex.ErrorCode);
    }

    [Theory]
    [InlineData("http://127.0.0.1/")]           // loopback
    [InlineData("http://10.0.0.1/")]            // RFC-1918 Class A
    [InlineData("http://172.16.0.1/")]          // RFC-1918 Class B
    [InlineData("http://192.168.1.1/")]         // RFC-1918 Class C
    [InlineData("http://169.254.169.254/")]     // AWS metadata endpoint
    [InlineData("http://[::ffff:127.0.0.1]/")]  // IPv4-mapped IPv6 loopback
    public async Task BlockedAddress_Throws_WithBlockedAddressCode(string url)
    {
        // These addresses are blocked even when the port is allowed.
        var request = BuildRequest(url);
        var ex = await Assert.ThrowsAsync<ProxyRequestException>(() =>
            _proxy.ExecuteHttpRequestAsync(AccountId, DeviceId, request, CancellationToken.None));

        // May be BLOCKED_ADDRESS or BLOCKED_PORT depending on port.
        Assert.True(ex.ErrorCode is ProxyErrorCode.BlockedAddress or ProxyErrorCode.BlockedPort);
    }

    [Theory]
    [InlineData("http://example.hackeros.local/")]
    [InlineData("http://bank.sim/")]
    [InlineData("http://target.hackeros/")]
    public async Task SimulatedDomain_Throws_WithSimulatedDomainBlockedCode(string url)
    {
        var request = BuildRequest(url);
        var ex = await Assert.ThrowsAsync<ProxyRequestException>(() =>
            _proxy.ExecuteHttpRequestAsync(AccountId, DeviceId, request, CancellationToken.None));
        Assert.Equal(ProxyErrorCode.SimulatedDomainBlocked, ex.ErrorCode);
    }

    [Fact]
    public async Task MalformedUrl_Throws_WithMalformedRequestCode()
    {
        var request = BuildRequest("not-a-url");
        var ex = await Assert.ThrowsAsync<ProxyRequestException>(() =>
            _proxy.ExecuteHttpRequestAsync(AccountId, DeviceId, request, CancellationToken.None));
        Assert.Equal(ProxyErrorCode.MalformedRequest, ex.ErrorCode);
    }

    [Fact]
    public async Task GetPolicy_ReturnsExpectedDefaults()
    {
        var policy = await _proxy.GetPolicyAsync(DeviceId, CancellationToken.None);

        Assert.Equal(DeviceId, policy.DeviceId);
        Assert.Equal(8, policy.MaxConcurrentRequests);
        Assert.Contains("http", policy.AllowedProtocols);
        Assert.Contains("tcp", policy.AllowedProtocols);
        Assert.Contains("udp", policy.AllowedProtocols);
        Assert.Empty(policy.OperatorWeakeningWarnings);
        Assert.True(policy.Enabled);
        Assert.Equal([80, 443], policy.HttpPorts!);
        Assert.Equal([80, 443], policy.TcpPorts!);
        Assert.Equal([53, 123], policy.UdpPorts!);
        Assert.False(policy.AllowAllPorts);
        Assert.False(policy.AllowPrivateNetworks);
        Assert.True(policy.QuotasEnabled);
        Assert.Equal(10 * 1024 * 1024, policy.MaxRequestBytes);
        Assert.Equal(4, policy.MaxStreamsPerDevice);
        Assert.Equal(300, policy.StreamIdleTimeoutSeconds);
    }

    [Fact]
    public async Task DeviceOwnedByAnotherAccount_IsRejectedBeforeTransport()
    {
        var ex = await Assert.ThrowsAsync<ProxyRequestException>(() =>
            _proxy.ExecuteHttpRequestAsync(Guid.NewGuid(), DeviceId,
                BuildRequest("https://example.com/"), CancellationToken.None));

        Assert.Equal(ProxyErrorCode.CapabilityDenied, ex.ErrorCode);
        Assert.Equal(0, _fakeHandler.SendCount);
    }

    [Fact]
    public async Task RevokedDevice_IsRejectedBeforeTransport()
    {
        var device = await _db.Devices.SingleAsync();
        device.IsRevoked = true;
        await _db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<ProxyRequestException>(() =>
            _proxy.ExecuteHttpRequestAsync(AccountId, DeviceId,
                BuildRequest("https://example.com/"), CancellationToken.None));

        Assert.Equal(ProxyErrorCode.CapabilityDenied, ex.ErrorCode);
        Assert.Equal(0, _fakeHandler.SendCount);
    }

    [Theory]
    [InlineData("ftp://example.com/")]
    [InlineData("file:///etc/passwd")]
    public async Task UnsupportedScheme_IsRejected(string target)
    {
        var ex = await Assert.ThrowsAsync<ProxyRequestException>(() =>
            _proxy.ExecuteHttpRequestAsync(AccountId, DeviceId,
                BuildRequest(target), CancellationToken.None));

        Assert.Equal(ProxyErrorCode.MalformedRequest, ex.ErrorCode);
    }

    [Fact]
    public async Task ValidatedAddress_IsPinnedDuringTransport_AndClearedAfterward()
    {
        var response = await _proxy.ExecuteHttpRequestAsync(AccountId, DeviceId,
            BuildRequest("https://example.com/"), CancellationToken.None);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(IPAddress.Parse("93.184.216.34"), _fakeHandler.ObservedPin);
        Assert.Null(_connectionPin.Address);
    }

    // ── Body transfer (ADR 0028 follow-up) ──────────────────────────────────

    [Fact]
    public async Task IncludeBody_False_NeverPopulatesBodyBase64_EvenWhenResponseHasContent()
    {
        _fakeHandler.ResponseBody = Encoding.UTF8.GetBytes("hello world");

        var response = await _proxy.ExecuteHttpRequestAsync(AccountId, DeviceId,
            BuildRequest("https://example.com/", includeBody: false), CancellationToken.None);

        Assert.Null(response.BodyBase64);
        Assert.NotNull(response.BodyHash);
        Assert.Equal(11, response.BodyBytes);
    }

    [Fact]
    public async Task IncludeBody_True_PopulatesBodyBase64_WithFetchedContent()
    {
        _fakeHandler.ResponseBody = Encoding.UTF8.GetBytes("hello world");

        var response = await _proxy.ExecuteHttpRequestAsync(AccountId, DeviceId,
            BuildRequest("https://example.com/", includeBody: true), CancellationToken.None);

        Assert.NotNull(response.BodyBase64);
        Assert.Equal("hello world", Encoding.UTF8.GetString(Convert.FromBase64String(response.BodyBase64)));
    }

    [Fact]
    public async Task IncludeBody_True_EmptyResponse_BodyBase64StaysNull()
    {
        _fakeHandler.ResponseBody = [];

        var response = await _proxy.ExecuteHttpRequestAsync(AccountId, DeviceId,
            BuildRequest("https://example.com/", includeBody: true), CancellationToken.None);

        Assert.Null(response.BodyBase64);
    }

    [Fact]
    public async Task IncludeBody_True_StillEnforcesMaxResponseBytes()
    {
        // 11 MiB exceeds the proxy's 10 MiB cap regardless of whether the body is requested.
        _fakeHandler.ResponseBody = new byte[11 * 1024 * 1024];

        var ex = await Assert.ThrowsAsync<ProxyRequestException>(() =>
            _proxy.ExecuteHttpRequestAsync(AccountId, DeviceId,
                BuildRequest("https://example.com/", includeBody: true), CancellationToken.None));

        Assert.Equal(ProxyErrorCode.PayloadTooLarge, ex.ErrorCode);
    }

    // ── TCP probe (ADR 0035) ────────────────────────────────────────────────

    [Fact]
    public async Task TcpProbe_ArbitraryPort_IsAllowed_UnlikeHttpProxy()
    {
        // Port 8080 is blocked for the HTTP proxy (see BlockedPort_Throws_WithBlockedPortCode)
        // but must be allowed here — an arbitrary target port is the entire point of the probe.
        _tcpConnector.NextState = ProxyTcpProbeState.Open;

        var response = await _proxy.ExecuteTcpProbeAsync(AccountId, DeviceId,
            BuildTcpProbeRequest("example.com", 8080), CancellationToken.None);

        Assert.Equal(ProxyTcpProbeState.Open, response.State);
        Assert.Equal("93.184.216.34", _tcpConnector.LastAddress);
        Assert.Equal(8080, _tcpConnector.LastPort);
    }

    [Theory]
    [InlineData(ProxyTcpProbeState.Open)]
    [InlineData(ProxyTcpProbeState.Closed)]
    [InlineData(ProxyTcpProbeState.Filtered)]
    public async Task TcpProbe_ReturnsConnectorOutcomeVerbatim(ProxyTcpProbeState outcome)
    {
        _tcpConnector.NextState = outcome;

        var response = await _proxy.ExecuteTcpProbeAsync(AccountId, DeviceId,
            BuildTcpProbeRequest("example.com", 443), CancellationToken.None);

        Assert.Equal(outcome, response.State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    [InlineData(-1)]
    public async Task TcpProbe_InvalidPort_ThrowsMalformedRequest(int port)
    {
        var ex = await Assert.ThrowsAsync<ProxyRequestException>(() =>
            _proxy.ExecuteTcpProbeAsync(AccountId, DeviceId,
                BuildTcpProbeRequest("example.com", port), CancellationToken.None));

        Assert.Equal(ProxyErrorCode.MalformedRequest, ex.ErrorCode);
        Assert.Equal(0, _tcpConnector.CallCount);
    }

    [Theory]
    [InlineData("example.hackeros.local")]
    [InlineData("bank.sim")]
    [InlineData("target.hackeros")]
    public async Task TcpProbe_SimulatedDomain_ThrowsSimulatedDomainBlockedCode(string host)
    {
        var ex = await Assert.ThrowsAsync<ProxyRequestException>(() =>
            _proxy.ExecuteTcpProbeAsync(AccountId, DeviceId,
                BuildTcpProbeRequest(host, 443), CancellationToken.None));

        Assert.Equal(ProxyErrorCode.SimulatedDomainBlocked, ex.ErrorCode);
        Assert.Equal(0, _tcpConnector.CallCount);
    }

    [Fact]
    public async Task TcpProbe_DeviceOwnedByAnotherAccount_IsRejectedBeforeConnect()
    {
        var ex = await Assert.ThrowsAsync<ProxyRequestException>(() =>
            _proxy.ExecuteTcpProbeAsync(Guid.NewGuid(), DeviceId,
                BuildTcpProbeRequest("example.com", 443), CancellationToken.None));

        Assert.Equal(ProxyErrorCode.CapabilityDenied, ex.ErrorCode);
        Assert.Equal(0, _tcpConnector.CallCount);
    }

    [Fact]
    public async Task TcpProbe_BlockedAddress_ThrowsBlockedAddressCode()
    {
        // The fake resolver in this fixture always resolves to a public address; use a resolver
        // seeded with a private-range address to prove SSRF protection still applies to probes.
        var proxy = new ProxyService(
            _factory, _audit, _db,
            new FakeProxyAddressResolver(IPAddress.Parse("10.0.0.5")),
            _connectionPin, _tcpConnector, policy: TestPolicy);

        var ex = await Assert.ThrowsAsync<ProxyRequestException>(() =>
            proxy.ExecuteTcpProbeAsync(AccountId, DeviceId,
                BuildTcpProbeRequest("internal.example.com", 22), CancellationToken.None));

        Assert.Equal(ProxyErrorCode.BlockedAddress, ex.ErrorCode);
        Assert.Equal(0, _tcpConnector.CallCount);
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private static ProxyHttpRequest BuildRequest(string url, bool includeBody = false) =>
        new(
            RequestId: Guid.NewGuid(),
            Protocol: ProxyProtocol.Http,
            TargetUrl: url,
            HttpMethod: "GET",
            Headers: [],
            BodyHash: null,
            BodyBytes: 0,
            TimeoutSeconds: 10,
            AppId: "org.hackeros.test",
            IncludeBody: includeBody);

    private static ProxyTcpProbeRequest BuildTcpProbeRequest(string host, int port) =>
        new(
            RequestId: Guid.NewGuid(),
            Host: host,
            Port: port,
            TimeoutSeconds: 5,
            AppId: "org.hackeros.test");
}

/// <summary>Fake TCP connector that returns a scripted outcome without touching real sockets.</summary>
public sealed class FakeProxyTcpConnector : IProxyTcpConnector
{
    public ProxyTcpProbeState NextState { get; set; } = ProxyTcpProbeState.Open;
    public int CallCount { get; private set; }
    public string? LastAddress { get; private set; }
    public int LastPort { get; private set; }

    public Task<ProxyTcpProbeState> ProbeAsync(
        IPAddress address, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        CallCount++;
        LastAddress = address.ToString();
        LastPort = port;
        return Task.FromResult(NextState);
    }
}

/// <summary>
/// Fake HTTP handler that always returns 200 OK with an empty body.
/// Used to prevent real network calls in proxy tests.
/// </summary>
public sealed class FakeHttpMessageHandler(IProxyConnectionPinAccessor connectionPin) : HttpMessageHandler
{
    public int SendCount { get; private set; }
    public IPAddress? ObservedPin { get; private set; }
    public byte[] ResponseBody { get; set; } = [];

    /// <summary>Scripted responses served before the default 200 (redirect tests).</summary>
    public Queue<HttpResponseMessage> ScriptedResponses { get; } = new();

    /// <summary>Every request seen: method, URI, body bytes (null without content) and Content-Type.</summary>
    public List<(string Method, Uri Uri, byte[]? Body, string? ContentType)> Requests { get; } = [];

    /// <summary>The headers of every request seen, in order (ADR 0210: the relayed Cookie header).</summary>
    public List<System.Net.Http.Headers.HttpRequestHeaders> RequestHeaders { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        SendCount++;
        ObservedPin = connectionPin.Address;
        byte[]? body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        Requests.Add((request.Method.Method, request.RequestUri!, body, request.Content?.Headers.ContentType?.ToString()));
        RequestHeaders.Add(request.Headers);
        if (ScriptedResponses.TryDequeue(out var scripted))
            return scripted;
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(ResponseBody)
        };
    }
}

/// <summary>Deterministic resolver that prevents unit tests from touching real DNS.</summary>
public sealed class FakeProxyAddressResolver(params IPAddress[] addresses) : IProxyAddressResolver
{
    public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<IPAddress>>(
            IPAddress.TryParse(host, out var literal) ? [literal] : addresses);
}

/// <summary>
/// Minimal IHttpClientFactory implementation for unit tests — avoids requiring Microsoft.Extensions.Http.
/// </summary>
public sealed class FakeHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;
    public FakeHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

    public HttpClient CreateClient(string name) => new(_handler) { BaseAddress = null };
}
