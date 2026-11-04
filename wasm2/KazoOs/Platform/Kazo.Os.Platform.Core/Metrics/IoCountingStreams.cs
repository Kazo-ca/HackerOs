using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.Platform.Core.Metrics;

/// <summary>
/// Read-only stream wrapper that reports every read call and the bytes it returned, so a process
/// is charged for what it actually consumes (<c>rchar</c>, <c>syscr</c>), not for the size of the
/// file it opened. A <c>head -c 10</c> of a large file costs ten bytes, as on Linux.
/// </summary>
internal sealed class ReadCountingStream(Stream inner, Action<int> onRead) : Stream
{
    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count) => Report(inner.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer) => Report(inner.Read(buffer));

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Report(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Report(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

    public override int ReadByte()
    {
        int value = inner.ReadByte();
        Report(value < 0 ? 0 : 1);
        return value;
    }

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => inner.DisposeAsync();

    private int Report(int read)
    {
        onRead(read);
        return read;
    }
}

/// <summary>
/// Content source wrapper that totals the bytes the provider pulls out of the caller's content,
/// i.e. the bytes the process handed to <c>write</c> (<c>wchar</c>).
/// </summary>
internal sealed class WriteCountingContentSource(IFileSystemContentSource inner) : IFileSystemContentSource
{
    private long _bytes;

    /// <summary>Gets whether the provider opened the content at all.</summary>
    public bool Opened { get; private set; }

    /// <summary>Gets the bytes read from the content so far.</summary>
    public long Bytes => Interlocked.Read(ref _bytes);

    public FileSystemContentDescriptor Descriptor => inner.Descriptor;

    public long? Length => inner.Length;

    public async ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
    {
        Stream stream = await inner.OpenReadAsync(cancellationToken).ConfigureAwait(false);
        Opened = true;
        return new ReadCountingStream(stream, read => Interlocked.Add(ref _bytes, Math.Max(0, read)));
    }
}
