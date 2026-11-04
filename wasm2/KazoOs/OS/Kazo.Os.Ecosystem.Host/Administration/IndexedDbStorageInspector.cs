using Kazo.Os.AppSdk.Administration;
using Kazo.Os.Infrastructure.Browser.Diagnostics;
using Kazo.Os.Infrastructure.Browser.Schema;
using Kazo.Os.Platform.Core.Administration;
using Microsoft.JSInterop;

namespace Kazo.Os.Ecosystem.Administration;

/// <summary>
/// <see cref="IStorageInspector"/> over the OS IndexedDB database (ADR 0056, section 11): reads it
/// with <see cref="IndexedDbInspectorService"/> and copies the result into the SDK's
/// <see cref="HackStorageSnapshot"/>, so the Diagnostic app depends on the SDK only.
/// </summary>
internal sealed class IndexedDbStorageInspector(IJSRuntime runtime) : IStorageInspector
{
    /// <inheritdoc />
    public async Task<HackStorageSnapshot> CaptureAsync(CancellationToken cancellationToken = default)
    {
        IndexedDbSnapshot snapshot = await new IndexedDbInspectorService(runtime)
            .InspectDatabaseAsync(cancellationToken)
            .ConfigureAwait(false);
        return new HackStorageSnapshot(
            snapshot.CapturedAtUtc,
            snapshot.DatabaseName,
            snapshot.DatabaseVersion,
            [.. snapshot.Stores.Select(store => new HackStorageStoreSnapshot(
                store.Name,
                store.Purpose,
                store.KeyPath,
                KindOf(store.Name),
                store.RecordCount,
                store.FormattedJson,
                store.RawElement))],
            snapshot.ResolvedPaths,
            snapshot.TotalLinkCount);
    }

    private static HackStorageStoreKind KindOf(string storeName) => storeName switch
    {
        HackerOsIndexedDbSchema.FileSystemEntryStoreName => HackStorageStoreKind.FileEntries,
        HackerOsIndexedDbSchema.FileSystemLinkStoreName => HackStorageStoreKind.FileLinks,
        _ => HackStorageStoreKind.Other
    };
}
