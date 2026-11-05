using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kazo.Os.Infrastructure.Browser.Schema;

namespace Kazo.Os.Infrastructure.Browser.Rename;

/// <summary>One record change of the rename: the old key to delete, the record to write, or both.</summary>
/// <param name="StoreName">The object store.</param>
/// <param name="DeleteKey">The key of a record to delete first (its key changed or it is no longer used); <see langword="null"/> for none.</param>
/// <param name="Put">The record to write; <see langword="null"/> for a delete only.</param>
internal sealed record RenamedRecordChange(string StoreName, JsonNode? DeleteKey, JsonObject? Put);

/// <summary>The OS database after the rename rewrite.</summary>
/// <param name="Stores">Every store's records, rewritten.</param>
/// <param name="Changes">The writes that turn the stored database into <paramref name="Stores"/>; empty when nothing held an old name.</param>
internal sealed record RenamedOsDatabase(
    IReadOnlyDictionary<string, IReadOnlyList<JsonObject>> Stores,
    IReadOnlyList<RenamedRecordChange> Changes);

/// <summary>
/// Rewrites the records of the OS database written before 3.0 (ADR 0230 §3), the same way for the
/// browser's database at boot and for the content of an old backup at restore.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>File names</b> (<c>fsLinks</c>): a link named with an old name is renamed when it lies in
/// a system area: anywhere outside <c>/home</c>, <c>/root</c>, <c>/tmp</c>, <c>/mnt</c> and
/// <c>/media</c>, plus the dot folders of each account (<c>~/.config</c>, <c>~/.local</c>,
/// <c>~/.hackeros</c>...). So
/// <c>/etc/hackeros</c> becomes <c>/etc/kazoos</c> and <c>~/.config/hackeros</c> becomes
/// <c>~/.config/kazoos</c>, while <c>~/Documents/hackeros</c> keeps its name.</item>
/// <item><b>Configuration files</b>: the text of a file under <c>/etc</c> or <c>~/.config</c> is
/// rewritten; its content is stored again under its new SHA-256 (chunks of
/// <see cref="FileContentStoragePolicy.DefaultMaxChunkSizeBytes"/>) and the old chunks are deleted
/// once no other file uses them. Other files (documents) are never rewritten.</item>
/// <item><b>Every other record</b> (catalog, grants, settings, sessions, logs, entry metadata...): each
/// string value is rewritten (app ids, theme ids, capabilities, paths), except the credentials, the
/// content hashes and data, and the names people chose (login and display names). A record whose
/// key changes (a catalog app id) is deleted under its old key.</item>
/// </list>
/// The rewrite is idempotent: rewriting its own result changes nothing.
/// </remarks>
internal static class RenamedOsDatabaseRewriter
{
    private const string RootEntryId = "00000000000000000000000000000001";
    private const int FileKind = 0;

    /// <summary>Properties never rewritten: secrets, content addressing and names a person chose.</summary>
    private static readonly HashSet<string> KeptProperties = new(StringComparer.Ordinal)
    {
        "credential", "contentHash", "dataBase64", "loginName", "displayName", "symbolicLinkTarget"
    };

    /// <summary>Top-level folders that hold the person's files.</summary>
    private static readonly HashSet<string> UserTopFolders = new(StringComparer.Ordinal) { "tmp", "mnt", "media" };

    /// <summary>Rewrites <paramref name="stores"/> (store name → records) and lists the writes it takes.</summary>
    /// <param name="stores">The records of every store of the OS database.</param>
    public static RenamedOsDatabase Rewrite(IReadOnlyDictionary<string, IReadOnlyList<JsonObject>> stores)
    {
        ArgumentNullException.ThrowIfNull(stores);
        Dictionary<string, List<JsonObject>> result = stores.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Select(record => record.DeepClone().AsObject()).ToList(),
            StringComparer.Ordinal);
        List<RenamedRecordChange> changes = [];

        List<JsonObject> links = Store(result, KazoOsIndexedDbSchema.FileSystemLinkStoreName);
        Dictionary<string, string> paths = EntryPaths(links);
        RewriteContents(result, paths, changes);
        RewriteLinks(links, paths, changes);

        foreach ((string storeName, List<JsonObject> records) in result)
        {
            if (storeName is KazoOsIndexedDbSchema.FileSystemLinkStoreName or KazoOsIndexedDbSchema.FileContentStoreName)
            {
                continue;
            }

            RewriteRecords(storeName, records, changes);
        }

        return new RenamedOsDatabase(
            result.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<JsonObject>)pair.Value, StringComparer.Ordinal),
            changes);
    }

    /// <summary>
    /// Rewrites the old names of an absolute path's segments that lie in a system area
    /// (<c>/etc/hackeros/x</c> → <c>/etc/kazoos/x</c>); a document path is returned as it is.
    /// </summary>
    /// <param name="path">A virtual file system path.</param>
    public static string RewriteSystemPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!path.StartsWith('/') || !RenamedNames.ContainsOldName(path))
        {
            return path;
        }

        string[] segments = path.Split('/');
        string current = string.Empty;
        for (int index = 1; index < segments.Length; index++)
        {
            string candidate = current + "/" + segments[index];
            if (IsSystemPath(candidate))
            {
                segments[index] = RenamedNames.Rewrite(segments[index]);
            }

            current = current + "/" + segments[index];
        }

        return string.Join('/', segments);
    }

    /// <summary>Gets whether <paramref name="path"/> lies in a system area, where old names are renamed.</summary>
    /// <param name="path">An absolute path.</param>
    public static bool IsSystemPath(string path)
    {
        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return true;
        }

        return segments[0] switch
        {
            "home" => segments.Length >= 3 && segments[2].StartsWith('.'),
            "root" => segments.Length >= 2 && segments[1].StartsWith('.'),
            _ => !UserTopFolders.Contains(segments[0]),
        };
    }

    /// <summary>Gets whether a file at <paramref name="path"/> is an OS configuration file whose text is rewritten.</summary>
    /// <param name="path">An absolute path.</param>
    public static bool IsConfigurationPath(string path)
    {
        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments switch
        {
            ["etc", _, ..] => true,
            ["home", _, ".config", _, ..] => true,
            ["root", ".config", _, ..] => true,
            _ => false,
        };
    }

    private static List<JsonObject> Store(Dictionary<string, List<JsonObject>> stores, string name)
    {
        if (!stores.TryGetValue(name, out List<JsonObject>? records))
        {
            records = [];
            stores[name] = records;
        }

        return records;
    }

    /// <summary>Gets the path of every linked entry (its first link wins for a hard-linked file).</summary>
    private static Dictionary<string, string> EntryPaths(List<JsonObject> links)
    {
        Dictionary<string, (string Parent, string Name)> parents = new(StringComparer.Ordinal);
        foreach (JsonObject link in links)
        {
            if (Text(link, "entryId") is { } entry && Text(link, "parentId") is { } parent && Text(link, "name") is { } name)
            {
                parents.TryAdd(entry, (parent, name));
            }
        }

        Dictionary<string, string> paths = new(StringComparer.Ordinal) { [RootEntryId] = string.Empty };
        foreach (string entry in parents.Keys)
        {
            Resolve(entry, parents, paths, depth: 0);
        }

        paths[RootEntryId] = "/";
        return paths;
    }

    private static string? Resolve(
        string entry,
        Dictionary<string, (string Parent, string Name)> parents,
        Dictionary<string, string> paths,
        int depth)
    {
        if (paths.TryGetValue(entry, out string? known))
        {
            return known;
        }

        // A cycle or a dangling chain (damaged tree) has no path: its files are left as they are.
        if (depth > 256 || !parents.TryGetValue(entry, out (string Parent, string Name) link))
        {
            return null;
        }

        string? parentPath = Resolve(link.Parent, parents, paths, depth + 1);
        if (parentPath is null)
        {
            return null;
        }

        string path = parentPath + "/" + link.Name;
        paths[entry] = path;
        return path;
    }

    private static void RewriteLinks(List<JsonObject> links, Dictionary<string, string> paths, List<RenamedRecordChange> changes)
    {
        HashSet<(string, string)> taken = [.. links
            .Select(link => (Text(link, "parentId") ?? string.Empty, Text(link, "name") ?? string.Empty))];
        foreach (JsonObject link in links)
        {
            string? parent = Text(link, "parentId");
            string? name = Text(link, "name");
            if (parent is null || name is null || !paths.TryGetValue(parent, out string? parentPath))
            {
                continue;
            }

            string renamed = RenamedNames.Rewrite(name);
            string path = (parentPath == "/" ? string.Empty : parentPath) + "/" + name;
            // A target name already taken (a folder the new OS created) keeps both: nothing is overwritten.
            if (renamed == name || !IsSystemPath(path) || !taken.Add((parent, renamed)))
            {
                continue;
            }

            taken.Remove((parent, name));
            link["name"] = renamed;
            changes.Add(new RenamedRecordChange(KazoOsIndexedDbSchema.FileSystemLinkStoreName, new JsonArray(parent, name), link));
        }
    }

    private static void RewriteContents(Dictionary<string, List<JsonObject>> stores, Dictionary<string, string> paths, List<RenamedRecordChange> changes)
    {
        List<JsonObject> entries = Store(stores, KazoOsIndexedDbSchema.FileSystemEntryStoreName);
        List<JsonObject> chunks = Store(stores, KazoOsIndexedDbSchema.FileContentStoreName);
        Dictionary<string, List<JsonObject>> chunksByHash = chunks
            .Where(chunk => Text(chunk, "contentHash") is not null)
            .GroupBy(chunk => Text(chunk, "contentHash")!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(chunk => (int?)chunk["chunkIndex"] ?? 0).ToList(), StringComparer.Ordinal);
        HashSet<string> replacedHashes = new(StringComparer.Ordinal);

        foreach (JsonObject entry in entries)
        {
            if ((int?)entry["kind"] != FileKind
                || Text(entry, "id") is not { } id
                || Text(entry, "contentHash") is not { } hash
                || !paths.TryGetValue(id, out string? path)
                || !IsConfigurationPath(path)
                || !chunksByHash.TryGetValue(hash, out List<JsonObject>? stored)
                || ReadText(stored) is not { } text)
            {
                continue;
            }

            string rewritten = RenamedNames.Rewrite(text);
            if (ReferenceEquals(rewritten, text) || rewritten == text)
            {
                continue;
            }

            byte[] bytes = Encoding.UTF8.GetBytes(rewritten);
            string newHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (!chunksByHash.ContainsKey(newHash))
            {
                List<JsonObject> written = Split(bytes, newHash, stored[0]["createdUtcMs"]?.DeepClone());
                chunksByHash[newHash] = written;
                chunks.AddRange(written);
                changes.AddRange(written.Select(chunk => new RenamedRecordChange(KazoOsIndexedDbSchema.FileContentStoreName, null, chunk)));
            }

            entry["contentHash"] = newHash;
            entry["length"] = bytes.Length;
            replacedHashes.Add(hash);
        }

        // Old chunks go once no file (a document with the same bytes, say) still uses them.
        HashSet<string> used = new(entries.Select(entry => Text(entry, "contentHash")).OfType<string>(), StringComparer.Ordinal);
        foreach (string hash in replacedHashes.Where(hash => !used.Contains(hash)))
        {
            foreach (JsonObject chunk in chunksByHash[hash])
            {
                chunks.Remove(chunk);
                changes.Add(new RenamedRecordChange(KazoOsIndexedDbSchema.FileContentStoreName, new JsonArray(hash, (int?)chunk["chunkIndex"] ?? 0), null));
            }
        }
    }

    /// <summary>Joins a file's chunks and decodes them as strict UTF-8; <see langword="null"/> for binary content.</summary>
    private static string? ReadText(List<JsonObject> chunks)
    {
        try
        {
            using MemoryStream content = new();
            foreach (JsonObject chunk in chunks)
            {
                content.Write(Convert.FromBase64String(Text(chunk, "dataBase64") ?? string.Empty));
            }

            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(content.ToArray());
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            return null;
        }
    }

    private static List<JsonObject> Split(byte[] bytes, string hash, JsonNode? createdUtcMs)
    {
        int size = (int)FileContentStoragePolicy.DefaultMaxChunkSizeBytes;
        List<JsonObject> chunks = [];
        for (int offset = 0, index = 0; offset < bytes.Length || index == 0; offset += size, index++)
        {
            int length = Math.Min(size, bytes.Length - offset);
            chunks.Add(new JsonObject
            {
                ["contentHash"] = hash,
                ["chunkIndex"] = index,
                ["dataBase64"] = Convert.ToBase64String(bytes, offset, length),
                ["createdUtcMs"] = createdUtcMs?.DeepClone(),
            });
        }

        return chunks;
    }

    private static void RewriteRecords(string storeName, List<JsonObject> records, List<RenamedRecordChange> changes)
    {
        IReadOnlyList<string>? keyPath = KazoOsIndexedDbSchema.ObjectStores
            .FirstOrDefault(store => store.Name == storeName)?.KeyPath;
        foreach (JsonObject record in records)
        {
            string before = record.ToJsonString();
            JsonNode? oldKey = keyPath is null ? null : Key(record, keyPath);
            RewriteObject(record);
            if (storeName == KazoOsIndexedDbSchema.FileSystemEntryStoreName && Text(record, "symbolicLinkTarget") is { } target)
            {
                record["symbolicLinkTarget"] = RewriteSystemPath(target);
            }

            if (record.ToJsonString() == before)
            {
                continue;
            }

            JsonNode? newKey = keyPath is null ? null : Key(record, keyPath);
            bool keyChanged = oldKey is not null && newKey is not null && !JsonNode.DeepEquals(oldKey, newKey);
            changes.Add(new RenamedRecordChange(storeName, keyChanged ? oldKey : null, record));
        }
    }

    private static void RewriteObject(JsonObject value)
    {
        foreach (string property in value.Select(pair => pair.Key).ToList())
        {
            if (!KeptProperties.Contains(property) && RewriteChild(value[property]) is { } replacement)
            {
                value[property] = replacement;
            }
        }
    }

    /// <summary>Rewrites a node in place; returns a new node only for a string that changed.</summary>
    private static JsonNode? RewriteChild(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject child:
                RewriteObject(child);
                return null;
            case JsonArray array:
                for (int index = 0; index < array.Count; index++)
                {
                    if (RewriteChild(array[index]) is { } replacement)
                    {
                        array[index] = replacement;
                    }
                }

                return null;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                string text = value.GetValue<string>();
                string rewritten = RenamedNames.Rewrite(text);
                return rewritten == text ? null : JsonValue.Create(rewritten);
            default:
                return null;
        }
    }

    private static JsonNode? Key(JsonObject record, IReadOnlyList<string> keyPath)
    {
        if (keyPath.Count == 1)
        {
            return record[keyPath[0]]?.DeepClone();
        }

        JsonArray key = [];
        foreach (string property in keyPath)
        {
            key.Add(record[property]?.DeepClone());
        }

        return key;
    }

    private static string? Text(JsonObject record, string property) =>
        record[property] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
}
