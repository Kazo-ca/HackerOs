using System.Text;
using System.Threading.Channels;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Kazo.Os.Simulation.Abstractions.Events;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Simulation.Abstractions.Time;

namespace Kazo.Os.Platform.Core.Diagnostics;

/// <summary>
/// The only component that writes under <c>/var/log</c> (ADR 0045, section 4). Producers hand it
/// already-formatted lines through the synchronous, never-blocking <see cref="Enqueue"/>; a
/// background flush groups them per file and appends each group with one read-concatenate-write,
/// rotating a file first when the batch would push it past <see cref="LogRotationPolicy.MaxFileBytes"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Queue.</b> Lines wait in a bounded <see cref="Channel{T}"/> of <see cref="QueueCapacity"/>
/// lines. When it is full the oldest line is dropped and counted; the next flush writes one
/// <c>WARN logging: &lt;n&gt; entries dropped</c> line to <c>syslog</c>, the same way a kernel ring
/// buffer reports overruns.
/// </para>
/// <para>
/// <b>Flush triggers.</b> Every <see cref="FlushInterval"/> of simulated time
/// (<see cref="ISimulationClock.Schedule"/>), as soon as <see cref="FlushThreshold"/> lines wait, on
/// <see cref="SessionLoggedOutEvent"/>, and on <see cref="DisposeAsync"/>. Only one flush runs at a
/// time per attached filesystem.
/// </para>
/// <para>
/// <b>Re-attaching.</b> The writer is a host singleton, but under <c>InteractiveServer</c> (a third-party host;
/// <c>Kazo.Os.Server</c> did it until ADR 0130) each circuit mounts its own scoped filesystem and attaches it
/// at boot. A reload can disconnect the previous circuit in the middle of a flush, leaving a JS
/// interop call that never returns. Attaching a different filesystem therefore abandons the
/// previous one: its flush token is cancelled and later flushes use a fresh gate, so the new
/// circuit's boot never waits behind a dead one. Lines of an abandoned batch stay in the
/// in-memory sink only.
/// </para>
/// <para>
/// <b>Before the filesystem exists.</b> Lines accumulate in the queue until the boot sequence calls
/// <see cref="AttachAsync"/> with the mounted <see cref="IFileSystemService"/>. Producers also keep
/// recording into the in-memory <see cref="IDiagnosticSink"/>, which stays visible to the boot error
/// screen even if the filesystem never becomes available.
/// </para>
/// <para>
/// <b>No space left.</b> When the storage is full (<c>ENOSPC</c>, ADR 0099 section 2), the lines of
/// the failed batch are abandoned and counted; the first flush that can write again adds one
/// <c>WARN logging: &lt;n&gt; lines lost (No space left on device)</c> line to <c>syslog</c>.
/// </para>
/// <para>
/// <b>No recursion.</b> A failed write is never logged to a file: it is noted once per failing file
/// in the in-memory sink under the <c>logging</c> category, and noted again only after that file has
/// been written successfully in between.
/// </para>
/// </remarks>
public sealed class LogFileWriter : IAsyncDisposable
{
    /// <summary>Maximum number of lines waiting to be written before the oldest ones are dropped.</summary>
    public const int QueueCapacity = 10_000;

    /// <summary>Number of waiting lines that triggers a flush without waiting for the next interval.</summary>
    public const int FlushThreshold = 100;

    /// <summary>Simulated-time interval between two periodic flushes.</summary>
    public static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

    /// <summary>Category used for the writer's own messages (dropped lines, write failures).</summary>
    public const string LoggingCategory = "logging";

    private const string KernelAppId = "org.kazoos.kernel";
    private const string SystemPrincipal = "system";
    private const ushort DirectoryMode = 0b111_101_101;

    private readonly Channel<PendingLine> _queue;
    private readonly ISimulationClock _clock;
    private readonly IDiagnosticSink? _memoryBuffer;
    private readonly Lock _stateGate = new();
    private readonly HashSet<string> _failingTargets = new(StringComparer.Ordinal);
    private readonly IDisposable? _logoutSubscription;
    private Attachment? _attachment;
    private IDisposable? _periodicFlush;
    private long _droppedSinceLastFlush;
    private long _lostToNoSpace;
    private long _totalDropped;
    private int _flushRequested;
    private bool _disposed;

    /// <summary>Initializes a detached writer; lines queue until <see cref="AttachAsync"/>.</summary>
    /// <param name="clock">Simulation clock providing timestamps and the periodic flush schedule.</param>
    /// <param name="memoryBuffer">
    /// In-memory recent-entries buffer where write failures are noted; never a file.
    /// </param>
    /// <param name="eventBus">Kernel event bus; when given, a logout triggers a flush.</param>
    public LogFileWriter(ISimulationClock clock, IDiagnosticSink? memoryBuffer = null, IEventBus? eventBus = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _memoryBuffer = memoryBuffer;
        _queue = Channel.CreateBounded<PendingLine>(
            new BoundedChannelOptions(QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            },
            _ =>
            {
                Interlocked.Increment(ref _droppedSinceLastFlush);
                Interlocked.Increment(ref _totalDropped);
            });
        _logoutSubscription = eventBus?.Subscribe<SessionLoggedOutEvent>(_ => RequestFlush());
    }

    /// <summary>Gets whether a filesystem is attached and flushes actually write files.</summary>
    public bool IsAttached => Volatile.Read(ref _attachment) is not null;

    /// <summary>Gets the number of lines currently waiting to be written.</summary>
    public int PendingCount => _queue.Reader.Count;

    /// <summary>Gets the number of lines abandoned because the storage was full and not yet reported in <c>syslog</c>.</summary>
    public long PendingNoSpaceLoss => Interlocked.Read(ref _lostToNoSpace);

    /// <summary>Gets the number of lines dropped because the queue was full, since the writer started.</summary>
    public long TotalDropped => Interlocked.Read(ref _totalDropped);

    /// <summary>
    /// Queues one formatted line for <paramref name="target"/>. Synchronous and never blocks: when
    /// the queue is full the oldest waiting line is dropped and counted instead.
    /// </summary>
    /// <param name="target">Log file the line belongs to.</param>
    /// <param name="line">One line produced by <see cref="LogLineFormat"/>, without terminator.</param>
    public void Enqueue(LogFileTarget target, string line)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(line);
        if (_disposed)
        {
            return;
        }

        _queue.Writer.TryWrite(new PendingLine(target, line));
        if (_queue.Reader.Count >= FlushThreshold)
        {
            RequestFlush();
        }
    }

    /// <summary>
    /// Formats one line stamped with the current simulation time and queues it, for platform code
    /// that writes a log line directly rather than through <c>ILogger</c> (e.g. the boot sequence).
    /// </summary>
    /// <param name="target">Log file the line belongs to.</param>
    /// <param name="severity">Severity of the line.</param>
    /// <param name="source">Logical source written before the colon.</param>
    /// <param name="message">Message text.</param>
    /// <param name="processId">PID to write after the source, when known.</param>
    public void Write(
        LogFileTarget target,
        DiagnosticSeverity severity,
        string source,
        string message,
        long? processId = null) =>
        Enqueue(target, LogLineFormat.Format(_clock.UtcNow, severity, source, processId, message));

    /// <summary>
    /// Attaches the mounted filesystem once IndexedDB bootstrap has completed, starts the periodic
    /// flush, and immediately writes every line queued since startup. Attaching again (e.g. after a
    /// host re-initialization) only swaps the filesystem used by later flushes; a different
    /// filesystem abandons a flush still running against the previous one (see remarks).
    /// </summary>
    /// <param name="fileSystem">Mounted, authorized filesystem; the writer uses a system context.</param>
    /// <param name="cancellationToken">Cancels the initial flush.</param>
    public async ValueTask AttachAsync(IFileSystemService fileSystem, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ObjectDisposedException.ThrowIf(_disposed, this);

        Attachment? replaced = null;
        lock (_stateGate)
        {
            if (_attachment is null || !ReferenceEquals(_attachment.FileSystem, fileSystem))
            {
                replaced = _attachment;
                _attachment = new Attachment(fileSystem);
            }

            _periodicFlush ??= _clock.Schedule(FlushInterval, OnPeriodicFlush);
        }

        // Outside the lock: cancellation runs the abandoned flush's continuations synchronously.
        replaced?.Abandon();
        await FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes every waiting line now. Does nothing while no filesystem is attached; lines then stay
    /// queued. Waits for a flush already in progress rather than running two at once.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for the flush gate and the file operations.</param>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        while (Volatile.Read(ref _attachment) is { } attachment)
        {
            try
            {
                await FlushToAsync(attachment, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (attachment.IsAbandoned && !cancellationToken.IsCancellationRequested)
            {
                // The filesystem was replaced while this flush waited or wrote: what is still
                // queued goes to the new one.
            }
        }
    }

    /// <summary>Drains the queue into <paramref name="attachment"/>, one flush at a time per attachment.</summary>
    private async Task FlushToAsync(Attachment attachment, CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, attachment.AbandonedToken);
        await attachment.FlushGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            List<(LogFileTarget Target, List<string> Lines)> batches = DrainQueue(out long reportedLoss);
            foreach ((LogFileTarget target, List<string> lines) in batches)
            {
                // The "lines lost" warning stands for reportedLoss lines: if it is lost too, they all are again.
                long carried = target.Path == LogFileTarget.Syslog.Path ? reportedLoss : 0;
                await WriteBatchAsync(attachment.FileSystem, target, lines, carried, linked.Token).ConfigureAwait(false);
                if (carried > 0)
                {
                    reportedLoss = 0;
                }
            }
        }
        finally
        {
            attachment.FlushGate.Release();
        }
    }

    /// <summary>Stops the periodic flush and writes every waiting line one last time.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        lock (_stateGate)
        {
            _periodicFlush?.Dispose();
            _periodicFlush = null;
        }

        _logoutSubscription?.Dispose();
        try
        {
            await FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _disposed = true;
        }
    }

    private void OnPeriodicFlush()
    {
        lock (_stateGate)
        {
            if (_disposed || _periodicFlush is null)
            {
                return;
            }

            // Re-arm first, so a slow or failing flush never stops the schedule.
            _periodicFlush = _clock.Schedule(FlushInterval, OnPeriodicFlush);
        }

        RequestFlush();
    }

    /// <summary>Starts one background flush unless one is already requested and not yet started.</summary>
    private void RequestFlush()
    {
        if (!IsAttached || Interlocked.CompareExchange(ref _flushRequested, 1, 0) != 0)
        {
            return;
        }

        _ = RunRequestedFlushAsync();
    }

    private async Task RunRequestedFlushAsync()
    {
        try
        {
            // Wait out a flush already in progress, then clear the request flag before draining:
            // a request arriving during our own flush must start another one, not be swallowed.
            if (Volatile.Read(ref _attachment) is { } attachment)
            {
                try
                {
                    await attachment.FlushGate.WaitAsync(attachment.AbandonedToken).ConfigureAwait(false);
                    attachment.FlushGate.Release();
                }
                catch (OperationCanceledException)
                {
                    // Replaced meanwhile: FlushAsync below uses the new attachment.
                }
            }

            Volatile.Write(ref _flushRequested, 0);
            await FlushAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Background flushes have no caller to report to; never let them fault silently into
            // the unobserved-task handler, and never write about it to a file.
            NoteFailure("*", $"Background log flush failed: {exception.GetType().Name}.");
        }
        finally
        {
            Volatile.Write(ref _flushRequested, 0);
        }
    }

    /// <summary>Reads every waiting line, grouped by file in first-seen order.</summary>
    private List<(LogFileTarget Target, List<string> Lines)> DrainQueue(out long reportedLoss)
    {
        List<(LogFileTarget Target, List<string> Lines)> batches = [];
        Dictionary<VirtualPath, List<string>> byPath = [];

        reportedLoss = Interlocked.Exchange(ref _lostToNoSpace, 0);
        if (reportedLoss > 0)
        {
            List<string> syslog = [];
            byPath[LogFileTarget.Syslog.Path] = syslog;
            batches.Add((LogFileTarget.Syslog, syslog));
            syslog.Add(LogLineFormat.Format(
                _clock.UtcNow,
                DiagnosticSeverity.Warning,
                LoggingCategory,
                null,
                $"{reportedLoss} lines lost (No space left on device)"));
        }

        long dropped = Interlocked.Exchange(ref _droppedSinceLastFlush, 0);
        if (dropped > 0)
        {
            if (!byPath.TryGetValue(LogFileTarget.Syslog.Path, out List<string>? syslog))
            {
                syslog = [];
                byPath[LogFileTarget.Syslog.Path] = syslog;
                batches.Add((LogFileTarget.Syslog, syslog));
            }

            syslog.Add(LogLineFormat.Format(
                _clock.UtcNow,
                DiagnosticSeverity.Warning,
                LoggingCategory,
                null,
                $"{dropped} entries dropped"));
        }

        while (_queue.Reader.TryRead(out PendingLine pending))
        {
            if (!byPath.TryGetValue(pending.Target.Path, out List<string>? lines))
            {
                lines = [];
                byPath[pending.Target.Path] = lines;
                batches.Add((pending.Target, lines));
            }

            lines.Add(pending.Line);
        }

        return batches;
    }

    private async Task WriteBatchAsync(
        IFileSystemService fileSystem,
        LogFileTarget target,
        List<string> lines,
        long carriedLoss,
        CancellationToken cancellationToken)
    {
        StringBuilder text = new();
        foreach (string line in lines)
        {
            text.Append(line).Append(LogLineFormat.LineTerminator);
        }

        byte[] batch = Encoding.UTF8.GetBytes(text.ToString());
        try
        {
            await AppendAsync(fileSystem, target, batch, cancellationToken).ConfigureAwait(false);
            lock (_stateGate)
            {
                _failingTargets.Remove(target.Path.Value);
            }
        }
        catch (LogStorageFullException)
        {
            // ENOSPC: abandon the batch and count it; the warning line itself is not a lost log line.
            long lost = carriedLoss > 0 ? lines.Count - 1 + carriedLoss : lines.Count;
            Interlocked.Add(ref _lostToNoSpace, lost);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            NoteFailure(
                target.Path.Value,
                $"Could not write {target.Path.Value}: {exception.Message} ({lines.Count} lines lost).");
        }
    }

    /// <summary>
    /// Appends <paramref name="batch"/> to the target's active file: read, concatenate, then write
    /// with the revision that was read. A revision conflict (another writer, e.g. a truncation from
    /// Error Log Viewer) retries from a fresh read.
    /// </summary>
    private async Task AppendAsync(
        IFileSystemService fileSystem,
        LogFileTarget target,
        byte[] batch,
        CancellationToken cancellationToken)
    {
        const int MaxAttempts = 3;
        for (int attempt = 1; ; attempt++)
        {
            FileSystemAuthorizationContext context = CreateSystemContext();
            await EnsureDirectoryAsync(fileSystem, target.Directory, context, cancellationToken).ConfigureAwait(false);

            (long length, long revision) = await EnsureFileAsync(fileSystem, target, context, cancellationToken)
                .ConfigureAwait(false);
            if (LogRotationPolicy.ShouldRotate(length, batch.Length))
            {
                await RotateAsync(fileSystem, target, context, cancellationToken).ConfigureAwait(false);
                (length, revision) = await EnsureFileAsync(fileSystem, target, context, cancellationToken)
                    .ConfigureAwait(false);
            }

            byte[] content = batch;
            if (length > 0)
            {
                byte[] existing = await ReadAllBytesAsync(fileSystem, target.Path, context, cancellationToken)
                    .ConfigureAwait(false);
                content = new byte[existing.Length + batch.Length];
                existing.CopyTo(content, 0);
                batch.CopyTo(content, existing.Length);
            }

            FileSystemMutationResult write = await fileSystem.WriteAsync(
                new FileSystemWriteRequest(target.Path, revision),
                new LogContentSource(content),
                context,
                cancellationToken).ConfigureAwait(false);
            if (write.Succeeded)
            {
                return;
            }

            FileSystemErrorCode? code = write.Transaction.Error?.Code;
            if (code == FileSystemErrorCode.RevisionConflict && attempt < MaxAttempts)
            {
                continue;
            }

            throw code == FileSystemErrorCode.NoSpace
                ? new LogStorageFullException()
                : new InvalidOperationException($"write rejected ({code})");
        }
    }

    /// <summary>
    /// Rotates <paramref name="target"/> like <c>logrotate</c>: delete <c>.5</c>, shift <c>.4</c>
    /// to <c>.5</c> … <c>.1</c> to <c>.2</c>, move the active file to <c>.1</c>, then create an
    /// empty active file. Each step is one atomic engine move or delete.
    /// </summary>
    private static async Task RotateAsync(
        IFileSystemService fileSystem,
        LogFileTarget target,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken)
    {
        FileSystemEntrySnapshot? oldest = await TryStatAsync(
            fileSystem, target.ArchivePath(LogRotationPolicy.MaxArchives), context, cancellationToken).ConfigureAwait(false);
        if (oldest is not null)
        {
            long parentRevision = await GetRevisionAsync(fileSystem, target.Directory, context, cancellationToken)
                .ConfigureAwait(false);
            FileSystemMutationResult deleted = await fileSystem.DeleteAsync(
                new FileSystemDeleteRequest(oldest.Path, oldest.Metadata.Revision, parentRevision),
                context,
                cancellationToken).ConfigureAwait(false);
            EnsureSucceeded(deleted, "delete oldest archive");
        }

        for (int index = LogRotationPolicy.MaxArchives - 1; index >= 0; index--)
        {
            VirtualPath source = index == 0 ? target.Path : target.ArchivePath(index);
            FileSystemEntrySnapshot? entry = await TryStatAsync(fileSystem, source, context, cancellationToken)
                .ConfigureAwait(false);
            if (entry is null)
            {
                continue;
            }

            long parentRevision = await GetRevisionAsync(fileSystem, target.Directory, context, cancellationToken)
                .ConfigureAwait(false);
            FileSystemMutationResult moved = await fileSystem.MoveAsync(
                new FileSystemMoveRequest(
                    source,
                    target.ArchivePath(index + 1),
                    entry.Metadata.Revision,
                    parentRevision,
                    parentRevision),
                context,
                cancellationToken).ConfigureAwait(false);
            EnsureSucceeded(moved, $"rotate {source.Value}");
        }
    }

    /// <summary>Returns the active file's length and revision, creating it empty with the target mode if missing.</summary>
    private static async Task<(long Length, long Revision)> EnsureFileAsync(
        IFileSystemService fileSystem,
        LogFileTarget target,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken)
    {
        FileSystemEntrySnapshot? existing = await TryStatAsync(fileSystem, target.Path, context, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            FileSystemMutationResult created = await fileSystem.CreateAsync(
                new FileSystemCreateRequest(
                    target.Path,
                    FileSystemEntryKind.File,
                    FileSystemPermissions.FromMode(target.Mode)),
                context,
                cancellationToken).ConfigureAwait(false);
            if (!created.Succeeded && created.Transaction.Error?.Code != FileSystemErrorCode.AlreadyExists)
            {
                throw created.Transaction.Error?.Code == FileSystemErrorCode.NoSpace
                    ? new LogStorageFullException()
                    : new InvalidOperationException($"create rejected ({created.Transaction.Error?.Code})");
            }

            existing = created.Entry
                ?? await TryStatAsync(fileSystem, target.Path, context, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("log file vanished after creation");
        }

        if (existing.Metadata is not FileMetadata file)
        {
            throw new InvalidOperationException($"{target.Path.Value} is not a regular file");
        }

        return (file.Length, file.Revision);
    }

    /// <summary>Creates <paramref name="directory"/> and any missing ancestor as <c>system:system 0755</c>.</summary>
    private static async Task EnsureDirectoryAsync(
        IFileSystemService fileSystem,
        VirtualPath directory,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken)
    {
        FileSystemEntrySnapshot? existing = await TryStatAsync(fileSystem, directory, context, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return;
        }

        int separator = directory.Value.LastIndexOf('/');
        if (separator > 0)
        {
            await EnsureDirectoryAsync(
                fileSystem, VirtualPath.Parse(directory.Value[..separator]), context, cancellationToken).ConfigureAwait(false);
        }

        FileSystemMutationResult created = await fileSystem.CreateAsync(
            new FileSystemCreateRequest(directory, FileSystemEntryKind.Directory, FileSystemPermissions.FromMode(DirectoryMode)),
            context,
            cancellationToken).ConfigureAwait(false);
        if (!created.Succeeded && created.Transaction.Error?.Code != FileSystemErrorCode.AlreadyExists)
        {
            throw created.Transaction.Error?.Code == FileSystemErrorCode.NoSpace
                ? new LogStorageFullException()
                : new InvalidOperationException($"mkdir {directory.Value} rejected ({created.Transaction.Error?.Code})");
        }
    }

    private static async Task<byte[]> ReadAllBytesAsync(
        IFileSystemService fileSystem,
        VirtualPath path,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken)
    {
        FileSystemResult<FileSystemContentReadHandle> read = await fileSystem.ReadAsync(
            new FileSystemReadRequest(path), context, cancellationToken).ConfigureAwait(false);
        if (!read.Succeeded || read.Value is null)
        {
            throw new InvalidOperationException($"read rejected ({read.Error?.Code})");
        }

        await using FileSystemContentReadHandle handle = read.Value;
        using MemoryStream buffer = new();
        await handle.Content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static async Task<FileSystemEntrySnapshot?> TryStatAsync(
        IFileSystemService fileSystem,
        VirtualPath path,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken)
    {
        FileSystemResult<FileSystemEntrySnapshot> stat = await fileSystem.StatAsync(
            new FileSystemStatRequest(path, FileSystemLinkBehavior.NoFollow), context, cancellationToken).ConfigureAwait(false);
        if (stat.Succeeded)
        {
            return stat.Value;
        }

        return stat.Error?.Code == FileSystemErrorCode.NotFound
            ? null
            : throw new InvalidOperationException($"stat {path.Value} rejected ({stat.Error?.Code})");
    }

    private static async Task<long> GetRevisionAsync(
        IFileSystemService fileSystem,
        VirtualPath path,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken) =>
        (await TryStatAsync(fileSystem, path, context, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"{path.Value} not found")).Metadata.Revision;

    private static void EnsureSucceeded(FileSystemMutationResult result, string step)
    {
        if (!result.Succeeded)
        {
            throw result.Transaction.Error?.Code == FileSystemErrorCode.NoSpace
                ? new LogStorageFullException()
                : new InvalidOperationException($"{step} rejected ({result.Transaction.Error?.Code})");
        }
    }

    /// <summary>
    /// Builds the trusted kernel context the writer acts with: <c>system</c> user and group, every
    /// capability. Apps never get this context; they only reach <c>/var/log</c> through file rights.
    /// </summary>
    private FileSystemAuthorizationContext CreateSystemContext()
    {
        AppOperationContext operation = new()
        {
            AppId = KernelAppId,
            UserId = SystemPrincipal,
            UserAuthority = AppAuthority.System,
            GrantedCapabilities = new HashSet<string>(AppCapabilities.All, StringComparer.Ordinal),
            IsSystemOperation = true
        };
        return new FileSystemAuthorizationContext(operation, [SystemPrincipal], _clock.UtcNow.ToUniversalTime());
    }

    /// <summary>Notes a write failure in the in-memory buffer only, once per failing file.</summary>
    private void NoteFailure(string key, string message)
    {
        lock (_stateGate)
        {
            if (!_failingTargets.Add(key))
            {
                return;
            }
        }

        _memoryBuffer?.Record(new DiagnosticEntry(
            _clock.UtcNow,
            DiagnosticSeverity.Warning,
            LoggingCategory,
            message,
            Guid.NewGuid()));
    }

    private readonly record struct PendingLine(LogFileTarget Target, string Line);

    private sealed class LogContentSource(byte[] content) : IFileSystemContentSource
    {
        public FileSystemContentDescriptor Descriptor { get; } = FileSystemContentDescriptor.Text();

        public long? Length => content.LongLength;

        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(new MemoryStream(content, writable: false));
        }
    }

    /// <summary>
    /// One attached filesystem with its own flush gate, so a flush stuck on a filesystem that will
    /// never answer (a disconnected circuit) never blocks flushes to its replacement.
    /// </summary>
    private sealed class Attachment(IFileSystemService fileSystem)
    {
        private readonly CancellationTokenSource _abandoned = new();

        /// <summary>Gets the attached filesystem.</summary>
        public IFileSystemService FileSystem { get; } = fileSystem;

        /// <summary>Gets the gate allowing one flush at a time to this filesystem.</summary>
        public SemaphoreSlim FlushGate { get; } = new(1, 1);

        /// <summary>Gets a token cancelled once another filesystem replaces this one.</summary>
        public CancellationToken AbandonedToken => _abandoned.Token;

        /// <summary>Gets whether another filesystem replaced this one.</summary>
        public bool IsAbandoned => _abandoned.IsCancellationRequested;

        /// <summary>
        /// Cancels flushes still running against this filesystem. The token source is not disposed:
        /// a flush that ignores cancellation may still hold a registration on it.
        /// </summary>
        public void Abandon() => _abandoned.Cancel();
    }
}

/// <summary>A log append was refused with <see cref="FileSystemErrorCode.NoSpace"/>: the batch is counted as lost.</summary>
internal sealed class LogStorageFullException() : IOException("No space left on device");
