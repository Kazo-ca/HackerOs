using System.Text.Json.Serialization;

namespace Kazo.Os.Server.Contracts.Sync;

// =============================================================================
// File Content Transfer Contracts — P5-SYNC-005
//
// File metadata (path, permissions, timestamps) is transferred as a regular
// SyncRecord in the FileSystem domain. Content is transferred separately through
// a chunked, resumable upload/download protocol keyed on the immutable SHA-256
// content hash. This means identical files across devices share one content blob
// on the server (deduplication). Packages sync as immutable content-addressed
// blobs identified solely by hash.
// =============================================================================

/// <summary>
/// Initiates a chunked file content upload.
/// The server creates an upload session and returns the chunk size to use.
/// </summary>
/// <param name="ContentHash">SHA-256 hex hash of the entire file content.</param>
/// <param name="TotalBytes">Total file size in bytes.</param>
/// <param name="OwnerAccountId">Account that owns this content.</param>
public sealed record InitiateContentUploadRequest(
    string ContentHash,
    long TotalBytes,
    Guid OwnerAccountId);

/// <summary>
/// Upload session parameters.
/// </summary>
/// <param name="UploadSessionId">Opaque session ID for subsequent chunk uploads.</param>
/// <param name="ContentHash">Confirmed content hash.</param>
/// <param name="AlreadyExists">True when the server already has this content; no upload needed.</param>
/// <param name="ChunkSizeBytes">Required chunk size for subsequent PUT requests (last chunk may be smaller).</param>
/// <param name="TotalChunks">Total number of chunks expected.</param>
/// <param name="ExpiresUtc">Session expiry; client must complete upload before this time.</param>
public sealed record InitiateContentUploadResponse(
    string UploadSessionId,
    string ContentHash,
    bool AlreadyExists,
    int ChunkSizeBytes,
    int TotalChunks,
    DateTimeOffset ExpiresUtc);

/// <summary>
/// Reports which chunks the server has already received for a session (resume support).
/// </summary>
/// <param name="UploadSessionId">The upload session to query.</param>
public sealed record QueryUploadProgressRequest(
    string UploadSessionId);

/// <summary>
/// Upload progress response.
/// </summary>
/// <param name="UploadSessionId">The queried session.</param>
/// <param name="ReceivedChunks">Zero-based chunk indices already received and verified.</param>
/// <param name="IsComplete">True when all chunks are received and content integrity is verified.</param>
public sealed record QueryUploadProgressResponse(
    string UploadSessionId,
    IReadOnlyList<int> ReceivedChunks,
    bool IsComplete);

/// <summary>
/// Initiates download of a file's content by hash.
/// </summary>
/// <param name="ContentHash">SHA-256 hex hash of the desired content.</param>
/// <param name="StartByteOffset">Byte offset to resume from (0 for fresh download).</param>
public sealed record InitiateContentDownloadRequest(
    string ContentHash,
    long StartByteOffset);

/// <summary>
/// Content download session.
/// </summary>
/// <param name="DownloadSessionId">Opaque session ID for subsequent GET chunk requests.</param>
/// <param name="ContentHash">Confirmed hash.</param>
/// <param name="TotalBytes">Total content size.</param>
/// <param name="RemainingBytes">Bytes remaining from StartByteOffset.</param>
/// <param name="ChunkSizeBytes">Chunk size for download responses.</param>
/// <param name="ExpiresUtc">Session expiry.</param>
public sealed record InitiateContentDownloadResponse(
    string DownloadSessionId,
    string ContentHash,
    long TotalBytes,
    long RemainingBytes,
    int ChunkSizeBytes,
    DateTimeOffset ExpiresUtc);

/// <summary>
/// Source-generated JSON context for file content transfer contracts.
/// Chunk data is transferred as raw binary (multipart/form-data), not JSON.
/// </summary>
[JsonSerializable(typeof(InitiateContentUploadRequest))]
[JsonSerializable(typeof(InitiateContentUploadResponse))]
[JsonSerializable(typeof(QueryUploadProgressRequest))]
[JsonSerializable(typeof(QueryUploadProgressResponse))]
[JsonSerializable(typeof(IReadOnlyList<int>))]
[JsonSerializable(typeof(InitiateContentDownloadRequest))]
[JsonSerializable(typeof(InitiateContentDownloadResponse))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
public sealed partial class ContentTransferContractsJsonContext : JsonSerializerContext { }
