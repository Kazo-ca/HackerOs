namespace Kazo.Os.Server.Services;

/// <summary>
/// The <c>KazoOs:Proxy</c> section of <c>appsettings.json</c> (ADR 0102 §5), as bound by
/// <c>IOptions</c>. Every list is <see langword="null"/> when the file does not set it, and
/// <see cref="ProxyRelayPolicy.From"/> fills the defaults: the configuration binder appends to a
/// list initialized in code instead of replacing it, so defaults cannot live in the initializers.
/// </summary>
public sealed class ProxyOptions
{
    /// <summary>The configuration section this class binds.</summary>
    public const string SectionName = "KazoOs:Proxy";

    /// <summary>Turns the whole relay off. Default <see langword="true"/>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>HTTP requests.</summary>
    public ProxyPortOptions Http { get; set; } = new();

    /// <summary>TCP streams (<c>nmap</c>'s single-port probe ignores this list, ADR 0035).</summary>
    public ProxyPortOptions Tcp { get; set; } = new();

    /// <summary>UDP streams.</summary>
    public ProxyPortOptions Udp { get; set; } = new();

    /// <summary>"Tout autoriser": no port filter at all. Default <see langword="false"/>.</summary>
    public bool AllowAllPorts { get; set; }

    /// <summary>
    /// Lets the relay reach private ranges (the local network of the server's machine). Loopback,
    /// link-local and cloud metadata (<c>169.254.0.0/16</c>) stay blocked. Default <see langword="false"/>.
    /// </summary>
    public bool AllowPrivateNetworks { get; set; }

    /// <summary>Size, concurrency, stream and idle limits.</summary>
    public ProxyQuotaOptions Quotas { get; set; } = new();

    /// <summary>
    /// App ids the server grants <c>network.real.access</c> to on its own (ADR 0102 §4); defaults
    /// to the system commands that declare it. A non-revoked grant record the server holds in the
    /// account's synchronized grants also counts.
    /// </summary>
    public string[]? GrantedApps { get; set; }

    /// <summary>
    /// Turns an explicit empty <c>BlockedPorts</c> (<c>"BlockedPorts": []</c>) into an empty list.
    /// The JSON provider stores an empty array as a key with a <see langword="null"/> value, which the
    /// binder leaves unbound, so without this an operator could not empty the list: the key is found
    /// among the section's children instead.
    /// </summary>
    /// <param name="options">The bound options, changed in place (the options pattern binds into one instance).</param>
    /// <param name="section">The <c>KazoOs:Proxy</c> section.</param>
    public static void KeepExplicitEmptyLists(ProxyOptions options, Microsoft.Extensions.Configuration.IConfiguration section)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(section);
        foreach ((string name, ProxyPortOptions ports) in new[] { ("Tcp", options.Tcp), ("Udp", options.Udp) })
        {
            if (ports.BlockedPorts is null
                && section.GetSection(name).GetChildren().Any(static child => child.Key == nameof(ProxyPortOptions.BlockedPorts)))
            {
                ports.BlockedPorts = [];
            }
        }
    }
}

/// <summary>Port filter of one relayed protocol.</summary>
public sealed class ProxyPortOptions
{
    /// <summary>Allowed target ports; <see langword="null"/> or empty keeps the protocol's default.</summary>
    public int[]? AllowedPorts { get; set; }

    /// <summary>
    /// Ports the relay never opens, whatever <see cref="AllowedPorts"/>, the ports an app declares
    /// (ADR 0235 §3) or <see cref="ProxyOptions.AllowAllPorts"/> say. <see langword="null"/> keeps
    /// the protocol's default list; an explicit empty list (<c>[]</c>, see
    /// <see cref="ProxyOptions.KeepExplicitEmptyLists"/>) blocks nothing and is warned about at startup.
    /// Not read for <see cref="ProxyOptions.Http"/>: HTTP requests use the TCP list.
    /// </summary>
    public int[]? BlockedPorts { get; set; }
}

/// <summary>Quotas of the relay (ADR 0102 §5). Unset values keep their defaults.</summary>
public sealed class ProxyQuotaOptions
{
    /// <summary>"Désactiver les quotas" when <see langword="false"/>. Default <see langword="true"/>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Largest response body, in bytes. Default 10 MiB.</summary>
    public long? MaxResponseBytes { get; set; }

    /// <summary>Largest request body, in bytes. Default 10 MiB.</summary>
    public long? MaxRequestBytes { get; set; }

    /// <summary>HTTP requests and probes in flight at once for one device. Default 8.</summary>
    public int? MaxConcurrentPerDevice { get; set; }

    /// <summary>TCP/UDP streams open at once for one device. Default 4.</summary>
    public int? MaxStreamsPerDevice { get; set; }

    /// <summary>Redirects followed for one HTTP request. Default 5; still applies with quotas off.</summary>
    public int? MaxRedirectHops { get; set; }

    /// <summary>Seconds without traffic after which a stream is closed. Default 300.</summary>
    public int? StreamIdleTimeoutSeconds { get; set; }
}

/// <summary>
/// The effective relay policy: <see cref="ProxyOptions"/> with defaults filled and quotas applied.
/// Built once at startup; <c>GET /api/proxy/policy</c> reports it.
/// </summary>
/// <param name="Enabled">Whether the relay is on.</param>
/// <param name="HttpPorts">Allowed HTTP ports (ignored when <paramref name="AllowAllPorts"/>).</param>
/// <param name="TcpPorts">Allowed TCP stream ports.</param>
/// <param name="UdpPorts">Allowed UDP stream ports.</param>
/// <param name="AllowAllPorts">No port filter.</param>
/// <param name="AllowPrivateNetworks">Private ranges reachable.</param>
/// <param name="QuotasEnabled">Whether the limits below apply.</param>
/// <param name="MaxResponseBytes">Largest response body (configured value, even with quotas off).</param>
/// <param name="MaxRequestBytes">Largest request body (configured value).</param>
/// <param name="MaxConcurrentPerDevice">Requests in flight per device (configured value).</param>
/// <param name="MaxStreamsPerDevice">Streams open per device (configured value).</param>
/// <param name="MaxRedirectHops">Redirects followed; enforced even with quotas off, a redirect loop never ends otherwise.</param>
/// <param name="StreamIdleTimeoutSeconds">Idle timeout of a stream (configured value).</param>
/// <param name="GrantedApps">App ids granted <c>network.real.access</c> by the server itself.</param>
/// <param name="TcpBlockedPorts">TCP ports never opened, HTTP requests included (ADR 0235 §3).</param>
/// <param name="UdpBlockedPorts">UDP ports never opened (ADR 0235 §3).</param>
public sealed record ProxyRelayPolicy(
    bool Enabled,
    IReadOnlyList<int> HttpPorts,
    IReadOnlyList<int> TcpPorts,
    IReadOnlyList<int> UdpPorts,
    bool AllowAllPorts,
    bool AllowPrivateNetworks,
    bool QuotasEnabled,
    long MaxResponseBytes,
    long MaxRequestBytes,
    int MaxConcurrentPerDevice,
    int MaxStreamsPerDevice,
    int MaxRedirectHops,
    int StreamIdleTimeoutSeconds,
    IReadOnlyList<string> GrantedApps,
    IReadOnlyList<int> TcpBlockedPorts,
    IReadOnlyList<int> UdpBlockedPorts)
{
    /// <summary>Default HTTP ports.</summary>
    public static readonly IReadOnlyList<int> DefaultHttpPorts = [80, 443];

    /// <summary>Default TCP stream ports.</summary>
    public static readonly IReadOnlyList<int> DefaultTcpPorts = [80, 443];

    /// <summary>Default UDP ports: DNS and NTP.</summary>
    public static readonly IReadOnlyList<int> DefaultUdpPorts = [53, 123];

    /// <summary>
    /// Default TCP ports the relay never opens (ADR 0235 §3), even for an app that declares them:
    /// mail (25, 465, 587: the server's address would send spam) and the Windows and Unix file and
    /// RPC services (111, 135, 137-139, 445, 2049) that worms and scans aim at.
    /// </summary>
    public static readonly IReadOnlyList<int> DefaultTcpBlockedPorts = [25, 111, 135, 137, 138, 139, 445, 465, 587, 2049];

    /// <summary>
    /// Default UDP ports the relay never opens (ADR 0235 §3): services known for reflection and
    /// amplification attacks (chargen 19, rpcbind 111, NetBIOS 137-138, CLDAP 389, SSDP 1900,
    /// memcached 11211).
    /// </summary>
    public static readonly IReadOnlyList<int> DefaultUdpBlockedPorts = [19, 111, 137, 138, 389, 1900, 11211];

    /// <summary>
    /// The system apps that declare <c>network.real.access</c> (curl, wget, ping, cat, nmap and the
    /// Browser, ADR 0210 §1): granted by default, as the OS grants them through its system policy.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultGrantedApps =
    [
        "org.kazoos.commands.curl",
        "org.kazoos.commands.wget",
        "org.kazoos.commands.ping",
        "org.kazoos.cmd.cat",
        "org.kazoos.commands.nmap",
        "org.kazoos.browser"
    ];

    /// <summary>The default policy, as an absent <c>KazoOs:Proxy</c> section gives.</summary>
    public static ProxyRelayPolicy Default { get; } = From(new ProxyOptions());

    /// <summary>Gets the response size limit actually enforced.</summary>
    public long EffectiveMaxResponseBytes => QuotasEnabled ? MaxResponseBytes : Array.MaxLength;

    /// <summary>Gets the request size limit actually enforced.</summary>
    public long EffectiveMaxRequestBytes => QuotasEnabled ? MaxRequestBytes : Array.MaxLength;

    /// <summary>Gets the per-device concurrency limit actually enforced.</summary>
    public int EffectiveMaxConcurrentPerDevice => QuotasEnabled ? MaxConcurrentPerDevice : int.MaxValue;

    /// <summary>Gets the per-device stream limit actually enforced.</summary>
    public int EffectiveMaxStreamsPerDevice => QuotasEnabled ? MaxStreamsPerDevice : int.MaxValue;

    /// <summary>Gets the stream idle timeout actually enforced; <see cref="Timeout.InfiniteTimeSpan"/> with quotas off.</summary>
    public TimeSpan EffectiveStreamIdleTimeout => QuotasEnabled
        ? TimeSpan.FromSeconds(StreamIdleTimeoutSeconds)
        : Timeout.InfiniteTimeSpan;

    /// <summary>Builds the effective policy from bound options, filling defaults and clamping nonsense values.</summary>
    /// <param name="options">The bound section.</param>
    public static ProxyRelayPolicy From(ProxyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        const long tenMiB = 10L * 1024 * 1024;
        ProxyQuotaOptions quotas = options.Quotas ?? new ProxyQuotaOptions();
        return new ProxyRelayPolicy(
            options.Enabled,
            Ports(options.Http?.AllowedPorts, DefaultHttpPorts),
            Ports(options.Tcp?.AllowedPorts, DefaultTcpPorts),
            Ports(options.Udp?.AllowedPorts, DefaultUdpPorts),
            options.AllowAllPorts,
            options.AllowPrivateNetworks,
            quotas.Enabled,
            Positive(quotas.MaxResponseBytes, tenMiB),
            Positive(quotas.MaxRequestBytes, tenMiB),
            (int)Positive(quotas.MaxConcurrentPerDevice, 8),
            (int)Positive(quotas.MaxStreamsPerDevice, 4),
            quotas.MaxRedirectHops is >= 0 and var hops ? hops : 5,
            (int)Positive(quotas.StreamIdleTimeoutSeconds, 300),
            options.GrantedApps is { Length: > 0 } apps
                ? [.. apps.Where(static app => !string.IsNullOrWhiteSpace(app)).Select(static app => app.Trim()).Distinct(StringComparer.Ordinal)]
                : DefaultGrantedApps,
            BlockedPorts(options.Tcp?.BlockedPorts, DefaultTcpBlockedPorts),
            BlockedPorts(options.Udp?.BlockedPorts, DefaultUdpBlockedPorts));
    }

    /// <summary>
    /// Whether <paramref name="port"/> passes the filter (ADR 0235 §3): never when it is in
    /// <paramref name="blocked"/>; otherwise when every port is allowed, it is in
    /// <paramref name="allowed"/>, or the app declared it and the declaration was approved
    /// (<paramref name="approvedForApp"/>).
    /// </summary>
    /// <param name="allowed">The protocol's allow-list.</param>
    /// <param name="blocked">The protocol's block-list; it always wins.</param>
    /// <param name="port">Target port.</param>
    /// <param name="approvedForApp">Whether the app's grant approves this port for this protocol.</param>
    public bool IsPortAllowed(IReadOnlyList<int> allowed, IReadOnlyList<int> blocked, int port, bool approvedForApp = false) =>
        port is >= 1 and <= 65535
        && !blocked.Contains(port)
        && (AllowAllPorts || allowed.Contains(port) || approvedForApp);

    /// <summary>The block-list of a stream protocol (<c>tcp</c> or <c>udp</c>).</summary>
    /// <param name="protocol">A <c>RealNetworkPorts</c> protocol name.</param>
    public IReadOnlyList<int> BlockedPortsOf(string protocol) =>
        protocol == Kazo.Os.App.Abstractions.RealNetworkPorts.Udp ? UdpBlockedPorts : TcpBlockedPorts;

    /// <summary>
    /// The settings that weaken the defaults, one sentence each: logged as warnings at startup and
    /// returned by <c>GET /api/proxy/policy</c>.
    /// </summary>
    public IReadOnlyList<string> Weakenings()
    {
        List<string> warnings = [];
        if (!Enabled)
        {
            return warnings;
        }

        if (AllowAllPorts)
        {
            warnings.Add("KazoOs:Proxy:AllowAllPorts is on: the relay reaches every TCP and UDP port.");
        }

        if (AllowPrivateNetworks)
        {
            warnings.Add("KazoOs:Proxy:AllowPrivateNetworks is on: the relay reaches the private networks of the server's machine (loopback and cloud metadata stay blocked).");
        }

        if (!QuotasEnabled)
        {
            warnings.Add("KazoOs:Proxy:Quotas:Enabled is off: no size, concurrency, stream or idle limit applies.");
        }

        if (TcpBlockedPorts.Count == 0)
        {
            warnings.Add("KazoOs:Proxy:Tcp:BlockedPorts is empty: the relay may open every TCP port an app declares or the allow-list names, mail (25) included.");
        }

        if (UdpBlockedPorts.Count == 0)
        {
            warnings.Add("KazoOs:Proxy:Udp:BlockedPorts is empty: the relay may open every UDP port an app declares or the allow-list names, amplification services included.");
        }

        return warnings;
    }

    private static IReadOnlyList<int> Ports(int[]? configured, IReadOnlyList<int> fallback) =>
        configured is { Length: > 0 }
            ? [.. configured.Where(static port => port is >= 1 and <= 65535).Distinct().Order()]
            : fallback;

    private static IReadOnlyList<int> BlockedPorts(int[]? configured, IReadOnlyList<int> fallback) =>
        configured is null
            ? fallback
            : [.. configured.Where(static port => port is >= 1 and <= 65535).Distinct().Order()];

    private static long Positive(long? value, long fallback) => value is > 0 ? value.Value : fallback;
}
