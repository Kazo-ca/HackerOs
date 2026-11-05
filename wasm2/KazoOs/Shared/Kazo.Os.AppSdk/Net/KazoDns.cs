using System.Net;

namespace Kazo.Os.AppSdk.Net;

/// <summary>An entry of the host table: a name and its addresses, like <see cref="IPHostEntry"/>.</summary>
/// <param name="HostName">Canonical host name.</param>
/// <param name="AddressList">Addresses of the host, never empty.</param>
/// <param name="Aliases">Other names of the same host.</param>
public sealed record KazoHostEntry(string HostName, IReadOnlyList<IPAddress> AddressList, IReadOnlyList<string> Aliases)
{
    /// <summary>
    /// Gets where the OS resolver (<c>kazodns</c>, ADR 0074 section 4) found the answer, as
    /// <c>nslookup</c> shows it; <see cref="KazoDnsSource.Unknown"/> for a reverse lookup.
    /// </summary>
    public KazoDnsSource Source { get; init; }

    /// <summary>
    /// Gets a detail of <see cref="Source"/>: the id of the web app that declares the name, the file
    /// and line of <c>/etc/hosts</c>, or <see langword="null"/>.
    /// </summary>
    public string? SourceDetail { get; init; }
}

/// <summary>
/// The sources of the OS resolver <c>kazodns</c>, in the order it asks them (ADR 0074, section 4).
/// </summary>
public enum KazoDnsSource
{
    /// <summary>Not recorded (reverse lookup, or a gateway without the resolver).</summary>
    Unknown,

    /// <summary>A line of <c>/etc/hosts</c>, which always wins.</summary>
    HostsFile,

    /// <summary><c>localhost</c> or an address of 127.0.0.0/8: this machine.</summary>
    Loopback,

    /// <summary>A URL declared by an installed web app (<c>web.urls</c> of its manifest).</summary>
    WebApp,

    /// <summary>A name bound by a hosted static site (ADR 0077).</summary>
    HostedSite,

    /// <summary>A host or site of the simulated network.</summary>
    SimulatedNetwork
}

/// <summary>
/// Name resolution, modeled on <see cref="Dns"/> (ADR 0056, section 6). Needs
/// <c>network.simulated.read</c>.
/// </summary>
/// <remarks>
/// Resolution asks the OS resolver <c>kazodns</c> (ADR 0074, section 4): <c>/etc/hosts</c>, then
/// <c>localhost</c>, the URLs of installed web apps, hosted sites and the simulated network; the
/// answer's <see cref="KazoHostEntry.Source"/> says which one answered. A name it does not know throws a
/// <see cref="System.Net.Sockets.SocketException"/> with <see cref="System.Net.Sockets.SocketError.HostNotFound"/>, as
/// <c>getaddrinfo</c> reports <c>EAI_NONAME</c>.
/// </remarks>
public static class KazoDns
{
    /// <summary>Gets the addresses of a host; an address literal is returned as is, without lookup.</summary>
    /// <exception cref="System.Net.Sockets.SocketException">The name is unknown.</exception>
    /// <exception cref="UnauthorizedAccessException">The app lacks <c>network.simulated.read</c>.</exception>
    public static async Task<IPAddress[]> GetHostAddressesAsync(string hostNameOrAddress, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostNameOrAddress);
        if (IPAddress.TryParse(hostNameOrAddress, out IPAddress? literal))
        {
            return [literal];
        }

        KazoHostEntry entry = await GetHostEntryAsync(hostNameOrAddress, cancellationToken).ConfigureAwait(false);
        return [.. entry.AddressList];
    }

    /// <summary>Resolves a host name, or reverse-resolves an address literal, to its entry.</summary>
    /// <exception cref="System.Net.Sockets.SocketException">The name or address is unknown.</exception>
    /// <exception cref="UnauthorizedAccessException">The app lacks <c>network.simulated.read</c>.</exception>
    public static async Task<KazoHostEntry> GetHostEntryAsync(string hostNameOrAddress, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostNameOrAddress);
        if (IPAddress.TryParse(hostNameOrAddress, out IPAddress? literal))
        {
            return await GetHostEntryAsync(literal, cancellationToken).ConfigureAwait(false);
        }

        KazoNetworkResult<KazoHostEntry> result = await KazoNetworkErrorTranslator
            .CallAsync(gateway => gateway.ResolveAsync(hostNameOrAddress, cancellationToken)).ConfigureAwait(false);
        return KazoNetworkErrorTranslator.UnwrapSocket(result);
    }

    /// <summary>Reverse-resolves an address to its entry.</summary>
    /// <exception cref="System.Net.Sockets.SocketException">No host has this address.</exception>
    /// <exception cref="UnauthorizedAccessException">The app lacks <c>network.simulated.read</c>.</exception>
    public static async Task<KazoHostEntry> GetHostEntryAsync(IPAddress address, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        KazoNetworkResult<KazoHostEntry> result = await KazoNetworkErrorTranslator
            .CallAsync(gateway => gateway.ReverseLookupAsync(address, cancellationToken)).ConfigureAwait(false);
        return KazoNetworkErrorTranslator.UnwrapSocket(result);
    }
}
