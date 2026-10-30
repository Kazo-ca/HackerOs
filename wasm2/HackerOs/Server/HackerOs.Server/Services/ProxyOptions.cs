namespace HackerOs.Server.Services;

/// <summary>
/// The <c>HackerOs:Proxy</c> section of <c>appsettings.json</c> (ADR 0102 §5), as bound by
/// <c>IOptions</c>. Every list is <see langword="null"/> when the file does not set it, and
/// <see cref="ProxyRelayPolicy.From"/> fills the defaults: the configuration binder appends to a
/// list initialized in code instead of replacing it, so defaults cannot live in the initializers.
/// </summary>
public sealed class ProxyOptions
{
    /// <summary>The configuration section this class binds.</summary>
    public const string SectionName = "HackerOs:Proxy";

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
}

/// <summary>Port filter of one relayed protocol.</summary>
public sealed class ProxyPortOptions
{
    /// <summary>Allowed target ports; <see langword="null"/> or empty keeps the protocol's default.</summary>
    public int[]? AllowedPorts { get; set; }
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
    IReadOnlyList<string> GrantedApps)
{
    /// <summary>Default HTTP ports.</summary>
    public static readonly IReadOnlyList<int> DefaultHttpPorts = [80, 443];

    /// <summary>Default TCP stream ports.</summary>
    public static readonly IReadOnlyList<int> DefaultTcpPorts = [80, 443];

    /// <summary>Default UDP ports: DNS and NTP.</summary>
    public static readonly IReadOnlyList<int> DefaultUdpPorts = [53, 123];

    /// <summary>
    /// The system apps that declare <c>network.real.access</c> (curl, wget, ping, cat, nmap and the
    /// Browser, ADR 0210 §1): granted by default, as the OS grants them through its system policy.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultGrantedApps =
    [
        "org.hackeros.commands.curl",
        "org.hackeros.commands.wget",
        "org.hackeros.commands.ping",
        "org.hackeros.cmd.cat",
        "org.hackeros.commands.nmap",
        "org.hackeros.browser"
    ];

    /// <summary>The default policy, as an absent <c>HackerOs:Proxy</c> section gives.</summary>
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
                : DefaultGrantedApps);
    }

    /// <summary>Whether <paramref name="port"/> passes the filter of <paramref name="allowed"/>.</summary>
    /// <param name="allowed">The protocol's list.</param>
    /// <param name="port">Target port.</param>
    public bool IsPortAllowed(IReadOnlyList<int> allowed, int port) =>
        port is >= 1 and <= 65535 && (AllowAllPorts || allowed.Contains(port));

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
            warnings.Add("HackerOs:Proxy:AllowAllPorts is on: the relay reaches every TCP and UDP port.");
        }

        if (AllowPrivateNetworks)
        {
            warnings.Add("HackerOs:Proxy:AllowPrivateNetworks is on: the relay reaches the private networks of the server's machine (loopback and cloud metadata stay blocked).");
        }

        if (!QuotasEnabled)
        {
            warnings.Add("HackerOs:Proxy:Quotas:Enabled is off: no size, concurrency, stream or idle limit applies.");
        }

        return warnings;
    }

    private static IReadOnlyList<int> Ports(int[]? configured, IReadOnlyList<int> fallback) =>
        configured is { Length: > 0 }
            ? [.. configured.Where(static port => port is >= 1 and <= 65535).Distinct().Order()]
            : fallback;

    private static long Positive(long? value, long fallback) => value is > 0 ? value.Value : fallback;
}
