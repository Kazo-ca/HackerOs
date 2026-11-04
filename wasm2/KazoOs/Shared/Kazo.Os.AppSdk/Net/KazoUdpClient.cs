using System.Net;
using System.Net.Sockets;

namespace Kazo.Os.AppSdk.Net;

/// <summary>A datagram received by <see cref="KazoUdpClient.ReceiveAsync"/>, as <see cref="UdpReceiveResult"/>.</summary>
/// <param name="Buffer">The datagram's payload.</param>
/// <param name="RemoteEndPoint">Who sent it: always the connected destination.</param>
public readonly record struct KazoUdpReceiveResult(byte[] Buffer, IPEndPoint RemoteEndPoint);

/// <summary>
/// A "connected" UDP socket to one destination, the part of <see cref="UdpClient"/> an app needs to
/// talk to a DNS or NTP server (ADR 0102 §3). Needs <c>network.simulated.write</c>.
/// </summary>
/// <remarks>
/// <para>
/// Each <see cref="SendAsync"/> is one datagram to the connected destination; there is no
/// broadcast, multicast or <c>SendTo</c> another address. The simulated network carries no UDP:
/// a simulated host refuses (<see cref="SocketError.ConnectionRefused"/>), and a host outside it is
/// reached through the optional server's relay when the app holds <c>network.real.access</c>, on
/// the ports the server's operator allows (53 and 123 by default).
/// </para>
/// <para>
/// As with a real UDP socket nothing guarantees an answer: wrap <see cref="ReceiveAsync"/> in a
/// timeout. An ICMP "port unreachable" from the destination closes the socket.
/// </para>
/// </remarks>
public sealed class KazoUdpClient : IDisposable, IAsyncDisposable
{
    private readonly IHackSocket _socket;
    private bool _disposed;

    private KazoUdpClient(IHackSocket socket) => _socket = socket;

    /// <summary>Gets the connected destination.</summary>
    public IPEndPoint RemoteEndPoint => _socket.RemoteEndPoint;

    /// <summary>
    /// Opens a UDP socket connected to <paramref name="hostNameOrAddress"/>:<paramref name="port"/>,
    /// as <c>new UdpClient()</c> then <c>Connect(host, port)</c> do. No packet is sent yet.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="port"/> is not within 1–65535.</exception>
    /// <exception cref="SocketException">The host is unknown, refused, or the port is not allowed by the relay.</exception>
    /// <exception cref="UnauthorizedAccessException">The app lacks <c>network.simulated.write</c>.</exception>
    public static async Task<KazoUdpClient> ConnectAsync(
        string hostNameOrAddress, int port, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostNameOrAddress);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);

        KazoNetworkResult<IHackSocket> result = await KazoNetworkErrorTranslator
            .CallAsync(gateway => gateway.ConnectUdpAsync(hostNameOrAddress, port, cancellationToken))
            .ConfigureAwait(false);
        return new KazoUdpClient(KazoNetworkErrorTranslator.UnwrapSocket(result));
    }

    /// <summary>Sends one datagram and returns its length, as <see cref="UdpClient.SendAsync(ReadOnlyMemory{byte}, CancellationToken)"/>.</summary>
    /// <exception cref="ArgumentException">The datagram exceeds 65 507 bytes.</exception>
    /// <exception cref="SocketException">The socket is closed.</exception>
    public async ValueTask<int> SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (datagram.Length > 65_507)
        {
            throw new ArgumentException("A UDP datagram carries at most 65 507 bytes.", nameof(datagram));
        }

        await _socket.SendAsync(datagram, cancellationToken).ConfigureAwait(false);
        return datagram.Length;
    }

    /// <summary>Waits for the next datagram, as <see cref="UdpClient.ReceiveAsync(CancellationToken)"/>.</summary>
    /// <exception cref="SocketException">The socket was closed (port unreachable, idle timeout).</exception>
    public async ValueTask<KazoUdpReceiveResult> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[]? datagram = await _socket.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        return datagram is null
            ? throw new SocketException((int)SocketError.ConnectionReset)
            : new KazoUdpReceiveResult(datagram, _socket.RemoteEndPoint);
    }

    /// <summary>Closes the socket.</summary>
    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            await _socket.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Closes the socket without waiting for the close to complete.</summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _ = _socket.DisposeAsync().AsTask();
        }
    }
}
