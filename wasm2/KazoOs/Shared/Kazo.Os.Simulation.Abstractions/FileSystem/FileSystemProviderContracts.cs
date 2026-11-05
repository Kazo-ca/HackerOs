using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Simulation.Abstractions.FileSystem;

/// <summary>Combines an atomic transaction outcome with its current entry snapshot.</summary>
public sealed record FileSystemMutationResult
{
    /// <summary>Initializes a mutation result.</summary>
    /// <param name="transaction">Atomic transaction outcome.</param>
    /// <param name="entry">Current affected entry, or null after delete/failure.</param>
    public FileSystemMutationResult(
        FileSystemTransactionResult transaction,
        FileSystemEntrySnapshot? entry = null)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        if (transaction.Status != FileSystemTransactionStatus.Committed && entry is not null)
        {
            throw new ArgumentException("Rejected or cancelled mutations cannot expose an entry.", nameof(entry));
        }

        Transaction = transaction;
        Entry = entry;
    }

    /// <summary>Gets the atomic transaction outcome.</summary>
    public FileSystemTransactionResult Transaction { get; }

    /// <summary>Gets the current affected entry, when the operation leaves one.</summary>
    public FileSystemEntrySnapshot? Entry { get; }

    /// <summary>Gets whether the mutation committed.</summary>
    public bool Succeeded => Transaction.Status == FileSystemTransactionStatus.Committed;
}

/// <summary>Provides provider-level filesystem operations over canonical absolute paths.</summary>
public interface IFileSystemProvider
{
    /// <summary>Gets a stable provider identity used for routing and diagnostics.</summary>
    string ProviderId { get; }

    /// <summary>Opens streamed regular-file content.</summary>
    ValueTask<FileSystemResult<FileSystemContentReadHandle>> ReadAsync(
        FileSystemReadRequest request,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default);

    /// <summary>Enumerates immediate directory children in ordinal name order.</summary>
    ValueTask<FileSystemResult<FileSystemDirectorySnapshot>> EnumerateAsync(
        FileSystemEnumerateRequest request,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default);

    /// <summary>Creates an empty file, directory, or symbolic link atomically.</summary>
    ValueTask<FileSystemMutationResult> CreateAsync(
        FileSystemCreateRequest request,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default);

    /// <summary>Streams and atomically replaces regular-file content.</summary>
    ValueTask<FileSystemMutationResult> WriteAsync(
        FileSystemWriteRequest request,
        IFileSystemContentSource content,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default);

    /// <summary>Moves or renames an entry atomically within this provider.</summary>
    ValueTask<FileSystemMutationResult> MoveAsync(
        FileSystemMoveRequest request,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default);

    /// <summary>Copies an entry or subtree atomically within this provider.</summary>
    ValueTask<FileSystemMutationResult> CopyAsync(
        FileSystemCopyRequest request,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes an entry or subtree atomically.</summary>
    ValueTask<FileSystemMutationResult> DeleteAsync(
        FileSystemDeleteRequest request,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one immutable entry snapshot.</summary>
    ValueTask<FileSystemResult<FileSystemEntrySnapshot>> StatAsync(
        FileSystemStatRequest request,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default);

    /// <summary>Atomically replaces owner/group/other permission bits.</summary>
    ValueTask<FileSystemMutationResult> SetPermissionsAsync(
        FileSystemSetPermissionsRequest request,
        FileSystemAuthorizationContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>Registers one provider as owner of a canonical namespace subtree.</summary>
public sealed record FileSystemMount
{
    /// <summary>Initializes a mount registration.</summary>
    public FileSystemMount(VirtualPath path, IFileSystemProvider provider, bool isProtectedRoot = true)
    {
        FileSystemContractGuard.ValidatePath(path, nameof(path));
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider.ProviderId);

        Path = path;
        Provider = provider;
        IsProtectedRoot = isProtectedRoot;
    }

    /// <summary>Gets the canonical mount path.</summary>
    public VirtualPath Path { get; }

    /// <summary>Gets the provider owning the mount subtree.</summary>
    public IFileSystemProvider Provider { get; }

    /// <summary>Gets whether ordinary filesystem operations may remove the mount root.</summary>
    public bool IsProtectedRoot { get; }
}

/// <summary>Identifies the selected provider and mount for one path.</summary>
/// <param name="Mount">Most-specific matching mount.</param>
/// <param name="Path">Canonical absolute path passed to the provider.</param>
public sealed record FileSystemMountResolution(FileSystemMount Mount, VirtualPath Path);

/// <summary>Resolves canonical paths using longest segment-boundary mount matching.</summary>
public interface IFileSystemMountRouter
{
    /// <summary>Resolves one canonical path to its owning provider.</summary>
    FileSystemMountResolution Resolve(VirtualPath path);

    /// <summary>Gets all mounts ordered from least to most specific.</summary>
    IReadOnlyList<FileSystemMount> Mounts { get; }
}

/// <summary>
/// Provides the mounted, authorized filesystem operation surface used by trusted gateways.
/// </summary>
public interface IFileSystemService : IFileSystemProvider
{
}