using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Kazo.Os.Server.Tests;

/// <summary>
/// Certificates for the relay's TLS tests (ADR 0235 §2): a test root, a server certificate it
/// signed for one name, and a self-signed one no root vouches for. Built in memory, never
/// installed in any store.
/// </summary>
internal sealed class TestCertificates : IDisposable
{
    /// <summary>The name the signed server certificate is issued to.</summary>
    public const string ServerName = "irc.tls-test.example";

    public TestCertificates()
    {
        using ECDsa rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest rootRequest = new("CN=Kazo OS relay test root", rootKey, HashAlgorithmName.SHA256);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        Root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        Signed = IssueLeaf(ServerName, Root);
        Untrusted = IssueLeaf(ServerName, issuer: null);
    }

    /// <summary>The root the tests trust.</summary>
    public X509Certificate2 Root { get; }

    /// <summary>A certificate for <see cref="ServerName"/> signed by <see cref="Root"/>.</summary>
    public X509Certificate2 Signed { get; }

    /// <summary>A self-signed certificate for <see cref="ServerName"/>: valid name, no trusted chain.</summary>
    public X509Certificate2 Untrusted { get; }

    /// <summary>Gets the roots to hand to <c>SslStreamProxyTlsNegotiator</c>.</summary>
    public X509Certificate2Collection TrustedRoots => [Root];

    public void Dispose()
    {
        Root.Dispose();
        Signed.Dispose();
        Untrusted.Dispose();
    }

    private static X509Certificate2 IssueLeaf(string name, X509Certificate2? issuer)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new($"CN={name}", key, HashAlgorithmName.SHA256);
        SubjectAlternativeNameBuilder san = new();
        san.AddDnsName(name);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));

        DateTimeOffset notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        DateTimeOffset notAfter = DateTimeOffset.UtcNow.AddDays(7);
        using X509Certificate2 certificate = issuer is null
            ? request.CreateSelfSigned(notBefore, notAfter)
            : request.Create(issuer, notBefore, notAfter, RandomNumberGenerator.GetBytes(8)).CopyWithPrivateKey(key);
        // Round-trip through PKCS#12 so the private key is usable by SslStream on every platform.
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), password: null);
    }
}

/// <summary>A TLS echo on loopback: completes the handshake with a given certificate, then echoes.</summary>
internal sealed class TlsEchoServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();

    public TlsEchoServer(X509Certificate2 certificate)
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception)
                {
                    return;
                }

                _ = Task.Run(() => EchoAsync(client, certificate));
            }
        });
    }

    public int Port { get; }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }

    private async Task EchoAsync(TcpClient client, X509Certificate2 certificate)
    {
        using (client)
        {
            try
            {
                await using SslStream tls = new(client.GetStream());
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, _stop.Token);
                byte[] buffer = new byte[4096];
                int read;
                while ((read = await tls.ReadAsync(buffer, _stop.Token)) > 0)
                {
                    await tls.WriteAsync(buffer.AsMemory(0, read), _stop.Token);
                }
            }
            catch (Exception)
            {
                // A refused handshake (what most tests want) or the test teardown.
            }
        }
    }
}
