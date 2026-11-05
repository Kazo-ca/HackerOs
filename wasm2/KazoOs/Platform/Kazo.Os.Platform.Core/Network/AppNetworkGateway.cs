using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using System.Text;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Net;
using Kazo.Os.Platform.Core.Network.Local;
using Kazo.Os.Platform.Core.ServerConnection;
using Kazo.Os.Server.Contracts.Proxy;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Metrics;
using Kazo.Os.Simulation.Abstractions.Network;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.ServerConnection;

namespace Kazo.Os.Platform.Core.Network;

/// <summary>
/// Trusted <see cref="IAppNetworkGateway"/> of one app instance (ADR 0056, section 6): the OS
/// network stack an app reaches through <c>KazoDns</c>, <c>KazoPing</c>, <c>KazoHttpClient</c> and
/// <c>KazoTcpClient</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every call checks the app's capability first (<c>network.simulated.read</c>, or
/// <c>network.simulated.write</c> for an HTTP method other than GET, HEAD and OPTIONS), then asks the
/// simulated network. Only when the simulated network does not know the target does the OS decide,
/// on its own, whether to relay the call to the real Internet: the app must hold
/// <c>network.real.access</c> and the optional server must be connected and hand out an access
/// token (ADR 0023, 0028, 0035). The token, the server URL and even whether a server exists never
/// reach the app: a missing route is just "host not found", like a machine without a default
/// gateway.
/// </para>
/// <para>
/// What the relay carries (ADR 0102): any HTTP method with its headers and body, a single-port TCP
/// connect probe, a timed HEAD standing in for an ICMP echo (which a browser cannot send), and TCP
/// streams and connected UDP sockets on the ports the server's operator allows. DNS, OS detection
/// and port ranges stay simulation-only. Inside the simulated network, any method travels with its
/// headers and body (ADR 0074, section 1); the simulated network has no raw TCP or UDP sockets, so
/// a simulated or local target refuses a socket connection.
/// </para>
/// <para>
/// This is also the one place network traffic of an app passes through, where ADR 0057 counts the
/// bytes shown in <c>/proc/net/dev</c> and charges them to the app's process
/// (<c>/proc/&lt;pid&gt;/io</c>); see <see cref="NetworkTrafficMeter"/> for how a packet is sized.
/// A call refused for a missing capability never reaches the wire and counts nothing.
/// </para>
/// <para>
/// With the session's local stack (<see cref="LocalNetworkCaller"/>, ADR 0074), names are resolved
/// by <c>kazodns</c> (<c>/etc/hosts</c>, <c>localhost</c>, web app URLs, hosted sites, then the
/// simulated network), and a target on this machine (127.0.0.0/8) is served by the process that
/// listens on its port, through the <see cref="ListeningPortTable"/>: an HTTP request is handed
/// to that process, a web app that does not run yet is started on demand, and nothing listening
/// is "connection refused". That traffic is counted on <c>lo</c>. The internal <c>.hack</c> domain
/// is never relayed to the real Internet.
/// </para>
/// </remarks>
public sealed class AppNetworkGateway : IAppNetworkGateway
{
    // ADR 0210 §5: a real page can be slow; 30 s is the most the server allows for one relay call.
    private const int ProxyTimeoutSeconds = 30;
    private const int PingTimeoutSeconds = 10;
    private const int ProbeTimeoutSeconds = 5;
    private const int MaxRedirects = 10;

    /// <summary>
    /// Memory the OS holds for one network operation in flight (ADR 0068): the default Linux TCP
    /// receive buffer (<c>tcp_rmem</c>, 128 KiB) plus the default send buffer (<c>tcp_wmem</c>,
    /// 16 KiB). Charged to the app's process for the duration of the call, as a socket's buffers
    /// live from <c>socket()</c> to <c>close()</c>.
    /// </summary>
    public const long SocketBufferBytes = (128 + 16) * 1024;

    private readonly ISimulatedNetworkService _network;
    private readonly ICapabilityChecker _capabilities;
    private readonly string _appId;
    private readonly IServerConnectionService? _serverConnection;
    private readonly IProxyClient? _proxy;
    private readonly NetworkTrafficMeter _meter;
    private readonly IKernelMemoryLedger? _kernelMemory;
    private readonly ProcessId? _pid;
    private readonly LocalNetworkCaller? _local;
    private int _echoSequence;

    /// <summary>Initializes the gateway of one app instance.</summary>
    /// <param name="network">The simulated network, always tried first.</param>
    /// <param name="capabilities">Capability checker bound to the app and its user.</param>
    /// <param name="appId">Id of the app, reported to the server proxy for its audit log.</param>
    /// <param name="serverConnection">Optional server connection; <see langword="null"/> disables the real-network fallback.</param>
    /// <param name="proxy">Optional server proxy client; <see langword="null"/> disables the real-network fallback.</param>
    /// <param name="statistics">Optional interface counters behind <c>/proc/net/dev</c> (ADR 0057).</param>
    /// <param name="processIo">Optional per-process I/O accounting, charged with this app's traffic.</param>
    /// <param name="pid">Process of this app instance, the one <paramref name="processIo"/> charges.</param>
    /// <param name="kernelMemory">Optional ledger charged with the buffers of each operation in flight (ADR 0068).</param>
    /// <param name="local">
    /// The session's local stack and who calls it (ADR 0074); <see langword="null"/> keeps the
    /// simulated network only, without <c>kazodns</c>, listening or local delivery.
    /// </param>
    public AppNetworkGateway(
        ISimulatedNetworkService network,
        ICapabilityChecker capabilities,
        string appId,
        IServerConnectionService? serverConnection = null,
        IProxyClient? proxy = null,
        INetworkInterfaceStatistics? statistics = null,
        IProcessIoAccounting? processIo = null,
        ProcessId? pid = null,
        IKernelMemoryLedger? kernelMemory = null,
        LocalNetworkCaller? local = null)
    {
        _kernelMemory = kernelMemory;
        _local = local;
        _pid = pid;
        _network = network ?? throw new ArgumentNullException(nameof(network));
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        _appId = appId;
        _serverConnection = serverConnection;
        _proxy = proxy;
        _meter = new NetworkTrafficMeter(statistics, processIo, pid);
    }

    /// <inheritdoc />
    public async ValueTask<KazoNetworkResult<KazoHostEntry>> ResolveAsync(string hostName, CancellationToken cancellationToken = default)
    {
        // ADR 0067: time spent in the gateway is the caller's stime (kernel sub-frame).
        using Kazo.Os.AppSdk.KazoExecutionContext.KernelScope kernel = Kazo.Os.AppSdk.KazoExecutionContext.EnterKernel();
        using KernelMemoryCharge socket = OpenSocket();
        KazoNetworkResult<KazoHostEntry> result = await ResolveCoreAsync(hostName, cancellationToken).ConfigureAwait(false);
        // A name answered on this machine never leaves it: the stub resolver is reached on lo.
        _meter.Dns(hostName, result.Value?.AddressList.Count ?? 0, result.Succeeded, local: IsAnsweredLocally(result.Value));
        return result;
    }

    /// <inheritdoc />
    public async ValueTask<KazoNetworkResult<KazoHostEntry>> ReverseLookupAsync(IPAddress address, CancellationToken cancellationToken = default)
    {
        // ADR 0067: time spent in the gateway is the caller's stime (kernel sub-frame).
        using Kazo.Os.AppSdk.KazoExecutionContext.KernelScope kernel = Kazo.Os.AppSdk.KazoExecutionContext.EnterKernel();
        using KernelMemoryCharge socket = OpenSocket();
        KazoNetworkResult<KazoHostEntry> result = await ReverseLookupCoreAsync(address, cancellationToken).ConfigureAwait(false);
        // A PTR query asks for "<octets>.in-addr.arpa".
        _meter.Dns($"{address}.in-addr.arpa", result.Succeeded ? 1 : 0, result.Succeeded);
        return result;
    }

    /// <inheritdoc />
    public async ValueTask<KazoNetworkResult<KazoPingReply>> PingAsync(
        string hostNameOrAddress, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        // ADR 0067: time spent in the gateway is the caller's stime (kernel sub-frame).
        using Kazo.Os.AppSdk.KazoExecutionContext.KernelScope kernel = Kazo.Os.AppSdk.KazoExecutionContext.EnterKernel();
        using KernelMemoryCharge socket = OpenSocket();
        (KazoNetworkResult<KazoPingReply> result, bool local) = await PingCoreAsync(hostNameOrAddress, timeout, cancellationToken).ConfigureAwait(false);
        _meter.Echo(result.Succeeded && result.Value!.Status == KazoIPStatus.Success, local);
        return result;
    }

    /// <inheritdoc />
    public async ValueTask<KazoNetworkResult<KazoHttpResponse>> SendAsync(
        KazoHttpRequest request, CancellationToken cancellationToken = default)
    {
        // ADR 0067: time spent in the gateway is the caller's stime (kernel sub-frame).
        using Kazo.Os.AppSdk.KazoExecutionContext.KernelScope kernel = Kazo.Os.AppSdk.KazoExecutionContext.EnterKernel();
        using KernelMemoryCharge socket = OpenSocket();
        (KazoNetworkResult<KazoHttpResponse> result, bool local) = await SendCoreAsync(request, cancellationToken).ConfigureAwait(false);
        _meter.Http(request, result, local);
        return result;
    }

    /// <inheritdoc />
    public async ValueTask<KazoNetworkResult<IReadOnlyList<KazoPortProbeResult>>> ProbeTcpAsync(
        string hostNameOrAddress, int firstPort, int lastPort, CancellationToken cancellationToken = default)
    {
        // ADR 0067: time spent in the gateway is the caller's stime (kernel sub-frame).
        using Kazo.Os.AppSdk.KazoExecutionContext.KernelScope kernel = Kazo.Os.AppSdk.KazoExecutionContext.EnterKernel();
        using KernelMemoryCharge socket = OpenSocket();
        (KazoNetworkResult<IReadOnlyList<KazoPortProbeResult>> result, bool local) =
            await ProbeTcpCoreAsync(hostNameOrAddress, firstPort, lastPort, cancellationToken).ConfigureAwait(false);
        _meter.Probe(result, Math.Max(1, lastPort - firstPort + 1), local);
        return result;
    }

    /// <inheritdoc />
    public async ValueTask<KazoNetworkResult<KazoOsFingerprint?>> DetectOperatingSystemAsync(
        string hostNameOrAddress, CancellationToken cancellationToken = default)
    {
        // ADR 0067: time spent in the gateway is the caller's stime (kernel sub-frame).
        using Kazo.Os.AppSdk.KazoExecutionContext.KernelScope kernel = Kazo.Os.AppSdk.KazoExecutionContext.EnterKernel();
        using KernelMemoryCharge socket = OpenSocket();
        (KazoNetworkResult<KazoOsFingerprint?> result, bool local) =
            await DetectOperatingSystemCoreAsync(hostNameOrAddress, cancellationToken).ConfigureAwait(false);
        _meter.Fingerprint(result.Succeeded, local);
        return result;
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">The gateway has no local network stack.</exception>
    /// <exception cref="ArgumentException">A prefix is not a valid listener prefix.</exception>
    public ValueTask<KazoNetworkResult<IKazoHttpListenerSocket>> ListenHttpAsync(
        IReadOnlyList<string> prefixes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prefixes);
        cancellationToken.ThrowIfCancellationRequested();
        using Kazo.Os.AppSdk.KazoExecutionContext.KernelScope kernel = Kazo.Os.AppSdk.KazoExecutionContext.EnterKernel();
        if (_local is not { } local || _pid is not { } pid)
        {
            throw new NotSupportedException("This network gateway has no local network stack to listen on.");
        }

        if (prefixes.Count == 0)
        {
            throw new ArgumentException("A listener needs at least one prefix.", nameof(prefixes));
        }

        List<WebBinding> bindings = [];
        foreach (string prefix in prefixes)
        {
            if (!WebAppUrls.TryParse(prefix, allowWildcardHost: true, out WebBinding binding, out string? error))
            {
                throw new ArgumentException(error, nameof(prefixes));
            }

            bindings.Add(binding);
        }

        // ADR 0073: a web app may always listen on the URLs of its own manifest; anything else
        // is network.listen, plus network.listen.privileged below port 1024 (CAP_NET_BIND_SERVICE).
        WebBinding[] own =
        [
            .. (local.Manifest.Web?.Urls ?? [])
                .Select(static url => WebAppUrls.TryParse(url, allowWildcardHost: false, out WebBinding parsed, out _) ? parsed : (WebBinding?)null)
                .OfType<WebBinding>()
        ];
        WebBinding[] foreign = [.. bindings.Where(binding => !own.Any(url => Covers(url, binding)))];
        if (foreign.Length > 0)
        {
            _capabilities.Require(AppCapabilities.NetworkListen);
            if (foreign.Any(static binding => binding.IsPrivilegedPort))
            {
                _capabilities.Require(AppCapabilities.NetworkListenPrivileged);
            }
        }

        ListeningSocketOwner owner = new(
            pid, _appId, ProgramName(_appId), local.Principal.UserId.ToString(), local.Principal.LoginName.Value);
        IKazoHttpListenerSocket? socket = local.Services.Ports.TryOpen(owner, bindings, out string? conflict);
        return ValueTask.FromResult(socket is null
            ? KazoNetworkResult<IKazoHttpListenerSocket>.Failure(KazoNetworkError.AddressInUse, $"{conflict}: address already in use")
            : KazoNetworkResult<IKazoHttpListenerSocket>.Success(socket));
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<KazoTcpListenerInfo>> GetTcpListenersAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_local is not { } local)
        {
            return ValueTask.FromResult<IReadOnlyList<KazoTcpListenerInfo>>([]);
        }

        // As /proc/net/tcp: every socket is listed, but its process only for its owner or root
        // (netstat -p prints "-" for the others).
        IReadOnlyList<KazoTcpListenerInfo> listeners =
        [
            .. local.Services.Ports.Snapshot().Select(row =>
            {
                bool visible = row.Owner is { } owner
                    && (local.IsRoot || string.Equals(owner.UserId, local.Principal.UserId.ToString(), StringComparison.Ordinal));
                return new KazoTcpListenerInfo(
                    row.Address,
                    row.Port,
                    visible ? row.Owner!.Pid.Value : null,
                    visible ? row.Owner!.ProgramName : null,
                    row.Owner?.LoginName ?? "root",
                    row.Prefixes);
            })
        ];
        return ValueTask.FromResult(listeners);
    }

    /// <inheritdoc />
    public ValueTask<KazoNetworkResult<IKazoSocket>> ConnectTcpAsync(
        string hostNameOrAddress, int port, CancellationToken cancellationToken = default) =>
        ConnectAsync(ProxyProtocol.Tcp, hostNameOrAddress, port, cancellationToken);

    /// <inheritdoc />
    public ValueTask<KazoNetworkResult<IKazoSocket>> ConnectUdpAsync(
        string hostNameOrAddress, int port, CancellationToken cancellationToken = default) =>
        ConnectAsync(ProxyProtocol.Udp, hostNameOrAddress, port, cancellationToken);

    /// <summary>
    /// Opens a TCP or connected UDP socket (ADR 0102 §2, §3). The simulated network has no raw
    /// sockets, so a target it knows (or this machine) refuses; an unknown target goes to the
    /// optional relay, which only exists for an app holding <c>network.real.access</c>.
    /// </summary>
    private async ValueTask<KazoNetworkResult<IKazoSocket>> ConnectAsync(
        ProxyProtocol protocol, string hostNameOrAddress, int port, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostNameOrAddress);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        cancellationToken.ThrowIfCancellationRequested();

        // ADR 0067: time spent in the gateway is the caller's stime (kernel sub-frame).
        using Kazo.Os.AppSdk.KazoExecutionContext.KernelScope kernel = Kazo.Os.AppSdk.KazoExecutionContext.EnterKernel();
        // A socket carries arbitrary bytes both ways: it is a write, like an HTTP POST.
        _capabilities.Require(AppCapabilities.NetworkSimulatedWrite);

        bool udp = protocol == ProxyProtocol.Udp;
        Route route = await RouteAsync(hostNameOrAddress, cancellationToken).ConfigureAwait(false);
        if (route.IsLocal || route.Host is not null)
        {
            _meter.ConnectFailed(udp);
            return KazoNetworkResult<IKazoSocket>.Failure(
                KazoNetworkError.ConnectionRefused,
                $"{hostNameOrAddress}:{port}: connection refused (the simulated network carries no raw {(udp ? "UDP" : "TCP")} sockets)");
        }

        if (!CanRelay(hostNameOrAddress) || await TryOpenRealRouteAsync(cancellationToken).ConfigureAwait(false) is not { } real)
        {
            return KazoNetworkResult<IKazoSocket>.Failure(KazoNetworkError.HostNotFound);
        }

        // The socket's buffers are charged to the process for as long as it stays open.
        KernelMemoryCharge buffers = OpenSocket();
        try
        {
            IProxyStreamChannel channel = await _proxy!.OpenStreamAsync(
                real.ServerBaseUrl, real.AccessToken, protocol, new ProxyStreamRequest(hostNameOrAddress, port, _appId), cancellationToken)
                .ConfigureAwait(false);
            // The handshake: a SYN out and a SYN-ACK back (a UDP "connect" sends nothing).
            if (!udp)
            {
                _meter.Segment(0, outbound: true, udp: false);
                _meter.Segment(0, outbound: false, udp: false);
            }

            return KazoNetworkResult<IKazoSocket>.Success(new RelayedSocket(channel, _meter, buffers));
        }
        catch (ProxyStreamException refused)
        {
            buffers.Dispose();
            _meter.ConnectFailed(udp);
            return KazoNetworkResult<IKazoSocket>.Failure(ToNetworkError(refused.ErrorCode), refused.Message);
        }
        catch (Exception exception) when (exception is ServerConnectionException or HttpRequestException or System.Net.WebSockets.WebSocketException)
        {
            buffers.Dispose();
            _meter.ConnectFailed(udp);
            return KazoNetworkResult<IKazoSocket>.Failure(KazoNetworkError.ConnectionFailed, exception.Message);
        }
    }

    /// <summary>
    /// What an app sees of a relay refusal: a policy refusal (port, address range) is a firewall
    /// REJECT, a missing server grant or a disabled relay is no route at all.
    /// </summary>
    private static KazoNetworkError ToNetworkError(string errorCode) => errorCode switch
    {
        ProxyErrorCode.ConnectionRefused or ProxyErrorCode.BlockedPort or ProxyErrorCode.BlockedAddress => KazoNetworkError.ConnectionRefused,
        ProxyErrorCode.HostUnreachable => KazoNetworkError.HostUnreachable,
        ProxyErrorCode.GrantMissing or ProxyErrorCode.RelayDisabled or ProxyErrorCode.SimulatedDomainBlocked => KazoNetworkError.HostNotFound,
        _ => KazoNetworkError.ConnectionFailed
    };

    /// <summary>Gets the short program name of an app (its last dotted segment, 15 characters), as <c>comm</c>.</summary>
    /// <param name="appId">The app id.</param>
    public static string ProgramName(string appId)
    {
        ArgumentNullException.ThrowIfNull(appId);
        string name = appId[(appId.LastIndexOf('.') + 1)..];
        if (name.Length == 0)
        {
            name = appId;
        }

        return name.Length > 15 ? name[..15] : name;
    }

    /// <summary>Whether a manifest URL covers a listener binding: same host and port, path under the URL's.</summary>
    private static bool Covers(WebBinding url, WebBinding binding) =>
        url.Port == binding.Port
        && string.Equals(url.Host, binding.Host, StringComparison.OrdinalIgnoreCase)
        && binding.Path.StartsWith(url.Path, StringComparison.Ordinal);

    private static bool IsAnsweredLocally(KazoHostEntry? entry) =>
        entry is not null && entry.Source is KazoDnsSource.HostsFile or KazoDnsSource.Loopback or KazoDnsSource.WebApp or KazoDnsSource.HostedSite;

    /// <summary>Charges the socket buffers of one operation to the app's process while it is in flight.</summary>
    private KernelMemoryCharge OpenSocket() =>
        _kernelMemory is { IsTracking: true } ledger && _pid is { } pid
            ? ledger.Charge(pid, KernelMemoryKind.Socket, SocketBufferBytes)
            : KernelMemoryCharge.None;

    // ── Simulated network ─────────────────────────────────────────────────

    private async ValueTask<KazoNetworkResult<KazoHostEntry>> ResolveCoreAsync(string hostName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostName);
        cancellationToken.ThrowIfCancellationRequested();
        _capabilities.Require(AppCapabilities.NetworkSimulatedRead);

        if (_local is { } local)
        {
            DnsAnswer? answer = await local.Services.Resolver.ResolveAsync(hostName, cancellationToken).ConfigureAwait(false);
            return answer is null
                ? KazoNetworkResult<KazoHostEntry>.Failure(KazoNetworkError.HostNotFound)
                : KazoNetworkResult<KazoHostEntry>.Success(new KazoHostEntry(answer.CanonicalName, answer.Addresses, answer.Aliases)
                {
                    Source = answer.Source,
                    SourceDetail = answer.Detail
                });
        }

        string? ip = _network.Dns.Resolve(hostName);
        return ip is null
            ? KazoNetworkResult<KazoHostEntry>.Failure(KazoNetworkError.HostNotFound)
            : KazoNetworkResult<KazoHostEntry>.Success(BuildEntry(_network.Dns.ReverseLookup(ip) ?? hostName, ip) with
            {
                Source = KazoDnsSource.SimulatedNetwork
            });
    }

    private async ValueTask<KazoNetworkResult<KazoHostEntry>> ReverseLookupCoreAsync(IPAddress address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        cancellationToken.ThrowIfCancellationRequested();
        _capabilities.Require(AppCapabilities.NetworkSimulatedRead);

        string ip = address.ToString();
        string? hostName = _local is { } local
            ? await local.Services.Resolver.ReverseLookupAsync(address, cancellationToken).ConfigureAwait(false)
            : _network.Dns.ReverseLookup(ip);
        return hostName is null
            ? KazoNetworkResult<KazoHostEntry>.Failure(KazoNetworkError.HostNotFound)
            : KazoNetworkResult<KazoHostEntry>.Success(BuildEntry(hostName, ip));
    }

    private async ValueTask<(KazoNetworkResult<KazoPingReply> Result, bool Local)> PingCoreAsync(
        string hostNameOrAddress, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostNameOrAddress);
        cancellationToken.ThrowIfCancellationRequested();
        _capabilities.Require(AppCapabilities.NetworkSimulatedRead);

        Route route = await RouteAsync(hostNameOrAddress, cancellationToken).ConfigureAwait(false);
        if (route.IsLocal)
        {
            // The loopback answers at once: a few tens of microseconds, as on a real machine.
            int localSequence = Interlocked.Increment(ref _echoSequence);
            TimeSpan loopbackTrip = TimeSpan.FromMilliseconds(0.040 + (0.005 * (localSequence % 3)));
            return (KazoNetworkResult<KazoPingReply>.Success(
                new KazoPingReply(route.Answer!.Addresses[0], KazoIPStatus.Success, loopbackTrip)), true);
        }

        SimulatedHost? host = route.Host;
        if (host is null)
        {
            return (CanRelay(hostNameOrAddress)
                ? await PingRealHostAsync(hostNameOrAddress, cancellationToken).ConfigureAwait(false)
                : KazoNetworkResult<KazoPingReply>.Failure(KazoNetworkError.HostNotFound), false);
        }

        IPAddress address = IPAddress.Parse(host.Ip);
        if (!host.IsUp)
        {
            return (KazoNetworkResult<KazoPingReply>.Success(
                new KazoPingReply(address, KazoIPStatus.DestinationHostUnreachable, TimeSpan.Zero)), false);
        }

        // Deterministic jitter of ±5% around the host's latency, cycling with each echo of this
        // instance, so consecutive `ping` lines differ the way a real link does.
        int sequence = Interlocked.Increment(ref _echoSequence);
        double milliseconds = Math.Round(host.LatencyMs + (host.LatencyMs * 0.05 * ((sequence % 3) - 1)), 3);
        TimeSpan roundTrip = TimeSpan.FromMilliseconds(milliseconds);
        return (KazoNetworkResult<KazoPingReply>.Success(roundTrip > timeout
            ? new KazoPingReply(address, KazoIPStatus.TimedOut, TimeSpan.Zero)
            : new KazoPingReply(address, KazoIPStatus.Success, roundTrip)), false);
    }

    private async ValueTask<(KazoNetworkResult<KazoHttpResponse> Result, bool Local)> SendCoreAsync(
        KazoHttpRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        string method = request.Method.ToUpperInvariant();
        _capabilities.Require(SimulatedHttpMethods.IsSafe(method)
            ? AppCapabilities.NetworkSimulatedRead
            : AppCapabilities.NetworkSimulatedWrite);

        Route first = await RouteAsync(request.RequestUri.Host, cancellationToken).ConfigureAwait(false);
        Dictionary<string, Dictionary<string, string>> jar = ToJar(request.Cookies);
        HashSet<KazoCookie> before = [.. FromJar(jar)];
        Uri currentUri = request.RequestUri;

        // ADR 0089: a Cookie header the app wrote itself (`curl -H 'Cookie: …'`) is sent with the
        // jar's cookies, on the hops that stay on the requested host only (curl drops it when a
        // redirect leaves for another host). It never enters the jar: a request header stores
        // nothing, so it never comes back as a Set-Cookie of the response.
        IReadOnlyList<KeyValuePair<string, string>> explicitCookies = ParseCookieHeaders(request.Headers);
        IReadOnlyList<KeyValuePair<string, string>> headers =
        [
            .. request.Headers.Where(static header => !header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
        ];
        string requestedHost = request.RequestUri.Host;
        SimulatedHttpBody? body = request.Content is { } content ? new SimulatedHttpBody(content.ContentType, content.Bytes.Span) : null;
        int redirectCount = 0;
        Route route = first;
        (Uri ServerBaseUrl, string AccessToken)? realRoute = null;
        bool includeBody = method != SimulatedHttpMethods.Head;

        // ADR 0074 section 1: one exchange per hop, carrying the method, headers and body; the
        // redirect rules are the browser's (RFC 9110 section 15.4), applied here so every app
        // gets them the same way. Each hop is routed on its own: a local site may redirect to the
        // simulated network and back.
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<KeyValuePair<string, string>> hopCookies =
                string.Equals(currentUri.Host, requestedHost, StringComparison.OrdinalIgnoreCase) ? explicitCookies : [];
            SimulatedHttpResponse response;
            KazoHttpResponseSource source;
            bool untyped = false;
            if (route.IsLocal)
            {
                source = KazoHttpResponseSource.LocalSite;
                (SimulatedHttpResponse? local, KazoNetworkError? error) = await SendLocalAsync(
                    method, currentUri, headers, body, jar, hopCookies, cancellationToken).ConfigureAwait(false);
                if (error is { } localError)
                {
                    return (KazoNetworkResult<KazoHttpResponse>.Failure(localError), first.IsLocal);
                }

                response = local!;
            }
            else if (route.Host is not { } host)
            {
                // Unknown to every source of kazodns: only the optional relay may still know it
                // (ADR 0102). Each real hop is one relay call that does not follow redirects, so the
                // cookies of every hop land in the jar and go back on the next (ADR 0210 §3).
                if (!CanRelay(currentUri.Host)
                    || (realRoute ??= await TryOpenRealRouteAsync(cancellationToken).ConfigureAwait(false)) is not { } real)
                {
                    return (KazoNetworkResult<KazoHttpResponse>.Failure(KazoNetworkError.HostNotFound), first.IsLocal);
                }

                (SimulatedHttpResponse? relayed, KazoNetworkError? relayError, string? relayMessage, untyped) = await SendRealHopAsync(
                    real, method, currentUri, headers, body, jar, hopCookies, includeBody, cancellationToken).ConfigureAwait(false);
                if (relayError is { } failure)
                {
                    return (KazoNetworkResult<KazoHttpResponse>.Failure(failure, relayMessage), first.IsLocal);
                }

                response = relayed!;
                source = KazoHttpResponseSource.RealNetwork;
            }
            else if (!host.IsUp)
            {
                return (KazoNetworkResult<KazoHttpResponse>.Failure(KazoNetworkError.HostUnreachable), first.IsLocal);
            }
            else
            {
                // A name /etc/hosts points at a simulated machine reaches it under its own name.
                Uri target = route.Answer is { Source: KazoDnsSource.HostsFile }
                    ? new UriBuilder(currentUri) { Host = host.Hostname }.Uri
                    : currentUri;
                SimulatedExchangeResult exchange = SendSimulated(method, target, headers, body, jar, hopCookies);
                if (exchange.NetworkError is not null)
                {
                    return (KazoNetworkResult<KazoHttpResponse>.Failure(MapSimulatedError(exchange.NetworkError)), first.IsLocal);
                }

                response = exchange.Response!;
                source = KazoHttpResponseSource.Simulated;
            }

            if (!response.IsRedirect)
            {
                KazoHttpResponse built = BuildResponse(response, currentUri, redirectCount, includeBody, jar, before) with { Source = source };
                // A real site that sent no Content-Type keeps none: the app sniffs the bytes, as curl does.
                return (KazoNetworkResult<KazoHttpResponse>.Success(untyped
                    ? built with { Headers = [.. built.Headers.Where(static header => !header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))] }
                    : built), first.IsLocal);
            }

            if (redirectCount == MaxRedirects)
            {
                return (KazoNetworkResult<KazoHttpResponse>.Failure(KazoNetworkError.TooManyRedirects), first.IsLocal);
            }

            redirectCount++;
            Uri next = new(ResolveLocation(currentUri, response.RedirectUrl!));
            if (!string.Equals(next.Host, currentUri.Host, StringComparison.OrdinalIgnoreCase))
            {
                route = await RouteAsync(next.Host, cancellationToken).ConfigureAwait(false);
            }

            currentUri = next;
            if (RedirectBecomesGet(response.StatusCode, method))
            {
                method = SimulatedHttpMethods.Get;
                body = null;
                includeBody = true;
            }
        }
    }

    /// <summary>
    /// Whether a redirect turns the request into a body-less <c>GET</c>: 303 does for every method but
    /// <c>HEAD</c>; 301 and 302 do for <c>POST</c> only (what every browser does, though the RFC
    /// would keep the method); 307 and 308 never do.
    /// </summary>
    public static bool RedirectBecomesGet(int statusCode, string method) => statusCode switch
    {
        SimulatedHttpStatus.SeeOther => method != SimulatedHttpMethods.Head,
        SimulatedHttpStatus.MovedPermanently or SimulatedHttpStatus.Found => method == SimulatedHttpMethods.Post,
        _ => false
    };

    private async ValueTask<(KazoNetworkResult<IReadOnlyList<KazoPortProbeResult>> Result, bool Local)> ProbeTcpCoreAsync(
        string hostNameOrAddress, int firstPort, int lastPort, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostNameOrAddress);
        cancellationToken.ThrowIfCancellationRequested();
        _capabilities.Require(AppCapabilities.NetworkSimulatedRead);

        Route route = await RouteAsync(hostNameOrAddress, cancellationToken).ConfigureAwait(false);
        if (route.IsLocal)
        {
            return (KazoNetworkResult<IReadOnlyList<KazoPortProbeResult>>.Success(ProbeLocalPorts(firstPort, lastPort)), true);
        }

        SimulatedHost? host = route.Host;
        if (host is null)
        {
            // ADR 0035: only one explicit port is ever relayed to a real host, never a range.
            return (firstPort == lastPort && CanRelay(hostNameOrAddress)
                ? await ProbeRealPortAsync(hostNameOrAddress, firstPort, cancellationToken).ConfigureAwait(false)
                : KazoNetworkResult<IReadOnlyList<KazoPortProbeResult>>.Failure(KazoNetworkError.HostNotFound), false);
        }

        if (!host.IsUp)
        {
            return (KazoNetworkResult<IReadOnlyList<KazoPortProbeResult>>.Failure(KazoNetworkError.HostUnreachable), false);
        }

        IReadOnlyList<KazoPortProbeResult> ports =
        [
            .. host.ScanPorts(firstPort, lastPort).Select(port => new KazoPortProbeResult(
                port.PortNumber, ToPortState(port.State), port.Service?.Name, port.Service?.Version))
        ];
        return (KazoNetworkResult<IReadOnlyList<KazoPortProbeResult>>.Success(ports), false);
    }

    /// <summary>
    /// Probes this machine: a port where a process listens is open (<c>http</c>, served by that
    /// program); the simulated system daemons of <c>localhost</c> keep their own state.
    /// </summary>
    private IReadOnlyList<KazoPortProbeResult> ProbeLocalPorts(int firstPort, int lastPort)
    {
        Dictionary<int, KazoPortProbeResult> ports = [];
        if (_network.GetHost(IPAddress.Loopback.ToString()) is { } daemons)
        {
            foreach (SimulatedPort port in daemons.ScanPorts(firstPort, lastPort))
            {
                ports[port.PortNumber] = new KazoPortProbeResult(port.PortNumber, ToPortState(port.State), port.Service?.Name, port.Service?.Version);
            }
        }

        foreach (ListeningSocketRow row in _local!.Services.Ports.Snapshot())
        {
            if (row.Owner is { } owner && row.Port >= firstPort && row.Port <= lastPort)
            {
                ports[row.Port] = new KazoPortProbeResult(row.Port, KazoPortState.Open, "http", owner.ProgramName);
            }
        }

        return [.. ports.Values.OrderBy(static port => port.Port)];
    }

    private async ValueTask<(KazoNetworkResult<KazoOsFingerprint?> Result, bool Local)> DetectOperatingSystemCoreAsync(
        string hostNameOrAddress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostNameOrAddress);
        cancellationToken.ThrowIfCancellationRequested();
        _capabilities.Require(AppCapabilities.NetworkSimulatedRead);

        Route route = await RouteAsync(hostNameOrAddress, cancellationToken).ConfigureAwait(false);
        // This machine answers with the fingerprint of the simulated localhost, when there is one.
        SimulatedHost? host = route.IsLocal ? _network.GetHost(IPAddress.Loopback.ToString()) : route.Host;
        KazoNetworkResult<KazoOsFingerprint?> result = host switch
        {
            null when route.IsLocal => KazoNetworkResult<KazoOsFingerprint?>.Success(null),
            null => KazoNetworkResult<KazoOsFingerprint?>.Failure(KazoNetworkError.HostNotFound),
            { IsUp: false } => KazoNetworkResult<KazoOsFingerprint?>.Failure(KazoNetworkError.HostUnreachable),
            { OsFingerprint: { } os } => KazoNetworkResult<KazoOsFingerprint?>.Success(new KazoOsFingerprint(os.Name, os.Version, os.Accuracy)),
            _ => KazoNetworkResult<KazoOsFingerprint?>.Success(null)
        };
        return (result, route.IsLocal);
    }

    // ── This machine (ADR 0074) ───────────────────────────────────────────

    /// <summary>Where a target lives: this machine, a simulated host, or nowhere the OS knows.</summary>
    /// <param name="IsLocal">Whether the target is this machine (127.0.0.0/8).</param>
    /// <param name="Host">The simulated host, when the target is one.</param>
    /// <param name="Answer">The resolver's answer, when the gateway has the local stack.</param>
    private readonly record struct Route(bool IsLocal, SimulatedHost? Host, DnsAnswer? Answer);

    /// <summary>Resolves a target through <c>kazodns</c> (or the simulated zone alone, without the local stack).</summary>
    private async ValueTask<Route> RouteAsync(string target, CancellationToken cancellationToken)
    {
        if (_local is not { } local)
        {
            return new Route(false, _network.GetHost(target), null);
        }

        DnsAnswer? answer = target.Contains("://", StringComparison.Ordinal)
            ? null
            : await local.Services.Resolver.ResolveAsync(target, cancellationToken).ConfigureAwait(false);
        if (answer is null)
        {
            return new Route(false, null, null);
        }

        return answer.IsLocal
            ? new Route(true, null, answer)
            : new Route(false, _network.GetHost(answer.Addresses[0].ToString()), answer);
    }

    /// <summary>Whether a target unknown to the OS may be relayed to the real Internet: never the internal <c>.hack</c> domain.</summary>
    private static bool CanRelay(string target)
    {
        string host = Uri.TryCreate(target, UriKind.Absolute, out Uri? uri) && target.Contains("://", StringComparison.Ordinal) ? uri.Host : target;
        return !WebAppUrls.IsInternalDomain(host.TrimEnd('.'));
    }

    /// <summary>
    /// Sends one hop to a simulated machine. Without explicit cookies the jar is handed over as is;
    /// with them the site reads a copy holding the merged cookies of the host, and only the
    /// cookies it sets are written back, so the explicit ones never land in the jar.
    /// </summary>
    private SimulatedExchangeResult SendSimulated(
        string method,
        Uri target,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        SimulatedHttpBody? body,
        Dictionary<string, Dictionary<string, string>> jar,
        IReadOnlyList<KeyValuePair<string, string>> explicitCookies)
    {
        if (explicitCookies.Count == 0)
        {
            return _network.Send(method, target.AbsoluteUri, headers, body, jar);
        }

        string host = target.Host;
        Dictionary<string, string> merged = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> cookie in MergeCookies(jar, host, explicitCookies))
        {
            merged[cookie.Key] = cookie.Value;
        }

        Dictionary<string, Dictionary<string, string>> hopJar = new(StringComparer.OrdinalIgnoreCase) { [host] = merged };
        SimulatedExchangeResult exchange = _network.Send(method, target.AbsoluteUri, headers, body, hopJar);
        if (exchange.Response is { SetCookies.IsEmpty: false } response)
        {
            if (!jar.TryGetValue(host, out Dictionary<string, string>? cookies))
            {
                cookies = new Dictionary<string, string>(StringComparer.Ordinal);
                jar[host] = cookies;
            }

            foreach (KeyValuePair<string, string> cookie in response.SetCookies)
            {
                cookies[cookie.Key] = cookie.Value;
            }
        }

        return exchange;
    }

    /// <summary>
    /// Reads the <c>name=value</c> pairs of every <c>Cookie</c> header the app wrote (RFC 6265
    /// section 4.2), in order; a pair without <c>=</c> or without a name is skipped.
    /// </summary>
    private static IReadOnlyList<KeyValuePair<string, string>> ParseCookieHeaders(
        IEnumerable<KeyValuePair<string, string>> headers)
    {
        List<KeyValuePair<string, string>> cookies = [];
        foreach (KeyValuePair<string, string> header in headers)
        {
            if (!header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (string pair in header.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int equals = pair.IndexOf('=', StringComparison.Ordinal);
                if (equals > 0)
                {
                    cookies.Add(new(pair[..equals].Trim(), pair[(equals + 1)..].Trim()));
                }
            }
        }

        return cookies;
    }

    /// <summary>
    /// The cookies a hop to <paramref name="host"/> carries: the jar's, in order, then the explicit
    /// ones; an explicit cookie replaces the jar's cookie of the same name, as what the user typed wins.
    /// </summary>
    private static IReadOnlyList<KeyValuePair<string, string>> MergeCookies(
        Dictionary<string, Dictionary<string, string>> jar,
        string host,
        IReadOnlyList<KeyValuePair<string, string>> explicitCookies)
    {
        List<KeyValuePair<string, string>> cookies = [];
        Dictionary<string, int> positions = new(StringComparer.Ordinal);
        if (jar.TryGetValue(host, out Dictionary<string, string>? stored))
        {
            foreach (KeyValuePair<string, string> cookie in stored)
            {
                positions[cookie.Key] = cookies.Count;
                cookies.Add(cookie);
            }
        }

        foreach (KeyValuePair<string, string> cookie in explicitCookies)
        {
            if (positions.TryGetValue(cookie.Key, out int position))
            {
                cookies[position] = cookie;
            }
            else
            {
                positions[cookie.Key] = cookies.Count;
                cookies.Add(cookie);
            }
        }

        return cookies;
    }

    /// <summary>
    /// Delivers one HTTP hop to the process listening on this machine (ADR 0074, section 2): routed
    /// by port, <c>Host</c> and path as <c>http.sys</c> does, the owning web app started on demand
    /// when nothing listens yet (section 3). The reply comes back as a simulated response so the
    /// redirect loop treats both worlds alike; its cookies land in <paramref name="jar"/>. The
    /// <c>Cookie</c> header carries the jar's cookies of the host merged with <paramref name="explicitCookies"/>.
    /// </summary>
    private async ValueTask<(SimulatedHttpResponse? Response, KazoNetworkError? Error)> SendLocalAsync(
        string method,
        Uri uri,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        SimulatedHttpBody? body,
        Dictionary<string, Dictionary<string, string>> jar,
        IReadOnlyList<KeyValuePair<string, string>> explicitCookies,
        CancellationToken cancellationToken)
    {
        LocalNetworkCaller local = _local!;
        string host = uri.Host.ToLowerInvariant();
        int port = uri.Port;
        string path = uri.AbsolutePath;
        OsHttpListenerSocket? socket = local.Services.Ports.Route(host, port, path, loopbackDestination: true);
        if (socket is null && local.Services.Activator is { } activator && local.Services.Resolver.FindWebApp(host) is { } app)
        {
            socket = await activator.ActivateAsync(app, local.Principal, host, port, path, cancellationToken).ConfigureAwait(false);
        }

        if (socket is null)
        {
            if (local.Services.Ports.IsListening(port, loopbackDestination: true))
            {
                // The port is open but no prefix claims this host: http.sys answers for the sites.
                return (new SimulatedHttpResponse(
                    400, Page: null, RedirectUrl: null, ImmutableDictionary<string, string>.Empty, ImmutableDictionary<string, string>.Empty,
                    new SimulatedHttpBody("text/html; charset=us-ascii", Encoding.ASCII.GetBytes(
                        "<h2>Bad Request - Invalid Hostname</h2>\n<hr><p>HTTP Error 400. The request hostname is invalid.</p>\n"))), null);
            }

            // Nothing bound to that port: the kernel answers the SYN with a RST.
            return (null, KazoNetworkError.ConnectionRefused);
        }

        List<KeyValuePair<string, string>> wire =
        [
            new("Host", uri.IsDefaultPort ? host : $"{host}:{port}"),
            .. headers.Where(static header =>
                !header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)
                && !header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
                && !header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
                && !header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
        ];
        IReadOnlyList<KeyValuePair<string, string>> cookies = MergeCookies(jar, host, explicitCookies);
        if (cookies.Count > 0)
        {
            wire.Add(new("Cookie", string.Join("; ", cookies.Select(static cookie => $"{cookie.Key}={cookie.Value}"))));
        }

        byte[] bytes = body?.ToArray() ?? [];
        if (body is not null)
        {
            wire.Add(new("Content-Type", body.ContentType));
            wire.Add(new("Content-Length", bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        IPEndPoint remote = new(IPAddress.Loopback, local.Services.Ports.NextEphemeralPort());
        IPEndPoint localEndPoint = new(IPAddress.Loopback, port);
        KazoHttpListenerReply? reply = await socket.ExchangeAsync(
            id => new KazoHttpListenerExchange(id, method, uri, wire, body?.ContentType, bytes, remote, localEndPoint),
            local.Services.Ports.ResponseTimeout,
            cancellationToken).ConfigureAwait(false);
        if (reply is null)
        {
            // The process closed the socket, died or never answered: "empty reply from server".
            return (null, KazoNetworkError.ConnectionFailed);
        }

        return (ToSimulatedResponse(reply, host, jar), null);
    }

    /// <summary>Turns a listener's reply into a simulated response, storing its <c>Set-Cookie</c> headers in the jar.</summary>
    private static SimulatedHttpResponse ToSimulatedResponse(
        KazoHttpListenerReply reply, string host, Dictionary<string, Dictionary<string, string>> jar)
    {
        ImmutableDictionary<string, string>.Builder headers = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        string? location = null;
        foreach (KeyValuePair<string, string> header in reply.Headers)
        {
            if (header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
            {
                ApplySetCookie(jar, host, header.Value);
            }
            else if (!header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
                && !header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                headers[header.Key] = header.Value;
                if (header.Key.Equals("Location", StringComparison.OrdinalIgnoreCase))
                {
                    location = header.Value;
                }
            }
        }

        SimulatedHttpBody? body = reply.ContentType is { } contentType
            ? new SimulatedHttpBody(contentType, reply.Body)
            : reply.Body.Length > 0 ? new SimulatedHttpBody(SimulatedMediaTypes.OctetStream, reply.Body) : null;
        return new SimulatedHttpResponse(
            reply.StatusCode, Page: null, location, ImmutableDictionary<string, string>.Empty, headers.ToImmutable(), body);
    }

    /// <summary>
    /// Applies one <c>Set-Cookie</c> header (RFC 6265: <c>name=value; attributes</c>) to the host's
    /// cookies; <c>Max-Age=0</c> or an empty value deletes the cookie.
    /// </summary>
    private static void ApplySetCookie(Dictionary<string, Dictionary<string, string>> jar, string host, string header)
    {
        string[] parts = header.Split(';', StringSplitOptions.TrimEntries);
        int equals = parts[0].IndexOf('=', StringComparison.Ordinal);
        if (equals <= 0)
        {
            return;
        }

        string name = parts[0][..equals].Trim();
        string value = parts[0][(equals + 1)..].Trim();
        bool expired = parts.Skip(1).Any(static attribute =>
            attribute.Equals("Max-Age=0", StringComparison.OrdinalIgnoreCase)
            || attribute.StartsWith("Max-Age=-", StringComparison.OrdinalIgnoreCase));
        if (!jar.TryGetValue(host, out Dictionary<string, string>? cookies))
        {
            cookies = new Dictionary<string, string>(StringComparer.Ordinal);
            jar[host] = cookies;
        }

        if (expired || value.Length == 0)
        {
            cookies.Remove(name);
        }
        else
        {
            cookies[name] = value;
        }
    }

    // ── Real-network fallback ─────────────────────────────────────────────

    /// <summary>
    /// Opens the route to the real Internet for one call: <see langword="null"/> unless the app
    /// holds <c>network.real.access</c> and the optional server is connected with a valid token.
    /// </summary>
    private async ValueTask<(Uri ServerBaseUrl, string AccessToken)?> TryOpenRealRouteAsync(CancellationToken cancellationToken)
    {
        if (_serverConnection is null || _proxy is null
            || !_capabilities.Evaluate(AppCapabilities.NetworkRealAccess).Granted)
        {
            return null;
        }

        ServerConnectionState? state = await _serverConnection.GetStateAsync(cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return null;
        }

        string? accessToken = await _serverConnection.EnsureAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        return accessToken is null ? null : (new Uri(state.ServerBaseUrl), accessToken);
    }

    private async ValueTask<KazoNetworkResult<KazoPingReply>> PingRealHostAsync(string target, CancellationToken cancellationToken)
    {
        if (await TryOpenRealRouteAsync(cancellationToken).ConfigureAwait(false) is not { } route)
        {
            return KazoNetworkResult<KazoPingReply>.Failure(KazoNetworkError.HostNotFound);
        }

        string url = target.Contains("://", StringComparison.Ordinal) ? target : $"https://{target}";
        try
        {
            ProxyHttpResponse response = await _proxy!.ExecuteHttpRequestAsync(
                route.ServerBaseUrl,
                route.AccessToken,
                new ProxyHttpRequest(Guid.NewGuid(), ProxyProtocol.Http, url, SimulatedHttpMethods.Head, [], null, 0, PingTimeoutSeconds, _appId),
                cancellationToken).ConfigureAwait(false);
            return KazoNetworkResult<KazoPingReply>.Success(
                new KazoPingReply(null, KazoIPStatus.Success, TimeSpan.FromMilliseconds(response.DurationMs)));
        }
        catch (Exception exception) when (exception is ServerConnectionException or HttpRequestException)
        {
            return KazoNetworkResult<KazoPingReply>.Success(new KazoPingReply(null, KazoIPStatus.TimedOut, TimeSpan.Zero));
        }
    }

    /// <summary>
    /// Sends one hop to the real Internet through the relay (ADR 0102, ADR 0210 §2 and §3): the
    /// server does not follow redirects, the <c>Cookie</c> header carries the jar's cookies of the host
    /// (its own and the domain cookies that match it), and the <c>Set-Cookie</c> headers of the answer
    /// land in the jar. The answer comes back as a simulated response so the redirect loop treats
    /// every world alike.
    /// </summary>
    private async ValueTask<(SimulatedHttpResponse? Response, KazoNetworkError? Error, string? Message, bool Untyped)> SendRealHopAsync(
        (Uri ServerBaseUrl, string AccessToken) route,
        string method,
        Uri uri,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        SimulatedHttpBody? body,
        Dictionary<string, Dictionary<string, string>> jar,
        IReadOnlyList<KeyValuePair<string, string>> explicitCookies,
        bool includeBody,
        CancellationToken cancellationToken)
    {
        string host = uri.Host.ToLowerInvariant();
        List<ProxyHeader> wire =
        [
            .. headers
                .Where(static header => !header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
                .Select(static header => new ProxyHeader(header.Key, header.Value))
        ];
        IReadOnlyList<KeyValuePair<string, string>> cookies = MergeCookies(RealCookieView(jar, host), host, explicitCookies);
        if (cookies.Count > 0)
        {
            wire.Add(new("Cookie", string.Join("; ", cookies.Select(static cookie => $"{cookie.Key}={cookie.Value}"))));
        }

        ProxyHttpResponse response;
        try
        {
            response = await _proxy!.ExecuteHttpRequestAsync(
                route.ServerBaseUrl,
                route.AccessToken,
                ProxyHttpRequest.Create(
                    Guid.NewGuid(),
                    uri.AbsoluteUri,
                    method,
                    wire,
                    body?.ToArray(),
                    body?.ContentType,
                    ProxyTimeoutSeconds,
                    _appId,
                    includeBody) with { FollowRedirects = false },
                cancellationToken).ConfigureAwait(false);
        }
        catch (ServerConnectionException exception)
        {
            return (null, MapRelayError(exception.ProxyErrorCode), exception.Message, false);
        }
        catch (HttpRequestException exception)
        {
            return (null, KazoNetworkError.ConnectionFailed, exception.Message, false);
        }

        ImmutableDictionary<string, string>.Builder responseHeaders = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        string? contentType = null;
        string? location = null;
        foreach (ProxyHeader header in response.Headers)
        {
            if (header.Name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
            {
                ApplyRealSetCookie(jar, host, header.Value);
            }
            else if (header.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                contentType = header.Value;
            }
            else if (!header.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                responseHeaders[header.Name] = header.Value;
                if (header.Name.Equals("Location", StringComparison.OrdinalIgnoreCase))
                {
                    location = header.Value;
                }
            }
        }

        byte[] content = includeBody && response.BodyBase64 is not null ? Convert.FromBase64String(response.BodyBase64) : [];
        SimulatedHttpBody? responseBody = contentType is not null
            ? new SimulatedHttpBody(contentType, content)
            : content.Length > 0 ? new SimulatedHttpBody(SimulatedMediaTypes.OctetStream, content) : null;
        bool isRedirect = response.StatusCode is 301 or 302 or 303 or 307 or 308 && location is not null;
        return (new SimulatedHttpResponse(
            response.StatusCode,
            Page: null,
            isRedirect ? location : null,
            ImmutableDictionary<string, string>.Empty,
            responseHeaders.ToImmutable(),
            responseBody), null, null, contentType is null);
    }

    /// <summary>
    /// Maps a relay refusal (<c>ProxyErrorCode</c>) to what the app sees (ADR 0210 §6): a rule of
    /// the server's operator is "blocked by the administrator", a slow site is a timeout; the
    /// operator's address and token never reach the app.
    /// </summary>
    internal static KazoNetworkError MapRelayError(string? proxyErrorCode) => proxyErrorCode switch
    {
        ProxyErrorCode.BlockedAddress or ProxyErrorCode.BlockedPort or ProxyErrorCode.DnsRebindingBlocked
            or ProxyErrorCode.GrantMissing or ProxyErrorCode.RelayDisabled or ProxyErrorCode.CapabilityDenied
            or ProxyErrorCode.SimulatedDomainBlocked => KazoNetworkError.BlockedByAdministrator,
        ProxyErrorCode.Timeout => KazoNetworkError.TimedOut,
        ProxyErrorCode.TooManyRedirects => KazoNetworkError.TooManyRedirects,
        ProxyErrorCode.ConnectionRefused => KazoNetworkError.ConnectionRefused,
        ProxyErrorCode.HostUnreachable => KazoNetworkError.HostUnreachable,
        _ => KazoNetworkError.ConnectionFailed
    };

    /// <summary>
    /// The jar seen by a real hop to <paramref name="host"/>: the host's own cookies, then the domain
    /// cookies (keys <c>.example.com</c>) that match it, the host's own winning on a shared name.
    /// </summary>
    private static Dictionary<string, Dictionary<string, string>> RealCookieView(
        Dictionary<string, Dictionary<string, string>> jar, string host)
    {
        Dictionary<string, string> cookies = new(StringComparer.Ordinal);
        foreach ((string key, Dictionary<string, string> domainCookies) in jar)
        {
            if (KazoCookieContainer.DomainMatches(host, key))
            {
                foreach (KeyValuePair<string, string> cookie in domainCookies)
                {
                    cookies[cookie.Key] = cookie.Value;
                }
            }
        }

        if (jar.TryGetValue(host, out Dictionary<string, string>? own))
        {
            foreach (KeyValuePair<string, string> cookie in own)
            {
                cookies[cookie.Key] = cookie.Value;
            }
        }

        return new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase) { [host] = cookies };
    }

    /// <summary>
    /// Applies one <c>Set-Cookie</c> header of a real site (RFC 6265 §5.2 and §5.3, ADR 0210 §2). A
    /// <c>Domain</c> attribute that domain-matches the host stores a domain cookie under
    /// <c>.example.com</c>; one that does not (another site, or a single label like <c>com</c>) drops
    /// the cookie, as a browser does. <c>Max-Age</c> of 0 or less, or an <c>Expires</c> date in the
    /// past, deletes it. <c>Path</c>, <c>Secure</c>, <c>HttpOnly</c> and <c>SameSite</c> are not kept.
    /// </summary>
    internal static void ApplyRealSetCookie(Dictionary<string, Dictionary<string, string>> jar, string host, string header)
    {
        string[] parts = header.Split(';', StringSplitOptions.TrimEntries);
        int equals = parts[0].IndexOf('=', StringComparison.Ordinal);
        if (equals <= 0)
        {
            return;
        }

        string name = parts[0][..equals].Trim();
        string value = parts[0][(equals + 1)..].Trim();
        string key = host;
        bool expired = false;
        foreach (string attribute in parts.Skip(1))
        {
            int separator = attribute.IndexOf('=', StringComparison.Ordinal);
            string attributeName = separator < 0 ? attribute : attribute[..separator].Trim();
            string attributeValue = separator < 0 ? string.Empty : attribute[(separator + 1)..].Trim();
            if (attributeName.Equals("Domain", StringComparison.OrdinalIgnoreCase) && attributeValue.Length > 0)
            {
                string domain = attributeValue.TrimStart('.').ToLowerInvariant();
                if (!domain.Contains('.', StringComparison.Ordinal) || !KazoCookieContainer.DomainMatches(host, "." + domain))
                {
                    return;
                }

                key = "." + domain;
            }
            else if (attributeName.Equals("Max-Age", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(attributeValue, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out int maxAge))
            {
                expired = maxAge <= 0;
            }
            else if (attributeName.Equals("Expires", StringComparison.OrdinalIgnoreCase)
                && DateTimeOffset.TryParse(attributeValue, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out DateTimeOffset expires)
                && !parts.Any(static part => part.StartsWith("Max-Age", StringComparison.OrdinalIgnoreCase)))
            {
                expired = expires <= DateTimeOffset.UtcNow;
            }
        }

        if (!jar.TryGetValue(key, out Dictionary<string, string>? cookies))
        {
            cookies = new Dictionary<string, string>(StringComparer.Ordinal);
            jar[key] = cookies;
        }

        if (expired)
        {
            cookies.Remove(name);
        }
        else
        {
            cookies[name] = value;
        }
    }

    private async ValueTask<KazoNetworkResult<IReadOnlyList<KazoPortProbeResult>>> ProbeRealPortAsync(
        string target, int port, CancellationToken cancellationToken)
    {
        if (await TryOpenRealRouteAsync(cancellationToken).ConfigureAwait(false) is not { } route)
        {
            return KazoNetworkResult<IReadOnlyList<KazoPortProbeResult>>.Failure(KazoNetworkError.HostNotFound);
        }

        try
        {
            ProxyTcpProbeResponse response = await _proxy!.ExecuteTcpProbeAsync(
                route.ServerBaseUrl,
                route.AccessToken,
                new ProxyTcpProbeRequest(Guid.NewGuid(), target, port, ProbeTimeoutSeconds, _appId),
                cancellationToken).ConfigureAwait(false);
            KazoPortState state = response.State switch
            {
                ProxyTcpProbeState.Open => KazoPortState.Open,
                ProxyTcpProbeState.Closed => KazoPortState.Closed,
                _ => KazoPortState.Filtered
            };
            return KazoNetworkResult<IReadOnlyList<KazoPortProbeResult>>.Success([new KazoPortProbeResult(port, state)]);
        }
        catch (Exception exception) when (exception is ServerConnectionException or HttpRequestException)
        {
            return KazoNetworkResult<IReadOnlyList<KazoPortProbeResult>>.Failure(KazoNetworkError.ConnectionFailed, exception.Message);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private KazoHostEntry BuildEntry(string hostName, string ip)
    {
        string[] aliases =
        [
            .. _network.Dns.AllRecords
                .Where(record => record.Ip == ip && !string.Equals(record.Hostname, hostName, StringComparison.OrdinalIgnoreCase))
                .Select(record => record.Hostname)
        ];
        return new KazoHostEntry(hostName, [IPAddress.Parse(ip)], aliases);
    }

    /// <summary>
    /// Turns a site's response into what the app receives. The body is the response's own
    /// <see cref="SimulatedHttpBody"/> with its <c>Content-Type</c>; a historical structured page
    /// travels as its text rendering, <c>text/plain</c>, with the page itself attached for the
    /// browser. A response with neither has no <c>Content-Type</c>, like a real 204 or 302.
    /// </summary>
    private static KazoHttpResponse BuildResponse(
        SimulatedHttpResponse response,
        Uri finalUri,
        int redirectCount,
        bool includeBody,
        Dictionary<string, Dictionary<string, string>> jar,
        HashSet<KazoCookie> before)
    {
        // A HEAD answer announces the length of the body a GET would have returned, without it.
        byte[] body = [];
        string? contentType = null;
        if (response.Body is { } responseBody)
        {
            body = responseBody.ToArray();
            contentType = responseBody.ContentType;
        }
        else if (response.Page is { } page)
        {
            using StringWriter writer = new() { NewLine = "\n" };
            SimulatedPageTextFormatter.WriteTo(writer, page);
            body = Encoding.UTF8.GetBytes(writer.ToString());
            contentType = "text/plain; charset=utf-8";
        }

        KazoCookie[] setCookies = [.. FromJar(jar).Where(cookie => !before.Contains(cookie))];
        List<KeyValuePair<string, string>> headers =
        [
            .. response.Headers.Where(static header =>
                !header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
                && !header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
        ];
        if (contentType is not null)
        {
            headers.Add(new("Content-Type", contentType));
        }

        headers.Add(new("Content-Length", body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        headers.AddRange(setCookies.Select(cookie => new KeyValuePair<string, string>("Set-Cookie", $"{cookie.Name}={cookie.Value}; Path=/")));

        return new KazoHttpResponse(
            response.StatusCode,
            KazoHttpResponse.GetReasonPhrase(response.StatusCode),
            finalUri,
            headers,
            includeBody ? body : [],
            response.Page,
            redirectCount,
            setCookies);
    }

    private static Dictionary<string, Dictionary<string, string>> ToJar(IReadOnlyList<KazoCookie> cookies)
    {
        Dictionary<string, Dictionary<string, string>> jar = new(StringComparer.OrdinalIgnoreCase);
        foreach (KazoCookie cookie in cookies)
        {
            if (!jar.TryGetValue(cookie.Domain, out Dictionary<string, string>? hostCookies))
            {
                hostCookies = new Dictionary<string, string>(StringComparer.Ordinal);
                jar[cookie.Domain] = hostCookies;
            }

            hostCookies[cookie.Name] = cookie.Value;
        }

        return jar;
    }

    private static IEnumerable<KazoCookie> FromJar(Dictionary<string, Dictionary<string, string>> jar) =>
        jar.SelectMany(host => host.Value.Select(cookie => new KazoCookie(host.Key.ToLowerInvariant(), cookie.Key, cookie.Value)));

    private static string ResolveLocation(Uri requestUri, string location) =>
        Uri.TryCreate(requestUri, location, out Uri? resolved) ? resolved.AbsoluteUri : location;

    private static KazoNetworkError MapSimulatedError(string error) => error switch
    {
        SimulatedNetworkErrors.UnknownHost => KazoNetworkError.HostNotFound,
        SimulatedNetworkErrors.ConnectionRefused => KazoNetworkError.ConnectionRefused,
        SimulatedNetworkErrors.TooManyRedirects => KazoNetworkError.TooManyRedirects,
        _ => KazoNetworkError.HostUnreachable
    };

    private static KazoPortState ToPortState(SimulatedPortState state) => state switch
    {
        SimulatedPortState.Open => KazoPortState.Open,
        SimulatedPortState.Closed => KazoPortState.Closed,
        _ => KazoPortState.Filtered
    };
}
