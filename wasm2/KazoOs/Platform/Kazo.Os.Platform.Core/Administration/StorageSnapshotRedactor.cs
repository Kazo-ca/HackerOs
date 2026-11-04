using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kazo.Os.AppSdk.Administration;
using Kazo.Os.Platform.Core.Keyring;
using Kazo.Os.Simulation.Abstractions.Settings;

namespace Kazo.Os.Platform.Core.Administration;

/// <summary>
/// Masks secrets in the raw storage snapshot Diagnostic shows and exports (ADR 0100 section 1):
/// the content chunks of every private settings file (<c>*.private.config</c>) and of every
/// keyring (<c>~/.local/share/keyrings/</c>) are replaced by a marker, so a copied or saved report
/// never carries a secret, even though the snapshot itself bypasses file permissions.
/// </summary>
internal static class StorageSnapshotRedactor
{
    /// <summary>Marker written in place of a masked chunk.</summary>
    public const string MaskedData = "[masked: ADR 0100]";

    private const string ContentStoreName = "fsContent";

    private static readonly JsonSerializerOptions IndentedOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Gets whether the content of a file is secret in a diagnostic report.</summary>
    /// <param name="path">Absolute virtual path.</param>
    public static bool IsSecretFile(string path) =>
        SettingsPrivateFiles.IsPrivateFile(path)
        || path.Contains("/" + UserKeyringService.KeyringDirectory + "/", StringComparison.Ordinal);

    /// <summary>Returns a copy of <paramref name="snapshot"/> whose secret file contents are masked.</summary>
    /// <param name="snapshot">Raw snapshot.</param>
    public static HackStorageSnapshot Redact(HackStorageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        HashSet<string> secretEntries = new(
            snapshot.ResolvedPaths.Where(static pair => IsSecretFile(pair.Value)).Select(static pair => pair.Key),
            StringComparer.Ordinal);
        if (secretEntries.Count == 0)
        {
            return snapshot;
        }

        HashSet<string> secretHashes = new(StringComparer.Ordinal);
        foreach (HackStorageStoreSnapshot store in snapshot.Stores.Where(static store => store.Kind == HackStorageStoreKind.FileEntries))
        {
            foreach (JsonElement entry in Records(store))
            {
                if (entry.TryGetProperty("id", out JsonElement id)
                    && id.GetString() is { } entryId
                    && secretEntries.Contains(entryId)
                    && entry.TryGetProperty("contentHash", out JsonElement hash)
                    && hash.GetString() is { Length: > 0 } contentHash)
                {
                    secretHashes.Add(contentHash);
                }
            }
        }

        if (secretHashes.Count == 0)
        {
            return snapshot;
        }

        return snapshot with
        {
            Stores = [.. snapshot.Stores.Select(store => store.Name == ContentStoreName ? MaskContent(store, secretHashes) : store)]
        };
    }

    private static HackStorageStoreSnapshot MaskContent(HackStorageStoreSnapshot store, HashSet<string> secretHashes)
    {
        JsonArray masked = [];
        foreach (JsonElement record in Records(store))
        {
            JsonNode? node = JsonNode.Parse(record.GetRawText());
            if (node is JsonObject chunk
                && chunk["contentHash"]?.GetValue<string>() is { } hash
                && secretHashes.Contains(hash))
            {
                chunk["dataBase64"] = MaskedData;
            }

            masked.Add(node);
        }

        string json = masked.ToJsonString(IndentedOptions);
        using JsonDocument document = JsonDocument.Parse(json);
        return store with { FormattedJson = json, Records = document.RootElement.Clone() };
    }

    private static IEnumerable<JsonElement> Records(HackStorageStoreSnapshot store) =>
        store.Records.ValueKind == JsonValueKind.Array ? store.Records.EnumerateArray() : [];
}
