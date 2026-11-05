using System.Text.Json.Serialization;

namespace Kazo.Os.Server.Contracts.Sync;

// =============================================================================
// ADR 0025 — Record Synchronization: Envelope, Conflict, and Cursor Model
//
// Decision: Every synchronized piece of data is wrapped in a SyncRecord<T>
// envelope that carries stable identity, schema discriminator, revision,
// timestamps, origin device, content hash, and tombstone flag. The server
// never merges silently; all conflicts are surfaced to the client which must
// resolve them via explicit ResolveSyncConflictRequest. Grant and policy
// records obey the server-authoritative conflict rule: the server never
// downgrades grants, and client wins only for non-security-sensitive domains
// (settings, files). Cursors are opaque strings; clients treat them as
// position bookmarks and never parse them.
// =============================================================================

/// <summary>
/// Sync domain discriminators — identify which logical data store the record belongs to.
/// The server applies domain-specific conflict rules based on this value.
/// </summary>
public static class SyncDomain
{
    /// <summary>User settings documents (app/user scope, app/device scope).</summary>
    public const string Settings = "settings";

    /// <summary>Virtual filesystem entries (metadata only; content transferred separately).</summary>
    public const string FileSystem = "filesystem";

    /// <summary>Capability grants and policy — server-authoritative; client never weakens.</summary>
    public const string Grants = "grants";

    /// <summary>App catalog enablement flags (not package binaries).</summary>
    public const string AppCatalog = "app_catalog";

    /// <summary>File association overrides.</summary>
    public const string FileAssociations = "file_associations";
}

/// <summary>
/// Conflict resolution strategy used when the server detects a concurrent edit.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConflictResolution>))]
public enum ConflictResolution
{
    /// <summary>The client's version wins and replaces the server record.</summary>
    ClientWins,

    /// <summary>The server's version wins; the client discards its local edit.</summary>
    ServerWins,

    /// <summary>A domain-specific merge is attempted; available for Settings and FileSystem only.</summary>
    Merge
}

/// <summary>
/// The universal sync record envelope. T carries the domain-specific payload.
/// </summary>
/// <typeparam name="T">The domain record payload type.</typeparam>
/// <param name="RecordId">Stable UUID assigned on creation; never changes across devices.</param>
/// <param name="Domain">Domain discriminator from <see cref="SyncDomain"/>.</param>
/// <param name="SchemaVersion">Schema version of the payload; allows rolling upgrades.</param>
/// <param name="OwnerAccountId">Account UUID that owns this record.</param>
/// <param name="OriginDeviceId">Device UUID that last wrote this record.</param>
/// <param name="Revision">Monotonically increasing per-record revision counter.</param>
/// <param name="ModifiedUtc">Wall-clock timestamp of the last modification (from the originating device).</param>
/// <param name="ServerReceivedUtc">Timestamp when the server persisted this revision. Null for client-only drafts.</param>
/// <param name="ContentHash">SHA-256 hex hash of the serialized payload for integrity verification.</param>
/// <param name="IsTombstone">True when the record is logically deleted; payload may be empty.</param>
/// <param name="Payload">The domain-specific payload. Null for tombstones.</param>
public sealed record SyncRecord<T>(
    Guid RecordId,
    string Domain,
    int SchemaVersion,
    Guid OwnerAccountId,
    Guid OriginDeviceId,
    long Revision,
    DateTimeOffset ModifiedUtc,
    DateTimeOffset? ServerReceivedUtc,
    string ContentHash,
    bool IsTombstone,
    T? Payload);

/// <summary>
/// Pull request: client asks for all records in a domain modified after its cursor.
/// The cursor is opaque; clients must not parse it.
/// </summary>
/// <param name="Domain">Domain to pull from.</param>
/// <param name="Cursor">Opaque position cursor from the previous pull, or null for a full refresh.</param>
/// <param name="MaxRecords">Maximum records to return in one batch (1–500; server may clamp).</param>
public sealed record PullRequest(
    string Domain,
    string? Cursor,
    int MaxRecords);

/// <summary>
/// Pull response carrying a page of records and the next cursor.
/// </summary>
/// <param name="Domain">Domain these records belong to.</param>
/// <param name="Records">Ordered list of sync record envelopes (JSON payload as raw string).</param>
/// <param name="NextCursor">Opaque cursor to use for the next pull; null when caught up.</param>
/// <param name="HasMore">True when more records exist beyond this batch.</param>
/// <param name="PulledAtUtc">Server-side timestamp when this batch was assembled.</param>
public sealed record PullResponse(
    string Domain,
    IReadOnlyList<SyncRecordEnvelope> Records,
    string? NextCursor,
    bool HasMore,
    DateTimeOffset PulledAtUtc);

/// <summary>
/// A sync record with its payload serialized as a raw JSON string.
/// Using raw JSON preserves the domain-specific schema without the contracts project
/// needing a reference to every domain type.
/// </summary>
/// <param name="RecordId">Stable UUID.</param>
/// <param name="Domain">Domain discriminator.</param>
/// <param name="SchemaVersion">Payload schema version.</param>
/// <param name="OwnerAccountId">Owning account.</param>
/// <param name="OriginDeviceId">Originating device.</param>
/// <param name="Revision">Monotonic revision counter.</param>
/// <param name="ModifiedUtc">Last modification timestamp.</param>
/// <param name="ServerReceivedUtc">Server ingestion timestamp.</param>
/// <param name="ContentHash">SHA-256 hex hash of PayloadJson.</param>
/// <param name="IsTombstone">True for deleted records.</param>
/// <param name="PayloadJson">Raw JSON payload string. Null for tombstones.</param>
public sealed record SyncRecordEnvelope(
    Guid RecordId,
    string Domain,
    int SchemaVersion,
    Guid OwnerAccountId,
    Guid OriginDeviceId,
    long Revision,
    DateTimeOffset ModifiedUtc,
    DateTimeOffset? ServerReceivedUtc,
    string ContentHash,
    bool IsTombstone,
    string? PayloadJson);

/// <summary>
/// Push request: client sends a batch of locally modified records.
/// </summary>
/// <param name="Domain">Domain being pushed.</param>
/// <param name="Records">Records to push (max 100 per batch; server may reject larger batches).</param>
/// <param name="IdempotencyKey">Client-generated UUID; duplicate pushes with the same key are safely ignored.</param>
public sealed record PushRequest(
    string Domain,
    IReadOnlyList<SyncRecordEnvelope> Records,
    Guid IdempotencyKey);

/// <summary>
/// Outcome of a push operation.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<PushOutcome>))]
public enum PushOutcome
{
    /// <summary>All records were accepted.</summary>
    Accepted,

    /// <summary>Some records have conflicts; client must resolve before they are persisted.</summary>
    ConflictsDetected,

    /// <summary>The batch was already processed (idempotent replay).</summary>
    AlreadyProcessed,

    /// <summary>The client schema version is incompatible with the server.</summary>
    SchemaIncompatible
}

/// <summary>
/// Summary of a single conflict detected during push.
/// </summary>
/// <param name="RecordId">The conflicting record.</param>
/// <param name="ClientRevision">Revision the client submitted.</param>
/// <param name="ServerRevision">Current server revision.</param>
/// <param name="ServerModifiedUtc">When the server version was last modified.</param>
/// <param name="ConflictCode">Domain-specific conflict code for client UI (e.g., "GRANT_SECURITY_BLOCK").</param>
public sealed record SyncConflict(
    Guid RecordId,
    long ClientRevision,
    long ServerRevision,
    DateTimeOffset ServerModifiedUtc,
    string ConflictCode);

/// <summary>
/// Response from a push operation.
/// </summary>
/// <param name="Outcome">High-level outcome.</param>
/// <param name="AcceptedCount">Number of records successfully stored.</param>
/// <param name="Conflicts">Records requiring explicit resolution. Empty when Outcome is Accepted.</param>
public sealed record PushResponse(
    PushOutcome Outcome,
    int AcceptedCount,
    IReadOnlyList<SyncConflict> Conflicts);

/// <summary>
/// Client submits an explicit conflict resolution for a single record.
/// The server validates that the chosen resolution does not weaken security policy.
/// </summary>
/// <param name="RecordId">Record being resolved.</param>
/// <param name="Domain">Domain of the record.</param>
/// <param name="Resolution">Chosen resolution strategy.</param>
/// <param name="MergedPayloadJson">When Resolution is Merge, the client-computed merged payload. Null otherwise.</param>
/// <param name="MergedContentHash">SHA-256 of MergedPayloadJson when Resolution is Merge. Null otherwise.</param>
public sealed record ResolveSyncConflictRequest(
    Guid RecordId,
    string Domain,
    ConflictResolution Resolution,
    string? MergedPayloadJson,
    string? MergedContentHash);

/// <summary>
/// Result of a conflict resolution request.
/// </summary>
/// <param name="Resolved">True when the resolution was accepted and the record is now consistent.</param>
/// <param name="Reason">Explanation when Resolved is false (e.g., security block on grant downgrade).</param>
/// <param name="FinalRevision">The server's revision number after resolution.</param>
public sealed record ResolveSyncConflictResponse(
    bool Resolved,
    string? Reason,
    long FinalRevision);

/// <summary>
/// Source-generated JSON context for all sync contracts.
/// </summary>
[JsonSerializable(typeof(PullRequest))]
[JsonSerializable(typeof(PullResponse))]
[JsonSerializable(typeof(SyncRecordEnvelope))]
[JsonSerializable(typeof(IReadOnlyList<SyncRecordEnvelope>))]
[JsonSerializable(typeof(PushRequest))]
[JsonSerializable(typeof(PushResponse))]
[JsonSerializable(typeof(SyncConflict))]
[JsonSerializable(typeof(IReadOnlyList<SyncConflict>))]
[JsonSerializable(typeof(ResolveSyncConflictRequest))]
[JsonSerializable(typeof(ResolveSyncConflictResponse))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
public sealed partial class SyncContractsJsonContext : JsonSerializerContext { }
