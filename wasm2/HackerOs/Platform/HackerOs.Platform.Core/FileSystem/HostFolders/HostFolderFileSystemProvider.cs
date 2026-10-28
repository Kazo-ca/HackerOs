using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using HackerOs.App.Abstractions;
using HackerOs.Simulation.Abstractions.FileSystem;

namespace HackerOs.Platform.Core.FileSystem.HostFolders;

/// <summary>
/// The <c>hostfs</c> provider (ADR 0181, section 2): one folder of the host computer, mounted as a
/// network drive. Every operation goes to the browser through <see cref="IHostFolder"/>; nothing is
/// stored in IndexedDB.
/// </summary>
/// <remarks>
/// <para>
/// The browser shows neither owners nor modes, so every entry belongs to the user who mounted the
/// drive, with <c>0700</c> for folders and <c>0600</c> for files; <c>chmod</c> is refused. There are
/// no symbolic links.
/// </para>
/// <para>
/// <b>Revisions.</b> A file's revision comes from its modification time and size, so a change made
/// on the host is seen at the next stat. A folder has no time in the browser: its revision is a
/// counter this provider raises on each change it makes inside the folder.
/// </para>
/// <para>
/// <b>Connection.</b> Until the browser grants its permission (after a reload, ADR 0181 section 8),
/// the drive is disconnected: its root still stats as an empty folder, everything else answers
/// <see cref="FileSystemErrorCode.ProviderUnavailable"/>.
/// </para>
/// </remarks>
public sealed class HostFolderFileSystemProvider : IFileSystemProvider
{
    /// <summary>Filesystem type of a host folder in <c>/proc/mounts</c> and <c>df -T</c>.</summary>
    public const string FileSystemType = "hostfs";

    /// <summary>Prefix of <see cref="ProviderId"/>; the mount path follows it.</summary>
    public const string ProviderIdPrefix = "hostfs:";

    private const ushort DirectoryMode = 0x1C0; // 0700
    private const ushort FileMode = 0x180; // 0600

    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly ConcurrentDictionary<string, long> _directoryRevisions = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly Func<Guid> _transactionIdFactory;
    private volatile bool _connected;

    /// <summary>Initializes the provider.</summary>
    /// <param name="mountPath">Canonical mount path, e.g. <c>/mnt/Projects</c>.</param>
    /// <param name="folder">The host folder.</param>
    /// <param name="ownerId">Login name of the user who mounted the drive.</param>
    /// <param name="groupId">That user's primary group.</param>
    /// <param name="connected">Whether the browser already granted its permission.</param>
    /// <param name="timeProvider">Clock of the mount time, the time of folders.</param>
    /// <param name="transactionIdFactory">Optional deterministic transaction ID source for tests.</param>
    public HostFolderFileSystemProvider(
        VirtualPath mountPath,
        IHostFolder folder,
        string ownerId,
        string groupId,
        bool connected,
        TimeProvider? timeProvider = null,
        Func<Guid>? transactionIdFactory = null)
    {
        if (string.IsNullOrEmpty(mountPath.Value) || mountPath.Value == "/")
        {
            throw new ArgumentException("A host folder needs a mount path below the root.", nameof(mountPath));
        }

        ArgumentNullException.ThrowIfNull(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        MountPath = mountPath;
        Folder = folder;
        OwnerId = ownerId;
        GroupId = groupId;
        _connected = connected;
        _time = timeProvider ?? TimeProvider.System;
        MountedAtUtc = _time.GetUtcNow().ToUniversalTime();
        _transactionIdFactory = transactionIdFactory ?? Guid.NewGuid;
    }

    /// <inheritdoc />
    public string ProviderId => ProviderIdPrefix + MountPath.Value;

    /// <summary>Gets the mount path.</summary>
    public VirtualPath MountPath { get; }

    /// <summary>Gets the host folder.</summary>
    public IHostFolder Folder { get; }

    /// <summary>Gets the login name of the user who mounted the drive.</summary>
    public string OwnerId { get; }

    /// <summary>Gets that user's primary group.</summary>
    public string GroupId { get; }

    /// <summary>Gets when the drive was mounted, the time every folder reports.</summary>
    public DateTimeOffset MountedAtUtc { get; }

    /// <summary>Gets whether the browser's permission is granted.</summary>
    public bool IsConnected => _connected;

    /// <summary>Marks the drive connected (permission granted) or disconnected.</summary>
    public void SetConnected(bool connected) => _connected = connected;

    /// <inheritdoc />
    public async ValueTask<FileSystemResult<FileSystemContentReadHandle>> ReadAsync(
        FileSystemReadRequest request,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            string[] segments = Segments(request.Path);
            HostFolderEntry entry = await Folder.StatAsync(segments, cancellationToken).ConfigureAwait(false);
            if (entry.Kind != HostFolderEntryKind.File)
            {
                return Failure<FileSystemContentReadHandle>(FileSystemOperation.Read, FileSystemErrorCode.NotFile, request.Path);
            }

            byte[] content = await Folder.ReadAsync(segments, cancellationToken).ConfigureAwait(false);
            return FileSystemResult<FileSystemContentReadHandle>.Success(new FileSystemContentReadHandle(
                new FileSystemEntrySnapshot(request.Path, Metadata(request.Path, entry)),
                FileSystemContentDescriptor.Binary(),
                new MemoryStream(content, writable: false)));
        }
        catch (Exception exception) when (Map(exception, cancellationToken) is { } code)
        {
            return Failure<FileSystemContentReadHandle>(FileSystemOperation.Read, code, request.Path);
        }
    }

    /// <inheritdoc />
    public async ValueTask<FileSystemResult<FileSystemDirectorySnapshot>> EnumerateAsync(
        FileSystemEnumerateRequest request,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            string[] segments = Segments(request.Path);
            HostFolderEntry folder = await Folder.StatAsync(segments, cancellationToken).ConfigureAwait(false);
            if (folder.Kind != HostFolderEntryKind.Directory)
            {
                return Failure<FileSystemDirectorySnapshot>(FileSystemOperation.Enumerate, FileSystemErrorCode.NotDirectory, request.Path);
            }

            IReadOnlyList<HostFolderEntry> children = await Folder.ListAsync(segments, cancellationToken).ConfigureAwait(false);
            List<FileSystemDirectoryItem> items = [];
            foreach (HostFolderEntry child in children)
            {
                // A host name HackerOS cannot represent (a control character) is left out.
                if (TryParseName(child.Name) is { } name)
                {
                    items.Add(new FileSystemDirectoryItem(name, Metadata(Child(request.Path, name.Value), child)));
                }
            }

            items.Sort(static (left, right) => string.CompareOrdinal(left.Name.Value, right.Name.Value));
            FileSystemDirectoryItem[] unique =
            [
                .. items.Where((item, index) => index == 0 || items[index - 1].Name.Value != item.Name.Value)
            ];
            return FileSystemResult<FileSystemDirectorySnapshot>.Success(
                new FileSystemDirectorySnapshot(request.Path, DirectoryRevision(request.Path), unique));
        }
        catch (Exception exception) when (Map(exception, cancellationToken) is { } code)
        {
            return Failure<FileSystemDirectorySnapshot>(FileSystemOperation.Enumerate, code, request.Path);
        }
    }

    /// <inheritdoc />
    public async ValueTask<FileSystemResult<FileSystemEntrySnapshot>> StatAsync(
        FileSystemStatRequest request,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Path == MountPath && !_connected)
        {
            // A disconnected drive still shows as a folder, so the explorer can offer to reconnect it.
            return FileSystemResult<FileSystemEntrySnapshot>.Success(
                new FileSystemEntrySnapshot(request.Path, DirectoryMetadata(request.Path)));
        }

        try
        {
            HostFolderEntry entry = await Folder.StatAsync(Segments(request.Path), cancellationToken).ConfigureAwait(false);
            return FileSystemResult<FileSystemEntrySnapshot>.Success(
                new FileSystemEntrySnapshot(request.Path, Metadata(request.Path, entry)));
        }
        catch (Exception exception) when (Map(exception, cancellationToken) is { } code)
        {
            return Failure<FileSystemEntrySnapshot>(FileSystemOperation.Stat, code, request.Path);
        }
    }

    /// <inheritdoc />
    public async ValueTask<FileSystemMutationResult> CreateAsync(
        FileSystemCreateRequest request,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Kind is not (FileSystemEntryKind.File or FileSystemEntryKind.Directory))
        {
            return Rejected(FileSystemOperation.Create, FileSystemErrorCode.UnsupportedOperation, request.Path);
        }

        if (request.Path == MountPath)
        {
            return Rejected(FileSystemOperation.Create, FileSystemErrorCode.AlreadyExists, request.Path);
        }

        VirtualPath parent = Parent(request.Path);
        try
        {
            if (await CheckDirectoryRevisionAsync(parent, request.ExpectedParentRevision, cancellationToken).ConfigureAwait(false) is { } conflict)
            {
                return Rejected(FileSystemOperation.Create, conflict, request.Path);
            }

            string[] segments = Segments(request.Path);
            HostFolderEntryKind kind = request.Kind == FileSystemEntryKind.File ? HostFolderEntryKind.File : HostFolderEntryKind.Directory;
            await Folder.CreateAsync(segments, kind, cancellationToken).ConfigureAwait(false);
            Touch(parent);
            return await CommittedAsync(request.Path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (Map(exception, cancellationToken) is { } code)
        {
            return Rejected(FileSystemOperation.Create, code, request.Path);
        }
    }

    /// <inheritdoc />
    public async ValueTask<FileSystemMutationResult> WriteAsync(
        FileSystemWriteRequest request,
        IFileSystemContentSource content,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(content);
        try
        {
            string[] segments = Segments(request.Path);
            HostFolderEntry entry = await Folder.StatAsync(segments, cancellationToken).ConfigureAwait(false);
            if (entry.Kind != HostFolderEntryKind.File)
            {
                return Rejected(FileSystemOperation.Write, FileSystemErrorCode.NotFile, request.Path);
            }

            if (request.ExpectedRevision is { } expected && expected != FileRevision(entry))
            {
                return Rejected(FileSystemOperation.Write, FileSystemErrorCode.RevisionConflict, request.Path);
            }

            byte[] bytes;
            await using (Stream source = await content.OpenReadAsync(cancellationToken).ConfigureAwait(false))
            {
                using MemoryStream buffer = new();
                await source.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                bytes = buffer.ToArray();
            }

            await Folder.WriteAsync(segments, bytes, cancellationToken).ConfigureAwait(false);
            return await CommittedAsync(request.Path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (Map(exception, cancellationToken) is { } code)
        {
            return Rejected(FileSystemOperation.Write, code, request.Path);
        }
    }

    /// <inheritdoc />
    public async ValueTask<FileSystemMutationResult> MoveAsync(
        FileSystemMoveRequest request,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SourcePath == MountPath || request.DestinationPath == MountPath)
        {
            return Rejected(FileSystemOperation.Move, FileSystemErrorCode.ProtectedEntry, request.SourcePath, request.DestinationPath);
        }

        if (request.DestinationPath.Value.StartsWith($"{request.SourcePath.Value}/", StringComparison.Ordinal))
        {
            return Rejected(FileSystemOperation.Move, FileSystemErrorCode.InvalidPath, request.SourcePath, request.DestinationPath);
        }

        VirtualPath sourceParent = Parent(request.SourcePath);
        VirtualPath destinationParent = Parent(request.DestinationPath);
        try
        {
            string[] source = Segments(request.SourcePath);
            string[] destination = Segments(request.DestinationPath);
            HostFolderEntry entry = await Folder.StatAsync(source, cancellationToken).ConfigureAwait(false);
            FileSystemErrorCode? conflict = Revision(request.SourcePath, entry) != request.ExpectedEntryRevision
                ? FileSystemErrorCode.RevisionConflict
                : await CheckDirectoryRevisionAsync(sourceParent, request.ExpectedSourceParentRevision, cancellationToken).ConfigureAwait(false)
                    ?? await CheckDirectoryRevisionAsync(destinationParent, request.ExpectedDestinationParentRevision, cancellationToken).ConfigureAwait(false);
            if (conflict is { } code)
            {
                return Rejected(FileSystemOperation.Move, code, request.SourcePath, request.DestinationPath);
            }

            if (await ExistsAsync(destination, cancellationToken).ConfigureAwait(false))
            {
                return Rejected(FileSystemOperation.Move, FileSystemErrorCode.AlreadyExists, request.SourcePath, request.DestinationPath);
            }

            if (!await Folder.TryMoveAsync(source, destination, cancellationToken).ConfigureAwait(false))
            {
                await CopyTreeAsync(source, destination, entry.Kind, cancellationToken).ConfigureAwait(false);
                await Folder.DeleteAsync(source, recursive: true, cancellationToken).ConfigureAwait(false);
            }

            ForgetDirectories(request.SourcePath);
            Touch(sourceParent);
            Touch(destinationParent);
            return await CommittedAsync(request.DestinationPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (Map(exception, cancellationToken) is { } code)
        {
            return Rejected(FileSystemOperation.Move, code, request.SourcePath, request.DestinationPath);
        }
    }

    /// <inheritdoc />
    public async ValueTask<FileSystemMutationResult> CopyAsync(
        FileSystemCopyRequest request,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.DestinationPath == MountPath
            || request.DestinationPath.Value.StartsWith($"{request.SourcePath.Value}/", StringComparison.Ordinal))
        {
            return Rejected(FileSystemOperation.Copy, FileSystemErrorCode.InvalidPath, request.SourcePath, request.DestinationPath);
        }

        VirtualPath destinationParent = Parent(request.DestinationPath);
        try
        {
            string[] source = Segments(request.SourcePath);
            string[] destination = Segments(request.DestinationPath);
            HostFolderEntry entry = await Folder.StatAsync(source, cancellationToken).ConfigureAwait(false);
            FileSystemErrorCode? conflict = Revision(request.SourcePath, entry) != request.ExpectedEntryRevision
                ? FileSystemErrorCode.RevisionConflict
                : await CheckDirectoryRevisionAsync(destinationParent, request.ExpectedDestinationParentRevision, cancellationToken).ConfigureAwait(false);
            if (conflict is { } code)
            {
                return Rejected(FileSystemOperation.Copy, code, request.SourcePath, request.DestinationPath);
            }

            if (await ExistsAsync(destination, cancellationToken).ConfigureAwait(false))
            {
                return Rejected(FileSystemOperation.Copy, FileSystemErrorCode.AlreadyExists, request.SourcePath, request.DestinationPath);
            }

            await CopyTreeAsync(source, destination, entry.Kind, cancellationToken).ConfigureAwait(false);
            Touch(destinationParent);
            return await CommittedAsync(request.DestinationPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (Map(exception, cancellationToken) is { } code)
        {
            return Rejected(FileSystemOperation.Copy, code, request.SourcePath, request.DestinationPath);
        }
    }

    /// <inheritdoc />
    public async ValueTask<FileSystemMutationResult> DeleteAsync(
        FileSystemDeleteRequest request,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Path == MountPath)
        {
            // The root of the drive goes away with umount, never with rm.
            return Rejected(FileSystemOperation.Delete, FileSystemErrorCode.ProtectedEntry, request.Path);
        }

        VirtualPath parent = Parent(request.Path);
        try
        {
            string[] segments = Segments(request.Path);
            HostFolderEntry entry = await Folder.StatAsync(segments, cancellationToken).ConfigureAwait(false);
            FileSystemErrorCode? conflict = Revision(request.Path, entry) != request.ExpectedEntryRevision
                ? FileSystemErrorCode.RevisionConflict
                : await CheckDirectoryRevisionAsync(parent, request.ExpectedParentRevision, cancellationToken).ConfigureAwait(false);
            if (conflict is { } code)
            {
                return Rejected(FileSystemOperation.Delete, code, request.Path);
            }

            if (entry.Kind == HostFolderEntryKind.Directory && !request.Recursive
                && (await Folder.ListAsync(segments, cancellationToken).ConfigureAwait(false)).Count > 0)
            {
                return Rejected(FileSystemOperation.Delete, FileSystemErrorCode.DirectoryNotEmpty, request.Path);
            }

            await Folder.DeleteAsync(segments, request.Recursive, cancellationToken).ConfigureAwait(false);
            ForgetDirectories(request.Path);
            Touch(parent);
            FileSystemEntryId id = StableId(request.Path);
            return new FileSystemMutationResult(FileSystemTransactionResult.Committed(NextTransactionId(), [id]));
        }
        catch (Exception exception) when (Map(exception, cancellationToken) is { } code)
        {
            return Rejected(FileSystemOperation.Delete, code, request.Path);
        }
    }

    /// <summary>Refused: the browser cannot change the modes of host files (ADR 0181, section 2).</summary>
    public ValueTask<FileSystemMutationResult> SetPermissionsAsync(
        FileSystemSetPermissionsRequest request,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ValueTask.FromResult(Rejected(FileSystemOperation.SetPermissions, FileSystemErrorCode.UnsupportedOperation, request.Path));
    }

    private async ValueTask CopyTreeAsync(
        string[] source,
        string[] destination,
        HostFolderEntryKind kind,
        CancellationToken cancellationToken)
    {
        if (kind == HostFolderEntryKind.File)
        {
            byte[] content = await Folder.ReadAsync(source, cancellationToken).ConfigureAwait(false);
            await Folder.CreateAsync(destination, HostFolderEntryKind.File, cancellationToken).ConfigureAwait(false);
            await Folder.WriteAsync(destination, content, cancellationToken).ConfigureAwait(false);
            return;
        }

        await Folder.CreateAsync(destination, HostFolderEntryKind.Directory, cancellationToken).ConfigureAwait(false);
        foreach (HostFolderEntry child in await Folder.ListAsync(source, cancellationToken).ConfigureAwait(false))
        {
            await CopyTreeAsync([.. source, child.Name], [.. destination, child.Name], child.Kind, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask<bool> ExistsAsync(string[] segments, CancellationToken cancellationToken)
    {
        try
        {
            await Folder.StatAsync(segments, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (HostFolderException exception) when (exception.Kind == HostFolderErrorKind.NotFound)
        {
            return false;
        }
    }

    private async ValueTask<FileSystemErrorCode?> CheckDirectoryRevisionAsync(
        VirtualPath directory,
        long? expected,
        CancellationToken cancellationToken)
    {
        HostFolderEntry entry = await Folder.StatAsync(Segments(directory), cancellationToken).ConfigureAwait(false);
        if (entry.Kind != HostFolderEntryKind.Directory)
        {
            return FileSystemErrorCode.NotDirectory;
        }

        return expected is { } revision && revision != DirectoryRevision(directory)
            ? FileSystemErrorCode.RevisionConflict
            : null;
    }

    private async ValueTask<FileSystemMutationResult> CommittedAsync(VirtualPath path, CancellationToken cancellationToken)
    {
        HostFolderEntry entry = await Folder.StatAsync(Segments(path), cancellationToken).ConfigureAwait(false);
        FileSystemEntrySnapshot snapshot = new(path, Metadata(path, entry));
        return new FileSystemMutationResult(
            FileSystemTransactionResult.Committed(NextTransactionId(), [snapshot.Metadata.Id]),
            snapshot);
    }

    private FileSystemEntryMetadata Metadata(VirtualPath path, HostFolderEntry entry)
    {
        if (entry.Kind == HostFolderEntryKind.Directory)
        {
            return DirectoryMetadata(path);
        }

        DateTimeOffset modified = (entry.LastModifiedUtc ?? MountedAtUtc).ToUniversalTime();
        return new FileMetadata(
            StableId(path),
            OwnerId,
            GroupId,
            FileSystemPermissions.FromMode(FileMode),
            new FileSystemTimestamps(modified, modified, modified),
            FileRevision(entry),
            entry.Length);
    }

    private DirectoryMetadata DirectoryMetadata(VirtualPath path) => new(
        StableId(path),
        OwnerId,
        GroupId,
        FileSystemPermissions.FromMode(DirectoryMode),
        new FileSystemTimestamps(MountedAtUtc, MountedAtUtc, MountedAtUtc),
        DirectoryRevision(path));

    private long Revision(VirtualPath path, HostFolderEntry entry) =>
        entry.Kind == HostFolderEntryKind.Directory ? DirectoryRevision(path) : FileRevision(entry);

    /// <summary>
    /// Revision of a host file: its modification time in milliseconds, times 1000, plus the last
    /// three digits of its size, so a change of either is seen. Always at least 1.
    /// </summary>
    private static long FileRevision(HostFolderEntry entry)
    {
        long milliseconds = Math.Max(0, entry.LastModifiedUtc?.ToUnixTimeMilliseconds() ?? 0);
        return Math.Max(1, (milliseconds * 1000) + (entry.Length % 1000));
    }

    private long DirectoryRevision(VirtualPath path) =>
        _directoryRevisions.TryGetValue(path.Value, out long revision) ? revision : 1;

    private void Touch(VirtualPath directory) =>
        _directoryRevisions.AddOrUpdate(directory.Value, 2, static (_, revision) => revision + 1);

    /// <summary>Drops the counters of a removed folder and its subfolders; a new folder there starts over.</summary>
    private void ForgetDirectories(VirtualPath removed)
    {
        foreach (string key in _directoryRevisions.Keys)
        {
            if (key == removed.Value || key.StartsWith($"{removed.Value}/", StringComparison.Ordinal))
            {
                _directoryRevisions.TryRemove(key, out _);
            }
        }
    }

    /// <summary>
    /// Path relative to the mount, split into segments; also the connection gate, because every
    /// operation that reaches the browser starts here.
    /// </summary>
    private string[] Segments(VirtualPath path)
    {
        if (!_connected)
        {
            throw new HostFolderException(HostFolderErrorKind.NotAllowed, "The network drive is disconnected.");
        }

        if (path == MountPath)
        {
            return [];
        }

        return path.Value.StartsWith($"{MountPath.Value}/", StringComparison.Ordinal)
            ? path.Value[(MountPath.Value.Length + 1)..].Split('/')
            : throw new ArgumentException($"'{path}' is outside the '{MountPath}' mount.", nameof(path));
    }

    /// <summary>
    /// Maps a failure to its error code. A refusal disconnects the drive only when the browser's
    /// permission is gone; otherwise the browser refused this one entry (a blocked name, say).
    /// </summary>
    private FileSystemErrorCode? Map(Exception exception, CancellationToken cancellationToken)
    {
        switch (exception)
        {
            case OperationCanceledException when cancellationToken.IsCancellationRequested:
                return FileSystemErrorCode.Cancelled;
            case HostFolderException { Kind: HostFolderErrorKind.NotAllowed }:
                if (_connected)
                {
                    CheckPermission();
                }

                return _connected ? FileSystemErrorCode.PermissionDenied : FileSystemErrorCode.ProviderUnavailable;
            case HostFolderException host:
                return host.Kind switch
                {
                    HostFolderErrorKind.NotFound => FileSystemErrorCode.NotFound,
                    HostFolderErrorKind.AlreadyExists => FileSystemErrorCode.AlreadyExists,
                    HostFolderErrorKind.TypeMismatch => FileSystemErrorCode.NotDirectory,
                    HostFolderErrorKind.NotEmpty => FileSystemErrorCode.DirectoryNotEmpty,
                    HostFolderErrorKind.NoSpace => FileSystemErrorCode.NoSpace,
                    HostFolderErrorKind.InvalidName => FileSystemErrorCode.InvalidName,
                    _ => FileSystemErrorCode.ProviderFailure
                };
            case IOException or InvalidOperationException or ObjectDisposedException:
                return FileSystemErrorCode.ProviderFailure;
            default:
                return null;
        }
    }

    /// <summary>
    /// Asks the browser whether its permission still stands and disconnects the drive when it does
    /// not; synchronously when the answer is already there, so the failing call already reports it.
    /// </summary>
    private void CheckPermission()
    {
        ValueTask<HostFolderPermission> query;
        try
        {
            query = Folder.QueryPermissionAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
            return;
        }

        if (query.IsCompletedSuccessfully)
        {
            Apply(query.Result);
            return;
        }

        _ = ApplyLaterAsync(query);

        async Task ApplyLaterAsync(ValueTask<HostFolderPermission> pending)
        {
            try
            {
                Apply(await pending.ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
            {
                // The next operation asks again.
            }
        }

        void Apply(HostFolderPermission permission)
        {
            if (permission != HostFolderPermission.Granted)
            {
                _connected = false;
            }
        }
    }

    private static FileSystemEntryName? TryParseName(string value)
    {
        try
        {
            return FileSystemEntryName.Parse(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private FileSystemEntryId StableId(VirtualPath path)
    {
        byte[] hash = SHA256.HashData(Utf8.GetBytes($"{ProviderId}:{path.Value}"));
        return FileSystemEntryId.FromGuid(new Guid(hash.AsSpan(0, 16)));
    }

    private static VirtualPath Parent(VirtualPath path)
    {
        int separator = path.Value.LastIndexOf('/');
        return separator <= 0 ? VirtualPath.Parse("/") : VirtualPath.Parse(path.Value[..separator]);
    }

    private static VirtualPath Child(VirtualPath parent, string name) =>
        VirtualPath.Parse(parent.Value == "/" ? $"/{name}" : $"{parent.Value}/{name}");

    private static FileSystemResult<T> Failure<T>(FileSystemOperation operation, FileSystemErrorCode code, VirtualPath path)
        where T : notnull =>
        FileSystemResult<T>.Failure(new FileSystemError(operation, code, path));

    private FileSystemMutationResult Rejected(
        FileSystemOperation operation,
        FileSystemErrorCode code,
        VirtualPath path,
        VirtualPath? relatedPath = null)
    {
        FileSystemError error = new(operation, code, path, relatedPath);
        FileSystemTransactionResult transaction = code == FileSystemErrorCode.Cancelled
            ? FileSystemTransactionResult.Cancelled(NextTransactionId(), error)
            : FileSystemTransactionResult.Rejected(NextTransactionId(), error);
        return new FileSystemMutationResult(transaction);
    }

    private Guid NextTransactionId()
    {
        Guid id = _transactionIdFactory();
        return id == Guid.Empty
            ? throw new InvalidOperationException("The transaction ID factory returned an empty ID.")
            : id;
    }
}
