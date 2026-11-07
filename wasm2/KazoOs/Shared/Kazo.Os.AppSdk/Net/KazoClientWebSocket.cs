using System.Net.WebSockets;

namespace Kazo.Os.AppSdk.Net;

/// <summary>
/// Options of a <see cref="KazoClientWebSocket"/>, the part of <see cref="ClientWebSocketOptions"/>
/// a browser lets a page set (ADR 0235 §1): the sub-protocols offered to the server.
/// </summary>
/// <remarks>
/// A browser sends no custom header, cookie or client certificate with a WebSocket handshake, and
/// chooses the keep-alive itself, so those options of <see cref="ClientWebSocketOptions"/> have no
/// counterpart here.
/// </remarks>
public sealed class KazoClientWebSocketOptions
{
    private readonly List<string> _subProtocols = [];

    internal KazoClientWebSocketOptions()
    {
    }

    /// <summary>Gets the sub-protocols offered to the server, in the order they were added.</summary>
    public IReadOnlyList<string> SubProtocols => _subProtocols;

    internal bool IsLocked { get; set; }

    /// <summary>Offers a sub-protocol to the server, as <see cref="ClientWebSocketOptions.AddSubProtocol"/>.</summary>
    /// <param name="subProtocol">A token of RFC 6455, for example <c>text.ircv3.net</c>.</param>
    /// <exception cref="ArgumentException">The name is empty, contains a separator or was already added.</exception>
    /// <exception cref="InvalidOperationException">The socket has already been started.</exception>
    public void AddSubProtocol(string subProtocol)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subProtocol);
        if (IsLocked)
        {
            throw new InvalidOperationException("The WebSocket has already been started.");
        }

        if (!IsToken(subProtocol))
        {
            throw new ArgumentException($"The WebSocket sub-protocol '{subProtocol}' is invalid.", nameof(subProtocol));
        }

        if (_subProtocols.Contains(subProtocol, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Duplicate WebSocket sub-protocol '{subProtocol}'.", nameof(subProtocol));
        }

        _subProtocols.Add(subProtocol);
    }

    /// <summary>RFC 6455 §4.1: a sub-protocol is an RFC 2616 token (visible ASCII, no separator).</summary>
    private static bool IsToken(string value)
    {
        const string Separators = "()<>@,;:\\\"/[]?={}";
        foreach (char c in value)
        {
            if (c is < '!' or > '~' || Separators.Contains(c, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// A WebSocket client to a real server, shaped like <see cref="ClientWebSocket"/> (ADR 0235 §1):
/// set <see cref="Options"/>, then <see cref="ConnectAsync"/>, <c>SendAsync</c>, <c>ReceiveAsync</c>
/// and <see cref="CloseAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// The OS opens the browser's own <c>WebSocket</c> for the app, so the optional server is not
/// needed, and counts the frames in <c>/proc/net/dev</c>. The app must hold
/// <c>network.real.access</c>, otherwise <see cref="ConnectAsync"/> throws
/// <see cref="UnauthorizedAccessException"/>.
/// </para>
/// <para>
/// The browser's limits apply: no custom headers, <c>wss://</c> only when the OS is served over
/// <c>https</c>, and a refusal without detail (<see cref="WebSocketException"/> with
/// <see cref="WebSocketError.Faulted"/>). An address of the simulated network is refused the
/// same way, with a "simulated host" message: the simulated network carries no WebSocket yet
/// (issue #393). An app running in a Web Worker cannot open one yet and gets
/// <see cref="NotSupportedException"/>.
/// </para>
/// </remarks>
public sealed class KazoClientWebSocket : WebSocket
{
    private WebSocket? _socket;
    private int _started;
    private volatile bool _disposed;

    /// <summary>Creates a socket that is not connected yet.</summary>
    public KazoClientWebSocket()
    {
    }

    /// <summary>Gets the options applied by <see cref="ConnectAsync"/>; change them before connecting.</summary>
    public KazoClientWebSocketOptions Options { get; } = new();

    /// <inheritdoc />
    public override WebSocketState State => _socket?.State ?? (_disposed
        ? WebSocketState.Closed
        : Volatile.Read(ref _started) != 0 ? WebSocketState.Connecting : WebSocketState.None);

    /// <inheritdoc />
    public override string? SubProtocol => _socket?.SubProtocol;

    /// <inheritdoc />
    public override WebSocketCloseStatus? CloseStatus => _socket?.CloseStatus;

    /// <inheritdoc />
    public override string? CloseStatusDescription => _socket?.CloseStatusDescription;

    /// <summary>Connects to a real WebSocket server, as <see cref="ClientWebSocket.ConnectAsync(Uri, CancellationToken)"/>.</summary>
    /// <param name="uri">Absolute <c>ws://</c> or <c>wss://</c> address.</param>
    /// <param name="cancellationToken">Cancels the handshake.</param>
    /// <exception cref="ArgumentException"><paramref name="uri"/> is not an absolute <c>ws://</c> or <c>wss://</c> address.</exception>
    /// <exception cref="InvalidOperationException">The socket was already started.</exception>
    /// <exception cref="ObjectDisposedException">The socket was disposed.</exception>
    /// <exception cref="UnauthorizedAccessException">The app lacks <c>network.real.access</c>.</exception>
    /// <exception cref="WebSocketException">
    /// The server refused or could not be reached, or the address is a simulated host
    /// (<see cref="WebSocketError.Faulted"/>).
    /// </exception>
    /// <exception cref="NotSupportedException">The app's context has no WebSocket (for example a Web Worker).</exception>
    public async Task ConnectAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || (uri.Scheme != "ws" && uri.Scheme != "wss"))
        {
            throw new ArgumentException("Only Uris starting with 'ws://' or 'wss://' are supported.", nameof(uri));
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("The WebSocket has already been started.");
        }

        Options.IsLocked = true;
        KazoNetworkResult<WebSocket> result;
        try
        {
            string[] subProtocols = [.. Options.SubProtocols];
            result = await KazoNetworkErrorTranslator
                .CallAsync(gateway => gateway.ConnectWebSocketAsync(uri, subProtocols, cancellationToken))
                .ConfigureAwait(false);
        }
        catch
        {
            _disposed = true;
            throw;
        }

        if (!result.Succeeded)
        {
            _disposed = true;
            throw new WebSocketException(WebSocketError.Faulted, result.Message ?? "Unable to connect to the remote server");
        }

        WebSocket socket = result.Value!;
        if (_disposed)
        {
            // Disposed while the handshake was in flight: the connection is not kept.
            socket.Abort();
            socket.Dispose();
            throw new ObjectDisposedException(GetType().FullName);
        }

        _socket = socket;
    }

    /// <inheritdoc />
    public override Task SendAsync(
        ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
        Connected.SendAsync(buffer, messageType, endOfMessage, cancellationToken);

    /// <inheritdoc />
    public override ValueTask SendAsync(
        ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken = default) =>
        Connected.SendAsync(buffer, messageType, endOfMessage, cancellationToken);

    /// <inheritdoc />
    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
        Connected.ReceiveAsync(buffer, cancellationToken);

    /// <inheritdoc />
    public override ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Connected.ReceiveAsync(buffer, cancellationToken);

    /// <inheritdoc />
    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
        Connected.CloseAsync(closeStatus, statusDescription, cancellationToken);

    /// <inheritdoc />
    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
        Connected.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

    /// <inheritdoc />
    public override void Abort()
    {
        _disposed = true;
        _socket?.Abort();
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _disposed = true;
        _socket?.Dispose();
    }

    /// <summary>The open socket, or the exception <see cref="ClientWebSocket"/> throws before a connect.</summary>
    private WebSocket Connected
    {
        get
        {
            if (_socket is { } socket)
            {
                return socket;
            }

            ObjectDisposedException.ThrowIf(_disposed, this);
            throw new InvalidOperationException("The WebSocket is not connected.");
        }
    }
}
