using Kazo.Os.Server.Contracts.Sync;
using Kazo.Os.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Kazo.Os.Server.Services;

// =============================================================================
// Audit Service — P5-SRV-003
// =============================================================================

/// <summary>Writes security audit entries to the database.</summary>
public interface IAuditService
{
    /// <summary>Records a security-sensitive server event.</summary>
    Task RecordAsync(Guid accountId, Guid? deviceId, string eventCode, string? detailsJson, CancellationToken ct);
}

/// <inheritdoc />
public sealed class AuditService : IAuditService
{
    private readonly KazoOsServerDbContext _db;

    /// <inheritdoc />
    public AuditService(KazoOsServerDbContext db) => _db = db;

    /// <inheritdoc />
    public async Task RecordAsync(Guid accountId, Guid? deviceId, string eventCode, string? detailsJson, CancellationToken ct)
    {
        _db.AuditEntries.Add(new AuditEntryEntity
        {
            AuditId = Guid.NewGuid(),
            AccountId = accountId,
            ActorDeviceId = deviceId,
            EventCode = eventCode,
            OccurredUtc = DateTimeOffset.UtcNow,
            Details = detailsJson
        });
        await _db.SaveChangesAsync(ct);
    }
}

// =============================================================================
// Content Blob Service — P5-SYNC-005
// =============================================================================

/// <summary>Manages chunked file content upload/download sessions.</summary>
public interface IContentBlobService
{
    Task<Contracts.Sync.InitiateContentUploadResponse> InitiateUploadAsync(
        Guid accountId, Contracts.Sync.InitiateContentUploadRequest request, CancellationToken ct);

    Task<Contracts.Sync.QueryUploadProgressResponse> QueryProgressAsync(
        Guid accountId, Contracts.Sync.QueryUploadProgressRequest request, CancellationToken ct);

    Task AcceptChunkAsync(
        Guid accountId, string uploadSessionId, int chunkIndex, byte[] chunkData, CancellationToken ct);

    Task<Contracts.Sync.InitiateContentDownloadResponse> InitiateDownloadAsync(
        Guid accountId, Contracts.Sync.InitiateContentDownloadRequest request, CancellationToken ct);

    /// <summary>
    /// Reads one chunk of previously-uploaded, content-addressed blob data. Content is immutable and
    /// deduplicated by hash, so chunk boundaries are deterministic from <paramref name="contentHash"/>
    /// and <paramref name="chunkIndex"/> alone — no download-session state is needed (ADR 0030).
    /// </summary>
    Task<byte[]> GetChunkAsync(
        Guid accountId, string contentHash, int chunkIndex, CancellationToken ct);
}

/// <inheritdoc />
public sealed class ContentBlobService : IContentBlobService
{
    private const int DefaultChunkSizeBytes = 256 * 1024; // 256 KiB
    private readonly KazoOsServerDbContext _db;
    private readonly IConfiguration _configuration;

    /// <inheritdoc />
    public ContentBlobService(KazoOsServerDbContext db, IConfiguration configuration)
    {
        _db = db;
        _configuration = configuration;
    }

    /// <inheritdoc />
    public async Task<InitiateContentUploadResponse> InitiateUploadAsync(
        Guid accountId, InitiateContentUploadRequest request, CancellationToken ct)
    {
        // Check if we already have this content (deduplication).
        var existing = await _db.ContentBlobs
            .FirstOrDefaultAsync(b => b.ContentHash == request.ContentHash, ct);

        if (existing is not null)
        {
            existing.ReferenceCount++;
            await _db.SaveChangesAsync(ct);
            return new InitiateContentUploadResponse(
                Guid.Empty.ToString(), request.ContentHash, true, DefaultChunkSizeBytes, 0, DateTimeOffset.UtcNow);
        }

        int totalChunks = (int)Math.Ceiling((double)request.TotalBytes / DefaultChunkSizeBytes);
        var session = new UploadSessionEntity
        {
            SessionId = Guid.NewGuid(),
            AccountId = accountId,
            ContentHash = request.ContentHash,
            TotalBytes = request.TotalBytes,
            TotalChunks = totalChunks,
            ChunkSizeBytes = DefaultChunkSizeBytes,
            ReceivedChunksJson = "[]",
            IsComplete = false,
            CreatedUtc = DateTimeOffset.UtcNow,
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(24)
        };
        _db.UploadSessions.Add(session);
        await _db.SaveChangesAsync(ct);

        return new InitiateContentUploadResponse(
            session.SessionId.ToString(), request.ContentHash, false,
            DefaultChunkSizeBytes, totalChunks, session.ExpiresUtc);
    }

    /// <inheritdoc />
    public async Task<QueryUploadProgressResponse> QueryProgressAsync(
        Guid accountId, QueryUploadProgressRequest request, CancellationToken ct)
    {
        if (!Guid.TryParse(request.UploadSessionId, out var sessionId))
            throw new KeyNotFoundException("Invalid upload session ID.");

        var session = await _db.UploadSessions
            .FirstOrDefaultAsync(s => s.SessionId == sessionId && s.AccountId == accountId, ct)
            ?? throw new KeyNotFoundException("Upload session not found.");

        var received = System.Text.Json.JsonSerializer.Deserialize<List<int>>(session.ReceivedChunksJson) ?? [];
        return new QueryUploadProgressResponse(request.UploadSessionId, received, session.IsComplete);
    }

    /// <inheritdoc />
    public async Task AcceptChunkAsync(
        Guid accountId, string uploadSessionId, int chunkIndex, byte[] chunkData, CancellationToken ct)
    {
        if (!Guid.TryParse(uploadSessionId, out var sessionId))
            throw new KeyNotFoundException("Invalid upload session ID.");

        var session = await _db.UploadSessions
            .FirstOrDefaultAsync(s => s.SessionId == sessionId && s.AccountId == accountId, ct)
            ?? throw new KeyNotFoundException("Upload session not found.");

        if (session.IsComplete)
            return; // idempotent

        if (session.ExpiresUtc < DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Upload session has expired.");

        // Write chunk to blob storage.
        var blobRoot = _configuration["BlobStorage:Root"] ?? "blobs";
        var sessionDir = Path.Combine(blobRoot, "uploads", sessionId.ToString("N"));
        Directory.CreateDirectory(sessionDir);
        var chunkPath = Path.Combine(sessionDir, $"chunk_{chunkIndex:D8}.bin");
        await File.WriteAllBytesAsync(chunkPath, chunkData, ct);

        // Update received chunks list.
        var received = System.Text.Json.JsonSerializer.Deserialize<List<int>>(session.ReceivedChunksJson) ?? [];
        if (!received.Contains(chunkIndex))
        {
            received.Add(chunkIndex);
            received.Sort();
        }
        session.ReceivedChunksJson = System.Text.Json.JsonSerializer.Serialize(received);

        // Check if all chunks are received.
        if (received.Count >= session.TotalChunks)
        {
            // Assemble and verify full file.
            var finalPath = Path.Combine(blobRoot, "content", $"{session.ContentHash}.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            await AssembleAndVerifyAsync(session, sessionDir, finalPath, ct);

            session.IsComplete = true;
            _db.ContentBlobs.Add(new ContentBlobEntity
            {
                ContentHash = session.ContentHash,
                TotalBytes = session.TotalBytes,
                StoragePath = finalPath,
                StoredUtc = DateTimeOffset.UtcNow,
                ReferenceCount = 1
            });
        }

        await _db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<InitiateContentDownloadResponse> InitiateDownloadAsync(
        Guid accountId, InitiateContentDownloadRequest request, CancellationToken ct)
    {
        var blob = await _db.ContentBlobs
            .FirstOrDefaultAsync(b => b.ContentHash == request.ContentHash, ct)
            ?? throw new KeyNotFoundException($"Content hash '{request.ContentHash}' not found.");

        long remaining = blob.TotalBytes - request.StartByteOffset;
        return new InitiateContentDownloadResponse(
            Guid.NewGuid().ToString(), request.ContentHash,
            blob.TotalBytes, remaining, DefaultChunkSizeBytes,
            DateTimeOffset.UtcNow.AddHours(1));
    }

    /// <inheritdoc />
    public async Task<byte[]> GetChunkAsync(
        Guid accountId, string contentHash, int chunkIndex, CancellationToken ct)
    {
        var blob = await _db.ContentBlobs
            .FirstOrDefaultAsync(b => b.ContentHash == contentHash, ct)
            ?? throw new KeyNotFoundException($"Content hash '{contentHash}' not found.");

        if (chunkIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkIndex), chunkIndex, "Chunk index cannot be negative.");
        }

        long start = (long)chunkIndex * DefaultChunkSizeBytes;
        if (start >= blob.TotalBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkIndex), chunkIndex, "Chunk index is past the end of the content.");
        }

        int length = (int)Math.Min(DefaultChunkSizeBytes, blob.TotalBytes - start);
        byte[] buffer = new byte[length];

        await using FileStream stream = new(blob.StoragePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        stream.Seek(start, SeekOrigin.Begin);
        await stream.ReadExactlyAsync(buffer, ct);
        return buffer;
    }

    private static async Task AssembleAndVerifyAsync(
        UploadSessionEntity session, string sessionDir, string finalPath, CancellationToken ct)
    {
        using var final = File.OpenWrite(finalPath);
        for (int i = 0; i < session.TotalChunks; i++)
        {
            var chunkPath = Path.Combine(sessionDir, $"chunk_{i:D8}.bin");
            var chunkBytes = await File.ReadAllBytesAsync(chunkPath, ct);
            await final.WriteAsync(chunkBytes, ct);
        }
        final.Close();

        // Verify final SHA-256 against the declared hash.
        var fileBytes = await File.ReadAllBytesAsync(finalPath, ct);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fileBytes)).ToLowerInvariant();
        if (hash != session.ContentHash)
        {
            File.Delete(finalPath);
            throw new InvalidDataException($"Content hash mismatch after assembly. Expected {session.ContentHash}, got {hash}.");
        }

        // Clean up chunk files.
        Directory.Delete(sessionDir, recursive: true);
    }
}
