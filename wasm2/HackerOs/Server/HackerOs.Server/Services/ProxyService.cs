using System.Net;
using HackerOs.Server.Contracts.Proxy;
using HackerOs.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace HackerOs.Server.Services;

/// <summary>
/// Server-side validated network relay with SSRF protection: HTTP requests with their bodies, a
/// single-port TCP probe, and TCP/UDP streams (P5-PROXY-001 to 007, ADR 0035, ADR 0102).
///
/// Security model:
///   - All policy is validated SERVER-SIDE. Client claims are never trusted; the app must hold
///     network.real.access on the server (<see cref="IProxyGrantAuthority"/>, ADR 0102 §4).
///   - Always blocked: loopback, link-local and cloud metadata, unspecified, multicast; private
///     ranges unless HackerOs:Proxy:AllowPrivateNetworks.
///   - Ports: per-protocol allow-lists from HackerOs:Proxy (HTTP 80/443, TCP 80/443, UDP 53/123)
///     unless AllowAllPorts.
///   - Max redirects: 5 (server follows; client sees final URL).
///   - Simulated domain suffixes (.hackeros.local, .sim, .hackeros) → always refused.
///   - DNS rebinding: validate every DNS answer and pin the selected address for the socket connect.
/// </summary>
public interface IProxyService
{
    Task<ProxyHttpResponse> ExecuteHttpRequestAsync(
        Guid accountId, Guid deviceId, ProxyHttpRequest request, CancellationToken ct);

    /// <summary>
    /// Executes a single-port TCP reachability probe (ADR 0035) — one connect attempt against one
    /// host:port, no data exchanged. Reuses the same SSRF address-range and simulated-domain
    /// protections as the HTTP proxy, but does not restrict the target port to 80/443, since an
    /// arbitrary-port probe is the entire point.
    /// </summary>
    Task<ProxyTcpProbeResponse> ExecuteTcpProbeAsync(
        Guid accountId, Guid deviceId, ProxyTcpProbeRequest request, CancellationToken ct);

    Task<ProxyPolicyResponse> GetPolicyAsync(Guid deviceId, CancellationToken ct);

    /// <summary>
    /// Validates and opens one TCP or UDP stream (ADR 0102 §2, §3): the socket is connected when
    /// this returns, and closing the returned channel closes it and frees the device's stream slot.
    /// </summary>
    /// <exception cref="ProxyRequestException">The request is refused or the target cannot be reached.</exception>
    Task<IProxyStreamChannel> OpenStreamAsync(
        Guid accountId, Guid deviceId, ProxyProtocol protocol, ProxyStreamRequest request, CancellationToken ct);
}

/// <inheritdoc />
public sealed class ProxyService : IProxyService
{
    // Simulated-domain suffixes that must never reach this proxy (P5-PROXY-006).
    private static readonly string[] SimulatedSuffixes = [".hackeros.local", ".sim", ".hackeros"];

    // Ranges no setting opens: the server itself, link-local and cloud metadata endpoints,
    // unspecified, documentation/benchmark and multicast addresses.
    private static readonly (IPAddress Network, int PrefixLength)[] AlwaysBlockedRanges =
    [
        (IPAddress.Parse("127.0.0.0"), 8),
        (IPAddress.Parse("169.254.0.0"), 16),     // link-local / cloud metadata
        (IPAddress.Parse("0.0.0.0"), 8),           // unspecified/current network
        (IPAddress.Parse("192.0.2.0"), 24),        // documentation
        (IPAddress.Parse("198.18.0.0"), 15),       // benchmark testing
        (IPAddress.Parse("198.51.100.0"), 24),     // documentation
        (IPAddress.Parse("203.0.113.0"), 24),      // documentation
        (IPAddress.Parse("224.0.0.0"), 4),         // multicast/reserved
        (IPAddress.Parse("::"), 128),              // IPv6 unspecified
        (IPAddress.Parse("::1"), 128),             // IPv6 loopback
        (IPAddress.Parse("fe80::"), 10),           // IPv6 link-local
        (IPAddress.Parse("ff00::"), 8),             // IPv6 multicast
    ];

    // Private ranges: the local network of the server's machine, open with AllowPrivateNetworks.
    private static readonly (IPAddress Network, int PrefixLength)[] PrivateRanges =
    [
        (IPAddress.Parse("10.0.0.0"), 8),
        (IPAddress.Parse("172.16.0.0"), 12),
        (IPAddress.Parse("192.168.0.0"), 16),
        (IPAddress.Parse("100.64.0.0"), 10),       // CGNAT
        (IPAddress.Parse("fc00::"), 7),            // IPv6 ULA
    ];

    // TCP probe timeout is clamped much shorter than the HTTP proxy's — a scan-shaped operation
    // should fail fast rather than hold a connection slot for up to 30 seconds per port.
    private const int MaxTcpProbeTimeoutSeconds = 5;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAuditService _audit;
    private readonly HackerOsServerDbContext _db;
    private readonly IProxyAddressResolver _addressResolver;
    private readonly IProxyConnectionPinAccessor _connectionPin;
    private readonly IProxyTcpConnector _tcpConnector;
    private readonly IProxyStreamConnector _streamConnector;
    private readonly ProxyRelayPolicy _policy;
    private readonly IProxyGrantAuthority _grants;

    // Simple in-memory concurrency trackers per device: requests in flight, streams open.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, int>
        _activeCounts = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, int>
        _streamCounts = new();

    /// <summary>Creates the relay of one request scope.</summary>
    /// <param name="httpClientFactory">Factory of the pinned "proxy" HTTP client.</param>
    /// <param name="audit">Audit log.</param>
    /// <param name="db">Server database (devices, synchronized grants).</param>
    /// <param name="addressResolver">DNS boundary.</param>
    /// <param name="connectionPin">Carries the validated address to the HTTP connect callback.</param>
    /// <param name="tcpConnector">Single-port probe boundary (ADR 0035).</param>
    /// <param name="streamConnector">TCP/UDP stream socket boundary; real sockets when omitted.</param>
    /// <param name="policy">Effective relay policy; the defaults of ADR 0102 §5 when omitted.</param>
    /// <param name="grants">Server-side grant check; built from <paramref name="policy"/> and <paramref name="db"/> when omitted.</param>
    public ProxyService(
        IHttpClientFactory httpClientFactory,
        IAuditService audit,
        HackerOsServerDbContext db,
        IProxyAddressResolver addressResolver,
        IProxyConnectionPinAccessor connectionPin,
        IProxyTcpConnector tcpConnector,
        IProxyStreamConnector? streamConnector = null,
        ProxyRelayPolicy? policy = null,
        IProxyGrantAuthority? grants = null)
    {
        _httpClientFactory = httpClientFactory;
        _audit = audit;
        _db = db;
        _addressResolver = addressResolver;
        _connectionPin = connectionPin;
        _tcpConnector = tcpConnector;
        _streamConnector = streamConnector ?? new SocketProxyStreamConnector();
        _policy = policy ?? ProxyRelayPolicy.Default;
        _grants = grants ?? new ProxyGrantAuthority(_policy, db);
    }

    /// <inheritdoc />
    public async Task<ProxyHttpResponse> ExecuteHttpRequestAsync(
        Guid accountId, Guid deviceId, ProxyHttpRequest request, CancellationToken ct)
    {
        // Authenticate against durable server state rather than trusting token
        // identifiers after a device is revoked or moved to another account.
        EnsureEnabled();
        await ValidateDeviceOwnershipAsync(accountId, deviceId, ct);

        // ── 1. Validate target ────────────────────────────────────────────────
        if (!Uri.TryCreate(request.TargetUrl, UriKind.Absolute, out var targetUri))
            throw new ProxyRequestException(ProxyErrorCode.MalformedRequest, "Target URL is not a valid absolute URI.");

        ValidateRequest(request, targetUri);
        ValidateBody(request);
        ValidateSimulatedDomain(targetUri.Host);
        await ValidateGrantAsync(accountId, request.AppId, ct);

        // ── 2. Enforce concurrency quota ──────────────────────────────────────
        EnterRequestSlot(deviceId);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            return await ExecuteCoreAsync(accountId, deviceId, request, targetUri, ct);
        }
        finally
        {
            sw.Stop();
            _activeCounts.AddOrUpdate(deviceId, 0, (_, v) => Math.Max(0, v - 1));
        }
    }

    /// <inheritdoc />
    public async Task<ProxyTcpProbeResponse> ExecuteTcpProbeAsync(
        Guid accountId, Guid deviceId, ProxyTcpProbeRequest request, CancellationToken ct)
    {
        EnsureEnabled();
        await ValidateDeviceOwnershipAsync(accountId, deviceId, ct);

        if (string.IsNullOrWhiteSpace(request.Host))
            throw new ProxyRequestException(ProxyErrorCode.MalformedRequest, "A target host is required.");
        if (request.Port is < 1 or > 65535)
            throw new ProxyRequestException(ProxyErrorCode.MalformedRequest, "Port must be between 1 and 65535.");
        ValidateAppId(request.AppId);

        ValidateSimulatedDomain(request.Host);
        await ValidateGrantAsync(accountId, request.AppId, ct);

        EnterRequestSlot(deviceId);

        try
        {
            // No port allow-list here — probing an arbitrary port is the entire point (unlike the
            // HTTP proxy, which only ever needs 80/443). Address-range and simulated-domain
            // protection still fully apply.
            var pinnedAddress = await ResolveAndValidateAddressAsync(request.Host, request.Port, allowedPorts: null, ct);

            var timeout = TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds, 1, MaxTcpProbeTimeoutSeconds));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ProxyTcpProbeState state = await _tcpConnector.ProbeAsync(pinnedAddress, request.Port, timeout, ct);
            sw.Stop();

            await _audit.RecordAsync(accountId, deviceId, "PROXY_TCP_PROBE",
                $"{{\"host\":\"{request.Host}\",\"port\":{request.Port},\"state\":\"{state}\",\"ms\":{sw.ElapsedMilliseconds}}}",
                ct);

            return new ProxyTcpProbeResponse(request.RequestId, state, sw.ElapsedMilliseconds);
        }
        finally
        {
            _activeCounts.AddOrUpdate(deviceId, 0, (_, v) => Math.Max(0, v - 1));
        }
    }

    /// <inheritdoc />
    public Task<ProxyPolicyResponse> GetPolicyAsync(Guid deviceId, CancellationToken ct)
    {
        var current = _activeCounts.GetValueOrDefault(deviceId, 0);
        return Task.FromResult(new ProxyPolicyResponse(
            deviceId,
            _policy.MaxConcurrentPerDevice,
            current,
            _policy.MaxResponseBytes,
            30, // MaxDurationSeconds
            _policy.MaxRedirectHops,
            _policy.Enabled ? ["http", "https", "tcp", "udp"] : [],
            30,  // AuditRetentionDays
            _policy.Weakenings(),
            _policy.Enabled,
            _policy.AllowAllPorts ? [] : _policy.HttpPorts,
            _policy.AllowAllPorts ? [] : _policy.TcpPorts,
            _policy.AllowAllPorts ? [] : _policy.UdpPorts,
            _policy.AllowAllPorts,
            _policy.AllowPrivateNetworks,
            _policy.QuotasEnabled,
            _policy.MaxRequestBytes,
            _policy.MaxStreamsPerDevice,
            _streamCounts.GetValueOrDefault(deviceId, 0),
            _policy.StreamIdleTimeoutSeconds,
            _policy.GrantedApps));
    }

    /// <inheritdoc />
    public async Task<IProxyStreamChannel> OpenStreamAsync(
        Guid accountId, Guid deviceId, ProxyProtocol protocol, ProxyStreamRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureEnabled();
        await ValidateDeviceOwnershipAsync(accountId, deviceId, ct);

        if (protocol is not (ProxyProtocol.Tcp or ProxyProtocol.Udp))
            throw new ProxyRequestException(ProxyErrorCode.MalformedRequest, "Only TCP and UDP are relayed as streams.");
        if (string.IsNullOrWhiteSpace(request.Host))
            throw new ProxyRequestException(ProxyErrorCode.MalformedRequest, "A target host is required.");
        if (request.Port is < 1 or > 65535)
            throw new ProxyRequestException(ProxyErrorCode.MalformedRequest, "Port must be between 1 and 65535.");
        ValidateAppId(request.AppId);
        ValidateSimulatedDomain(request.Host);
        await ValidateGrantAsync(accountId, request.AppId, ct);

        IReadOnlyList<int> allowedPorts = protocol == ProxyProtocol.Tcp ? _policy.TcpPorts : _policy.UdpPorts;
        var pinnedAddress = await ResolveAndValidateAddressAsync(request.Host, request.Port, allowedPorts, ct);

        // ── Stream quota: a slot is held from the connect to the close ────────
        var open = _streamCounts.AddOrUpdate(deviceId, 1, (_, v) => v + 1);
        if (open > _policy.EffectiveMaxStreamsPerDevice)
        {
            ReleaseStreamSlot(deviceId);
            throw new ProxyRequestException(ProxyErrorCode.QuotaExceeded,
                $"Open stream limit of {_policy.MaxStreamsPerDevice} exceeded.");
        }

        System.Net.Sockets.Socket socket;
        try
        {
            socket = await _streamConnector.ConnectAsync(protocol, pinnedAddress, request.Port, ct);
        }
        catch
        {
            ReleaseStreamSlot(deviceId);
            throw;
        }

        string kind = protocol == ProxyProtocol.Tcp ? "TCP" : "UDP";
        await _audit.RecordAsync(accountId, deviceId, $"PROXY_{kind}_OPEN",
            $"{{\"host\":\"{request.Host}\",\"address\":\"{pinnedAddress}\",\"port\":{request.Port},\"appId\":\"{request.AppId}\"}}", ct);

        return new SocketProxyStreamChannel(
            socket,
            protocol,
            new ProxyStreamOpened(pinnedAddress.ToString(), request.Port),
            _policy.EffectiveStreamIdleTimeout,
            (closure, sent, received) =>
            {
                ReleaseStreamSlot(deviceId);
                // Fire-and-forget: the close may run on a socket callback after the request scope
                // ended, so a failed audit write must never surface as an unobserved exception.
                _ = RecordCloseAsync(accountId, deviceId, kind, request.Host, request.Port, closure, sent, received);
            });
    }

    private async Task RecordCloseAsync(
        Guid accountId, Guid deviceId, string kind, string host, int port, ProxyStreamClosure closure, long sent, long received)
    {
        try
        {
            await _audit.RecordAsync(accountId, deviceId, $"PROXY_{kind}_CLOSE",
                $"{{\"host\":\"{host}\",\"port\":{port},\"code\":{closure.CloseCode},\"sent\":{sent},\"received\":{received}}}",
                CancellationToken.None);
        }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException or DbUpdateException)
        {
            // The request scope (and its DbContext) is gone: the open entry is already audited.
        }
    }

    private static void ReleaseStreamSlot(Guid deviceId) =>
        _streamCounts.AddOrUpdate(deviceId, 0, (_, v) => Math.Max(0, v - 1));

    // ── Core HTTP execution ───────────────────────────────────────────────────

    private async Task<ProxyHttpResponse> ExecuteCoreAsync(
        Guid accountId, Guid deviceId, ProxyHttpRequest request,
        Uri targetUri, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("proxy");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var currentUri = targetUri;
        int redirectHops = 0;
        HttpResponseMessage? lastResponse = null;
        // A redirect may change the method and drop the body, as a browser does (RFC 9110 §15.4).
        string method = request.HttpMethod ?? "GET";
        byte[]? body = request.Body;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds, 1, 30)));

        try
        {
            while (true)
            {
                var pinnedAddress = await ResolveAndValidateAddressAsync(
                    currentUri.Host, currentUri.Port, _policy.HttpPorts, cts.Token);
                using var httpRequest = BuildHttpRequest(request, method, body, currentUri);
                lastResponse?.Dispose();
                using (_connectionPin.Push(pinnedAddress))
                {
                    lastResponse = await client.SendAsync(
                        httpRequest, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                }

                // ADR 0210 §3: the OS follows the hops itself, to keep each hop's cookies.
                if (!request.FollowRedirects)
                    break;

                if (IsRedirect(lastResponse.StatusCode) && redirectHops < _policy.MaxRedirectHops)
                {
                    var location = lastResponse.Headers.Location;
                    if (location is null) break;
                    currentUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);

                    // SSRF: re-validate the redirect target.
                    ValidateSimulatedDomain(currentUri.Host);
                    ValidateRequest(request, currentUri);

                    // 303 always, and 301/302 after a POST, continue as a bodiless GET; 307/308 keep both.
                    if (lastResponse.StatusCode == HttpStatusCode.SeeOther
                        || (lastResponse.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
                            && string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)))
                    {
                        method = string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase) ? "HEAD" : "GET";
                        body = null;
                    }

                    redirectHops++;
                    continue;
                }

                if (IsRedirect(lastResponse.StatusCode) && redirectHops >= _policy.MaxRedirectHops)
                    throw new ProxyRequestException(ProxyErrorCode.TooManyRedirects,
                        $"Maximum of {_policy.MaxRedirectHops} redirect hops exceeded.");

                break;
            }

            // Read body with size limit.
            var bodyStream = await lastResponse!.Content.ReadAsStreamAsync(cts.Token);
            var bodyBytes = await ReadWithLimitAsync(bodyStream, _policy.EffectiveMaxResponseBytes, cts.Token);
            var bodyHash = bodyBytes.Length > 0
                ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bodyBytes)).ToLowerInvariant()
                : null;

            var responseHeaders = lastResponse.Headers
                .Concat(lastResponse.Content.Headers)
                .Where(h => !IsHopByHopHeader(h.Key))
                .SelectMany(h => h.Value.Select(v => new ProxyHeader(h.Key, v)))
                .ToList();

            sw.Stop();
            await _audit.RecordAsync(accountId, deviceId, "PROXY_REQUEST",
                $"{{\"url\":\"{request.TargetUrl}\",\"method\":\"{request.HttpMethod}\",\"requestBytes\":{request.BodyBytes},\"status\":{(int)lastResponse.StatusCode},\"ms\":{sw.ElapsedMilliseconds},\"includeBody\":{(request.IncludeBody ? "true" : "false")}}}",
                ct);

            // Body bytes were already fetched (and size-capped) above regardless of IncludeBody, to
            // compute the hash every response carries; only base64-encode and return them when asked.
            string? bodyBase64 = request.IncludeBody && bodyBytes.Length > 0
                ? Convert.ToBase64String(bodyBytes)
                : null;

            return new ProxyHttpResponse(
                request.RequestId,
                (int)lastResponse.StatusCode,
                lastResponse.ReasonPhrase ?? string.Empty,
                responseHeaders,
                bodyHash,
                bodyBytes.Length,
                currentUri.ToString(),
                redirectHops,
                sw.ElapsedMilliseconds,
                bodyBase64);
        }
        finally
        {
            lastResponse?.Dispose();
        }
    }

    private static HttpRequestMessage BuildHttpRequest(ProxyHttpRequest request, string method, byte[]? body, Uri targetUri)
    {
        var httpRequest = new HttpRequestMessage(new HttpMethod(method), targetUri);
        if (body is not null)
        {
            // ADR 0102 §1: the bytes the OS sent, already checked against their hash and size.
            httpRequest.Content = new ByteArrayContent(body);
            if (request.ContentType is { Length: > 0 } contentType)
                httpRequest.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }

        // Forward safe headers only; strip hop-by-hop and dangerous headers. Content headers
        // (Content-Language…) go on the content when there is one; Content-Type and
        // Content-Length come from the body itself.
        foreach (var header in request.Headers)
        {
            if (IsBlockedRequestHeader(header.Name) || IsHopByHopHeader(header.Name)
                || header.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                || header.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!httpRequest.Headers.TryAddWithoutValidation(header.Name, header.Value))
                httpRequest.Content?.Headers.TryAddWithoutValidation(header.Name, header.Value);
        }

        return httpRequest;
    }

    // ── SSRF validation ───────────────────────────────────────────────────────

    private static void ValidateSimulatedDomain(string host)
    {
        foreach (var suffix in SimulatedSuffixes)
        {
            if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                throw new ProxyRequestException(ProxyErrorCode.SimulatedDomainBlocked,
                    $"Simulated domain '{host}' cannot be reached through the real proxy.");
        }
    }

    /// <param name="allowedPorts">
    /// The protocol's allow-list; <see langword="null"/> for the single-port probe (ADR 0035), where
    /// an arbitrary target port is the entire point — but the port must still be a valid number.
    /// </param>
    private async Task<IPAddress> ResolveAndValidateAddressAsync(
        string host, int port, IReadOnlyList<int>? allowedPorts, CancellationToken ct)
    {
        if (allowedPorts is null ? port is < 1 or > 65535 : !_policy.IsPortAllowed(allowedPorts, port))
            throw new ProxyRequestException(ProxyErrorCode.BlockedPort,
                $"Port {port} is not in the proxy allow-list.");

        // Resolve and validate address.
        IPAddress[] addresses;
        try
        {
            addresses = [.. await _addressResolver.ResolveAsync(host, ct)];
        }
        catch (Exception ex)
        {
            throw new ProxyRequestException(ProxyErrorCode.MalformedRequest,
                $"DNS resolution failed for '{host}': {ex.Message}");
        }

        if (addresses.Length == 0)
            throw new ProxyRequestException(ProxyErrorCode.MalformedRequest,
                $"DNS resolution returned no addresses for '{host}'.");

        foreach (var address in addresses)
        {
            if (IsBlockedAddress(address, _policy.AllowPrivateNetworks))
                throw new ProxyRequestException(ProxyErrorCode.BlockedAddress,
                    $"Resolved address {address} for '{host}' is in a blocked range.");
        }

        // Sorting avoids resolver-order-dependent behavior while preserving both
        // IPv4 and IPv6 validation. The chosen address is pinned for ConnectCallback.
        return addresses.OrderBy(static address => address.ToString(), StringComparer.Ordinal).First();
    }

    private async Task ValidateDeviceOwnershipAsync(Guid accountId, Guid deviceId, CancellationToken ct)
    {
        var authorized = await _db.Devices.AsNoTracking().AnyAsync(
            device => device.DeviceId == deviceId
                && device.AccountId == accountId
                && !device.IsRevoked,
            ct);

        if (!authorized)
            throw new ProxyRequestException(ProxyErrorCode.CapabilityDenied,
                "The authenticated device is not registered to this account or has been revoked.");
    }

    private void EnsureEnabled()
    {
        if (!_policy.Enabled)
            throw new ProxyRequestException(ProxyErrorCode.RelayDisabled,
                "The operator turned the network relay off (HackerOs:Proxy:Enabled).");
    }

    private static void ValidateAppId(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId) || appId.Length > 256)
            throw new ProxyRequestException(ProxyErrorCode.CapabilityDenied,
                "A valid registered application identifier is required.");
    }

    private async Task ValidateGrantAsync(Guid accountId, string appId, CancellationToken ct)
    {
        if (!await _grants.IsGrantedAsync(accountId, appId, ct))
            throw new ProxyRequestException(ProxyErrorCode.GrantMissing,
                $"The server does not grant 'network.real.access' to '{appId}'.");
    }

    private void EnterRequestSlot(Guid deviceId)
    {
        var current = _activeCounts.AddOrUpdate(deviceId, 1, (_, v) => v + 1);
        if (current > _policy.EffectiveMaxConcurrentPerDevice)
        {
            _activeCounts.AddOrUpdate(deviceId, 0, (_, v) => Math.Max(0, v - 1));
            throw new ProxyRequestException(ProxyErrorCode.QuotaExceeded,
                $"Concurrent request limit of {_policy.MaxConcurrentPerDevice} exceeded.");
        }
    }

    /// <summary>ADR 0102 §1: the body must be exactly the bytes announced by its size and SHA-256.</summary>
    private void ValidateBody(ProxyHttpRequest request)
    {
        if (request.BodyBytes < 0 || request.BodyBytes > _policy.EffectiveMaxRequestBytes)
            throw new ProxyRequestException(ProxyErrorCode.PayloadTooLarge,
                $"The request body exceeds the {_policy.MaxRequestBytes / 1024 / 1024} MiB limit.");

        if (request.Body is null)
        {
            if (request.BodyBytes != 0)
                throw new ProxyRequestException(ProxyErrorCode.BodyMismatch,
                    $"The request announces {request.BodyBytes} body bytes but carries none.");
            return;
        }

        if (request.Body.LongLength != request.BodyBytes)
            throw new ProxyRequestException(ProxyErrorCode.BodyMismatch,
                $"The request body is {request.Body.LongLength} bytes, not the {request.BodyBytes} announced.");

        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(request.Body));
        if (!string.Equals(hash, request.BodyHash, StringComparison.OrdinalIgnoreCase))
            throw new ProxyRequestException(ProxyErrorCode.BodyMismatch,
                "The request body does not match its SHA-256 hash.");
    }

    private static void ValidateRequest(ProxyHttpRequest request, Uri targetUri)
    {
        if (request.Protocol is not ProxyProtocol.Http)
            throw new ProxyRequestException(ProxyErrorCode.MalformedRequest,
                "This endpoint accepts only the HTTP proxy protocol.");

        if (targetUri.Scheme is not ("http" or "https"))
            throw new ProxyRequestException(ProxyErrorCode.MalformedRequest,
                "Only HTTP and HTTPS target URLs are supported.");

        ValidateAppId(request.AppId);
    }

    /// <summary>Whether the relay may not connect to <paramref name="address"/>.</summary>
    /// <param name="address">A resolved address.</param>
    /// <param name="allowPrivateNetworks">Whether private ranges are open (ADR 0102 §5).</param>
    public static bool IsBlockedAddress(IPAddress address, bool allowPrivateNetworks)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        foreach (var (network, prefixLength) in AlwaysBlockedRanges)
        {
            if (address.AddressFamily == network.AddressFamily && IsInSubnet(address, network, prefixLength))
                return true;
        }

        if (allowPrivateNetworks)
            return false;

        foreach (var (network, prefixLength) in PrivateRanges)
        {
            if (address.AddressFamily == network.AddressFamily && IsInSubnet(address, network, prefixLength))
                return true;
        }
        return false;
    }

    private static bool IsInSubnet(IPAddress address, IPAddress network, int prefixLength)
    {
        var addressBytes = address.GetAddressBytes();
        var networkBytes = network.GetAddressBytes();
        if (addressBytes.Length != networkBytes.Length) return false;

        int fullBytes = prefixLength / 8;
        int remainingBits = prefixLength % 8;

        for (int i = 0; i < fullBytes; i++)
            if (addressBytes[i] != networkBytes[i]) return false;

        if (remainingBits > 0)
        {
            int mask = 0xFF << (8 - remainingBits) & 0xFF;
            if ((addressBytes[fullBytes] & mask) != (networkBytes[fullBytes] & mask)) return false;
        }

        return true;
    }

    // ── Header filtering ──────────────────────────────────────────────────────

    private static readonly HashSet<string> HopByHopHeaders =
        new(StringComparer.OrdinalIgnoreCase)
        { "connection", "keep-alive", "proxy-authenticate", "proxy-authorization",
          "te", "trailers", "transfer-encoding", "upgrade" };

    // ADR 0210 §2: Cookie is relayed. The OS sends only the cookies its own container holds for the
    // target host (the ones that host set through the relay), so a user can stay signed in to a real site.
    private static readonly HashSet<string> BlockedRequestHeaders =
        new(StringComparer.OrdinalIgnoreCase)
        { "host", "authorization", "x-forwarded-for", "x-real-ip" };

    private static bool IsHopByHopHeader(string name) => HopByHopHeaders.Contains(name);
    private static bool IsBlockedRequestHeader(string name) => BlockedRequestHeaders.Contains(name);

    // ── Utilities ─────────────────────────────────────────────────────────────

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently
                   or HttpStatusCode.Found
                   or HttpStatusCode.SeeOther
                   or HttpStatusCode.TemporaryRedirect
                   or HttpStatusCode.PermanentRedirect;

    private static async Task<byte[]> ReadWithLimitAsync(
        Stream stream, long maxBytes, CancellationToken ct)
    {
        using var ms = new System.IO.MemoryStream();
        var buffer = new byte[8192];
        long totalRead = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            totalRead += read;
            if (totalRead > maxBytes)
                throw new ProxyRequestException(ProxyErrorCode.PayloadTooLarge,
                    $"Response exceeds the {maxBytes / 1024 / 1024} MiB limit.");
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }
}

/// <summary>
/// Exception thrown by the proxy service when a request violates a security rule.
/// The endpoint maps this to a ProxyErrorResponse with the appropriate HTTP status.
/// </summary>
public sealed class ProxyRequestException : Exception
{
    public string ErrorCode { get; }
    public ProxyRequestException(string errorCode, string message)
        : base(message) => ErrorCode = errorCode;
}
