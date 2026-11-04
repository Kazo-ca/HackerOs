using System.Diagnostics.CodeAnalysis;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.Platform.Core.FileSystem;

/// <summary>
/// Stores filesystem identity, directory links, metadata, and content in memory.
/// </summary>
public sealed class InMemoryFileSystemRepository : IFileSystemProvider
{
    private readonly object _gate = new();
    private readonly Dictionary<FileSystemEntryId, EntryRecord> _entries = [];
    private readonly Dictionary<string, FileSystemEntryId> _paths = new(StringComparer.Ordinal);
    private readonly Func<FileSystemEntryId> _entryIdFactory;
    private readonly Func<Guid> _transactionIdFactory;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes an empty repository containing only `/`.</summary>
    public InMemoryFileSystemRepository(
        Func<FileSystemEntryId>? entryIdFactory = null,
        Func<Guid>? transactionIdFactory = null,
        TimeProvider? timeProvider = null)
    {
        _entryIdFactory = entryIdFactory ?? (() => FileSystemEntryId.FromGuid(Guid.NewGuid()));
        _transactionIdFactory = transactionIdFactory ?? Guid.NewGuid;
        _timeProvider = timeProvider ?? TimeProvider.System;

        FileSystemEntryId rootId = NextEntryId();
        DateTimeOffset now = GetUtcNow();
        _entries.Add(rootId, EntryRecord.Directory(
            rootId,
            "system",
            "system",
            FileSystemPermissions.FromMode(0x01ED),
            now));
        _paths.Add("/", rootId);
    }

    /// <inheritdoc />
    public string ProviderId => "memory";

    /// <inheritdoc />
    public ValueTask<FileSystemResult<FileSystemContentReadHandle>> ReadAsync(
        FileSystemReadRequest request,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(ReadFailure(FileSystemErrorCode.Cancelled, request.Path));
        }

        lock (_gate)
        {
            if (!TryGetRecord(request.Path, out EntryRecord? record))
            {
                return ValueTask.FromResult(ReadFailure(FileSystemErrorCode.NotFound, request.Path));
            }

            if (record.Kind != FileSystemEntryKind.File)
            {
                return ValueTask.FromResult(ReadFailure(FileSystemErrorCode.NotFile, request.Path));
            }

            byte[] content = record.Content.ToArray();
            FileSystemEntrySnapshot snapshot = Snapshot(request.Path, record);
            FileSystemContentReadHandle handle = new(
                snapshot,
                record.ContentDescriptor,
                new MemoryStream(content, writable: false));
            return ValueTask.FromResult(FileSystemResult<FileSystemContentReadHandle>.Success(handle));
        }
    }

    /// <inheritdoc />
    public ValueTask<FileSystemResult<FileSystemDirectorySnapshot>> EnumerateAsync(
        FileSystemEnumerateRequest request,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(DirectoryFailure(FileSystemErrorCode.Cancelled, request.Path));
        }

        lock (_gate)
        {
            if (!TryGetRecord(request.Path, out EntryRecord? directory))
            {
                return ValueTask.FromResult(DirectoryFailure(FileSystemErrorCode.NotFound, request.Path));
            }

            if (directory.Kind != FileSystemEntryKind.Directory)
            {
                return ValueTask.FromResult(DirectoryFailure(FileSystemErrorCode.NotDirectory, request.Path));
            }

            string prefix = request.Path.Value == "/" ? "/" : $"{request.Path.Value}/";
            FileSystemDirectoryItem[] children = _paths
                .Where(pair => pair.Key != request.Path.Value
                    && pair.Key.StartsWith(prefix, StringComparison.Ordinal)
                    && !pair.Key[prefix.Length..].Contains('/'))
                .Select(pair => new FileSystemDirectoryItem(
                    FileSystemEntryName.Parse(pair.Key[prefix.Length..]),
                    _entries[pair.Value].ToMetadata()))
                .OrderBy(static item => item.Name.Value, StringComparer.Ordinal)
                .ToArray();

            return ValueTask.FromResult(FileSystemResult<FileSystemDirectorySnapshot>.Success(
                new FileSystemDirectorySnapshot(request.Path, directory.Revision, children)));
        }
    }

    /// <inheritdoc />
    public ValueTask<FileSystemMutationResult> CreateAsync(
        FileSystemCreateRequest request,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        lock (_gate)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromResult(MutationFailure(
                    FileSystemOperation.Create,
                    FileSystemErrorCode.Cancelled,
                    request.Path));
            }

            // Character devices exist only in virtual providers (/dev, ADR 0046); a persistent
            // store never holds one, matching the IndexedDB provider.
            if (request.Kind == FileSystemEntryKind.CharacterDevice)
            {
                return ValueTask.FromResult(MutationFailure(
                    FileSystemOperation.Create,
                    FileSystemErrorCode.UnsupportedOperation,
                    request.Path));
            }

            if (_paths.ContainsKey(request.Path.Value))
            {
                return ValueTask.FromResult(MutationFailure(
                    FileSystemOperation.Create,
                    FileSystemErrorCode.AlreadyExists,
                    request.Path));
            }

            VirtualPath parentPath = GetParent(request.Path);
            if (!TryGetRecord(parentPath, out EntryRecord? parent))
            {
                return ValueTask.FromResult(MutationFailure(
                    FileSystemOperation.Create,
                    FileSystemErrorCode.NotFound,
                    parentPath));
            }

            if (parent.Kind != FileSystemEntryKind.Directory)
            {
                return ValueTask.FromResult(MutationFailure(
                    FileSystemOperation.Create,
                    FileSystemErrorCode.NotDirectory,
                    parentPath));
            }

            if (request.ExpectedParentRevision is { } expectedParentRevision && parent.Revision != expectedParentRevision)
            {
                return ValueTask.FromResult(MutationFailure(
                    FileSystemOperation.Create,
                    FileSystemErrorCode.RevisionConflict,
                    parentPath));
            }

            DateTimeOffset now = GetUtcNow();
            FileSystemEntryId id = NextEntryId();
            string ownerId = context.OperationContext.UserId;
            string groupId = context.GroupIds.Order(StringComparer.Ordinal).FirstOrDefault() ?? ownerId;
            EntryRecord created = request.Kind switch
            {
                FileSystemEntryKind.File => EntryRecord.File(id, ownerId, groupId, request.Permissions, now),
                FileSystemEntryKind.Directory => EntryRecord.Directory(id, ownerId, groupId, request.Permissions, now),
                FileSystemEntryKind.SymbolicLink => EntryRecord.Link(
                    id,
                    ownerId,
                    groupId,
                    request.Permissions,
                    now,
                    request.SymbolicLinkTarget!),
                _ => throw new ArgumentOutOfRangeException(nameof(request))
            };

            _entries.Add(id, created);
            _paths.Add(request.Path.Value, id);
            parent.TouchContent(now);
            return ValueTask.FromResult(MutationSuccess(FileSystemOperation.Create, request.Path, created));
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
        ArgumentNullException.ThrowIfNull(context);

        byte[] candidate;
        try
        {
            await using Stream source = await content.OpenReadAsync(cancellationToken).ConfigureAwait(false);
            if (!source.CanRead)
            {
                return MutationFailure(
                    FileSystemOperation.Write,
                    FileSystemErrorCode.ProviderFailure,
                    request.Path);
            }

            using MemoryStream buffer = new();
            await source.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            candidate = buffer.ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return MutationFailure(FileSystemOperation.Write, FileSystemErrorCode.Cancelled, request.Path);
        }

        lock (_gate)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return MutationFailure(FileSystemOperation.Write, FileSystemErrorCode.Cancelled, request.Path);
            }

            if (!TryGetRecord(request.Path, out EntryRecord? record))
            {
                return MutationFailure(FileSystemOperation.Write, FileSystemErrorCode.NotFound, request.Path);
            }

            if (record.Kind != FileSystemEntryKind.File)
            {
                return MutationFailure(FileSystemOperation.Write, FileSystemErrorCode.NotFile, request.Path);
            }

            if (request.ExpectedRevision is { } expectedRevision && record.Revision != expectedRevision)
            {
                return MutationFailure(FileSystemOperation.Write, FileSystemErrorCode.RevisionConflict, request.Path);
            }

            record.Content = candidate;
            record.ContentDescriptor = content.Descriptor;
            record.TouchContent(GetUtcNow());
            return MutationSuccess(FileSystemOperation.Write, request.Path, record);
        }
    }

    /// <inheritdoc />
    public ValueTask<FileSystemMutationResult> MoveAsync(
        FileSystemMoveRequest request,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        lock (_gate)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromResult(MutationFailure(
                    FileSystemOperation.Move,
                    FileSystemErrorCode.Cancelled,
                    request.SourcePath,
                    request.DestinationPath));
            }

            FileSystemMutationResult? validation = ValidateTransfer(
                FileSystemOperation.Move,
                request.SourcePath,
                request.DestinationPath,
                request.ExpectedEntryRevision,
                request.ExpectedSourceParentRevision,
                request.ExpectedDestinationParentRevision,
                out EntryRecord? source,
                out EntryRecord? sourceParent,
                out EntryRecord? destinationParent);
            if (validation is not null)
            {
                return ValueTask.FromResult(validation);
            }

            EntryRecord sourceRecord = source!;
            EntryRecord sourceParentRecord = sourceParent!;
            EntryRecord destinationParentRecord = destinationParent!;
            if (sourceRecord.Kind == FileSystemEntryKind.Directory
                && IsWithin(request.DestinationPath, request.SourcePath))
            {
                return ValueTask.FromResult(MutationFailure(
                    FileSystemOperation.Move,
                    FileSystemErrorCode.InvalidPath,
                    request.SourcePath,
                    request.DestinationPath));
            }

            (string OldPath, string NewPath, FileSystemEntryId Id)[] changes = _paths
                .Where(pair => IsWithin(VirtualPath.Parse(pair.Key), request.SourcePath))
                .Select(pair => (
                    OldPath: pair.Key,
                    NewPath: $"{request.DestinationPath.Value}{pair.Key[request.SourcePath.Value.Length..]}",
                    Id: pair.Value))
                .OrderBy(static change => change.OldPath.Length)
                .ToArray();
            if (changes.Any(change => _paths.ContainsKey(change.NewPath)))
            {
                return ValueTask.FromResult(MutationFailure(
                    FileSystemOperation.Move,
                    FileSystemErrorCode.AlreadyExists,
                    request.SourcePath,
                    request.DestinationPath));
            }

            foreach ((string oldPath, _, _) in changes)
            {
                _paths.Remove(oldPath);
            }

            foreach ((_, string newPath, FileSystemEntryId id) in changes)
            {
                _paths.Add(newPath, id);
            }

            DateTimeOffset now = GetUtcNow();
            sourceRecord.TouchMetadata(now);
            TouchParents(sourceParentRecord, destinationParentRecord, now);
            return ValueTask.FromResult(MutationSuccess(
                FileSystemOperation.Move,
                request.DestinationPath,
                sourceRecord));
        }
    }

    /// <inheritdoc />
    public ValueTask<FileSystemMutationResult> CopyAsync(
        FileSystemCopyRequest request,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        lock (_gate)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromResult(MutationFailure(
                    FileSystemOperation.Copy,
                    FileSystemErrorCode.Cancelled,
                    request.SourcePath,
                    request.DestinationPath));
            }

            if (!TryGetRecord(request.SourcePath, out EntryRecord? source))
            {
                return ValueTask.FromResult(MutationFailure(
                    FileSystemOperation.Copy,
                    FileSystemErrorCode.NotFound,
                    request.SourcePath));
            }

            if (source.Revision != request.ExpectedEntryRevision)
            {
                return ValueTask.FromResult(MutationFailure(
                    FileSystemOperation.Copy,
                    FileSystemErrorCode.RevisionConflict,
                    request.SourcePath));
            }

            if (_paths.ContainsKey(request.DestinationPath.Value))
            {
                return ValueTask.FromResult(MutationFailure(
                    FileSystemOperation.Copy,
                    FileSystemErrorCode.AlreadyExists,
                    request.DestinationPath));
            }

            VirtualPath destinationParentPath = GetParent(request.DestinationPath);
            if (!TryGetRecord(destinationParentPath, out EntryRecord? destinationParent)
                || destinationParent.Kind != FileSystemEntryKind.Directory)
            {
                return ValueTask.FromResult(MutationFailure(
                    FileSystemOperation.Copy,
                    FileSystemErrorCode.NotDirectory,
                    destinationParentPath));
            }

            if (destinationParent.Revision != request.ExpectedDestinationParentRevision)
            {
                return ValueTask.FromResult(MutationFailure(
                    FileSystemOperation.Copy,
                    FileSystemErrorCode.RevisionConflict,
                    destinationParentPath));
            }

            if (source.Kind == FileSystemEntryKind.Directory
                && IsWithin(request.DestinationPath, request.SourcePath))
            {
                return ValueTask.FromResult(MutationFailure(
                    FileSystemOperation.Copy,
                    FileSystemErrorCode.InvalidPath,
                    request.SourcePath,
                    request.DestinationPath));
            }

            DateTimeOffset now = GetUtcNow();
            (string Path, EntryRecord Record)[] copies = _paths
                .Where(pair => IsWithin(VirtualPath.Parse(pair.Key), request.SourcePath))
                .OrderBy(static pair => pair.Key.Length)
                .Select(pair => (
                    $"{request.DestinationPath.Value}{pair.Key[request.SourcePath.Value.Length..]}",
                    _entries[pair.Value].Copy(NextEntryId(), now)))
                .ToArray();

            foreach ((string path, EntryRecord record) in copies)
            {
                _entries.Add(record.Id, record);
                _paths.Add(path, record.Id);
            }

            destinationParent.TouchContent(now);
            return ValueTask.FromResult(MutationSuccess(
                FileSystemOperation.Copy,
                request.DestinationPath,
                copies[0].Record));
        }
    }

    /// <inheritdoc />
    public ValueTask<FileSystemMutationResult> DeleteAsync(
        FileSystemDeleteRequest request,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        lock (_gate)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromResult(MutationFailure(
                    FileSystemOperation.Delete,
                    FileSystemErrorCode.Cancelled,
                    request.Path));
            }

            if (request.Path.Value == "/")
            {
                return ValueTask.FromResult(MutationFailure(
                    FileSystemOperation.Delete,
                    FileSystemErrorCode.ProtectedEntry,
                    request.Path));
            }

            if (!TryGetRecord(request.Path, out EntryRecord? record))
            {
                return ValueTask.FromResult(MutationFailure(
                    FileSystemOperation.Delete,
                    FileSystemErrorCode.NotFound,
                    request.Path));
            }

            VirtualPath parentPath = GetParent(request.Path);
            EntryRecord parent = GetRecord(parentPath);
            if (record.Revision != request.ExpectedEntryRevision
                || parent.Revision != request.ExpectedParentRevision)
            {
                return ValueTask.FromResult(MutationFailure(
                    FileSystemOperation.Delete,
                    FileSystemErrorCode.RevisionConflict,
                    request.Path));
            }

            string descendantPrefix = $"{request.Path.Value}/";
            bool hasChildren = _paths.Keys.Any(path => path.StartsWith(descendantPrefix, StringComparison.Ordinal));
            if (hasChildren && !request.Recursive)
            {
                return ValueTask.FromResult(MutationFailure(
                    FileSystemOperation.Delete,
                    FileSystemErrorCode.DirectoryNotEmpty,
                    request.Path));
            }

            (string Path, FileSystemEntryId Id)[] removals = _paths
                .Where(pair => pair.Key == request.Path.Value
                    || pair.Key.StartsWith(descendantPrefix, StringComparison.Ordinal))
                .Select(static pair => (pair.Key, pair.Value))
                .ToArray();
            foreach ((string path, FileSystemEntryId id) in removals)
            {
                _paths.Remove(path);
                _entries.Remove(id);
            }

            parent.TouchContent(GetUtcNow());
            return ValueTask.FromResult(MutationSuccess(
                FileSystemOperation.Delete,
                removals.Select(static removal => removal.Id)));
        }
    }

    /// <inheritdoc />
    public ValueTask<FileSystemResult<FileSystemEntrySnapshot>> StatAsync(
        FileSystemStatRequest request,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(StatFailure(FileSystemErrorCode.Cancelled, request.Path));
        }

        lock (_gate)
        {
            return ValueTask.FromResult(TryGetRecord(request.Path, out EntryRecord? record)
                ? FileSystemResult<FileSystemEntrySnapshot>.Success(Snapshot(request.Path, record))
                : StatFailure(FileSystemErrorCode.NotFound, request.Path));
        }
    }

    /// <inheritdoc />
    public ValueTask<FileSystemMutationResult> SetPermissionsAsync(
        FileSystemSetPermissionsRequest request,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        lock (_gate)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromResult(MutationFailure(
                    FileSystemOperation.SetPermissions,
                    FileSystemErrorCode.Cancelled,
                    request.Path));
            }

            if (!TryGetRecord(request.Path, out EntryRecord? record))
            {
                return ValueTask.FromResult(MutationFailure(
                    FileSystemOperation.SetPermissions,
                    FileSystemErrorCode.NotFound,
                    request.Path));
            }

            if (record.Revision != request.ExpectedRevision)
            {
                return ValueTask.FromResult(MutationFailure(
                    FileSystemOperation.SetPermissions,
                    FileSystemErrorCode.RevisionConflict,
                    request.Path));
            }

            record.Permissions = request.Permissions;
            record.TouchMetadata(GetUtcNow());
            return ValueTask.FromResult(MutationSuccess(
                FileSystemOperation.SetPermissions,
                request.Path,
                record));
        }
    }

    private FileSystemMutationResult? ValidateTransfer(
        FileSystemOperation operation,
        VirtualPath sourcePath,
        VirtualPath destinationPath,
        long expectedEntryRevision,
        long expectedSourceParentRevision,
        long expectedDestinationParentRevision,
        out EntryRecord? source,
        out EntryRecord? sourceParent,
        out EntryRecord? destinationParent)
    {
        source = null;
        sourceParent = null;
        destinationParent = null;
        if (sourcePath.Value == "/")
        {
            return MutationFailure(operation, FileSystemErrorCode.ProtectedEntry, sourcePath);
        }

        if (!TryGetRecord(sourcePath, out source))
        {
            return MutationFailure(operation, FileSystemErrorCode.NotFound, sourcePath);
        }

        if (_paths.ContainsKey(destinationPath.Value))
        {
            return MutationFailure(operation, FileSystemErrorCode.AlreadyExists, destinationPath);
        }

        VirtualPath sourceParentPath = GetParent(sourcePath);
        VirtualPath destinationParentPath = GetParent(destinationPath);
        if (!TryGetRecord(sourceParentPath, out sourceParent)
            || !TryGetRecord(destinationParentPath, out destinationParent)
            || destinationParent.Kind != FileSystemEntryKind.Directory)
        {
            return MutationFailure(operation, FileSystemErrorCode.NotDirectory, destinationParentPath);
        }

        if (source.Revision != expectedEntryRevision
            || sourceParent.Revision != expectedSourceParentRevision
            || destinationParent.Revision != expectedDestinationParentRevision)
        {
            return MutationFailure(operation, FileSystemErrorCode.RevisionConflict, sourcePath, destinationPath);
        }

        return null;
    }

    private FileSystemMutationResult MutationSuccess(
        FileSystemOperation operation,
        VirtualPath path,
        EntryRecord record)
    {
        FileSystemTransactionResult transaction = FileSystemTransactionResult.Committed(
            NextTransactionId(),
            [record.Id]);
        return new FileSystemMutationResult(transaction, Snapshot(path, record));
    }

    private FileSystemMutationResult MutationSuccess(
        FileSystemOperation operation,
        IEnumerable<FileSystemEntryId> affectedIds) =>
        new(FileSystemTransactionResult.Committed(NextTransactionId(), affectedIds));

    private FileSystemMutationResult MutationFailure(
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

    private static FileSystemResult<FileSystemContentReadHandle> ReadFailure(
        FileSystemErrorCode code,
        VirtualPath path) =>
        FileSystemResult<FileSystemContentReadHandle>.Failure(
            new FileSystemError(FileSystemOperation.Read, code, path));

    private static FileSystemResult<FileSystemDirectorySnapshot> DirectoryFailure(
        FileSystemErrorCode code,
        VirtualPath path) =>
        FileSystemResult<FileSystemDirectorySnapshot>.Failure(
            new FileSystemError(FileSystemOperation.Enumerate, code, path));

    private static FileSystemResult<FileSystemEntrySnapshot> StatFailure(
        FileSystemErrorCode code,
        VirtualPath path) =>
        FileSystemResult<FileSystemEntrySnapshot>.Failure(
            new FileSystemError(FileSystemOperation.Stat, code, path));

    private static VirtualPath GetParent(VirtualPath path)
    {
        int separator = path.Value.LastIndexOf('/');
        return separator <= 0 ? VirtualPath.Parse("/") : VirtualPath.Parse(path.Value[..separator]);
    }

    private static bool IsWithin(VirtualPath candidate, VirtualPath root) =>
        candidate == root
        || (root.Value == "/" && candidate.Value.StartsWith('/'))
        || candidate.Value.StartsWith($"{root.Value}/", StringComparison.Ordinal);

    private bool TryGetRecord(
        VirtualPath path,
        [NotNullWhen(true)] out EntryRecord? record)
    {
        if (_paths.TryGetValue(path.Value, out FileSystemEntryId id))
        {
            record = _entries[id];
            return true;
        }

        record = null;
        return false;
    }

    private EntryRecord GetRecord(VirtualPath path) => _entries[_paths[path.Value]];

    private static FileSystemEntrySnapshot Snapshot(VirtualPath path, EntryRecord record) =>
        new(path, record.ToMetadata());

    private FileSystemEntryId NextEntryId()
    {
        FileSystemEntryId id = _entryIdFactory();
        if (id.IsEmpty || _entries.ContainsKey(id))
        {
            throw new InvalidOperationException("The filesystem entry ID factory returned an empty or duplicate ID.");
        }

        return id;
    }

    private Guid NextTransactionId()
    {
        Guid id = _transactionIdFactory();
        return id == Guid.Empty
            ? throw new InvalidOperationException("The transaction ID factory returned an empty ID.")
            : id;
    }

    private DateTimeOffset GetUtcNow() => _timeProvider.GetUtcNow().ToUniversalTime();

    private static void TouchParents(EntryRecord sourceParent, EntryRecord destinationParent, DateTimeOffset now)
    {
        sourceParent.TouchContent(now);
        if (!ReferenceEquals(sourceParent, destinationParent))
        {
            destinationParent.TouchContent(now);
        }
    }

    private sealed class EntryRecord
    {
        private EntryRecord(
            FileSystemEntryId id,
            FileSystemEntryKind kind,
            string ownerId,
            string groupId,
            FileSystemPermissions permissions,
            DateTimeOffset createdAtUtc,
            string? symbolicLinkTarget = null)
        {
            Id = id;
            Kind = kind;
            OwnerId = ownerId;
            GroupId = groupId;
            Permissions = permissions;
            CreatedAtUtc = createdAtUtc;
            ContentModifiedAtUtc = createdAtUtc;
            MetadataChangedAtUtc = createdAtUtc;
            SymbolicLinkTarget = symbolicLinkTarget;
        }

        internal FileSystemEntryId Id { get; }
        internal FileSystemEntryKind Kind { get; }
        internal string OwnerId { get; }
        internal string GroupId { get; }
        internal FileSystemPermissions Permissions { get; set; }
        internal DateTimeOffset CreatedAtUtc { get; }
        internal DateTimeOffset ContentModifiedAtUtc { get; private set; }
        internal DateTimeOffset MetadataChangedAtUtc { get; private set; }
        internal long Revision { get; private set; } = 1;
        internal byte[] Content { get; set; } = [];
        internal FileSystemContentDescriptor ContentDescriptor { get; set; } =
            FileSystemContentDescriptor.Binary();
        internal string? SymbolicLinkTarget { get; }

        internal static EntryRecord File(FileSystemEntryId id, string owner, string group, FileSystemPermissions permissions, DateTimeOffset now) =>
            new(id, FileSystemEntryKind.File, owner, group, permissions, now);

        internal static EntryRecord Directory(FileSystemEntryId id, string owner, string group, FileSystemPermissions permissions, DateTimeOffset now) =>
            new(id, FileSystemEntryKind.Directory, owner, group, permissions, now);

        internal static EntryRecord Link(FileSystemEntryId id, string owner, string group, FileSystemPermissions permissions, DateTimeOffset now, string target) =>
            new(id, FileSystemEntryKind.SymbolicLink, owner, group, permissions, now, target);

        internal FileSystemEntryMetadata ToMetadata()
        {
            FileSystemTimestamps timestamps = new(CreatedAtUtc, ContentModifiedAtUtc, MetadataChangedAtUtc);
            return Kind switch
            {
                FileSystemEntryKind.File => new FileMetadata(
                    Id,
                    OwnerId,
                    GroupId,
                    Permissions,
                    timestamps,
                    Revision,
                    Content.LongLength,
                    ContentDescriptor.MediaType),
                FileSystemEntryKind.Directory => new DirectoryMetadata(Id, OwnerId, GroupId, Permissions, timestamps, Revision),
                FileSystemEntryKind.SymbolicLink => new SymbolicLinkMetadata(Id, OwnerId, GroupId, Permissions, timestamps, Revision, SymbolicLinkTarget!),
                _ => throw new InvalidOperationException("Unknown in-memory entry kind.")
            };
        }

        internal void TouchContent(DateTimeOffset now)
        {
            Revision++;
            ContentModifiedAtUtc = now;
            MetadataChangedAtUtc = now;
        }

        internal void TouchMetadata(DateTimeOffset now)
        {
            Revision++;
            MetadataChangedAtUtc = now;
        }

        internal EntryRecord Copy(FileSystemEntryId id, DateTimeOffset now)
        {
            EntryRecord copy = new(id, Kind, OwnerId, GroupId, Permissions, now, SymbolicLinkTarget)
            {
                Content = Content.ToArray(),
                ContentDescriptor = ContentDescriptor
            };
            return copy;
        }
    }
}