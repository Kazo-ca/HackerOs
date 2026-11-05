namespace Kazo.Os.Simulation.Abstractions.Sync;

/// <summary>
/// The last-synced sync-<c>Revision</c> and <c>ContentHash</c> tracked for one record (ADR 0029).
/// Independent of any document's own local optimistic-concurrency revision.
/// </summary>
/// <param name="Revision">Last revision this device pushed or applied.</param>
/// <param name="ContentHash">Envelope content hash of that revision.</param>
/// <param name="RelativePath">
/// FileSystem domain only (ADR 0101 section 1): home-relative path of the entry at that revision,
/// so a received tombstone or rename finds the local entry again. <see langword="null"/> for the
/// other domains and for states written before ADR 0101.
/// </param>
/// <param name="LocalEntryId">
/// FileSystem domain only: the local <c>FileSystemEntryId</c> carrying this record when it differs
/// from the record id (an entry created on this device by a pull gets a fresh local id).
/// </param>
/// <param name="IsTombstone">Whether that revision is a deletion (ADR 0101 section 1).</param>
/// <param name="UserLogin">
/// FileSystem domain only: login of the user whose home holds the entry. A deletion is detected only
/// for the records of the user being synchronized; a state with no login (written before ADR 0101)
/// is never turned into a tombstone.
/// </param>
public sealed record SyncRecordTrackingState(
    long Revision,
    string ContentHash,
    string? RelativePath = null,
    Guid? LocalEntryId = null,
    bool IsTombstone = false,
    string? UserLogin = null);

/// <summary>One tracked record of a domain, as listed by <see cref="ISyncRecordStateRepository.ListAsync"/>.</summary>
/// <param name="RecordId">Sync record id.</param>
/// <param name="State">Its tracked state.</param>
public sealed record SyncTrackedRecord(Guid RecordId, SyncRecordTrackingState State);

/// <summary>
/// Persists one opaque pull cursor per sync domain (ADR 0029). Domain-agnostic — shared by every
/// sync-domain pass, not specific to any one domain.
/// </summary>
public interface ISyncCursorRepository
{
    /// <summary>Gets the stored cursor for <paramref name="domain"/>, or <see langword="null"/> if never pulled.</summary>
    ValueTask<string?> GetCursorAsync(string domain, CancellationToken cancellationToken = default);

    /// <summary>Persists the cursor for <paramref name="domain"/>, replacing any prior value.</summary>
    ValueTask SetCursorAsync(string domain, string? cursor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets when <paramref name="domain"/> last finished a pull on this device, or
    /// <see langword="null"/> if never recorded. Drives the full resynchronization of a device
    /// that stayed offline longer than the server keeps tombstones (ADR 0101 section 1).
    /// </summary>
    ValueTask<DateTimeOffset?> GetLastPulledAtAsync(string domain, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<DateTimeOffset?>(null);

    /// <summary>Records when <paramref name="domain"/> last finished a pull.</summary>
    ValueTask SetLastPulledAtAsync(string domain, DateTimeOffset pulledAtUtc, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
}

/// <summary>
/// Persists <see cref="SyncRecordTrackingState"/> per <c>(domain, recordId)</c> pair (ADR 0029).
/// Domain-agnostic like <see cref="ISyncCursorRepository"/>.
/// </summary>
public interface ISyncRecordStateRepository
{
    /// <summary>Gets the tracked state for one record, or <see langword="null"/> if never synced.</summary>
    ValueTask<SyncRecordTrackingState?> GetAsync(string domain, Guid recordId, CancellationToken cancellationToken = default);

    /// <summary>Persists the tracked state for one record, replacing any prior value.</summary>
    ValueTask SetAsync(string domain, Guid recordId, SyncRecordTrackingState state, CancellationToken cancellationToken = default);

    /// <summary>Persists a revision and hash only (the shape every domain but FileSystem uses).</summary>
    ValueTask SetAsync(string domain, Guid recordId, long revision, string contentHash, CancellationToken cancellationToken = default) =>
        SetAsync(domain, recordId, new SyncRecordTrackingState(revision, contentHash), cancellationToken);

    /// <summary>Lists every tracked record of <paramref name="domain"/> (ADR 0101: deletion detection).</summary>
    ValueTask<IReadOnlyList<SyncTrackedRecord>> ListAsync(string domain, CancellationToken cancellationToken = default);

    /// <summary>Forgets one record; a missing record is not an error.</summary>
    ValueTask RemoveAsync(string domain, Guid recordId, CancellationToken cancellationToken = default);
}
