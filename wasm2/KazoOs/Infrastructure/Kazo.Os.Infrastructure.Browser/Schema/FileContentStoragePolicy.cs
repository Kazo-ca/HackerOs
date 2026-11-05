namespace Kazo.Os.Infrastructure.Browser.Schema;

/// <summary>
/// Declares the browser-storage policy for the file-content store, including chunk sizing,
/// hashing, deduplication, and garbage-collection retention.
/// </summary>
/// <remarks>
/// This is the decision contract for <c>P2-IDB-003</c> (<c>D-009</c>). Repositories and future
/// migration code use it to decide how to split large files, which hash to compute, whether to
/// reuse identical content chunks, and how long to retain orphaned content before cleanup.
/// </remarks>
public sealed record FileContentStoragePolicy
{
    /// <summary>Gets the default maximum size of a single file in bytes.</summary>
    public const long DefaultMaxFileSizeBytes = 16 * 1024 * 1024;

    /// <summary>Gets the default maximum size of one content chunk in bytes.</summary>
    public const long DefaultMaxChunkSizeBytes = 256 * 1024;

    /// <summary>Gets the default content hashing algorithm name.</summary>
    public const string DefaultContentHashAlgorithm = "SHA-256";

    /// <summary>Gets whether identical content chunks are reused by hash.</summary>
    public const bool DefaultDeduplicateChunks = true;

    /// <summary>Gets the default retention window for orphaned content chunks.</summary>
    public static readonly TimeSpan DefaultOrphanRetention = TimeSpan.FromDays(30);

    /// <summary>Initializes a validated content-storage policy.</summary>
    /// <param name="maxFileSizeBytes">Maximum supported file size in bytes; must be positive.</param>
    /// <param name="maxChunkSizeBytes">Maximum size of one content chunk in bytes; must be positive.</param>
    /// <param name="contentHashAlgorithm">Hash algorithm name, such as <c>SHA-256</c>.</param>
    /// <param name="deduplicateChunks">Whether identical chunks should be deduplicated by hash.</param>
    /// <param name="orphanRetention">Retention window before orphaned chunks are eligible for cleanup.</param>
    /// <exception cref="ArgumentOutOfRangeException">Any size is non-positive or the chunk size exceeds the file size.</exception>
    /// <exception cref="ArgumentException">The hash algorithm name is empty or the retention window is not positive.</exception>
    public FileContentStoragePolicy(
        long maxFileSizeBytes,
        long maxChunkSizeBytes,
        string contentHashAlgorithm,
        bool deduplicateChunks,
        TimeSpan orphanRetention)
    {
        if (maxFileSizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFileSizeBytes), "The maximum file size must be positive.");
        }

        if (maxChunkSizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxChunkSizeBytes), "The maximum chunk size must be positive.");
        }

        if (maxChunkSizeBytes > maxFileSizeBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maxChunkSizeBytes), "The chunk size cannot exceed the maximum file size.");
        }

        if (string.IsNullOrWhiteSpace(contentHashAlgorithm))
        {
            throw new ArgumentException("A hash algorithm name is required.", nameof(contentHashAlgorithm));
        }

        if (orphanRetention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(orphanRetention), "The orphan-retention window must be positive.");
        }

        MaxFileSizeBytes = maxFileSizeBytes;
        MaxChunkSizeBytes = maxChunkSizeBytes;
        ContentHashAlgorithm = contentHashAlgorithm;
        DeduplicateChunks = deduplicateChunks;
        OrphanRetention = orphanRetention;
    }

    /// <summary>Gets the default policy as accepted by this ADR.</summary>
    public static FileContentStoragePolicy Default { get; } = new(
        DefaultMaxFileSizeBytes,
        DefaultMaxChunkSizeBytes,
        DefaultContentHashAlgorithm,
        DefaultDeduplicateChunks,
        DefaultOrphanRetention);

    /// <summary>Gets the maximum supported file size in bytes.</summary>
    public long MaxFileSizeBytes { get; }

    /// <summary>Gets the maximum size of one content chunk in bytes.</summary>
    public long MaxChunkSizeBytes { get; }

    /// <summary>Gets the content hashing algorithm name used for chunk deduplication.</summary>
    public string ContentHashAlgorithm { get; }

    /// <summary>Gets whether identical content chunks should be deduplicated.</summary>
    public bool DeduplicateChunks { get; }

    /// <summary>Gets the retention window for orphaned content chunks before cleanup.</summary>
    public TimeSpan OrphanRetention { get; }

    /// <summary>Determines whether a file requires chunking at the configured chunk size.</summary>
    public bool RequiresChunking(long fileSizeBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fileSizeBytes);
        return fileSizeBytes > MaxChunkSizeBytes;
    }

    /// <summary>Returns the number of chunks needed to store a file of the given byte size.</summary>
    public int ChunkCountFor(long fileSizeBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fileSizeBytes);
        return Math.Max(1, (int)Math.Ceiling(fileSizeBytes / (double)MaxChunkSizeBytes));
    }
}
