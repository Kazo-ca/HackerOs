using System.Net;

namespace HackerOs.AppSdk.Net;

/// <summary>
/// What this machine's TCP/IP stack is doing, the part of
/// <c>System.Net.NetworkInformation.IPGlobalProperties</c> Kazo OS has (ADR 0074, section 2).
/// Needs no capability: like <c>/proc/net/tcp</c>, the table of listening ports is world-readable;
/// only the owners of other users' sockets are hidden.
/// </summary>
public static class HackIPGlobalProperties
{
    /// <summary>
    /// Lists the sockets in the <c>LISTEN</c> state with their owner, as <c>netstat -tlnp</c> does;
    /// the owner of a socket is only named when the caller may see its process.
    /// </summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    public static async Task<IReadOnlyList<HackTcpListenerInfo>> GetActiveTcpListenersAsync(CancellationToken cancellationToken = default) =>
        await HackNetworkErrorTranslator.CallAsync(gateway => gateway.GetTcpListenersAsync(cancellationToken)).ConfigureAwait(false);

    /// <summary>Gets the local end points of the listening sockets, like <c>IPGlobalProperties.GetActiveTcpListeners</c>.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    public static async Task<IPEndPoint[]> GetActiveTcpListenerEndPointsAsync(CancellationToken cancellationToken = default) =>
        [.. (await GetActiveTcpListenersAsync(cancellationToken).ConfigureAwait(false))
            .Select(static listener => new IPEndPoint(listener.Address, listener.Port))
            .Distinct()];
}
