using System.Net.WebSockets;
using System.Text.Json;
using Kazo.Os.Server.Contracts.Proxy;

namespace Kazo.Os.Platform.Core.ServerConnection;

/// <summary>
/// The client side of a relayed TCP or UDP stream (ADR 0102 §2, §3): a WebSocket to the server,
/// whose first text frame says the socket is connected and whose binary frames are the data.
/// </summary>
internal sealed class WebSocketProxyStreamChannel : IProxyStreamChannel
{
    private readonly WebSocket _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ProxyStreamClosure? _closure;

    private WebSocketProxyStreamChannel(WebSocket socket, ProxyProtocol protocol, ProxyStreamOpened remote)
    {
        _socket = socket;
        Protocol = protocol;
        Remote = remote;
    }

    /// <inheritdoc />
    public ProxyProtocol Protocol { get; }

    /// <inheritdoc />
    public ProxyStreamOpened Remote { get; }

    /// <inheritdoc />
    public ProxyStreamClosure? Closure => Volatile.Read(ref _closure);

    /// <summary>Waits for the server's verdict: the "opened" frame, or a close carrying the refusal.</summary>
    /// <exception cref="ProxyStreamException">The server refused the stream.</exception>
    public static async Task<IProxyStreamChannel> OpenAsync(WebSocket socket, ProxyProtocol protocol, CancellationToken cancellationToken)
    {
        (WebSocketMessageType type, byte[]? message) = await ReadMessageAsync(socket, ProxyStreamRoutes.MaxChunkBytes, cancellationToken).ConfigureAwait(false);
        if (message is null || type != WebSocketMessageType.Text)
        {
            throw socket.CloseStatus is { } status && (int)status == ProxyStreamCloseCodes.Rejected
                ? ProxyStreamException.FromCloseReason(socket.CloseStatusDescription)
                : new ProxyStreamException(ProxyErrorCode.MalformedRequest, $"The relay closed the stream before opening it ({socket.CloseStatusDescription}).");
        }

        ProxyStreamOpened? opened = JsonSerializer.Deserialize(message, ProxyContractsJsonContext.Default.ProxyStreamOpened);
        return opened is null
            ? throw new ProxyStreamException(ProxyErrorCode.MalformedRequest, "The relay sent an unreadable opening frame.")
            : new WebSocketProxyStreamChannel(socket, protocol, opened);
    }

    /// <inheritdoc />
    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (Closure is not null || _socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("The stream is closed.");
        }

        if (Protocol == ProxyProtocol.Udp && data.Length > ProxyStreamRoutes.MaxDatagramBytes)
        {
            throw new ArgumentException($"A UDP datagram carries at most {ProxyStreamRoutes.MaxDatagramBytes} bytes.", nameof(data));
        }

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Protocol == ProxyProtocol.Udp)
            {
                // One message is one datagram, an empty one included.
                await _socket.SendAsync(data, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // A TCP chunk larger than one relay message is split; the byte stream stays the same.
                for (int offset = 0; offset < data.Length; offset += ProxyStreamRoutes.MaxChunkBytes)
                {
                    ReadOnlyMemory<byte> part = data.Slice(offset, Math.Min(ProxyStreamRoutes.MaxChunkBytes, data.Length - offset));
                    await _socket.SendAsync(part, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (WebSocketException exception)
        {
            Volatile.Write(ref _closure, new ProxyStreamClosure(ProxyStreamCloseCodes.RemoteClosed, exception.Message));
            throw new InvalidOperationException("The stream is closed.", exception);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        while (Closure is null)
        {
            (WebSocketMessageType type, byte[]? message) = await ReadMessageAsync(
                _socket, Protocol == ProxyProtocol.Udp ? 65_535 : ProxyStreamRoutes.MaxChunkBytes, cancellationToken).ConfigureAwait(false);
            if (message is null)
            {
                Volatile.Write(ref _closure, new ProxyStreamClosure(
                    _socket.CloseStatus is { } status ? (int)status : ProxyStreamCloseCodes.RemoteClosed,
                    _socket.CloseStatusDescription));
                return null;
            }

            if (type == WebSocketMessageType.Binary)
            {
                return message;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Volatile.Write(ref _closure, _closure ?? new ProxyStreamClosure((int)WebSocketCloseStatus.NormalClosure, "closed by the client"));
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            try
            {
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
            {
                // The server is already gone.
            }
        }

        _socket.Dispose();
        _sendLock.Dispose();
    }

    /// <summary>Reads one whole message; <see langword="null"/> when the socket closed instead.</summary>
    private static async Task<(WebSocketMessageType Type, byte[]? Message)> ReadMessageAsync(
        WebSocket socket, int limit, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[limit];
        int length = 0;
        try
        {
            while (true)
            {
                if (length == buffer.Length)
                {
                    throw new ProxyStreamException(ProxyErrorCode.MalformedRequest, "The relay sent a message larger than the protocol allows.");
                }

                ValueWebSocketReceiveResult result = await socket.ReceiveAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return (WebSocketMessageType.Close, null);
                }

                length += result.Count;
                if (result.EndOfMessage)
                {
                    return (result.MessageType, buffer[..length]);
                }
            }
        }
        catch (WebSocketException)
        {
            // The connection dropped without a close frame: the stream is gone all the same.
            return (WebSocketMessageType.Close, null);
        }
    }
}
