using Kazo.Os.Infrastructure.Browser.Interop;
using Kazo.Os.Infrastructure.Browser.Schema;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Microsoft.JSInterop;

namespace Kazo.Os.Infrastructure.Browser.FileSystem;

/// <summary>Initializes the stable filesystem root without overwriting an existing profile.</summary>
public sealed class IndexedDbFileSystemBootstrapper : IAsyncDisposable
{
    internal static readonly FileSystemEntryId RootEntryId = FileSystemEntryId.FromGuid(
        Guid.Parse("00000000-0000-0000-0000-000000000001"));

    private readonly IndexedDbInteropAdapter _adapter;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a browser filesystem bootstrapper.</summary>
    public IndexedDbFileSystemBootstrapper(IJSRuntime runtime, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _adapter = new IndexedDbInteropAdapter(runtime);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Adds the canonical root at revision one only when it is absent.</summary>
    public async ValueTask EnsureRootAsync(CancellationToken cancellationToken = default)
    {
        await _adapter.OpenAsync(IndexedDbMigrationPlan.CreateCurrent(), cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = _timeProvider.GetUtcNow().ToUniversalTime();
        DirectoryMetadata root = new(
            RootEntryId,
            "system",
            "system",
            FileSystemPermissions.FromMode(0x01ED),
            new FileSystemTimestamps(now, now, now),
            1);

        await _adapter.ExecuteAsync(
            "FileSystemMetadataMutation",
            IndexedDbTransactionMode.ReadWrite,
            [new IndexedDbOperation(
                "addIfAbsent",
                KazoOsIndexedDbSchema.FileSystemEntryStoreName,
                Key: RootEntryId.ToString(),
                Value: IndexedDbFileSystemEntryRecord.FromMetadata(root))],
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _adapter.DisposeAsync();
}
