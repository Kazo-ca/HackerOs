namespace Kazo.Os.AppSdk.Net;

/// <summary>State of a probed TCP port, as <c>nmap</c> reports it.</summary>
public enum KazoPortState
{
    /// <summary>The handshake completed: something listens.</summary>
    Open,

    /// <summary>The host refused the connection (RST).</summary>
    Closed,

    /// <summary>No answer: a firewall dropped the probe.</summary>
    Filtered
}

/// <summary>One probed TCP port.</summary>
/// <param name="Port">Port number.</param>
/// <param name="State">Observed state.</param>
/// <param name="ServiceName">Service that answered, when the stack recognized it (<c>ssh</c>, <c>http</c>…).</param>
/// <param name="ServiceVersion">Version banner of that service, when known.</param>
public sealed record KazoPortProbeResult(int Port, KazoPortState State, string? ServiceName = null, string? ServiceVersion = null);

/// <summary>An operating-system guess for a host.</summary>
/// <param name="Name">OS family, e.g. <c>Linux</c>.</param>
/// <param name="Version">Version, e.g. <c>5.15</c>.</param>
/// <param name="Accuracy">Confidence of the guess, 0 to 100.</param>
public sealed record KazoOsFingerprint(string Name, string Version, int Accuracy);

/// <summary>
/// TCP connections and probes, the parts of <c>System.Net.Sockets.TcpClient</c> an app and a port
/// scanner need (ADR 0056 section 6, ADR 0102 §2). Probes need <c>network.simulated.read</c>, a
/// connection <c>network.simulated.write</c>.
/// </summary>
/// <remarks>
/// For a host outside the simulated network, an app holding <c>network.real.access</c> can probe
/// <b>one</b> port through the optional server (ADR 0035); a range against a real host is never
/// relayed and reports the host as not found. <see cref="ConnectAsync(string, int, CancellationToken)"/> reaches such a host
/// through the server's relay, on the ports its operator allows; the simulated network carries no
/// raw byte streams, so a simulated host refuses the connection.
/// </remarks>
public static class KazoTcpClient
{
    /// <summary>
    /// Connects to <paramref name="hostNameOrAddress"/>:<paramref name="port"/> with
    /// <paramref name="options"/> (ADR 0235 §2). With <see cref="KazoTcpConnectOptions.UseTls"/>,
    /// the server's relay opens TLS to the target, as <c>SslStream.AuthenticateAsClientAsync</c>
    /// would, and the returned stream carries clear text: the browser has no <c>SslStream</c>, so
    /// the encryption runs between the server and the target, and the trip from the browser to the
    /// server is protected by the server's own <c>https</c>.
    /// </summary>
    /// <remarks>
    /// TLS needs the optional server: without it the host is not found, as for any real host. The
    /// certificate is checked by the server's system trust store against
    /// <see cref="KazoTcpConnectOptions.TargetHost"/>; there is no way to accept an invalid one.
    /// <see cref="KazoNetworkStream.TlsProtocol"/> and <see cref="KazoNetworkStream.RemoteCertificateSubject"/>
    /// say what was negotiated.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="port"/> is not within 1–65535.</exception>
    /// <exception cref="System.Security.Authentication.AuthenticationException">
    /// The TLS handshake failed: invalid, expired or untrusted certificate, or a name mismatch.
    /// </exception>
    /// <exception cref="System.Net.Sockets.SocketException">
    /// The host is unknown (no route to the real network), refused the connection, or is unreachable.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">The app lacks <c>network.simulated.write</c>.</exception>
    public static async Task<KazoNetworkStream> ConnectAsync(
        string hostNameOrAddress, int port, KazoTcpConnectOptions options, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostNameOrAddress);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        ArgumentNullException.ThrowIfNull(options);

        KazoNetworkResult<IKazoSocket> result = await KazoNetworkErrorTranslator
            .CallAsync(gateway => gateway.ConnectTcpAsync(hostNameOrAddress, port, options, cancellationToken))
            .ConfigureAwait(false);
        if (result.Error == KazoNetworkError.TlsFailed)
        {
            throw new System.Security.Authentication.AuthenticationException(
                result.Message ?? $"The TLS handshake with {options.TargetHost ?? hostNameOrAddress} failed.");
        }

        return new KazoNetworkStream(KazoNetworkErrorTranslator.UnwrapSocket(result));
    }

    /// <summary>Probes the ports <paramref name="firstPort"/> to <paramref name="lastPort"/> of a host.</summary>
    /// <returns>
    /// The ports that showed something, in port order; every port of the range that is not listed
    /// is closed (<c>nmap</c>'s "Not shown: N closed tcp ports").
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">The range is not within 1–65535 or is reversed.</exception>
    /// <exception cref="System.Net.Sockets.SocketException">The host is unknown or unreachable.</exception>
    /// <exception cref="UnauthorizedAccessException">The app lacks <c>network.simulated.read</c>.</exception>
    public static async Task<IReadOnlyList<KazoPortProbeResult>> ProbeAsync(
        string hostNameOrAddress, int firstPort, int lastPort, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostNameOrAddress);
        ArgumentOutOfRangeException.ThrowIfLessThan(firstPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lastPort, 65535);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(firstPort, lastPort);

        KazoNetworkResult<IReadOnlyList<KazoPortProbeResult>> result = await KazoNetworkErrorTranslator
            .CallAsync(gateway => gateway.ProbeTcpAsync(hostNameOrAddress, firstPort, lastPort, cancellationToken))
            .ConfigureAwait(false);
        return KazoNetworkErrorTranslator.UnwrapSocket(result);
    }

    /// <summary>
    /// Connects to <paramref name="hostNameOrAddress"/>:<paramref name="port"/> and returns the
    /// connection's stream, as <c>new TcpClient().ConnectAsync(host, port)</c> then
    /// <c>GetStream()</c> do. Dispose the stream to close the connection.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="port"/> is not within 1–65535.</exception>
    /// <exception cref="System.Net.Sockets.SocketException">
    /// The host is unknown (no route to the real network), refused the connection, or is unreachable.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">The app lacks <c>network.simulated.write</c>.</exception>
    public static async Task<KazoNetworkStream> ConnectAsync(
        string hostNameOrAddress, int port, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostNameOrAddress);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);

        KazoNetworkResult<IKazoSocket> result = await KazoNetworkErrorTranslator
            .CallAsync(gateway => gateway.ConnectTcpAsync(hostNameOrAddress, port, cancellationToken))
            .ConfigureAwait(false);
        return new KazoNetworkStream(KazoNetworkErrorTranslator.UnwrapSocket(result));
    }

    /// <summary>Guesses the operating system of a host from its TCP/IP stack (<c>nmap -O</c>).</summary>
    /// <returns>The guess, or <see langword="null"/> when the stack matched nothing known.</returns>
    /// <exception cref="System.Net.Sockets.SocketException">The host is unknown or unreachable.</exception>
    /// <exception cref="UnauthorizedAccessException">The app lacks <c>network.simulated.read</c>.</exception>
    public static async Task<KazoOsFingerprint?> DetectOperatingSystemAsync(
        string hostNameOrAddress, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostNameOrAddress);
        KazoNetworkResult<KazoOsFingerprint?> result = await KazoNetworkErrorTranslator
            .CallAsync(gateway => gateway.DetectOperatingSystemAsync(hostNameOrAddress, cancellationToken))
            .ConfigureAwait(false);
        return KazoNetworkErrorTranslator.UnwrapSocket(result);
    }
}
