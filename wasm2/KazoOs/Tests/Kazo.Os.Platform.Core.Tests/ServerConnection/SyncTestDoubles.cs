using Kazo.Os.Platform.Core.ServerConnection;
using Kazo.Os.Server.Contracts.Sync;
using Kazo.Os.Simulation.Abstractions.Sync;

namespace Kazo.Os.Platform.Core.Tests.ServerConnection;

/// <summary>In-memory <see cref="ISyncCursorRepository"/> shared by the sync tests.</summary>
internal sealed class InMemorySyncCursorRepository : ISyncCursorRepository
{
    private readonly Dictionary<string, string?> _cursors = [];
    private readonly Dictionary<string, DateTimeOffset> _pulledAt = [];

    public ValueTask<string?> GetCursorAsync(string domain, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_cursors.GetValueOrDefault(domain));

    public ValueTask SetCursorAsync(string domain, string? cursor, CancellationToken cancellationToken = default)
    {
        _cursors[domain] = cursor;
        return ValueTask.CompletedTask;
    }

    public ValueTask<DateTimeOffset?> GetLastPulledAtAsync(string domain, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<DateTimeOffset?>(_pulledAt.TryGetValue(domain, out DateTimeOffset value) ? value : null);

    public ValueTask SetLastPulledAtAsync(string domain, DateTimeOffset pulledAtUtc, CancellationToken cancellationToken = default)
    {
        _pulledAt[domain] = pulledAtUtc;
        return ValueTask.CompletedTask;
    }
}

/// <summary>In-memory <see cref="ISyncRecordStateRepository"/> shared by the sync tests.</summary>
internal sealed class InMemorySyncRecordStateRepository : ISyncRecordStateRepository
{
    private readonly Dictionary<(string Domain, Guid RecordId), SyncRecordTrackingState> _state = [];

    public ValueTask<SyncRecordTrackingState?> GetAsync(string domain, Guid recordId, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_state.GetValueOrDefault((domain, recordId)));

    public ValueTask SetAsync(string domain, Guid recordId, SyncRecordTrackingState state, CancellationToken cancellationToken = default)
    {
        _state[(domain, recordId)] = state;
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<SyncTrackedRecord>> ListAsync(string domain, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<SyncTrackedRecord>>(
            [.. _state.Where(pair => pair.Key.Domain == domain).Select(pair => new SyncTrackedRecord(pair.Key.RecordId, pair.Value))]);

    public ValueTask RemoveAsync(string domain, Guid recordId, CancellationToken cancellationToken = default)
    {
        _state.Remove((domain, recordId));
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// An in-memory stand-in for the optional server's sync and content endpoints with the same rules
/// as <c>Kazo.Os.Server.Services.SyncService</c> (revision check, tombstones, ClientWins/ServerWins,
/// sequence cursors), so two simulated devices can synchronize through it.
/// </summary>
internal sealed class InMemorySyncServer : ISyncClient, Platform.Core.ServerConnection.IContentTransferClient
{
    private const int ChunkSize = 256 * 1024;
    private readonly List<(long Sequence, SyncRecordEnvelope Envelope)> _rows = [];
    private readonly Dictionary<string, byte[]> _blobs = [];
    private readonly Dictionary<string, (string Hash, List<byte[]> Chunks, int Total)> _uploads = [];
    private long _sequence;

    public List<ResolveSyncConflictRequest> Resolutions { get; } = [];

    public List<SyncRecordEnvelope> Rows => [.. _rows.Select(row => row.Envelope)];

    /// <summary>Latest envelope of a record, or <see langword="null"/>.</summary>
    public SyncRecordEnvelope? Latest(Guid recordId) =>
        _rows.Where(row => row.Envelope.RecordId == recordId).OrderByDescending(row => row.Envelope.Revision).Select(row => row.Envelope).FirstOrDefault();

    /// <summary>Drops every row of a record, like the 90-day tombstone purge.</summary>
    public void Purge(Guid recordId) => _rows.RemoveAll(row => row.Envelope.RecordId == recordId);

    public Task<PullResponse> PullAsync(Uri serverBaseUrl, string accessToken, PullRequest request, CancellationToken cancellationToken = default)
    {
        long after = request.Cursor is null ? 0 : long.Parse(request.Cursor, System.Globalization.CultureInfo.InvariantCulture);
        List<(long Sequence, SyncRecordEnvelope Envelope)> page = [.. _rows
            .Where(row => row.Envelope.Domain == request.Domain && row.Sequence > after)
            .OrderBy(row => row.Sequence)
            .Take(request.MaxRecords + 1)];
        bool hasMore = page.Count > request.MaxRecords;
        page = [.. page.Take(request.MaxRecords)];
        string? next = page.Count > 0 ? page[^1].Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture) : request.Cursor;
        return Task.FromResult(new PullResponse(request.Domain, [.. page.Select(row => row.Envelope)], next, hasMore, DateTimeOffset.UtcNow));
    }

    public Task<PushResponse> PushAsync(Uri serverBaseUrl, string accessToken, PushRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Records.Count > 100)
        {
            throw new ArgumentException("Batch exceeds maximum of 100 records.");
        }

        List<SyncConflict> conflicts = [];
        int accepted = 0;
        foreach (SyncRecordEnvelope envelope in request.Records)
        {
            SyncRecordEnvelope? current = Latest(envelope.RecordId);
            if (current is not null && current.Revision >= envelope.Revision)
            {
                conflicts.Add(new SyncConflict(envelope.RecordId, envelope.Revision, current.Revision, current.ModifiedUtc, "CONCURRENT_EDIT"));
                continue;
            }

            _rows.Add((++_sequence, envelope with { ServerReceivedUtc = DateTimeOffset.UtcNow }));
            accepted++;
        }

        return Task.FromResult(new PushResponse(
            conflicts.Count > 0 ? PushOutcome.ConflictsDetected : PushOutcome.Accepted, accepted, conflicts));
    }

    public Task<ResolveSyncConflictResponse> ResolveConflictAsync(
        Uri serverBaseUrl, string accessToken, ResolveSyncConflictRequest request, CancellationToken cancellationToken = default)
    {
        Resolutions.Add(request);
        SyncRecordEnvelope current = Latest(request.RecordId) ?? throw new KeyNotFoundException();
        if (request.Resolution == ConflictResolution.ServerWins)
        {
            return Task.FromResult(new ResolveSyncConflictResponse(true, null, current.Revision));
        }

        SyncRecordEnvelope winner = current with
        {
            Revision = current.Revision + 1,
            ContentHash = request.MergedContentHash ?? current.ContentHash,
            IsTombstone = false,
            PayloadJson = request.MergedPayloadJson,
            ModifiedUtc = DateTimeOffset.UtcNow
        };
        _rows.Add((++_sequence, winner));
        return Task.FromResult(new ResolveSyncConflictResponse(true, null, winner.Revision));
    }

    public Task<InitiateContentUploadResponse> InitiateUploadAsync(
        Uri serverBaseUrl, string accessToken, InitiateContentUploadRequest request, CancellationToken cancellationToken = default)
    {
        bool exists = _blobs.ContainsKey(request.ContentHash);
        int total = exists ? 0 : (int)Math.Ceiling((double)request.TotalBytes / ChunkSize);
        string session = Guid.NewGuid().ToString("N");
        if (!exists)
        {
            _uploads[session] = (request.ContentHash, [], total);
            if (total == 0)
            {
                _blobs[request.ContentHash] = [];
            }
        }

        return Task.FromResult(new InitiateContentUploadResponse(
            session, request.ContentHash, exists, ChunkSize, total, DateTimeOffset.UtcNow.AddHours(1)));
    }

    public Task<QueryUploadProgressResponse> QueryUploadProgressAsync(
        Uri serverBaseUrl, string accessToken, string uploadSessionId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task UploadChunkAsync(
        Uri serverBaseUrl, string accessToken, string uploadSessionId, int chunkIndex, byte[] chunkData, CancellationToken cancellationToken = default)
    {
        (string hash, List<byte[]> chunks, int total) = _uploads[uploadSessionId];
        chunks.Add(chunkData);
        if (chunks.Count == total)
        {
            _blobs[hash] = [.. chunks.SelectMany(chunk => chunk)];
        }

        return Task.CompletedTask;
    }

    public Task<InitiateContentDownloadResponse> InitiateDownloadAsync(
        Uri serverBaseUrl, string accessToken, InitiateContentDownloadRequest request, CancellationToken cancellationToken = default)
    {
        byte[] content = _blobs[request.ContentHash];
        return Task.FromResult(new InitiateContentDownloadResponse(
            request.ContentHash, request.ContentHash, content.Length, content.Length, ChunkSize, DateTimeOffset.UtcNow.AddHours(1)));
    }

    public Task<byte[]> DownloadChunkAsync(
        Uri serverBaseUrl, string accessToken, string contentHash, int chunkIndex, CancellationToken cancellationToken = default)
    {
        byte[] content = _blobs[contentHash];
        int start = chunkIndex * ChunkSize;
        return Task.FromResult(content[start..Math.Min(content.Length, start + ChunkSize)]);
    }
}

/// <summary>A clock the tests move by hand.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}
