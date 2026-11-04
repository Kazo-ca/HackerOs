using System.Diagnostics;
using System.Text.Json;
using Kazo.Os.Infrastructure.Browser.Schema;
using Kazo.Os.Infrastructure.Browser.Storage;
using Kazo.Os.Simulation.Abstractions.Metrics;
using Microsoft.JSInterop;

namespace Kazo.Os.Infrastructure.Browser.Interop;

/// <summary>
/// Owns the browser-only JavaScript module and exposes transaction-oriented IndexedDB primitives
/// to repositories in this assembly.
/// </summary>
internal sealed class IndexedDbInteropAdapter : IAsyncDisposable
{
    internal const string ModulePath = "./_content/Kazo.Os.Infrastructure.Browser/indexedDb.js";

    /// <summary>
    /// Bytes a transaction is charged when its caller gives no size: one 4 KiB block, the size a
    /// Linux filesystem reads or writes for an inode or directory update.
    /// </summary>
    internal const long MetadataBlockBytes = 4096;

    private readonly IJSRuntime _runtime;
    private readonly IBlockDeviceStatistics? _disk;
    private readonly SemaphoreSlim _moduleGate = new(1, 1);
    private IJSObjectReference? _module;
    private bool _disposed;

    /// <summary>Initializes the adapter without importing JavaScript until the first operation.</summary>
    /// <param name="runtime">JavaScript runtime the module is imported into.</param>
    /// <param name="disk">
    /// Optional statistics of the OS disk <c>hda</c> (ADR 0057 section 4). When present, every
    /// transaction this adapter runs is timed and counted as one read (read-only transaction) or
    /// one write (read-write transaction) in <c>/proc/diskstats</c>.
    /// </param>
    public IndexedDbInteropAdapter(IJSRuntime runtime, IBlockDeviceStatistics? disk = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
        _disk = disk;
    }

    /// <summary>Opens the canonical database and applies a C#-owned declarative migration plan.</summary>
    public async ValueTask OpenAsync(
        IndexedDbMigrationPlan migrationPlan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(migrationPlan);
        IJSObjectReference module = await GetModuleAsync(cancellationToken).ConfigureAwait(false);
        await module.InvokeVoidAsync(
            "openDatabase",
            cancellationToken,
            HackerOsIndexedDbSchema.DatabaseName,
            HackerOsIndexedDbSchema.CurrentVersion,
            migrationPlan).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes an ordered operation batch inside one named schema transaction boundary.
    /// </summary>
    /// <param name="boundaryName">Declared transaction boundary.</param>
    /// <param name="mode">Read-only or read-write.</param>
    /// <param name="operations">Operations run in order inside the transaction.</param>
    /// <param name="cancellationToken">Cancels the interop call.</param>
    /// <param name="ioBytes">
    /// Bytes the transaction moves, reported to the disk statistics (rounded up to 512-byte
    /// sectors); <see cref="MetadataBlockBytes"/> when omitted.
    /// </param>
    public async ValueTask<IReadOnlyList<JsonElement>> ExecuteAsync(
        string boundaryName,
        IndexedDbTransactionMode mode,
        IReadOnlyList<IndexedDbOperation> operations,
        CancellationToken cancellationToken = default,
        long? ioBytes = null)
    {
        if (_disk is null)
        {
            return await ExecuteCoreAsync(boundaryName, mode, operations, cancellationToken).ConfigureAwait(false);
        }

        // The request is "in flight" from here to completion, like a bio between submit and end_io.
        long started = Stopwatch.GetTimestamp();
        bool succeeded = false;
        _disk.Begin();
        try
        {
            IReadOnlyList<JsonElement> results =
                await ExecuteCoreAsync(boundaryName, mode, operations, cancellationToken).ConfigureAwait(false);
            succeeded = true;
            return results;
        }
        finally
        {
            _disk.Complete(
                mode == IndexedDbTransactionMode.ReadOnly ? BlockIoDirection.Read : BlockIoDirection.Write,
                ioBytes ?? MetadataBlockBytes,
                Stopwatch.GetElapsedTime(started),
                succeeded);
        }
    }

    private async ValueTask<IReadOnlyList<JsonElement>> ExecuteCoreAsync(
        string boundaryName,
        IndexedDbTransactionMode mode,
        IReadOnlyList<IndexedDbOperation> operations,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(boundaryName);
        ArgumentNullException.ThrowIfNull(operations);

        IndexedDbTransactionBoundary boundary = HackerOsIndexedDbSchema.TransactionBoundaries
            .SingleOrDefault(candidate => StringComparer.Ordinal.Equals(candidate.Name, boundaryName))
            ?? throw new ArgumentException(
                $"Unknown IndexedDB transaction boundary '{boundaryName}'.", nameof(boundaryName));

        foreach (IndexedDbOperation operation in operations)
        {
            if (!boundary.ObjectStoreNames.Contains(operation.ObjectStoreName, StringComparer.Ordinal))
            {
                throw new ArgumentException(
                    $"Operation store '{operation.ObjectStoreName}' is outside boundary '{boundaryName}'.",
                    nameof(operations));
            }

            if (operation.ReferenceObjectStoreName is not null
                && !boundary.ObjectStoreNames.Contains(
                    operation.ReferenceObjectStoreName,
                    StringComparer.Ordinal))
            {
                throw new ArgumentException(
                    $"Reference store '{operation.ReferenceObjectStoreName}' is outside boundary '{boundaryName}'.",
                    nameof(operations));
            }
        }

        IJSObjectReference module = await GetModuleAsync(cancellationToken).ConfigureAwait(false);
        JsonElement[] results;
        try
        {
            results = await module.InvokeAsync<JsonElement[]>(
                "executeTransaction",
                cancellationToken,
                HackerOsIndexedDbSchema.DatabaseName,
                HackerOsIndexedDbSchema.CurrentVersion,
                boundary.ObjectStoreNames,
                mode == IndexedDbTransactionMode.ReadOnly ? "readonly" : "readwrite",
                operations).ConfigureAwait(false);
        }
        catch (JSException exception) when (exception.Message.Contains("QuotaExceededError", StringComparison.Ordinal))
        {
            throw new BrowserStorageQuotaException(
                "Browser storage quota was exhausted; the IndexedDB transaction was not committed.",
                exception);
        }

        return results;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync().ConfigureAwait(false);
            }
            catch (JSDisconnectedException)
            {
                // Browser shutdown can disconnect JS before the DI scope is disposed.
            }
        }

        _moduleGate.Dispose();
    }

    private async ValueTask<IJSObjectReference> GetModuleAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_module is not null)
        {
            return _module;
        }

        await _moduleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _module ??= await _runtime.InvokeAsync<IJSObjectReference>(
                "import", cancellationToken, ModulePath).ConfigureAwait(false);
            return _module;
        }
        finally
        {
            _moduleGate.Release();
        }
    }
}

/// <summary>Specifies the IndexedDB transaction mode passed to the browser adapter.</summary>
internal enum IndexedDbTransactionMode
{
    /// <summary>Allows reads only.</summary>
    ReadOnly,

    /// <summary>Allows reads and mutations.</summary>
    ReadWrite
}

/// <summary>Describes one operation in an ordered IndexedDB transaction batch.</summary>
internal sealed record IndexedDbOperation(
    string Kind,
    string ObjectStoreName,
    object? Key = null,
    object? Value = null,
    string? IndexName = null,
    object? Query = null,
    int? Count = null,
    string? CompareProperty = null,
    object? ExpectedValue = null,
    string? FailureCode = null,
    string? ReferenceObjectStoreName = null,
    string? ReferenceIndexName = null);

/// <summary>Describes the ordered schema migration chain up to the current database version.</summary>
internal sealed record IndexedDbMigrationPlan
{
    /// <summary>Initializes and validates a contiguous migration chain beginning at version one.</summary>
    public IndexedDbMigrationPlan(IReadOnlyList<IndexedDbMigrationStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count == 0 || steps[^1].TargetVersion != HackerOsIndexedDbSchema.CurrentVersion)
        {
            throw new ArgumentException(
                "The migration chain must end at the current schema version.", nameof(steps));
        }

        for (int index = 0; index < steps.Count; index++)
        {
            if (steps[index].TargetVersion != index + 1)
            {
                throw new ArgumentException(
                    "Migration target versions must be contiguous and begin at version one.", nameof(steps));
            }
        }

        Steps = steps;
    }

    /// <summary>Gets the migration steps ordered by their target schema version.</summary>
    public IReadOnlyList<IndexedDbMigrationStep> Steps { get; }

    /// <summary>Creates the complete migration chain through the current schema version.</summary>
    public static IndexedDbMigrationPlan CreateCurrent() => new(
    [
        new(
            1,
            HackerOsIndexedDbSchema.ObjectStores,
            [],
            [],
            []),
        new(
            2,
            [],
            [],
            [new IndexedDbIndexChange(
                HackerOsIndexedDbSchema.FileSystemEntryStoreName,
                new IndexedDbIndexDefinition("contentHash", ["contentHash"]))],
            []),
        new(
            3,
            [HackerOsIndexedDbSchema.ObjectStores.Single(
                store => store.Name == HackerOsIndexedDbSchema.ServerConnectionStoreName)],
            [],
            [],
            []),
        new(
            4,
            [.. HackerOsIndexedDbSchema.ObjectStores.Where(store =>
                store.Name is HackerOsIndexedDbSchema.SyncCursorStoreName
                    or HackerOsIndexedDbSchema.SyncRecordStateStoreName)],
            [],
            [],
            [])
    ]);
}

/// <summary>Describes schema changes applied while upgrading to one exact version.</summary>
internal sealed record IndexedDbMigrationStep(
    int TargetVersion,
    IReadOnlyList<IndexedDbObjectStoreDefinition> CreateObjectStores,
    IReadOnlyList<string> DeleteObjectStores,
    IReadOnlyList<IndexedDbIndexChange> CreateIndexes,
    IReadOnlyList<IndexedDbIndexRemoval> DeleteIndexes);

/// <summary>Describes one index addition to an existing object store.</summary>
internal sealed record IndexedDbIndexChange(
    string ObjectStoreName,
    IndexedDbIndexDefinition Index);

/// <summary>Describes one index removal from an existing object store.</summary>
internal sealed record IndexedDbIndexRemoval(
    string ObjectStoreName,
    string IndexName);
