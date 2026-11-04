using System.Text.Json;
using Kazo.Os.Server.Contracts.Sync;
using Kazo.Os.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Kazo.Os.Server.Services;

/// <summary>
/// Record-level versioned push/pull sync with explicit conflict resolution.
/// Implements P5-SYNC-001 through P5-SYNC-006 and ADR 0025.
/// </summary>
public interface ISyncService
{
    Task<PullResponse> PullAsync(Guid accountId, PullRequest request, CancellationToken ct);
    Task<PushResponse> PushAsync(Guid accountId, Guid deviceId, PushRequest request, CancellationToken ct);
    Task<ResolveSyncConflictResponse> ResolveConflictAsync(
        Guid accountId, ResolveSyncConflictRequest request, CancellationToken ct);

    /// <summary>
    /// Deletes every record whose latest revision is a tombstone received more than
    /// <see cref="SyncService.TombstoneRetention"/> before <paramref name="nowUtc"/> (ADR 0101
    /// section 1), with all its older revisions. A device offline for longer resynchronizes fully.
    /// </summary>
    /// <returns>The number of records purged.</returns>
    Task<int> PurgeExpiredTombstonesAsync(DateTimeOffset nowUtc, CancellationToken ct);
}

/// <inheritdoc />
public sealed class SyncService : ISyncService
{
    private readonly KazoOsServerDbContext _db;
    private readonly IAuditService _audit;

    // Idempotency cache: maps idempotency key → PushResponse (in-memory, evicted after 5 minutes).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (DateTimeOffset Expires, PushResponse Response)>
        _idempotencyCache = new();

    /// <inheritdoc />
    public SyncService(KazoOsServerDbContext db, IAuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    /// <inheritdoc />
    public async Task<PullResponse> PullAsync(Guid accountId, PullRequest request, CancellationToken ct)
    {
        // Validate domain.
        if (!IsKnownDomain(request.Domain))
            throw new ArgumentException($"Unknown sync domain: {request.Domain}", nameof(request));

        // Decode opaque cursor → server sequence number.
        long afterSequence = DecodeCursor(request.Cursor);

        // Clamp MaxRecords to [1, 500].
        int limit = Math.Clamp(request.MaxRecords, 1, 500);

        // Pull records for this account+domain after the cursor, ordered by ServerSequence.
        // We fetch limit+1 to detect HasMore without a separate COUNT.
        var rawRows = await _db.SyncRecords
            .Where(r => r.OwnerAccountId == accountId
                     && r.Domain == request.Domain
                     && r.ServerSequence > afterSequence)
            .OrderBy(r => r.ServerSequence)
            .Take(limit + 1)
            .ToListAsync(ct);

        bool hasMore = rawRows.Count > limit;
        var rows = rawRows.Take(limit).ToList();

        string? nextCursor = rows.Count > 0
            ? EncodeCursor(rows[^1].ServerSequence)
            : request.Cursor;

        var records = rows.Select(MapToEnvelope).ToList();

        return new PullResponse(request.Domain, records, hasMore ? nextCursor : null, hasMore, DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public async Task<PushResponse> PushAsync(
        Guid accountId, Guid deviceId, PushRequest request, CancellationToken ct)
    {
        // Idempotency check — return cached response for replayed batches.
        EvictExpiredIdempotencyEntries();
        if (_idempotencyCache.TryGetValue(request.IdempotencyKey, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
            return cached.Response with { Outcome = PushOutcome.AlreadyProcessed };

        if (!IsKnownDomain(request.Domain))
            throw new ArgumentException($"Unknown sync domain: {request.Domain}", nameof(request));

        // Enforce max batch size.
        if (request.Records.Count > 100)
            throw new ArgumentException("Batch exceeds maximum of 100 records.", nameof(request));

        var conflicts = new List<SyncConflict>();
        int acceptedCount = 0;
        var now = DateTimeOffset.UtcNow;
        long nextSequence = await GetNextServerSequenceAsync(ct);

        foreach (var envelope in request.Records)
        {
            // Validate content hash.
            if (!VerifyHash(envelope))
            {
                conflicts.Add(new SyncConflict(
                    envelope.RecordId, envelope.Revision, -1, now, "HASH_MISMATCH"));
                continue;
            }

            // Check for grant/policy security blocks (P5-SYNC-004).
            if (IsSecuritySensitiveDomain(request.Domain) && envelope.IsTombstone)
            {
                // Grants and policy records can never be deleted by the client.
                conflicts.Add(new SyncConflict(
                    envelope.RecordId, envelope.Revision, -1, now, "GRANT_SECURITY_BLOCK"));
                continue;
            }

            // Find the current server revision for this record.
            var currentRow = await _db.SyncRecords
                .Where(r => r.RecordId == envelope.RecordId && r.Domain == request.Domain)
                .OrderByDescending(r => r.Revision)
                .FirstOrDefaultAsync(ct);

            if (currentRow is not null && currentRow.Revision >= envelope.Revision)
            {
                // Concurrent edit detected — surface as explicit conflict.
                conflicts.Add(new SyncConflict(
                    envelope.RecordId, envelope.Revision,
                    currentRow.Revision, currentRow.ModifiedUtc,
                    GetConflictCode(request.Domain)));
                continue;
            }

            // Accept the record.
            _db.SyncRecords.Add(new SyncRecordEntity
            {
                RecordId = envelope.RecordId,
                Domain = envelope.Domain,
                SchemaVersion = envelope.SchemaVersion,
                OwnerAccountId = accountId,
                OriginDeviceId = envelope.OriginDeviceId,
                Revision = envelope.Revision,
                ModifiedUtc = envelope.ModifiedUtc,
                ServerReceivedUtc = now,
                ServerSequence = nextSequence++,
                ContentHash = envelope.ContentHash,
                IsTombstone = envelope.IsTombstone,
                PayloadJson = envelope.PayloadJson
            });
            acceptedCount++;
        }

        await _db.SaveChangesAsync(ct);

        var outcome = conflicts.Count > 0
            ? PushOutcome.ConflictsDetected
            : PushOutcome.Accepted;

        var response = new PushResponse(outcome, acceptedCount, conflicts);

        // Cache for idempotency.
        _idempotencyCache[request.IdempotencyKey] = (DateTimeOffset.UtcNow.AddMinutes(5), response);

        return response;
    }

    /// <inheritdoc />
    public async Task<ResolveSyncConflictResponse> ResolveConflictAsync(
        Guid accountId, ResolveSyncConflictRequest request, CancellationToken ct)
    {
        // Security block: grants and policy always use ServerWins.
        if (IsSecuritySensitiveDomain(request.Domain)
            && request.Resolution != ConflictResolution.ServerWins)
        {
            return new ResolveSyncConflictResponse(false,
                "Security-sensitive domain; only ServerWins is permitted.", -1);
        }

        var serverRow = await _db.SyncRecords
            .Where(r => r.RecordId == request.RecordId && r.Domain == request.Domain)
            .OrderByDescending(r => r.Revision)
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException($"Record {request.RecordId} not found in domain {request.Domain}.");

        long finalRevision = serverRow.Revision;
        var now = DateTimeOffset.UtcNow;
        long nextSeq = await GetNextServerSequenceAsync(ct);

        switch (request.Resolution)
        {
            case ConflictResolution.ServerWins:
                // No change needed; server record is already authoritative.
                break;

            case ConflictResolution.ClientWins:
                // The client's submitted merged payload replaces the server record.
                if (request.MergedPayloadJson is null)
                    return new ResolveSyncConflictResponse(false, "MergedPayloadJson is required for ClientWins.", -1);

                finalRevision = serverRow.Revision + 1;
                _db.SyncRecords.Add(new SyncRecordEntity
                {
                    RecordId = request.RecordId,
                    Domain = request.Domain,
                    SchemaVersion = serverRow.SchemaVersion,
                    OwnerAccountId = accountId,
                    OriginDeviceId = serverRow.OriginDeviceId,
                    Revision = finalRevision,
                    ModifiedUtc = now,
                    ServerReceivedUtc = now,
                    ServerSequence = nextSeq,
                    ContentHash = request.MergedContentHash ?? serverRow.ContentHash,
                    IsTombstone = false,
                    PayloadJson = request.MergedPayloadJson
                });
                await _db.SaveChangesAsync(ct);
                break;

            case ConflictResolution.Merge:
                // Domain-specific merge: only allowed for Settings and FileSystem.
                if (!IsMergeSupportedDomain(request.Domain))
                    return new ResolveSyncConflictResponse(false, $"Merge is not supported for domain '{request.Domain}'.", -1);

                if (request.MergedPayloadJson is null || request.MergedContentHash is null)
                    return new ResolveSyncConflictResponse(false, "MergedPayloadJson and MergedContentHash are required for Merge.", -1);

                finalRevision = serverRow.Revision + 1;
                _db.SyncRecords.Add(new SyncRecordEntity
                {
                    RecordId = request.RecordId,
                    Domain = request.Domain,
                    SchemaVersion = serverRow.SchemaVersion,
                    OwnerAccountId = accountId,
                    OriginDeviceId = serverRow.OriginDeviceId,
                    Revision = finalRevision,
                    ModifiedUtc = now,
                    ServerReceivedUtc = now,
                    ServerSequence = nextSeq,
                    ContentHash = request.MergedContentHash,
                    IsTombstone = false,
                    PayloadJson = request.MergedPayloadJson
                });
                await _db.SaveChangesAsync(ct);
                break;
        }

        await _audit.RecordAsync(accountId, null, "SYNC_CONFLICT_RESOLVED",
            $"{{\"recordId\":\"{request.RecordId}\",\"domain\":\"{request.Domain}\",\"resolution\":\"{request.Resolution}\"}}",
            ct);

        return new ResolveSyncConflictResponse(true, null, finalRevision);
    }

    /// <summary>How long a tombstone is kept (ADR 0101 section 1, deferred decision 7).</summary>
    public static readonly TimeSpan TombstoneRetention = TimeSpan.FromDays(90);

    /// <inheritdoc />
    public async Task<int> PurgeExpiredTombstonesAsync(DateTimeOffset nowUtc, CancellationToken ct)
    {
        DateTimeOffset threshold = nowUtc - TombstoneRetention;

        // The row holding the highest sequence is never deleted: the next sequence is computed as
        // max + 1, and reusing a number a device's cursor already passed would hide new records.
        long? highestSequence = await _db.SyncRecords.MaxAsync(r => (long?)r.ServerSequence, ct);

        // Only tombstone rows are candidates; the date is compared in memory because SQLite cannot
        // translate DateTimeOffset comparisons.
        var candidates = (await _db.SyncRecords
                .Where(r => r.IsTombstone)
                .Select(r => new { r.RecordId, r.Domain, r.Revision, r.ServerReceivedUtc, r.ServerSequence })
                .ToListAsync(ct))
            .Where(r => r.ServerReceivedUtc < threshold && r.ServerSequence != highestSequence)
            .ToList();

        int purged = 0;
        foreach (var candidate in candidates)
        {
            // Purged only if that tombstone is still the latest revision (not resurrected since).
            var rows = await _db.SyncRecords
                .Where(r => r.RecordId == candidate.RecordId && r.Domain == candidate.Domain)
                .ToListAsync(ct);
            if (rows.Count == 0 || rows.Max(r => r.Revision) != candidate.Revision)
                continue;

            _db.SyncRecords.RemoveRange(rows);
            purged++;
        }

        if (purged > 0)
            await _db.SaveChangesAsync(ct);
        return purged;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static bool IsKnownDomain(string domain) =>
        domain is SyncDomain.Settings or SyncDomain.FileSystem or SyncDomain.Grants
               or SyncDomain.AppCatalog or SyncDomain.FileAssociations;

    private static bool IsSecuritySensitiveDomain(string domain) =>
        domain is SyncDomain.Grants;

    private static bool IsMergeSupportedDomain(string domain) =>
        domain is SyncDomain.Settings or SyncDomain.FileSystem;

    private static string GetConflictCode(string domain) =>
        domain == SyncDomain.Grants ? "GRANT_CONCURRENT_EDIT" : "CONCURRENT_EDIT";

    /// <summary>Decodes an opaque pull cursor back to a server sequence number.</summary>
    private static long DecodeCursor(string? cursor)
    {
        if (cursor is null) return 0;
        try
        {
            var bytes = Convert.FromBase64String(cursor);
            return BitConverter.ToInt64(bytes, 0);
        }
        catch
        {
            return 0; // Invalid cursor → full refresh.
        }
    }

    /// <summary>Encodes a server sequence number as an opaque Base64 cursor.</summary>
    private static string EncodeCursor(long sequence) =>
        Convert.ToBase64String(BitConverter.GetBytes(sequence));

    private static SyncRecordEnvelope MapToEnvelope(SyncRecordEntity row) =>
        new(row.RecordId, row.Domain, row.SchemaVersion, row.OwnerAccountId,
            row.OriginDeviceId, row.Revision, row.ModifiedUtc, row.ServerReceivedUtc,
            row.ContentHash, row.IsTombstone, row.PayloadJson);

    private async Task<long> GetNextServerSequenceAsync(CancellationToken ct)
    {
        var max = await _db.SyncRecords.MaxAsync(r => (long?)r.ServerSequence, ct) ?? 0;
        return max + 1;
    }

    private static bool VerifyHash(SyncRecordEnvelope envelope)
    {
        if (envelope.PayloadJson is null) return true; // tombstone; no content to verify
        var bytes = System.Text.Encoding.UTF8.GetBytes(envelope.PayloadJson);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        var computed = Convert.ToHexString(hash).ToLowerInvariant();
        return string.Equals(computed, envelope.ContentHash, StringComparison.OrdinalIgnoreCase);
    }

    private static void EvictExpiredIdempotencyEntries()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var key in _idempotencyCache.Keys.ToList())
        {
            if (_idempotencyCache.TryGetValue(key, out var entry) && entry.Expires <= now)
                _idempotencyCache.TryRemove(key, out _);
        }
    }
}
