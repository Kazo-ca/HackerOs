namespace Kazo.Os.Simulation.Abstractions.Metrics;

/// <summary>
/// What the host's storage reports for the whole origin (<c>navigator.storage.estimate()</c> in a
/// browser): the bytes used and the quota granted, the real figures behind <c>df</c> (ADR 0099,
/// section 2).
/// </summary>
/// <param name="UsageBytes">Bytes the origin uses, every database and cache included.</param>
/// <param name="QuotaBytes">Bytes the host grants to the origin.</param>
public readonly record struct StorageCapacity(long UsageBytes, long QuotaBytes)
{
    /// <summary>Gets the free bytes, never negative.</summary>
    public long AvailableBytes => Math.Max(0, QuotaBytes - UsageBytes);
}

/// <summary>
/// Reads <see cref="StorageCapacity"/> from the host. Implemented by infrastructure code only (the
/// browser's <c>StorageManager</c>, ADR 0055); a host that cannot tell returns <see langword="null"/>.
/// </summary>
public interface IStorageCapacityProbe
{
    /// <summary>Reads the current usage and quota, or <see langword="null"/> when the host cannot tell.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    ValueTask<StorageCapacity?> GetCapacityAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Free-space state the filesystem consults before a write (ADR 0099, section 2): like the blocks
/// <c>ext4</c> reserves for root, the last part of the quota is kept for the system and the
/// administrator.
/// </summary>
public interface IStorageSpaceGuard
{
    /// <summary>
    /// Gets whether the free space is inside the root reserve
    /// (<see cref="StorageSpacePolicy.ReserveBytes"/>): writes of a non-administrator then fail with
    /// <c>ENOSPC</c>.
    /// </summary>
    bool IsRootReserveReached { get; }

    /// <summary>Asks for a new measure soon, without waiting; called after writes and after an <c>ENOSPC</c>.</summary>
    void RequestRefresh();
}

/// <summary>Thresholds of ADR 0099, section 2, shared by the browser storage manager and the OS monitor.</summary>
public static class StorageSpacePolicy
{
    /// <summary>Free space under which storage is low, whatever the quota: 64 MiB.</summary>
    public const long LowSpaceBytes = 64L * 1024 * 1024;

    /// <summary>Share of the quota under which storage is low: 10 %.</summary>
    public const double LowSpaceRatio = 0.10;

    /// <summary>Share of the quota reserved for the system and the administrator: 5 %.</summary>
    public const double ReserveRatio = 0.05;

    /// <summary>Largest root reserve, whatever the quota: 256 MiB.</summary>
    public const long MaxReserveBytes = 256L * 1024 * 1024;

    /// <summary>Gets whether the storage is low: under 64 MiB or 10 % of the quota free.</summary>
    /// <param name="capacity">Measured usage and quota.</param>
    public static bool IsLow(StorageCapacity capacity) =>
        capacity.QuotaBytes > 0
        && (capacity.AvailableBytes < LowSpaceBytes
            || (double)capacity.AvailableBytes / capacity.QuotaBytes < LowSpaceRatio);

    /// <summary>Gets the root reserve of a quota: 5 % of it, at most 256 MiB.</summary>
    /// <param name="quotaBytes">Quota granted to the origin.</param>
    public static long ReserveBytes(long quotaBytes) =>
        quotaBytes <= 0 ? 0 : Math.Min((long)(quotaBytes * ReserveRatio), MaxReserveBytes);

    /// <summary>Gets whether the free space is inside the root reserve.</summary>
    /// <param name="capacity">Measured usage and quota.</param>
    public static bool IsInRootReserve(StorageCapacity capacity) =>
        capacity.QuotaBytes > 0 && capacity.AvailableBytes < ReserveBytes(capacity.QuotaBytes);
}
