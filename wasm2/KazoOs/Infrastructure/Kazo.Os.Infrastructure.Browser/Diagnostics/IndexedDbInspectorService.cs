using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kazo.Os.Infrastructure.Browser.Interop;
using Kazo.Os.Infrastructure.Browser.Schema;
using Kazo.Os.Infrastructure.Browser.Storage;
using Microsoft.JSInterop;

namespace Kazo.Os.Infrastructure.Browser.Diagnostics;

/// <summary>
/// Service that inspects all 12 IndexedDB object stores and resolves virtual file paths by traversing directory links.
/// </summary>
public sealed class IndexedDbInspectorService
{
    private readonly IJSRuntime _runtime;
    private static readonly JsonSerializerOptions IndentedJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Initializes the inspector service with the active JS runtime.</summary>
    public IndexedDbInspectorService(IJSRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    /// <summary>Reads all 12 object stores in one atomic BackupRestore transaction and resolves virtual file paths.</summary>
    public async ValueTask<IndexedDbSnapshot> InspectDatabaseAsync(CancellationToken cancellationToken = default)
    {
        await using IndexedDbInteropAdapter adapter = new(_runtime);
        
        string[] storeNames = [.. HackerOsIndexedDbSchema.ObjectStores.Select(s => s.Name)];
        IndexedDbOperation[] operations = [.. storeNames.Select(storeName =>
            new IndexedDbOperation("getAll", storeName))];

        IReadOnlyList<JsonElement> results = await adapter.ExecuteAsync(
            "BackupRestore",
            IndexedDbTransactionMode.ReadOnly,
            operations,
            cancellationToken).ConfigureAwait(false);

        Dictionary<string, JsonElement> storeData = [];
        for (int i = 0; i < storeNames.Length && i < results.Count; i++)
        {
            storeData[storeNames[i]] = results[i];
        }

        // Build path resolution lookup from fsLinks (parentId + name -> entryId)
        Dictionary<string, (string ParentId, string Name)> linkMap = [];
        if (storeData.TryGetValue(HackerOsIndexedDbSchema.FileSystemLinkStoreName, out JsonElement linksElement)
            && linksElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement link in linksElement.EnumerateArray())
            {
                string? parentId = link.TryGetProperty("parentId", out JsonElement p) ? p.GetString() : null;
                string? name = link.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
                string? entryId = link.TryGetProperty("entryId", out JsonElement e) ? e.GetString() : null;

                if (!string.IsNullOrEmpty(entryId) && !string.IsNullOrEmpty(parentId) && !string.IsNullOrEmpty(name))
                {
                    linkMap[entryId] = (parentId, name);
                }
            }
        }

        // Resolve virtual path for every entryId in fsEntries
        Dictionary<string, string> resolvedPaths = [];
        if (storeData.TryGetValue(HackerOsIndexedDbSchema.FileSystemEntryStoreName, out JsonElement entriesElement)
            && entriesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement entry in entriesElement.EnumerateArray())
            {
                string? entryId = entry.TryGetProperty("id", out JsonElement idProp) ? idProp.GetString() : null;
                if (!string.IsNullOrEmpty(entryId))
                {
                    resolvedPaths[entryId] = ResolvePath(entryId, linkMap);
                }
            }
        }

        List<IndexedDbStoreSnapshot> stores = [];
        foreach (IndexedDbObjectStoreDefinition storeDef in HackerOsIndexedDbSchema.ObjectStores)
        {
            JsonElement element = storeData.GetValueOrDefault(storeDef.Name);
            int count = element.ValueKind == JsonValueKind.Array ? element.GetArrayLength() : 0;
            string json = element.ValueKind != JsonValueKind.Undefined && element.ValueKind != JsonValueKind.Null
                ? (JsonNode.Parse(element.GetRawText())?.ToJsonString(IndentedJsonOptions) ?? "[]")
                : "[]";

            stores.Add(new IndexedDbStoreSnapshot(
                storeDef.Name,
                storeDef.Purpose,
                storeDef.KeyPath,
                count,
                json,
                element));
        }

        return new IndexedDbSnapshot(
            DateTimeOffset.UtcNow,
            HackerOsIndexedDbSchema.DatabaseName,
            HackerOsIndexedDbSchema.CurrentVersion,
            stores,
            resolvedPaths,
            linkMap.Count);
    }

    private static string ResolvePath(string entryId, Dictionary<string, (string ParentId, string Name)> linkMap)
    {
        if (entryId == "00000000-0000-0000-0000-000000000001")
        {
            return "/";
        }

        List<string> segments = [];
        HashSet<string> visited = [];
        string currentId = entryId;

        while (visited.Add(currentId) && linkMap.TryGetValue(currentId, out var link))
        {
            segments.Add(link.Name);
            currentId = link.ParentId;
            if (currentId == "00000000-0000-0000-0000-000000000001")
            {
                break;
            }
        }

        if (segments.Count == 0)
        {
            return $"/ [unlinked id: {entryId}]";
        }

        segments.Reverse();
        return $"/{string.Join('/', segments)}";
    }
}

/// <summary>Represents a full database snapshot including resolved virtual file paths.</summary>
public sealed record IndexedDbSnapshot(
    DateTimeOffset CapturedAtUtc,
    string DatabaseName,
    int DatabaseVersion,
    IReadOnlyList<IndexedDbStoreSnapshot> Stores,
    IReadOnlyDictionary<string, string> ResolvedPaths,
    int TotalLinkCount)
{
    /// <summary>Serializes the entire database state into a comprehensive JSON export formatted for AI inspection.</summary>
    public string ToExportJson()
    {
        JsonObject root = new()
        {
            ["capturedAtUtc"] = CapturedAtUtc.ToString("o"),
            ["databaseName"] = DatabaseName,
            ["databaseVersion"] = DatabaseVersion,
            ["architectureNote"] = "Kazo OS uses inode-based storage (fsEntries stores attributes; fsLinks stores directory hierarchy). Resolved paths are computed below.",
            ["totalResolvedFilePaths"] = ResolvedPaths.Count
        };

        JsonObject pathsObj = [];
        foreach (var pair in ResolvedPaths)
        {
            pathsObj[pair.Key] = pair.Value;
        }
        root["resolvedFilePaths"] = pathsObj;

        JsonObject storesObj = [];
        foreach (IndexedDbStoreSnapshot store in Stores)
        {
            storesObj[store.Name] = new JsonObject
            {
                ["purpose"] = store.Purpose,
                ["recordCount"] = store.RecordCount,
                ["records"] = JsonNode.Parse(store.FormattedJson)
            };
        }

        root["objectStores"] = storesObj;

        return root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }
}

/// <summary>Represents a snapshot of one IndexedDB object store.</summary>
public sealed record IndexedDbStoreSnapshot(
    string Name,
    string Purpose,
    IReadOnlyList<string> KeyPath,
    int RecordCount,
    string FormattedJson,
    JsonElement RawElement);
