using System.Text;
using System.Text.Json;

namespace Kazo.Os.Platform.Core.Intents;

/// <summary>One configured default file-association entry, per `P1-APP-008B`.</summary>
/// <param name="Extension">Normalized lowercase extension including the leading dot, when set.</param>
/// <param name="MediaType">Normalized lowercase media type, when set.</param>
/// <param name="AppId">Target application ID.</param>
/// <param name="Actions">Normalized lowercase supported actions (e.g. <c>open</c>, <c>edit</c>).</param>
public sealed record FileAssociationEntry(
    string? Extension,
    string? MediaType,
    string AppId,
    IReadOnlyList<string> Actions);

/// <summary>
/// A rebuildable, in-memory lookup over the file-association document's configured defaults, per
/// `P1-APP-008B`. Built fresh from raw document content on every resolution so it always reflects
/// the current committed revision without a separate cache-invalidation mechanism.
/// </summary>
public sealed class FileAssociationIndex
{
    private readonly Dictionary<string, FileAssociationEntry> _byExtension;
    private readonly Dictionary<string, FileAssociationEntry> _byMediaType;

    private FileAssociationIndex(
        IReadOnlyList<FileAssociationEntry> entries,
        Dictionary<string, FileAssociationEntry> byExtension,
        Dictionary<string, FileAssociationEntry> byMediaType)
    {
        Entries = entries;
        _byExtension = byExtension;
        _byMediaType = byMediaType;
    }

    /// <summary>Gets every entry in document order.</summary>
    public IReadOnlyList<FileAssociationEntry> Entries { get; }

    /// <summary>
    /// Parses the file-association document's JSON content into a lookup index. The first entry
    /// for a given normalized extension or media type and action wins; later duplicates are ignored
    /// so the index stays deterministic regardless of how the document was hand-edited. Keying by
    /// action lets two entries name different apps for <c>open</c> and <c>edit</c> of one type
    /// (ADR 0121, section 1).
    /// </summary>
    /// <param name="content">Complete, already schema-valid document content.</param>
    public static FileAssociationIndex Build(string content)
    {
        List<FileAssociationEntry> entries = [];
        Dictionary<string, FileAssociationEntry> byExtension = new(StringComparer.Ordinal);
        Dictionary<string, FileAssociationEntry> byMediaType = new(StringComparer.Ordinal);

        using JsonDocument document = JsonDocument.Parse(content);
        foreach (JsonElement association in document.RootElement.GetProperty("associations").EnumerateArray())
        {
            string? extension = association.TryGetProperty("extension", out JsonElement extensionElement)
                ? extensionElement.GetString()?.ToLowerInvariant()
                : null;
            string? mediaType = association.TryGetProperty("mediaType", out JsonElement mediaTypeElement)
                ? mediaTypeElement.GetString()?.ToLowerInvariant()
                : null;
            string appId = association.GetProperty("appId").GetString()!;
            string[] actions = [.. association.GetProperty("actions")
                .EnumerateArray()
                .Select(action => action.GetString()!.ToLowerInvariant())];

            FileAssociationEntry entry = new(extension, mediaType, appId, actions);
            entries.Add(entry);

            foreach (string action in actions)
            {
                if (extension is not null)
                {
                    byExtension.TryAdd(Key(extension, action), entry);
                }

                if (mediaType is not null)
                {
                    byMediaType.TryAdd(Key(mediaType, action), entry);
                }
            }
        }

        return new FileAssociationIndex(entries, byExtension, byMediaType);
    }

    /// <summary>
    /// Attempts to find a configured default entry for the given extension or media type that
    /// supports the requested action.
    /// </summary>
    /// <param name="extension">Candidate extension including the leading dot, or <see langword="null"/>.</param>
    /// <param name="mediaType">Candidate media type, or <see langword="null"/>.</param>
    /// <param name="action">Normalized lowercase requested action.</param>
    /// <param name="entry">The matching entry, when found.</param>
    public bool TryGetDefault(string? extension, string? mediaType, string action, out FileAssociationEntry entry)
    {
        if (extension is not null
            && _byExtension.TryGetValue(Key(extension.ToLowerInvariant(), action), out FileAssociationEntry? byExtensionEntry))
        {
            entry = byExtensionEntry;
            return true;
        }

        if (mediaType is not null
            && _byMediaType.TryGetValue(Key(mediaType.ToLowerInvariant(), action), out FileAssociationEntry? byMediaTypeEntry))
        {
            entry = byMediaTypeEntry;
            return true;
        }

        entry = null!;
        return false;
    }

    private static string Key(string target, string action) => target + "\n" + action;

    /// <summary>Serializes a candidate entry list back into canonical document content.</summary>
    /// <param name="entries">Entries to serialize, in the desired document order.</param>
    public static string Serialize(IReadOnlyList<FileAssociationEntry> entries)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteStartArray("associations");
            foreach (FileAssociationEntry entry in entries)
            {
                writer.WriteStartObject();
                if (entry.Extension is not null)
                {
                    writer.WriteString("extension", entry.Extension);
                }

                if (entry.MediaType is not null)
                {
                    writer.WriteString("mediaType", entry.MediaType);
                }

                writer.WriteString("appId", entry.AppId);
                writer.WriteStartArray("actions");
                foreach (string action in entry.Actions)
                {
                    writer.WriteStringValue(action);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
