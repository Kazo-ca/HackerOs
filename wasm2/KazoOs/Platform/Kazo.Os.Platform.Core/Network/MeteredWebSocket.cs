using System.Net.WebSockets;
using System.Text;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Platform.Core.Network;

/// <summary>
/// A browser WebSocket an app opened to a real server (ADR 0235 §1). Every frame is counted on
/// <c>eth0</c> and charged to the app's process (ADR 0057), as one TCP segment carrying the frame
/// header and payload of RFC 6455 §5.2, and the socket's buffers stay charged until it is closed
/// (ADR 0068).
/// </summary>
internal sealed class MeteredWebSocket : WebSocket
{
    // RFC 6455 §5.2: 2 bytes, plus 2 or 8 for a long length; the client masks its frames (4 bytes).
    private const int BaseFrameHeader = 2;
    private const int MaskBytes = 4;

    private readonly WebSocket _inner;
    private readonly NetworkTrafficMeter _meter;
    private readonly KernelMemoryCharge _buffers;
    private long _inboundMessageBytes;
    private int _released;

    /// <summary>Wraps an open WebSocket.</summary>
    /// <param name="inner">The browser's socket, already connected.</param>
    /// <param name="meter">The app's traffic meter.</param>
    /// <param name="buffers">The socket-buffer charge, released on close.</param>
    public MeteredWebSocket(WebSocket inner, NetworkTrafficMeter meter, KernelMemoryCharge buffers)
    {
        _inner = inner;
        _meter = meter;
        _buffers = buffers;
    }

    /// <inheritdoc />
    public override WebSocketState State => _inner.State;

    /// <inheritdoc />
    public override string? SubProtocol => _inner.SubProtocol;

    /// <inheritdoc />
    public override WebSocketCloseStatus? CloseStatus => _inner.CloseStatus;

    /// <inheritdoc />
    public override string? CloseStatusDescription => _inner.CloseStatusDescription;

    /// <summary>
    /// Counts the opening handshake (ADR 0235 §1): the TCP SYN and SYN-ACK, then the HTTP
    /// <c>Upgrade</c> request a browser sends and the <c>101 Switching Protocols</c> answer.
    /// </summary>
    /// <param name="meter">The app's traffic meter.</param>
    /// <param name="uri">The address connected to.</param>
    /// <param name="offered">Sub-protocols offered.</param>
    /// <param name="accepted">Sub-protocol the server chose, if any.</param>
    public static void CountHandshake(NetworkTrafficMeter meter, Uri uri, IReadOnlyList<string> offered, string? accepted)
    {
        meter.Segment(0, outbound: true, udp: false);
        meter.Segment(0, outbound: false, udp: false);

        StringBuilder request = new();
        request.Append("GET ").Append(uri.PathAndQuery).Append(" HTTP/1.1\r\n")
            .Append("Host: ").Append(uri.Authority).Append("\r\n")
            .Append("Upgrade: websocket\r\nConnection: Upgrade\r\n")
            .Append("Sec-WebSocket-Key: ").Append('x', 24).Append("\r\n")
            .Append("Sec-WebSocket-Version: 13\r\n");
        if (offered.Count > 0)
        {
            request.Append("Sec-WebSocket-Protocol: ").AppendJoin(", ", offered).Append("\r\n");
        }

        request.Append("\r\n");
        meter.Segment(Encoding.ASCII.GetByteCount(request.ToString()), outbound: true, udp: false);

        StringBuilder response = new("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n");
        response.Append("Sec-WebSocket-Accept: ").Append('x', 28).Append("\r\n");
        if (accepted is not null)
        {
            response.Append("Sec-WebSocket-Protocol: ").Append(accepted).Append("\r\n");
        }

        response.Append("\r\n");
        meter.Segment(Encoding.ASCII.GetByteCount(response.ToString()), outbound: false, udp: false);
    }

    /// <summary>Size of the header of one frame carrying <paramref name="payload"/> bytes (RFC 6455 §5.2).</summary>
    /// <param name="payload">Payload length.</param>
    /// <param name="masked">Whether the frame is masked (every frame a client sends).</param>
    public static int FrameHeaderSize(long payload, bool masked) =>
        BaseFrameHeader + (payload < 126 ? 0 : payload <= ushort.MaxValue ? 2 : 8) + (masked ? MaskBytes : 0);

    /// <inheritdoc />
    public override Task SendAsync(
        ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
        SendAsync(buffer.AsMemory(), messageType, endOfMessage, cancellationToken).AsTask();

    /// <inheritdoc />
    public override async ValueTask SendAsync(
        ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken = default)
    {
        using Kazo.Os.AppSdk.KazoExecutionContext.KernelScope kernel = Kazo.Os.AppSdk.KazoExecutionContext.EnterKernel();
        await _inner.SendAsync(buffer, messageType, endOfMessage, cancellationToken).ConfigureAwait(false);
        // One SendAsync is one frame (a fragment when endOfMessage is false).
        _meter.Segment(FrameHeaderSize(buffer.Length, masked: true) + buffer.Length, outbound: true, udp: false);
    }

    /// <inheritdoc />
    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        ValueWebSocketReceiveResult result = await ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
        return result.MessageType == WebSocketMessageType.Close
            ? new WebSocketReceiveResult(result.Count, result.MessageType, result.EndOfMessage, _inner.CloseStatus, _inner.CloseStatusDescription)
            : new WebSocketReceiveResult(result.Count, result.MessageType, result.EndOfMessage);
    }

    /// <inheritdoc />
    public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using Kazo.Os.AppSdk.KazoExecutionContext.KernelScope kernel = Kazo.Os.AppSdk.KazoExecutionContext.EnterKernel();
        ValueWebSocketReceiveResult result = await _inner.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (result.MessageType == WebSocketMessageType.Close)
        {
            // The server's close frame: a 2-byte code and its reason.
            int closePayload = _inner.CloseStatus is null ? 0 : 2 + Encoding.UTF8.GetByteCount(_inner.CloseStatusDescription ?? string.Empty);
            _meter.Segment(FrameHeaderSize(closePayload, masked: false) + closePayload, outbound: false, udp: false);
            return result;
        }

        // A message can arrive in several reads: its header is counted once, with its last part.
        _inboundMessageBytes += result.Count;
        if (result.EndOfMessage)
        {
            _meter.Segment(FrameHeaderSize(_inboundMessageBytes, masked: false) + (int)Math.Min(_inboundMessageBytes, int.MaxValue), outbound: false, udp: false);
            _inboundMessageBytes = 0;
        }

        return result;
    }

    /// <inheritdoc />
    public override async Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        CountClose(closeStatus, statusDescription);
        try
        {
            await _inner.CloseAsync(closeStatus, statusDescription, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Release();
        }
    }

    /// <inheritdoc />
    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        CountClose(closeStatus, statusDescription);
        return _inner.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);
    }

    /// <inheritdoc />
    public override void Abort()
    {
        _inner.Abort();
        Release();
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _inner.Dispose();
        Release();
    }

    private void CountClose(WebSocketCloseStatus closeStatus, string? statusDescription)
    {
        int payload = closeStatus == WebSocketCloseStatus.Empty ? 0 : 2 + Encoding.UTF8.GetByteCount(statusDescription ?? string.Empty);
        _meter.Segment(FrameHeaderSize(payload, masked: true) + payload, outbound: true, udp: false);
    }

    private void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _buffers.Dispose();
        }
    }
}
