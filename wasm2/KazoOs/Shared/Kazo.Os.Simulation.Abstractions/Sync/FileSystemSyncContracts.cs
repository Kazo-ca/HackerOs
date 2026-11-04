using System.Text.Json.Serialization;

namespace Kazo.Os.Simulation.Abstractions.Sync;

// =============================================================================
// FileSystem Domain Sync Payload — ADR 0030
//
// Carried as SyncRecordEnvelope.PayloadJson for the "filesystem" SyncDomain. File
// bytes never travel through this payload — only through the separate chunked
// content-transfer protocol (Server/Kazo.Os.Server.Contracts/Sync/ContentTransferContracts.cs),
// tied to this payload solely by ContentHash.
// =============================================================================

/// <summary>
/// Reconstructs one filesystem entry under <c>/home/{userId}</c> (ADR 0030 Decision 1/3).
/// </summary>
/// <param name="RelativePath">Path relative to the user's home directory, using <c>/</c> separators.</param>
/// <param name="Kind">One of <c>File</c>, <c>Directory</c>, <c>SymbolicLink</c> (mirrors <c>FileSystemEntryKind</c>).</param>
/// <param name="OwnerId">Owning user identity captured at sync time (informational — not settable via the provider API).</param>
/// <param name="GroupId">Owning group identity captured at sync time (informational — not settable via the provider API).</param>
/// <param name="PermissionMode">Nine-bit Unix-style owner/group/other mode.</param>
/// <param name="CreatedAtUtc">Entry creation time.</param>
/// <param name="ContentModifiedAtUtc">Last content modification time.</param>
/// <param name="MetadataChangedAtUtc">Last metadata change time.</param>
/// <param name="ContentHash">SHA-256 hex hash of file content, present only when <see cref="Kind"/> is <c>File</c>.</param>
/// <param name="Length">File content length in bytes, present only when <see cref="Kind"/> is <c>File</c>.</param>
/// <param name="SymlinkTarget">Symbolic-link target text, present only when <see cref="Kind"/> is <c>SymbolicLink</c>.</param>
public sealed record FileSystemSyncPayload(
    string RelativePath,
    string Kind,
    string OwnerId,
    string GroupId,
    ushort PermissionMode,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ContentModifiedAtUtc,
    DateTimeOffset MetadataChangedAtUtc,
    string? ContentHash,
    long? Length,
    string? SymlinkTarget);

/// <summary>Source-generated JSON context for the FileSystem sync payload.</summary>
[JsonSerializable(typeof(FileSystemSyncPayload))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
public sealed partial class FileSystemSyncContractsJsonContext : JsonSerializerContext { }
