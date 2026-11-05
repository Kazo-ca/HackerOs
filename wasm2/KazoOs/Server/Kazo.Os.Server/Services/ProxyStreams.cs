using System.Net;
using System.Net.Sockets;
using Kazo.Os.Server.Contracts.Proxy;

namespace Kazo.Os.Server.Services;

/// <summary>
/// Opens the socket of a relayed stream against an already validated address, through an
/// injectable, testable boundary (ADR 0102 §2, §3).
/// </summary>
public interface IProxyStreamConnector
{
    /// <summary>
    /// Connects a TCP socket (three-way handshake) or a "connected" UDP socket (no packet sent)
    /// to <paramref name="address"/>:<paramref name="port"/>.
    /// </summary>
    /// <exception cref="ProxyRequestException">
    /// <see cref="ProxyErrorCode.ConnectionRefused"/> on RST, <see cref="ProxyErrorCode.HostUnreachable"/>
    /// when nothing answers in time.
    /// </exception>
    Task<Socket> ConnectAsync(ProxyProtocol protocol, IPAddress address, int port, CancellationToken cancellationToken);
}

/// <summary>Uses real OS sockets.</summary>
public sealed class SocketProxyStreamConnector : IProxyStreamConnector
{
    /// <summary>How long a TCP handshake may take, as <c>SocketsHttpHandler.ConnectTimeout</c> for HTTP.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    public async Task<Socket> ConnectAsync(ProxyProtocol protocol, IPAddress address, int port, CancellationToken cancellationToken)
    {
        Socket socket = protocol == ProxyProtocol.Udp
            ? new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp)
            : new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            await socket.ConnectAsync(address, port, timeout.Token).ConfigureAwait(false);
            return socket;
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.ConnectionRefused)
        {
            socket.Dispose();
            throw new ProxyRequestException(ProxyErrorCode.ConnectionRefused, $"{address}:{port} refused the connection.");
        }
        catch (SocketException exception)
        {
            socket.Dispose();
            throw new ProxyRequestException(ProxyErrorCode.HostUnreachable, $"{address}:{port} is unreachable ({exception.SocketErrorCode}).");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            throw new ProxyRequestException(ProxyErrorCode.HostUnreachable, $"{address}:{port} did not answer within {ConnectTimeout.TotalSeconds:0} s.");
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

/// <summary>
/// A relayed stream backed by the server's own socket: what the WebSocket endpoint pumps, and
/// what an OS hosted in this process (ADR 0036) uses directly.
/// </summary>
/// <remarks>
/// The stream closes itself after <c>StreamIdleTimeoutSeconds</c> without a byte in either
/// direction, like a NAT forgetting an idle mapping. The close callback runs exactly once,
/// whatever closed it, with the byte counts for the audit log and to release the device's slot.
/// </remarks>
public sealed class SocketProxyStreamChannel : IProxyStreamChannel
{
    private readonly Socket socket;
    private readonly TimeSpan idleTimeout;
    private readonly Action<ProxyStreamClosure, long, long> onClosed;
    private readonly CancellationTokenSource _idle;
    private ProxyStreamClosure? _closure;
    private long _sent;
    private long _received;
    private int _closed;

    /// <summary>Wraps a connected socket and arms the idle timer.</summary>
    /// <param name="socket">The connected socket; owned by the channel.</param>
    /// <param name="protocol">TCP or UDP.</param>
    /// <param name="remote">The validated address and port.</param>
    /// <param name="idleTimeout">Idle timeout, or <see cref="Timeout.InfiniteTimeSpan"/>.</param>
    /// <param name="onClosed">Called once on close with (closure, bytes sent to the target, bytes received from it).</param>
    public SocketProxyStreamChannel(
        Socket socket,
        ProxyProtocol protocol,
        ProxyStreamOpened remote,
        TimeSpan idleTimeout,
        Action<ProxyStreamClosure, long, long> onClosed)
    {
        this.socket = socket ?? throw new ArgumentNullException(nameof(socket));
        this.idleTimeout = idleTimeout;
        this.onClosed = onClosed ?? throw new ArgumentNullException(nameof(onClosed));
        Protocol = protocol;
        Remote = remote;
        _idle = Arm(idleTimeout);
        // The timer closes the socket itself, so an idle stream nobody reads from is reclaimed too.
        _idle.Token.Register(() => Close(ProxyStreamCloseCodes.IdleTimeout, $"idle for {idleTimeout.TotalSeconds:0} s"));
    }

    /// <inheritdoc />
    public ProxyProtocol Protocol { get; }

    /// <inheritdoc />
    public ProxyStreamOpened Remote { get; }

    /// <inheritdoc />
    public ProxyStreamClosure? Closure => Volatile.Read(ref _closure);

    /// <inheritdoc />
    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (Closure is not null)
        {
            throw new InvalidOperationException("The stream is closed.");
        }

        if (Protocol == ProxyProtocol.Udp && data.Length > ProxyStreamRoutes.MaxDatagramBytes)
        {
            throw new ArgumentException($"A UDP datagram carries at most {ProxyStreamRoutes.MaxDatagramBytes} bytes.", nameof(data));
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _idle.Token);
        try
        {
            // TCP: a send may be partial, as send(2); loop until the kernel took every byte.
            int offset = 0;
            do
            {
                offset += await socket.SendAsync(data[offset..], SocketFlags.None, linked.Token).ConfigureAwait(false);
            }
            while (Protocol == ProxyProtocol.Tcp && offset < data.Length);

            Interlocked.Add(ref _sent, data.Length);
            Touch();
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            Close(ProxyStreamCloseCodes.RemoteClosed, exception is SocketException socketError
                ? $"send failed: {socketError.SocketErrorCode}"
                : "the stream was closed");
            throw new InvalidOperationException("The stream is closed.", exception);
        }
    }

    /// <inheritdoc />
    public async ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        if (Closure is not null)
        {
            return null;
        }

        byte[] buffer = new byte[Protocol == ProxyProtocol.Udp ? 65_535 : ProxyStreamRoutes.MaxChunkBytes];
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _idle.Token);
        try
        {
            int read = await socket.ReceiveAsync(buffer, SocketFlags.None, linked.Token).ConfigureAwait(false);
            if (read == 0 && Protocol == ProxyProtocol.Tcp)
            {
                Close(ProxyStreamCloseCodes.RemoteClosed, "closed by the remote host");
                return null;
            }

            Interlocked.Add(ref _received, read);
            Touch();
            return buffer[..read];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Only the idle timer cancels without the caller asking.
            Close(ProxyStreamCloseCodes.IdleTimeout, $"idle for {idleTimeout.TotalSeconds:0} s");
            return null;
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
        {
            // An ICMP port unreachable answering a UDP datagram surfaces here as ConnectionRefused.
            Close(ProxyStreamCloseCodes.RemoteClosed, exception is SocketException socketError
                ? socketError.SocketErrorCode == SocketError.ConnectionRefused ? "port unreachable" : $"receive failed: {socketError.SocketErrorCode}"
                : "the stream was closed");
            return null;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Close(1000, "closed by the client");
        _idle.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Closes the socket once, with the given reason, and reports it.</summary>
    /// <param name="code">One of <see cref="ProxyStreamCloseCodes"/>, or 1000 for the client.</param>
    /// <param name="reason">Free text.</param>
    public void Close(int code, string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref _closure, new ProxyStreamClosure(code, reason));
        try
        {
            if (Protocol == ProxyProtocol.Tcp && socket.Connected)
            {
                socket.Shutdown(SocketShutdown.Both);
            }
        }
        catch (SocketException)
        {
            // Already reset by the peer: nothing left to shut down.
        }

        socket.Dispose();
        onClosed(_closure!, Interlocked.Read(ref _sent), Interlocked.Read(ref _received));
    }

    private void Touch()
    {
        if (idleTimeout != Timeout.InfiniteTimeSpan && Volatile.Read(ref _closed) == 0 && !_idle.IsCancellationRequested)
        {
            try
            {
                _idle.CancelAfter(idleTimeout);
            }
            catch (ObjectDisposedException)
            {
                // Closed concurrently.
            }
        }
    }

    private static CancellationTokenSource Arm(TimeSpan idleTimeout)
    {
        CancellationTokenSource idle = new();
        if (idleTimeout != Timeout.InfiniteTimeSpan)
        {
            idle.CancelAfter(idleTimeout);
        }

        return idle;
    }
}
