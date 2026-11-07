using System.Net;
using System.Net.Sockets;

namespace Kazo.Os.AppSdk.Net;

/// <summary>
/// The byte stream of a TCP connection, shaped like <see cref="NetworkStream"/> (ADR 0102 §2);
/// returned by <see cref="KazoTcpClient.ConnectAsync(string, int, CancellationToken)"/>.
/// </summary>
/// <remarks>
/// <para>
/// Only the asynchronous methods can wait for the network: the browser has one thread, so a
/// synchronous <see cref="Read(byte[], int, int)"/> returns only bytes already received and throws
/// <see cref="NotSupportedException"/> when none are, and the synchronous writes always throw.
/// Use <see cref="ReadAsync(Memory{byte}, CancellationToken)"/> and
/// <see cref="WriteAsync(ReadOnlyMemory{byte}, CancellationToken)"/> (and <c>await using</c>).
/// </para>
/// <para>
/// A read returns 0 once the peer closed its side, the relay closed an idle connection, or the
/// stream was disposed, as a socket read does at end of stream.
/// </para>
/// </remarks>
public sealed class KazoNetworkStream : Stream
{
    private readonly IKazoSocket _socket;
    private byte[] _pending = [];
    private int _pendingOffset;
    private bool _endOfStream;
    private bool _disposed;

    internal KazoNetworkStream(IKazoSocket socket) => _socket = socket;

    /// <summary>Gets the address and port of the other end, as <c>Socket.RemoteEndPoint</c>.</summary>
    public IPEndPoint RemoteEndPoint => _socket.RemoteEndPoint;

    /// <summary>
    /// Gets whether the connection is encrypted, as <c>SslStream.IsEncrypted</c>: opened with
    /// <see cref="KazoTcpConnectOptions.UseTls"/> (ADR 0235 §2).
    /// </summary>
    public bool IsEncrypted => _socket.TlsProtocol is not null;

    /// <summary>
    /// Gets the TLS version negotiated with the server (<c>TLS 1.3</c>, <c>TLS 1.2</c>), or
    /// <see langword="null"/> for a clear-text connection; what <c>SslStream.SslProtocol</c> says.
    /// </summary>
    public string? TlsProtocol => _socket.TlsProtocol;

    /// <summary>
    /// Gets the subject of the server's certificate (<c>CN=irc.example.net</c>), or
    /// <see langword="null"/> for a clear-text connection; what <c>SslStream.RemoteCertificate.Subject</c> says.
    /// </summary>
    public string? RemoteCertificateSubject => _socket.RemoteCertificateSubject;

    /// <summary>Gets whether bytes already received wait to be read, as <see cref="NetworkStream.DataAvailable"/>.</summary>
    public bool DataAvailable => _pendingOffset < _pending.Length;

    /// <inheritdoc />
    public override bool CanRead => !_disposed;

    /// <inheritdoc />
    public override bool CanWrite => !_disposed;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException("A network stream has no length.");

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException("A network stream has no position.");
        set => throw new NotSupportedException("A network stream has no position.");
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.IsEmpty)
        {
            return 0;
        }

        while (!DataAvailable)
        {
            if (_endOfStream)
            {
                return 0;
            }

            byte[]? received = await _socket.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (received is null)
            {
                _endOfStream = true;
                return 0;
            }

            _pending = received;
            _pendingOffset = 0;
        }

        return TakePending(buffer.Span);
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">No byte was received yet: a blocking read cannot wait on the browser's thread.</exception>
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">No byte was received yet: a blocking read cannot wait on the browser's thread.</exception>
    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.IsEmpty || (_endOfStream && !DataAvailable))
        {
            return 0;
        }

        return DataAvailable
            ? TakePending(buffer)
            : throw new NotSupportedException("A blocking read cannot wait for the network on the browser's thread; use ReadAsync.");
    }

    /// <inheritdoc />
    /// <exception cref="SocketException">The connection is closed or was reset.</exception>
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!buffer.IsEmpty)
        {
            await _socket.SendAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always: use WriteAsync.</exception>
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("A blocking write cannot wait for the network on the browser's thread; use WriteAsync.");

    /// <inheritdoc />
    public override void Flush()
    {
        // Every write is handed to the OS at once, as NetworkStream.Flush does nothing.
    }

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("A network stream cannot seek.");

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException("A network stream has no length.");

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            await _socket.DisposeAsync().ConfigureAwait(false);
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            // Closing only starts the close handshake; nothing waits for it, as close(2) on a socket.
            _ = _socket.DisposeAsync().AsTask();
        }

        base.Dispose(disposing);
    }

    private int TakePending(Span<byte> buffer)
    {
        int count = Math.Min(buffer.Length, _pending.Length - _pendingOffset);
        _pending.AsSpan(_pendingOffset, count).CopyTo(buffer);
        _pendingOffset += count;
        return count;
    }
}
