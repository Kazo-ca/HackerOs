using System.Net;
using System.Net.Sockets;
using Kazo.Os.AppSdk.Net;
using Kazo.Os.Server.Contracts.Proxy;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Platform.Core.Network;

/// <summary>
/// A socket of an app reaching the real network through the server's relay (ADR 0102 §2, §3).
/// Its traffic is counted on <c>eth0</c> and charged to the app's process, segment by segment
/// (ADR 0057), and its buffers stay charged until it is closed (ADR 0068).
/// </summary>
internal sealed class RelayedSocket : IKazoSocket
{
    private readonly IProxyStreamChannel _channel;
    private readonly NetworkTrafficMeter _meter;
    private readonly KernelMemoryCharge _buffers;
    private readonly bool _udp;
    private int _disposed;

    /// <summary>Wraps an open relay channel.</summary>
    /// <param name="channel">The open stream.</param>
    /// <param name="meter">The app's traffic meter.</param>
    /// <param name="buffers">The socket-buffer charge, released on close.</param>
    public RelayedSocket(IProxyStreamChannel channel, NetworkTrafficMeter meter, KernelMemoryCharge buffers)
    {
        _channel = channel;
        _meter = meter;
        _buffers = buffers;
        _udp = channel.Protocol == ProxyProtocol.Udp;
        RemoteEndPoint = new IPEndPoint(
            IPAddress.TryParse(channel.Remote.RemoteAddress, out IPAddress? address) ? address : IPAddress.None,
            channel.Remote.RemotePort);
    }

    /// <inheritdoc />
    public IPEndPoint RemoteEndPoint { get; }

    /// <inheritdoc />
    public string? TlsProtocol => _channel.Remote.TlsProtocol;

    /// <inheritdoc />
    public string? RemoteCertificateSubject => _channel.Remote.CertificateSubject;

    /// <inheritdoc />
    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        using Kazo.Os.AppSdk.KazoExecutionContext.KernelScope kernel = Kazo.Os.AppSdk.KazoExecutionContext.EnterKernel();
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new SocketException((int)SocketError.NotConnected);
        }

        try
        {
            await _channel.SendAsync(data, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // The peer reset the connection or the relay closed it (EPIPE).
            throw new SocketException((int)SocketError.ConnectionReset);
        }

        _meter.Segment(data.Length, outbound: true, _udp);
    }

    /// <inheritdoc />
    public async ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        using Kazo.Os.AppSdk.KazoExecutionContext.KernelScope kernel = Kazo.Os.AppSdk.KazoExecutionContext.EnterKernel();
        if (Volatile.Read(ref _disposed) != 0)
        {
            return null;
        }

        byte[]? data = await _channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        if (data is not null)
        {
            _meter.Segment(data.Length, outbound: false, _udp);
        }

        return data;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await _channel.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _buffers.Dispose();
        }
    }
}
