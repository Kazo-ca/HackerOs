using System.Security.Cryptography;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Diagnostics;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Time;

namespace Kazo.Os.Platform.Core.FileSystem.Virtual;

/// <summary>
/// Serves the <c>/dev</c> mount (ADR 0046): <c>null</c>, <c>zero</c>, <c>random</c> and
/// <c>urandom</c> as <see cref="FileSystemEntryKind.CharacterDevice"/> entries.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>/dev/null</c> reads as an immediate end of stream.</item>
/// <item><c>/dev/zero</c> is an endless stream of <c>0x00</c>.</item>
/// <item><c>/dev/random</c> and <c>/dev/urandom</c> are endless streams from
/// <see cref="RandomNumberGenerator"/>, never from <c>ISimulationRandom</c>, which ADR 0012 keeps for
/// deterministic simulation only.</item>
/// <item><c>/dev/kmsg</c> (when the host gives a <see cref="KernelLog"/>, ADR 0126) reads as the
/// kernel's ring buffer, one record per line, which <c>dmesg</c> formats; a line written to it is
/// added to the buffer as a <c>KERN_NOTICE</c> message, as <c>echo text &gt; /dev/kmsg</c> does. It is
/// <c>0644</c> and owned by the system, like Linux's.</item>
/// </list>
/// Every other device accepts and discards writes without changing revision or raising a change event.
/// Endless streams hand out at most 4 KiB per read and honor cancellation, so a reader must bound
/// its own read (like <c>head -c 16 /dev/urandom</c>) instead of reading to the end.
/// </remarks>
public sealed class DevFileSystemProvider : VirtualFileSystemProvider
{
    /// <summary>Canonical <c>/dev</c> mount path.</summary>
    public static readonly VirtualPath DefaultMountPath = VirtualPath.Parse("/dev");

    private const ushort DirectoryMode = 0b111_101_101; // 0755
    private const ushort DeviceMode = 0b110_110_110; // 0666
    private const ushort KernelMessagesMode = 0b110_100_100; // 0644
    private const string KernelMessagesDevice = "kmsg";

    private static readonly string[] BaseDeviceNames = ["null", "random", "urandom", "zero"];

    private readonly Func<KernelLog?>? _kernelLog;

    /// <summary>Initializes the <c>/dev</c> provider.</summary>
    /// <param name="clock">Simulation clock supplying revisions and the boot timestamp.</param>
    /// <param name="transactionIdFactory">Optional deterministic transaction ID source for tests.</param>
    public DevFileSystemProvider(ISimulationClock clock, Func<Guid>? transactionIdFactory = null)
        : this(clock, kernelLog: null, transactionIdFactory)
    {
    }

    /// <summary>Initializes the <c>/dev</c> provider with the kernel's ring buffer behind <c>/dev/kmsg</c>.</summary>
    /// <param name="clock">Simulation clock supplying revisions and the boot timestamp.</param>
    /// <param name="kernelLog">Lazily resolves the ring buffer (ADR 0126); <c>/dev/kmsg</c> is absent while it returns <see langword="null"/>.</param>
    /// <param name="transactionIdFactory">Optional deterministic transaction ID source for tests.</param>
    public DevFileSystemProvider(ISimulationClock clock, Func<KernelLog?>? kernelLog, Func<Guid>? transactionIdFactory = null)
        : base(DefaultMountPath, clock, transactionIdFactory)
    {
        _kernelLog = kernelLog;
    }

    private IReadOnlyList<string> DeviceNames =>
        _kernelLog?.Invoke() is null ? BaseDeviceNames : [KernelMessagesDevice, .. BaseDeviceNames];

    /// <inheritdoc />
    public override string ProviderId => "devfs";

    /// <inheritdoc />
    public override ValueTask<FileSystemResult<FileSystemContentReadHandle>> ReadAsync(
        FileSystemReadRequest request,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default)
    {
        string? device = DeviceName(request.Path);
        if (device is null)
        {
            return ValueTask.FromResult(request.Path == MountPath
                ? Failure<FileSystemContentReadHandle>(FileSystemOperation.Read, FileSystemErrorCode.NotFile, request.Path)
                : Failure<FileSystemContentReadHandle>(FileSystemOperation.Read, FileSystemErrorCode.NotFound, request.Path));
        }

        Stream content = device switch
        {
            "null" => Stream.Null,
            KernelMessagesDevice => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(_kernelLog!()!.FormatDevKmsg()), writable: false),
            "zero" => new EndlessDeviceStream(static buffer => buffer.Clear()),
            _ => new EndlessDeviceStream(static buffer => RandomNumberGenerator.Fill(buffer))
        };
        return ValueTask.FromResult(FileSystemResult<FileSystemContentReadHandle>.Success(
            new FileSystemContentReadHandle(
                new FileSystemEntrySnapshot(request.Path, DeviceMetadata(request.Path)),
                FileSystemContentDescriptor.Binary(),
                content)));
    }

    /// <inheritdoc />
    public override ValueTask<FileSystemResult<FileSystemDirectorySnapshot>> EnumerateAsync(
        FileSystemEnumerateRequest request,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default)
    {
        if (request.Path != MountPath)
        {
            return ValueTask.FromResult(DeviceName(request.Path) is null
                ? Failure<FileSystemDirectorySnapshot>(FileSystemOperation.Enumerate, FileSystemErrorCode.NotFound, request.Path)
                : Failure<FileSystemDirectorySnapshot>(FileSystemOperation.Enumerate, FileSystemErrorCode.NotDirectory, request.Path));
        }

        FileSystemDirectoryItem[] items = [.. DeviceNames.Select(name => new FileSystemDirectoryItem(
            FileSystemEntryName.Parse(name),
            DeviceMetadata(Child(MountPath, name))))];
        return ValueTask.FromResult(FileSystemResult<FileSystemDirectorySnapshot>.Success(
            new FileSystemDirectorySnapshot(MountPath, CurrentRevision, items)));
    }

    /// <inheritdoc />
    public override ValueTask<FileSystemResult<FileSystemEntrySnapshot>> StatAsync(
        FileSystemStatRequest request,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default)
    {
        if (request.Path == MountPath)
        {
            return ValueTask.FromResult(FileSystemResult<FileSystemEntrySnapshot>.Success(new FileSystemEntrySnapshot(
                MountPath,
                Directory(MountPath, SystemPrincipal, SystemPrincipal, DirectoryMode, BootTimeUtc))));
        }

        return ValueTask.FromResult(DeviceName(request.Path) is null
            ? Failure<FileSystemEntrySnapshot>(FileSystemOperation.Stat, FileSystemErrorCode.NotFound, request.Path)
            : FileSystemResult<FileSystemEntrySnapshot>.Success(
                new FileSystemEntrySnapshot(request.Path, DeviceMetadata(request.Path))));
    }

    /// <summary>
    /// Drains the written stream and discards it, the way a device driver swallows bytes sent to
    /// <c>/dev/null</c>. The entry's revision does not change and no change event is published.
    /// </summary>
    public override async ValueTask<FileSystemMutationResult> WriteAsync(
        FileSystemWriteRequest request,
        IFileSystemContentSource content,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (DeviceName(request.Path) is null)
        {
            return Rejected(
                FileSystemOperation.Write,
                request.Path == MountPath ? FileSystemErrorCode.NotFile : FileSystemErrorCode.NotFound,
                request.Path);
        }

        try
        {
            await using Stream stream = await content.OpenReadAsync(cancellationToken).ConfigureAwait(false);
            if (DeviceName(request.Path) == KernelMessagesDevice && _kernelLog?.Invoke() is { } kernelLog)
            {
                // printk from user space: each written line becomes one message of the ring buffer.
                using StreamReader reader = new(stream, System.Text.Encoding.UTF8);
                string text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    kernelLog.Write(KernelLogLevel.Notice, line);
                }
            }
            else
            {
                await stream.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Rejected(FileSystemOperation.Write, FileSystemErrorCode.Cancelled, request.Path);
        }

        return Committed(new FileSystemEntrySnapshot(request.Path, DeviceMetadata(request.Path)));
    }

    private string? DeviceName(VirtualPath path)
    {
        string[] segments = RelativeSegments(path);
        return segments.Length == 1 && DeviceNames.Contains(segments[0], StringComparer.Ordinal)
            ? segments[0]
            : null;
    }

    private CharacterDeviceMetadata DeviceMetadata(VirtualPath path) =>
        new(
            StableId(path),
            SystemPrincipal,
            SystemPrincipal,
            FileSystemPermissions.FromMode(DeviceName(path) == KernelMessagesDevice ? KernelMessagesMode : DeviceMode),
            Timestamps(BootTimeUtc, BootTimeUtc),
            CurrentRevision);

    /// <summary>
    /// A read-only, non-seekable stream that never ends. Each read fills at most one 4 KiB block,
    /// so a careless caller spins in small, cancellable steps instead of allocating forever.
    /// </summary>
    private sealed class EndlessDeviceStream(SpanFiller fill) : Stream
    {
        private const int BlockSize = 4096;
        private const int YieldEveryBlocks = 16;
        private long _reads;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            Span<byte> block = buffer[..Math.Min(buffer.Length, BlockSize)];
            fill(block);
            return block.Length;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The browser has a single thread: a reader looping to "end of stream" (cat /dev/zero)
            // must still let the UI run and let Ctrl+C cancel it, so every few blocks yield.
            if (++_reads % YieldEveryBlocks == 0)
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return Read(buffer.Span);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private delegate void SpanFiller(Span<byte> buffer);
}
