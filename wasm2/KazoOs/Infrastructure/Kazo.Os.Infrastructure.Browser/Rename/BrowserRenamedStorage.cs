using System.Text.Json;
using System.Text.Json.Nodes;
using Kazo.Os.Infrastructure.Browser.Interop;
using Kazo.Os.Infrastructure.Browser.Schema;
using Microsoft.JSInterop;

namespace Kazo.Os.Infrastructure.Browser.Rename;

/// <summary>The browser storage the rename migration reads and moves (a fake in the unit tests).</summary>
internal interface IRenamedStorageBrowser
{
    /// <summary>Lists the IndexedDB databases of the origin.</summary>
    Task<IReadOnlyList<string>> ListDatabasesAsync(CancellationToken cancellationToken);

    /// <summary>Copies a database to a new name; <see langword="false"/>, copying nothing, when the target exists.</summary>
    Task<bool> CopyDatabaseAsync(string source, string target, CancellationToken cancellationToken);

    /// <summary>Deletes a database.</summary>
    Task DeleteDatabaseAsync(string name, CancellationToken cancellationToken);

    /// <summary>Reads every key of <c>local</c> or <c>session</c> storage.</summary>
    Task<IReadOnlyList<KeyValuePair<string, string>>> ReadStorageAsync(string area, CancellationToken cancellationToken);

    /// <summary>Writes one storage key.</summary>
    Task WriteStorageAsync(string area, string key, string value, CancellationToken cancellationToken);

    /// <summary>Removes one storage key.</summary>
    Task RemoveStorageAsync(string area, string key, CancellationToken cancellationToken);

    /// <summary>Lists the Cache Storage caches.</summary>
    Task<IReadOnlyList<string>> CacheNamesAsync(CancellationToken cancellationToken);

    /// <summary>Deletes one cache.</summary>
    Task DeleteCacheAsync(string name, CancellationToken cancellationToken);

    /// <summary>Rewrites the records of the OS database; returns the number of record changes.</summary>
    Task<int> RewriteOsDatabaseAsync(CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IRenamedStorageBrowser"/> over the page: <c>renamedStorage.js</c> for what only
/// JavaScript reaches (database list and copy, storage keys, caches) and the OS database adapter
/// for the record rewrite, decided in C# by <see cref="RenamedOsDatabaseRewriter"/>.
/// </summary>
internal sealed class BrowserRenamedStorage(IJSRuntime runtime) : IRenamedStorageBrowser, IAsyncDisposable
{
    private const string ModulePath = "./_content/Kazo.Os.Infrastructure.Browser/renamedStorage.js";
    private const string Boundary = "BackupRestore";
    private readonly IndexedDbInteropAdapter _adapter = new(runtime);
    private IJSObjectReference? _module;

    public async Task<IReadOnlyList<string>> ListDatabasesAsync(CancellationToken cancellationToken) =>
        await (await ModuleAsync(cancellationToken)).InvokeAsync<string[]>("listDatabases", cancellationToken);

    public async Task<bool> CopyDatabaseAsync(string source, string target, CancellationToken cancellationToken) =>
        await (await ModuleAsync(cancellationToken)).InvokeAsync<bool>("copyDatabase", cancellationToken, source, target);

    public async Task DeleteDatabaseAsync(string name, CancellationToken cancellationToken) =>
        await (await ModuleAsync(cancellationToken)).InvokeVoidAsync("deleteDatabase", cancellationToken, name);

    public async Task<IReadOnlyList<KeyValuePair<string, string>>> ReadStorageAsync(string area, CancellationToken cancellationToken)
    {
        string[][] entries = await (await ModuleAsync(cancellationToken)).InvokeAsync<string[][]>("readStorage", cancellationToken, area);
        return [.. entries.Where(entry => entry.Length == 2).Select(entry => new KeyValuePair<string, string>(entry[0], entry[1]))];
    }

    public async Task WriteStorageAsync(string area, string key, string value, CancellationToken cancellationToken) =>
        await (await ModuleAsync(cancellationToken)).InvokeVoidAsync("writeStorage", cancellationToken, area, key, value);

    public async Task RemoveStorageAsync(string area, string key, CancellationToken cancellationToken) =>
        await (await ModuleAsync(cancellationToken)).InvokeVoidAsync("removeStorage", cancellationToken, area, key);

    public async Task<IReadOnlyList<string>> CacheNamesAsync(CancellationToken cancellationToken) =>
        await (await ModuleAsync(cancellationToken)).InvokeAsync<string[]>("cacheNames", cancellationToken);

    public async Task DeleteCacheAsync(string name, CancellationToken cancellationToken) =>
        await (await ModuleAsync(cancellationToken)).InvokeVoidAsync("deleteCache", cancellationToken, name);

    /// <summary>
    /// Opens the OS database (upgrading its schema like any boot), reads every store, and writes
    /// the rewriter's changes in one read-write transaction: all of them or none.
    /// </summary>
    public async Task<int> RewriteOsDatabaseAsync(CancellationToken cancellationToken)
    {
        await _adapter.OpenAsync(IndexedDbMigrationPlan.CreateCurrent(), cancellationToken).ConfigureAwait(false);
        IndexedDbObjectStoreDefinition[] definitions = [.. KazoOsIndexedDbSchema.ObjectStores];
        IReadOnlyList<JsonElement> results = await _adapter.ExecuteAsync(
            Boundary,
            IndexedDbTransactionMode.ReadOnly,
            [.. definitions.Select(store => new IndexedDbOperation("getAll", store.Name))],
            cancellationToken).ConfigureAwait(false);

        Dictionary<string, IReadOnlyList<JsonObject>> stores = new(StringComparer.Ordinal);
        for (int index = 0; index < definitions.Length; index++)
        {
            stores[definitions[index].Name] = [.. results[index].EnumerateArray()
                .Select(record => JsonNode.Parse(record.GetRawText())!.AsObject())];
        }

        RenamedOsDatabase rewritten = RenamedOsDatabaseRewriter.Rewrite(stores);
        if (rewritten.Changes.Count == 0)
        {
            return 0;
        }

        List<IndexedDbOperation> operations = [];
        foreach (RenamedRecordChange change in rewritten.Changes)
        {
            if (change.DeleteKey is not null)
            {
                operations.Add(new IndexedDbOperation("delete", change.StoreName, Key: ToElement(change.DeleteKey)));
            }

            if (change.Put is not null)
            {
                operations.Add(new IndexedDbOperation("put", change.StoreName, Value: ToElement(change.Put)));
            }
        }

        await _adapter.ExecuteAsync(Boundary, IndexedDbTransactionMode.ReadWrite, operations, cancellationToken).ConfigureAwait(false);
        return rewritten.Changes.Count;
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync().ConfigureAwait(false);
            }
            catch (JSDisconnectedException)
            {
                // The page is going away: nothing left to release.
            }
        }

        await _adapter.DisposeAsync().ConfigureAwait(false);
    }

    private static JsonElement ToElement(JsonNode node)
    {
        using JsonDocument document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    private async ValueTask<IJSObjectReference> ModuleAsync(CancellationToken cancellationToken) =>
        _module ??= await runtime.InvokeAsync<IJSObjectReference>("import", cancellationToken, ModulePath).ConfigureAwait(false);
}
