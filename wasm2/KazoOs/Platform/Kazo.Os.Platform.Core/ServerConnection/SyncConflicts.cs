using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kazo.Os.Platform.Core.ServerConnection;

/// <summary>The user's answer to one synchronization conflict (ADR 0101 section 2).</summary>
public enum SyncConflictChoice
{
    /// <summary>"Garder la mienne": this device's version replaces the server's (<c>ClientWins</c>).</summary>
    KeepMine = 0,

    /// <summary>"Garder celle de l'autre appareil": the server's version wins; the local one goes to the trash.</summary>
    KeepOther = 1,

    /// <summary>
    /// "Garder les deux": the server's version takes the original path and the local one is kept
    /// next to it as <c>&lt;name&gt;.sync-conflict-&lt;yyyyMMdd-HHmmss&gt;-&lt;device&gt;.&lt;ext&gt;</c>.
    /// </summary>
    KeepBoth = 2
}

/// <summary>One side of a conflict, as the dialog shows it.</summary>
/// <param name="IsDeleted">The file was deleted on that side.</param>
/// <param name="ModifiedAtUtc">Last content modification, when known.</param>
/// <param name="Length">Size in bytes, when known.</param>
/// <param name="ContentHash">SHA-256 of the file content, when it is a file.</param>
/// <param name="DeviceId">Device that wrote this version (the server's <c>OriginDeviceId</c>), when known.</param>
public sealed record SyncFileVersion(
    bool IsDeleted,
    DateTimeOffset? ModifiedAtUtc,
    long? Length,
    string? ContentHash,
    Guid? DeviceId);

/// <summary>
/// A file both this device and another one changed (or one changed and the other deleted) since
/// their last common revision (ADR 0101 section 2). While it is pending the file is frozen: it keeps
/// its local version, stays editable here, but the sync neither sends nor replaces it.
/// </summary>
/// <param name="RecordId">Sync record id of the file.</param>
/// <param name="RelativePath">Path below the home directory.</param>
/// <param name="DetectedAtUtc">When the conflict was found.</param>
/// <param name="Local">This device's version when the conflict was found.</param>
/// <param name="Remote">
/// The other device's version, filled by the pull that follows the conflicting push;
/// <see langword="null"/> until then (only "Garder la mienne" is possible meanwhile).
/// </param>
/// <param name="RemoteRevision">Server revision of <paramref name="Remote"/>; 0 while unknown.</param>
/// <param name="RemotePayloadJson">The server's <c>FileSystemSyncPayload</c>, or <see langword="null"/> for a deletion or while unknown.</param>
public sealed record SyncFileConflict(
    Guid RecordId,
    string RelativePath,
    DateTimeOffset DetectedAtUtc,
    SyncFileVersion Local,
    SyncFileVersion? Remote,
    long RemoteRevision,
    string? RemotePayloadJson);

/// <summary>Text preview of both sides of a conflict (ADR 0101 section 2: "pour un fichier texte un aperçu des deux").</summary>
/// <param name="LocalText">Start of this device's version, or <see langword="null"/> when deleted or not text.</param>
/// <param name="RemoteText">Start of the other device's version, or <see langword="null"/> when deleted, unknown or not text.</param>
public sealed record SyncConflictPreview(string? LocalText, string? RemoteText);

/// <summary>On-disk shape of <c>~/.local/state/hackeros/sync-conflicts.json</c>.</summary>
/// <param name="SchemaVersion">Format version, 1.</param>
/// <param name="Conflicts">Pending conflicts, oldest first.</param>
internal sealed record SyncConflictFile(int SchemaVersion, IReadOnlyList<SyncFileConflict> Conflicts);

/// <summary>Source-generated JSON for the pending-conflict file.</summary>
[JsonSerializable(typeof(SyncConflictFile))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
internal sealed partial class SyncConflictJsonContext : JsonSerializerContext;

/// <summary>
/// Pending conflicts of one user, kept in <c>~/.local/state/hackeros/sync-conflicts.json</c>:
/// device-local state, never synchronized (ADR 0044 section 10), so they survive a reload and come
/// back at the next login (ADR 0101 section 2).
/// </summary>
internal static class SyncConflictFileFormat
{
    /// <summary>Home-relative path of the file.</summary>
    public const string RelativePath = ".local/state/hackeros/sync-conflicts.json";

    /// <summary>Parses the file; a missing, empty or damaged file gives no conflict.</summary>
    public static List<SyncFileConflict> Parse(byte[]? content)
    {
        if (content is null || content.Length == 0)
        {
            return [];
        }

        try
        {
            SyncConflictFile? file = JsonSerializer.Deserialize(content, SyncConflictJsonContext.Default.SyncConflictFile);
            return file?.Conflicts is { } conflicts ? [.. conflicts] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Serializes the pending conflicts.</summary>
    public static byte[] Format(IReadOnlyList<SyncFileConflict> conflicts) =>
        JsonSerializer.SerializeToUtf8Bytes(new SyncConflictFile(1, conflicts), SyncConflictJsonContext.Default.SyncConflictFile);
}
