using System.Text;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Tests.Support;

/// <summary>
/// In-memory <see cref="IAppFileSystemGateway"/> double that enforces the same optimistic
/// revision preconditions as the real filesystem, so command tests exercise genuine
/// success/conflict/not-found paths instead of a gateway that always succeeds.
/// </summary>
public sealed class FakeAppFileSystemGateway : IAppFileSystemGateway
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    public FakeAppFileSystemGateway()
    {
        _entries["/"] = Entry.NewDirectory();
    }

    /// <summary>
    /// Gets or sets what <see cref="ChargeStreamBuffer"/> returns: <see langword="null"/> (the default)
    /// answers <see cref="KernelMemoryCharge.None"/>, like a host that does not measure memory.
    /// </summary>
    public Func<KernelMemoryCharge>? StreamBufferCharges { get; set; }

    /// <inheritdoc />
    public KernelMemoryCharge ChargeStreamBuffer() => StreamBufferCharges?.Invoke() ?? KernelMemoryCharge.None;

    /// <summary>Seeds a directory (and any missing ancestors) at revision 1.</summary>
    public FakeAppFileSystemGateway WithDirectory(string path)
    {
        string canonical = VirtualPath.Parse(path).Value;
        foreach (string ancestor in AncestorsAndSelf(canonical))
        {
            _entries.TryAdd(ancestor, Entry.NewDirectory());
        }
        return this;
    }

    /// <summary>Seeds a file (and any missing ancestor directories) at revision 1.</summary>
    public FakeAppFileSystemGateway WithFile(string path, string content = "")
    {
        string canonical = VirtualPath.Parse(path).Value;
        WithDirectory(GetParentPath(canonical));
        _entries[canonical] = Entry.NewFile(Encoding.UTF8.GetBytes(content));
        return this;
    }

    public bool Exists(string path) => _entries.ContainsKey(VirtualPath.Parse(path).Value);

    public long RevisionOf(string path) =>
        _entries.TryGetValue(VirtualPath.Parse(path).Value, out Entry? entry)
            ? entry.Revision
            : throw new KeyNotFoundException($"No fake entry seeded at '{path}'.");

    /// <summary>
    /// Directly bumps an entry's revision, simulating another actor committing a concurrent
    /// change outside of this gateway's own operations (a TOCTOU race).
    /// </summary>
    public void SimulateConcurrentChange(string path)
    {
        if (!_entries.TryGetValue(VirtualPath.Parse(path).Value, out Entry? entry))
        {
            throw new KeyNotFoundException($"No fake entry seeded at '{path}'.");
        }
        entry.Revision++;
    }

    public string ContentOf(string path) =>
        _entries.TryGetValue(VirtualPath.Parse(path).Value, out Entry? entry)
            ? Encoding.UTF8.GetString(entry.Content)
            : throw new KeyNotFoundException($"No fake entry seeded at '{path}'.");

    public ValueTask<FileSystemResult<FileSystemEntrySnapshot>> StatAsync(
        FileSystemStatRequest request, CancellationToken cancellationToken = default)
    {
        if (!_entries.TryGetValue(request.Path.Value, out Entry? entry))
        {
            return ValueTask.FromResult(FileSystemResult<FileSystemEntrySnapshot>.Failure(
                new FileSystemError(FileSystemOperation.Stat, FileSystemErrorCode.NotFound, request.Path)));
        }

        return ValueTask.FromResult(
            FileSystemResult<FileSystemEntrySnapshot>.Success(Snapshot(request.Path, entry)));
    }

    public ValueTask<FileSystemResult<FileSystemContentReadHandle>> ReadAsync(
        FileSystemReadRequest request, CancellationToken cancellationToken = default)
    {
        if (!_entries.TryGetValue(request.Path.Value, out Entry? entry) || entry.Kind != FileSystemEntryKind.File)
        {
            // Like the real engine, reading a directory is NotFile rather than NotFound.
            FileSystemErrorCode code = entry is null ? FileSystemErrorCode.NotFound : FileSystemErrorCode.NotFile;
            return ValueTask.FromResult(FileSystemResult<FileSystemContentReadHandle>.Failure(
                new FileSystemError(FileSystemOperation.Read, code, request.Path)));
        }

        return ValueTask.FromResult(FileSystemResult<FileSystemContentReadHandle>.Success(
            new FileSystemContentReadHandle(
                Snapshot(request.Path, entry),
                FileSystemContentDescriptor.Text(),
                new MemoryStream(entry.Content, writable: false))));
    }

    public ValueTask<FileSystemResult<FileSystemDirectorySnapshot>> EnumerateAsync(
        FileSystemEnumerateRequest request, CancellationToken cancellationToken = default)
    {
        if (!_entries.TryGetValue(request.Path.Value, out Entry? directory) ||
            directory.Kind != FileSystemEntryKind.Directory)
        {
            return ValueTask.FromResult(FileSystemResult<FileSystemDirectorySnapshot>.Failure(
                new FileSystemError(FileSystemOperation.Enumerate, FileSystemErrorCode.NotFound, request.Path)));
        }

        string prefix = request.Path.Value == "/" ? "/" : request.Path.Value + "/";
        List<FileSystemDirectoryItem> items = [];
        foreach ((string path, Entry entry) in _entries)
        {
            if (path == request.Path.Value || !path.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            string remainder = path[prefix.Length..];
            if (remainder.Contains('/'))
            {
                continue;
            }

            items.Add(new FileSystemDirectoryItem(FileSystemEntryName.Parse(remainder), Metadata(entry)));
        }

        items.Sort((a, b) => string.CompareOrdinal(a.Name.Value, b.Name.Value));
        return ValueTask.FromResult(FileSystemResult<FileSystemDirectorySnapshot>.Success(
            new FileSystemDirectorySnapshot(request.Path, directory.Revision, items)));
    }

    public ValueTask<FileSystemMutationResult> CreateAsync(
        FileSystemCreateRequest request, CancellationToken cancellationToken = default)
    {
        if (_entries.ContainsKey(request.Path.Value))
        {
            return ValueTask.FromResult(Rejected(FileSystemOperation.Create, FileSystemErrorCode.AlreadyExists, request.Path));
        }

        string parentPath = GetParentPath(request.Path.Value);
        if (!_entries.TryGetValue(parentPath, out Entry? parent) || parent.Kind != FileSystemEntryKind.Directory)
        {
            return ValueTask.FromResult(Rejected(FileSystemOperation.Create, FileSystemErrorCode.NotFound, request.Path));
        }

        // A null expected revision is an unconditional create, as in the real engine.
        if (request.ExpectedParentRevision is { } expectedParent && parent.Revision != expectedParent)
        {
            return ValueTask.FromResult(Rejected(FileSystemOperation.Create, FileSystemErrorCode.RevisionConflict, request.Path));
        }

        Entry entry = new(request.Kind, [], request.Permissions, 1);
        _entries[request.Path.Value] = entry;
        parent.Revision++;
        return ValueTask.FromResult(Committed(Snapshot(request.Path, entry)));
    }

    public async ValueTask<FileSystemMutationResult> WriteAsync(
        FileSystemWriteRequest request, IFileSystemContentSource content, CancellationToken cancellationToken = default)
    {
        if (!_entries.TryGetValue(request.Path.Value, out Entry? entry) || entry.Kind != FileSystemEntryKind.File)
        {
            return Rejected(
                FileSystemOperation.Write,
                entry is null ? FileSystemErrorCode.NotFound : FileSystemErrorCode.NotFile,
                request.Path);
        }

        // A null expected revision is an unconditional overwrite, as in the real engine.
        if (request.ExpectedRevision is { } expected && entry.Revision != expected)
        {
            return Rejected(FileSystemOperation.Write, FileSystemErrorCode.RevisionConflict, request.Path);
        }

        await using Stream stream = await content.OpenReadAsync(cancellationToken);
        using MemoryStream buffer = new();
        await stream.CopyToAsync(buffer, cancellationToken);
        entry.Content = buffer.ToArray();
        entry.Revision++;
        return Committed(Snapshot(request.Path, entry));
    }

    public ValueTask<FileSystemMutationResult> DeleteAsync(
        FileSystemDeleteRequest request, CancellationToken cancellationToken = default)
    {
        if (!_entries.TryGetValue(request.Path.Value, out Entry? entry))
        {
            return ValueTask.FromResult(Rejected(FileSystemOperation.Delete, FileSystemErrorCode.NotFound, request.Path));
        }

        string parentPath = GetParentPath(request.Path.Value);
        if (!_entries.TryGetValue(parentPath, out Entry? parent))
        {
            return ValueTask.FromResult(Rejected(FileSystemOperation.Delete, FileSystemErrorCode.NotFound, request.Path));
        }

        if (entry.Revision != request.ExpectedEntryRevision || parent.Revision != request.ExpectedParentRevision)
        {
            return ValueTask.FromResult(Rejected(FileSystemOperation.Delete, FileSystemErrorCode.RevisionConflict, request.Path));
        }

        List<string> descendants = DescendantsAndSelf(request.Path.Value).ToList();
        if (descendants.Count > 1 && !request.Recursive)
        {
            return ValueTask.FromResult(Rejected(FileSystemOperation.Delete, FileSystemErrorCode.DirectoryNotEmpty, request.Path));
        }

        foreach (string path in descendants)
        {
            _entries.Remove(path);
        }
        parent.Revision++;
        return ValueTask.FromResult(Committed(entry: null));
    }

    public ValueTask<FileSystemMutationResult> MoveAsync(
        FileSystemMoveRequest request, CancellationToken cancellationToken = default)
    {
        if (!_entries.TryGetValue(request.SourcePath.Value, out Entry? entry))
        {
            return ValueTask.FromResult(Rejected(FileSystemOperation.Move, FileSystemErrorCode.NotFound, request.SourcePath));
        }

        string sourceParentPath = GetParentPath(request.SourcePath.Value);
        string destinationParentPath = GetParentPath(request.DestinationPath.Value);
        if (!_entries.TryGetValue(sourceParentPath, out Entry? sourceParent) ||
            !_entries.TryGetValue(destinationParentPath, out Entry? destinationParent))
        {
            return ValueTask.FromResult(Rejected(FileSystemOperation.Move, FileSystemErrorCode.NotFound, request.SourcePath));
        }

        if (_entries.ContainsKey(request.DestinationPath.Value))
        {
            return ValueTask.FromResult(Rejected(FileSystemOperation.Move, FileSystemErrorCode.AlreadyExists, request.DestinationPath));
        }

        if (entry.Revision != request.ExpectedEntryRevision ||
            sourceParent.Revision != request.ExpectedSourceParentRevision ||
            destinationParent.Revision != request.ExpectedDestinationParentRevision)
        {
            return ValueTask.FromResult(Rejected(FileSystemOperation.Move, FileSystemErrorCode.RevisionConflict, request.SourcePath));
        }

        foreach (string path in DescendantsAndSelf(request.SourcePath.Value).ToList())
        {
            Entry moved = _entries[path];
            _entries.Remove(path);
            string rewritten = request.DestinationPath.Value + path[request.SourcePath.Value.Length..];
            _entries[rewritten] = moved;
        }
        sourceParent.Revision++;
        destinationParent.Revision++;

        Entry moved2 = _entries[request.DestinationPath.Value];
        return ValueTask.FromResult(Committed(Snapshot(request.DestinationPath, moved2)));
    }

    public ValueTask<FileSystemMutationResult> CopyAsync(
        FileSystemCopyRequest request, CancellationToken cancellationToken = default)
    {
        if (!_entries.TryGetValue(request.SourcePath.Value, out Entry? source))
        {
            return ValueTask.FromResult(Rejected(FileSystemOperation.Copy, FileSystemErrorCode.NotFound, request.SourcePath));
        }

        string destinationParentPath = GetParentPath(request.DestinationPath.Value);
        if (!_entries.TryGetValue(destinationParentPath, out Entry? destinationParent))
        {
            return ValueTask.FromResult(Rejected(FileSystemOperation.Copy, FileSystemErrorCode.NotFound, request.DestinationPath));
        }

        if (_entries.ContainsKey(request.DestinationPath.Value))
        {
            return ValueTask.FromResult(Rejected(FileSystemOperation.Copy, FileSystemErrorCode.AlreadyExists, request.DestinationPath));
        }

        if (source.Revision != request.ExpectedEntryRevision ||
            destinationParent.Revision != request.ExpectedDestinationParentRevision)
        {
            return ValueTask.FromResult(Rejected(FileSystemOperation.Copy, FileSystemErrorCode.RevisionConflict, request.SourcePath));
        }

        foreach (string path in DescendantsAndSelf(request.SourcePath.Value).ToList())
        {
            Entry original = _entries[path];
            string rewritten = request.DestinationPath.Value + path[request.SourcePath.Value.Length..];
            _entries[rewritten] = new Entry(original.Kind, (byte[])original.Content.Clone(), original.Permissions, 1);
        }
        destinationParent.Revision++;

        Entry copied = _entries[request.DestinationPath.Value];
        return ValueTask.FromResult(Committed(Snapshot(request.DestinationPath, copied)));
    }

    public ValueTask<FileSystemMutationResult> SetPermissionsAsync(
        FileSystemSetPermissionsRequest request, CancellationToken cancellationToken = default)
    {
        if (!_entries.TryGetValue(request.Path.Value, out Entry? entry))
        {
            return ValueTask.FromResult(Rejected(FileSystemOperation.SetPermissions, FileSystemErrorCode.NotFound, request.Path));
        }

        if (entry.Revision != request.ExpectedRevision)
        {
            return ValueTask.FromResult(Rejected(FileSystemOperation.SetPermissions, FileSystemErrorCode.RevisionConflict, request.Path));
        }

        entry.Permissions = request.Permissions;
        entry.Revision++;
        return ValueTask.FromResult(Committed(Snapshot(request.Path, entry)));
    }

    public IAppFileSystemGateway WithSelectedHandle(FileSystemSelectedResourceHandle handle) => this;

    private IEnumerable<string> DescendantsAndSelf(string path)
    {
        string prefix = path == "/" ? "/" : path + "/";
        foreach (string candidate in _entries.Keys.ToList())
        {
            if (candidate == path || candidate.StartsWith(prefix, StringComparison.Ordinal))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> AncestorsAndSelf(string path)
    {
        if (path == "/")
        {
            yield return "/";
            yield break;
        }

        string[] segments = path.Trim('/').Split('/');
        string current = "";
        foreach (string segment in segments)
        {
            current += "/" + segment;
            yield return current;
        }
    }

    private static string GetParentPath(string canonicalPath)
    {
        int lastSlash = canonicalPath.LastIndexOf('/');
        return lastSlash <= 0 ? "/" : canonicalPath[..lastSlash];
    }

    private FileSystemEntrySnapshot Snapshot(VirtualPath path, Entry entry) => new(path, Metadata(entry));

    private FileSystemEntryMetadata Metadata(Entry entry) => entry.Kind switch
    {
        FileSystemEntryKind.Directory => new DirectoryMetadata(
            FileSystemEntryId.FromGuid(entry.Id), "user", "users", entry.Permissions,
            new FileSystemTimestamps(_now, _now, _now), entry.Revision),
        _ => new FileMetadata(
            FileSystemEntryId.FromGuid(entry.Id), "user", "users", entry.Permissions,
            new FileSystemTimestamps(_now, _now, _now), entry.Revision, entry.Content.Length, "text/plain"),
    };

    private static FileSystemMutationResult Committed(FileSystemEntrySnapshot? entry) =>
        new(FileSystemTransactionResult.Committed(Guid.NewGuid(), entry is null ? [] : [entry.Metadata.Id]), entry);

    private static FileSystemMutationResult Rejected(
        FileSystemOperation operation, FileSystemErrorCode code, VirtualPath path) =>
        new(FileSystemTransactionResult.Rejected(Guid.NewGuid(), new FileSystemError(operation, code, path)));

    private sealed class Entry(FileSystemEntryKind kind, byte[] content, FileSystemPermissions permissions, long revision)
    {
        public Guid Id { get; } = Guid.NewGuid();
        public FileSystemEntryKind Kind { get; } = kind;
        public byte[] Content { get; set; } = content;
        public FileSystemPermissions Permissions { get; set; } = permissions;
        public long Revision { get; set; } = revision;

        public static Entry NewDirectory() =>
            new(FileSystemEntryKind.Directory, [], FileSystemPermissions.FromMode(0b111_101_101), 1);

        public static Entry NewFile(byte[] content) =>
            new(FileSystemEntryKind.File, content, FileSystemPermissions.FromMode(0b110_100_100), 1);
    }
}
