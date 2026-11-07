using System.Net;

namespace Kazo.Os.AppSdk.Net;

/// <summary>
/// The network stack of the OS as one app instance sees it (ADR 0056, section 6): name
/// resolution, echo requests, HTTP exchanges and TCP port probes. Reached through
/// <see cref="IAppExecutionContext.Network"/>; apps normally use the <see cref="KazoDns"/>,
/// <see cref="KazoPing"/>, <see cref="KazoHttpClient"/>, <see cref="KazoTcpClient"/> and
/// <see cref="KazoUdpClient"/> facades.
/// </summary>
/// <remarks>
/// <para>
/// The OS tries the simulated network first. When the target is not part of it, the app holds
/// <c>network.real.access</c> and the optional server is connected, the OS relays the call through
/// the server proxy, managing the access token itself. The app sees neither the token nor the
/// state of the server: without a server the outcome is <see cref="KazoNetworkError.HostNotFound"/>.
/// </para>
/// <para>
/// Capabilities: <c>network.simulated.read</c> for resolution, ping, GET, HEAD and probes;
/// <c>network.simulated.write</c> for any other HTTP method and for a TCP or UDP socket. A missing capability throws
/// <c>AppGatewayAccessDeniedException</c>; network failures are returned as a result, never thrown.
/// </para>
/// </remarks>
public interface IAppNetworkGateway
{
    /// <summary>Resolves a host name to its entry (forward lookup).</summary>
    ValueTask<KazoNetworkResult<KazoHostEntry>> ResolveAsync(string hostName, CancellationToken cancellationToken = default);

    /// <summary>Finds the host name of an address (reverse lookup).</summary>
    ValueTask<KazoNetworkResult<KazoHostEntry>> ReverseLookupAsync(IPAddress address, CancellationToken cancellationToken = default);

    /// <summary>Sends one echo request to a host name or address.</summary>
    ValueTask<KazoNetworkResult<KazoPingReply>> PingAsync(
        string hostNameOrAddress, TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>Performs one HTTP exchange, following redirects (at most 10, like a browser).</summary>
    ValueTask<KazoNetworkResult<KazoHttpResponse>> SendAsync(
        KazoHttpRequest request, CancellationToken cancellationToken = default);

    /// <summary>Probes the TCP ports <paramref name="firstPort"/> to <paramref name="lastPort"/> of a host.</summary>
    ValueTask<KazoNetworkResult<IReadOnlyList<KazoPortProbeResult>>> ProbeTcpAsync(
        string hostNameOrAddress, int firstPort, int lastPort, CancellationToken cancellationToken = default);

    /// <summary>
    /// Guesses the operating system of a host from its TCP/IP stack, as <c>nmap -O</c> does;
    /// a <see langword="null"/> value means the stack matched nothing known.
    /// </summary>
    ValueTask<KazoNetworkResult<KazoOsFingerprint?>> DetectOperatingSystemAsync(
        string hostNameOrAddress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens an HTTP listening socket on this machine for <paramref name="prefixes"/>
    /// (<c>http://host:port/path/</c>, ADR 0074 section 2), as <c>HttpListener.Start</c> does. The
    /// OS records the port in its table of listening ports until the socket is disposed or the
    /// process ends; requests sent to it are handed to <see cref="IKazoHttpListenerSocket.AcceptAsync"/>.
    /// </summary>
    /// <remarks>
    /// Needs <c>network.listen</c>, plus <c>network.listen.privileged</c> for a port below 1024;
    /// a web app may listen on the URLs of its own manifest without either (ADR 0073). Another
    /// process already listening on the same host and port yields <see cref="KazoNetworkError.AddressInUse"/>.
    /// </remarks>
    /// <param name="prefixes">Listener prefixes, each ending with <c>/</c>; <c>+</c> and <c>*</c> stand for any host.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    ValueTask<KazoNetworkResult<IKazoHttpListenerSocket>> ListenHttpAsync(
        IReadOnlyList<string> prefixes, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This network gateway cannot listen on ports.");

    /// <summary>
    /// Lists the TCP sockets listening on this machine, as <c>netstat -tlnp</c> and
    /// <c>IPGlobalProperties.GetActiveTcpListeners</c> see them (ADR 0074, section 2). The process
    /// of a socket is only named when the caller may see that process, as on Linux.
    /// </summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    ValueTask<IReadOnlyList<KazoTcpListenerInfo>> GetTcpListenersAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This network gateway cannot list listening ports.");

    /// <summary>
    /// Opens a TCP connection to <paramref name="hostNameOrAddress"/>:<paramref name="port"/>, as
    /// <c>TcpClient.ConnectAsync</c> does (ADR 0102 §2). Apps use it through
    /// <see cref="KazoTcpClient.ConnectAsync(string, int, CancellationToken)"/>.
    /// </summary>
    /// <remarks>
    /// Needs <c>network.simulated.write</c> (a stream carries arbitrary bytes both ways). The
    /// simulated network has no raw byte streams: a simulated or local target answers
    /// <see cref="KazoNetworkError.ConnectionRefused"/>. A target outside it is reached through the
    /// optional server's relay when the app holds <c>network.real.access</c>; otherwise it is
    /// <see cref="KazoNetworkError.HostNotFound"/>.
    /// </remarks>
    /// <param name="hostNameOrAddress">Target host name or IP literal.</param>
    /// <param name="port">Target port, 1–65535.</param>
    /// <param name="cancellationToken">Cancels the connect.</param>
    ValueTask<KazoNetworkResult<IKazoSocket>> ConnectTcpAsync(
        string hostNameOrAddress, int port, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This network gateway cannot open TCP connections.");

    /// <summary>
    /// Opens a TCP connection with <paramref name="options"/> (ADR 0235 §2). With
    /// <see cref="KazoTcpConnectOptions.UseTls"/> the server's relay opens TLS to the target and
    /// checks its certificate; a refused handshake is <see cref="KazoNetworkError.TlsFailed"/>.
    /// Apps use it through <see cref="KazoTcpClient.ConnectAsync(string, int, KazoTcpConnectOptions, CancellationToken)"/>.
    /// </summary>
    /// <remarks>Same capabilities and routes as <see cref="ConnectTcpAsync(string, int, CancellationToken)"/>.</remarks>
    /// <param name="hostNameOrAddress">Target host name or IP literal.</param>
    /// <param name="port">Target port, 1–65535.</param>
    /// <param name="options">TLS and its server name.</param>
    /// <param name="cancellationToken">Cancels the connect.</param>
    ValueTask<KazoNetworkResult<IKazoSocket>> ConnectTcpAsync(
        string hostNameOrAddress, int port, KazoTcpConnectOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.UseTls
            ? throw new NotSupportedException("This network gateway cannot open TLS connections.")
            : ConnectTcpAsync(hostNameOrAddress, port, cancellationToken);
    }

    /// <summary>
    /// Opens a "connected" UDP socket to one destination, as <c>UdpClient.Connect</c> does
    /// (ADR 0102 §3): each send is one datagram, no broadcast or multicast. Apps use it through
    /// <see cref="KazoUdpClient"/>.
    /// </summary>
    /// <remarks>Same capabilities and routes as <see cref="ConnectTcpAsync(string, int, CancellationToken)"/>.</remarks>
    /// <param name="hostNameOrAddress">Target host name or IP literal.</param>
    /// <param name="port">Target port, 1–65535.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    ValueTask<KazoNetworkResult<IKazoSocket>> ConnectUdpAsync(
        string hostNameOrAddress, int port, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This network gateway cannot open UDP sockets.");

    /// <summary>
    /// Opens a WebSocket to a real server, as <c>ClientWebSocket.ConnectAsync</c> does
    /// (ADR 0235 §1). Apps use it through <see cref="KazoClientWebSocket"/>.
    /// </summary>
    /// <remarks>
    /// Needs <c>network.real.access</c>. The OS opens the browser's own <c>WebSocket</c>, so no
    /// server is needed, with the browser's limits (no custom headers, <c>wss://</c> only when the
    /// OS is served over <c>https</c>). A host of the simulated network, this machine or the
    /// internal domain answers <see cref="KazoNetworkError.ConnectionRefused"/> (the simulated
    /// network carries no WebSocket yet); a server that refuses or cannot be reached answers
    /// <see cref="KazoNetworkError.ConnectionFailed"/>, without detail, as a browser reports it.
    /// The returned socket's traffic is counted in <c>/proc/net/dev</c>.
    /// </remarks>
    /// <param name="uri">Absolute <c>ws://</c> or <c>wss://</c> address.</param>
    /// <param name="subProtocols">Sub-protocols offered to the server, in order of preference.</param>
    /// <param name="cancellationToken">Cancels the handshake.</param>
    ValueTask<KazoNetworkResult<System.Net.WebSockets.WebSocket>> ConnectWebSocketAsync(
        Uri uri, IReadOnlyList<string> subProtocols, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This network gateway cannot open WebSockets.");
}

/// <summary>
/// A connected socket of the OS: a TCP stream or a "connected" UDP socket (ADR 0102), opened by
/// <see cref="IAppNetworkGateway.ConnectTcpAsync(string, int, CancellationToken)"/> or <see cref="IAppNetworkGateway.ConnectUdpAsync"/>.
/// Disposing it closes the connection (a TCP FIN), as <c>close(2)</c>.
/// </summary>
public interface IKazoSocket : IAsyncDisposable
{
    /// <summary>Gets the address and port the socket is connected to.</summary>
    IPEndPoint RemoteEndPoint { get; }

    /// <summary>Sends bytes on a TCP stream, or exactly one datagram on a UDP socket.</summary>
    /// <param name="data">The bytes; a UDP datagram carries at most 65 507.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <exception cref="System.Net.Sockets.SocketException">The connection is closed or was reset.</exception>
    ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits for the next bytes (TCP, any amount) or the next datagram (UDP); <see langword="null"/>
    /// once the peer closed the connection, the socket went idle too long, or it was disposed.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait (the socket stays open).</param>
    ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the TLS version the relay negotiated with the target (ADR 0235 §2), or
    /// <see langword="null"/> for a clear-text socket.
    /// </summary>
    string? TlsProtocol => null;

    /// <summary>Gets the subject of the target's TLS certificate, or <see langword="null"/> for a clear-text socket.</summary>
    string? RemoteCertificateSubject => null;
}

/// <summary>
/// An HTTP listening socket of the OS (ADR 0074, section 2), opened by
/// <see cref="IAppNetworkGateway.ListenHttpAsync"/>. Apps use it through <see cref="KazoHttpListener"/>.
/// </summary>
public interface IKazoHttpListenerSocket : IAsyncDisposable
{
    /// <summary>Gets the prefixes the socket answers, normalized (lower-case host, explicit port when not the default).</summary>
    IReadOnlyList<string> Prefixes { get; }

    /// <summary>
    /// Waits for the next request sent to one of <see cref="Prefixes"/>; <see langword="null"/> once
    /// the socket is closed.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait (the socket stays open).</param>
    ValueTask<KazoHttpListenerExchange?> AcceptAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends the reply of an accepted request. Never blocks: the OS takes the bytes and the client
    /// receives them. A second reply to the same request is ignored, as a closed connection would.
    /// </summary>
    /// <param name="exchangeId">The <see cref="KazoHttpListenerExchange.Id"/> answered.</param>
    /// <param name="reply">The status, headers and body.</param>
    void Respond(long exchangeId, KazoHttpListenerReply reply);
}

/// <summary>One request received by an <see cref="IKazoHttpListenerSocket"/>, as it arrived on the wire.</summary>
/// <param name="Id">Identifier of the exchange, unique on its socket, passed back to <see cref="IKazoHttpListenerSocket.Respond"/>.</param>
/// <param name="Method">HTTP method, upper case.</param>
/// <param name="Url">Absolute URL requested (scheme, <c>Host</c> header, port, path and query).</param>
/// <param name="Headers">Request headers, including <c>Host</c> and <c>Cookie</c>.</param>
/// <param name="ContentType">The body's <c>Content-Type</c>, or <see langword="null"/>.</param>
/// <param name="Body">Body bytes, empty when there is none.</param>
/// <param name="RemoteEndPoint">Client address and ephemeral port.</param>
/// <param name="LocalEndPoint">Address and port the request arrived on.</param>
public sealed record KazoHttpListenerExchange(
    long Id,
    string Method,
    Uri Url,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    string? ContentType,
    byte[] Body,
    IPEndPoint RemoteEndPoint,
    IPEndPoint LocalEndPoint);

/// <summary>The reply of a listening process to one <see cref="KazoHttpListenerExchange"/>.</summary>
/// <param name="StatusCode">Status code, e.g. 200.</param>
/// <param name="ReasonPhrase">Reason phrase, or <see langword="null"/> for the standard one.</param>
/// <param name="Headers">Response headers (<c>Set-Cookie</c>, <c>Location</c>…), without <c>Content-Type</c> and <c>Content-Length</c>.</param>
/// <param name="ContentType">The body's <c>Content-Type</c>, or <see langword="null"/> when there is no body.</param>
/// <param name="Body">Body bytes, possibly empty.</param>
public sealed record KazoHttpListenerReply(
    int StatusCode,
    string? ReasonPhrase,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    string? ContentType,
    byte[] Body);

/// <summary>One TCP socket in the <c>LISTEN</c> state, as <c>netstat -tlnp</c> prints it.</summary>
/// <param name="Address">Local address: <c>0.0.0.0</c> for every interface, <c>127.0.0.1</c> for loopback only.</param>
/// <param name="Port">Local port.</param>
/// <param name="ProcessId">PID of the owner, or <see langword="null"/> when the caller may not see it (or it is a kernel daemon).</param>
/// <param name="ProgramName">Program name of the owner (its command name or app name), or <see langword="null"/>.</param>
/// <param name="Owner">Login name of the owner, or <see langword="null"/>.</param>
/// <param name="Prefixes">HTTP prefixes served on the socket; empty for a non-HTTP daemon.</param>
public sealed record KazoTcpListenerInfo(
    IPAddress Address,
    int Port,
    long? ProcessId,
    string? ProgramName,
    string? Owner,
    IReadOnlyList<string> Prefixes);

/// <summary>Why a network call failed, independent of the transport that carried it.</summary>
public enum KazoNetworkError
{
    /// <summary>The call succeeded.</summary>
    None,

    /// <summary>No network knows this name (<c>ERR_NAME_NOT_RESOLVED</c>).</summary>
    HostNotFound,

    /// <summary>The host exists but does not answer (<c>ERR_HOST_UNREACHABLE</c>).</summary>
    HostUnreachable,

    /// <summary>The host answered with a refusal (<c>ERR_CONNECTION_REFUSED</c>).</summary>
    ConnectionRefused,

    /// <summary>More than 10 redirects (<c>ERR_TOO_MANY_REDIRECTS</c>).</summary>
    TooManyRedirects,

    /// <summary>The connection broke or timed out on the way (<c>ERR_CONNECTION_FAILED</c>).</summary>
    ConnectionFailed,

    /// <summary>Another process already listens on this address and port (<c>EADDRINUSE</c>).</summary>
    AddressInUse,

    /// <summary>The target answered too slowly (<c>ERR_TIMED_OUT</c>).</summary>
    TimedOut,

    /// <summary>
    /// A rule of the machine's administrator refused the target (<c>ERR_BLOCKED_BY_ADMINISTRATOR</c>):
    /// for example a port or a private address the relay's operator does not allow (ADR 0210 §6).
    /// </summary>
    BlockedByAdministrator,

    /// <summary>
    /// The TLS handshake with the target failed: invalid, expired or untrusted certificate, or a
    /// name that does not match (ADR 0235 §2). <see cref="KazoTcpClient"/> throws
    /// <c>AuthenticationException</c> for it, as <c>SslStream</c> does.
    /// </summary>
    TlsFailed
}

/// <summary>Outcome of one <see cref="IAppNetworkGateway"/> call: a value or a <see cref="KazoNetworkError"/>.</summary>
/// <typeparam name="T">Type of the value on success.</typeparam>
public sealed record KazoNetworkResult<T>
{
    private KazoNetworkResult(T? value, KazoNetworkError error, string? message)
    {
        Value = value;
        Error = error;
        Message = message;
    }

    /// <summary>Gets the value; meaningful only when <see cref="Succeeded"/> is <see langword="true"/>.</summary>
    public T? Value { get; }

    /// <summary>Gets the failure, <see cref="KazoNetworkError.None"/> on success.</summary>
    public KazoNetworkError Error { get; }

    /// <summary>Gets an optional human-readable detail of the failure.</summary>
    public string? Message { get; }

    /// <summary>Gets whether the call succeeded.</summary>
    public bool Succeeded => Error == KazoNetworkError.None;

    /// <summary>Creates a successful result.</summary>
    public static KazoNetworkResult<T> Success(T value) => new(value, KazoNetworkError.None, null);

    /// <summary>Creates a failed result.</summary>
    /// <exception cref="ArgumentException"><paramref name="error"/> is <see cref="KazoNetworkError.None"/>.</exception>
    public static KazoNetworkResult<T> Failure(KazoNetworkError error, string? message = null) =>
        error == KazoNetworkError.None
            ? throw new ArgumentException("A failure needs an error.", nameof(error))
            : new(default, error, message);
}

/// <summary>Stable codes of <see cref="KazoNetworkError"/>, as a browser prints them.</summary>
public static class KazoNetworkErrorCodes
{
    /// <summary>Gets the <c>ERR_*</c> code of <paramref name="error"/>, or an empty string for <see cref="KazoNetworkError.None"/>.</summary>
    public static string ToCode(KazoNetworkError error) => error switch
    {
        KazoNetworkError.HostNotFound => "ERR_NAME_NOT_RESOLVED",
        KazoNetworkError.HostUnreachable => "ERR_HOST_UNREACHABLE",
        KazoNetworkError.ConnectionRefused => "ERR_CONNECTION_REFUSED",
        KazoNetworkError.TooManyRedirects => "ERR_TOO_MANY_REDIRECTS",
        KazoNetworkError.ConnectionFailed => "ERR_CONNECTION_FAILED",
        KazoNetworkError.AddressInUse => "ERR_ADDRESS_IN_USE",
        KazoNetworkError.TimedOut => "ERR_TIMED_OUT",
        KazoNetworkError.BlockedByAdministrator => "ERR_BLOCKED_BY_ADMINISTRATOR",
        _ => string.Empty
    };
}

/// <summary>Gateway used by contexts that have no network (tests, detached contexts).</summary>
internal sealed class UnsupportedAppNetworkGateway : IAppNetworkGateway
{
    private const string Reason = "This IAppExecutionContext implementation does not provide a Network gateway.";

    public ValueTask<KazoNetworkResult<KazoHostEntry>> ResolveAsync(string hostName, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(Reason);

    public ValueTask<KazoNetworkResult<KazoHostEntry>> ReverseLookupAsync(IPAddress address, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(Reason);

    public ValueTask<KazoNetworkResult<KazoPingReply>> PingAsync(
        string hostNameOrAddress, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(Reason);

    public ValueTask<KazoNetworkResult<KazoHttpResponse>> SendAsync(
        KazoHttpRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(Reason);

    public ValueTask<KazoNetworkResult<IReadOnlyList<KazoPortProbeResult>>> ProbeTcpAsync(
        string hostNameOrAddress, int firstPort, int lastPort, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(Reason);

    public ValueTask<KazoNetworkResult<KazoOsFingerprint?>> DetectOperatingSystemAsync(
        string hostNameOrAddress, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(Reason);

    public ValueTask<KazoNetworkResult<IKazoSocket>> ConnectTcpAsync(
        string hostNameOrAddress, int port, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(Reason);

    public ValueTask<KazoNetworkResult<IKazoSocket>> ConnectUdpAsync(
        string hostNameOrAddress, int port, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(Reason);

    public ValueTask<KazoNetworkResult<System.Net.WebSockets.WebSocket>> ConnectWebSocketAsync(
        Uri uri, IReadOnlyList<string> subProtocols, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(Reason);
}
