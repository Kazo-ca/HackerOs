using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Platform.Core.Execution;

/// <summary>
/// One descriptor a process holds in the <see cref="IProcessFileDescriptorTable"/> (ADR 0125):
/// disposing it is <c>close(2)</c>. Closing twice does nothing, like the kernel ignoring a closed
/// descriptor.
/// </summary>
internal sealed class ProcessDescriptorLease : IDisposable
{
    private readonly IProcessFileDescriptorTable _table;
    private readonly ProcessId _pid;
    private int _descriptor;

    /// <summary>Wraps an open descriptor.</summary>
    /// <param name="table">The table that opened it.</param>
    /// <param name="pid">Its process.</param>
    /// <param name="descriptor">Its number (<c>-1</c> when the table did not count it).</param>
    public ProcessDescriptorLease(IProcessFileDescriptorTable table, ProcessId pid, int descriptor)
    {
        _table = table;
        _pid = pid;
        _descriptor = descriptor;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        int descriptor = Interlocked.Exchange(ref _descriptor, -1);
        if (descriptor >= 0)
        {
            _table.Close(_pid, descriptor);
        }
    }
}

/// <summary>
/// The content stream of a read handle that holds a descriptor (ADR 0125): reading passes through,
/// and disposing the stream (or the handle that owns it) closes the descriptor.
/// </summary>
internal sealed class DescriptorHoldingStream : Stream
{
    private readonly Stream _inner;
    private readonly ProcessDescriptorLease _lease;

    public DescriptorHoldingStream(Stream inner, ProcessDescriptorLease lease)
    {
        _inner = inner;
        _lease = lease;
    }

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => _inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => _inner.Read(buffer);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(buffer, cancellationToken);

    public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken) =>
        _inner.CopyToAsync(destination, bufferSize, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _lease.Dispose();
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        _lease.Dispose();
        await _inner.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
