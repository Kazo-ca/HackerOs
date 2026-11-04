using Kazo.Os.Infrastructure.Browser.Schema;
using Microsoft.JSInterop;

namespace Kazo.Os.Infrastructure.Browser.FileSystem;

/// <summary>Runs bounded browser-filesystem maintenance at startup or on explicit request.</summary>
public sealed class IndexedDbFileSystemMaintenance : IAsyncDisposable
{
    private readonly IndexedDbFileContentRepository _contentRepository;

    /// <summary>Initializes maintenance with the accepted content retention policy.</summary>
    public IndexedDbFileSystemMaintenance(IJSRuntime runtime)
        : this(runtime, FileContentStoragePolicy.Default, TimeProvider.System)
    {
    }

    internal IndexedDbFileSystemMaintenance(
        IJSRuntime runtime,
        FileContentStoragePolicy policy,
        TimeProvider timeProvider)
    {
        _contentRepository = new IndexedDbFileContentRepository(runtime, policy, timeProvider);
    }

    /// <summary>Runs the same bounded collection pass intended for deterministic host startup.</summary>
    public ValueTask<IndexedDbFileContentCleanupResult> InitializeAsync(
        CancellationToken cancellationToken = default) =>
        _contentRepository.CleanupAsync(cancellationToken: cancellationToken);

    /// <summary>Runs a bounded collection pass for explicit recovery or quota maintenance.</summary>
    public ValueTask<IndexedDbFileContentCleanupResult> CleanupAsync(
        int maximumHashes = 64,
        CancellationToken cancellationToken = default) =>
        _contentRepository.CleanupAsync(maximumHashes, cancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _contentRepository.DisposeAsync();
}