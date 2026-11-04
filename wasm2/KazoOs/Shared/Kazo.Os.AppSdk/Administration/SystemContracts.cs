using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kazo.Os.AppSdk.Administration;

/// <summary>
/// Machine power operations for one app instance (ADR 0056, section 11), behind
/// <see cref="HackSystem"/>. Every member requires the dangerous capability <c>system.power</c> and
/// an administrator through the OS authentication agent, like <c>reboot</c> under Linux.
/// </summary>
public interface IAppSystemPowerGateway
{
    /// <summary>Flushes pending writes, ends every process and restarts the machine.</summary>
    /// <param name="cancellationToken">Cancels the authentication; once it succeeded the restart cannot be cancelled.</param>
    ValueTask RebootAsync(CancellationToken cancellationToken = default);

    /// <summary>Erases the machine's whole storage, then restarts it as freshly installed.</summary>
    /// <param name="cancellationToken">Cancels the authentication; once it succeeded the reset cannot be cancelled.</param>
    ValueTask FactoryResetAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Raw storage inspection for one app instance (ADR 0056, section 11), behind
/// <see cref="HackStorageDiagnostics"/>. Requires the dangerous capability
/// <c>system.storage.inspect</c>: the snapshot bypasses file permissions.
/// </summary>
public interface IAppStorageDiagnosticsGateway
{
    /// <summary>Reads every object store of the OS database in one transaction.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    ValueTask<HackStorageSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the browser databases of web sites, <c>hackweb.*</c> (ADR 0088, section 4): they are not in
    /// the OS database nor in its files. Defaults to none so existing implementations keep compiling.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    ValueTask<IReadOnlyList<HackWebSiteDatabaseInfo>> GetWebSiteDatabasesAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<HackWebSiteDatabaseInfo>>([]);

    /// <summary>
    /// Deletes the browser database of a web site; the site recreates it empty, with its schema, on its
    /// next request (ADR 0088, section 4).
    /// </summary>
    /// <param name="databaseName">A <c>hackweb.*</c> name from <see cref="GetWebSiteDatabasesAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the deletion.</param>
    /// <exception cref="ArgumentException">The name is not a site database name.</exception>
    ValueTask DeleteWebSiteDatabaseAsync(string databaseName, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This host has no site databases.");
}

/// <summary>Role of one object store, so a viewer can render the filesystem stores specially.</summary>
public enum HackStorageStoreKind
{
    /// <summary>Any other store, shown as JSON.</summary>
    Other = 0,

    /// <summary>Filesystem inodes (<c>fsEntries</c>): attributes, owner, content hash.</summary>
    FileEntries = 1,

    /// <summary>Directory links (<c>fsLinks</c>): <c>(parentId, name) → entryId</c>.</summary>
    FileLinks = 2
}

/// <summary>A snapshot of one object store.</summary>
/// <param name="Name">Store name, such as <c>fsEntries</c>.</param>
/// <param name="Purpose">What the store holds, from the OS schema.</param>
/// <param name="KeyPath">Key path of the store.</param>
/// <param name="Kind">Role of the store.</param>
/// <param name="RecordCount">Number of records.</param>
/// <param name="FormattedJson">Records as indented JSON.</param>
/// <param name="Records">Records as a JSON array.</param>
public sealed record HackStorageStoreSnapshot(
    string Name,
    string Purpose,
    IReadOnlyList<string> KeyPath,
    HackStorageStoreKind Kind,
    int RecordCount,
    string FormattedJson,
    JsonElement Records);

/// <summary>
/// The whole OS database at one instant, with every filesystem entry's resolved virtual path
/// (inodes in <c>fsEntries</c>, hierarchy in <c>fsLinks</c>).
/// </summary>
/// <param name="CapturedAtUtc">When the snapshot was read.</param>
/// <param name="DatabaseName">Name of the OS database.</param>
/// <param name="DatabaseVersion">Schema version of the OS database.</param>
/// <param name="Stores">Every object store, in schema order.</param>
/// <param name="ResolvedPaths">Entry id to absolute virtual path.</param>
/// <param name="TotalLinkCount">Number of directory links read.</param>
public sealed record HackStorageSnapshot(
    DateTimeOffset CapturedAtUtc,
    string DatabaseName,
    int DatabaseVersion,
    IReadOnlyList<HackStorageStoreSnapshot> Stores,
    IReadOnlyDictionary<string, string> ResolvedPaths,
    int TotalLinkCount)
{
    private static readonly JsonSerializerOptions ExportOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Serializes the whole snapshot into one indented JSON document, for export or inspection.</summary>
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

        JsonObject paths = [];
        foreach ((string entryId, string path) in ResolvedPaths)
        {
            paths[entryId] = path;
        }

        root["resolvedFilePaths"] = paths;

        JsonObject stores = [];
        foreach (HackStorageStoreSnapshot store in Stores)
        {
            stores[store.Name] = new JsonObject
            {
                ["purpose"] = store.Purpose,
                ["recordCount"] = store.RecordCount,
                ["records"] = JsonNode.Parse(store.FormattedJson)
            };
        }

        root["objectStores"] = stores;
        return root.ToJsonString(ExportOptions);
    }
}
