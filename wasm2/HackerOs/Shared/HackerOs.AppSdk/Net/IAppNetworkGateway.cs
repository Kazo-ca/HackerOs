using System.Net;

namespace HackerOs.AppSdk.Net;

/// <summary>
/// The network stack of the OS as one app instance sees it (ADR 0056, section 6): name
/// resolution, echo requests, HTTP exchanges and TCP port probes. Reached through
/// <see cref="IAppExecutionContext.Network"/>; apps normally use the <see cref="HackDns"/>,
/// <see cref="HackPing"/>, <see cref="HackHttpClient"/>, <see cref="HackTcpClient"/> and
/// <see cref="HackUdpClient"/> facades.
/// </summary>
/// <remarks>
/// <para>
/// The OS tries the simulated network first. When the target is not part of it, the app holds
/// <c>network.real.access</c> and the optional server is connected, the OS relays the call through
/// the server proxy, managing the access token itself. The app sees neither the token nor the
/// state of the server: without a server the outcome is <see cref="HackNetworkError.HostNotFound"/>.
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
    ValueTask<HackNetworkResult<HackHostEntry>> ResolveAsync(string hostName, CancellationToken cancellationToken = default);

    /// <summary>Finds the host name of an address (reverse lookup).</summary>
    ValueTask<HackNetworkResult<HackHostEntry>> ReverseLookupAsync(IPAddress address, CancellationToken cancellationToken = default);

    /// <summary>Sends one echo request to a host name or address.</summary>
    ValueTask<HackNetworkResult<HackPingReply>> PingAsync(
        string hostNameOrAddress, TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>Performs one HTTP exchange, following redirects (at most 10, like a browser).</summary>
    ValueTask<HackNetworkResult<HackHttpResponse>> SendAsync(
        HackHttpRequest request, CancellationToken cancellationToken = default);

    /// <summary>Probes the TCP ports <paramref name="firstPort"/> to <paramref name="lastPort"/> of a host.</summary>
    ValueTask<HackNetworkResult<IReadOnlyList<HackPortProbeResult>>> ProbeTcpAsync(
        string hostNameOrAddress, int firstPort, int lastPort, CancellationToken cancellationToken = default);

    /// <summary>
    /// Guesses the operating system of a host from its TCP/IP stack, as <c>nmap -O</c> does;
    /// a <see langword="null"/> value means the stack matched nothing known.
    /// </summary>
    ValueTask<HackNetworkResult<HackOsFingerprint?>> DetectOperatingSystemAsync(
        string hostNameOrAddress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens an HTTP listening socket on this machine for <paramref name="prefixes"/>
    /// (<c>http://host:port/path/</c>, ADR 0074 section 2), as <c>HttpListener.Start</c> does. The
    /// OS records the port in its table of listening ports until the socket is disposed or the
    /// process ends; requests sent to it are handed to <see cref="IHackHttpListenerSocket.AcceptAsync"/>.
    /// </summary>
    /// <remarks>
    /// Needs <c>network.listen</c>, plus <c>network.listen.privileged</c> for a port below 1024;
    /// a web app may listen on the URLs of its own manifest without either (ADR 0073). Another
    /// process already listening on the same host and port yields <see cref="HackNetworkError.AddressInUse"/>.
    /// </remarks>
    /// <param name="prefixes">Listener prefixes, each ending with <c>/</c>; <c>+</c> and <c>*</c> stand for any host.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    ValueTask<HackNetworkResult<IHackHttpListenerSocket>> ListenHttpAsync(
        IReadOnlyList<string> prefixes, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This network gateway cannot listen on ports.");

    /// <summary>
    /// Lists the TCP sockets listening on this machine, as <c>netstat -tlnp</c> and
    /// <c>IPGlobalProperties.GetActiveTcpListeners</c> see them (ADR 0074, section 2). The process
    /// of a socket is only named when the caller may see that process, as on Linux.
    /// </summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    ValueTask<IReadOnlyList<HackTcpListenerInfo>> GetTcpListenersAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This network gateway cannot list listening ports.");

    /// <summary>
    /// Opens a TCP connection to <paramref name="hostNameOrAddress"/>:<paramref name="port"/>, as
    /// <c>TcpClient.ConnectAsync</c> does (ADR 0102 §2). Apps use it through
    /// <see cref="HackTcpClient.ConnectAsync"/>.
    /// </summary>
    /// <remarks>
    /// Needs <c>network.simulated.write</c> (a stream carries arbitrary bytes both ways). The
    /// simulated network has no raw byte streams: a simulated or local target answers
    /// <see cref="HackNetworkError.ConnectionRefused"/>. A target outside it is reached through the
    /// optional server's relay when the app holds <c>network.real.access</c>; otherwise it is
    /// <see cref="HackNetworkError.HostNotFound"/>.
    /// </remarks>
    /// <param name="hostNameOrAddress">Target host name or IP literal.</param>
    /// <param name="port">Target port, 1–65535.</param>
    /// <param name="cancellationToken">Cancels the connect.</param>
    ValueTask<HackNetworkResult<IHackSocket>> ConnectTcpAsync(
        string hostNameOrAddress, int port, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This network gateway cannot open TCP connections.");

    /// <summary>
    /// Opens a "connected" UDP socket to one destination, as <c>UdpClient.Connect</c> does
    /// (ADR 0102 §3): each send is one datagram, no broadcast or multicast. Apps use it through
    /// <see cref="HackUdpClient"/>.
    /// </summary>
    /// <remarks>Same capabilities and routes as <see cref="ConnectTcpAsync"/>.</remarks>
    /// <param name="hostNameOrAddress">Target host name or IP literal.</param>
    /// <param name="port">Target port, 1–65535.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    ValueTask<HackNetworkResult<IHackSocket>> ConnectUdpAsync(
        string hostNameOrAddress, int port, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This network gateway cannot open UDP sockets.");
}

/// <summary>
/// A connected socket of the OS: a TCP stream or a "connected" UDP socket (ADR 0102), opened by
/// <see cref="IAppNetworkGateway.ConnectTcpAsync"/> or <see cref="IAppNetworkGateway.ConnectUdpAsync"/>.
/// Disposing it closes the connection (a TCP FIN), as <c>close(2)</c>.
/// </summary>
public interface IHackSocket : IAsyncDisposable
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
}

/// <summary>
/// An HTTP listening socket of the OS (ADR 0074, section 2), opened by
/// <see cref="IAppNetworkGateway.ListenHttpAsync"/>. Apps use it through <see cref="HackHttpListener"/>.
/// </summary>
public interface IHackHttpListenerSocket : IAsyncDisposable
{
    /// <summary>Gets the prefixes the socket answers, normalized (lower-case host, explicit port when not the default).</summary>
    IReadOnlyList<string> Prefixes { get; }

    /// <summary>
    /// Waits for the next request sent to one of <see cref="Prefixes"/>; <see langword="null"/> once
    /// the socket is closed.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait (the socket stays open).</param>
    ValueTask<HackHttpListenerExchange?> AcceptAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends the reply of an accepted request. Never blocks: the OS takes the bytes and the client
    /// receives them. A second reply to the same request is ignored, as a closed connection would.
    /// </summary>
    /// <param name="exchangeId">The <see cref="HackHttpListenerExchange.Id"/> answered.</param>
    /// <param name="reply">The status, headers and body.</param>
    void Respond(long exchangeId, HackHttpListenerReply reply);
}

/// <summary>One request received by an <see cref="IHackHttpListenerSocket"/>, as it arrived on the wire.</summary>
/// <param name="Id">Identifier of the exchange, unique on its socket, passed back to <see cref="IHackHttpListenerSocket.Respond"/>.</param>
/// <param name="Method">HTTP method, upper case.</param>
/// <param name="Url">Absolute URL requested (scheme, <c>Host</c> header, port, path and query).</param>
/// <param name="Headers">Request headers, including <c>Host</c> and <c>Cookie</c>.</param>
/// <param name="ContentType">The body's <c>Content-Type</c>, or <see langword="null"/>.</param>
/// <param name="Body">Body bytes, empty when there is none.</param>
/// <param name="RemoteEndPoint">Client address and ephemeral port.</param>
/// <param name="LocalEndPoint">Address and port the request arrived on.</param>
public sealed record HackHttpListenerExchange(
    long Id,
    string Method,
    Uri Url,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    string? ContentType,
    byte[] Body,
    IPEndPoint RemoteEndPoint,
    IPEndPoint LocalEndPoint);

/// <summary>The reply of a listening process to one <see cref="HackHttpListenerExchange"/>.</summary>
/// <param name="StatusCode">Status code, e.g. 200.</param>
/// <param name="ReasonPhrase">Reason phrase, or <see langword="null"/> for the standard one.</param>
/// <param name="Headers">Response headers (<c>Set-Cookie</c>, <c>Location</c>…), without <c>Content-Type</c> and <c>Content-Length</c>.</param>
/// <param name="ContentType">The body's <c>Content-Type</c>, or <see langword="null"/> when there is no body.</param>
/// <param name="Body">Body bytes, possibly empty.</param>
public sealed record HackHttpListenerReply(
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
public sealed record HackTcpListenerInfo(
    IPAddress Address,
    int Port,
    long? ProcessId,
    string? ProgramName,
    string? Owner,
    IReadOnlyList<string> Prefixes);

/// <summary>Why a network call failed, independent of the transport that carried it.</summary>
public enum HackNetworkError
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
    BlockedByAdministrator
}

/// <summary>Outcome of one <see cref="IAppNetworkGateway"/> call: a value or a <see cref="HackNetworkError"/>.</summary>
/// <typeparam name="T">Type of the value on success.</typeparam>
public sealed record HackNetworkResult<T>
{
    private HackNetworkResult(T? value, HackNetworkError error, string? message)
    {
        Value = value;
        Error = error;
        Message = message;
    }

    /// <summary>Gets the value; meaningful only when <see cref="Succeeded"/> is <see langword="true"/>.</summary>
    public T? Value { get; }

    /// <summary>Gets the failure, <see cref="HackNetworkError.None"/> on success.</summary>
    public HackNetworkError Error { get; }

    /// <summary>Gets an optional human-readable detail of the failure.</summary>
    public string? Message { get; }

    /// <summary>Gets whether the call succeeded.</summary>
    public bool Succeeded => Error == HackNetworkError.None;

    /// <summary>Creates a successful result.</summary>
    public static HackNetworkResult<T> Success(T value) => new(value, HackNetworkError.None, null);

    /// <summary>Creates a failed result.</summary>
    /// <exception cref="ArgumentException"><paramref name="error"/> is <see cref="HackNetworkError.None"/>.</exception>
    public static HackNetworkResult<T> Failure(HackNetworkError error, string? message = null) =>
        error == HackNetworkError.None
            ? throw new ArgumentException("A failure needs an error.", nameof(error))
            : new(default, error, message);
}

/// <summary>Stable codes of <see cref="HackNetworkError"/>, as a browser prints them.</summary>
public static class HackNetworkErrorCodes
{
    /// <summary>Gets the <c>ERR_*</c> code of <paramref name="error"/>, or an empty string for <see cref="HackNetworkError.None"/>.</summary>
    public static string ToCode(HackNetworkError error) => error switch
    {
        HackNetworkError.HostNotFound => "ERR_NAME_NOT_RESOLVED",
        HackNetworkError.HostUnreachable => "ERR_HOST_UNREACHABLE",
        HackNetworkError.ConnectionRefused => "ERR_CONNECTION_REFUSED",
        HackNetworkError.TooManyRedirects => "ERR_TOO_MANY_REDIRECTS",
        HackNetworkError.ConnectionFailed => "ERR_CONNECTION_FAILED",
        HackNetworkError.AddressInUse => "ERR_ADDRESS_IN_USE",
        HackNetworkError.TimedOut => "ERR_TIMED_OUT",
        HackNetworkError.BlockedByAdministrator => "ERR_BLOCKED_BY_ADMINISTRATOR",
        _ => string.Empty
    };
}

/// <summary>Gateway used by contexts that have no network (tests, detached contexts).</summary>
internal sealed class UnsupportedAppNetworkGateway : IAppNetworkGateway
{
    private const string Reason = "This IAppExecutionContext implementation does not provide a Network gateway.";

    public ValueTask<HackNetworkResult<HackHostEntry>> ResolveAsync(string hostName, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(Reason);

    public ValueTask<HackNetworkResult<HackHostEntry>> ReverseLookupAsync(IPAddress address, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(Reason);

    public ValueTask<HackNetworkResult<HackPingReply>> PingAsync(
        string hostNameOrAddress, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(Reason);

    public ValueTask<HackNetworkResult<HackHttpResponse>> SendAsync(
        HackHttpRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(Reason);

    public ValueTask<HackNetworkResult<IReadOnlyList<HackPortProbeResult>>> ProbeTcpAsync(
        string hostNameOrAddress, int firstPort, int lastPort, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(Reason);

    public ValueTask<HackNetworkResult<HackOsFingerprint?>> DetectOperatingSystemAsync(
        string hostNameOrAddress, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(Reason);

    public ValueTask<HackNetworkResult<IHackSocket>> ConnectTcpAsync(
        string hostNameOrAddress, int port, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(Reason);

    public ValueTask<HackNetworkResult<IHackSocket>> ConnectUdpAsync(
        string hostNameOrAddress, int port, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(Reason);
}
