namespace Kazo.Os.Server.Contracts.Proxy;

// =============================================================================
// TCP/UDP stream relay contracts — ADR 0102 §2 and §3
//
// A stream is one WebSocket: GET /api/proxy/tcp (or /udp)?host=…&port=…&appId=…
//   1. The server validates the request (grant, simulated domain, address range, port, quota)
//      and connects the TCP (or "connected" UDP) socket BEFORE accepting the WebSocket.
//   2. On success its first frame is a TEXT frame carrying ProxyStreamOpened (JSON); every later
//      frame, in both directions, is BINARY: a chunk of the byte stream for TCP, exactly one
//      datagram for UDP.
//   3. On refusal the WebSocket is accepted then closed at once with ProxyStreamCloseCodes.Rejected
//      and the reason "<ProxyErrorCode> <message>": a browser cannot read the HTTP status of a
//      failed upgrade, but it can read a close code.
//   4. The target closing its side closes the WebSocket with RemoteClosed; the client closing the
//      WebSocket closes the socket; idleness closes it with IdleTimeout.
// A browser WebSocket cannot carry an Authorization header, so the access token may travel as the
// access_token query parameter on these two routes only (as SignalR does).
// =============================================================================

/// <summary>Routes and query parameter names of the stream relay (ADR 0102 §2, §3).</summary>
public static class ProxyStreamRoutes
{
    /// <summary>Relative path of the TCP stream WebSocket.</summary>
    public const string TcpPath = "api/proxy/tcp";

    /// <summary>Relative path of the UDP stream WebSocket.</summary>
    public const string UdpPath = "api/proxy/udp";

    /// <summary>Query parameter naming the target host.</summary>
    public const string HostParameter = "host";

    /// <summary>Query parameter naming the target port.</summary>
    public const string PortParameter = "port";

    /// <summary>Query parameter naming the app the stream is opened for.</summary>
    public const string AppIdParameter = "appId";

    /// <summary>Query parameter carrying the access token when no <c>Authorization</c> header can be set.</summary>
    public const string AccessTokenParameter = "access_token";

    /// <summary>Largest UDP payload one datagram can carry over IPv4 (65 535 − 20 − 8).</summary>
    public const int MaxDatagramBytes = 65_507;

    /// <summary>Largest TCP chunk relayed in one WebSocket message.</summary>
    public const int MaxChunkBytes = 64 * 1024;

    /// <summary>Gets the relative path of the stream route for <paramref name="protocol"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="protocol"/> is not TCP or UDP.</exception>
    public static string PathOf(ProxyProtocol protocol) => protocol switch
    {
        ProxyProtocol.Tcp => TcpPath,
        ProxyProtocol.Udp => UdpPath,
        _ => throw new ArgumentOutOfRangeException(nameof(protocol), protocol, "Only TCP and UDP are relayed as streams.")
    };
}

/// <summary>WebSocket close codes of the stream relay, in the 4000–4999 range left to applications.</summary>
public static class ProxyStreamCloseCodes
{
    /// <summary>The target closed the connection (TCP FIN or RST), or the UDP socket failed.</summary>
    public const int RemoteClosed = 4000;

    /// <summary>The server refused to open the stream; the reason starts with a <see cref="ProxyErrorCode"/>.</summary>
    public const int Rejected = 4001;

    /// <summary>No traffic for <c>StreamIdleTimeoutSeconds</c>.</summary>
    public const int IdleTimeout = 4002;
}

/// <summary>A request to open one TCP or UDP stream.</summary>
/// <param name="Host">Target hostname or IP literal.</param>
/// <param name="Port">Target port (1–65535), filtered by <c>Tcp:AllowedPorts</c> or <c>Udp:AllowedPorts</c>.</param>
/// <param name="AppId">App the stream is opened for; the server checks it holds <c>network.real.access</c>.</param>
public sealed record ProxyStreamRequest(string Host, int Port, string AppId);

/// <summary>First (text) frame of an opened stream: the address the server connected to.</summary>
/// <param name="RemoteAddress">The pinned, validated IP address.</param>
/// <param name="RemotePort">The target port.</param>
public sealed record ProxyStreamOpened(string RemoteAddress, int RemotePort);

/// <summary>Why a stream ended.</summary>
/// <param name="CloseCode">One of <see cref="ProxyStreamCloseCodes"/>, or <c>1000</c> for a normal close by the client.</param>
/// <param name="Reason">Free text; for <see cref="ProxyStreamCloseCodes.Rejected"/> it starts with a <see cref="ProxyErrorCode"/>.</param>
public sealed record ProxyStreamClosure(int CloseCode, string? Reason);

/// <summary>
/// One open TCP or UDP stream relayed by the server, whatever carries it (a WebSocket from the
/// browser, or the socket itself when the server hosts the OS in process).
/// </summary>
public interface IProxyStreamChannel : IAsyncDisposable
{
    /// <summary>Gets <see cref="ProxyProtocol.Tcp"/> or <see cref="ProxyProtocol.Udp"/>.</summary>
    ProxyProtocol Protocol { get; }

    /// <summary>Gets the address and port the server connected to.</summary>
    ProxyStreamOpened Remote { get; }

    /// <summary>Gets why the stream ended, once it has; <see langword="null"/> while it is open.</summary>
    ProxyStreamClosure? Closure { get; }

    /// <summary>Sends a chunk of the byte stream (TCP) or exactly one datagram (UDP).</summary>
    /// <exception cref="InvalidOperationException">The stream is closed.</exception>
    ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits for the next chunk (TCP) or datagram (UDP); <see langword="null"/> once the stream is
    /// closed, with <see cref="Closure"/> saying why.
    /// </summary>
    ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default);
}

/// <summary>The server refused to open a stream, or a relayed request failed with a structured code.</summary>
public sealed class ProxyStreamException : Exception
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="errorCode">One of <see cref="ProxyErrorCode"/>.</param>
    /// <param name="message">Human-readable detail.</param>
    public ProxyStreamException(string errorCode, string message)
        : base(message) => ErrorCode = errorCode;

    /// <summary>Initializes the exception with a generic code.</summary>
    public ProxyStreamException()
        : this(ProxyErrorCode.MalformedRequest, "The stream could not be opened.") { }

    /// <summary>Initializes the exception with a generic code.</summary>
    /// <param name="message">Human-readable detail.</param>
    public ProxyStreamException(string message)
        : this(ProxyErrorCode.MalformedRequest, message) { }

    /// <summary>Initializes the exception with a generic code and an inner exception.</summary>
    /// <param name="message">Human-readable detail.</param>
    /// <param name="innerException">The cause.</param>
    public ProxyStreamException(string message, Exception innerException)
        : base(message, innerException) => ErrorCode = ProxyErrorCode.MalformedRequest;

    /// <summary>Gets the <see cref="ProxyErrorCode"/>.</summary>
    public string ErrorCode { get; }

    /// <summary>Parses the reason of a <see cref="ProxyStreamCloseCodes.Rejected"/> close.</summary>
    /// <param name="reason">"<c>CODE message</c>".</param>
    public static ProxyStreamException FromCloseReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return new ProxyStreamException(ProxyErrorCode.MalformedRequest, "The server refused the stream.");
        }

        int space = reason.IndexOf(' ', StringComparison.Ordinal);
        return space < 0
            ? new ProxyStreamException(reason, reason)
            : new ProxyStreamException(reason[..space], reason[(space + 1)..]);
    }

    /// <summary>Builds the close reason of a refusal, within the 123 bytes a close frame allows.</summary>
    /// <param name="errorCode">One of <see cref="ProxyErrorCode"/>.</param>
    /// <param name="message">Human-readable detail.</param>
    public static string ToCloseReason(string errorCode, string message)
    {
        ArgumentNullException.ThrowIfNull(errorCode);
        string reason = $"{errorCode} {message}";
        while (System.Text.Encoding.UTF8.GetByteCount(reason) > 123)
        {
            reason = reason[..^1];
        }

        return reason;
    }
}
