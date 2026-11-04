using System.Text.Json;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Infrastructure.Browser.Interop;
using Kazo.Os.Infrastructure.Browser.Schema;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Metrics;
using Microsoft.JSInterop;

namespace Kazo.Os.Infrastructure.Browser.FileSystem;

/// <summary>Resolves filesystem paths and directory children from canonical IndexedDB records.</summary>
internal sealed class IndexedDbFileSystemReader : IAsyncDisposable
{
    private const string Boundary = "FileSystemMetadataMutation";
    private readonly IndexedDbInteropAdapter _adapter;

    /// <summary>Initializes a browser filesystem metadata reader.</summary>
    /// <param name="runtime">JavaScript runtime.</param>
    /// <param name="disk">Optional statistics of <c>hda</c>; each lookup transaction counts as one 4 KiB read.</param>
    internal IndexedDbFileSystemReader(IJSRuntime runtime, IBlockDeviceStatistics? disk = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _adapter = new IndexedDbInteropAdapter(runtime, disk);
    }

    /// <summary>
    /// Sums the length of every distinct content blob that a file entry references: the bytes the
    /// deduplicated store keeps for live files (ADR 0057 section 7). Two files with the same content
    /// count once, as their chunks are stored once. Orphaned chunks awaiting cleanup are not counted.
    /// </summary>
    internal async ValueTask<long> MeasureStoredContentBytesAsync(CancellationToken cancellationToken = default)
    {
        await _adapter.OpenAsync(IndexedDbMigrationPlan.CreateCurrent(), cancellationToken).ConfigureAwait(false);
        IReadOnlyList<JsonElement> results = await _adapter.ExecuteAsync(
            Boundary,
            IndexedDbTransactionMode.ReadOnly,
            [new IndexedDbOperation("getAll", HackerOsIndexedDbSchema.FileSystemEntryStoreName)],
            cancellationToken).ConfigureAwait(false);
        JsonElement entries = results.Single();
        if (entries.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Persisted filesystem entry scan result is malformed.");
        }

        Dictionary<string, long> lengths = new(StringComparer.Ordinal);
        foreach (JsonElement element in entries.EnumerateArray())
        {
            if (element.TryGetProperty("contentHash", out JsonElement hash)
                && hash.ValueKind == JsonValueKind.String
                && hash.GetString() is { Length: > 0 } contentHash
                && element.TryGetProperty("length", out JsonElement length)
                && length.ValueKind == JsonValueKind.Number)
            {
                lengths[contentHash] = length.GetInt64();
            }
        }

        return lengths.Values.Sum();
    }

    /// <summary>Resolves a canonical path to its persisted entry, or null when a link is absent.</summary>
    /// <remarks>
    /// The root is one entry read. Any deeper path is walked inside a single read-only
    /// transaction (see <see cref="ResolveComponentsAsync"/>).
    /// </remarks>
    internal async ValueTask<IndexedDbFileSystemEntryRecord?> ResolveAsync(
        VirtualPath path,
        CancellationToken cancellationToken = default)
    {
        await _adapter.OpenAsync(IndexedDbMigrationPlan.CreateCurrent(), cancellationToken).ConfigureAwait(false);
        if (path.Value == "/")
        {
            return await GetEntryAsync(IndexedDbFileSystemBootstrapper.RootEntryId.ToString(), cancellationToken)
                .ConfigureAwait(false);
        }

        IReadOnlyList<IndexedDbFileSystemEntryRecord> components =
            await ResolveComponentsAsync(path, cancellationToken).ConfigureAwait(false);
        return components.Count == CountNames(path) ? components[^1] : null;
    }

    /// <summary>
    /// Reads the entry of each component of a path other than <c>/</c>, in order, stopping before
    /// the first missing name. The whole walk is one read-only transaction (the
    /// <c>resolvePath</c> operation of <c>indexedDb.js</c>): one browser round trip and one
    /// consistent snapshot instead of two round trips per component.
    /// </summary>
    /// <exception cref="InvalidDataException">A link references a missing entry.</exception>
    internal async ValueTask<IReadOnlyList<IndexedDbFileSystemEntryRecord>> ResolveComponentsAsync(
        VirtualPath path,
        CancellationToken cancellationToken = default)
    {
        await _adapter.OpenAsync(IndexedDbMigrationPlan.CreateCurrent(), cancellationToken).ConfigureAwait(false);
        string[] names = path.Value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        IReadOnlyList<JsonElement> results = await _adapter.ExecuteAsync(
            Boundary,
            IndexedDbTransactionMode.ReadOnly,
            [new IndexedDbOperation(
                "resolvePath",
                HackerOsIndexedDbSchema.FileSystemEntryStoreName,
                Key: IndexedDbFileSystemBootstrapper.RootEntryId.ToString(),
                Query: names,
                ReferenceObjectStoreName: HackerOsIndexedDbSchema.FileSystemLinkStoreName)],
            cancellationToken).ConfigureAwait(false);
        JsonElement chain = results.Single();
        if (chain.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Persisted filesystem path walk result is malformed.");
        }

        List<IndexedDbFileSystemEntryRecord> components = [];
        foreach (JsonElement element in chain.EnumerateArray())
        {
            if (element.TryGetProperty("danglingLink", out JsonElement danglingLink))
            {
                throw new InvalidDataException(
                    $"Filesystem link '{danglingLink.GetString()}' references missing entry '{RequiredString(element, "entryId")}'.");
            }

            components.Add(IndexedDbFileSystemEntryRecord.FromJson(element));
        }

        return components;
    }

    /// <summary>Returns immediate child records in ordinal name order, or null when the path is absent.</summary>
    internal async ValueTask<IReadOnlyList<(FileSystemEntryName Name, IndexedDbFileSystemEntryRecord Entry)>?> EnumerateAsync(
        VirtualPath path,
        CancellationToken cancellationToken = default)
    {
        IndexedDbFileSystemDirectoryRead? read =
            await ReadDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
        return read?.Children;
    }

    /// <summary>Returns the observed directory and immediate child records in ordinal name order.</summary>
    internal async ValueTask<IndexedDbFileSystemDirectoryRead?> ReadDirectoryAsync(
        VirtualPath path,
        CancellationToken cancellationToken = default)
    {
        IndexedDbFileSystemEntryRecord? directory = await ResolveAsync(path, cancellationToken).ConfigureAwait(false);
        if (directory is null)
        {
            return null;
        }

        return await ReadChildrenAsync(directory, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns immediate children for an already resolved directory record.</summary>
    internal async ValueTask<IndexedDbFileSystemDirectoryRead> ReadChildrenAsync(
        IndexedDbFileSystemEntryRecord directory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directory);

        if (directory.Kind != (int)FileSystemEntryKind.Directory)
        {
            throw new InvalidDataException($"Filesystem entry '{directory.Id}' is not a directory.");
        }

        IReadOnlyList<JsonElement> linkResults = await _adapter.ExecuteAsync(
            Boundary,
            IndexedDbTransactionMode.ReadOnly,
            [new IndexedDbOperation(
                "getAll",
                HackerOsIndexedDbSchema.FileSystemLinkStoreName,
                IndexName: "parentId",
                Query: directory.Id)],
            cancellationToken).ConfigureAwait(false);
        IndexedDbFileSystemLinkRecord[] links =
        [..
            linkResults.Single()
                .EnumerateArray()
                .Select(ReadLink)
                .OrderBy(link => link.Name, StringComparer.Ordinal)
        ];
        if (links.Length == 0)
        {
            return new IndexedDbFileSystemDirectoryRead(directory, []);
        }

        IReadOnlyList<JsonElement> entryResults = await _adapter.ExecuteAsync(
            Boundary,
            IndexedDbTransactionMode.ReadOnly,
            [.. links.Select(link => new IndexedDbOperation(
                "get",
                HackerOsIndexedDbSchema.FileSystemEntryStoreName,
                Key: link.EntryId))],
            cancellationToken).ConfigureAwait(false);

        List<(FileSystemEntryName Name, IndexedDbFileSystemEntryRecord Entry)> children = [];
        for (int index = 0; index < links.Length; index++)
        {
            if (IsMissing(entryResults[index]))
            {
                throw new InvalidDataException(
                    $"Filesystem link '{links[index].Name}' references missing entry '{links[index].EntryId}'.");
            }

            children.Add((FileSystemEntryName.Parse(links[index].Name), IndexedDbFileSystemEntryRecord.FromJson(entryResults[index])));
        }

        return new IndexedDbFileSystemDirectoryRead(directory, children);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _adapter.DisposeAsync();

    private async ValueTask<IndexedDbFileSystemEntryRecord?> GetEntryAsync(
        string id,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<JsonElement> results = await _adapter.ExecuteAsync(
            Boundary,
            IndexedDbTransactionMode.ReadOnly,
            [new IndexedDbOperation("get", HackerOsIndexedDbSchema.FileSystemEntryStoreName, Key: id)],
            cancellationToken).ConfigureAwait(false);
        return IsMissing(results.Single()) ? null : IndexedDbFileSystemEntryRecord.FromJson(results.Single());
    }

    private static IndexedDbFileSystemLinkRecord ReadLink(JsonElement element) => new(
        RequiredString(element, "parentId"),
        RequiredString(element, "name"),
        RequiredString(element, "entryId"));

    private static int CountNames(VirtualPath path) =>
        path.Value.Split('/', StringSplitOptions.RemoveEmptyEntries).Length;

    private static bool IsMissing(JsonElement element) =>
        element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;

    private static string RequiredString(JsonElement element, string propertyName) =>
        element.GetProperty(propertyName).GetString()
        ?? throw new InvalidDataException($"Persisted filesystem property '{propertyName}' is missing.");
}

/// <summary>Contains one directory record and the child records observed with it.</summary>
internal sealed record IndexedDbFileSystemDirectoryRead(
    IndexedDbFileSystemEntryRecord Directory,
    IReadOnlyList<(FileSystemEntryName Name, IndexedDbFileSystemEntryRecord Entry)> Children);
