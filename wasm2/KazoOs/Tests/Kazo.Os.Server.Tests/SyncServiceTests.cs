using Kazo.Os.Server.Contracts.Sync;
using Kazo.Os.Server.Data;
using Kazo.Os.Server.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kazo.Os.Server.Tests;

// =============================================================================
// Sync Service Tests — P5-SYNC-006
// =============================================================================

public sealed class SyncServiceTests : IDisposable
{
    private readonly KazoOsServerDbContext _db;
    private readonly SyncService _svc;
    private readonly AuditService _audit;

    private static readonly Guid AccountA = Guid.NewGuid();
    private static readonly Guid DeviceA = Guid.NewGuid();

    public SyncServiceTests()
    {
        var options = new DbContextOptionsBuilder<KazoOsServerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new KazoOsServerDbContext(options);
        _audit = new AuditService(_db);
        _svc = new SyncService(_db, _audit);
    }

    public void Dispose() => _db.Dispose();

    // ── Pull tests ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Pull_EmptyDomain_ReturnsEmptyWithNullCursor()
    {
        var result = await _svc.PullAsync(AccountA,
            new PullRequest(SyncDomain.Settings, null, 100), CancellationToken.None);

        Assert.Equal(SyncDomain.Settings, result.Domain);
        Assert.Empty(result.Records);
        Assert.False(result.HasMore);
        Assert.Null(result.NextCursor);
    }

    [Fact]
    public async Task Pull_AfterPush_ReturnsAcceptedRecord()
    {
        // Arrange: push one record.
        var recordId = Guid.NewGuid();
        var envelope = BuildEnvelope(recordId, SyncDomain.Settings, 1, AccountA, DeviceA, false, "{\"key\":\"val\"}");
        await _svc.PushAsync(AccountA, DeviceA,
            new PushRequest(SyncDomain.Settings, [envelope], Guid.NewGuid()),
            CancellationToken.None);

        // Act: pull.
        var result = await _svc.PullAsync(AccountA,
            new PullRequest(SyncDomain.Settings, null, 100), CancellationToken.None);

        Assert.Single(result.Records);
        Assert.Equal(recordId, result.Records[0].RecordId);
    }

    [Fact]
    public async Task Pull_WithCursor_ReturnsOnlyNewRecords()
    {
        // Push two records.
        var r1 = Guid.NewGuid();
        var r2 = Guid.NewGuid();
        var push1 = BuildEnvelope(r1, SyncDomain.Settings, 1, AccountA, DeviceA, false, "{\"a\":1}");
        var push2 = BuildEnvelope(r2, SyncDomain.Settings, 1, AccountA, DeviceA, false, "{\"b\":2}");
        await _svc.PushAsync(AccountA, DeviceA,
            new PushRequest(SyncDomain.Settings, [push1], Guid.NewGuid()), CancellationToken.None);
        await _svc.PushAsync(AccountA, DeviceA,
            new PushRequest(SyncDomain.Settings, [push2], Guid.NewGuid()), CancellationToken.None);

        // Pull first page (limit=1).
        var page1 = await _svc.PullAsync(AccountA,
            new PullRequest(SyncDomain.Settings, null, 1), CancellationToken.None);

        Assert.Single(page1.Records);
        Assert.True(page1.HasMore);
        Assert.NotNull(page1.NextCursor);

        // Pull second page with cursor.
        var page2 = await _svc.PullAsync(AccountA,
            new PullRequest(SyncDomain.Settings, page1.NextCursor, 100), CancellationToken.None);

        Assert.Single(page2.Records);
        Assert.False(page2.HasMore);
    }

    [Fact]
    public async Task Pull_RejectsUnknownDomain()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _svc.PullAsync(AccountA, new PullRequest("unknown_domain", null, 100), CancellationToken.None));
    }

    // ── Push tests ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Push_ValidRecord_ReturnsAccepted()
    {
        var recordId = Guid.NewGuid();
        var envelope = BuildEnvelope(recordId, SyncDomain.Settings, 1, AccountA, DeviceA, false, "{\"x\":1}");
        var response = await _svc.PushAsync(AccountA, DeviceA,
            new PushRequest(SyncDomain.Settings, [envelope], Guid.NewGuid()),
            CancellationToken.None);

        Assert.Equal(PushOutcome.Accepted, response.Outcome);
        Assert.Equal(1, response.AcceptedCount);
        Assert.Empty(response.Conflicts);
    }

    [Fact]
    public async Task Push_DuplicateIdempotencyKey_ReturnsAlreadyProcessed()
    {
        var idempotencyKey = Guid.NewGuid();
        var envelope = BuildEnvelope(Guid.NewGuid(), SyncDomain.Settings, 1, AccountA, DeviceA, false, "{\"x\":1}");

        await _svc.PushAsync(AccountA, DeviceA,
            new PushRequest(SyncDomain.Settings, [envelope], idempotencyKey), CancellationToken.None);

        var response2 = await _svc.PushAsync(AccountA, DeviceA,
            new PushRequest(SyncDomain.Settings, [envelope], idempotencyKey), CancellationToken.None);

        Assert.Equal(PushOutcome.AlreadyProcessed, response2.Outcome);
    }

    [Fact]
    public async Task Push_ConcurrentEdit_ReturnsConflict()
    {
        var recordId = Guid.NewGuid();

        // Push revision 1.
        var envelope1 = BuildEnvelope(recordId, SyncDomain.Settings, 1, AccountA, DeviceA, false, "{\"v\":1}");
        await _svc.PushAsync(AccountA, DeviceA,
            new PushRequest(SyncDomain.Settings, [envelope1], Guid.NewGuid()), CancellationToken.None);

        // Push revision 1 again (same revision → conflict).
        var envelope2 = BuildEnvelope(recordId, SyncDomain.Settings, 1, AccountA, DeviceA, false, "{\"v\":2}");
        var response = await _svc.PushAsync(AccountA, DeviceA,
            new PushRequest(SyncDomain.Settings, [envelope2], Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(PushOutcome.ConflictsDetected, response.Outcome);
        Assert.Single(response.Conflicts);
        Assert.Equal(recordId, response.Conflicts[0].RecordId);
    }

    [Fact]
    public async Task Push_Tombstone_ForGrantDomain_IsBlocked()
    {
        var recordId = Guid.NewGuid();
        var envelope = BuildEnvelope(recordId, SyncDomain.Grants, 1, AccountA, DeviceA, true, null);
        var response = await _svc.PushAsync(AccountA, DeviceA,
            new PushRequest(SyncDomain.Grants, [envelope], Guid.NewGuid()), CancellationToken.None);

        // Tombstone on grants domain must be blocked with GRANT_SECURITY_BLOCK.
        Assert.Equal(PushOutcome.ConflictsDetected, response.Outcome);
        Assert.Equal("GRANT_SECURITY_BLOCK", response.Conflicts[0].ConflictCode);
    }

    [Fact]
    public async Task Push_HashMismatch_ReturnsHashMismatchConflict()
    {
        var recordId = Guid.NewGuid();
        // Deliberate wrong hash.
        var envelope = new SyncRecordEnvelope(
            recordId, SyncDomain.Settings, 1, AccountA, DeviceA, 1,
            DateTimeOffset.UtcNow, null, "badhash", false, "{\"x\":1}");

        var response = await _svc.PushAsync(AccountA, DeviceA,
            new PushRequest(SyncDomain.Settings, [envelope], Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(PushOutcome.ConflictsDetected, response.Outcome);
        Assert.Equal("HASH_MISMATCH", response.Conflicts[0].ConflictCode);
    }

    // ── Conflict resolution tests ─────────────────────────────────────────────

    [Fact]
    public async Task ResolveConflict_ServerWins_ResolvesWithoutChange()
    {
        var recordId = Guid.NewGuid();
        var envelope = BuildEnvelope(recordId, SyncDomain.Settings, 1, AccountA, DeviceA, false, "{\"v\":1}");
        await _svc.PushAsync(AccountA, DeviceA,
            new PushRequest(SyncDomain.Settings, [envelope], Guid.NewGuid()), CancellationToken.None);

        var result = await _svc.ResolveConflictAsync(AccountA,
            new ResolveSyncConflictRequest(recordId, SyncDomain.Settings, ConflictResolution.ServerWins, null, null),
            CancellationToken.None);

        Assert.True(result.Resolved);
    }

    [Fact]
    public async Task ResolveConflict_GrantDomain_OnlyServerWinsAllowed()
    {
        var recordId = Guid.NewGuid();
        var envelope = BuildEnvelope(recordId, SyncDomain.Grants, 1, AccountA, DeviceA, false, "{\"grant\":\"test\"}");
        await _svc.PushAsync(AccountA, DeviceA,
            new PushRequest(SyncDomain.Grants, [envelope], Guid.NewGuid()), CancellationToken.None);

        var result = await _svc.ResolveConflictAsync(AccountA,
            new ResolveSyncConflictRequest(recordId, SyncDomain.Grants, ConflictResolution.ClientWins,
                "{\"grant\":\"override\"}", null),
            CancellationToken.None);

        Assert.False(result.Resolved);
        Assert.Contains("Security-sensitive", result.Reason);
    }

    [Fact]
    public async Task ResolveConflict_Merge_NotAllowedForGrantsDomain()
    {
        var recordId = Guid.NewGuid();
        var envelope = BuildEnvelope(recordId, SyncDomain.Grants, 1, AccountA, DeviceA, false, "{\"grant\":\"test\"}");
        await _svc.PushAsync(AccountA, DeviceA,
            new PushRequest(SyncDomain.Grants, [envelope], Guid.NewGuid()), CancellationToken.None);

        var result = await _svc.ResolveConflictAsync(AccountA,
            new ResolveSyncConflictRequest(recordId, SyncDomain.Grants, ConflictResolution.Merge,
                "{\"merged\":true}", "abc123"),
            CancellationToken.None);

        Assert.False(result.Resolved);
    }

    // ── Tombstone purge (ADR 0101 section 1) ─────────────────────────────────

    [Fact]
    public async Task PurgeExpiredTombstones_RemovesEveryRowOfARecordDeletedMoreThan90DaysAgo()
    {
        var deleted = Guid.NewGuid();
        var alive = Guid.NewGuid();
        await PushAsync(BuildEnvelope(deleted, SyncDomain.FileSystem, 1, AccountA, DeviceA, false, "{\"relativePath\":\"a\"}"));
        await PushAsync(BuildEnvelope(deleted, SyncDomain.FileSystem, 2, AccountA, DeviceA, true, null));
        await PushAsync(BuildEnvelope(alive, SyncDomain.FileSystem, 1, AccountA, DeviceA, false, "{\"relativePath\":\"b\"}"));

        Assert.Equal(0, await _svc.PurgeExpiredTombstonesAsync(DateTimeOffset.UtcNow.AddDays(89), CancellationToken.None));
        Assert.Equal(1, await _svc.PurgeExpiredTombstonesAsync(DateTimeOffset.UtcNow.AddDays(91), CancellationToken.None));

        Assert.DoesNotContain(_db.SyncRecords, r => r.RecordId == deleted);
        Assert.Contains(_db.SyncRecords, r => r.RecordId == alive);
    }

    [Fact]
    public async Task PurgeExpiredTombstones_KeepsTheRowHoldingTheHighestSequence()
    {
        // Removing it would let the next push reuse a sequence number a device cursor already passed.
        var deleted = Guid.NewGuid();
        await PushAsync(BuildEnvelope(deleted, SyncDomain.FileSystem, 1, AccountA, DeviceA, false, "{\"relativePath\":\"a\"}"));
        await PushAsync(BuildEnvelope(deleted, SyncDomain.FileSystem, 2, AccountA, DeviceA, true, null));

        Assert.Equal(0, await _svc.PurgeExpiredTombstonesAsync(DateTimeOffset.UtcNow.AddDays(91), CancellationToken.None));
        Assert.Equal(2, _db.SyncRecords.Count(r => r.RecordId == deleted));
    }

    private Task<PushResponse> PushAsync(SyncRecordEnvelope envelope) =>
        _svc.PushAsync(AccountA, DeviceA, new PushRequest(envelope.Domain, [envelope], Guid.NewGuid()), CancellationToken.None);

    // ── Helper ────────────────────────────────────────────────────────────────

    private static SyncRecordEnvelope BuildEnvelope(
        Guid recordId, string domain, int revision,
        Guid accountId, Guid deviceId, bool tombstone, string? payload)
    {
        string hash = payload is null
            ? "da39a3ee5e6b4b0d3255bfef95601890afd80709" // SHA-256("") placeholder for tombstone
            : Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

        return new SyncRecordEnvelope(
            recordId, domain, 1, accountId, deviceId,
            revision, DateTimeOffset.UtcNow, null,
            hash, tombstone, payload);
    }
}
