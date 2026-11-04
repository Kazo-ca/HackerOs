using System.Net;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Net;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Network;
using Kazo.Os.Simulation.Abstractions.Settings;
using Kazo.Os.Simulation.Abstractions.Time;

namespace Kazo.Os.Platform.Core.Network.Local;

/// <summary>One answer of <see cref="KazoDnsResolver"/>.</summary>
/// <param name="CanonicalName">Canonical name of the host.</param>
/// <param name="Addresses">Its IPv4 addresses, never empty.</param>
/// <param name="Aliases">Other names of the same host.</param>
/// <param name="Source">Which source answered (ADR 0074, section 4).</param>
/// <param name="Detail">The file and line, or the app id, that answered; <see langword="null"/> otherwise.</param>
public sealed record DnsAnswer(
    string CanonicalName,
    IReadOnlyList<IPAddress> Addresses,
    IReadOnlyList<string> Aliases,
    KazoDnsSource Source,
    string? Detail)
{
    /// <summary>Gets whether the answer points at this machine (127.0.0.0/8).</summary>
    public bool IsLocal => Addresses.Any(IPAddress.IsLoopback);
}

/// <summary>
/// Names bound by hosted static sites (ADR 0077, <c>hackwebd</c>): a binding such as
/// <c>http://docs.hack/</c> makes <c>docs.hack</c> resolve to this machine. Implemented by
/// <see cref="HostedSiteNameSource"/>, which reads the <c>.site</c> files (ADR 0080, section 4); the
/// resolver works without one.
/// </summary>
public interface IHostedSiteNameSource
{
    /// <summary>Gets the name of the hosted site that binds <paramref name="hostName"/>, or <see langword="null"/>.</summary>
    /// <param name="hostName">A lower-case host name.</param>
    /// <param name="cancellationToken">Cancels the read of the site files.</param>
    ValueTask<string?> FindSiteAsync(string hostName, CancellationToken cancellationToken = default);
}

/// <summary>
/// <c>hackdns</c>, the resolver of the OS (ADR 0074, section 4). It answers <c>KazoDns</c>, the
/// browser and every HTTP call of <see cref="AppNetworkGateway"/>, asking in this order:
/// </summary>
/// <remarks>
/// <list type="number">
/// <item><c>/etc/hosts</c>, a real file the administrator edits, which always wins;</item>
/// <item><c>localhost</c>, <c>*.localhost</c> and 127.0.0.0/8: this machine;</item>
/// <item>the URLs of installed, enabled web apps (<c>web.urls</c>): this machine;</item>
/// <item>the names bound by hosted static sites (<see cref="IHostedSiteNameSource"/>): this machine;</item>
/// <item>the simulated network's zone.</item>
/// </list>
/// <para>
/// The sixth source of the ADR, the optional server's relay, has no DNS: the gateway sends an HTTP
/// request for an unresolved name there itself, never for the internal <c>.hack</c> domain.
/// </para>
/// <para>
/// Answers from sources 2 to 5 are cached for <see cref="CacheTimeToLive"/> (simulated time), like
/// <c>systemd-resolved</c>; the cache is emptied when <c>/etc/hosts</c> or the set of web URLs
/// changes. <c>/etc/hosts</c> is read again whenever its revision changes.
/// </para>
/// </remarks>
public sealed class KazoDnsResolver
{
    /// <summary>The file read first.</summary>
    public static readonly VirtualPath HostsFilePath = VirtualPath.Parse("/etc/hosts");

    /// <summary>Content of a freshly seeded <c>/etc/hosts</c>.</summary>
    public const string DefaultHostsFileContent =
        "# /etc/hosts: static table of host names, read first by hackdns (ADR 0074).\n" +
        "# <address> <canonical name> [aliases...]; edit as root, the change applies at once.\n" +
        "127.0.0.1\tlocalhost\n" +
        "127.0.1.1\thackeros\n";

    private readonly ISimulatedNetworkService _network;
    private readonly IFileSystemService? _fileSystem;
    private readonly ISimulationClock _clock;
    private readonly Func<IEnumerable<AppManifest>> _webApps;
    private readonly IHostedSiteNameSource? _hostedSites;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (DnsAnswer Answer, DateTimeOffset Expires)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _hostsGate = new(1, 1);
    private HostsFile _hosts = HostsFile.Empty;
    private long? _hostsRevision;
    private bool _hostsLoaded;
    private string _webZoneSignature = string.Empty;

    /// <summary>Initializes the resolver.</summary>
    /// <param name="network">The simulated network (source 5).</param>
    /// <param name="fileSystem">The file system <c>/etc/hosts</c> is read from as root; <see langword="null"/> skips source 1.</param>
    /// <param name="clock">Simulated clock of the cache.</param>
    /// <param name="webApps">The installed, enabled apps (source 3 reads their <c>web.urls</c>).</param>
    /// <param name="hostedSites">Names of hosted static sites (source 4), when <c>hackwebd</c> exists.</param>
    public KazoDnsResolver(
        ISimulatedNetworkService network,
        IFileSystemService? fileSystem,
        ISimulationClock clock,
        Func<IEnumerable<AppManifest>> webApps,
        IHostedSiteNameSource? hostedSites = null)
    {
        _network = network ?? throw new ArgumentNullException(nameof(network));
        _fileSystem = fileSystem;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _webApps = webApps ?? throw new ArgumentNullException(nameof(webApps));
        _hostedSites = hostedSites;
    }

    /// <summary>Gets how long an answer stays in the cache (simulated time).</summary>
    public TimeSpan CacheTimeToLive { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Resolves a host name; an IPv4 literal is answered as is.</summary>
    /// <param name="hostName">The name.</param>
    /// <param name="cancellationToken">Cancels the read of <c>/etc/hosts</c>.</param>
    /// <returns>The answer, or <see langword="null"/> when no source knows the name.</returns>
    public async ValueTask<DnsAnswer?> ResolveAsync(string hostName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostName);
        string name = hostName.Trim().TrimEnd('.').ToLowerInvariant();
        if (IPAddress.TryParse(name, out IPAddress? literal))
        {
            return new DnsAnswer(name, [literal], [], IPAddress.IsLoopback(literal) ? KazoDnsSource.Loopback : KazoDnsSource.Unknown, null);
        }

        HostsFile hosts = await GetHostsAsync(cancellationToken).ConfigureAwait(false);
        if (hosts.Find(name) is { } line)
        {
            return new DnsAnswer(
                line.Names[0], [line.Address], [.. line.Names.Skip(1)], KazoDnsSource.HostsFile, $"{HostsFilePath.Value}:{line.LineNumber}");
        }

        IReadOnlyList<AppManifest> webApps = [.. _webApps().Where(static app => app.Web is not null)];
        lock (_gate)
        {
            string signature = string.Join('\n', webApps.SelectMany(static app => app.Web!.Urls.Select(url => app.Id + " " + url)));
            if (signature != _webZoneSignature)
            {
                _webZoneSignature = signature;
                _cache.Clear();
            }

            if (_cache.TryGetValue(name, out var cached))
            {
                if (cached.Expires > _clock.UtcNow)
                {
                    return cached.Answer;
                }

                _cache.Remove(name);
            }
        }

        DnsAnswer? answer = await ResolveUncachedAsync(name, webApps, cancellationToken).ConfigureAwait(false);
        if (answer is not null)
        {
            lock (_gate)
            {
                _cache[name] = (answer, _clock.UtcNow + CacheTimeToLive);
            }
        }

        return answer;
    }

    /// <summary>Finds the name of an address: <c>/etc/hosts</c> first, then the simulated zone.</summary>
    /// <param name="address">The address.</param>
    /// <param name="cancellationToken">Cancels the read of <c>/etc/hosts</c>.</param>
    /// <returns>The canonical name, or <see langword="null"/>.</returns>
    public async ValueTask<string?> ReverseLookupAsync(IPAddress address, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        HostsFile hosts = await GetHostsAsync(cancellationToken).ConfigureAwait(false);
        return hosts.FindAddress(address)?.Names[0]
            ?? _network.Dns.ReverseLookup(address.ToString())
            ?? (IPAddress.IsLoopback(address) ? "localhost" : null);
    }

    /// <summary>Finds the web app whose <c>web.urls</c> declare <paramref name="hostName"/>, if any.</summary>
    /// <param name="hostName">A host name.</param>
    public AppManifest? FindWebApp(string hostName) =>
        _webApps().FirstOrDefault(app => app.Web is not null && DeclaresHost(app, hostName));

    /// <summary>Forgets every cached answer (<c>resolvectl flush-caches</c>).</summary>
    public void FlushCache()
    {
        lock (_gate)
        {
            _cache.Clear();
        }
    }

    private async ValueTask<DnsAnswer?> ResolveUncachedAsync(string name, IReadOnlyList<AppManifest> webApps, CancellationToken cancellationToken)
    {
        if (WebAppUrls.IsLoopbackName(name))
        {
            return new DnsAnswer(name, [IPAddress.Loopback], [], KazoDnsSource.Loopback, null);
        }

        if (webApps.FirstOrDefault(app => DeclaresHost(app, name)) is { } webApp)
        {
            return new DnsAnswer(name, [IPAddress.Loopback], [], KazoDnsSource.WebApp, webApp.Id);
        }

        if (_hostedSites is not null && await _hostedSites.FindSiteAsync(name, cancellationToken).ConfigureAwait(false) is { } site)
        {
            return new DnsAnswer(name, [IPAddress.Loopback], [], KazoDnsSource.HostedSite, site);
        }

        if (_network.Dns.Resolve(name) is { } ip && IPAddress.TryParse(ip, out IPAddress? address))
        {
            string canonical = _network.Dns.ReverseLookup(ip) ?? name;
            string[] aliases =
            [
                .. _network.Dns.AllRecords
                    .Where(record => record.Ip == ip && !string.Equals(record.Hostname, canonical, StringComparison.OrdinalIgnoreCase))
                    .Select(static record => record.Hostname)
            ];
            return new DnsAnswer(canonical, [address], aliases, KazoDnsSource.SimulatedNetwork, null);
        }

        return null;
    }

    private static bool DeclaresHost(AppManifest app, string hostName) =>
        app.Web!.Urls.Any(url =>
            WebAppUrls.TryParse(url, allowWildcardHost: false, out WebBinding binding, out _)
            && string.Equals(binding.Host, hostName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns the parsed <c>/etc/hosts</c>, reading it again when its revision changed.</summary>
    private async ValueTask<HostsFile> GetHostsAsync(CancellationToken cancellationToken)
    {
        if (_fileSystem is null)
        {
            return HostsFile.Empty;
        }

        await _hostsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IAppFileSystemGateway root = new AppFileSystemGateway(_fileSystem, _clock, RootOperation(), ["system"]);
            FileSystemResult<FileSystemEntrySnapshot> stat = await root
                .StatAsync(new FileSystemStatRequest(HostsFilePath, FileSystemLinkBehavior.Follow), cancellationToken)
                .ConfigureAwait(false);
            long? revision = stat.Succeeded ? stat.Value!.Metadata.Revision : null;
            if (_hostsLoaded && revision == _hostsRevision)
            {
                return _hosts;
            }

            string? content = null;
            if (stat.Succeeded)
            {
                SettingsFileReadResult read = await SettingsTextFile.ReadAsync(root, HostsFilePath, cancellationToken).ConfigureAwait(false);
                content = read.Content;
            }

            _hosts = HostsFile.Parse(content);
            _hostsRevision = revision;
            _hostsLoaded = true;
            FlushCache();
            return _hosts;
        }
        finally
        {
            _hostsGate.Release();
        }
    }

    /// <summary>Reads as the resolver daemon, root, like <c>systemd-resolved</c> reading <c>/etc/hosts</c>.</summary>
    internal static AppOperationContext RootOperation() => new()
    {
        AppId = "org.hackeros.kernel",
        UserId = "system",
        UserAuthority = AppAuthority.System,
        GrantedCapabilities = new HashSet<string>(AppCapabilities.All, StringComparer.Ordinal),
        IsSystemOperation = true
    };
}

/// <summary>One line of <c>/etc/hosts</c>.</summary>
/// <param name="Address">The address.</param>
/// <param name="Names">Canonical name first, then aliases, lower case.</param>
/// <param name="LineNumber">1-based line number, for <c>nslookup</c>.</param>
public sealed record HostsFileLine(IPAddress Address, IReadOnlyList<string> Names, int LineNumber);

/// <summary>A parsed <c>/etc/hosts</c> (<c>hosts(5)</c>): <c>address name [aliases...]</c>, <c>#</c> comments.</summary>
public sealed class HostsFile
{
    private HostsFile(IReadOnlyList<HostsFileLine> lines) => Lines = lines;

    /// <summary>Gets a file with no line.</summary>
    public static HostsFile Empty { get; } = new([]);

    /// <summary>Gets the valid lines, in file order.</summary>
    public IReadOnlyList<HostsFileLine> Lines { get; }

    /// <summary>Parses the content; invalid lines (bad address, no name) are skipped, as the C library does.</summary>
    /// <param name="content">The file content, or <see langword="null"/> when the file is missing.</param>
    public static HostsFile Parse(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return Empty;
        }

        List<HostsFileLine> lines = [];
        string[] raw = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int index = 0; index < raw.Length; index++)
        {
            string line = raw[index];
            int comment = line.IndexOf('#', StringComparison.Ordinal);
            if (comment >= 0)
            {
                line = line[..comment];
            }

            string[] fields = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2 || !IPAddress.TryParse(fields[0], out IPAddress? address)
                || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                // IPv6 lines are ignored: the simulated network is IPv4 only.
                continue;
            }

            lines.Add(new HostsFileLine(address, [.. fields.Skip(1).Select(static name => name.ToLowerInvariant())], index + 1));
        }

        return new HostsFile(lines);
    }

    /// <summary>Gets the first line naming <paramref name="hostName"/>, as <c>gethostbyname</c> stops at the first match.</summary>
    public HostsFileLine? Find(string hostName) =>
        Lines.FirstOrDefault(line => line.Names.Contains(hostName, StringComparer.OrdinalIgnoreCase));

    /// <summary>Gets the first line of <paramref name="address"/>.</summary>
    public HostsFileLine? FindAddress(IPAddress address) =>
        Lines.FirstOrDefault(line => line.Address.Equals(address));
}
