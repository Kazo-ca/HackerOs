using Kazo.Os.Infrastructure.Browser.Interop;
using Kazo.Os.Infrastructure.Browser.Schema;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Metrics;
using Microsoft.JSInterop;

namespace Kazo.Os.Infrastructure.Browser.FileSystem;

/// <summary>Executes atomic IndexedDB filesystem metadata mutation plans.</summary>
internal sealed class IndexedDbFileSystemWriter : IAsyncDisposable
{
    private const string Boundary = "FileSystemMetadataMutation";
    private readonly IndexedDbInteropAdapter _adapter;

    /// <summary>Initializes the browser filesystem metadata writer.</summary>
    /// <param name="runtime">JavaScript runtime.</param>
    /// <param name="disk">Optional statistics of <c>hda</c>; each metadata transaction counts as one 4 KiB write.</param>
    internal IndexedDbFileSystemWriter(IJSRuntime runtime, IBlockDeviceStatistics? disk = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _adapter = new IndexedDbInteropAdapter(runtime, disk);
    }

    /// <summary>Atomically creates an entry and its parent link.</summary>
    internal async ValueTask CreateAsync(
        IndexedDbFileSystemEntryRecord parent,
        FileSystemEntryMetadata created,
        FileSystemEntryName name,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await _adapter.OpenAsync(IndexedDbMigrationPlan.CreateCurrent(), cancellationToken).ConfigureAwait(false);
        await _adapter.ExecuteAsync(
            Boundary,
            IndexedDbTransactionMode.ReadWrite,
            IndexedDbFileSystemMutationPlanner.PlanCreate(parent, created, name, committedAtUtc),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Atomically replaces one entry's permission bits and metadata revision.</summary>
    internal async ValueTask SetPermissionsAsync(
        IndexedDbFileSystemEntryRecord entry,
        FileSystemPermissions permissions,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await _adapter.OpenAsync(IndexedDbMigrationPlan.CreateCurrent(), cancellationToken).ConfigureAwait(false);
        await _adapter.ExecuteAsync(
            Boundary,
            IndexedDbTransactionMode.ReadWrite,
            IndexedDbFileSystemMutationPlanner.PlanSetPermissions(entry, permissions, committedAtUtc),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Atomically publishes file metadata that references previously persisted chunks.</summary>
    internal async ValueTask WriteAsync(
        IndexedDbFileSystemEntryRecord entry,
        string contentHash,
        long length,
        FileSystemContentDescriptor descriptor,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await _adapter.OpenAsync(IndexedDbMigrationPlan.CreateCurrent(), cancellationToken).ConfigureAwait(false);
        await _adapter.ExecuteAsync(
            Boundary,
            IndexedDbTransactionMode.ReadWrite,
            IndexedDbFileSystemMutationPlanner.PlanWrite(
                entry,
                contentHash,
                length,
                descriptor,
                committedAtUtc),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Atomically moves or renames one entry link and updates observed revisions.</summary>
    internal async ValueTask MoveAsync(
        IndexedDbFileSystemEntryRecord entry,
        IndexedDbFileSystemEntryRecord sourceParent,
        FileSystemEntryName sourceName,
        IndexedDbFileSystemEntryRecord destinationParent,
        FileSystemEntryName destinationName,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await _adapter.OpenAsync(IndexedDbMigrationPlan.CreateCurrent(), cancellationToken).ConfigureAwait(false);
        await _adapter.ExecuteAsync(
            Boundary,
            IndexedDbTransactionMode.ReadWrite,
            IndexedDbFileSystemMutationPlanner.PlanMove(
                entry,
                sourceParent,
                sourceName,
                destinationParent,
                destinationName,
                committedAtUtc),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Atomically deletes one entry or complete observed subtree.</summary>
    internal async ValueTask DeleteAsync(
        IndexedDbFileSystemEntryRecord parent,
        IReadOnlyList<IndexedDbFileSystemDeletionEntry> removals,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await _adapter.OpenAsync(IndexedDbMigrationPlan.CreateCurrent(), cancellationToken).ConfigureAwait(false);
        await _adapter.ExecuteAsync(
            Boundary,
            IndexedDbTransactionMode.ReadWrite,
            IndexedDbFileSystemMutationPlanner.PlanDelete(parent, removals, committedAtUtc),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Atomically copies one complete observed subtree and updates its destination parent.</summary>
    internal async ValueTask CopyAsync(
        IndexedDbFileSystemEntryRecord destinationParent,
        IReadOnlyList<IndexedDbFileSystemCopyEntry> copies,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await _adapter.OpenAsync(IndexedDbMigrationPlan.CreateCurrent(), cancellationToken).ConfigureAwait(false);
        await _adapter.ExecuteAsync(
            Boundary,
            IndexedDbTransactionMode.ReadWrite,
            IndexedDbFileSystemMutationPlanner.PlanCopy(destinationParent, copies, committedAtUtc),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _adapter.DisposeAsync();
}