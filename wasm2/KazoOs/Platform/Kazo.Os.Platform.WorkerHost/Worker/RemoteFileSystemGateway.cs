using System.Text.Json;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.WorkerHost.Protocol;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;

namespace Kazo.Os.Platform.WorkerHost.Worker;

/// <summary>
/// <see cref="IAppFileSystemGateway"/> of a process in a worker: every operation is a call to the OS,
/// which runs it through the process's own filesystem gateway (permissions, capabilities, revisions,
/// <c>/proc</c> and <c>/dev</c>) and sends the result back.
/// </summary>
/// <remarks>
/// A read opens a handle on the OS side, like <c>open(2)</c>: the first answer carries the entry, the
/// content descriptor and the first bytes (the whole content of a regular file up to
/// <c>16 MiB</c>); the rest of a longer file or of a character device is read in chunks.
/// </remarks>
internal sealed class RemoteFileSystemGateway(RemoteProcessChannel channel) : IAppFileSystemGateway
{
    /// <summary>
    /// Takes a descriptor in the OS's table for a write stream (ADR 0125): the OS answers
    /// <see cref="FileSystemErrorCode.TooManyOpenFiles"/> at the <c>nofile</c> limit; disposing the
    /// handle sends the <c>close(2)</c>.
    /// </summary>
    public async ValueTask<FileSystemResult<IDisposable>> OpenDescriptorAsync(VirtualPath path, CancellationToken cancellationToken = default)
    {
        JsonElement value = await channel.CallAsync(RemoteOps.FsDescriptorOpen, writer => writer.WriteString("p", path.Value), cancellationToken).ConfigureAwait(false);
        return WireCodec.ReadResult<IDisposable>(value, element => new RemoteDescriptor(channel, element.GetInt32("fd")));
    }

    public async ValueTask<FileSystemResult<FileSystemContentReadHandle>> ReadAsync(
        FileSystemReadRequest request, CancellationToken cancellationToken = default)
    {
        JsonElement value = await channel.CallAsync(RemoteOps.FsRead, writer => WireCodec.WriteRead(writer, request), cancellationToken).ConfigureAwait(false);
        return WireCodec.ReadResult(value, element => new FileSystemContentReadHandle(
            WireCodec.ReadSnapshot(element.GetProperty("en")),
            WireCodec.ReadDescriptor(element.GetProperty("d")),
            new RemoteReadStream(
                channel,
                element.GetProperty("b").GetBytesFromBase64(),
                element.GetFlag("eof") ? null : element.GetInt32("h"))));
    }

    public async ValueTask<FileSystemResult<FileSystemDirectorySnapshot>> EnumerateAsync(
        FileSystemEnumerateRequest request, CancellationToken cancellationToken = default)
    {
        JsonElement value = await channel.CallAsync(RemoteOps.FsEnumerate, writer => WireCodec.WriteEnumerate(writer, request), cancellationToken).ConfigureAwait(false);
        return WireCodec.ReadResult(value, WireCodec.ReadDirectory);
    }

    public async ValueTask<FileSystemMutationResult> CreateAsync(
        FileSystemCreateRequest request, CancellationToken cancellationToken = default) =>
        WireCodec.ReadMutation(await channel.CallAsync(
            RemoteOps.FsCreate, writer => WireCodec.WriteCreate(writer, request), cancellationToken).ConfigureAwait(false));

    public async ValueTask<FileSystemMutationResult> WriteAsync(
        FileSystemWriteRequest request, IFileSystemContentSource content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        // The content is the app's own data: read it here, in the worker, and send it in one piece.
        byte[] bytes;
        await using (Stream source = await content.OpenReadAsync(cancellationToken).ConfigureAwait(false))
        {
            using MemoryStream copy = new();
            await source.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
            bytes = copy.ToArray();
        }

        return WireCodec.ReadMutation(await channel.CallAsync(
            RemoteOps.FsWrite, writer => WireCodec.WriteWrite(writer, request, content.Descriptor, bytes), cancellationToken).ConfigureAwait(false));
    }

    public async ValueTask<FileSystemMutationResult> MoveAsync(
        FileSystemMoveRequest request, CancellationToken cancellationToken = default) =>
        WireCodec.ReadMutation(await channel.CallAsync(
            RemoteOps.FsMove, writer => WireCodec.WriteMove(writer, request), cancellationToken).ConfigureAwait(false));

    public async ValueTask<FileSystemMutationResult> CopyAsync(
        FileSystemCopyRequest request, CancellationToken cancellationToken = default) =>
        WireCodec.ReadMutation(await channel.CallAsync(
            RemoteOps.FsCopy, writer => WireCodec.WriteCopy(writer, request), cancellationToken).ConfigureAwait(false));

    public async ValueTask<FileSystemMutationResult> DeleteAsync(
        FileSystemDeleteRequest request, CancellationToken cancellationToken = default) =>
        WireCodec.ReadMutation(await channel.CallAsync(
            RemoteOps.FsDelete, writer => WireCodec.WriteDelete(writer, request), cancellationToken).ConfigureAwait(false));

    public async ValueTask<FileSystemResult<FileSystemEntrySnapshot>> StatAsync(
        FileSystemStatRequest request, CancellationToken cancellationToken = default)
    {
        JsonElement value = await channel.CallAsync(RemoteOps.FsStat, writer => WireCodec.WriteStat(writer, request), cancellationToken).ConfigureAwait(false);
        return WireCodec.ReadResult(value, element => WireCodec.ReadSnapshot(element.GetProperty("en")));
    }

    public async ValueTask<FileSystemMutationResult> SetPermissionsAsync(
        FileSystemSetPermissionsRequest request, CancellationToken cancellationToken = default) =>
        WireCodec.ReadMutation(await channel.CallAsync(
            RemoteOps.FsSetPermissions, writer => WireCodec.WriteSetPermissions(writer, request), cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Not offered in a worker: a selected-resource handle comes from a file dialog of a window app,
    /// and window apps run on the main thread (ADR 0070, section 1).
    /// </summary>
    public IAppFileSystemGateway WithSelectedHandle(FileSystemSelectedResourceHandle handle) =>
        throw RemoteUnsupported.Member("IAppFileSystemGateway.WithSelectedHandle");
}

/// <summary>
/// The content of a file read by a process in a worker: the bytes already received, then the next
/// chunks asked from the OS's open handle (asynchronous reads only, see <see cref="RemoteStandardInput"/>).
/// </summary>
internal sealed class RemoteReadStream : Stream
{
    private const int ChunkSize = 64 * 1024;

    private readonly RemoteProcessChannel _channel;
    private byte[] _buffer;
    private int _position;
    private int? _handle;
    private long _consumed;

    public RemoteReadStream(RemoteProcessChannel channel, byte[] initial, int? handle)
    {
        _channel = channel;
        _buffer = initial;
        _handle = handle;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _consumed;
        set => throw new NotSupportedException();
    }

    private int Available => _buffer.Length - _position;

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (Available == 0 && _handle is not null)
        {
            throw new IOException(
                "A process in a worker reads past the first 16 MiB of a file asynchronously (ReadAsync): " +
                "its thread cannot block waiting for the OS (ADR 0070).");
        }

        return Take(buffer);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        if (Available == 0 && _handle is { } handle)
        {
            JsonElement chunk = await _channel.CallAsync(RemoteOps.FsReadChunk, writer =>
            {
                writer.WriteNumber("h", handle);
                writer.WriteNumber("n", Math.Max(buffer.Length, ChunkSize));
            }, cancellationToken).ConfigureAwait(false);
            _buffer = chunk.GetProperty("b").GetBytesFromBase64();
            _position = 0;
            if (chunk.GetFlag("eof"))
            {
                _handle = null;
            }
        }

        return Take(buffer.Span);
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && _handle is { } handle)
        {
            _handle = null;
            _channel.Notify(RemoteOps.FsReadClose, writer => writer.WriteNumber("h", handle));
        }

        base.Dispose(disposing);
    }

    private int Take(Span<byte> destination)
    {
        int count = Math.Min(destination.Length, Available);
        _buffer.AsSpan(_position, count).CopyTo(destination);
        _position += count;
        _consumed += count;
        return count;
    }
}

/// <summary>The exception of an SDK member a worker does not offer.</summary>
internal static class RemoteUnsupported
{
    public static NotSupportedException Member(string member) =>
        new($"{member} is not available to a process running in a worker (ADR 0070). " +
            "An app that needs it declares [AppManifest(Execution = AppExecutionHost.MainThread)].");
}

/// <summary>A descriptor held in the OS's table by a process in a worker; disposing it closes it once.</summary>
internal sealed class RemoteDescriptor(RemoteProcessChannel channel, int id) : IDisposable
{
    private int _closed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            channel.Notify(RemoteOps.FsDescriptorClose, writer => writer.WriteNumber("fd", id));
        }
    }
}
