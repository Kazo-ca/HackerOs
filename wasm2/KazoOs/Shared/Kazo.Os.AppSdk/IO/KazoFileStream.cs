using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.AppSdk.IO;

/// <summary>
/// A <see cref="Stream"/> over one virtual file, shaped like <see cref="FileStream"/> (ADR 0043).
/// </summary>
/// <remarks>
/// <para>
/// <b>Read</b> streams wrap the engine's streamed read handle. <b>Write</b> streams buffer in memory
/// (the browser has no temporary file) and publish with <b>one</b> atomic engine write on
/// <see cref="FlushAsync(CancellationToken)"/> or <see cref="DisposeAsync"/>; a very large file
/// written through a stream therefore costs its size in memory.
/// </para>
/// <para>
/// The synchronous <see cref="Flush"/> does nothing (it cannot wait for IndexedDB without blocking
/// the only browser thread), and synchronously disposing a write stream that still holds unpublished
/// bytes throws <see cref="InvalidOperationException"/> rather than losing them: always
/// <c>await using</c> a write stream (and <c>await using</c> any <see cref="StreamWriter"/> over it).
/// </para>
/// <para>
/// The stream keeps the gateway of the app that opened it, so publishing on disposal never depends
/// on the ambient context of the code that disposes it.
/// </para>
/// <para>
/// When the OS measures memory (ADR 0068), a write stream's buffer is charged to the app's process
/// (<c>VmData</c> of <c>/proc/&lt;pid&gt;/status</c>) through
/// <see cref="IAppFileSystemGateway.ChargeStreamBuffer"/>, until the stream is released.
/// </para>
/// </remarks>
public sealed class KazoFileStream : Stream
{
    private readonly IAppFileSystemGateway _gateway;
    private readonly VirtualPath _path;
    private readonly StreamMode _mode;
    private readonly Stream _inner;
    private readonly FileSystemContentReadHandle? _readHandle;
    private readonly FileSystemContentDescriptor _descriptor;
    private readonly bool _strict;
    private readonly KernelMemoryCharge _bufferCharge;
    private long _appendBaseLength;
    private bool _dirty;
    private bool _disposed;

    private KazoFileStream(
        IAppFileSystemGateway gateway,
        VirtualPath path,
        StreamMode mode,
        Stream inner,
        FileSystemContentReadHandle? readHandle,
        FileSystemContentDescriptor descriptor,
        long? revision,
        bool strict)
    {
        _gateway = gateway;
        _path = path;
        _mode = mode;
        _inner = inner;
        _readHandle = readHandle;
        _descriptor = descriptor;
        Revision = revision;
        _strict = strict;
        _bufferCharge = mode == StreamMode.Read ? KernelMemoryCharge.None : gateway.ChargeStreamBuffer();
    }

    private enum StreamMode
    {
        Read,
        Write,
        Append
    }

    /// <summary>Gets the canonical path of the file.</summary>
    public string Name => _path.Value;

    /// <summary>
    /// Gets the file's revision: the one read for a read stream; for a write stream, the one the next
    /// publish is based on in strict mode, updated after every successful publish (<see langword="null"/>
    /// before the first publish of a new file).
    /// </summary>
    public long? Revision { get; private set; }

    /// <inheritdoc />
    public override bool CanRead => !_disposed && _mode == StreamMode.Read;

    /// <inheritdoc />
    public override bool CanWrite => !_disposed && _mode != StreamMode.Read;

    /// <inheritdoc />
    public override bool CanSeek => !_disposed && _mode switch
    {
        StreamMode.Read => _inner.CanSeek,
        StreamMode.Write => true,
        _ => false
    };

    /// <inheritdoc />
    public override long Length
    {
        get
        {
            ThrowIfDisposed();
            return _mode == StreamMode.Append ? _appendBaseLength + _inner.Length : _inner.Length;
        }
    }

    /// <inheritdoc />
    public override long Position
    {
        get
        {
            ThrowIfDisposed();
            return _mode == StreamMode.Append ? Length : _inner.Position;
        }
        set
        {
            ThrowIfDisposed();
            if (_mode == StreamMode.Append)
            {
                throw new NotSupportedException("An append stream cannot seek.");
            }

            _inner.Position = value;
        }
    }

    /// <summary>Opens <paramref name="path"/> for reading through <paramref name="gateway"/>.</summary>
    internal static async Task<KazoFileStream> OpenReadAsync(
        IAppFileSystemGateway gateway, VirtualPath path, CancellationToken cancellationToken)
    {
        FileSystemResult<FileSystemContentReadHandle> result =
            await gateway.ReadAsync(new FileSystemReadRequest(path), cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw KazoFileSystemErrorTranslator.Translate(result.Error!, KazoEntryTarget.File, path.Value);
        }

        FileSystemContentReadHandle handle = result.Value!;
        return new KazoFileStream(
            gateway, path, StreamMode.Read, handle.Content, handle, handle.Descriptor, handle.Entry.Metadata.Revision, strict: false);
    }

    /// <summary>
    /// Opens <paramref name="path"/> for replacement (or append) writing. Validates up front what
    /// <see cref="FileStream"/> would reject on open: a missing parent directory, or a directory target.
    /// </summary>
    internal static async Task<KazoFileStream> OpenWriteAsync(
        IAppFileSystemGateway gateway, VirtualPath path, bool append, KazoWriteOptions? options, CancellationToken cancellationToken)
    {
        if (options?.CreateMode is { } createMode)
        {
            KazoUnixFileMode.ToPermissions(createMode, nameof(options));
        }

        await KazoFileSystemCore.RequireParentAsync(gateway, path, cancellationToken).ConfigureAwait(false);
        FileSystemEntrySnapshot? existing = await KazoFileSystemCore.TryStatAsync(
            gateway, path, FileSystemLinkBehavior.Follow, KazoEntryTarget.File, cancellationToken).ConfigureAwait(false);
        if (existing?.Metadata.Kind == FileSystemEntryKind.Directory)
        {
            throw KazoFileSystemCore.IsDirectory(path);
        }

        if (options?.ExpectedRevision is { } expected && existing?.Metadata.Revision != expected)
        {
            throw existing is null
                ? new KazoFileNotFoundException(path.Value)
                : new KazoFileConflictException(path.Value, existing.Metadata.Revision);
        }

        // ADR 0125: a write stream holds a descriptor of its process while it buffers (EMFILE past nofile).
        FileSystemResult<IDisposable> opened = await gateway.OpenDescriptorAsync(path, cancellationToken).ConfigureAwait(false);
        if (!opened.Succeeded)
        {
            throw KazoFileSystemErrorTranslator.Translate(opened.Error!, KazoEntryTarget.File, path.Value);
        }

        KazoFileStream stream = new(
            gateway,
            path,
            append ? StreamMode.Append : StreamMode.Write,
            new MemoryStream(),
            readHandle: null,
            DescriptorFor(existing),
            options?.ExpectedRevision ?? existing?.Metadata.Revision,
            strict: options?.ExpectedRevision is not null)
        {
            OpenDescriptor = opened.Value,
            CreateOptions = options,
            // A replacing open truncates even if nothing is written; an append open creates a missing file.
            _dirty = !append || existing is null,
            _appendBaseLength = existing?.Metadata is FileMetadata file ? file.Length : 0
        };
        return stream;
    }

    private KazoWriteOptions? CreateOptions { get; init; }

    /// <summary>The descriptor a write stream holds until it is released (ADR 0125); a read stream's is held by its read handle.</summary>
    private IDisposable? OpenDescriptor { get; init; }

    /// <summary>Keeps an existing text file's media type (as UTF-8 text); anything else is published as binary.</summary>
    private static FileSystemContentDescriptor DescriptorFor(FileSystemEntrySnapshot? existing) =>
        existing?.Metadata is FileMetadata { MediaType: var mediaType } && mediaType.StartsWith("text/", StringComparison.Ordinal)
            ? FileSystemContentDescriptor.Text(mediaType)
            : FileSystemContentDescriptor.Binary();

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        ThrowIfNotReadable();
        return _inner.Read(buffer, offset, count);
    }

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        ThrowIfNotReadable();
        return _inner.Read(buffer);
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ThrowIfNotReadable();
        return _inner.ReadAsync(buffer, offset, count, cancellationToken);
    }

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfNotReadable();
        return _inner.ReadAsync(buffer, cancellationToken);
    }

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        ThrowIfNotWritable();
        _inner.Write(buffer, offset, count);
        _dirty = true;
        ChargeBuffer();
    }

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ThrowIfNotWritable();
        _inner.Write(buffer);
        _dirty = true;
        ChargeBuffer();
    }

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Write(buffer, offset, count);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
    {
        ThrowIfDisposed();
        if (!CanSeek)
        {
            throw new NotSupportedException("This stream cannot seek.");
        }

        return _inner.Seek(offset, origin);
    }

    /// <inheritdoc />
    public override void SetLength(long value)
    {
        ThrowIfNotWritable();
        if (_mode == StreamMode.Append)
        {
            throw new NotSupportedException("An append stream cannot change its length.");
        }

        _inner.SetLength(value);
        _dirty = true;
        ChargeBuffer();
    }

    /// <summary>Does nothing: buffered bytes are published by <see cref="FlushAsync(CancellationToken)"/> or <see cref="DisposeAsync"/>.</summary>
    public override void Flush()
    {
    }

    /// <summary>Publishes the buffered content with one atomic engine write (no-op for a read stream or when nothing changed).</summary>
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_mode == StreamMode.Read || !_dirty)
        {
            return;
        }

        KazoWriteOptions options = new()
        {
            ExpectedRevision = _strict ? Revision : null,
            CreateMode = CreateOptions?.CreateMode
        };

        FileSystemEntrySnapshot committed;
        if (_mode == StreamMode.Write)
        {
            byte[] content = ((MemoryStream)_inner).ToArray();
            committed = await KazoFileSystemCore.PublishAsync(
                _gateway, _path, _ => content, needsExistingContent: false, _descriptor, options, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            byte[] appended = ((MemoryStream)_inner).ToArray();
            committed = await KazoFileSystemCore.PublishAsync(
                _gateway, _path, existing => existing is null ? appended : [.. existing, .. appended],
                needsExistingContent: true, _descriptor, options, cancellationToken).ConfigureAwait(false);

            // The appended bytes are now part of the file: start a fresh buffer after them.
            _appendBaseLength = committed.Metadata is FileMetadata file ? file.Length : _appendBaseLength + appended.Length;
            _inner.SetLength(0);
            ChargeBuffer();
        }

        Revision = committed.Metadata.Revision;
        _dirty = false;
    }

    /// <summary>Publishes any unpublished bytes, then releases the stream.</summary>
    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (_mode != StreamMode.Read)
            {
                await FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            _disposed = true;
            _bufferCharge.Dispose();
            OpenDescriptor?.Dispose();
            if (_readHandle is not null)
            {
                await _readHandle.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
            }

            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// Releases the stream synchronously. A write stream with unpublished bytes throws instead of
    /// silently dropping them, because publishing needs an asynchronous write.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (_disposed || !disposing)
        {
            base.Dispose(disposing);
            return;
        }

        if (_mode != StreamMode.Read && _dirty)
        {
            throw new InvalidOperationException(
                $"KazoFileStream '{_path.Value}' still holds unpublished bytes: dispose it with 'await using' (DisposeAsync) " +
                "or call FlushAsync() before a synchronous Dispose().");
        }

        _disposed = true;
        _bufferCharge.Dispose();
        OpenDescriptor?.Dispose();
        _inner.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>Keeps the kernel-memory charge equal to the memory the buffer really holds (its capacity).</summary>
    private void ChargeBuffer()
    {
        if (_inner is MemoryStream buffer)
        {
            _bufferCharge.Resize(buffer.Capacity);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private void ThrowIfNotReadable()
    {
        ThrowIfDisposed();
        if (_mode != StreamMode.Read)
        {
            throw new NotSupportedException("This stream was opened for writing.");
        }
    }

    private void ThrowIfNotWritable()
    {
        ThrowIfDisposed();
        if (_mode == StreamMode.Read)
        {
            throw new NotSupportedException("This stream was opened for reading.");
        }
    }
}
