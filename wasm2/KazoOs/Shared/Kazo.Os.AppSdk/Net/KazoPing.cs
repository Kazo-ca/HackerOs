using System.Net;

namespace Kazo.Os.AppSdk.Net;

/// <summary>Status of an echo request, the subset of <c>System.Net.NetworkInformation.IPStatus</c> Kazo OS produces.</summary>
public enum KazoIPStatus
{
    /// <summary>An echo reply came back.</summary>
    Success,

    /// <summary>The host exists but did not answer.</summary>
    DestinationHostUnreachable,

    /// <summary>No reply before the timeout.</summary>
    TimedOut
}

/// <summary>One echo reply, modeled on <c>System.Net.NetworkInformation.PingReply</c>.</summary>
/// <param name="Address">
/// Address that answered; <see langword="null"/> when the reply came from the real network through
/// the optional server, which does not disclose it.
/// </param>
/// <param name="Status">Outcome of the request.</param>
/// <param name="RoundtripTime">
/// Round-trip time; <see cref="TimeSpan.Zero"/> unless <see cref="Status"/> is
/// <see cref="KazoIPStatus.Success"/>. A <see cref="TimeSpan"/> rather than .NET's whole
/// milliseconds, so simulated sub-millisecond latencies survive.
/// </param>
/// <param name="Ttl">Time-to-live of the reply packet.</param>
public sealed record KazoPingReply(IPAddress? Address, KazoIPStatus Status, TimeSpan RoundtripTime, int Ttl = 64);

/// <summary>
/// Thrown when an echo request cannot even be sent, typically because the name does not resolve,
/// like <c>System.Net.NetworkInformation.PingException</c>.
/// </summary>
public sealed class KazoPingException : InvalidOperationException
{
    /// <summary>Initializes the exception.</summary>
    public KazoPingException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Echo requests (ICMP), modeled on <c>System.Net.NetworkInformation.Ping</c> (ADR 0056, section 6).
/// Needs <c>network.simulated.read</c>.
/// </summary>
/// <remarks>
/// For a host outside the simulated network, an app holding <c>network.real.access</c> gets an
/// approximation through the optional server: a browser cannot send ICMP, so the OS times an HTTP
/// <c>HEAD</c> round trip instead.
/// </remarks>
public static class KazoPing
{
    /// <summary>Default timeout, as <c>Ping.Send</c>: 5 seconds.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Sends one echo request and waits for its reply.</summary>
    /// <exception cref="KazoPingException">The name does not resolve (inner <see cref="System.Net.Sockets.SocketException"/>).</exception>
    /// <exception cref="UnauthorizedAccessException">The app lacks <c>network.simulated.read</c>.</exception>
    public static async Task<KazoPingReply> SendAsync(
        string hostNameOrAddress, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostNameOrAddress);
        KazoNetworkResult<KazoPingReply> result = await KazoNetworkErrorTranslator
            .CallAsync(gateway => gateway.PingAsync(hostNameOrAddress, timeout ?? DefaultTimeout, cancellationToken))
            .ConfigureAwait(false);
        try
        {
            return KazoNetworkErrorTranslator.UnwrapSocket(result);
        }
        catch (System.Net.Sockets.SocketException socketException)
        {
            throw new KazoPingException($"An exception occurred during a Ping request to '{hostNameOrAddress}'.", socketException);
        }
    }

    /// <summary>Sends one echo request to an address.</summary>
    /// <exception cref="UnauthorizedAccessException">The app lacks <c>network.simulated.read</c>.</exception>
    public static Task<KazoPingReply> SendAsync(IPAddress address, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        return SendAsync(address.ToString(), timeout, cancellationToken);
    }
}
