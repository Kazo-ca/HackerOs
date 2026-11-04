using Kazo.Os.Infrastructure.Browser.Storage;
using Kazo.Os.Simulation.Abstractions.Metrics;
using Microsoft.JSInterop;

namespace Kazo.Os.Infrastructure.Browser.FileSystem;

/// <summary>
/// Browser <see cref="IStorageUsageProvider"/> behind <c>HackDriveInfo</c> and <c>df</c>. Since
/// ADR 0099 (section 2) both figures are the browser's real ones from
/// <c>navigator.storage.estimate()</c> through <see cref="BrowserStorageManager"/>: the origin's
/// usage and its quota, so <c>df</c> agrees with the low-space warning and the root reserve. A
/// browser that cannot tell falls back to ADR 0057 section 7: the bytes the deduplicated IndexedDB
/// content store keeps for live files, with no quota. Only infrastructure code calls the browser
/// here, never an app (ADR 0055).
/// </summary>
public sealed class IndexedDbStorageUsageProvider : IStorageUsageProvider, IAsyncDisposable
{
    private readonly IndexedDbFileSystemProvider _fileSystem;
    private readonly BrowserStorageManager _storage;

    /// <summary>Initializes the provider.</summary>
    /// <param name="fileSystem">Persistent filesystem measured for the used space.</param>
    /// <param name="runtime">JavaScript runtime used to ask the browser for its quota.</param>
    public IndexedDbStorageUsageProvider(IndexedDbFileSystemProvider fileSystem, IJSRuntime runtime)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _storage = new BrowserStorageManager(runtime ?? throw new ArgumentNullException(nameof(runtime)));
    }

    /// <inheritdoc />
    public async ValueTask<StorageUsage> GetUsageAsync(CancellationToken cancellationToken = default)
    {
        if (await _storage.GetCapacityAsync(cancellationToken).ConfigureAwait(false) is { } capacity)
        {
            return new StorageUsage(capacity.UsageBytes, capacity.QuotaBytes);
        }

        // A browser without the StorageManager API: the disk keeps its configured size.
        long used = await _fileSystem.MeasureStoredContentBytesAsync(cancellationToken).ConfigureAwait(false);
        return new StorageUsage(used, null);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _storage.DisposeAsync();
}
