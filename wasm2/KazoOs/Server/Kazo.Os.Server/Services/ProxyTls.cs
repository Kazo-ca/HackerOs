using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Kazo.Os.Server.Contracts.Proxy;

namespace Kazo.Os.Server.Services;

/// <summary>A TLS session the relay opened to the target (ADR 0235 §2).</summary>
/// <param name="Stream">The authenticated stream; owns the socket, so disposing it closes the connection.</param>
/// <param name="Protocol">The negotiated version, written as <c>TLS 1.3</c>.</param>
/// <param name="CertificateSubject">The subject of the target's certificate.</param>
public sealed record ProxyTlsSession(Stream Stream, string Protocol, string CertificateSubject);

/// <summary>
/// Opens TLS over a connected relay socket, through an injectable, testable boundary
/// (ADR 0235 §2). The browser has no <see cref="SslStream"/>, so the server negotiates TLS with
/// the target and the app reads and writes clear text over the stream WebSocket.
/// </summary>
public interface IProxyTlsNegotiator
{
    /// <summary>
    /// Completes the TLS handshake with the target on <paramref name="socket"/>, sending
    /// <paramref name="targetHost"/> as SNI and checking the certificate against it.
    /// </summary>
    /// <param name="socket">The connected TCP socket; owned by the returned session, closed on failure.</param>
    /// <param name="targetHost">The server name the certificate must match.</param>
    /// <param name="cancellationToken">Cancels the handshake.</param>
    /// <exception cref="ProxyRequestException">
    /// <see cref="ProxyErrorCode.TlsFailed"/>: an invalid, expired or untrusted certificate, a name
    /// mismatch, no common protocol, or a target that closed or did not finish the handshake in time.
    /// </exception>
    Task<ProxyTlsSession> AuthenticateAsync(Socket socket, string targetHost, CancellationToken cancellationToken);
}

/// <summary>
/// Uses <see cref="SslStream"/> with the default certificate validation: the chain must build
/// to a root of the system trust store, and the name must match. There is deliberately no option
/// to accept an invalid certificate (ADR 0235 §2): a relay that did would hand the app a
/// connection an attacker on the path can read.
/// </summary>
public sealed class SslStreamProxyTlsNegotiator : IProxyTlsNegotiator
{
    /// <summary>How long the handshake may take once the socket is connected.</summary>
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    private readonly X509Certificate2Collection? _trustedRoots;

    /// <summary>Validates certificates against the system trust store (what the server registers).</summary>
    public SslStreamProxyTlsNegotiator()
    {
    }

    /// <summary>
    /// Validates certificates against <paramref name="trustedRoots"/> only, in place of the system
    /// store. Tests use it to trust the root of a test certificate; the name, the validity dates
    /// and the chain are still checked.
    /// </summary>
    /// <param name="trustedRoots">The only roots a certificate chain may end at.</param>
    public SslStreamProxyTlsNegotiator(X509Certificate2Collection trustedRoots) =>
        _trustedRoots = trustedRoots ?? throw new ArgumentNullException(nameof(trustedRoots));

    /// <inheritdoc />
    public async Task<ProxyTlsSession> AuthenticateAsync(Socket socket, string targetHost, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetHost);

        SslStream stream = new(new NetworkStream(socket, ownsSocket: true), leaveInnerStreamOpen: false);
        SslClientAuthenticationOptions options = new()
        {
            TargetHost = targetHost,
            // Let the operating system pick the versions it still considers safe (TLS 1.2 and 1.3).
            EnabledSslProtocols = SslProtocols.None,
            CertificateChainPolicy = _trustedRoots is null ? null : CustomRootPolicy(_trustedRoots),
        };

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HandshakeTimeout);
        try
        {
            await stream.AuthenticateAsClientAsync(options, timeout.Token).ConfigureAwait(false);
            return new ProxyTlsSession(stream, ProtocolName(stream.SslProtocol), stream.RemoteCertificate?.Subject ?? string.Empty);
        }
        catch (AuthenticationException exception)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw new ProxyRequestException(ProxyErrorCode.TlsFailed, $"TLS with {targetHost} failed: {exception.Message}");
        }
        catch (IOException)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw new ProxyRequestException(ProxyErrorCode.TlsFailed, $"{targetHost} closed the connection during the TLS handshake.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw new ProxyRequestException(ProxyErrorCode.TlsFailed,
                $"{targetHost} did not finish the TLS handshake within {HandshakeTimeout.TotalSeconds:0} s.");
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Writes a negotiated version the way people read it: <c>TLS 1.3</c>.</summary>
    /// <param name="protocol">The negotiated protocol.</param>
    public static string ProtocolName(SslProtocols protocol) => protocol switch
    {
        SslProtocols.Tls13 => "TLS 1.3",
        SslProtocols.Tls12 => "TLS 1.2",
        _ => protocol.ToString()
    };

    private static X509ChainPolicy CustomRootPolicy(X509Certificate2Collection roots)
    {
        X509ChainPolicy policy = new()
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            // A test root publishes no revocation list; the system-store path keeps SslStream's default.
            RevocationMode = X509RevocationMode.NoCheck,
        };
        policy.CustomTrustStore.AddRange(roots);
        return policy;
    }
}
