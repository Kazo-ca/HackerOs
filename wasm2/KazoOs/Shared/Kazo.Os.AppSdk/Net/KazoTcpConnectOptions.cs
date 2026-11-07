namespace Kazo.Os.AppSdk.Net;

/// <summary>
/// How <see cref="KazoTcpClient.ConnectAsync(string, int, KazoTcpConnectOptions, CancellationToken)"/>
/// opens a connection (ADR 0235 §2): today, whether the server's relay wraps it in TLS.
/// </summary>
/// <example>
/// <code>
/// await using KazoNetworkStream stream = await KazoTcpClient.ConnectAsync(
///     "irc.libera.chat", 6697, new KazoTcpConnectOptions { UseTls = true }, cancellationToken);
/// </code>
/// </example>
public sealed class KazoTcpConnectOptions
{
    /// <summary>
    /// Gets whether the relay opens TLS to the target and checks its certificate before the
    /// connection is handed to the app. The app then reads and writes clear text.
    /// </summary>
    public bool UseTls { get; init; }

    /// <summary>
    /// Gets the server name sent as SNI and checked against the certificate, as
    /// <c>SslClientAuthenticationOptions.TargetHost</c>; the host name passed to
    /// <c>ConnectAsync</c> when <see langword="null"/>. Ignored without <see cref="UseTls"/>.
    /// </summary>
    public string? TargetHost { get; init; }
}
