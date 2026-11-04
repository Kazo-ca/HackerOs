using System.Security.Cryptography;
using System.Text;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Time;

namespace Kazo.Os.Platform.Core.FileSystem.Virtual;

/// <summary>
/// Marks a provider whose entries are computed on every read and never persisted (ADR 0046).
/// </summary>
/// <remarks>
/// <see cref="FileSystemService"/> never publishes a <c>FileSystemChangeEvent</c> for a path owned
/// by such a provider, the same way <c>inotify</c> stays silent on Linux's <c>/proc</c>.
/// </remarks>
public interface IVirtualFileSystemProvider : IFileSystemProvider
{
}

/// <summary>
/// Shared plumbing for the read-only virtual mounts <c>/proc</c> and <c>/dev</c> (ADR 0046):
/// stable entry identities, clock-derived revisions, and <see cref="FileSystemErrorCode.PermissionDenied"/>
/// for every structural mutation (create, delete, move, copy, chmod), like Linux's <c>EACCES</c>.
/// </summary>
/// <remarks>
/// Nothing here touches a storage backend: derived classes build metadata and content from live
/// simulation state on each call, so the mount is always current and costs no IndexedDB space.
/// </remarks>
public abstract class VirtualFileSystemProvider : IVirtualFileSystemProvider
{
    /// <summary>Owner and group of every system-owned virtual entry.</summary>
    protected const string SystemPrincipal = "system";

    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly Func<Guid> _transactionIdFactory;

    /// <summary>Initializes the shared virtual-provider state.</summary>
    /// <param name="mountPath">Canonical mount path, e.g. <c>/proc</c>.</param>
    /// <param name="clock">Simulation clock supplying revisions and timestamps.</param>
    /// <param name="transactionIdFactory">Optional deterministic transaction ID source for tests.</param>
    protected VirtualFileSystemProvider(
        VirtualPath mountPath,
        ISimulationClock clock,
        Func<Guid>? transactionIdFactory)
    {
        MountPath = mountPath;
        Clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _transactionIdFactory = transactionIdFactory ?? Guid.NewGuid;
    }

    /// <inheritdoc />
    public abstract string ProviderId { get; }

    /// <summary>Gets the canonical mount path this provider answers for.</summary>
    protected VirtualPath MountPath { get; }

    /// <summary>Gets the simulation clock.</summary>
    protected ISimulationClock Clock { get; }

    /// <summary>
    /// Gets the revision every entry reports right now: <see cref="ISimulationClock.CurrentTick"/>
    /// plus one, because contract revisions are strictly positive while ticks start at zero.
    /// </summary>
    protected long CurrentRevision => Clock.CurrentTick + 1;

    /// <summary>Gets the current simulated UTC time.</summary>
    protected DateTimeOffset NowUtc => Clock.UtcNow.ToUniversalTime();

    /// <summary>
    /// Gets the simulated boot time, derived from the clock as <c>now - tick × tickDuration</c>.
    /// </summary>
    protected DateTimeOffset BootTimeUtc =>
        NowUtc - TimeSpan.FromTicks(Clock.TickDuration.Ticks * Clock.CurrentTick);

    /// <inheritdoc />
    public abstract ValueTask<FileSystemResult<FileSystemContentReadHandle>> ReadAsync(
        FileSystemReadRequest request,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public abstract ValueTask<FileSystemResult<FileSystemDirectorySnapshot>> EnumerateAsync(
        FileSystemEnumerateRequest request,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public abstract ValueTask<FileSystemResult<FileSystemEntrySnapshot>> StatAsync(
        FileSystemStatRequest request,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default);

    /// <summary>Writes are refused by default; <c>/dev</c> overrides this to absorb device writes.</summary>
    public virtual ValueTask<FileSystemMutationResult> WriteAsync(
        FileSystemWriteRequest request,
        IFileSystemContentSource content,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Rejected(FileSystemOperation.Write, FileSystemErrorCode.PermissionDenied, request.Path));

    /// <inheritdoc />
    public ValueTask<FileSystemMutationResult> CreateAsync(
        FileSystemCreateRequest request,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Rejected(FileSystemOperation.Create, FileSystemErrorCode.PermissionDenied, request.Path));

    /// <inheritdoc />
    public ValueTask<FileSystemMutationResult> MoveAsync(
        FileSystemMoveRequest request,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Rejected(
            FileSystemOperation.Move, FileSystemErrorCode.PermissionDenied, request.SourcePath, request.DestinationPath));

    /// <inheritdoc />
    public ValueTask<FileSystemMutationResult> CopyAsync(
        FileSystemCopyRequest request,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Rejected(
            FileSystemOperation.Copy, FileSystemErrorCode.PermissionDenied, request.SourcePath, request.DestinationPath));

    /// <inheritdoc />
    public ValueTask<FileSystemMutationResult> DeleteAsync(
        FileSystemDeleteRequest request,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Rejected(FileSystemOperation.Delete, FileSystemErrorCode.PermissionDenied, request.Path));

    /// <inheritdoc />
    public ValueTask<FileSystemMutationResult> SetPermissionsAsync(
        FileSystemSetPermissionsRequest request,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Rejected(FileSystemOperation.SetPermissions, FileSystemErrorCode.PermissionDenied, request.Path));

    /// <summary>
    /// Derives a stable entry ID from the first 16 bytes of <c>SHA-256("&lt;providerId&gt;:&lt;path&gt;")</c>,
    /// so an entry keeps its identity across reads although nothing is stored.
    /// </summary>
    protected FileSystemEntryId StableId(VirtualPath path)
    {
        byte[] hash = SHA256.HashData(Utf8.GetBytes($"{ProviderId}:{path.Value}"));
        return FileSystemEntryId.FromGuid(new Guid(hash.AsSpan(0, 16)));
    }

    /// <summary>Builds timestamps where only the content time moves (it is "now" for computed files).</summary>
    protected static FileSystemTimestamps Timestamps(DateTimeOffset createdAtUtc, DateTimeOffset contentModifiedAtUtc)
    {
        DateTimeOffset created = createdAtUtc.ToUniversalTime();
        DateTimeOffset modified = contentModifiedAtUtc.ToUniversalTime();
        if (modified < created)
        {
            modified = created;
        }

        return new FileSystemTimestamps(created, modified, created);
    }

    /// <summary>Builds directory metadata with the given owner and mode.</summary>
    protected DirectoryMetadata Directory(VirtualPath path, string ownerId, string groupId, ushort mode, DateTimeOffset createdAtUtc) =>
        new(StableId(path), ownerId, groupId, FileSystemPermissions.FromMode(mode), Timestamps(createdAtUtc, createdAtUtc), CurrentRevision);

    /// <summary>Builds zero-length regular-file metadata: virtual files report size 0, as on Linux.</summary>
    protected FileMetadata File(VirtualPath path, string ownerId, string groupId, ushort mode, DateTimeOffset createdAtUtc, string mediaType = "text/plain") =>
        new(StableId(path), ownerId, groupId, FileSystemPermissions.FromMode(mode), Timestamps(createdAtUtc, NowUtc), CurrentRevision, 0, mediaType);

    /// <summary>Builds symbolic-link metadata (mode <c>0777</c>, like every Linux symlink).</summary>
    protected SymbolicLinkMetadata Link(VirtualPath path, string ownerId, string groupId, DateTimeOffset createdAtUtc, string target) =>
        new(StableId(path), ownerId, groupId, FileSystemPermissions.FromMode(0x1FF), Timestamps(createdAtUtc, createdAtUtc), CurrentRevision, target);

    /// <summary>Wraps UTF-8 text in a read handle.</summary>
    protected static FileSystemResult<FileSystemContentReadHandle> TextHandle(VirtualPath path, FileSystemEntryMetadata metadata, string text) =>
        FileSystemResult<FileSystemContentReadHandle>.Success(new FileSystemContentReadHandle(
            new FileSystemEntrySnapshot(path, metadata),
            FileSystemContentDescriptor.Text("text/plain", "utf-8"),
            new MemoryStream(Utf8.GetBytes(text), writable: false)));

    /// <summary>Creates a failed result of any value type.</summary>
    protected static FileSystemResult<T> Failure<T>(FileSystemOperation operation, FileSystemErrorCode code, VirtualPath path)
        where T : notnull =>
        FileSystemResult<T>.Failure(new FileSystemError(operation, code, path));

    /// <summary>Creates a rejected (or cancelled) mutation result.</summary>
    protected FileSystemMutationResult Rejected(
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

    /// <summary>Creates a committed mutation result that leaves <paramref name="entry"/> unchanged.</summary>
    protected FileSystemMutationResult Committed(FileSystemEntrySnapshot entry) =>
        new(FileSystemTransactionResult.Committed(NextTransactionId(), [entry.Metadata.Id]), entry);

    /// <summary>Returns the path relative to the mount, split into segments (empty for the mount root).</summary>
    protected string[] RelativeSegments(VirtualPath path)
    {
        if (path == MountPath)
        {
            return [];
        }

        return path.Value.StartsWith($"{MountPath.Value}/", StringComparison.Ordinal)
            ? path.Value[(MountPath.Value.Length + 1)..].Split('/')
            : throw new ArgumentException($"'{path}' is outside the '{MountPath}' mount.", nameof(path));
    }

    /// <summary>Joins a child name under a parent path.</summary>
    protected static VirtualPath Child(VirtualPath parent, string name) =>
        VirtualPath.Parse(parent.Value == "/" ? $"/{name}" : $"{parent.Value}/{name}");

    private Guid NextTransactionId()
    {
        Guid id = _transactionIdFactory();
        return id == Guid.Empty
            ? throw new InvalidOperationException("The transaction ID factory returned an empty ID.")
            : id;
    }
}
